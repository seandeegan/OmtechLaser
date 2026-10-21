namespace LaserPilot.Core.Machines;

public enum SettingRisk
{
    Safe,       // cosmetic / reporting
    Caution,    // affects motion quality
    Dangerous,  // wrong value can crash the machine or damage hardware
}

public sealed record GrblSettingInfo(int Number, string Name, string Unit, string Description, SettingRisk Risk);

/// <summary>Descriptions and risk levels for every GRBL 1.1 '$' setting,
/// so the UI can explain settings in plain language and warn before risky edits.</summary>
public static class GrblSettingsCatalog
{
    public static GrblSettingInfo Get(int number) =>
        All.FirstOrDefault(s => s.Number == number)
        ?? new GrblSettingInfo(number, $"${number}", "", "Unknown / board-specific setting.", SettingRisk.Caution);

    public static readonly IReadOnlyList<GrblSettingInfo> All = new List<GrblSettingInfo>
    {
        new(0,  "Step pulse time", "µs", "Length of each step pulse. Leave at default unless your drivers require otherwise.", SettingRisk.Caution),
        new(1,  "Step idle delay", "ms", "How long motors stay energized after motion. 255 = always on (holds position).", SettingRisk.Safe),
        new(2,  "Step pulse invert", "mask", "Inverts step signal polarity per axis. Board-specific.", SettingRisk.Dangerous),
        new(3,  "Direction invert", "mask", "Flips motor direction per axis. If jog moves the wrong way, change this — bit0=X, bit1=Y.", SettingRisk.Dangerous),
        new(4,  "Step enable invert", "bool", "Inverts the stepper enable pin. Board-specific.", SettingRisk.Dangerous),
        new(5,  "Limit pins invert", "bool", "Set to 1 if limit switches read 'triggered' when they are not pressed (normally-open vs normally-closed wiring).", SettingRisk.Dangerous),
        new(6,  "Probe pin invert", "bool", "Inverts the probe input.", SettingRisk.Caution),
        new(10, "Status report options", "mask", "What the '?' status report contains: 0 = work position (B10 factory), 1 = machine position. LaserPilot works with either.", SettingRisk.Safe),
        new(11, "Junction deviation", "mm", "Cornering aggressiveness. Smaller = slower, smoother corners.", SettingRisk.Caution),
        new(12, "Arc tolerance", "mm", "Accuracy of arc approximation.", SettingRisk.Safe),
        new(13, "Report in inches", "bool", "Status reports in inches instead of mm. Keep 0 — LaserPilot works in mm.", SettingRisk.Caution),
        new(20, "Soft limits", "bool", "Reject any motion that would leave the machine work area. Requires homing. STRONGLY recommended once homing works — this is your rail-crash protection.", SettingRisk.Caution),
        new(21, "Hard limits", "bool", "Halt immediately if a limit switch is hit during motion. Requires reliable switches (electrical noise can cause false alarms).", SettingRisk.Caution),
        new(22, "Homing cycle enable", "bool", "Enables the $H homing cycle. Only enable if the machine has working limit switches.", SettingRisk.Dangerous),
        new(23, "Homing direction invert", "mask", "Which direction each axis homes. bit0=X, bit1=Y. 0 = toward +X/+Y (rear-right), 3 = toward -X/-Y (front-left). WRONG VALUE = the head drives away from the switches and grinds the rails. OMTech B10: 3.", SettingRisk.Dangerous),
        new(24, "Homing locate feed", "mm/min", "Slow speed for the final precise switch location. Typical 100.", SettingRisk.Caution),
        new(25, "Homing search seek", "mm/min", "Fast speed used to find the switch initially. Lower = gentler if something goes wrong. Typical 1000–2000.", SettingRisk.Caution),
        new(26, "Homing debounce", "ms", "Switch debounce delay. Typical 250.", SettingRisk.Safe),
        new(27, "Homing pull-off", "mm", "Distance to back off the switch after homing. Typical 1–3 mm.", SettingRisk.Caution),
        new(30, "Max spindle speed (S max)", "S", "S value that equals 100% laser power. Must match the S values in generated G-code (LaserPilot uses 1000).", SettingRisk.Caution),
        new(31, "Min spindle speed", "S", "S value for minimum power. Usually 0.", SettingRisk.Safe),
        new(32, "Laser mode", "bool", "1 = laser mode: no dwell on power changes, power scales with speed, laser off during rapids. Keep 1 for lasers.", SettingRisk.Caution),
        new(100, "X steps per mm", "steps", "Motor steps per millimetre of X travel. Wrong value = wrong engraving size. Typically 80 on the B10 — calibrate by engraving a 100 mm square and measuring it.", SettingRisk.Dangerous),
        new(101, "Y steps per mm", "steps", "Motor steps per millimetre of Y travel. Typically 80 on the B10.", SettingRisk.Dangerous),
        new(102, "Z steps per mm", "steps", "Steps per mm for Z (unused on most diode lasers).", SettingRisk.Caution),
        new(110, "X max rate", "mm/min", "Maximum X speed. Too high = lost steps and skewed jobs.", SettingRisk.Caution),
        new(111, "Y max rate", "mm/min", "Maximum Y speed.", SettingRisk.Caution),
        new(112, "Z max rate", "mm/min", "Maximum Z speed.", SettingRisk.Safe),
        new(120, "X acceleration", "mm/s²", "X acceleration. Too high = lost steps, ringing artifacts.", SettingRisk.Caution),
        new(121, "Y acceleration", "mm/s²", "Y acceleration.", SettingRisk.Caution),
        new(122, "Z acceleration", "mm/s²", "Z acceleration.", SettingRisk.Safe),
        new(130, "X max travel", "mm", "X work area size. Used by soft limits and homing search distance (search = 1.5× this). OMTech B10: 400.", SettingRisk.Dangerous),
        new(131, "Y max travel", "mm", "Y work area size. OMTech B10: 400.", SettingRisk.Dangerous),
        new(132, "Z max travel", "mm", "Z travel.", SettingRisk.Caution),
    };
}
