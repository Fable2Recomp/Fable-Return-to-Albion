using GdUnit4;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The phone's no-spot-lights option: which fixtures stay shown.</summary>
[TestSuite]
public sealed class AndroidNoSpotLightsTests
{
    /// <summary>Spots are hidden only with the option on; omni fixtures always show.</summary>
    [TestCase]
    public void OnlySpotsAreHidden()
    {
        AssertThat(AndroidNoSpotLights.Shown(omni: true, noSpots: true)).IsTrue();
        AssertThat(AndroidNoSpotLights.Shown(omni: true, noSpots: false)).IsTrue();
        AssertThat(AndroidNoSpotLights.Shown(omni: false, noSpots: false)).IsTrue();
        AssertThat(AndroidNoSpotLights.Shown(omni: false, noSpots: true)).IsFalse();
    }
}
