namespace Refii.Tests;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.AndroidRuntime;

[TestSuite]
public class AndroidDebugIdleQuitTests
{
    [TestCase]
    public void OffUntilArmedAndUntilTheMinutesPass()
    {
        AssertThat(AndroidDebugIdleQuit.Due(5, -1, 10_000_000)).IsFalse();      // no request yet
        AssertThat(AndroidDebugIdleQuit.Due(0, 1_000, 10_000_000)).IsFalse();    // setting off
        AssertThat(AndroidDebugIdleQuit.Due(5, 1_000, 1_000 + 299_999)).IsFalse();
        AssertThat(AndroidDebugIdleQuit.Due(5, 1_000, 1_000 + 300_000)).IsTrue();
    }
}
