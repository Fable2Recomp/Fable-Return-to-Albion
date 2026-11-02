namespace Refii.Tests;

using System;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;

[TestSuite]
public class DeviceClocksTests
{
    private static string FakeRoot(bool throttled)
    {
        string root = Path.Combine(Path.GetTempPath(), "refii-clocks-" + Guid.NewGuid().ToString("N"));
        void Write(string relative, string text)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text + "\n");
        }
        // Two clusters as on a Snapdragon 8 Elite.
        Write("sys/devices/system/cpu/cpufreq/policy0/scaling_cur_freq", "2918400");
        Write("sys/devices/system/cpu/cpufreq/policy0/scaling_max_freq", "3532800");
        Write("sys/devices/system/cpu/cpufreq/policy0/cpuinfo_max_freq", "3532800");
        Write("sys/devices/system/cpu/cpufreq/policy6/scaling_cur_freq", "1017600");
        Write("sys/devices/system/cpu/cpufreq/policy6/scaling_max_freq", throttled ? "2246400" : "4473600");
        Write("sys/devices/system/cpu/cpufreq/policy6/cpuinfo_max_freq", "4473600");
        Write("sys/class/kgsl/kgsl-3d0/devfreq/cur_freq", "525000000");
        Write("sys/class/kgsl/kgsl-3d0/max_clock_mhz", throttled ? "734" : "1200");
        Write("sys/class/kgsl/kgsl-3d0/freq_table_mhz", "1200 1100 1050 967 900 832 734 660 607 525 443 389 342 222 160 ");
        Write("sys/class/kgsl/kgsl-3d0/gpu_busy_percentage", "91 %");
        Write("sys/class/thermal/thermal_zone1/type", "cpu-0-0-0");
        Write("sys/class/thermal/thermal_zone1/temp", "61900");
        Write("sys/class/thermal/thermal_zone16/type", "cpu-1-0-0");
        Write("sys/class/thermal/thermal_zone16/temp", "76200");
        Write("sys/class/thermal/thermal_zone23/type", "gpuss-0");
        Write("sys/class/thermal/thermal_zone23/temp", "51300");
        Write("sys/class/thermal/thermal_zone61/type", "sdr0_pa");
        Write("sys/class/thermal/thermal_zone61/temp", "-273000");
        Write("sys/class/thermal/thermal_zone60/type", "battery");
        Write("sys/class/thermal/thermal_zone60/temp", "37600");
        return root;
    }

    [TestCase]
    public void TestThrottledPhoneReadsCapsAndTemperatures()
    {
        string root = FakeRoot(throttled: true);
        try
        {
            Engine_DeviceClocks.Snapshot s = Engine_DeviceClocks.Read(root);
            AssertThat(s.Clusters.Count).IsEqual(2);
            AssertThat(s.Clusters[1].CapKhz).IsEqual(2246400L);
            AssertThat(s.GpuMhz).IsEqual(525L);
            AssertThat(s.GpuCapMhz).IsEqual(734L);
            AssertThat(s.GpuMaxMhz).IsEqual(1200L);
            AssertThat(s.Throttled).IsTrue();
            string[] lines = Engine_DeviceClocks.Format(s).ToArray();
            AssertThat(lines[0]).IsEqual("cpu 2.9/3.5 · 1.0/2.2 GHz   gpu 525/734 MHz (max 1200)");
            AssertThat(lines[1]).IsEqual("cpu 76°C  gpu 51°C  battery 38°C  gpu busy 91%");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestCase]
    public void TestFullClocksAreNotThrottled()
    {
        string root = FakeRoot(throttled: false);
        try
        {
            Engine_DeviceClocks.Snapshot s = Engine_DeviceClocks.Read(root);
            AssertThat(s.Throttled).IsFalse();
            AssertThat(Engine_DeviceClocks.Format(s).First()).IsEqual("cpu 2.9/3.5 · 1.0/4.5 GHz   gpu 525/1200 MHz (max 1200)");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestCase]
    public void TestGpuClockFallsBackToClockMhzWhenDevfreqIsUnreadable()
    {
        string root = FakeRoot(throttled: false);
        try
        {
            // As on an Adreno 640 (Galaxy S10e): devfreq/cur_freq is root-only, clock_mhz is not.
            File.Delete(Path.Combine(root, "sys/class/kgsl/kgsl-3d0/devfreq/cur_freq"));
            File.WriteAllText(Path.Combine(root, "sys/class/kgsl/kgsl-3d0/clock_mhz"), "257\n");
            AssertThat(Engine_DeviceClocks.Read(root).GpuMhz).IsEqual(257L);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestCase]
    public void TestPlatformWithoutClocksShowsNothing()
    {
        string root = Path.Combine(Path.GetTempPath(), "refii-clocks-none-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Engine_DeviceClocks.Snapshot s = Engine_DeviceClocks.Read(root);
            AssertThat(s.Throttled).IsFalse();
            AssertThat(Engine_DeviceClocks.Format(s).Count()).IsEqual(0);
        }
        finally { Directory.Delete(root, true); }
    }

    private static Engine_DeviceClocks.Snapshot Cpus(params Engine_DeviceClocks.Cluster[] clusters) =>
        new(clusters, 0, 0, 0, -1, double.NaN, double.NaN, double.NaN);

    [TestCase]
    public void TestDesktopPerCpuPoliciesShowAsOneRange()
    {
        // A Ryzen 7 7800X3D: sixteen policies, one per logical CPU, all with the same limit.
        var cpus = Enumerable.Range(0, 16)
            .Select(i => new Engine_DeviceClocks.Cluster(i == 5 ? 4157660 : 4802301 + i * 1000, 5053377, 5053377))
            .ToArray();
        AssertThat(Engine_DeviceClocks.Format(Cpus(cpus)).Single()).IsEqual("cpu 4.2–4.8/5.1 GHz ×16");
    }

    [TestCase]
    public void TestHybridDesktopShowsOneRangePerCoreType()
    {
        var p = new Engine_DeviceClocks.Cluster(5_200_000, 5_400_000, 5_400_000);
        var e = new Engine_DeviceClocks.Cluster(3_900_000, 4_100_000, 4_100_000);
        var cpus = new[] { p, p, p, p, p, p, e, e, e, e, e, e, e, e };
        AssertThat(Engine_DeviceClocks.Format(Cpus(cpus)).Single())
            .IsEqual("cpu 5.2/5.4 GHz ×6 · 3.9/4.1 GHz ×8");
    }

    [TestCase]
    public void TestFourClustersAreStillListedOneByOne()
    {
        // A Snapdragon 8 Gen 2's four clusters, two of them sharing a maximum, keep their own entries.
        var cpus = new[]
        {
            new Engine_DeviceClocks.Cluster(1_800_000, 2_016_000, 2_016_000),
            new Engine_DeviceClocks.Cluster(2_500_000, 2_803_200, 2_803_200),
            new Engine_DeviceClocks.Cluster(2_400_000, 2_803_200, 2_803_200),
            new Engine_DeviceClocks.Cluster(3_000_000, 3_187_200, 3_187_200),
        };
        AssertThat(Engine_DeviceClocks.Format(Cpus(cpus)).Single()).IsEqual("cpu 1.8/2.0 · 2.5/2.8 · 2.4/2.8 · 3.0/3.2 GHz");
    }
}
