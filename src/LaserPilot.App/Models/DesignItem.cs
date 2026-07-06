using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LaserPilot.Core.Gcode;
using LaserPilot.Core.Geometry;
using LaserPilot.Core.Importers;

namespace LaserPilot.App.Models;

/// <summary>A design element placed on the workspace: vector shapes or a raster image.</summary>
public sealed class DesignItem
{
    public string Name { get; set; } = "";
    public bool IsRaster => Gray is not null;

    // Vector: shapes normalized so their bounds start at (0,0); NaturalW/H = original size.
    public List<Polyline> Shapes { get; } = new();

    // Raster
    public byte[]? Gray { get; private set; }
    public int PxW { get; private set; }
    public int PxH { get; private set; }
    public BitmapSource? Preview { get; private set; }

    public double NaturalW { get; private set; } = 1;
    public double NaturalH { get; private set; } = 1;

    // Placement in work space (mm, origin front-left, Y toward rear).
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 10;
    public double H { get; set; } = 10;
    /// <summary>Rotation in degrees CCW. Vector: any angle. Raster: snapped to 90° steps.</summary>
    public double RotationDeg { get; set; }

    // Per-item job settings
    public CutSettings Cut { get; set; } = new();
    public RasterSettings Raster { get; set; } = new();

    public static DesignItem FromVector(string name, List<Polyline> shapes)
    {
        var item = new DesignItem { Name = name };
        var (min, max) = Polyline.BoundsOf(shapes);
        var w = Math.Max(0.001, max.X - min.X);
        var h = Math.Max(0.001, max.Y - min.Y);
        foreach (var p in shapes)
        {
            // Normalize to (0,0) and flip Y: SVG/DXF Y-down → workspace Y-up.
            var flipped = new Polyline(p.Points.Select(pt => new Vec2(pt.X - min.X, h - (pt.Y - min.Y))), p.Closed);
            item.Shapes.Add(flipped);
        }
        item.NaturalW = w;
        item.NaturalH = h;
        item.W = w;
        item.H = h;
        return item;
    }

    public static DesignItem FromImageFile(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(Path.GetFullPath(path));
        bmp.EndInit();
        bmp.Freeze();

        var gray = new FormatConvertedBitmap(bmp, PixelFormats.Gray8, null, 0);
        int w = gray.PixelWidth, h = gray.PixelHeight;
        var pixels = new byte[w * h];
        gray.CopyPixels(pixels, w, 0);

        var item = new DesignItem
        {
            Name = Path.GetFileName(path),
            Gray = pixels,
            PxW = w,
            PxH = h,
            Preview = bmp,
        };
        // Default physical size: 100 mm wide, keep aspect.
        item.NaturalW = w;
        item.NaturalH = h;
        item.W = 100;
        item.H = 100.0 * h / w;
        return item;
    }

    public static DesignItem FromFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".svg" => FromVector(Path.GetFileName(path), SvgImporter.Import(path)),
            ".dxf" => FromVector(Path.GetFileName(path), DxfImporter.Import(path)),
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" => FromImageFile(path),
            _ => throw new NotSupportedException($"Unsupported file type: {ext}"),
        };
    }

    /// <summary>Shapes transformed into work-space coordinates (mm).</summary>
    public List<Polyline> TransformedShapes()
    {
        var sx = W / NaturalW;
        var sy = H / NaturalH;
        var m = Mat2D.Translate(X + W / 2, Y + H / 2)
            .Multiply(Mat2D.Rotate(RotationDeg))
            .Multiply(Mat2D.Translate(-W / 2, -H / 2))
            .Multiply(Mat2D.Scale(sx, sy));
        var result = new List<Polyline>();
        foreach (var p in Shapes)
        {
            var copy = new Polyline(p.Points, p.Closed);
            copy.Transform(m);
            result.Add(copy);
        }
        return result;
    }

    /// <summary>Gray pixels rotated by the snapped rotation, for raster G-code.</summary>
    public (byte[] gray, int w, int h) RotatedGray()
    {
        if (Gray is null) return (Array.Empty<byte>(), 0, 0);
        int steps = ((int)Math.Round(RotationDeg / 90.0) % 4 + 4) % 4;
        var src = Gray;
        int w = PxW, h = PxH;
        for (int s = 0; s < steps; s++)
        {
            // rotate 90° CCW
            var dst = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    dst[(w - 1 - x) * h + y] = src[y * w + x];
            src = dst;
            (w, h) = (h, w);
        }
        return (src, w, h);
    }

    /// <summary>Axis-aligned bounds in work space.</summary>
    public (double x, double y, double w, double h) WorkBounds()
    {
        if (IsRaster || RotationDeg % 360 == 0) return (X, Y, W, H);
        var (min, max) = Polyline.BoundsOf(TransformedShapes());
        return (min.X, min.Y, max.X - min.X, max.Y - min.Y);
    }
}
