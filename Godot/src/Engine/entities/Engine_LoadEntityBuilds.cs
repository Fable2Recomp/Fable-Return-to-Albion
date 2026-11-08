using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Godot;

namespace Refii.Entities;

/// <summary>
/// The entity builds of one load hold, for the release's log line: what they cost, what they
/// waited on and which took longest.
/// </summary>
/// <remarks>
/// Worker threads add to it, a handful of times a build, so a lock costs nothing that shows.
/// Worker time is summed across threads, so it can exceed the hold's wall time, and it includes
/// any GC pause that landed in the build.
/// </remarks>
public static class Engine_LoadEntityBuilds
{
    /// <summary>Slowest builds the line names.</summary>
    public const int SlowestKept = 5;

    private static readonly object Gate = new();
    private static readonly List<(long Ticks, string Name)> Slowest = new();
    private static long _meshes;
    private static long _meshTicks;
    private static long _composites;
    private static long _assembleTicks;
    private static long _textureTicks;
    private static long _lockTicks;
    private static long _lockWaits;
    private static long _ormPacks;
    private static long _ormPackTicks;
    private static long _ormReused;

    /// <summary>Whether a hold is on: builds outside one are play's, not the load's.</summary>
    public static volatile bool Collecting;

    /// <summary>One model loaded off the main thread. Any thread.</summary>
    public static void NoteMesh(string name, long ticks)
    {
        if (!Collecting)
        {
            return;
        }

        lock (Gate)
        {
            ++_meshes;
            _meshTicks += ticks;
            KeepIfSlow(ticks, name);
        }
    }

    /// <summary>One composite built off the main thread: its meshes joined, then its textures. Any thread.</summary>
    public static void NoteComposite(string name, long assembleTicks, long textureTicks)
    {
        if (!Collecting)
        {
            return;
        }

        lock (Gate)
        {
            ++_composites;
            _assembleTicks += assembleTicks;
            _textureTicks += textureTicks;
            KeepIfSlow(assembleTicks + textureTicks, name);
        }
    }

    /// <summary>How long a worker waited for a model's lock, on another load of that lock. Any thread.</summary>
    public static void NoteLockWait(long ticks)
    {
        if (!Collecting)
        {
            return;
        }

        lock (Gate)
        {
            ++_lockWaits;
            _lockTicks += ticks;
        }
    }

    /// <summary>One composed ORM (Engine_TexLibrary.ComposedOrm): packed, or found kept. Any thread.</summary>
    public static void NoteOrm(bool reused, long ticks)
    {
        if (!Collecting)
        {
            return;
        }

        lock (Gate)
        {
            if (reused)
            {
                ++_ormReused;
            }
            else
            {
                ++_ormPacks;
                _ormPackTicks += ticks;
            }
        }
    }

    private static void KeepIfSlow(long ticks, string name)
    {
        int at = Slowest.Count;
        while (at > 0 && Slowest[at - 1].Ticks < ticks)
        {
            --at;
        }

        if (at < SlowestKept)
        {
            Slowest.Insert(at, (ticks, name));
            if (Slowest.Count > SlowestKept)
            {
                Slowest.RemoveAt(SlowestKept);
            }
        }
    }

    /// <summary>The line's text, and a clean slate for the next hold.</summary>
    public static string Take()
    {
        lock (Gate)
        {
            var line = new StringBuilder();
            line.Append($"{_meshes} model(s) {Ms(_meshTicks):0} ms, ");
            line.Append($"{_composites} composite(s) {Ms(_assembleTicks):0} ms joining and {Ms(_textureTicks):0} ms on textures, ");
            line.Append($"{Ms(_lockTicks):0} ms waiting for model locks ({_lockWaits} ask(s)); slowest:");
            foreach ((long ticks, string name) in Slowest)
            {
                line.Append($" {name} {Ms(ticks):0}");
            }

            line.Append($"; composed ORMs: {_ormPacks} packed {Ms(_ormPackTicks):0} ms, {_ormReused} kept");

            _meshes = 0;
            _meshTicks = 0;
            _composites = 0;
            _assembleTicks = 0;
            _textureTicks = 0;
            _lockTicks = 0;
            _lockWaits = 0;
            _ormPacks = 0;
            _ormPackTicks = 0;
            _ormReused = 0;
            Slowest.Clear();
            return line.ToString();
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
