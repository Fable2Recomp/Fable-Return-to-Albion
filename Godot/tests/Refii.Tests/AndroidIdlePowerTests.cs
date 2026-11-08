using GdUnit4;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>What the phone does when nobody is using it.</summary>
[TestSuite]
public sealed class AndroidIdlePowerTests
{
    [TestCase]
    public void UntouchedMenuIsCappedAndNothingElseIs()
    {
        AssertThat(AndroidIdlePower.CapMenu(AndroidIdlePower.MenuIdleSeconds, true, false, false)).IsTrue();
        AssertThat(AndroidIdlePower.CapMenu(AndroidIdlePower.MenuIdleSeconds - 1, true, false, false)).IsFalse();
        // Play, cutscenes and loading screens state no menu.
        AssertThat(AndroidIdlePower.CapMenu(3600, false, false, false)).IsFalse();
        AssertThat(AndroidIdlePower.CapMenu(3600, true, true, false)).IsFalse();
        AssertThat(AndroidIdlePower.CapMenu(3600, true, false, true)).IsFalse();
    }

    [TestCase]
    public void CapNeverRaisesTheConfiguredOne()
    {
        AssertThat(AndroidIdlePower.Capped(false, 0)).IsEqual(0);
        AssertThat(AndroidIdlePower.Capped(false, 30)).IsEqual(30);
        AssertThat(AndroidIdlePower.Capped(true, 0)).IsEqual(AndroidIdlePower.MenuFrameCap);
        AssertThat(AndroidIdlePower.Capped(true, 30)).IsEqual(AndroidIdlePower.MenuFrameCap);
        AssertThat(AndroidIdlePower.Capped(true, 5)).IsEqual(5);
    }

    [TestCase]
    public void ScreenIsLetGoAfterTheLimitButNotInCoopOrWhenOff()
    {
        AssertThat(AndroidIdlePower.LetScreenGo(599, 10, false)).IsFalse();
        AssertThat(AndroidIdlePower.LetScreenGo(600, 10, false)).IsTrue();
        AssertThat(AndroidIdlePower.LetScreenGo(600, 10, true)).IsFalse();
        AssertThat(AndroidIdlePower.LetScreenGo(86400, 0, false)).IsFalse();
    }
}
