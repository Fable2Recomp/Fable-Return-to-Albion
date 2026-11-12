using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Refii.Recompile;

/// <summary>
/// Shared memory-aware job planning, Longest-Processing-Time-First (LPT) unit ordering, and parallel compilation
/// scheduling used by both Desktop (<c>GameCodeBuild</c>) and Android (<c>AndroidPhoneCompiler</c>).
/// </summary>
public static class CompileScheduler
{
    /// <summary>Estimated peak working set of one Clang invocation on Android (512 MiB).</summary>
    public const long AndroidJobMemoryBytes = 512L * 1024 * 1024;

    /// <summary>Minimum free RAM reserved for Android OS and foreground UI while compilers run (1 GiB).</summary>
    public const long AndroidFreeMemoryFloorBytes = 1024L * 1024 * 1024;

    /// <summary>Maximum simultaneous compiler processes on Android.</summary>
    public const int AndroidMostJobs = 8;

    /// <summary>Estimated working set budget per concurrent Desktop Clang worker (600 MiB).</summary>
    public const long DesktopJobMemoryBytes = 600L * 1024 * 1024;

    /// <summary>Minimum free RAM reserved for Desktop OS and apps while compilers run (1 GiB).</summary>
    public const long DesktopFreeMemoryFloorBytes = 1024L * 1024 * 1024;

    private const int MemoryRecheckMilliseconds = 1000;
    private const int KilledUnitRetries = 3;

    /// <summary>
    /// Orders translation unit indices descending by source file byte length (breaking ties ascending by unit index),
    /// ensuring massive translation units such as <c>refii_register.cpp</c> start immediately on parallel builds
    /// rather than stalling a single core at the tail of the build.
    /// </summary>
    /// <param name="unitIndices">Candidate unit indices to compile.</param>
    /// <param name="allUnitPaths">Full list of source paths indexed by unit slot.</param>
    /// <returns>Ordered list of unit indices to dispatch.</returns>
    public static List<int> OrderUnitsLargestFirst(IReadOnlyList<int> unitIndices, IReadOnlyList<string> allUnitPaths)
    {
        if (unitIndices.Count <= 1)
        {
            return unitIndices.ToList();
        }

        var sized = new List<(int Index, long Size)>(unitIndices.Count);
        foreach (int index in unitIndices)
        {
            long size = 0;
            if ((uint)index < (uint)allUnitPaths.Count)
            {
                try
                {
                    var info = new FileInfo(allUnitPaths[index]);
                    if (info.Exists)
                    {
                        size = info.Length;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }

            sized.Add((index, size));
        }

        return sized
            .OrderByDescending(entry => entry.Size)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Index)
            .ToList();
    }

    /// <summary>Plans the initial number of simultaneous Android compiler jobs from core count and <c>MemAvailable</c>.</summary>
    /// <param name="cores">Available logical processor count.</param>
    /// <param name="availableBytes">Available physical memory in bytes, or negative when unknown.</param>
    /// <returns>Bounded job count in <c>[1, 8]</c>.</returns>
    public static int PlanAndroidJobs(int cores, long availableBytes) =>
        availableBytes < 0
            ? 1
            : (int)Math.Clamp(
                (availableBytes - AndroidFreeMemoryFloorBytes) / AndroidJobMemoryBytes,
                1,
                Math.Max(1, Math.Min(cores, AndroidMostJobs)));

    /// <summary>
    /// Plans the number of simultaneous Desktop compiler jobs from logical core count, user preference, and free RAM.
    /// </summary>
    /// <param name="cores">Available logical processor count.</param>
    /// <param name="availableBytes">Available physical memory in bytes, or negative when unknown.</param>
    /// <param name="useAllCores">When true, targets all logical cores; when false, leaves 2 logical threads for desktop UI.</param>
    /// <returns>Bounded job count of at least 1.</returns>
    public static int PlanDesktopJobs(int cores, long availableBytes, bool useAllCores)
    {
        int safeCores = Math.Max(1, cores);
        int cpuTarget = useAllCores
            ? safeCores
            : safeCores <= 2 ? 1 : safeCores - 2;

        if (availableBytes <= 0)
        {
            return cpuTarget;
        }

        long memoryJobs = Math.Max(1L, (availableBytes - DesktopFreeMemoryFloorBytes) / DesktopJobMemoryBytes);
        return (int)Math.Clamp(memoryJobs, 1L, cpuTarget);
    }

    /// <summary>Queries currently available physical memory in bytes on Windows, Linux, or Android, or <c>-1</c> when unavailable.</summary>
    /// <returns>Available physical RAM in bytes, or <c>-1</c>.</returns>
    public static long AvailableMemoryBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                if (GlobalMemoryStatusEx(ref status) && status.ullAvailPhys > 0)
                {
                    return status.ullAvailPhys > long.MaxValue ? long.MaxValue : (long)status.ullAvailPhys;
                }
            }
            catch
            {
            }

            return -1;
        }

        try
        {
            if (File.Exists("/proc/meminfo"))
            {
                foreach (string line in File.ReadLines("/proc/meminfo"))
                {
                    if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    return parts.Length > 1 && long.TryParse(parts[1], out long kb) ? kb * 1024 : -1;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        return -1;
    }

    /// <summary>
    /// Executes compilation units in parallel up to <paramref name="jobs"/>, holding back additional workers when free
    /// memory drops below <paramref name="memoryThresholdBytes"/> and automatically halving concurrency if the OS kills
    /// a compiler process due to memory pressure (<c>exit &gt;= 128</c>).
    /// </summary>
    /// <param name="units">Ordered unit indices to compile.</param>
    /// <param name="jobs">Maximum concurrent compiler processes.</param>
    /// <param name="memory">Callback returning current free memory in bytes.</param>
    /// <param name="compile">Async callback compiling one unit index and returning its exit status.</param>
    /// <param name="completed">Callback invoked sequentially as each unit succeeds.</param>
    /// <param name="failure">Factory producing the exception to raise on a genuine non-zero exit code.</param>
    /// <param name="cancellation">Cancellation token stopping active and queued work.</param>
    /// <param name="memoryThresholdBytes">Minimum free memory required to launch additional concurrent workers.</param>
    /// <param name="waitWhilePaused">Optional pause gate awaited before launching each unit.</param>
    public static async Task RunUnitsAsync(
        IReadOnlyList<int> units,
        int jobs,
        Func<long> memory,
        Func<int, CancellationToken, Task<int>> compile,
        Action<int> completed,
        Func<int, int, Exception> failure,
        CancellationToken cancellation,
        long memoryThresholdBytes = AndroidFreeMemoryFloorBytes + AndroidJobMemoryBytes,
        Func<CancellationToken, Task>? waitWhilePaused = null)
    {
        if (units.Count == 0)
        {
            cancellation.ThrowIfCancellationRequested();
            return;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var waiting = new LinkedList<int>(units);
        var running = new Dictionary<Task<int>, int>();
        var killed = new Dictionary<int, int>();
        int limit = Math.Max(1, jobs);
        Exception? error = null;

        while (running.Count > 0 || (waiting.Count > 0 && error == null))
        {
            bool held = false;
            while (error == null && waiting.Count > 0 && running.Count < limit)
            {
                cancellation.ThrowIfCancellationRequested();
                if (waitWhilePaused != null)
                {
                    await waitWhilePaused(cancellation).ConfigureAwait(false);
                }

                if (running.Count > 0 && memory() < memoryThresholdBytes)
                {
                    held = true;
                    break;
                }

                int unit = waiting.First!.Value;
                waiting.RemoveFirst();
                running[compile(unit, stop.Token)] = unit;
            }

            if (running.Count == 0)
            {
                break;
            }

            Task any = Task.WhenAny(running.Keys);
            if (held)
            {
                await Task.WhenAny(any, Task.Delay(MemoryRecheckMilliseconds, stop.Token)).ConfigureAwait(false);
            }
            else
            {
                await any.ConfigureAwait(false);
            }

            foreach (Task<int> task in running.Keys.Where(task => task.IsCompleted).ToList())
            {
                int unit = running[task];
                running.Remove(task);
                try
                {
                    int status = await task.ConfigureAwait(false);
                    if (status == 0)
                    {
                        completed(unit);
                    }
                    else if (status >= 128 && !stop.IsCancellationRequested && killed.GetValueOrDefault(unit) < KilledUnitRetries)
                    {
                        killed[unit] = killed.GetValueOrDefault(unit) + 1;
                        limit = Math.Max(1, limit / 2);
                        waiting.AddFirst(unit);
                    }
                    else
                    {
                        error ??= failure(unit, status);
                    }
                }
                catch (Exception thrown)
                {
                    error ??= thrown;
                }

                if (error != null)
                {
                    stop.Cancel();
                }
            }
        }

        cancellation.ThrowIfCancellationRequested();
        if (error != null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
