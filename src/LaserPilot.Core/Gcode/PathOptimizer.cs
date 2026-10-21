using LaserPilot.Core.Geometry;

namespace LaserPilot.Core.Gcode;

/// <summary>
/// Orders vector paths for cutting: anything inside a closed shape is cut
/// before that shape (so a part never drops out or shifts before its holes
/// are cut), and within that rule the nearest path is taken next to cut down
/// travel time. Closed paths start at the vertex nearest the head; open paths
/// may be reversed.
/// </summary>
public static class PathOptimizer
{
    /// <summary>Above this many paths the containment test is skipped (it is O(n²)).</summary>
    private const int ContainmentLimit = 4000;

    public static List<T> Order<T>(IReadOnlyList<T> items, Func<T, Polyline> pathOf, Func<T, Polyline, T> withPath, Vec2 start)
    {
        int n = items.Count;
        var result = new List<T>(n);
        if (n == 0) return result;

        var paths = items.Select(pathOf).ToArray();
        var bounds = paths.Select(p => p.Bounds()).ToArray();

        // blockers[j] = how many not-yet-cut paths lie inside path j.
        var blockers = new int[n];
        var parents = new List<int>[n];
        for (int i = 0; i < n; i++) parents[i] = new List<int>();
        if (n <= ContainmentLimit)
        {
            for (int outer = 0; outer < n; outer++)
            {
                if (!FillGenerator.IsFillable(paths[outer])) continue;
                var (omin, omax) = bounds[outer];
                for (int inner = 0; inner < n; inner++)
                {
                    if (inner == outer || paths[inner].Points.Count == 0) continue;
                    var (imin, imax) = bounds[inner];
                    if (imin.X < omin.X || imin.Y < omin.Y || imax.X > omax.X || imax.Y > omax.Y) continue;
                    // Identical bounds: only count it once (lower index is "inside").
                    if (imin == omin && imax == omax && inner > outer) continue;
                    if (!Contains(paths[outer], paths[inner].Points[0])) continue;
                    blockers[outer]++;
                    parents[inner].Add(outer);
                }
            }
        }

        var done = new bool[n];
        var head = start;
        for (int step = 0; step < n; step++)
        {
            int best = -1;
            double bestDist = double.MaxValue;
            bool bestReverse = false;
            int bestStartIndex = 0;
            for (int i = 0; i < n; i++)
            {
                if (done[i] || blockers[i] > 0) continue;
                var pts = paths[i].Points;
                if (pts.Count == 0) { if (best < 0) best = i; continue; }
                if (paths[i].Closed)
                {
                    // Sample a few vertices to rank paths cheaply; the exact nearest
                    // vertex of the winner is found below.
                    int stride = Math.Max(1, pts.Count / 16);
                    for (int k = 0; k < pts.Count; k += stride)
                    {
                        double d = Dist2(head, pts[k]);
                        if (d < bestDist) { bestDist = d; best = i; bestReverse = false; bestStartIndex = k; }
                    }
                }
                else
                {
                    double d0 = Dist2(head, pts[0]);
                    double d1 = Dist2(head, pts[^1]);
                    if (d0 < bestDist) { bestDist = d0; best = i; bestReverse = false; bestStartIndex = 0; }
                    if (d1 < bestDist) { bestDist = d1; best = i; bestReverse = true; bestStartIndex = 0; }
                }
            }
            if (best < 0)
            {
                // A containment cycle can only come from degenerate data; take any remaining path.
                best = Array.FindIndex(done, d => !d);
                bestReverse = false;
                bestStartIndex = 0;
            }

            done[best] = true;
            foreach (var p in parents[best]) blockers[p]--;
            if (paths[best].Closed)
            {
                var pts = paths[best].Points;
                double nearest = double.MaxValue;
                for (int k = 0; k < pts.Count; k++)
                {
                    double d = Dist2(head, pts[k]);
                    if (d < nearest) { nearest = d; bestStartIndex = k; }
                }
            }
            var path = Reorient(paths[best], bestReverse, bestStartIndex);
            result.Add(withPath(items[best], path));
            if (path.Points.Count > 0)
                head = path.Closed ? path.Points[0] : path.Points[^1];
        }
        return result;
    }

    public static List<Polyline> Order(IReadOnlyList<Polyline> paths, Vec2 start) =>
        Order(paths, p => p, (_, p) => p, start);

    private static Polyline Reorient(Polyline p, bool reverse, int startIndex)
    {
        if (!reverse && startIndex == 0) return p;
        var pts = p.Points;
        if (reverse) return new Polyline(Enumerable.Reverse(pts), p.Closed);
        // Rotate a closed ring to begin at startIndex (drop a duplicated closing point first).
        var ring = pts.Count > 1 && pts[0] == pts[^1] ? pts.Take(pts.Count - 1).ToList() : pts.ToList();
        startIndex %= ring.Count;
        var rotated = ring.Skip(startIndex).Concat(ring.Take(startIndex));
        return new Polyline(rotated, p.Closed);
    }

    private static double Dist2(Vec2 a, Vec2 b) { var dx = a.X - b.X; var dy = a.Y - b.Y; return dx * dx + dy * dy; }

    /// <summary>Even-odd point-in-polygon test.</summary>
    public static bool Contains(Polyline poly, Vec2 p)
    {
        var pts = poly.Points;
        bool inside = false;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
        {
            var a = pts[i];
            var b = pts[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) &&
                p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
}
