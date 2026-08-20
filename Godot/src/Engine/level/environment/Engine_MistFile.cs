using System;
using System.Collections.Generic;

namespace Refii.Level;

public sealed class MistVolume
{
    public float[] BoxMin { get; } = new float[3];

    public float[] BoxSize { get; } = new float[3];

    public float Density { get; set; }

    public float ScrollU { get; set; }

    public float ScrollV { get; set; }

    public float GenScalar1 { get; set; }

    public float GenScalar2 { get; set; }

    public float PackedX { get; set; }

    public float PackedY { get; set; }

    public float[] Colour { get; } = new float[3];

    public float Field208 { get; set; }

    public float Reciprocal { get; set; }

    public float Falloff { get; set; }

    public bool HasTexture { get; set; }

    public byte[] TextureBlob { get; set; } = Array.Empty<byte>();
}

public sealed class MistScene
{
    public uint Version { get; set; }

    public List<MistVolume> Volumes { get; } = new();
}

public static class Engine_MistFile
{
    private const uint MistVersion = 4;

    private const uint MaxRecordCount = 256;

    private const uint MinRecordSize = 77;

    public static bool TryParse(byte[] bytes, out MistScene scene, out string error)
    {
        scene = new MistScene();
        error = string.Empty;

        if (bytes.Length < 8)
        {
            error = "too small for a .mist header";
            return false;
        }

        var reader = new Reader(bytes);

        if (!reader.U32(out uint version) || version != MistVersion)
        {
            error = $"unsupported .mist version {version} (expected {MistVersion})";
            return false;
        }

        scene.Version = version;

        if (!reader.U32(out uint count) || count > MaxRecordCount)
        {
            error = $"implausible record count {count}";
            return false;
        }

        if (count == 0)
        {
            return true;
        }

        var extents = new (uint Offset, uint Length)[count];
        for (uint i = 0; i < count; i++)
        {
            if (!reader.U32(out uint offset) || !reader.U32(out uint length))
            {
                error = "truncated reading extent table";
                return false;
            }

            if (length < MinRecordSize)
            {
                error = $"record {i} length {length} below minimum {MinRecordSize}";
                return false;
            }

            extents[i] = (offset, length);
        }

        for (uint i = 0; i < count; i++)
        {
            (uint offset, uint length) = extents[i];
            if (offset + length > (uint)bytes.Length)
            {
                error = $"record {i} extent [{offset}..{offset + length}) past end of file";
                return false;
            }

            var rec = new Reader(bytes, (int)offset, (int)length);
            if (!TryParseRecord(rec, out MistVolume volume, out string recordError))
            {
                error = $"record {i}: {recordError}";
                return false;
            }

            scene.Volumes.Add(volume);
        }

        return true;
    }

    private static bool TryParseRecord(Reader r, out MistVolume vol, out string error)
    {
        vol = new MistVolume();
        error = string.Empty;

        if (!r.F32(out vol.BoxMin[0]) || !r.F32(out vol.BoxMin[1]) || !r.F32(out vol.BoxMin[2]))
        {
            error = "truncated reading box_min";
            return false;
        }

        if (!r.F32(out vol.BoxSize[0]) || !r.F32(out vol.BoxSize[1]) || !r.F32(out vol.BoxSize[2]))
        {
            error = "truncated reading box_size";
            return false;
        }

        if (!r.F32(out float density))
        {
            error = "truncated reading density";
            return false;
        }

        vol.Density = density;

        if (!r.F32(out float scrollU) || !r.F32(out float scrollV))
        {
            error = "truncated reading scroll";
            return false;
        }

        vol.ScrollU = scrollU;
        vol.ScrollV = scrollV;

        if (!r.F32(out float genScalar1) || !r.F32(out float genScalar2))
        {
            error = "truncated reading gen_scalar";
            return false;
        }

        vol.GenScalar1 = genScalar1;
        vol.GenScalar2 = genScalar2;

        if (!r.F32(out float packedX) || !r.F32(out float packedY))
        {
            error = "truncated reading packed";
            return false;
        }

        vol.PackedX = packedX;
        vol.PackedY = packedY;

        if (!r.F32(out vol.Colour[0]) || !r.F32(out vol.Colour[1]) || !r.F32(out vol.Colour[2]))
        {
            error = "truncated reading colour";
            return false;
        }

        if (!r.F32(out float field208))
        {
            error = "truncated reading field_208";
            return false;
        }

        vol.Field208 = field208;

        if (!r.F32(out float reciprocal))
        {
            error = "truncated reading reciprocal";
            return false;
        }

        vol.Reciprocal = reciprocal;

        if (!r.F32(out float falloff))
        {
            error = "truncated reading falloff";
            return false;
        }

        vol.Falloff = falloff;

        if (!r.U8(out byte hasTex))
        {
            error = "truncated reading has_texture flag";
            return false;
        }

        vol.HasTexture = hasTex != 0;

        if (vol.HasTexture)
        {
            int remaining = r.Remaining;
            if (remaining > 0)
            {
                vol.TextureBlob = new byte[remaining];
                if (!r.Blob(vol.TextureBlob, remaining))
                {
                    error = "truncated reading texture blob";
                    return false;
                }
            }
        }

        return true;
    }

    private sealed class Reader
    {
        private readonly byte[] _data;
        private readonly int _base;
        private readonly int _limit;

        public Reader(byte[] data) : this(data, 0, data.Length) { }

        public Reader(byte[] data, int start, int length)
        {
            _data = data;
            _base = start;
            _limit = start + length;
            Position = start;
        }

        public int Position { get; private set; }

        public int Remaining => _limit - Position;

        public bool U8(out byte value)
        {
            value = 0;
            if (Position + 1 > _limit)
            {
                return false;
            }

            value = _data[Position++];
            return true;
        }

        public bool U32(out uint value)
        {
            value = 0;
            if (Position + 4 > _limit)
            {
                return false;
            }

            value = ((uint)_data[Position] << 24) | ((uint)_data[Position + 1] << 16) |
                    ((uint)_data[Position + 2] << 8) | _data[Position + 3];
            Position += 4;
            return true;
        }

        public bool F32(out float value)
        {
            value = 0f;
            if (!U32(out uint raw))
            {
                return false;
            }

            value = BitConverter.Int32BitsToSingle(unchecked((int)raw));
            return true;
        }

        public bool Blob(byte[] dst, int length)
        {
            if (Position + length > _limit)
            {
                return false;
            }

            Array.Copy(_data, Position, dst, 0, length);
            Position += length;
            return true;
        }
    }
}
