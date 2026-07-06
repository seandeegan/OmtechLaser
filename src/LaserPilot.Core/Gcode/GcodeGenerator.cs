using System.Globalization;
using System.Text;
using LaserPilot.Core.Geometry;

namespace LaserPilot.Core.Gcode;

public sealed class CutSettings
{
    public double SpeedMmMin { get; set; } = 600;
    public double PowerPercent { get; set; } = 80;   // 0–100
    public int Passes { get; set; } = 1;
}

public sealed class RasterSettings
{
    public double SpeedMmMin { get; set; } = 3000;
    public double MaxPowerPercent { get; set; } = 60;
    public double LineIntervalMm { get; set; } = 0.1;   // distance between scan lines
    public double OverscanMm { get; set; } = 2.5;       // travel past the image so the head is at speed
    public bool BidirectionalScan { get; set; } = true;
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

    public static List<string> Preamble() => new()
    {
        "; LaserPilot job",
        "G21",          // millimetres
        "G90",          // absolute positioning
        "M5",           // laser off
        "G0 X0 Y0 S0",
    };

    public static List<string> Postamble() => new()
    {
        "M5",
        "G0 X0 Y0",
        "; end of job",
    };

    /// <summary>Vector cut/engrave along polylines. Coordinates are already in work space (mm, origin front-left).</summary>
    public static List<string> Vector(IEnumerable<Polyline> shapes, CutSettings s, double maxPowerS = 1000)
    {
        var g = new List<string>();
        var power = Math.Clamp(s.PowerPercent, 0, 100) / 100.0 * maxPowerS;
        var shapeList = shapes.Where(p => p.Points.Count >= 2).ToList();

        for (int pass = 0; pass < Math.Max(1, s.Passes); pass++)
        {
            g.Add($"; pass {pass + 1}");
            foreach (var poly in shapeList)
            {
                var pts = poly.Points;
                g.Add($"G0 X{F(pts[0].X)} Y{F(pts[0].Y)}");
                g.Add($"M4 S{F(power)}");
                for (int i = 1; i < pts.Count; i++)
                    g.Add($"G1 X{F(pts[i].X)} Y{F(pts[i].Y)} F{F(s.SpeedMmMin)}");
                if (poly.Closed)
                    g.Add($"G1 X{F(pts[0].X)} Y{F(pts[0].Y)} F{F(s.SpeedMmMin)}");
                g.Add("M5");
            }
        }
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
        RasterSettings s, double maxPowerS = 1000)
    {
        var g = new List<string>();
        if (pxW <= 0 || pxH <= 0 || widthMm <= 0 || heightMm <= 0) return g;

        double pxSizeX = widthMm / pxW;
        int rows = Math.Max(1, (int)Math.Round(heightMm / s.LineIntervalMm));
        double maxS = Math.Clamp(s.MaxPowerPercent, 0, 100) / 100.0 * maxPowerS;
        bool leftToRight = true;

        g.Add($"; raster {pxW}x{pxH}px at {F(widthMm)}x{F(heightMm)}mm, {rows} lines");
        g.Add($"M4 S0");

        for (int row = 0; row < rows; row++)
        {
            double yMm = originY + (row + 0.5) * (heightMm / rows);
            // Map this row to an image row: top of image = last row emitted.
            int imgY = Math.Clamp((int)((1.0 - (row + 0.5) / rows) * pxH), 0, pxH - 1);

            // Build run-length spans of equal power along the row.
            var spans = BuildSpans(gray, pxW, imgY, maxS);
            if (spans.Count == 0) continue;

            if (!s.BidirectionalScan) leftToRight = true;

            double rowStartX = originX + spans[0].startPx * pxSizeX;
            double rowEndX = originX + spans[^1].endPx * pxSizeX;
            double entryX = leftToRight ? rowStartX - s.OverscanMm : rowEndX + s.OverscanMm;
            double exitX = leftToRight ? rowEndX + s.OverscanMm : rowStartX - s.OverscanMm;

            g.Add($"G0 X{F(entryX)} Y{F(yMm)}");
            g.Add($"G1 X{F(leftToRight ? rowStartX : rowEndX)} Y{F(yMm)} S0 F{F(s.SpeedMmMin)}");

            var ordered = leftToRight ? spans : Reverse(spans);
            foreach (var (startPx, endPx, sVal) in ordered)
            {
                double xTarget = originX + (leftToRight ? endPx : startPx) * pxSizeX;
                g.Add($"G1 X{F(xTarget)} S{F(sVal)}");
            }
            g.Add($"G1 X{F(exitX)} S0");
            leftToRight = !leftToRight;
        }

        g.Add("M5");
        return g;

        static List<(int startPx, int endPx, double sVal)> Reverse(List<(int startPx, int endPx, double sVal)> spans)
        {
            var r = new List<(int, int, double)>();
            for (int i = spans.Count - 1; i >= 0; i--) r.Add(spans[i]);
            return r;
        }
    }

    private static List<(int startPx, int endPx, double sVal)> BuildSpans(byte[] gray, int pxW, int imgY, double maxS)
    {
        // Quantize to 64 power levels and merge equal neighbours.
        var spans = new List<(int, int, double)>();
        int runStart = -1;
        int runLevel = -1;
        for (int x = 0; x <= pxW; x++)
        {
            int level = -1; // -1 = off
            if (x < pxW)
            {
                int v = gray[imgY * pxW + x];
                if (v < 250) level = (255 - v) * 63 / 255; // 0..63
                if (level == 0 && v < 250) level = 1;
            }
            if (level != runLevel)
            {
                if (runLevel > 0 && runStart >= 0)
                    spans.Add((runStart, x, runLevel / 63.0 * maxS));
                runStart = x;
                runLevel = level;
            }
        }
        // Trim leading/trailing off-spans are excluded by runLevel > 0 check.
        return spans;
    }

    /// <summary>Frame (trace the bounding box) so the user can verify placement. Laser stays off (or barely visible if powerS > 0).</summary>
    public static List<string> Frame(double x, double y, double w, double h, double feed = 3000, double powerS = 0)
    {
        var laserOn = powerS > 0 ? $"M4 S{F(powerS)}" : "M5";
        var move = powerS > 0 ? "G1" : "G0";
        var feedSuffix = powerS > 0 ? $" F{F(feed)}" : "";
        return new List<string>
        {
            "; frame",
            "G21", "G90", "M5",
            $"G0 X{F(x)} Y{F(y)}",
            laserOn,
            $"{move} X{F(x + w)} Y{F(y)}{feedSuffix}",
            $"{move} X{F(x + w)} Y{F(y + h)}{feedSuffix}",
            $"{move} X{F(x)} Y{F(y + h)}{feedSuffix}",
            $"{move} X{F(x)} Y{F(y)}{feedSuffix}",
            "M5",
        };
    }
}
