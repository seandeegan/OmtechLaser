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
    /// <summary>Images larger than this (longest side, pixels) are scaled down on import.</summary>
    public const int MaxImagePixels = 6000;

    public string Name { get; set; } = "";
    public bool IsRaster => Gray is not null;
    /// <summary>"Output" switch: disabled items stay on the canvas but are not burned.</summary>
    public bool Enabled { get; set; } = true;
    public VectorMode Mode { get; set; } = VectorMode.Line;
    public string? PresetName { get; set; }

    // Vector: shapes normalized so their bounds start at (0,0); NaturalW/H = original size.
    // Shared between clones — never mutated after creation.
    public List<Polyline> Shapes { get; private set; } = new();

    // Raster (shared between clones — never mutated after creation)
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

    /// <summary>Whether Fill can do anything (it needs closed shapes).</summary>
    public bool HasClosedShapes => Shapes.Any(FillGenerator.IsFillable);

    public DesignItem Clone() => new()
    {
        Name = Name,
        Enabled = Enabled,
        Mode = Mode,
        PresetName = PresetName,
        Shapes = Shapes,
        Gray = Gray,
        PxW = PxW,
        PxH = PxH,
        Preview = Preview,
        NaturalW = NaturalW,
        NaturalH = NaturalH,
        X = X,
        Y = Y,
        W = W,
        H = H,
        RotationDeg = RotationDeg,
        Cut = Cut.Clone(),
        Raster = Raster.Clone(),
    };

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

    /// <summary>Recreates a vector item from saved, already-normalized shapes.</summary>
    public static DesignItem FromSavedVector(string name, List<Polyline> normalizedShapes, double naturalW, double naturalH) => new()
    {
        Name = name,
        Shapes = normalizedShapes,
        NaturalW = Math.Max(0.001, naturalW),
        NaturalH = Math.Max(0.001, naturalH),
    };

    /// <summary>Raster item from 8-bit gray pixels (0 = black).</summary>
    public static DesignItem FromGray(string name, byte[] gray, int w, int h)
    {
        var preview = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, gray, w);
        preview.Freeze();
        return new DesignItem
        {
            Name = name,
            Gray = gray,
            PxW = w,
            PxH = h,
            Preview = preview,
            NaturalW = w,
            NaturalH = h,
            W = 100,
            H = 100.0 * h / w,
        };
    }

    public static DesignItem FromImageFile(string path)
    {
        var decoder = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)),
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource src = decoder.Frames[0];

        // Phone photos are stored sideways with an EXIF "rotate me" flag.
        src = ApplyExifOrientation(src, ReadExifOrientation(decoder.Frames[0]));

        // Keep memory sane for huge photos: more pixels than the laser can resolve don't help.
        int longest = Math.Max(src.PixelWidth, src.PixelHeight);
        if (longest > MaxImagePixels)
        {
            double k = (double)MaxImagePixels / longest;
            src = new TransformedBitmap(src, new ScaleTransform(k, k));
        }

        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);

        // Composite onto white: transparent areas must stay unburned (their
        // hidden color is usually black, which would engrave a solid block).
        var gray = new byte[w * h];
        for (int i = 0, j = 0; i < gray.Length; i++, j += 4)
        {
            double a = px[j + 3] / 255.0;
            double lum = 0.114 * px[j] + 0.587 * px[j + 1] + 0.299 * px[j + 2];
            gray[i] = (byte)Math.Clamp(Math.Round(lum * a + 255 * (1 - a)), 0, 255);
        }

        return FromGray(Path.GetFileName(path), gray, w, h);
    }

    private static int ReadExifOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata meta) return 1;
            foreach (var query in new[] { "System.Photo.Orientation", "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                try
                {
                    var value = meta.GetQuery(query);
                    if (value is ushort u) return u;
                    if (value is int i) return i;
                }
                catch { /* query not supported for this format */ }
            }
        }
        catch { }
        return 1;
    }

    private static BitmapSource ApplyExifOrientation(BitmapSource src, int orientation)
    {
        Transform? t = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
            8 => new RotateTransform(270),
            _ => null,
        };
        if (t is null) return src;
        var tb = new TransformedBitmap(src, t);
        tb.Freeze();
        return tb;
    }

    public static DesignItem FromFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var item = ext switch
        {
            ".svg" => FromVector(Path.GetFileName(path), SvgImporter.Import(path)),
            ".dxf" => FromVector(Path.GetFileName(path), DxfImporter.Import(path)),
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp" => FromImageFile(path),
            _ => throw new NotSupportedException($"Unsupported file type: {ext}"),
        };
        if (!item.IsRaster && item.Shapes.Count == 0)
            throw new InvalidDataException("No drawable shapes were found in this file. If it contains text, convert the text to paths/outlines in your design program first.");
        return item;
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
        var result = new List<Polyline>(Shapes.Count);
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
        int steps = RotationSteps;
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

    /// <summary>Quarter turns (0–3) of a raster's snapped rotation.</summary>
    public int RotationSteps => ((int)Math.Round(RotationDeg / 90.0) % 4 + 4) % 4;

    /// <summary>Axis-aligned bounds in work space.</summary>
    public (double x, double y, double w, double h) WorkBounds()
    {
        if (IsRaster || RotationDeg % 360 == 0) return (X, Y, W, H);
        var (min, max) = Polyline.BoundsOf(TransformedShapes());
        return (min.X, min.Y, max.X - min.X, max.Y - min.Y);
    }

    /// <summary>One-line description for lists: mode, speed, power.</summary>
    public string Summary
    {
        get
        {
            if (IsRaster)
                return $"Image · {Raster.SpeedMmMin:0} mm/min · {Raster.MaxPowerPercent:0}%";
            var mode = Mode switch { VectorMode.Fill => "Fill", VectorMode.FillAndLine => "Fill+Line", _ => "Line" };
            var passes = Cut.Passes > 1 ? $" · {Cut.Passes}×" : "";
            return $"{mode} · {Cut.SpeedMmMin:0} mm/min · {Cut.PowerPercent:0}%{passes}";
        }
    }
}
