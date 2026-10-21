using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LaserPilot.Core.Gcode;
using LaserPilot.Core.Geometry;

namespace LaserPilot.App.Models;

/// <summary>
/// LaserPilot project files (.lpproj): every design item with its placement
/// and laser settings, plus job options. JSON, gzip-compressed; images are
/// stored as lossless 8-bit grayscale PNG.
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".lpproj";
    public const string Filter = "LaserPilot project (*.lpproj)|*.lpproj";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed class Document
    {
        public string Format { get; set; } = "LaserPilotProject";
        public int Version { get; set; } = FormatVersion;
        public string? ProfileId { get; set; }
        public JobOptions Job { get; set; } = new();
        public List<Item> Items { get; set; } = new();
    }

    public sealed class Item
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "vector";
        public bool Enabled { get; set; } = true;
        public VectorMode Mode { get; set; }
        public string? PresetName { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        public double Rotation { get; set; }
        public CutSettings? Cut { get; set; }
        public RasterSettings? Raster { get; set; }
        public double NaturalW { get; set; }
        public double NaturalH { get; set; }
        public List<PathData>? Paths { get; set; }
        public string? ImagePng { get; set; }
    }

    public sealed class PathData
    {
        public bool Closed { get; set; }
        /// <summary>x0, y0, x1, y1, …</summary>
        public double[] Points { get; set; } = Array.Empty<double>();
    }

    public static void Save(string path, IEnumerable<DesignItem> items, string? profileId, JobOptions job)
    {
        var doc = new Document { ProfileId = profileId, Job = job };
        foreach (var it in items)
        {
            var dto = new Item
            {
                Name = it.Name,
                Kind = it.IsRaster ? "image" : "vector",
                Enabled = it.Enabled,
                Mode = it.Mode,
                PresetName = it.PresetName,
                X = it.X, Y = it.Y, W = it.W, H = it.H,
                Rotation = it.RotationDeg,
                Cut = it.Cut,
                Raster = it.IsRaster ? it.Raster : null,
                NaturalW = it.NaturalW,
                NaturalH = it.NaturalH,
            };
            if (it.IsRaster) dto.ImagePng = Convert.ToBase64String(EncodeGrayPng(it.Gray!, it.PxW, it.PxH));
            else
                dto.Paths = it.Shapes.Select(p => new PathData
                {
                    Closed = p.Closed,
                    Points = p.Points.SelectMany(v => new[] { Math.Round(v.X, 5), Math.Round(v.Y, 5) }).ToArray(),
                }).ToList();
            doc.Items.Add(dto);
        }

        var tmp = path + ".tmp";
        using (var file = File.Create(tmp))
        using (var gz = new GZipStream(file, CompressionLevel.Optimal))
            JsonSerializer.Serialize(gz, doc, Json);
        File.Move(tmp, path, overwrite: true);
    }

    public static Document Load(string path, out List<DesignItem> items)
    {
        Document doc;
        using (var file = File.OpenRead(path))
        {
            // Accept both compressed and plain JSON files.
            bool gzip = file.ReadByte() == 0x1F && file.ReadByte() == 0x8B;
            file.Position = 0;
            using Stream s = gzip ? new GZipStream(file, CompressionMode.Decompress) : file;
            doc = JsonSerializer.Deserialize<Document>(s, Json) ?? throw new InvalidDataException("Empty project file.");
        }
        if (doc.Format != "LaserPilotProject") throw new InvalidDataException("This isn't a LaserPilot project file.");
        if (doc.Version > FormatVersion) throw new InvalidDataException("This project was saved by a newer version of LaserPilot — please update.");

        items = new List<DesignItem>();
        foreach (var dto in doc.Items)
        {
            DesignItem item;
            if (dto.Kind == "image" && dto.ImagePng is not null)
            {
                var (gray, w, h) = DecodeGrayPng(Convert.FromBase64String(dto.ImagePng));
                item = DesignItem.FromGray(dto.Name, gray, w, h);
                item.Raster = dto.Raster ?? new RasterSettings();
            }
            else
            {
                var shapes = (dto.Paths ?? new()).Select(p =>
                {
                    var pts = new List<Vec2>(p.Points.Length / 2);
                    for (int i = 0; i + 1 < p.Points.Length; i += 2) pts.Add(new Vec2(p.Points[i], p.Points[i + 1]));
                    return new Polyline(pts, p.Closed);
                }).ToList();
                item = DesignItem.FromSavedVector(dto.Name, shapes, dto.NaturalW, dto.NaturalH);
            }
            item.Enabled = dto.Enabled;
            item.Mode = dto.Mode;
            item.PresetName = dto.PresetName;
            item.X = dto.X; item.Y = dto.Y;
            item.W = Math.Max(0.01, dto.W); item.H = Math.Max(0.01, dto.H);
            item.RotationDeg = dto.Rotation;
            if (dto.Cut is not null) item.Cut = dto.Cut;
            items.Add(item);
        }
        return doc;
    }

    private static byte[] EncodeGrayPng(byte[] gray, int w, int h)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, gray, w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static (byte[] gray, int w, int h) DecodeGrayPng(byte[] png)
    {
        using var ms = new MemoryStream(png);
        var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var gray8 = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
        int w = gray8.PixelWidth, h = gray8.PixelHeight;
        var px = new byte[w * h];
        gray8.CopyPixels(px, w, 0);
        return (px, w, h);
    }
}
