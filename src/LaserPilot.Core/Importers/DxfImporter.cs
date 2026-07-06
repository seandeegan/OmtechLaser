using System.Globalization;
using LaserPilot.Core.Geometry;

namespace LaserPilot.Core.Importers;

/// <summary>
/// Minimal ASCII DXF importer covering the entities hobby CAD exports use:
/// LINE, CIRCLE, ARC, LWPOLYLINE (with bulges), POLYLINE/VERTEX, and SPLINE
/// (via its control points, coarsely). Coordinates are assumed millimetres.
/// </summary>
public static class DxfImporter
{
    public static List<Polyline> Import(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        var result = new List<Polyline>();

        // DXF is (group-code, value) pairs.
        var pairs = new List<(int code, string value)>();
        for (int i = 0; i + 1 < lines.Length; i += 2)
            if (int.TryParse(lines[i].Trim(), out var code))
                pairs.Add((code, lines[i + 1].Trim()));

        // Find ENTITIES section.
        int start = -1;
        for (int i = 0; i + 1 < pairs.Count; i++)
            if (pairs[i].code == 2 && pairs[i].value == "ENTITIES") { start = i + 1; break; }
        if (start < 0) return result;

        int idx = start;
        while (idx < pairs.Count)
        {
            var (code, value) = pairs[idx];
            if (code == 0)
            {
                if (value == "ENDSEC") break;
                switch (value)
                {
                    case "LINE": idx = ParseLine(pairs, idx + 1, result); continue;
                    case "CIRCLE": idx = ParseCircle(pairs, idx + 1, result); continue;
                    case "ARC": idx = ParseArc(pairs, idx + 1, result); continue;
                    case "LWPOLYLINE": idx = ParseLwPolyline(pairs, idx + 1, result); continue;
                    case "POLYLINE": idx = ParsePolyline(pairs, idx + 1, result); continue;
                    case "SPLINE": idx = ParseSpline(pairs, idx + 1, result); continue;
                }
            }
            idx++;
        }
        return result;
    }

    private static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // Read entity fields until the next 0 group code; returns index of that code.
    private static int Collect(List<(int code, string value)> pairs, int idx, Action<int, string> onField)
    {
        while (idx < pairs.Count && pairs[idx].code != 0)
        {
            onField(pairs[idx].code, pairs[idx].value);
            idx++;
        }
        return idx;
    }

    private static int ParseLine(List<(int code, string value)> pairs, int idx, List<Polyline> result)
    {
        double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
        idx = Collect(pairs, idx, (c, v) =>
        {
            if (c == 10) x1 = D(v); else if (c == 20) y1 = D(v);
            else if (c == 11) x2 = D(v); else if (c == 21) y2 = D(v);
        });
        result.Add(new Polyline(new[] { new Vec2(x1, y1), new Vec2(x2, y2) }));
        return idx;
    }

    private static int ParseCircle(List<(int code, string value)> pairs, int idx, List<Polyline> result)
    {
        double cx = 0, cy = 0, r = 0;
        idx = Collect(pairs, idx, (c, v) =>
        {
            if (c == 10) cx = D(v); else if (c == 20) cy = D(v); else if (c == 40) r = D(v);
        });
        if (r > 0) result.Add(ArcPoly(cx, cy, r, 0, 360, closed: true));
        return idx;
    }

    private static int ParseArc(List<(int code, string value)> pairs, int idx, List<Polyline> result)
    {
        double cx = 0, cy = 0, r = 0, a1 = 0, a2 = 360;
        idx = Collect(pairs, idx, (c, v) =>
        {
            if (c == 10) cx = D(v); else if (c == 20) cy = D(v); else if (c == 40) r = D(v);
            else if (c == 50) a1 = D(v); else if (c == 51) a2 = D(v);
        });
        if (r > 0) result.Add(ArcPoly(cx, cy, r, a1, a2, closed: false));
        return idx;
    }

    private static Polyline ArcPoly(double cx, double cy, double r, double a1Deg, double a2Deg, bool closed)
    {
        var a1 = a1Deg * Math.PI / 180;
        var a2 = a2Deg * Math.PI / 180;
        if (a2 <= a1) a2 += 2 * Math.PI;
        var sweep = a2 - a1;
        int segs = Math.Max(8, (int)(sweep / (2 * Math.PI) * 64));
        var pts = new List<Vec2>();
        int last = closed ? segs - 1 : segs;
        for (int i = 0; i <= last; i++)
        {
            var t = a1 + sweep * i / segs;
            pts.Add(new Vec2(cx + r * Math.Cos(t), cy + r * Math.Sin(t)));
        }
        return new Polyline(pts, closed);
    }

    private static int ParseLwPolyline(List<(int code, string value)> pairs, int idx, List<Polyline> result)
    {
        var pts = new List<Vec2>();
        var bulges = new List<double>();
        bool closed = false;
        double? pendingX = null;
        idx = Collect(pairs, idx, (c, v) =>
        {
            switch (c)
            {
                case 70: closed = ((int)D(v) & 1) != 0; break;
                case 10: pendingX = D(v); break;
                case 20:
                    if (pendingX is { } px) { pts.Add(new Vec2(px, D(v))); bulges.Add(0); pendingX = null; }
                    break;
                case 42:
                    if (bulges.Count > 0) bulges[^1] = D(v);
                    break;
            }
        });
        if (pts.Count >= 2) result.Add(ExpandBulges(pts, bulges, closed));
        return idx;
    }

    private static int ParsePolyline(List<(int code, string value)> pairs, int idx, List<Polyline> result)
    {
        bool closed = false;
        idx = Collect(pairs, idx, (c, v) => { if (c == 70) closed = ((int)D(v) & 1) != 0; });
        var pts = new List<Vec2>();
        var bulges = new List<double>();
        while (idx < pairs.Count && pairs[idx].code == 0 && pairs[idx].value == "VERTEX")
        {
            double x = 0, y = 0, bulge = 0;
            idx = Collect(pairs, idx + 1, (c, v) =>
            {
                if (c == 10) x = D(v); else if (c == 20) y = D(v); else if (c == 42) bulge = D(v);
            });
            pts.Add(new Vec2(x, y));
            bulges.Add(bulge);
        }
        if (idx < pairs.Count && pairs[idx].code == 0 && pairs[idx].value == "SEQEND")
            idx = Collect(pairs, idx + 1, (_, _) => { });
        if (pts.Count >= 2) result.Add(ExpandBulges(pts, bulges, closed));
        return idx;
    }

    // A bulge b describes an arc between consecutive vertices; b = tan(theta/4).
    private static Polyline ExpandBulges(List<Vec2> pts, List<double> bulges, bool closed)
    {
        var outPts = new List<Vec2> { pts[0] };
        int segCount = closed ? pts.Count : pts.Count - 1;
        for (int i = 0; i < segCount; i++)
        {
            var p1 = pts[i];
            var p2 = pts[(i + 1) % pts.Count];
            var b = i < bulges.Count ? bulges[i] : 0;
            if (Math.Abs(b) < 1e-9)
            {
                if (!(closed && i == segCount - 1)) outPts.Add(p2);
                continue;
            }
            var theta = 4 * Math.Atan(b);
            var chord = (p2 - p1).Length;
            if (chord < 1e-9) continue;
            var r = chord / (2 * Math.Sin(Math.Abs(theta) / 2));
            var mid = (p1 + p2) * 0.5;
            var dir = new Vec2(-(p2.Y - p1.Y) / chord, (p2.X - p1.X) / chord);
            var d = Math.Sqrt(Math.Max(0, r * r - chord * chord / 4)) * (Math.Abs(theta) > Math.PI ? -1 : 1);
            var center = b > 0 ? mid + dir * d : mid - dir * d;
            var a1 = Math.Atan2(p1.Y - center.Y, p1.X - center.X);
            int segs = Math.Max(4, (int)(Math.Abs(theta) / (2 * Math.PI) * 64));
            for (int k = 1; k <= segs; k++)
            {
                var t = a1 + theta * k / segs;
                var p = new Vec2(center.X + r * Math.Cos(t), center.Y + r * Math.Sin(t));
                if (closed && i == segCount - 1 && k == segs) break;
                outPts.Add(p);
            }
        }
        return new Polyline(outPts, closed);
    }

    private static int ParseSpline(List<(int code, string value)> pairs, int idx, List<Polyline> result)
    {
        // Coarse: connect control points (good enough for preview/cutting simple splines).
        var pts = new List<Vec2>();
        double? pendingX = null;
        bool closed = false;
        idx = Collect(pairs, idx, (c, v) =>
        {
            if (c == 70) closed = ((int)D(v) & 1) != 0;
            else if (c == 10) pendingX = D(v);
            else if (c == 20 && pendingX is { } px) { pts.Add(new Vec2(px, D(v))); pendingX = null; }
        });
        if (pts.Count >= 2) result.Add(new Polyline(pts, closed));
        return idx;
    }
}

