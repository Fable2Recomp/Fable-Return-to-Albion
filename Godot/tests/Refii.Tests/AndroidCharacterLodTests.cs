using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Godot;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The character geometry levers: far clustered levels (`android_character_far_lod_cm`), the body
/// merge on the union of level keys (`android_body_merge_any_lods`), fur shells cut from the body's
/// levels (`android_fur_shell_lods`), and the live LOD bias / far shading keys.
/// </summary>
[TestSuite]
public sealed class AndroidCharacterLodTests
{
    /// <summary>A UV-mapped grid of <paramref name="n"/> x <paramref name="n"/> quads over one metre, z = 0, normals +z.</summary>
    private static (float[] Positions, float[] Normals, float[] Uvs, uint[] Indices) Grid(int n, float uvScale = 1f, float x0 = 0f)
    {
        var positions = new List<float>();
        var normals = new List<float>();
        var uvs = new List<float>();
        var indices = new List<uint>();
        for (int j = 0; j <= n; j++)
        {
            for (int i = 0; i <= n; i++)
            {
                positions.AddRange(new[] { x0 + i / (float)n, j / (float)n, 0f });
                normals.AddRange(new[] { 0f, 0f, 1f });
                uvs.AddRange(new[] { i / (float)n * uvScale, j / (float)n * uvScale });
            }
        }

        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                uint a = (uint)(j * (n + 1) + i), b = a + 1, c = a + (uint)(n + 1), d = c + 1;
                indices.AddRange(new[] { a, c, b, b, c, d });
            }
        }

        return (positions.ToArray(), normals.ToArray(), uvs.ToArray(), indices.ToArray());
    }

    private static Vector3 P(float[] positions, int v) => new(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]);

    [TestCase]
    public void TheSettingIsOffUnlessSetAndClamped()
    {
        AssertThat(AndroidCharacterFarLods.Wanted(new Dictionary<string, string>())).IsEqual(0f);
        AssertThat(AndroidCharacterFarLods.Wanted(new Dictionary<string, string> { [AndroidCharacterFarLods.Key] = "5" })).IsEqual(5f);
        AssertThat(AndroidCharacterFarLods.Wanted(new Dictionary<string, string> { [AndroidCharacterFarLods.Key] = "0.2" })).IsEqual(AndroidCharacterFarLods.MinCentimetres);
        AssertThat(AndroidCharacterFarLods.Wanted(new Dictionary<string, string> { [AndroidCharacterFarLods.Key] = "500" })).IsEqual(AndroidCharacterFarLods.MaxCentimetres);
        AssertThat(AndroidCharacterFarLods.CellsFor(0f).Length).IsEqual(0);
        float[] cells = AndroidCharacterFarLods.CellsFor(4f);
        AssertThat(cells.Length).IsEqual(2);
        AssertThat(MathF.Abs(cells[0] - 0.04f) < 1e-6f && MathF.Abs(cells[1] - 0.1f) < 1e-6f).IsTrue();
    }

    [TestCase]
    public void ClusteringCutsAFineGridToItsCellsAndKeepsTheWinding()
    {
        (float[] positions, float[] normals, float[] uvs, uint[] indices) = Grid(40, 0.1f);
        AndroidCharacterFarLods.Clustered level = AndroidCharacterFarLods.Cluster(positions, normals, uvs, indices, positions.Length / 3, 0.25f);

        // 3,200 triangles on a 4 x 4 cell (5 x 5 cell-corner) lattice: at most 2 x 5 x 5 triangles survive.
        AssertThat(level.Indices.Length / 3).IsGreater(0);
        AssertThat(level.Indices.Length / 3 <= 50).IsTrue();
        AssertThat(level.Representatives.Length).IsEqual(level.Positions.Length / 3);
        for (int t = 0; t < level.Indices.Length; t += 3)
        {
            Vector3 a = P(level.Positions, level.Indices[t]), b = P(level.Positions, level.Indices[t + 1]), c = P(level.Positions, level.Indices[t + 2]);
            Vector3 normal = (b - a).Cross(c - a);
            AssertThat(normal.Length() > 1e-6f).IsTrue();

            // The grid's own winding faces -z ({a, c, b} with c above a): the level faces the same way.
            Vector3 own = (P(positions, (int)indices[1]) - P(positions, (int)indices[0])).Cross(P(positions, (int)indices[2]) - P(positions, (int)indices[0]));
            AssertThat(Math.Sign(normal.Z)).IsEqual(Math.Sign(own.Z));
        }

        // Each new vertex stands at its cell's mean and carries a member's skin.
        foreach (int rep in level.Representatives)
        {
            AssertThat(rep >= 0 && rep < positions.Length / 3).IsTrue();
        }
    }

    [TestCase]
    public void TwoUvIslandsOnOnePlaceNeverShareATriangleButShareTheirPositions()
    {
        // The same strip twice, one copy's UVs in another eighth of the texture: a seam.
        (float[] p1, float[] n1, float[] u1, uint[] i1) = Grid(8, 0.1f);
        (float[] p2, float[] n2, float[] u2, uint[] i2) = Grid(8, 0.1f);
        for (int k = 0; k < u2.Length; k += 2)
        {
            u2[k] += 0.5f;
        }

        int count = p1.Length / 3;
        float[] positions = p1.Concat(p2).ToArray(), normals = n1.Concat(n2).ToArray(), uvs = u1.Concat(u2).ToArray();
        uint[] indices = i1.Concat(i2.Select(i => i + (uint)count)).ToArray();
        AndroidCharacterFarLods.Clustered level = AndroidCharacterFarLods.Cluster(positions, normals, uvs, indices, count * 2, 0.3f);

        AssertThat(level.Indices.Length).IsGreater(0);
        for (int t = 0; t < level.Indices.Length; t += 3)
        {
            bool[] island = Enumerable.Range(0, 3).Select(k => level.Representatives[level.Indices[t + k]] >= count).ToArray();
            AssertThat(island[0] == island[1] && island[1] == island[2]).IsTrue();
        }

        // Both islands' vertices of one cell stand at one point: the seam stays closed.
        var byPosition = level.Representatives.Select((rep, n) => (Island: rep >= count, At: P(level.Positions, n)))
            .GroupBy(e => e.At).ToList();
        AssertThat(byPosition.Any(g => g.Select(e => e.Island).Distinct().Count() == 2)).IsTrue();
    }

    [TestCase]
    public void AppendAddsLevelsWithTheirOwnVerticesAfterTheShippedOnes()
    {
        (float[] positions, float[] normals, float[] uvs, uint[] indices) = Grid(30);
        int vertices = positions.Length / 3;
        var geo = new MdlGeometry
        {
            Positions = positions,
            Normals = normals,
            Uvs = uvs,
            Tangents = Enumerable.Repeat(new[] { 1f, 0f, 0f, 1f }, vertices).SelectMany(t => t).ToArray(),
            BoneIds = Enumerable.Range(0, vertices).SelectMany(v => new[] { v % 7, 0, 0, 0 }).ToArray(),
            BoneWeights = Enumerable.Range(0, vertices).SelectMany(_ => new[] { 1f, 0f, 0f, 0f }).ToArray(),
            Indices = indices,
            SourceVertices = Enumerable.Range(100, vertices).ToArray(),
        };
        geo.Lods.Add((0.04f, indices.Take(indices.Length / 2).ToArray()));

        int kept = AndroidCharacterFarLods.Append(geo, new[] { 0.05f, 0.125f });
        AssertThat(kept).IsEqual(2);
        AssertThat(geo.Lods.Count).IsEqual(3);

        // Keyed past the shipped level by the step, then by its own cell; each smaller than the last.
        AssertThat(geo.Lods[1].EdgeLength).IsEqual(MathF.Max(0.05f, 0.04f * AndroidCharacterFarLods.KeyStep));
        AssertThat(geo.Lods[2].EdgeLength).IsEqual(MathF.Max(0.125f, geo.Lods[1].EdgeLength * AndroidCharacterFarLods.KeyStep));
        AssertThat(geo.Lods[1].Indices.Length < geo.Lods[0].Indices.Length).IsTrue();
        AssertThat(geo.Lods[2].Indices.Length < geo.Lods[1].Indices.Length).IsTrue();

        // Every per-vertex array grew together, and the far levels index only the new vertices.
        int total = geo.VertexCount;
        AssertThat(total).IsGreater(vertices);
        AssertThat(geo.Normals.Length).IsEqual(total * 3);
        AssertThat(geo.Uvs.Length).IsEqual(total * 2);
        AssertThat(geo.Tangents.Length).IsEqual(total * 4);
        AssertThat(geo.BoneIds.Length).IsEqual(total * 4);
        AssertThat(geo.BoneWeights.Length).IsEqual(total * 4);
        AssertThat(geo.SourceVertices.Length).IsEqual(total);
        AssertThat(geo.Lods[1].Indices.All(i => i >= vertices && i < total)).IsTrue();
        for (int v = vertices; v < total; v++)
        {
            int source = geo.SourceVertices[v];
            AssertThat(source >= 100 && source < 100 + vertices).IsTrue();
            AssertThat(geo.BoneIds[v * 4]).IsEqual((source - 100) % 7);
        }
    }

    [TestCase]
    public void ClothAndBonelessSurfacesGetNoFarLevel()
    {
        (float[] positions, float[] normals, float[] uvs, uint[] indices) = Grid(20);
        int vertices = positions.Length / 3;
        var rigid = new MdlGeometry { Positions = positions, Normals = normals, Uvs = uvs, Indices = indices };
        AssertThat(AndroidCharacterFarLods.Append(rigid, new[] { 0.1f })).IsEqual(0);

        var cloth = new MdlGeometry
        {
            Positions = positions, Normals = normals, Uvs = uvs, Indices = indices, ClothRecord = 2,
            BoneIds = new int[vertices * 4], BoneWeights = new float[vertices * 4],
        };
        AssertThat(AndroidCharacterFarLods.Append(cloth, new[] { 0.1f })).IsEqual(0);
        AssertThat(cloth.VertexCount).IsEqual(vertices);
    }

    /// <summary>Godot's pick (mesh_storage.h `mesh_surface_get_lod`): the last level whose key is at or under the distance's limit, or the full list.</summary>
    private static int[] Pick(int[] full, IEnumerable<(float Key, int[] Indices)> levels, float limit)
    {
        int[] pick = full;
        foreach ((float key, int[] indices) in levels.OrderBy(l => l.Key))
        {
            if (key > limit)
            {
                break;
            }

            pick = indices;
        }

        return pick;
    }

    [TestCase]
    public void TheUnionMergeDrawsWhatEachPieceDrawsAloneAtEveryDistance()
    {
        var a = new AndroidBodyMerge.Piece(new[] { 0, 1, 2, 2, 1, 3, 3, 1, 4 }, 5,
            new SortedList<float, int[]> { [0.01f] = new[] { 0, 1, 2, 2, 1, 3 }, [0.05f] = new[] { 0, 1, 2 } });
        var b = new AndroidBodyMerge.Piece(new[] { 0, 1, 2, 0, 2, 3 }, 4,
            new SortedList<float, int[]> { [0.02f] = new[] { 0, 1, 2 } });
        var c = new AndroidBodyMerge.Piece(new[] { 0, 1, 2 }, 3, new SortedList<float, int[]>());

        List<(float Key, int[] Indices)>? joined = AndroidBodyMerge.Join(new[] { a, b, c });
        AssertThat(joined).IsNotNull();
        AssertThat(joined!.Select(l => l.Key).ToArray()).IsEqual(new[] { 0.01f, 0.02f, 0.05f });

        int[] full = a.Indices.Concat(b.Indices.Select(i => i + 5)).Concat(c.Indices.Select(i => i + 9)).ToArray();
        foreach (float limit in new[] { 0f, 0.005f, 0.01f, 0.015f, 0.02f, 0.03f, 0.05f, 1f })
        {
            int[] alone = Pick(a.Indices, a.Levels.Select(l => (l.Key, l.Value)), limit)
                .Concat(Pick(b.Indices, b.Levels.Select(l => (l.Key, l.Value)), limit).Select(i => i + 5))
                .Concat(Pick(c.Indices, c.Levels.Select(l => (l.Key, l.Value)), limit).Select(i => i + 9)).ToArray();
            AssertThat(Pick(full, joined, limit)).IsEqual(alone);
        }

        // Every union level is smaller than the joined surface, as Godot requires.
        AssertThat(joined.All(l => l.Indices.Length < full.Length)).IsTrue();
        AssertThat(AndroidBodyMerge.Join(new[] { c, c })).IsNull();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void AShellLevelKeepsTheFurredTrianglesThroughTheShellsMap()
    {
        var levels = new Godot.Collections.Dictionary
        {
            [0.01f] = new[] { 0, 1, 2, 2, 1, 5 },
            [0.05f] = new[] { 0, 1, 2, 0, 0, 0 },
        };

        var mapped = new Dictionary<int, int>();
        int Map(int v) => mapped.TryGetValue(v, out int at) ? at : mapped[v] = mapped.Count;
        bool Fur(int v) => v != 5;

        Godot.Collections.Dictionary? cut = AndroidFurShellLods.Cut(levels, Fur, Map, 9);
        AssertThat(cut).IsNotNull();
        AssertThat(cut!.Count).IsEqual(2);
        AssertThat(cut[0.01f].AsInt32Array()).IsEqual(new[] { 0, 1, 2 });
        AssertThat(cut[0.05f].AsInt32Array()).IsEqual(new[] { 0, 1, 2, 0, 0, 0 });
        AssertThat(mapped.ContainsKey(5)).IsFalse();

        // A level no smaller than the shell itself is refused, as Godot would.
        AssertThat(AndroidFurShellLods.Cut(levels, Fur, Map, 3)).IsNull();
        AssertThat(AndroidFurShellLods.Cut(null, Fur, Map, 9)).IsNull();
    }

    [TestCase]
    public void TheBiasAndFarShadingAreLiveKeysWithNeutralDefaults()
    {
        AssertThat(AndroidCharacterLevers.WantedBias(new Dictionary<string, string>())).IsEqual(1f);
        AssertThat(AndroidCharacterLevers.WantedBias(new Dictionary<string, string> { [AndroidCharacterLevers.LodBiasKey] = "0.5" })).IsEqual(0.5f);
        AssertThat(AndroidCharacterLevers.WantedBias(new Dictionary<string, string> { [AndroidCharacterLevers.LodBiasKey] = "0" })).IsEqual(1f);
        AssertThat(AndroidCharacterLevers.WantedBias(new Dictionary<string, string> { [AndroidCharacterLevers.LodBiasKey] = "9" })).IsEqual(4f);
        AssertThat(AndroidCharacterLevers.WantedMetres(new Dictionary<string, string>())).IsEqual(0f);
        AssertThat(AndroidCharacterLevers.WantedMetres(new Dictionary<string, string> { [AndroidCharacterLevers.FarShadingKey] = "20" })).IsEqual(20f);
        AssertThat(AndroidCharacterLevers.Idle(0f, 1f, 0, 0)).IsTrue();
        AssertThat(AndroidCharacterLevers.Idle(20f, 1f, 0, 0)).IsFalse();
        AssertThat(AndroidCharacterLevers.Idle(0f, 0.5f, 0, 0)).IsFalse();
        AssertThat(AndroidCharacterLevers.Idle(0f, 1f, 3, 0)).IsFalse();
        AssertThat(AndroidCharacterLevers.Idle(0f, 1f, 0, 5)).IsFalse();
        AssertThat(AndroidFurShellLods.Wanted(new Dictionary<string, string>())).IsFalse();
        AssertThat(AndroidBodyMerge.Wanted(new Dictionary<string, string>())).IsTrue();
    }

    [TestCase]
    public void EveryKeyIsRegisteredWithItsDefaultAndAPlayersName()
    {
        var expected = new Dictionary<string, string>
        {
            [Engine_MdlAutoLod.SkinnedKey] = "false",
            [AndroidCharacterFarLods.Key] = "0",
            [AndroidCharacterLevers.LodBiasKey] = "1",
            [AndroidCharacterLevers.FarShadingKey] = "0",
            [AndroidFurShellLods.Key] = "false",
            [AndroidBodyMerge.Key] = "true",
        };
        foreach ((string key, string value) in expected)
        {
            RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == key);
            AssertThat(row).OverrideFailureMessage(key).IsNotNull();
            AssertThat(row!.DefaultValue).OverrideFailureMessage(key).IsEqual(value);
            AssertThat(AndroidSettingNames.Keys.Contains(key)).OverrideFailureMessage(key).IsTrue();
        }
    }
}
