using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Refii.Models;

public sealed class MdlReader
{
    private readonly byte[] _data;

    public MdlReader(byte[] data)
    {
        _data = data;
    }

    public int Position { get; set; }

    public int Length => _data.Length;

    public byte this[int index] => _data[index];

    public bool Need(int count) => Position + count <= _data.Length;

    public bool U8(out byte value)
    {
        value = 0;
        if (!Need(1)) return false;
        value = _data[Position++];
        return true;
    }

    public bool U16(out ushort value)
    {
        value = 0;
        if (!Need(2)) return false;
        value = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(Position));
        Position += 2;
        return true;
    }

    public bool U32(out uint value)
    {
        value = 0;
        if (!Need(4)) return false;
        value = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(Position));
        Position += 4;
        return true;
    }

    public bool F32(out float value)
    {
        value = 0f;
        if (!U32(out uint raw)) return false;
        value = BitConverter.Int32BitsToSingle(unchecked((int)raw));
        return true;
    }

    public bool Strz(out string value, int maxLength = 8192)
    {
        var sb = new StringBuilder();
        int limit = Math.Min(_data.Length, Position + maxLength);
        while (Position < limit)
        {
            char c = (char)_data[Position++];
            if (c == 0)
            {
                value = sb.ToString();
                return true;
            }

            sb.Append(c);
        }

        value = sb.ToString();
        return true;
    }

    public bool Skip(int count)
    {
        if (!Need(count)) return false;
        Position += count;
        return true;
    }

    public static float HalfToFloat(ushort h)
    {
        uint s = (uint)((h >> 15) & 1);
        uint e = (uint)((h >> 10) & 0x1F);
        uint f = (uint)(h & 0x3FF);
        uint bigE;
        uint bigF;

        if (e == 0)
        {
            if (f == 0)
            {
                bigE = 0;
                bigF = 0;
            }
            else
            {
                int t = 0;
                while ((f & 0x400) == 0)
                {
                    f <<= 1;
                    ++t;
                }

                f &= 0x3FF;
                bigE = (uint)(127 - 15 - t);
                bigF = f << 13;
            }
        }
        else if (e == 31)
        {
            bigE = 255;
            bigF = f != 0 ? 0x7FFFFFu : 0u;
        }
        else
        {
            bigE = e + (127 - 15);
            bigF = f << 13;
        }

        uint u = (s << 31) | (bigE << 23) | bigF;
        return BitConverter.Int32BitsToSingle(unchecked((int)u));
    }

    public static void BuildTrianglesFromStrip(IReadOnlyList<ushort> strip, List<uint> indices)
    {
        indices.Clear();
        if (strip.Count < 3) return;

        const ushort restart = 0xFFFF;
        bool wind = false;
        ushort a = strip[0];
        ushort b = strip[1];

        for (int i = 2; i < strip.Count; ++i)
        {
            ushort c = strip[i];
            if (a == restart || b == restart || c == restart)
            {
                int j = i + 1;
                while (j < strip.Count && strip[j] == restart) ++j;
                if (j + 1 < strip.Count)
                {
                    a = strip[j];
                    b = strip[j + 1];
                    i = j + 1;
                    wind = false;
                    continue;
                }

                break;
            }

            if (a != b && b != c && c != a)
            {
                if (!wind)
                {
                    indices.Add(a);
                    indices.Add(b);
                    indices.Add(c);
                }
                else
                {
                    indices.Add(b);
                    indices.Add(a);
                    indices.Add(c);
                }
            }

            a = b;
            b = c;
            wind = !wind;
        }
    }

    public static (float x, float y, float z) DecodeNormal_11_11_10(uint packed)
    {
        int ix = (int)(packed >> 21);
        if ((ix & 0x400) != 0) ix -= 0x800;
        int iy = (int)((packed >> 10) & 0x7FF);
        if ((iy & 0x400) != 0) iy -= 0x800;
        int iz = (int)(packed & 0x3FF);
        if ((iz & 0x200) != 0) iz -= 0x400;
        return (ix / 1023f, iy / 1023f, iz / 511f);
    }

    public static (float x, float y, float z, float w) DecodeTangent_DEC3N(uint packed)
    {
        uint iw = (packed >> 30) & 0x3;
        int ix = (int)((packed >> 20) & 0x3FF);
        if ((ix & 0x200) != 0) ix -= 0x400;
        int iy = (int)((packed >> 10) & 0x3FF);
        if ((iy & 0x200) != 0) iy -= 0x400;
        int iz = (int)(packed & 0x3FF);
        if ((iz & 0x200) != 0) iz -= 0x400;
        float w = (iw == 1 || iw == 0) ? 1f : -1f;
        return (ix / 511f, iy / 511f, iz / 511f, w);
    }

    public static uint ReadU32BE(byte[] data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) |
        ((uint)data[at + 2] << 8) | data[at + 3];

    public static float[] ComputeSmoothNormals(int vertexCount, List<uint> indices, float[] positions)
    {
        var normals = new float[vertexCount * 3];

        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            uint ia = indices[i], ib = indices[i + 1], ic = indices[i + 2];
            if (ia * 3 + 2 >= positions.Length ||
                ib * 3 + 2 >= positions.Length ||
                ic * 3 + 2 >= positions.Length)
            {
                continue;
            }

            float ax = positions[ia * 3], ay = positions[ia * 3 + 1], az = positions[ia * 3 + 2];
            float bx = positions[ib * 3], by = positions[ib * 3 + 1], bz = positions[ib * 3 + 2];
            float cx = positions[ic * 3], cy = positions[ic * 3 + 1], cz = positions[ic * 3 + 2];

            float ux = bx - ax, uy = by - ay, uz = bz - az;
            float vx = cx - ax, vy = cy - ay, vz = cz - az;
            float nx = uy * vz - uz * vy;
            float ny = uz * vx - ux * vz;
            float nz = ux * vy - uy * vx;

            normals[ia * 3] += nx; normals[ia * 3 + 1] += ny; normals[ia * 3 + 2] += nz;
            normals[ib * 3] += nx; normals[ib * 3 + 1] += ny; normals[ib * 3 + 2] += nz;
            normals[ic * 3] += nx; normals[ic * 3 + 1] += ny; normals[ic * 3 + 2] += nz;
        }

        for (int v = 0; v < vertexCount; ++v)
        {
            float x = normals[v * 3], y = normals[v * 3 + 1], z = normals[v * 3 + 2];
            float len = MathF.Sqrt(x * x + y * y + z * z);
            if (len > 1e-6f)
            {
                normals[v * 3] = x / len;
                normals[v * 3 + 1] = y / len;
                normals[v * 3 + 2] = z / len;
            }
            else
            {
                normals[v * 3] = 0f;
                normals[v * 3 + 1] = 1f;
                normals[v * 3 + 2] = 0f;
            }
        }

        if (vertexCount >= 8)
        {
            double ax = 0, ay = 0, az = 0;
            for (int v = 0; v < vertexCount; ++v)
            {
                ax += positions[v * 3];
                ay += positions[v * 3 + 1];
                az += positions[v * 3 + 2];
            }

            float cx = (float)(ax / vertexCount);
            float cy = (float)(ay / vertexCount);
            float cz = (float)(az / vertexCount);

            int outward = 0, inward = 0;
            for (int v = 0; v < vertexCount; ++v)
            {
                float dx = positions[v * 3] - cx;
                float dy = positions[v * 3 + 1] - cy;
                float dz = positions[v * 3 + 2] - cz;
                if (dx * dx + dy * dy + dz * dz < 1e-12f) continue;
                float d = dx * normals[v * 3] + dy * normals[v * 3 + 1] + dz * normals[v * 3 + 2];
                if (d > 1e-6f) ++outward;
                else if (d < -1e-6f) ++inward;
            }

            int voted = outward + inward;
            if (voted >= 8 && inward * 5 >= voted * 4)
            {
                for (int i = 0; i < normals.Length; ++i)
                {
                    normals[i] = -normals[i];
                }
            }
        }

        return normals;
    }
}
