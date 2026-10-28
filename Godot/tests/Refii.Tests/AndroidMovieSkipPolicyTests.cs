using GdUnit4;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Checks the embedded startup-logo lifecycle without native calls or private movie data.</summary>
[TestSuite]
public sealed class AndroidMovieSkipPolicyTests
{
    /// <summary>An unnamed but real buffer request retires only after dwell, exactly once per generation.</summary>
    [TestCase]
    public void UnnamedEmbeddedRequestsRespectDwellAndGeneration()
    {
        var policy = new AndroidMovieSkipPolicy(true, 2);
        // hasPayload is true for a nonzero embedded buffer even when its pathname is empty.
        AssertThat(policy.ShouldFinish(true, 1, true, 10)).IsFalse();
        AssertThat(policy.ShouldFinish(true, 1, true, 11.99)).IsFalse();
        AssertThat(policy.ShouldFinish(true, 1, true, 12)).IsTrue();
        AssertThat(policy.ShouldFinish(true, 1, true, 99)).IsFalse();
        AssertThat(policy.ShouldFinish(false, 1, true, 100)).IsFalse();
        AssertThat(policy.ShouldFinish(true, 2, true, 101)).IsFalse();
        AssertThat(policy.ShouldFinish(true, 2, true, 102.99)).IsFalse();
        AssertThat(policy.ShouldFinish(true, 2, true, 103)).IsTrue();
        AssertThat(policy.ShouldFinish(true, 2, true, 104)).IsFalse();
    }

    /// <summary>Absent, ended, disabled or invalid-time observations cannot synthesize movie completion.</summary>
    [TestCase]
    public void OnlyWantedPayloadRequestsCanRetire()
    {
        var policy = new AndroidMovieSkipPolicy(true, 0.5);
        AssertThat(policy.ShouldFinish(true, 0, false, 0)).IsFalse();
        AssertThat(policy.ShouldFinish(false, 1, true, 0)).IsFalse();
        AssertThat(policy.ShouldFinish(true, 1, true, double.NaN)).IsFalse();
        AssertThat(policy.ShouldFinish(true, 1, true, 10)).IsFalse();
        AssertThat(policy.ShouldFinish(true, 1, true, 9)).IsFalse();
        AssertThat(policy.ShouldFinish(false, 1, true, 11)).IsFalse();
        AssertThat(policy.ShouldFinish(true, 1, true, 11)).IsTrue();
        var disabled = new AndroidMovieSkipPolicy(false, 0.5);
        AssertThat(disabled.ShouldFinish(true, 7, true, 0)).IsFalse();
        AssertThat(disabled.ShouldFinish(true, 7, true, 60)).IsFalse();
    }
}
