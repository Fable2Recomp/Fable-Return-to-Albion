using GdUnit4;
using Refii.Game;
using System.Collections.Generic;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`android_gc_big_core_thread`, `android_gc_nonblocking` and `android_gc_load_ceiling_mb`: the keys and the trigger.</summary>
[TestSuite]
public sealed class AndroidLoadCollectionTests
{
    /// <summary>Every arm is off unless refii.toml says otherwise, and the presets leave them alone.</summary>
    [TestCase]
    public void EveryArmIsOffUnlessConfigured()
    {
        var none = new Dictionary<string, string>();
        AssertThat(AndroidMemoryRelease.LoadPlanFrom(none)).IsEqual(default(AndroidMemoryRelease.LoadPlan));
        AssertThat(AndroidMemoryRelease.LoadPlanFrom(none).Any).IsFalse();
        foreach (string key in new[] { AndroidMemoryRelease.BigCoreThreadKey, AndroidMemoryRelease.NonBlockingKey, AndroidMemoryRelease.CeilingKey })
        {
            AssertThat(Refii.Launcher.Core.GraphicsPresets.LowValues.ContainsKey(key)).IsFalse();
            AssertThat(Refii.Launcher.Core.GraphicsPresets.HighValues.ContainsKey(key)).IsFalse();
        }
    }

    /// <summary>One line each.</summary>
    [TestCase]
    public void EachArmHasItsOwnKey()
    {
        AssertThat(AndroidMemoryRelease.LoadPlanFrom(new Dictionary<string, string> { [AndroidMemoryRelease.BigCoreThreadKey] = "true" }))
            .IsEqual(new AndroidMemoryRelease.LoadPlan(true, false, 0));
        AssertThat(AndroidMemoryRelease.LoadPlanFrom(new Dictionary<string, string> { [AndroidMemoryRelease.NonBlockingKey] = "true" }))
            .IsEqual(new AndroidMemoryRelease.LoadPlan(false, true, 0));
        AssertThat(AndroidMemoryRelease.LoadPlanFrom(new Dictionary<string, string> { [AndroidMemoryRelease.CeilingKey] = "1536" }))
            .IsEqual(new AndroidMemoryRelease.LoadPlan(false, false, 1536L << 20));
        AssertThat(AndroidMemoryRelease.LoadPlanFrom(new Dictionary<string, string> { [AndroidMemoryRelease.CeilingKey] = "-5" }).Any).IsFalse();
    }

    /// <summary>The ceiling and the floor: collects at either, and when memory cannot be read.</summary>
    [TestCase]
    public void TheCeilingWaitsForTheHeapOrThePhone()
    {
        long ceiling = 1536L << 20;
        long floor = AndroidMemoryRelease.LoadHeadroomFloorBytes;
        AssertThat(AndroidMemoryRelease.NearLimit(ceiling - 1, ceiling, floor)).IsFalse();
        AssertThat(AndroidMemoryRelease.NearLimit(ceiling, ceiling, floor + (1L << 30))).IsTrue();
        AssertThat(AndroidMemoryRelease.NearLimit(1L << 28, ceiling, floor - 1)).IsTrue();
        AssertThat(AndroidMemoryRelease.NearLimit(1L << 28, ceiling, -1)).IsTrue();
    }

    /// <summary>Headroom is available memory and free swap; no MemAvailable is unreadable, no swap is none.</summary>
    [TestCase]
    public void HeadroomIsAvailableAndSwap()
    {
        AssertThat(AndroidMemoryRelease.HeadroomBytes("MemTotal: 9 kB\nMemAvailable:    1000 kB\nSwapTotal: 5 kB\nSwapFree:   24 kB\n")).IsEqual(1024L * 1024);
        AssertThat(AndroidMemoryRelease.HeadroomBytes("MemAvailable:    1000 kB\n")).IsEqual(1000L * 1024);
        AssertThat(AndroidMemoryRelease.HeadroomBytes("MemTotal: 9 kB\nSwapFree: 24 kB\n")).IsEqual(-1L);
        AssertThat(AndroidMemoryRelease.HeadroomBytes("MemAvailable: lots\n")).IsEqual(-1L);
    }
}
