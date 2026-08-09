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
}

public sealed class EngineLevelBlock
{
    public int Offset { get; set; }

    public uint Type { get; set; }

    public string ModelPath { get; set; } = string.Empty;

    public string ShadowModelPath { get; set; } = string.Empty;

    public string LodModelPath { get; set; } = string.Empty;

    public string ExtraModelPath { get; set; } = string.Empty;

    public List<EngineLevelInstance> Instances { get; } = new();
}

public sealed class EngineLevelFile
{
    public bool Ok { get; set; }

    public uint Version { get; set; }

    public uint EntryCount { get; set; }

    public List<EngineLevelBlock> PropBlocks { get; } = new();

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
                    if (!reader.CString(out _))
                    {
                        file.Error = $"truncated reading string for type {type}";
                        return file;
                    }

                    if (type == 4 && !reader.Skip(8))
                    {
                        file.Error = "truncated reading type-4 tail";
                        return file;
                    }

                    break;

                case 21:
                    if (!ReadType21(reader, file))
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

    private static bool ReadType21(Reader reader, EngineLevelFile file)
    {
        if (!reader.CString(out _) || !reader.CString(out _))
        {
            file.Error = "truncated reading strings for type 21";
            return false;
        }

        if (!reader.Skip(8 + 1 + 1))
        {
            file.Error = "truncated reading type-21 hash+flags";
            return false;
        }

        if (!reader.U32(out uint loop1Count))
        {
            file.Error = "truncated type-21 ext header";
            return false;
        }

        if (!reader.Skip((7 * 4) + 12 + 4 + 24))
        {
            file.Error = "truncated type-21 ext header (mid)";
            return false;
        }

        if (loop1Count > 100000)
        {
            file.Error = "type-21 loop1 count looks corrupt";
            return false;
        }

        int loop1Stride = file.Version == 11 ? 4 * 4 : (3 * 4) + (5 * 2);
        if (!reader.Skip((long)loop1Count * loop1Stride))
        {
            file.Error = "truncated type-21 loop1 body";
            return false;
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

        if (!reader.Skip((long)loop2Count * 8 * 4))
        {
            file.Error = "truncated type-21 loop2 body";
            return false;
        }

        file.Type21Blocks++;
        file.Type21Instances += (int)(loop1Count + loop2Count);
        return true;
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
