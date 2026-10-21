using LaserPilot.Core.Gcode;
using LaserPilot.Core.Geometry;

namespace LaserPilot.Core.Tests;

[TestClass]
public sealed class GcodeTests
{
    private static List<ToolpathMove> Burns(IEnumerable<string> g) =>
        ToolpathParser.Parse(g).Moves.Where(m => !m.Rapid && m.Power > 0).ToList();

    private static Polyline Square(double x, double y, double size) =>
        new(new[] { new Vec2(x, y), new Vec2(x + size, y), new Vec2(x + size, y + size), new Vec2(x, y + size) }, closed: true);

    [TestMethod]
    public void Raster_DoesNotBurnTheGapsBetweenDots()
    {
        // One row: dark 0–2 mm, white 2–6 mm, dark 6–8 mm, white 8–10 mm.
        var gray = new byte[] { 0, 0, 255, 255, 255, 255, 0, 0, 255, 255 };
        var s = new RasterSettings { LineIntervalMm = 1, Dither = DitherMode.Threshold, OverscanMm = 2 };
        foreach (var bidirectional in new[] { true, false })
        {
            s.BidirectionalScan = bidirectional;
            var burns = Burns(GcodeGenerator.Raster(gray, 10, 1, 0, 0, 10, 1, s, 1000));
            Assert.AreEqual(4.0, burns.Sum(m => Math.Abs(m.X1 - m.X0)), 1e-6, "only the 4 mm of dark pixels may burn");
            foreach (var m in burns)
            {
                double lo = Math.Min(m.X0, m.X1), hi = Math.Max(m.X0, m.X1);
                Assert.IsTrue(hi <= 2 + 1e-6 || lo >= 6 - 1e-6, $"burn {lo}..{hi} crosses the white gap");
                Assert.IsTrue(hi <= 8 + 1e-6, "burned past the image");
            }
        }
    }

    [TestMethod]
    public void Raster_RightToLeftRows_AlsoSkipGaps()
    {
        // Two identical rows: the second runs right-to-left when bidirectional.
        var row = new byte[] { 0, 255, 255, 0, 255, 0 };
        var gray = row.Concat(row).ToArray();
        var s = new RasterSettings { LineIntervalMm = 1, Dither = DitherMode.Threshold, BidirectionalScan = true };
        var burns = Burns(GcodeGenerator.Raster(gray, 6, 2, 0, 0, 6, 2, s, 1000));
        Assert.AreEqual(6.0, burns.Sum(m => Math.Abs(m.X1 - m.X0)), 1e-6); // 3 dark pixels × 2 rows
        Assert.IsTrue(burns.Any(m => m.X1 < m.X0), "expected a right-to-left row");
    }

    [TestMethod]
    public void Raster_ImageIsUpright_TopRowAtHighestY()
    {
        var gray = new byte[] { 0, 255 }; // 1 px wide, 2 rows: top black, bottom white
        var s = new RasterSettings { LineIntervalMm = 1, Dither = DitherMode.Threshold };
        var burns = Burns(GcodeGenerator.Raster(gray, 1, 2, 5, 10, 1, 2, s, 1000));
        Assert.IsTrue(burns.Count > 0);
        Assert.IsTrue(burns.All(m => Math.Abs(m.Y0 - 11.5) < 1e-6), "the top image row must be burned at the top (rear) of the image area");
    }

    [TestMethod]
    public void Raster_OverscanIsClampedToTheBed()
    {
        var gray = Enumerable.Repeat((byte)0, 20).ToArray();
        var s = new RasterSettings { LineIntervalMm = 1, Dither = DitherMode.Threshold, OverscanMm = 5 };
        var moves = ToolpathParser.Parse(GcodeGenerator.Raster(gray, 10, 2, 0, 0, 10, 2, s, 1000, minX: 0, maxX: 12)).Moves;
        Assert.IsTrue(moves.All(m => Math.Min(m.X0, m.X1) >= -1e-9 && Math.Max(m.X0, m.X1) <= 12 + 1e-9),
            "overscan travelled outside the bed (would hit soft limits or the rails)");
    }

    [TestMethod]
    public void Raster_GrayscaleUsesMinAndMaxPower()
    {
        var gray = new byte[] { 0, 128, 249, 255 };
        var s = new RasterSettings { LineIntervalMm = 1, Dither = DitherMode.Grayscale, MaxPowerPercent = 80, MinPowerPercent = 20 };
        var burns = Burns(GcodeGenerator.Raster(gray, 4, 1, 0, 0, 4, 1, s, 1000));
        Assert.AreEqual(800, burns.Max(b => b.Power), 1);
        Assert.IsTrue(burns.Min(b => b.Power) >= 200, "lightest tone must use at least the minimum power");
        Assert.AreEqual(3.0, burns.Sum(m => Math.Abs(m.X1 - m.X0)), 1e-6, "pure white is never burned");
    }

    [TestMethod]
    public void Fill_EvenOddLeavesHolesEmpty()
    {
        var rows = FillGenerator.ScanRows(new[] { Square(0, 0, 10), Square(3, 3, 4) }, 1);
        var middle = rows.First(r => Math.Abs(r.Y - 5.5) < 1e-9);
        Assert.AreEqual(2, middle.Segments.Count);
        Assert.AreEqual(0, middle.Segments[0].A, 1e-9);
        Assert.AreEqual(3, middle.Segments[0].B, 1e-9);
        Assert.AreEqual(7, middle.Segments[1].A, 1e-9);
        Assert.AreEqual(10, middle.Segments[1].B, 1e-9);
        var bottom = rows.First();
        Assert.AreEqual(1, bottom.Segments.Count);
    }

    [TestMethod]
    public void Fill_BurnsAreaDividedByInterval()
    {
        var s = new CutSettings { FillIntervalMm = 0.5, PowerPercent = 50, SpeedMmMin = 3000 };
        var burns = Burns(GcodeGenerator.Fill(new[] { Square(10, 10, 20) }, s, 1000, 0, 400));
        double burned = burns.Sum(m => Math.Abs(m.X1 - m.X0));
        Assert.AreEqual(20 * 20 / 0.5, burned, 1e-6);
        Assert.IsTrue(burns.All(b => Math.Abs(b.Power - 500) < 1e-9));
    }

    [TestMethod]
    public void Fill_IgnoresOpenPaths()
    {
        var line = new Polyline(new[] { new Vec2(0, 0), new Vec2(10, 10) });
        Assert.AreEqual(0, FillGenerator.ScanRows(new[] { line }, 0.1).Count);
    }

    [TestMethod]
    public void VectorJob_RunsPassesInRounds()
    {
        var a = Square(0, 0, 5);
        var b = Square(10, 0, 5);
        var g = GcodeGenerator.VectorJob(new List<(Polyline, CutSettings)>
        {
            (a, new CutSettings { Passes = 2, PowerPercent = 100 }),
            (b, new CutSettings { Passes = 1, PowerPercent = 50 }),
        }, 1000);
        var starts = g.Where(l => l.StartsWith("G0")).ToList();
        // a needs two rounds, b one: b runs in the final round only.
        CollectionAssert.AreEqual(new[] { "G0 X0 Y0", "G0 X0 Y0", "G0 X10 Y0" }, starts);
        Assert.AreEqual(2, g.Count(l => l == "M4 S1000"));
        Assert.AreEqual(1, g.Count(l => l == "M4 S500"));
        // Closed shapes return to their start point.
        Assert.AreEqual(4 * 5 * 3, Burns(g).Sum(m => Math.Sqrt(Math.Pow(m.X1 - m.X0, 2) + Math.Pow(m.Y1 - m.Y0, 2))), 1e-6);
    }

    [TestMethod]
    public void VectorJob_HoleWithMorePassesStillFinishesBeforeItsOutline()
    {
        var hole = Square(40, 40, 20);
        var outline = Square(0, 0, 100);
        var g = GcodeGenerator.VectorJob(new List<(Polyline, CutSettings)>
        {
            (hole, new CutSettings { Passes = 3 }),
            (outline, new CutSettings { Passes = 1 }),
        }, 1000);
        var starts = g.Where(l => l.StartsWith("G0")).ToList();
        CollectionAssert.AreEqual(new[] { "G0 X40 Y40", "G0 X40 Y40", "G0 X40 Y40", "G0 X0 Y0" }, starts,
            "the outline (which frees the part) must be the very last cut");
    }

    [TestMethod]
    public void ScanLineTravelMovesCarryNoPower()
    {
        // A row ending at the clamped bed edge: the next row's travel must not inherit the burn power.
        var gray = Enumerable.Repeat((byte)0, 20).ToArray();
        var s = new RasterSettings { LineIntervalMm = 1, Dither = DitherMode.Threshold, OverscanMm = 2 };
        var g = GcodeGenerator.Raster(gray, 10, 2, 390, 0, 10, 2, s, 1000, 0, 400);
        foreach (var travel in g.Where(l => l.StartsWith("G0")))
            StringAssert.EndsWith(travel, " S0");
    }

    [TestMethod]
    public void PathOptimizer_CutsInnerShapesFirst()
    {
        var outer = Square(0, 0, 100);
        var hole = Square(40, 40, 20);
        var other = Square(200, 0, 10);
        var order = PathOptimizer.Order(new[] { outer, other, hole }, new Vec2(0, 0));
        int iOuter = order.FindIndex(p => p.Bounds().max.X == 100);
        int iHole = order.FindIndex(p => p.Bounds().max.X == 60);
        Assert.IsTrue(iHole < iOuter, "the hole must be cut before the outline around it");
        Assert.AreEqual(3, order.Count);
    }

    [TestMethod]
    public void PathOptimizer_StartsClosedPathsNearTheHead()
    {
        var sq = Square(0, 0, 10);
        var order = PathOptimizer.Order(new[] { sq }, new Vec2(11, 11));
        Assert.AreEqual(new Vec2(10, 10), order[0].Points[0]);
        Assert.AreEqual(4, order[0].Points.Count);
    }

    [TestMethod]
    public void RasterProcessor_DithersToPureBlackAndWhite()
    {
        var gray = Enumerable.Range(0, 64 * 64).Select(i => (byte)(i % 256)).ToArray();
        foreach (var mode in new[] { DitherMode.FloydSteinberg, DitherMode.Jarvis, DitherMode.Stucki, DitherMode.Atkinson, DitherMode.Ordered, DitherMode.Threshold })
        {
            var (outGray, w, h) = RasterProcessor.Prepare(gray, 64, 64, 6.4, 6.4, new RasterSettings { Dither = mode, LineIntervalMm = 0.1 });
            Assert.AreEqual(64, w);
            Assert.AreEqual(64, h);
            Assert.IsTrue(outGray.All(v => v is 0 or 255), $"{mode} produced gray levels");
        }
    }

    [TestMethod]
    public void RasterProcessor_DitherKeepsAverageTone()
    {
        var gray = Enumerable.Repeat((byte)64, 100 * 100).ToArray(); // 75 % dark
        var (outGray, _, _) = RasterProcessor.Prepare(gray, 100, 100, 10, 10, new RasterSettings { Dither = DitherMode.FloydSteinberg, LineIntervalMm = 0.1 });
        double dark = outGray.Count(v => v == 0) / (double)outGray.Length;
        Assert.AreEqual(0.75, dark, 0.03);
    }

    [TestMethod]
    public void RasterProcessor_ResamplesToTheLineInterval()
    {
        var gray = new byte[1000 * 500];
        var (_, w, h) = RasterProcessor.Prepare(gray, 1000, 500, 50, 25, new RasterSettings { Dither = DitherMode.Grayscale, LineIntervalMm = 0.25 });
        Assert.AreEqual(200, w);
        Assert.AreEqual(100, h);
    }

    [TestMethod]
    public void RasterProcessor_AdjustmentsAndInvert()
    {
        var gray = new byte[] { 0, 128, 255, 255 };
        var inverted = RasterProcessor.Prepare(gray, 4, 1, 4, 1, new RasterSettings { Dither = DitherMode.Grayscale, LineIntervalMm = 1, Invert = true }).gray;
        CollectionAssert.AreEqual(new byte[] { 255, 127, 0, 0 }, inverted);
        var brighter = RasterProcessor.Prepare(gray, 4, 1, 4, 1, new RasterSettings { Dither = DitherMode.Grayscale, LineIntervalMm = 1, Brightness = 50 }).gray;
        Assert.IsTrue(brighter[1] > 128 && brighter[0] > 0);
    }

    [TestMethod]
    public void RasterProcessor_RefusesAbsurdResolutions()
    {
        Assert.ThrowsException<InvalidOperationException>(() =>
            RasterProcessor.Prepare(new byte[4], 2, 2, 400, 400, new RasterSettings { LineIntervalMm = 0.02 }));
    }

    [TestMethod]
    public void Estimator_MatchesTrapezoidMath()
    {
        // 100 mm at 6000 mm/min (100 mm/s), 1000 mm/s²: 0.1 s up + 0.1 s down + 90 mm cruise.
        var limits = new MotionLimits(MaxRateXMmMin: 10000, MaxRateYMmMin: 10000, AccelXMmS2: 1000, AccelYMmS2: 1000);
        var est = JobEstimator.Estimate(new[] { "M4 S100", "G1 X100 F6000", "M5" }, limits);
        Assert.AreEqual(1.1, est.Total.TotalSeconds, 0.01);
        Assert.AreEqual(100, est.BurnDistanceMm, 1e-9);
        Assert.AreEqual(3, est.LineCount);
        Assert.AreEqual(1.1, est.RemainingAfter(1).TotalSeconds, 0.01);
        Assert.AreEqual(0, est.RemainingAfter(3).TotalSeconds, 1e-9);
    }

    [TestMethod]
    public void Estimator_StraightContinuationDoesNotStop()
    {
        var limits = new MotionLimits(10000, 10000, 1000, 1000);
        var one = JobEstimator.Estimate(new[] { "G1 X100 F6000" }, limits).Total.TotalSeconds;
        var split = JobEstimator.Estimate(Enumerable.Range(1, 100).Select(i => $"G1 X{i} F6000"), limits).Total.TotalSeconds;
        Assert.AreEqual(one, split, 0.01, "collinear segments must keep their speed through the junctions");
        // A 90° corner forces a slowdown.
        var corner = JobEstimator.Estimate(new[] { "G1 X50 F6000", "G1 Y50" }, limits).Total.TotalSeconds;
        Assert.IsTrue(corner > one, "a sharp corner must cost time");
    }

    [TestMethod]
    public void Estimator_HandlesRapidsArcsAndDwell()
    {
        var limits = new MotionLimits(6000, 6000, 1000, 1000);
        var est = JobEstimator.Estimate(new[] { "G0 X10", "G4 P2", "M4 S100", "G2 X20 Y0 I5 J0 F600" }, limits);
        Assert.IsTrue(est.Total.TotalSeconds > 2 + Math.PI * 5 / 10, "dwell + half circle at 10 mm/s");
        Assert.AreEqual(10, est.TravelDistanceMm, 1e-6);
        Assert.AreEqual(Math.PI * 5, est.BurnDistanceMm, 0.05);
        Assert.AreEqual(5, est.BurnExtents.MaxY, 0.01, "a clockwise arc from the left goes over the top");
    }

    [TestMethod]
    public void Parser_TracksModesAndPower()
    {
        var p = ToolpathParser.Parse(new[] { "G21 G91", "M4 S300", "G1 X5 Y5 F1000", "X5", "M5", "G90 G0 X0 Y0", "G20", "G1 X1 F10" });
        var moves = p.Moves;
        Assert.AreEqual(4, moves.Count);
        Assert.AreEqual((5.0, 5.0), (moves[0].X1, moves[0].Y1));
        Assert.AreEqual(300, moves[0].Power);
        Assert.AreEqual(10, moves[1].X1, 1e-9);
        Assert.IsTrue(moves[2].Rapid);
        Assert.AreEqual(25.4, moves[3].X1, 1e-9);
        Assert.AreEqual(0, moves[3].Power, "laser is off after M5");
        Assert.AreEqual(8, p.LineCount);
    }
}
