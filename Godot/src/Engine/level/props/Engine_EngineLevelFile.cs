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

    /// <summary>
    /// Which sun cascades the instance casts into (Engine_ClassicShadows.Casts*), from the three
    /// flag bytes as `sub_82AB9CF8` folds them (0x82ABA570; Fable 3 the same at 0x82CD8498): a
    /// zero first byte is 0x40 (no cast), a non-zero second 0x80 (no distant cast), a non-zero
    /// third 0x100 (both faces).
    /// </summary>
    public byte Casting { get; set; } = Refii.Models.Engine_ClassicShadows.CastsDefault;

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

    /// <summary>Which sun cascades a type-21 block's instances cast into (Engine_ClassicShadows.Casts*).</summary>
    public byte Casting { get; set; } = Refii.Models.Engine_ClassicShadows.CastsDefault;

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

    /// <summary>A type-21 block's fade fraction f (header float 5, a[0]): it fades over [D(1-f), D].</summary>
    public float FadeFraction { get; set; }

    public List<EngineLevelInstance> Instances { get; } = new();

    /// <summary>Type-21 only: the block's cull clusters, which tile <see cref="Instances"/> in order.</summary>
    public List<EngineLevelCluster> Clusters { get; } = new();
}

/// <summary>
/// One type-21 cull cluster: instances [First, First + Count) and their guest-space bounds.
/// </summary>
/// <remarks>
/// The second loop of a type-21 record — u32 first, u32 count, then min and max. Over all 78
/// shipped .engine_level files (19009 blocks) the ranges tile loop1 exactly and every
/// instance lies in its own cluster's box. It places nothing: reading it as more plants stood
/// one at each box's min corner, which on a slope is in the air.
/// </remarks>
public readonly record struct EngineLevelCluster(
    uint First, uint Count, System.Numerics.Vector3 Min, System.Numerics.Vector3 Max);

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

    /// <summary>
    /// Type-5 records: one .water per record, loaded by `sub_82AB9818` (0x82abb374). Fable 3's
    /// record is the same path string alone (`sub_82CD8B48` 0x82CD8F7C reads one string, then
    /// `sub_82CB3ED0(level, path, 1)` with the ZERO vector unk_82001400 in v1 at 0x82CD8F88), so
    /// the file's patch centres and body height stand as written. The offset `sub_82CF8198` adds
    /// (v1.xy onto each patch's first two floats at 0x82CF8820, v1.z onto the body height at
    /// 0x82CF86C4) is non-zero only through the Lua native `LoadEngineDataForRegion` (level
    /// command 23, `sub_824CE950`), which loads `Fable3\Maps\&lt;region&gt;` — the map table's water,
    /// not a level's.
    /// </summary>
    public List<string> WaterPaths { get; } = new();

    /// <summary>Type-32 records: one .mist per record, loaded by `sub_82AB99F8` (0x82abb3a0).</summary>
    public List<string> MistPaths { get; } = new();

    public int Type21Blocks { get; set; }

    public int Type21Instances { get; set; }

    public uint StoppedAtType { get; set; }

    public string Error { get; set; } = string.Empty;
}

public static class Engine_EngineLevelFile
{
    public const string Magic = "LevelGraphicsFile";

    public const uint MinVersion = 11;

    public const uint MaxVersion = 12;

    public const int Type2InstanceFloats = 20;

    /// <summary>The model paths heading a type-2 record: model, shadow, LOD, then Fable 2's unused fourth.</summary>
    /// <remarks>
    /// Fable 3 dropped the fourth. Both readers hash the first three to msg+0x70/+0x74/+0x78;
    /// Fable 2's `sub_82AB9CF8` reads four (0x82AB9D28..0x82AB9D70) and only frees the last,
    /// Fable 3's `sub_82CD7C50` reads three (0x82CD7C80..0x82CD7CB0). The header states no
    /// difference (both accept versions 11..12), so the title the bytes came from says which.
    /// </remarks>
    public static int Type2PathCountFor(Refii.Core.Engine_Title title) =>
        title == Refii.Core.Engine_Title.Fable3 ? 3 : 4;

    /// <summary>The paths heading a type-21 record: the model, then Fable 2's LOD.</summary>
    /// <remarks>
    /// Fable 2's `sub_82ABA800` reads two (0x82ABA828, 0x82ABA840); Fable 3's `sub_82CD8680`
    /// reads one (0x82CD86A8) and goes straight to the 64-bit block id.
    /// </remarks>
    public static int Type21PathCountFor(Refii.Core.Engine_Title title) =>
        title == Refii.Core.Engine_Title.Fable3 ? 1 : 2;

    public static EngineLevelFile Parse(byte[]? bytes) => Parse(bytes, Refii.Core.Engine_Title.Active);

    public static EngineLevelFile Parse(byte[]? bytes, Refii.Core.Engine_Title title)
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
                    if (!ReadType2(reader, offset, file, title))
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
                    if (!ReadType21(reader, offset, file, title))
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

    private static bool ReadType2(
        Reader reader, int offset, EngineLevelFile file, Refii.Core.Engine_Title title)
    {
        var block = new EngineLevelBlock { Offset = offset, Type = 2 };

        string[] paths = { string.Empty, string.Empty, string.Empty, string.Empty };
        int pathCount = Type2PathCountFor(title);
        for (int p = 0; p < pathCount; p++)
        {
            if (!reader.CString(out paths[p]))
            {
                file.Error = "truncated reading type-2 model paths";
                return false;
            }
        }

        block.ModelPath = paths[0];
        block.ShadowModelPath = paths[1];
        block.LodModelPath = paths[2];
        block.ExtraModelPath = paths[3];

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
            instance.Casting = Refii.Models.Engine_ClassicShadows.CastingOf(0x30u |
                (f0 == 0 ? 0x40u : 0u) | (f1 != 0 ? 0x80u : 0u) | (f2 != 0 ? 0x100u : 0u));
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

    private static bool ReadType21(
        Reader reader, int offset, EngineLevelFile file, Refii.Core.Engine_Title title)
    {
        var block = new EngineLevelBlock { Offset = offset, Type = 21 };

        if (!reader.CString(out string modelPath))
        {
            file.Error = "truncated reading strings for type 21";
            return false;
        }

        block.ModelPath = modelPath;

        if (Type21PathCountFor(title) > 1)
        {
            if (!reader.CString(out string lodPath))
            {
                file.Error = "truncated reading strings for type 21";
                return false;
            }

            block.LodModelPath = lodPath;
        }

        if (!reader.U64(out ulong blockHash) || !reader.U8(out byte casts) ||
            !reader.U8(out byte noDistant))
        {
            file.Error = "truncated reading type-21 hash+flags";
            return false;
        }

        block.Hash = blockHash;
        // The block's two flag bytes (0x82ABA9AC; Fable 3 0x82CD8808): a zero first is 0x40, a
        // non-zero second 0x80.
        block.Casting = Refii.Models.Engine_ClassicShadows.CastingOf(0x10u |
            (casts == 0 ? 0x40u : 0u) | (noDistant != 0 ? 0x80u : 0u));

        if (!reader.U32(out uint loop1Count))
        {
            file.Error = "truncated type-21 ext header";
            return false;
        }

        // Seven floats (`sub_82B202C0`): two to msg+0xA0/+0xA4, a[7], a[8], the draw distance
        // a[1], the fade fraction a[0] and a[2]. The CPU cull at D is hard; the pixel shader
        // stipples the last fraction away (`sub_821FCCB0`: c15 = (D(1-f), 1/(fD), D(1-f), 0)).
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
        block.FadeFraction = header[5];

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
                if (!ReadType21V11Instance(reader, instance, title))
                {
                    file.Error = "truncated type-21 v11 loop1 body";
                    return false;
                }
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

                // The whole rotation, kept. The yaw pair stays as the fallback for anything that
                // cannot use a quaternion.
                SetRotation(instance, new System.Numerics.Quaternion(qx, qy, qz, qw));

                float s = scale > 0f ? scale : 1f;
                instance.Values[9] = s;
                instance.Values[10] = s;
                instance.Values[11] = s;
            }

            block.Instances.Add(instance);
        }

        if (!reader.U32(out uint clusterCount))
        {
            file.Error = "truncated type-21 cluster count";
            return false;
        }

        if (clusterCount > 100000)
        {
            file.Error = "type-21 cluster count looks corrupt";
            return false;
        }

        for (uint k = 0; k < clusterCount; k++)
        {
            if (!reader.U32(out uint first) || !reader.U32(out uint count) ||
                !reader.F32(out float minX) || !reader.F32(out float minY) ||
                !reader.F32(out float minZ) ||
                !reader.F32(out float maxX) || !reader.F32(out float maxY) ||
                !reader.F32(out float maxZ))
            {
                file.Error = "truncated type-21 cluster";
                return false;
            }

            block.Clusters.Add(new EngineLevelCluster(
                first, count,
                new System.Numerics.Vector3(minX, minY, minZ),
                new System.Numerics.Vector3(maxX, maxY, maxZ)));
        }

        file.Type21Blocks++;
        file.Type21Instances += block.Instances.Count;

        if (block.Instances.Count != 0)
        {
            file.FoliageBlocks.Add(block);
        }

        return true;
    }

    /// <summary>
    /// A type-21 v11 instance: a stated 4x4 matrix, a scale, and in Fable 2 a trailing float4.
    /// </summary>
    /// <remarks>
    /// Four float4 rows (`sub_82DA58E0` x4 in Fable 3's `sub_82CE6498` from 0x82CE6BDC;
    /// `sub_82A21B78` x4 in Fable 2's `sub_82B202C0` from 0x82B20A8C), then one float the guest
    /// stores where v12 keeps its scale (0x82CE6E08). Fable 2 reads one more float4 after it
    /// (0x82B20CBC); Fable 3 does not. The guest transposes the rows into the same 3x4 form the
    /// v12 quaternion builds, so rows 0..2 are the local axes and row 3's xyz is the position;
    /// the fourth column is never read. Neither title ships a v11 file today.
    /// </remarks>
    private static bool ReadType21V11Instance(
        Reader reader, EngineLevelInstance instance, Refii.Core.Engine_Title title)
    {
        float[] m = new float[16];
        for (int j = 0; j < m.Length; j++)
        {
            if (!reader.F32(out m[j]))
            {
                return false;
            }
        }

        if (!reader.F32(out _))
        {
            return false;
        }

        if (title != Refii.Core.Engine_Title.Fable3 && !reader.Skip(16))
        {
            return false;
        }

        var x = new System.Numerics.Vector3(m[0], m[1], m[2]);
        var y = new System.Numerics.Vector3(m[4], m[5], m[6]);
        var z = new System.Numerics.Vector3(m[8], m[9], m[10]);

        instance.Values[0] = m[12];
        instance.Values[1] = m[13];
        instance.Values[2] = m[14];

        float sx = x.Length(), sy = y.Length(), sz = z.Length();
        instance.Values[9] = sx > 1e-6f ? sx : 1f;
        instance.Values[10] = sy > 1e-6f ? sy : 1f;
        instance.Values[11] = sz > 1e-6f ? sz : 1f;

        x /= instance.Values[9];
        y /= instance.Values[10];
        z /= instance.Values[11];

        // System.Numerics is row-vector, as the guest's sub_8220C8F0 is: row 1 is the local X.
        SetRotation(instance, System.Numerics.Quaternion.CreateFromRotationMatrix(new System.Numerics.Matrix4x4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            0f, 0f, 0f, 1f)));
        return true;
    }

    /// <summary>Keeps the full rotation and reduces it to the yaw sin/cos pair in values[6]/[7].</summary>
    private static void SetRotation(EngineLevelInstance instance, System.Numerics.Quaternion q)
    {
        instance.Rotation = q;

        float num = 2f * ((q.W * q.Z) + (q.X * q.Y));
        float den = 1f - (2f * ((q.Y * q.Y) + (q.Z * q.Z)));
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
    }

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
