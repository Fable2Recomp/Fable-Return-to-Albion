using System;
using Refii.Models;

namespace Refii.Physics;

public static class Engine_ClothBuilder
{
    public const int FreeAttribute = 64;

    public static ClothAsset? Build(byte[] bytes, MdlClothBlock block, string sourcePath)
    {
        int vertexCount = (int)block.SimVertexCount;
        int edgeCount = (int)block.EdgeCount;

        if (vertexCount <= 0 || bytes.Length == 0)
        {
            return null;
        }

        var asset = new ClothAsset
        {
            SourcePath = sourcePath,
            RecordIndex = block.RecordIndex,
            SimParams = block.SimParams,
        };

        asset.BindPos = ReadPositions(bytes, block.RestPosOffset, vertexCount);
        if (asset.BindPos.Length == 0)
        {
            return null;
        }

        asset.VertexAttr = ReadBytes(bytes, block.FlagOffset, vertexCount);
        if (asset.VertexAttr.Length == 0)
        {
            asset.VertexAttr = new byte[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                asset.VertexAttr[i] = FreeAttribute;
            }
        }

        asset.Edges = ReadEdges(bytes, block.EdgePairOffset, edgeCount);
        asset.EdgeParams = ReadEdgeParams(bytes, block.EdgeParamOffset, edgeCount, asset);

        ResolveSimRange(asset);
        asset.Driver = ReadDriver(bytes, block);
        return asset;
    }

    private static void ResolveSimRange(ClothAsset asset)
    {
        int begin = -1;
        int end = 0;

        for (int i = 0; i < asset.VertexAttr.Length; i++)
        {
            if (asset.VertexAttr[i] == 0)
            {
                continue;
            }

            if (begin < 0)
            {
                begin = i;
            }

            end = i + 1;
        }

        asset.SimBegin = begin < 0 ? 0 : begin;
        asset.SimEnd = begin < 0 ? 0 : end;
    }

    private static PhysVec4[] ReadPositions(byte[] bytes, int offset, int count)
    {
        if (offset <= 0 || offset + (count * 12) > bytes.Length)
        {
            return Array.Empty<PhysVec4>();
        }

        var positions = new PhysVec4[count];
        for (int i = 0; i < count; i++)
        {
            int at = offset + (i * 12);
            positions[i] = new PhysVec4(
                ReadF32(bytes, at),
                ReadF32(bytes, at + 4),
                ReadF32(bytes, at + 8),
                1.0f);
        }

        return positions;
    }

    private static byte[] ReadBytes(byte[] bytes, int offset, int count)
    {
        if (offset <= 0 || offset + count > bytes.Length)
        {
            return Array.Empty<byte>();
        }

        var values = new byte[count];
        Buffer.BlockCopy(bytes, offset, values, 0, count);
        return values;
    }

    private static ushort[] ReadEdges(byte[] bytes, int offset, int count)
    {
        if (count <= 0 || offset <= 0 || offset + (count * 4) > bytes.Length)
        {
            return Array.Empty<ushort>();
        }

        var edges = new ushort[count * 2];
        for (int i = 0; i < count; i++)
        {
            int at = offset + (i * 4);
            edges[i * 2] = ReadU16(bytes, at);
            edges[(i * 2) + 1] = ReadU16(bytes, at + 2);
        }

        return edges;
    }

    private static PhysVec4[] ReadEdgeParams(
        byte[] bytes, int offset, int count, ClothAsset asset)
    {
        if (count <= 0 || offset <= 0 || offset + (count * 16) > bytes.Length)
        {
            return Array.Empty<PhysVec4>();
        }

        var parameters = new PhysVec4[count];
        for (int i = 0; i < count; i++)
        {
            int at = offset + (i * 16);
            parameters[i] = new PhysVec4(
                ReadF32(bytes, at),
                ReadF32(bytes, at + 4),
                ReadF32(bytes, at + 8),
                ReadF32(bytes, at + 12));
        }

        Sanitise(parameters, asset);
        return parameters;
    }

    private static void Sanitise(PhysVec4[] parameters, ClothAsset asset)
    {
        for (int i = 0; i < parameters.Length; i++)
        {
            PhysVec4 p = parameters[i];

            if (!float.IsFinite(p.X) || p.X < 0.0f)
            {
                p.X = RestLengthOf(asset, i);
            }

            if (!float.IsFinite(p.Y) || p.Y <= 0.0f || p.Y > 1.0f)
            {
                p.Y = 1.0f;
            }

            if (!float.IsFinite(p.Z) || p.Z < 0.0f)
            {
                p.Z = 0.5f;
            }

            if (!float.IsFinite(p.W) || p.W < 0.0f)
            {
                p.W = 0.5f;
            }

            parameters[i] = p;
        }
    }

    private static float RestLengthOf(ClothAsset asset, int edge)
    {
        int ia = edge * 2;
        int ib = ia + 1;

        if (ib >= asset.Edges.Length)
        {
            return 0.0f;
        }

        int a = asset.Edges[ia];
        int b = asset.Edges[ib];

        if (a >= asset.BindPos.Length || b >= asset.BindPos.Length)
        {
            return 0.0f;
        }

        PhysVec4 d = PhysVec4.Sub(asset.BindPos[b], asset.BindPos[a]);
        return MathF.Sqrt(PhysVec4.MSum3(d, d).X);
    }

    private static ClothSkinDriver? ReadDriver(byte[] bytes, MdlClothBlock block)
    {
        int count = (int)block.BoneVertexCount;
        int stride = (int)block.BonesPerVertex;

        if (count <= 0 || stride <= 0)
        {
            return null;
        }

        int indexBytes = count * stride;
        if (block.BoneIdOffset <= 0 || block.BoneIdOffset + indexBytes > bytes.Length)
        {
            return null;
        }

        if (block.WeightOffset <= 0 || block.WeightOffset + (count * 16) > bytes.Length)
        {
            return null;
        }

        var driver = new ClothSkinDriver
        {
            Count = count,
            IndexStride = stride,
            BoneIndices = new byte[indexBytes],
            Weights = new PhysVec4[count],
        };

        Buffer.BlockCopy(bytes, block.BoneIdOffset, driver.BoneIndices, 0, indexBytes);

        for (int i = 0; i < count; i++)
        {
            int at = block.WeightOffset + (i * 16);
            driver.Weights[i] = new PhysVec4(
                ReadF32(bytes, at),
                ReadF32(bytes, at + 4),
                ReadF32(bytes, at + 8),
                ReadF32(bytes, at + 12));
        }

        return driver;
    }

    private static ushort ReadU16(byte[] data, int at) =>
        (ushort)(((uint)data[at] << 8) | data[at + 1]);

    private static uint ReadU32(byte[] data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) |
        ((uint)data[at + 2] << 8) | data[at + 3];

    private static float ReadF32(byte[] data, int at) =>
        BitConverter.Int32BitsToSingle(unchecked((int)ReadU32(data, at)));
}
