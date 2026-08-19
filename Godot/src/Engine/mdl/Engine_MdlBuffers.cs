using System;

namespace Refii.Models;

public static class Engine_MdlBuffers
{
    private const int VertexStride = 28;

    /// <summary>
    /// The largest VERTEX count a part can legitimately carry.
    /// </summary>
    /// <remarks>
    /// From the guest (IDA, 2026-08-19): every index buffer is created with `Format = 1`, two
    /// bytes per index, and `0xFFFF` is the strip-restart sentinel — `sub_82B26460` skips it
    /// when computing a batch's max index, and `sub_82B229B8` sizes its instance replication as
    /// `0xFFFE / vertexCount`. So an index can address 0..0xFFFE and a part cannot hold more
    /// than 65535 vertices. A larger count means the cursor is misaligned, not that the model is
    /// big.
    ///
    /// `&gt;` and not `&gt;=`: an exactly-65535-vertex part is legal and the old rule rejected it.
    /// </remarks>
    private const uint MaxVertexCount = 65_535u;

    /// <summary>
    /// The largest INDEX count, which is NOT bounded the same way.
    /// </summary>
    /// <remarks>
    /// Index VALUES are 16-bit; the index COUNT is not. A strip length may exceed 65535 freely,
    /// and the old rule capped it there — rejecting legitimate geometry. This is the sanity
    /// bound the guest-derived <c>Engine_MdlEngine</c> uses, so the two parsers agree on what
    /// "absurd" means.
    /// </remarks>
    private const uint MaxIndexCount = 4_000_000u;

    public static void ParseStandardBuffers(MdlReader r, MdlInfo info)
    {
        if (!ReadBuffer(r, info, 0))
        {
            return;
        }

        int firstEnd = r.Position;
        int scanPos = firstEnd;

        for (uint mi = 1; mi < info.MeshCount; ++mi)
        {
            bool aligned = false;
            for (int sp = scanPos; sp + 4 <= r.Length; ++sp)
            {
                uint marker = (uint)((r[sp] << 24) | (r[sp + 1] << 16) | (r[sp + 2] << 8) | r[sp + 3]);
                if (marker == 0xFFFFFFFFu && sp >= 24)
                {
                    if (r[sp - 24] == 0x00 && r[sp - 23] == 0x00 &&
                        r[sp - 22] == 0x00 && r[sp - 21] >= 0x01)
                    {
                        r.Position = sp - 24;
                        aligned = true;
                        break;
                    }
                }
            }

            if (!aligned)
            {
                r.Position = firstEnd + 9;
            }

            if (!ReadBuffer(r, info, mi))
            {
                return;
            }

            scanPos = r.Position;
        }
    }

    private const int AltVertexStride = 20;

    public static void ParseAltBuffers(MdlReader r, MdlInfo info)
    {
        int searchAnchor = r.Position;
        bool wasStringFound = false;

        if (r.Position < r.Length)
        {
            byte next = r[r.Position];
            if (next >= 32 && next < 127)
            {
                int save = r.Position;
                if (r.Strz(out _))
                {
                    wasStringFound = true;
                    if (r.U8(out byte follow))
                    {
                        if (follow != 0x01)
                        {
                            wasStringFound = false;
                            r.Position = save;
                        }
                        else if (!r.U32(out _) || !r.U32(out _))
                        {
                            return;
                        }
                    }
                }
            }
        }

        for (uint mi = 0; mi < info.MeshCount; ++mi)
        {
            if (!wasStringFound)
            {
                if (!SearchBufferById(r, info, mi, searchAnchor))
                {
                    info.MeshBuffers.Add(new MdlMeshBuffer { MeshIndex = mi });
                }

                continue;
            }

            if (mi > 0 && !SeekNextAltMesh(r))
            {
                return;
            }

            if (!r.U32(out _)) return;
            if (!r.U32(out uint indexCount)) return;
            if (!r.U32(out uint vertexCount)) return;
            if (!r.Skip(40)) return;

            if (!r.U32(out uint subMeshCount)) return;
            if (!r.U32(out uint nextValue)) return;

            uint finalSubMeshCount;
            if (nextValue != 0xFFFFFFFFu || subMeshCount >= 256)
            {
                finalSubMeshCount = 1;
            }
            else
            {
                r.Position -= 4;
                finalSubMeshCount = subMeshCount;
            }

            bool markerFound = false;
            int limit = Math.Min(r.Length - 4, r.Position + 1000);
            for (int sp = r.Position; sp <= limit; ++sp)
            {
                uint marker = (uint)((r[sp] << 24) | (r[sp + 1] << 16) | (r[sp + 2] << 8) | r[sp + 3]);
                if (marker == 0xFFFFFFFFu)
                {
                    r.Position = sp;
                    markerFound = true;
                    break;
                }
            }

            if (!markerFound)
            {
                return;
            }

            var buffer = new MdlMeshBuffer
            {
                VertexCount = vertexCount,
                FaceCount = indexCount,
                MeshIndex = mi,
                IsAltPath = true,
            };

            for (uint s = 0; s < finalSubMeshCount; ++s)
            {
                if (!r.U32(out uint marker)) return;
                if (!r.U32(out uint matIndexRaw)) return;
                if (!r.U8(out _)) return;
                if (!r.U32(out uint faceCount)) return;
                if (!r.U32(out uint startIndex)) return;
                for (int k = 0; k < 6; ++k)
                {
                    if (!r.F32(out _)) return;
                }

                buffer.SubMeshes.Add(new MdlSubMesh
                {
                    RegionIndex = marker,
                    MaterialIndex = (byte)(matIndexRaw & 0xFF),
                    FaceCount = faceCount,
                    StartIndex = startIndex,
                });
            }

            // Same correction as the standard path above: the offsets are always assigned when
            // the buffer is read, so the decoder can never be pointed at the model header.
            if (vertexCount > MaxVertexCount || indexCount > MaxIndexCount)
            {
                return;
            }

            if (vertexCount > 0)
            {
                buffer.VertexOffset = r.Position;
                if (!r.Skip((int)(vertexCount * AltVertexStride))) return;
            }

            if (indexCount > 0)
            {
                buffer.FaceOffset = r.Position;
                if (!r.Skip((int)(indexCount * 2))) return;
            }

            if (vertexCount > 0)
            {
                int secondary = (int)(vertexCount * 16) + 4;
                bool lastMesh = mi + 1 == info.MeshCount;
                bool skinned = info.Bones.Count > 0;

                if (skinned)
                {
                    if (r.Position + vertexCount * 16 <= r.Length)
                    {
                        buffer.UvBufferOffset = r.Position;
                        buffer.UvBufferStride = 16;
                    }
                }
                else if (!lastMesh)
                {
                    if (LooksLikeMeshHeader(r, r.Position))
                    {
                    }
                    else if (r.Position + 4 <= r.Length && LooksLikeMeshHeader(r, r.Position + 4))
                    {
                        r.Skip(4);
                    }
                    else if (r.Position + secondary <= r.Length &&
                             LooksLikeMeshHeader(r, r.Position + secondary))
                    {
                        buffer.UvBufferOffset = r.Position;
                        buffer.UvBufferStride = 16;
                        r.Skip(secondary);
                    }
                }
                else if (r.Position + secondary <= r.Length)
                {
                    buffer.UvBufferOffset = r.Position;
                    buffer.UvBufferStride = 16;
                }
            }

            info.MeshBuffers.Add(buffer);
        }
    }

    private static bool SearchBufferById(MdlReader r, MdlInfo info, uint meshIndex, int anchor)
    {
        int start = meshIndex == 0 && anchor < r.Position ? anchor : r.Position;

        for (int sp = start; sp + 28 <= r.Length; ++sp)
        {
            uint bufferId = Read32(r, sp);
            if (bufferId != meshIndex)
            {
                continue;
            }

            uint someCount = Read32(r, sp + 8);
            uint indexCount = Read32(r, sp + 12);
            uint vertexCount = Read32(r, sp + 16);
            uint subMeshCount = Read32(r, sp + 20);

            if (someCount >= 65535u || indexCount >= 65535u ||
                vertexCount >= 65535u || subMeshCount >= 256u)
            {
                continue;
            }

            if (subMeshCount > 0 && Read32(r, sp + 24) != 0xFFFFFFFFu)
            {
                continue;
            }

            r.Position = sp;
            return ReadBuffer(r, info, meshIndex);
        }

        return false;
    }

    private static uint Read32(MdlReader r, int at) =>
        (uint)((r[at] << 24) | (r[at + 1] << 16) | (r[at + 2] << 8) | r[at + 3]);

    private static bool SeekNextAltMesh(MdlReader r)
    {
        for (int sp = r.Position; sp < r.Length; ++sp)
        {
            byte b = r[sp];
            if (b < 32 || b >= 127)
            {
                continue;
            }

            r.Position = sp;
            if (!r.Strz(out _)) return false;
            if (!r.U8(out byte follow)) return false;
            if (follow == 0x01)
            {
                return r.U32(out _) && r.U32(out _);
            }

            r.Position = sp + 1;
        }

        return false;
    }

    private static bool LooksLikeMeshHeader(MdlReader r, int at)
    {
        if (at >= r.Length) return false;
        byte b0 = r[at];
        if (b0 < 32 || b0 >= 127) return false;

        int limit = Math.Min(r.Length, at + 64);
        int p = at;
        while (p < limit && r[p] != 0)
        {
            byte c = r[p];
            if (c < 32 || c >= 127) return false;
            ++p;
        }

        if (p >= limit || r[p] != 0) return false;
        if (p == at || p - at > 32) return false;
        if (p + 1 >= r.Length) return false;
        return r[p + 1] == 0x01;
    }

    private static bool ReadBuffer(MdlReader r, MdlInfo info, uint meshIndex)
    {
        if (!r.U32(out _) || !r.U32(out _) || !r.U32(out _))
        {
            return false;
        }

        if (!r.U32(out uint indexCount)) return false;
        if (!r.U32(out uint vertexCount)) return false;
        if (!r.U32(out uint subMeshCount)) return false;

        var buffer = new MdlMeshBuffer
        {
            VertexCount = vertexCount,
            FaceCount = indexCount,
            MeshIndex = meshIndex,
        };

        if (subMeshCount > 0 && subMeshCount < 65535u)
        {
            for (uint s = 0; s < subMeshCount; ++s)
            {
                if (!r.U32(out uint marker)) return false;
                if (!r.U32(out uint matIndexRaw)) return false;
                if (!r.U8(out _)) return false;
                if (!r.U32(out uint faceCount)) return false;
                if (!r.U32(out uint startIndex)) return false;
                for (int k = 0; k < 6; ++k)
                {
                    if (!r.F32(out _)) return false;
                }

                buffer.SubMeshes.Add(new MdlSubMesh
                {
                    RegionIndex = marker,
                    MaterialIndex = (byte)(matIndexRaw & 0xFF),
                    FaceCount = faceCount,
                    StartIndex = startIndex,
                });
            }
        }

        // 65535 WAS NEVER A FORMAT RULE.
        //
        // It arrived as a Python `_bounded_count(x, 0, 65535)` helper written for BONE and
        // MATERIAL counts while the format was being scanned, and was copy-pasted onto vertex
        // and index counts when the buffer parser was ported to C++ and then to C#. The one
        // parser in this tree derived from the guest's OWN loader bounds at 4,000,000
        // (Engine_MdlEngine), and 0xFFFF's real role in this format is the strip-restart index,
        // not a count limit — a STRIP LENGTH can legitimately exceed 65535.
        //
        // What the old rule actually did was skip the OFFSET assignment while still storing the
        // count and adding the buffer, leaving VertexOffset at its default of 0. The decoder's
        // own check is `0 + count*stride <= data.Length`, which PASSES on any large enough file,
        // so positions were decoded from byte 0 — the model HEADER — as big-endian half-floats.
        // Header bytes land in 0x7C..0x7F / 0xFC..0xFF often, and those decode to NaN, which
        // hangs Godot's render-list sort outright.
        //
        // So the offsets are now always assigned when the buffer is read, and a count that is
        // genuinely absurd aborts rather than silently desyncing the reader — which is what the
        // engine-format parser already does with the same bound.
        if (vertexCount > MaxVertexCount || indexCount > MaxIndexCount)
        {
            return false;
        }

        if (vertexCount > 0)
        {
            buffer.VertexOffset = r.Position;
            if (!r.Skip((int)(vertexCount * VertexStride))) return false;
        }

        if (indexCount > 0)
        {
            buffer.FaceOffset = r.Position;
            if (!r.Skip((int)(indexCount * 2))) return false;
        }

        if (vertexCount > 0)
        {
            if (!r.Skip((int)(vertexCount * 16))) return false;
        }

        info.MeshBuffers.Add(buffer);
        return true;
    }
}
