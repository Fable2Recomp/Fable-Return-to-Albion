using System;
using System.Collections.Generic;

namespace Refii.Models;

public static class Engine_MdlReparse
{
    private const int AltVertexStride = 20;

    public static void Apply(byte[] data, MdlInfo info, string virtualPath)
    {
        if (info.IsStub)
        {
            return;
        }

        if (AllBuffersEmpty(info))
        {
            TryPolymshScan(data, info);
        }

        if (CountMissing(info) > 0)
        {
            TryMissingBuffers(data, info);
        }

        if (CountMissing(info) > 0)
        {
            TryFoliage48(data, info);
        }

        if (IsMultiInstanceTarget(virtualPath))
        {
            TryMultiInstance(data, info);
        }
    }

    private static bool IsMultiInstanceTarget(string virtualPath)
    {
        string p = virtualPath.Replace('\\', '/').ToLowerInvariant();
        return p.Contains("bs_townhouse_basic_snow_v2", StringComparison.Ordinal) &&
               (p.Contains("/exterior.mdl", StringComparison.Ordinal) ||
                p.Contains("/interior.mdl", StringComparison.Ordinal));
    }

    private static int CountMissing(MdlInfo info)
    {
        int n = 0;
        if (info.MeshBuffers.Count < info.MeshCount)
        {
            n += (int)info.MeshCount - info.MeshBuffers.Count;
        }

        foreach (MdlMeshBuffer mb in info.MeshBuffers)
        {
            if (mb.VertexCount == 0 || mb.FaceCount == 0)
            {
                ++n;
            }
        }

        return n;
    }

    private static bool AllBuffersEmpty(MdlInfo info)
    {
        if (info.MeshBuffers.Count == 0)
        {
            return false;
        }

        foreach (MdlMeshBuffer mb in info.MeshBuffers)
        {
            if (mb.VertexCount > 0 && mb.FaceCount > 0)
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryMissingBuffers(byte[] data, MdlInfo info)
    {
        if (info.MeshCount == 0)
        {
            return false;
        }

        while (info.MeshBuffers.Count < info.MeshCount)
        {
            info.MeshBuffers.Add(new MdlMeshBuffer { MeshIndex = (uint)info.MeshBuffers.Count });
        }

        var r = new MdlReader(data);
        bool added = false;

        for (uint mi = 0; mi < info.MeshCount; ++mi)
        {
            if (info.MeshBuffers[(int)mi].VertexCount > 0)
            {
                continue;
            }

            for (int pos = 1; pos + 9 < data.Length; ++pos)
            {
                if (data[pos] != 0x01 || data[pos - 1] != 0x00)
                {
                    continue;
                }

                if (Read32(data, pos + 1) != mi)
                {
                    continue;
                }

                int strEnd = pos - 1;
                int strStart = strEnd;
                while (strStart > 0 && data[strStart - 1] >= 32 && data[strStart - 1] < 127)
                {
                    --strStart;
                }

                if (strEnd - strStart < 3)
                {
                    continue;
                }

                int headerPos = pos + 9;
                if (headerPos + 12 > data.Length)
                {
                    continue;
                }

                r.Position = headerPos;
                if (!TryReadAltRecord(r, true, out MdlMeshBuffer buffer))
                {
                    continue;
                }

                buffer.MeshIndex = mi;
                info.MeshBuffers[(int)mi] = buffer;
                added = true;
                break;
            }
        }

        return added;
    }

    public static bool TryPolymshScan(byte[] data, MdlInfo info)
    {
        if (data.Length < 16)
        {
            return false;
        }

        ReadOnlySpan<byte> magic = new byte[]
        {
            (byte)'p', (byte)'o', (byte)'l', (byte)'y', (byte)'m', (byte)'s', (byte)'h', 0x00, 0x01,
        };

        var recovered = new List<MdlMeshBuffer>();
        var r = new MdlReader(data);

        for (int scan = 0; scan + magic.Length <= data.Length; ++scan)
        {
            if (!data.AsSpan(scan, magic.Length).SequenceEqual(magic))
            {
                continue;
            }

            r.Position = scan + magic.Length;
            if (!r.U32(out _) || !r.U32(out _))
            {
                continue;
            }

            if (!TryReadAltRecord(r, false, out MdlMeshBuffer buffer))
            {
                continue;
            }

            buffer.MeshIndex = (uint)recovered.Count;
            recovered.Add(buffer);
            scan = r.Position - 1;
        }

        if (recovered.Count == 0)
        {
            return false;
        }

        info.MeshBuffers.Clear();
        for (int mi = 0; mi < info.Meshes.Count; ++mi)
        {
            if (mi < recovered.Count)
            {
                MdlMeshBuffer mb = recovered[mi];
                mb.MeshIndex = (uint)mi;
                info.MeshBuffers.Add(mb);
            }
            else
            {
                info.MeshBuffers.Add(new MdlMeshBuffer { MeshIndex = (uint)mi });
            }
        }

        if (info.MeshCount == 0 || info.MeshCount < info.Meshes.Count)
        {
            info.MeshCount = (uint)info.Meshes.Count;
        }

        return true;
    }

    public static bool TryFoliage48(byte[] data, MdlInfo info)
    {
        if (info.MeshCount != 1)
        {
            return false;
        }

        for (int pass = 0; pass < 2; ++pass)
        {
            var r = new MdlReader(data);
            for (int scan = 0; scan + 18 <= data.Length; ++scan)
            {
                r.Position = scan;
                if (!r.U16(out ushort lead)) break;
                if (pass == 0 ? lead != 0 : lead > 255) continue;

                if (!r.U32(out uint f0)) break;
                if (!r.U32(out _)) break;
                if (!r.U32(out uint indexCount)) break;
                if (!r.U32(out uint vertexCount)) break;

                if (pass == 1 && f0 != 0) continue;
                if (vertexCount == 0 || vertexCount > 100000u) continue;
                if (indexCount == 0 || indexCount > 100000u) continue;

                long needed = ((long)vertexCount * 48) + ((long)indexCount * 2);
                if (r.Position + needed > data.Length) continue;
                if (data.Length - (r.Position + needed) > 16) continue;

                info.MeshBuffers.Clear();
                info.MeshBuffers.Add(new MdlMeshBuffer
                {
                    VertexCount = vertexCount,
                    VertexOffset = r.Position,
                    FaceCount = indexCount,
                    FaceOffset = r.Position + (int)(vertexCount * 48),
                    MeshIndex = 0,
                    IsFoliagePath = true,
                    FoliageVertexStride = 48,
                });

                return true;
            }
        }

        return false;
    }

    public static bool TryMultiInstance(byte[] data, MdlInfo info)
    {
        if (info.MeshCount == 0 || info.Meshes.Count == 0 || data.Length < 32)
        {
            return false;
        }

        var byString = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int pos = 1; pos + 16 < data.Length; ++pos)
        {
            if (data[pos] != 0x00 || data[pos + 1] != 0x01)
            {
                continue;
            }

            if (Read32(data, pos + 2) > 100000u || Read32(data, pos + 6) >= info.MeshCount)
            {
                continue;
            }

            int strStart = pos;
            while (strStart > 0 && data[strStart - 1] >= 32 && data[strStart - 1] < 127)
            {
                --strStart;
            }

            int length = pos - strStart;
            if (length < 3 || length > 64)
            {
                continue;
            }

            string s = System.Text.Encoding.ASCII.GetString(data, strStart, length);
            if (!byString.TryGetValue(s, out List<int>? list))
            {
                byString[s] = list = new List<int>();
            }

            list.Add(strStart);
        }

        string bestString = string.Empty;
        List<int> positions = new();
        foreach (KeyValuePair<string, List<int>> kv in byString)
        {
            if (kv.Value.Count > positions.Count)
            {
                positions = kv.Value;
                bestString = kv.Key;
            }
        }

        if (positions.Count <= info.MeshBuffers.Count)
        {
            return false;
        }

        positions.Sort();

        List<MdlMesh> baseMeshes = new(info.Meshes);
        var newBuffers = new List<MdlMeshBuffer>();
        var newMeshes = new List<MdlMesh>();
        var r = new MdlReader(data);

        foreach (int hpos in positions)
        {
            int afterStr = hpos + bestString.Length;
            if (afterStr + 10 > data.Length) continue;
            if (data[afterStr] != 0x00 || data[afterStr + 1] != 0x01) continue;

            uint mid2 = Read32(data, afterStr + 6);
            if (mid2 >= baseMeshes.Count) continue;

            r.Position = afterStr + 10;
            if (!TryReadAltRecord(r, false, out MdlMeshBuffer buffer))
            {
                continue;
            }

            buffer.MeshIndex = (uint)newBuffers.Count;
            newBuffers.Add(buffer);
            newMeshes.Add(baseMeshes[(int)mid2]);
        }

        if (newBuffers.Count <= info.MeshBuffers.Count)
        {
            return false;
        }

        info.MeshBuffers.Clear();
        info.MeshBuffers.AddRange(newBuffers);
        info.Meshes.Clear();
        info.Meshes.AddRange(newMeshes);
        info.MeshCount = (uint)info.Meshes.Count;
        return true;
    }

    private static bool TryReadAltRecord(MdlReader r, bool checkSomeCount, out MdlMeshBuffer buffer)
    {
        buffer = null!;

        if (!r.U32(out uint someCount)) return false;
        if (!r.U32(out uint indexCount)) return false;
        if (!r.U32(out uint vertexCount)) return false;

        if (vertexCount == 0 || vertexCount >= 65535u) return false;
        if (indexCount == 0 || indexCount >= 65535u) return false;
        if (checkSomeCount && someCount >= 65535u) return false;
        if (!r.Skip(40)) return false;

        if (!r.U32(out uint subMeshCount)) return false;
        if (!r.U32(out uint nextValue)) return false;

        uint finalSubMeshCount;
        if (nextValue != 0xFFFFFFFFu || subMeshCount >= 256u)
        {
            finalSubMeshCount = 1;
        }
        else
        {
            r.Position -= 4;
            finalSubMeshCount = subMeshCount;
        }

        bool markerFound = false;
        int limit = Math.Min(r.Length - 4, r.Position + 1024);
        for (int sp = r.Position; sp <= limit; ++sp)
        {
            if ((uint)((r[sp] << 24) | (r[sp + 1] << 16) | (r[sp + 2] << 8) | r[sp + 3]) == 0xFFFFFFFFu)
            {
                r.Position = sp;
                markerFound = true;
                break;
            }
        }

        if (!markerFound)
        {
            return false;
        }

        var result = new MdlMeshBuffer
        {
            VertexCount = vertexCount,
            FaceCount = indexCount,
            IsAltPath = true,
        };

        for (uint s = 0; s < finalSubMeshCount; ++s)
        {
            if (!r.U32(out uint marker)) return false;
            if (!r.U32(out uint materialRaw)) return false;
            if (!r.U8(out _)) return false;
            if (!r.U32(out uint faceCount)) return false;
            if (!r.U32(out uint startIndex)) return false;
            for (int k = 0; k < 6; ++k)
            {
                if (!r.F32(out _)) return false;
            }

            result.SubMeshes.Add(new MdlSubMesh
            {
                RegionIndex = marker,
                MaterialIndex = (byte)(materialRaw & 0xFF),
                FaceCount = faceCount,
                StartIndex = startIndex,
            });
        }

        result.VertexOffset = r.Position;
        if ((long)r.Position + ((long)vertexCount * AltVertexStride) > r.Length) return false;
        if (!r.Skip((int)(vertexCount * AltVertexStride))) return false;

        result.FaceOffset = r.Position;
        if ((long)r.Position + ((long)indexCount * 2) > r.Length) return false;

        buffer = result;
        return true;
    }

    private static uint Read32(byte[] data, int at) =>
        (uint)((data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3]);
}
