using System.Globalization;
using LaserPilot.Core.Comm;

namespace LaserPilot.Core.Gcode;

/// <summary>One straight move of the head. <see cref="Power"/> is the programmed S value (0 when the laser is off).</summary>
public readonly record struct ToolpathMove(
    int Line, double X0, double Y0, double X1, double Y1, bool Rapid, double Power, double FeedMmMin, bool SyncBefore);

/// <summary>
/// Interprets GRBL laser G-code into straight moves (arcs are flattened) for
/// previews and time estimates. <see cref="ToolpathMove.Line"/> is the index
/// among non-empty lines after <see cref="GrblController.CleanLine"/>, which
/// is exactly how the controller counts lines while streaming.
/// </summary>
public static class ToolpathParser
{
    public sealed class Result
    {
        public List<ToolpathMove> Moves { get; } = new();
        /// <summary>Non-empty streamed lines.</summary>
        public int LineCount { get; set; }
        /// <summary>Seconds of G4 dwell attributed to each line index.</summary>
        public Dictionary<int, double> Dwells { get; } = new();
        /// <summary>Bytes GRBL receives for each line (for serial-throughput limits).</summary>
        public List<int> LineBytes { get; } = new();
    }

    public static Result Parse(IEnumerable<string> gcode, double startX = 0, double startY = 0)
    {
        var r = new Result();
        double x = startX, y = startY;
        double feed = 0, power = 0;
        bool laserOn = false, absolute = true, inches = false;
        int motion = 0; // 0,1,2,3
        bool syncPending = false;

        var words = new List<(char L, double V)>();
        foreach (var raw in gcode)
        {
            var line = GrblController.CleanLine(raw);
            if (line.Length == 0) continue;
            int lineIndex = r.LineCount++;
            r.LineBytes.Add(line.Length + 1);
            if (line[0] == '$') continue; // system commands ($H, $J=, settings) are not part of the toolpath

            ParseWords(line, words);
            double? nx = null, ny = null, ni = null, nj = null, nr = null, p = null;
            bool dwell = false;
            foreach (var (l, v) in words)
            {
                switch (l)
                {
                    case 'G':
                        switch ((int)Math.Round(v * 10))
                        {
                            case 0: motion = 0; break;
                            case 10: motion = 1; break;
                            case 20: motion = 2; break;
                            case 30: motion = 3; break;
                            case 40: dwell = true; break;
                            case 200: inches = true; break;
                            case 210: inches = false; break;
                            case 900: absolute = true; break;
                            case 910: absolute = false; break;
                        }
                        break;
                    case 'M':
                        switch ((int)Math.Round(v))
                        {
                            case 3:
                            case 4: if (!laserOn) syncPending = true; laserOn = true; break;
                            case 5: if (laserOn) syncPending = true; laserOn = false; break;
                            case 2:
                            case 30: laserOn = false; break;
                        }
                        break;
                    case 'S': power = v; break;
                    case 'F': feed = inches ? v * 25.4 : v; break;
                    case 'X': nx = v; break;
                    case 'Y': ny = v; break;
                    case 'I': ni = v; break;
                    case 'J': nj = v; break;
                    case 'R': nr = v; break;
                    case 'P': p = v; break;
                }
            }

            if (dwell)
            {
                if (p is { } secs && secs > 0) r.Dwells[lineIndex] = r.Dwells.GetValueOrDefault(lineIndex) + secs;
                syncPending = true;
                continue;
            }
            if (nx is null && ny is null) continue;

            double scale = inches ? 25.4 : 1;
            double tx = nx is { } vx ? (absolute ? vx * scale : x + vx * scale) : x;
            double ty = ny is { } vy ? (absolute ? vy * scale : y + vy * scale) : y;
            bool rapid = motion == 0;
            double movePower = !rapid && laserOn ? power : 0;

            if ((motion == 2 || motion == 3) && (ni.HasValue || nj.HasValue || nr.HasValue))
            {
                foreach (var (ax, ay) in FlattenArc(x, y, tx, ty, (ni ?? 0) * scale, (nj ?? 0) * scale, nr * scale, motion == 2))
                {
                    r.Moves.Add(new ToolpathMove(lineIndex, x, y, ax, ay, false, movePower, feed, syncPending));
                    syncPending = false;
                    x = ax; y = ay;
                }
                x = tx; y = ty;
                continue;
            }

            if (tx != x || ty != y)
            {
                r.Moves.Add(new ToolpathMove(lineIndex, x, y, tx, ty, rapid, movePower, feed, syncPending));
                syncPending = false;
            }
            x = tx; y = ty;
        }
        return r;
    }

    private static void ParseWords(string line, List<(char, double)> words)
    {
        words.Clear();
        int i = 0;
        while (i < line.Length)
        {
            char c = char.ToUpperInvariant(line[i]);
            if (c < 'A' || c > 'Z') { i++; continue; }
            int j = i + 1;
            while (j < line.Length && (char.IsDigit(line[j]) || line[j] is '.' or '-' or '+')) j++;
            if (j > i + 1 && double.TryParse(line.AsSpan(i + 1, j - i - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                words.Add((c, v));
            i = j;
        }
    }

    /// <summary>Points along a G2 (clockwise) / G3 arc, ending exactly at the target.</summary>
    private static IEnumerable<(double, double)> FlattenArc(double x0, double y0, double x1, double y1,
        double i, double j, double? radius, bool clockwise)
    {
        double cx, cy;
        if (radius is { } rr && rr != 0)
        {
            // Radius format: centre on the perpendicular bisector; negative R = the long way round.
            double dx = x1 - x0, dy = y1 - y0;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-9 || Math.Abs(rr) < d / 2) { yield return (x1, y1); yield break; }
            double h = Math.Sqrt(rr * rr - d * d / 4);
            if (clockwise ^ (rr < 0)) h = -h;
            cx = (x0 + x1) / 2 - h * dy / d;
            cy = (y0 + y1) / 2 + h * dx / d;
        }
        else
        {
            cx = x0 + i;
            cy = y0 + j;
        }
        double r = Math.Sqrt((x0 - cx) * (x0 - cx) + (y0 - cy) * (y0 - cy));
        double a0 = Math.Atan2(y0 - cy, x0 - cx);
        double a1 = Math.Atan2(y1 - cy, x1 - cx);
        double sweep = a1 - a0;
        if (clockwise) { if (sweep >= -1e-9) sweep -= 2 * Math.PI; }
        else if (sweep <= 1e-9) sweep += 2 * Math.PI;

        int segments = Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) * r / 0.5), 2, 720);
        for (int k = 1; k < segments; k++)
        {
            double a = a0 + sweep * k / segments;
            yield return (cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }
        yield return (x1, y1);
    }
}
