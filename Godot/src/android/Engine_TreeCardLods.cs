using System;
using System.Collections.Generic;

namespace Refii.Models;

/// <summary>
/// Coarser levels for a tree model that ships none: the same trunk and branches with a
/// deterministic share of its leaf cards left out.
/// </summary>
/// <remarks>
/// THE DISTANT FOREST IS MOST OF A PHONE'S TRIANGLES. In Bowerstone Market the 360's own far
/// tree models (`BW_TreeBeechSapling_LOD1` x663, the large oaks' `_LOD1`s) were 650k of the 2.5M
/// triangles drawn, all past 50 m, with no further level; an Adreno 640 pays ~12 GPU cycles a
/// triangle. A mesh simplifier cannot help: a leaf card is two triangles with an open border,
/// which meshoptimizer locks. Thinning the cards can: a card is a small connected component, so
/// dropping whole components never tears the mesh.
///
/// Components of more than <see cref="CardTriangles"/> triangles (trunk, boughs) are always kept.
/// Which cards survive is a hash of each card's first triangle, so the same cards go at every
/// placement and the kept set of a coarser level is a subset of the finer one's.
/// </remarks>
public static class Engine_TreeCardLods
{
    /// <summary>The most triangles a component may have and still count as a card.</summary>
    public const int CardTriangles = 4;

    /// <summary>The share of cards each level keeps, and its key as a share of the tree's extent.</summary>
    private static readonly (float Keep, float Key)[] Levels = { (0.5f, 0.04f), (0.25f, 0.08f) };

    /// <summary>
    /// <see cref="Levels"/> and two more under `tree_card_far_levels`: an eighth of the cards at
    /// 16% of the extent, a sixteenth at 32%.
    /// </summary>
    /// <remarks>
    /// With no impostor on Android a tree draws its far model out to its stated reach (1 km for the
    /// shipped trees), and a quarter of the cards is where the two levels stop. At mesh_lod_pixels 4
    /// on 1080 lines a 10 m tree takes the eighth from about 225 m and the sixteenth from about
    /// 450 m, where it is a few pixels of a 30% buffer. The 30% rule below drops a level that would
    /// save too little, so a tree whose triangles are mostly boughs keeps the levels it had.
    /// </remarks>
    private static readonly (float Keep, float Key)[] FarLevels =
        { (0.5f, 0.04f), (0.25f, 0.08f), (0.125f, 0.16f), (0.0625f, 0.32f) };

    /// <summary>The coarser index lists of one surface, finest first.</summary>
    /// <param name="indices">The surface's triangle list.</param>
    /// <param name="vertexCount">Vertices the indices address.</param>
    /// <param name="extent">The surface's largest dimension, in the mesh's units.</param>
    /// <param name="far">Also the two coarser levels (`tree_card_far_levels`).</param>
    /// <returns>(key, indices) pairs as Godot's `lods` dictionary takes them; empty when the
    /// surface has too few cards for a level to save 30%.</returns>
    public static List<(float Key, int[] Indices)> Build(ReadOnlySpan<int> indices, int vertexCount, float extent, bool far = false) =>
        Build(indices, vertexCount, extent, far, default, CardTriangles);

    /// <summary>
    /// <see cref="Build(ReadOnlySpan{int}, int, float, bool)"/> with the card size stated, and components
    /// found across vertices at equal positions when <paramref name="positions"/> is given.
    /// </summary>
    /// <param name="positions">The surface's positions, or empty to join by index alone.</param>
    /// <param name="cardTriangles">The most triangles a component may have and still be dropped.</param>
    public static List<(float Key, int[] Indices)> Build(
        ReadOnlySpan<int> indices, int vertexCount, float extent, bool far, ReadOnlySpan<Godot.Vector3> positions, int cardTriangles)
    {
        var levels = new List<(float, int[])>();
        int triangles = indices.Length / 3;
        if (triangles < 32 || !(extent > 0f) || !float.IsFinite(extent) ||
            Places(indices, vertexCount, positions, cardTriangles) is not { } place)
        {
            return levels;
        }

        var level = new int[triangles * 3];
        int previous = triangles;
        foreach ((float keep, float key) in far ? FarLevels : Levels)
        {
            int kept = 0;
            for (int t = 0; t < triangles; t++)
            {
                if (place[t] >= keep)
                {
                    continue;
                }

                level[kept++] = indices[t * 3];
                level[kept++] = indices[(t * 3) + 1];
                level[kept++] = indices[(t * 3) + 2];
            }

            if (kept / 3 > previous * 7 / 10 || kept == 0)
            {
                continue;
            }

            levels.Add((key * extent, level.AsSpan(0, kept).ToArray()));
            previous = kept / 3;
        }

        return levels;
    }

    /// <summary>
    /// Each triangle's fixed place in [0, 1) when its component is a card, -1 when it is always kept;
    /// null for indices outside the vertices.
    /// </summary>
    private static float[]? Places(ReadOnlySpan<int> indices, int vertexCount, ReadOnlySpan<Godot.Vector3> positions, int cardTriangles)
    {
        int triangles = indices.Length / 3;
        if (vertexCount <= 0)
        {
            return null;
        }

        var parent = new int[vertexCount];
        for (int v = 0; v < vertexCount; v++)
        {
            parent[v] = v;
        }

        int Find(int v)
        {
            while (parent[v] != v)
            {
                parent[v] = parent[parent[v]];
                v = parent[v];
            }

            return v;
        }

        // A trunk's UV seams split it into small index islands; joined by position it is one piece.
        if (positions.Length == vertexCount)
        {
            var at = new Dictionary<Godot.Vector3, int>(vertexCount);
            for (int v = 0; v < vertexCount; v++)
            {
                if (!at.TryAdd(positions[v], v))
                {
                    parent[v] = at[positions[v]];
                }
            }
        }

        for (int t = 0; t < triangles; t++)
        {
            int a = indices[t * 3], b = indices[(t * 3) + 1], c = indices[(t * 3) + 2];
            if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount)
            {
                return null;
            }

            parent[Find(a)] = Find(b);
            parent[Find(b)] = Find(c);
        }

        // Per component, by its root vertex: its triangle count and first triangle, which names it stably.
        var count = new int[vertexCount];
        var first = new int[vertexCount];
        var root = new int[triangles];
        for (int t = 0; t < triangles; t++)
        {
            int r = Find(indices[t * 3]);
            root[t] = r;
            if (count[r]++ == 0)
            {
                first[r] = t;
            }
        }

        var place = new float[triangles];
        for (int t = 0; t < triangles; t++)
        {
            int r = root[t];
            place[t] = count[r] <= cardTriangles ? Hash01(first[r]) : -1f;
        }

        return place;
    }

    /// <summary>
    /// Whether a tree surface is trunk and boughs rather than leaves: at least <see cref="TrunkShare"/>
    /// of its triangles sit in pieces larger than a leaf cluster, joined by position. Pure.
    /// </summary>
    public static bool IsTrunk(ReadOnlySpan<int> indices, int vertexCount, ReadOnlySpan<Godot.Vector3> positions)
    {
        if (indices.Length < 3 || positions.Length != vertexCount ||
            Places(indices, vertexCount, positions, ClusterTriangles) is not { } place)
        {
            return false;
        }

        int solid = 0;
        foreach (float p in place)
        {
            if (p < 0f)
            {
                ++solid;
            }
        }

        return solid >= place.Length * TrunkShare;
    }

    /// <summary>The share of a surface's triangles in large pieces that makes it a trunk.</summary>
    public const float TrunkShare = 0.7f;

    /// <summary>A card's fixed place in [0, 1): golden-ratio spacing, so any share keeps an even spread.</summary>
    private static float Hash01(int id) => (float)((id * 0.6180339887498949) % 1.0);

    /// <summary>`tree_card_lod`: whether tree models without levels get thinned ones. On in both
    /// phone presets; the desktop draws the 360's trees as they are. Read once per launch.</summary>
    public static bool Enabled { get; } = ReadEnabled();

    /// <summary>`tree_card_far_levels`: whether the thinned trees also get an eighth and a sixteenth
    /// of their cards. Off by default; read once, so it applies to models loaded after a relaunch.</summary>
    public static bool FarLevelsEnabled { get; } = ReadFarLevels();

    /// <summary>The refii.toml key of <see cref="ClustersEnabled"/>.</summary>
    public const string ClusterKey = "tree_cluster_lod";

    /// <summary>The most triangles a leaf cluster may have under <see cref="ClusterKey"/>.</summary>
    public const int ClusterTriangles = 32;

    /// <summary>
    /// `tree_cluster_lod`: leaf clusters of up to <see cref="ClusterTriangles"/> triangles thin as
    /// cards do. Off by default; read once, so it applies to models loaded after a relaunch.
    /// </summary>
    /// <remarks>
    /// Most shipped trees build their crowns from clusters of 8 to 16 triangles, which the card rule
    /// (<see cref="CardTriangles"/>) never thins: in Oakfield 0.94 of 1.05 million tree triangles in
    /// view had no coarser level. Components are joined across vertices at equal positions, so a
    /// trunk's UV islands count as the one piece they are and stay whole.
    /// </remarks>
    public static bool ClustersEnabled { get; } = ReadClusters();

    private static bool ReadClusters()
    {
        try
        {
            return Enabled && Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), ClusterKey, false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The refii.toml key of <see cref="TrunksEnabled"/>.</summary>
    public const string TrunkKey = "tree_trunk_lod";

    /// <summary>
    /// `tree_trunk_lod`: a tree's trunk and bough surfaces (<see cref="IsTrunk"/>) get generated simpler
    /// versions, as other scenery does under mesh_auto_lod. Off by default; read once.
    /// </summary>
    /// <remarks>A trunk is 300 to 2,000 triangles and ships no coarser level, so every tree in view draws
    /// it whole: about a third of Oakfield's tree triangles. The levels switch by the shared
    /// mesh_lod_pixels rule, as the props' do.</remarks>
    public static bool TrunksEnabled { get; } = ReadTrunks();

    private static bool ReadTrunks()
    {
        try
        {
            return Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), TrunkKey, false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ReadFarLevels()
    {
        try
        {
            return Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), "tree_card_far_levels", false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ReadEnabled()
    {
        try
        {
            return Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), "tree_card_lod",
                Refii.Core.Engine_Platform.IsAndroid);
        }
        catch (Exception)
        {
            return Refii.Core.Engine_Platform.IsAndroid;
        }
    }
}
