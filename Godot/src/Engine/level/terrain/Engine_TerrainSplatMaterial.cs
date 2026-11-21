using System;
using System.Collections.Generic;
using Godot;
using Refii.Textures;

namespace Refii.Level;

/// <summary>Builds the shared terrain arrays and their detached, conservative weight support.</summary>
public static partial class Engine_TerrainSplatMaterial
{
    private static readonly Engine_TerrainWeightRegistry<ShaderMaterial> WeightSupports = new();

    /// <summary>The per-draw mask; -1 preserves every material when support is unavailable.</summary>
    internal const string ActiveMaterialMaskParameter = "terrain_active_material_mask";

    /// <summary>Records only quantized support after the complete immutable shared material has been built.</summary>
    /// <param name="material">The exact material whose weight array was uploaded from the supplied data.</param>
    /// <param name="data">The upload metadata; invalid or missing support restores the full-loop fallback.</param>
    internal static void RecordWeightSupport(ShaderMaterial material, TerrainSplatData data) =>
        WeightSupports.Record(material, Engine_TerrainWeightSupport.TryCreate(data));

    /// <summary>Sets a draw-local mask from unchanged mesh-local bounds, before the region's mirrored transform.</summary>
    /// <param name="chunk">A terrain draw using the unchanged mesh vertices.</param>
    /// <param name="material">Its shared material; paged/imported/unknown materials retain the default full loop.</param>
    /// <remarks>No material is duplicated and no texture ownership moves. The factory's identity mesh_xform
    /// is the support contract; the page shader exits before the sparse loop. Loaded scenes without
    /// this process-local support safely use -1, as do rebuilt materials with malformed metadata.</remarks>
    internal static void ApplyWeightSupport(MeshInstance3D chunk, Material? material)
    {
        if (material is not ShaderMaterial splat || !Refii.Models.Engine_ShaderVariant.IsTerrainSplat(Refii.Models.Engine_ShaderSpecialisation.PathOf(splat.Shader)) ||
            chunk.Mesh is not { } mesh) return;
        Aabb bounds = mesh.GetAabb();
        Vector3 end = bounds.End;
        chunk.SetInstanceShaderParameter(ActiveMaterialMaskParameter,
            WeightSupports.Mask(splat, bounds.Position.X, bounds.Position.Z, end.X, end.Z));
    }
    public const string ShaderPath = Refii.Models.Engine_ShaderVariant.TerrainFullPath;

    /// <summary>Loads the splat shaders this process can use, on the caller's (main) thread.</summary>
    public static void WarmShaders()
    {
        _ = Refii.Models.Engine_ShaderSpecialisation.LoadFile(ShaderPath);
        WarmLiteShaders();
    }

    public delegate byte[]? ReadAsset(string virtualPath);

    public delegate string? FindByName(string fileName);

    /// <summary>Creates the terrain's shared material and records support from the exact uploaded R8 weights.</summary>
    /// <param name="ehf">The privately supplied landscape payload.</param><param name="read">Its asset reader.</param>
    /// <param name="findByName">Optional filename fallback.</param><param name="material">The complete shared material.</param>
    /// <param name="error">A diagnostic when construction fails.</param><returns>Whether all required arrays were built.</returns>
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

        var shader = Refii.Models.Engine_ShaderSpecialisation.LoadFile(ShaderPath);
        if (shader is null)
        {
            error = $"terrain shader missing at {ShaderPath}";
            return false;
        }

        long parseMs = clock.ElapsedMilliseconds;
        clock.Restart();

        bool normalMaps = NormalMapsWanted();
        List<SplatMaterial> materials = ResolveMaterials(parsed, read, findByName, normalMaps, out int resolved);
        if (resolved == 0)
        {
            error = $"none of the {parsed.Lods.Count} palette textures resolved";
            return false;
        }

        long resolveMs = clock.ElapsedMilliseconds;
        clock.Restart();

        if (!Engine_EhfSplat.TryBuild(parsed, materials, out TerrainSplatData data, out error, normalMaps))
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
        if (BuildLayerMask(data) is { } mask)
        {
            material.SetShaderParameter("material_mask", mask);
        }

        // The layers' normal maps, which the .ehf has always named and nothing has ever loaded.
        // A missing pair leaves the shader's flat default, which costs a sample and nothing else.
        Texture2DArray? baseNormalArray = !normalMaps ? null :
            BuildColourArray(data.BaseNormalSlices, data.SliceSize, isNormalMap: true);
        Texture2DArray? detailNormalArray = !normalMaps ? null :
            BuildColourArray(data.DetailNormalSlices, data.SliceSize, isNormalMap: true);
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

        // g_CliffBlendOffsetAndScale, the level's own: the two header floats after the spacing
        // (res+84, res+88; Engine_ClassicTerrain reads the same pair). The landscape pixel shader
        // takes ground = saturate(|n.up| x scale + offset) (blob 0197 1285) — a per-level slope,
        // not one hard-coded band.
        if (ehf.Length >= 55)
        {
            float cliffOffset = BitConverter.Int32BitsToSingle((ehf[47] << 24) | (ehf[48] << 16) | (ehf[49] << 8) | ehf[50]);
            float cliffScale = BitConverter.Int32BitsToSingle((ehf[51] << 24) | (ehf[52] << 16) | (ehf[53] << 8) | ehf[54]);
            if (float.IsFinite(cliffOffset) && float.IsFinite(cliffScale) && cliffScale != 0f)
            {
                material.SetShaderParameter("cliff_blend", new Vector2(cliffOffset, cliffScale));
            }
        }

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
        RecordWeightSupport(material, data);

        ApplyLite(material, data, parameters,
            baseNormalArray is not null && detailNormalArray is not null && normalCount != 0);

        GD.Print(
            $"[terrain] splat: {data.ChunkWidth}x{data.ChunkHeight} chunks, " +
            $"{data.MaterialCount} materials ({resolved} textured, {normalCount} normal-mapped), " +
            $"weights {data.WeightWidth}x{data.WeightHeight} " +
            $"({data.Contributions} layer samples, {data.PaintedTexels} painted, " +
            $"{data.FallbackTexels} fallback)");

        return true;
    }

    /// <param name="normalMaps">Off, the layers' normal maps are not read at all: nothing would upload them.</param>
    internal static List<SplatMaterial> ResolveMaterials(
        EhfParsedBody parsed, ReadAsset read, FindByName? findByName, bool normalMaps, out int resolved)
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
                    if (!normalMaps)
                    {
                        continue;
                    }
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
                LoadTexture(normalMaps ? lod.BaseNormal : string.Empty, read, findByName, cache, true);
            (byte[] detailNormal, int detailNormalWidth, int detailNormalHeight) =
                LoadTexture(normalMaps ? lod.DetailNormal : string.Empty, read, findByName, cache, true);

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

    /// <summary>The decoded splat sources held and their bytes, for the heap census.</summary>
    internal static (long Entries, long Bytes) SharedCensus()
    {
        lock (Shared)
        {
            return (Shared.Count, _sharedBytes);
        }
    }

    /// <summary>
    /// Drops the decoded splat sources. They are raw RGBA byte arrays of the ENHANCED result —
    /// 16 MB for a 2048 source — and the budget above only sheds them when a later region
    /// overflows it, so a region left standing holds its own for the rest of the session.
    /// </summary>
    public static void ClearSharedCache()
    {
        lock (Shared)
        {
            Shared.Clear();
            _sharedBytes = 0;
        }
    }

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

    /// <summary>Copies an owned decoded/enhanced image into splat bytes, then releases its native pixels.</summary>
    private static (byte[] Rgba, int Width, int Height) DecodeTexture(
        string normalized, ReadAsset read, FindByName? findByName, bool normalMap)
    {
        using var temporary = new Engine_TextureTemporaries();
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
            Image? image = temporary.Own(Engine_TexImage.TryCreateImage(bytes, out bool hasAlpha, out string why));
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
                Image? enhanced = temporary.Own(Engine_TexLibrary.EnhanceDecoded(
                    bytes, image,
                    normalMap ? TexSemantic.Normal : TexSemantic.Albedo,
                    hasAlpha, normalized));
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

    /// <summary>
    /// One layer array, block compressed.
    /// </summary>
    /// <remarks>
    /// THESE WERE THE ONLY TEXTURES IN THE ENGINE THAT SKIPPED COMPRESSION. Four arrays reach
    /// here — base colour, detail colour, base normal, detail normal — at up to 32 materials of
    /// 1024 square with a full mip chain, which is 5.33 MB a slice as RGBA8 and roughly 680 MB a
    /// region across the four. Compressed it is an eighth of that for the colours and a quarter
    /// for the normals.
    ///
    /// ONE FORMAT FOR THE WHOLE ARRAY, decided by scanning every slice first:
    /// `CreateFromImages` rejects a mixed set, so a per-slice choice would fail the moment one
    /// layer carried alpha and another did not. Normals are BC5 by construction and so are
    /// uniform already.
    /// </remarks>
    private static Texture2DArray? BuildColourArray(
        byte[][] slices, int size, bool isNormalMap = false)
    {
        using var temporary = new Engine_TextureTemporaries();
        if (slices.Length == 0)
        {
            return null;
        }

        // Layers that name one texture hold one array (ResolveMaterials), so they take one scan, one
        // key and one cache entry, and share the image: 16 of Bower Lake's 40 colour slices.
        var owners = new Dictionary<byte[], int>(slices.Length);
        var owner = new int[slices.Length];
        for (int i = 0; i < slices.Length; i++)
        {
            owner[i] = owners.TryAdd(slices[i], i) ? i : owners[slices[i]];
        }

        // The format first, from each slice's base level: any layer with alpha takes the whole
        // array to BC3. Godot's own DetectAlpha on the base answers exactly what it did on the
        // mipped image — a mip is an average, so an all-opaque level stays all-opaque — and it
        // has to be known before a slice can be looked up, because the format is in its key.
        Refii.Textures.TexSurfaceFormat target;
        if (isNormalMap)
        {
            target = Refii.Textures.TexSurfaceFormat.Bc5;
        }
        else
        {
            int alphaSlices = 0;
            System.Threading.Tasks.Parallel.For(0, slices.Length, i =>
            {
                if (owner[i] == i && DetectsAlpha(slices[i]))
                {
                    System.Threading.Interlocked.Increment(ref alphaSlices);
                }
            });

            target = alphaSlices > 0
                ? Refii.Textures.TexSurfaceFormat.Bc3
                : Refii.Textures.TexSurfaceFormat.Bc1;
        }

        // Cached per slice, by content: a slice's compressed mip chain depends only on its bytes,
        // its size and the target format. A hit is one inflate; a miss builds and stores.
        string recipe = $"terrain-slice|{size}|{target}|{(isNormalMap ? "n" : "c")}";
        Image.Format expected = Refii.Textures.Engine_TexImage.ToGodotFormat(target);
        var built = new Image[slices.Length];
        bool compressed = true;
        System.Threading.Tasks.Parallel.For(0, slices.Length, i =>
        {
            if (owner[i] != i)
            {
                return;
            }

            string key = Refii.Textures.Engine_TexCache.DerivedKey(recipe, slices[i]);
            if (temporary.Own(Refii.Textures.Engine_TexCache.TryLoad(key, out _)) is { } hit &&
                hit.GetFormat() == expected && hit.HasMipmaps() &&
                hit.GetWidth() == size && hit.GetHeight() == size)
            {
                built[i] = hit;
                return;
            }

            Image image = temporary.Own(Image.CreateFromData(size, size, false, Image.Format.Rgba8, slices[i]))!;
            image.GenerateMipmaps();
            built[i] = image;

            // A failure leaves that slice RGBA8, which would fail the array build, so the whole
            // array falls back to uncompressed rather than shipping a mixed set.
            if (!Refii.Textures.Engine_TexImage.CompressBlocks(image, isNormalMap, target))
            {
                compressed = false;
                return;
            }

            Refii.Textures.Engine_TexCache.Store(
                key, image, target == Refii.Textures.TexSurfaceFormat.Bc3,
                Refii.Textures.TexCacheCost.Derived);
        });

        if (!compressed)
        {
            for (int i = 0; i < built.Length; i++)
            {
                built[i]?.Dispose();
                built[i] = null!;
            }

            System.Threading.Tasks.Parallel.For(0, slices.Length, i =>
            {
                if (owner[i] != i)
                {
                    return;
                }

                Image image = temporary.Own(Image.CreateFromData(
                    size, size, false, Image.Format.Rgba8, slices[i]))!;
                image.GenerateMipmaps();
                built[i] = image;
            });
        }

        var images = new Godot.Collections.Array<Image>();
        using Godot.Collections.Array imageStorage = (Godot.Collections.Array)images;
        for (int i = 0; i < built.Length; i++)
        {
            Image image = built[owner[i]];
            if (image is null)
            {
                return null;
            }

            images.Add(image);
        }

        var array = new Texture2DArray();
        return Refii.Textures.Engine_TextureUpload.CreateLayered(array, images, world: true) == Error.Ok ? array : null;
    }

    /// <summary>
    /// Whether <see cref="Image.DetectAlpha"/> on these RGBA8 pixels would answer anything but None:
    /// some alpha under 254 (Godot's DETECT_ALPHA_MAX_THRESHOLD), without copying them into an Image.
    /// </summary>
    /// <param name="rgba">The pixels, four bytes each, alpha last.</param>
    internal static bool DetectsAlpha(ReadOnlySpan<byte> rgba)
    {
        // A little-endian pixel word's top byte is its alpha, so the word alone answers.
        const uint Opaque = 0xFE000000u;
        ReadOnlySpan<uint> pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(rgba);
        int at = 0;
        var limit = System.Runtime.Intrinsics.Vector128.Create(Opaque);
        for (; at + 4 <= pixels.Length; at += 4)
        {
            if (System.Runtime.Intrinsics.Vector128.LessThanAny(System.Runtime.Intrinsics.Vector128.Create(pixels.Slice(at, 4)), limit))
            {
                return true;
            }
        }

        for (; at < pixels.Length; at++)
        {
            if (pixels[at] < Opaque)
            {
                return true;
            }
        }

        return false;
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
        using var temporary = new Engine_TextureTemporaries();
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
        // Cached per layer, by both normal slices' content: the cavity filters and the 24-pass
        // Poisson height solve are the most expensive part of a terrain build, and deterministic.
        // The entry's one flag carries "a height was solved", which is what `hasHeight` reports.
        // Encoded as BC1 (4 bpp) rather than uncompressed RGB8 (which Vulkan/D3D12 expand to 32 bpp
        // RGBA8), reducing terrain cavity VRAM and upload staging by 8x.
        string recipe = $"terrain-cavity-bc1-v2|{size}|{strength.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}|{(solveHeight ? "h" : "-")}";
        bool compressed = true;
        System.Threading.Tasks.Parallel.For(0, baseSlices.Length, i =>
        {
            string key = Refii.Textures.Engine_TexCache.DerivedKey(recipe, baseSlices[i], detailSlices[i]);
            if (temporary.Own(Refii.Textures.Engine_TexCache.TryLoad(key, out bool solved)) is { } hit &&
                hit.GetFormat() == Image.Format.Dxt1 && hit.HasMipmaps() &&
                hit.GetWidth() == size && hit.GetHeight() == size)
            {
                anyHeight[i] = solved;
                built[i] = hit;
                return;
            }

            byte[] baseCavity = Engine_TexOrm.CavityFromTilingNormal(
                baseSlices[i], size, size, strength);
            byte[] detailCavity = Engine_TexOrm.CavityFromTilingNormal(
                detailSlices[i], size, size, strength);

            byte[]? height = null;
            if (solveHeight)
            {
                using Image normal = Image.CreateFromData(
                    size, size, false, Image.Format.Rgba8, baseSlices[i]);
                height = Engine_TexHeight.TrySolve(normal, size, size);
                anyHeight[i] = height is not null;
            }

            var rgba = new byte[size * size * 4];
            for (int t = 0; t < baseCavity.Length; t++)
            {
                rgba[t * 4] = baseCavity[t];
                rgba[(t * 4) + 1] = detailCavity[t];
                rgba[(t * 4) + 2] = height is null ? (byte)128 : height[t];
                rgba[(t * 4) + 3] = 255;
            }

            Image image = temporary.Own(Image.CreateFromData(size, size, false, Image.Format.Rgba8, rgba))!;
            image.GenerateMipmaps();
            built[i] = image;
            if (!Refii.Textures.Engine_TexImage.CompressBlocks(
                    image, false, Refii.Textures.TexSurfaceFormat.Bc1))
            {
                compressed = false;
                return;
            }

            Refii.Textures.Engine_TexCache.Store(key, image, anyHeight[i], Refii.Textures.TexCacheCost.Derived);
        });

        if (!compressed)
        {
            for (int i = 0; i < built.Length; i++)
            {
                if (built[i] is { } slice && slice.IsCompressed())
                {
                    slice.Decompress();
                }
            }
        }

        var images = new Godot.Collections.Array<Image>();
        using Godot.Collections.Array imageStorage = (Godot.Collections.Array)images;
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
        return Refii.Textures.Engine_TextureUpload.CreateLayered(array, images, world: true) == Error.Ok ? array : null;
    }

    /// <summary>Uploads owned R8 weight images and releases their CPU wrappers and native array afterwards.</summary>
    private static Texture2DArray? BuildWeightArray(TerrainSplatData data)
    {
        using var temporary = new Engine_TextureTemporaries();
        long area = (long)data.WeightWidth * data.WeightHeight;
        if (area == 0 || data.MaterialCount == 0)
        {
            return null;
        }

        int planes = Math.Min(data.MaterialCount, Engine_EhfSplat.MaxMaterials);
        var images = new Godot.Collections.Array<Image>();
        using Godot.Collections.Array imageStorage = (Godot.Collections.Array)images;

        // EIGHT BITS A CHANNEL, NOT THIRTY-TWO: every value is in [0,1], and a byte carries a
        // blend weight with no visible difference.
        // R the weight, G the share of it from layers that state the cliff.
        var packed = new byte[planes][];
        System.Threading.Tasks.Parallel.For(0, planes, plane =>
        {
            packed[plane] = Engine_TerrainWeightSupport.PackLayer(data.Weights, data.CliffShares, plane * area, checked((int)area));
        });

        foreach (byte[] bytes in packed)
        {
            images.Add(temporary.Own(Image.CreateFromData(
                data.WeightWidth, data.WeightHeight, false, Image.Format.Rg8, bytes))!);
        }

        var array = new Texture2DArray();
        return Refii.Textures.Engine_TextureUpload.CreateLayered(array, images, world: true) == Error.Ok ? array : null;
    }

    /// <summary>
    /// Bit m of a texel = material m's weight byte is non-zero somewhere in the 3x3 texels around
    /// it, as RGBA8 (bits 0-7 in R). The shader's bilinear weight fetch reads at most the 2x2
    /// around its nearest texel, so a layer outside the mask fetches exactly zero there and the
    /// loop skips it without the fetch.
    /// </summary>
    private static ImageTexture? BuildLayerMask(TerrainSplatData data)
    {
        int width = data.WeightWidth;
        int height = data.WeightHeight;
        long area = (long)width * height;
        if (area == 0)
        {
            return null;
        }

        int planes = Math.Min(data.MaterialCount, Engine_EhfSplat.MaxMaterials);
        var own = new uint[area];
        System.Threading.Tasks.Parallel.For(0, height, y =>
        {
            long first = (long)y * width;
            for (int plane = 0; plane < planes; plane++)
            {
                long baseIndex = plane * area;
                for (long t = first; t < first + width; t++)
                {
                    // The same test as the weight byte: anything that rounds to a non-zero byte.
                    if (data.Weights[baseIndex + t] * 255f + 0.5f >= 1f)
                    {
                        own[t] |= 1u << plane;
                    }
                }
            }
        });

        var bytes = new byte[area * 4];
        long layers = 0;
        int widest = 0;
        var tally = new object();
        System.Threading.Tasks.Parallel.For(0, height, y =>
        {
            long rowLayers = 0;
            int rowWidest = 0;
            for (int x = 0; x < width; x++)
            {
                uint bits = 0;
                for (int dy = Math.Max(y - 1, 0); dy <= Math.Min(y + 1, height - 1); dy++)
                {
                    for (int dx = Math.Max(x - 1, 0); dx <= Math.Min(x + 1, width - 1); dx++)
                    {
                        bits |= own[((long)dy * width) + dx];
                    }
                }

                long t = ((long)y * width) + x;
                bytes[t * 4] = (byte)bits;
                bytes[(t * 4) + 1] = (byte)(bits >> 8);
                bytes[(t * 4) + 2] = (byte)(bits >> 16);
                bytes[(t * 4) + 3] = (byte)(bits >> 24);
                int count = System.Numerics.BitOperations.PopCount(bits);
                rowLayers += count;
                rowWidest = Math.Max(rowWidest, count);
            }

            lock (tally)
            {
                layers += rowLayers;
                widest = Math.Max(widest, rowWidest);
            }
        });

        GD.Print($"[terrain] layer mask: {layers / (double)area:0.00} layers a texel on average, at most {widest} of {planes}");
        using Image maskImage = Image.CreateFromData(width, height, false, Image.Format.Rgba8, bytes);
        return Refii.Textures.Engine_TextureUpload.CreateTexture(maskImage);
    }
}
