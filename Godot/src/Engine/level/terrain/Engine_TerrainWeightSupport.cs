using System;
using System.Runtime.CompilerServices;

namespace Refii.Level;

/// <summary>Immutable quantized LOD0 support, independent of GPU resources and source weight lifetimes.</summary>
/// <remarks>Each bit means an uploaded R8 texel is nonzero. The terrain weight sampler has no
/// mipmaps and explicitly samples LOD0 with bilinear filtering. Bounds include that footprint
/// plus one texel of conservative boundary/rounding allowance; they may keep extra work but
/// never discard a possibly contributing layer. Local terrain coordinates precede the region's
/// mirrored world transform, exactly as the shader's v_local/identity mesh_xform do.</remarks>
internal sealed class Engine_TerrainWeightSupport
{
    private readonly ulong[] _presence;
    private readonly int _width, _height, _planes, _wordsPerRow, _chunksX, _chunksZ;
    private readonly float _originX, _originZ, _extentX, _extentZ;

    /// <summary>Owns only a packed presence copy; it never retains or disposes weight arrays or textures.</summary>
    private Engine_TerrainWeightSupport(TerrainSplatData data, ulong[] presence, int wordsPerRow)
    {
        _presence = presence; _wordsPerRow = wordsPerRow;
        _width = data.WeightWidth; _height = data.WeightHeight; _planes = data.MaterialCount;
        _chunksX = data.ChunkWidth; _chunksZ = data.ChunkHeight;
        _originX = data.WorldOriginX; _originZ = data.WorldOriginZ;
        _extentX = data.ChunkExtentX; _extentZ = data.ChunkExtentZ;
    }

    /// <summary>Bytes retained for this immutable support, excluding transient source arrays.</summary>
    internal long RetainedBytes => (long)_presence.Length * sizeof(ulong);

    /// <summary>Creates a detached support snapshot or declines malformed/unknown contracts for full-loop fallback.</summary>
    /// <param name="data">The exact scalar metadata and normalized weights used for upload.</param>
    /// <returns>A packed snapshot, or null when the complete support cannot safely be established.</returns>
    internal static Engine_TerrainWeightSupport? TryCreate(TerrainSplatData data)
    {
        if (data is null || data.MaterialCount is < 1 or > 32 || data.ChunkWidth <= 0 || data.ChunkHeight <= 0 ||
            data.WeightWidth != (long)data.ChunkWidth * 32 + 1 || data.WeightHeight != (long)data.ChunkHeight * 32 + 1 ||
            !float.IsFinite(data.WorldOriginX) || !float.IsFinite(data.WorldOriginZ) ||
            !float.IsFinite(data.ChunkExtentX) || data.ChunkExtentX <= 0 ||
            !float.IsFinite(data.ChunkExtentZ) || data.ChunkExtentZ <= 0 ||
            data.Weights is null || data.Weights.LongLength != (long)data.WeightWidth * data.WeightHeight * data.MaterialCount)
            return null;
        int rowWords = checked((data.WeightWidth + 63) / 64);
        long required = (long)rowWords * data.WeightHeight * data.MaterialCount;
        if (required > int.MaxValue) return null;
        var presence = new ulong[(int)required];
        int area = checked(data.WeightWidth * data.WeightHeight);
        for (int plane = 0; plane < data.MaterialCount; plane++)
        for (int y = 0; y < data.WeightHeight; y++)
        for (int x = 0; x < data.WeightWidth; x++)
        {
            float weight = data.Weights[plane * area + y * data.WeightWidth + x];
            if (!float.IsFinite(weight)) return null;
            if (Quantize(weight) != 0)
                presence[(plane * data.WeightHeight + y) * rowWords + x / 64] |= 1UL << (x & 63);
        }
        return new Engine_TerrainWeightSupport(data, presence, rowWords);
    }

    /// <summary>Matches the uploaded R8 conversion, including low weights that round to exact zero.</summary>
    /// <param name="weight">An upload weight; creation separately rejects nonfinite source data.</param>
    /// <returns>The exact normalized-R8 byte used by the terrain sampler.</returns>
    internal static byte Quantize(float weight) => weight <= 0 ? (byte)0 : weight >= 1 ? (byte)255 : (byte)(weight * 255f + .5f);

    /// <summary>Packs a terrain plane's ground weight and authored cliff share into the uploaded RG8 channels.</summary>
    /// <param name="weights">Normalized weights across all material planes.</param>
    /// <param name="cliffShares">Authored cliff shares; missing legacy entries keep the full cliff share.</param>
    /// <param name="offset">First source texel of this plane.</param>
    /// <param name="area">Number of texels in the plane.</param>
    /// <returns>Owned interleaved R/G bytes using the same weight quantization as the support mask.</returns>
    internal static byte[] PackLayer(float[] weights, float[] cliffShares, long offset, int area)
    {
        if (offset < 0 || area < 0 || offset > weights.LongLength - area)
            throw new ArgumentOutOfRangeException(nameof(offset));
        var bytes = new byte[checked(area * 2)];
        for (int t = 0; t < area; t++)
        {
            bytes[t * 2] = Quantize(weights[offset + t]);
            bytes[t * 2 + 1] = cliffShares.LongLength > offset + t ? Quantize(cliffShares[offset + t]) : (byte)255;
        }
        return bytes;
    }

    /// <summary>Finds possibly contributing layers for a local-space draw rectangle; malformed bounds keep all 32 bits.</summary>
    /// <param name="minX">Minimum mesh-local x.</param><param name="minZ">Minimum mesh-local z.</param>
    /// <param name="maxX">Maximum mesh-local x.</param><param name="maxZ">Maximum mesh-local z.</param>
    /// <returns>A signed bit-pattern preserving material31; -1 is the safe full-loop default.</returns>
    internal int Mask(float minX, float minZ, float maxX, float maxZ)
    {
        if (!float.IsFinite(minX) || !float.IsFinite(minZ) || !float.IsFinite(maxX) || !float.IsFinite(maxZ) ||
            minX > maxX || minZ > maxZ) return -1;
        var x = Support(minX, maxX, _originX, _extentX, _chunksX, _width);
        var z = Support(minZ, maxZ, _originZ, _extentZ, _chunksZ, _height);
        uint result = 0;
        for (int plane = 0; plane < _planes; plane++)
            if (Contains(plane, x.Min, x.Max, z.Min, z.Max)) result |= 1U << plane;
        return unchecked((int)result);
    }

    /// <summary>Converts shader-clamped world coordinates to an inclusive conservative bilinear rectangle.</summary>
    private static (int Min, int Max) Support(float min, float max, float origin, float extent, int chunks, int size)
    {
        double Texel(float value) => Math.Clamp(Math.Clamp(((double)value - origin) / extent, 0, chunks - .001) * 32, 0, size - 1);
        return (Math.Clamp((int)Math.Floor(Texel(min)) - 1, 0, size - 1),
            Math.Clamp((int)Math.Ceiling(Texel(max)) + 1, 0, size - 1));
    }

    /// <summary>Tests an inclusive row range without mistaking adjacent words or planes for contributing texels.</summary>
    private bool Contains(int plane, int minX, int maxX, int minZ, int maxZ)
    {
        int first = minX / 64, last = maxX / 64;
        ulong firstMask = ulong.MaxValue << (minX & 63);
        ulong lastMask = ulong.MaxValue >> (63 - (maxX & 63));
        for (int y = minZ; y <= maxZ; y++)
        {
            int row = (plane * _height + y) * _wordsPerRow;
            for (int word = first; word <= last; word++)
            {
                ulong allowed = (word == first ? firstMask : ulong.MaxValue) & (word == last ? lastMask : ulong.MaxValue);
                if ((_presence[row + word] & allowed) != 0) return true;
            }
        }
        return false;
    }
}

/// <summary>Associates immutable support with a live material identity without retaining that owner or its textures.</summary>
/// <typeparam name="TOwner">The reference identity; pure tests use ordinary objects in place of native resources.</typeparam>
internal sealed class Engine_TerrainWeightRegistry<TOwner> where TOwner : class
{
    private readonly ConditionalWeakTable<TOwner, Engine_TerrainWeightSupport> _supports = new();

    /// <summary>Publishes regenerated support; missing/invalid data retires an earlier optimization safely.</summary>
    /// <param name="owner">A newly built or regenerated material identity.</param>
    /// <param name="support">Its detached immutable support, or null to require the full loop.</param>
    internal void Record(TOwner owner, Engine_TerrainWeightSupport? support)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _supports.Remove(owner);
        if (support is not null) _supports.Add(owner, support);
    }

    /// <summary>Queries the captured support for a mesh-local rectangle without acquiring or disposing GPU resources.</summary>
    /// <param name="owner">The exact shared material identity; unknown identities retain the full loop.</param>
    /// <param name="minX">Minimum mesh-local x.</param><param name="minZ">Minimum mesh-local z.</param>
    /// <param name="maxX">Maximum mesh-local x.</param><param name="maxZ">Maximum mesh-local z.</param>
    /// <returns>The signed active-layer mask, or -1 when support or finite bounds are unavailable.</returns>
    internal int Mask(TOwner? owner, float minX, float minZ, float maxX, float maxZ) =>
        owner is not null && _supports.TryGetValue(owner, out Engine_TerrainWeightSupport? support)
            ? support.Mask(minX, minZ, maxX, maxZ) : -1;
}
