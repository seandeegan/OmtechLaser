using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
    /// <summary>Every line has been accepted; the machine is finishing its last moves.</summary>
    public bool Finishing { get; init; }
    public int Errors { get; init; }
    public TimeSpan Elapsed { get; init; }
    public double Percent => TotalLines == 0 ? 0 : 100.0 * AckedLines / TotalLines;
}

/// <summary>How a job ended. <see cref="Completed"/> is false when it was stopped, aborted by an alarm, or the connection dropped.</summary>
public sealed record JobResult(bool Completed, int TotalLines, int AckedLines, int Errors, TimeSpan Elapsed, string Reason);

/// <summary>
/// GRBL 1.1 controller: serial connection, status polling, command/response
/// tracking, and character-counting job streaming (the same protocol approach
/// proven by LaserGRBL / grbl's stream.py).
/// </summary>
public sealed partial class GrblController : IDisposable
{
    /// <summary>GRBL 1.1 serial RX buffer.</summary>
    public const int RxBufferSize = 127;
    /// <summary>Longest line GRBL accepts (LINE_BUFFER_SIZE 80, minus the terminator).</summary>
    public const int MaxLineLength = 79;

    private const int StatusPollMs = 200;
    private const int InitialConnectWaitMs = 2500;
    private const int ResetRetryWaitMs = 4000;
    private static readonly TimeSpan StallWarning = TimeSpan.FromSeconds(10);

    private readonly Func<string, int, ISerialTransport> _transportFactory;
    private ISerialTransport? _port;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private Task? _pollTask;
    private TaskCompletionSource<bool>? _connectionReady;
    private volatile bool _controllerResponsive;
    private readonly ManualResetEventSlim _helloSeen = new(false);

    private readonly object _sync = new();
    private readonly object _connectionSync = new();
    private readonly object _writeSync = new();
    private readonly object _overrideSync = new();

    // Every line written to GRBL that still awaits its ok/error, in send order.
    // GRBL answers strictly in order, so the head of this queue owns the next
    // response — whether it is an interactive command or a streamed job line.
    // Its byte total is what currently occupies GRBL's RX buffer.
    private readonly Queue<InFlight> _inflight = new();
    private int _inflightBytes;
    // A line that writes GRBL's EEPROM is in flight: send nothing else until it's answered.
    private bool _syncLineInFlight;
    // Interactive commands waiting for room in GRBL's RX buffer.
    private readonly Queue<PendingCommand> _outbox = new();

    // Streaming job state (guarded by _sync)
    private List<string>? _jobLines;
    private int _jobId;
    private int _jobTotalLines;
    private int _jobNextLine;                 // next line index to send
    private int _jobAckedLines;               // lines acknowledged with ok/error
    private int _jobErrors;
    private volatile bool _jobRunning;
    private volatile bool _jobPaused;
    private bool _jobFinishing;
    private DateTime _jobStart;
    private TimeSpan _lastJobElapsed;
    private System.Timers.Timer? _progressTimer;
    private DateTime? _idleWithWorkSince;
    private bool _stallReported;

    private CancellationTokenSource? _feedOverrideCts;
    private CancellationTokenSource? _powerOverrideCts;

    private sealed class PendingCommand(string original, string line, TaskCompletionSource<GrblResponse> tcs)
    {
        public string Original { get; } = original;
        public string Line { get; } = line;
        public int Bytes { get; } = line.Length + 1; // +\n
        public TaskCompletionSource<GrblResponse> Tcs { get; } = tcs;
        /// <summary>The caller gave up waiting; if it hasn't been sent yet, it never will be.</summary>
        public volatile bool Abandoned;
    }

    private sealed record InFlight(int Bytes, PendingCommand? Command, string? JobLine, bool Sync);

    private sealed record JobAck(string? Log, bool PauseForError, bool AbortLocked, int LineNumber, string Line, string ErrorText);

    public sealed record GrblResponse(bool Ok, int ErrorCode, string ErrorText);

    private int _bytesReceived;
    private int _linesReceived;

    private bool IsPortOpen => _port?.IsOpen == true;
    public bool IsConnected => _controllerResponsive && IsPortOpen;
    public bool IsJobRunning => _jobRunning;
    public bool IsJobPaused => _jobPaused;
    private volatile GrblStatus _status = new() { State = MachineState.Disconnected };
    public GrblStatus Status => _status;
    private long _lastStatusTicks;
    /// <summary>UTC time of the last status report (GRBL is silent while homing).</summary>
    public DateTime LastStatusTimeUtc => new(Interlocked.Read(ref _lastStatusTicks), DateTimeKind.Utc);
    public string FirmwareVersion { get; private set; } = "";
    public string PortName => _port?.PortName ?? "";

    /// <summary>Snapshot of the controller's '$' settings.</summary>
    public IReadOnlyDictionary<int, double> Settings
    {
        get { lock (_sync) return new SortedDictionary<int, double>(_settings); }
    }
    private readonly SortedDictionary<int, double> _settings = new();
    private readonly Dictionary<string, double[]> _offsets = new(StringComparer.OrdinalIgnoreCase);

    public event Action<LogEntry>? Log;
    public event Action<GrblStatus>? StatusUpdated;
    public event Action<int, string>? AlarmRaised;           // code, description
    public event Action<JobProgress>? JobProgressChanged;
    public event Action<JobResult>? JobFinished;
    public event Action? SettingsRefreshed;
    public event Action? ConnectionChanged;
    /// <summary>The port vanished unexpectedly (USB unplugged, laser powered off).</summary>
    public event Action<string>? ConnectionLost;
    /// <summary>The controller rejected a job line; the job has been paused. (line number, line, error text)</summary>
    public event Action<int, string, string>? JobLineRejected;
    /// <summary>The machine has sat idle for a while with commands still unanswered.</summary>
    public event Action? JobStalled;

    public GrblController() : this(null) { }

    /// <param name="transportFactory">Creates the transport for (port name, baud). Null = real serial port.</param>
    public GrblController(Func<string, int, ISerialTransport>? transportFactory)
    {
        _transportFactory = transportFactory ?? ((name, baud) => new SerialPortTransport(name, baud));
    }

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

        var port = _transportFactory(portName, baud);
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
            _helloSeen.Reset();
            WriteBytes(port, 0x18);
            if (WaitForController(ready, InitialConnectWaitMs)) { AwaitHello(); return; }
            ThrowIfConnectionWasClosed(port);

            // Retry the hardware-reset transition used by Arduino/CH340-style
            // boards, but release the lines again so they cannot hold the MCU
            // in reset.
            Info("No reply with DTR/RTS off; trying a brief USB control-line reset…");
            PulseResetLines(port);
            Thread.Sleep(300);
            _helloSeen.Reset();
            WriteBytes(port, 0x18);
            if (WaitForController(ready, ResetRetryWaitMs)) { AwaitHello(); return; }
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
                // Already torn down (e.g. the read loop saw the device vanish).
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
                try { port.Close(); } catch { }
                port.Dispose();
                cts.Dispose();
            }
            throw;
        }
    }

    public void Disconnect() => DisconnectCore(expected: null);

    /// <summary>Closes the connection — only if it is still <paramref name="expected"/> (when given).</summary>
    private bool DisconnectCore(ISerialTransport? expected)
    {
        ISerialTransport? port;
        CancellationTokenSource? cts;
        TaskCompletionSource<bool>? ready;
        Task? readTask;
        Task? pollTask;
        lock (_connectionSync)
        {
            if (expected is not null && !ReferenceEquals(_port, expected)) return false;
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

        EndJob(completed: false, "disconnected", sendReset: false);
        ready?.TrySetResult(false);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { port?.Close(); } catch { /* port may already be gone */ }
        try
        {
            var tasks = new[] { readTask, pollTask }.Where(t => t is not null).Cast<Task>().ToArray();
            if (tasks.Length > 0) Task.WaitAll(tasks, 1000);
        }
        catch { /* cancellation/port closure can fault a worker while disconnecting */ }
        try { port?.Dispose(); } catch { }
        cts?.Dispose();
        FailAllPending("disconnected");
        FirmwareVersion = "";
        lock (_sync)
        {
            _settings.Clear();
            _offsets.Clear();
        }
        _status = new GrblStatus { State = MachineState.Disconnected };
        StatusUpdated?.Invoke(_status);
        ConnectionChanged?.Invoke();
        return true;
    }

    // ---------- Realtime commands (bypass the buffer, act immediately) ----------

    /// <summary>
    /// Emergency stop: GRBL soft-reset (Ctrl-X). Halts everything instantly and
    /// turns the laser off. The reset byte goes out FIRST, whenever the port is
    /// open — even if GRBL has not answered yet. Returns false if it could not
    /// be sent: then only the machine's power switch can stop it.
    /// </summary>
    public bool EmergencyStop()
    {
        bool sent = WriteBytes(0x18);
        EndJob(completed: false, "emergency stop", sendReset: false);
        FailAllPending("emergency stop");
        if (sent) Info("EMERGENCY STOP (soft reset) sent");
        else if (_port is not null)
            Error("EMERGENCY STOP could not be sent — the laser isn't answering. Switch it off at its power switch!");
        return sent;
    }

    public void SoftReset() { WriteBytes(0x18); FailAllPending("reset"); }
    public void FeedHold() => WriteBytes((byte)'!');
    public void CycleResume() => WriteBytes((byte)'~');
    public void JogCancel() => WriteBytes(0x85);
    public void QueryStatus() => WriteBytes((byte)'?');

    /// <summary>Live feed-rate override, 10–200 % (applies immediately, even mid-job).</summary>
    public Task SetFeedOverrideAsync(int percent) =>
        RunOverride(ref _feedOverrideCts, OverrideSequence(percent, 0x90, 0x91, 0x92, 0x93, 0x94));

    /// <summary>Live laser-power override, 10–200 % of the programmed S value.</summary>
    public Task SetPowerOverrideAsync(int percent) =>
        RunOverride(ref _powerOverrideCts, OverrideSequence(percent, 0x99, 0x9A, 0x9B, 0x9C, 0x9D));

    /// <summary>Puts feed, rapid and laser-power overrides back to 100 % and stops any change in progress.</summary>
    public void ResetOverrides()
    {
        lock (_overrideSync)
        {
            _feedOverrideCts?.Cancel();
            _powerOverrideCts?.Cancel();
            _feedOverrideCts = _powerOverrideCts = null;
            WriteBytes(0x90, 0x95, 0x99);
        }
    }

    /// <summary>
    /// GRBL only offers "reset to 100 %", ±10 % and ±1 % override commands, so a
    /// target is reached by a reset followed by steps.
    /// </summary>
    public static List<byte> OverrideSequence(int percent, byte reset, byte up10, byte down10, byte up1, byte down1)
    {
        var target = Math.Clamp(percent, 10, 200);
        var seq = new List<byte> { reset };
        int cur = 100;
        while (target - cur >= 10) { seq.Add(up10); cur += 10; }
        while (cur - target >= 10) { seq.Add(down10); cur -= 10; }
        while (cur < target) { seq.Add(up1); cur++; }
        while (cur > target) { seq.Add(down1); cur--; }
        return seq;
    }

    private Task RunOverride(ref CancellationTokenSource? slot, List<byte> sequence)
    {
        var cts = new CancellationTokenSource();
        lock (_overrideSync)
        {
            slot?.Cancel();
            slot = cts;
        }
        return SendOverrideSequenceAsync(sequence, cts.Token);
    }

    private async Task SendOverrideSequenceAsync(List<byte> sequence, CancellationToken ct)
    {
        try
        {
            foreach (var b in sequence)
            {
                // Checking and writing under the lock means a cancelled sequence can
                // never slip a byte in after a newer reset.
                lock (_overrideSync)
                {
                    if (ct.IsCancellationRequested) return;
                    WriteBytes(b);
                }
                // GRBL latches override requests as flags, so identical requests sent
                // back-to-back collapse into one. Space them out so each one counts.
                await Task.Delay(40, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* superseded by a newer target */ }
    }

    // ---------- Interactive commands ----------

    /// <summary>
    /// Why a (cleaned) line can't be sent to GRBL, or null if it can. '?', '!'
    /// and '~' are realtime commands that GRBL acts on wherever they appear.
    /// </summary>
    public static string? ValidateLine(string line)
    {
        if (line.Length == 0) return "There's nothing to send (only a comment?).";
        if (line.Length > MaxLineLength) return $"The command is too long for GRBL ({line.Length} characters, max {MaxLineLength}).";
        foreach (var c in line)
        {
            if (c is '?' or '!' or '~')
                return $"The command contains '{c}', which GRBL treats as an instant command ('?' status, '!' pause, '~' resume) wherever it appears.";
            if (c > 0x7E) return "The command contains characters GRBL doesn't understand.";
        }
        return null;
    }

    /// <summary>Settings and work offsets live in EEPROM; GRBL can drop serial data while writing it.</summary>
    public static bool WritesEeprom(string line) => EepromPattern().IsMatch(line);

    [GeneratedRegex(@"^\$(?!J=)[^=]*=|G0*10L0*20?(?!\d)|L0*20?(?!\d).*G0*10(?!\d)|G0*(28|30)\.1", RegexOptions.IgnoreCase)]
    private static partial Regex EepromPattern();

    /// <summary>Send a single command and await its ok/error response.</summary>
    public Task<GrblResponse> SendAsync(string command, int timeoutMs = 10000)
    {
        var tcs = new TaskCompletionSource<GrblResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var line = CleanLine(command);
        if (ValidateLine(line) is { } problem)
        {
            tcs.SetResult(new GrblResponse(false, -1, problem));
            return tcs.Task;
        }
        var pc = new PendingCommand(command.Trim(), line, tcs);
        lock (_sync)
        {
            if (!IsConnected)
            {
                tcs.SetResult(new GrblResponse(false, -1, "Not connected"));
                return tcs.Task;
            }
            if (_jobRunning)
            {
                tcs.SetResult(new GrblResponse(false, -1, "A job is running — stop it before sending manual commands."));
                return tcs.Task;
            }
            _outbox.Enqueue(pc);
            PumpOutbox();
        }
        return WithTimeout(pc, timeoutMs);
    }

    // Interactive commands share GRBL's RX budget with everything else, so a burst
    // of jog clicks can't overflow it. Caller holds _sync.
    private void PumpOutbox()
    {
        while (_outbox.Count > 0 && !_syncLineInFlight)
        {
            var pc = _outbox.Peek();
            if (pc.Abandoned) { _outbox.Dequeue(); continue; }
            bool sync = WritesEeprom(pc.Line);
            if (sync && _inflightBytes > 0) break;           // EEPROM writes go into an empty buffer
            if (_inflightBytes + pc.Bytes > RxBufferSize) break;
            _outbox.Dequeue();
            _inflight.Enqueue(new InFlight(pc.Bytes, pc, null, sync));
            _inflightBytes += pc.Bytes;
            WriteLine(pc.Line, logAs: pc.Original);
            if (sync) { _syncLineInFlight = true; break; }
        }
    }

    private static async Task<GrblResponse> WithTimeout(PendingCommand pc, int ms)
    {
        var done = await Task.WhenAny(pc.Tcs.Task, Task.Delay(ms)).ConfigureAwait(false);
        if (done == pc.Tcs.Task) return pc.Tcs.Task.Result;
        pc.Abandoned = true;
        return new GrblResponse(false, -1, $"Timeout waiting for response to '{pc.Original}'");
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

    /// <summary>Reads the work-coordinate offsets ($#), e.g. [G54:x,y,z].</summary>
    public async Task<bool> RefreshOffsetsAsync()
    {
        var r = await SendAsync("$#", 5000).ConfigureAwait(false);
        return r.Ok;
    }

    /// <summary>An offset reported by $# (G54…G59, G28, G30, G92, TLO) as [x, y, z].</summary>
    public double[]? GetOffset(string name)
    {
        lock (_sync) return _offsets.TryGetValue(name, out var v) ? (double[])v.Clone() : null;
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

    /// <summary>
    /// Strips comments and whitespace. GRBL ignores both, but they would still
    /// occupy its small serial buffer and slow streaming down.
    /// </summary>
    public static string CleanLine(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        bool inParen = false;
        foreach (var c in raw)
        {
            if (inParen) { if (c == ')') inParen = false; continue; }
            if (c == '(') { inParen = true; continue; }
            if (c == ';') break;
            if (c <= ' ' || c == '%') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    public void StartJob(IEnumerable<string> gcodeLines)
    {
        var lines = new List<string>();
        foreach (var raw in gcodeLines)
        {
            var l = CleanLine(raw);
            if (l.Length == 0) continue;
            // A line that can never fit GRBL's buffer would stall the stream forever,
            // and a stray '!' or '~' would pause or resume the machine.
            if (ValidateLine(l) is { } problem)
                throw new InvalidOperationException($"G-code line {lines.Count + 1} can't be sent ({l[..Math.Min(40, l.Length)]}): {problem}");
            lines.Add(l);
        }
        if (lines.Count == 0) throw new InvalidOperationException("The job contains no G-code commands.");
        // A dwell of 0 waits for every move to finish before it is acknowledged,
        // so the final "ok" really means the machine has stopped.
        lines.Add("G4P0");

        ResetOverrides(); // every job starts at 100 % speed and power
        lock (_sync)
        {
            if (!IsConnected) throw new InvalidOperationException("Not connected.");
            if (_jobRunning) throw new InvalidOperationException("A job is already running.");
            if (_status.State == MachineState.Alarm)
                throw new InvalidOperationException("The laser is locked (alarm). Home or unlock it, then start the job again.");
            if (_outbox.Count > 0)
                throw new InvalidOperationException("The laser is still busy with earlier commands — try again in a moment.");
            _jobLines = lines;
            _jobId++;
            _jobTotalLines = lines.Count;
            _jobNextLine = 0;
            _jobAckedLines = 0;
            _jobErrors = 0;
            _jobPaused = false;
            _jobFinishing = false;
            _jobRunning = true;
            _jobStart = DateTime.Now;
            _idleWithWorkSince = null;
            _stallReported = false;

            var timer = new System.Timers.Timer(500) { AutoReset = true };
            timer.Elapsed += (_, _) => RaiseProgress();
            _progressTimer = timer;
            timer.Start();
            FillStreamBuffer();
        }
        Info($"Job started: {lines.Count} lines");
        RaiseProgress();
    }

    public void PauseJob()
    {
        if (!_jobRunning) return;
        _jobPaused = true;
        FeedHold();
        Info("Job paused (feed hold)");
        RaiseProgress();
    }

    public void ResumeJob()
    {
        if (!_jobRunning) return;
        _jobPaused = false;
        CycleResume();
        lock (_sync) FillStreamBuffer();
        Info("Job resumed");
        RaiseProgress();
        // GRBL ignores '~' while a hold is still slowing down (Hold:1): repeat it
        // until the machine is running again. Extra '~' are harmless.
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 8; i++)
            {
                await Task.Delay(250).ConfigureAwait(false);
                if (!_jobRunning || _jobPaused || _status.State != MachineState.Hold) return;
                CycleResume();
            }
        });
    }

    /// <summary>Stop the job instantly with a soft reset (may lose position if moving).</summary>
    public void StopJob() => EndJob(completed: false, "stopped", sendReset: true);

    /// <summary>
    /// Stop the job without losing position: feed hold (decelerate), wait for the
    /// hold to complete, then soft-reset to flush GRBL's buffers. A reset while
    /// stationary keeps the machine position, so no re-home is needed.
    /// </summary>
    public async Task StopJobAsync()
    {
        EndJob(completed: false, "stopped by user", sendReset: false);
        if (!IsPortOpen) return;
        var holdSent = DateTime.UtcNow;
        FeedHold();
        QueryStatus();
        var deadline = holdSent.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(60).ConfigureAwait(false);
            // Only trust reports made after the hold was requested.
            if (LastStatusTimeUtc <= holdSent.AddMilliseconds(20)) continue;
            var st = _status;
            if (st.State is MachineState.Idle or MachineState.Alarm or MachineState.Door or MachineState.Disconnected) break;
            if (st.State == MachineState.Hold && st.SubState.EndsWith(":0", StringComparison.Ordinal)) break;
        }
        SoftReset();
        Info("Motion halted with a feed hold, then GRBL was reset to clear its buffer (position kept).");
    }

    // Send as many job lines as fit in GRBL's RX buffer (character counting). Caller holds _sync.
    private void FillStreamBuffer()
    {
        if (!_jobRunning || _jobFinishing || _jobPaused || _jobLines is null) return;
        while (_jobNextLine < _jobLines.Count && !_syncLineInFlight)
        {
            var line = _jobLines[_jobNextLine];
            var len = line.Length + 1; // +\n
            bool sync = WritesEeprom(line);
            if (sync && _inflightBytes > 0) break;   // EEPROM writes go into an empty buffer, alone
            if (_inflightBytes + len > RxBufferSize) break;
            _inflight.Enqueue(new InFlight(len, null, line, sync));
            _inflightBytes += len;
            _jobNextLine++;
            WriteLine(line, quiet: true);
            if (sync) { _syncLineInFlight = true; break; }
        }
    }

    // Caller holds _sync.
    private JobAck? OnJobLineAcked(bool ok, int errorCode, string jobLine)
    {
        if (!_jobRunning || _jobLines is null) return null;
        _jobAckedLines++;
        JobAck? ack = null;
        if (!ok)
        {
            _jobErrors++;
            var text = GrblCodes.ErrorText(errorCode);
            ack = new JobAck($"Job line {_jobAckedLines} '{jobLine}': {text}",
                PauseForError: !_jobPaused && errorCode != 9,
                AbortLocked: errorCode == 9,
                _jobAckedLines, jobLine, text);
        }
        if (_jobAckedLines >= _jobTotalLines)
            _jobFinishing = true; // complete once the machine reports Idle
        else
            FillStreamBuffer();
        return ack;
    }

    private void CompleteJobIfFinishing()
    {
        int id;
        lock (_sync)
        {
            if (!(_jobRunning && _jobFinishing && !_jobPaused)) return;
            id = _jobId;
        }
        EndJob(completed: true, "complete", sendReset: false, expectedJobId: id);
    }

    // Warns once if the machine sits idle while lines are still unanswered (e.g. a lost "ok").
    private void CheckForStall(GrblStatus st)
    {
        bool report = false;
        lock (_sync)
        {
            bool waiting = _jobRunning && !_jobPaused && !_jobFinishing && _inflight.Count > 0 && st.State == MachineState.Idle;
            if (!waiting) { _idleWithWorkSince = null; return; }
            _idleWithWorkSince ??= DateTime.UtcNow;
            if (!_stallReported && DateTime.UtcNow - _idleWithWorkSince.Value > StallWarning)
                report = _stallReported = true;
        }
        if (!report) return;
        Error("The laser has been idle for 10 seconds while LaserPilot is still waiting for it to confirm commands.");
        JobStalled?.Invoke();
    }

    private void EndJob(bool completed, string reason, bool sendReset, int? expectedJobId = null)
    {
        JobResult? result = null;
        System.Timers.Timer? timer;
        lock (_sync)
        {
            if (expectedJobId is { } id && id != _jobId) return;
            timer = _progressTimer;
            _progressTimer = null;
            if (_jobRunning)
            {
                _lastJobElapsed = DateTime.Now - _jobStart;
                result = new JobResult(completed, _jobTotalLines, _jobAckedLines, _jobErrors, _lastJobElapsed, reason);
            }
            _jobRunning = false;
            _jobFinishing = false;
            _jobPaused = false;
            _jobLines = null;
        }
        timer?.Stop();
        timer?.Dispose();
        if (sendReset && IsPortOpen) SoftReset();
        if (result is null) return;
        // Overrides outlive the job in GRBL; a 200 % power override must not carry over to the aiming dot.
        ResetOverrides();
        Info(completed
            ? $"Job complete: {result.TotalLines} lines in {result.Elapsed:hh\\:mm\\:ss}" + (result.Errors > 0 ? $" ({result.Errors} line(s) rejected)" : "")
            : $"Job {reason} after {result.AckedLines}/{result.TotalLines} lines ({result.Elapsed:hh\\:mm\\:ss})");
        RaiseProgress();
        JobFinished?.Invoke(result);
    }

    private void RaiseProgress()
    {
        JobProgress p;
        lock (_sync)
        {
            p = new JobProgress
            {
                TotalLines = _jobTotalLines,
                AckedLines = _jobAckedLines,
                Running = _jobRunning,
                Paused = _jobPaused,
                Finishing = _jobFinishing,
                Errors = _jobErrors,
                Elapsed = _jobRunning ? DateTime.Now - _jobStart : _lastJobElapsed,
            };
        }
        JobProgressChanged?.Invoke(p);
    }

    // ---------- Serial I/O ----------

    /// <summary>Writes realtime bytes. Returns false if they could not be sent.</summary>
    private bool WriteBytes(params byte[] bytes)
    {
        var port = _port;
        return port is not null && WriteBytes(port, bytes);
    }

    private bool WriteBytes(ISerialTransport port, params byte[] bytes)
    {
        try
        {
            lock (_writeSync)
            {
                if (!port.IsOpen) return false;
                port.Write(bytes, 0, bytes.Length);
                port.Flush();
                return true;
            }
        }
        catch (Exception ex)
        {
            Error($"Serial write failed: {ex.Message}");
            return false;
        }
    }

    private void WriteLine(string line, bool quiet = false, string? logAs = null)
    {
        if (!quiet) Log?.Invoke(new LogEntry { Direction = LogDirection.Sent, Text = logAs ?? line });
        WriteBytes(Encoding.ASCII.GetBytes(line + "\n"));
    }

    private void ReadLoop(ISerialTransport port, CancellationToken ct)
    {
        var buf = new byte[1024];
        var rxLine = new StringBuilder();
        var timer = new Stopwatch();
        int failures = 0, instantEmptyReads = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!port.IsOpen)
                {
                    LoseConnection(port, "The connection to the laser was closed (USB cable unplugged or laser switched off?).");
                    return;
                }
                timer.Restart();
                int n = port.Read(buf, 0, buf.Length);
                if (n <= 0)
                {
                    // A timeout returns 0 after ReadTimeout. Returning 0 *instantly*,
                    // over and over, means the device is gone (end of stream).
                    if (timer.ElapsedMilliseconds < 50)
                    {
                        if (++instantEmptyReads >= 20)
                        {
                            LoseConnection(port, "The laser stopped responding (USB cable unplugged or laser switched off?).");
                            return;
                        }
                        Thread.Sleep(10);
                    }
                    else instantEmptyReads = 0;
                    continue;
                }
                instantEmptyReads = 0;
                failures = 0;
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
                    else if (rxLine.Length < 512) rxLine.Append(c);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (TimeoutException) { /* normal */ }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                if (++failures >= 3)
                {
                    LoseConnection(port, $"Lost contact with the laser: {ex.Message}");
                    return;
                }
                Error($"Serial read error: {ex.Message}");
                Thread.Sleep(250);
            }
        }
    }

    private void LoseConnection(ISerialTransport port, string reason)
    {
        // Disconnect() waits for this read loop, so tear down from another thread.
        _ = Task.Run(() =>
        {
            var hadJob = _jobRunning;
            if (!DisconnectCore(expected: port)) return; // already disconnected on purpose
            Error(reason);
            ConnectionLost?.Invoke(hadJob
                ? reason + " The job was interrupted — if the laser is still moving or burning, switch it off at its power switch."
                : reason);
        });
    }

    private void PollLoop(ISerialTransport port, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!port.IsOpen) return;
            WriteBytes(port, (byte)'?');
            if (ct.WaitHandle.WaitOne(StatusPollMs)) return;
        }
    }

    private void HandleLine(ISerialTransport source, string line)
    {
        Interlocked.Increment(ref _linesReceived);
        line = line.Trim();
        if (line.Length == 0) return;

        // Status reports are frequent — don't log them.
        if (line.StartsWith('<'))
        {
            if (GrblStatus.TryParse(line, _status, out var st))
            {
                MarkControllerResponsive(source);
                _status = st;
                Interlocked.Exchange(ref _lastStatusTicks, DateTime.UtcNow.Ticks);
                StatusUpdated?.Invoke(st);
                if (st.State is MachineState.Idle or MachineState.Check) CompleteJobIfFinishing();
                if (_jobRunning) CheckForStall(st);
            }
            return;
        }

        Log?.Invoke(new LogEntry { Direction = LogDirection.Received, Text = line });
        if (IsRecognizedGrblLine(line)) MarkControllerResponsive(source);

        if (line.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            AckLine(true, 0);
        }
        else if (line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
        {
            int.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code);
            AckLine(false, code);
        }
        else if (line.StartsWith("ALARM:", StringComparison.OrdinalIgnoreCase))
        {
            int.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code);
            var text = GrblCodes.AlarmText(code);
            Error($"ALARM {code}: {text}");
            // Alarms reset GRBL's buffers: anything unacknowledged will never get an ok.
            EndJob(completed: false, $"aborted by ALARM {code}", sendReset: false);
            FailAllPending($"ALARM:{code} {text}");
            AlarmRaised?.Invoke(code, text);
        }
        else if (line.StartsWith("Grbl", StringComparison.OrdinalIgnoreCase))
        {
            FirmwareVersion = line;
            Info($"GRBL reset/hello: {line}");
            EndJob(completed: false, "interrupted by a controller reset", sendReset: false);
            FailAllPending("controller reset");
            _helloSeen.Set();
        }
        else if (line.StartsWith('$') && line.Contains('='))
        {
            ParseSettingLine(line);
        }
        else if (line.StartsWith('[') && line.EndsWith(']'))
        {
            ParseBracketLine(line);
        }
        // [MSG:...], [VER:...], [OPT:...] and other feedback — already logged above.
    }

    private void ParseSettingLine(string line)
    {
        var eq = line.IndexOf('=');
        if (eq < 2) return;
        var value = line[(eq + 1)..];
        // GRBL 0.9 appends a description: "$0=10 (step pulse, usec)".
        var cut = value.IndexOfAny(new[] { ' ', '(' });
        if (cut >= 0) value = value[..cut];
        if (int.TryParse(line.AsSpan(1, eq - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
        {
            lock (_sync) _settings[num] = val;
        }
    }

    // $# output, e.g. [G54:0.000,0.000,0.000], [G92:0.000,0.000,0.000], [TLO:0.000].
    private void ParseBracketLine(string line)
    {
        var body = line[1..^1];
        var colon = body.IndexOf(':');
        if (colon <= 0) return;
        var name = body[..colon];
        if (!(name.Length >= 2 && name[0] == 'G' && char.IsDigit(name[1])) && name != "TLO") return;
        var parts = body[(colon + 1)..].Split(',');
        var values = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])) return;
        lock (_sync) _offsets[name] = values;
    }

    private static bool IsRecognizedGrblLine(string line) =>
        line.Equals("ok", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Grbl", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("ALARM:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith('$') ||
        line.StartsWith("[MSG:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("[VER:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("[OPT:", StringComparison.OrdinalIgnoreCase);

    private void MarkControllerResponsive(ISerialTransport source)
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

    // A status report can prove the board is alive before the hello from our
    // connect-time reset arrives. That hello fails every pending command, so
    // let it land before anyone can send one.
    private void AwaitHello() => _helloSeen.Wait(1500);

    private void ThrowIfConnectionWasClosed(ISerialTransport port)
    {
        if (!ReferenceEquals(_port, port) || !port.IsOpen)
            throw new IOException("The serial connection was closed while waiting for GRBL.");
    }

    private void PulseResetLines(ISerialTransport port)
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

    private void AckLine(bool ok, int errorCode)
    {
        PendingCommand? command = null;
        JobAck? jobAck = null;
        lock (_sync)
        {
            if (_inflight.Count == 0) return; // stray response (e.g. sent before a reset)
            var item = _inflight.Dequeue();
            _inflightBytes = Math.Max(0, _inflightBytes - item.Bytes);
            if (item.Sync) _syncLineInFlight = false;
            if (item.Command is not null) command = item.Command;
            else if (item.JobLine is not null) jobAck = OnJobLineAcked(ok, errorCode, item.JobLine);
            if (_jobRunning) FillStreamBuffer();
            PumpOutbox();
        }
        if (jobAck is { } a)
        {
            Error(a.Log!);
            if (a.AbortLocked)
            {
                EndJob(completed: false, "stopped because the laser is locked (alarm) — home or unlock it, then start again", sendReset: false);
            }
            else if (a.PauseForError)
            {
                // A rejected travel move would make the next burn start from the wrong place.
                PauseJob();
                JobLineRejected?.Invoke(a.LineNumber, a.Line, a.ErrorText);
            }
        }
        if (command is not null)
        {
            if (!ok) Error($"{command.Original}: {GrblCodes.ErrorText(errorCode)}");
            command.Tcs.TrySetResult(new GrblResponse(ok, errorCode, ok ? "" : GrblCodes.ErrorText(errorCode)));
        }
    }

    private void FailAllPending(string reason)
    {
        List<PendingCommand> failed = new();
        lock (_sync)
        {
            while (_inflight.Count > 0)
            {
                var item = _inflight.Dequeue();
                if (item.Command is not null) failed.Add(item.Command);
            }
            while (_outbox.Count > 0) failed.Add(_outbox.Dequeue());
            _inflightBytes = 0;
            _syncLineInFlight = false;
        }
        foreach (var p in failed)
            p.Tcs.TrySetResult(new GrblResponse(false, -1, reason));
    }

    private void Info(string msg) => Log?.Invoke(new LogEntry { Direction = LogDirection.Info, Text = msg });
    private void Error(string msg) => Log?.Invoke(new LogEntry { Direction = LogDirection.Error, Text = msg });

    public void Dispose() => Disconnect();
}
