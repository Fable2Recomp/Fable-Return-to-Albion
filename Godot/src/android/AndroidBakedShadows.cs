using System;
using System.Collections.Generic;
using Refii.Core;
using Refii.Lighting;

namespace Refii.AndroidRuntime;

/// <summary>
/// The launcher's "Baked lamp shadows" choice: when a phone works out lamp and torch shadows ahead of
/// time (<see cref="Engine_LevelShadowPrebuild"/>), which a phone's startup does not do on its own.
/// <list type="bullet">
/// <item>Before playing: Play first runs the desktop's sweep over every area (lamp shadows and ground AO).
/// A sweep stopped part way carries on at the next Play.</item>
/// <item>While playing: no sweep; an area's lamp shadows bake in the background the first time it is
/// entered (<see cref="InGameBakesAo"/>: not its ground AO on a phone).</item>
/// <item>Off: no bake, and any baked file on the phone is ignored.</item>
/// </list>
/// An area with no bake plays as it does with the choice off.
/// </summary>
public static class AndroidBakedShadows
{
    /// <summary>The refii.toml setting. Off unless the player picks a bake.</summary>
    public const string Key = "android_baked_shadows";

    /// <summary>When a phone bakes lamp shadows.</summary>
    public enum Mode
    {
        /// <summary>No bake; baked files are ignored.</summary>
        Off,

        /// <summary>Each area's lamp shadows bake the first time it is entered.</summary>
        WhilePlaying,

        /// <summary>Play bakes every area first.</summary>
        BeforePlaying,
    }

    /// <summary><see cref="Mode.BeforePlaying"/>'s stored value. "true" and "false" are the values the earlier on/off
    /// switch stored, so a configuration from before the choice keeps its meaning.</summary>
    public const string BeforePlayingValue = "true";

    /// <summary><see cref="Mode.WhilePlaying"/>'s stored value.</summary>
    public const string WhilePlayingValue = "while_playing";

    /// <summary><see cref="Mode.Off"/>'s stored value.</summary>
    public const string OffValue = "false";

    /// <summary>The launcher's options, in the order it lists them.</summary>
    public const string Options = BeforePlayingValue + "|Before playing," + WhilePlayingValue + "|While playing," + OffValue + "|Off";

    /// <summary>Free storage a bake asks for: Fable 2's areas came to 1.7 GB at the default resolution.</summary>
    public const long StorageNeeded = 2_500_000_000;

    /// <summary>The player's choice. Anything unknown reads as off.</summary>
    /// <param name="values">The runtime configuration's values.</param>
    public static Mode Chosen(IReadOnlyDictionary<string, string> values) =>
        values.TryGetValue(Key, out string? raw) ? Parse(raw) : Mode.Off;

    /// <summary>A stored value's mode: "true" or "1" before playing, "while_playing" while playing, anything else off.</summary>
    /// <param name="raw">The value as refii.toml stores it, quoted or not.</param>
    public static Mode Parse(string? raw)
    {
        string text = (raw ?? string.Empty).Trim().Trim('"');
        if (text.Equals(BeforePlayingValue, System.StringComparison.OrdinalIgnoreCase) || text == "1") return Mode.BeforePlaying;
        if (text.Equals(WhilePlayingValue, System.StringComparison.OrdinalIgnoreCase)) return Mode.WhilePlaying;
        return Mode.Off;
    }

    /// <summary>The launcher's line under the choice: what the chosen one costs.</summary>
    /// <param name="mode">The choice.</param>
    public static string Note(Mode mode) => mode switch
    {
        Mode.BeforePlaying => "Play bakes every area before the game starts. It can take hours the first time and "
            + "needs about 2.5 GB of space. You can cancel and carry on next time.",
        Mode.WhilePlaying => "Each area bakes the first time you enter it, with up to a minute of stutter. "
            + "The soft shadow where objects meet the ground is left out.",
        _ => "No baked shadows from lamps and torches: no baking and no space used.",
    };

    /// <summary>Whether Play runs the sweep first.</summary>
    /// <param name="values">The runtime configuration's values.</param>
    public static bool BakesBeforePlaying(IReadOnlyDictionary<string, string> values) => Chosen(values) == Mode.BeforePlaying;

    /// <summary>Whether a level load ignores any baked file: a phone with the choice off, whatever is on disk.</summary>
    public static bool LevelsIgnoreBakes => Engine_Platform.IsAndroid && Chosen(Engine_RefiiConfig.Read(out _)) == Mode.Off;

    /// <summary>Whether the sweep keeps only its latest bake for reuse. Scenarios sharing one are neighbours, and on
    /// a phone every atlas kept is tens of megabytes of heap for the rest of the sweep.</summary>
    public static bool KeepsOneBake => Engine_Platform.IsAndroid;

    /// <summary>Between two areas of a sweep on a phone, gives back the area's memory. Every model's triangles
    /// stay cached otherwise, and the S25+ ran out of its 11 GB partway through the sweep.</summary>
    public static void AfterArea()
    {
        if (!Engine_Platform.IsAndroid) return;
        Engine_LevelShadowMaps.ClearBakeTriangles();
        System.GC.Collect();
    }

    /// <summary>Whether a level's background bake during play includes ground AO. Not on a phone: on the S10e it is
    /// about 20 minutes of GPU jobs of 100-400 ms each, so the game stalls several times a second, and the area's BVH
    /// (about a gigabyte) stays in memory throughout. A phone bakes an area's lamp shadows during play (under a minute)
    /// and leaves its AO to the launcher's sweep; until then the area plays without baked AO.</summary>
    public static bool InGameBakesAo => !Engine_Platform.IsAndroid;

    /// <summary>Whether a level's shadow and ground-AO GPU bakes share one persistent local <see cref="Godot.RenderingDevice"/>
    /// and single uploaded BVH buffer pair across both passes. True on desktop; false on Android so each step releases
    /// its driver allocations before <see cref="BetweenSteps"/> compacts managed memory.</summary>
    public static bool SharesLocalBakeDevice => !Engine_Platform.IsAndroid;

    /// <summary>On a phone, after a level's background bake during play, collects what the bake left and returns
    /// the freed memory to the system. A plain compacting collection (<see cref="BetweenSteps"/>) kept it committed:
    /// on the S10e the game stayed about 450 MB above its level before the bake, near the size at which Android
    /// stops it. <see cref="System.GCCollectionMode.Aggressive"/> decommits what it frees.</summary>
    public static void AfterInGameBake()
    {
        if (!Engine_Platform.IsAndroid) return;
        Stage("in-game bake done");
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        System.GC.Collect(2, System.GCCollectionMode.Aggressive, blocking: true, compacting: true);
        Stage("memory returned");
    }

    /// <summary>Texels a phone's ground-AO bake traces in one GPU submission. A desktop strip (256 rows of a 2048
    /// map, about half a million texels) ran long enough on the S10e's Adreno 640 for Android to reset the GPU
    /// ("Vulkan device was lost"); the same work in small strips finishes, a strip a frame.</summary>
    public const int PhoneAoStripTexels = 16384;

    /// <summary>Lamps a phone's shadow-atlas bake traces in one GPU submission, for the same reason.</summary>
    public const long PhoneShadowBatchLights = 2;

    /// <summary>Rows of the ground-AO map a bake traces in one GPU submission: the desktop's on a desktop, a
    /// strip of about <see cref="PhoneAoStripTexels"/> on a phone.</summary>
    /// <param name="width">The AO map's width in texels.</param>
    /// <param name="desktopRows">The rows the desktop traces at once.</param>
    public static int AoStripRows(int width, int desktopRows) =>
        Engine_Platform.IsAndroid ? System.Math.Clamp(PhoneAoStripTexels / System.Math.Max(1, width), 1, desktopRows) : desktopRows;

    /// <summary>Ground-AO texels a phone traces in one GPU submission. A strip of
    /// <see cref="PhoneAoStripTexels"/> still lost the device in Dunecrest (a 4M-triangle BVH under a dense terrain mesh),
    /// so a phone traces each strip in column pieces of about this many texels.</summary>
    public const int PhoneAoTraceTexels = 4096;

    /// <summary>Columns of a ground-AO strip a bake traces in one GPU submission: the whole width on a desktop, pieces
    /// of about <see cref="PhoneAoTraceTexels"/> texels (a multiple of the shader's 8-wide groups) on a phone.</summary>
    /// <param name="width">The AO map's width in texels.</param>
    /// <param name="rows">The rows one trace covers (the strip and its halo).</param>
    public static int AoTraceColumns(int width, int rows) => Engine_Platform.IsAndroid ? PhoneAoTraceColumns(width, rows) : width;

    /// <summary>On a phone, traces one ground-AO strip in column pieces of <paramref name="columns"/>, each its own
    /// submission waited on before the next on the worker-owned local <see cref="Godot.RenderingDevice"/>, then filters
    /// the strip in one more: the same shader writing the same texels as the desktop's single submission (the trace's
    /// push constant at byte 60 is its column offset). The caller reads the strip back after its own Sync.</summary>
    /// <returns>False once the device is lost.</returns>
    public static bool TraceAoInPieces(Godot.RenderingDevice rd, Godot.Rid tracePipeline, Godot.Rid traceSet, byte[] tracePush,
        int columns, int width, int traceGroupsY, Godot.Rid filterPipeline, Godot.Rid filterSet, byte[] filterPush,
        int filterGroupsX, int filterGroupsY)
    {
        for (int offset = 0; offset < width; offset += columns)
        {
            if (Engine_RdHealth.IsDeviceLost) return false;
            int groupsX = (System.Math.Min(columns, width - offset) + 7) / 8;
            byte[] push = (byte[])tracePush.Clone();
            System.BitConverter.TryWriteBytes(push.AsSpan(60), offset);
            long list = rd.ComputeListBegin();
            rd.ComputeListBindComputePipeline(list, tracePipeline);
            rd.ComputeListBindUniformSet(list, traceSet, 0);
            rd.ComputeListSetPushConstant(list, push, (uint)push.Length);
            rd.ComputeListDispatch(list, (uint)groupsX, (uint)traceGroupsY, 1);
            rd.ComputeListEnd();
            rd.Submit();
            rd.Sync();
        }

        if (Engine_RdHealth.IsDeviceLost) return false;
        long filterList = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(filterList, filterPipeline);
        rd.ComputeListBindUniformSet(filterList, filterSet, 0);
        rd.ComputeListSetPushConstant(filterList, filterPush, (uint)filterPush.Length);
        rd.ComputeListDispatch(filterList, (uint)filterGroupsX, (uint)filterGroupsY, 1);
        rd.ComputeListEnd();
        rd.Submit();
        return true;
    }

    /// <summary>A phone's piece of an AO strip: whole 8-wide shader groups covering about
    /// <see cref="PhoneAoTraceTexels"/> texels, at least one group, never wider than needed for the map.</summary>
    /// <param name="width">The AO map's width in texels.</param>
    /// <param name="rows">The rows one trace covers (the strip and its halo).</param>
    public static int PhoneAoTraceColumns(int width, int rows) =>
        System.Math.Clamp(PhoneAoTraceTexels / System.Math.Max(1, rows) / 8 * 8, 8, System.Math.Max(8, width));

    /// <summary>Lamps a shadow-atlas bake traces in one GPU submission: the desktop's on a desktop, fewer on a phone.</summary>
    /// <param name="desktopLights">The desktop's batch.</param>
    public static long ShadowBatchLights(long desktopLights) =>
        Engine_Platform.IsAndroid ? System.Math.Min(desktopLights, PhoneShadowBatchLights) : desktopLights;

    /// <summary>On a phone, between the big steps of an area's bake (after its BVH, after its shadows), gives back
    /// what the step left for the collector: a step's dead arrays (hundreds of megabytes in a large area) were
    /// still held when the next step allocated, and Android stopped the bake for memory.</summary>
    public static void BetweenSteps()
    {
        if (!Engine_Platform.IsAndroid) return;
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        System.GC.Collect(2, System.GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>On a phone, logs a step of a bake with the memory it holds, so a bake the system stops for memory
    /// shows how far it got.</summary>
    /// <param name="step">What just happened.</param>
    /// <param name="count">A count for the step (triangles, lights), or -1.</param>
    public static void Stage(string step, long count = -1)
    {
        if (!Engine_Platform.IsAndroid) return;
        System.GCMemoryInfo info = System.GC.GetGCMemoryInfo();
        long heap = info.HeapSizeBytes >> 20;
        long committed = info.TotalCommittedBytes >> 20;
        long working = System.Environment.WorkingSet >> 20;
        Godot.GD.Print($"[bake-memory] {step}{(count >= 0 ? $" ({count:N0})" : "")}: managed heap {heap} MB, committed {committed} MB, process {working} MB");
    }

    /// <summary>Whether Play has to run the sweep first. Only Fable 2, the title the bake was made for.</summary>
    /// <param name="wanted">The option's value.</param>
    /// <param name="title">The title about to start.</param>
    /// <param name="settled">The sweep already finished, stopped or failed since the launcher opened.</param>
    public static bool ShouldBake(bool wanted, Engine_Title title, bool settled) =>
        wanted && !settled && ReferenceEquals(title, Engine_Title.Fable2);

    /// <summary>The launcher's line for the sweep's progress.</summary>
    /// <param name="progress">The sweep's latest report.</param>
    public static string Line(Engine_LevelShadowPrebuild.Progress progress) =>
        progress.Total == 0 || progress.Built == 0 && progress.Done == progress.UpToDate
            ? "Checking baked lamp shadows..."
            : $"Baking lamp shadows: {progress.Done:N0} of {progress.Total:N0} areas. "
              + "This can take a long time. You can cancel from the File menu and carry on next time.";

    /// <summary>The launcher's line after a sweep that did not reach the game.</summary>
    /// <param name="progress">The sweep's result.</param>
    public static string Stopped(Engine_LevelShadowPrebuild.Progress progress) =>
        $"Baking stopped at {progress.Done:N0} of {progress.Total:N0} areas. "
        + "Press Play to start without the rest, or reopen the launcher to carry on baking.";
}
