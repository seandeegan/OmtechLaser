using System.IO;
using System.Text;

namespace LaserPilot.App.Services;

/// <summary>Writes unexpected errors to %AppData%\LaserPilot\logs so they can be reported.</summary>
public static class CrashLog
{
    public static string Folder => Path.Combine(AppSettings.Folder, "logs");

    public static string? Write(Exception? ex, string source)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, $"error-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var sb = new StringBuilder()
                .AppendLine($"LaserPilot {UpdateService.CurrentVersion} — {DateTime.Now:O}")
                .AppendLine($"Source: {source}")
                .AppendLine($"OS: {Environment.OSVersion}")
                .AppendLine()
                .AppendLine(ex?.ToString() ?? "(no exception object)");
            File.WriteAllText(path, sb.ToString());

            // Keep the folder from growing forever.
            foreach (var old in new DirectoryInfo(Folder).GetFiles("error-*.txt").OrderByDescending(f => f.Name).Skip(30))
                old.Delete();
            return path;
        }
        catch
        {
            return null;
        }
    }
}
