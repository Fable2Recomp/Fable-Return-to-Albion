using System;
using System.Collections.Generic;
using Godot;
using Refii.Textures;

namespace Refii.Level;

public static class Engine_TerrainSplatMaterial
{
    public const string ShaderPath = "res://src/Engine/level/terrain/terrain_splat.gdshader";

    public delegate byte[]? ReadAsset(string virtualPath);

    public delegate string? FindByName(string fileName);

    public static bool TryBuild(
        byte[] ehf,
        ReadAsset read,
        FindByName? findByName,
        out ShaderMaterial material,
        out string error)
    {
        material = null!;
        error = string.Empty;

        var clock = System.Diagnostics.Stopwatch.StartNew();

        if (!Engine_EhfBody.TryParse(ehf, out EhfParsedBody parsed, out error))
        {
            return false;
        }

        if (parsed.Lods.Count == 0)
        {
            error = "no LOD palette in the .ehf";
            return false;
        }

        var shader = GD.Load<Shader>(ShaderPath);
        if (shader is null)
        {
            error = $"terrain shader missing at {ShaderPath}";
            return false;
        }

        long parseMs = clock.ElapsedMilliseconds;
        clock.Restart();

        List<SplatMaterial> materials = ResolveMaterials(parsed, read, findByName, out int resolved);
        if (resolved == 0)
        {
            error = $"none of the {parsed.Lods.Count} palette textures resolved";
            return false;
        }

        long resolveMs = clock.ElapsedMilliseconds;
        clock.Restart();

        if (!Engine_EhfSplat.TryBuild(parsed, materials, out TerrainSplatData data, out error))
        {
            return false;
        }

        long splatMs = clock.ElapsedMilliseconds;
        clock.Restart();

        Texture2DArray? baseArray = BuildColourArray(data.BaseSlices, data.SliceSize);
        Texture2DArray? detailArray = BuildColourArray(data.DetailSlices, data.SliceSize);
        Texture2DArray? weightArray = BuildWeightArray(data);

        if (baseArray is null || detailArray is null || weightArray is null)
        {
            error = "could not create the terrain texture arrays";
            return false;
        }

        GD.Print(
            $"[terrain] splat timing: parse {parseMs} ms, textures {resolveMs} ms, " +
            $"splat {splatMs} ms, arrays {clock.ElapsedMilliseconds} ms");

        material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("lod_array", baseArray);
        material.SetShaderParameter("lod_detail_array", detailArray);
        material.SetShaderParameter("material_weight", weightArray);

        // The layers' normal maps, which the .ehf has always named and nothing has ever loaded.
        // A missing pair leaves the shader's flat default, which costs a sample and nothing else.
        Texture2DArray? baseNormalArray = BuildColourArray(data.BaseNormalSlices, data.SliceSize);
        Texture2DArray? detailNormalArray =
            BuildColourArray(data.DetailNormalSlices, data.SliceSize);
        int normalCount = 0;
        foreach (EhfLodEntry lod in parsed.Lods)
        {
            if (!string.IsNullOrEmpty(lod.BaseNormal) || !string.IsNullOrEmpty(lod.DetailNormal))
            {
                ++normalCount;
            }
        }

        if (baseNormalArray is not null && detailNormalArray is not null)
        {
            material.SetShaderParameter("lod_normal_array", baseNormalArray);
            material.SetShaderParameter("lod_detail_normal_array", detailNormalArray);
            material.SetShaderParameter("has_normals", normalCount != 0);

            // The ground's share of the ORM: per-layer cavity derived from the same normal
            // slices, base in R and cliff in G, sampled with the colour's own tiling. The
            // authored terrain lightmap is a different frequency band entirely — one texel per
            // heightfield CELL, metres across — so texture-scale cavity occludes nothing the
            // bake already encodes, which is why the props' authored-bake exemption does not
            // apply here.
            if (normalCount != 0 &&
                Engine_TexSettings.OrmEnabled && Engine_TexSettings.OrmAoStrength > 0.0f)
            {
                Texture2DArray? aoArray = BuildCavityArray(
                    data, Engine_TexSettings.OrmAoStrength, out bool hasHeight);
                if (aoArray is not null)
                {
                    material.SetShaderParameter("lod_ao_array", aoArray);
                    material.SetShaderParameter("has_ao", true);
                    material.SetShaderParameter("has_height", hasHeight);
                }
            }
        }

        material.SetShaderParameter(
            "chunk_origin_extent",
            new Vector4(data.WorldOriginX, data.WorldOriginZ, data.ChunkExtentX, data.ChunkExtentZ));

        material.SetShaderParameter(
            "chunk_grid_size",
            new Vector4(data.ChunkWidth, data.ChunkHeight, data.MaterialCount, 255f));

        material.SetShaderParameter(
            "splat_params",
            new Vector4(
                3f,
                data.TileScale > 0f ? data.TileScale : 0.125f,
                parsed.SplatWidth > 0 ? 32f / parsed.SplatWidth : 0f,
                parsed.SplatHeight > 0 ? 32f / parsed.SplatHeight : 0f));

        material.SetShaderParameter("mesh_xform", new Vector4(1f, 1f, 0f, 0f));

        material.SetShaderParameter(
            "weight_size",
            new Vector4(
                data.WeightWidth,
                data.WeightHeight,
                data.WeightWidth > 0 ? 1f / data.WeightWidth : 1f,
                data.WeightHeight > 0 ? 1f / data.WeightHeight : 1f));

        var parameters = new Vector4[Engine_EhfSplat.MaxMaterials];
        for (int i = 0; i < parameters.Length; i++)
        {
            parameters[i] = new Vector4(
                data.MaterialParams[(i * 4) + 0],
                data.MaterialParams[(i * 4) + 1],
                data.MaterialParams[(i * 4) + 2],
                data.MaterialParams[(i * 4) + 3]);
        }

        material.SetShaderParameter("material_params", parameters);

        GD.Print(
            $"[terrain] splat: {data.ChunkWidth}x{data.ChunkHeight} chunks, " +
            $"{data.MaterialCount} materials ({resolved} textured, {normalCount} normal-mapped), " +
            $"weights {data.WeightWidth}x{data.WeightHeight} " +
            $"({data.Contributions} layer samples, {data.PaintedTexels} painted, " +
            $"{data.FallbackTexels} fallback)");

        return true;
    }

    private static List<SplatMaterial> ResolveMaterials(
        EhfParsedBody parsed, ReadAsset read, FindByName? findByName, out int resolved)
    {
        resolved = 0;

        var cache = new Dictionary<string, (byte[] Rgba, int Width, int Height)>(
            StringComparer.OrdinalIgnoreCase);
        var materials = new List<SplatMaterial>(parsed.Lods.Count);

        var unique = new List<string>();
        var normalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (EhfLodEntry lod in parsed.Lods)
        {
            string[] slots = { lod.BaseDiffuse, lod.DetailDiffuse, lod.BaseNormal, lod.DetailNormal };
            for (int slot = 0; slot < slots.Length; slot++)
            {
                string path = slots[slot];
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                string normalized = path.Replace('\\', '/').Trim('/');
                if (slot >= 2)
                {
                    normalPaths.Add(normalized);
                }

                if (!cache.ContainsKey(normalized))
                {
                    cache[normalized] = default;
                    unique.Add(normalized);
                }
            }
        }

        var decoded = new (byte[] Rgba, int Width, int Height)[unique.Count];
        System.Threading.Tasks.Parallel.For(0, unique.Count, i =>
        {
            decoded[i] = DecodeShared(unique[i], read, findByName, normalPaths.Contains(unique[i]));
        });

        for (int i = 0; i < unique.Count; i++)
        {
            cache[unique[i]] = decoded[i];
        }

        foreach (EhfLodEntry lod in parsed.Lods)
        {
            (byte[] baseRgba, int baseWidth, int baseHeight) =
                LoadTexture(lod.BaseDiffuse, read, findByName, cache, false);
            (byte[] detailRgba, int detailWidth, int detailHeight) =
                LoadTexture(lod.DetailDiffuse, read, findByName, cache, false);
            (byte[] baseNormal, int baseNormalWidth, int baseNormalHeight) =
                LoadTexture(lod.BaseNormal, read, findByName, cache, true);
            (byte[] detailNormal, int detailNormalWidth, int detailNormalHeight) =
                LoadTexture(lod.DetailNormal, read, findByName, cache, true);

            if (baseRgba.Length != 0 || detailRgba.Length != 0)
            {
                resolved++;
            }

            materials.Add(new SplatMaterial
            {
                BaseRgba = baseRgba,
                BaseWidth = baseWidth,
                BaseHeight = baseHeight,
                DetailRgba = detailRgba,
                DetailWidth = detailWidth,
                DetailHeight = detailHeight,
                BaseNormalRgba = baseNormal,
                BaseNormalWidth = baseNormalWidth,
                BaseNormalHeight = baseNormalHeight,
                DetailNormalRgba = detailNormal,
                DetailNormalWidth = detailNormalWidth,
                DetailNormalHeight = detailNormalHeight,
                BaseTileScale = lod.BaseTileScale,
                DetailTileScale = lod.DetailTileScale,
                DetailIntensity = lod.DetailIntensity,
            });
        }

        return materials;
    }

    private static (byte[] Rgba, int Width, int Height) LoadTexture(
        string path,
        ReadAsset read,
        FindByName? findByName,
        Dictionary<string, (byte[], int, int)> cache,
        bool normalMap)
    {
        if (string.IsNullOrEmpty(path))
        {
            return (Array.Empty<byte>(), 0, 0);
        }

        string normalized = path.Replace('\\', '/').Trim('/');
        if (cache.TryGetValue(normalized, out (byte[] Rgba, int Width, int Height) hit) &&
            hit.Rgba is not null)
        {
            return hit;
        }

        (byte[], int, int) result = DecodeTexture(normalized, read, findByName, normalMap);
        cache[normalized] = result;
        return result;
    }

    private static readonly Dictionary<string, (byte[] Rgba, int Width, int Height)> Shared =
        new(StringComparer.OrdinalIgnoreCase);

    private const long SharedBudget = 384L << 20;

    private static long _sharedBytes;

    private static (byte[] Rgba, int Width, int Height) DecodeShared(
        string normalized, ReadAsset read, FindByName? findByName, bool normalMap)
    {
        lock (Shared)
        {
            if (Shared.TryGetValue(normalized, out (byte[] Rgba, int Width, int Height) hit))
            {
                return hit;
            }
        }

        (byte[] Rgba, int Width, int Height) result =
            DecodeTexture(normalized, read, findByName, normalMap);
        if (result.Rgba.Length == 0)
        {
            return result;
        }

        lock (Shared)
        {
            if (_sharedBytes + result.Rgba.LongLength > SharedBudget)
            {
                Shared.Clear();
                _sharedBytes = 0;
            }

            if (Shared.TryAdd(normalized, result))
            {
                _sharedBytes += result.Rgba.LongLength;
            }
        }

        return result;
    }

    private static (byte[] Rgba, int Width, int Height) DecodeTexture(
        string normalized, ReadAsset read, FindByName? findByName, bool normalMap)
    {
        byte[]? bytes = read(normalized) ?? read("data/" + normalized);

        if (bytes is null && findByName is not null)
        {
            int slash = normalized.LastIndexOf('/');
            string name = slash < 0 ? normalized : normalized[(slash + 1)..];
            string? found = findByName(name);
            if (found is not null)
            {
                bytes = read(found);
            }
        }

        (byte[], int, int) result = (Array.Empty<byte>(), 0, 0);

        if (bytes is not null)
        {
            Image? image = Engine_TexImage.TryCreateImage(bytes, out bool hasAlpha, out string why);
            if (image is null)
            {
                GD.Print($"[terrain] {normalized}: {why}");
            }
            else
            {
                // THE SAME ENHANCEMENT EVERY PROP TEXTURE GETS. The splat arrays resample all
                // slices to CommonLodSize regardless, so feeding the upscaled result costs no
                // VRAM — it swaps a bilinear stretch up to 1024 for a minified sharp 2048.
                // `compress: true` (the default) keeps the cache entry IDENTICAL to the prop
                // pipeline's, so the boot sweep and the props share this artefact; ToRgba
                // decompresses for the CPU-side array assembly. Synchronous by design: the
                // arrays are built once at level load and cannot swap a texture in later.
                // `texture_upscaling_when` governs this like every texture: "off" makes
                // EnhanceDecoded a no-op, "play" pays here per region, "boot" hits the
                // sweep's cache.
                Image? enhanced = Engine_TexLibrary.EnhanceDecoded(
                    bytes, image,
                    normalMap ? TexSemantic.Normal : TexSemantic.Albedo,
                    hasAlpha, normalized);
                result = ToRgba(enhanced ?? image, normalized);
            }
        }

        return result;
    }

    private static (byte[] Rgba, int Width, int Height) ToRgba(Image image, string path)
    {
        var empty = (Array.Empty<byte>(), 0, 0);

        if (image.IsCompressed())
        {
            Error decompressed = image.Decompress();
            if (decompressed != Error.Ok)
            {
                GD.Print($"[terrain] {path}: decompress failed ({decompressed})");
                return empty;
            }
        }

        if (image.HasMipmaps())
        {
            image.ClearMipmaps();
        }

        if (image.GetFormat() != Image.Format.Rgba8)
        {
            image.Convert(Image.Format.Rgba8);
            if (image.GetFormat() != Image.Format.Rgba8)
            {
                GD.Print($"[terrain] {path}: could not convert {image.GetFormat()} to RGBA8");
                return empty;
            }
        }

        int width = image.GetWidth();
        int height = image.GetHeight();
        byte[] data = image.GetData();

        if (width <= 0 || height <= 0 || data.Length < (long)width * height * 4)
        {
            GD.Print(
                $"[terrain] {path}: {width}x{height} needs {(long)width * height * 4} bytes, " +
                $"got {data.Length}");
            return empty;
        }

        return (data, width, height);
    }

    private static Texture2DArray? BuildColourArray(byte[][] slices, int size)
    {
        if (slices.Length == 0)
        {
            return null;
        }

        var built = new Image[slices.Length];
        System.Threading.Tasks.Parallel.For(0, slices.Length, i =>
        {
            Image image = Image.CreateFromData(size, size, false, Image.Format.Rgba8, slices[i]);
            image.GenerateMipmaps();
            built[i] = image;
        });

        var images = new Godot.Collections.Array<Image>();
        foreach (Image image in built)
        {
            if (image is null)
            {
                return null;
            }

            images.Add(image);
        }

        var array = new Texture2DArray();
        return array.CreateFromImages(images) == Error.Ok ? array : null;
    }

    /// <summary>
    /// Per-layer cavity, derived from the layer normal slices: base occlusion in R, the cliff
    /// projection's in G. One two-channel slice per material, mip-mapped like the colours.
    /// </summary>
    /// <remarks>
    /// The TILING derivation, not the prop one — these textures repeat across the whole ground,
    /// so the divergence neighbourhood wraps; the clamped filter would print a seam at every
    /// tile boundary. A layer that shipped no normal map has a flat slice, whose divergence is
    /// zero everywhere, and its cavity comes out a uniform 1 — neutral by construction.
    /// </remarks>
    private static Texture2DArray? BuildCavityArray(
        TerrainSplatData data, float strength, out bool hasHeight)
    {
        hasHeight = false;
        byte[][] baseSlices = data.BaseNormalSlices;
        byte[][] detailSlices = data.DetailNormalSlices;
        int size = data.SliceSize;
        if (baseSlices.Length == 0 || detailSlices.Length != baseSlices.Length || size <= 0)
        {
            return null;
        }

        // B carries the ground layer's HEIGHT, solved from the same tiling normal by the same
        // wrapped Poisson the props' ORM alpha uses — the ground's share of the parallax the
        // cobbles already take. A layer whose normal states no relief gets the flat 128, the
        // value the shader's (h - 0.5) turns into exactly no offset.
        bool solveHeight = Engine_TexSettings.OrmHeight;
        var anyHeight = new bool[baseSlices.Length];
        var built = new Image[baseSlices.Length];
        System.Threading.Tasks.Parallel.For(0, baseSlices.Length, i =>
        {
            byte[] baseCavity = Engine_TexOrm.CavityFromTilingNormal(
                baseSlices[i], size, size, strength);
            byte[] detailCavity = Engine_TexOrm.CavityFromTilingNormal(
                detailSlices[i], size, size, strength);

            byte[]? height = null;
            if (solveHeight)
            {
                Image normal = Image.CreateFromData(
                    size, size, false, Image.Format.Rgba8, baseSlices[i]);
                height = Engine_TexHeight.TrySolve(normal, size, size);
                anyHeight[i] = height is not null;
            }

            var rgb = new byte[size * size * 3];
            for (int t = 0; t < baseCavity.Length; t++)
            {
                rgb[t * 3] = baseCavity[t];
                rgb[(t * 3) + 1] = detailCavity[t];
                rgb[(t * 3) + 2] = height is null ? (byte)128 : height[t];
            }

            Image image = Image.CreateFromData(size, size, false, Image.Format.Rgb8, rgb);
            image.GenerateMipmaps();
            built[i] = image;
        });

        var images = new Godot.Collections.Array<Image>();
        foreach (Image image in built)
        {
            if (image is null)
            {
                return null;
            }

            images.Add(image);
        }

        foreach (bool solved in anyHeight)
        {
            hasHeight |= solved;
        }

        var array = new Texture2DArray();
        return array.CreateFromImages(images) == Error.Ok ? array : null;
    }

    private static Texture2DArray? BuildWeightArray(TerrainSplatData data)
    {
        long area = (long)data.WeightWidth * data.WeightHeight;
        if (area == 0 || data.MaterialCount == 0)
        {
            return null;
        }

        int planes = Math.Min(data.MaterialCount, Engine_EhfSplat.MaxMaterials);
        var images = new Godot.Collections.Array<Image>();

        for (int plane = 0; plane < planes; plane++)
        {
            var bytes = new byte[area * 4];
            Buffer.BlockCopy(data.Weights, (int)(plane * area * 4), bytes, 0, bytes.Length);

            images.Add(Image.CreateFromData(
                data.WeightWidth, data.WeightHeight, false, Image.Format.Rf, bytes));
        }

        var array = new Texture2DArray();
        return array.CreateFromImages(images) == Error.Ok ? array : null;
    }
}
