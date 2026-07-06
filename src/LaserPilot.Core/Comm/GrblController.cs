using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using RJCP.IO.Ports;

namespace LaserPilot.Core.Comm;

public enum LogDirection { Sent, Received, Info, Error }

public sealed class LogEntry
{
    public DateTime Time { get; } = DateTime.Now;
    public LogDirection Direction { get; init; }
    public string Text { get; init; } = "";
}

public sealed class JobProgress
{
    public int TotalLines { get; init; }
    public int AckedLines { get; init; }
    public bool Running { get; init; }
    public bool Paused { get; init; }
    public TimeSpan Elapsed { get; init; }
    public double Percent => TotalLines == 0 ? 0 : 100.0 * AckedLines / TotalLines;
}

/// <summary>
/// GRBL 1.1 controller: serial connection, status polling, command/response
/// tracking, and character-counting job streaming (the same protocol approach
/// proven by LaserGRBL / grbl's stream.py).
/// </summary>
public sealed class GrblController : IDisposable
{
    private const int RxBufferSize = 127;      // GRBL 1.1 serial RX buffer
    private const int StatusPollMs = 200;
    private const int InitialConnectWaitMs = 2500;
    private const int ResetRetryWaitMs = 4000;

    private SerialPortStream? _port;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private Task? _pollTask;
    private TaskCompletionSource<bool>? _connectionReady;
    private volatile bool _controllerResponsive;

    private readonly object _sync = new();
    private readonly object _connectionSync = new();
    private readonly object _writeSync = new();

    // Interactive commands awaiting ok/error (FIFO, matches GRBL response order).
    private readonly ConcurrentQueue<PendingCommand> _pending = new();

    // Streaming job state
    private List<string>? _jobLines;
    private int _jobNextLine;                 // next line index to send
    private int _jobAckedLines;               // lines acknowledged with ok/error
    private readonly Queue<int> _inflightLengths = new(); // byte counts of unacked lines
    private int _inflightBytes;
    private bool _jobPaused;
    private DateTime _jobStart;
    private System.Timers.Timer? _progressTimer;

    private sealed record PendingCommand(string Command, TaskCompletionSource<GrblResponse> Tcs);

    public sealed record GrblResponse(bool Ok, int ErrorCode, string ErrorText);

    private int _bytesReceived;
    private int _linesReceived;

    private bool IsPortOpen => _port?.IsOpen == true;
    public bool IsConnected => _controllerResponsive && IsPortOpen;
    public bool IsJobRunning { get; private set; }
    public bool IsJobPaused => _jobPaused;
    public GrblStatus Status { get; private set; } = new() { State = MachineState.Disconnected };
    public string FirmwareVersion { get; private set; } = "";
    public IReadOnlyDictionary<int, double> Settings => _settings;
    private readonly SortedDictionary<int, double> _settings = new();

    public event Action<LogEntry>? Log;
    public event Action<GrblStatus>? StatusUpdated;
    public event Action<int, string>? AlarmRaised;           // code, description
    public event Action<JobProgress>? JobProgressChanged;
    public event Action? SettingsRefreshed;
    public event Action? ConnectionChanged;

    public static string[] ListPorts()
    {
        using var serial = new SerialPortStream();
        return serial.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Connect(string portName, int baud = 115200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baud);
        Disconnect();

        // LaserGRBL leaves these control lines off for normal GRBL connections.
        // Some USB controllers use DTR/RTS as reset or enable pins and can be
        // held in reset when they are asserted continuously.
        var port = new SerialPortStream(portName, baud, 8, Parity.None, StopBits.One)
        {
            NewLine = "\n",
            ReadTimeout = 500,
            WriteTimeout = 2000,
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            Encoding = Encoding.ASCII,
        };

        var cts = new CancellationTokenSource();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            port.Open();
            port.DiscardInBuffer();
            port.DiscardOutBuffer();

            lock (_connectionSync)
            {
                _port = port;
                _cts = cts;
                _connectionReady = ready;
                _controllerResponsive = false;
                Interlocked.Exchange(ref _bytesReceived, 0);
                Interlocked.Exchange(ref _linesReceived, 0);
                _readTask = Task.Run(() => ReadLoop(port, cts.Token));
                _pollTask = Task.Run(() => PollLoop(port, cts.Token));
            }

            Info($"Port {portName} opened @ {baud} baud; probing for GRBL…");

            // A GRBL soft reset produces its hello line. Status polling also
            // provides a non-destructive path to detect a responsive board.
            WriteByte(port, 0x18);
            if (WaitForController(ready, InitialConnectWaitMs)) return;
            ThrowIfConnectionWasClosed(port);

            // Retry the hardware-reset transition used by Arduino/CH340-style
            // boards, but release the lines again so they cannot hold the MCU
            // in reset.
            Info("No reply with DTR/RTS off; trying a brief USB control-line reset…");
            PulseResetLines(port);
            Thread.Sleep(300);
            WriteByte(port, 0x18);
            if (WaitForController(ready, ResetRetryWaitMs)) return;
            ThrowIfConnectionWasClosed(port);

            var bytes = Volatile.Read(ref _bytesReceived);
            var lines = Volatile.Read(ref _linesReceived);
            var detail = bytes == 0
                ? "No bytes were received from the controller."
                : $"{bytes} byte(s) in {lines} complete line(s) were received, but none was a valid GRBL response.";
            var message =
                $"{portName} opened at {baud} baud, but GRBL did not answer. {detail} " +
                "LaserPilot tried both a GRBL soft reset and a brief DTR/RTS reset. " +
                "Close LightBurn/LaserGRBL completely, power-cycle the laser, and verify that this is the same COM port and baud rate they use.";
            Error(message);
            throw new TimeoutException(message);
        }
        catch
        {
            if (ReferenceEquals(_port, port))
                Disconnect();
            else
            {
                cts.Cancel();
                try { port.Close(); } catch { }
                port.Dispose();
                cts.Dispose();
            }
            throw;
        }
    }

    public void Disconnect()
    {
        StopJobInternal(sendReset: false);

        SerialPortStream? port;
        CancellationTokenSource? cts;
        TaskCompletionSource<bool>? ready;
        Task? readTask;
        Task? pollTask;
        lock (_connectionSync)
        {
            port = _port;
            cts = _cts;
            ready = _connectionReady;
            readTask = _readTask;
            pollTask = _pollTask;
            _port = null;
            _cts = null;
            _readTask = null;
            _pollTask = null;
            _connectionReady = null;
            _controllerResponsive = false;
        }

        ready?.TrySetResult(false);
        cts?.Cancel();
        try { port?.Close(); } catch { /* port may already be gone */ }
        try
        {
            var tasks = new[] { readTask, pollTask }.Where(t => t is not null).Cast<Task>().ToArray();
            if (tasks.Length > 0) Task.WaitAll(tasks, 1000);
        }
        catch { /* cancellation/port closure can fault a worker while disconnecting */ }
        port?.Dispose();
        cts?.Dispose();
        FailAllPending("disconnected");
        FirmwareVersion = "";
        lock (_sync) _settings.Clear();
        Status = new GrblStatus { State = MachineState.Disconnected };
        StatusUpdated?.Invoke(Status);
        ConnectionChanged?.Invoke();
    }

    // ---------- Realtime commands (bypass the buffer, act immediately) ----------

    /// <summary>Emergency stop: GRBL soft-reset (Ctrl-X). Halts everything instantly; laser off.</summary>
    public void EmergencyStop()
    {
        StopJobInternal(sendReset: true);
        Info("EMERGENCY STOP (soft reset) sent");
    }

    public void SoftReset() { WriteByte(0x18); FailAllPending("reset"); }
    public void FeedHold() => WriteRaw("!");
    public void CycleResume() => WriteRaw("~");
    public void JogCancel() => WriteByte(0x85);
    public void QueryStatus() => WriteRaw("?");

    // ---------- Interactive commands ----------

    /// <summary>Send a single command and await its ok/error response.</summary>
    public Task<GrblResponse> SendAsync(string command, int timeoutMs = 10000)
    {
        var tcs = new TaskCompletionSource<GrblResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (!IsConnected)
            {
                tcs.SetResult(new GrblResponse(false, -1, "Not connected"));
                return tcs.Task;
            }
            if (IsJobRunning)
            {
                tcs.SetResult(new GrblResponse(false, -1, "A job is running — stop it before sending manual commands."));
                return tcs.Task;
            }
            _pending.Enqueue(new PendingCommand(command, tcs));
            WriteLine(command);
        }
        return WithTimeout(tcs.Task, timeoutMs, command);
    }

    private static async Task<GrblResponse> WithTimeout(Task<GrblResponse> task, int ms, string cmd)
    {
        var done = await Task.WhenAny(task, Task.Delay(ms)).ConfigureAwait(false);
        return done == task ? task.Result : new GrblResponse(false, -1, $"Timeout waiting for response to '{cmd}'");
    }

    public Task<GrblResponse> HomeAsync(int timeoutMs = 90000) => SendAsync("$H", timeoutMs);
    public Task<GrblResponse> UnlockAsync() => SendAsync("$X");

    /// <summary>Relative jog using GRBL 1.1 $J= (safely cancellable, respects soft limits).</summary>
    public Task<GrblResponse> JogAsync(double dx, double dy, double feed)
    {
        var c = string.Create(CultureInfo.InvariantCulture,
            $"$J=G91 G21 X{dx:0.###} Y{dy:0.###} F{feed:0.#}");
        return SendAsync(c);
    }

    public Task<GrblResponse> JogAbsoluteAsync(double x, double y, double feed)
    {
        var c = string.Create(CultureInfo.InvariantCulture,
            $"$J=G90 G21 X{x:0.###} Y{y:0.###} F{feed:0.#}");
        return SendAsync(c);
    }

    public async Task<bool> RefreshSettingsAsync()
    {
        var r = await SendAsync("$$", 5000).ConfigureAwait(false);
        if (r.Ok) SettingsRefreshed?.Invoke();
        return r.Ok;
    }

    public async Task<GrblResponse> WriteSettingAsync(int number, double value)
    {
        var cmd = string.Create(CultureInfo.InvariantCulture, $"${number}={value:0.###}");
        var r = await SendAsync(cmd, 5000).ConfigureAwait(false);
        if (r.Ok)
        {
            lock (_sync) _settings[number] = value;
            SettingsRefreshed?.Invoke();
        }
        return r;
    }

    public double? GetSetting(int number)
    {
        lock (_sync) return _settings.TryGetValue(number, out var v) ? v : null;
    }

    // ---------- Job streaming ----------

    public void StartJob(IEnumerable<string> gcodeLines)
    {
        lock (_sync)
        {
            if (!IsConnected) throw new InvalidOperationException("Not connected.");
            if (IsJobRunning) throw new InvalidOperationException("A job is already running.");
            _jobLines = gcodeLines
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith(';') && !l.StartsWith('('))
                .ToList();
            _jobNextLine = 0;
            _jobAckedLines = 0;
            _inflightLengths.Clear();
            _inflightBytes = 0;
            _jobPaused = false;
            IsJobRunning = true;
            _jobStart = DateTime.Now;
            FillStreamBuffer();
        }
        _progressTimer = new System.Timers.Timer(500) { AutoReset = true };
        _progressTimer.Elapsed += (_, _) => RaiseProgress();
        _progressTimer.Start();
        Info($"Job started: {_jobLines!.Count} lines");
        RaiseProgress();
    }

    public void PauseJob()
    {
        if (!IsJobRunning) return;
        _jobPaused = true;
        FeedHold();
        Info("Job paused (feed hold)");
        RaiseProgress();
    }

    public void ResumeJob()
    {
        if (!IsJobRunning) return;
        _jobPaused = false;
        CycleResume();
        lock (_sync) FillStreamBuffer();
        Info("Job resumed");
        RaiseProgress();
    }

    /// <summary>Stop the job: soft-reset so motion and laser stop immediately.</summary>
    public void StopJob() => StopJobInternal(sendReset: true);

    private void StopJobInternal(bool sendReset)
    {
        bool wasRunning;
        lock (_sync)
        {
            wasRunning = IsJobRunning;
            IsJobRunning = false;
            _jobLines = null;
            _inflightLengths.Clear();
            _inflightBytes = 0;
            _jobPaused = false;
        }
        _progressTimer?.Stop();
        _progressTimer?.Dispose();
        _progressTimer = null;
        if (sendReset && IsConnected) SoftReset();
        if (wasRunning) { Info("Job stopped"); RaiseProgress(); }
    }

    // Send as many job lines as fit in GRBL's RX buffer (character counting).
    private void FillStreamBuffer()
    {
        if (!IsJobRunning || _jobPaused || _jobLines is null) return;
        while (_jobNextLine < _jobLines.Count)
        {
            var line = _jobLines[_jobNextLine];
            var len = line.Length + 1; // +\n
            if (_inflightBytes + len > RxBufferSize) break;
            _inflightLengths.Enqueue(len);
            _inflightBytes += len;
            _jobNextLine++;
            WriteLine(line, quiet: true);
        }
    }

    private void OnJobLineAcked(bool ok, int errorCode, string rawLine)
    {
        List<string>? finishedLines = null;
        lock (_sync)
        {
            if (!IsJobRunning || _jobLines is null) return;
            if (_inflightLengths.Count > 0) _inflightBytes -= _inflightLengths.Dequeue();
            _jobAckedLines++;
            if (!ok)
                Error($"Job line {_jobAckedLines}: {rawLine} → {GrblCodes.ErrorText(errorCode)}");
            if (_jobAckedLines >= _jobLines.Count)
            {
                IsJobRunning = false;
                finishedLines = _jobLines;
                _jobLines = null;
            }
            else
            {
                FillStreamBuffer();
            }
        }
        if (finishedLines is not null)
        {
            _progressTimer?.Stop();
            _progressTimer?.Dispose();
            _progressTimer = null;
            Info($"Job complete: {finishedLines.Count} lines in {DateTime.Now - _jobStart:hh\\:mm\\:ss}");
            RaiseProgress();
        }
    }

    private void RaiseProgress()
    {
        JobProgressChanged?.Invoke(new JobProgress
        {
            TotalLines = _jobLines?.Count ?? _jobAckedLines,
            AckedLines = _jobAckedLines,
            Running = IsJobRunning,
            Paused = _jobPaused,
            Elapsed = IsJobRunning ? DateTime.Now - _jobStart : TimeSpan.Zero,
        });
    }

    // ---------- Serial I/O ----------

    private void WriteRaw(string s)
    {
        var port = _port;
        if (port is not null) WriteRaw(port, s);
    }

    private void WriteByte(byte b)
    {
        var port = _port;
        if (port is not null) WriteByte(port, b);
    }

    private void WriteRaw(SerialPortStream port, string s)
    {
        try
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            lock (_writeSync)
            {
                if (!port.IsOpen) return;
                port.Write(bytes, 0, bytes.Length);
                port.Flush();
            }
        }
        catch (Exception ex) { Error($"Serial write failed: {ex.Message}"); }
    }

    private void WriteByte(SerialPortStream port, byte b)
    {
        try
        {
            lock (_writeSync)
            {
                if (!port.IsOpen) return;
                port.WriteByte(b);
                port.Flush();
            }
        }
        catch (Exception ex) { Error($"Serial write failed: {ex.Message}"); }
    }

    private void WriteLine(string line, bool quiet = false)
    {
        if (!quiet) Log?.Invoke(new LogEntry { Direction = LogDirection.Sent, Text = line });
        WriteRaw(line + "\n");
    }

    private void ReadLoop(SerialPortStream port, CancellationToken ct)
    {
        var buf = new byte[1024];
        var rxLine = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!port.IsOpen) return;
                int n = port.Read(buf, 0, buf.Length);
                if (n <= 0) continue;
                Interlocked.Add(ref _bytesReceived, n);
                for (int i = 0; i < n; i++)
                {
                    char c = (char)buf[i];
                    if (c == '\n' || c == '\r')
                    {
                        if (rxLine.Length > 0)
                        {
                            var line = rxLine.ToString();
                            rxLine.Clear();
                            try { HandleLine(port, line); }
                            catch (Exception ex) { Error($"Parse error on '{line}': {ex.Message}"); }
                        }
                    }
                    else rxLine.Append(c);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (TimeoutException) { /* normal */ }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                Error($"Serial read error: {ex.Message}");
                Thread.Sleep(250);
            }
        }
    }

    private void PollLoop(SerialPortStream port, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!port.IsOpen) return;
            WriteRaw(port, "?");
            if (ct.WaitHandle.WaitOne(StatusPollMs)) return;
        }
    }

    private void HandleLine(SerialPortStream source, string line)
    {
        Interlocked.Increment(ref _linesReceived);

        // Status reports are frequent — don't log them.
        if (line.StartsWith('<'))
        {
            if (GrblStatus.TryParse(line, Status, out var st))
            {
                MarkControllerResponsive(source);
                Status = st;
                StatusUpdated?.Invoke(st);
            }
            return;
        }

        Log?.Invoke(new LogEntry { Direction = LogDirection.Received, Text = line });
        if (IsRecognizedGrblLine(line)) MarkControllerResponsive(source);

        if (line == "ok")
        {
            AckLine(true, 0, line);
        }
        else if (line.StartsWith("error:"))
        {
            int.TryParse(line.AsSpan(6), out var code);
            Error(GrblCodes.ErrorText(code));
            AckLine(false, code, line);
        }
        else if (line.StartsWith("ALARM:"))
        {
            int.TryParse(line.AsSpan(6), out var code);
            var text = GrblCodes.AlarmText(code);
            Error($"ALARM {code}: {text}");
            // Alarms abort streaming; anything unacked will never get an ok.
            StopJobInternal(sendReset: false);
            FailAllPending($"ALARM:{code} {text}");
            AlarmRaised?.Invoke(code, text);
        }
        else if (line.StartsWith("Grbl"))
        {
            FirmwareVersion = line;
            Info($"GRBL reset/hello: {line}");
            StopJobInternal(sendReset: false);
            FailAllPending("controller reset");
        }
        else if (line.StartsWith('$') && line.Contains('='))
        {
            var eq = line.IndexOf('=');
            if (int.TryParse(line.AsSpan(1, eq - 1), out var num) &&
                double.TryParse(line.AsSpan(eq + 1), CultureInfo.InvariantCulture, out var val))
            {
                lock (_sync) _settings[num] = val;
            }
        }
        // [MSG:...], [VER:...], [OPT:...], feedback lines — already logged above.
    }

    private static bool IsRecognizedGrblLine(string line) =>
        line == "ok" ||
        line.StartsWith("Grbl", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("ALARM:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith('$') ||
        line.StartsWith("[MSG:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("[VER:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("[OPT:", StringComparison.OrdinalIgnoreCase);

    private void MarkControllerResponsive(SerialPortStream source)
    {
        TaskCompletionSource<bool>? ready;
        lock (_connectionSync)
        {
            if (!ReferenceEquals(_port, source) || _controllerResponsive) return;
            _controllerResponsive = true;
            ready = _connectionReady;
        }

        ready?.TrySetResult(true);
        Info($"GRBL responded on {source.PortName}; connection ready.");
        ConnectionChanged?.Invoke();
    }

    private static bool WaitForController(TaskCompletionSource<bool> ready, int timeoutMs) =>
        ready.Task.Wait(timeoutMs) && ready.Task.GetAwaiter().GetResult();

    private void ThrowIfConnectionWasClosed(SerialPortStream port)
    {
        if (!ReferenceEquals(_port, port) || !port.IsOpen)
            throw new IOException("The serial connection was closed while waiting for GRBL.");
    }

    private void PulseResetLines(SerialPortStream port)
    {
        try
        {
            lock (_writeSync)
            {
                if (!port.IsOpen) return;
                try
                {
                    port.DtrEnable = true;
                    port.RtsEnable = true;
                    Thread.Sleep(100);
                }
                finally
                {
                    // Release each line independently; if a driver rejects one,
                    // still make a best effort not to leave the other asserted.
                    try { port.DtrEnable = false; } catch { }
                    try { port.RtsEnable = false; } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Info($"USB control-line reset was unavailable: {ex.Message}");
        }
    }

    private void AckLine(bool ok, int errorCode, string raw)
    {
        if (IsJobRunning)
        {
            OnJobLineAcked(ok, errorCode, raw);
            return;
        }
        if (_pending.TryDequeue(out var p))
            p.Tcs.TrySetResult(new GrblResponse(ok, errorCode, ok ? "" : GrblCodes.ErrorText(errorCode)));
    }

    private void FailAllPending(string reason)
    {
        while (_pending.TryDequeue(out var p))
            p.Tcs.TrySetResult(new GrblResponse(false, -1, reason));
    }

    private void Info(string msg) => Log?.Invoke(new LogEntry { Direction = LogDirection.Info, Text = msg });
    private void Error(string msg) => Log?.Invoke(new LogEntry { Direction = LogDirection.Error, Text = msg });

    public void Dispose() => Disconnect();
}
