using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using LaserPilot.Core.Geometry;

namespace LaserPilot.Core.Importers;

/// <summary>
/// Lightweight SVG importer: paths, rects (incl. rounded), circles, ellipses,
/// lines, polylines, polygons and &lt;use&gt; references, with nested group
/// transforms. Hidden layers and non-rendered content (defs, clip paths,
/// masks, markers…) are skipped. Curves are flattened adaptively to within
/// ~0.02 mm. Output is in millimetres.
/// </summary>
public static class SvgImporter
{
    private const double ToleranceMm = 0.02;
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    // Content that is never drawn by itself (only referenced, or not geometry).
    private static readonly HashSet<string> NonRendered = new(StringComparer.Ordinal)
    {
        "defs", "clipPath", "mask", "symbol", "marker", "pattern", "linearGradient", "radialGradient",
        "style", "script", "title", "desc", "metadata", "filter", "foreignObject", "text", "image",
    };

    public static List<Polyline> Import(string filePath)
    {
        using var reader = XmlReader.Create(filePath, SafeSettings());
        return Import(XDocument.Load(reader));
    }

    /// <summary>Parses SVG markup (used by tests and paste).</summary>
    public static List<Polyline> ImportText(string svg)
    {
        using var reader = XmlReader.Create(new StringReader(svg), SafeSettings());
        return Import(XDocument.Load(reader));
    }

    // Illustrator's "preserve editing" files define entities (&ns_ai; …) in an inline DTD,
    // so the DTD is parsed — but external DTDs are never fetched (no resolver) and entity
    // expansion is capped.
    private static XmlReaderSettings SafeSettings() => new()
    {
        DtdProcessing = DtdProcessing.Parse,
        XmlResolver = null,
        MaxCharactersFromEntities = 1_000_000,
        IgnoreComments = false,
    };

    private sealed class Context
    {
        public required Dictionary<string, XElement> Ids { get; init; }
        public List<Polyline> Result { get; } = new();
        public int UseDepth { get; set; }
    }

    private static List<Polyline> Import(XDocument doc)
    {
        var root = doc.Root ?? throw new InvalidDataException("Not an SVG file.");
        if (root.Name.LocalName != "svg") throw new InvalidDataException("Not an SVG file (no <svg> root element).");

        // Illustrator writes "px" meaning 1/72 inch rather than CSS's 1/96 inch.
        bool illustrator = doc.Nodes().OfType<XComment>().Any(c => c.Value.Contains("Adobe Illustrator", StringComparison.OrdinalIgnoreCase));
        double pxToMm = illustrator ? 25.4 / 72.0 : 25.4 / 96.0;

        // Document scale: user units → mm, honouring width/height + viewBox when present.
        var docScale = Mat2D.Scale(pxToMm, pxToMm);
        var viewBox = root.Attribute("viewBox")?.Value;
        var widthAttr = root.Attribute("width")?.Value;
        var heightAttr = root.Attribute("height")?.Value;
        if (viewBox is not null)
        {
            var vb = SplitNumbers(viewBox);
            if (vb.Count == 4 && vb[2] > 0 && vb[3] > 0)
            {
                double w = widthAttr is null ? 0 : ParseLengthMm(widthAttr, pxToMm);
                double h = heightAttr is null ? 0 : ParseLengthMm(heightAttr, pxToMm);
                double sx = w > 0 ? w / vb[2] : pxToMm;
                double sy = h > 0 ? h / vb[3] : pxToMm;
                if (w > 0 && h <= 0) sy = sx;
                if (h > 0 && w <= 0) sx = sy;
                // Default preserveAspectRatio is "xMidYMid meet": uniform scale (the larger one for "slice").
                var par = root.Attribute("preserveAspectRatio")?.Value?.Trim() ?? "";
                if (!par.StartsWith("none", StringComparison.Ordinal))
                    sx = sy = par.EndsWith("slice", StringComparison.Ordinal) ? Math.Max(sx, sy) : Math.Min(sx, sy);
                docScale = Mat2D.Scale(sx, sy).Multiply(Mat2D.Translate(-vb[0], -vb[1]));
            }
        }

        var ids = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var e in root.DescendantsAndSelf())
            if (e.Attribute("id")?.Value is { Length: > 0 } id) ids.TryAdd(id, e);

        var ctx = new Context { Ids = ids };
        foreach (var child in root.Elements()) Walk(child, docScale, ctx);
        return ctx.Result;
    }

    private static bool IsHidden(XElement el)
    {
        if (el.Attribute("display")?.Value.Trim() == "none") return true;
        if (el.Attribute("visibility")?.Value.Trim() is "hidden" or "collapse") return true;
        var style = el.Attribute("style")?.Value;
        if (style is null) return false;
        var compact = style.Replace(" ", "", StringComparison.Ordinal);
        return compact.Contains("display:none", StringComparison.OrdinalIgnoreCase) ||
               compact.Contains("visibility:hidden", StringComparison.OrdinalIgnoreCase);
    }

    private static void Walk(XElement el, Mat2D xform, Context ctx)
    {
        if (IsHidden(el)) return;
        var name = el.Name.LocalName;
        if (NonRendered.Contains(name)) return;

        var m = xform.Multiply(ParseTransform(el.Attribute("transform")?.Value));
        double mmPerUnit = Math.Sqrt(Math.Abs(m.A * m.D - m.B * m.C));
        if (mmPerUnit <= 0 || double.IsNaN(mmPerUnit)) return; // degenerate (e.g. scale(0))
        double tol = ToleranceMm / mmPerUnit;                    // tolerance in user units

        switch (name)
        {
            case "path":
                foreach (var poly in ParsePathData(el.Attribute("d")?.Value ?? "", tol))
                    Add(poly, m, ctx);
                return;
            case "rect":
            {
                double x = Len(el, "x"), y = Len(el, "y"), w = Len(el, "width"), h = Len(el, "height");
                if (w <= 0 || h <= 0) return;
                double rx = Len(el, "rx"), ry = Len(el, "ry");
                if (el.Attribute("rx") is null) rx = ry;
                if (el.Attribute("ry") is null) ry = rx;
                rx = Math.Min(Math.Max(0, rx), w / 2);
                ry = Math.Min(Math.Max(0, ry), h / 2);
                Add(rx > 0 && ry > 0 ? RoundedRect(x, y, w, h, rx, ry, tol)
                    : new Polyline(new[] { new Vec2(x, y), new Vec2(x + w, y), new Vec2(x + w, y + h), new Vec2(x, y + h) }, closed: true), m, ctx);
                return;
            }
            case "circle":
            {
                double r = Len(el, "r");
                if (r > 0) Add(Ellipse(Len(el, "cx"), Len(el, "cy"), r, r, tol), m, ctx);
                return;
            }
            case "ellipse":
            {
                double rx = Len(el, "rx"), ry = Len(el, "ry");
                if (rx > 0 && ry > 0) Add(Ellipse(Len(el, "cx"), Len(el, "cy"), rx, ry, tol), m, ctx);
                return;
            }
            case "line":
                Add(new Polyline(new[] { new Vec2(Len(el, "x1"), Len(el, "y1")), new Vec2(Len(el, "x2"), Len(el, "y2")) }), m, ctx);
                return;
            case "polyline":
            case "polygon":
            {
                var nums = SplitNumbers(el.Attribute("points")?.Value ?? "");
                var pts = new List<Vec2>();
                for (int i = 0; i + 1 < nums.Count; i += 2) pts.Add(new Vec2(nums[i], nums[i + 1]));
                if (pts.Count >= 2) Add(new Polyline(pts, closed: name == "polygon"), m, ctx);
                return;
            }
            case "use":
                WalkUse(el, m, ctx);
                return;
            case "switch":
                // A <switch> renders only its first child whose conditions we meet. Skip
                // extension-only content such as Illustrator's private <foreignObject> data.
                var chosen = el.Elements().FirstOrDefault(c =>
                    c.Name.LocalName != "foreignObject" &&
                    string.IsNullOrWhiteSpace(c.Attribute("requiredExtensions")?.Value));
                if (chosen is not null) Walk(chosen, m, ctx);
                return;
        }

        foreach (var child in el.Elements()) Walk(child, m, ctx);
    }

    private static void WalkUse(XElement use, Mat2D m, Context ctx)
    {
        var href = use.Attribute("href")?.Value ?? use.Attribute(XLink + "href")?.Value;
        if (href is null || !href.StartsWith('#') || ctx.UseDepth >= 16) return;
        if (!ctx.Ids.TryGetValue(href[1..], out var target) || target == use || target.DescendantsAndSelf().Contains(use)) return;

        var placed = m.Multiply(Mat2D.Translate(Len(use, "x"), Len(use, "y")));
        ctx.UseDepth++;
        try
        {
            if (target.Name.LocalName == "symbol")
            {
                if (IsHidden(target)) return;
                foreach (var child in target.Elements()) Walk(child, placed, ctx);
            }
            else Walk(target, placed, ctx);
        }
        finally { ctx.UseDepth--; }
    }

    private static void Add(Polyline poly, Mat2D m, Context ctx)
    {
        if (poly.Points.Count < 2) return;
        poly.Transform(m);
        ctx.Result.Add(poly);
    }

    private static int ArcSegments(double radius, double sweepRad, double tol)
    {
        if (radius <= tol) return Math.Max(2, (int)Math.Ceiling(Math.Abs(sweepRad) / (Math.PI / 2)));
        double step = 2 * Math.Acos(Math.Clamp(1 - tol / radius, -1, 1));
        if (step <= 1e-6) step = 1e-6;
        return Math.Clamp((int)Math.Ceiling(Math.Abs(sweepRad) / step), 2, 2000);
    }

    private static Polyline Ellipse(double cx, double cy, double rx, double ry, double tol)
    {
        int n = Math.Max(12, ArcSegments(Math.Max(rx, ry), 2 * Math.PI, tol));
        var pts = new List<Vec2>(n);
        for (int i = 0; i < n; i++)
        {
            var t = 2 * Math.PI * i / n;
            pts.Add(new Vec2(cx + rx * Math.Cos(t), cy + ry * Math.Sin(t)));
        }
        return new Polyline(pts, closed: true);
    }

    private static Polyline RoundedRect(double x, double y, double w, double h, double rx, double ry, double tol)
    {
        var pts = new List<Vec2>();
        int n = Math.Max(3, ArcSegments(Math.Max(rx, ry), Math.PI / 2, tol));
        void Corner(double cx, double cy, double startAngle)
        {
            for (int i = 0; i <= n; i++)
            {
                var t = startAngle + Math.PI / 2 * i / n;
                pts.Add(new Vec2(cx + rx * Math.Cos(t), cy + ry * Math.Sin(t)));
            }
        }
        // SVG is Y-down: walk top edge → right → bottom → left.
        Corner(x + w - rx, y + ry, -Math.PI / 2);
        Corner(x + w - rx, y + h - ry, 0);
        Corner(x + rx, y + h - ry, Math.PI / 2);
        Corner(x + rx, y + ry, Math.PI);
        return new Polyline(pts, closed: true);
    }

    // ---------- lengths ----------

    /// <summary>An attribute length in user units (CSS px); absolute units are converted.</summary>
    private static double Len(XElement el, string name)
    {
        var s = el.Attribute(name)?.Value?.Trim();
        if (string.IsNullOrEmpty(s)) return 0;
        double unit = 1;
        if (s.EndsWith("mm", StringComparison.Ordinal)) { unit = 96 / 25.4; s = s[..^2]; }
        else if (s.EndsWith("cm", StringComparison.Ordinal)) { unit = 96 / 2.54; s = s[..^2]; }
        else if (s.EndsWith("in", StringComparison.Ordinal)) { unit = 96; s = s[..^2]; }
        else if (s.EndsWith("pt", StringComparison.Ordinal)) { unit = 96 / 72.0; s = s[..^2]; }
        else if (s.EndsWith("pc", StringComparison.Ordinal)) { unit = 16; s = s[..^2]; }
        else if (s.EndsWith("px", StringComparison.Ordinal)) { s = s[..^2]; }
        else if (s.EndsWith('%')) return 0;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v * unit : 0;
    }

    private static double ParseLengthMm(string s, double pxToMm)
    {
        s = s.Trim();
        double unit = pxToMm;
        if (s.EndsWith("mm", StringComparison.Ordinal)) { unit = 1; s = s[..^2]; }
        else if (s.EndsWith("cm", StringComparison.Ordinal)) { unit = 10; s = s[..^2]; }
        else if (s.EndsWith("in", StringComparison.Ordinal)) { unit = 25.4; s = s[..^2]; }
        else if (s.EndsWith("pt", StringComparison.Ordinal)) { unit = 25.4 / 72; s = s[..^2]; }
        else if (s.EndsWith("pc", StringComparison.Ordinal)) { unit = 25.4 / 6; s = s[..^2]; }
        else if (s.EndsWith("px", StringComparison.Ordinal)) { s = s[..^2]; }
        else if (s.EndsWith('%')) return 0;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v * unit : 0;
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
                "skewX" => new Mat2D(1, 0, Math.Tan(args.ElementAtOrDefault(0) * Math.PI / 180), 1, 0, 0),
                "skewY" => new Mat2D(1, Math.Tan(args.ElementAtOrDefault(0) * Math.PI / 180), 0, 1, 0, 0),
                "matrix" when args.Count >= 6 => new Mat2D(args[0], args[1], args[2], args[3], args[4], args[5]),
                _ => Mat2D.Identity,
            };
            m = m.Multiply(op);
            i = close + 1;
        }
        return m;
    }

    // ---------- path d="..." ----------

    /// <summary>Cursor over path data. Arc flags are read as single characters
    /// ("a5 5 0 015 5" is legal SVG: flags 0 and 1, then x = 5).</summary>
    private sealed class PathReader(string d)
    {
        private int _i;

        private void SkipSeparators()
        {
            while (_i < d.Length && (char.IsWhiteSpace(d[_i]) || d[_i] == ',')) _i++;
        }

        public bool AtEnd { get { SkipSeparators(); return _i >= d.Length; } }

        public bool NextIsNumber
        {
            get
            {
                SkipSeparators();
                if (_i >= d.Length) return false;
                char c = d[_i];
                return char.IsDigit(c) || c is '.' or '-' or '+';
            }
        }

        public char? ReadCommand()
        {
            SkipSeparators();
            if (_i < d.Length && "MmLlHhVvCcSsQqTtAaZz".Contains(d[_i])) return d[_i++];
            return null;
        }

        public void SkipOne() { if (_i < d.Length) _i++; }

        public bool TryNumber(out double value)
        {
            value = 0;
            SkipSeparators();
            int start = _i, j = _i;
            if (j < d.Length && d[j] is '+' or '-') j++;
            bool digits = false;
            while (j < d.Length && char.IsDigit(d[j])) { j++; digits = true; }
            if (j < d.Length && d[j] == '.')
            {
                j++;
                while (j < d.Length && char.IsDigit(d[j])) { j++; digits = true; }
            }
            if (!digits) return false;
            if (j < d.Length && d[j] is 'e' or 'E')
            {
                int k = j + 1;
                if (k < d.Length && d[k] is '+' or '-') k++;
                if (k < d.Length && char.IsDigit(d[k]))
                {
                    while (k < d.Length && char.IsDigit(d[k])) k++;
                    j = k;
                }
            }
            if (!double.TryParse(d.AsSpan(start, j - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value)) return false;
            _i = j;
            return true;
        }

        public bool TryFlag(out bool flag)
        {
            SkipSeparators();
            flag = false;
            if (_i < d.Length && d[_i] is '0' or '1') { flag = d[_i++] == '1'; return true; }
            return false;
        }
    }

    private static List<Polyline> ParsePathData(string d, double tol)
    {
        var result = new List<Polyline>();
        var reader = new PathReader(d);
        var cur = new Vec2(0, 0);
        var start = new Vec2(0, 0);
        Vec2? lastCubicCtrl = null, lastQuadCtrl = null;
        Polyline? poly = null;

        void Flush(bool closed)
        {
            if (poly is { Points.Count: >= 2 })
            {
                poly.Closed = closed;
                result.Add(poly);
            }
            poly = null;
        }
        void Begin(Vec2 p)
        {
            Flush(closed: false);
            poly = new Polyline();
            poly.Points.Add(p);
            start = p;
        }
        // Drawing right after Z starts a new subpath at the same initial point.
        void EnsureOpen() { if (poly is null) Begin(cur); }
        void AddPoint(Vec2 p) { EnsureOpen(); poly!.Points.Add(p); }

        char cmd = '\0';
        while (!reader.AtEnd)
        {
            var c = reader.ReadCommand();
            if (c is { } letter) cmd = letter;
            else if (cmd == '\0' || cmd is 'Z' or 'z' || !reader.NextIsNumber)
            {
                reader.SkipOne(); // junk (or numbers after Z): skip, guaranteeing progress
                continue;
            }

            bool rel = char.IsLower(cmd);
            bool ok = true;
            bool Num(out double v) { if (reader.TryNumber(out v)) return true; ok = false; return false; }
            Vec2 Pt()
            {
                Num(out var x); Num(out var y);
                return rel ? cur + new Vec2(x, y) : new Vec2(x, y);
            }

            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                {
                    var p = Pt();
                    if (!ok) break;
                    Begin(p); cur = p;
                    cmd = rel ? 'l' : 'L'; // subsequent pairs are implicit LineTo
                    lastCubicCtrl = lastQuadCtrl = null;
                    break;
                }
                case 'L':
                {
                    var p = Pt();
                    if (!ok) break;
                    AddPoint(p); cur = p;
                    lastCubicCtrl = lastQuadCtrl = null;
                    break;
                }
                case 'H':
                {
                    if (!Num(out var x)) break;
                    cur = new Vec2(rel ? cur.X + x : x, cur.Y);
                    AddPoint(cur);
                    lastCubicCtrl = lastQuadCtrl = null;
                    break;
                }
                case 'V':
                {
                    if (!Num(out var y)) break;
                    cur = new Vec2(cur.X, rel ? cur.Y + y : y);
                    AddPoint(cur);
                    lastCubicCtrl = lastQuadCtrl = null;
                    break;
                }
                case 'C':
                {
                    var c1 = Pt(); var c2 = Pt(); var p = Pt();
                    if (!ok) break;
                    Cubic(cur, c1, c2, p);
                    lastCubicCtrl = c2; lastQuadCtrl = null; cur = p;
                    break;
                }
                case 'S':
                {
                    var c1 = lastCubicCtrl is { } lc ? cur + (cur - lc) : cur;
                    var c2 = Pt(); var p = Pt();
                    if (!ok) break;
                    Cubic(cur, c1, c2, p);
                    lastCubicCtrl = c2; lastQuadCtrl = null; cur = p;
                    break;
                }
                case 'Q':
                {
                    var q = Pt(); var p = Pt();
                    if (!ok) break;
                    Quad(cur, q, p);
                    lastQuadCtrl = q; lastCubicCtrl = null; cur = p;
                    break;
                }
                case 'T':
                {
                    var q = lastQuadCtrl is { } lq ? cur + (cur - lq) : cur;
                    var p = Pt();
                    if (!ok) break;
                    Quad(cur, q, p);
                    lastQuadCtrl = q; lastCubicCtrl = null; cur = p;
                    break;
                }
                case 'A':
                {
                    if (!Num(out var rx) || !Num(out var ry) || !Num(out var rot)) break;
                    if (!reader.TryFlag(out var largeArc) || !reader.TryFlag(out var sweep)) { ok = false; break; }
                    var p = Pt();
                    if (!ok) break;
                    Arc(cur, rx, ry, rot, largeArc, sweep, p);
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
            }
            if (!ok) break; // malformed data: keep what was parsed so far
        }
        Flush(closed: false);
        return result;

        int CurveSegments(double secondDifference)
        {
            // Uniform-parameter flattening error ≤ M/(8n²), M = max |second derivative|.
            if (secondDifference <= 0) return 1;
            return Math.Clamp((int)Math.Ceiling(Math.Sqrt(secondDifference / (8 * tol))), 1, 1000);
        }
        void Cubic(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3)
        {
            double dd = 6 * Math.Max((p0 - p1 * 2 + p2).Length, (p1 - p2 * 2 + p3).Length);
            int n = CurveSegments(dd);
            for (int k = 1; k <= n; k++)
            {
                double t = (double)k / n, u = 1 - t;
                AddPoint(p0 * (u * u * u) + p1 * (3 * u * u * t) + p2 * (3 * u * t * t) + p3 * (t * t * t));
            }
        }
        void Quad(Vec2 p0, Vec2 p1, Vec2 p2)
        {
            int n = CurveSegments(2 * (p0 - p1 * 2 + p2).Length);
            for (int k = 1; k <= n; k++)
            {
                double t = (double)k / n, u = 1 - t;
                AddPoint(p0 * (u * u) + p1 * (2 * u * t) + p2 * (t * t));
            }
        }
        void Arc(Vec2 p0, double rx, double ry, double rotDeg, bool largeArc, bool sweepFlag, Vec2 p1)
        {
            // SVG arc endpoint → center parameterization (W3C spec appendix).
            if (rx == 0 || ry == 0 || p0 == p1) { AddPoint(p1); return; }
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
            var coef = (den == 0 ? 0 : Math.Sqrt(Math.Max(0, num / den))) * (largeArc == sweepFlag ? -1 : 1);
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
            if (!sweepFlag && dTheta > 0) dTheta -= 2 * Math.PI;
            if (sweepFlag && dTheta < 0) dTheta += 2 * Math.PI;
            int segs = ArcSegments(Math.Max(rx, ry), dTheta, tol);
            for (int k = 1; k <= segs; k++)
            {
                var t = theta1 + dTheta * k / segs;
                var px = cx + rx * Math.Cos(t) * cos - ry * Math.Sin(t) * sin;
                var py = cy + rx * Math.Cos(t) * sin + ry * Math.Sin(t) * cos;
                AddPoint(k == segs ? p1 : new Vec2(px, py));
            }
        }
    }

    private static List<double> SplitNumbers(string s)
    {
        // Handles "1,2 3-4" and "1.5.5" style compaction too.
        var list = new List<double>();
        var reader = new PathReader(s);
        while (!reader.AtEnd)
        {
            if (reader.TryNumber(out var v)) list.Add(v);
            else reader.SkipOne();
        }
        return list;
    }
}
