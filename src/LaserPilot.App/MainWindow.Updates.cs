using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LaserPilot.App.Services;
using LaserPilot.Core.Comm;
using LaserPilot.Core.Updates;

namespace LaserPilot.App;

public partial class MainWindow
{
    private DispatcherTimer? _updateTimer;
    private ReleaseInfo? _pendingRelease;
    private bool _updateDialogOpen;

    private static string VersionText(ReleaseInfo r) => $"{r.Version.Major}.{r.Version.Minor}.{Math.Max(0, r.Version.Build)}";

    /// <summary>
    /// After restarting into an update: if the new program doesn't report the release's version
    /// (a release made by hand with a mismatched tag), skip that release so it isn't offered forever.
    /// </summary>
    private void CheckInstalledUpdate()
    {
        if (_settings.InstallingVersion is not { } expected) return;
        _settings.InstallingVersion = null;
        if (GitHubUpdateClient.TryParseVersion(expected, out var v) && GitHubUpdateClient.IsNewer(v, UpdateService.CurrentVersion))
        {
            _settings.SkippedVersion = expected;
            ShowAlert($"⚠ The update to {expected} was installed, but this copy of LaserPilot still reports version {UpdateService.CurrentVersionText}. " +
                      "That release won't be offered again; a newer one will be.", AlertKind.Warn);
        }
        _settings.Save();
    }

    private void StartUpdateChecks()
    {
        CheckInstalledUpdate();
        if (UpdateService.Repository is null) return;
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        first.Tick += async (_, _) =>
        {
            first.Stop();
            await AutoCheckForUpdatesAsync();
        };
        first.Start();
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += async (_, _) => await AutoCheckForUpdatesAsync();
        _updateTimer.Start();
    }

    private async Task AutoCheckForUpdatesAsync()
    {
        if (!_settings.CheckForUpdates || UpdateService.IsDevBuild || _updateDialogOpen || _closingConfirmed) return;
        if (!_updates.HasToken)
        {
            if (!_settings.UpdateSetupHintShown)
            {
                _settings.UpdateSetupHintShown = true;
                _settings.Save();
                ShowAlert("🔄 Automatic updates need a one-time setup on this computer, because LaserPilot's download page is private.",
                    AlertKind.Info, "Set up updates", () => UpdateSettings_Click(this, new RoutedEventArgs()));
            }
            return;
        }
        try
        {
            var release = await _updates.CheckAsync();
            if (release is null || _settings.SkippedVersion == release.Version.ToString()) return;
            OfferUpdate(release, popup: true);
        }
        catch (UpdateCheckException ex) when (ex.Kind is UpdateFailure.Unauthorized or UpdateFailure.NotFound)
        {
            ShowAlert("🔄 Couldn't check for updates: " + ex.Message, AlertKind.Warn, "Update settings",
                () => UpdateSettings_Click(this, new RoutedEventArgs()));
        }
        catch
        {
            // Offline or GitHub unavailable: try again at the next check.
        }
    }

    private void OfferUpdate(ReleaseInfo release, bool popup)
    {
        _pendingRelease = release;
        UpdateBannerText.Text = $"🎉 LaserPilot {VersionText(release)} is available — you have {UpdateService.CurrentVersionText}.";
        UpdateBanner.Visibility = Visibility.Visible;
        if (!popup) return;
        // Pop up straight away unless the user is busy with the laser, a dialog, or the tour;
        // then wait until they're done (the banner shows meanwhile).
        if (NothingToInterrupt()) { ShowUpdateDialog(release); return; }
        _updatePopupWait ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _updatePopupWait.Tick -= UpdatePopupWait_Tick;
        _updatePopupWait.Tick += UpdatePopupWait_Tick;
        _updatePopupWait.Start();
    }

    private DispatcherTimer? _updatePopupWait;

    private void UpdatePopupWait_Tick(object? sender, EventArgs e)
    {
        if (_pendingRelease is null || UpdateBanner.Visibility != Visibility.Visible || _closingConfirmed)
        {
            _updatePopupWait?.Stop(); // installed, skipped or dismissed meanwhile
            return;
        }
        if (!NothingToInterrupt()) return;
        _updatePopupWait?.Stop();
        ShowUpdateDialog(_pendingRelease);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    /// <summary>True when a pop-up can't get in the way of the machine or of what the user is doing.</summary>
    private bool NothingToInterrupt()
    {
        if (_grbl.IsJobRunning || _homingInProgress || _homeBusy || _connectBusy || _startBusy || _framing || _focusLaserOn ||
            _wizardOpen || IsTutorialActive || _drag != DragMode.None)
            return false;
        if (_grbl.IsConnected && _grbl.Status.State is not (MachineState.Idle or MachineState.Alarm or MachineState.Sleep))
            return false; // moving, paused, homing, or the flame sensor tripped
        // Any dialog or message box (they disable this window while open).
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        return hwnd != IntPtr.Zero && IsWindowEnabled(hwnd) && OwnedWindows.Count == 0;
    }

    private void ShowUpdateDialog(ReleaseInfo release)
    {
        if (_updateDialogOpen) return;
        _updatePopupWait?.Stop();
        _updateDialogOpen = true;
        try
        {
            var dlg = new UpdateDialog(release, _updates, CanInstallUpdateNow) { Owner = this };
            dlg.ShowDialog();
            switch (dlg.Outcome)
            {
                case UpdateDialog.Result.Skip:
                    _settings.SkippedVersion = release.Version.ToString();
                    _settings.Save();
                    UpdateBanner.Visibility = Visibility.Collapsed;
                    break;
                case UpdateDialog.Result.Installed:
                    // The new exe is in place; close cleanly (beam off, settings saved), then start it.
                    _settings.InstallingVersion = release.Version.ToString();
                    _settings.Save();
                    _launchUpdateOnExit = true;
                    _closingConfirmed = true;
                    Close();
                    break;
            }
        }
        finally { _updateDialogOpen = false; }
    }

    /// <summary>Null if it's fine to install right now; otherwise why not ("" = the user cancelled).</summary>
    private string? CanInstallUpdateNow()
    {
        if (_grbl.IsJobRunning) return "A job is running. Install the update after it has finished.";
        if (UpdateService.CannotSelfUpdateReason() is { } reason) return reason;
        return ConfirmDiscardChanges() ? null : "";
    }

    private void UpdateBannerOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingRelease is { } r) ShowUpdateDialog(r);
    }

    private void UpdateBannerClose_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateService.Repository is null)
        {
            Info("This copy of LaserPilot wasn't built with an update source.");
            return;
        }
        if (!_updates.HasToken)
        {
            if (Ask("LaserPilot's updates come from a private GitHub repository, so this computer needs a one-time access key.\n\nSet it up now?",
                    "Check for updates") == MessageBoxResult.Yes)
                UpdateSettings_Click(sender, e);
            return;
        }
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var release = await _updates.CheckAsync();
            Mouse.OverrideCursor = null;
            if (release is null)
            {
                Info($"You're up to date — LaserPilot {UpdateService.CurrentVersionText} is the newest version.", "Check for updates");
                return;
            }
            OfferUpdate(release, popup: false);
            ShowUpdateDialog(release);
        }
        catch (UpdateCheckException ex)
        {
            Warn(ex.Message, "Check for updates");
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private async void UpdateSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new UpdateSettingsDialog(_settings, _updates) { Owner = this };
        if (dlg.ShowDialog() == true && _updates.HasToken && !UpdateService.IsDevBuild)
            await AutoCheckForUpdatesAsync();
    }

    // ==================== Help ====================

    private void About_Click(object sender, RoutedEventArgs e)
    {
        Info($"LaserPilot {UpdateService.CurrentVersionText}{(UpdateService.IsDevBuild ? " (developer build)" : "")}\n\n" +
             "Safety-first laser control for the OMTech Light B10 and other GRBL lasers.\n\n" +
             $"Updates from: {UpdateService.Repository ?? "(none)"}\n" +
             $"Settings and logs: {AppSettings.Folder}\n\n" +
             "A laser engraver is a fire and eye hazard. Always wear rated goggles and never leave it running unattended.",
            "About LaserPilot");
    }

    private void OpenSetupGuide_Click(object sender, RoutedEventArgs e) =>
        GuideWindow.Open(this, "OMTECH_B10_SETUP.md", "OMTech B10 setup guide");

    private void OpenHomingGuide_Click(object sender, RoutedEventArgs e) =>
        GuideWindow.Open(this, "HOMING_TROUBLESHOOTING.md", "Homing troubleshooting");

    private void OpenUpdatesGuide_Click(object sender, RoutedEventArgs e) =>
        GuideWindow.Open(this, "UPDATES.md", "Automatic updates");

    private void DriverHelp_Click(object sender, RoutedEventArgs e)
    {
        if (Ask("The B10 talks to the computer through a \"CH340\" USB chip. Windows 10 and 11 usually install its driver automatically, " +
                "but if no port ever appears when the laser is plugged in and switched on, install the driver from the chip maker (WCH).\n\n" +
                "Open the driver download page in your web browser?", "USB driver") == MessageBoxResult.Yes)
            OpenUrl("https://www.wch-ic.com/downloads/CH341SER_EXE.html");
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(CrashLog.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CrashLog.Folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Warn($"Couldn't open the folder: {ex.Message}");
        }
    }

    internal static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser available */ }
    }
}
