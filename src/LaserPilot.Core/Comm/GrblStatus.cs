namespace LaserPilot.Core.Comm;

public enum MachineState
{
    Unknown, Idle, Run, Hold, Jog, Alarm, Door, Check, Home, Sleep, Disconnected
}

/// <summary>Parsed GRBL 1.1 real-time status report, e.g.
/// &lt;Idle|MPos:0.000,0.000,0.000|FS:0,0|Pn:XY|WCO:0.000,0.000,0.000&gt;</summary>
public sealed class GrblStatus
{
    public MachineState State { get; set; } = MachineState.Unknown;
    public string SubState { get; set; } = "";           // e.g. Hold:0, Door:1
    public double MX { get; set; }
    public double MY { get; set; }
    public double MZ { get; set; }
    public double WX { get; set; }
    public double WY { get; set; }
    public double WZ { get; set; }
    public double WcoX { get; set; }
    public double WcoY { get; set; }
    public double WcoZ { get; set; }
    public double FeedRate { get; set; }
    public double SpindleSpeed { get; set; }
    /// <summary>Active input pins: X, Y, Z (limit switches), P (probe), D, H, R, S.</summary>
    public string Pins { get; set; } = "";
    public int? BufferBlocks { get; set; }
    public int? BufferBytes { get; set; }
    public double OverrideFeed { get; set; } = 100;
    public double OverrideRapid { get; set; } = 100;
    public double OverrideSpindle { get; set; } = 100;

    public bool XLimitActive => Pins.Contains('X');
    public bool YLimitActive => Pins.Contains('Y');
    public bool ZLimitActive => Pins.Contains('Z');

    public static bool TryParse(string line, GrblStatus? previous, out GrblStatus status)
    {
        status = new GrblStatus();
        if (previous is not null)
        {
            // WCO is only reported intermittently; carry it forward.
            status.WcoX = previous.WcoX; status.WcoY = previous.WcoY; status.WcoZ = previous.WcoZ;
            status.Pins = ""; // pins are only present while active
        }

        if (line.Length < 3 || line[0] != '<' || !line.EndsWith('>')) return false;
        var body = line[1..^1];
        var fields = body.Split('|');
        if (fields.Length == 0) return false;

        var stateField = fields[0];
        status.SubState = stateField;
        var stateName = stateField.Split(':')[0];
        status.State = stateName switch
        {
            "Idle" => MachineState.Idle,
            "Run" => MachineState.Run,
            "Hold" => MachineState.Hold,
            "Jog" => MachineState.Jog,
            "Alarm" => MachineState.Alarm,
            "Door" => MachineState.Door,
            "Check" => MachineState.Check,
            "Home" => MachineState.Home,
            "Sleep" => MachineState.Sleep,
            _ => MachineState.Unknown
        };

        bool haveMPos = false, haveWPos = false;
        foreach (var f in fields.Skip(1))
        {
            var idx = f.IndexOf(':');
            if (idx < 0) continue;
            var key = f[..idx];
            var val = f[(idx + 1)..];
            var parts = val.Split(',');
            switch (key)
            {
                case "MPos":
                    if (TryXyz(parts, out var mx, out var my, out var mz)) { status.MX = mx; status.MY = my; status.MZ = mz; haveMPos = true; }
                    break;
                case "WPos":
                    if (TryXyz(parts, out var wx, out var wy, out var wz)) { status.WX = wx; status.WY = wy; status.WZ = wz; haveWPos = true; }
                    break;
                case "WCO":
                    if (TryXyz(parts, out var cx, out var cy, out var cz)) { status.WcoX = cx; status.WcoY = cy; status.WcoZ = cz; }
                    break;
                case "FS":
                    if (parts.Length >= 2 && Num(parts[0], out var fr) && Num(parts[1], out var ss)) { status.FeedRate = fr; status.SpindleSpeed = ss; }
                    break;
                case "F":
                    if (Num(parts[0], out var f2)) status.FeedRate = f2;
                    break;
                case "Pn":
                    status.Pins = val;
                    break;
                case "Bf":
                    if (parts.Length >= 2 && int.TryParse(parts[0], out var bb) && int.TryParse(parts[1], out var by)) { status.BufferBlocks = bb; status.BufferBytes = by; }
                    break;
                case "Ov":
                    if (parts.Length >= 3 && Num(parts[0], out var of) && Num(parts[1], out var or) && Num(parts[2], out var os))
                    { status.OverrideFeed = of; status.OverrideRapid = or; status.OverrideSpindle = os; }
                    break;
            }
        }

        // Derive whichever position wasn't reported (WPos = MPos - WCO).
        if (haveMPos && !haveWPos)
        {
            status.WX = status.MX - status.WcoX;
            status.WY = status.MY - status.WcoY;
            status.WZ = status.MZ - status.WcoZ;
        }
        else if (haveWPos && !haveMPos)
        {
            status.MX = status.WX + status.WcoX;
            status.MY = status.WY + status.WcoY;
            status.MZ = status.WZ + status.WcoZ;
        }
        return true;

        static bool Num(string s, out double d) => double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out d);
        static bool TryXyz(string[] p, out double x, out double y, out double z)
        {
            x = y = z = 0;
            if (p.Length < 2) return false;
            if (!Num(p[0], out x) || !Num(p[1], out y)) return false;
            if (p.Length >= 3) Num(p[2], out z);
            return true;
        }
    }
}
