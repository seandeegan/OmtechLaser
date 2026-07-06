using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.IO.Path;
using LaserPilot.App.Models;
using LaserPilot.Core.Comm;
using LaserPilot.Core.Gcode;
using LaserPilot.Core.Machines;
using LaserPilot.Core.Materials;
using LaserPilot.Core.Safety;
using Microsoft.Win32;

namespace LaserPilot.App;

public partial class MainWindow : Window
{
    private readonly GrblController _grbl = new();
    private readonly HomingSafetyService _safety;
    private MachineProfile _profile = MachineProfile.OmtechB10;
    private readonly List<DesignItem> _items = new();
    private DesignItem? _selected;
    private bool _homedThisSession;
    private bool _suppressPropEvents;

    // Canvas view state
    private double _zoom = 1.2;              // px per mm
    private double _panX = 40, _panY = 20;
    private bool _viewInitialized;
    private Point _lastMouse;
    private bool _draggingItem, _panning;

    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaserPilot");

    public MainWindow()
    {
        InitializeComponent();
        _safety = new HomingSafetyService(_grbl);

        foreach (var p in MachineProfile.BuiltIn) ProfileCombo.Items.Add(p);
        ProfileCombo.SelectedIndex = 0;

        PresetCombo.Items.Add("— Material preset —");
        foreach (var m in MaterialPresets.ForDiode10W) PresetCombo.Items.Add(m);
        PresetCombo.SelectedIndex = 0;

        OpDither.Items.Add("Photo (Floyd–Steinberg)");
        OpDither.Items.Add("Photo (Jarvis, smoother)");
        OpDither.Items.Add("Grayscale (depth)");
        OpDither.Items.Add("Black/white threshold");

        RefreshPorts();
        HookGrblEvents();
        UpdateJobButtons(new JobProgress());
        Loaded += (_, _) => OnFirstLoad();
        Closing += (_, _) => _grbl.Dispose();
    }

    private void OnFirstLoad()
    {
        FitView();
        RedrawCanvas();
        var marker = Path.Combine(SettingsDir, "setup-done");
        if (!File.Exists(marker))
        {
            var r = MessageBox.Show(this,
                "Welcome to LaserPilot!\n\n" +
                "It looks like this is your first time here. Before moving the machine, " +
                "the Setup Wizard will safely verify your OMTech B10's homing direction and " +
                "limit switches so it can't crash into the rails.\n\nRun the Setup Wizard now?",
                "First-time setup", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (r == MessageBoxResult.Yes) Wizard_Click(this, new RoutedEventArgs());
        }
    }

    public static void MarkSetupDone()
    {
        Directory.CreateDirectory(SettingsDir);
        File.WriteAllText(Path.Combine(SettingsDir, "setup-done"), DateTime.Now.ToString("o"));
    }

    // ==================== GRBL events ====================

    private void HookGrblEvents()
    {
        _grbl.Log += e => Dispatcher.BeginInvoke(() => AppendConsole(e));
        _grbl.StatusUpdated += s => Dispatcher.BeginInvoke(() => OnStatus(s));
        _grbl.JobProgressChanged += p => Dispatcher.BeginInvoke(() => UpdateJobButtons(p));
        _grbl.ConnectionChanged += () => Dispatcher.BeginInvoke(UpdateConnectionUi);
        _grbl.AlarmRaised += (code, text) => Dispatcher.BeginInvoke(() =>
        {
            var advice = code is >= 6 and <= 10 ? HomingSafetyService.HomingFailureAdvice(code) : text;
            MessageBox.Show(this, advice, $"GRBL ALARM {code}", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void OnStatus(GrblStatus s)
    {
        StateText.Text = s.State.ToString();
        StateBadge.Background = s.State switch
        {
            MachineState.Idle => (Brush)FindResource("OkBrush"),
            MachineState.Run or MachineState.Jog or MachineState.Home => (Brush)FindResource("AccentBrush"),
            MachineState.Alarm => (Brush)FindResource("DangerBrush"),
            MachineState.Hold or MachineState.Door => (Brush)FindResource("WarnBrush"),
            _ => new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
        };
        PosText.Text = $"X {s.WX,8:0.00}  Y {s.WY,8:0.00}";
        if (!string.IsNullOrEmpty(_grbl.FirmwareVersion)) FirmwareText.Text = _grbl.FirmwareVersion;
        RedrawLaserMarker(s);
    }

    private void UpdateConnectionUi()
    {
        ConnectBtn.Content = _grbl.IsConnected ? "Disconnect" : "Connect";
        if (!_grbl.IsConnected)
        {
            StateText.Text = "Disconnected";
            StateBadge.Background = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            _homedThisSession = false;
        }
    }

    // ==================== Toolbar ====================

    private void RefreshPorts()
    {
        var current = PortCombo.SelectedItem as string;
        PortCombo.Items.Clear();
        foreach (var p in GrblController.ListPorts()) PortCombo.Items.Add(p);
        if (PortCombo.Items.Count > 0)
            PortCombo.SelectedIndex = current is not null && PortCombo.Items.Contains(current)
                ? PortCombo.Items.IndexOf(current) : PortCombo.Items.Count - 1;
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_grbl.IsConnected) { _grbl.Disconnect(); return; }
        if (PortCombo.SelectedItem is not string port)
        {
            MessageBox.Show(this, "No serial port selected. Plug in the laser's USB cable and press ⟳.\n\n" +
                "If no port ever appears, you may need the CH340 USB driver (see the Help docs).",
                "Connect", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            ConnectBtn.IsEnabled = false;
            ConnectBtn.Content = "Connecting…";
            StateText.Text = "Connecting…";
            StateBadge.Background = (Brush)FindResource("WarnBrush");
            await Task.Run(() => _grbl.Connect(port, _profile.Baud));
            await Task.Delay(600);
            await _grbl.RefreshSettingsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not connect to {port}:\n{ex.Message}\n\n" +
                "Close any other program using the port (LightBurn, LaserGRBL, Arduino IDE…) and try again.",
                "Connection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ConnectBtn.IsEnabled = true;
            UpdateConnectionUi();
        }
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is MachineProfile p)
        {
            _profile = p;
            _viewInitialized = false;
            FitView();
            RedrawCanvas();
        }
    }

    private void Wizard_Click(object sender, RoutedEventArgs e)
    {
        var wiz = new SetupWizardWindow(_grbl, _profile) { Owner = this };
        wiz.ShowDialog();
        if (wiz.HomedSuccessfully) _homedThisSession = true;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_grbl) { Owner = this }.ShowDialog();
    }

    private void EStop_Click(object sender, RoutedEventArgs e) => _grbl.EmergencyStop();

    // ==================== Machine controls ====================

    private async void Home_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected()) return;

        HomeBtn.IsEnabled = false;
        try
        {
            var checks = await _safety.DiagnoseAsync(_profile);
            var failures = checks.Where(c => c.Result == CheckResult.Fail).ToList();
            var warnings = checks.Where(c => c.Result == CheckResult.Warning).ToList();

            if (failures.Count > 0)
            {
                MessageBox.Show(this,
                    "Homing is blocked because pre-flight checks failed:\n\n" +
                    string.Join("\n\n", failures.Select(f => $"✖ {f.Title}\n   {f.Detail}")) +
                    "\n\nOpen the Setup Wizard to fix these safely.",
                    "Homing pre-flight failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var msg = "Ready to home. The head will move toward the " +
                      (_profile.Origin == OriginCorner.FrontLeft ? "FRONT-LEFT" : _profile.Origin.ToString().ToUpperInvariant()) +
                      " corner until it touches the limit switches.\n\n" +
                      "LaserPilot supervises the cycle and will hit the brakes automatically if it takes " +
                      "longer than physically possible.\n";
            if (warnings.Count > 0)
                msg += "\nWarnings:\n" + string.Join("\n", warnings.Select(w => $"⚠ {w.Title}: {w.Detail}"));
            msg += "\n\nStart homing?";
            if (MessageBox.Show(this, msg, "Home machine", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var result = await _safety.SafeHomeAsync(_profile);
            if (result.Success)
            {
                _homedThisSession = true;
                MessageBox.Show(this, result.Message, "Homing complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this, result.Message, "Homing failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally { HomeBtn.IsEnabled = true; }
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected()) return;
        var r = await _grbl.UnlockAsync();
        if (r.Ok && _grbl.Status.State == MachineState.Alarm)
            MessageBox.Show(this, "Unlocked. Note: after an alarm the machine may not know where it is — re-home or jog carefully.",
                "Unlock", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => _grbl.SoftReset();

    private async void SetOrigin_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected()) return;
        await _grbl.SendAsync("G10 L20 P1 X0 Y0");
        AppendConsole(new LogEntry { Direction = LogDirection.Info, Text = "Work origin set to current position." });
    }

    private async void GotoOrigin_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected()) return;
        await _grbl.SendAsync("G90 G0 X0 Y0");
    }

    private double JogStep()
    {
        var s = (JogStepCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "10 mm";
        return double.Parse(s.Split(' ')[0], CultureInfo.InvariantCulture);
    }

    private double JogFeed()
    {
        var s = (JogFeedCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "2000 mm/min";
        return double.Parse(s.Split(' ')[0], CultureInfo.InvariantCulture);
    }

    private async void Jog(double dx, double dy)
    {
        if (!EnsureConnected()) return;
        var r = await _grbl.JogAsync(dx, dy, JogFeed());
        if (!r.Ok && r.ErrorCode == 15)
            AppendConsole(new LogEntry { Direction = LogDirection.Info, Text = "Jog blocked by soft limits (would leave the work area)." });
    }

    private void JogXPlus_Click(object sender, RoutedEventArgs e) => Jog(JogStep(), 0);
    private void JogXMinus_Click(object sender, RoutedEventArgs e) => Jog(-JogStep(), 0);
    private void JogYPlus_Click(object sender, RoutedEventArgs e) => Jog(0, JogStep());
    private void JogYMinus_Click(object sender, RoutedEventArgs e) => Jog(0, -JogStep());
    private void JogCancel_Click(object sender, RoutedEventArgs e) => _grbl.JogCancel();

    private async void LaserOn_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected()) return;
        if (!double.TryParse(PulsePowerBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) pct = 1;
        pct = Math.Clamp(pct, 0, 20); // focus beam is capped low on purpose
        var s = pct / 100.0 * (_grbl.GetSetting(30) ?? _profile.MaxPowerS);
        if (MessageBox.Show(this,
            $"The laser will fire continuously at {pct:0.#}% power for focusing.\n\nWEAR YOUR LASER GOGGLES. Never look at the dot directly.\n\nTurn the laser on?",
            "Laser test fire", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _grbl.SendAsync($"M3 S{s.ToString("0", CultureInfo.InvariantCulture)}");
    }

    private async void LaserOff_Click(object sender, RoutedEventArgs e)
    {
        if (_grbl.IsConnected) await _grbl.SendAsync("M5");
    }

    private bool EnsureConnected()
    {
        if (_grbl.IsConnected) return true;
        MessageBox.Show(this, "Not connected. Select the COM port and press Connect first.",
            "LaserPilot", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    // ==================== Shapes, text, trace, align ====================

    private void AddItem(DesignItem item)
    {
        item.X = (_profile.WidthMm - item.W) / 2;
        item.Y = (_profile.HeightMm - item.H) / 2;
        _items.Add(item);
        _selected = item;
        SyncItemsList();
        SyncPropPanel();
        RedrawCanvas();
    }

    private void Shape_Click(object sender, RoutedEventArgs e)
    {
        if (ShapeBtn.ContextMenu is null) return;
        ShapeBtn.ContextMenu.PlacementTarget = ShapeBtn;
        ShapeBtn.ContextMenu.IsOpen = true;
    }

    private void AddRect_Click(object sender, RoutedEventArgs e) => AddItem(ShapeFactory.Rect());
    private void AddEllipse_Click(object sender, RoutedEventArgs e) => AddItem(ShapeFactory.Ellipse());
    private void AddLine_Click(object sender, RoutedEventArgs e) => AddItem(ShapeFactory.Line());
    private void AddStar_Click(object sender, RoutedEventArgs e) => AddItem(ShapeFactory.Star());

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new TextDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        try
        {
            AddItem(ShapeFactory.Text(dlg.TextValue, dlg.FontValue, dlg.Bold, dlg.Italic, dlg.HeightMm));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not create text: {ex.Message}", "Text", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Trace_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || !_selected.IsRaster || _selected.Gray is null)
        {
            MessageBox.Show(this, "Select an imported image first, then trace it.", "Trace",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new TraceDialog(_selected.Gray, _selected.PxW, _selected.PxH) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        var traced = DesignItem.FromVector($"Traced {_selected.Name}", dlg.Result);
        // Same physical size and position as the source image.
        traced.W = _selected.W;
        traced.H = _selected.H;
        traced.X = _selected.X;
        traced.Y = _selected.Y;
        _items.Add(traced);
        _selected = traced;
        SyncItemsList();
        SyncPropPanel();
        RedrawCanvas();
    }

    private void Align(double? x = null, double? y = null)
    {
        if (_selected is null) return;
        var (bx, by, bw, bh) = _selected.WorkBounds();
        if (x.HasValue) _selected.X += x.Value - bx;
        if (y.HasValue) _selected.Y += y.Value - by;
        SyncPropPanel();
        RedrawCanvas();
    }

    private void AlignL_Click(object sender, RoutedEventArgs e) => Align(x: 0);
    private void AlignCX_Click(object sender, RoutedEventArgs e) { if (_selected is not null) Align(x: (_profile.WidthMm - _selected.WorkBounds().w) / 2); }
    private void AlignR_Click(object sender, RoutedEventArgs e) { if (_selected is not null) Align(x: _profile.WidthMm - _selected.WorkBounds().w); }
    private void AlignB_Click(object sender, RoutedEventArgs e) => Align(y: 0);
    private void AlignCY_Click(object sender, RoutedEventArgs e) { if (_selected is not null) Align(y: (_profile.HeightMm - _selected.WorkBounds().h) / 2); }
    private void AlignT_Click(object sender, RoutedEventArgs e) { if (_selected is not null) Align(y: _profile.HeightMm - _selected.WorkBounds().h); }

    private static DitherMode DitherFromIndex(int i) => i switch
    {
        1 => DitherMode.Jarvis,
        2 => DitherMode.Grayscale,
        3 => DitherMode.Threshold,
        _ => DitherMode.FloydSteinberg,
    };

    private static int IndexFromDither(DitherMode m) => m switch
    {
        DitherMode.Jarvis => 1,
        DitherMode.Grayscale => 2,
        DitherMode.Threshold => 3,
        _ => 0,
    };

    // ==================== Import & items ====================

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "All supported|*.svg;*.dxf;*.png;*.jpg;*.jpeg;*.bmp;*.gif|Vector (SVG, DXF)|*.svg;*.dxf|Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var file in dlg.FileNames)
        {
            try
            {
                var item = DesignItem.FromFile(file);
                // Place near origin; clamp size into the workspace.
                if (item.W > _profile.WidthMm || item.H > _profile.HeightMm)
                {
                    var k = Math.Min(_profile.WidthMm / item.W, _profile.HeightMm / item.H) * 0.9;
                    item.W *= k; item.H *= k;
                }
                item.X = 10; item.Y = 10;
                if (item.IsRaster) { item.Raster = new RasterSettings(); }
                _items.Add(item);
                _selected = item;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not import {Path.GetFileName(file)}:\n{ex.Message}",
                    "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        SyncItemsList();
        SyncPropPanel();
        RedrawCanvas();
    }

    private void SyncItemsList()
    {
        ItemsList.Items.Clear();
        foreach (var it in _items) ItemsList.Items.Add($"{(it.IsRaster ? "🖼" : "✒")} {it.Name}");
        ItemsList.SelectedIndex = _selected is null ? -1 : _items.IndexOf(_selected);
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedIndex >= 0 && ItemsList.SelectedIndex < _items.Count)
        {
            _selected = _items[ItemsList.SelectedIndex];
            SyncPropPanel();
            RedrawCanvas();
        }
    }

    private void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        _items.Remove(_selected);
        _selected = _items.LastOrDefault();
        SyncItemsList();
        SyncPropPanel();
        RedrawCanvas();
    }

    private void Rotate90_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        _selected.RotationDeg = (_selected.RotationDeg + 90) % 360;
        if (_selected.IsRaster) (_selected.W, _selected.H) = (_selected.H, _selected.W);
        SyncPropPanel();
        RedrawCanvas();
    }

    private void CenterItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        _selected.X = (_profile.WidthMm - _selected.W) / 2;
        _selected.Y = (_profile.HeightMm - _selected.H) / 2;
        SyncPropPanel();
        RedrawCanvas();
    }

    private void SyncPropPanel()
    {
        _suppressPropEvents = true;
        try
        {
            if (_selected is null)
            {
                ItemX.Text = ItemY.Text = ItemW.Text = ItemH.Text = ItemRot.Text = "";
                OpSpeed.Text = OpPower.Text = OpPasses.Text = OpInterval.Text = "";
                return;
            }
            ItemX.Text = _selected.X.ToString("0.##", CultureInfo.InvariantCulture);
            ItemY.Text = _selected.Y.ToString("0.##", CultureInfo.InvariantCulture);
            ItemW.Text = _selected.W.ToString("0.##", CultureInfo.InvariantCulture);
            ItemH.Text = _selected.H.ToString("0.##", CultureInfo.InvariantCulture);
            ItemRot.Text = _selected.RotationDeg.ToString("0.#", CultureInfo.InvariantCulture);
            if (_selected.IsRaster)
            {
                OpSpeed.Text = _selected.Raster.SpeedMmMin.ToString("0", CultureInfo.InvariantCulture);
                OpPower.Text = _selected.Raster.MaxPowerPercent.ToString("0", CultureInfo.InvariantCulture);
                OpPasses.Text = "1";
                OpInterval.Text = _selected.Raster.LineIntervalMm.ToString("0.###", CultureInfo.InvariantCulture);
                OpInterval.IsEnabled = true; OpPasses.IsEnabled = false;
                OpDither.SelectedIndex = IndexFromDither(_selected.Raster.Dither);
                OpInvert.IsChecked = _selected.Raster.Invert;
                OpDither.IsEnabled = OpInvert.IsEnabled = true;
            }
            else
            {
                OpSpeed.Text = _selected.Cut.SpeedMmMin.ToString("0", CultureInfo.InvariantCulture);
                OpPower.Text = _selected.Cut.PowerPercent.ToString("0", CultureInfo.InvariantCulture);
                OpPasses.Text = _selected.Cut.Passes.ToString(CultureInfo.InvariantCulture);
                OpInterval.Text = "";
                OpInterval.IsEnabled = false; OpPasses.IsEnabled = true;
                OpDither.SelectedIndex = -1;
                OpInvert.IsChecked = false;
                OpDither.IsEnabled = OpInvert.IsEnabled = false;
            }
        }
        finally { _suppressPropEvents = false; }
    }

    private static double ParseOr(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private void ItemProp_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressPropEvents || _selected is null) return;
        var oldW = _selected.W;
        var oldH = _selected.H;
        _selected.X = ParseOr(ItemX.Text, _selected.X);
        _selected.Y = ParseOr(ItemY.Text, _selected.Y);
        var newW = Math.Max(0.1, ParseOr(ItemW.Text, oldW));
        var newH = Math.Max(0.1, ParseOr(ItemH.Text, oldH));
        if (KeepAspect.IsChecked == true)
        {
            if (Math.Abs(newW - oldW) > 0.001) newH = oldH * (newW / oldW);
            else if (Math.Abs(newH - oldH) > 0.001) newW = oldW * (newH / oldH);
        }
        _selected.W = newW;
        _selected.H = newH;
        _selected.RotationDeg = ParseOr(ItemRot.Text, _selected.RotationDeg);
        if (_selected.IsRaster) _selected.RotationDeg = Math.Round(_selected.RotationDeg / 90) * 90;
        SyncPropPanel();
        RedrawCanvas();
    }

    private void OpProp_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressPropEvents || _selected is null) return;
        if (_selected.IsRaster)
        {
            _selected.Raster.SpeedMmMin = Math.Max(1, ParseOr(OpSpeed.Text, _selected.Raster.SpeedMmMin));
            _selected.Raster.MaxPowerPercent = Math.Clamp(ParseOr(OpPower.Text, _selected.Raster.MaxPowerPercent), 0, 100);
            _selected.Raster.LineIntervalMm = Math.Clamp(ParseOr(OpInterval.Text, _selected.Raster.LineIntervalMm), 0.02, 5);
            if (OpDither.SelectedIndex >= 0) _selected.Raster.Dither = DitherFromIndex(OpDither.SelectedIndex);
            _selected.Raster.Invert = OpInvert.IsChecked == true;
        }
        else
        {
            _selected.Cut.SpeedMmMin = Math.Max(1, ParseOr(OpSpeed.Text, _selected.Cut.SpeedMmMin));
            _selected.Cut.PowerPercent = Math.Clamp(ParseOr(OpPower.Text, _selected.Cut.PowerPercent), 0, 100);
            _selected.Cut.Passes = Math.Max(1, (int)ParseOr(OpPasses.Text, _selected.Cut.Passes));
        }
        SyncPropPanel();
    }

    private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPropEvents || PresetCombo.SelectedItem is not MaterialPreset preset || _selected is null) return;
        if (_selected.IsRaster)
        {
            _selected.Raster.SpeedMmMin = preset.SpeedMmMin;
            _selected.Raster.MaxPowerPercent = preset.PowerPercent;
        }
        else
        {
            _selected.Cut = preset.ToCutSettings();
        }
        SyncPropPanel();
    }

    // ==================== Canvas ====================

    private double MapX(double mmX) => _panX + mmX * _zoom;
    private double MapY(double mmY) => _panY + (_profile.HeightMm - mmY) * _zoom;
    private double UnmapX(double px) => (px - _panX) / _zoom;
    private double UnmapY(double py) => _profile.HeightMm - (py - _panY) / _zoom;

    private void FitView()
    {
        if (WorkCanvas.ActualWidth < 50 || WorkCanvas.ActualHeight < 50) return;
        var kx = (WorkCanvas.ActualWidth - 60) / _profile.WidthMm;
        var ky = (WorkCanvas.ActualHeight - 60) / _profile.HeightMm;
        _zoom = Math.Max(0.05, Math.Min(kx, ky));
        _panX = (WorkCanvas.ActualWidth - _profile.WidthMm * _zoom) / 2;
        _panY = (WorkCanvas.ActualHeight - _profile.HeightMm * _zoom) / 2;
        _viewInitialized = true;
    }

    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_viewInitialized) FitView();
        RedrawCanvas();
    }

    private Ellipse? _laserMarker;

    private void RedrawCanvas()
    {
        WorkCanvas.Children.Clear();
        _laserMarker = null;

        // Workspace border + grid
        var wsRect = new Rectangle
        {
            Width = _profile.WidthMm * _zoom,
            Height = _profile.HeightMm * _zoom,
            Stroke = (Brush)FindResource("PanelBorderBrush"),
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Color.FromRgb(0x1B, 0x1C, 0x21)),
        };
        Canvas.SetLeft(wsRect, MapX(0));
        Canvas.SetTop(wsRect, MapY(_profile.HeightMm));
        WorkCanvas.Children.Add(wsRect);

        var gridBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x27, 0x2E));
        for (double g = 50; g < _profile.WidthMm; g += 50)
            WorkCanvas.Children.Add(new Line { X1 = MapX(g), Y1 = MapY(0), X2 = MapX(g), Y2 = MapY(_profile.HeightMm), Stroke = gridBrush, StrokeThickness = 1 });
        for (double g = 50; g < _profile.HeightMm; g += 50)
            WorkCanvas.Children.Add(new Line { X1 = MapX(0), Y1 = MapY(g), X2 = MapX(_profile.WidthMm), Y2 = MapY(g), Stroke = gridBrush, StrokeThickness = 1 });

        // Origin marker (front-left)
        var origin = new Ellipse { Width = 10, Height = 10, Fill = (Brush)FindResource("OkBrush") };
        Canvas.SetLeft(origin, MapX(0) - 5);
        Canvas.SetTop(origin, MapY(0) - 5);
        WorkCanvas.Children.Add(origin);
        var originLabel = new TextBlock { Text = "origin (front-left)", FontSize = 10, Foreground = (Brush)FindResource("SubtleTextBrush") };
        Canvas.SetLeft(originLabel, MapX(0) + 8);
        Canvas.SetTop(originLabel, MapY(0) + 2);
        WorkCanvas.Children.Add(originLabel);

        // Items
        foreach (var item in _items)
        {
            if (item.IsRaster && item.Preview is not null)
            {
                var img = new Image
                {
                    Source = RotatedPreview(item),
                    Width = item.W * _zoom,
                    Height = item.H * _zoom,
                    Stretch = Stretch.Fill,
                    Opacity = 0.95,
                };
                Canvas.SetLeft(img, MapX(item.X));
                Canvas.SetTop(img, MapY(item.Y + item.H));
                WorkCanvas.Children.Add(img);
            }
            else
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    foreach (var poly in item.TransformedShapes())
                    {
                        if (poly.Points.Count < 2) continue;
                        ctx.BeginFigure(new Point(MapX(poly.Points[0].X), MapY(poly.Points[0].Y)), false, poly.Closed);
                        for (int i = 1; i < poly.Points.Count; i++)
                            ctx.LineTo(new Point(MapX(poly.Points[i].X), MapY(poly.Points[i].Y)), true, false);
                    }
                }
                geo.Freeze();
                WorkCanvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = geo,
                    Stroke = item == _selected ? (Brush)FindResource("AccentBrush") : Brushes.Gainsboro,
                    StrokeThickness = 1.4,
                });
            }

            // Selection outline
            if (item == _selected)
            {
                var (bx, by, bw, bh) = item.WorkBounds();
                var sel = new Rectangle
                {
                    Width = Math.Max(2, bw * _zoom),
                    Height = Math.Max(2, bh * _zoom),
                    Stroke = (Brush)FindResource("AccentBrush"),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                };
                Canvas.SetLeft(sel, MapX(bx));
                Canvas.SetTop(sel, MapY(by + bh));
                WorkCanvas.Children.Add(sel);
            }
        }

        RedrawLaserMarker(_grbl.Status);
    }

    private System.Windows.Media.Imaging.BitmapSource RotatedPreview(DesignItem item)
    {
        var steps = ((int)Math.Round(item.RotationDeg / 90.0) % 4 + 4) % 4;
        if (steps == 0 || item.Preview is null) return item.Preview!;
        // Workspace-CCW rotation == screen-CCW; WPF Rotation enum is clockwise, so invert.
        var rot = steps switch { 1 => 270, 2 => 180, _ => 90 };
        var tb = new System.Windows.Media.Imaging.TransformedBitmap(item.Preview, new RotateTransform(rot));
        tb.Freeze();
        return tb;
    }

    private void RedrawLaserMarker(GrblStatus s)
    {
        if (s.State is MachineState.Disconnected or MachineState.Unknown)
        {
            if (_laserMarker is not null) { WorkCanvas.Children.Remove(_laserMarker); _laserMarker = null; }
            return;
        }
        if (_laserMarker is null)
        {
            _laserMarker = new Ellipse
            {
                Width = 11, Height = 11,
                Stroke = (Brush)FindResource("DangerBrush"),
                StrokeThickness = 2,
                Fill = Brushes.Transparent,
            };
            WorkCanvas.Children.Add(_laserMarker);
        }
        Canvas.SetLeft(_laserMarker, MapX(s.WX) - 5.5);
        Canvas.SetTop(_laserMarker, MapY(s.WY) - 5.5);
    }

    private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var pos = e.GetPosition(WorkCanvas);
        var mmX = UnmapX(pos.X);
        var mmY = UnmapY(pos.Y);
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.05, 40);
        // keep the point under the cursor fixed
        _panX = pos.X - mmX * _zoom;
        _panY = pos.Y - (_profile.HeightMm - mmY) * _zoom;
        RedrawCanvas();
    }

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _lastMouse = e.GetPosition(WorkCanvas);
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            _panning = true;
            WorkCanvas.CaptureMouse();
            return;
        }
        if (e.ChangedButton == MouseButton.Left)
        {
            var mmX = UnmapX(_lastMouse.X);
            var mmY = UnmapY(_lastMouse.Y);
            DesignItem? hit = null;
            foreach (var item in _items) // topmost = last
            {
                var (bx, by, bw, bh) = item.WorkBounds();
                if (mmX >= bx && mmX <= bx + bw && mmY >= by && mmY <= by + bh) hit = item;
            }
            _selected = hit ?? _selected;
            if (hit is not null)
            {
                _draggingItem = true;
                WorkCanvas.CaptureMouse();
            }
            SyncItemsList();
            SyncPropPanel();
            RedrawCanvas();
        }
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(WorkCanvas);
        var dx = pos.X - _lastMouse.X;
        var dy = pos.Y - _lastMouse.Y;
        if (_panning)
        {
            _panX += dx; _panY += dy;
            _lastMouse = pos;
            RedrawCanvas();
        }
        else if (_draggingItem && _selected is not null)
        {
            _selected.X += dx / _zoom;
            _selected.Y -= dy / _zoom;
            _lastMouse = pos;
            SyncPropPanel();
            RedrawCanvas();
        }
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _panning = _draggingItem = false;
        WorkCanvas.ReleaseMouseCapture();
    }

    // ==================== Job ====================

    private List<string>? BuildJobGcode(out string? problem)
    {
        problem = null;
        if (_items.Count == 0) { problem = "Nothing to run — import a design first."; return null; }

        var g = new List<string>(GcodeGenerator.Preamble());
        var maxS = _grbl.GetSetting(30) ?? _profile.MaxPowerS;
        foreach (var item in _items)
        {
            var (bx, by, bw, bh) = item.WorkBounds();
            if (bx < -0.01 || by < -0.01 || bx + bw > _profile.WidthMm + 0.01 || by + bh > _profile.HeightMm + 0.01)
            {
                problem = $"'{item.Name}' extends outside the {_profile.WidthMm:0}×{_profile.HeightMm:0} mm workspace. Move or resize it.";
                return null;
            }
            g.Add($"; --- {item.Name} ---");
            if (item.IsRaster)
            {
                var (gray, w, h) = item.RotatedGray();
                g.AddRange(GcodeGenerator.Raster(gray, w, h, item.X, item.Y, item.W, item.H, item.Raster, maxS));
            }
            else
            {
                g.AddRange(GcodeGenerator.Vector(item.TransformedShapes(), item.Cut, maxS));
            }
        }
        g.AddRange(GcodeGenerator.Postamble());
        return g;
    }

    private async void Frame_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected() || _items.Count == 0) return;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var item in _items)
        {
            var (bx, by, bw, bh) = item.WorkBounds();
            minX = Math.Min(minX, bx); minY = Math.Min(minY, by);
            maxX = Math.Max(maxX, bx + bw); maxY = Math.Max(maxY, by + bh);
        }
        if (!WarnIfNotHomed()) return;
        var frame = GcodeGenerator.Frame(minX, minY, maxX - minX, maxY - minY);
        foreach (var line in frame)
        {
            var r = await _grbl.SendAsync(line);
            if (!r.Ok) { AppendConsole(new LogEntry { Direction = LogDirection.Error, Text = $"Frame aborted: {r.ErrorText}" }); return; }
        }
    }

    private bool WarnIfNotHomed()
    {
        if (_homedThisSession) return true;
        return MessageBox.Show(this,
            "The machine hasn't been homed this session, so it may not know exactly where it is. " +
            "Positions on screen may not match reality, and soft limits can't protect the rails.\n\n" +
            "Recommended: press 🏠 Home (safe) first, or use 'Set origin here'.\n\nContinue anyway?",
            "Machine not homed", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void PreviewGcode_Click(object sender, RoutedEventArgs e)
    {
        var g = BuildJobGcode(out var problem);
        if (g is null)
        {
            MessageBox.Show(this, problem, "G-code", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var win = new Window
        {
            Title = $"G-code preview — {g.Count} lines",
            Owner = this,
            Width = 640, Height = 700,
            Background = (Brush)FindResource("BgBrush"),
        };
        var dock = new DockPanel();
        var save = new Button { Content = "💾 Save .gcode…", Margin = new Thickness(6) };
        save.Click += (_, _) =>
        {
            var dlg = new SaveFileDialog { Filter = "G-code|*.gcode;*.nc|All files|*.*", FileName = "job.gcode" };
            if (dlg.ShowDialog(win) == true) File.WriteAllLines(dlg.FileName, g);
        };
        DockPanel.SetDock(save, Dock.Top);
        dock.Children.Add(save);
        dock.Children.Add(new TextBox
        {
            Text = string.Join(Environment.NewLine, g),
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        win.Content = dock;
        win.ShowDialog();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected()) return;
        var g = BuildJobGcode(out var problem);
        if (g is null)
        {
            MessageBox.Show(this, problem, "Cannot start", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!WarnIfNotHomed()) return;
        var summary = string.Join("\n", _items.Select(i =>
            i.IsRaster
                ? $"  🖼 {i.Name}: engrave {i.W:0}×{i.H:0} mm @ {i.Raster.SpeedMmMin:0} mm/min, {i.Raster.MaxPowerPercent:0}%"
                : $"  ✒ {i.Name}: {i.Cut.Passes}× @ {i.Cut.SpeedMmMin:0} mm/min, {i.Cut.PowerPercent:0}%"));
        if (MessageBox.Show(this,
            $"Ready to start ({g.Count} G-code lines):\n\n{summary}\n\n" +
            "Safety checklist:\n  • Laser goggles on\n  • Material secured\n  • Ventilation running\n  • Never leave the machine unattended\n\nStart the job?",
            "Start job", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            _grbl.StartJob(g);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Start failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_grbl.IsJobPaused) _grbl.ResumeJob(); else _grbl.PauseJob();
    }

    private void StopJob_Click(object sender, RoutedEventArgs e) => _grbl.StopJob();

    private void UpdateJobButtons(JobProgress p)
    {
        JobProgress.Value = p.Percent;
        JobText.Text = p.Running
            ? $"{p.AckedLines}/{p.TotalLines} ({p.Percent:0}%) {(p.Paused ? "— paused" : "")} {p.Elapsed:hh\\:mm\\:ss}"
            : "";
        StartBtn.IsEnabled = !p.Running;
        PauseBtn.IsEnabled = p.Running;
        PauseBtn.Content = p.Paused ? "▶ Resume" : "⏸ Pause";
        StopBtn.IsEnabled = p.Running;
    }

    // ==================== Console ====================

    private void AppendConsole(LogEntry e)
    {
        var prefix = e.Direction switch
        {
            LogDirection.Sent => "→ ",
            LogDirection.Received => "← ",
            LogDirection.Error => "✖ ",
            _ => "ℹ ",
        };
        ConsoleList.Items.Add($"{e.Time:HH:mm:ss} {prefix}{e.Text}");
        while (ConsoleList.Items.Count > 2000) ConsoleList.Items.RemoveAt(0);
        if (ConsoleList.Items.Count > 0)
            ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
    }

    private async void ConsoleSend_Click(object sender, RoutedEventArgs e)
    {
        var cmd = ConsoleInput.Text.Trim();
        if (cmd.Length == 0 || !EnsureConnected()) return;
        ConsoleInput.Clear();
        await _grbl.SendAsync(cmd);
    }

    private void ConsoleInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ConsoleSend_Click(sender, e);
    }
}
