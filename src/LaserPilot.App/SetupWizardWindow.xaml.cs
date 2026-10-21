using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LaserPilot.App.Services;
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
    private const int LastStep = 6;
    /// <summary>The limit switch test, where the switch corner is chosen too.</summary>
    public const int SwitchTestStep = 3;

    private readonly GrblController _grbl;
    private readonly MachineProfile _profile;
    private readonly AppSettings _settings;
    private readonly HomingSafetyService _safety;
    private int _step;
    private CancellationTokenSource? _testCts;

    // Limits we switched off temporarily; they are switched back on after homing works.
    private bool _hardLimitsPaused;
    private bool _softLimitsPaused;

    public bool HomedSuccessfully { get; private set; }

    public SetupWizardWindow(GrblController grbl, MachineProfile profile, AppSettings settings, int startStep = 0)
    {
        InitializeComponent();
        _grbl = grbl;
        _profile = profile;
        _settings = settings;
        _safety = new HomingSafetyService(grbl);
        // Later steps need the connection: start at "Connect" if there isn't one.
        ShowStep(startStep > 1 && !grbl.IsConnected ? 1 : Math.Clamp(startStep, 0, LastStep));
        Closing += async (_, _) =>
        {
            _testCts?.Cancel();
            await RestorePausedLimitsAsync();
        };
    }

    private int HomingDirection => _settings.HomingDirectionMask
                                   ?? (int)(_profile.RecommendedSettings.TryGetValue(23, out var d) ? d : 3);

    private void EStop_Click(object sender, RoutedEventArgs e) => _grbl.EmergencyStop();
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
    private void Back_Click(object sender, RoutedEventArgs e) { if (_step > 0) ShowStep(_step - 1); }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 1 && !_grbl.IsConnected)
        {
            MessageBox.Show(this, "Connect to the laser before continuing.", "Setup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_step < LastStep) ShowStep(_step + 1);
        else
        {
            _settings.SetupDone = true;
            _settings.Save();
            Close();
        }
    }

    private void ShowStep(int step)
    {
        _testCts?.Cancel();
        _testCts = null;
        _step = step;
        BackBtn.IsEnabled = step > 0;
        NextBtn.Content = step == LastStep ? "Finish ✔" : "Next ▶";
        StepProgress.Value = step;
        (StepTitle.Text, StepSubtitle.Text, StepContent.Content) = step switch
        {
            0 => ("Welcome", $"Setting up: {_profile.Name}", BuildWelcome()),
            1 => ("Step 1 — Connect", "Plug in USB, switch the laser on, then connect.", BuildConnect()),
            2 => ("Step 2 — Machine settings", "Compare the controller's settings with the safe values for this machine.", BuildSettingsStep()),
            3 => ("Step 3 — Limit switch test", "Check both limit switches work — without the machine moving at all.", BuildLimitTest()),
            4 => ("Step 4 — Motor direction test", "Tiny 10 mm moves to confirm the motors turn the right way.", BuildDirectionTest()),
            5 => ("Step 5 — Supervised homing", "The first real homing cycle, watched closely with an automatic stop.", BuildHomingTest()),
            _ => ("All done!", "Your machine is configured.", BuildFinish()),
        };
    }

    // ---------- helpers ----------

    private static TextBlock Para(string text, double size = 13.5) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, Margin = new Thickness(0, 4, 0, 6),
    };

    private static TextBlock StatusLine(string initial = "")
    {
        var tb = Para(initial);
        tb.FontFamily = new FontFamily("Consolas");
        return tb;
    }

    private async Task<bool> ApplySetting(int n, double v, TextBlock log)
    {
        var r = await _grbl.WriteSettingAsync(n, v);
        var value = v.ToString("0.###", CultureInfo.InvariantCulture);
        log.Text += r.Ok ? $"\n  ${n}={value} ✔" : $"\n  ${n}={value} ✖ {r.ErrorText}";
        return r.Ok;
    }

    /// <summary>Leaves the machine as we found it: limits paused for a test are switched back on.</summary>
    private async Task RestorePausedLimitsAsync()
    {
        if (!_grbl.IsConnected || _grbl.IsJobRunning) return;
        if (_softLimitsPaused && (await _grbl.WriteSettingAsync(20, 1)).Ok) _softLimitsPaused = false;
        if (_hardLimitsPaused && (await _grbl.WriteSettingAsync(21, 1)).Ok) _hardLimitsPaused = false;
    }

    // ---------- Step 0: welcome ----------

    private UIElement BuildWelcome()
    {
        var sp = new StackPanel();
        sp.Children.Add(Para("This wizard makes your laser safe to use before it makes a single big movement. It will:"));
        sp.Children.Add(Para("  1.  Connect to the machine over USB\n" +
                             "  2.  Check the controller's safety settings (work area, homing direction, laser mode)\n" +
                             "  3.  Test the limit switches by hand — nothing moves\n" +
                             "  4.  Make tiny 10 mm test moves to check the motor directions\n" +
                             "  5.  Run the first homing cycle under close supervision, with an automatic stop\n" +
                             "  6.  Turn on the soft limits, so the machine can never be driven past its work area again"));
        sp.Children.Add(Para("⚠ Wear your laser goggles whenever the machine is powered, and keep a hand near the STOP button (bottom-left of this window)."));
        sp.Children.Add(Para("The wizard never moves the machine without telling you first, and your current settings are backed up automatically before anything is changed."));
        return sp;
    }

    // ---------- Step 1: connect ----------

    private UIElement BuildConnect()
    {
        var sp = new StackPanel();
        var ports = new ComboBox { Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
        void FillPorts()
        {
            ports.Items.Clear();
            var list = PortScanner.Scan();
            foreach (var p in list) ports.Items.Add(p);
            int idx = list.FindIndex(p => p.Name.Equals(_settings.LastPort, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = list.FindIndex(p => p.LikelyLaser);
            if (idx < 0) idx = list.Count - 1;
            ports.SelectedIndex = idx;
        }
        FillPorts();

        var status = StatusLine(_grbl.IsConnected ? $"Already connected. {_grbl.FirmwareVersion}" : "Not connected.");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var refresh = new Button { Content = "⟳", Width = 32, ToolTip = "Refresh the port list" };
        refresh.Click += (_, _) => FillPorts();
        var connect = new Button { Content = "Connect", Width = 110, Style = (Style)FindResource("AccentButton") };
        connect.Click += async (_, _) =>
        {
            if (ports.SelectedItem is not PortInfo port) { status.Text = "No port found. Plug in the USB cable, switch the laser on, then press ⟳."; return; }
            try
            {
                connect.IsEnabled = false;
                status.Text = $"Connecting to {port.Name}…";
                await Task.Run(() => _grbl.Connect(port.Name, _profile.Baud));
                await _grbl.RefreshSettingsAsync();
                GrblBackup.SaveIfChanged(_grbl.Settings, _grbl.FirmwareVersion);
                _settings.LastPort = port.Name;
                _settings.Save();
                status.Text = _grbl.IsConnected
                    ? $"Connected ✔  {(_grbl.FirmwareVersion.Length > 0 ? _grbl.FirmwareVersion : "(GRBL detected)")}"
                    : "The port opened but GRBL didn't answer yet — check the laser is switched on.";
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

        sp.Children.Add(Para("Connect the USB cable and switch the laser on. The B10 shows up as a \"USB-SERIAL CH340\" port (marked ★). " +
                             "If no port appears, install the CH340 driver (Help → Install the USB driver in the main window) and press ⟳."));
        sp.Children.Add(row);
        sp.Children.Add(status);
        return sp;
    }

    // ---------- Step 2: settings review ----------

    private UIElement BuildSettingsStep()
    {
        var sp = new StackPanel();
        var list = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        var reference = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("SubtleTextBrush") };
        var log = StatusLine();
        var inv = CultureInfo.InvariantCulture;

        // Homing/limit enables come later, after the tests prove them safe.
        IEnumerable<KeyValuePair<int, double>> Safe() => _profile.RecommendedSettings
            .Where(kv => kv.Key is not (20 or 21 or 22))
            .Select(kv => kv.Key == 23 ? new KeyValuePair<int, double>(23, HomingDirection) : kv)
            .OrderBy(kv => kv.Key);

        async void Refresh()
        {
            if (!_grbl.IsConnected) { list.Text = "Not connected."; return; }
            await _grbl.RefreshSettingsAsync();
            string Line(int n, double want)
            {
                var have = _grbl.GetSetting(n);
                var match = have.HasValue && Math.Abs(have.Value - want) < 0.001;
                return $"{(match ? "✔" : "≠")} ${n} {GrblSettingsCatalog.Get(n).Name}: {(have?.ToString("0.###", inv) ?? "?")} (recommended {want.ToString("0.###", inv)})";
            }
            list.Text = string.Join("\n", Safe().Select(kv => Line(kv.Key, kv.Value)));
            reference.Text = _profile.ReferenceSettings.Count == 0 ? "" :
                "For comparison only — these are factory calibration values and are NOT changed:\n" +
                string.Join("\n", _profile.ReferenceSettings.OrderBy(k => k.Key).Select(kv =>
                    $"   ${kv.Key} {GrblSettingsCatalog.Get(kv.Key).Name}: {(_grbl.GetSetting(kv.Key)?.ToString("0.###", inv) ?? "?")} (typical {kv.Value.ToString("0.###", inv)})"));
        }

        var apply = new Button { Content = "Apply the safe values", Style = (Style)FindResource("AccentButton"), HorizontalAlignment = HorizontalAlignment.Left };
        apply.Click += async (_, _) =>
        {
            if (!_grbl.IsConnected) return;
            if (MessageBox.Show(this,
                "This writes the values marked ≠ to the controller's memory (homing and limits are switched on later, after testing). " +
                "Your current settings were backed up when you connected.\n\nApply?",
                "Apply settings", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            apply.IsEnabled = false;
            log.Text = "Applying…";
            foreach (var (n, v) in Safe())
            {
                var have = _grbl.GetSetting(n);
                if (have.HasValue && Math.Abs(have.Value - v) < 0.001) continue;
                await ApplySetting(n, v, log);
            }
            log.Text += "\nDone.";
            apply.IsEnabled = true;
            Refresh();
        };

        sp.Children.Add(Para("Current controller values compared with safe values for this machine. Mismatches (≠) are worth fixing — " +
                             "especially $23 (homing direction) and $130/$131 (work area), which are exactly what cause rail crashes."));
        sp.Children.Add(list);
        sp.Children.Add(apply);
        sp.Children.Add(log);
        sp.Children.Add(new Border { Height = 10 });
        sp.Children.Add(reference);
        Refresh();
        return sp;
    }

    // ---------- Step 3: limit switch test ----------

    private UIElement BuildLimitTest()
    {
        var sp = new StackPanel();
        var pinLine = StatusLine("Live switch state: (not running)");
        var result = StatusLine();

        sp.Children.Add(Para("Where are the limit switches on your machine? Switch the laser off and look at the ends of the two rails for small " +
                             "switches with wires (often a tiny lever that clicks). The X switch (on the gantry arm) is on the left or the right; " +
                             "the Y switch (on a side rail) is at the front or the back. Together they give the corner the head homes to.\n\n" +
                             "LaserPilot's B10 profile expects the FRONT-LEFT, but go by what you see on your machine."));
        var cornerLog = StatusLine();
        var corners = new (string Label, int Mask)[]
        {
            ("Front-left — nearest you, on the left (B10 default)", 3),
            ("Rear-left — at the back, on the left", 1),
            ("Front-right — nearest you, on the right", 2),
            ("Rear-right — at the back, on the right", 0),
        };
        foreach (var (label, mask) in corners)
        {
            var rb = new RadioButton { Content = label, GroupName = "corner", IsChecked = HomingDirection == mask, Margin = new Thickness(0, 2, 0, 2) };
            rb.Checked += async (_, _) =>
            {
                if (_settings.HomingDirectionMask != mask) _settings.HomingProven = false; // new direction: first homing is gentle again
                _settings.HomingDirectionMask = mask;
                _settings.Save();
                // Point homing at that corner straight away ($23 is only a setting; nothing moves).
                if (_grbl.IsConnected && !_grbl.IsJobRunning && _grbl.GetSetting(23) != mask)
                {
                    var w = await _grbl.WriteSettingAsync(23, mask);
                    cornerLog.Text = w.Ok ? $"Homing direction set: $23={mask}" : $"Couldn't set $23: {w.ErrorText}";
                }
            };
            sp.Children.Add(rb);
        }
        sp.Children.Add(cornerLog);
        sp.Children.Add(Para("Can't find any switches? Then this laser can't home — don't use 🏠 Home. Instead move the head to the front-left " +
                             "corner with the arrows and press 📍 Set origin (and set $22 to 0 in GRBL Settings so homing stays off).", 12.5));

        var start = new Button { Content = "▶ Start the 30-second test", Style = (Style)FindResource("AccentButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        start.Click += async (_, _) =>
        {
            if (!_grbl.IsConnected) { result.Text = "Not connected."; return; }
            start.IsEnabled = false;
            result.Text = "";

            // With hard limits on, pressing a switch raises ALARM 1, which freezes GRBL until it
            // is reset — the second switch could never be seen. Pause hard limits for the test.
            if (_grbl.GetSetting(21) == 1 && !_hardLimitsPaused)
            {
                if ((await _grbl.WriteSettingAsync(21, 0)).Ok)
                {
                    _hardLimitsPaused = true;
                    result.Text = "(Hard limits are paused during this test — they're switched back on in the last step.)\n";
                }
            }

            bool alarm = false;
            void OnAlarm(int code, string _)
            {
                alarm = true;
                if (code == 1) _grbl.SoftReset(); // un-freeze GRBL so the test can carry on
            }
            _grbl.AlarmRaised += OnAlarm;
            pinLine.Text = "Live switch state: (none pressed)";
            _testCts = new CancellationTokenSource();
            try
            {
                var t = await _safety.LimitSwitchTestAsync(TimeSpan.FromSeconds(30),
                    pins => Dispatcher.BeginInvoke(() => pinLine.Text = $"Live switch state: {(pins.Length == 0 ? "(none pressed)" : pins)}"),
                    _testCts.Token);
                static string Line(string axis, bool seen, bool stuck) =>
                    stuck ? $"{axis} switch: ⚠ reads PRESSED the whole time" : $"{axis} switch: {(seen ? "✔ detected" : "✖ NOT detected")}";
                result.Text += Line("X", t.XSeen, t.XStuck) + "\n" + Line("Y", t.YSeen, t.YStuck);
                _settings.LimitSwitchesVerified = t.BothWork;
                _settings.Save();
                result.Text += t.BothWork
                    ? "\n\nGreat — both switches work. Homing is unlocked."
                    : t.XStuck || t.YStuck
                        ? "\n\nA switch that reads pressed all the time is usually wired 'normally closed': in GRBL Settings set $5 to 1, " +
                          "then run this test again. (If the head is really resting on that switch, move it away first.) Homing stays locked until both switches pass."
                        : "\n\nA switch wasn't seen. Check its plug on the control board and the wiring along the rail, then test again. " +
                          "Homing stays locked until both switches respond.";
            }
            catch (OperationCanceledException) { result.Text += "Test cancelled."; }
            finally
            {
                _grbl.AlarmRaised -= OnAlarm;
                start.IsEnabled = true;
            }
            if (alarm && _grbl.IsConnected)
            {
                await Task.Delay(1500);
                await _grbl.UnlockAsync();
                result.Text += "\n\n(The controller raised an alarm when a switch was pressed; it has been reset and unlocked.)";
            }
        };

        sp.Children.Add(Para("With the machine switched ON but standing still, you'll press each limit switch by hand while LaserPilot watches the controller's inputs. " +
                             "The head must not be touching a switch: if it's sitting in a corner, switch the laser off, slide the head to the middle by hand, " +
                             "switch it back on and reconnect."));
        sp.Children.Add(Para("1. Press ▶ Start below.\n2. Gently click the X limit switch (on the moving gantry arm) with your finger.\n3. Then click the Y limit switch (on the frame).\n\n" +
                             "Each press should appear in the live line below within a moment."));
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
        sp.Children.Add(Para("First, put the laser head roughly in the MIDDLE of the bed. You can push it gently by hand while the motors are relaxed " +
                             "(if they're holding firm, switch the laser off, move the head, switch it on and reconnect)."));
        sp.Children.Add(Para("Then use these buttons. Each one makes ONE slow 10 mm move. Keep a hand near STOP."));

        var unlock = new Button { Content = "🔓 Unlock ($X) — needed if the controller started up locked", HorizontalAlignment = HorizontalAlignment.Left };
        unlock.Click += async (_, _) =>
        {
            var r = await _grbl.UnlockAsync();
            log.Text += r.Ok ? "\nUnlocked." : $"\nUnlock failed: {r.ErrorText}";
        };
        sp.Children.Add(unlock);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var bx = new Button { Content = "Move X +10 mm", Width = 150 };
        var by = new Button { Content = "Move Y +10 mm", Width = 150 };
        row.Children.Add(bx);
        row.Children.Add(by);
        sp.Children.Add(row);
        sp.Children.Add(log);

        async void TestAxis(string axis)
        {
            if (!_grbl.IsConnected) { log.Text = "Not connected."; return; }
            var r = await _grbl.JogAsync(axis == "X" ? 10 : 0, axis == "Y" ? 10 : 0, 500);
            if (!r.Ok && r.ErrorCode == 15 && _grbl.GetSetting(20) == 1)
            {
                // Soft limits judge moves by a position that isn't known until homing works.
                if (MessageBox.Show(this,
                        "The move was blocked by the soft limits — they can't know where the head is until homing works.\n\n" +
                        "Pause the soft limits for this test? They're switched back on after the homing test.",
                        "Soft limits", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                if (!(await _grbl.WriteSettingAsync(20, 0)).Ok) { log.Text += "\nCouldn't change $20."; return; }
                _softLimitsPaused = true;
                r = await _grbl.JogAsync(axis == "X" ? 10 : 0, axis == "Y" ? 10 : 0, 500);
            }
            if (!r.Ok) { log.Text += $"\nMove rejected: {r.ErrorText}\nIf it says 'locked out', press Unlock first."; return; }
            await Task.Delay(1800);
            var expected = axis == "X" ? "to the RIGHT" : "AWAY from you (toward the back)";
            var ok = MessageBox.Show(this, $"Did the head move {expected}?", $"{axis} direction",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (ok)
            {
                log.Text += $"\n{axis} direction ✔";
                return;
            }
            var bit = axis == "X" ? 1 : 2;
            var cur = (int)(_grbl.GetSetting(3) ?? 0);
            var newVal = cur ^ bit;
            if (MessageBox.Show(this,
                    $"The {axis} motor runs backwards. This is fixed by flipping one bit of the direction setting ($3): {cur} → {newVal}.\n\nApply and re-test?",
                    "Fix direction", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var w = await _grbl.WriteSettingAsync(3, newVal);
                log.Text += w.Ok ? $"\n$3 set to {newVal}. Press 'Move {axis} +10 mm' again to check." : $"\nCouldn't change $3: {w.ErrorText}";
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
        var corner = HomingSafetyService.DescribeHomingCorner(HomingDirection, _profile.Origin).ToUpperInvariant();
        sp.Children.Add(Para("Now the real thing — supervised. LaserPilot works out the longest a correct homing cycle can take from your machine's own settings. " +
                             "If it runs longer (the head is lost or grinding), it's stopped automatically. The first homing runs at a gentle speed."));
        sp.Children.Add(Para($"What you should see: the head moves toward the {corner} corner, touches both switches, backs off about 2 mm and stops. " +
                             "That corner becomes the start of the bed."));
        sp.Children.Add(Para("⚠ If the head reaches the end of a rail and keeps pushing (grinding or knocking), press ⛔ STOP straight away."));

        var enable = new Button { Content = "1️⃣ Enable homing in the controller ($22=1, direction, speeds)", HorizontalAlignment = HorizontalAlignment.Left };
        enable.Click += async (_, _) =>
        {
            if (!_grbl.IsConnected) { log.Text = "Not connected."; return; }
            log.Text = "Writing the homing configuration…";
            foreach (var n in new[] { 23, 24, 25, 26, 27, 22 })
            {
                double? v = n == 23 ? HomingDirection : _profile.RecommendedSettings.TryGetValue(n, out var rv) ? rv : null;
                if (v is { } value) await ApplySetting(n, value, log);
            }
            log.Text += "\nHoming configured.";
        };
        sp.Children.Add(enable);

        var home = new Button { Content = "2️⃣ ▶ Run supervised homing now", Style = (Style)FindResource("AccentButton"), HorizontalAlignment = HorizontalAlignment.Left };
        home.Click += async (_, _) =>
        {
            if (!_grbl.IsConnected) { log.Text = "Not connected."; return; }
            if (!_settings.LimitSwitchesVerified)
            {
                log.Text = "Homing is locked until both limit switches pass the test in step 3. Press ◀ Back twice to get there.";
                return;
            }
            var checks = await _safety.DiagnoseAsync(_profile, HomingDirection);
            var fails = checks.Where(c => c.Result == CheckResult.Fail).ToList();
            if (fails.Count > 0)
            {
                log.Text = "Blocked by safety checks:\n" + string.Join("\n", fails.Select(f => $"✖ {f.Title}: {f.Detail}"));
                return;
            }
            if (MessageBox.Show(this, "The machine will now move on its own for the first time. Hand near STOP?\n\nStart homing?",
                "Supervised homing", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            home.IsEnabled = false;
            log.Text = "Homing… watch the machine.";
            var result = await _safety.SafeHomeAsync(_profile, _settings.HomingProven ? null : HomingSafetyService.GentleSeekMmMin);
            log.Text = result.Message;
            home.IsEnabled = true;
            if (!result.Success) return;

            HomedSuccessfully = true;
            _settings.HomingProven = true;
            _settings.Save();
            if (MessageBox.Show(this,
                    "Homing works! 🎉\n\nLast safety step: switch on the soft limits ($20=1) and hard limits ($21=1), so the machine " +
                    "physically can't be driven outside its work area from now on.\n\nSwitch them on?",
                    "Enable limits", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await ApplySetting(20, 1, log);
                await ApplySetting(21, 1, log);
                _softLimitsPaused = _hardLimitsPaused = false;
                log.Text += "\nSoft + hard limits on. Your rails are protected.";
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
            ? "✅ Setup complete — homing is verified and the limits are protecting the machine."
            : "Setup finished. Note: homing wasn't verified in this run — you can open the wizard again any time from the toolbar."));
        sp.Children.Add(Para("Tips for your first job:\n" +
            "  • Click 🏠 Home each time you switch the laser on.\n" +
            "  • Focus with the B10's focusing tool (or use the faint aiming dot).\n" +
            "  • Frame the job to check where it goes before burning.\n" +
            "  • Start from the material presets and adjust — always on a scrap first (Tools → Material test).\n" +
            "  • Goggles on, ventilation running, never leave a running laser alone."));
        return sp;
    }
}
