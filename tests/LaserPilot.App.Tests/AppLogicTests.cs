using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LaserPilot.App.Models;
using LaserPilot.Core.Gcode;
using LaserPilot.Core.Geometry;
using LaserPilot.Core.Machines;

namespace LaserPilot.App.Tests;

[TestClass]
public sealed class AppLogicTests
{
    private static readonly MachineProfile B10 = MachineProfile.OmtechB10;
    private static readonly MotionLimits Limits = new(6000, 6000, 1000, 1000);

    private static CompiledJob Compile(IReadOnlyList<DesignItem> items, JobOptions? options = null, Vec2? head = null, bool homed = true) =>
        JobCompiler.Compile(items, B10, options ?? new JobOptions(), 1000, head, homed, Limits);

    private static DesignItem Rect(double x, double y, double w, double h, VectorMode mode = VectorMode.Line)
    {
        var r = ShapeFactory.Rect(w, h);
        r.X = x;
        r.Y = y;
        r.Mode = mode;
        return r;
    }

    private static DesignItem Circle(double cx, double cy, double d)
    {
        var e = ShapeFactory.Ellipse(d, d);
        e.X = cx - d / 2;
        e.Y = cy - d / 2;
        return e;
    }

    [TestMethod]
    public void EngravingRunsBeforeCutting()
    {
        var cut = Rect(10, 10, 50, 30);
        var fill = Rect(100, 100, 20, 20, VectorMode.Fill);
        var g = Compile(new[] { cut, fill }).Gcode;
        int fillAt = g.FindIndex(l => l.StartsWith("; fill", StringComparison.Ordinal));
        int cutAt = g.FindIndex(l => l == "; --- line / cut paths ---");
        Assert.IsTrue(fillAt >= 0 && cutAt >= 0);
        Assert.IsTrue(fillAt < cutAt, "fills must run before cuts");

        var listOrder = Compile(new[] { cut, fill }, new JobOptions { EngraveBeforeCut = false }).Gcode;
        Assert.IsTrue(listOrder.FindIndex(l => l == "; --- line / cut paths ---") < listOrder.FindIndex(l => l.StartsWith("; fill", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void HolesAreCutBeforeTheOutlineAroundThem_EvenAcrossItems()
    {
        var outline = Rect(50, 50, 100, 100);   // added first
        var hole = Circle(100, 100, 20);        // added second, sits inside
        var g = Compile(new[] { outline, hole }).Gcode;
        var rapids = g.Where(l => l.StartsWith("G0 X", StringComparison.Ordinal)).ToList();
        // The first cut starts on the circle (x within 90..110), not on the rectangle.
        var first = ToolpathParser.Parse(new[] { rapids[0] }).Moves[0];
        Assert.IsTrue(first.X1 is > 89 and < 111 && first.Y1 is > 89 and < 111, $"first cut started at {first.X1},{first.Y1}");
    }

    [TestMethod]
    public void DesignsOffTheBedAreRejected()
    {
        var item = Rect(380, 10, 50, 30);
        var ex = Assert.ThrowsException<JobException>(() => Compile(new[] { item }));
        StringAssert.Contains(ex.Message, "outside");
    }

    [TestMethod]
    public void DisabledItemsAreNotBurned()
    {
        var on = Rect(10, 10, 20, 20);
        var off = Rect(200, 200, 20, 20);
        off.Enabled = false;
        var job = Compile(new[] { on, off });
        Assert.AreEqual(1, job.Items.Count);
        Assert.IsTrue(job.Estimate.BurnExtents.MaxX < 40);

        Assert.ThrowsException<JobException>(() => Compile(new[] { off }));
    }

    [TestMethod]
    public void CurrentPositionMode_PutsTheAnchorUnderTheLaser()
    {
        var item = Rect(100, 100, 50, 30);
        var job = Compile(new[] { item }, new JobOptions { Origin = JobOrigin.CurrentPosition, Anchor = JobAnchor.BottomLeft },
            head: new Vec2(20, 30));
        var e = job.Estimate.BurnExtents;
        Assert.AreEqual(20, e.MinX, 1e-6);
        Assert.AreEqual(30, e.MinY, 1e-6);
        Assert.AreEqual(70, e.MaxX, 1e-6);

        var centered = Compile(new[] { item }, new JobOptions { Origin = JobOrigin.CurrentPosition, Anchor = JobAnchor.Center },
            head: new Vec2(200, 200));
        Assert.AreEqual(175, centered.Estimate.BurnExtents.MinX, 1e-6);
        Assert.AreEqual(185, centered.Estimate.BurnExtents.MinY, 1e-6);

        // Without a known head position the job can't be placed.
        Assert.ThrowsException<JobException>(() => Compile(new[] { item }, new JobOptions { Origin = JobOrigin.CurrentPosition }));
        // Off the bed from the current position (homed): rejected.
        Assert.ThrowsException<JobException>(() => Compile(new[] { item },
            new JobOptions { Origin = JobOrigin.CurrentPosition, Anchor = JobAnchor.BottomLeft }, head: new Vec2(380, 380)));
    }

    [TestMethod]
    public void RasterAtTheBedEdge_NeverTravelsOffTheBed()
    {
        var gray = Enumerable.Repeat((byte)0, 50 * 20).ToArray();
        var img = DesignItem.FromGray("edge", gray, 50, 20);
        img.X = 0;
        img.Y = 0;
        img.W = 50;
        img.H = 20;
        img.Raster.Dither = DitherMode.Threshold;
        img.Raster.LineIntervalMm = 0.5;
        var job = Compile(new[] { img });
        Assert.IsTrue(job.Estimate.Extents.MinX >= -1e-9, $"min X {job.Estimate.Extents.MinX}");
        Assert.IsTrue(job.Estimate.Extents.MinY >= -1e-9);
    }

    [TestMethod]
    public void AirAssistIsSwitchedOnAndOff()
    {
        var g = Compile(new[] { Rect(10, 10, 10, 10) }, new JobOptions { AirAssist = true }).Gcode;
        int on = g.IndexOf("M8"), off = g.IndexOf("M9");
        Assert.IsTrue(on >= 0 && off > on);
        Assert.AreEqual("M5", g[g.IndexOf("M9") - 1], "laser off before air off");
    }

    [TestMethod]
    public void TransparentPngBackgroundIsNotBurned()
    {
        // 4×1 Bgra32: transparent black, opaque black, opaque white, half-transparent black.
        var pixels = new byte[]
        {
            0, 0, 0, 0,
            0, 0, 0, 255,
            255, 255, 255, 255,
            0, 0, 0, 128,
        };
        var bmp = BitmapSource.Create(4, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        var path = Path.Combine(Path.GetTempPath(), $"lp-alpha-{Guid.NewGuid():N}.png");
        try
        {
            using (var fs = File.Create(path))
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                enc.Save(fs);
            }
            var item = DesignItem.FromImageFile(path);
            Assert.IsNotNull(item.Gray);
            Assert.AreEqual(255, item.Gray[0], "fully transparent must read as white (unburned)");
            Assert.AreEqual(0, item.Gray[1]);
            Assert.AreEqual(255, item.Gray[2]);
            Assert.AreEqual(127, item.Gray[3], 2, "half-transparent black is mid gray");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ProjectsRoundTrip()
    {
        var text = ShapeFactory.Text("Hi", "Arial", false, false, 12);
        text.X = 30; text.Y = 40; text.RotationDeg = 15;
        text.Cut.SpeedMmMin = 2500; text.Cut.PowerPercent = 33; text.Cut.FillIntervalMm = 0.08;
        text.PresetName = "Wood engrave (light)";
        var gray = Enumerable.Range(0, 30 * 20).Select(i => (byte)(i % 256)).ToArray();
        var img = DesignItem.FromGray("photo.png", gray, 30, 20);
        img.X = 200; img.Y = 210; img.Enabled = false;
        img.Raster.Dither = DitherMode.Stucki; img.Raster.Gamma = 1.4; img.Raster.Invert = true;

        var path = Path.Combine(Path.GetTempPath(), $"lp-project-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(path, new[] { text, img }, B10.Id, new JobOptions { AirAssist = true, Origin = JobOrigin.CurrentPosition, Anchor = JobAnchor.Center });
            var doc = ProjectFile.Load(path, out var items);

            Assert.AreEqual(B10.Id, doc.ProfileId);
            Assert.IsTrue(doc.Job.AirAssist);
            Assert.AreEqual(JobOrigin.CurrentPosition, doc.Job.Origin);
            Assert.AreEqual(JobAnchor.Center, doc.Job.Anchor);
            Assert.AreEqual(2, items.Count);

            var t = items[0];
            Assert.AreEqual(VectorMode.Fill, t.Mode);
            Assert.AreEqual(2500, t.Cut.SpeedMmMin);
            Assert.AreEqual(0.08, t.Cut.FillIntervalMm);
            Assert.AreEqual("Wood engrave (light)", t.PresetName);
            Assert.AreEqual(15, t.RotationDeg);
            var before = text.TransformedShapes().SelectMany(p => p.Points).ToList();
            var after = t.TransformedShapes().SelectMany(p => p.Points).ToList();
            Assert.AreEqual(before.Count, after.Count);
            for (int i = 0; i < before.Count; i++) Assert.AreEqual(0, (before[i] - after[i]).Length, 1e-3);

            var r = items[1];
            Assert.IsTrue(r.IsRaster);
            Assert.IsFalse(r.Enabled);
            CollectionAssert.AreEqual(gray, r.Gray);
            Assert.AreEqual(DitherMode.Stucki, r.Raster.Dither);
            Assert.AreEqual(1.4, r.Raster.Gamma);
            Assert.IsTrue(r.Raster.Invert);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void MaterialTestGrid()
    {
        var o = new MaterialTestOptions { Rows = 3, Columns = 4, SpeedMin = 1000, SpeedMax = 3000, PowerMin = 10, PowerMax = 40, Labels = true };
        var items = MaterialTest.Generate(o);
        var boxes = items.Where(i => i.Name.StartsWith("Test ", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(12, boxes.Count);
        Assert.AreEqual(3 + 4 + 1, items.Count - boxes.Count, "row labels + column labels + caption");
        CollectionAssert.AreEquivalent(new double[] { 1000, 2000, 3000 }, boxes.Select(b => b.Cut.SpeedMmMin).Distinct().ToList());
        CollectionAssert.AreEquivalent(new double[] { 10, 20, 30, 40 }, boxes.Select(b => b.Cut.PowerPercent).Distinct().ToList());
        Assert.IsTrue(boxes.All(b => b.Mode == VectorMode.Fill));
        // The whole card fits within its advertised size.
        var b = JobCompiler.DesignBounds(items)!.Value;
        var (w, h) = MaterialTest.Size(o);
        Assert.IsTrue(b.W <= w + 0.5 && b.H <= h + 0.5, $"{b.W}×{b.H} vs {w}×{h}");
        // And it compiles into a valid job.
        Assert.IsTrue(Compile(items).Gcode.Count > 100);
    }

    [TestMethod]
    public void SvgImportKeepsTheDrawingUpright()
    {
        // A small square at the TOP of the drawing must end up at the top (rear) of the item.
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100mm\" height=\"100mm\" viewBox=\"0 0 100 100\">" +
                  "<rect x=\"0\" y=\"0\" width=\"100\" height=\"100\"/><rect x=\"40\" y=\"5\" width=\"20\" height=\"10\"/></svg>";
        var path = Path.Combine(Path.GetTempPath(), $"lp-{Guid.NewGuid():N}.svg");
        try
        {
            File.WriteAllText(path, svg);
            var item = DesignItem.FromFile(path);
            item.X = 0; item.Y = 0;
            var small = item.TransformedShapes().OrderBy(p => p.Bounds().max.X - p.Bounds().min.X).First();
            Assert.AreEqual(95, small.Bounds().max.Y, 1e-6);
            Assert.AreEqual(85, small.Bounds().min.Y, 1e-6);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void UndoSnapshotsAreIndependent()
    {
        var a = Rect(10, 10, 20, 20);
        var stack = new UndoStack();
        var snapshot = new DesignSnapshot(new List<DesignItem> { a.Clone() }, new List<int> { 0 }, 0);
        stack.Push(snapshot);
        a.X = 99;
        a.Cut.PowerPercent = 5;
        var restored = stack.Undo(new DesignSnapshot(new List<DesignItem> { a.Clone() }, new List<int>(), -1))!;
        Assert.AreEqual(10, restored.Items[0].X);
        Assert.AreEqual(80, restored.Items[0].Cut.PowerPercent, "the snapshot must not share settings objects");
        Assert.IsTrue(stack.CanRedo);
    }
}
