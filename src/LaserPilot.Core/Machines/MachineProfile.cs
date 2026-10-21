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

    /// <summary>True if the firmware homes X and Y at the same time (diagonally) rather than one
    /// after the other. Sets how long a correct homing cycle may take.</summary>
    public bool HomesXYTogether { get; init; }

    public double MaxPowerS { get; init; } = 1000;    // $30
    /// <summary>Fastest speed the machine is rated for (mm/min).</summary>
    public double MaxFeedXy { get; init; } = 6000;
    /// <summary>Typical acceleration (mm/s²) — used for time estimates until the controller reports $120/$121.</summary>
    public double AccelXy { get; init; } = 1000;
    /// <summary>Laser spot size (mm): the finest useful line interval.</summary>
    public double SpotSizeMm { get; init; } = 0.1;

    /// <summary>Recommended GRBL settings ($ number → value). Applied only
    /// with explicit user consent, one at a time, with read-back verification.</summary>
    public Dictionary<int, double> RecommendedSettings { get; init; } = new();

    /// <summary>
    /// Factory calibration values shown for comparison only. They are never
    /// written automatically: steps/mm, speed and acceleration are tuned per
    /// machine at the factory, and a wrong guess would silently change the
    /// size of every engraving or cap the machine's speed.
    /// </summary>
    public Dictionary<int, double> ReferenceSettings { get; init; } = new();

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
    ///
    /// Hardware facts (OMTech spec sheet): 10 W optical output, 450 nm,
    /// 0.07×0.07 mm spot, 250 mm/s max speed, built-in air assist, flame
    /// sensor on the frame, USB + MicroSD, firmware reports "Grbl 1.1h".
    /// </summary>
    public static MachineProfile OmtechB10 => new()
    {
        Id = "omtech-light-b10",
        Name = "OMTech Light B10 (10W diode)",
        Notes = "400×400 mm work area, GRBL 1.1h, limit switches front-left, flame sensor, air assist. " +
                "Run the Setup Wizard before first use to verify homing direction safely.",
        WidthMm = 400,
        HeightMm = 400,
        Baud = 115200,
        Origin = OriginCorner.FrontLeft,
        HasLimitSwitches = true,
        HomesXYTogether = true, // the head travels diagonally toward the home corner
        MaxPowerS = 1000,
        MaxFeedXy = 15000,
        AccelXy = 1500,
        SpotSizeMm = 0.07,
        RecommendedSettings = new Dictionary<int, double>
        {
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
            [130] = 400,  // X max travel mm
            [131] = 400,  // Y max travel mm
        },
        ReferenceSettings = new Dictionary<int, double>
        {
            [100] = 80,   // X steps/mm (typical for GT2 belt, 20T pulley, 1/16 microstepping)
            [101] = 80,   // Y steps/mm
            [110] = 15000, // X max rate mm/min (B10 rated 250 mm/s)
            [111] = 15000, // Y max rate mm/min
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
