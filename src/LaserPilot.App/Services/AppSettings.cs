using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LaserPilot.Core.Materials;

namespace LaserPilot.App.Services;

/// <summary>User preferences, stored as JSON in %AppData%\LaserPilot\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>%AppData%\LaserPilot, or LASERPILOT_DATA_DIR when set (handy for testing without touching real settings).</summary>
    public static string Folder =>
        Environment.GetEnvironmentVariable("LASERPILOT_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaserPilot");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    // Machine & connection
    public string? LastPort { get; set; }
    public string ProfileId { get; set; } = "omtech-light-b10";
    public bool SetupDone { get; set; }
    /// <summary>$23 confirmed in the Setup Wizard (3 = front-left switches, 1 = rear-left). Null = profile default.</summary>
    public int? HomingDirectionMask { get; set; }

    // First run & tutorial
    public bool WelcomeShown { get; set; }
    public bool TutorialCompleted { get; set; }

    // Updates
    public bool CheckForUpdates { get; set; } = true;
    public bool IncludePrereleases { get; set; }
    public string? SkippedVersion { get; set; }
    public bool UpdateSetupHintShown { get; set; }
    /// <summary>Release being installed when LaserPilot last restarted itself (checked on the next start).</summary>
    public string? InstallingVersion { get; set; }
    /// <summary>GitHub token, encrypted for the current Windows user (DPAPI).</summary>
    public string? UpdateTokenProtected { get; set; }

    // Workspace
    public List<string> RecentFiles { get; set; } = new();
    public List<MaterialPreset> CustomPresets { get; set; } = new();
    public double JogStepMm { get; set; } = 10;
    public double JogFeedMmMin { get; set; } = 2000;
    public double FocusPowerPercent { get; set; } = 1;
    public bool EngraveBeforeCut { get; set; } = true;
    public bool AirAssist { get; set; }
    public bool FrameWithLaser { get; set; }
    public bool ShowEngravePreview { get; set; } = true;

    // Window placement
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public static AppSettings Load()
    {
        try
        {
            var s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings()
                : new AppSettings();
            // A hand-edited or half-written file can hold nulls where lists are expected.
            s.ProfileId ??= "omtech-light-b10";
            s.RecentFiles = (s.RecentFiles ?? new()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            s.CustomPresets = (s.CustomPresets ?? new()).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Material))
                .Select(p => p with { BuiltIn = false }).ToList();
            // Earlier versions marked a finished Setup Wizard with an empty file.
            if (!s.SetupDone && File.Exists(Path.Combine(Folder, "setup-done"))) s.SetupDone = true;
            return s;
        }
        catch
        {
            return new AppSettings(); // corrupt file: start fresh rather than refuse to run
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Preferences are a convenience; never let saving them break anything.
        }
    }

    public void AddRecentFile(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 10) RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LaserPilot.UpdateToken.v1");

    [JsonIgnore]
    public string? UpdateToken
    {
        get
        {
            if (string.IsNullOrEmpty(UpdateTokenProtected)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(UpdateTokenProtected), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null; // created by another Windows user, or corrupted
            }
        }
        set
        {
            UpdateTokenProtected = string.IsNullOrWhiteSpace(value)
                ? null
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), Entropy, DataProtectionScope.CurrentUser));
        }
    }
}
