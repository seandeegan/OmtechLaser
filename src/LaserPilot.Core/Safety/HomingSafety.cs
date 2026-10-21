using LaserPilot.Core.Comm;
using LaserPilot.Core.Machines;

namespace LaserPilot.Core.Safety;

public enum CheckResult { Pass, Warning, Fail, Info }

public sealed record HomingCheck(CheckResult Result, string Title, string Detail);

public sealed record SafeHomeResult(bool Success, string Message, int AlarmCode = 0);

/// <summary>Outcome of pressing the switches by hand. "Stuck" = reported as pressed the whole time.</summary>
public sealed record LimitTestResult(bool XSeen, bool YSeen, bool XStuck, bool YStuck)
{
    public bool BothWork => XSeen && YSeen && !XStuck && !YStuck;
}

/// <summary>
/// Pre-flight diagnostics and a supervised homing cycle. The goal: never let
/// the machine grind into its rails. We check configuration before homing,
/// compute a hard time budget for the cycle, and abort with a soft reset the
/// moment it exceeds that budget.
/// </summary>
public sealed class HomingSafetyService
{
    /// <summary>Seek speed (mm/min) for a machine's first, unproven homing: a missed switch bumps gently.</summary>
    public const double GentleSeekMmMin = 600;

    private readonly GrblController _grbl;

    public HomingSafetyService(GrblController grbl) => _grbl = grbl;

    /// <summary>The $23 value matching where this machine's switches are (user choice wins over the profile).</summary>
    public static double ExpectedHomingDirection(MachineProfile profile, int? userDirectionMask) =>
        userDirectionMask ?? (profile.RecommendedSettings.TryGetValue(23, out var rd) ? rd : double.NaN);

    /// <summary>Run configuration checks before allowing $H.</summary>
    public async Task<List<HomingCheck>> DiagnoseAsync(MachineProfile profile, int? userDirectionMask = null)
    {
        var checks = new List<HomingCheck>();

        if (!_grbl.IsConnected)
        {
            checks.Add(new(CheckResult.Fail, "Connection", "Not connected to the laser."));
            return checks;
        }

        if (!await _grbl.RefreshSettingsAsync().ConfigureAwait(false))
        {
            checks.Add(new(CheckResult.Fail, "GRBL settings", "Could not read GRBL settings ($$). If the machine is in an alarm state, unlock it first ($X)."));
            return checks;
        }
        checks.Add(new(CheckResult.Pass, "GRBL settings", "Settings read successfully."));

        double Get(int n) => _grbl.GetSetting(n) ?? double.NaN;

        // Homing enabled?
        var homingEnabled = Get(22) == 1;
        if (!profile.HasLimitSwitches)
        {
            checks.Add(homingEnabled
                ? new(CheckResult.Fail, "Homing enabled without switches",
                    "This machine profile has no limit switches, but homing ($22) is enabled in GRBL. Running $H would drive the head into the rails. Disable $22 or switch to a profile with limit switches.")
                : new(CheckResult.Pass, "Homing", "Homing disabled, matching a machine without limit switches. Use 'Set origin here' instead of $H."));
            return checks;
        }

        checks.Add(homingEnabled
            ? new(CheckResult.Pass, "Homing cycle ($22)", "Enabled.")
            : new(CheckResult.Warning, "Homing cycle ($22)", "Disabled. $H will be rejected (error 5). Enable it once the direction checks below pass."));

        // Homing direction
        var dir = Get(23);
        var recommendedDir = ExpectedHomingDirection(profile, userDirectionMask);
        var corner = DescribeHomingCorner(recommendedDir, profile.Origin);
        if (double.IsNaN(dir))
        {
            checks.Add(new(CheckResult.Fail, "Homing direction ($23)",
                "The controller did not report $23, so the homing direction can't be verified. Re-read the settings or run the Setup Wizard."));
        }
        else if (!double.IsNaN(recommendedDir) && dir != recommendedDir)
        {
            checks.Add(new(CheckResult.Fail, "Homing direction ($23)",
                $"Current value is {dir:0}, but this machine's switches are at the {corner} — expected $23={recommendedDir:0}. " +
                "With the wrong direction the head drives AWAY from the switches and bangs into the opposite rail. Fix this before homing."));
        }
        else
        {
            checks.Add(new(CheckResult.Pass, "Homing direction ($23)",
                $"$23={dir:0} — homing toward the {corner} corner where the switches are."));
        }

        // Travel sizes drive the homing search distance (1.5× max travel).
        CheckNear(checks, 130, "X max travel", Get(130), profile.WidthMm);
        CheckNear(checks, 131, "Y max travel", Get(131), profile.HeightMm);

        // Seek rate sanity
        var seek = Get(25);
        if (seek > 4000)
            checks.Add(new(CheckResult.Warning, "Homing seek rate ($25)",
                $"{seek:0} mm/min is fast for homing. If anything is misconfigured the impact will be harder. 1000–2000 is safer."));
        else
            checks.Add(new(CheckResult.Pass, "Homing seek rate ($25)", $"{seek:0} mm/min."));

        // Limit pin state right now
        _grbl.QueryStatus();
        await Task.Delay(400).ConfigureAwait(false);
        var st = _grbl.Status;
        if (st.XLimitActive || st.YLimitActive)
        {
            checks.Add(new(CheckResult.Warning, "Limit switch state",
                $"Limit input(s) currently active: {st.Pins}. If the head is NOT physically parked at the home corner, the switches are probably wired normally-closed and GRBL expects normally-open — set $5=1 (or the head really is sitting on a switch, which is fine)."));
        }
        else
        {
            checks.Add(new(CheckResult.Pass, "Limit switch state", "No limit inputs active (head is away from the switches)."));
        }

        return checks;
    }

    /// <summary>
    /// Watch limit pins while the user manually presses each switch. A switch
    /// only counts once it has been seen released and then pressed — an input
    /// that reads "pressed" the whole time (normally-closed wiring with $5=0,
    /// or a short) is reported as stuck instead of passing the test.
    /// </summary>
    public async Task<LimitTestResult> LimitSwitchTestAsync(TimeSpan window, Action<string>? onPinChange = null, CancellationToken ct = default)
    {
        bool x = false, y = false, xReleased = false, yReleased = false, sampled = false;
        string? lastPins = null;
        var end = DateTime.UtcNow + window;
        while (DateTime.UtcNow < end && !ct.IsCancellationRequested && !(x && y))
        {
            _grbl.QueryStatus();
            await Task.Delay(100, ct).ConfigureAwait(false);
            var st = _grbl.Status;
            if (st.Pins != lastPins)
            {
                lastPins = st.Pins;
                onPinChange?.Invoke(st.Pins);
            }
            sampled = true;
            if (st.XLimitActive) { if (xReleased) x = true; } else xReleased = true;
            if (st.YLimitActive) { if (yReleased) y = true; } else yReleased = true;
        }
        return new LimitTestResult(x, y, XStuck: sampled && !xReleased, YStuck: sampled && !yReleased);
    }

    /// <summary>
    /// The longest a CORRECT homing cycle can take, following GRBL 1.1's
    /// sequence per homing cycle: seek toward the switch (at worst the whole
    /// axis length), pull off, re-approach slowly, pull off again, with a
    /// debounce pause after each phase. Axes homed one after the other take
    /// twice as long as axes homed together.
    /// Note GRBL itself gives up after searching 1.5× the axis length, so at
    /// full seek speed this budget is only a little shorter than GRBL's own
    /// limit; the real protection is testing the switches before homing.
    /// </summary>
    public static TimeSpan HomingTimeBudget(double travelMm, double seekMmMin, double feedMmMin,
        double pullOffMm, double debounceMs, bool xyTogether)
    {
        if (!(seekMmMin > 0)) seekMmMin = 1000;
        if (!(feedMmMin > 0)) feedMmMin = 100;
        if (!(pullOffMm >= 0)) pullOffMm = 2;
        if (!(debounceMs >= 0)) debounceMs = 250;
        double seek = (travelMm + 10) / seekMmMin * 60;            // from the far end, plus a little slack
        double locate = (pullOffMm + 2) / feedMmMin * 60;          // back onto the switch slowly (pull-off + switch travel)
        double pullOffs = 2 * pullOffMm / seekMmMin * 60;
        double debounce = 4 * debounceMs / 1000;
        double cycle = seek + locate + pullOffs + debounce;
        double total = cycle * (xyTogether ? 1 : 2) + 2;           // +2 s for the command round trip
        return TimeSpan.FromSeconds(Math.Clamp(total, 10, 300));
    }

    /// <summary>
    /// Supervised homing: compute the longest a correct cycle can take from
    /// the machine's own settings, run $H, and hard-abort (soft reset) if it
    /// takes longer.
    /// </summary>
    /// <param name="gentleSeekMmMin">
    /// For a first, unproven homing: temporarily lower the seek speed ($25) to
    /// this, so a missed switch bumps gently. The old value is put back afterwards.
    /// </param>
    public async Task<SafeHomeResult> SafeHomeAsync(MachineProfile profile, double? gentleSeekMmMin = null, CancellationToken ct = default)
    {
        if (!_grbl.IsConnected) return new(false, "Not connected.");

        double travel = Math.Max(_grbl.GetSetting(130) ?? profile.WidthMm,
                                 _grbl.GetSetting(131) ?? profile.HeightMm);
        double seek = _grbl.GetSetting(25) ?? 1000;
        if (seek <= 0) seek = 1000;

        double? restoreSeek = null;
        if (gentleSeekMmMin is { } gentle && gentle > 0 && seek > gentle)
        {
            if (!(await _grbl.WriteSettingAsync(25, gentle).ConfigureAwait(false)).Ok)
                return new(false, "Couldn't set the gentle homing speed ($25), so homing was not started. Check the connection and try again.");
            restoreSeek = seek;
            seek = gentle;
        }

        var budget = HomingTimeBudget(travel, seek, _grbl.GetSetting(24) ?? 100, _grbl.GetSetting(27) ?? 2,
            _grbl.GetSetting(26) ?? 250, profile.HomesXYTogether);

        int alarmCode = 0;
        void OnAlarm(int code, string _) => alarmCode = code;
        _grbl.AlarmRaised += OnAlarm;
        try
        {
            var homeTask = _grbl.HomeAsync((int)budget.TotalMilliseconds + 5000);
            var done = await Task.WhenAny(homeTask, Task.Delay(budget, ct)).ConfigureAwait(false);
            if (done != homeTask)
            {
                _grbl.SoftReset();
                return new(false,
                    $"Homing took longer than a correct cycle can ({budget.TotalSeconds:0} s), so LaserPilot stopped the machine. " +
                    "The head almost certainly reached the end of a rail without a limit switch registering.\n\n" +
                    "Check where your switches really are and run the limit switch test in the Setup Wizard (step 3) before homing again.");
            }
            var r = await homeTask.ConfigureAwait(false);
            if (r.Ok)
            {
                var origin = await EnsureBedOriginAsync(profile).ConfigureAwait(false);
                return new(true, "Homing completed successfully. Machine position is now known." +
                                 (origin.Length > 0 ? "\n\n" + origin : ""));
            }

            if (alarmCode != 0)
                return new(false, HomingFailureAdvice(alarmCode), alarmCode);
            return new(false, $"Homing failed: {r.ErrorText}");
        }
        finally
        {
            _grbl.AlarmRaised -= OnAlarm;
            if (restoreSeek is { } normal) await RestoreSettingAsync(25, normal).ConfigureAwait(false);
        }
    }

    // After a reset GRBL restarts and drops commands for a moment: retry a few times.
    private async Task RestoreSettingAsync(int setting, double value)
    {
        for (int attempt = 0; attempt < 5 && _grbl.IsConnected; attempt++)
        {
            if ((await _grbl.WriteSettingAsync(setting, value).ConfigureAwait(false)).Ok) return;
            await Task.Delay(750).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The G54 offset for one axis that puts work 0 at the bed's minimum edge
    /// (front / left), given where homing left the machine. Stock GRBL uses an
    /// all-negative machine space [−travel, 0]. Builds with
    /// HOMING_FORCE_SET_ORIGIN call the home position 0 instead, which gives
    /// [0, +travel] when homing toward the minimum end.
    /// </summary>
    public static double BedOffsetForAxis(bool homesTowardMinimum, double machinePosAfterHoming, double travel)
    {
        if (homesTowardMinimum && machinePosAfterHoming > -travel / 2) return 0; // force-origin build
        return -travel;
    }

    /// <summary>
    /// After homing, make work (0,0) the bed's front-left corner so designs on
    /// the screen land in the same place on the bed. Writes G54 only when it
    /// is wrong (it lives in EEPROM). Returns a message when something changed.
    /// </summary>
    public async Task<string> EnsureBedOriginAsync(MachineProfile profile)
    {
        if (!_grbl.IsConnected || !profile.HasLimitSwitches) return "";
        _grbl.QueryStatus();
        await Task.Delay(350).ConfigureAwait(false);
        if (!await _grbl.RefreshOffsetsAsync().ConfigureAwait(false)) return "";
        var g54 = _grbl.GetOffset("G54");
        var g92 = _grbl.GetOffset("G92") ?? new double[] { 0, 0, 0 };
        if (g54 is null || g54.Length < 2 || g92.Length < 2) return "";

        // Without the real homing direction and travel, a "fix" could move the origin off the bed.
        if (_grbl.GetSetting(23) is not { } dirSetting || _grbl.GetSetting(130) is not { } travelX ||
            _grbl.GetSetting(131) is not { } travelY || travelX <= 0 || travelY <= 0)
            return "";
        var st = _grbl.Status;
        double mx = st.HasMachinePosition ? st.MX : st.WX + g54[0] + g92[0];
        double my = st.HasMachinePosition ? st.MY : st.WY + g54[1] + g92[1];
        int dir = (int)dirSetting;
        double wantX = BedOffsetForAxis((dir & 1) != 0, mx, travelX);
        double wantY = BedOffsetForAxis((dir & 2) != 0, my, travelY);

        var changes = new List<string>();
        if (Math.Abs(g92[0]) > 1e-6 || Math.Abs(g92[1]) > 1e-6)
        {
            // G92 offsets are temporary and would shift every job; clear them.
            if ((await _grbl.SendAsync("G92.1").ConfigureAwait(false)).Ok) changes.Add("cleared a temporary G92 offset");
        }
        if (Math.Abs(g54[0] - wantX) > 0.01 || Math.Abs(g54[1] - wantY) > 0.01)
        {
            var cmd = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"G10 L2 P1 X{wantX:0.###} Y{wantY:0.###}");
            if ((await _grbl.SendAsync(cmd).ConfigureAwait(false)).Ok)
                changes.Add($"set the work origin (G54) to the bed's front-left corner: X{wantX:0.###} Y{wantY:0.###}");
        }
        return changes.Count == 0 ? "" : "LaserPilot " + string.Join(" and ", changes) + ".";
    }

    public static string DescribeHomingCorner(double dirMask, OriginCorner fallback)
    {
        if (double.IsNaN(dirMask)) return DescribeCorner(fallback);
        int m = (int)dirMask;
        var x = (m & 1) != 0 ? "left" : "right";
        var y = (m & 2) != 0 ? "front" : "rear";
        return $"{y}-{x}";
    }

    public static string HomingFailureAdvice(int alarmCode) => alarmCode switch
    {
        6 => "Homing was cancelled/reset before completing (ALARM 6). If you pressed stop, that's expected. Otherwise check the USB connection.",
        8 => "The head could not pull off the limit switch after triggering it (ALARM 8). Increase the pull-off distance ($27, try 2–3 mm) and check the switch isn't sticking.",
        9 => "GRBL could not find a limit switch within the search distance (ALARM 9). Most likely causes, in order:\n" +
             "  1. The head homed toward a corner without switches — the homing direction ($23) must match the corner your switches are in. Set it in the Setup Wizard, step 3.\n" +
             "  2. A limit switch is unplugged or faulty — run the limit switch test (Setup Wizard, step 3).\n" +
             "  3. Max travel ($130/$131) is set far too small, so the search gives up early.",
        7 => "The safety door input opened during homing (ALARM 7). Check the door switch / lid sensor wiring.",
        _ => $"Homing failed with ALARM {alarmCode}: {GrblCodes.AlarmText(alarmCode)}",
    };

    private static void CheckNear(List<HomingCheck> checks, int setting, string name, double actual, double expected)
    {
        if (double.IsNaN(actual))
            checks.Add(new(CheckResult.Warning, $"{name} (${setting})", "Value not reported by controller."));
        else if (Math.Abs(actual - expected) > 1)
            checks.Add(new(CheckResult.Warning, $"{name} (${setting})",
                $"Controller reports {actual:0} mm but this machine's work area is {expected:0} mm. Soft limits and the homing search distance depend on this value."));
        else
            checks.Add(new(CheckResult.Pass, $"{name} (${setting})", $"{actual:0} mm."));
    }

    private static string DescribeCorner(OriginCorner c) => c switch
    {
        OriginCorner.FrontLeft => "front-left",
        OriginCorner.FrontRight => "front-right",
        OriginCorner.RearLeft => "rear-left",
        OriginCorner.RearRight => "rear-right",
        _ => "?",
    };
}
