using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using GdUnit4;
using Godot;
using Refii.Core;
using Refii.Level;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The terrain build's shortcuts against the longer way round: each must give the same bytes.</summary>
[TestSuite]
public sealed class TerrainBuildTests
{
    private const BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;

    /// <summary>The managed alpha test answers as Godot's DetectAlpha does, for every alpha and position.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void AlphaScanAnswersAsGodotDoes()
    {
        const int Size = 9;
        for (int alpha = 0; alpha < 256; alpha++)
        {
            foreach (int pixel in new[] { 0, 3, 4, 40, 79, 80 })
            {
                var rgba = new byte[Size * Size * 4];
                Array.Fill(rgba, (byte)255);
                rgba[(pixel * 4) + 3] = (byte)alpha;
                using Image image = Image.CreateFromData(Size, Size, false, Image.Format.Rgba8, rgba);
                AssertThat(Engine_TerrainSplatMaterial.DetectsAlpha(rgba))
                    .IsEqual(image.DetectAlpha() != Image.AlphaMode.None);
            }
        }

        // Colour bytes never count, however low.
        var opaque = new byte[Size * Size * 4];
        for (int i = 3; i < opaque.Length; i += 4)
        {
            opaque[i] = 255;
        }

        AssertThat(Engine_TerrainSplatMaterial.DetectsAlpha(opaque)).IsFalse();
    }

    private static byte[] Gzip(byte[] plain)
    {
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(plain);
        }

        return packed.ToArray();
    }

    /// <summary>What the doubling stream this replaced returned, or null where it refused.</summary>
    private static byte[]? ByStream(byte[] gz)
    {
        try
        {
            using var source = new MemoryStream(gz);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static void GunzipsAsTheStreamDid(byte[] gz)
    {
        byte[]? expected = ByStream(gz);
        byte[] bytes = gz;
        bool inflated = Engine_GameGzip.MaybeGunzip(ref bytes);
        AssertThat(inflated).IsEqual(expected is not null);
        AssertThat(bytes).ContainsExactly(expected ?? gz);
    }

    /// <summary>A trailer that is right, wrong, short, huge or only the last member's changes no output.</summary>
    [TestCase]
    public void GunzipGivesTheStreamsBytesWhateverTheTrailerSays()
    {
        var random = new Random(7);
        var plain = new byte[70000];
        random.NextBytes(plain);
        byte[] good = Gzip(plain);
        GunzipsAsTheStreamDid(good);
        GunzipsAsTheStreamDid(Gzip(Array.Empty<byte>()));
        GunzipsAsTheStreamDid(Gzip(new byte[] { 1 }));
        GunzipsAsTheStreamDid(Gzip(new byte[1 << 20]));

        byte[] two = Gzip(plain[..30000]);
        byte[] members = new byte[good.Length + two.Length];
        good.CopyTo(members, 0);
        two.CopyTo(members, good.Length);
        GunzipsAsTheStreamDid(members);

        foreach (int stated in new[] { 1, 1000, 69999, 70001, int.MaxValue, -1, 0 })
        {
            var lying = (byte[])good.Clone();
            BitConverter.TryWriteBytes(lying.AsSpan(^4), stated);
            GunzipsAsTheStreamDid(lying);
        }

        GunzipsAsTheStreamDid(good[..(good.Length / 2)]);
        GunzipsAsTheStreamDid(good[..10]);

        byte[] trailing = new byte[good.Length + 5];
        good.CopyTo(trailing, 0);
        GunzipsAsTheStreamDid(trailing);

        byte[] corrupt = (byte[])good.Clone();
        corrupt[corrupt.Length / 2] ^= 0xFF;
        GunzipsAsTheStreamDid(corrupt);

        byte[] notGzip = { 1, 2, 3, 4, 5 };
        byte[] left = notGzip;
        AssertThat(Engine_GameGzip.MaybeGunzip(ref left)).IsFalse();
        AssertThat(ReferenceEquals(left, notGzip)).IsTrue();
    }

    private static GhfHeights Grid(int width, int height)
    {
        var values = new float[width * height];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = MathF.Sin(i * 0.37f) * 5f;
        }

        return new GhfHeights { Width = width, Height = height, Values = values, OriginX = 3f, OriginZ = -8f };
    }

    /// <summary>Chunks come back in grid order, with the vertices and winding of one pass over the grid.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void ChunksKeepTheirGridOrderVerticesAndWinding()
    {
        const int ChunkCells = Engine_LevelMesh.ChunkCells;
        foreach ((int w, int h) in new[] { (129, 65), (65, 65), (40, 10), (130, 67) })
        {
            GhfHeights grid = Grid(w, h);
            TerrainMesh terrain = Engine_GhfTerrain.BuildMesh(grid, indices: false);
            AssertThat(terrain.Indices.Length).IsEqual(0);
            AssertThat(terrain.Positions).ContainsExactly(Engine_GhfTerrain.BuildMesh(grid).Positions);

            List<ArrayMesh> chunks = Engine_LevelMesh.ToChunkMeshes(terrain, "T");
            int columns = Math.Max(1, (w - 1 + ChunkCells - 1) / ChunkCells);
            int rows = Math.Max(1, (h - 1 + ChunkCells - 1) / ChunkCells);
            AssertThat(chunks.Count).IsEqual(rows * columns);

            var local = new int[w * h];
            int at = 0;
            for (int cy = 0; cy < rows; cy++)
            {
                int y0 = cy * ChunkCells;
                int y1 = Math.Min(h - 1, y0 + ChunkCells);
                for (int cx = 0; cx < columns; cx++)
                {
                    int x0 = cx * ChunkCells;
                    int x1 = Math.Min(w - 1, x0 + ChunkCells);
                    var vertices = new List<Vector3>();
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            local[(y * w) + x] = vertices.Count;
                            int v = ((y * w) + x) * 3;
                            vertices.Add(new Vector3(terrain.Positions[v], terrain.Positions[v + 1], terrain.Positions[v + 2]));
                        }
                    }

                    var indices = new List<int>();
                    for (int y = y0; y < y1; y++)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            int i00 = local[(y * w) + x];
                            int i10 = local[(y * w) + x + 1];
                            int i01 = local[((y + 1) * w) + x];
                            int i11 = local[((y + 1) * w) + x + 1];
                            indices.AddRange(new[] { i00, i01, i10, i10, i01, i11 });
                        }
                    }

                    ArrayMesh chunk = chunks[at++];
                    AssertThat(chunk.SurfaceGetName(0).ToString()).IsEqual($"T[{cx},{cy}]");
                    Godot.Collections.Array arrays = chunk.SurfaceGetArrays(0);
                    AssertThat(arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array()).ContainsExactly(vertices.ToArray());
                    AssertThat(arrays[(int)Mesh.ArrayType.Index].AsInt32Array()).ContainsExactly(indices.ToArray());
                }
            }
        }
    }

    private static EhfParsedBody Body(int planes, int chunksX, int chunksZ, int seed)
    {
        var random = new Random(seed);
        var body = new EhfParsedBody { ChunkWidth = (uint)chunksX, ChunkHeight = (uint)chunksZ };
        const int MapSize = 64;
        var splat = new byte[MapSize * MapSize];
        random.NextBytes(splat);
        body.SplatIndices = splat;
        body.SplatWidth = MapSize;
        body.SplatHeight = MapSize;
        var other = new byte[32 * 32];
        random.NextBytes(other);
        body.PaintResources.Add(new EhfPaintResource { Width = 32, Height = 32, Data = other });
        body.PaintResources.Add(new EhfPaintResource { Width = 16, Height = 16, Data = new byte[5] });
        for (int c = 0; c < chunksX * chunksZ; c++)
        {
            var chunk = new EhfChunk();
            chunk.Origin[0] = c % chunksX * 32f;
            chunk.Origin[1] = c / chunksX * 32f;
            chunk.Extent[0] = chunk.Origin[0] + 32f;
            chunk.Extent[1] = chunk.Origin[1] + 32f;
            int layers = random.Next(0, 20);
            for (int l = 0; l < layers; l++)
            {
                var layer = new EhfChunkLayer
                {
                    MaterialIndex = (uint)random.Next(0, planes + 2),
                    NameIndex = (uint)random.Next(0, 4),
                    TileU = (float)random.NextDouble() * 1.2f - 0.1f,
                    TileV = (float)random.NextDouble() * 1.2f - 0.1f,
                };
                layer.Blend[random.Next(0, 4)] = (byte)random.Next(0, 4);
                chunk.Layers.Add(layer);
            }

            body.Chunks.Add(chunk);
        }

        return body;
    }

    private static float SampleByHand(EhfParsedBody parsed, EhfChunkLayer layer, int px, int py)
    {
        byte[] map = parsed.SplatIndices;
        int width = (int)parsed.SplatWidth;
        int height = (int)parsed.SplatHeight;
        if (layer.NameIndex < parsed.PaintResources.Count)
        {
            EhfPaintResource resource = parsed.PaintResources[(int)layer.NameIndex];
            if (resource.Data.Length != 0 && resource.Width > 0 && resource.Height > 0)
            {
                map = resource.Data;
                width = (int)resource.Width;
                height = (int)resource.Height;
            }
        }

        if (map.Length == 0 || width <= 0 || height <= 0 || map.Length != (long)width * height)
        {
            return 1f;
        }

        int originX = Math.Clamp((int)MathF.Floor(layer.TileU * width), 0, width - 1);
        int originY = Math.Clamp((int)MathF.Floor(layer.TileV * height), 0, height - 1);
        return map[(Math.Clamp(originY + py, 0, height - 1) * (long)width) + Math.Clamp(originX + px, 0, width - 1)] / 255f;
    }

    /// <summary>The planes baked side by side hold the bits one pass over the chunks wrote, counters too.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void WeightsBakeToTheBitsOfOnePassOverTheChunks()
    {
        foreach ((int planes, int chunksX, int chunksZ, int seed) in new[] { (5, 3, 4, 1), (1, 2, 2, 2), (9, 5, 1, 3) })
        {
            EhfParsedBody parsed = Body(planes, chunksX, chunksZ, seed);
            var materials = new List<SplatMaterial>();
            byte[] pixels = new byte[16];
            for (int m = 0; m < planes; m++)
            {
                materials.Add(new SplatMaterial { BaseRgba = pixels, BaseWidth = 2, BaseHeight = 2, DetailRgba = pixels, DetailWidth = 2, DetailHeight = 2 });
            }

            AssertThat(Engine_EhfSplat.TryBuild(parsed, materials, out TerrainSplatData data, out string error, normalSlices: false)).IsTrue();
            AssertThat(error).IsEqual(string.Empty);
            AssertThat(data.BaseNormalSlices.Length).IsEqual(0);

            int width = (chunksX * 32) + 1;
            int height = (chunksZ * 32) + 1;
            long area = (long)width * height;
            var weights = new float[planes * area];
            var cliff = new float[planes * area];
            long contributions = 0, painted = 0, fallback = 0;
            for (int ci = 0; ci < parsed.Chunks.Count; ci++)
            {
                int cx = ci / chunksZ;
                int cy = ci % chunksZ;
                EhfChunk chunk = parsed.Chunks[ci];
                for (int li = 0; li < Math.Min(chunk.Layers.Count, Engine_EhfSplat.MaxLayers); li++)
                {
                    EhfChunkLayer layer = chunk.Layers[li];
                    int material = Math.Clamp((int)layer.MaterialIndex, 0, planes - 1);
                    bool isCliff = ((layer.Blend[0] | layer.Blend[1] | layer.Blend[2] | layer.Blend[3]) & 2) != 0;
                    for (int py = 0; py <= 32; py++)
                    {
                        for (int px = 0; px <= 32; px++)
                        {
                            float coverage = MathF.Max((SampleByHand(parsed, layer, px, py) - (1f / 15f)) * (15f / 14f), 0f);
                            if (coverage <= 0.0001f)
                            {
                                continue;
                            }

                            long at = (material * area) + (((cy * 32) + py) * (long)width) + (cx * 32) + px;
                            weights[at] += coverage;
                            if (isCliff)
                            {
                                cliff[at] += coverage;
                            }

                            contributions++;
                        }
                    }
                }
            }

            for (long p = 0; p < area; p++)
            {
                float sum = 0f;
                for (int m = 0; m < planes; m++)
                {
                    long at = (m * area) + p;
                    sum += weights[at];
                    cliff[at] = weights[at] > 0f ? Math.Clamp(cliff[at] / weights[at], 0f, 1f) : 0f;
                }

                if (sum > 0.00001f)
                {
                    painted++;
                }
                else
                {
                    weights[p] = 1f;
                    fallback++;
                }
            }

            AssertThat(data.Contributions).IsEqual(contributions);
            AssertThat(data.PaintedTexels).IsEqual(painted);
            AssertThat(data.FallbackTexels).IsEqual(fallback);
            AssertThat(System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.Weights.AsSpan())
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(weights.AsSpan()))).IsTrue();
            AssertThat(System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.CliffShares.AsSpan())
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(cliff.AsSpan()))).IsTrue();
        }
    }

    /// <summary>Layer normals left out are never read; a texture a colour slot also names still is, as a normal map.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void NormalsLeftOutAreNotRead()
    {
        var body = new EhfParsedBody();
        var lod = new EhfLodEntry();
        lod.Strings[0] = "art\\base.tex";
        lod.Strings[1] = "art\\base_n.tex";
        lod.Strings[3] = "art\\shared.tex";
        lod.Strings[4] = "art\\shared.tex";
        body.Lods.Add(lod);

        MethodInfo resolve = typeof(Engine_TerrainSplatMaterial).GetMethod("ResolveMaterials", Private)!;
        foreach (bool wanted in new[] { false, true })
        {
            Engine_TerrainSplatMaterial.ClearSharedCache();
            var asked = new System.Collections.Concurrent.ConcurrentBag<string>();
            Engine_TerrainSplatMaterial.ReadAsset read = path =>
            {
                asked.Add(path);
                return null;
            };

            resolve.Invoke(null, new object?[] { body, read, null, wanted, 0 });
            AssertThat(asked).Contains("art/base.tex", "art/shared.tex");
            AssertThat(new List<string>(asked).Contains("art/base_n.tex")).IsEqual(wanted);
        }
    }

    /// <summary>A layer that repeats an image is prepared once, keeps its place, and the caller's images survive.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void RepeatedLayersArePreparedOnce()
    {
        FieldInfo field = typeof(AndroidTextureUpload).GetField("_policy", Private)!;
        object? before = field.GetValue(null);
        field.SetValue(null, new AndroidTextureUploadPolicy(true, false, false, false, false, false, true, true, true));
        try
        {
            using Image first = Image.CreateEmpty(8, 8, false, Image.Format.Dxt1);
            using Image second = Image.CreateEmpty(8, 8, false, Image.Format.Dxt1);
            var images = new Godot.Collections.Array<Image> { first, second, first, first };
            long prepared = AndroidTextureUpload.PreparedImages;
            using var layered = new Texture2DArray();
            AssertThat(AndroidTextureUpload.CreateLayered(layered, images)).IsEqual(Error.Ok);
            AssertThat(AndroidTextureUpload.PreparedImages - prepared).IsEqual(2L);
            AssertThat(layered.GetLayers()).IsEqual(4);
            AssertThat(first.GetFormat()).IsEqual(Image.Format.Dxt1);
            AssertThat(first.IsEmpty()).IsFalse();
        }
        finally
        {
            field.SetValue(null, before);
        }
    }
}
