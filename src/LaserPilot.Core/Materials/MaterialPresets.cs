using LaserPilot.Core.Gcode;

namespace LaserPilot.Core.Materials;

public enum OperationKind { Engrave, Cut }

public sealed record MaterialPreset(
    string Material,
    OperationKind Operation,
    double SpeedMmMin,
    double PowerPercent,
    int Passes,
    string Notes)
{
    public string Display => $"{Material} — {Operation} ({SpeedMmMin:0} mm/min, {PowerPercent:0}%, {Passes}×)";

    public CutSettings ToCutSettings() => new()
    {
        SpeedMmMin = SpeedMmMin,
        PowerPercent = PowerPercent,
        Passes = Passes,
    };
}

/// <summary>
/// Starting-point presets for a 10W optical-output diode laser (OMTech B10
/// class). Always test on scrap first — material batches vary a lot.
/// </summary>
public static class MaterialPresets
{
    public static readonly IReadOnlyList<MaterialPreset> ForDiode10W = new List<MaterialPreset>
    {
        new("Plywood 3mm", OperationKind.Cut, 180, 100, 2, "Birch ply. Use air assist if available."),
        new("Plywood 5mm", OperationKind.Cut, 120, 100, 4, "Slow, multiple passes. Watch for charring."),
        new("Basswood 3mm", OperationKind.Cut, 300, 100, 2, "Cuts easily."),
        new("MDF 3mm", OperationKind.Cut, 150, 100, 3, "Produces a lot of smoke; ventilate well."),
        new("Wood engrave (light)", OperationKind.Engrave, 3000, 40, 1, "Light surface mark."),
        new("Wood engrave (dark)", OperationKind.Engrave, 1500, 70, 1, "Deep dark burn."),
        new("Cardboard", OperationKind.Cut, 600, 60, 1, "FIRE RISK — never leave unattended."),
        new("Leather engrave", OperationKind.Engrave, 2000, 35, 1, "Vegetable-tanned only. Chrome-tanned releases toxic fumes."),
        new("Black acrylic 3mm", OperationKind.Cut, 100, 100, 4, "Only DARK acrylic — clear/blue acrylic does not absorb 450nm diode light."),
        new("Acrylic engrave (dark)", OperationKind.Engrave, 2500, 50, 1, "Dark/opaque acrylic only."),
        new("Anodized aluminium", OperationKind.Engrave, 2500, 80, 1, "Marks the anodized layer white/silver."),
        new("Painted metal", OperationKind.Engrave, 2000, 90, 1, "Removes the coating."),
        new("Slate / stone", OperationKind.Engrave, 1800, 90, 1, "Wipe with a damp cloth first for contrast."),
        new("Glass (coated)", OperationKind.Engrave, 2000, 85, 1, "Coat with tempera paint or use marking spray; bare glass passes diode light."),
    };
}
