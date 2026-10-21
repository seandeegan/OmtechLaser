using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LaserPilot.App.Models;
using LaserPilot.Core.Geometry;

namespace LaserPilot.App.Models;

/// <summary>Creates vector DesignItems for built-in shapes and text.</summary>
public static class ShapeFactory
{
    public static DesignItem Rect(double w = 50, double h = 30) =>
        DesignItem.FromVector("Rectangle", new List<Polyline>
        {
            new(new[] { new Vec2(0, 0), new Vec2(w, 0), new Vec2(w, h), new Vec2(0, h) }, closed: true),
        });

    public static DesignItem Ellipse(double w = 50, double h = 50)
    {
        var pts = new List<Vec2>();
        const int n = 64;
        for (int i = 0; i < n; i++)
        {
            var t = 2 * Math.PI * i / n;
            pts.Add(new Vec2(w / 2 + w / 2 * Math.Cos(t), h / 2 + h / 2 * Math.Sin(t)));
        }
        return DesignItem.FromVector("Ellipse", new List<Polyline> { new(pts, closed: true) });
    }

    public static DesignItem Line(double len = 80) =>
        DesignItem.FromVector("Line", new List<Polyline>
        {
            new(new[] { new Vec2(0, 0), new Vec2(len, 0) }),
        });

    public static DesignItem Star(int points = 5, double outer = 30, double inner = 12)
    {
        var pts = new List<Vec2>();
        for (int i = 0; i < points * 2; i++)
        {
            var r = i % 2 == 0 ? outer : inner;
            var t = Math.PI * i / points - Math.PI / 2;
            pts.Add(new Vec2(outer + r * Math.Cos(t), outer + r * Math.Sin(t)));
        }
        return DesignItem.FromVector("Star", new List<Polyline> { new(pts, closed: true) });
    }

    /// <summary>Renders text to vector outlines using the system font engine.</summary>
    public static DesignItem Text(string text, string fontFamily, bool bold, bool italic, double heightMm = 20)
    {
        var typeface = new Typeface(new FontFamily(fontFamily),
            italic ? FontStyles.Italic : FontStyles.Normal,
            bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);

        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, 100, Brushes.Black, 1.0);
        var geo = ft.BuildGeometry(new Point(0, 0));
        var flat = geo.GetFlattenedPathGeometry(0.15, ToleranceType.Absolute);

        var polys = new List<Polyline>();
        foreach (var fig in flat.Figures)
        {
            var pts = new List<Vec2> { new(fig.StartPoint.X, fig.StartPoint.Y) };
            foreach (var seg in fig.Segments)
            {
                switch (seg)
                {
                    case PolyLineSegment pls:
                        foreach (var p in pls.Points) pts.Add(new Vec2(p.X, p.Y));
                        break;
                    case LineSegment ls:
                        pts.Add(new Vec2(ls.Point.X, ls.Point.Y));
                        break;
                }
            }
            if (pts.Count >= 2) polys.Add(new Polyline(pts, fig.IsClosed));
        }
        if (polys.Count == 0) throw new InvalidOperationException("Text produced no outlines.");

        var item = DesignItem.FromVector($"Text: {Truncate(text)}", polys);
        // Scale so the text height equals heightMm, keeping aspect.
        var k = heightMm / item.H;
        item.W *= k;
        item.H = heightMm;
        // Text is almost always engraved solid: start in Fill mode with engraving settings.
        item.Mode = LaserPilot.Core.Gcode.VectorMode.Fill;
        item.Cut.SpeedMmMin = 3000;
        item.Cut.PowerPercent = 40;
        return item;
    }

    private static string Truncate(string s)
    {
        s = s.Replace("\r", "").Replace('\n', ' ');
        return s.Length <= 18 ? s : s[..18] + "…";
    }

    /// <summary>A rectangle with exact settings (used by the material test generator).</summary>
    public static DesignItem Box(string name, double x, double y, double w, double h)
    {
        var item = Rect(w, h);
        item.Name = name;
        item.X = x;
        item.Y = y;
        return item;
    }
}
