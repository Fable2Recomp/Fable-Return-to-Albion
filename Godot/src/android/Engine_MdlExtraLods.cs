using System;
using Godot;

namespace Refii.Models;

/// <summary>
/// Levels for a model surface that ships none (Engine_MdlMesh): a tree's thinned leaf cards
/// under `tree_card_lod`, anything else's generated levels under `mesh_auto_lod`.
/// </summary>
public static class Engine_MdlExtraLods
{
    /// <summary>Thinned tree levels added (Engine_TreeCardLods). Run total.</summary>
    public static int TreeCardLevels;

    /// <summary>The fewest indices a trunk needs to get generated levels: a far model's trunk is about 100 triangles.</summary>
    internal const int TrunkMinIndices = 150;

    /// <summary>The levels for a surface that has none of its own, or null when neither setting gives any.</summary>
    /// <param name="surface">The surface's arrays so far: its positions, normals and indices.</param>
    internal static Engine_MdlSurfaceArrays.Levels? For(
        MdlInfo info, MdlGeometry geo, ReadOnlySpan<Vector3> vertices, Godot.Collections.Array surface,
        ReadOnlySpan<int> indices)
    {
        Engine_MdlSurfaceArrays.Levels? levels = null;

        // A tree that ships no coarser level (the 360's `_LOD1` far trees) gets thinned
        // ones under `tree_card_lod`: the same boughs with fewer leaf cards (Engine_TreeCardLods).
        if (info.MovementMode == 2 && Engine_TreeCardLods.Enabled)
        {
            Vector3 low = vertices[0], high = vertices[0];
            foreach (Vector3 v in vertices)
            {
                low = low.Min(v);
                high = high.Max(v);
            }

            Vector3 span = high - low;
            bool clusters = Engine_TreeCardLods.ClustersEnabled;
            foreach ((float key, int[] level) in Engine_TreeCardLods.Build(
                indices, vertices.Length, System.MathF.Max(span.X, System.MathF.Max(span.Y, span.Z)),
                Engine_TreeCardLods.FarLevelsEnabled, clusters ? vertices : default,
                clusters ? Engine_TreeCardLods.ClusterTriangles : Engine_TreeCardLods.CardTriangles))
            {
                levels ??= new Engine_MdlSurfaceArrays.Levels();
                levels.Add(key, level);
                System.Threading.Interlocked.Increment(ref TreeCardLevels);
            }
        }

        // A tree's trunk and boughs: generated levels under `tree_trunk_lod`.
        if (levels is null && info.MovementMode == 2 && Engine_TreeCardLods.TrunksEnabled && Engine_MdlAutoLod.Enabled &&
            Engine_TreeCardLods.IsTrunk(indices, vertices.Length, vertices))
        {
            levels = Engine_MdlAutoLod.GenerateFor(vertices, indices.Length, surface, far: true, minIndices: TrunkMinIndices);
        }

        // Still none and not a tree (trees thin only by their cards, above): generated levels on a phone
        // (Engine_MdlAutoLod, `mesh_auto_lod`). After the card thinning, which suits a leaf card better.
        if (levels is null && (!geo.HasBones || Engine_MdlAutoLod.Skinned) && info.MovementMode != 2 && Engine_MdlAutoLod.Enabled)
        {
            levels = Engine_MdlAutoLod.Generate(vertices, indices.Length, surface);
        }

        return levels;
    }
}
