using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using LaserPilot.Core.Updates;

namespace LaserPilot.App.Services;

/// <summary>
/// Checks GitHub Releases for newer builds and installs them by swapping the
/// portable .exe. The repository comes from the build (UpdateRepository in the
/// .csproj); for a private repository a read-only token is needed — either
/// entered by the user (stored encrypted) or embedded by CI from a repository
/// secret.
/// </summary>
public sealed class UpdateService
{
    private readonly AppSettings _settings;

    public UpdateService(AppSettings settings) => _settings = settings;

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

    public static string CurrentVersionText
    {
        get
        {
            var v = CurrentVersion;
            return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    /// <summary>"owner/name" of the GitHub repository releases come from.</summary>
    public static string? Repository =>
        Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value is { Length: > 0 } r ? r : null;

    /// <summary>
    /// Builds made on a developer's PC (build number 0) are not replaced
    /// automatically — that would overwrite the dev output with a release.
    /// </summary>
    public static bool IsDevBuild => CurrentVersion.Build <= 0;

    /// <summary>Token embedded by CI (lightly obfuscated so it isn't sitting in the file as plain text).</summary>
    public static string? EmbeddedToken
    {
        get
        {
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LaserPilot.update.key");
                if (s is null) return null;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                for (int i = 0; i < bytes.Length; i++) bytes[i] ^= 0x5A;
                var token = Encoding.UTF8.GetString(bytes).Trim();
                return token.Length > 0 ? token : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>The token the user typed in wins over the built-in one.</summary>
    public string? Token => _settings.UpdateToken ?? EmbeddedToken;

    public bool HasToken => !string.IsNullOrEmpty(Token);

    private GitHubUpdateClient CreateClient(string? tokenOverride = null)
    {
        var repo = Repository ?? throw new UpdateCheckException(UpdateFailure.NotFound, "This build doesn't know which GitHub repository to update from.");
        return new GitHubUpdateClient(repo, () => tokenOverride ?? Token);
    }

    /// <summary>The newest release if it is newer than this build, else null.</summary>
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default, string? tokenOverride = null)
    {
        using var client = CreateClient(tokenOverride);
        var latest = await client.GetLatestAsync(_settings.IncludePrereleases, ct);
        return latest is not null && GitHubUpdateClient.IsNewer(latest.Version, CurrentVersion) ? latest : null;
    }

    /// <summary>Tests a token by listing releases. Returns null on success, otherwise the problem.</summary>
    public async Task<string?> TestTokenAsync(string token)
    {
        try
        {
            using var client = CreateClient(token);
            await client.GetLatestAsync(true);
            return null;
        }
        catch (UpdateCheckException ex)
        {
            return ex.Message;
        }
    }

    private static string StagingFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaserPilot", "updates");

    /// <summary>Downloads the release and extracts LaserPilot.exe. Returns the staged exe path.</summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct)
    {
        var folder = Path.Combine(StagingFolder, release.Version.ToString());
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        var zip = Path.Combine(folder, release.Asset.Name);

        using (var client = CreateClient())
            await client.DownloadAsync(release.Asset, zip, progress, ct);

        var exe = Path.Combine(folder, "LaserPilot.exe");
        using (var archive = ZipFile.OpenRead(zip))
        {
            var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("LaserPilot.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("The update package doesn't contain LaserPilot.exe.");
            entry.ExtractToFile(exe, overwrite: true);
        }
        File.Delete(zip);

        // Sanity checks: a Windows executable of plausible size.
        var info = new FileInfo(exe);
        using (var fs = info.OpenRead())
        {
            if (info.Length < 1_000_000 || fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
                throw new InvalidDataException("The downloaded update doesn't look like a valid LaserPilot.exe.");
        }
        return exe;
    }

    /// <summary>Why the running copy can't replace itself, or null if it can.</summary>
    public static string? CannotSelfUpdateReason()
    {
        if (IsDevBuild) return "This is a developer build, so it isn't replaced automatically. Install the release build to get automatic updates.";
        var current = Environment.ProcessPath;
        if (current is null || !current.EndsWith("LaserPilot.exe", StringComparison.OrdinalIgnoreCase))
            return "LaserPilot couldn't locate its own program file.";
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(current)!, $".lp-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch
        {
            return $"LaserPilot doesn't have permission to change files in\n{Path.GetDirectoryName(current)}\n\n" +
                   "Move LaserPilot.exe to a folder you own (for example Desktop or Documents), or download the update manually.";
        }
    }

    /// <summary>
    /// Puts the staged exe in place of the running one. Windows lets a running
    /// program be renamed (not overwritten), so: rename current → .old, copy
    /// the new one into place. Any failure rolls back and rethrows.
    /// </summary>
    /// <param name="currentExe">The exe to replace (defaults to the running program).</param>
    public static void SwapInNewExe(string stagedExe, string? currentExe = null)
    {
        var current = currentExe ?? Environment.ProcessPath ?? throw new InvalidOperationException("Unknown program path.");
        if (!File.Exists(stagedExe)) throw new FileNotFoundException("The downloaded update is missing.", stagedExe);
        var old = current + ".old";
        try { if (File.Exists(old)) File.Delete(old); }
        catch { old = $"{current}.{DateTime.Now:yyyyMMddHHmmss}.old"; } // previous copy still locked

        File.Move(current, old);
        try
        {
            File.Copy(stagedExe, current, overwrite: false);
        }
        catch
        {
            try { if (File.Exists(current)) File.Delete(current); } catch { }
            File.Move(old, current); // roll back
            throw;
        }
    }

    /// <summary>Starts the freshly installed version. Call after the app has closed its windows.</summary>
    public static void LaunchInstalledVersion(string previousVersion)
    {
        var current = Environment.ProcessPath;
        if (current is null) return;
        Process.Start(new ProcessStartInfo(current)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(current)!,
            ArgumentList = { "--updated-from", previousVersion },
        });
    }

    /// <summary>Removes the previous version left behind by an update (it may still be exiting).</summary>
    public static void CleanupAfterUpdate()
    {
        try { if (Directory.Exists(StagingFolder)) Directory.Delete(StagingFolder, recursive: true); } catch { }
        _ = Task.Run(async () =>
        {
            var current = Environment.ProcessPath;
            if (current is null) return;
            var dir = Path.GetDirectoryName(current);
            if (dir is null) return;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                bool remaining = false;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, Path.GetFileName(current) + "*.old"))
                    {
                        try { File.Delete(f); } catch { remaining = true; }
                    }
                }
                catch { return; }
                if (!remaining) break;
                await Task.Delay(1000);
            }
        });
    }
}
