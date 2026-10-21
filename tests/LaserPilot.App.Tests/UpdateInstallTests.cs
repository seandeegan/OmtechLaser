using System.IO;
using LaserPilot.App.Services;

namespace LaserPilot.App.Tests;

[TestClass]
public sealed class UpdateInstallTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lp-swap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public void SwapReplacesTheExe_EvenWhileItIsOpen()
    {
        var dir = NewDir();
        try
        {
            var current = Path.Combine(dir, "LaserPilot.exe");
            var staged = Path.Combine(dir, "staged.exe");
            File.WriteAllText(current, "old version");
            File.WriteAllText(staged, "new version");

            // A running exe is open without write/delete sharing, but can be renamed.
            using (new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                UpdateService.SwapInNewExe(staged, current);
            }

            Assert.AreEqual("new version", File.ReadAllText(current));
            Assert.AreEqual("old version", File.ReadAllText(current + ".old"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void FailedSwapLeavesTheOldExeInPlace()
    {
        var dir = NewDir();
        try
        {
            var current = Path.Combine(dir, "LaserPilot.exe");
            File.WriteAllText(current, "old version");
            Assert.ThrowsException<FileNotFoundException>(() =>
                UpdateService.SwapInNewExe(Path.Combine(dir, "missing.exe"), current));
            Assert.AreEqual("old version", File.ReadAllText(current));

            // Copy fails after the rename (staged file locked): rolled back.
            var staged = Path.Combine(dir, "staged.exe");
            File.WriteAllText(staged, "new version");
            using (new FileStream(staged, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.ThrowsException<IOException>(() => UpdateService.SwapInNewExe(staged, current));
            }
            Assert.AreEqual("old version", File.ReadAllText(current));
            Assert.IsFalse(File.Exists(current + ".old"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void SecondUpdateWorksWhileThePreviousOldFileIsStillLocked()
    {
        var dir = NewDir();
        try
        {
            var current = Path.Combine(dir, "LaserPilot.exe");
            File.WriteAllText(current, "v2");
            File.WriteAllText(current + ".old", "v1 still exiting");
            var staged = Path.Combine(dir, "staged.exe");
            File.WriteAllText(staged, "v3");
            using (new FileStream(current + ".old", FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                UpdateService.SwapInNewExe(staged, current);
            }
            Assert.AreEqual("v3", File.ReadAllText(current));
            Assert.IsTrue(Directory.GetFiles(dir, "LaserPilot.exe.*.old").Length == 1, "the old copy gets a unique name");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
