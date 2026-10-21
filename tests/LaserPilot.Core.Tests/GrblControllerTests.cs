using System.Globalization;
using LaserPilot.Core.Comm;
using LaserPilot.Core.Machines;
using LaserPilot.Core.Safety;

namespace LaserPilot.Core.Tests;

[TestClass]
public sealed class GrblControllerTests
{
    private static (GrblController grbl, FakeGrbl fake) Connect(Action<FakeGrbl>? configure = null)
    {
        var fake = new FakeGrbl();
        configure?.Invoke(fake);
        var grbl = new GrblController((_, _) => fake);
        grbl.Connect("SIM");
        return (grbl, fake);
    }

    private static List<string> MakeJob(int lines)
    {
        var rnd = new Random(42);
        var g = new List<string> { "G21", "G90", "M4 S0" };
        for (int i = 0; i < lines; i++)
            g.Add(string.Create(CultureInfo.InvariantCulture,
                $"G1 X{rnd.NextDouble() * 400:0.###} Y{rnd.NextDouble() * 400:0.###} S{rnd.Next(0, 1000)} F{rnd.Next(500, 6000)} ; c{i}"));
        g.Add("M5");
        return g;
    }

    private static async Task<JobResult> RunJob(GrblController grbl, IEnumerable<string> lines, int timeoutMs = 30000)
    {
        var done = new TaskCompletionSource<JobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinished(JobResult r) => done.TrySetResult(r);
        grbl.JobFinished += OnFinished;
        try
        {
            grbl.StartJob(lines);
            var finished = await Task.WhenAny(done.Task, Task.Delay(timeoutMs));
            Assert.AreSame(done.Task, finished, "job did not finish in time");
            return await done.Task;
        }
        finally { grbl.JobFinished -= OnFinished; }
    }

    [TestMethod]
    public async Task Connects_AndReadsSettings()
    {
        var (grbl, _) = Connect();
        using var _g = grbl;
        Assert.IsTrue(grbl.IsConnected);
        StringAssert.StartsWith(grbl.FirmwareVersion, "Grbl 1.1h");
        Assert.IsTrue(await grbl.RefreshSettingsAsync());
        Assert.AreEqual(400, grbl.GetSetting(130));
        Assert.AreEqual(3, grbl.GetSetting(23));
    }

    [TestMethod]
    public async Task ParsesGrbl09StyleSettings()
    {
        var (grbl, _) = Connect(f => f.Grbl09SettingsFormat = true);
        using var _g = grbl;
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        grbl.Log += e => log.Enqueue($"{e.Direction}: {e.Text}");
        Assert.IsTrue(await grbl.RefreshSettingsAsync(), string.Join("\n", log));
        Assert.AreEqual(1000, grbl.GetSetting(30));
    }

    [TestMethod]
    public async Task Streaming_NeverOverflowsRxBuffer_AndPreservesOrder()
    {
        var (grbl, fake) = Connect(f => f.LineDelayMs = 0);
        using var _g = grbl;
        var job = MakeJob(3000);
        var result = await RunJob(grbl, job);

        Assert.IsTrue(result.Completed, result.Reason);
        Assert.IsFalse(fake.Overflowed, $"RX buffer overflowed (max {fake.MaxRxUsed})");
        var expected = job.Select(GrblController.CleanLine).Where(l => l.Length > 0).Append("G4P0").ToList();
        CollectionAssert.AreEqual(expected, fake.ReceivedLines.Skip(fake.ReceivedLines.Count - expected.Count).ToList());
        Assert.AreEqual(expected.Count, result.TotalLines);
        Assert.IsTrue(fake.MaxRxUsed > 60, "streaming should keep GRBL's buffer well filled");
    }

    [TestMethod]
    public async Task CommandInFlight_WhenJobStarts_IsAnsweredFirst_AndCounted()
    {
        var (grbl, fake) = Connect(f => f.LineDelayMs = 20);
        using var _g = grbl;
        // A manual command still waiting for its ok when Start is pressed.
        var pending = grbl.SendAsync("G0 X10 Y10 ; a long comment that is stripped by GRBL", 10000);
        var result = await RunJob(grbl, MakeJob(150));

        var reply = await pending;
        Assert.IsTrue(reply.Ok, reply.ErrorText);
        Assert.IsTrue(result.Completed, result.Reason);
        Assert.IsFalse(fake.Overflowed, $"RX buffer overflowed (max {fake.MaxRxUsed})");
    }

    [TestMethod]
    public async Task Job_CompletesOnlyAfterMachineIsIdle()
    {
        var (grbl, fake) = Connect(f => f.MotionTailMs = 800);
        using var _g = grbl;
        var result = await RunJob(grbl, new[] { "G1 X10 Y10 F1000", "G1 X20 Y10" });
        var lastLine = fake.LineTimes[^1];
        Assert.IsTrue(result.Completed);
        Assert.IsTrue(DateTime.UtcNow - lastLine >= TimeSpan.FromMilliseconds(600),
            "the job was reported complete while the machine was still moving");
    }

    [TestMethod]
    public async Task RejectedLine_PausesTheJob_ThenItCanContinue()
    {
        var (grbl, _) = Connect(f => f.Respond = l => l == "G1X5" ? "error:20" : null);
        using var _g = grbl;
        var rejected = new TaskCompletionSource<(int Line, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
        grbl.JobLineRejected += (n, line, _) => rejected.TrySetResult((n, line));
        grbl.JobLineRejected += (_, _, _) => grbl.ResumeJob(); // the user clicks Resume
        var result = await RunJob(grbl, new[] { "G1 X1", "G1 X5", "G1 X9" });
        Assert.IsTrue(rejected.Task.IsCompleted, "the rejection must be reported");
        Assert.AreEqual((2, "G1X5"), rejected.Task.Result);
        Assert.IsTrue(result.Completed);
        Assert.AreEqual(1, result.Errors);
        Assert.AreEqual(4, result.AckedLines); // 3 lines + the final G4P0
    }

    [TestMethod]
    public async Task Alarm_AbortsJob()
    {
        var (grbl, _) = Connect(f => f.Respond = l => l == "G1X300" ? "ALARM:2" : null);
        using var _g = grbl;
        var alarms = new List<int>();
        grbl.AlarmRaised += (c, _) => alarms.Add(c);
        var lines = Enumerable.Range(0, 200).Select(i => i == 50 ? "G1 X300" : $"G1 X{i % 100}").ToList();
        var result = await RunJob(grbl, lines);
        Assert.IsFalse(result.Completed);
        StringAssert.Contains(result.Reason, "ALARM 2");
        CollectionAssert.Contains(alarms, 2);
        Assert.IsFalse(grbl.IsJobRunning);
    }

    [TestMethod]
    public async Task StopJobAsync_HoldsBeforeResetting()
    {
        var (grbl, fake) = Connect(f => f.LineDelayMs = 15);
        using var _g = grbl;
        var finished = new TaskCompletionSource<JobResult>();
        grbl.JobFinished += r => finished.TrySetResult(r);
        grbl.StartJob(MakeJob(500));
        await Task.Delay(300);
        await grbl.StopJobAsync();
        var result = await finished.Task;

        Assert.IsFalse(result.Completed);
        var bytes = fake.RealtimeSnapshot();
        int hold = bytes.LastIndexOf((byte)'!');
        int reset = bytes.LastIndexOf(0x18);
        Assert.IsTrue(hold >= 0 && reset > hold, "expected a feed hold before the soft reset");
        Assert.IsFalse(grbl.IsJobRunning);
        // The controller is usable again afterwards.
        await Task.Delay(100);
        Assert.IsTrue((await grbl.SendAsync("G0 X0 Y0")).Ok);
    }

    [TestMethod]
    public async Task PauseAndResume_JobStillCompletes()
    {
        var (grbl, fake) = Connect(f => f.LineDelayMs = 5);
        using var _g = grbl;
        var finished = new TaskCompletionSource<JobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        grbl.JobFinished += r => finished.TrySetResult(r);
        var job = MakeJob(300);
        grbl.StartJob(job);
        await Task.Delay(200);
        grbl.PauseJob();
        Assert.IsTrue(grbl.IsJobPaused);
        await Task.Delay(500);
        int sentWhilePaused = fake.ReceivedLines.Count;
        await Task.Delay(500);
        Assert.AreEqual(sentWhilePaused, fake.ReceivedLines.Count, "nothing may execute while held");
        Assert.IsFalse(finished.Task.IsCompleted);
        grbl.ResumeJob();
        var done = await Task.WhenAny(finished.Task, Task.Delay(30000));
        Assert.AreSame(finished.Task, done);
        Assert.IsTrue(finished.Task.Result.Completed);
        Assert.IsFalse(fake.Overflowed);
    }

    [TestMethod]
    public async Task PausedWhileFinishing_DoesNotCompleteUntilResumed()
    {
        var (grbl, fake) = Connect(f => f.MotionTailMs = 1500);
        using var _g = grbl;
        var finished = new TaskCompletionSource<JobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        grbl.JobFinished += r => finished.TrySetResult(r);
        grbl.StartJob(new[] { "G1 X10 F1000", "G1 X20" });
        // Wait until every line is acknowledged (the machine is still "moving").
        for (int i = 0; i < 100 && fake.ReceivedLines.Count(l => l.StartsWith("G1", StringComparison.Ordinal)) < 2; i++) await Task.Delay(20);
        grbl.PauseJob();
        await Task.Delay(2000); // motion tail has elapsed, but the machine reports Hold
        Assert.IsFalse(finished.Task.IsCompleted, "a held job must not be reported complete");
        grbl.ResumeJob();
        var done = await Task.WhenAny(finished.Task, Task.Delay(10000));
        Assert.AreSame(finished.Task, done);
        Assert.IsTrue(finished.Task.Result.Completed);
    }

    [TestMethod]
    public async Task ManualCommands_AreRefusedWhileJobRuns()
    {
        var (grbl, _) = Connect(f => f.LineDelayMs = 10);
        using var _g = grbl;
        grbl.StartJob(MakeJob(200));
        var r = await grbl.SendAsync("$$");
        Assert.IsFalse(r.Ok);
        grbl.EmergencyStop();
        Assert.IsFalse(grbl.IsJobRunning);
    }

    [TestMethod]
    public void EmergencyStop_SendsSoftReset()
    {
        var (grbl, fake) = Connect();
        using var _g = grbl;
        fake.ClearRealtime();
        grbl.EmergencyStop();
        CollectionAssert.Contains(fake.RealtimeSnapshot(), (byte)0x18);
    }

    [TestMethod]
    public async Task UnpluggingUsb_RaisesConnectionLost()
    {
        var (grbl, fake) = Connect();
        using var _g = grbl;
        var lost = new TaskCompletionSource<string>();
        grbl.ConnectionLost += reason => lost.TrySetResult(reason);
        fake.Unplug();
        var done = await Task.WhenAny(lost.Task, Task.Delay(5000));
        Assert.AreSame(lost.Task, done, "unplug was not detected");
        Assert.IsFalse(grbl.IsConnected);
    }

    [TestMethod]
    public void StartJob_RejectsLinesGrblCannotHold()
    {
        var (grbl, _) = Connect();
        using var _g = grbl;
        var tooLong = "G1 X" + new string('1', 90);
        Assert.ThrowsException<InvalidOperationException>(() => grbl.StartJob(new[] { "G0 X0", tooLong }));
        Assert.IsFalse(grbl.IsJobRunning);
    }

    [TestMethod]
    public async Task Overrides_AreSentAsSpacedRealtimeBytes()
    {
        var (grbl, fake) = Connect();
        using var _g = grbl;
        fake.ClearRealtime();
        await grbl.SetFeedOverrideAsync(120);
        var sent = fake.RealtimeSnapshot().Where(b => b is >= 0x90 and <= 0x94).ToList();
        CollectionAssert.AreEqual(new byte[] { 0x90, 0x91, 0x91 }, sent);
    }

    [TestMethod]
    public void OverrideSequence_ReachesTarget()
    {
        foreach (var target in new[] { 10, 37, 75, 100, 133, 200, 250, 0 })
        {
            var seq = GrblController.OverrideSequence(target, 0x99, 0x9A, 0x9B, 0x9C, 0x9D);
            Assert.AreEqual((byte)0x99, seq[0]);
            int value = 100;
            foreach (var b in seq.Skip(1))
                value += b switch { 0x9A => 10, 0x9B => -10, 0x9C => 1, 0x9D => -1, _ => 0 };
            Assert.AreEqual(Math.Clamp(target, 10, 200), value, $"target {target}");
        }
    }

    [TestMethod]
    public void CleanLine_StripsCommentsAndWhitespace()
    {
        Assert.AreEqual("G1X10Y5", GrblController.CleanLine("G1 X10  Y5 ; move"));
        Assert.AreEqual("G0X1Y2", GrblController.CleanLine("(start) G0 X1 (mid) Y2"));
        Assert.AreEqual("", GrblController.CleanLine("; only a comment"));
        Assert.AreEqual("", GrblController.CleanLine("%"));
        Assert.AreEqual("$J=G91X10F500", GrblController.CleanLine("$J=G91 X10 F500\r"));
    }

    [TestMethod]
    public async Task SafeHome_SetsBedOrigin_ForNegativeSpaceFirmware()
    {
        var (grbl, fake) = Connect();
        using var _g = grbl;
        await grbl.RefreshSettingsAsync();
        var safety = new HomingSafetyService(grbl);
        var result = await safety.SafeHomeAsync(MachineProfile.OmtechB10);
        Assert.IsTrue(result.Success, result.Message);
        Assert.IsTrue(fake.ReceivedLines.Contains("G10L2P1X-400Y-400"), string.Join(" | ", fake.ReceivedLines));
        await grbl.RefreshOffsetsAsync();
        CollectionAssert.AreEqual(new double[] { -400, -400, 0 }, grbl.GetOffset("G54"));
    }

    [TestMethod]
    public async Task SafeHome_LeavesOriginAlone_ForForceOriginFirmware()
    {
        var (grbl, fake) = Connect(f => { f.HomePosition = (0, 0); f.ReportMachinePosition = false; });
        using var _g = grbl;
        await grbl.RefreshSettingsAsync();
        var result = await new HomingSafetyService(grbl).SafeHomeAsync(MachineProfile.OmtechB10);
        Assert.IsTrue(result.Success, result.Message);
        Assert.IsFalse(fake.ReceivedLines.Any(l => l.StartsWith("G10", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task FirstHoming_RunsGently_ThenRestoresTheSeekSpeed()
    {
        var (grbl, fake) = Connect();
        using var _g = grbl;
        await grbl.RefreshSettingsAsync();
        fake.Settings[25] = 1500;
        await grbl.RefreshSettingsAsync();
        var result = await new HomingSafetyService(grbl).SafeHomeAsync(MachineProfile.OmtechB10, HomingSafetyService.GentleSeekMmMin);
        Assert.IsTrue(result.Success, result.Message);
        var lines = fake.ReceivedLines.ToList();
        int gentle = lines.IndexOf("$25=600"), home = lines.IndexOf("$H"), restore = lines.IndexOf("$25=1500");
        Assert.IsTrue(gentle >= 0 && gentle < home && home < restore, string.Join(" | ", lines));
        Assert.AreEqual(1500, fake.Settings[25]);
    }

    [TestMethod]
    public void HomingBudget_IsTheLongestACorrectCycleCanTake()
    {
        // B10: 400 mm, seek 1500, locate 100, pull-off 2 mm, debounce 250 ms, X and Y together.
        var b10 = HomingSafetyService.HomingTimeBudget(400, 1500, 100, 2, 250, xyTogether: true).TotalSeconds;
        Assert.AreEqual(22, b10, 1, "seek 16.4 s + locate 2.4 s + pull-offs + debounce + round trip");
        // GRBL itself only gives up after searching 1.5 × 400 mm = 24 s: the budget stops sooner.
        Assert.IsTrue(b10 < 1.5 * 400 / 1500 * 60);
        // Gentle first homing: well inside GRBL's own 60 s.
        var gentle = HomingSafetyService.HomingTimeBudget(400, 600, 100, 2, 250, xyTogether: true).TotalSeconds;
        Assert.IsTrue(gentle > 41 && gentle < 50, gentle.ToString());
        // One axis after the other takes about twice as long.
        var sequential = HomingSafetyService.HomingTimeBudget(400, 1500, 100, 2, 250, xyTogether: false).TotalSeconds;
        Assert.AreEqual(2 * (b10 - 2) + 2, sequential, 0.01);
    }

    [TestMethod]
    public async Task SwitchTest_NeedsARealPress_NotAStuckInput()
    {
        var (grbl, fake) = Connect();
        using var _g = grbl;
        var safety = new HomingSafetyService(grbl);

        // Normally-closed switches read with $5=0: "pressed" all the time.
        fake.Pins = "XY";
        var stuck = await safety.LimitSwitchTestAsync(TimeSpan.FromSeconds(1));
        Assert.IsFalse(stuck.BothWork);
        Assert.IsTrue(stuck.XStuck && stuck.YStuck);

        // Released, then each switch pressed in turn.
        fake.Pins = "";
        var presses = Task.Run(async () =>
        {
            await Task.Delay(400); fake.Pins = "X";
            await Task.Delay(400); fake.Pins = "";
            await Task.Delay(400); fake.Pins = "Y";
        });
        var good = await safety.LimitSwitchTestAsync(TimeSpan.FromSeconds(5));
        await presses;
        Assert.IsTrue(good.BothWork, good.ToString());

        // A switch that never registers.
        fake.Pins = "";
        var missing = Task.Run(async () => { await Task.Delay(300); fake.Pins = "X"; });
        var oneOnly = await safety.LimitSwitchTestAsync(TimeSpan.FromSeconds(1.5));
        await missing;
        Assert.IsTrue(oneOnly.XSeen);
        Assert.IsFalse(oneOnly.YSeen);
        Assert.IsFalse(oneOnly.BothWork);
    }

    [TestMethod]
    public void BedOffset_CoversBothGrblConventions()
    {
        // Stock GRBL, homing toward X-/Y- (B10): home ≈ -travel + pull-off.
        Assert.AreEqual(-400, HomingSafetyService.BedOffsetForAxis(true, -398, 400));
        // HOMING_FORCE_SET_ORIGIN build, homing toward the minimum: home = 0, space [0, 400].
        Assert.AreEqual(0, HomingSafetyService.BedOffsetForAxis(true, 0, 400));
        // Homing toward the maximum: space is [-400, 0] either way.
        Assert.AreEqual(-400, HomingSafetyService.BedOffsetForAxis(false, -2, 400));
        Assert.AreEqual(-400, HomingSafetyService.BedOffsetForAxis(false, 0, 400));
    }

    [TestMethod]
    public void Status_CarriesOverridesAndAccessoriesForward()
    {
        Assert.IsTrue(GrblStatus.TryParse("<Run|MPos:1.000,2.000,0.000|FS:500,300|Ov:120,100,80|A:SF|WCO:-400.000,-400.000,0.000>", null, out var a));
        Assert.AreEqual(120, a.OverrideFeed);
        Assert.IsTrue(a.LaserEnabled);
        Assert.IsTrue(a.AirAssistOn);
        Assert.AreEqual(401, a.WX, 1e-9);

        Assert.IsTrue(GrblStatus.TryParse("<Run|MPos:3.000,2.000,0.000|FS:500,300>", a, out var b));
        Assert.AreEqual(120, b.OverrideFeed, "Ov must persist between reports");
        Assert.IsTrue(b.LaserEnabled, "A: must persist between reports");
        Assert.AreEqual(403, b.WX, 1e-9, "WCO must persist between reports");

        Assert.IsTrue(GrblStatus.TryParse("<Idle|MPos:3.000,2.000,0.000|FS:0,0|Ov:100,100,100>", b, out var c));
        Assert.IsFalse(c.LaserEnabled, "Ov without A: means everything is off");
        Assert.AreEqual("Idle", c.State.ToString());

        Assert.IsTrue(GrblStatus.TryParse("<Hold:0|WPos:5.000,6.000,0.000|FS:0,0|Pn:XD>", c, out var d));
        Assert.AreEqual(MachineState.Hold, d.State);
        Assert.AreEqual("Hold:0", d.SubState);
        Assert.IsTrue(d.XLimitActive && d.DoorPinActive);
        Assert.IsFalse(d.HasMachinePosition);
    }
}
