using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Refii.Level;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>What the ground reads with `terrain_normal_maps` off and on, and that off leaves its colours alone.</summary>
[TestSuite]
public sealed class TerrainNormalSourcesTests
{
    private const string Colour = "art/ground_c.tex", Normal = "art/ground_n.tex", Both = "art/both.tex";
    private const string DetailColour = "art/detail_c.tex", DetailNormal = "art/detail_n.tex";

    private static EhfParsedBody Palette(string baseNormal = Normal)
    {
        var parsed = new EhfParsedBody();
        var lod = new EhfLodEntry();
        lod.Strings[0] = Colour;
        lod.Strings[1] = baseNormal;
        lod.Strings[3] = DetailColour;
        lod.Strings[4] = DetailNormal;
        parsed.Lods.Add(lod);
        return parsed;
    }

    private static HashSet<string> Reads(EhfParsedBody parsed, bool normalMaps)
    {
        var read = new ConcurrentBag<string>();
        Engine_TerrainSplatMaterial.ClearSharedCache();
        Engine_TerrainSplatMaterial.ResolveMaterials(parsed, path =>
        {
            read.Add(path);
            return null;
        }, null, normalMaps, out _);
        return read.Where(p => !p.StartsWith("data/", StringComparison.Ordinal)).ToHashSet();
    }

    [TestCase, RequireGodotRuntime]
    public void OffReadsOnlyTheColours() =>
        AssertThat(Reads(Palette(), false).OrderBy(p => p).ToArray()).IsEqual(new[] { Colour, DetailColour }.OrderBy(p => p).ToArray());

    [TestCase, RequireGodotRuntime]
    public void OnReadsTheNormalsEvenAfterABuildWithoutThem()
    {
        Reads(Palette(), false);
        AssertThat(Reads(Palette(), true).OrderBy(p => p).ToArray())
            .IsEqual(new[] { Colour, DetailColour, Normal, DetailNormal }.OrderBy(p => p).ToArray());
    }

    [TestCase, RequireGodotRuntime]
    public void AColourAlsoNamedAsANormalIsStillRead()
    {
        var parsed = Palette(baseNormal: Colour);
        AssertThat(Reads(parsed, false).Contains(Colour)).IsTrue();
        AssertThat(Reads(parsed, false).Contains(Normal)).IsFalse();
    }

    private static SplatMaterial Layer(bool normals, byte seed)
    {
        byte[] Pixels(byte tint)
        {
            var pixels = new byte[4 * 4 * 4];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(tint + i);
            return pixels;
        }

        return new SplatMaterial
        {
            BaseRgba = Pixels(seed), BaseWidth = 4, BaseHeight = 4,
            DetailRgba = Pixels((byte)(seed + 40)), DetailWidth = 4, DetailHeight = 4,
            BaseNormalRgba = normals ? Pixels((byte)(seed + 80)) : Array.Empty<byte>(), BaseNormalWidth = normals ? 4 : 0, BaseNormalHeight = normals ? 4 : 0,
            DetailNormalRgba = normals ? Pixels((byte)(seed + 120)) : Array.Empty<byte>(), DetailNormalWidth = normals ? 4 : 0, DetailNormalHeight = normals ? 4 : 0,
            BaseTileScale = 0.25f, DetailTileScale = 0.5f, DetailIntensity = 1.5f,
        };
    }

    private static TerrainSplatData Splat(bool normals)
    {
        var parsed = new EhfParsedBody { ChunkWidth = 1, ChunkHeight = 1, SplatWidth = 2, SplatHeight = 2, SplatIndices = new byte[] { 0, 1, 1, 0 } };
        parsed.Chunks.Add(new EhfChunk());
        AssertThat(Engine_EhfSplat.TryBuild(parsed, new[] { Layer(normals, 1), Layer(normals, 9) }, out TerrainSplatData data, out _)).IsTrue();
        return data;
    }

    /// <summary>The arrays that reach the GPU with the setting off are built from the colours alone.</summary>
    [TestCase]
    public void NormalSourcesChangeNothingButTheNormalSlices()
    {
        TerrainSplatData with = Splat(true), without = Splat(false);
        AssertThat(without.SliceSize).IsEqual(with.SliceSize);
        AssertThat(without.MaterialCount).IsEqual(with.MaterialCount);
        AssertThat(without.MaterialParams.SequenceEqual(with.MaterialParams)).IsTrue();
        AssertThat(without.Weights.SequenceEqual(with.Weights)).IsTrue();
        AssertThat(without.CliffShares.SequenceEqual(with.CliffShares)).IsTrue();
        for (int i = 0; i < with.BaseSlices.Length; i++)
        {
            AssertThat(without.BaseSlices[i].AsSpan().SequenceEqual(with.BaseSlices[i])).IsTrue();
            AssertThat(without.DetailSlices[i].AsSpan().SequenceEqual(with.DetailSlices[i])).IsTrue();
        }

        // Off shares one flat slice across every layer rather than a slice a layer.
        AssertThat(ReferenceEquals(without.BaseNormalSlices[0], without.BaseNormalSlices[1])).IsTrue();
        AssertThat(with.BaseNormalSlices[0].AsSpan().SequenceEqual(without.BaseNormalSlices[0])).IsFalse();
    }
}
