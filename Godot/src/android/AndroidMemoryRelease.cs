using System;
using Godot;

namespace Refii.Game;

/// <summary>
/// `android_memory_release`: gives memory the game has finished with back during a level build,
/// for a phone that runs out of memory while one is under way.
/// </summary>
/// <remarks>
/// The .NET GC keeps a build's garbage and the gaps a departed region left until something makes
/// it collect, and the post-load compaction comes only at the END of a build, after its peak. So
/// a build collects as it grows (<see cref="PumpLoad"/>), and <see cref="GameWorld"/>'s region
/// retirement compacts in its last pass.
/// </remarks>
public static class AndroidMemoryRelease
{
    /// <summary>The setting that turns the release off.</summary>
    public const string Key = "android_memory_release";

    /// <summary>The managed growth during a level build that asks for a collection.</summary>
    public const long LoadStepBytes = 200L << 20;

    /// <summary>The least time between two collections during a level build.</summary>
    public const double LoadCollectionGapSeconds = 3;

    /// <summary>The setting that runs a level build's forced collection on its own thread on the faster cores.</summary>
    public const string BigCoreThreadKey = "android_gc_big_core_thread";

    /// <summary>The setting that makes a level build's forced collection a background one.</summary>
    public const string NonBlockingKey = "android_gc_nonblocking";

    /// <summary>The setting that holds a level build's forced collection back until the memory asks for it, in MB; 0 is off.</summary>
    public const string CeilingKey = "android_gc_load_ceiling_mb";

    /// <summary>
    /// Under this much <c>MemAvailable + SwapFree</c>, <see cref="NearLimit"/> collects whatever
    /// the heap's size. Fresh loads on a 5.33 GiB phone reached lows of about 160-170 MB in the two
    /// that were killed and 217 MB in one that took 57 s; those that collected reached 1080-1270 MB
    /// on the same build and 2000-2570 MB on later ones. Managed memory grows by 35-130 MB a second
    /// in a load, and this is read once a second.
    /// </summary>
    public const long LoadHeadroomFloorBytes = 1200L << 20;

    private static long _loadBaseline = -1;
    private static long _loadChecked;
    private static long _loadCollected;
    private static bool _loadWanted;
    private static LoadPlan _loadPlan;
    private static int _loadStepsPassed;
    private static long _loadLowestHeadroom;
    private static System.Threading.Tasks.Task? _loadCollection;

    /// <summary>How a level build runs its forced collection: all off is the default.</summary>
    /// <param name="BigCoreThread"><see cref="BigCoreThreadKey"/>.</param>
    /// <param name="NonBlocking"><see cref="NonBlockingKey"/>.</param>
    /// <param name="CeilingBytes"><see cref="CeilingKey"/>, in bytes.</param>
    public readonly record struct LoadPlan(bool BigCoreThread, bool NonBlocking, long CeilingBytes)
    {
        /// <summary>Whether anything differs from the default.</summary>
        public bool Any => BigCoreThread || NonBlocking || CeilingBytes > 0;
    }

    /// <summary>The plan refii.toml asks for.</summary>
    public static LoadPlan LoadPlanFrom(System.Collections.Generic.IReadOnlyDictionary<string, string> values) => new(
        Refii.Core.Engine_RefiiConfig.Bool(values, BigCoreThreadKey, false),
        Refii.Core.Engine_RefiiConfig.Bool(values, NonBlockingKey, false),
        Math.Max(0L, Refii.Core.Engine_RefiiConfig.Int(values, CeilingKey, 0)) << 20);

    private static LoadPlan ReadLoadPlan()
    {
        try
        {
            return LoadPlanFrom(Refii.Core.Engine_RefiiConfig.Read(out _));
        }
        catch (Exception)
        {
            return default;
        }
    }

    /// <summary>
    /// One frame of a level build: a full collection once the managed heap has grown a step.
    /// </summary>
    /// <remarks>
    /// A build's decode buffers are garbage by the hundreds of MB, and whether a full collection
    /// comes in time was left to the runtime. On a OnePlus 7 Pro the same Bower Lake load peaked at
    /// about 1.5 GB of GC heap with two gen2 collections 15-25 s in, and at 2.2-2.4 GB with none,
    /// which took the phone out of memory; the high-load threshold (97%, GraphicsPresets) is only met
    /// as it runs out. The collection does not compact, so a pause stays near a gen2's 100-300 ms.
    /// It runs off the main thread, under the loading screen.
    /// </remarks>
    /// <param name="loading">Whether a world build holds the game (<see cref="GameWorld.WorldBuildHeld"/>).</param>
    public static void PumpLoad(bool loading)
    {
        if (!loading)
        {
            if (_loadBaseline >= 0 && _loadPlan.CeilingBytes > 0)
                GD.Print($"[memory-release] level build: {_loadStepsPassed} step(s) under the {_loadPlan.CeilingBytes >> 20} MB ceiling passed without a collection, " +
                    $"lowest headroom read {(_loadLowestHeadroom == long.MaxValue ? "none" : $"{_loadLowestHeadroom >> 20} MB")}");
            _loadBaseline = -1;
            return;
        }

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_loadBaseline < 0)
        {
            _loadBaseline = GC.GetTotalMemory(false);
            _loadCollected = _loadChecked = now;
            _loadWanted = Wanted();
            _loadPlan = ReadLoadPlan();
            _loadStepsPassed = 0;
            _loadLowestHeadroom = long.MaxValue;
            if (_loadPlan.Any)
                GD.Print($"[memory-release] level build plan: {_loadPlan}");
            return;
        }

        if (!_loadWanted || _loadCollection is { IsCompleted: false }) return;
        if (now - _loadChecked < System.Diagnostics.Stopwatch.Frequency) return;
        _loadChecked = now;
        long managed = GC.GetTotalMemory(false);
        double sinceCollection = (now - _loadCollected) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (managed < _loadBaseline) _loadBaseline = managed;
        if (!ShouldCollect(managed, _loadBaseline, sinceCollection)) return;
        if (_loadPlan.CeilingBytes > 0)
        {
            long headroom = ReadHeadroomBytes();
            if (headroom >= 0) _loadLowestHeadroom = Math.Min(_loadLowestHeadroom, headroom);
            if (!NearLimit(managed, _loadPlan.CeilingBytes, headroom))
            {
                ++_loadStepsPassed;
                return;
            }
        }

        _loadCollected = now;
        LoadPlan plan = _loadPlan;
        _loadCollection = plan.BigCoreThread
            ? RunOnBigCoreThread(plan, managed)
            : System.Threading.Tasks.Task.Run(() => CollectForLoad(plan, managed, string.Empty));
    }

    /// <summary>One forced collection of a level build, on the thread it is to run on.</summary>
    /// <param name="plan">How.</param>
    /// <param name="managed">Managed bytes when it was asked for.</param>
    /// <param name="placed">Where the thread was put, for the log; empty when it was not.</param>
    private static void CollectForLoad(LoadPlan plan, long managed, string placed)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (plan.NonBlocking)
        {
            // The sweep frees it later, so there is no "after" yet: PumpLoad lowers the baseline as
            // the count falls. While a background collection runs, this call does nothing.
            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
            System.Threading.Volatile.Write(ref _loadBaseline, managed);
            GD.Print($"[memory-release] level build: background collection asked for at {managed / 1048576} MB, call took " +
                $"{System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms{placed}");
            return;
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        long after = GC.GetTotalMemory(false);
        System.Threading.Volatile.Write(ref _loadBaseline, after);
        GD.Print($"[memory-release] level build: collected {managed / 1048576} -> {after / 1048576} MB live in " +
            $"{System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms{placed}");
    }

    /// <summary>
    /// `android_gc_big_core_thread`: the collection on a thread of its own, held on the faster cores.
    /// </summary>
    /// <remarks>
    /// A blocking collection runs on its caller, so this picks its core; a background one's thread
    /// is made from its caller when none is alive, and inherits the same. A refused placement only
    /// costs the placement. Started from the main thread, so a nice is set rather than inherited.
    /// </remarks>
    private static System.Threading.Tasks.Task RunOnBigCoreThread(LoadPlan plan, long managed)
    {
        var done = new System.Threading.Tasks.TaskCompletionSource();
        new System.Threading.Thread(() =>
        {
            try
            {
                string placed = Refii.AndroidRuntime.AndroidGuestPlacement.PlaceLoadCollectionThread(plan.NonBlocking);
                CollectForLoad(plan, managed, $" ({placed}, began on cpu{System.Threading.Thread.GetCurrentProcessorId()})");
            }
            catch (Exception error)
            {
                GD.Print($"[memory-release] level build: the collection thread failed: {error.Message}");
            }
            finally
            {
                done.SetResult();
            }
        }) { IsBackground = true, Name = Refii.AndroidRuntime.AndroidGuestPlacement.LoadCollectionThreadName }.Start();
        return done.Task;
    }

    /// <summary>Whether a level build's managed growth asks for a collection now.</summary>
    /// <param name="managed">Managed bytes now.</param>
    /// <param name="baseline">Managed bytes after the last collection, or at the build's start.</param>
    /// <param name="sinceCollection">Seconds since the last one.</param>
    public static bool ShouldCollect(long managed, long baseline, double sinceCollection) =>
        managed - baseline >= LoadStepBytes && sinceCollection >= LoadCollectionGapSeconds;

    /// <summary>
    /// `android_gc_load_ceiling_mb`: whether a step of growth (<see cref="ShouldCollect"/>) is worth
    /// a collection yet: the heap has reached the ceiling, or the phone's memory is short.
    /// </summary>
    /// <remarks>
    /// The fixed step exists for the phone that dies at 2.2-2.4 GB of heap, and fires as often on
    /// one with 4 GB spare. What endangers a phone is its own memory, so this reads it; a reading
    /// that fails counts as short, which is the step's own behaviour.
    /// </remarks>
    /// <param name="managed">Managed bytes now: what survived the last collection plus the growth since.</param>
    /// <param name="ceiling">The heap this phone may reach regardless.</param>
    /// <param name="headroom"><see cref="HeadroomBytes"/>, negative when unreadable.</param>
    public static bool NearLimit(long managed, long ceiling, long headroom) =>
        managed >= ceiling || headroom < LoadHeadroomFloorBytes;

    /// <summary>/proc/meminfo's MemAvailable plus SwapFree in bytes, or -1 when MemAvailable is absent or malformed.</summary>
    /// <remarks>Swap is counted at face value, though zram pays for it in RAM at a fraction: the
    /// floor was read from this same sum.</remarks>
    /// <param name="meminfo">The file's text.</param>
    public static long HeadroomBytes(string meminfo)
    {
        long available = MeminfoKilobytes(meminfo, "MemAvailable:");
        return available <= 0 ? -1 : (available + MeminfoKilobytes(meminfo, "SwapFree:")) * 1024;
    }

    private static long ReadHeadroomBytes()
    {
        try
        {
            return HeadroomBytes(System.IO.File.ReadAllText("/proc/meminfo"));
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>
    /// Once a region has loaded, drops the CPU copies of its scenery's meshes
    /// (<see cref="Refii.Models.Engine_MdlSurfaceArrays"/>).
    /// </summary>
    /// <remarks>
    /// They exist so a later reader need not stall on a GPU readback. A region's readers are its
    /// build (bakes, occluders, caster merges), and until the next region they sit cold. Characters'
    /// meshes are outside the region's tree and keep theirs: they are built from their sources in
    /// play. A reader that does come later, such as equipment sharing a prop's cached mesh, reads
    /// back once and is logged.
    /// </remarks>
    /// <param name="region">The loaded region's root.</param>
    public static void ForgetMeshCopies(Node region)
    {
        if (!Wanted()) return;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var pending = new System.Collections.Generic.Stack<Node>();
        var seen = new System.Collections.Generic.HashSet<ulong>();
        pending.Push(region);
        int nodes = 0;
        while (pending.Count > 0)
        {
            Node node = pending.Pop();
            ++nodes;
            Mesh? mesh = node switch
            {
                MeshInstance3D single => single.Mesh,
                MultiMeshInstance3D many => many.Multimesh?.Mesh,
                _ => null,
            };
            if (mesh is not null && seen.Add(mesh.GetInstanceId())) Refii.Models.Engine_MdlSurfaceArrays.Forget(mesh);
            int children = node.GetChildCount();
            for (int i = 0; i < children; ++i) pending.Push(node.GetChild(i));
        }
        GD.Print($"[memory-release] dropped the CPU copies of {seen.Count} mesh(es) across {nodes} node(s) in " +
            $"{System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
    }

    private static readonly Lazy<bool> ForgetsClothParts = new(() => Refii.Core.Engine_Platform.IsAndroid && Wanted());

    /// <summary>
    /// A character's cloth part, through the prop cache as on desktop, without its CPU copies on a phone.
    /// </summary>
    /// <remarks>
    /// The cloth reads only the part's metadata (source vertex map, rig) and its surfaces' sizes and
    /// materials, none of which needs the arrays; a composite is built from its own loads
    /// (GameEntities.LoadPart), not these. A later reader of the same cached part reads back once and
    /// is logged. 7.5 MB at Bower Lake on a OnePlus 7 Pro (native heap sample).
    /// </remarks>
    public static ArrayMesh? ResolveClothPart(string path)
    {
        ArrayMesh? part = Refii.Level.Engine_PropStreamer.Resolve(path);
        if (part is not null && ForgetsClothParts.Value) Refii.Models.Engine_MdlSurfaceArrays.Forget(part);
        return part;
    }

    /// <summary>Under this much memory a phone counts as short of it: below the 8 GB phones' 7.6 GB.</summary>
    public const long LowRamKilobytes = 7L * 1024 * 1024;

    /// <summary>The setting that decides the low-memory tier: "auto", "on" or "off".</summary>
    public const string LowRamModeKey = "android_low_ram_mode";

    /// <summary>The decoded hero texture sources kept in the low-memory tier, in MB, when none is set.</summary>
    public const int LowRamHeroSourceMegabytes = 32;

    private static readonly Lazy<bool> LowRam = new(ReadLowRamMode);

    /// <summary>
    /// The low-memory tier, for what costs a little picture or waiting and is worth it only on a
    /// phone that needs the room: smaller normal maps (<see cref="Refii.Textures.AndroidTextureUpload"/>),
    /// menus built when first opened (<see cref="Refii.Ui.Engine_UiBookPrebuild"/>), no shader folds
    /// (Engine_ShaderSpecialisation) and a bound on the hero's decoded texture sources. On by the
    /// phone's memory, and on any phone under the Low preset, which sets it. Read once, when the game starts.
    /// </summary>
    public static bool LowRamMode => LowRam.Value;

    /// <summary>
    /// Whether a level entity's animator keeps one clip rather than its default. Nothing in play
    /// plays that library (the guest's clips drive the rig), and one clip still binds the skin.
    /// Every phone.
    /// </summary>
    public static bool LevelAnimatorOneClip => Refii.Core.Engine_Platform.IsAndroid;

    private static bool ReadLowRamMode()
    {
        if (!Refii.Core.Engine_Platform.IsAndroid) return false;
        string mode = "auto";
        try
        {
            if (Refii.Core.Engine_RefiiConfig.Read(out _).TryGetValue(LowRamModeKey, out string? value) && value is not null)
                mode = value.Trim().ToLowerInvariant();
        }
        catch (Exception) { }
        bool low = ResolveLowRamMode(mode, LowRamPhone());
        GD.Print($"[memory-release] low-memory tier {(low ? "on" : "off")} ({LowRamModeKey} = {mode})");
        return low;
    }

    /// <summary>The tier a setting asks for: "on" and "off" as written, anything else by the phone.</summary>
    /// <param name="mode">The setting's value, lower case.</param>
    /// <param name="lowRamPhone">Whether the phone is short of memory.</param>
    public static bool ResolveLowRamMode(string mode, bool lowRamPhone) =>
        mode == "on" || (mode != "off" && lowRamPhone);

    /// <summary>Whether this phone's total memory is under <see cref="LowRamKilobytes"/>; unreadable counts as not.</summary>
    public static bool LowRamPhone()
    {
        try
        {
            long total = MemTotalKilobytes(System.IO.File.ReadAllText("/proc/meminfo"));
            return total > 0 && total < LowRamKilobytes;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>/proc/meminfo's MemTotal in kB, or 0 when it is absent or malformed.</summary>
    /// <param name="meminfo">The file's text.</param>
    public static long MemTotalKilobytes(string meminfo) => MeminfoKilobytes(meminfo, "MemTotal:");

    private static long MeminfoKilobytes(string meminfo, string field)
    {
        foreach (string line in meminfo.Split('\n'))
        {
            if (!line.StartsWith(field, StringComparison.Ordinal)) continue;
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 && long.TryParse(parts[1], out long kb) ? kb : 0;
        }
        return 0;
    }

    /// <summary>Whether the release is on; a config that cannot be read leaves it on.</summary>
    public static bool Wanted()
    {
        try
        {
            return Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), Key, true);
        }
        catch (Exception)
        {
            return true;
        }
    }
}
