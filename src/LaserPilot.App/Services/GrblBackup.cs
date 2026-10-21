using System.Globalization;
using System.IO;
using System.Text;

namespace LaserPilot.App.Services;

/// <summary>
/// Plain-text backups of the controller's '$' settings, so factory values can
/// always be restored. A backup is written automatically whenever the settings
/// differ from the most recent backup.
/// </summary>
public static class GrblBackup
{
    public static string Folder => Path.Combine(AppSettings.Folder, "grbl-backups");

    public static string Format(IReadOnlyDictionary<int, double> settings, string firmware)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"; GRBL settings backup — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (firmware.Length > 0) sb.AppendLine($"; Firmware: {firmware}");
        foreach (var (n, v) in settings.OrderBy(kv => kv.Key))
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"${n}={v:0.###}"));
        return sb.ToString();
    }

    public static Dictionary<int, double> Parse(string text)
    {
        var result = new Dictionary<int, double>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('$')) continue;
            var eq = line.IndexOf('=');
            if (eq < 2) continue;
            var value = line[(eq + 1)..].Split(' ', '(', ';')[0];
            if (int.TryParse(line[1..eq], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) &&
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                result[n] = v;
        }
        return result;
    }

    /// <summary>Writes a backup unless the latest one holds the same values. Returns the new file, if any.</summary>
    public static string? SaveIfChanged(IReadOnlyDictionary<int, double> settings, string firmware)
    {
        if (settings.Count == 0) return null;
        try
        {
            Directory.CreateDirectory(Folder);
            var latest = new DirectoryInfo(Folder).GetFiles("settings-*.txt").OrderByDescending(f => f.Name).FirstOrDefault();
            if (latest is not null)
            {
                var previous = Parse(File.ReadAllText(latest.FullName));
                if (previous.Count == settings.Count && settings.All(kv => previous.TryGetValue(kv.Key, out var v) && Math.Abs(v - kv.Value) < 1e-6))
                    return null;
            }
            var path = Path.Combine(Folder, $"settings-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, Format(settings, firmware));
            foreach (var old in new DirectoryInfo(Folder).GetFiles("settings-*.txt").OrderByDescending(f => f.Name).Skip(50))
                old.Delete();
            return path;
        }
        catch
        {
            return null;
        }
    }
}
