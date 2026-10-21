using System.Globalization;
using LaserPilot.Core.Gcode;

namespace LaserPilot.App.Models;

/// <summary>Settings for a material test card: a grid of squares at different speeds and powers.</summary>
public sealed class MaterialTestOptions
{
    /// <summary>Fill = engraving test; Line = cutting/scoring test.</summary>
    public VectorMode Mode { get; set; } = VectorMode.Fill;
    public int Columns { get; set; } = 5;          // power steps
    public int Rows { get; set; } = 5;             // speed steps
    public double PowerMin { get; set; } = 20;
    public double PowerMax { get; set; } = 100;
    public double SpeedMin { get; set; } = 1000;
    public double SpeedMax { get; set; } = 6000;
    public double BoxSizeMm { get; set; } = 8;
    public double GapMm { get; set; } = 3;
    public double IntervalMm { get; set; } = 0.1;
    public int Passes { get; set; } = 1;
    public bool Labels { get; set; } = true;
    public double LabelSpeed { get; set; } = 3000;
    public double LabelPower { get; set; } = 35;
    public double X { get; set; } = 10;
    public double Y { get; set; } = 10;
}

/// <summary>
/// Builds a material test card. Each square is its own design item carrying
/// its own speed/power, so it can be inspected, tweaked or switched off.
/// </summary>
public static class MaterialTest
{
    private const double LabelHeight = 2.5;

    public static (double W, double H) Size(MaterialTestOptions o)
    {
        double labelW = o.Labels ? 14 : 0, labelH = o.Labels ? 9 : 0;
        return (labelW + o.Columns * o.BoxSizeMm + (o.Columns - 1) * o.GapMm,
                labelH + o.Rows * o.BoxSizeMm + (o.Rows - 1) * o.GapMm);
    }

    public static List<DesignItem> Generate(MaterialTestOptions o)
    {
        var items = new List<DesignItem>();
        var inv = CultureInfo.InvariantCulture;
        double labelW = o.Labels ? 14 : 0;
        double gridTop = o.Y + o.Rows * o.BoxSizeMm + (o.Rows - 1) * o.GapMm;

        double Step(double min, double max, int i, int n) => n <= 1 ? min : min + (max - min) * i / (n - 1);

        for (int r = 0; r < o.Rows; r++)
        {
            // Row 0 (top) = slowest speed.
            double speed = Math.Round(Step(o.SpeedMin, o.SpeedMax, r, o.Rows));
            double y = gridTop - (r + 1) * o.BoxSizeMm - r * o.GapMm;
            for (int c = 0; c < o.Columns; c++)
            {
                double power = Math.Round(Step(o.PowerMin, o.PowerMax, c, o.Columns));
                double x = o.X + labelW + c * (o.BoxSizeMm + o.GapMm);
                var box = ShapeFactory.Box($"Test {speed.ToString("0", inv)} mm/min {power.ToString("0", inv)}%", x, y, o.BoxSizeMm, o.BoxSizeMm);
                box.Mode = o.Mode == VectorMode.Line ? VectorMode.Line : VectorMode.Fill;
                box.Cut.SpeedMmMin = speed;
                box.Cut.PowerPercent = power;
                box.Cut.Passes = Math.Max(1, o.Passes);
                box.Cut.FillIntervalMm = o.IntervalMm;
                items.Add(box);
            }
            if (o.Labels)
                items.Add(Label(speed.ToString("0", inv), o.X, y + (o.BoxSizeMm - LabelHeight) / 2, o));
        }

        if (o.Labels)
        {
            for (int c = 0; c < o.Columns; c++)
            {
                double power = Math.Round(Step(o.PowerMin, o.PowerMax, c, o.Columns));
                double x = o.X + labelW + c * (o.BoxSizeMm + o.GapMm);
                items.Add(Label($"{power.ToString("0", inv)}%", x, gridTop + 1.5, o));
            }
            items.Add(Label("mm/min ↓   power →", o.X, gridTop + 2 + LabelHeight + 1, o));
        }
        return items;
    }

    private static DesignItem Label(string text, double x, double y, MaterialTestOptions o)
    {
        var label = ShapeFactory.Text(text, "Arial", bold: false, italic: false, heightMm: LabelHeight);
        label.Name = $"Label {text}";
        label.X = x;
        label.Y = y;
        label.Mode = VectorMode.Fill;
        label.Cut.SpeedMmMin = o.LabelSpeed;
        label.Cut.PowerPercent = o.LabelPower;
        label.Cut.FillIntervalMm = 0.1;
        return label;
    }
}
