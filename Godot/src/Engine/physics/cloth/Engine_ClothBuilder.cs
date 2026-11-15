using System;
using Refii.Models;

namespace Refii.Physics;

/// <summary>
/// Reads one cloth block into a <see cref="ClothAsset"/>, table for table as the guest's
/// <c>sub_82B36E50</c> stores them. Values are taken as stated; only a non-finite float is
/// replaced, because the guest itself does no validation.
///
/// EVERYTHING SPATIAL IS MAPPED INTO THE MESH'S SPACE. The mesh reader bakes the file's
/// Z-up into Godot's Y-up with (x, y, z) → (x, z, -y) (<c>Engine_MdlGeometry.ConvertZUpToYUp</c>)
/// and the rig is posed in that space, so the block's rest positions, collider centres, axes,
/// normals and box matrices take the same map here. Left in file space they drew every cloth
/// piece turned a quarter turn about X from the body it stands in for.
/// </summary>
public static class Engine_ClothBuilder
{
    public const int NoBone = -1;

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
            SimBegin = Math.Clamp(block.SimBegin, 0, vertexCount),
            SimEnd = Math.Clamp(block.SimEnd, 0, vertexCount),
            AttrScale = Finite(block.SimParams[0], 1.0f),
            Damping = Finite(block.SimParams[2], 0.2f),
            WindStrength = Finite(block.SimParams[3], 1.0f),
            GravityStrength = Finite(block.SimParams[4], 1.0f),
            Inertia = Finite(block.SimParams[5], 1.0f),
            FollowRotation = block.FollowByte1 != 0,
            RetainsPerStep = block.RetainsPerStep,
        };

        asset.BindPos = ReadPositions(bytes, block.RestPosOffset, vertexCount);
        if (asset.BindPos.Length == 0)
        {
            return null;
        }

        asset.VertexAttr = ReadBytes(bytes, block.FlagOffset, vertexCount);
        if (asset.VertexAttr.Length == 0)
        {
            return null;
        }

        asset.Edges = ReadU16s(bytes, block.EdgePairOffset, edgeCount * 2);
        asset.EdgeParams = ReadVec4s(bytes, block.EdgeParamOffset, edgeCount);
        asset.Indices = ReadU16s(bytes, block.IndexListOffset, (int)block.IndexCount);
        asset.Uvs = ReadF32s(bytes, block.UvOffset, vertexCount * 2);
        asset.TangentW = ReadTangentW(bytes, block.TangentWOffset, vertexCount);
        asset.Weld = ReadU32s(bytes, block.WeldOffset, (int)block.WeldCount * 2);
        asset.Driver = ReadDriver(bytes, block);
        asset.Spheres = ReadSpheres(bytes, block);
        asset.Planes = ReadPlanes(bytes, block);
        asset.Capsules = ReadCapsules(bytes, block);
        asset.Boxes = ReadBoxes(bytes, block);

        for (int i = 0; i < asset.BindPos.Length; i++)
        {
            asset.BindPos[i] = ToMeshSpace(asset.BindPos[i]);
        }

        for (int i = 0; i < asset.Spheres.Length; i++)
        {
            asset.Spheres[i].Center = ToMeshSpace(asset.Spheres[i].Center);
            asset.Spheres[i].LocalCenter = ToMeshSpace(asset.Spheres[i].LocalCenter);
        }

        for (int i = 0; i < asset.Planes.Length; i++)
        {
            asset.Planes[i].Point = ToMeshSpace(asset.Planes[i].Point);
            asset.Planes[i].Normal = ToMeshSpace(asset.Planes[i].Normal);
            asset.Planes[i].LocalPoint = ToMeshSpace(asset.Planes[i].LocalPoint);
            asset.Planes[i].LocalNormal = ToMeshSpace(asset.Planes[i].LocalNormal);
        }

        for (int i = 0; i < asset.Capsules.Length; i++)
        {
            asset.Capsules[i].P0 = ToMeshSpace(asset.Capsules[i].P0);
            asset.Capsules[i].Axis = ToMeshSpace(asset.Capsules[i].Axis);
            asset.Capsules[i].LocalP0 = ToMeshSpace(asset.Capsules[i].LocalP0);
            asset.Capsules[i].LocalAxis = ToMeshSpace(asset.Capsules[i].LocalAxis);
        }

        for (int i = 0; i < asset.Boxes.Length; i++)
        {
            asset.Boxes[i].LocalToWorld = ToMeshSpace(asset.Boxes[i].LocalToWorld);
            asset.Boxes[i].WorldToLocal = ToMeshSpace(asset.Boxes[i].WorldToLocal);
            asset.Boxes[i].LocalMatrix = ToMeshSpace(asset.Boxes[i].LocalMatrix);
        }

        for (int i = 0; i < asset.EdgeParams.Length; i++)
        {
            PhysVec4 p = asset.EdgeParams[i];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) || !float.IsFinite(p.W))
            {
                asset.EdgeParams[i] = new PhysVec4(RestLengthOf(asset, i), 1.0f, 0.5f, 0.5f);
            }
        }

        return asset;
    }

    private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;

    /// <summary>The mesh reader's map, (x, y, z) → (x, z, -y); w is kept.</summary>
    public static PhysVec4 ToMeshSpace(in PhysVec4 v) => new(v.X, v.Z, -v.Y, v.W);

    /// <summary>
    /// A row-vector transform conjugated into the mesh's space: with the map as the row-vector
    /// matrix C (p' = p C), M' = C⁻¹ M C, which for this permutation is applying the map to
    /// every row and then to every column of the 3x3 and to the translation row.
    /// </summary>
    public static PhysMat4 ToMeshSpace(in PhysMat4 m)
    {
        // Rows of C⁻¹ M: the map reorders the rows the way it reorders coordinates.
        PhysVec4 r0 = m.R0;
        PhysVec4 r1 = m.R2;
        PhysVec4 r2 = PhysVec4.Mul(m.R1, PhysVec4.Splat(-1.0f));
        PhysVec4 r3 = m.R3;
        r2.W = m.R1.W;

        var result = default(PhysMat4);
        result.R0 = ToMeshSpace(r0);
        result.R1 = ToMeshSpace(r1);
        result.R2 = ToMeshSpace(r2);
        result.R3 = ToMeshSpace(r3);
        return result;
    }

    private static bool Fits(byte[] bytes, int offset, int size) =>
        size >= 0 && offset > 0 && offset + size <= bytes.Length;

    private static PhysVec4[] ReadPositions(byte[] bytes, int offset, int count)
    {
        if (!Fits(bytes, offset, count * 12))
        {
            return Array.Empty<PhysVec4>();
        }

        var positions = new PhysVec4[count];
        for (int i = 0; i < count; i++)
        {
            positions[i] = Vec3(bytes, offset + (i * 12), 1.0f);
        }

        return positions;
    }

    private static byte[] ReadBytes(byte[] bytes, int offset, int count)
    {
        if (!Fits(bytes, offset, count))
        {
            return Array.Empty<byte>();
        }

        var values = new byte[count];
        Buffer.BlockCopy(bytes, offset, values, 0, count);
        return values;
    }

    private static ushort[] ReadU16s(byte[] bytes, int offset, int count)
    {
        if (count <= 0 || !Fits(bytes, offset, count * 2))
        {
            return Array.Empty<ushort>();
        }

        var values = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = ReadU16(bytes, offset + (i * 2));
        }

        return values;
    }

    private static uint[] ReadU32s(byte[] bytes, int offset, int count)
    {
        if (count <= 0 || !Fits(bytes, offset, count * 4))
        {
            return Array.Empty<uint>();
        }

        var values = new uint[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = ReadU32(bytes, offset + (i * 4));
        }

        return values;
    }

    private static float[] ReadF32s(byte[] bytes, int offset, int count)
    {
        if (count <= 0 || !Fits(bytes, offset, count * 4))
        {
            return Array.Empty<float>();
        }

        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            float v = ReadF32(bytes, offset + (i * 4));
            values[i] = float.IsFinite(v) ? v : 0.0f;
        }

        return values;
    }

    /// <summary>The w lane of each Data+120 float4, ±1 for a handedness, else +1.</summary>
    private static float[] ReadTangentW(byte[] bytes, int offset, int count)
    {
        var values = new float[count];
        Array.Fill(values, 1.0f);
        if (count <= 0 || !Fits(bytes, offset, count * 16))
        {
            return values;
        }

        for (int i = 0; i < count; i++)
        {
            float w = ReadF32(bytes, offset + (i * 16) + 12);
            values[i] = w < 0.0f ? -1.0f : 1.0f;
        }

        return values;
    }

    private static PhysVec4[] ReadVec4s(byte[] bytes, int offset, int count)
    {
        if (count <= 0 || !Fits(bytes, offset, count * 16))
        {
            return Array.Empty<PhysVec4>();
        }

        var values = new PhysVec4[count];
        for (int i = 0; i < count; i++)
        {
            int at = offset + (i * 16);
            values[i] = new PhysVec4(
                ReadF32(bytes, at), ReadF32(bytes, at + 4), ReadF32(bytes, at + 8), ReadF32(bytes, at + 12));
        }

        return values;
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

        if (count <= 0 || stride <= 0 ||
            !Fits(bytes, block.BoneIdOffset, count * stride) ||
            !Fits(bytes, block.WeightOffset, count * 16))
        {
            return null;
        }

        var driver = new ClothSkinDriver
        {
            Count = count,
            IndexStride = stride,
            BoneIndices = ReadBytes(bytes, block.BoneIdOffset, count * stride),
            Weights = ReadVec4s(bytes, block.WeightOffset, count),
        };

        return driver;
    }

    /// <summary>32 bytes: initial centre, bone-local centre, radius, bone.</summary>
    private static SphereCollider[] ReadSpheres(byte[] bytes, MdlClothBlock block)
    {
        int count = (int)block.SphereCount;
        int size = MdlClothBlock.SphereRecordSize;
        if (count <= 0 || !Fits(bytes, block.SphereOffset, count * size))
        {
            return Array.Empty<SphereCollider>();
        }

        var records = new SphereCollider[count];
        for (int i = 0; i < count; i++)
        {
            int at = block.SphereOffset + (i * size);
            records[i] = new SphereCollider
            {
                Center = Vec3(bytes, at, 0.0f),
                LocalCenter = Vec3(bytes, at + 12, 0.0f),
                Radius = ReadF32(bytes, at + 24),
                BoneIndex = unchecked((int)ReadU32(bytes, at + 28)),
            };
        }

        return records;
    }

    /// <summary>52 bytes: initial point, initial normal, bone-local point, bone-local normal, bone.</summary>
    private static PlaneCollider[] ReadPlanes(byte[] bytes, MdlClothBlock block)
    {
        int count = (int)block.PlaneCount;
        int size = MdlClothBlock.PlaneRecordSize;
        if (count <= 0 || !Fits(bytes, block.PlaneOffset, count * size))
        {
            return Array.Empty<PlaneCollider>();
        }

        var records = new PlaneCollider[count];
        for (int i = 0; i < count; i++)
        {
            int at = block.PlaneOffset + (i * size);
            records[i] = new PlaneCollider
            {
                Point = Vec3(bytes, at, 0.0f),
                Normal = Vec3(bytes, at + 12, 0.0f),
                LocalPoint = Vec3(bytes, at + 24, 0.0f),
                LocalNormal = Vec3(bytes, at + 36, 0.0f),
                BoneIndex = unchecked((int)ReadU32(bytes, at + 48)),
            };
        }

        return records;
    }

    /// <summary>56 bytes: initial start, initial axis, bone-local start, bone-local axis, radius, bone.</summary>
    private static CapsuleCollider[] ReadCapsules(byte[] bytes, MdlClothBlock block)
    {
        int count = (int)block.CapsuleCount;
        int size = MdlClothBlock.CapsuleRecordSize;
        if (count <= 0 || !Fits(bytes, block.CapsuleOffset, count * size))
        {
            return Array.Empty<CapsuleCollider>();
        }

        var records = new CapsuleCollider[count];
        for (int i = 0; i < count; i++)
        {
            int at = block.CapsuleOffset + (i * size);
            records[i] = new CapsuleCollider
            {
                P0 = Vec3(bytes, at, 0.0f),
                Axis = Vec3(bytes, at + 12, 0.0f),
                LocalP0 = Vec3(bytes, at + 24, 0.0f),
                LocalAxis = Vec3(bytes, at + 36, 0.0f),
                Radius = ReadF32(bytes, at + 48),
                BoneIndex = unchecked((int)ReadU32(bytes, at + 52)),
            };
        }

        return records;
    }

    /// <summary>148 bytes: three 3x4 matrices — initial world, its inverse, bone-local — then the bone.</summary>
    private static BoxCollider[] ReadBoxes(byte[] bytes, MdlClothBlock block)
    {
        int count = (int)block.BoxCount;
        int size = MdlClothBlock.BoxRecordSize;
        if (count <= 0 || !Fits(bytes, block.BoxOffset, count * size))
        {
            return Array.Empty<BoxCollider>();
        }

        Span<float> f = stackalloc float[12];
        var records = new BoxCollider[count];
        for (int i = 0; i < count; i++)
        {
            int at = block.BoxOffset + (i * size);
            var box = new BoxCollider { BoneIndex = unchecked((int)ReadU32(bytes, at + 144)) };
            box.LocalToWorld = Engine_PhysicsMatrix.FromFile3x4(Floats(bytes, at, f));
            box.WorldToLocal = Engine_PhysicsMatrix.FromFile3x4(Floats(bytes, at + 48, f));
            box.LocalMatrix = Engine_PhysicsMatrix.FromFile3x4(Floats(bytes, at + 96, f));
            records[i] = box;
        }

        return records;
    }

    private static ReadOnlySpan<float> Floats(byte[] bytes, int at, Span<float> into)
    {
        for (int k = 0; k < into.Length; k++)
        {
            into[k] = ReadF32(bytes, at + (k * 4));
        }

        return into;
    }

    private static PhysVec4 Vec3(byte[] bytes, int at, float w) =>
        new(ReadF32(bytes, at), ReadF32(bytes, at + 4), ReadF32(bytes, at + 8), w);

    private static ushort ReadU16(byte[] data, int at) =>
        (ushort)(((uint)data[at] << 8) | data[at + 1]);

    private static uint ReadU32(byte[] data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) |
        ((uint)data[at + 2] << 8) | data[at + 3];

    private static float ReadF32(byte[] data, int at) =>
        BitConverter.Int32BitsToSingle(unchecked((int)ReadU32(data, at)));
}
