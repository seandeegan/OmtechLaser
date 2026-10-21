namespace LaserPilot.Core.Gcode;

/// <summary>Machine motion limits used for time estimates (GRBL $110/$111, $120/$121, $11).</summary>
public sealed record MotionLimits(
    double MaxRateXMmMin = 6000, double MaxRateYMmMin = 6000,
    double AccelXMmS2 = 1000, double AccelYMmS2 = 1000,
    double JunctionDeviationMm = 0.01, int BaudRate = 115200);

public sealed class JobEstimate
{
    public TimeSpan Total { get; init; }
    public double BurnDistanceMm { get; init; }
    public double TravelDistanceMm { get; init; }
    public int LineCount { get; init; }
    /// <summary>Estimated seconds elapsed once streamed line i has executed.</summary>
    public double[] CumulativeSeconds { get; init; } = Array.Empty<double>();
    /// <summary>Extents of every move (including overscan and travel); NaN when there are no moves.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Extents { get; init; }
    /// <summary>Extents of the moves that actually burn.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) BurnExtents { get; init; }

    /// <summary>Remaining time after <paramref name="ackedLines"/> lines were accepted.</summary>
    public TimeSpan RemainingAfter(int ackedLines)
    {
        if (CumulativeSeconds.Length == 0) return TimeSpan.Zero;
        int idx = Math.Clamp(ackedLines - 1, -1, CumulativeSeconds.Length - 1);
        double done = idx < 0 ? 0 : CumulativeSeconds[idx];
        return TimeSpan.FromSeconds(Math.Max(0, Total.TotalSeconds - done));
    }
}

/// <summary>
/// Estimates job duration by modelling GRBL's planner: per-axis rate and
/// acceleration limits, junction-deviation cornering speeds, and trapezoidal
/// acceleration with full look-ahead. Raster jobs can also be limited by how
/// fast G-code fits down the serial cable, so that is accounted for too.
/// </summary>
public static class JobEstimator
{
    /// <summary>Cap for estimates (about 115 days) — anything longer is a broken file.</summary>
    public const double MaxSeconds = 1e7;

    public static JobEstimate Estimate(IEnumerable<string> gcode, MotionLimits limits) =>
        Estimate(ToolpathParser.Parse(gcode), limits);

    public static JobEstimate Estimate(ToolpathParser.Result parsed, MotionLimits limits)
    {
        var moves = parsed.Moves;
        int n = moves.Count;
        var length = new double[n];
        var nominal = new double[n];   // mm/s
        var accel = new double[n];     // mm/s²
        var maxEntry = new double[n];  // mm/s
        var entry = new double[n + 1];

        double burn = 0, travel = 0;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        double bMinX = double.MaxValue, bMinY = double.MaxValue, bMaxX = double.MinValue, bMaxY = double.MinValue;

        double prevUx = 0, prevUy = 0;
        for (int i = 0; i < n; i++)
        {
            var m = moves[i];
            double dx = m.X1 - m.X0, dy = m.Y1 - m.Y0;
            double len = Math.Sqrt(dx * dx + dy * dy);
            length[i] = len;
            double ux = len > 0 ? dx / len : 0, uy = len > 0 ? dy / len : 0;

            double rateLimit = AxisLimited(limits.MaxRateXMmMin / 60, limits.MaxRateYMmMin / 60, ux, uy);
            double v = m.Rapid || m.FeedMmMin <= 0 ? rateLimit : Math.Min(m.FeedMmMin / 60, rateLimit);
            nominal[i] = Math.Max(0.01, v);
            accel[i] = Math.Max(1, AxisLimited(limits.AccelXMmS2, limits.AccelYMmS2, ux, uy));

            if (i == 0 || m.SyncBefore) maxEntry[i] = 0;
            else
            {
                double cosTheta = -(prevUx * ux + prevUy * uy);
                double junctionSpeed;
                if (cosTheta > 0.999999) junctionSpeed = 0;                 // full reversal
                else if (cosTheta < -0.999999) junctionSpeed = double.MaxValue; // straight on
                else
                {
                    double jx = ux - prevUx, jy = uy - prevUy;
                    double jl = Math.Sqrt(jx * jx + jy * jy);
                    double ja = jl > 0 ? AxisLimited(limits.AccelXMmS2, limits.AccelYMmS2, jx / jl, jy / jl) : accel[i];
                    double sinHalf = Math.Sqrt(0.5 * (1 - cosTheta));
                    junctionSpeed = Math.Sqrt(ja * limits.JunctionDeviationMm * sinHalf / (1 - sinHalf));
                }
                maxEntry[i] = Math.Min(junctionSpeed, Math.Min(nominal[i - 1], nominal[i]));
            }
            prevUx = ux; prevUy = uy;

            if (m.Power > 0 && !m.Rapid) burn += len; else travel += len;
            minX = Math.Min(minX, Math.Min(m.X0, m.X1)); maxX = Math.Max(maxX, Math.Max(m.X0, m.X1));
            minY = Math.Min(minY, Math.Min(m.Y0, m.Y1)); maxY = Math.Max(maxY, Math.Max(m.Y0, m.Y1));
            if (m.Power > 0 && !m.Rapid)
            {
                bMinX = Math.Min(bMinX, Math.Min(m.X0, m.X1)); bMaxX = Math.Max(bMaxX, Math.Max(m.X0, m.X1));
                bMinY = Math.Min(bMinY, Math.Min(m.Y0, m.Y1)); bMaxY = Math.Max(bMaxY, Math.Max(m.Y0, m.Y1));
            }
        }

        // Backward pass: every block must be able to stop by the end of the job.
        double next = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            entry[i] = Math.Min(maxEntry[i], Math.Sqrt(next * next + 2 * accel[i] * length[i]));
            next = entry[i];
        }
        // Forward pass: speeds must be reachable from the previous block.
        entry[n] = 0;
        for (int i = 0; i < n; i++)
        {
            double reachable = Math.Sqrt(entry[i] * entry[i] + 2 * accel[i] * length[i]);
            if (entry[i + 1] > reachable) entry[i + 1] = reachable;
        }

        // Time per streamed line.
        var perLine = new double[parsed.LineCount];
        for (int i = 0; i < n; i++)
            perLine[moves[i].Line] += BlockTime(length[i], entry[i], entry[i + 1], nominal[i], accel[i]);
        foreach (var (line, secs) in parsed.Dwells) perLine[line] += secs;

        // Serial floor: 10 bits per byte on the wire.
        double bytesPerSecond = Math.Max(1, limits.BaudRate / 10.0);
        var cumulative = new double[parsed.LineCount];
        double motionSum = 0, serialSum = 0;
        for (int i = 0; i < parsed.LineCount; i++)
        {
            motionSum += perLine[i];
            serialSum += parsed.LineBytes[i] / bytesPerSecond;
            cumulative[i] = Math.Max(motionSum, serialSum);
        }
        double total = parsed.LineCount > 0 ? cumulative[^1] : 0;
        // Absurd files (e.g. a 10^20 mm move at F1) must not overflow TimeSpan.
        if (!double.IsFinite(total) || total > MaxSeconds) total = MaxSeconds;

        return new JobEstimate
        {
            Total = TimeSpan.FromSeconds(total),
            BurnDistanceMm = burn,
            TravelDistanceMm = travel,
            LineCount = parsed.LineCount,
            CumulativeSeconds = cumulative,
            Extents = n > 0 ? (minX, minY, maxX, maxY) : (double.NaN, double.NaN, double.NaN, double.NaN),
            BurnExtents = bMinX <= bMaxX ? (bMinX, bMinY, bMaxX, bMaxY) : (double.NaN, double.NaN, double.NaN, double.NaN),
        };
    }

    /// <summary>The per-axis limit along a direction: the tightest axis wins.</summary>
    private static double AxisLimited(double limitX, double limitY, double ux, double uy)
    {
        double v = double.MaxValue;
        if (Math.Abs(ux) > 1e-9) v = Math.Min(v, limitX / Math.Abs(ux));
        if (Math.Abs(uy) > 1e-9) v = Math.Min(v, limitY / Math.Abs(uy));
        return v == double.MaxValue ? Math.Min(limitX, limitY) : v;
    }

    /// <summary>Trapezoid (or triangle) profile time for one block.</summary>
    private static double BlockTime(double length, double v0, double v1, double vMax, double a)
    {
        if (length <= 0) return 0;
        v0 = Math.Min(v0, vMax);
        v1 = Math.Min(v1, vMax);
        double accelDist = (vMax * vMax - v0 * v0) / (2 * a);
        double decelDist = (vMax * vMax - v1 * v1) / (2 * a);
        if (accelDist + decelDist <= length)
            return (vMax - v0) / a + (vMax - v1) / a + (length - accelDist - decelDist) / vMax;
        double peak = Math.Sqrt(Math.Max(0, (2 * a * length + v0 * v0 + v1 * v1) / 2));
        return Math.Max(0, (peak - v0) / a) + Math.Max(0, (peak - v1) / a);
    }
}
