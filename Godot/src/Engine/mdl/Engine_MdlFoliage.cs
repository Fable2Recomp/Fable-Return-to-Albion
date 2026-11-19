using System;
using System.Collections.Generic;

namespace Refii.Models;

public static class Engine_MdlFoliage
{
    public static bool IsFoliagePath(string path)
    {
        if (path.Length == 0)
        {
            return false;
        }

        string normalised = path.Replace('\\', '/');
        return normalised.Contains("/foliage/", StringComparison.OrdinalIgnoreCase) ||
               normalised.StartsWith("foliage/", StringComparison.OrdinalIgnoreCase);
    }

    public static void ParseBuffers(MdlReader r, MdlInfo info)
    {
        bool hasClassTag = ReadClassTag(r);

        if (!r.U16(out _))
        {
            return;
        }

        if (IsTreeFoliage(r))
        {
            ParseTreeBuffers(r, info);
            return;
        }

        ParseGroundBuffers(r, info, hasClassTag);
    }

    private static bool ReadClassTag(MdlReader r)
    {
        if (r.Position >= r.Length)
        {
            return false;
        }

        byte b0 = r[r.Position];
        bool startsAlpha = (b0 >= 'A' && b0 <= 'Z') || (b0 >= 'a' && b0 <= 'z');
        if (!startsAlpha)
        {
            return false;
        }

        int save = r.Position;
        int scanLimit = Math.Min(r.Length, r.Position + 64);
        bool valid = false;
        for (int p = r.Position; p < scanLimit; ++p)
        {
            byte b = r[p];
            if (b == 0)
            {
                valid = p > r.Position;
                break;
            }

            if (b < 32 || b >= 127)
            {
                valid = false;
                break;
            }
        }

        if (!valid || !r.Strz(out _, 64))
        {
            r.Position = save;
            return false;
        }

        if (r.Position + 3 > r.Length || !r.Skip(3))
        {
            r.Position = save;
            return false;
        }

        return true;
    }

    private static bool IsTreeFoliage(MdlReader r)
    {
        int save = r.Position;
        bool result = false;

        if (r.U8(out byte peek) && peek == 0x01 && r.U32(out uint check) && check == 0)
        {
            result = true;
        }

        r.Position = save;
        return result;
    }

    private static void ParseTreeBuffers(MdlReader r, MdlInfo info)
    {
        if (!r.U8(out _))
        {
            return;
        }

        for (uint mi = 0; mi < info.MeshCount; ++mi)
        {
            if (!ReadCounts(r, out uint indexCount, out uint vertexCount))
            {
                return;
            }

            var buffer = new MdlMeshBuffer
            {
                VertexCount = vertexCount,
                VertexOffset = r.Position,
                MeshIndex = mi,
                IsFoliagePath = true,
                FoliageVertexStride = 36,
            };

            if (vertexCount > 0 && vertexCount < 100000u && !r.Skip((int)(vertexCount * 36)))
            {
                return;
            }

            buffer.FaceCount = indexCount;
            buffer.FaceOffset = r.Position;

            if (indexCount > 0 && indexCount < 100000u && !r.Skip((int)(indexCount * 2)))
            {
                return;
            }

            if (vertexCount > 0 && vertexCount < 100000u && !r.Skip((int)(vertexCount * 16)))
            {
                return;
            }

            if (mi + 1 < info.MeshCount && !r.Skip(2))
            {
                return;
            }

            info.MeshBuffers.Add(buffer);
        }
    }

    private static void ParseGroundBuffers(MdlReader r, MdlInfo info, bool hasClassTag)
    {
        for (uint mi = 0; mi < info.MeshCount; ++mi)
        {
            if (!ReadCounts(r, out uint indexCount, out uint vertexCount))
            {
                return;
            }

            bool sane = vertexCount > 0 && vertexCount < 200000u &&
                        indexCount > 0 && indexCount < 200000u;
            bool fits48 = sane &&
                          (long)r.Position + ((long)vertexCount * 48) + ((long)indexCount * 2)
                              <= r.Length;

            if (!sane)
            {
                AddEmptyFrom(info, mi);
                return;
            }

            int stride = 0;
            int vertexOffset = 0;
            int faceOffset = 0;
            var subMeshes = new List<MdlSubMesh>();

            if (fits48 && !hasClassTag)
            {
                vertexOffset = r.Position;
                if (!r.Skip((int)(vertexCount * 48))) return;
                faceOffset = r.Position;
                if (!r.Skip((int)(indexCount * 2))) return;
                stride = 48;
            }

            if (stride == 0 && hasClassTag)
            {
                int headerStart = r.Position;
                bool ok = TryReadClassTagMesh(
                    r, vertexCount, indexCount, subMeshes,
                    out vertexOffset, out faceOffset);

                if (ok)
                {
                    stride = 20;
                }
                else
                {
                    r.Position = headerStart;
                    subMeshes.Clear();

                    if (fits48)
                    {
                        vertexOffset = r.Position;
                        if (!r.Skip((int)(vertexCount * 48))) return;
                        faceOffset = r.Position;
                        if (!r.Skip((int)(indexCount * 2))) return;
                        stride = 48;
                    }
                    else
                    {
                        info.MeshBuffers.Add(new MdlMeshBuffer
                        {
                            VertexCount = vertexCount,
                            VertexOffset = headerStart,
                            FaceCount = indexCount,
                            MeshIndex = mi,
                            IsFoliagePath = true,
                            FoliageVertexStride = 0,
                        });

                        AddEmptyFrom(info, mi + 1);
                        return;
                    }
                }
            }

            if (stride == 0)
            {
                return;
            }

            var buffer = new MdlMeshBuffer
            {
                VertexCount = vertexCount,
                VertexOffset = vertexOffset,
                FaceCount = indexCount,
                FaceOffset = faceOffset,
                MeshIndex = mi,
                IsFoliagePath = true,
                FoliageVertexStride = stride,
            };

            buffer.SubMeshes.AddRange(subMeshes);
            info.MeshBuffers.Add(buffer);

            if (!hasClassTag && mi + 1 < info.MeshCount && !r.Skip(2))
            {
                return;
            }
        }
    }

    private static bool TryReadClassTagMesh(
        MdlReader r, uint vertexCount, uint indexCount, List<MdlSubMesh> subMeshes,
        out int vertexOffset, out int faceOffset)
    {
        vertexOffset = 0;
        faceOffset = 0;

        if (r.Position + 44 > r.Length || !r.Skip(40))
        {
            return false;
        }

        if (!r.U32(out uint subCount) || subCount == 0 || subCount > 64)
        {
            return false;
        }

        long need = ((long)subCount * 41) + ((long)vertexCount * 20) + ((long)indexCount * 2);
        if (r.Position + need > r.Length)
        {
            return false;
        }

        for (uint s = 0; s < subCount; ++s)
        {
            if (!r.U32(out _)) return false;
            if (!r.U32(out _)) return false;
            if (!r.U8(out byte material)) return false;
            if (!r.U32(out uint faceCount)) return false;
            if (!r.U32(out uint startIndex)) return false;
            if (!r.Skip(24)) return false;

            subMeshes.Add(new MdlSubMesh
            {
                FaceCount = faceCount,
                StartIndex = startIndex,
                MaterialIndex = material,
            });
        }

        vertexOffset = r.Position;
        if (!r.Skip((int)(vertexCount * 20))) return false;
        faceOffset = r.Position;
        return r.Skip((int)(indexCount * 2));
    }

    private static bool ReadCounts(MdlReader r, out uint indexCount, out uint vertexCount)
    {
        indexCount = 0;
        vertexCount = 0;

        if (!r.U32(out _) || !r.U32(out _)) return false;
        if (!r.U32(out indexCount)) return false;
        return r.U32(out vertexCount);
    }

    private static void AddEmptyFrom(MdlInfo info, uint firstMesh)
    {
        for (uint mj = firstMesh; mj < info.MeshCount; ++mj)
        {
            info.MeshBuffers.Add(new MdlMeshBuffer
            {
                MeshIndex = mj,
                IsFoliagePath = true,
                FoliageVertexStride = 0,
            });
        }
    }

    public static bool TryDecodeVertices(
        byte[] data, MdlMeshBuffer mb,
        out float[] positions, out float[] uvs, out float[]? normals)
    {
        positions = Array.Empty<float>();
        uvs = Array.Empty<float>();
        normals = null;

        int stride = mb.FoliageVertexStride == 0 ? 48 : mb.FoliageVertexStride;
        if (stride is not (20 or 36 or 48))
        {
            return false;
        }

        long vertexEnd = (long)mb.VertexOffset + ((long)mb.VertexCount * stride);
        if (vertexEnd > data.Length)
        {
            return false;
        }

        positions = new float[mb.VertexCount * 3];
        uvs = new float[mb.VertexCount * 2];

        if (stride == 48)
        {
            normals = new float[mb.VertexCount * 3];
            for (uint v = 0; v < mb.VertexCount; ++v)
            {
                int p = mb.VertexOffset + (int)(v * 48);
                for (int k = 0; k < 3; ++k)
                {
                    positions[(v * 3) + k] = ReadF32(data, p + (k * 4));
                    normals[(v * 3) + k] = ReadF32(data, p + 12 + (k * 4));
                }

                uvs[v * 2] = ReadF32(data, p + 24);
                uvs[(v * 2) + 1] = ReadF32(data, p + 28);
            }

            return true;
        }

        normals = new float[mb.VertexCount * 3];
        int unit = 0;
        for (uint v = 0; v < mb.VertexCount; ++v)
        {
            int p = mb.VertexOffset + (int)(v * stride);
            positions[v * 3] = ReadHalf(data, p);
            positions[(v * 3) + 1] = ReadHalf(data, p + 2);
            positions[(v * 3) + 2] = ReadHalf(data, p + 4);
            uvs[v * 2] = ReadHalf(data, p + 12);
            uvs[(v * 2) + 1] = ReadHalf(data, p + 14);

            var (nx, ny, nz) = MdlReader.DecodeNormal_11_11_10(MdlReader.ReadU32BE(data, p + 8));
            float len = MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
            if (len > 0.8f && len < 1.2f)
            {
                unit++;
            }

            if (len > 1e-6f)
            {
                nx /= len;
                ny /= len;
                nz /= len;
            }

            normals[v * 3] = nx;
            normals[(v * 3) + 1] = ny;
            normals[(v * 3) + 2] = nz;
        }

        if (unit < (mb.VertexCount * 9) / 10)
        {
            normals = null;
        }

        return true;
    }

    private static float ReadHalf(byte[] data, int at) =>
        MdlReader.HalfToFloat((ushort)((data[at] << 8) | data[at + 1]));

    private static float ReadF32(byte[] data, int at)
    {
        uint u = ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) |
                 ((uint)data[at + 2] << 8) | data[at + 3];
        return BitConverter.Int32BitsToSingle((int)u);
    }
}
