using System.Globalization;
using LaserPilot.Core.Geometry;

namespace LaserPilot.Core.Importers;

/// <summary>
/// ASCII DXF importer covering the entities hobby CAD exports use: LINE,
/// CIRCLE, ARC, ELLIPSE, LWPOLYLINE (with bulges), POLYLINE/VERTEX, SPLINE
/// (true NURBS evaluation) and INSERT (block references, incl. arrays).
/// Drawing units ($INSUNITS) are converted to millimetres; paper-space
/// entities are ignored.
/// </summary>
public static class DxfImporter
{
    private const int MaxInsertDepth = 8;

    private sealed class Block
    {
        public double BaseX, BaseY;
        public int Start, End; // pair index range of the block's entities
    }

    private sealed class Document
    {
        public required List<(int code, string value)> Pairs { get; init; }
        public Dictionary<string, Block> Blocks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int InsertBudget = 200_000; // stops absurd nested arrays from exhausting memory
    }

    public static List<Polyline> Import(string filePath) => ImportText(File.ReadAllText(filePath));

    public static List<Polyline> ImportText(string text)
    {
        if (text.StartsWith("AutoCAD Binary DXF", StringComparison.Ordinal))
            throw new InvalidDataException("Binary DXF is not supported — save the drawing as ASCII DXF.");
        var lines = text.Split('\n');

        // DXF is (group-code, value) pairs.
        var pairs = new List<(int code, string value)>(lines.Length / 2);
        for (int i = 0; i + 1 < lines.Length; i += 2)
            if (int.TryParse(lines[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                pairs.Add((code, lines[i + 1].Trim()));

        var doc = new Document { Pairs = pairs };
        double unit = ReadUnitScale(pairs);
        ReadBlocks(doc);

        var result = new List<Polyline>();
        int start = FindSection(pairs, "ENTITIES");
        if (start < 0) return result;
        ParseEntities(doc, start, pairs.Count, Mat2D.Scale(unit, unit), 0, result);
        return result;
    }

    private static double D(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : 0;
    private static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static int FindSection(List<(int code, string value)> pairs, string name)
    {
        for (int i = 0; i + 1 < pairs.Count; i++)
            if (pairs[i] is (0, "SECTION") && pairs[i + 1].code == 2 && pairs[i + 1].value == name) return i + 2;
        return -1;
    }

    /// <summary>Millimetres per drawing unit from the header's $INSUNITS (unitless → mm).</summary>
    private static double ReadUnitScale(List<(int code, string value)> pairs)
    {
        int header = FindSection(pairs, "HEADER");
        if (header < 0) return 1;
        for (int i = header; i + 1 < pairs.Count && pairs[i] != (0, "ENDSEC"); i++)
        {
            if (pairs[i] is not (9, "$INSUNITS")) continue;
            return I(pairs[i + 1].value) switch
            {
                1 => 25.4,        // inches
                2 => 304.8,       // feet
                5 => 10,          // centimetres
                6 => 1000,        // metres
                8 => 25.4e-6,     // microinches
                9 => 0.0254,      // mils
                10 => 914.4,      // yards
                14 => 100,        // decimetres
                _ => 1,           // 0 unitless, 4 millimetres, others
            };
        }
        return 1;
    }

    private static void ReadBlocks(Document doc)
    {
        var pairs = doc.Pairs;
        int i = FindSection(pairs, "BLOCKS");
        if (i < 0) return;
        while (i < pairs.Count && pairs[i] != (0, "ENDSEC"))
        {
            if (pairs[i] != (0, "BLOCK")) { i++; continue; }
            string name = "";
            var block = new Block();
            i++;
            while (i < pairs.Count && pairs[i].code != 0)
            {
                switch (pairs[i].code)
                {
                    case 2: name = pairs[i].value; break;
                    case 10: block.BaseX = D(pairs[i].value); break;
                    case 20: block.BaseY = D(pairs[i].value); break;
                }
                i++;
            }
            block.Start = i;
            while (i < pairs.Count && pairs[i] != (0, "ENDBLK") && pairs[i] != (0, "ENDSEC")) i++;
            block.End = i;
            if (name.Length > 0) doc.Blocks.TryAdd(name, block);
        }
    }

    // Reads entity fields until the next 0 group code; returns that index.
    private static int Collect(List<(int code, string value)> pairs, int idx, Action<int, string> onField)
    {
        while (idx < pairs.Count && pairs[idx].code != 0)
        {
            onField(pairs[idx].code, pairs[idx].value);
            idx++;
        }
        return idx;
    }

    private static void ParseEntities(Document doc, int start, int end, Mat2D xform, int depth, List<Polyline> result)
    {
        var pairs = doc.Pairs;
        // Curves are flattened to ~0.01 mm on the finished design, whatever the drawing units or block scale.
        double mmPerUnit = Math.Sqrt(Math.Abs(xform.A * xform.D - xform.B * xform.C));
        double tol = 0.01 / Math.Max(1e-9, mmPerUnit);
        int idx = start;
        while (idx < end)
        {
            var (code, value) = pairs[idx];
            if (code != 0) { idx++; continue; }
            if (value is "ENDSEC" or "ENDBLK") break;

            var local = new List<Polyline>();
            bool paperSpace = false;
            double extrusionZ = 1;
            void Common(int c, string v)
            {
                if (c == 67) paperSpace = I(v) == 1;
                else if (c == 230) extrusionZ = D(v);
            }

            int next;
            switch (value)
            {
                case "LINE": next = ParseLine(pairs, idx + 1, local, Common); break;
                case "CIRCLE": next = ParseCircle(pairs, idx + 1, local, Common, tol); break;
                case "ARC": next = ParseArc(pairs, idx + 1, local, Common, tol); break;
                case "ELLIPSE": next = ParseEllipse(pairs, idx + 1, local, Common, tol); break;
                case "LWPOLYLINE": next = ParseLwPolyline(pairs, idx + 1, local, Common, tol); break;
                case "POLYLINE": next = ParsePolyline(pairs, idx + 1, local, Common, tol); break;
                case "SPLINE": next = ParseSpline(pairs, idx + 1, local, Common, tol); break;
                case "INSERT":
                    next = ParseInsert(doc, idx + 1, xform, depth, result, Common, out paperSpace);
                    idx = next;
                    continue; // already transformed and added
                default:
                    next = Collect(pairs, idx + 1, (_, _) => { });
                    break;
            }
            idx = next;
            if (paperSpace) continue;

            // Entities drawn with a downward extrusion (0,0,-1) are mirrored in X.
            var m = extrusionZ < 0 && value != "ELLIPSE" && value != "SPLINE" && value != "LINE"
                ? xform.Multiply(Mat2D.Scale(-1, 1))
                : xform;
            foreach (var p in local)
            {
                if (p.Points.Count < 2) continue;
                p.Transform(m);
                result.Add(p);
            }
        }
    }

    private static int ParseInsert(Document doc, int idx, Mat2D xform, int depth, List<Polyline> result,
        Action<int, string> common, out bool paperSpace)
    {
        string name = "";
        double x = 0, y = 0, sx = 1, sy = 1, rot = 0, colSpacing = 0, rowSpacing = 0, extrusionZ = 1;
        int cols = 1, rows = 1;
        bool ps = false;
        idx = Collect(doc.Pairs, idx, (c, v) =>
        {
            switch (c)
            {
                case 2: name = v; break;
                case 10: x = D(v); break;
                case 20: y = D(v); break;
                case 41: sx = D(v); break;
                case 42: sy = D(v); break;
                case 50: rot = D(v); break;
                case 70: cols = Math.Max(1, I(v)); break;
                case 71: rows = Math.Max(1, I(v)); break;
                case 44: colSpacing = D(v); break;
                case 45: rowSpacing = D(v); break;
                case 67: ps = I(v) == 1; break;
                case 230: extrusionZ = D(v); break;
            }
        });
        paperSpace = ps;
        if (ps || depth >= MaxInsertDepth || !doc.Blocks.TryGetValue(name, out var block)) return idx;
        if (sx == 0) sx = 1;
        if (sy == 0) sy = 1;

        var mirror = extrusionZ < 0 ? Mat2D.Scale(-1, 1) : Mat2D.Identity;
        for (int r = 0; r < Math.Min(rows, 1000); r++)
        {
            for (int c = 0; c < Math.Min(cols, 1000); c++)
            {
                if (--doc.InsertBudget < 0) return idx;
                // world = mirror · T(insert) · R(rot) · T(array offset) · S(scale) · T(-base)
                var m = xform.Multiply(mirror)
                    .Multiply(Mat2D.Translate(x, y))
                    .Multiply(Mat2D.Rotate(rot))
                    .Multiply(Mat2D.Translate(c * colSpacing, r * rowSpacing))
                    .Multiply(Mat2D.Scale(sx, sy))
                    .Multiply(Mat2D.Translate(-block.BaseX, -block.BaseY));
                ParseEntities(doc, block.Start, block.End, m, depth + 1, result);
            }
        }
        return idx;
    }

    private static int ParseLine(List<(int code, string value)> pairs, int idx, List<Polyline> result, Action<int, string> common)
    {
        double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
        idx = Collect(pairs, idx, (c, v) =>
        {
            if (c == 10) x1 = D(v); else if (c == 20) y1 = D(v);
            else if (c == 11) x2 = D(v); else if (c == 21) y2 = D(v);
            else common(c, v);
        });
        if (x1 != x2 || y1 != y2) result.Add(new Polyline(new[] { new Vec2(x1, y1), new Vec2(x2, y2) }));
        return idx;
    }

    private static int ParseCircle(List<(int code, string value)> pairs, int idx, List<Polyline> result, Action<int, string> common, double tol)
    {
        double cx = 0, cy = 0, r = 0;
        idx = Collect(pairs, idx, (c, v) =>
        {
            if (c == 10) cx = D(v); else if (c == 20) cy = D(v); else if (c == 40) r = D(v);
            else common(c, v);
        });
        if (r > 0) result.Add(ArcPoly(cx, cy, r, 0, 360, closed: true, tol));
        return idx;
    }

    private static int ParseArc(List<(int code, string value)> pairs, int idx, List<Polyline> result, Action<int, string> common, double tol)
    {
        double cx = 0, cy = 0, r = 0, a1 = 0, a2 = 360;
        idx = Collect(pairs, idx, (c, v) =>
        {
            if (c == 10) cx = D(v); else if (c == 20) cy = D(v); else if (c == 40) r = D(v);
            else if (c == 50) a1 = D(v); else if (c == 51) a2 = D(v);
            else common(c, v);
        });
        if (r > 0) result.Add(ArcPoly(cx, cy, r, a1, a2, closed: false, tol));
        return idx;
    }

    /// <summary>Segments for an arc so the chord error stays within <paramref name="tol"/> (drawing units).</summary>
    private static int SegmentsFor(double radius, double sweepRad, double tol)
    {
        double step = radius > tol ? 2 * Math.Acos(Math.Clamp(1 - tol / radius, -1, 1)) : Math.PI / 4;
        return Math.Clamp((int)Math.Ceiling(Math.Abs(sweepRad) / Math.Max(step, 1e-4)), 8, 4000);
    }

    private static Polyline ArcPoly(double cx, double cy, double r, double a1Deg, double a2Deg, bool closed, double tol)
    {
        var a1 = a1Deg * Math.PI / 180;
        var a2 = a2Deg * Math.PI / 180;
        if (a2 <= a1) a2 += 2 * Math.PI;
        var sweep = a2 - a1;
        int segs = SegmentsFor(r, sweep, tol);
        var pts = new List<Vec2>();
        int last = closed ? segs - 1 : segs;
        for (int i = 0; i <= last; i++)
        {
            var t = a1 + sweep * i / segs;
            pts.Add(new Vec2(cx + r * Math.Cos(t), cy + r * Math.Sin(t)));
        }
        return new Polyline(pts, closed);
    }

    private static int ParseEllipse(List<(int code, string value)> pairs, int idx, List<Polyline> result, Action<int, string> common, double tol)
    {
        double cx = 0, cy = 0, mx = 1, my = 0, ratio = 1, t0 = 0, t1 = 2 * Math.PI, nz = 1;
        idx = Collect(pairs, idx, (c, v) =>
        {
            switch (c)
            {
                case 10: cx = D(v); break;
                case 20: cy = D(v); break;
                case 11: mx = D(v); break;
                case 21: my = D(v); break;
                case 40: ratio = D(v); break;
                case 41: t0 = D(v); break;
                case 42: t1 = D(v); break;
                case 230: nz = D(v); common(c, v); break;
                default: common(c, v); break;
            }
        });
        double major = Math.Sqrt(mx * mx + my * my);
        if (major <= 0 || ratio <= 0) return idx;
        // Minor axis = ratio · (N × M); N = (0,0,±1).
        double sign = nz < 0 ? -1 : 1;
        double nx = -my * ratio * sign, ny = mx * ratio * sign;
        if (t1 <= t0) t1 += 2 * Math.PI;
        double sweep = t1 - t0;
        bool closed = Math.Abs(sweep - 2 * Math.PI) < 1e-6;
        int segs = SegmentsFor(major, sweep, tol);
        var pts = new List<Vec2>();
        int last = closed ? segs - 1 : segs;
        for (int i = 0; i <= last; i++)
        {
            double t = t0 + sweep * i / segs;
            pts.Add(new Vec2(cx + Math.Cos(t) * mx + Math.Sin(t) * nx, cy + Math.Cos(t) * my + Math.Sin(t) * ny));
        }
        result.Add(new Polyline(pts, closed));
        return idx;
    }

    private static int ParseLwPolyline(List<(int code, string value)> pairs, int idx, List<Polyline> result, Action<int, string> common, double tol)
    {
        var pts = new List<Vec2>();
        var bulges = new List<double>();
        bool closed = false;
        double? pendingX = null;
        idx = Collect(pairs, idx, (c, v) =>
        {
            switch (c)
            {
                case 70: closed = (I(v) & 1) != 0; break;
                case 10: pendingX = D(v); break;
                case 20:
                    if (pendingX is { } px) { pts.Add(new Vec2(px, D(v))); bulges.Add(0); pendingX = null; }
                    break;
                case 42:
                    if (bulges.Count > 0) bulges[^1] = D(v);
                    break;
                default: common(c, v); break;
            }
        });
        if (pts.Count >= 2) result.Add(ExpandBulges(pts, bulges, closed, tol));
        return idx;
    }

    private static int ParsePolyline(List<(int code, string value)> pairs, int idx, List<Polyline> result, Action<int, string> common, double tol)
    {
        int flags = 0;
        idx = Collect(pairs, idx, (c, v) => { if (c == 70) flags = I(v); else common(c, v); });
        bool closed = (flags & 1) != 0;
        bool skip = (flags & (16 | 64)) != 0; // 3D polygon mesh / polyface mesh
        var pts = new List<Vec2>();
        var bulges = new List<double>();
        while (idx < pairs.Count && pairs[idx].code == 0 && pairs[idx].value == "VERTEX")
        {
            double x = 0, y = 0, bulge = 0;
            int vflags = 0;
            idx = Collect(pairs, idx + 1, (c, v) =>
            {
                if (c == 10) x = D(v); else if (c == 20) y = D(v); else if (c == 42) bulge = D(v);
                else if (c == 70) vflags = I(v);
            });
            if ((vflags & 16) != 0) continue; // spline frame control point, not on the curve
            pts.Add(new Vec2(x, y));
            bulges.Add(bulge);
        }
        if (idx < pairs.Count && pairs[idx].code == 0 && pairs[idx].value == "SEQEND")
            idx = Collect(pairs, idx + 1, (_, _) => { });
        if (!skip && pts.Count >= 2) result.Add(ExpandBulges(pts, bulges, closed, tol));
        return idx;
    }

    // A bulge b describes an arc between consecutive vertices; b = tan(theta/4).
    private static Polyline ExpandBulges(List<Vec2> pts, List<double> bulges, bool closed, double tol)
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
            int segs = Math.Max(4, SegmentsFor(r, theta, tol));
            for (int k = 1; k <= segs; k++)
            {
                var t = a1 + theta * k / segs;
                var p = new Vec2(center.X + r * Math.Cos(t), center.Y + r * Math.Sin(t));
                if (closed && i == segCount - 1 && k == segs) break;
                outPts.Add(k == segs ? p2 : p);
            }
        }
        return new Polyline(outPts, closed);
    }

    private static int ParseSpline(List<(int code, string value)> pairs, int idx, List<Polyline> result, Action<int, string> common, double tol)
    {
        var ctrl = new List<Vec2>();
        var fit = new List<Vec2>();
        var knots = new List<double>();
        var weights = new List<double>();
        int degree = 3, flags = 0;
        double? pendingX = null, pendingFitX = null;
        idx = Collect(pairs, idx, (c, v) =>
        {
            switch (c)
            {
                case 70: flags = I(v); break;
                case 71: degree = I(v); break;
                case 40: knots.Add(D(v)); break;
                case 41: weights.Add(D(v)); break;
                case 10: pendingX = D(v); break;
                case 20: if (pendingX is { } px) { ctrl.Add(new Vec2(px, D(v))); pendingX = null; } break;
                case 11: pendingFitX = D(v); break;
                case 21: if (pendingFitX is { } fx) { fit.Add(new Vec2(fx, D(v))); pendingFitX = null; } break;
                default: common(c, v); break;
            }
        });
        bool closed = (flags & 1) != 0;

        List<Vec2> pts;
        if (ctrl.Count >= 2 && degree >= 1 && knots.Count == ctrl.Count + degree + 1)
            pts = EvaluateNurbs(ctrl, knots, weights.Count == ctrl.Count ? weights : null, degree, tol);
        else if (fit.Count >= 2)
            pts = fit;           // fit points lie on the curve
        else
            pts = ctrl;          // last resort: the control polygon
        if (pts.Count >= 2)
        {
            if (closed && pts.Count > 2 && (pts[0] - pts[^1]).Length < 1e-6) pts.RemoveAt(pts.Count - 1);
            result.Add(new Polyline(pts, closed));
        }
        return idx;
    }

    /// <summary>Samples a (rational) B-spline with de Boor's algorithm.</summary>
    private static List<Vec2> EvaluateNurbs(List<Vec2> ctrl, List<double> knots, List<double>? weights, int p, double tol)
    {
        int n = ctrl.Count - 1;
        double u0 = knots[p], u1 = knots[n + 1];
        var result = new List<Vec2>();
        if (!(u1 > u0)) return ctrl;

        double polyLen = 0;
        for (int i = 1; i < ctrl.Count; i++) polyLen += (ctrl[i] - ctrl[i - 1]).Length;
        // About one sample per 0.2 mm of control polygon, at least 16 per span, capped for huge splines.
        int samples = Math.Min(20000, Math.Max(16 * Math.Max(1, n - p + 1), (int)Math.Min(20000, Math.Ceiling(polyLen / (20 * tol)))));

        var dx = new double[p + 1];
        var dy = new double[p + 1];
        var dw = new double[p + 1];
        for (int s = 0; s <= samples; s++)
        {
            double u = u0 + (u1 - u0) * s / samples;
            // Knot span k with knots[k] <= u < knots[k+1] (clamped to the last valid span at u1).
            int k = p;
            while (k < n && knots[k + 1] <= u) k++;
            for (int j = 0; j <= p; j++)
            {
                int ci = Math.Clamp(j + k - p, 0, n);
                double w = weights?[ci] ?? 1;
                dx[j] = ctrl[ci].X * w;
                dy[j] = ctrl[ci].Y * w;
                dw[j] = w;
            }
            for (int r = 1; r <= p; r++)
            {
                for (int j = p; j >= r; j--)
                {
                    int i = j + k - p;
                    double denom = knots[i + p - r + 1] - knots[i];
                    double alpha = denom == 0 ? 0 : (u - knots[i]) / denom;
                    dx[j] = (1 - alpha) * dx[j - 1] + alpha * dx[j];
                    dy[j] = (1 - alpha) * dy[j - 1] + alpha * dy[j];
                    dw[j] = (1 - alpha) * dw[j - 1] + alpha * dw[j];
                }
            }
            double ww = dw[p] == 0 ? 1 : dw[p];
            result.Add(new Vec2(dx[p] / ww, dy[p] / ww));
        }
        return result;
    }
}
