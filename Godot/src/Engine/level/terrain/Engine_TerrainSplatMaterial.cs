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

        List<SplatMaterial> materials = ResolveMaterials(parsed, read, findByName, out int resolved);
        if (resolved == 0)
        {
            error = $"none of the {parsed.Lods.Count} palette textures resolved";
            return false;
        }

        if (!Engine_EhfSplat.TryBuild(parsed, materials, out TerrainSplatData data, out error))
        {
            return false;
        }

        Texture2DArray? baseArray = BuildColourArray(data.BaseSlices, data.SliceSize);
        Texture2DArray? detailArray = BuildColourArray(data.DetailSlices, data.SliceSize);
        Texture2DArray? weightArray = BuildWeightArray(data);

        if (baseArray is null || detailArray is null || weightArray is null)
        {
            error = "could not create the terrain texture arrays";
            return false;
        }

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

        foreach (EhfLodEntry lod in parsed.Lods)
        {
            (byte[] baseRgba, int baseWidth, int baseHeight) =
                LoadTexture(lod.BaseDiffuse, read, findByName, cache);
            (byte[] detailRgba, int detailWidth, int detailHeight) =
                LoadTexture(lod.DetailDiffuse, read, findByName, cache);
            (byte[] baseNormal, int baseNormalWidth, int baseNormalHeight) =
                LoadTexture(lod.BaseNormal, read, findByName, cache);
            (byte[] detailNormal, int detailNormalWidth, int detailNormalHeight) =
                LoadTexture(lod.DetailNormal, read, findByName, cache);

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
        Dictionary<string, (byte[], int, int)> cache)
    {
        if (string.IsNullOrEmpty(path))
        {
            return (Array.Empty<byte>(), 0, 0);
        }

        string normalized = path.Replace('\\', '/').Trim('/');
        if (cache.TryGetValue(normalized, out (byte[] Rgba, int Width, int Height) hit))
        {
            return hit;
        }

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
            Image? image = Engine_TexImage.TryCreateImage(bytes, out string why);
            if (image is null)
            {
                GD.Print($"[terrain] {normalized}: {why}");
            }
            else
            {
                result = ToRgba(image, normalized);
            }
        }

        cache[normalized] = result;
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

        var images = new Godot.Collections.Array<Image>();
        foreach (byte[] slice in slices)
        {
            Image image = Image.CreateFromData(size, size, false, Image.Format.Rgba8, slice);
            image.GenerateMipmaps();
            images.Add(image);
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
