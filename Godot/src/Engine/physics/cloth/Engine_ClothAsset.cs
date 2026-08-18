using System;

namespace Refii.Physics;

public sealed class ClothSkinDriver
{
    public int Count { get; set; }

    public int IndexStride { get; set; }

    public byte[] BoneIndices { get; set; } = Array.Empty<byte>();

    public PhysVec4[] Weights { get; set; } = Array.Empty<PhysVec4>();
}

public struct SphereCollider
{
    public PhysVec4 Center;
    public PhysVec4 LocalCenter;
    public float Radius;
    public int BoneIndex;
}

public struct PlaneCollider
{
    public PhysVec4 Point;
    public PhysVec4 Normal;
    public PhysVec4 LocalPoint;
    public PhysVec4 LocalNormal;
    public int BoneIndex;
}

public struct CapsuleCollider
{
    public PhysVec4 P0;
    public PhysVec4 Axis;
    public PhysVec4 LocalP0;
    public PhysVec4 LocalAxis;
    public float Radius;
    public int BoneIndex;
}

public struct BoxCollider
{
    public PhysMat4 LocalToWorld;
    public PhysMat4 WorldToLocal;
    public PhysMat4 LocalMatrix;
    public int BoneIndex;
}

public sealed class ClothAsset
{
    public int SimBegin { get; set; }

    public int SimEnd { get; set; }

    public ushort[] Edges { get; set; } = Array.Empty<ushort>();

    public PhysVec4[] EdgeParams { get; set; } = Array.Empty<PhysVec4>();

    public byte[] VertexAttr { get; set; } = Array.Empty<byte>();

    public PhysVec4[] BindPos { get; set; } = Array.Empty<PhysVec4>();

    public uint[] Weld { get; set; } = Array.Empty<uint>();

    public ClothSkinDriver? Driver { get; set; }

    public SphereCollider[] Spheres { get; set; } = Array.Empty<SphereCollider>();

    public PlaneCollider[] Planes { get; set; } = Array.Empty<PlaneCollider>();

    public CapsuleCollider[] Capsules { get; set; } = Array.Empty<CapsuleCollider>();

    public BoxCollider[] Boxes { get; set; } = Array.Empty<BoxCollider>();

    public float[] SimParams { get; set; } = new float[6];

    public string SourcePath { get; set; } = string.Empty;

    public uint RecordIndex { get; set; }

    public int VertexCount => BindPos.Length;

    public int EdgeCount => Edges.Length / 2;

    public int WeldCount => Weld.Length / 2;

    public bool Simulates => VertexCount != 0 && EdgeCount != 0 && SimEnd > SimBegin;
}
