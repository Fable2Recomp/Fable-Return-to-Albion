using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_fur_shell_lods`: a furred body's shell mesh (Engine_ClassicFur) carries the body
/// surface's own coarser levels, cut to its furred triangles, so every shell pass draws the level
/// the body draws. Off by default while it is measured.
/// </summary>
/// <remarks>
/// <para>WHY. The shells are the coat drawn again once per layer, 15 passes over the dog's 5,600
/// fur triangles (84,000 triangles a frame for one dog), and the shell mesh is built with no level
/// at all (`AddShellSurface` passed none), so the body's levels under `mesh_auto_lod_skinned` or
/// `android_character_far_lod_cm` changed the body and none of its shells.</para>
/// <para>HOW. Each of the body surface's levels, keyed the same, keeps the triangles whose three
/// corners are furred, through the shell's own vertex map (the same `Fur` and `Map` the full shell
/// is cut with, so a level's own vertices join the shell's arrays like any other). The shell node
/// is on the same skeleton and takes the body's lod_bias (AndroidCharacterLevers.Shell), so at a
/// given distance it picks the body's level (its box is the coat's, a fur length larger).</para>
/// </remarks>
public static class AndroidFurShellLods
{
    public const string Key = "android_fur_shell_lods";

    private static readonly Lazy<bool> Chosen = new(Read, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
    private static int _levels;

    /// <summary>Whether shells carry their body's levels; read once.</summary>
    public static bool On => Chosen.Value;

    /// <summary>Shell levels added since start-up.</summary>
    public static int LevelsAdded => System.Threading.Volatile.Read(ref _levels);

    /// <summary>Whether the configuration turns it on.</summary>
    public static bool Wanted(IReadOnlyDictionary<string, string> values) => Engine_RefiiConfig.Bool(values, Key, false);

    private static bool Read()
    {
        if (!Engine_Platform.IsAndroid)
        {
            return false;
        }

        try
        {
            return Wanted(Engine_RefiiConfig.Read(out _));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The shell surface's levels, or null when it is off or the body surface has none. Called
    /// before the shell's arrays are packed: <paramref name="map"/> may add vertices to them.
    /// </summary>
    /// <param name="source">The furred body mesh.</param>
    /// <param name="surface">The body surface the shell surface is cut from.</param>
    /// <param name="fur">Whether a body vertex is furred.</param>
    /// <param name="map">A furred body vertex's shell vertex, added on first use.</param>
    /// <param name="shellIndices">The shell surface's own index count.</param>
    public static Godot.Collections.Dictionary? For(
        ArrayMesh source, int surface, Func<int, bool> fur, Func<int, int> map, int shellIndices) =>
        On ? Cut(Refii.Models.Engine_MdlMesh.SurfaceLods(source, surface), fur, map, shellIndices) : null;

    /// <summary><see cref="For"/> without the setting. Pure but for what <paramref name="map"/> does.</summary>
    public static Godot.Collections.Dictionary? Cut(
        Godot.Collections.Dictionary? levels, Func<int, bool> fur, Func<int, int> map, int shellIndices)
    {
        if (levels is null || levels.Count == 0)
        {
            return null;
        }

        Godot.Collections.Dictionary? result = null;
        var cut = new List<int>();
        foreach (KeyValuePair<Variant, Variant> level in levels)
        {
            cut.Clear();
            int[] indices = level.Value.AsInt32Array();
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                if (!fur(a) || !fur(b) || !fur(c))
                {
                    continue;
                }

                cut.Add(map(a));
                cut.Add(map(b));
                cut.Add(map(c));
            }

            // Godot refuses a level that is not smaller than the surface (mesh_add_surface).
            if (cut.Count < 3 || cut.Count >= shellIndices)
            {
                continue;
            }

            result ??= new Godot.Collections.Dictionary();
            result[level.Key] = cut.ToArray();
            System.Threading.Interlocked.Increment(ref _levels);
        }

        return result;
    }
}
