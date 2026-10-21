using System.Globalization;
using LaserPilot.Core.Geometry;

namespace LaserPilot.Core.Gcode;

/// <summary>How a vector design is burned.</summary>
public enum VectorMode
{
    /// <summary>Follow the outlines (cutting, scoring, line engraving).</summary>
    Line,
    /// <summary>Engrave the inside of closed shapes with scan lines (text, logos).</summary>
    Fill,
    /// <summary>Fill, then trace the outline for a crisp edge.</summary>
    FillAndLine,
}

public sealed class CutSettings
{
    public double SpeedMmMin { get; set; } = 600;
    public double PowerPercent { get; set; } = 80;   // 0–100
    public int Passes { get; set; } = 1;
    /// <summary>Fill mode: distance between scan lines (mm).</summary>
    public double FillIntervalMm { get; set; } = 0.1;
    /// <summary>Fill mode: travel past the shape so the head is at speed while burning.</summary>
    public double OverscanMm { get; set; } = 2.5;
    public bool Bidirectional { get; set; } = true;

    public CutSettings Clone() => (CutSettings)MemberwiseClone();
}

public sealed class RasterSettings
{
    public double SpeedMmMin { get; set; } = 3000;
    public double MaxPowerPercent { get; set; } = 60;
    /// <summary>Grayscale mode: power used for the lightest engraved tone.</summary>
    public double MinPowerPercent { get; set; }
    public double LineIntervalMm { get; set; } = 0.1;   // distance between scan lines
    public double OverscanMm { get; set; } = 2.5;       // travel past the image so the head is at speed
    public bool BidirectionalScan { get; set; } = true;
    public DitherMode Dither { get; set; } = DitherMode.FloydSteinberg;
    public bool Invert { get; set; }
    /// <summary>−100…100.</summary>
    public double Brightness { get; set; }
    /// <summary>−100…100.</summary>
    public double Contrast { get; set; }
    /// <summary>Above 1 lightens mid-tones, below 1 darkens them.</summary>
    public double Gamma { get; set; } = 1.0;
    public int Passes { get; set; } = 1;

    public RasterSettings Clone() => (RasterSettings)MemberwiseClone();
}

/// <summary>
/// Generates GRBL 1.1 laser G-code. Uses M4 (dynamic power) so power scales
/// with actual speed — no burnt corners during acceleration — and G-code that
/// always ends with the laser off and spindle stopped.
/// </summary>
public static class GcodeGenerator
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string F(double v) => v.ToString("0.###", Inv);
    // Scan lines: 0.01 mm is far below the laser spot size and keeps lines short.
    private static string F2(double v) => v.ToString("0.##", Inv);
    private static string Pwr(double s) => Math.Round(s).ToString("0", Inv);

    public static List<string> Preamble(bool airAssist = false)
    {
        var g = new List<string>
        {
            "; LaserPilot job",
            "G21",          // millimetres
            "G90",          // absolute positioning
            "G54",          // the bed-origin work offset (a file may have left another one active)
            "M5",           // laser off
            "S0",
        };
        if (airAssist) g.Add("M8"); // air assist on (controller's coolant/air output)
        return g;
    }

    /// <param name="returnTo">Where to park the head afterwards (null = stay put).</param>
    public static List<string> Postamble(bool airAssist = false, Vec2? returnTo = null)
    {
        var g = new List<string> { "M5" };
        if (airAssist) g.Add("M9");
        if (returnTo is { } r) g.Add($"G0 X{F(r.X)} Y{F(r.Y)}");
        g.Add("; end of job");
        return g;
    }

    /// <summary>
    /// Vector cut/engrave along polylines, in the given order. Coordinates are
    /// already in work space (mm, origin front-left).
    /// </summary>
    public static List<string> Vector(IEnumerable<Polyline> shapes, CutSettings s, double maxPowerS = 1000) =>
        VectorJob(shapes.Select(p => (p, s)).ToList(), maxPowerS);

    /// <summary>
    /// Vector paths that may each carry their own settings. Passes run in
    /// rounds so heat spreads out, and each path does its passes in the FINAL
    /// rounds: every path finishes in the last round, in the given order. With
    /// holes ordered before outlines, a hole that needs more passes than its
    /// outline is still fully cut before the outline frees the part.
    /// </summary>
    public static List<string> VectorJob(IReadOnlyList<(Polyline Path, CutSettings Settings)> paths, double maxPowerS = 1000)
    {
        var g = new List<string>();
        var usable = paths.Where(p => p.Path.Points.Count >= 2).ToList();
        int rounds = usable.Count == 0 ? 0 : usable.Max(p => Math.Max(1, p.Settings.Passes));
        for (int pass = 0; pass < rounds; pass++)
        {
            g.Add($"; pass {pass + 1}");
            foreach (var (poly, s) in usable)
            {
                if (pass < rounds - Math.Max(1, s.Passes)) continue;
                var power = Math.Clamp(s.PowerPercent, 0, 100) / 100.0 * maxPowerS;
                var pts = poly.Points;
                g.Add($"G0 X{F(pts[0].X)} Y{F(pts[0].Y)}");
                g.Add($"M4 S{Pwr(power)}");
                g.Add($"G1 X{F(pts[1].X)} Y{F(pts[1].Y)} F{F(s.SpeedMmMin)}");
                for (int i = 2; i < pts.Count; i++)
                    g.Add($"X{F(pts[i].X)} Y{F(pts[i].Y)}");
                if (poly.Closed && pts[^1] != pts[0])
                    g.Add($"X{F(pts[0].X)} Y{F(pts[0].Y)}");
                g.Add("M5");
            }
        }
        return g;
    }

    /// <summary>
    /// Fill (engrave) the inside of closed shapes with horizontal scan lines,
    /// even-odd rule so holes in letters stay unburned.
    /// </summary>
    /// <param name="minX">Lowest X the head may travel to (overscan is clamped to the bed).</param>
    /// <param name="maxX">Highest X the head may travel to.</param>
    public static List<string> Fill(IReadOnlyList<Polyline> shapes, CutSettings s, double maxPowerS = 1000,
        double minX = double.NegativeInfinity, double maxX = double.PositiveInfinity)
    {
        var g = new List<string>();
        var rows = FillGenerator.ScanRows(shapes, s.FillIntervalMm);
        if (rows.Count == 0) return g;
        var power = Math.Clamp(s.PowerPercent, 0, 100) / 100.0 * maxPowerS;

        g.Add($"; fill {rows.Count} lines @ {F(s.FillIntervalMm)} mm");
        g.Add("M4 S0");
        for (int pass = 0; pass < Math.Max(1, s.Passes); pass++)
        {
            bool leftToRight = true;
            foreach (var (y, segments) in rows)
            {
                var spans = segments.Select(seg => (seg.A, seg.B, power)).ToList();
                if (!s.Bidirectional) leftToRight = true;
                EmitScanLine(g, y, spans, leftToRight, s.OverscanMm, minX, maxX, s.SpeedMmMin);
                leftToRight = !leftToRight;
            }
        }
        g.Add("M5");
        return g;
    }

    /// <summary>
    /// Raster engrave a grayscale image (row-major, 0 = black/full power,
    /// 255 = white/off) placed at (originX, originY) with the given physical
    /// size. Scan lines run along X; rows are emitted bottom-to-top so the
    /// image appears upright in a front-left-origin work space (row 0 = top
    /// of the image).
    /// </summary>
    public static List<string> Raster(byte[] gray, int pxW, int pxH,
        double originX, double originY, double widthMm, double heightMm,
        RasterSettings s, double maxPowerS = 1000,
        double minX = double.NegativeInfinity, double maxX = double.PositiveInfinity)
    {
        var g = new List<string>();
        if (pxW <= 0 || pxH <= 0 || widthMm <= 0 || heightMm <= 0) return g;

        // Resample to one pixel per scan-line interval, adjust, dither.
        (gray, pxW, pxH) = RasterProcessor.Prepare(gray, pxW, pxH, widthMm, heightMm, s);

        double pxSizeX = widthMm / pxW;
        double rowPitch = heightMm / pxH;
        double maxS = Math.Clamp(s.MaxPowerPercent, 0, 100) / 100.0 * maxPowerS;
        double minS = s.Dither == DitherMode.Grayscale
            ? Math.Clamp(Math.Min(s.MinPowerPercent, s.MaxPowerPercent), 0, 100) / 100.0 * maxPowerS
            : maxS;

        g.Add($"; raster {pxW}x{pxH}px at {F(widthMm)}x{F(heightMm)}mm, {pxH} lines");
        g.Add("M4 S0");

        for (int pass = 0; pass < Math.Max(1, s.Passes); pass++)
        {
            bool leftToRight = true;
            for (int row = 0; row < pxH; row++)
            {
                double yMm = originY + (row + 0.5) * rowPitch;
                int imgY = pxH - 1 - row; // image row 0 is the top of the picture

                var spans = BuildSpans(gray, pxW, imgY, minS, maxS, originX, pxSizeX);
                if (spans.Count == 0) continue;
                if (!s.BidirectionalScan) leftToRight = true;
                EmitScanLine(g, yMm, spans, leftToRight, s.OverscanMm, minX, maxX, s.SpeedMmMin);
                leftToRight = !leftToRight;
            }
        }

        g.Add("M5");
        return g;
    }

    /// <summary>
    /// One scan line: rapid to the overscan entry, accelerate up to the first
    /// span, then burn each span at its power and cross every gap at S0.
    /// Spans must be in ascending X order and must not overlap.
    /// </summary>
    private static void EmitScanLine(List<string> g, double y, List<(double A, double B, double S)> spans,
        bool leftToRight, double overscan, double minX, double maxX, double speed)
    {
        double rowStart = spans[0].A, rowEnd = spans[^1].B;
        overscan = Math.Max(0, overscan);
        double entry = leftToRight ? rowStart - overscan : rowEnd + overscan;
        double exit = leftToRight ? rowEnd + overscan : rowStart - overscan;
        entry = Math.Clamp(entry, Math.Min(minX, rowStart), Math.Max(maxX, rowEnd));
        exit = Math.Clamp(exit, Math.Min(minX, rowStart), Math.Max(maxX, rowEnd));

        double first = leftToRight ? rowStart : rowEnd;
        // S0 on the travel move: laser mode ($32=1) already turns the beam off for G0,
        // but a controller without laser mode would otherwise burn between rows.
        g.Add($"G0 X{F2(entry)} Y{F(y)} S0");
        g.Add($"G1 X{F2(first)} S0 F{F(speed)}");
        double head = first;
        for (int k = 0; k < spans.Count; k++)
        {
            var (a, b, sVal) = spans[leftToRight ? k : spans.Count - 1 - k];
            double enter = leftToRight ? a : b;
            double leave = leftToRight ? b : a;
            if (Math.Abs(enter - head) > 1e-6) g.Add($"X{F2(enter)} S0");
            g.Add($"X{F2(leave)} S{Pwr(sVal)}");
            head = leave;
        }
        if (Math.Abs(exit - head) > 1e-6) g.Add($"X{F2(exit)} S0");
    }

    /// <summary>Runs of equal power along one image row, in mm. Near-white pixels are skipped.</summary>
    private static List<(double A, double B, double S)> BuildSpans(byte[] gray, int pxW, int imgY,
        double minS, double maxS, double originX, double pxSize)
    {
        // Quantize to 64 power levels and merge equal neighbours.
        var spans = new List<(double, double, double)>();
        int runStart = -1;
        int runLevel = -1;
        for (int x = 0; x <= pxW; x++)
        {
            int level = -1; // -1 = off
            if (x < pxW)
            {
                int v = gray[imgY * pxW + x];
                if (v < 250) level = Math.Max(1, (255 - v) * 63 / 255); // 1..63
            }
            if (level != runLevel)
            {
                if (runLevel > 0)
                    spans.Add((originX + runStart * pxSize, originX + x * pxSize, minS + (maxS - minS) * runLevel / 63.0));
                runStart = x;
                runLevel = level;
            }
        }
        return spans;
    }

    /// <summary>
    /// Frame (trace the bounding box) so the user can verify placement. The
    /// outline is traced at a steady, watchable feed rate — with the laser off
    /// (M5), or barely visible if powerS > 0.
    /// </summary>
    public static List<string> Frame(double x, double y, double w, double h, double feed = 3000, double powerS = 0)
    {
        var laserOn = powerS > 0 ? $"M4 S{F(powerS)}" : "M5";
        return new List<string>
        {
            "; frame",
            "G21", "G90", "G54", "M5",
            $"G0 X{F(x)} Y{F(y)}",
            laserOn,
            $"G1 X{F(x + w)} Y{F(y)} F{F(feed)}",
            $"G1 X{F(x + w)} Y{F(y + h)}",
            $"G1 X{F(x)} Y{F(y + h)}",
            $"G1 X{F(x)} Y{F(y)}",
            "M5",
        };
    }
}
