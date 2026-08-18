using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;
using Refii.Level;
using Refii.Textures;

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

    private sealed class DecodedTexture
    {
        public int Width { get; set; }

        public int Height { get; set; }

        public bool HasAlpha { get; set; }

        public bool Ok { get; set; }

        public byte[] Rgba { get; set; } = Array.Empty<byte>();
    }

    private static readonly object MorphLock = new();

    private static readonly Dictionary<string, ImageTexture> Composited =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, DecodedTexture> DecodedCache =
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
            DecodedCache.Clear();
        }
    }

    public static void Invalidate()
    {
        lock (MorphLock)
        {
            _pairs = null;
            Composited.Clear();
            DecodedCache.Clear();
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
        GD.Print($"[morph] {built.Count} texture morph pair(s), " +
            $"{Engine_TexPathIndex.Count} texture(s) indexed");

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

        uint hash = Engine_TexPathIndex.PathHash(textureName);
        if (!active.TryGetValue(hash, out List<TextureMorphEntry>? entries))
        {
            return null;
        }

        string baseName = textureName;
        var overlays = new List<(string Path, int Quantized)>();

        foreach (TextureMorphEntry entry in entries)
        {
            string? target = Engine_TexPathIndex.Resolve(entry.MorphHash);
            if (target is null)
            {
                continue;
            }

            DecodedTexture decoded = Decode(target);
            int quantized = Quantize(entry.Weight);

            if (!decoded.Ok)
            {
                if (entry.Weight >= ReplaceThreshold)
                {
                    baseName = target;
                    overlays.Clear();
                }
            }
            else if (!decoded.HasAlpha && quantized >= 4)
            {
                baseName = target;
                overlays.Clear();
            }
            else if (quantized >= 1)
            {
                overlays.Add((target, quantized));
            }
        }

        if (overlays.Count == 0 &&
            baseName.Equals(textureName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var key = new System.Text.StringBuilder();
        key.Append(Engine_TexPathIndex.PathHash(baseName).ToString("x8"));
        foreach ((string path, int quantized) in overlays)
        {
            key.Append('_')
                .Append(Engine_TexPathIndex.PathHash(path).ToString("x8"))
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

        DecodedTexture source = Decode(baseName);
        if (!source.Ok)
        {
            return null;
        }

        int width = source.Width;
        int height = source.Height;
        byte[] output = (byte[])source.Rgba.Clone();

        foreach ((string path, int quantized) in overlays)
        {
            DecodedTexture over = Decode(path);
            if (!over.Ok)
            {
                continue;
            }

            float amount = quantized * 0.25f;

            for (int y = 0; y < height; y++)
            {
                int sy = over.Height == height
                    ? y
                    : Math.Min(over.Height - 1, y * over.Height / height);

                for (int x = 0; x < width; x++)
                {
                    int sx = over.Width == width
                        ? x
                        : Math.Min(over.Width - 1, x * over.Width / width);

                    int src = ((sy * over.Width) + sx) * 4;
                    float alpha = (over.Rgba[src + 3] / 255.0f) * amount;
                    if (alpha <= 0.0f)
                    {
                        continue;
                    }

                    int dst = ((y * width) + x) * 4;
                    for (int k = 0; k < 3; k++)
                    {
                        output[dst + k] = (byte)(
                            (output[dst + k] * (1.0f - alpha)) +
                            (over.Rgba[src + k] * alpha) + 0.5f);
                    }
                }
            }
        }

        ImageTexture texture = ImageTexture.CreateFromImage(
            Image.CreateFromData(width, height, false, Image.Format.Rgba8, output));

        lock (MorphLock)
        {
            Composited[cacheKey] = texture;
        }

        GD.Print(overlays.Count != 0
            ? $"[morph] composited {overlays.Count} overlay(s) onto {baseName}"
            : $"[morph] swapped {textureName} for {baseName}");

        return texture;
    }

    private static DecodedTexture Decode(string path)
    {
        string key = path.Replace('\\', '/');

        lock (MorphLock)
        {
            if (DecodedCache.TryGetValue(key, out DecodedTexture? cached))
            {
                return cached;
            }
        }

        var value = new DecodedTexture();
        byte[]? bytes = Refii.Models.Engine_BnkMeshLoader.ReadBytes(path);

        if (bytes is not null)
        {
            try
            {
                Image? image = Engine_TexImage.TryCreateImage(bytes, out bool hasAlpha, out _);

                if (image is not null)
                {
                    value.HasAlpha = hasAlpha;
                    Fill(value, image);
                }
            }
            catch (Exception)
            {
                value.Ok = false;
            }
        }

        lock (MorphLock)
        {
            DecodedCache[key] = value;
        }

        return value;
    }

    private static void Fill(DecodedTexture value, Image image)
    {
        if (image.IsCompressed())
        {
            image.Decompress();
        }

        if (image.GetFormat() != Image.Format.Rgba8)
        {
            image.Convert(Image.Format.Rgba8);
        }

        int width = image.GetWidth();
        int height = image.GetHeight();
        if (width <= 0 || height <= 0)
        {
            return;
        }

        byte[] data = image.GetData();
        int needed = width * height * 4;
        if (data.Length < needed)
        {
            return;
        }

        value.Width = width;
        value.Height = height;
        value.Rgba = data.Length == needed ? data : data[..needed];
        value.Ok = true;
    }
}
