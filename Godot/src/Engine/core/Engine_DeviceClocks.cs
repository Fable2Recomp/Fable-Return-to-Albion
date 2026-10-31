using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Refii.Core;

/// <summary>
/// The CPU and GPU clocks and the caps the OS has put on them, read from sysfs, for the F5
/// readout: a phone that has warmed up runs at a fraction of its clocks, and the frame rate it
/// shows means nothing without them.
/// </summary>
/// <remarks>
/// Linux and Android only; elsewhere, or where a file is unreadable, the parts are missing and
/// the readout leaves them out. A cap below the hardware maximum is the thermal governor
/// throttling: the CPU's `scaling_max_freq` against `cpuinfo_max_freq`, and Adreno's
/// `max_clock_mhz` against the top of its frequency table. Temperatures are the hottest CPU
/// and GPU zones and the battery; zone names follow the Qualcomm kernels' (`cpu-*`, `gpuss-*`).
/// </remarks>
public static class Engine_DeviceClocks
{
    /// <summary>One CPU cluster: kHz now, the cap the governor allows, the hardware maximum.</summary>
    public readonly record struct Cluster(long CurKhz, long CapKhz, long MaxKhz);

    /// <summary>A snapshot; GPU fields are 0 and temperatures NaN where unavailable.</summary>
    public sealed record Snapshot(IReadOnlyList<Cluster> Clusters, long GpuMhz, long GpuCapMhz, long GpuMaxMhz,
        int GpuBusyPercent, double CpuCelsius, double GpuCelsius, double BatteryCelsius)
    {
        /// <summary>Whether any clock is held below its hardware maximum.</summary>
        public bool Throttled => Clusters.Any(c => c.CapKhz > 0 && c.CapKhz < c.MaxKhz)
            || (GpuCapMhz > 0 && GpuMaxMhz > 0 && GpuCapMhz < GpuMaxMhz);
    }

    /// <summary>A phone has at most four CPU clusters; more policies than this are a desktop's per-CPU ones.</summary>
    private const int MaxListedClusters = 4;

    private static string[]? _cpuZones, _gpuZones, _batteryZones;
    private static string? _zonesRoot;

    /// <summary>Reads the current clocks under <paramref name="root"/> ("/" on a device).</summary>
    public static Snapshot Read(string root = "/")
    {
        var clusters = new List<Cluster>();
        string cpufreq = Path.Combine(root, "sys/devices/system/cpu/cpufreq");
        if (Directory.Exists(cpufreq))
        {
            foreach (string policy in Directory.GetDirectories(cpufreq, "policy*")
                .OrderBy(p => Number(Path.GetFileName(p)["policy".Length..])))
            {
                long cur = Long(Path.Combine(policy, "scaling_cur_freq"));
                long max = Long(Path.Combine(policy, "cpuinfo_max_freq"));
                if (cur > 0 && max > 0)
                {
                    clusters.Add(new Cluster(cur, Long(Path.Combine(policy, "scaling_max_freq")), max));
                }
            }
        }

        string kgsl = Path.Combine(root, "sys/class/kgsl/kgsl-3d0");
        long gpu = Long(Path.Combine(kgsl, "devfreq/cur_freq")) / 1_000_000;
        // Older Adreno kernels (the S10e's 640) keep devfreq root-only but publish clock_mhz.
        if (gpu == 0) gpu = Long(Path.Combine(kgsl, "clock_mhz"));
        long cap = Long(Path.Combine(kgsl, "max_clock_mhz"));
        long top = Text(Path.Combine(kgsl, "freq_table_mhz")).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(Number).DefaultIfEmpty(0).Max();
        string busy = Text(Path.Combine(kgsl, "gpu_busy_percentage")).Trim().TrimEnd('%').Trim();

        FindZones(root);
        return new Snapshot(clusters, gpu, cap, top, int.TryParse(busy, out int b) ? b : -1,
            Hottest(_cpuZones!), Hottest(_gpuZones!), Hottest(_batteryZones!));
    }

    /// <summary>The two readout lines, or none when the platform publishes no clocks.</summary>
    public static IEnumerable<string> Format(Snapshot s)
    {
        if (s.Clusters.Count == 0 && s.GpuMhz == 0)
        {
            yield break;
        }

        var line = new StringBuilder();
        if (s.Clusters.Count > MaxListedClusters)
        {
            // A desktop kernel gives every logical CPU its own policy, 16 on an 8-core Ryzen, and
            // one entry each ran the line off the panel. Policies with the same limit become one
            // range: "cpu 4.1–4.8/5.1 GHz ×16", or one range per core type on a hybrid CPU.
            line.Append("cpu ").Append(string.Join(" · ", s.Clusters
                .GroupBy(c => (Limit: c.CapKhz > 0 ? c.CapKhz : c.MaxKhz, c.MaxKhz))
                .Select(g =>
                {
                    string low = Ghz(g.Min(c => c.CurKhz)), high = Ghz(g.Max(c => c.CurKhz));
                    string now = low == high ? high : low + "–" + high;
                    return now + "/" + Ghz(g.Key.Limit) + " GHz ×" + g.Count().ToString(CultureInfo.InvariantCulture);
                })));
        }
        else if (s.Clusters.Count > 0)
        {
            line.Append("cpu ").Append(string.Join(" · ", s.Clusters.Select(c =>
                Ghz(c.CurKhz) + "/" + Ghz(c.CapKhz > 0 ? c.CapKhz : c.MaxKhz)))).Append(" GHz");
        }
        if (s.GpuMhz > 0)
        {
            if (line.Length > 0) line.Append("   ");
            line.Append(CultureInfo.InvariantCulture, $"gpu {s.GpuMhz}");
            if (s.GpuCapMhz > 0) line.Append(CultureInfo.InvariantCulture, $"/{s.GpuCapMhz}");
            line.Append(" MHz");
            if (s.GpuMaxMhz > 0) line.Append(CultureInfo.InvariantCulture, $" (max {s.GpuMaxMhz})");
        }
        if (s.Throttled) line.Append("   [throttled]");
        yield return line.ToString();

        var heat = new StringBuilder();
        if (!double.IsNaN(s.CpuCelsius)) heat.Append(CultureInfo.InvariantCulture, $"cpu {s.CpuCelsius:0}°C  ");
        if (!double.IsNaN(s.GpuCelsius)) heat.Append(CultureInfo.InvariantCulture, $"gpu {s.GpuCelsius:0}°C  ");
        if (!double.IsNaN(s.BatteryCelsius)) heat.Append(CultureInfo.InvariantCulture, $"battery {s.BatteryCelsius:0}°C  ");
        if (s.GpuBusyPercent >= 0) heat.Append(CultureInfo.InvariantCulture, $"gpu busy {s.GpuBusyPercent}%");
        if (heat.Length > 0) yield return heat.ToString().TrimEnd();
    }

    /// <summary>Sorts the thermal zones once per root: their names do not change while running.</summary>
    private static void FindZones(string root)
    {
        if (_zonesRoot == root && _cpuZones is not null) return;
        var cpu = new List<string>(); var gpu = new List<string>(); var battery = new List<string>();
        string thermal = Path.Combine(root, "sys/class/thermal");
        if (Directory.Exists(thermal))
        {
            foreach (string zone in Directory.GetDirectories(thermal, "thermal_zone*"))
            {
                string type = Text(Path.Combine(zone, "type")).Trim();
                string temp = Path.Combine(zone, "temp");
                if (type.StartsWith("cpu-", StringComparison.Ordinal)) cpu.Add(temp);
                else if (type.StartsWith("gpuss-", StringComparison.Ordinal)) gpu.Add(temp);
                else if (type == "battery") battery.Add(temp);
            }
        }
        (_cpuZones, _gpuZones, _batteryZones, _zonesRoot) = (cpu.ToArray(), gpu.ToArray(), battery.ToArray(), root);
    }

    /// <summary>The hottest plausible reading in degrees Celsius; zones report millidegrees.</summary>
    private static double Hottest(string[] zones)
    {
        double hottest = double.NaN;
        foreach (string zone in zones)
        {
            long milli = Long(zone, long.MinValue);
            if (milli is > -40_000 and < 150_000 && (double.IsNaN(hottest) || milli / 1000.0 > hottest))
                hottest = milli / 1000.0;
        }
        return hottest;
    }

    private static string Ghz(long khz) => (khz / 1e6).ToString("0.0", CultureInfo.InvariantCulture);

    private static long Number(string text) =>
        long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : 0;

    private static long Long(string path, long fallback = 0)
    {
        string text = Text(path).Trim();
        return text.Length != 0 && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)
            ? n : fallback;
    }

    private static string Text(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
