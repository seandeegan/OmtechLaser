using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LaserPilot.App.Services;
using LaserPilot.Core.Comm;
using LaserPilot.Core.Gcode;
using LaserPilot.Core.Machines;
using LaserPilot.Core.Safety;

namespace LaserPilot.App;

public partial class MainWindow
{
    private bool _homingInProgress;
    private bool _homeBusy;
    private bool _connectBusy;
    private bool _wizardOpen;
    private MachineState _lastState = MachineState.Disconnected;
    private DispatcherTimer? _focusTimer;
    private bool _focusLaserOn;
    private DispatcherTimer? _linkWatchdog;
    private bool _wasConnected;
    private DateTime _connectedSinceUtc;
    private bool _silenceReported;

    private double MaxPowerS => _grbl.GetSetting(30) is > 0 and var s ? s : _profile.MaxPowerS;

    private MotionLimits CurrentLimits() => new(
        _grbl.GetSetting(110) ?? _profile.MaxFeedXy,
        _grbl.GetSetting(111) ?? _profile.MaxFeedXy,
        _grbl.GetSetting(120) ?? _profile.AccelXy,
        _grbl.GetSetting(121) ?? _profile.AccelXy,
        _grbl.GetSetting(11) is > 0 and var jd ? jd : 0.01,
        _profile.Baud);

    // ==================== GRBL events ====================

    private void HookGrblEvents()
    {
        _grbl.Log += e => Dispatcher.BeginInvoke(() => AppendConsole(e));
        _grbl.StatusUpdated += s => Dispatcher.BeginInvoke(() => OnStatus(s));
        _grbl.JobProgressChanged += p => Dispatcher.BeginInvoke(() => UpdateJobButtons(p));
        _grbl.JobFinished += r => Dispatcher.BeginInvoke(() => OnJobFinished(r));
        _grbl.ConnectionChanged += () => Dispatcher.BeginInvoke(UpdateConnectionUi);
        _grbl.ConnectionLost += reason => Dispatcher.BeginInvoke(() => OnConnectionLost(reason));
        _grbl.AlarmRaised += (code, text) => Dispatcher.BeginInvoke(() => OnAlarm(code, text));
        _grbl.JobLineRejected += (n, line, error) => Dispatcher.BeginInvoke(() => OnJobLineRejected(n, line, error));
        _grbl.JobStalled += () => Dispatcher.BeginInvoke(OnJobStalled);

        _linkWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _linkWatchdog.Tick += (_, _) => CheckControllerSilence();
        _linkWatchdog.Start();
    }

    private void OnJobLineRejected(int lineNumber, string line, string error)
    {
        if (!_grbl.IsJobRunning) return;
        System.Media.SystemSounds.Exclamation.Play();
        var shown = line.Length > 40 ? line[..40] + "…" : line;
        ShowAlert($"⏸ Job paused: the controller rejected line {lineNumber:N0} ({shown}) — {error}. " +
                  "Carrying on could put the next part in the wrong place. Resume to continue anyway, or press ⏹ Stop.",
            AlertKind.Warn, "▶ Resume", () => { if (_grbl.IsJobRunning && _grbl.IsJobPaused) _grbl.ResumeJob(); }, tag: "job");
    }

    private void OnJobStalled()
    {
        if (!_grbl.IsJobRunning) return;
        ShowAlert("⚠ The laser has stood still for 10 seconds while LaserPilot is still waiting for it to confirm commands. " +
                  "If nothing is happening, stop the job and start it again.",
            AlertKind.Warn, "⏹ Stop the job", () => StopJob_Click(this, new RoutedEventArgs()), tag: "job");
    }

    // GRBL answers status queries several times a second. Silence means it has hung or the link is bad,
    // and the user must not believe the screen while the machine may still be moving.
    private void CheckControllerSilence()
    {
        // Start the clock afresh on every new connection: the last report may be from an old one.
        bool connected = _grbl.IsConnected;
        if (connected && !_wasConnected) _connectedSinceUtc = DateTime.UtcNow;
        _wasConnected = connected;

        bool watch = connected && !_homingInProgress && !_wizardOpen;
        var lastHeard = _grbl.LastStatusTimeUtc > _connectedSinceUtc ? _grbl.LastStatusTimeUtc : _connectedSinceUtc;
        bool silent = watch && DateTime.UtcNow - lastHeard > TimeSpan.FromSeconds(8);
        if (silent && !_silenceReported)
        {
            _silenceReported = true;
            System.Media.SystemSounds.Hand.Play();
            ShowAlert("⚠ The laser has stopped answering (nothing for 8 seconds). If it is moving or burning, press ⛔ STOP — " +
                      "if that doesn't stop it, switch it off at its power switch. Then check the USB cable and reconnect.",
                AlertKind.Danger, "Disconnect", () => { if (_grbl.IsConnected) Connect_Click(this, new RoutedEventArgs()); }, tag: "silent");
        }
        else if (!silent && _silenceReported)
        {
            _silenceReported = false;
            HideAlert("silent");
        }
    }

    /// <summary>The link is gone while the aiming laser was on: only the power switch can turn it off now.</summary>
    private void WarnIfBeamLeftOn()
    {
        if (!_focusLaserOn) return;
        _focusLaserOn = false;
        _focusTimer?.Stop();
        System.Media.SystemSounds.Hand.Play();
        Dispatcher.BeginInvoke(() => MessageBox.Show(this,
            "The connection to the laser was lost while the aiming laser was switched ON.\n\n" +
            "LaserPilot can't switch it off any more. SWITCH THE LASER OFF AT ITS POWER SWITCH NOW, then check the USB cable and reconnect.",
            "The laser may still be on!", MessageBoxButton.OK, MessageBoxImage.Stop));
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private void OnStatus(GrblStatus s)
    {
        var (text, brush) = s.State switch
        {
            MachineState.Idle => ("Ready", Res("OkBrush")),
            MachineState.Run => ("Running", Res("AccentBrush")),
            MachineState.Jog => ("Moving", Res("AccentBrush")),
            MachineState.Home => ("Homing…", Res("AccentBrush")),
            MachineState.Hold => (s.SubState.EndsWith(":1", StringComparison.Ordinal) ? "Pausing…" : "Paused", Res("WarnBrush")),
            MachineState.Alarm => ("Locked (alarm)", Res("DangerBrush")),
            MachineState.Door => ("Safety stop", Res("DangerBrush")),
            MachineState.Check => ("Check mode", Res("WarnBrush")),
            MachineState.Sleep => ("Sleeping", (Brush)new SolidColorBrush(Color.FromRgb(0x55, 0x57, 0x5F))),
            MachineState.Disconnected => ("Disconnected", new SolidColorBrush(Color.FromRgb(0x55, 0x57, 0x5F))),
            _ => ("Connecting…", Res("WarnBrush")),
        };
        StateText.Text = text;
        StateBadge.Background = brush;
        StateBadge.ToolTip = s.State switch
        {
            MachineState.Alarm => "The controller is locked. Click 🏠 Home (recommended) or 🔓 Unlock.",
            MachineState.Door => "The flame sensor or safety switch stopped the machine. Check for fire first!",
            _ => $"GRBL state: {s.SubState}",
        };
        if (s.State is not (MachineState.Disconnected or MachineState.Unknown))
            PosText.Text = $"Laser X {s.WX,7:0.00}  Y {s.WY,7:0.00}";
        if (!string.IsNullOrEmpty(_grbl.FirmwareVersion)) FirmwareText.Text = _grbl.FirmwareVersion;
        LaserIndicator.Visibility = _focusLaserOn || (s.LaserEnabled && s.State is MachineState.Run or MachineState.Idle or MachineState.Jog)
            ? Visibility.Visible : Visibility.Collapsed;
        if (_grbl.IsJobRunning)
        {
            FeedOverrideText.Text = $"{s.OverrideFeed:0}%";
            PowerOverrideText.Text = $"{s.OverrideSpindle:0}%";
        }

        if (s.State != _lastState) OnStateChanged(_lastState, s.State);
        _lastState = s.State;
        RedrawLaserMarker();
    }

    private void OnStateChanged(MachineState from, MachineState to)
    {
        switch (to)
        {
            case MachineState.Alarm when !_homingInProgress && !_wizardOpen:
                ShowAlert(_profile.HasLimitSwitches
                        ? "🔒 The laser is locked. That's normal right after switching it on or after an alarm — click Home so it finds its position (or Unlock to skip homing)."
                        : "🔒 The laser is locked after an alarm. Click Unlock, then check the laser's position.",
                    AlertKind.Warn,
                    _profile.HasLimitSwitches ? "🏠 Home" : "🔓 Unlock",
                    () => { if (_profile.HasLimitSwitches) Home_Click(this, new RoutedEventArgs()); else Unlock_Click(this, new RoutedEventArgs()); },
                    tag: "state");
                break;
            case MachineState.Door:
                System.Media.SystemSounds.Hand.Play();
                ShowAlert("🔥 SAFETY STOP — the flame sensor or the lid/door switch was triggered. CHECK FOR FIRE FIRST. " +
                          "When it's safe, clear the cause and press Resume (to continue the job) or Reset.",
                    AlertKind.Danger,
                    _grbl.IsJobRunning ? "▶ Resume" : "↺ Reset",
                    () => { if (_grbl.IsJobRunning) _grbl.ResumeJob(); else _grbl.SoftReset(); },
                    tag: "state");
                break;
            case MachineState.Idle or MachineState.Run or MachineState.Jog or MachineState.Home:
                HideAlert("state");
                break;
        }
    }

    private void UpdateConnectionUi()
    {
        bool connected = _grbl.IsConnected;
        ConnectBtn.Content = connected ? "Disconnect" : "Connect";
        PortCombo.IsEnabled = !connected;
        RefreshPortsBtn.IsEnabled = !connected;
        if (!connected)
        {
            WarnIfBeamLeftOn();
            _silenceReported = false;
            HideAlert("silent");
            StateText.Text = "Disconnected";
            StateBadge.Background = new SolidColorBrush(Color.FromRgb(0x55, 0x57, 0x5F));
            PosText.Text = "Laser X —  Y —";
            FirmwareText.Text = "";
            LaserIndicator.Visibility = Visibility.Collapsed;
            AirBtn.IsChecked = false;
            _homedThisSession = false;
            _focusLaserOn = false;
            _focusTimer?.Stop();
            _lastState = MachineState.Disconnected;
            HideAlert("state");
        }
        RedrawLaserMarker();
    }

    private void OnAlarm(int code, string text)
    {
        // Position is unknown after a hard limit, a reset while moving, or a failed homing.
        if (code is 1 or 3 or 6 or 7 or 8 or 9 or 10) _homedThisSession = false;
        if (_homingInProgress || _wizardOpen) return; // those flows explain the alarm themselves
        var advice = code is >= 6 and <= 10 ? HomingSafetyService.HomingFailureAdvice(code) : text;
        MessageBox.Show(this, advice, $"GRBL ALARM {code}", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnConnectionLost(string reason)
    {
        WarnIfBeamLeftOn();
        StopFocusLaserTimer(sendOff: false);
        ShowAlert("🔌 " + reason + " Check the USB cable and that the laser is switched on, then reconnect.",
            AlertKind.Danger, "Reconnect", () => Connect_Click(this, new RoutedEventArgs()));
        RefreshPorts();
    }

    // ==================== Alert banner ====================

    private enum AlertKind { Info, Warn, Danger }

    private Action? _alertAction;
    private string? _alertTag;

    private void ShowAlert(string text, AlertKind kind, string? actionText = null, Action? action = null, string? tag = null)
    {
        AlertText.Text = text;
        AlertBanner.Background = Res(kind switch
        {
            AlertKind.Info => "InfoBannerBrush",
            AlertKind.Warn => "WarnBannerBrush",
            _ => "DangerBannerBrush",
        });
        _alertAction = action;
        _alertTag = tag;
        AlertActionBtn.Content = actionText;
        AlertActionBtn.Style = (Style)FindResource(kind == AlertKind.Danger ? "DangerButton" : "AccentButton");
        AlertActionBtn.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        AlertBanner.Visibility = Visibility.Visible;
    }

    private void HideAlert(string? tag = null)
    {
        if (tag is not null && _alertTag != tag) return;
        AlertBanner.Visibility = Visibility.Collapsed;
        _alertAction = null;
        _alertTag = null;
    }

    private void AlertAction_Click(object sender, RoutedEventArgs e)
    {
        var action = _alertAction;
        HideAlert();
        action?.Invoke();
    }

    private void AlertClose_Click(object sender, RoutedEventArgs e) => HideAlert();

    // ==================== Connection ====================

    private void RefreshPorts()
    {
        var previous = (PortCombo.SelectedItem as PortInfo)?.Name ?? _settings.LastPort;
        var ports = PortScanner.Scan();
        PortCombo.Items.Clear();
        foreach (var p in ports) PortCombo.Items.Add(p);
        int idx = ports.FindIndex(p => p.Name.Equals(previous, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) idx = ports.FindIndex(p => p.LikelyLaser);
        if (idx < 0) idx = ports.Count - 1;
        PortCombo.SelectedIndex = idx;
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_connectBusy) return; // e.g. the banner's Reconnect while a connect is still under way
        _connectBusy = true;
        try { await ConnectOrDisconnectAsync(); }
        finally { _connectBusy = false; }
    }

    private async Task ConnectOrDisconnectAsync()
    {
        if (_grbl.IsConnected)
        {
            if (_grbl.IsJobRunning)
            {
                if (Ask("A job is running. Disconnecting will STOP the laser.\n\nStop and disconnect?", "Disconnect", MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
                _grbl.EmergencyStop();
            }
            bool beamOn = _focusLaserOn || _grbl.Status.LaserEnabled;
            StopFocusLaserTimer(sendOff: false);
            ConnectBtn.IsEnabled = false;
            try
            {
                // Beam off — and confirmed by the controller — before we let go of the machine.
                if (beamOn && _grbl.IsConnected && !_grbl.IsJobRunning) await _grbl.SendAsync("M5", 1500);
                await Task.Run(_grbl.Disconnect);
            }
            finally { ConnectBtn.IsEnabled = true; UpdateConnectionUi(); }
            return;
        }
        if (PortCombo.SelectedItem is not PortInfo port)
        {
            Info("No connection port was found.\n\n1. Plug the laser's USB cable into this computer.\n2. Switch the laser on.\n3. Press ⟳ next to the port list.\n\n" +
                 "If nothing ever appears, Windows needs the CH340 USB driver: Help → Install the USB driver.", "Connect");
            return;
        }
        try
        {
            ConnectBtn.IsEnabled = false;
            ConnectBtn.Content = "Connecting…";
            StateText.Text = "Connecting…";
            StateBadge.Background = Res("WarnBrush");
            HideAlert();
            await Task.Run(() => _grbl.Connect(port.Name, _profile.Baud));
            _settings.LastPort = port.Name;
            _settings.Save();
            await AfterConnectedAsync();
        }
        catch (Exception ex)
        {
            Warn($"Could not connect to {port.Name}:\n{ex.Message}\n\n" +
                 "Close any other program using the laser (LightBurn, LaserGRBL, Arduino IDE…), check that the laser is switched on, and try again.",
                "Connection failed");
        }
        finally
        {
            ConnectBtn.IsEnabled = true;
            UpdateConnectionUi();
        }
    }

    private async Task AfterConnectedAsync()
    {
        if (!await _grbl.RefreshSettingsAsync()) return;
        var backup = GrblBackup.SaveIfChanged(_grbl.Settings, _grbl.FirmwareVersion);
        if (backup is not null)
            AppendConsole(new LogEntry { Direction = LogDirection.Info, Text = $"Controller settings backed up to {backup}" });

        if (_grbl.GetSetting(32) == 0)
        {
            ShowAlert("⚠ Laser mode is OFF ($32=0). Engravings will be uneven and corners will over-burn. Turn it on?",
                AlertKind.Warn, "Turn laser mode on", async () =>
                {
                    var r = await _grbl.WriteSettingAsync(32, 1);
                    if (!r.Ok) Warn($"The controller refused: {r.ErrorText}");
                });
        }
        else if (!_settings.SetupDone && _profile.HasLimitSwitches)
        {
            ShowAlert("👋 First time with this laser? The Setup Wizard checks the homing direction and limit switches safely before anything moves.",
                AlertKind.Info, "🧙 Open the Setup Wizard", () => Wizard_Click(this, new RoutedEventArgs()));
        }
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not MachineProfile p) return;
        _profile = p;
        _settings.ProfileId = p.Id;
        _settings.Save();
        UpdateTitle();
        if (!IsLoaded) return;
        FitView();
        RedrawCanvas();
    }

    private void Wizard_Click(object sender, RoutedEventArgs e)
    {
        if (_grbl.IsJobRunning) { Info("Wait for the job to finish (or stop it) before running the Setup Wizard."); return; }
        var wiz = new SetupWizardWindow(_grbl, _profile, _settings) { Owner = this };
        _wizardOpen = true;
        try { wiz.ShowDialog(); }
        finally { _wizardOpen = false; }
        if (wiz.HomedSuccessfully) _homedThisSession = true;
        UpdateConnectionUi();
        if (_grbl.IsConnected) OnStateChanged(MachineState.Unknown, _grbl.Status.State);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_grbl) { Owner = this }.ShowDialog();
    }

    private void EStop_Click(object sender, RoutedEventArgs e)
    {
        bool linked = _grbl.IsConnected;
        bool sent = _grbl.EmergencyStop();
        _focusTimer?.Stop();
        _homedThisSession = _homedThisSession && _grbl.Status.State == MachineState.Idle;
        if (sent)
        {
            _focusLaserOn = false;
            return;
        }
        // The stop never left this computer: say so loudly instead of pretending it worked.
        _focusLaserOn = false;
        System.Media.SystemSounds.Hand.Play();
        MessageBox.Show(this,
            linked
                ? "The STOP signal could NOT be sent — LaserPilot has lost contact with the laser.\n\nSWITCH THE LASER OFF AT ITS POWER SWITCH NOW."
                : "LaserPilot isn't connected to the laser, so it can't stop it.\n\nIf the laser is moving or burning (for example a job started from its memory card), switch it off at its power switch.",
            "STOP", MessageBoxButton.OK, MessageBoxImage.Stop);
    }

    // ==================== Machine controls ====================

    private bool EnsureConnected()
    {
        if (_grbl.IsConnected) return true;
        Info("The laser isn't connected yet. Pick its port at the top left (it usually says CH340) and press Connect.");
        return false;
    }

    private bool EnsureNoJob()
    {
        if (!_grbl.IsJobRunning) return true;
        Info("A job is running. Wait for it to finish, or press ⏹ Stop first.");
        return false;
    }

    private async void Home_Click(object sender, RoutedEventArgs e)
    {
        if (_homeBusy) return; // already checking or homing (e.g. the banner's Home button clicked as well)
        if (!EnsureConnected() || !EnsureNoJob()) return;
        if (!_profile.HasLimitSwitches)
        {
            Info("This machine profile has no limit switches, so it can't home.\n\nMove the laser to the front-left corner of your work area with the arrows, then press 📍 Set origin.");
            return;
        }

        _homeBusy = true;
        HomeBtn.IsEnabled = false;
        try
        {
            var checks = await _safety.DiagnoseAsync(_profile, _settings.HomingDirectionMask);
            var failures = checks.Where(c => c.Result == CheckResult.Fail).ToList();
            var warnings = checks.Where(c => c.Result == CheckResult.Warning).ToList();

            if (failures.Count > 0)
            {
                Warn("Homing is blocked because these safety checks failed:\n\n" +
                     string.Join("\n\n", failures.Select(f => $"✖ {f.Title}\n   {f.Detail}")) +
                     "\n\nOpen the Setup Wizard to fix them safely.", "Homing pre-flight failed");
                return;
            }
            var dir = HomingSafetyService.ExpectedHomingDirection(_profile, _settings.HomingDirectionMask);
            var msg = $"Ready to home. The head will move toward the {HomingSafetyService.DescribeHomingCorner(dir, _profile.Origin).ToUpperInvariant()} " +
                      "corner until it touches the limit switches.\n\n" +
                      "LaserPilot watches the cycle and stops the machine automatically if it takes longer than it physically should.\n";
            if (warnings.Count > 0)
                msg += "\nWarnings:\n" + string.Join("\n", warnings.Select(w => $"⚠ {w.Title}: {w.Detail}"));
            msg += "\n\nStart homing?";
            if (Ask(msg, "Home machine") != MessageBoxResult.Yes) return;

            // Beam off before anything moves.
            StopFocusLaserTimer(sendOff: false);
            if (_grbl.Status.State == MachineState.Idle) await _grbl.SendAsync("M5");

            _homingInProgress = true;
            var result = await _safety.SafeHomeAsync(_profile);
            if (result.Success)
            {
                _homedThisSession = true;
                HideAlert("state");
                Info(result.Message, "Homing complete");
            }
            else
            {
                Warn(result.Message, "Homing failed");
            }
        }
        finally
        {
            _homingInProgress = false;
            _homeBusy = false;
            HomeBtn.IsEnabled = true;
        }
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected() || !EnsureNoJob()) return;
        var wasAlarm = _grbl.Status.State == MachineState.Alarm;
        var r = await _grbl.UnlockAsync();
        if (!r.Ok) { Warn($"Unlock failed: {r.ErrorText}"); return; }
        HideAlert("state");
        if (wasAlarm && _profile.HasLimitSwitches)
            ShowAlert("🔓 Unlocked. The laser may not know exactly where it is — Home before running a job on the bed.",
                AlertKind.Info, "🏠 Home now", () => Home_Click(this, new RoutedEventArgs()));
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!_grbl.IsConnected) return;
        if (_grbl.IsJobRunning && Ask("Resetting will stop the running job. Continue?", "Reset", MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (_grbl.IsJobRunning) _grbl.StopJob();
        else _grbl.SoftReset();
        _focusLaserOn = false;
        _focusTimer?.Stop();
    }

    private async void SetOrigin_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected() || !EnsureNoJob()) return;
        if (_profile.HasLimitSwitches && Ask(
                "On the B10, homing sets the origin to the front-left corner of the bed automatically, so this is rarely needed.\n\n" +
                "Make the laser's current position X0 Y0 anyway? (Homing again puts the origin back in the corner.)",
                "Set origin here") != MessageBoxResult.Yes) return;
        var r = await _grbl.SendAsync("G10 L20 P1 X0 Y0");
        AppendConsole(new LogEntry
        {
            Direction = r.Ok ? LogDirection.Info : LogDirection.Error,
            Text = r.Ok ? "Work origin set to the current position." : $"Could not set the origin: {r.ErrorText}",
        });
    }

    private async void GotoOrigin_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected() || !EnsureNoJob()) return;
        var r = await _grbl.SendAsync("G90 G0 X0 Y0");
        if (!r.Ok) Warn($"The move was refused: {r.ErrorText}");
    }

    private async void AirAssist_Click(object sender, RoutedEventArgs e)
    {
        bool on = AirBtn.IsChecked == true;
        if (!_grbl.IsConnected || _grbl.IsJobRunning)
        {
            AirBtn.IsChecked = !on;
            EnsureConnected();
            EnsureNoJob();
            return;
        }
        var r = await _grbl.SendAsync(on ? "M8" : "M9");
        if (!r.Ok)
        {
            AirBtn.IsChecked = !on;
            Warn($"The controller refused the air assist command: {r.ErrorText}");
        }
    }

    // ==================== Jogging ====================

    private static readonly double[] JogSteps = { 0.1, 1, 10, 50, 100 };
    private static readonly double[] JogFeeds = { 500, 2000, 4000, 8000 };

    private void SelectJogCombos()
    {
        int s = Array.FindIndex(JogSteps, v => Math.Abs(v - _settings.JogStepMm) < 1e-9);
        int f = Array.FindIndex(JogFeeds, v => Math.Abs(v - _settings.JogFeedMmMin) < 1e-9);
        JogStepCombo.SelectedIndex = s >= 0 ? s : 2;
        JogFeedCombo.SelectedIndex = f >= 0 ? f : 1;
    }

    private void JogCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.JogStepMm = JogStep();
        _settings.JogFeedMmMin = JogFeed();
        _settings.Save();
    }

    private double JogStep() => JogStepCombo.SelectedIndex is >= 0 and var i && i < JogSteps.Length ? JogSteps[i] : 10;
    private double JogFeed() => JogFeedCombo.SelectedIndex is >= 0 and var i && i < JogFeeds.Length ? JogFeeds[i] : 2000;

    private async void Jog(double dx, double dy)
    {
        if (!EnsureConnected() || !EnsureNoJob()) return;
        if (_grbl.Status.State == MachineState.Alarm)
        {
            Info("The laser is locked (alarm). Click 🏠 Home or 🔓 Unlock first.");
            return;
        }
        var r = await _grbl.JogAsync(dx, dy, JogFeed());
        if (!r.Ok && r.ErrorCode == 15)
            AppendConsole(new LogEntry { Direction = LogDirection.Info, Text = "Move blocked by soft limits — it would leave the work area." });
    }

    private void JogXPlus_Click(object sender, RoutedEventArgs e) => Jog(JogStep(), 0);
    private void JogXMinus_Click(object sender, RoutedEventArgs e) => Jog(-JogStep(), 0);
    private void JogYPlus_Click(object sender, RoutedEventArgs e) => Jog(0, JogStep());
    private void JogYMinus_Click(object sender, RoutedEventArgs e) => Jog(0, -JogStep());
    private void JogCancel_Click(object sender, RoutedEventArgs e) => _grbl.JogCancel();

    // ==================== Focus laser ====================

    private async void LaserOn_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureReadyToMove()) return; // connected, unlocked, no job, flame sensor clear
        if (!TryParseNumber(PulsePowerBox.Text, out var pct)) pct = 1;
        pct = Math.Clamp(pct, 0.1, 20); // the aiming dot is capped low on purpose
        PulsePowerBox.Text = pct.ToString("0.#", Inv);
        _settings.FocusPowerPercent = pct;
        _settings.Save();
        if (Ask($"The laser will switch on at {pct:0.#}% power so you can see the dot. It turns itself off after 30 seconds.\n\n" +
                "WEAR YOUR LASER GOGGLES. Never look at the dot directly.\n\nSwitch the laser on?",
                "Laser on (aiming dot)", MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!EnsureReadyToMove()) return; // things may have changed while the question was open
        var s = pct / 100.0 * MaxPowerS;

        // Arm the 30-second auto-off BEFORE the beam can come on, so it can never be left burning.
        _focusLaserOn = true;
        LaserIndicator.Visibility = Visibility.Visible;
        _focusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _focusTimer.Tick -= FocusTimer_Tick;
        _focusTimer.Tick += FocusTimer_Tick;
        _focusTimer.Stop();
        _focusTimer.Start();

        // M3 = constant power: in laser mode it fires even while the head is still.
        var r = await _grbl.SendAsync($"M3 S{s.ToString("0", Inv)}");
        if (!r.Ok)
        {
            StopFocusLaserTimer(); // sends M5 in case it got through after all
            Warn($"The laser didn't switch on: {r.ErrorText}");
        }
    }

    private void FocusTimer_Tick(object? sender, EventArgs e)
    {
        StopFocusLaserTimer();
        AppendConsole(new LogEntry { Direction = LogDirection.Info, Text = "Aiming laser switched off automatically after 30 s." });
    }

    private void LaserOff_Click(object sender, RoutedEventArgs e)
    {
        // Always send M5 when asked — never second-guess an "off" request.
        StopFocusLaserTimer(sendOff: false);
        if (_grbl.IsConnected && !_grbl.IsJobRunning) _ = _grbl.SendAsync("M5");
    }

    private void StopFocusLaserTimer(bool sendOff = true)
    {
        _focusTimer?.Stop();
        bool wasOn = _focusLaserOn;
        _focusLaserOn = false;
        if (!_grbl.IsJobRunning) LaserIndicator.Visibility = Visibility.Collapsed;
        if (sendOff && _grbl.IsConnected && !_grbl.IsJobRunning && (wasOn || _grbl.Status.LaserEnabled))
            _ = _grbl.SendAsync("M5");
    }
}
