namespace LaserPilot.Core.Geometry;

public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
    public double Length => Math.Sqrt(X * X + Y * Y);
}

/// <summary>2D affine transform: [a c e; b d f].</summary>
public readonly record struct Mat2D(double A, double B, double C, double D, double E, double F)
{
    public static Mat2D Identity => new(1, 0, 0, 1, 0, 0);
    public static Mat2D Translate(double tx, double ty) => new(1, 0, 0, 1, tx, ty);
    public static Mat2D Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);
    public static Mat2D Rotate(double deg)
    {
        var r = deg * Math.PI / 180;
        var (s, c) = (Math.Sin(r), Math.Cos(r));
        return new(c, s, -s, c, 0, 0);
    }
    /// <summary>this ∘ other (apply other first, then this).</summary>
    public Mat2D Multiply(Mat2D o) => new(
        A * o.A + C * o.B,
        B * o.A + D * o.B,
        A * o.C + C * o.D,
        B * o.C + D * o.D,
        A * o.E + C * o.F + E,
        B * o.E + D * o.F + F);
    public Vec2 Apply(Vec2 p) => new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);
}

/// <summary>A flattened open or closed polyline in millimetres.</summary>
public sealed class Polyline
{
    public List<Vec2> Points { get; } = new();
    public bool Closed { get; set; }

    public Polyline() { }
    public Polyline(IEnumerable<Vec2> pts, bool closed = false) { Points.AddRange(pts); Closed = closed; }

    public void Transform(Mat2D m)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] = m.Apply(Points[i]);
    }

    public (Vec2 min, Vec2 max) Bounds()
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in Points)
        {
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        return (new Vec2(minX, minY), new Vec2(maxX, maxY));
    }

    public static (Vec2 min, Vec2 max) BoundsOf(IEnumerable<Polyline> polys)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var poly in polys)
        {
            foreach (var p in poly.Points)
            {
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
            }
        }
        if (minX > maxX) return (new Vec2(0, 0), new Vec2(0, 0));
        return (new Vec2(minX, minY), new Vec2(maxX, maxY));
    }
}
