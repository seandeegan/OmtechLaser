using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LaserPilot.App.Models;
using LaserPilot.Core.Comm;
using LaserPilot.Core.Gcode;
using LaserPilot.Core.Geometry;
using Microsoft.Win32;

namespace LaserPilot.App;

public partial class MainWindow
{
    private JobEstimate? _runningEstimate;
    private bool _suppressJobOptionEvents;
    private bool _suppressOverrideEvents;
    private bool _frameLaserConfirmed;
    private readonly RadioButton[] _anchorButtons = new RadioButton[9];

    private static readonly string[] AnchorNames =
        { "Top-left", "Top", "Top-right", "Left", "Center", "Right", "Bottom-left", "Bottom", "Bottom-right" };

    // ==================== Job options ====================

    private void InitJobControls()
    {
        _suppressJobOptionEvents = true;
        for (int i = 0; i < 9; i++)
        {
            var rb = new RadioButton
            {
                GroupName = "JobAnchor",
                ToolTip = $"{AnchorNames[i]} of the design goes under the laser",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0),
            };
            rb.Click += JobOption_Changed;
            _anchorButtons[i] = rb;
            AnchorGrid.Children.Add(rb);
        }
        ApplyJobOptions(new JobOptions
        {
            EngraveBeforeCut = _settings.EngraveBeforeCut,
            AirAssist = _settings.AirAssist,
        });
        FrameLaserCheck.IsChecked = _settings.FrameWithLaser;
        _suppressJobOptionEvents = false;
    }

    private void ApplyJobOptions(JobOptions o)
    {
        _suppressJobOptionEvents = true;
        try
        {
            OriginCombo.SelectedIndex = o.Origin == JobOrigin.CurrentPosition ? 1 : 0;
            EngraveFirstCheck.IsChecked = o.EngraveBeforeCut;
            AirAssistCheck.IsChecked = o.AirAssist;
            _anchorButtons[(int)o.Anchor].IsChecked = true;
            AnchorRow.Visibility = o.Origin == JobOrigin.CurrentPosition ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _suppressJobOptionEvents = false; }
    }

    private JobOptions CurrentJobOptions() => new()
    {
        EngraveBeforeCut = EngraveFirstCheck.IsChecked == true,
        AirAssist = AirAssistCheck.IsChecked == true,
        Origin = OriginCombo.SelectedIndex == 1 ? JobOrigin.CurrentPosition : JobOrigin.Absolute,
        Anchor = (JobAnchor)Math.Max(0, Array.FindIndex(_anchorButtons, b => b?.IsChecked == true)),
    };

    private void JobOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressJobOptionEvents || _anchorButtons[0] is null) return;
        var o = CurrentJobOptions();
        AnchorRow.Visibility = o.Origin == JobOrigin.CurrentPosition ? Visibility.Visible : Visibility.Collapsed;
        _settings.EngraveBeforeCut = o.EngraveBeforeCut;
        _settings.AirAssist = o.AirAssist;
        _settings.FrameWithLaser = FrameLaserCheck.IsChecked == true;
        _settings.Save();
        EstimateText.Text = "";
        if (IsLoaded) RedrawLaserMarker();
    }

    // ==================== Building ====================

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s"
        : $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))} s";

    private enum BuildPurpose { Run, Preview, Export }

    /// <summary>Compiles the design into G-code on a background thread. Shows problems and returns null.</summary>
    private async Task<CompiledJob?> BuildJobAsync(BuildPurpose purpose = BuildPurpose.Run)
    {
        var items = _items.Select(i => i.Clone()).ToList(); // the user may keep editing meanwhile
        var options = CurrentJobOptions();
        Vec2? head = _grbl.IsConnected ? new Vec2(_grbl.Status.WX, _grbl.Status.WY) : null;
        // A file for the MicroSD card can't know where the head will be: always use bed positions.
        // A preview without a connection shows the design where it sits on screen.
        if (options.Origin == JobOrigin.CurrentPosition && (purpose == BuildPurpose.Export || (purpose == BuildPurpose.Preview && head is null)))
            options.Origin = JobOrigin.Absolute;
        var profile = _profile;
        var maxS = MaxPowerS;
        var homed = _homedThisSession;
        var limits = CurrentLimits();

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var job = await Task.Run(() => JobCompiler.Compile(items, profile, options, maxS, head, homed, limits));
            EstimateText.Text = $"⏱ About {FormatDuration(job.Estimate.Total)}  ·  {job.Gcode.Count:N0} G-code lines";
            if (job.Warnings.Count > 0) Warn(string.Join("\n\n", job.Warnings), "Heads up");
            return job;
        }
        catch (Exception ex) when (ex is JobException or InvalidOperationException)
        {
            Warn(ex.Message, "Can't run this job yet");
            return null;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private bool WarnIfNotHomed()
    {
        if (_homedThisSession || !_profile.HasLimitSwitches) return true;
        return Ask("The laser hasn't been homed since it was connected, so it may not know exactly where it is. " +
                   "Your design could land in the wrong place, and the safety limits can't protect the rails.\n\n" +
                   "Recommended: press 🏠 Home first (or choose \"Start from: Current laser position\").\n\nContinue anyway?",
                   "Machine not homed", MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private bool EnsureReadyToMove()
    {
        if (!EnsureConnected() || !EnsureNoJob()) return false;
        var st = _grbl.Status.State;
        if (st == MachineState.Alarm)
        {
            Info("The laser is locked (alarm). Click 🏠 Home (recommended) or 🔓 Unlock first.");
            return false;
        }
        if (st == MachineState.Door)
        {
            Warn("The flame sensor or door switch has stopped the machine. Check for fire and clear the cause first.");
            return false;
        }
        if (st is not (MachineState.Idle or MachineState.Check))
        {
            Info($"The machine is busy ({StateText.Text}). Wait until the status says Ready.");
            return false;
        }
        return true;
    }

    // ==================== Frame / preview ====================

    private bool _framing;
    private bool _startBusy;

    private async void Frame_Click(object sender, RoutedEventArgs e)
    {
        if (_framing) return;
        var enabled = _items.Where(i => i.Enabled).ToList();
        if (enabled.Count == 0) { Info("There's nothing to frame yet — add a design first."); return; }
        if (!EnsureReadyToMove()) return;
        var opts = CurrentJobOptions();
        if (opts.Origin == JobOrigin.Absolute && !WarnIfNotHomed()) return;

        var b = JobCompiler.DesignBounds(enabled)!.Value;
        var offset = opts.Origin == JobOrigin.CurrentPosition
            ? new Vec2(_grbl.Status.WX, _grbl.Status.WY) - JobCompiler.AnchorPoint(b, opts.Anchor)
            : new Vec2(0, 0);
        double x = b.X + offset.X, y = b.Y + offset.Y;
        if ((opts.Origin == JobOrigin.Absolute || _homedThisSession) &&
            (x < -0.05 || y < -0.05 || x + b.W > _profile.WidthMm + 0.05 || y + b.H > _profile.HeightMm + 0.05))
        {
            Warn("The frame would leave the work area. Move the design (or the laser) so it fits on the bed.", "Frame");
            return;
        }

        double powerS = 0;
        if (FrameLaserCheck.IsChecked == true)
        {
            if (!_frameLaserConfirmed && Ask("Framing with a faint laser dot (1% power).\n\nWEAR YOUR LASER GOGGLES. Continue?",
                    "Frame with laser", MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _frameLaserConfirmed = true;
            powerS = 0.01 * MaxPowerS;
        }

        _framing = true;
        FrameBtn.IsEnabled = false;
        try
        {
            foreach (var line in GcodeGenerator.Frame(x, y, b.W, b.H, 3000, powerS))
            {
                if (line.StartsWith(';')) continue;
                var r = await _grbl.SendAsync(line);
                if (!r.Ok)
                {
                    AppendConsole(new LogEntry { Direction = LogDirection.Error, Text = $"Frame stopped: {r.ErrorText}" });
                    // The closing M5 was never sent: make sure the dot is off.
                    if (powerS > 0 && _grbl.IsConnected && !_grbl.IsJobRunning) await _grbl.SendAsync("M5");
                    break;
                }
            }
        }
        finally
        {
            _framing = false;
            FrameBtn.IsEnabled = !_grbl.IsJobRunning;
        }
    }

    private async void PreviewGcode_Click(object sender, RoutedEventArgs e)
    {
        var job = await BuildJobAsync(BuildPurpose.Preview);
        if (job is null) return;
        var win = new PreviewWindow(job.Gcode, job.Estimate, _profile, MaxPowerS, "Job preview",
            onStart: () => Start_Click(this, new RoutedEventArgs())) { Owner = this };
        win.ShowDialog();
    }

    // ==================== Run ====================

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_startBusy) return; // a second click while the first is still being prepared
        _startBusy = true;
        try { await StartJobAsync(); }
        finally { _startBusy = false; }
    }

    private async Task StartJobAsync()
    {
        if (!EnsureReadyToMove()) return;
        var opts = CurrentJobOptions();
        if (opts.Origin == JobOrigin.Absolute && !WarnIfNotHomed()) return;
        var job = await BuildJobAsync();
        if (job is null || !EnsureReadyToMove()) return;

        if (_grbl.GetSetting(32) == 0 &&
            Ask("Laser mode is OFF ($32=0) on the controller. Engravings will be uneven and corners will over-burn.\n\nTurn laser mode on now? (Recommended)",
                "Laser mode", MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            var r = await _grbl.WriteSettingAsync(32, 1);
            if (!r.Ok) { Warn($"The controller refused: {r.ErrorText}"); return; }
        }

        var summary = string.Join("\n", job.Items.Take(12).Select(i => $"  {(i.IsRaster ? "🖼" : "✒")} {i.Name}: {i.Summary}"));
        if (job.Items.Count > 12) summary += $"\n  … and {job.Items.Count - 12} more";
        var where = job.PlacedAtHead is not null
            ? $"\nStarting from the laser's current position ({AnchorNames[(int)CurrentJobOptions().Anchor].ToLowerInvariant()} of the design).\n"
            : "";
        if (Ask($"Ready to start — about {FormatDuration(job.Estimate.Total)}.\n{where}\n{summary}\n\n" +
                "Safety checklist:\n" +
                "  • Laser goggles ON (everyone in the room)\n" +
                "  • Material flat, secured and focused\n" +
                "  • Air assist / ventilation running\n" +
                "  • Fire extinguisher nearby — NEVER leave the laser running alone\n\n" +
                "Start the job?", "Start job") != MessageBoxResult.Yes) return;

        // Things can change while the question is open: an alarm, the flame sensor, a pulled cable.
        if (!EnsureReadyToMove()) return;
        if (job.PlacedAtHead is { } placedAt &&
            (Math.Abs(_grbl.Status.WX - placedAt.X) > 0.05 || Math.Abs(_grbl.Status.WY - placedAt.Y) > 0.05))
        {
            Info("The laser moved after the job was prepared, so the design would no longer line up with it.\n\nPress START again.", "Start job");
            return;
        }
        StartStreaming(job.Gcode, job.Estimate);
    }

    private void StartStreaming(IReadOnlyList<string> gcode, JobEstimate estimate)
    {
        StopFocusLaserTimer(sendOff: false); // the job begins with M5 anyway
        try
        {
            ResetOverrideSliders();
            _runningEstimate = estimate;
            _grbl.StartJob(gcode);
            HideAlert();
        }
        catch (Exception ex)
        {
            _runningEstimate = null;
            Warn(ex.Message, "Start failed");
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_grbl.IsJobPaused)
        {
            HideAlert("job"); // a "line rejected" notice is answered by resuming
            _grbl.ResumeJob();
        }
        else _grbl.PauseJob();
    }

    private async void StopJob_Click(object sender, RoutedEventArgs e)
    {
        StopBtn.IsEnabled = false;
        await _grbl.StopJobAsync();
    }

    private void UpdateJobButtons(JobProgress p)
    {
        JobProgress.Value = p.Percent;
        if (p.Running)
        {
            var remaining = _runningEstimate?.RemainingAfter(p.AckedLines);
            JobText.Text = p.Finishing
                ? "Finishing the last moves…"
                : $"{p.Percent:0}%  ·  {p.Elapsed:hh\\:mm\\:ss}" +
                  (remaining is { } r && !p.Paused ? $"  ·  about {FormatDuration(r)} left" : "") +
                  (p.Paused ? "  ·  PAUSED" : "") +
                  (p.Errors > 0 ? $"  ·  {p.Errors} rejected line(s)" : "");
        }
        else JobText.Text = "";
        StartBtn.IsEnabled = !p.Running;
        FrameBtn.IsEnabled = !p.Running;
        PauseBtn.IsEnabled = p.Running;
        PauseBtn.Content = p.Paused ? "▶ Resume" : "⏸ Pause";
        StopBtn.IsEnabled = p.Running;
        OverridePanel.IsEnabled = p.Running;
    }

    private void OnJobFinished(JobResult r)
    {
        _runningEstimate = null;
        ResetOverrideSliders();
        if (r.Completed)
        {
            System.Media.SystemSounds.Asterisk.Play();
            ShowAlert($"✅ Job finished in {FormatDuration(r.Elapsed)}." +
                      (r.Errors > 0 ? $" {r.Errors} line(s) were rejected by the controller — see the console." : ""), AlertKind.Info);
        }
        else if (r.Reason is "stopped by user" or "emergency stop" or "stopped")
        {
            ShowAlert($"⏹ Job {(r.Reason == "emergency stop" ? "halted by the emergency stop" : "stopped")} after {FormatDuration(r.Elapsed)}.", AlertKind.Info);
        }
        else if (r.Reason != "disconnected")
        {
            System.Media.SystemSounds.Hand.Play();
            ShowAlert($"⚠ The job ended early: {r.Reason} ({r.AckedLines:N0} of {r.TotalLines:N0} lines done).", AlertKind.Warn);
        }
    }

    // ==================== Live overrides ====================

    private void ResetOverrideSliders()
    {
        _suppressOverrideEvents = true;
        FeedOverrideSlider.Value = 100;
        PowerOverrideSlider.Value = 100;
        FeedOverrideText.Text = PowerOverrideText.Text = "100%";
        _suppressOverrideEvents = false;
    }

    private void FeedOverride_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FeedOverrideText is null) return;
        FeedOverrideText.Text = $"{e.NewValue:0}%";
        if (!_suppressOverrideEvents && _grbl.IsJobRunning) _ = _grbl.SetFeedOverrideAsync((int)e.NewValue);
    }

    private void PowerOverride_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PowerOverrideText is null) return;
        PowerOverrideText.Text = $"{e.NewValue:0}%";
        if (!_suppressOverrideEvents && _grbl.IsJobRunning) _ = _grbl.SetPowerOverrideAsync((int)e.NewValue);
    }

    private void OverrideReset_Click(object sender, RoutedEventArgs e)
    {
        FeedOverrideSlider.Value = 100;
        PowerOverrideSlider.Value = 100;
    }

    // ==================== Files & tools ====================

    private async void ExportGcode_Click(object sender, RoutedEventArgs e)
    {
        var job = await BuildJobAsync(BuildPurpose.Export);
        if (job is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "G-code (*.gcode)|*.gcode|G-code (*.nc)|*.nc|All files|*.*",
            FileName = (_projectPath is null ? "job" : Path.GetFileNameWithoutExtension(_projectPath)) + ".gcode",
            Title = "Export G-code",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllLines(dlg.FileName, job.Gcode);
            Info($"Saved {job.Gcode.Count:N0} lines (about {FormatDuration(job.Estimate.Total)} of work).\n\n" +
                 "To run it without a computer: copy the file onto the laser's MicroSD card and start it from the machine. " +
                 "Home the laser first so the design lands in the same place as on screen.", "G-code exported");
        }
        catch (Exception ex)
        {
            Warn($"Could not save the file:\n{ex.Message}");
        }
    }

    private void RunGcodeFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "G-code|*.gcode;*.nc;*.gc;*.ngc;*.txt|All files|*.*", Title = "Run a G-code file" };
        if (dlg.ShowDialog(this) == true) RunGcodeFile(dlg.FileName);
    }

    private void RunGcodeFile(string path)
    {
        List<string> lines;
        try { lines = File.ReadAllLines(path).ToList(); }
        catch (Exception ex) { Warn($"Could not read the file:\n{ex.Message}"); return; }

        JobEstimate estimate;
        try { estimate = JobEstimator.Estimate(lines, CurrentLimits()); }
        catch (Exception ex)
        {
            Warn($"LaserPilot couldn't understand this G-code file:\n{ex.Message}", "Run G-code file");
            return;
        }
        var e = estimate.Extents;
        string bounds = double.IsNaN(e.MinX) ? "" : $"\n\nIt moves within X {e.MinX:0.#}…{e.MaxX:0.#} mm, Y {e.MinY:0.#}…{e.MaxY:0.#} mm.";
        bool outside = !double.IsNaN(e.MinX) &&
                       (e.MinX < -0.05 || e.MinY < -0.05 || e.MaxX > _profile.WidthMm + 0.05 || e.MaxY > _profile.HeightMm + 0.05);

        var win = new PreviewWindow(lines, estimate, _profile, MaxPowerS, Path.GetFileName(path), onStart: () =>
        {
            if (!EnsureReadyToMove()) return;
            if (outside && Ask($"This file moves outside the {_profile.WidthMm:0}×{_profile.HeightMm:0} mm work area.{bounds}\n\nRun it anyway?",
                    "Outside the work area", MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (!WarnIfNotHomed()) return;
            if (Ask($"Run {Path.GetFileName(path)} (about {FormatDuration(estimate.Total)})?{bounds}\n\n" +
                    "LaserPilot can't check files made by other programs as carefully as its own jobs — make sure it was made for this laser.\n\n" +
                    "Goggles on, material secured, never leave it running alone.", "Run G-code file", MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            StartStreaming(lines, estimate);
        }) { Owner = this };
        win.ShowDialog();
    }

    private void MaterialTest_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new MaterialTestDialog(_profile) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Options is not { } o) return;
        var items = MaterialTest.Generate(o);
        if (items.Count == 0) return;
        if (dlg.ReplaceDesign)
        {
            if (!ConfirmDiscardChanges()) return;
            PushUndo(); // one step: Undo brings the old design back
            _items = new List<DesignItem>(items);
            _projectPath = null; // Save must not overwrite the old project with the test grid
            MarkDirty();
            SetSelection(items, items[^1]);
        }
        else AddItems(items, centerOnBed: false);
        if (dlg.RunAtLaserPosition)
            ApplyJobOptions(new JobOptions
            {
                Origin = JobOrigin.CurrentPosition,
                Anchor = JobAnchor.BottomLeft,
                EngraveBeforeCut = EngraveFirstCheck.IsChecked == true,
                AirAssist = AirAssistCheck.IsChecked == true,
            });
        RedrawLaserMarker();
        ShowAlert($"🧪 Material test added ({o.Rows}×{o.Columns} squares). Put a scrap of your material under the laser, focus, " +
                  "then Frame to check it fits and press START. Afterwards pick the best-looking square and use its speed/power.",
            AlertKind.Info);
    }
}
