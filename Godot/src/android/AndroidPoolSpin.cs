using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// <c>android_pool_spin</c>: how long an idle .NET thread-pool worker spins before it sleeps.
/// </summary>
/// <remarks>
/// WHY. A pool worker that runs out of work spins on the pool's semaphore before it blocks, so work
/// that arrives within that window starts without a wake. On ARM64 .NET 9 spins 280 rounds (70 on
/// x64), each a short pause or a sched_yield, and NativeAOT reads a clock in every pause. The game
/// hands the pool a burst of pose and cloth work each frame and nothing for the rest of it, so the
/// five workers spin out the window after nearly every burst: on the S10e standing at Bower Lake,
/// 1,993 of 72,897 samples (2.7%) were in that wait, 1,409 of them spinning.
///
/// HOW. The runtime reads the limit once, from the AppContext value
/// System.Threading.ThreadPool.UnfairSemaphoreSpinLimit, when the pool first starts a worker
/// (PortableThreadPool.WorkerThread's static semaphore). .NET 9 has no environment alias for it,
/// and a runtimeconfig option would be compiled into the library, so the value is set here, from
/// refii.toml, in a module initializer: the earliest managed code, before anything queues work.
/// It cannot change afterwards, so a load spins as little as play does; a load keeps its workers
/// busy from a full queue, where the spin is not reached.
///
/// THE VALUE. "auto" spins <see cref="LowTierLimit"/> rounds in the low-memory tier and leaves the
/// runtime's own limit elsewhere; a number from 0 sets that limit; "default" leaves it. Less
/// spinning costs a wake (a futex call) for work that would have arrived inside the window.
/// </remarks>
public static class AndroidPoolSpin
{
    /// <summary>The refii.toml key.</summary>
    public const string Key = "android_pool_spin";

    /// <summary>The runtime's own name for the limit, read through AppContext.GetData.</summary>
    public const string RuntimeKnob = "System.Threading.ThreadPool.UnfairSemaphoreSpinLimit";

    /// <summary>The limit "auto" gives the low-memory tier: an eighth of .NET 9's ARM64 default of 280.</summary>
    public const int LowTierLimit = 35;

    /// <summary>The limit set at start, or null when the runtime's own is in force.</summary>
    public static int? Applied { get; private set; }

    /// <summary>What the start did, for the log; set once by <see cref="Apply"/>.</summary>
    public static string Outcome { get; private set; } = "not read";

    /// <summary>
    /// The limit a setting asks for: null to leave the runtime's own.
    /// </summary>
    /// <param name="value">The key's value; null or empty reads as "auto".</param>
    /// <param name="lowMemoryTier">Whether the low-memory tier is on.</param>
    public static int? Resolve(string? value, bool lowMemoryTier)
    {
        string text = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0 || text == "auto")
            return lowMemoryTier ? LowTierLimit : null;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int limit))
            return limit;
        return null;
    }

    /// <summary>The refii.toml the launcher writes for this app: the same file the GC preinit reads.</summary>
    /// <param name="cmdline">/proc/self/cmdline.</param>
    /// <returns>Null without a package name.</returns>
    public static string? ConfigPathFor(string cmdline)
    {
        int end = cmdline.IndexOfAny(new[] { '\0', ':' });
        string package = end < 0 ? cmdline : cmdline[..end];
        if (package.Length == 0 || package.Contains('/'))
            return null;
        return $"/data/data/{package}/files/runtime/refii.toml";
    }

    /// <summary>Sets the limit from refii.toml before any pool work; a phone build only.</summary>
#pragma warning disable CA2255 // Must precede the pool's first worker, which reads the value once.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Apply()
    {
        if (!Engine_Platform.IsLinuxBionic)
            return;

        try
        {
            string? path = ConfigPathFor(System.IO.File.ReadAllText("/proc/self/cmdline"));
            IReadOnlyDictionary<string, string> values = path is null
                ? new Dictionary<string, string>()
                : Refii.Launcher.Core.RuntimeConfig.ReadValues(path);
            values.TryGetValue(Key, out string? value);
            values.TryGetValue(Refii.Game.AndroidMemoryRelease.LowRamModeKey, out string? tier);
            bool low = Refii.Game.AndroidMemoryRelease.ResolveLowRamMode(
                (tier ?? "auto").Trim().ToLowerInvariant(), Refii.Game.AndroidMemoryRelease.LowRamPhone());
            Applied = Resolve(value, low);
            if (Applied is int limit)
                AppContext.SetData(RuntimeKnob, limit);
            Outcome = $"{(Applied is int set ? set + " rounds" : "the runtime's own")} " +
                $"({Key} = {value ?? "auto"}, low-memory tier {(low ? "on" : "off")})";
        }
        catch (Exception error)
        {
            Outcome = "the runtime's own (" + error.GetType().Name + ")";
        }
    }
}
