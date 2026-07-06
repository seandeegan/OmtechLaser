using System.Globalization;
using System.Xml.Linq;
using LaserPilot.Core.Geometry;

namespace LaserPilot.Core.Importers;

/// <summary>
/// Lightweight SVG importer: paths, rects, circles, ellipses, lines,
/// polylines and polygons, with nested group transforms. Curves are
/// flattened to polylines. Output is in millimetres (96 dpi user units).
/// </summary>
public static class SvgImporter
{
    private const double PxToMm = 25.4 / 96.0;
    private const int CurveSegments = 24;

    public static List<Polyline> Import(string filePath)
    {
        var doc = XDocument.Load(filePath);
        var root = doc.Root ?? throw new InvalidDataException("Not an SVG file.");
        var result = new List<Polyline>();

        // Document scale: user units → mm, honouring width/height + viewBox when present.
        var docScale = Mat2D.Scale(PxToMm, PxToMm);
        var viewBox = root.Attribute("viewBox")?.Value;
        var widthAttr = root.Attribute("width")?.Value;
        var heightAttr = root.Attribute("height")?.Value;
        if (viewBox is not null && widthAttr is not null && heightAttr is not null)
        {
            var vb = SplitNumbers(viewBox);
            var w = ParseLengthMm(widthAttr);
            var h = ParseLengthMm(heightAttr);
            if (vb.Count == 4 && vb[2] > 0 && vb[3] > 0 && w > 0 && h > 0)
                docScale = Mat2D.Scale(w / vb[2], h / vb[3]).Multiply(Mat2D.Translate(-vb[0], -vb[1]));
        }

        Walk(root, docScale, result);
        return result;
    }

    private static void Walk(XElement el, Mat2D xform, List<Polyline> result)
    {
        var local = ParseTransform(el.Attribute("transform")?.Value);
        var m = xform.Multiply(local);
        var name = el.Name.LocalName;

        switch (name)
        {
            case "path":
                foreach (var poly in ParsePathData(el.Attribute("d")?.Value ?? ""))
                { poly.Transform(m); result.Add(poly); }
                break;
            case "rect":
            {
                double x = Attr(el, "x"), y = Attr(el, "y"), w = Attr(el, "width"), h = Attr(el, "height");
                if (w > 0 && h > 0)
                {
                    var poly = new Polyline(new[] { new Vec2(x, y), new Vec2(x + w, y), new Vec2(x + w, y + h), new Vec2(x, y + h) }, closed: true);
                    poly.Transform(m); result.Add(poly);
                }
                break;
            }
            case "circle":
            {
                double cx = Attr(el, "cx"), cy = Attr(el, "cy"), r = Attr(el, "r");
                if (r > 0) { var poly = Ellipse(cx, cy, r, r); poly.Transform(m); result.Add(poly); }
                break;
            }
            case "ellipse":
            {
                double cx = Attr(el, "cx"), cy = Attr(el, "cy"), rx = Attr(el, "rx"), ry = Attr(el, "ry");
                if (rx > 0 && ry > 0) { var poly = Ellipse(cx, cy, rx, ry); poly.Transform(m); result.Add(poly); }
                break;
            }
            case "line":
            {
                var poly = new Polyline(new[] { new Vec2(Attr(el, "x1"), Attr(el, "y1")), new Vec2(Attr(el, "x2"), Attr(el, "y2")) });
                poly.Transform(m); result.Add(poly);
                break;
            }
            case "polyline":
            case "polygon":
            {
                var nums = SplitNumbers(el.Attribute("points")?.Value ?? "");
                var pts = new List<Vec2>();
                for (int i = 0; i + 1 < nums.Count; i += 2) pts.Add(new Vec2(nums[i], nums[i + 1]));
                if (pts.Count >= 2)
                {
                    var poly = new Polyline(pts, closed: name == "polygon");
                    poly.Transform(m); result.Add(poly);
                }
                break;
            }
        }

        foreach (var child in el.Elements()) Walk(child, m, result);
    }

    private static Polyline Ellipse(double cx, double cy, double rx, double ry)
    {
        var pts = new List<Vec2>();
        const int n = 48;
        for (int i = 0; i < n; i++)
        {
            var t = 2 * Math.PI * i / n;
            pts.Add(new Vec2(cx + rx * Math.Cos(t), cy + ry * Math.Sin(t)));
        }
        return new Polyline(pts, closed: true);
    }

    private static double Attr(XElement el, string name) =>
        double.TryParse(el.Attribute(name)?.Value?.TrimEnd("pxmcin%".ToCharArray()), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double ParseLengthMm(string s)
    {
        s = s.Trim();
        double unit = PxToMm;
        if (s.EndsWith("mm")) { unit = 1; s = s[..^2]; }
        else if (s.EndsWith("cm")) { unit = 10; s = s[..^2]; }
        else if (s.EndsWith("in")) { unit = 25.4; s = s[..^2]; }
        else if (s.EndsWith("pt")) { unit = 25.4 / 72; s = s[..^2]; }
        else if (s.EndsWith("px")) { s = s[..^2]; }
        else if (s.EndsWith('%')) return 0;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v * unit : 0;
    }

    // ---------- transform="..." ----------

    private static Mat2D ParseTransform(string? t)
    {
        var m = Mat2D.Identity;
        if (string.IsNullOrWhiteSpace(t)) return m;
        int i = 0;
        while (i < t.Length)
        {
            var open = t.IndexOf('(', i);
            if (open < 0) break;
            var close = t.IndexOf(')', open);
            if (close < 0) break;
            var fn = t[i..open].Trim(' ', ',', '\t', '\n', '\r');
            var args = SplitNumbers(t[(open + 1)..close]);
            var op = fn switch
            {
                "translate" => Mat2D.Translate(args.ElementAtOrDefault(0), args.ElementAtOrDefault(1)),
                "scale" => Mat2D.Scale(args.ElementAtOrDefault(0), args.Count > 1 ? args[1] : args.ElementAtOrDefault(0)),
                "rotate" when args.Count >= 3 => Mat2D.Translate(args[1], args[2]).Multiply(Mat2D.Rotate(args[0])).Multiply(Mat2D.Translate(-args[1], -args[2])),
                "rotate" => Mat2D.Rotate(args.ElementAtOrDefault(0)),
                "matrix" when args.Count >= 6 => new Mat2D(args[0], args[1], args[2], args[3], args[4], args[5]),
                _ => Mat2D.Identity,
            };
            m = m.Multiply(op);
            i = close + 1;
        }
        return m;
    }

    // ---------- path d="..." ----------

    private static List<Polyline> ParsePathData(string d)
    {
        var result = new List<Polyline>();
        var tokens = TokenizePath(d);
        int i = 0;
        var cur = new Vec2(0, 0);
        var start = new Vec2(0, 0);
        Vec2? lastCubicCtrl = null, lastQuadCtrl = null;
        Polyline? poly = null;

        void Begin(Vec2 p)
        {
            Flush(closed: false);
            poly = new Polyline();
            poly.Points.Add(p);
            start = p;
        }
        void Flush(bool closed)
        {
            if (poly is { Points.Count: >= 2 })
            {
                poly.Closed = closed;
                result.Add(poly);
            }
            poly = null;
        }
        void LineTo(Vec2 p) { poly?.Points.Add(p); cur = p; }

        char cmd = ' ';
        while (i < tokens.Count)
        {
            if (tokens[i] is string s) { cmd = s[0]; i++; }
            else if (cmd == ' ') break;

            bool rel = char.IsLower(cmd);
            double N() { if (i < tokens.Count && tokens[i] is double v) { i++; return v; } return 0; }
            Vec2 Pt() { var x = N(); var y = N(); return rel ? cur + new Vec2(x, y) : new Vec2(x, y); }

            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                {
                    var p = Pt();
                    Begin(p); cur = p;
                    cmd = rel ? 'l' : 'L'; // subsequent pairs are implicit LineTo
                    lastCubicCtrl = lastQuadCtrl = null;
                    break;
                }
                case 'L': LineTo(Pt()); lastCubicCtrl = lastQuadCtrl = null; break;
                case 'H': { var x = N(); LineTo(new Vec2(rel ? cur.X + x : x, cur.Y)); lastCubicCtrl = lastQuadCtrl = null; break; }
                case 'V': { var y = N(); LineTo(new Vec2(cur.X, rel ? cur.Y + y : y)); lastCubicCtrl = lastQuadCtrl = null; break; }
                case 'C':
                {
                    var c1 = Pt(); var c2 = Pt(); var p = Pt();
                    Cubic(cur, c1, c2, p);
                    lastCubicCtrl = c2; lastQuadCtrl = null; cur = p;
                    break;
                }
                case 'S':
                {
                    var c1 = lastCubicCtrl is { } lc ? cur + (cur - lc) : cur;
                    var c2 = Pt(); var p = Pt();
                    Cubic(cur, c1, c2, p);
                    lastCubicCtrl = c2; lastQuadCtrl = null; cur = p;
                    break;
                }
                case 'Q':
                {
                    var c = Pt(); var p = Pt();
                    Quad(cur, c, p);
                    lastQuadCtrl = c; lastCubicCtrl = null; cur = p;
                    break;
                }
                case 'T':
                {
                    var c = lastQuadCtrl is { } lq ? cur + (cur - lq) : cur;
                    var p = Pt();
                    Quad(cur, c, p);
                    lastQuadCtrl = c; lastCubicCtrl = null; cur = p;
                    break;
                }
                case 'A':
                {
                    var rx = N(); var ry = N(); var rot = N(); var laf = N(); var sf = N();
                    var p = Pt();
                    Arc(cur, rx, ry, rot, laf != 0, sf != 0, p);
                    lastCubicCtrl = lastQuadCtrl = null; cur = p;
                    break;
                }
                case 'Z':
                {
                    if (poly is not null)
                    {
                        Flush(closed: true);
                        cur = start;
                    }
                    lastCubicCtrl = lastQuadCtrl = null;
                    break;
                }
                default:
                    i++; // skip unknown token
                    break;
            }
        }
        Flush(closed: false);
        return result;

        void Cubic(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3)
        {
            for (int k = 1; k <= CurveSegments; k++)
            {
                double t = (double)k / CurveSegments, u = 1 - t;
                var p = p0 * (u * u * u) + p1 * (3 * u * u * t) + p2 * (3 * u * t * t) + p3 * (t * t * t);
                poly?.Points.Add(p);
            }
        }
        void Quad(Vec2 p0, Vec2 p1, Vec2 p2)
        {
            for (int k = 1; k <= CurveSegments; k++)
            {
                double t = (double)k / CurveSegments, u = 1 - t;
                var p = p0 * (u * u) + p1 * (2 * u * t) + p2 * (t * t);
                poly?.Points.Add(p);
            }
        }
        void Arc(Vec2 p0, double rx, double ry, double rotDeg, bool largeArc, bool sweep, Vec2 p1)
        {
            // SVG arc endpoint → center parameterization (W3C spec appendix).
            if (rx == 0 || ry == 0 || p0 == p1) { poly?.Points.Add(p1); return; }
            rx = Math.Abs(rx); ry = Math.Abs(ry);
            var phi = rotDeg * Math.PI / 180;
            var (cos, sin) = (Math.Cos(phi), Math.Sin(phi));
            var dx = (p0.X - p1.X) / 2; var dy = (p0.Y - p1.Y) / 2;
            var x1 = cos * dx + sin * dy;
            var y1 = -sin * dx + cos * dy;
            var lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
            if (lambda > 1) { var s = Math.Sqrt(lambda); rx *= s; ry *= s; }
            var num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
            var den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
            var coef = Math.Sqrt(Math.Max(0, num / den)) * (largeArc == sweep ? -1 : 1);
            var cxp = coef * rx * y1 / ry;
            var cyp = -coef * ry * x1 / rx;
            var cx = cos * cxp - sin * cyp + (p0.X + p1.X) / 2;
            var cy = sin * cxp + cos * cyp + (p0.Y + p1.Y) / 2;
            double Angle(double ux, double uy, double vx, double vy)
            {
                var dot = ux * vx + uy * vy;
                var len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
                var a = Math.Acos(Math.Clamp(dot / len, -1, 1));
                return (ux * vy - uy * vx) < 0 ? -a : a;
            }
            var theta1 = Angle(1, 0, (x1 - cxp) / rx, (y1 - cyp) / ry);
            var dTheta = Angle((x1 - cxp) / rx, (y1 - cyp) / ry, (-x1 - cxp) / rx, (-y1 - cyp) / ry);
            if (!sweep && dTheta > 0) dTheta -= 2 * Math.PI;
            if (sweep && dTheta < 0) dTheta += 2 * Math.PI;
            int segs = Math.Max(2, (int)(Math.Abs(dTheta) / (2 * Math.PI) * 64));
            for (int k = 1; k <= segs; k++)
            {
                var t = theta1 + dTheta * k / segs;
                var px = cx + rx * Math.Cos(t) * cos - ry * Math.Sin(t) * sin;
                var py = cy + rx * Math.Cos(t) * sin + ry * Math.Sin(t) * cos;
                poly?.Points.Add(new Vec2(px, py));
            }
        }
    }

    private static List<object> TokenizePath(string d)
    {
        var tokens = new List<object>();
        int i = 0;
        while (i < d.Length)
        {
            char c = d[i];
            if (char.IsLetter(c)) { tokens.Add(c.ToString()); i++; }
            else if (c == '-' || c == '+' || c == '.' || char.IsDigit(c))
            {
                int j = i;
                bool seenDot = false, seenExp = false;
                if (d[j] == '-' || d[j] == '+') j++;
                while (j < d.Length)
                {
                    char k = d[j];
                    if (char.IsDigit(k)) j++;
                    else if (k == '.' && !seenDot && !seenExp) { seenDot = true; j++; }
                    else if ((k == 'e' || k == 'E') && !seenExp) { seenExp = true; j++; if (j < d.Length && (d[j] == '-' || d[j] == '+')) j++; }
                    else break;
                }
                if (double.TryParse(d[i..j], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    tokens.Add(v);
                i = j == i ? i + 1 : j;
            }
            else i++;
        }
        return tokens;
    }

    private static List<double> SplitNumbers(string s)
    {
        var list = new List<double>();
        foreach (var part in s.Split(new[] { ' ', ',', '\t', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries))
            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                list.Add(v);
        return list;
    }
}
