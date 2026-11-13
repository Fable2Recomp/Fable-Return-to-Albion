using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_body_merge_any_lods` (default on): a dressed body's same-material surfaces
/// (Engine_BodyVariants) join even when their coarser levels switch at different keys, on the
/// union of the keys. The same picture at every distance.
/// </summary>
/// <remarks>
/// <para>WHY. The variant joins surfaces only when their level keys are identical. Shipped levels
/// share the part's keys, but generated ones (`mesh_auto_lod_skinned`: 0.2/0.8/3% of EACH surface's
/// own extent, none under 600 indices, one dropped where it saves under 30%) never do, so turning
/// them on split the hero's 19 body regions back into 19 draws, and every same-material piece of a
/// garment with them. That is half of why that setting measured no gain.</para>
/// <para>EXACT. Godot picks a surface's level by its own keys against the same distance for every
/// surface of a node (render_forward_mobile.cpp `_fill_render_list`: the instance's box, model scale
/// and lod_bias): the last key whose error projects under the threshold. At a union key K the joined
/// level holds, for each piece, its own coarsest level keyed at or under K (its full index list when
/// none is), which is exactly the level that piece picks on its own wherever the joined surface
/// picks K, since no key of the piece lies between its pick and K. A union level is always smaller
/// than the joined surface, as Godot requires: the piece whose key it is contributes a level.</para>
/// </remarks>
public static class AndroidBodyMerge
{
    public const string Key = "android_body_merge_any_lods";

    private static readonly Lazy<bool> Chosen = new(Read, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether surfaces with different keys join; read once, false off the phone.</summary>
    public static bool AnyLods => Chosen.Value;

    /// <summary>Whether the configuration turns it on (default on).</summary>
    public static bool Wanted(IReadOnlyDictionary<string, string> values) => Engine_RefiiConfig.Bool(values, Key, true);

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
            return true;
        }
    }

    /// <summary>The joined levels of <paramref name="group"/>'s surfaces of <paramref name="source"/>, keyed on the union; null when none has any.</summary>
    public static Godot.Collections.Dictionary? JoinedLods(ArrayMesh source, List<int> group)
    {
        var pieces = new List<Piece>(group.Count);
        foreach (int s in group)
        {
            pieces.Add(new Piece(
                Refii.Models.Engine_MdlSurfaceArrays.Of(source, s)[(int)Mesh.ArrayType.Index].AsInt32Array(),
                Refii.Models.Engine_MdlSurfaceArrays.VertexCount(source, s),
                LevelsOf(Refii.Models.Engine_MdlMesh.SurfaceLods(source, s))));
        }

        List<(float Key, int[] Indices)>? joined = Join(pieces);
        if (joined is null)
        {
            return null;
        }

        var result = new Godot.Collections.Dictionary();
        foreach ((float key, int[] indices) in joined)
        {
            result[key] = indices;
        }

        return result;
    }

    /// <summary>One surface of a group: its full index list, its vertex count and its levels by key.</summary>
    public readonly record struct Piece(int[] Indices, int Vertices, SortedList<float, int[]> Levels);

    /// <summary>
    /// The union-keyed levels of <paramref name="pieces"/> laid end to end in order (each piece's
    /// indices offset by the vertices before it), ascending by key; null when no piece has a level. Pure.
    /// </summary>
    public static List<(float Key, int[] Indices)>? Join(IReadOnlyList<Piece> pieces)
    {
        var keys = new SortedSet<float>();
        foreach (Piece piece in pieces)
        {
            foreach (float key in piece.Levels.Keys)
            {
                keys.Add(key);
            }
        }

        if (keys.Count == 0)
        {
            return null;
        }

        var result = new List<(float, int[])>(keys.Count);
        var level = new List<int>();
        foreach (float key in keys)
        {
            level.Clear();
            int offset = 0;
            foreach (Piece piece in pieces)
            {
                int[] own = piece.Indices;
                foreach ((float at, int[] indices) in piece.Levels)
                {
                    if (at > key)
                    {
                        break;
                    }

                    own = indices;
                }

                foreach (int index in own)
                {
                    level.Add(offset + index);
                }

                offset += piece.Vertices;
            }

            result.Add((key, level.ToArray()));
        }

        return result;
    }

    private static SortedList<float, int[]> LevelsOf(Godot.Collections.Dictionary? lods)
    {
        var levels = new SortedList<float, int[]>();
        if (lods is null)
        {
            return levels;
        }

        foreach (KeyValuePair<Variant, Variant> entry in lods)
        {
            levels[entry.Key.AsSingle()] = entry.Value.AsInt32Array();
        }

        return levels;
    }
}
