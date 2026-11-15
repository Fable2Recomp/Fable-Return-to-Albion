using System;

namespace Refii.Physics;

/// <summary>The cloth block's skin driver: per sim vertex, bone ids and a float4 of weights.</summary>
public sealed class ClothSkinDriver
{
    public int Count { get; set; }

    public int IndexStride { get; set; }

    public byte[] BoneIndices { get; set; } = Array.Empty<byte>();

    public PhysVec4[] Weights { get; set; } = Array.Empty<PhysVec4>();
}

/// <summary>
/// Collider records as the guest holds them at runtime. <c>Center</c>/<c>Point</c>/<c>P0</c>
/// and the box's <c>LocalToWorld</c> are the posed values; the <c>Local*</c> fields are what the
/// file states relative to <c>BoneIndex</c>, re-posed every step. A bone index of -1 leaves the
/// file's initial value in place.
/// </summary>
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

/// <summary>
/// One cloth piece as the guest's cloth definition holds it: the sim mesh's own vertex set, the
/// distance-constraint edges, the skin driver, the colliders, the welds and the per-item
/// parameters. Immutable once built; a <see cref="ClothInstance"/> holds the moving state.
/// </summary>
public sealed class ClothAsset
{
    /// <summary>First vertex the solver integrates and collides (Data+28).</summary>
    public int SimBegin { get; set; }

    /// <summary>End, exclusive, of that range (Data+32).</summary>
    public int SimEnd { get; set; }

    /// <summary>u16 pairs, one per edge.</summary>
    public ushort[] Edges { get; set; } = Array.Empty<ushort>();

    /// <summary>Per edge: rest length, stiffness, weight of endpoint A, weight of endpoint B.</summary>
    public PhysVec4[] EdgeParams { get; set; } = Array.Empty<PhysVec4>();

    /// <summary>
    /// Per vertex 0..64: indexes the force LUT (<c>attr * scale / 64</c>) and the skin-retention
    /// LUT (<c>pow(attr * scale / 64, dt)</c>). 0 is pinned to the skinned pose, 64 is free.
    /// </summary>
    public byte[] VertexAttr { get; set; } = Array.Empty<byte>();

    /// <summary>Rest positions (Data+104), the skin driver's input; w = 1.</summary>
    public PhysVec4[] BindPos { get; set; } = Array.Empty<PhysVec4>();

    /// <summary>The sim mesh's own u16 triangle list (Data+40); the normals are built from it.</summary>
    public ushort[] Indices { get; set; } = Array.Empty<ushort>();

    /// <summary>The sim mesh's own UVs (Data+136), two floats per vertex.</summary>
    public float[] Uvs { get; set; } = Array.Empty<float>();

    /// <summary>
    /// Per vertex, the float4 the guest adds to its recomputed tangent (Data+120); the w lane is
    /// the handedness the computed tangent lacks.
    /// </summary>
    public float[] TangentW { get; set; } = Array.Empty<float>();

    /// <summary>(src, dst) pairs copied after each solve so seam duplicates stay coincident.</summary>
    public uint[] Weld { get; set; } = Array.Empty<uint>();

    public ClothSkinDriver? Driver { get; set; }

    public SphereCollider[] Spheres { get; set; } = Array.Empty<SphereCollider>();

    public PlaneCollider[] Planes { get; set; } = Array.Empty<PlaneCollider>();

    public CapsuleCollider[] Capsules { get; set; } = Array.Empty<CapsuleCollider>();

    public BoxCollider[] Boxes { get; set; } = Array.Empty<BoxCollider>();

    /// <summary>Scale on the attribute byte before the LUTs (Data+244, default 1).</summary>
    public float AttrScale { get; set; } = 1.0f;

    /// <summary>Velocity damping, applied as <c>pow(1 - damping, dt * 30)</c> (Data+252, default 0.2).</summary>
    public float Damping { get; set; } = 0.2f;

    /// <summary>Multiplier on the wind acceleration (Data+256, default 1).</summary>
    public float WindStrength { get; set; } = 1.0f;

    /// <summary>Multiplier on the gravity acceleration (Data+260, default 1).</summary>
    public float GravityStrength { get; set; } = 1.0f;

    /// <summary>Fraction of the root's frame rotation carried into the cloth (Data+264, default 1).</summary>
    public float Inertia { get; set; } = 1.0f;

    /// <summary>
    /// Data+269: the root-motion follow keeps the rotation of the frame delta. Clear, the guest
    /// rewrites the delta to the identity (<c>sub_82B4FD98</c>) and the pass moves nothing; the
    /// translation row is discarded either way, so Data+268 on its own does nothing.
    /// </summary>
    public bool FollowRotation { get; set; } = true;

    /// <summary>
    /// The skin-retention weight is the attribute alone, <c>attr / 64</c> per guest step, not
    /// <c>pow(attr * scale / 64, dt)</c>. Set for a block read by Fable 3's engine
    /// (<see cref="MdlClothBlock.RetainsPerStep"/>); see <see cref="Engine_ClothSolver.UpdateTimeLuts"/>.
    /// </summary>
    public bool RetainsPerStep { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public uint RecordIndex { get; set; }

    public int VertexCount => BindPos.Length;

    public int EdgeCount => Edges.Length / 2;

    public int WeldCount => Weld.Length / 2;

    public int TriangleCount => Indices.Length / 3;

    public bool Simulates => VertexCount != 0 && EdgeCount != 0 && SimEnd > SimBegin;

    /// <summary>The highest bone id any driver entry or collider names, or -1 when none does.</summary>
    public int MaxBoneIndex()
    {
        int max = -1;
        if (Driver is not null)
        {
            foreach (byte id in Driver.BoneIndices)
            {
                max = Math.Max(max, id);
            }
        }

        foreach (SphereCollider c in Spheres)
        {
            max = Math.Max(max, c.BoneIndex);
        }

        foreach (PlaneCollider c in Planes)
        {
            max = Math.Max(max, c.BoneIndex);
        }

        foreach (CapsuleCollider c in Capsules)
        {
            max = Math.Max(max, c.BoneIndex);
        }

        foreach (BoxCollider c in Boxes)
        {
            max = Math.Max(max, c.BoneIndex);
        }

        return max;
    }
}
