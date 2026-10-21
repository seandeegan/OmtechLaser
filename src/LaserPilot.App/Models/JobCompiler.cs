using LaserPilot.Core.Gcode;
using LaserPilot.Core.Geometry;
using LaserPilot.Core.Machines;

namespace LaserPilot.App.Models;

public enum JobOrigin { Absolute, CurrentPosition }

public enum JobAnchor { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }

public sealed class JobOptions
{
    /// <summary>Engrave (fill/image) everything before cutting, so cut parts can't shift mid-engrave.</summary>
    public bool EngraveBeforeCut { get; set; } = true;
    /// <summary>Switch the air assist output on for the job (M8 … M9).</summary>
    public bool AirAssist { get; set; }
    public JobOrigin Origin { get; set; } = JobOrigin.Absolute;
    /// <summary>Which point of the design lands under the laser in <see cref="JobOrigin.CurrentPosition"/> mode.</summary>
    public JobAnchor Anchor { get; set; } = JobAnchor.BottomLeft;
}

public sealed class JobException(string message) : Exception(message);

public sealed class CompiledJob
{
    public required List<string> Gcode { get; init; }
    public required JobEstimate Estimate { get; init; }
    /// <summary>Design bounds where the job will actually run (work coordinates).</summary>
    public required (double X, double Y, double W, double H) Bounds { get; init; }
    public required Vec2 Offset { get; init; }
    public required IReadOnlyList<DesignItem> Items { get; init; }
    /// <summary>Where the head was when the job was placed relative to it (current-position mode only).</summary>
    public Vec2? PlacedAtHead { get; init; }
    public List<string> Warnings { get; } = new();
}

/// <summary>Turns the design into one G-code program.</summary>
public static class JobCompiler
{
    private const double Tolerance = 0.05;

    public static (double X, double Y, double W, double H)? DesignBounds(IEnumerable<DesignItem> items)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var item in items)
        {
            var (bx, by, bw, bh) = item.WorkBounds();
            minX = Math.Min(minX, bx); minY = Math.Min(minY, by);
            maxX = Math.Max(maxX, bx + bw); maxY = Math.Max(maxY, by + bh);
        }
        return minX > maxX ? null : (minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>The anchor point of a box; "top" is the rear of the bed (higher Y).</summary>
    public static Vec2 AnchorPoint((double X, double Y, double W, double H) b, JobAnchor a)
    {
        double x = a switch
        {
            JobAnchor.TopLeft or JobAnchor.Left or JobAnchor.BottomLeft => b.X,
            JobAnchor.TopRight or JobAnchor.Right or JobAnchor.BottomRight => b.X + b.W,
            _ => b.X + b.W / 2,
        };
        double y = a switch
        {
            JobAnchor.BottomLeft or JobAnchor.Bottom or JobAnchor.BottomRight => b.Y,
            JobAnchor.TopLeft or JobAnchor.Top or JobAnchor.TopRight => b.Y + b.H,
            _ => b.Y + b.H / 2,
        };
        return new Vec2(x, y);
    }

    /// <param name="headPosition">Current laser position (work coordinates), if connected.</param>
    /// <param name="positionKnown">True once homed: positions can be checked against the bed.</param>
    public static CompiledJob Compile(IReadOnlyList<DesignItem> allItems, MachineProfile profile, JobOptions options,
        double maxPowerS, Vec2? headPosition, bool positionKnown, MotionLimits limits)
    {
        var items = allItems.Where(i => i.Enabled).ToList();
        if (items.Count == 0)
            throw new JobException(allItems.Count == 0
                ? "Nothing to run — import a design or add a shape first."
                : "Every design item is switched off. Tick the box next to at least one item in the Design list.");

        foreach (var item in items)
        {
            var (bx, by, bw, bh) = item.WorkBounds();
            if (!double.IsFinite(bx) || !double.IsFinite(by) || !double.IsFinite(bw) || !double.IsFinite(bh))
                throw new JobException($"'{item.Name}' contains invalid coordinates. Delete it and import the file again.");
        }
        var bounds = DesignBounds(items)!.Value;
        var offset = new Vec2(0, 0);
        if (options.Origin == JobOrigin.CurrentPosition)
        {
            if (headPosition is null)
                throw new JobException("\"Start from: current laser position\" needs the machine connected so LaserPilot knows where the laser is.");
            offset = headPosition.Value - AnchorPoint(bounds, options.Anchor);
        }
        var placed = (X: bounds.X + offset.X, Y: bounds.Y + offset.Y, bounds.W, bounds.H);

        // Positions can be trusted in absolute mode (the canvas is the bed) or once homed.
        bool checkBed = options.Origin == JobOrigin.Absolute || positionKnown;
        if (checkBed)
        {
            foreach (var item in items)
            {
                var (bx, by, bw, bh) = item.WorkBounds();
                bx += offset.X; by += offset.Y;
                if (bx < -Tolerance || by < -Tolerance || bx + bw > profile.WidthMm + Tolerance || by + bh > profile.HeightMm + Tolerance)
                    throw new JobException(options.Origin == JobOrigin.Absolute
                        ? $"'{item.Name}' extends outside the {profile.WidthMm:0}×{profile.HeightMm:0} mm work area. Move or shrink it so it sits fully inside the grid."
                        : $"Starting at the laser's current position, '{item.Name}' would run off the {profile.WidthMm:0}×{profile.HeightMm:0} mm bed. Move the laser or pick a different anchor point.");
            }
        }

        var warnings = new List<string>();
        foreach (var item in items.Where(i => !i.IsRaster && i.Mode != VectorMode.Line && !i.HasClosedShapes))
            warnings.Add($"'{item.Name}' has no closed shapes to fill, so it will be traced as lines instead.");

        double minX = checkBed ? 0 : double.NegativeInfinity;
        double maxX = checkBed ? profile.WidthMm : double.PositiveInfinity;
        var start = headPosition ?? new Vec2(0, 0);

        var g = GcodeGenerator.Preamble(options.AirAssist);
        var output = new List<DesignItem>();

        bool Engraves(DesignItem i) => i.IsRaster || (i.Mode != VectorMode.Line && i.HasClosedShapes);
        bool Traces(DesignItem i) => !i.IsRaster && (i.Mode != VectorMode.Fill || !i.HasClosedShapes);

        if (options.EngraveBeforeCut)
        {
            foreach (var item in items.Where(Engraves)) { EmitEngrave(item); output.Add(item); }
            var lineItems = items.Where(Traces).ToList();
            EmitLines(lineItems);
            output.AddRange(lineItems.Where(i => !output.Contains(i)));
        }
        else
        {
            foreach (var item in items)
            {
                if (Engraves(item)) EmitEngrave(item);
                if (Traces(item)) EmitLines(new[] { item });
                output.Add(item);
            }
        }

        g.AddRange(GcodeGenerator.Postamble(options.AirAssist,
            options.Origin == JobOrigin.Absolute ? new Vec2(0, 0) : headPosition));

        var estimate = JobEstimator.Estimate(g, limits);
        if (checkBed)
        {
            var e = estimate.Extents;
            if (!double.IsNaN(e.MinX) &&
                (e.MinX < -Tolerance || e.MinY < -Tolerance || e.MaxX > profile.WidthMm + Tolerance || e.MaxY > profile.HeightMm + Tolerance))
                throw new JobException(
                    $"Some moves would leave the work area (X {e.MinX:0.#}…{e.MaxX:0.#}, Y {e.MinY:0.#}…{e.MaxY:0.#} mm). " +
                    "Move the design a little further from the edge.");
        }

        var job = new CompiledJob
        {
            Gcode = g, Estimate = estimate, Bounds = placed, Offset = offset, Items = output,
            PlacedAtHead = options.Origin == JobOrigin.CurrentPosition ? headPosition : null,
        };
        job.Warnings.AddRange(warnings);
        return job;

        void EmitEngrave(DesignItem item)
        {
            g.Add($"; --- {Sanitize(item.Name)} ---");
            if (item.IsRaster)
            {
                var (gray, w, h) = item.RotatedGray();
                g.AddRange(GcodeGenerator.Raster(gray, w, h, item.X + offset.X, item.Y + offset.Y, item.W, item.H,
                    item.Raster, maxPowerS, minX, maxX));
            }
            else
            {
                g.AddRange(GcodeGenerator.Fill(Translate(item.TransformedShapes(), offset), item.Cut, maxPowerS, minX, maxX));
            }
        }

        void EmitLines(IEnumerable<DesignItem> lineItems)
        {
            var paths = new List<(Polyline Path, CutSettings Settings)>();
            foreach (var item in lineItems)
                foreach (var p in Translate(item.TransformedShapes(), offset))
                    paths.Add((p, item.Cut));
            if (paths.Count == 0) return;
            // Holes before outlines and nearest-next across every line item.
            var ordered = PathOptimizer.Order(paths, p => p.Path, (p, poly) => (poly, p.Settings), start);
            g.Add("; --- line / cut paths ---");
            g.AddRange(GcodeGenerator.VectorJob(ordered, maxPowerS));
        }
    }

    private static List<Polyline> Translate(List<Polyline> shapes, Vec2 offset)
    {
        if (offset.X == 0 && offset.Y == 0) return shapes;
        var m = Mat2D.Translate(offset.X, offset.Y);
        foreach (var p in shapes) p.Transform(m);
        return shapes;
    }

    // Comments must not contain characters that end or confuse a G-code comment.
    private static string Sanitize(string name) =>
        new string(name.Where(c => c >= ' ' && c != '(' && c != ')' && c != ';').ToArray());
}
