using System;

namespace Refii.Physics;

public sealed class ClothInstance
{
    public const float DefaultGravityZ = -9.8f;

    public const float DefaultDamping = 0.2f;

    public const float CtorField174 = 25.0f;

    public ClothAsset? Asset { get; set; }

    public PhysVec4[] PosPrev { get; set; } = Array.Empty<PhysVec4>();

    public PhysVec4[] PosCur { get; set; } = Array.Empty<PhysVec4>();

    public PhysVec4[] SkinTarget { get; set; } = Array.Empty<PhysVec4>();

    public PhysVec4[] VertexNormals { get; set; } = Array.Empty<PhysVec4>();

    public PhysVec4[] CollidedFlags { get; set; } = Array.Empty<PhysVec4>();

    public PhysVec4[] BoneMatrices { get; set; } = Array.Empty<PhysVec4>();

    public SphereCollider[] Spheres { get; set; } = Array.Empty<SphereCollider>();

    public PlaneCollider[] Planes { get; set; } = Array.Empty<PlaneCollider>();

    public CapsuleCollider[] Capsules { get; set; } = Array.Empty<CapsuleCollider>();

    public BoxCollider[] Boxes { get; set; } = Array.Empty<BoxCollider>();

    public PhysMat4 XformPrev;

    public PhysMat4 XformCur;

    public float PrevDt { get; set; }

    public PhysVec4 WindDirWorld;

    public PhysVec4 GravityDirWorld;

    public float AttrScale { get; set; } = 1.0f;

    public float Field174 { get; set; } = CtorField174;

    public float Damping { get; set; } = DefaultDamping;

    public float WindStrength { get; set; } = 1.0f;

    public float GravityStrength { get; set; } = 1.0f;

    public float InertiaStrength { get; set; } = 1.0f;

    public bool AnchorEnabled { get; set; } = true;

    public bool WindEnabled { get; set; } = true;

    public PhysVec4 BoundsSphere;

    public PhysVec4 BoundsMin;

    public PhysVec4 BoundsMax;

    public int[] BoneRemap { get; set; } = Array.Empty<int>();

    public PhysMat4[] BonePoses { get; set; } = Array.Empty<PhysMat4>();

    public int RelaxIterations { get; set; }

    public float Residual { get; set; }

    public static ClothInstance Create()
    {
        var instance = new ClothInstance
        {
            WindDirWorld = PhysVec4.Zero,
            GravityDirWorld = new PhysVec4(0.0f, 0.0f, DefaultGravityZ, 0.0f),
            BoundsSphere = PhysVec4.Zero,
            PrevDt = 0.0f,
        };

        Engine_PhysicsMatrix.LoadIdentity(ref instance.XformPrev);
        Engine_PhysicsMatrix.LoadIdentity(ref instance.XformCur);
        return instance;
    }

    public void Bind(ClothAsset asset)
    {
        Asset = asset;

        int count = asset.VertexCount;
        PosPrev = new PhysVec4[count];
        PosCur = new PhysVec4[count];
        SkinTarget = new PhysVec4[count];
        VertexNormals = new PhysVec4[count];
        CollidedFlags = new PhysVec4[count];

        Spheres = (SphereCollider[])asset.Spheres.Clone();
        Planes = (PlaneCollider[])asset.Planes.Clone();
        Capsules = (CapsuleCollider[])asset.Capsules.Clone();
        Boxes = (BoxCollider[])asset.Boxes.Clone();

        ResetToSkinned();
    }

    public void ResetToSkinned()
    {
        if (Asset is null)
        {
            return;
        }

        PhysVec4[] seed = Asset.BindPos;

        if (Asset.Driver is not null && Asset.Driver.IndexStride > 0 && BoneMatrices.Length != 0)
        {
            Engine_ClothSolver.SkinDriverPositions(
                Asset.Driver, SkinTarget, Asset.BindPos, BoneMatrices);
            seed = SkinTarget;
        }

        for (int i = 0; i < PosCur.Length && i < seed.Length; i++)
        {
            PhysVec4 p = PhysVec4.KeepXyzTakeW(seed[i], PhysVec4.One);
            PosCur[i] = p;
            PosPrev[i] = p;
            SkinTarget[i] = p;
        }

        Array.Clear(CollidedFlags, 0, CollidedFlags.Length);
        PrevDt = 0.0f;
        XformPrev = XformCur;
    }
}
