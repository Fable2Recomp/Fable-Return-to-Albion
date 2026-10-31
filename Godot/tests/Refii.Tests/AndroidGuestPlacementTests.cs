using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using System.Collections.Generic;
using System.Text;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`android_guest_prime_core`: the key, the presets, the prime cluster and the thread's name.</summary>
[TestSuite]
public sealed class AndroidGuestPlacementTests
{
    /// <summary>Off unless refii.toml says true; on in both presets.</summary>
    [TestCase]
    public void OffUnlessConfigured()
    {
        AssertThat(AndroidGuestPlacement.Configured(new Dictionary<string, string>())).IsFalse();
        AssertThat(AndroidGuestPlacement.Configured(new Dictionary<string, string> { [AndroidGuestPlacement.Key] = "false" })).IsFalse();
        AssertThat(AndroidGuestPlacement.Configured(new Dictionary<string, string> { [AndroidGuestPlacement.Key] = "true" })).IsTrue();

        AssertThat(GraphicsPresets.BalancedValues[AndroidGuestPlacement.Key]).IsEqual("true");
        AssertThat(GraphicsPresets.PerformanceValues[AndroidGuestPlacement.Key]).IsEqual("true");
    }

    /// <summary>The S10e's one prime A76, a two-prime-core layout like the 8 Elite's, and nothing where all cores are alike.</summary>
    [TestCase]
    public void ThePrimeClusterIsTheFastestCores()
    {
        var snapdragon855 = new Dictionary<int, long>
        {
            [0] = 1785600, [1] = 1785600, [2] = 1785600, [3] = 1785600,
            [4] = 2419200, [5] = 2419200, [6] = 2419200, [7] = 2841600,
        };
        AssertThat(AndroidGuestPlacement.PrimeCpus(snapdragon855)).IsEqual(new[] { 7 });

        var snapdragon8Elite = new Dictionary<int, long>
        {
            [0] = 4320000, [1] = 4320000,
            [2] = 3532800, [3] = 3532800, [4] = 3532800, [5] = 3532800, [6] = 3532800, [7] = 3532800,
        };
        AssertThat(AndroidGuestPlacement.PrimeCpus(snapdragon8Elite)).IsEqual(new[] { 0, 1 });

        AssertThat(AndroidGuestPlacement.PrimeCpus(new Dictionary<int, long> { [0] = 2000000, [1] = 2000000 })).IsEmpty();
        AssertThat(AndroidGuestPlacement.PrimeCpus(new Dictionary<int, long>())).IsEmpty();
        AssertThat(AndroidGuestPlacement.Describe(new[] { 7 })).IsEqual("cpu7");
        AssertThat(AndroidGuestPlacement.Describe(new[] { 0, 1 })).IsEqual("cpu0,1");
    }

    /// <summary>The guest's game thread by its cut name, and none of its other threads.</summary>
    [TestCase]
    public void OnlyTheGameThreadIsPlaced()
    {
        AssertThat(AndroidGuestPlacement.IsGameThread(Encoding.ASCII.GetBytes("GameThread (F80"))).IsTrue();
        AssertThat(AndroidGuestPlacement.IsGameThread(Encoding.ASCII.GetBytes("XThread (F8000"))).IsFalse();
        AssertThat(AndroidGuestPlacement.IsGameThread(Encoding.ASCII.GetBytes("3D Engine (F800"))).IsFalse();
        AssertThat(AndroidGuestPlacement.IsGameThread(Encoding.ASCII.GetBytes("VkThread"))).IsFalse();
        AssertThat(AndroidGuestPlacement.IsGameThread(Encoding.ASCII.GetBytes("Game"))).IsFalse();
    }

    /// <summary>`android_host_off_prime`: off unless refii.toml says true.</summary>
    [TestCase]
    public void OffPrimeIsOffUnlessConfigured()
    {
        AssertThat(AndroidGuestPlacement.OffPrimeConfigured(new Dictionary<string, string>())).IsFalse();
        AssertThat(AndroidGuestPlacement.OffPrimeConfigured(new Dictionary<string, string> { [AndroidGuestPlacement.OffPrimeKey] = "true" })).IsTrue();
    }

    /// <summary>Another thread loses the prime cores and keeps the rest; one holding only the prime
    /// cores gets every other core; one holding none of them is left alone.</summary>
    [TestCase]
    public void OtherThreadsLeaveThePrimeCoresAndKeepTheRest()
    {
        ulong[] all = { 0xFFUL, 0UL };
        ulong[] prime = { 1UL << 7, 0UL };
        ulong[] result = new ulong[2];

        AssertThat(AndroidGuestPlacement.WithoutPrime(new ulong[] { 0xFFUL, 0UL }, prime, all, result)).IsTrue();
        AssertThat(result[0]).IsEqual(0x7FUL);

        // The big cores only: they keep the gold ones.
        AssertThat(AndroidGuestPlacement.WithoutPrime(new ulong[] { 0xF0UL, 0UL }, prime, all, result)).IsTrue();
        AssertThat(result[0]).IsEqual(0x70UL);

        // Inherited from the placed game thread: cpu7 alone. It gets every other core.
        AssertThat(AndroidGuestPlacement.WithoutPrime(new ulong[] { 1UL << 7, 0UL }, prime, all, result)).IsTrue();
        AssertThat(result[0]).IsEqual(0x7FUL);

        // Already off it (the little cores only).
        AssertThat(AndroidGuestPlacement.WithoutPrime(new ulong[] { 0x0FUL, 0UL }, prime, all, result)).IsFalse();

        ulong[] elite = { 0x3UL, 0UL };
        AssertThat(AndroidGuestPlacement.WithoutPrime(new ulong[] { 0xFFUL, 0UL }, elite, all, result)).IsTrue();
        AssertThat(result[0]).IsEqual(0xFCUL);
    }

    /// <summary>`android_guest_real_clock`: off unless refii.toml says true.</summary>
    [TestCase]
    public void TheRealClockIsOffUnlessConfigured()
    {
        AssertThat(AndroidNativeSession.RealClockConfigured(new Dictionary<string, string>())).IsFalse();
        AssertThat(AndroidNativeSession.RealClockConfigured(new Dictionary<string, string> { [AndroidNativeSession.RealClockKey] = "false" })).IsFalse();
        AssertThat(AndroidNativeSession.RealClockConfigured(new Dictionary<string, string> { [AndroidNativeSession.RealClockKey] = "true" })).IsTrue();
    }
}
