using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LaserPilot.App.Models;
using LaserPilot.App.Services;
using LaserPilot.Core.Comm;
using LaserPilot.Core.Gcode;
using LaserPilot.Core.Machines;
using LaserPilot.Core.Safety;

namespace LaserPilot.App;

/// <summary>
/// Main window. The code is split by area into partial files:
/// Machine (connection, homing, jogging), Design (items, selection, undo,
/// projects), Canvas (drawing and mouse), Job (frame, preview, run),
/// Console, Tutorial and Updates.
/// </summary>
public partial class MainWindow : Window
{
    private readonly GrblController _grbl = new();
    private readonly HomingSafetyService _safety;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly UpdateService _updates;
    private MachineProfile _profile = MachineProfile.OmtechB10;

    private bool _homedThisSession;
    private bool _suppressPropEvents;
    private bool _closingConfirmed;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public MainWindow()
    {
        InitializeComponent();
        _safety = new HomingSafetyService(_grbl);
        _updates = new UpdateService(_settings);

        RestoreWindowPlacement();
        foreach (var p in MachineProfile.BuiltIn) ProfileCombo.Items.Add(p);
        var profileIndex = MachineProfile.BuiltIn.ToList().FindIndex(p => p.Id == _settings.ProfileId);
        ProfileCombo.SelectedIndex = Math.Max(0, profileIndex);

        ModeCombo.Items.Add("Line — follow the outline");
        ModeCombo.Items.Add("Fill — engrave the inside");
        ModeCombo.Items.Add("Fill + Line");
        foreach (var (name, _) in DitherChoices) OpDither.Items.Add(name);
        PulsePowerBox.Text = _settings.FocusPowerPercent.ToString("0.#", Inv);
        PreviewToggle.IsChecked = _settings.ShowEngravePreview;

        InitJobControls();
        RefreshPresetList();
        SelectJogCombos();
        BuildTutorialMenu();
        BuildRecentMenu();

        RefreshPorts();
        HookGrblEvents();
        UpdateJobButtons(new JobProgress());
        UpdateConnectionUi();
        SyncItemsList();
        SyncPropPanel();
        UpdateTitle();

        Loaded += (_, _) => OnFirstLoad();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _grbl.Dispose();
            if (_launchUpdateOnExit)
            {
                try { UpdateService.LaunchInstalledVersion(UpdateService.CurrentVersionText); }
                catch (Exception ex)
                {
                    MessageBox.Show($"The update was installed, but LaserPilot couldn't restart itself:\n{ex.Message}\n\nPlease start LaserPilot again.",
                        "LaserPilot update", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        };
    }

    /// <summary>Set once a new version has been swapped in: start it after this window closes.</summary>
    private bool _launchUpdateOnExit;

    private void OnFirstLoad()
    {
        FitView();
        RedrawCanvas();

        if (App.StartupFile is { } file) OpenAnyFile(file);
        if (App.UpdatedFrom is { } from)
            ShowAlert($"🎉 LaserPilot was updated from v{from} to v{UpdateService.CurrentVersionText}.", AlertKind.Info);

        if (!_settings.WelcomeShown)
        {
            _settings.WelcomeShown = true;
            _settings.Save();
            Dispatcher.BeginInvoke(ShowWelcome, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        StartUpdateChecks();
    }

    private void ShowWelcome()
    {
        var choice = WelcomeDialog.Ask(this, _settings.SetupDone);
        switch (choice)
        {
            case WelcomeDialog.Choice.Tutorial:
                StartTutorial(0);
                break;
            case WelcomeDialog.Choice.Wizard:
                Wizard_Click(this, new RoutedEventArgs());
                break;
        }
    }

    private bool _beamOffBeforeClose;

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_closingConfirmed)
        {
            if (_grbl.IsJobRunning)
            {
                // "Stop" means stop now — not after the save question below.
                if (Ask("A job is running. Closing LaserPilot will STOP the laser immediately.\n\nStop the job and close?",
                        "Job running", MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _grbl.EmergencyStop();
            }
            if (!ConfirmDiscardChanges())
            {
                e.Cancel = true;
                return;
            }
        }
        _closingConfirmed = true;

        // Never leave the beam on when we let go of the machine: wait until GRBL confirms M5,
        // because closing the port can throw away bytes that haven't gone out yet.
        bool beamOn = _focusLaserOn || _grbl.Status.LaserEnabled;
        StopFocusLaserTimer(sendOff: false);
        if (beamOn && !_beamOffBeforeClose && _grbl.IsConnected && !_grbl.IsJobRunning)
        {
            e.Cancel = true;
            _beamOffBeforeClose = true;
            await _grbl.SendAsync("M5", 1500);
            Close(); // comes back here with the beam off
            return;
        }
        EndTutorial(completed: false, remember: false);
        _updateTimer?.Stop();
        SaveWindowPlacement();
        _settings.Save();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // ==================== Keyboard ====================

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool typing = Keyboard.FocusedElement is TextBox or PasswordBox or ComboBox or ComboBoxItem;

        if (key == Key.F1) { StartTutorial(0); e.Handled = true; return; }
        if (key == Key.Escape && IsTutorialActive) { EndTutorial(completed: false); e.Handled = true; return; }

        if (ctrl)
        {
            switch (key)
            {
                case Key.N: NewProject(); break;
                case Key.O: OpenProject(); break;
                case Key.S: SaveProject(saveAs: shift); break;
                case Key.I: Import_Click(this, e); break;
                case Key.Z when !typing: if (shift) Redo(); else Undo(); break;
                case Key.Y when !typing: Redo(); break;
                case Key.D: Duplicate(); break;
                case Key.A when !typing: SelectAll(); break;
                case Key.Left when !typing: Nudge(-0.1, 0); break;
                case Key.Right when !typing: Nudge(0.1, 0); break;
                case Key.Up when !typing: Nudge(0, 0.1); break;
                case Key.Down when !typing: Nudge(0, -0.1); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (typing) return;

        double step = shift ? 10 : 1;
        switch (key)
        {
            case Key.Delete: DeleteSelection(); break;
            case Key.Left: Nudge(-step, 0); break;
            case Key.Right: Nudge(step, 0); break;
            case Key.Up: Nudge(0, step); break;
            case Key.Down: Nudge(0, -step); break;
            case Key.Escape: SetSelection(Array.Empty<DesignItem>(), null); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Enter in a property box commits it straight away.</summary>
    private void PropBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox tb) return;
        tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (tb.Name.StartsWith("Item", StringComparison.Ordinal)) ItemProp_Changed(tb, e);
        else OpProp_Changed(tb, e);
        tb.SelectAll();
        e.Handled = true;
    }

    // ==================== Helpers ====================

    private MessageBoxResult Ask(string text, string title, MessageBoxImage icon = MessageBoxImage.Question) =>
        MessageBox.Show(this, text, title, MessageBoxButton.YesNo, icon);

    private void Info(string text, string title = "LaserPilot") =>
        MessageBox.Show(this, text, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private void Warn(string text, string title = "LaserPilot") =>
        MessageBox.Show(this, text, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>Parses a number typed with either '.' or ',' as the decimal separator.</summary>
    internal static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().Replace(',', '.').TrimEnd('%');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static double ParseOr(string text, double fallback) => TryParseNumber(text, out var v) ? v : fallback;

    private void UpdateTitle()
    {
        var name = _projectPath is null ? "Untitled" : Path.GetFileNameWithoutExtension(_projectPath);
        Title = $"{name}{(_dirty ? " •" : "")} — LaserPilot {UpdateService.CurrentVersionText} — {_profile.Name}";
    }

    private void RestoreWindowPlacement()
    {
        if (_settings.WindowWidth is > 600 and var w && _settings.WindowHeight is > 400 and var h)
        {
            // Screens change (a laptop undocked from a big monitor): never open larger than the desktop.
            Width = Math.Min(w, Math.Max(800, SystemParameters.VirtualScreenWidth));
            Height = Math.Min(h, Math.Max(600, SystemParameters.VirtualScreenHeight));
            if (_settings.WindowLeft is { } l && _settings.WindowTop is { } t &&
                l > SystemParameters.VirtualScreenLeft - 50 && t > SystemParameters.VirtualScreenTop - 50 &&
                l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
                t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = l;
                Top = t;
            }
        }
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowPlacement()
    {
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.Width > 0 && r.Height > 0)
        {
            _settings.WindowLeft = r.Left;
            _settings.WindowTop = r.Top;
            _settings.WindowWidth = r.Width;
            _settings.WindowHeight = r.Height;
        }
    }

    private static readonly (string Name, DitherMode Mode)[] DitherChoices =
    {
        ("Photo — Floyd–Steinberg dither", DitherMode.FloydSteinberg),
        ("Photo — Jarvis (smoother)", DitherMode.Jarvis),
        ("Photo — Stucki (sharp)", DitherMode.Stucki),
        ("Photo — Atkinson (high contrast)", DitherMode.Atkinson),
        ("Photo — ordered pattern (even on wood)", DitherMode.Ordered),
        ("Grayscale (varies power — depth)", DitherMode.Grayscale),
        ("Black & white (threshold — logos)", DitherMode.Threshold),
    };

    private static DitherMode DitherFromIndex(int i) =>
        i >= 0 && i < DitherChoices.Length ? DitherChoices[i].Mode : DitherMode.FloydSteinberg;

    private static int IndexFromDither(DitherMode m) =>
        Math.Max(0, Array.FindIndex(DitherChoices, c => c.Mode == m));
}
