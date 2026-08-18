using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;
using Refii.Level;

namespace Refii.Hero;

public sealed class TextureMorphEntry
{
    public uint MorphHash { get; init; }

    public uint Layer { get; init; }

    public float Weight { get; init; }
}

public static class Engine_HeroTextureMorph
{
    public const float ReplaceThreshold = 0.5f;

    public const float OverlayMinWeight = 0.05f;

    private static readonly object MorphLock = new();

    private static readonly Dictionary<string, ImageTexture> Composited =
        new(StringComparer.OrdinalIgnoreCase);

    private static Func<string, byte[]?>? _resolver;

    private static List<TextureMorphPair>? _pairs;

    public static void SetResolver(Func<string, byte[]?>? resolver)
    {
        lock (MorphLock)
        {
            _resolver = resolver;
            _pairs = null;
            Composited.Clear();
        }
    }

    public static void Invalidate()
    {
        lock (MorphLock)
        {
            _pairs = null;
            Composited.Clear();
        }
    }

    public static List<TextureMorphPair> Pairs()
    {
        lock (MorphLock)
        {
            if (_pairs is not null)
            {
                return _pairs;
            }

            if (_resolver is null)
            {
                _pairs = new List<TextureMorphPair>();
                return _pairs;
            }
        }

        var blobs = new List<byte[]?>();
        foreach (string path in Engine_LevelProps.SupplementalGdbPaths)
        {
            blobs.Add(_resolver(path) ?? _resolver("data/" + path));
        }

        List<TextureMorphPair> built = Engine_GdbTextureMorphs.Build(GdbMulti.Build(blobs));
        GD.Print($"[morph] {built.Count} texture morph pair(s)");

        lock (MorphLock)
        {
            _pairs = built;
        }

        return built;
    }

    public static Dictionary<uint, List<TextureMorphEntry>> Active(HeroMorphWeights weights)
    {
        var active = new Dictionary<uint, List<TextureMorphEntry>>();

        foreach (TextureMorphPair pair in Pairs())
        {
            float weight = Engine_HeroMorphState.TextureWeight(weights, pair.MorphType);
            if (weight < OverlayMinWeight)
            {
                continue;
            }

            if (!active.TryGetValue(pair.OriginalTextureHash, out List<TextureMorphEntry>? list))
            {
                list = new List<TextureMorphEntry>();
                active[pair.OriginalTextureHash] = list;
            }

            list.Add(new TextureMorphEntry
            {
                MorphHash = pair.MorphTextureHash,
                Layer = pair.Layer,
                Weight = weight,
            });
        }

        foreach (List<TextureMorphEntry> list in active.Values)
        {
            list.Sort((a, b) =>
                a.Layer != b.Layer
                    ? a.Layer.CompareTo(b.Layer)
                    : a.Weight.CompareTo(b.Weight));
        }

        return active;
    }

    public static int Quantize(float weight)
    {
        int step = (int)MathF.Floor((weight * 4.0f) + 0.5f);
        return Math.Min(4, Math.Max(0, step));
    }

    public static Texture2D? Resolve(
        string textureName, Dictionary<uint, List<TextureMorphEntry>> active)
    {
        if (textureName.Length == 0 || active.Count == 0)
        {
            return null;
        }

        uint hash = Engine_PropModelIndex.PathHash(textureName);
        if (!active.TryGetValue(hash, out List<TextureMorphEntry>? entries))
        {
            return null;
        }

        string baseName = textureName;
        var overlays = new List<(string Path, int Quantized)>();

        foreach (TextureMorphEntry entry in entries)
        {
            string? target = Engine_PropModelIndex.Resolve(
                entry.MorphHash, Array.Empty<string>());

            if (target is null)
            {
                continue;
            }

            Image? decoded = Decode(target);
            int quantized = Quantize(entry.Weight);

            if (decoded is null)
            {
                if (entry.Weight >= ReplaceThreshold)
                {
                    baseName = target;
                    overlays.Clear();
                }

                continue;
            }

            if (!HasAlpha(decoded) && quantized >= 4)
            {
                baseName = target;
                overlays.Clear();
            }
            else if (quantized >= 1)
            {
                overlays.Add((target, quantized));
            }
        }

        if (overlays.Count == 0)
        {
            return baseName.Equals(textureName, StringComparison.OrdinalIgnoreCase)
                ? null
                : Load(baseName);
        }

        var key = new System.Text.StringBuilder();
        key.Append(Engine_PropModelIndex.PathHash(baseName).ToString("x8"));
        foreach ((string path, int quantized) in overlays)
        {
            key.Append('_')
                .Append(Engine_PropModelIndex.PathHash(path).ToString("x8"))
                .Append('q')
                .Append(quantized);
        }

        string cacheKey = key.ToString();

        lock (MorphLock)
        {
            if (Composited.TryGetValue(cacheKey, out ImageTexture? cached))
            {
                return cached;
            }
        }

        Image? composed = Decode(baseName);
        if (composed is null)
        {
            return null;
        }

        composed.Decompress();
        composed.Convert(Image.Format.Rgba8);

        int width = composed.GetWidth();
        int height = composed.GetHeight();

        foreach ((string path, int quantized) in overlays)
        {
            Image? over = Decode(path);
            if (over is null)
            {
                continue;
            }

            over.Decompress();
            over.Convert(Image.Format.Rgba8);

            float amount = quantized * 0.25f;
            int overWidth = over.GetWidth();
            int overHeight = over.GetHeight();

            if (overWidth <= 0 || overHeight <= 0)
            {
                continue;
            }

            for (int y = 0; y < height; y++)
            {
                int sy = overHeight == height
                    ? y
                    : Math.Min(overHeight - 1, y * overHeight / height);

                for (int x = 0; x < width; x++)
                {
                    int sx = overWidth == width
                        ? x
                        : Math.Min(overWidth - 1, x * overWidth / width);

                    Color src = over.GetPixel(sx, sy);
                    float alpha = src.A * amount;
                    if (alpha <= 0.0f)
                    {
                        continue;
                    }

                    Color dst = composed.GetPixel(x, y);
                    composed.SetPixel(x, y, new Color(
                        (dst.R * (1.0f - alpha)) + (src.R * alpha),
                        (dst.G * (1.0f - alpha)) + (src.G * alpha),
                        (dst.B * (1.0f - alpha)) + (src.B * alpha),
                        dst.A));
                }
            }
        }

        ImageTexture texture = ImageTexture.CreateFromImage(composed);

        lock (MorphLock)
        {
            Composited[cacheKey] = texture;
        }

        GD.Print($"[morph] composited {overlays.Count} overlay(s) onto {baseName}");
        return texture;
    }

    private static bool HasAlpha(Image image)
    {
        Image.Format format = image.GetFormat();
        return format is Image.Format.Rgba8 or Image.Format.Rgba4444 or
            Image.Format.Dxt3 or Image.Format.Dxt5 && image.DetectAlpha() !=
            Image.AlphaMode.None;
    }

    private static Image? Decode(string path)
    {
        byte[]? bytes = Refii.Models.Engine_BnkMeshLoader.ReadBytes(path);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return Refii.Textures.Engine_TexImage.TryCreateImage(bytes, out _);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Texture2D? Load(string path)
    {
        Image? image = Decode(path);
        return image is null ? null : ImageTexture.CreateFromImage(image);
    }
}
