using System;
using System.Collections.Generic;
using System.Text;

namespace Refii.Level;

public sealed class EngineLevelInstance
{
    public ulong Hash { get; set; }

    public byte Flag0 { get; set; }

    public byte Flag1 { get; set; }

    public byte Flag2 { get; set; }

    public float[] Values { get; } = new float[20];

    /// <summary>
    /// The instance's full authored rotation, (x, y, z, w), when the record carried one.
    ///
    /// Type-21 v12 ships a quaternion, and reducing it to a yaw angle throws away the pitch
    /// and roll the file actually states — which is most of what makes foliage look planted
    /// rather than stamped. Kept alongside <see cref="Values"/> rather than packed into it
    /// because the two type-21 versions fill different slots and overloading them again is how
    /// the static-placement records ended up being read with the foliage layout.
    /// </summary>
    public System.Numerics.Quaternion? Rotation { get; set; }
}

public sealed class EngineLevelBlock
{
    public int Offset { get; set; }

    public uint Type { get; set; }

    /// <summary>
    /// The block's 64-bit id (type-21 only; type-2 carries one per instance instead).
    ///
    /// This is the join into the <c>.lmp</c>: chapter2posh's 158 type-21 blocks match its 158
    /// u32-array entries key-for-key, and each array holds exactly one D3DCOLOR per loop1
    /// instance — the authored per-instance foliage bake.
    /// </summary>
    public ulong Hash { get; set; }

    public string ModelPath { get; set; } = string.Empty;

    public string ShadowModelPath { get; set; } = string.Empty;

    public string LodModelPath { get; set; } = string.Empty;

    public string ExtraModelPath { get; set; } = string.Empty;

    /// <summary>
    /// A type-21 block's draw distance: the guest stops drawing the whole block once the camera
    /// is this far from its bounding sphere (`sub_822261C8`). The fifth of the seven header
    /// floats, which `sub_82B202C0` stores to a[1] (0x82b207c8). Zero on type-2 blocks, whose
    /// distances are per instance.
    /// </summary>
    public float DrawDistance { get; set; }

    public List<EngineLevelInstance> Instances { get; } = new();
}

/// <summary>
/// A type-4 record: one heightfield, and the id of the baked lightmap that belongs to it.
/// </summary>
/// <remarks>
/// This record is how a landscape finds its bake, and the pairing exists nowhere else. The
/// guest reads the eight bytes that follow the name straight into its descriptor
/// (<c>descriptor+0x28</c>, <c>sub_82ABAEF0</c>), carries them to the render entity
/// (<c>entity+0x110</c>, <c>sub_82A8EBE8</c>) and looks them up in the <c>.lmp</c>'s terrain
/// map with a 64-bit compare (<c>sub_82A8EA50</c>: <c>*(level+0xD50)+0x1C</c>).
///
/// Confirmed against the shipped data rather than argued. For chapter2slums the two type-4
/// records carry 0xE6ABDD70 and 0x5129BEB3, and those are exactly the two keys in the .lmp's
/// terrain map — the first a 576x576 bake for the region's own heightfield, the second a
/// 32x64 one for the sea vista.
/// </remarks>
public sealed class EngineLevelHeightfield
{
    public string Path { get; set; } = string.Empty;

    public ulong LightmapKey { get; set; }
}

public sealed class EngineLevelFile
{
    public bool Ok { get; set; }

    public uint Version { get; set; }

    public uint EntryCount { get; set; }

    public List<EngineLevelBlock> PropBlocks { get; } = new();

    public List<EngineLevelBlock> FoliageBlocks { get; } = new();

    /// <summary>The level's heightfields, in file order, each with its lightmap id.</summary>
    public List<EngineLevelHeightfield> Heightfields { get; } = new();

    /// <summary>Type-5 records: one .water per record, loaded by `sub_82AB9818` (0x82abb374).</summary>
    public List<string> WaterPaths { get; } = new();

    /// <summary>Type-32 records: one .mist per record, loaded by `sub_82AB99F8` (0x82abb3a0).</summary>
    public List<string> MistPaths { get; } = new();

    public int Type21Blocks { get; set; }

    public int Type21Instances { get; set; }

    public int Type21Rejected { get; set; }

    public uint StoppedAtType { get; set; }

    public string Error { get; set; } = string.Empty;
}

public static class Engine_EngineLevelFile
{
    public const string Magic = "LevelGraphicsFile";

    public const uint MinVersion = 11;

    public const uint MaxVersion = 12;

    public const int Type2InstanceFloats = 20;

    public static EngineLevelFile Parse(byte[]? bytes)
    {
        var file = new EngineLevelFile();

        if (bytes is null || bytes.Length < Magic.Length + 8)
        {
            file.Error = "file too small for header";
            return file;
        }

        var reader = new Reader(bytes);

        for (int i = 0; i < Magic.Length; i++)
        {
            if (bytes[i] != (byte)Magic[i])
            {
                file.Error = "magic mismatch (expected \"LevelGraphicsFile\")";
                return file;
            }
        }

        reader.Skip(Magic.Length);

        if (!reader.U32(out uint version))
        {
            file.Error = "truncated reading version";
            return file;
        }

        file.Version = version;
        if (version < MinVersion || version > MaxVersion)
        {
            file.Error = $"unsupported version {version} (engine accepts 11..12)";
            return file;
        }

        if (!reader.U32(out uint entryCount))
        {
            file.Error = "truncated reading entry_count";
            return file;
        }

        file.EntryCount = entryCount;
        if (entryCount > (1u << 20))
        {
            file.Error = "entry_count looks corrupt";
            return file;
        }

        for (uint entry = 0; entry < entryCount; entry++)
        {
            int offset = reader.Position;

            if (!reader.U32(out uint type))
            {
                file.Error = $"truncated at entry {entry} of {entryCount}";
                return file;
            }

            switch (type)
            {
                case 2:
                    if (!ReadType2(reader, offset, file))
                    {
                        return file;
                    }

                    break;

                case 4:
                case 5:
                case 32:
                    if (!reader.CString(out string assetPath))
                    {
                        file.Error = $"truncated reading string for type {type}";
                        return file;
                    }

                    // The type-4 tail is the heightfield's LIGHTMAP ID, not padding. It used to
                    // be skipped, and skipping it is why the ground was lit from whichever of
                    // the level's bakes happened to be the larger — the vista, which has one of
                    // its own, got none at all.
                    if (type == 4)
                    {
                        if (!reader.U64(out ulong lightmapKey))
                        {
                            file.Error = "truncated reading type-4 lightmap id";
                            return file;
                        }

                        file.Heightfields.Add(new EngineLevelHeightfield
                        {
                            Path = assetPath,
                            LightmapKey = lightmapKey,
                        });
                    }
                    else if (type == 5 && assetPath.Length != 0)
                    {
                        file.WaterPaths.Add(assetPath);
                    }
                    else if (type == 32 && assetPath.Length != 0)
                    {
                        file.MistPaths.Add(assetPath);
                    }

                    break;

                case 21:
                    if (!ReadType21(reader, offset, file))
                    {
                        return file;
                    }

                    break;

                default:
                    file.StoppedAtType = type;
                    file.Ok = true;
                    return file;
            }
        }

        file.Ok = true;
        return file;
    }

    private static bool ReadType2(Reader reader, int offset, EngineLevelFile file)
    {
        var block = new EngineLevelBlock { Offset = offset, Type = 2 };

        if (!reader.CString(out string modelPath) ||
            !reader.CString(out string shadowPath) ||
            !reader.CString(out string lodPath) ||
            !reader.CString(out string extraPath))
        {
            file.Error = "truncated reading type-2 model paths";
            return false;
        }

        block.ModelPath = modelPath;
        block.ShadowModelPath = shadowPath;
        block.LodModelPath = lodPath;
        block.ExtraModelPath = extraPath;

        if (!reader.U32(out uint instanceCount))
        {
            file.Error = "truncated reading type-2 instance count";
            return false;
        }

        if (instanceCount > 100000)
        {
            file.Error = "type-2 instance count looks corrupt";
            return false;
        }

        for (uint i = 0; i < instanceCount; i++)
        {
            var instance = new EngineLevelInstance();

            if (!reader.U8(out byte f0) || !reader.U8(out byte f1) ||
                !reader.U8(out byte f2) || !reader.U64(out ulong hash))
            {
                file.Error = "truncated reading type-2 instance header";
                return false;
            }

            instance.Flag0 = f0;
            instance.Flag1 = f1;
            instance.Flag2 = f2;
            instance.Hash = hash;

            for (int v = 0; v < Type2InstanceFloats; v++)
            {
                if (!reader.F32(out float value))
                {
                    file.Error = "truncated reading type-2 instance floats";
                    return false;
                }

                instance.Values[v] = value;
            }

            block.Instances.Add(instance);
        }

        file.PropBlocks.Add(block);
        return true;
    }

    private static bool ReadType21(Reader reader, int offset, EngineLevelFile file)
    {
        var block = new EngineLevelBlock { Offset = offset, Type = 21 };

        if (!reader.CString(out string modelPath) || !reader.CString(out string lodPath))
        {
            file.Error = "truncated reading strings for type 21";
            return false;
        }

        block.ModelPath = modelPath;
        block.LodModelPath = lodPath;

        if (!reader.U64(out ulong blockHash) || !reader.Skip(1 + 1))
        {
            file.Error = "truncated reading type-21 hash+flags";
            return false;
        }

        block.Hash = blockHash;

        if (!reader.U32(out uint loop1Count))
        {
            file.Error = "truncated type-21 ext header";
            return false;
        }

        // Seven floats (`sub_82B202C0`): two to msg+0xA0/+0xA4, a[7], a[8], the draw distance
        // a[1], the fade fraction a[0] — which foliage uses only as an `a > 0` gate, so the cut
        // is hard — and a[2].
        float[] header = new float[7];
        for (int f = 0; f < header.Length; f++)
        {
            if (!reader.F32(out header[f]))
            {
                file.Error = "truncated type-21 ext header (floats)";
                return false;
            }
        }

        block.DrawDistance = header[4];

        if (!reader.Skip(12 + 4 + 24))
        {
            file.Error = "truncated type-21 ext header (mid)";
            return false;
        }

        if (loop1Count > 100000)
        {
            file.Error = "type-21 loop1 count looks corrupt";
            return false;
        }

        for (uint k = 0; k < loop1Count; k++)
        {
            var instance = new EngineLevelInstance();

            if (file.Version == 11)
            {
                for (int j = 0; j < 4; j++)
                {
                    if (!reader.F32(out float value))
                    {
                        file.Error = "truncated type-21 v11 loop1 body";
                        return false;
                    }

                    instance.Values[j] = value;
                }

                instance.Values[7] = 1f;
                instance.Values[9] = 1f;
                instance.Values[10] = 1f;
                instance.Values[11] = 1f;
            }
            else
            {
                if (!reader.F32(out float px) || !reader.F32(out float py) ||
                    !reader.F32(out float pz))
                {
                    file.Error = "truncated type-21 v12 instance vec3";
                    return false;
                }

                if (!reader.Half(out float qx) || !reader.Half(out float qy) ||
                    !reader.Half(out float qz) || !reader.Half(out float qw) ||
                    !reader.Half(out float scale))
                {
                    file.Error = "truncated type-21 v12 instance quat/scale";
                    return false;
                }

                instance.Values[0] = px;
                instance.Values[1] = py;
                instance.Values[2] = pz;

                // The whole rotation, kept. The yaw pair below stays as the fallback for
                // anything that cannot use a quaternion.
                instance.Rotation = new System.Numerics.Quaternion(qx, qy, qz, qw);

                float num = 2f * ((qw * qz) + (qx * qy));
                float den = 1f - (2f * ((qy * qy) + (qz * qz)));
                float mag = MathF.Sqrt((num * num) + (den * den));
                if (mag > 1e-6f)
                {
                    instance.Values[6] = num / mag;
                    instance.Values[7] = den / mag;
                }
                else
                {
                    instance.Values[6] = 0f;
                    instance.Values[7] = 1f;
                }

                float s = scale > 0f ? scale : 1f;
                instance.Values[9] = s;
                instance.Values[10] = s;
                instance.Values[11] = s;
            }

            block.Instances.Add(instance);
        }

        if (!reader.U32(out uint loop2Count))
        {
            file.Error = "truncated type-21 loop2 count";
            return false;
        }

        if (loop2Count > 100000)
        {
            file.Error = "type-21 loop2 count looks corrupt";
            return false;
        }

        for (uint k = 0; k < loop2Count; k++)
        {
            if (!reader.F32(out float aValue) || !reader.F32(out _) ||
                !reader.F32(out float p1X) || !reader.F32(out float p1Y) ||
                !reader.F32(out float p1Z) ||
                !reader.F32(out float p2X) || !reader.F32(out float p2Y) ||
                !reader.F32(out _))
            {
                file.Error = "truncated type-21 loop2 body";
                return false;
            }

            bool plausible =
                InBounds(p1X, -2048f, 2048f) &&
                InBounds(p1Y, -2048f, 2048f) &&
                InBounds(p1Z, -512f, 512f) &&
                (MathF.Abs(p1X) + MathF.Abs(p1Y) + MathF.Abs(p1Z)) > 0.5f;

            if (!plausible)
            {
                file.Type21Rejected++;
                continue;
            }

            var instance = new EngineLevelInstance();
            instance.Values[0] = p1X;
            instance.Values[1] = p1Y;
            instance.Values[2] = p1Z;

            float fxy = MathF.Sqrt((p2X * p2X) + (p2Y * p2Y));
            if (float.IsFinite(fxy) && fxy > 0.001f && fxy < 100f)
            {
                instance.Values[6] = p2Y / fxy;
                instance.Values[7] = p2X / fxy;
            }
            else
            {
                instance.Values[6] = 0f;
                instance.Values[7] = 1f;
            }

            float s = float.IsFinite(aValue) && aValue > 0.05f && aValue < 100f
                ? aValue
                : 1f;
            instance.Values[9] = s;
            instance.Values[10] = s;
            instance.Values[11] = s;

            block.Instances.Add(instance);
        }

        file.Type21Blocks++;
        file.Type21Instances += block.Instances.Count;

        if (block.Instances.Count != 0)
        {
            file.FoliageBlocks.Add(block);
        }

        return true;
    }

    private static bool InBounds(float value, float low, float high) =>
        float.IsFinite(value) && value >= low && value <= high;

    private sealed class Reader
    {
        private readonly byte[] _data;

        public Reader(byte[] data) => _data = data;

        public int Position { get; private set; }

        public bool Skip(long count)
        {
            if (count < 0 || Position + count > _data.Length)
            {
                return false;
            }

            Position += (int)count;
            return true;
        }

        public bool U8(out byte value)
        {
            value = 0;
            if (Position + 1 > _data.Length)
            {
                return false;
            }

            value = _data[Position++];
            return true;
        }

        public bool U32(out uint value)
        {
            value = 0;
            if (Position + 4 > _data.Length)
            {
                return false;
            }

            value = ((uint)_data[Position] << 24) | ((uint)_data[Position + 1] << 16) |
                    ((uint)_data[Position + 2] << 8) | _data[Position + 3];
            Position += 4;
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

        public bool Half(out float value)
        {
            value = 0f;
            if (Position + 2 > _data.Length)
            {
                return false;
            }

            uint h = ((uint)_data[Position] << 8) | _data[Position + 1];
            Position += 2;

            uint sign = (h & 0x8000u) << 16;
            uint exponent = (h >> 10) & 0x1fu;
            uint mantissa = h & 0x3ffu;
            uint bits;

            if (exponent == 0)
            {
                if (mantissa == 0)
                {
                    bits = sign;
                }
                else
                {
                    uint e = 127 - 14;
                    uint m = mantissa;
                    while ((m & 0x400u) == 0)
                    {
                        m <<= 1;
                        e--;
                    }

                    m &= 0x3ffu;
                    bits = sign | (e << 23) | (m << 13);
                }
            }
            else if (exponent == 31)
            {
                bits = sign | (0xffu << 23) | (mantissa << 13);
            }
            else
            {
                bits = sign | ((exponent + (127 - 15)) << 23) | (mantissa << 13);
            }

            value = BitConverter.Int32BitsToSingle(unchecked((int)bits));
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

        public bool CString(out string value)
        {
            value = string.Empty;
            int start = Position;
            int limit = Math.Min(_data.Length, start + 4096);

            while (Position < limit)
            {
                byte c = _data[Position++];
                if (c == 0)
                {
                    value = Encoding.ASCII.GetString(_data, start, Position - start - 1);
                    return true;
                }
            }

            return false;
        }
    }
}
