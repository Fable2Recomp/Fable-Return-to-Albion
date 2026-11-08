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
    public static List<(float Key, int[] Indices)> Build(ReadOnlySpan<int> indices, int vertexCount, float extent, bool far = false)
    {
        var levels = new List<(float, int[])>();
        int triangles = indices.Length / 3;
        if (triangles < 32 || vertexCount <= 0 || !(extent > 0f) || !float.IsFinite(extent))
        {
            return levels;
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

        for (int t = 0; t < triangles; t++)
        {
            int a = indices[t * 3], b = indices[(t * 3) + 1], c = indices[(t * 3) + 2];
            if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount)
            {
                return levels;
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

        // A card's fixed place in [0, 1), or -1 for a component always kept.
        var place = new float[triangles];
        for (int t = 0; t < triangles; t++)
        {
            int r = root[t];
            place[t] = count[r] <= CardTriangles ? Hash01(first[r]) : -1f;
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

    /// <summary>A card's fixed place in [0, 1): golden-ratio spacing, so any share keeps an even spread.</summary>
    private static float Hash01(int id) => (float)((id * 0.6180339887498949) % 1.0);

    /// <summary>`tree_card_lod`: whether tree models without levels get thinned ones. On in both
    /// phone presets; the desktop draws the 360's trees as they are. Read once per launch.</summary>
    public static bool Enabled { get; } = ReadEnabled();

    /// <summary>`tree_card_far_levels`: whether the thinned trees also get an eighth and a sixteenth
    /// of their cards. Off by default; read once, so it applies to models loaded after a relaunch.</summary>
    public static bool FarLevelsEnabled { get; } = ReadFarLevels();

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
