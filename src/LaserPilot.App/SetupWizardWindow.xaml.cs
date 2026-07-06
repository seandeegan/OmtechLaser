using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LaserPilot.Core.Comm;
using LaserPilot.Core.Machines;
using LaserPilot.Core.Safety;

namespace LaserPilot.App;

/// <summary>
/// Guided first-time setup for the OMTech B10 (and other GRBL machines).
/// Verifies connection, applies recommended settings, tests limit switches
/// and motion direction, then runs a supervised homing cycle — in an order
/// where nothing dangerous can happen before its prerequisites are verified.
/// </summary>
public partial class SetupWizardWindow : Window
{
    private readonly GrblController _grbl;
    private readonly MachineProfile _profile;
    private readonly HomingSafetyService _safety;
    private int _step;
    private CancellationTokenSource? _testCts;

    public bool HomedSuccessfully { get; private set; }
    private bool _limitTestPassed;

    public SetupWizardWindow(GrblController grbl, MachineProfile profile)
    {
        InitializeComponent();
        _grbl = grbl;
        _profile = profile;
        _safety = new HomingSafetyService(grbl);
        ShowStep(0);
        Closing += (_, _) => _testCts?.Cancel();
    }

    private void EStop_Click(object sender, RoutedEventArgs e) => _grbl.EmergencyStop();
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
    private void Back_Click(object sender, RoutedEventArgs e) { if (_step > 0) ShowStep(_step - 1); }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 1 && !_grbl.IsConnected)
        {
            MessageBox.Show(this, "Connect to the machine before continuing.", "Setup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_step < 6) ShowStep(_step + 1);
        else { MainWindow.MarkSetupDone(); Close(); }
    }

    private void ShowStep(int step)
    {
        _testCts?.Cancel();
        _testCts = null;
        _step = step;
        BackBtn.IsEnabled = step > 0;
        NextBtn.Content = step == 6 ? "Finish ✔" : "Next ▶";
        (StepTitle.Text, StepSubtitle.Text, StepContent.Content) = step switch
        {
            0 => ("Welcome", $"Setting up: {_profile.Name}", BuildWelcome()),
            1 => ("Step 1 — Connect", "Plug in USB, power the machine on, then connect.", BuildConnect()),
            2 => ("Step 2 — Machine settings", "Compare the controller's GRBL settings with the recommended values for this machine.", BuildSettingsStep()),
            3 => ("Step 3 — Limit switch test", "Verify both limit switches actually work — without moving the machine at all.", BuildLimitTest()),
            4 => ("Step 4 — Motion direction test", "Tiny 10 mm moves to confirm the motors run the right way.", BuildDirectionTest()),
            5 => ("Step 5 — Supervised homing test", "The first real homing cycle, watched closely with an automatic abort.", BuildHomingTest()),
            _ => ("All done!", "Your machine is configured.", BuildFinish()),
        };
    }

    // ---------- helpers ----------

    private static TextBlock Para(string text, double size = 13) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, Margin = new Thickness(0, 4, 0, 4),
    };

    private TextBlock StatusLine(string initial = "")
    {
        var tb = Para(initial);
        tb.FontFamily = new FontFamily("Consolas");
        return tb;
    }

    private async Task<bool> ApplySetting(int n, double v, TextBlock log)
    {
        var r = await _grbl.WriteSettingAsync(n, v);
        log.Text += r.Ok ? $"\n  ${n}={v.ToString(CultureInfo.InvariantCulture)} ✔" : $"\n  ${n}={v} ✖ {r.ErrorText}";
        return r.Ok;
    }

    // ---------- Step 0: welcome ----------

    private UIElement BuildWelcome()
    {
        var sp = new StackPanel();
        sp.Children.Add(Para("This wizard makes your laser safe to use before it makes a single big movement. It will:"));
        sp.Children.Add(Para("  1.  Connect to the machine over USB\n" +
                             "  2.  Check and fix the GRBL configuration (work area, homing direction, laser mode)\n" +
                             "  3.  Test the limit switches by hand — no motion involved\n" +
                             "  4.  Make tiny 10 mm test moves to verify motor directions\n" +
                             "  5.  Run the first homing cycle under close supervision with an automatic abort\n" +
                             "  6.  Turn on soft limits so the machine can never leave its work area again"));
        sp.Children.Add(Para("⚠ Safety first: wear your laser goggles whenever the machine is powered, and keep a hand near the STOP button (bottom-left of this window)."));
        sp.Children.Add(Para("The wizard never moves the machine without telling you first, and every movement step has a STOP button."));
        return sp;
    }

    // ---------- Step 1: connect ----------

    private UIElement BuildConnect()
    {
        var sp = new StackPanel();
        var ports = new ComboBox { Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var p in GrblController.ListPorts()) ports.Items.Add(p);
        if (ports.Items.Count > 0) ports.SelectedIndex = ports.Items.Count - 1;

        var status = StatusLine(_grbl.IsConnected ? $"Already connected. {_grbl.FirmwareVersion}" : "Not connected.");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var refresh = new Button { Content = "⟳", Width = 32 };
        refresh.Click += (_, _) =>
        {
            ports.Items.Clear();
            foreach (var p in GrblController.ListPorts()) ports.Items.Add(p);
            if (ports.Items.Count > 0) ports.SelectedIndex = ports.Items.Count - 1;
        };
        var connect = new Button { Content = "Connect", Width = 110, Style = (Style)FindResource("AccentButton") };
        connect.Click += async (_, _) =>
        {
            if (ports.SelectedItem is not string port) { status.Text = "No port selected. Press ⟳ after plugging in USB."; return; }
            try
            {
                connect.IsEnabled = false;
                status.Text = $"Connecting to {port}…";
                await Task.Run(() => _grbl.Connect(port, _profile.Baud));
                await Task.Delay(800);
                await _grbl.RefreshSettingsAsync();
                status.Text = _grbl.IsConnected
                    ? $"Connected ✔  {(_grbl.FirmwareVersion.Length > 0 ? _grbl.FirmwareVersion : "(GRBL detected)")}"
                    : "Port opened but no GRBL response yet — check the machine is powered on.";
            }
            catch (Exception ex)
            {
                status.Text = $"Failed: {ex.Message}\nClose other laser software (LightBurn/LaserGRBL) — only one program can use the port.";
            }
            finally { connect.IsEnabled = true; }
        };
        row.Children.Add(ports);
        row.Children.Add(refresh);
        row.Children.Add(connect);

        sp.Children.Add(Para("Connect the USB cable and power on the laser. The B10 shows up as a 'USB-SERIAL CH340' COM port. If no port appears, install the CH340 driver (link in the README) and press ⟳."));
        sp.Children.Add(row);
        sp.Children.Add(status);
        return sp;
    }

    // ---------- Step 2: settings review ----------

    private UIElement BuildSettingsStep()
    {
        var sp = new StackPanel();
        var list = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var log = StatusLine();

        async void Refresh()
        {
            if (!_grbl.IsConnected) { list.Text = "Not connected."; return; }
            await _grbl.RefreshSettingsAsync();
            var lines = new List<string>();
            foreach (var (n, want) in _profile.RecommendedSettings.OrderBy(k => k.Key))
            {
                if (n is 20 or 21 or 22) continue; // enabled later, after verification
                var have = _grbl.GetSetting(n);
                var info = GrblSettingsCatalog.Get(n);
                var match = have.HasValue && Math.Abs(have.Value - want) < 0.001;
                lines.Add($"{(match ? "✔" : "≠")} ${n} {info.Name}: {(have?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?")} (recommended {want.ToString("0.###", CultureInfo.InvariantCulture)})");
            }
            list.Text = string.Join("\n", lines);
        }

        var apply = new Button { Content = "Apply recommended values", Style = (Style)FindResource("AccentButton"), HorizontalAlignment = HorizontalAlignment.Left };
        apply.Click += async (_, _) =>
        {
            if (!_grbl.IsConnected) return;
            if (MessageBox.Show(this,
                "This writes the recommended values to the controller's memory (everything except homing/limits, " +
                "which are enabled later after testing). Your previous values will be shown in the console log.\n\nApply?",
                "Apply settings", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            apply.IsEnabled = false;
            log.Text = "Applying…";
            foreach (var (n, v) in _profile.RecommendedSettings.OrderBy(k => k.Key))
            {
                if (n is 20 or 21 or 22) continue;
                await ApplySetting(n, v, log);
            }
            log.Text += "\nDone.";
            apply.IsEnabled = true;
            Refresh();
        };

        sp.Children.Add(Para("Current controller values vs the recommended OMTech B10 configuration. Mismatches (≠) are worth fixing — especially $23 homing direction and $130/$131 work area, which are exactly what causes rail crashes."));
        sp.Children.Add(list);
        sp.Children.Add(apply);
        sp.Children.Add(log);
        Refresh();
        return sp;
    }

    // ---------- Step 3: limit switch test ----------

    private UIElement BuildLimitTest()
    {
        var sp = new StackPanel();
        var pinLine = StatusLine("Live switch state: (not running)");
        var result = StatusLine();
        var start = new Button { Content = "▶ Start 30-second test", Style = (Style)FindResource("AccentButton"), HorizontalAlignment = HorizontalAlignment.Left };

        start.Click += async (_, _) =>
        {
            if (!_grbl.IsConnected) { result.Text = "Not connected."; return; }
            start.IsEnabled = false;
            result.Text = "";
            pinLine.Text = "Live switch state: (none active)";
            _testCts = new CancellationTokenSource();
            try
            {
                var (x, y) = await _safety.LimitSwitchTestAsync(TimeSpan.FromSeconds(30),
                    pins => Dispatcher.BeginInvoke(() => pinLine.Text = $"Live switch state: {(pins.Length == 0 ? "(none active)" : pins)}"),
                    _testCts.Token);
                _limitTestPassed = x && y;
                result.Text = $"X switch: {(x ? "✔ detected" : "✖ NOT detected")}\nY switch: {(y ? "✔ detected" : "✖ NOT detected")}";
                result.Text += _limitTestPassed
                    ? "\n\nGreat — both switches work. Homing will be able to find them."
                    : "\n\nA switch was not seen. Check its plug on the control board and the wiring along the rail. " +
                      "Do NOT enable homing until both switches respond. (If pins show as ALWAYS active instead, the switches are " +
                      "wired normally-closed — set $5=1 in GRBL Settings and re-test.)";
            }
            catch (OperationCanceledException) { result.Text = "Test cancelled."; }
            finally { start.IsEnabled = true; }
        };

        sp.Children.Add(Para("With the machine POWERED but IDLE (no motion), you'll press each limit switch by hand and LaserPilot will watch the controller's inputs."));
        sp.Children.Add(Para("1. Press ▶ Start below.\n2. Reach into the machine and gently click the X limit switch (front-left, on the moving gantry arm) with your finger.\n3. Then click the Y limit switch (front-left corner of the frame).\n\nEach press should appear in the live state line below within a fraction of a second."));
        sp.Children.Add(start);
        sp.Children.Add(pinLine);
        sp.Children.Add(result);
        return sp;
    }

    // ---------- Step 4: direction test ----------

    private UIElement BuildDirectionTest()
    {
        var sp = new StackPanel();
        var log = StatusLine();
        sp.Children.Add(Para("First, position the laser head roughly in the MIDDLE of the bed by hand: with the machine idle you can gently push the gantry (steppers may hold it — if so use the $1 idle setting or just power-cycle, move it, and reconnect)."));
        sp.Children.Add(Para("Then use these buttons. Each makes ONE slow 10 mm move. Keep a hand near STOP."));

        var unlock = new Button { Content = "🔓 Unlock ($X) — needed after power-on if GRBL starts in alarm", HorizontalAlignment = HorizontalAlignment.Left };
        unlock.Click += async (_, _) => await _grbl.UnlockAsync();
        sp.Children.Add(unlock);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var bx = new Button { Content = "Move X +10 mm", Width = 150 };
        var by = new Button { Content = "Move Y +10 mm", Width = 150 };
        row.Children.Add(bx); row.Children.Add(by);
        sp.Children.Add(row);

        var q = Para("");
        sp.Children.Add(q);
        sp.Children.Add(log);

        async void TestAxis(string axis)
        {
            if (!_grbl.IsConnected) { log.Text = "Not connected."; return; }
            var r = await _grbl.JogAsync(axis == "X" ? 10 : 0, axis == "Y" ? 10 : 0, 500);
            if (!r.Ok) { log.Text = $"Move rejected: {r.ErrorText}\nIf 'locked out' — press Unlock first."; return; }
            await Task.Delay(1500);
            var expected = axis == "X" ? "to the RIGHT" : "AWAY from you (toward the back)";
            var ok = MessageBox.Show(this, $"Did the head move {expected}?", $"{axis} direction",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (ok)
            {
                log.Text += $"\n{axis} direction ✔";
            }
            else
            {
                var bit = axis == "X" ? 1 : 2;
                var cur = (int)(_grbl.GetSetting(3) ?? 0);
                var newVal = cur ^ bit;
                if (MessageBox.Show(this,
                    $"The {axis} motor runs backwards. This is fixed by flipping one bit of the GRBL direction mask ($3): {cur} → {newVal}.\n\nApply and re-test?",
                    "Fix direction", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    await _grbl.WriteSettingAsync(3, newVal);
                    log.Text += $"\n$3 set to {newVal}. Press 'Move {axis} +10 mm' again to verify.";
                }
            }
        }

        bx.Click += (_, _) => TestAxis("X");
        by.Click += (_, _) => TestAxis("Y");
        return sp;
    }

    // ---------- Step 5: supervised homing ----------

    private UIElement BuildHomingTest()
    {
        var sp = new StackPanel();
        var log = StatusLine();
        sp.Children.Add(Para("Now the real thing — but supervised. LaserPilot calculates the maximum time a correct homing cycle can take from your machine's own settings. If the cycle runs longer (i.e. the head is lost or grinding), it is stopped automatically."));
        sp.Children.Add(Para("What you should see: the head moves toward the FRONT-LEFT corner, touches both switches, backs off ~2 mm and stops. That corner becomes X0 Y0."));

        var enable = new Button { Content = "1️⃣ Enable homing in GRBL ($22=1, direction, speeds)", HorizontalAlignment = HorizontalAlignment.Left };
        enable.Click += async (_, _) =>
        {
            if (!_grbl.IsConnected) { log.Text = "Not connected."; return; }
            log.Text = "Writing homing configuration…";
            foreach (var n in new[] { 23, 24, 25, 26, 27, 22 })
                if (_profile.RecommendedSettings.TryGetValue(n, out var v))
                    await ApplySetting(n, v, log);
            log.Text += "\nHoming configured.";
        };
        sp.Children.Add(enable);

        var home = new Button { Content = "2️⃣ ▶ Run supervised homing now", Style = (Style)FindResource("AccentButton"), HorizontalAlignment = HorizontalAlignment.Left };
        home.Click += async (_, _) =>
        {
            if (!_grbl.IsConnected) { log.Text = "Not connected."; return; }
            var checks = await _safety.DiagnoseAsync(_profile);
            var fails = checks.Where(c => c.Result == CheckResult.Fail).ToList();
            if (fails.Count > 0)
            {
                log.Text = "Blocked by pre-flight checks:\n" + string.Join("\n", fails.Select(f => $"✖ {f.Title}: {f.Detail}"));
                return;
            }
            if (MessageBox.Show(this, "The machine will now move on its own for the first time. Hand near STOP?\n\nStart homing?",
                "Supervised homing", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            home.IsEnabled = false;
            log.Text = "Homing… watch the machine.";
            var result = await _safety.SafeHomeAsync(_profile);
            log.Text = result.Message;
            home.IsEnabled = true;
            if (result.Success)
            {
                HomedSuccessfully = true;
                if (MessageBox.Show(this,
                    "Homing works! 🎉\n\nFinal safety step: enable soft limits ($20=1) and hard limits ($21=1) so the machine " +
                    "physically cannot be driven outside its work area from now on.\n\nEnable them?",
                    "Enable limits", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    await ApplySetting(20, 1, log);
                    await ApplySetting(21, 1, log);
                    log.Text += "\nSoft + hard limits enabled. Your rails are protected.";
                }
            }
        };
        sp.Children.Add(home);
        sp.Children.Add(log);
        return sp;
    }

    // ---------- Step 6: finish ----------

    private UIElement BuildFinish()
    {
        var sp = new StackPanel();
        sp.Children.Add(Para(HomedSuccessfully
            ? "✅ Setup complete — homing verified and limits are protecting the machine."
            : "Setup finished. Note: homing was not verified in this run — you can re-open the wizard any time from the toolbar."));
        sp.Children.Add(Para("Tips for your first job:\n" +
            "  • Use 🏠 Home (safe) each time you power the machine on.\n" +
            "  • Focus the laser with the fixed-focus spacer bar (or the low-power pulse button).\n" +
            "  • Frame the job to check placement before starting.\n" +
            "  • Start with the material presets and adjust from there — always on scrap first.\n" +
            "  • Goggles on, ventilation running, never leave a running laser alone."));
        return sp;
    }
}
