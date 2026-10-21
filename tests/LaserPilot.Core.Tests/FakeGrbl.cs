using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using LaserPilot.Core.Comm;

namespace LaserPilot.Core.Tests;

/// <summary>
/// A simulated GRBL 1.1 controller behind <see cref="ISerialTransport"/>.
/// Like the real firmware it has a 127-byte serial RX buffer, intercepts
/// realtime bytes immediately, answers every line with ok/error in order,
/// and reports Run until its (simulated) motion has finished. If the host
/// ever sends more than the buffer holds, <see cref="Overflowed"/> is set.
/// </summary>
internal sealed class FakeGrbl : ISerialTransport
{
    public const int RxCapacity = 127;

    private readonly object _lock = new();
    private readonly StringBuilder _rx = new();
    private readonly ConcurrentQueue<byte> _tx = new();
    private readonly SemaphoreSlim _txSignal = new(0);
    private Thread? _worker;
    private volatile bool _open;
    private volatile bool _unplugged;
    private DateTime _busyUntil = DateTime.MinValue;
    private double _g54X, _g54Y;

    public string PortName => "SIM";
    public bool IsOpen => _open && !_unplugged;
    public bool DtrEnable { get; set; }
    public bool RtsEnable { get; set; }

    public bool Overflowed { get; private set; }
    public int MaxRxUsed { get; private set; }
    public List<string> ReceivedLines { get; } = new();
    public List<byte> RealtimeBytes { get; } = new();
    public List<byte> RealtimeSnapshot() { lock (_lock) return RealtimeBytes.ToList(); }
    public void ClearRealtime() { lock (_lock) RealtimeBytes.Clear(); }
    public List<DateTime> LineTimes { get; } = new();
    public bool Held { get; private set; }
    public string? ForcedState { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Return e.g. "error:20" to reject a line, or "ALARM:2" to raise an alarm.</summary>
    public Func<string, string?>? Respond { get; set; }
    public int LineDelayMs { get; set; } = 1;
    public int HelloDelayMs { get; set; } = 40;
    /// <summary>How long the machine keeps moving after its last line was accepted.</summary>
    public int MotionTailMs { get; set; } = 100;
    public bool Grbl09SettingsFormat { get; set; }
    public bool ReportMachinePosition { get; set; } = true;
    /// <summary>Where homing leaves the machine (machine coordinates).</summary>
    public (double X, double Y) HomePosition { get; set; } = (-398, -398);
    public Dictionary<int, double> Settings { get; } = new()
    {
        [10] = 1, [11] = 0.01, [20] = 0, [21] = 0, [22] = 1, [23] = 3, [24] = 100, [25] = 1500, [26] = 250, [27] = 2,
        [30] = 1000, [31] = 0, [32] = 1, [100] = 80, [101] = 80, [110] = 6000, [111] = 6000, [120] = 1000, [121] = 1000,
        [130] = 400, [131] = 400,
    };

    public void Open()
    {
        _open = true;
        _worker = new Thread(Work) { IsBackground = true, Name = "FakeGrbl" };
        _worker.Start();
    }

    public void Close() => _open = false;
    public void Dispose() => _open = false;
    public void DiscardInBuffer() { while (_tx.TryDequeue(out _)) { } }
    public void DiscardOutBuffer() { }
    public void Flush() { }

    /// <summary>Simulates pulling the USB cable: the port closes and reads return nothing.</summary>
    public void Unplug() => _unplugged = true;

    /// <summary>The device is gone but the port still claims to be open: reads return 0 instantly.</summary>
    public void UnplugSilently() => _silentlyGone = true;
    private volatile bool _silentlyGone;

    /// <summary>Set when bytes arrived while GRBL was writing its EEPROM (they'd be lost on real hardware).</summary>
    public bool EepromViolation { get; private set; }
    private volatile bool _eepromWriting;

    public int Read(byte[] buffer, int offset, int count)
    {
        if (_unplugged || !_open || _silentlyGone) return 0;
        // Block until data arrives or the 100 ms read timeout passes, like a real port.
        var deadline = DateTime.UtcNow.AddMilliseconds(100);
        while (_tx.IsEmpty)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return 0;
            _txSignal.Wait(left);
        }
        int n = 0;
        while (n < count && _tx.TryDequeue(out var b)) buffer[offset + n++] = b;
        return n;
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        if (_unplugged || _silentlyGone) throw new IOException("device removed");
        for (int i = offset; i < offset + count; i++)
        {
            byte b = buffer[i];
            // GRBL's serial ISR picks realtime commands out of the stream.
            if (b is (byte)'?' or (byte)'!' or (byte)'~' or 0x18 || b >= 0x80)
            {
                Realtime(b);
                continue;
            }
            lock (_lock)
            {
                // Interrupts are off while GRBL writes EEPROM: incoming bytes are lost.
                if (_eepromWriting) { EepromViolation = true; continue; }
                _rx.Append((char)b);
                if (_rx.Length > RxCapacity) Overflowed = true;
                MaxRxUsed = Math.Max(MaxRxUsed, _rx.Length);
            }
        }
    }

    private void Realtime(byte b)
    {
        lock (_lock) RealtimeBytes.Add(b);
        switch (b)
        {
            case (byte)'?':
                Send(StatusReport());
                break;
            case (byte)'!':
                lock (_lock) Held = true;
                break;
            case (byte)'~':
                lock (_lock) Held = false;
                break;
            case 0x18:
                lock (_lock)
                {
                    _rx.Clear();
                    Held = false;
                    _busyUntil = DateTime.MinValue;
                    if (ForcedState == "Alarm") ForcedState = null;
                }
                // Real GRBL reboots for a moment first, so a status report can beat the hello.
                if (HelloDelayMs > 0)
                    Task.Delay(HelloDelayMs).ContinueWith(_ => Send("Grbl 1.1h ['$' for help]"));
                else
                    Send("Grbl 1.1h ['$' for help]");
                break;
        }
    }

    private string StatusReport()
    {
        string state;
        lock (_lock)
        {
            state = ForcedState ?? (Held ? "Hold:0"
                : DateTime.UtcNow < _busyUntil || _rx.ToString().Contains('\n') ? "Run" : "Idle");
        }
        var inv = CultureInfo.InvariantCulture;
        return ReportMachinePosition
            ? string.Create(inv, $"<{state}|MPos:{X:0.000},{Y:0.000},0.000|FS:0,0|WCO:{_g54X:0.000},{_g54Y:0.000},0.000>")
            : string.Create(inv, $"<{state}|WPos:{X - _g54X:0.000},{Y - _g54Y:0.000},0.000|FS:0,0|WCO:{_g54X:0.000},{_g54Y:0.000},0.000>");
    }

    private readonly object _txLock = new();

    private void Send(string line)
    {
        lock (_txLock)
            foreach (var b in Encoding.ASCII.GetBytes(line + "\r\n")) _tx.Enqueue(b);
        _txSignal.Release();
    }

    private void Work()
    {
        while (_open)
        {
            string? line = null;
            lock (_lock)
            {
                if (!Held)
                {
                    var text = _rx.ToString();
                    var nl = text.IndexOf('\n');
                    if (nl >= 0)
                    {
                        line = text[..nl];
                        _rx.Remove(0, nl + 1);
                    }
                }
            }
            if (line is null) { Thread.Sleep(1); continue; }
            if (LineDelayMs > 0) Thread.Sleep(LineDelayMs);
            Execute(line);
        }
    }

    private void Execute(string line)
    {
        line = new string(line.Where(c => c > ' ').ToArray()); // GRBL drops whitespace
        lock (_lock)
        {
            ReceivedLines.Add(line);
            LineTimes.Add(DateTime.UtcNow);
        }
        if (GrblController.WritesEeprom(line))
        {
            // Writing EEPROM takes a few ms per byte with interrupts disabled.
            _eepromWriting = true;
            Thread.Sleep(30);
            _eepromWriting = false;
        }
        var custom = Respond?.Invoke(line);
        if (custom is not null && custom.StartsWith("ALARM", StringComparison.Ordinal))
        {
            lock (_lock) { _rx.Clear(); ForcedState = "Alarm"; }
            Send(custom);
            return;
        }
        if (custom is not null) { Send(custom); return; }

        var inv = CultureInfo.InvariantCulture;
        switch (line)
        {
            case "$$":
                foreach (var (k, v) in Settings.OrderBy(kv => kv.Key))
                    Send(Grbl09SettingsFormat ? string.Create(inv, $"${k}={v} (description)") : string.Create(inv, $"${k}={v}"));
                break;
            case "$#":
                Send(string.Create(inv, $"[G54:{_g54X:0.000},{_g54Y:0.000},0.000]"));
                Send("[G55:0.000,0.000,0.000]");
                Send("[G92:0.000,0.000,0.000]");
                Send("[TLO:0.000]");
                Send("[PRB:0.000,0.000,0.000:0]");
                break;
            case "$H":
                Thread.Sleep(50);
                X = HomePosition.X;
                Y = HomePosition.Y;
                break;
            default:
                if (line.StartsWith("G10L2P1", StringComparison.Ordinal))
                {
                    var xi = line.IndexOf('X');
                    var yi = line.IndexOf('Y');
                    _g54X = double.Parse(line[(xi + 1)..yi], inv);
                    _g54Y = double.Parse(line[(yi + 1)..], inv);
                }
                else if (line.StartsWith('$') && line.Contains('='))
                {
                    var eq = line.IndexOf('=');
                    if (int.TryParse(line[1..eq], out var num)) Settings[num] = double.Parse(line[(eq + 1)..], inv);
                }
                else if (line.Contains('X') || line.Contains('Y'))
                {
                    lock (_lock) _busyUntil = DateTime.UtcNow.AddMilliseconds(MotionTailMs);
                }
                break;
        }
        Send("ok");
    }
}
