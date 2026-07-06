using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Ports;
using System.Text;

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

    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private Task? _pollTask;

    private readonly object _sync = new();
    private readonly StringBuilder _rxLine = new();

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

    public bool IsConnected => _port?.IsOpen == true;
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

    public static string[] ListPorts() => SerialPort.GetPortNames().Distinct().OrderBy(p => p).ToArray();

    public void Connect(string portName, int baud = 115200)
    {
        Disconnect();
        _port = new SerialPort(portName, baud)
        {
            NewLine = "\n",
            ReadTimeout = 500,
            WriteTimeout = 2000,
            DtrEnable = true,
            RtsEnable = true,
            Encoding = Encoding.ASCII,
        };
        _port.Open();
        _cts = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoop(_cts.Token));
        _pollTask = Task.Run(() => PollLoop(_cts.Token));

        // Wake GRBL and flush any boot noise.
        Thread.Sleep(300);
        WriteRaw("\r\n\r\n");
        Info($"Connected to {portName} @ {baud} baud");
        ConnectionChanged?.Invoke();
    }

    public void Disconnect()
    {
        StopJobInternal(sendReset: false);
        _cts?.Cancel();
        try { _port?.Close(); } catch { /* port may already be gone */ }
        _port?.Dispose();
        _port = null;
        FailAllPending("disconnected");
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
        try { _port?.Write(s); }
        catch (Exception ex) { Error($"Serial write failed: {ex.Message}"); }
    }

    private void WriteByte(byte b)
    {
        try
        {
            var port = _port;
            if (port?.IsOpen == true) { port.BaseStream.WriteByte(b); port.BaseStream.Flush(); }
        }
        catch (Exception ex) { Error($"Serial write failed: {ex.Message}"); }
    }

    private void WriteLine(string line, bool quiet = false)
    {
        if (!quiet) Log?.Invoke(new LogEntry { Direction = LogDirection.Sent, Text = line });
        WriteRaw(line + "\n");
    }

    private void ReadLoop(CancellationToken ct)
    {
        var buf = new byte[1024];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var port = _port;
                if (port is null || !port.IsOpen) { Thread.Sleep(100); continue; }
                int n = port.BaseStream.Read(buf, 0, buf.Length);
                for (int i = 0; i < n; i++)
                {
                    char c = (char)buf[i];
                    if (c == '\n' || c == '\r')
                    {
                        if (_rxLine.Length > 0)
                        {
                            var line = _rxLine.ToString();
                            _rxLine.Clear();
                            try { HandleLine(line); }
                            catch (Exception ex) { Error($"Parse error on '{line}': {ex.Message}"); }
                        }
                    }
                    else _rxLine.Append(c);
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

    private void PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (IsConnected) WriteRaw("?");
            Thread.Sleep(StatusPollMs);
        }
    }

    private void HandleLine(string line)
    {
        // Status reports are frequent — don't log them.
        if (line.StartsWith('<'))
        {
            if (GrblStatus.TryParse(line, Status, out var st))
            {
                Status = st;
                StatusUpdated?.Invoke(st);
            }
            return;
        }

        Log?.Invoke(new LogEntry { Direction = LogDirection.Received, Text = line });

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
