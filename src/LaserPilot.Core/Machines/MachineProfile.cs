namespace LaserPilot.Core.Machines;

public enum OriginCorner { FrontLeft, FrontRight, RearLeft, RearRight }

/// <summary>
/// A machine profile: everything LaserPilot needs to know to drive a
/// specific laser safely — dimensions, homing behaviour, and the GRBL
/// settings we recommend for it.
/// </summary>
public sealed class MachineProfile
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Notes { get; init; } = "";
    public double WidthMm { get; init; }
    public double HeightMm { get; init; }
    public int Baud { get; init; } = 115200;
    public OriginCorner Origin { get; init; } = OriginCorner.FrontLeft;

    /// <summary>Whether this machine ships with physical limit switches.
    /// If false, $H homing must never be enabled and the wizard uses
    /// "set origin manually" flow instead.</summary>
    public bool HasLimitSwitches { get; init; }

    public double MaxPowerS { get; init; } = 1000;    // $30
    public double MaxFeedXy { get; init; } = 6000;    // mm/min

    /// <summary>Recommended GRBL settings ($ number → value). Applied only
    /// with explicit user consent, one at a time, with read-back verification.</summary>
    public Dictionary<int, double> RecommendedSettings { get; init; } = new();

    public override string ToString() => Name;

    // ------------------------------------------------------------------
    // Built-in profiles
    // ------------------------------------------------------------------

    /// <summary>
    /// OMTech Light B10 — 10W output diode laser, 400×400 mm work area,
    /// GRBL 1.1 controller, limit switches at the front-left corner.
    ///
    /// Key safety choices for this machine:
    ///  • $23=3 homes toward X- Y- (front-left, where the switches are).
    ///    The most common cause of "banging into the rails" on this machine
    ///    is $23=0 (GRBL default: home toward X+ Y+) — the head drives to
    ///    the BACK-RIGHT where there are no switches and stalls until the
    ///    homing search distance runs out. The setup wizard verifies the
    ///    real direction empirically before homing is enabled.
    ///  • $24/$25 use conservative homing feed/seek rates.
    ///  • Soft limits ($20=1) + hard limits stay available once homing is
    ///    verified, so jogs and jobs can never leave the work area.
    ///  • $32=1 laser mode so moves without S power don't burn, and
    ///    power scales with speed during acceleration (no charred corners).
    /// </summary>
    public static MachineProfile OmtechB10 => new()
    {
        Id = "omtech-light-b10",
        Name = "OMTech Light B10 (10W diode)",
        Notes = "400×400 mm work area, GRBL 1.1, limit switches front-left. " +
                "Run the Setup Wizard before first use to verify homing direction safely.",
        WidthMm = 400,
        HeightMm = 400,
        Baud = 115200,
        Origin = OriginCorner.FrontLeft,
        HasLimitSwitches = true,
        MaxPowerS = 1000,
        MaxFeedXy = 6000,
        RecommendedSettings = new Dictionary<int, double>
        {
            [10] = 1,     // status report: MPos
            [20] = 1,     // soft limits ON (requires homing) — protects rails
            [21] = 1,     // hard limits ON — switches also act as crash guards
            [22] = 1,     // homing cycle enabled
            [23] = 3,     // homing direction: X- Y- (front-left switches)
            [24] = 100,   // homing locate feed, mm/min (slow & gentle)
            [25] = 1500,  // homing search seek, mm/min (conservative)
            [26] = 250,   // homing debounce, ms
            [27] = 2.0,   // homing pull-off, mm
            [30] = 1000,  // max spindle/laser S value
            [31] = 0,     // min S value
            [32] = 1,     // laser mode ON
            [100] = 80,   // X steps/mm (B10 factory value)
            [101] = 80,   // Y steps/mm
            [110] = 6000, // X max rate mm/min
            [111] = 6000, // Y max rate mm/min
            [120] = 1000, // X acceleration mm/s²
            [121] = 1000, // Y acceleration mm/s²
            [130] = 400,  // X max travel mm
            [131] = 400,  // Y max travel mm
        },
    };

    /// <summary>Generic GRBL machine — no assumptions, homing disabled until configured.</summary>
    public static MachineProfile GenericGrbl => new()
    {
        Id = "generic-grbl",
        Name = "Generic GRBL laser",
        Notes = "No assumptions made. Use the Setup Wizard to configure size and homing.",
        WidthMm = 300,
        HeightMm = 300,
        HasLimitSwitches = false,
        RecommendedSettings = new(),
    };

    public static IReadOnlyList<MachineProfile> BuiltIn => new[] { OmtechB10, GenericGrbl };
}
