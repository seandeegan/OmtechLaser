using LaserPilot.Core.Comm;

namespace LaserPilot.Core.Tests;

/// <summary>Protocol hazards found in review: buffer budgets, EEPROM writes, overrides, locked or silent controllers.</summary>
[TestClass]
public sealed class GrblControllerSafetyTests
{
    private static (GrblController grbl, FakeGrbl fake) Connect(Action<FakeGrbl>? configure = null)
    {
        var fake = new FakeGrbl();
        configure?.Invoke(fake);
        var grbl = new GrblController((_, _) => fake);
        grbl.Connect("SIM");
        return (grbl, fake);
    }

    private static async Task<JobResult> RunJob(GrblController grbl, IEnumerable<string> lines, int timeoutMs = 30000)
    {
        var done = new TaskCompletionSource<JobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        grbl.JobFinished += r => done.TrySetResult(r);
        grbl.StartJob(lines);
        var finished = await Task.WhenAny(done.Task, Task.Delay(timeoutMs));
        Assert.AreSame(done.Task, finished, "job did not finish in time");
        return await done.Task;
    }

    [TestMethod]
    public async Task BurstOfJogClicks_NeverOverflowsGrblsBuffer()
    {
        // GRBL is slow to read (its planner is full), and the user clicks a jog button 40 times.
        var (grbl, fake) = Connect(f => f.LineDelayMs = 25);
        using var _g = grbl;
        var replies = Enumerable.Range(0, 40).Select(i => grbl.JogAsync(i % 2 == 0 ? 1 : -1, 0, 2000)).ToList();
        var all = await Task.WhenAll(replies);
        Assert.IsFalse(fake.Overflowed, $"RX buffer overflowed (max {fake.MaxRxUsed} bytes)");
        Assert.IsTrue(all.All(r => r.Ok), string.Join(", ", all.Where(r => !r.Ok).Select(r => r.ErrorText)));
        Assert.AreEqual(40, fake.ReceivedLines.Count(l => l.StartsWith("$J=", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RealtimeCharactersInCommandsAreRefused()
    {
        var (grbl, fake) = Connect();
        using var _g = grbl;
        var bad = await grbl.SendAsync("G0 X10!");
        Assert.IsFalse(bad.Ok);
        StringAssert.Contains(bad.ErrorText, "'!'");
        // Inside a comment it's harmless: comments are stripped before sending.
        var ok = await grbl.SendAsync("G0 X10 ; go!");
        Assert.IsTrue(ok.Ok, ok.ErrorText);
        Assert.IsFalse(fake.Held, "the comment's '!' must never reach GRBL");
        Assert.ThrowsException<InvalidOperationException>(() => grbl.StartJob(new[] { "G1 X1", "G1 X2~" }));
    }

    [TestMethod]
    public async Task EepromWritesInAJob_AreSentAlone()
    {
        var (grbl, fake) = Connect(f => f.LineDelayMs = 0);
        using var _g = grbl;
        var lines = Enumerable.Range(0, 60).Select(i => $"G1 X{i} Y{i} F3000").ToList();
        lines.Insert(20, "G10 L2 P1 X0 Y0");
        lines.Insert(40, "$21=0");
        var result = await RunJob(grbl, lines);
        Assert.IsTrue(result.Completed, result.Reason);
        Assert.IsFalse(fake.EepromViolation, "bytes were sent while GRBL was writing its EEPROM");
        // 60 moves + the two EEPROM lines + the final G4P0: nothing lost.
        Assert.AreEqual(63, result.AckedLines);
        CollectionAssert.IsSubsetOf(new[] { "G10L2P1X0Y0", "$21=0" }, fake.ReceivedLines);
    }

    [TestMethod]
    public void EepromDetection()
    {
        Assert.IsTrue(GrblController.WritesEeprom("$21=0"));
        Assert.IsTrue(GrblController.WritesEeprom("$N0=G21"));
        Assert.IsTrue(GrblController.WritesEeprom("G10L2P1X0Y0"));
        Assert.IsTrue(GrblController.WritesEeprom("G10L20P1X0Y0"));
        Assert.IsTrue(GrblController.WritesEeprom("G28.1"));
        Assert.IsTrue(GrblController.WritesEeprom("G30.1"));
        Assert.IsFalse(GrblController.WritesEeprom("$J=G91X10F1000"));
        Assert.IsFalse(GrblController.WritesEeprom("$H"));
        Assert.IsFalse(GrblController.WritesEeprom("G1X10L2"));
        Assert.IsFalse(GrblController.WritesEeprom("G10L200"));
        Assert.IsFalse(GrblController.WritesEeprom("G28"));
    }

    [TestMethod]
    public async Task OverridesAreResetWhenAJobEnds()
    {
        var (grbl, fake) = Connect(f => f.LineDelayMs = 5);
        using var _g = grbl;
        var done = new TaskCompletionSource<JobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        grbl.JobFinished += r => done.TrySetResult(r);
        grbl.StartJob(Enumerable.Range(0, 100).Select(i => $"G1 X{i % 50} F3000"));
        await grbl.SetPowerOverrideAsync(150);
        fake.ClearRealtime();
        await Task.WhenAny(done.Task, Task.Delay(30000));
        Assert.IsTrue(done.Task.IsCompleted);
        var after = fake.RealtimeSnapshot();
        CollectionAssert.Contains(after, (byte)0x99, "power override must be reset to 100 %");
        CollectionAssert.Contains(after, (byte)0x90, "feed override must be reset to 100 %");
        Assert.IsFalse(after.Any(b => b is 0x9A or 0x9B or 0x9C or 0x9D), "no late override steps after the reset");
    }

    [TestMethod]
    public async Task LockedController_RefusesToStartAJob()
    {
        var (grbl, fake) = Connect();
        using var _g = grbl;
        fake.ForcedState = "Alarm";
        await Task.Delay(500); // an Alarm status report arrives
        var ex = Assert.ThrowsException<InvalidOperationException>(() => grbl.StartJob(new[] { "G1 X10" }));
        StringAssert.Contains(ex.Message, "locked");
        Assert.IsFalse(grbl.IsJobRunning);
    }

    [TestMethod]
    public async Task LockedMidJob_EndsTheJobInsteadOfHanging()
    {
        var (grbl, _) = Connect(f => f.Respond = l => l.StartsWith("G1X3", StringComparison.Ordinal) ? "error:9" : null);
        using var _g = grbl;
        var result = await RunJob(grbl, Enumerable.Range(0, 50).Select(i => $"G1 X{i}"), 10000);
        Assert.IsFalse(result.Completed);
        StringAssert.Contains(result.Reason, "locked");
    }

    [TestMethod]
    public async Task SilentUnplug_IsDetected()
    {
        var (grbl, fake) = Connect();
        using var _g = grbl;
        var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        grbl.ConnectionLost += r => lost.TrySetResult(r);
        fake.UnplugSilently(); // port still "open", but reads return nothing instantly
        var done = await Task.WhenAny(lost.Task, Task.Delay(5000));
        Assert.AreSame(lost.Task, done, "a vanished device must be noticed even if the port still looks open");
        Assert.IsFalse(grbl.IsConnected);
    }

    [TestMethod]
    public async Task EmergencyStop_ReportsWhenItCouldNotBeSent()
    {
        var (grbl, fake) = Connect();
        using var _g = grbl;
        Assert.IsTrue(grbl.EmergencyStop());
        fake.UnplugSilently();
        Assert.IsFalse(grbl.EmergencyStop(), "a stop that never left the PC must not be reported as sent");
        await Task.Delay(100);
    }

    [TestMethod]
    public async Task CommandsThatTimeOutBeforeSending_AreDropped()
    {
        // GRBL is busy on one line while its buffer is full; a command still waiting on our
        // side times out — it must not be sent once GRBL catches up.
        var (grbl, fake) = Connect(f => { f.LineDelayMs = 0; f.Respond = l => { if (l == "G4P2") Thread.Sleep(1500); return null; }; });
        using var _g = grbl;
        var slow = grbl.SendAsync("G4 P2", 10000);
        var filler = Enumerable.Range(0, 14).Select(i => grbl.SendAsync($"G1 X{i} Y{i} F1000")).ToList();
        var late = await grbl.SendAsync("$J=G91 X50 F1000", 200);
        Assert.IsFalse(late.Ok);
        await slow;
        await Task.WhenAll(filler);
        await Task.Delay(300);
        Assert.IsFalse(fake.ReceivedLines.Any(l => l.StartsWith("$J=G91X50", StringComparison.Ordinal)),
            "a command the caller gave up on must not be sent afterwards");
    }
}
