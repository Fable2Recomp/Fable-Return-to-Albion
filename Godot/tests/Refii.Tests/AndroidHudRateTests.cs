using GdUnit4;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`hud_half_rate` (AndroidHudRate): when the bare HUD may skip a frame.</summary>
[TestSuite]
public sealed class AndroidHudRateTests
{
    /// <summary>Only the bare HUD, nothing from the world, no mod piece, a still camera, and never twice running.</summary>
    [TestCase]
    public void SkipsOnlyTheStillBareHudEveryOtherFrame()
    {
        AssertThat(AndroidHudRate.Skip(true, 0, false, 1f, skippedLast: false)).IsTrue();
        AssertThat(AndroidHudRate.Skip(true, 0, false, 1f, skippedLast: true)).IsFalse();
        AssertThat(AndroidHudRate.Skip(false, 0, false, 1f, false)).IsFalse();
        AssertThat(AndroidHudRate.Skip(true, 1, false, 1f, false)).IsFalse();
        AssertThat(AndroidHudRate.Skip(true, 0, true, 1f, false)).IsFalse();
        AssertThat(AndroidHudRate.Skip(true, 0, false, 0.9999f, false)).IsFalse();
    }
}
