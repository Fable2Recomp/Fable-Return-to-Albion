using System.Collections.Generic;

namespace Refii.Lighting;

/// <summary>
/// A heightfield's baked lightmap, and the lattice the bake was made for.
/// </summary>
/// <remarks>
/// The two counts beside the blob are the heightfield's SAMPLE counts, and they are worth
/// keeping because they are the one check that a bake belongs to the terrain it is about to be
/// drawn on. A bake is one texel per CELL, so the texture is (samples - 1) on each axis —
/// exact for both of chapter2slums' heightfields, 577x577 samples over a 576x576 bake and
/// 33x65 over 32x64. That relation is also why the guest's two landscape UV constants are the
/// same number, which is what lets the terrain sample its bake with the plain normalised
/// heightfield coordinate.
/// </remarks>
public sealed class TerrainLightmap
{
    public uint SamplesX { get; init; }

    public uint SamplesY { get; init; }

    public byte[] Blob { get; init; } = System.Array.Empty<byte>();
}

public sealed class LightmapContainer
{
    public uint Version { get; set; }

    public uint BuiltChangelist { get; set; }

    public Dictionary<ulong, TerrainLightmap> TerrainLightmapBlobs { get; } = new();

    public Dictionary<ulong, byte[]> ObjectLightmapBlobs { get; } = new();

    /// <summary>The region's object lightmaps packed into one texture; built on first use by
    /// <see cref="Engine_LevelBakedLighting.TryGetObjectLightmapRect"/>. The guest binds one
    /// 64x64 texture per object per draw; an atlas plus a per-instance rect is the same data
    /// under Godot's shared-material design, the way Phase 0 streams banks in place.</summary>
    public Godot.ImageTexture? ObjectAtlas { get; set; }

    /// <summary>
    /// Each object lightmap as its own texture, decoded on first use: what the title binds per
    /// drawable (one 64x64 per object, sampled by the mesh's second UV set), for the classic
    /// renderer. The modern path samples the atlas above instead.
    /// </summary>
    public Dictionary<ulong, Godot.ImageTexture?> ObjectTextures { get; } = new();

    public Dictionary<ulong, Godot.Rect2> ObjectAtlasRects { get; } = new();

    public bool ObjectAtlasBuilt { get; set; }

    public Dictionary<ulong, uint[]> VertexBakes { get; } = new();

    public Dictionary<ulong, uint[]> U32Arrays { get; } = new();

    public Dictionary<ulong, byte[]> Blocks48 { get; } = new();

    public int TextureCount => TerrainLightmapBlobs.Count + ObjectLightmapBlobs.Count;
}

public static class Engine_LightmapFile
{
    public const string Magic = "LightmapFile";

    public const uint MinVersion = 10;

    public const uint MaxVersion = 12;

    public static bool TryParse(byte[] bytes, out LightmapContainer container, out string error)
    {
        container = new LightmapContainer();
        error = string.Empty;

        var reader = new BeReader(bytes);

        if (!reader.MatchAscii(Magic))
        {
            error = "magic mismatch (expected \"LightmapFile\")";
            return false;
        }

        if (!reader.U32(out uint version) || version < MinVersion || version > MaxVersion)
        {
            error = $"unsupported version (accepts {MinVersion}..{MaxVersion})";
            return false;
        }

        container.Version = version;
        container.BuiltChangelist = 0;
        if (version >= 12)
        {
            if (!reader.U32(out uint changelist))
            {
                error = "truncated reading built changelist";
                return false;
            }

            container.BuiltChangelist = changelist;
        }

        if (!reader.U32(out uint terrainCount))
        {
            error = "truncated reading terrain-lightmap count";
            return false;
        }

        for (uint i = 0; i < terrainCount; i++)
        {
            if (!reader.U64(out ulong key) ||
                !reader.U32(out uint samplesX) ||
                !reader.U32(out uint samplesY) ||
                !reader.U32(out uint blobSize) ||
                !reader.Bytes(blobSize, out byte[] blob))
            {
                error = $"truncated terrain lightmap {i}";
                return false;
            }

            container.TerrainLightmapBlobs[key] = new TerrainLightmap
            {
                SamplesX = samplesX,
                SamplesY = samplesY,
                Blob = blob,
            };
        }

        if (!reader.U32(out uint objectCount))
        {
            error = "truncated reading object-bake count";
            return false;
        }

        for (uint i = 0; i < objectCount; i++)
        {
            if (!reader.U8(out byte isTexture) || !reader.U64(out ulong key))
            {
                error = $"truncated object bake {i} header";
                return false;
            }

            if (isTexture != 0)
            {
                if (!reader.U32(out uint blobSize) || !reader.Bytes(blobSize, out byte[] blob))
                {
                    error = $"truncated object lightmap {i}";
                    return false;
                }

                container.ObjectLightmapBlobs[key] = blob;
            }
            else
            {
                if (!reader.U32(out uint vertCount))
                {
                    error = $"truncated vertex bake {i} count";
                    return false;
                }

                if (vertCount > 0)
                {
                    if (!reader.U32Array(vertCount, out uint[] colours))
                    {
                        error = $"truncated vertex bake {i} data";
                        return false;
                    }

                    container.VertexBakes[key] = colours;
                }
            }
        }

        if (!reader.U32(out uint arrayCount))
        {
            error = "truncated reading u32-array count";
            return false;
        }

        for (uint i = 0; i < arrayCount; i++)
        {
            if (!reader.U64(out ulong key) ||
                !reader.U32(out uint count) ||
                !reader.U32Array(count, out uint[] values))
            {
                error = $"truncated u32 array {i}";
                return false;
            }

            container.U32Arrays[key] = values;
        }

        if (version >= 11)
        {
            if (!reader.U32(out uint blockCount))
            {
                error = "truncated reading block48 count";
                return false;
            }

            for (uint i = 0; i < blockCount; i++)
            {
                if (!reader.U64(out ulong key) || !reader.Bytes(48, out byte[] block))
                {
                    error = $"truncated block48 {i}";
                    return false;
                }

                container.Blocks48[key] = block;
            }
        }

        return true;
    }
}

internal sealed class BeReader
{
    private readonly byte[] _data;
    private int _at;

    public BeReader(byte[] data) => _data = data;

    public int Position => _at;

    public void Rewind(int count) => _at -= count;

    public bool MatchAscii(string text)
    {
        if (_at + text.Length > _data.Length)
        {
            return false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            if (_data[_at + i] != (byte)text[i])
            {
                return false;
            }
        }

        _at += text.Length;
        return true;
    }

    public bool U8(out byte value)
    {
        value = 0;
        if (_at + 1 > _data.Length)
        {
            return false;
        }

        value = _data[_at];
        _at += 1;
        return true;
    }

    public bool U16(out ushort value)
    {
        value = 0;
        if (_at + 2 > _data.Length)
        {
            return false;
        }

        value = (ushort)((_data[_at] << 8) | _data[_at + 1]);
        _at += 2;
        return true;
    }

    public bool U32(out uint value)
    {
        value = 0;
        if (_at + 4 > _data.Length)
        {
            return false;
        }

        value = ((uint)_data[_at] << 24) | ((uint)_data[_at + 1] << 16) |
                ((uint)_data[_at + 2] << 8) | _data[_at + 3];
        _at += 4;
        return true;
    }

    public bool U64(out ulong value)
    {
        value = 0;
        if (!U32(out uint high) || !U32(out uint low))
        {
            return false;
        }

        value = ((ulong)high << 32) | low;
        return true;
    }

    public bool U32Array(uint count, out uint[] values)
    {
        values = System.Array.Empty<uint>();
        long span = (long)count * 4;
        if (_at + span > _data.Length)
        {
            return false;
        }

        values = new uint[count];
        for (uint i = 0; i < count; i++)
        {
            U32(out values[i]);
        }

        return true;
    }

    public bool Bytes(uint count, out byte[] value)
    {
        value = System.Array.Empty<byte>();
        if (_at + (long)count > _data.Length)
        {
            return false;
        }

        value = new byte[count];
        System.Array.Copy(_data, _at, value, 0, (int)count);
        _at += (int)count;
        return true;
    }
}
