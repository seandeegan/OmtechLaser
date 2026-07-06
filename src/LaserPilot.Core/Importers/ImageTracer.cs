using LaserPilot.Core.Geometry;

namespace LaserPilot.Core.Importers;

/// <summary>
/// Traces a grayscale bitmap into closed vector outlines (pixel-boundary
/// contour following + Ramer–Douglas–Peucker simplification). Dark areas
/// (below the threshold) become shapes; output coordinates are in pixels
/// with Y down, same convention as SVG, ready for DesignItem.FromVector.
/// </summary>
public static class ImageTracer
{
    public static List<Polyline> Trace(byte[] gray, int w, int h, int threshold = 128,
        bool invert = false, double simplifyPx = 0.8)
    {
        bool Dark(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return false;
            var d = gray[y * w + x] < threshold;
            return invert ? !d : d;
        }

        // Collect directed boundary edges (dark pixel on the left of travel).
        // Key: start lattice point → list of end points.
        var outgoing = new Dictionary<(int x, int y), List<(int x, int y)>>();
        void AddEdge((int x, int y) a, (int x, int y) b)
        {
            if (!outgoing.TryGetValue(a, out var list)) outgoing[a] = list = new();
            list.Add(b);
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!Dark(x, y)) continue;
                if (!Dark(x, y - 1)) AddEdge((x + 1, y), (x, y));         // top edge, travel -x
                if (!Dark(x, y + 1)) AddEdge((x, y + 1), (x + 1, y + 1)); // bottom edge, travel +x
                if (!Dark(x - 1, y)) AddEdge((x, y), (x, y + 1));         // left edge, travel +y
                if (!Dark(x + 1, y)) AddEdge((x + 1, y + 1), (x + 1, y)); // right edge, travel -y
            }
        }

        // Chain edges into closed loops. At saddle points (two choices),
        // prefer the sharpest left turn to keep separate blobs separate.
        var result = new List<Polyline>();
        var starts = outgoing.Keys.ToList();
        foreach (var start in starts)
        {
            while (outgoing.TryGetValue(start, out var firstList) && firstList.Count > 0)
            {
                var loop = new List<Vec2>();
                var cur = start;
                var next = firstList[^1];
                firstList.RemoveAt(firstList.Count - 1);
                loop.Add(new Vec2(cur.x, cur.y));
                var prevDir = (dx: next.x - cur.x, dy: next.y - cur.y);
                cur = next;

                int guard = 4 * w * h + 8;
                while (cur != start && guard-- > 0)
                {
                    loop.Add(new Vec2(cur.x, cur.y));
                    if (!outgoing.TryGetValue(cur, out var options) || options.Count == 0) break;
                    int pick = 0;
                    if (options.Count > 1)
                    {
                        // sharpest left turn: maximize cross(prevDir, candidateDir), tie-break on dot
                        double best = double.MinValue;
                        for (int i = 0; i < options.Count; i++)
                        {
                            var d = (dx: options[i].x - cur.x, dy: options[i].y - cur.y);
                            double cross = prevDir.dx * d.dy - prevDir.dy * d.dx;
                            double dot = prevDir.dx * d.dx + prevDir.dy * d.dy;
                            double score = cross * 10 - dot;
                            if (score > best) { best = score; pick = i; }
                        }
                    }
                    next = options[pick];
                    options.RemoveAt(pick);
                    prevDir = (next.x - cur.x, next.y - cur.y);
                    cur = next;
                }

                if (cur == start && loop.Count >= 4)
                {
                    var simplified = Simplify(loop, simplifyPx);
                    if (simplified.Count >= 3)
                        result.Add(new Polyline(simplified, closed: true));
                }
            }
        }
        return result;
    }

    /// <summary>Ramer–Douglas–Peucker for a closed ring (split at the two farthest points).</summary>
    private static List<Vec2> Simplify(List<Vec2> ring, double eps)
    {
        if (eps <= 0 || ring.Count <= 4) return ring;
        // Split the ring roughly in half and simplify each open run.
        int half = ring.Count / 2;
        var a = Rdp(ring.GetRange(0, half + 1), eps);
        var b = Rdp(ring.GetRange(half, ring.Count - half), eps);
        var outPts = new List<Vec2>(a);
        outPts.AddRange(b.Skip(1));
        if (outPts.Count > 1 && outPts[^1] == outPts[0]) outPts.RemoveAt(outPts.Count - 1);
        return outPts;
    }

    private static List<Vec2> Rdp(List<Vec2> pts, double eps)
    {
        if (pts.Count < 3) return pts;
        int index = -1;
        double maxDist = 0;
        var (p1, p2) = (pts[0], pts[^1]);
        for (int i = 1; i < pts.Count - 1; i++)
        {
            var d = PerpDist(pts[i], p1, p2);
            if (d > maxDist) { maxDist = d; index = i; }
        }
        if (maxDist <= eps) return new List<Vec2> { p1, p2 };
        var left = Rdp(pts.GetRange(0, index + 1), eps);
        var right = Rdp(pts.GetRange(index, pts.Count - index), eps);
        var res = new List<Vec2>(left);
        res.AddRange(right.Skip(1));
        return res;
    }

    private static double PerpDist(Vec2 p, Vec2 a, Vec2 b)
    {
        var d = b - a;
        var len = d.Length;
        if (len < 1e-12) return (p - a).Length;
        return Math.Abs(d.X * (a.Y - p.Y) - d.Y * (a.X - p.X)) / len;
    }
}
