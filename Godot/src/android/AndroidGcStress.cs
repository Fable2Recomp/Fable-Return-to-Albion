using Godot;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Refii.AndroidRuntime;

/// <summary>
/// <c>debug_gc_stress_ms</c> (0, off): a background thread forces a blocking, compacting
/// collection of the young generations every that many milliseconds. A test instrument, not a
/// setting for players.
/// </summary>
/// <remarks>
/// WHY. The world-load crashes in <c>Array::_unref</c> and <c>StringName::unref</c>, seen on both
/// test phones, were GodotSharp's destroy reading a wrapper's field twice through an
/// unpinned pointer while a GC moved objects (tools/android/godot-patches/0004), each one crash in
/// a day of loads. Young collections move what the finalizer is about to dispose, so forcing them
/// often while a level loads makes that window far more likely to be hit, and a handful of paired
/// loads can tell a template with the patch from one without it.
/// </remarks>
internal static class AndroidGcStress
{
    public const string Key = "debug_gc_stress_ms";

    private static int _collections;

    /// <summary>Collections forced since start-up.</summary>
    public static int Collections => Volatile.Read(ref _collections);

    /// <summary>Starts the thread when refii.toml sets a positive interval; false otherwise.</summary>
    public static bool Start(IReadOnlyDictionary<string, string> values)
    {
        int milliseconds = Refii.Core.Engine_RefiiConfig.Int(values, Key, 0);
        if (milliseconds <= 0)
            return false;
        milliseconds = Math.Clamp(milliseconds, 1, 60_000);
        new Thread(() => Run(milliseconds)) { IsBackground = true, Name = "refii-gc-stress" }.Start();
        GD.Print($"[android] GC stress: a compacting collection of generations 0-1 every {milliseconds} ms ({Key})");
        return true;
    }

    private static void Run(int milliseconds)
    {
        while (true)
        {
            Thread.Sleep(milliseconds);
            GC.Collect(1, GCCollectionMode.Forced, blocking: true, compacting: true);
            Interlocked.Increment(ref _collections);
        }
    }
}
