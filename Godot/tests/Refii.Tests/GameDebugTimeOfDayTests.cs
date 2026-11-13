using GdUnit4;
using Refii.Game;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The debug server's timeofday op posts the mod API's set_time_of_day command.</summary>
[TestSuite]
public sealed class GameDebugTimeOfDayTests
{
    /// <summary>Hours become the invariant-culture command, clamped to the clock's 0..24.</summary>
    [TestCase]
    public void HoursBecomeTheSetTimeOfDayCommand()
    {
        AssertThat(GameDebugServer.TimeOfDayCommand(0.83)).IsEqual("set_time_of_day 0.83");
        AssertThat(GameDebugServer.TimeOfDayCommand(-3)).IsEqual("set_time_of_day 0");
        AssertThat(GameDebugServer.TimeOfDayCommand(30)).IsEqual("set_time_of_day 24");
    }

    /// <summary>Not-finite hours post nothing.</summary>
    [TestCase]
    public void NonFiniteHoursAreRefused()
    {
        AssertThat(GameDebugServer.TimeOfDayCommand(double.NaN)).IsNull();
        AssertThat(GameDebugServer.TimeOfDayCommand(double.PositiveInfinity)).IsNull();
    }
}
