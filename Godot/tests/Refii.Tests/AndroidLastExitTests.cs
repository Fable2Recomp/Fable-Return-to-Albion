namespace Refii.Tests;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.AndroidRuntime;

[TestSuite]
public class AndroidLastExitTests
{
    private static AndroidLastExit.Exit NativeCrash(long timestamp = 1_793_000_000_000) => new(
        5, 11, 100, 812_000, 1_024_000, timestamp, "crash", 4321, "org.refii.android", "ApplicationExitInfo(...)");

    [TestCase]
    public void TestReasonsAreApplicationExitInfosNames()
    {
        AssertThat(AndroidLastExit.ReasonName(0)).IsEqual("UNKNOWN");
        AssertThat(AndroidLastExit.ReasonName(1)).IsEqual("EXIT_SELF");
        AssertThat(AndroidLastExit.ReasonName(2)).IsEqual("SIGNALED");
        AssertThat(AndroidLastExit.ReasonName(3)).IsEqual("LOW_MEMORY");
        AssertThat(AndroidLastExit.ReasonName(4)).IsEqual("CRASH");
        AssertThat(AndroidLastExit.ReasonName(5)).IsEqual("CRASH_NATIVE");
        AssertThat(AndroidLastExit.ReasonName(6)).IsEqual("ANR");
        AssertThat(AndroidLastExit.ReasonName(10)).IsEqual("USER_REQUESTED");
        AssertThat(AndroidLastExit.ReasonName(16)).IsEqual("PACKAGE_UPDATED");
        // A reason newer than this table still says its number.
        AssertThat(AndroidLastExit.ReasonName(42)).IsEqual("REASON_42");
        AssertThat(AndroidLastExit.ImportanceName(100)).IsEqual("FOREGROUND");
        AssertThat(AndroidLastExit.ImportanceName(400)).IsEqual("CACHED");
        AssertThat(AndroidLastExit.ImportanceName(777)).IsEqual("777");
    }

    [TestCase]
    public void TestStatusIsASignalOnlyWhereItIsOne()
    {
        AssertThat(AndroidLastExit.StatusText(5, 11)).IsEqual("signal 11");
        AssertThat(AndroidLastExit.StatusText(2, 9)).IsEqual("signal 9");
        AssertThat(AndroidLastExit.StatusText(1, 0)).IsEqual("exit 0");
        AssertThat(AndroidLastExit.StatusText(3, 0)).IsEqual("status 0");
        AssertThat(AndroidLastExit.HasTrace(5)).IsTrue();
        AssertThat(AndroidLastExit.HasTrace(6)).IsTrue();
        AssertThat(AndroidLastExit.HasTrace(4)).IsFalse();
    }

    [TestCase]
    public void TestSummaryLine()
    {
        AssertThat(AndroidLastExit.Summary(NativeCrash(0))).IsEqual(
            "[android] last exit: CRASH_NATIVE (signal 11) at 1970-01-01 00:00:00Z, org.refii.android pid 4321, " +
            "FOREGROUND, pss 792 MB, rss 1000 MB: crash");
        string text = AndroidLastExit.Format(new[] { NativeCrash(0) }, "last-exit-trace.bin, 10 bytes");
        AssertThat(text.StartsWith("key: 4321@0\n", StringComparison.Ordinal)).IsTrue();
        AssertThat(text).Contains("reason: CRASH_NATIVE (5)");
        AssertThat(text).Contains("trace: last-exit-trace.bin, 10 bytes");
    }

    [TestCase]
    public void TestANewExitKeepsThePreviousAndTheSameExitIsWrittenOnce()
    {
        string logs = Path.Combine(Refii.Tests.TestTemp.Root, "refii-exit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logs);
        try
        {
            string current = Path.Combine(logs, AndroidLastExit.FileName);
            var first = NativeCrash(1000);
            AssertThat(AndroidLastExit.Rotate(logs, AndroidLastExit.Key(first))).IsTrue();
            File.WriteAllText(current, AndroidLastExit.Format(new[] { first }, ""));
            File.WriteAllText(Path.Combine(logs, AndroidLastExit.TraceFileName), "tombstone");

            // The next start reads the same record: nothing moves.
            AssertThat(AndroidLastExit.Rotate(logs, AndroidLastExit.Key(first))).IsFalse();
            AssertThat(File.Exists(Path.Combine(logs, AndroidLastExit.PreviousFileName))).IsFalse();

            // A newer exit: the record and its trace become the previous ones.
            AssertThat(AndroidLastExit.Rotate(logs, AndroidLastExit.Key(NativeCrash(2000)))).IsTrue();
            AssertThat(File.Exists(current)).IsFalse();
            AssertThat(File.ReadAllText(Path.Combine(logs, AndroidLastExit.PreviousFileName))).Contains("key: 4321@1000");
            AssertThat(File.ReadAllText(Path.Combine(logs, AndroidLastExit.PreviousTraceFileName))).IsEqual("tombstone");
            AssertThat(File.Exists(Path.Combine(logs, AndroidLastExit.TraceFileName))).IsFalse();
        }
        finally { Directory.Delete(logs, true); }
    }
}
