
using LaserPilot.Core.Geometry;
using LaserPilot.Core.Importers;

namespace LaserPilot.Core.Tests;

[TestClass]
public sealed class ImporterTests
{
    private const string MmSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"100mm\" height=\"100mm\" viewBox=\"0 0 100 100\">{0}</svg>";

    private static List<Polyline> Svg(string body) => SvgImporter.ImportText(MmSvg.Replace("{0}", body));

    private static (Vec2 min, Vec2 max) Bounds(IEnumerable<Polyline> p) => Polyline.BoundsOf(p);

    [TestMethod]
    public void Svg_ViewBoxInMillimetres()
    {
        var r = Svg("<rect x=\"10\" y=\"20\" width=\"30\" height=\"40\"/>");
        Assert.AreEqual(1, r.Count);
        var (min, max) = Bounds(r);
        Assert.AreEqual(10, min.X, 1e-9);
        Assert.AreEqual(60, max.Y, 1e-9);
    }

    [TestMethod]
    public void Svg_SkipsDefsClipPathsAndHiddenLayers()
    {
        var r = Svg(
            "<defs><path id=\"p\" d=\"M0 0 L50 50\"/><clipPath id=\"c\"><rect width=\"99\" height=\"99\"/></clipPath></defs>" +
            "<g style=\"display:none\"><rect width=\"80\" height=\"80\"/></g>" +
            "<g display=\"none\"><circle r=\"5\"/></g>" +
            "<rect x=\"1\" y=\"1\" width=\"2\" height=\"2\"/>");
        Assert.AreEqual(1, r.Count, "only the visible rectangle should import");
    }

    [TestMethod]
    public void Svg_UseReferencesAreDrawn()
    {
        var r = Svg("<defs><symbol id=\"s\"><rect width=\"10\" height=\"10\"/></symbol></defs>" +
                    "<use href=\"#s\" x=\"20\" y=\"0\"/><use xlink:href=\"#s\" x=\"50\" y=\"0\"/>");
        Assert.AreEqual(2, r.Count);
        Assert.AreEqual(60, Bounds(r).max.X, 1e-9);
    }

    [TestMethod]
    public void Svg_CompactArcFlags()
    {
        // "a5 5 0 015 5" = rx 5, ry 5, rotation 0, large-arc 0, sweep 1, then (5,5).
        var compact = Svg("<path d=\"M10 10a5 5 0 015 5\"/>");
        var spaced = Svg("<path d=\"M10 10 a5 5 0 0 1 5 5\"/>");
        Assert.AreEqual(1, compact.Count);
        var a = compact[0].Points;
        var b = spaced[0].Points;
        Assert.AreEqual(b.Count, a.Count);
        Assert.AreEqual(b[^1], a[^1]);
        Assert.AreEqual(15, a[^1].X, 1e-9);
        Assert.AreEqual(15, a[^1].Y, 1e-9);
    }

    [TestMethod]
    public void Svg_DrawingAfterClosePathStartsAtSubpathStart()
    {
        var r = Svg("<path d=\"M10 10 L20 10 L20 20 Z L30 30\"/>");
        Assert.AreEqual(2, r.Count);
        Assert.IsTrue(r[0].Closed);
        Assert.AreEqual(new Vec2(10, 10), r[1].Points[0]);
        Assert.AreEqual(new Vec2(30, 30), r[1].Points[^1]);
    }

    [TestMethod, Timeout(5000)]
    public void Svg_MalformedPathsDoNotHang()
    {
        var r = Svg("<path d=\"M0 0 L10 0 Z 5 5 L\"/><path d=\"M 1 2 3\"/><path d=\"Q 1 2\"/><path d=\"xyz M0 0 L 1 1\"/>");
        Assert.IsTrue(r.Count >= 1);
    }

    [TestMethod]
    public void Svg_CurvesAreFlattenedSmoothly()
    {
        var r = Svg("<circle cx=\"50\" cy=\"50\" r=\"40\"/>");
        var pts = r[0].Points;
        Assert.IsTrue(pts.Count >= 64, $"too coarse: {pts.Count} points");
        // Every vertex on the circle; chord midpoints within ~0.03 mm.
        for (int i = 0; i < pts.Count; i++)
        {
            var m = (pts[i] + pts[(i + 1) % pts.Count]) * 0.5;
            Assert.IsTrue(40 - (m - new Vec2(50, 50)).Length < 0.03);
        }
    }

    [TestMethod]
    public void Svg_RoundedRectAndTransforms()
    {
        var r = Svg("<g transform=\"translate(10,10) scale(2)\"><rect width=\"10\" height=\"5\" rx=\"1\"/></g>");
        var (min, max) = Bounds(r);
        Assert.AreEqual(10, min.X, 1e-6);
        Assert.AreEqual(30, max.X, 1e-6);
        Assert.AreEqual(20, max.Y, 1e-6);
        Assert.IsTrue(r[0].Points.Count > 8, "rounded corners expected");
    }

    [TestMethod]
    public void Svg_IllustratorPixelsAre72PerInch()
    {
        var svg = "<?xml version=\"1.0\"?><!-- Generator: Adobe Illustrator 27.0.0, SVG Export Plug-In --><!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">" +
                  "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"72px\" height=\"72px\" viewBox=\"0 0 72 72\"><rect width=\"72\" height=\"72\"/></svg>";
        var r = SvgImporter.ImportText(svg);
        Assert.AreEqual(25.4, Bounds(r).max.X, 1e-6, "72 Illustrator px = 1 inch");
    }

    [TestMethod]
    public void Svg_IllustratorPreserveEditingFilesImport()
    {
        // Inline-DTD entities plus a <switch> whose first child is Illustrator's private data.
        var svg = "<?xml version=\"1.0\"?><!-- Generator: Adobe Illustrator 27.0.0 -->" +
                  "<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\" [" +
                  "<!ENTITY ns_ai \"http://ns.adobe.com/AdobeIllustrator/10.0/\"><!ENTITY ns_extend \"http://ns.adobe.com/Extensibility/1.0/\">]>" +
                  "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:i=\"&ns_ai;\" width=\"72px\" height=\"72px\" viewBox=\"0 0 72 72\">" +
                  "<switch><foreignObject requiredExtensions=\"&ns_ai;\" x=\"0\" y=\"0\" width=\"1\" height=\"1\"/>" +
                  "<g i:extraneous=\"self\"><rect width=\"72\" height=\"36\"/></g></switch></svg>";
        var r = SvgImporter.ImportText(svg);
        Assert.AreEqual(1, r.Count);
        Assert.AreEqual(25.4, Bounds(r).max.X, 1e-6);
    }

    [TestMethod]
    public void Svg_RejectsNonFiniteNumbers()
    {
        var r = Svg("<rect x=\"NaN\" y=\"0\" width=\"10\" height=\"10\"/><path d=\"M0 0 L1e400 0\"/>");
        Assert.IsTrue(r.SelectMany(p => p.Points).All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)));
    }

    [TestMethod]
    public void Dxf_HugeSplinesAndInchCirclesAreSmooth()
    {
        // 300 control points used to crash; an inch-unit circle used to become an octagon.
        var ctrl = string.Join("|", Enumerable.Range(0, 300).Select(i => $"10|{i}|20|{(i % 2) * 5}"));
        var knots = string.Join("|", Enumerable.Range(0, 304).Select(i => $"40|{Math.Clamp(i - 3, 0, 297)}"));
        var spline = $"0|SPLINE|70|8|71|3|72|304|73|300|{knots}|{ctrl}";
        Assert.IsTrue(DxfImporter.ImportText(Dxf(spline))[0].Points.Count > 300);

        var header = string.Join("\n", "0", "SECTION", "2", "HEADER", "9", "$INSUNITS", "70", "1", "0", "ENDSEC") + "\n";
        var circle = DxfImporter.ImportText(header + Dxf("0|CIRCLE|10|0|20|0|40|0.125"))[0].Points; // 1/4" hole
        Assert.IsTrue(circle.Count >= 40, $"only {circle.Count} points");
        for (int i = 0; i < circle.Count; i++)
        {
            var mid = (circle[i] + circle[(i + 1) % circle.Count]) * 0.5;
            Assert.IsTrue(3.175 - mid.Length < 0.02, "chord error must stay within ~0.01 mm");
        }
    }

    [TestMethod]
    public void Svg_PlainPixelsAre96PerInch()
    {
        var r = SvgImporter.ImportText("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"96\" height=\"10\"/></svg>");
        Assert.AreEqual(25.4, Bounds(r).max.X, 1e-6);
    }

    private static string Dxf(params string[] entities)
    {
        var lines = new List<string> { "0", "SECTION", "2", "ENTITIES" };
        foreach (var e in entities) lines.AddRange(e.Split('|'));
        lines.AddRange(new[] { "0", "ENDSEC", "0", "EOF" });
        return string.Join("\n", lines);
    }

    [TestMethod]
    public void Dxf_LinesCirclesAndPolylines()
    {
        var r = DxfImporter.ImportText(Dxf(
            "0|LINE|8|0|10|0|20|0|11|10|21|0",
            "0|CIRCLE|10|50|20|50|40|10",
            "0|LWPOLYLINE|90|4|70|1|10|0|20|0|10|10|20|0|10|10|20|10|10|0|20|10"));
        Assert.AreEqual(3, r.Count);
        Assert.IsTrue(r[1].Closed && r[2].Closed);
        Assert.AreEqual(60, Bounds(r).max.X, 1e-6);
    }

    [TestMethod]
    public void Dxf_InchDrawingsAreScaledToMm()
    {
        var text = string.Join("\n", "0", "SECTION", "2", "HEADER", "9", "$INSUNITS", "70", "1", "0", "ENDSEC") + "\n" +
                   Dxf("0|LINE|10|0|20|0|11|2|21|0");
        var r = DxfImporter.ImportText(text);
        Assert.AreEqual(50.8, Bounds(r).max.X, 1e-9);
    }

    [TestMethod]
    public void Dxf_BulgeMakesAnArc()
    {
        // Semicircle from (0,0) to (10,0), bulge 1 = 180° counter-clockwise (dips below the chord).
        var r = DxfImporter.ImportText(Dxf("0|LWPOLYLINE|90|2|70|0|10|0|20|0|42|1|10|10|20|0"));
        var (min, max) = Bounds(r);
        Assert.AreEqual(-5, min.Y, 0.01);
        Assert.AreEqual(0, max.Y, 0.01);
        Assert.AreEqual(new Vec2(10, 0), r[0].Points[^1]);
    }

    [TestMethod]
    public void Dxf_SplineIsEvaluated_NotJustItsControlPolygon()
    {
        // Clamped cubic Bézier as a NURBS: control points (0,0) (0,10) (10,10) (10,0).
        var spline = "0|SPLINE|70|8|71|3|72|8|73|4|40|0|40|0|40|0|40|0|40|1|40|1|40|1|40|1|" +
                     "10|0|20|0|10|0|20|10|10|10|20|10|10|10|20|0";
        var r = DxfImporter.ImportText(Dxf(spline));
        var pts = r[0].Points;
        Assert.IsTrue(pts.Count > 16);
        Assert.AreEqual(new Vec2(0, 0), pts[0]);
        Assert.AreEqual(10, pts[^1].X, 1e-9);
        // Bézier midpoint is (5, 7.5) — the control polygon would peak at 10.
        Assert.AreEqual(7.5, pts.Max(p => p.Y), 0.01);
    }

    [TestMethod]
    public void Dxf_EllipseAndBlockInserts()
    {
        var text = string.Join("\n",
            "0", "SECTION", "2", "BLOCKS",
            "0", "BLOCK", "2", "SQ", "70", "0", "10", "0", "20", "0",
            "0", "LWPOLYLINE", "90", "4", "70", "1", "10", "0", "20", "0", "10", "1", "20", "0", "10", "1", "20", "1", "10", "0", "20", "1",
            "0", "ENDBLK",
            "0", "ENDSEC") + "\n" +
            Dxf("0|INSERT|2|SQ|10|100|20|0|41|5|42|5",
                "0|ELLIPSE|10|0|20|0|11|20|21|0|40|0.5|41|0|42|6.283185307179586");
        var r = DxfImporter.ImportText(text);
        Assert.AreEqual(2, r.Count);
        var square = r.First(p => p.Points.Count == 4);
        Assert.AreEqual(105, Bounds(new[] { square }).max.X, 1e-9);
        var ellipse = r.First(p => p.Points.Count > 4);
        var (emin, emax) = Bounds(new[] { ellipse });
        Assert.AreEqual(-20, emin.X, 0.01);
        Assert.AreEqual(10, emax.Y, 0.01);
    }

    [TestMethod]
    public void Dxf_PaperSpaceIsIgnored()
    {
        var r = DxfImporter.ImportText(Dxf("0|LINE|67|1|10|0|20|0|11|5|21|5", "0|LINE|10|0|20|0|11|1|21|1"));
        Assert.AreEqual(1, r.Count);
    }

    [TestMethod]
    public void ImageTracer_FindsOuterAndInnerContours()
    {
        // 6×6 black ring with a 2×2 hole.
        int w = 8, h = 8;
        var g = Enumerable.Repeat((byte)255, w * h).ToArray();
        for (int y = 1; y < 7; y++)
            for (int x = 1; x < 7; x++)
                g[y * w + x] = (x is 3 or 4 && y is 3 or 4) ? (byte)255 : (byte)0;
        var contours = ImageTracer.Trace(g, w, h);
        Assert.AreEqual(2, contours.Count);
        Assert.IsTrue(contours.All(c => c.Closed));
        var (min, max) = Bounds(contours);
        Assert.AreEqual(new Vec2(1, 1), min);
        Assert.AreEqual(new Vec2(7, 7), max);
    }
}
