using System;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.Level;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Checks exact R8 support and complete publication/retirement lifecycles without native Godot constructors.</summary>
[TestSuite]
public sealed class TerrainWeightSupportTests
{
    /// <summary>Creates a complete synthetic upload contract; each chunk owns 32 intervals and one shared edge.</summary>
    private static TerrainSplatData Data(int planes = 2, int chunksX = 1, int chunksZ = 1,
        float[]? weights = null, float originX = 0, float originZ = 0, float extentX = 32, float extentZ = 32) => new()
    {
        MaterialCount = planes, ChunkWidth = chunksX, ChunkHeight = chunksZ,
        WeightWidth = chunksX * 32 + 1, WeightHeight = chunksZ * 32 + 1,
        WorldOriginX = originX, WorldOriginZ = originZ, ChunkExtentX = extentX, ChunkExtentZ = extentZ,
        Weights = weights ?? new float[(chunksX * 32 + 1) * (chunksZ * 32 + 1) * planes],
    };

    /// <summary>Sets one uploaded weight without changing any scalar address metadata.</summary>
    private static void Set(TerrainSplatData data, int plane, int x, int z, float value = 1) =>
        data.Weights[(plane * data.WeightHeight + z) * data.WeightWidth + x] = value;

    /// <summary>Clamping, round-to-nearest and all byte centres exactly match the prior uploader expression.</summary>
    [TestCase]
    public void UploadQuantizationPreservesExactBytes()
    {
        AssertThat(Engine_TerrainWeightSupport.Quantize(-1)).IsEqual((byte)0);
        AssertThat(Engine_TerrainWeightSupport.Quantize(.001f)).IsEqual((byte)0);
        AssertThat(Engine_TerrainWeightSupport.Quantize(.002f)).IsEqual((byte)1);
        AssertThat(Engine_TerrainWeightSupport.Quantize(.5f)).IsEqual((byte)128);
        AssertThat(Engine_TerrainWeightSupport.Quantize(2)).IsEqual((byte)255);
        for (int i = -1; i <= 256; i++)
        {
            float weight = i / 255f;
            byte previous = weight <= 0f ? (byte)0 : weight >= 1f ? (byte)255 : (byte)((weight * 255f) + .5f);
            AssertThat(Engine_TerrainWeightSupport.Quantize(weight)).IsEqual(previous);
        }
    }

    /// <summary>Zero support, each of 32 signed bits and the all-material pattern remain distinct.</summary>
    [TestCase]
    public void EveryMaterialBitAndQuantizedZeroRemainExact()
    {
        var data = Data(32);
        Set(data, 0, 16, 16, .001f);
        AssertThat(Engine_TerrainWeightSupport.TryCreate(data)!.Mask(0, 0, 32, 32)).IsEqual(0);
        for (int plane = 0; plane < 32; plane++)
        {
            Array.Clear(data.Weights);
            Set(data, plane, 16, 16, .002f);
            int expected = unchecked((int)(1U << plane));
            AssertThat(Engine_TerrainWeightSupport.TryCreate(data)!.Mask(0, 0, 32, 32)).IsEqual(expected);
        }
        for (int plane = 0; plane < 32; plane++) Set(data, plane, 16, 16);
        var full = Engine_TerrainWeightSupport.TryCreate(data)!;
        AssertThat(full.Mask(0, 0, 32, 32)).IsEqual(-1);
        AssertThat(full.RetainedBytes).IsEqual(8448L);
    }

    /// <summary>Fractional footprint, extra halo and low/high shader clamps never omit a contributing edge.</summary>
    [TestCase]
    public void FractionalHaloAndBothBordersStayConservative()
    {
        var data = Data(4);
        Set(data, 0, 0, 0);
        Set(data, 1, 12, 12, 1f / 255);
        Set(data, 2, 32, 32);
        Set(data, 3, 16, 16);
        var support = Engine_TerrainWeightSupport.TryCreate(data)!;
        AssertThat(support.Mask(-100, -100, -1, -1)).IsEqual(1);
        AssertThat(support.Mask(10.2f, 10.2f, 10.8f, 10.8f)).IsEqual(2);
        AssertThat(support.Mask(100, 100, 1000, 1000)).IsEqual(4);
        AssertThat(support.Mask(15, 15, 17, 17)).IsEqual(8);
        AssertThat(support.Mask(4, 4, 5, 5)).IsEqual(0);
    }

    /// <summary>Packed word transitions and plane strides cannot leak a neighbouring layer into a mask.</summary>
    [TestCase]
    public void PackedRowsWordsAndPlane31AreIndependent()
    {
        var data = Data(32, chunksX: 3, extentX: 16);
        Set(data, 31, 64, 16);
        Set(data, 0, 63, 16);
        Set(data, 1, 65, 17);
        Set(data, 2, 66, 16);
        var support = Engine_TerrainWeightSupport.TryCreate(data)!;
        AssertThat(support.Mask(32, 16, 32, 16)).IsEqual(unchecked((int)0x80000003U));
        AssertThat(support.Mask(1, 1, 2, 2)).IsEqual(0);
        AssertThat(support.Mask(32, 1, 32, 2)).IsEqual(0);
    }

    /// <summary>Invalid upload shapes, null arrays, nonfinite weights and unsupported material counts use the full loop.</summary>
    [TestCase]
    public void InvalidMetadataDeclinesOptimization()
    {
        AssertThat(Engine_TerrainWeightSupport.TryCreate(null!) is null).IsTrue();
        AssertThat(Engine_TerrainWeightSupport.TryCreate(Data(0)) is null).IsTrue();
        AssertThat(Engine_TerrainWeightSupport.TryCreate(Data(33)) is null).IsTrue();
        AssertThat(Engine_TerrainWeightSupport.TryCreate(Data(chunksX: 0)) is null).IsTrue();
        AssertThat(Engine_TerrainWeightSupport.TryCreate(Data(weights: Array.Empty<float>())) is null).IsTrue();
        AssertThat(Engine_TerrainWeightSupport.TryCreate(Data(originX: float.NaN)) is null).IsTrue();
        AssertThat(Engine_TerrainWeightSupport.TryCreate(Data(extentX: 0)) is null).IsTrue();
        AssertThat(Engine_TerrainWeightSupport.TryCreate(Data(extentZ: float.PositiveInfinity)) is null).IsTrue();
        var missing = new TerrainSplatData { MaterialCount = 1, ChunkWidth = 1, ChunkHeight = 1,
            WeightWidth = 33, WeightHeight = 33, ChunkExtentX = 32, ChunkExtentZ = 32, Weights = null! };
        AssertThat(Engine_TerrainWeightSupport.TryCreate(missing) is null).IsTrue();
        foreach (float invalid in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity })
        {
            var data = Data(); Set(data, 1, 16, 16, invalid);
            AssertThat(Engine_TerrainWeightSupport.TryCreate(data) is null).IsTrue();
        }
        var support = Engine_TerrainWeightSupport.TryCreate(Data())!;
        AssertThat(support.Mask(float.NaN, 0, 1, 1)).IsEqual(-1);
        AssertThat(support.Mask(1, 0, 0, 1)).IsEqual(-1);
        AssertThat(support.Mask(0, float.NegativeInfinity, 1, 1)).IsEqual(-1);
    }

    /// <summary>Omitted layers sample exact zero throughout randomized bilinear footprints, including nonzero origins.</summary>
    [TestCase]
    public void OmittedBilinearSamplesAreExactZero()
    {
        var data = Data(8, chunksX: 2, originX: -16, originZ: -8, extentX: 16, extentZ: 8);
        for (int plane = 0; plane < 8; plane++)
            Set(data, plane, plane * 8, plane * 4, plane % 2 == 0 ? .001f : .3f);
        var support = Engine_TerrainWeightSupport.TryCreate(data)!;
        var random = new Random(390);
        for (int sample = 0; sample < 400; sample++)
        {
            float minX = -20 + (float)random.NextDouble() * 40;
            float minZ = -10 + (float)random.NextDouble() * 12;
            float maxX = minX + (float)random.NextDouble() * 2;
            float maxZ = minZ + (float)random.NextDouble() * 2;
            uint mask = unchecked((uint)support.Mask(minX, minZ, maxX, maxZ));
            double x = Math.Clamp(((minX + maxX) / 2d + 16) / 16, 0, 1.999) * 32;
            double z = Math.Clamp(((minZ + maxZ) / 2d + 8) / 8, 0, .999) * 32;
            int x0 = (int)Math.Floor(x), z0 = (int)Math.Floor(z);
            for (int plane = 0; plane < 8; plane++)
            {
                if ((mask & (1U << plane)) != 0) continue;
                foreach (int px in new[] { x0, Math.Min(x0 + 1, data.WeightWidth - 1) })
                foreach (int pz in new[] { z0, Math.Min(z0 + 1, data.WeightHeight - 1) })
                    AssertThat(Engine_TerrainWeightSupport.Quantize(data.Weights[
                        (plane * data.WeightHeight + pz) * data.WeightWidth + px])).IsEqual((byte)0);
            }
        }
    }

    /// <summary>A shared owner gives adjacent chunks independent masks and regeneration cannot mutate earlier snapshots.</summary>
    [TestCase]
    public void SharedOwnerRegenerationAndFallbackHaveCompleteLifecycles()
    {
        var data = Data(32, chunksX: 2);
        Set(data, 0, 16, 16); Set(data, 31, 48, 16);
        var snapshot = Engine_TerrainWeightSupport.TryCreate(data)!;
        var registry = new Engine_TerrainWeightRegistry<object>();
        object shared = new(), duplicate = new();
        registry.Record(shared, snapshot);
        AssertThat(registry.Mask(shared, 0, 0, 32, 32)).IsEqual(1);
        AssertThat(registry.Mask(shared, 32, 0, 64, 32)).IsEqual(int.MinValue);
        AssertThat(registry.Mask(duplicate, 0, 0, 64, 32)).IsEqual(-1);
        AssertThat(registry.Mask(null, 0, 0, 64, 32)).IsEqual(-1);
        Array.Clear(data.Weights);
        registry.Record(shared, Engine_TerrainWeightSupport.TryCreate(data));
        AssertThat(registry.Mask(shared, 0, 0, 64, 32)).IsEqual(0);
        AssertThat(snapshot.Mask(0, 0, 64, 32)).IsEqual(unchecked((int)0x80000001U));
        registry.Record(shared, null);
        AssertThat(registry.Mask(shared, 0, 0, 64, 32)).IsEqual(-1);
        registry.Record(shared, snapshot);
        AssertThat(registry.Mask(shared, 0, 0, 64, 32)).IsEqual(unchecked((int)0x80000001U));
    }

    /// <summary>Creates a no-inline owner/source lifetime boundary so the weak table's retirement is observable.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Owner, WeakReference Source) PublishTemporary(Engine_TerrainWeightRegistry<object> registry)
    {
        var data = Data(); Set(data, 1, 16, 16);
        object owner = new();
        registry.Record(owner, Engine_TerrainWeightSupport.TryCreate(data));
        return (new WeakReference(owner), new WeakReference(data.Weights));
    }

    /// <summary>Support retains neither native-owner identities nor the much larger float upload array.</summary>
    [TestCase]
    public void OwnerAndSourceCanRetireWithoutDisposingSurvivors()
    {
        var registry = new Engine_TerrainWeightRegistry<object>();
        var data = Data(); Set(data, 0, 16, 16);
        object survivor = new(); registry.Record(survivor, Engine_TerrainWeightSupport.TryCreate(data));
        var retired = PublishTemporary(registry);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        AssertThat(retired.Owner.IsAlive).IsFalse();
        AssertThat(retired.Source.IsAlive).IsFalse();
        AssertThat(registry.Mask(survivor, 0, 0, 32, 32)).IsEqual(1);
        GC.KeepAlive(survivor); GC.KeepAlive(registry);
    }

    /// <summary>Real GHF vertices retain positive local x before the region mirror; mask bounds must use those vertices.</summary>
    [TestCase]
    public void ActualTerrainVerticesUsePreMirrorBounds()
    {
        var heights = new GhfHeights { Width = 129, Height = 65, OriginX = 12, OriginZ = -4,
            Spacing = .5f, Values = new float[129 * 65] };
        TerrainMesh mesh = Engine_GhfTerrain.BuildMesh(heights);
        var data = Data(2, chunksX: 2, originX: 12, originZ: -4);
        Set(data, 0, 16, 16); Set(data, 1, 48, 16);
        var support = Engine_TerrainWeightSupport.TryCreate(data)!;
        AssertThat(mesh.Positions[0]).IsEqual(12f);
        AssertThat(mesh.Positions[(128 * 3)]).IsEqual(76f);
        AssertThat(mesh.Positions[(64 * 129 * 3) + 2]).IsEqual(28f);
        AssertThat(support.Mask(mesh.Positions[0], -4, mesh.Positions[64 * 3], 28)).IsEqual(1);
        AssertThat(support.Mask(mesh.Positions[64 * 3], -4, mesh.Positions[128 * 3], 28)).IsEqual(2);
    }
}
