using LaserPilot.Core.Gcode;

namespace LaserPilot.Core.Materials;

public enum OperationKind
{
    /// <summary>Fill / image engraving.</summary>
    Engrave,
    /// <summary>Cut all the way through.</summary>
    Cut,
    /// <summary>Line engraving: a surface mark that follows the outline.</summary>
    Score,
}

public sealed record MaterialPreset(
    string Material,
    OperationKind Operation,
    double SpeedMmMin,
    double PowerPercent,
    int Passes,
    string Notes,
    double IntervalMm = 0.1)
{
    /// <summary>False for presets the user saved themselves.</summary>
    public bool BuiltIn { get; init; } = true;

    public string Display =>
        $"{(BuiltIn ? "" : "★ ")}{Material} · {Operation.ToString().ToLowerInvariant()} · {SpeedMmMin:0} mm/min · {PowerPercent:0}%" +
        (Passes > 1 ? $" · {Passes}×" : "");

    // ComboBoxes show ToString(); the record default would print every property.
    public override string ToString() => Display;

    public CutSettings ToCutSettings() => new()
    {
        SpeedMmMin = SpeedMmMin,
        PowerPercent = PowerPercent,
        Passes = Passes,
        FillIntervalMm = IntervalMm,
    };
}

/// <summary>
/// Starting-point presets for a 10W optical-output diode laser (OMTech B10
/// class, with its air assist running). Always test on scrap first — material
/// batches vary a lot; Tools → Material test finds the best values quickly.
/// </summary>
public static class MaterialPresets
{
    public static readonly IReadOnlyList<MaterialPreset> ForDiode10W = new List<MaterialPreset>
    {
        new("Plywood 3mm", OperationKind.Cut, 180, 100, 2, "Birch ply. Keep the air assist on."),
        new("Plywood 5mm", OperationKind.Cut, 120, 100, 4, "Slow, multiple passes. Watch for charring."),
        new("Basswood 3mm", OperationKind.Cut, 300, 100, 2, "Cuts easily."),
        new("MDF 3mm", OperationKind.Cut, 150, 100, 3, "Produces a lot of smoke; ventilate well."),
        new("Cardboard", OperationKind.Cut, 600, 60, 1, "FIRE RISK — never leave unattended."),
        new("Paper / card stock", OperationKind.Cut, 1200, 35, 1, "FIRE RISK — tape it down so it can't lift into the beam."),
        new("Felt 3mm", OperationKind.Cut, 600, 70, 1, "Natural wool felt; synthetic felt melts."),
        new("Black acrylic 3mm", OperationKind.Cut, 100, 100, 4, "Only DARK acrylic — clear/blue acrylic does not absorb 450nm diode light."),
        new("Wood score (outline)", OperationKind.Score, 3000, 35, 1, "A light line that follows the outline."),
        new("Wood engrave (light)", OperationKind.Engrave, 3000, 40, 1, "Light surface mark."),
        new("Wood engrave (dark)", OperationKind.Engrave, 1500, 70, 1, "Deep dark burn."),
        new("Photo on wood", OperationKind.Engrave, 3000, 35, 1, "Use a dither image mode; light woods (maple, basswood) give the best contrast.", 0.1),
        new("Cork", OperationKind.Engrave, 3000, 30, 1, "Engraves very easily — start low."),
        new("Leather engrave", OperationKind.Engrave, 2000, 35, 1, "Vegetable-tanned only. Chrome-tanned releases toxic fumes."),
        new("Acrylic engrave (dark)", OperationKind.Engrave, 2500, 50, 1, "Dark/opaque acrylic only."),
        new("Anodized aluminium", OperationKind.Engrave, 2500, 80, 1, "Marks the anodized layer white/silver.", 0.08),
        new("Painted metal", OperationKind.Engrave, 2000, 90, 1, "Removes the coating."),
        new("Stainless steel + marking spray", OperationKind.Engrave, 1000, 100, 1, "Needs a laser-marking spray/paste; wash the residue off afterwards.", 0.08),
        new("Slate / stone", OperationKind.Engrave, 1800, 90, 1, "Wipe with a damp cloth first for contrast."),
        new("Ceramic tile (painted)", OperationKind.Engrave, 3000, 60, 1, "Spray white tile with black paint, engrave, then wipe the loose paint off."),
        new("Glass (coated)", OperationKind.Engrave, 2000, 85, 1, "Coat with tempera paint or use marking spray; bare glass passes diode light."),
    };
}
