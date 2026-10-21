using LaserPilot.Core.Geometry;

namespace LaserPilot.Core.Gcode;

/// <summary>
/// Scan-line geometry for filling closed shapes. Uses the even-odd rule, so a
/// letter's counter (the hole in "O") or a cut-out inside a logo stays empty.
/// </summary>
public static class FillGenerator
{
    /// <summary>Paths that enclose an area: flagged closed, or ending where they start.</summary>
    public static bool IsFillable(Polyline p) =>
        p.Points.Count >= 3 && (p.Closed || (p.Points[0] - p.Points[^1]).Length < 0.01);

    /// <summary>
    /// For each scan line (bottom to top) the X ranges inside the shapes.
    /// Rows are spaced evenly at roughly <paramref name="intervalMm"/>.
    /// </summary>
    public static List<(double Y, List<(double A, double B)> Segments)> ScanRows(IReadOnlyList<Polyline> shapes, double intervalMm)
    {
        var rows = new List<(double, List<(double, double)>)>();
        var closed = shapes.Where(IsFillable).ToList();
        if (closed.Count == 0) return rows;

        var edges = new List<(double X1, double Y1, double X2, double Y2)>();
        foreach (var p in closed)
        {
            var pts = p.Points;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                if (a.Y != b.Y) edges.Add((a.X, a.Y, b.X, b.Y)); // horizontal edges never cross a scan line
            }
        }
        if (edges.Count == 0) return rows;
        // Sort by lower Y so each row only scans edges that can reach it.
        edges.Sort((e1, e2) => Math.Min(e1.Y1, e1.Y2).CompareTo(Math.Min(e2.Y1, e2.Y2)));

        var (min, max) = Polyline.BoundsOf(closed);
        double height = max.Y - min.Y;
        double interval = Math.Max(0.02, intervalMm);
        int n = Math.Max(1, (int)Math.Round(height / interval));
        double pitch = height / n;

        var active = new List<(double X1, double Y1, double X2, double Y2)>();
        int nextEdge = 0;
        var xs = new List<double>();
        for (int k = 0; k < n; k++)
        {
            double y = min.Y + (k + 0.5) * pitch;
            while (nextEdge < edges.Count && Math.Min(edges[nextEdge].Y1, edges[nextEdge].Y2) <= y)
                active.Add(edges[nextEdge++]);
            active.RemoveAll(e => Math.Max(e.Y1, e.Y2) <= y);

            xs.Clear();
            foreach (var e in active)
            {
                // Half-open rule [ymin, ymax): a vertex on the scan line is counted once.
                if ((e.Y1 <= y && e.Y2 > y) || (e.Y2 <= y && e.Y1 > y))
                    xs.Add(e.X1 + (y - e.Y1) * (e.X2 - e.X1) / (e.Y2 - e.Y1));
            }
            if (xs.Count < 2) continue;
            xs.Sort();
            var segments = new List<(double, double)>();
            for (int i = 0; i + 1 < xs.Count; i += 2)
                if (xs[i + 1] - xs[i] > 1e-6) segments.Add((xs[i], xs[i + 1]));
            if (segments.Count > 0) rows.Add((y, segments));
        }
        return rows;
    }
}
