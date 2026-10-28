using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Refii.Textures;

/// <summary>Distinguishes initial 2D allocations, replacements and layered uploads in diagnostics.</summary>
public enum TextureUploadKind
{
    /// <summary>A new ordinary 2D texture.</summary>
    Create2D,
    /// <summary>An image replacing an existing texture's storage.</summary>
    Replace2D,
    /// <summary>One face or slice of a successfully uploaded layered texture.</summary>
    Layer,
}

/// <summary>Detached counters for one format conversion; bytes are cumulative upload payload, not live VRAM.</summary>
/// <param name="Kind">The operation represented by the counters.</param>
/// <param name="SourceFormat">The caller's original image format.</param>
/// <param name="UploadedFormat">The capability-compatible image format passed to Godot.</param>
/// <param name="Images">Number of successful image uploads, including replacement operations.</param>
/// <param name="SourceBytes">Sum of original CPU payload sizes, with authored mip chains.</param>
/// <param name="UploadedBytes">Sum of uploaded payload sizes, with mip chains; driver overhead is excluded.</param>
/// <param name="LargestUploadBytes">Largest individual face or 2D upload in this bucket.</param>
/// <param name="LargestEdge">Largest width or height observed.</param>
public sealed record TextureUploadTotals(TextureUploadKind Kind, Image.Format SourceFormat,
    Image.Format UploadedFormat, long Images, long SourceBytes, long UploadedBytes,
    long LargestUploadBytes, int LargestEdge);

/// <summary>Records only scalar metadata, with bounded format buckets and no image/resource references.</summary>
public sealed class Engine_TextureUploadCensus
{
    private readonly object _gate = new();
    private readonly Dictionary<(TextureUploadKind, Image.Format, Image.Format), TextureUploadTotals> _totals = new();

    /// <summary>Counts one successful upload without retaining native images or copying their pixel data.</summary>
    /// <param name="kind">Initial, replacement or layered operation.</param>
    /// <param name="source">Original source format.</param>
    /// <param name="uploaded">Format actually passed to Godot.</param>
    /// <param name="sourceBytes">Original image data size including mipmaps.</param>
    /// <param name="uploadedBytes">Prepared image data size including mipmaps.</param>
    /// <param name="width">Prepared image width.</param>
    /// <param name="height">Prepared image height.</param>
    /// <returns>False for invalid metadata; diagnostics never turn an empty image into a new failure.</returns>
    public bool Record(TextureUploadKind kind, Image.Format source, Image.Format uploaded,
        long sourceBytes, long uploadedBytes, int width, int height)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(source) || !Enum.IsDefined(uploaded) ||
            sourceBytes <= 0 || uploadedBytes <= 0 || width <= 0 || height <= 0) return false;
        lock (_gate)
        {
            var key = (kind, source, uploaded);
            _totals.TryGetValue(key, out TextureUploadTotals? previous);
            _totals[key] = new(kind, source, uploaded, (previous?.Images ?? 0) + 1,
                (previous?.SourceBytes ?? 0) + sourceBytes,
                (previous?.UploadedBytes ?? 0) + uploadedBytes,
                Math.Max(previous?.LargestUploadBytes ?? 0, uploadedBytes),
                Math.Max(previous?.LargestEdge ?? 0, Math.Max(width, height)));
        }
        return true;
    }

    /// <summary>Returns a stable, detached snapshot; subsequent worker uploads cannot mutate it.</summary>
    /// <returns>Format buckets ordered consistently for evidence comparisons.</returns>
    public TextureUploadTotals[] Snapshot()
    {
        lock (_gate) return _totals.Values.OrderBy(x => x.Kind).ThenBy(x => x.SourceFormat)
            .ThenBy(x => x.UploadedFormat).ToArray();
    }
}
