using LaserPilot.Core.Comm;
using LaserPilot.Core.Machines;

namespace LaserPilot.Core.Safety;

public enum CheckResult { Pass, Warning, Fail, Info }

public sealed record HomingCheck(CheckResult Result, string Title, string Detail);

public sealed record SafeHomeResult(bool Success, string Message, int AlarmCode = 0);

/// <summary>
/// Pre-flight diagnostics and a supervised homing cycle. The goal: never let
/// the machine grind into its rails. We check configuration before homing,
/// compute a hard time budget for the cycle, and abort with a soft reset the
/// moment it exceeds that budget.
/// </summary>
public sealed class HomingSafetyService
{
    private readonly GrblController _grbl;

    public HomingSafetyService(GrblController grbl) => _grbl = grbl;

    /// <summary>Run configuration checks before allowing $H.</summary>
    public async Task<List<HomingCheck>> DiagnoseAsync(MachineProfile profile)
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
        var recommendedDir = profile.RecommendedSettings.TryGetValue(23, out var rd) ? rd : double.NaN;
        if (!double.IsNaN(recommendedDir) && dir != recommendedDir)
        {
            checks.Add(new(CheckResult.Fail, "Homing direction ($23)",
                $"Current value is {dir:0}, but this machine's switches are at the {DescribeCorner(profile.Origin)} — expected $23={recommendedDir:0}. " +
                "With the wrong direction the head drives AWAY from the switches and bangs into the opposite rail. Fix this before homing."));
        }
        else
        {
            checks.Add(new(CheckResult.Pass, "Homing direction ($23)",
                $"$23={dir:0} — homing toward the {DescribeCorner(profile.Origin)} corner where the switches are."));
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
    /// Watch limit pins while the user manually presses each switch.
    /// Returns which axes were seen triggering within the time window.
    /// </summary>
    public async Task<(bool xSeen, bool ySeen)> LimitSwitchTestAsync(TimeSpan window, Action<string>? onPinChange = null, CancellationToken ct = default)
    {
        bool x = false, y = false;
        string lastPins = "";
        var end = DateTime.UtcNow + window;
        while (DateTime.UtcNow < end && !ct.IsCancellationRequested && !(x && y))
        {
            _grbl.QueryStatus();
            await Task.Delay(100, ct).ConfigureAwait(false);
            var pins = _grbl.Status.Pins;
            if (pins != lastPins)
            {
                lastPins = pins;
                onPinChange?.Invoke(pins);
            }
            if (_grbl.Status.XLimitActive) x = true;
            if (_grbl.Status.YLimitActive) y = true;
        }
        return (x, y);
    }

    /// <summary>
    /// Supervised homing: compute the worst-case legitimate cycle time from
    /// the machine's own settings, run $H, and hard-abort (soft reset) if it
    /// exceeds the budget — so a wrong direction can't grind for long.
    /// </summary>
    public async Task<SafeHomeResult> SafeHomeAsync(MachineProfile profile, CancellationToken ct = default)
    {
        if (!_grbl.IsConnected) return new(false, "Not connected.");

        double travel = Math.Max(_grbl.GetSetting(130) ?? profile.WidthMm,
                                 _grbl.GetSetting(131) ?? profile.HeightMm);
        double seek = _grbl.GetSetting(25) ?? 1000;
        double feed = _grbl.GetSetting(24) ?? 100;
        if (seek <= 0) seek = 1000;
        if (feed <= 0) feed = 100;

        // GRBL searches up to 1.5× max travel at seek rate, then locates at
        // feed rate over the pull-off region, per axis pair. Budget generously
        // but finitely: search + locate + margin, ×2 axes, +10 s fixed.
        double seconds = 2 * ((travel * 1.5) / seek * 60 + 10.0 / feed * 60) + 10;
        var budget = TimeSpan.FromSeconds(Math.Clamp(seconds, 15, 180));

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
                    $"Homing did not complete within the expected {budget.TotalSeconds:0} s, so LaserPilot stopped the machine (soft reset). " +
                    "This usually means the head was moving in the WRONG direction (check $23) or a limit switch is not being detected. " +
                    "Run the limit switch test in the Setup Wizard.");
            }
            var r = await homeTask.ConfigureAwait(false);
            if (r.Ok) return new(true, "Homing completed successfully. Machine position is now known.");

            if (alarmCode != 0)
                return new(false, HomingFailureAdvice(alarmCode), alarmCode);
            return new(false, $"Homing failed: {r.ErrorText}");
        }
        finally
        {
            _grbl.AlarmRaised -= OnAlarm;
        }
    }

    public static string HomingFailureAdvice(int alarmCode) => alarmCode switch
    {
        6 => "Homing was cancelled/reset before completing (ALARM 6). If you pressed stop, that's expected. Otherwise check the USB connection.",
        8 => "The head could not pull off the limit switch after triggering it (ALARM 8). Increase the pull-off distance ($27, try 2–3 mm) and check the switch isn't sticking.",
        9 => "GRBL could not find a limit switch within the search distance (ALARM 9). Most likely causes, in order:\n" +
             "  1. Homing direction ($23) is wrong — the head moved AWAY from the switches. For the OMTech B10 use $23=3 (front-left).\n" +
             "  2. A limit switch is unplugged or faulty — run the limit switch test.\n" +
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
