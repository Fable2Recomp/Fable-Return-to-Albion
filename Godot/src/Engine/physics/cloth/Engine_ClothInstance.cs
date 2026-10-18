using System;

namespace Refii.Physics;

/// <summary>
/// The moving state of one cloth piece — the guest's 544-byte sim item. Positions, the skin
/// target, the normals and the collided masks live in the model's space; the two transforms
/// are the owner's world matrices for this frame and the last; the bone palette is in the
/// CLOTH's bone id space, already remapped by whoever binds it.
/// </summary>
public sealed class ClothInstance
{
    public ClothAsset? Asset { get; set; }

    public PhysVec4[] PosPrev { get; set; } = Array.Empty<PhysVec4>();

    public PhysVec4[] PosCur { get; set; } = Array.Empty<PhysVec4>();

    public PhysVec4[] SkinTarget { get; set; } = Array.Empty<PhysVec4>();

    /// <summary>Vertex normals of the last published pose; the wind term reads them.</summary>
    public PhysVec4[] VertexNormals { get; set; } = Array.Empty<PhysVec4>();

    /// <summary>Tangents of the last published pose, xyz and handedness in w.</summary>
    public PhysVec4[] Tangents { get; set; } = Array.Empty<PhysVec4>();

    /// <summary>Per vertex, non-zero where a collider moved it in the last relaxation pass.</summary>
    public PhysVec4[] CollidedFlags { get; set; } = Array.Empty<PhysVec4>();

    /// <summary>Three skinning rows per cloth bone id, or empty when no rig drives it.</summary>
    public PhysVec4[] BoneMatrices { get; set; } = Array.Empty<PhysVec4>();

    /// <summary>The same bones as row-vector 4x4s, for posing the colliders.</summary>
    public PhysMat4[] BonePoses { get; set; } = Array.Empty<PhysMat4>();

    public bool HasBones => BoneMatrices.Length != 0;

    public SphereCollider[] Spheres { get; set; } = Array.Empty<SphereCollider>();

    public PlaneCollider[] Planes { get; set; } = Array.Empty<PlaneCollider>();

    public CapsuleCollider[] Capsules { get; set; } = Array.Empty<CapsuleCollider>();

    public BoxCollider[] Boxes { get; set; } = Array.Empty<BoxCollider>();

    public PhysMat4 XformPrev;

    public PhysMat4 XformCur;

    public float PrevDt { get; set; }

    /// <summary>The level's wind as a world-space velocity: direction times speed.</summary>
    public PhysVec4 WindDirWorld;

    /// <summary>World-space gravity; the guest's is (0, 0, -9.8) in its Z-up world.</summary>
    public PhysVec4 GravityDirWorld;

    /// <summary>The distance fade the guest draws with: 1 simulated, 0 skinned, between a lerp.</summary>
    public float LodWeight { get; set; } = 1.0f;

    public PhysVec4 BoundsSphere;

    public PhysVec4 BoundsMin;

    public PhysVec4 BoundsMax;

    public int RelaxIterations { get; set; }

    public float Residual { get; set; }

    public PhysVec4[] ForceWeight { get; set; } = Array.Empty<PhysVec4>();

    public PhysVec4[] BlendWeight { get; set; } = Array.Empty<PhysVec4>();

    public float LutScale { get; set; } = float.NaN;

    public float LutDt { get; set; } = float.NaN;

    /// <summary>The largest squared velocity the last integration saw, for sleeping a still prop.</summary>
    public float LastMoveSquared { get; set; }

    /// <summary>Consecutive steps under the sleep threshold.</summary>
    public int StillSteps { get; set; }

    /// <summary>Vertices a collider moved in the last relaxation pass of the last step.</summary>
    public int CollidedVertices { get; set; }

    public static ClothInstance Create()
    {
        var instance = new ClothInstance
        {
            WindDirWorld = PhysVec4.Zero,
            GravityDirWorld = new PhysVec4(0.0f, 0.0f, -9.8f, 0.0f),
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
        Tangents = new PhysVec4[count];
        CollidedFlags = new PhysVec4[count];

        Spheres = (SphereCollider[])asset.Spheres.Clone();
        Planes = (PlaneCollider[])asset.Planes.Clone();
        Capsules = (CapsuleCollider[])asset.Capsules.Clone();
        Boxes = (BoxCollider[])asset.Boxes.Clone();

        ResetToSkinned();
    }

    /// <summary>
    /// The guest's reset (<c>sub_82A942A8</c>): every position buffer takes the skinned pose
    /// when a driver exists, else the rest pose; the colliders are re-copied from the
    /// definition; the previous dt is zeroed and the previous transform made the current one.
    /// Run at creation, on a teleport of more than one unit, and when the owner asks.
    /// </summary>
    public void ResetToSkinned()
    {
        if (Asset is null)
        {
            return;
        }

        PhysVec4[] seed = Asset.BindPos;

        if (Asset.Driver is not null && Asset.Driver.IndexStride > 0 && HasBones)
        {
            Engine_ClothSolver.SkinDriverPositions(Asset.Driver, SkinTarget, Asset.BindPos, BoneMatrices);
            seed = SkinTarget;
        }

        for (int i = 0; i < PosCur.Length && i < seed.Length; i++)
        {
            PhysVec4 p = PhysVec4.KeepXyzTakeW(seed[i], PhysVec4.One);
            PosCur[i] = p;
            PosPrev[i] = p;
            SkinTarget[i] = p;
        }

        Array.Copy(Asset.Spheres, Spheres, Spheres.Length);
        Array.Copy(Asset.Planes, Planes, Planes.Length);
        Array.Copy(Asset.Capsules, Capsules, Capsules.Length);
        Array.Copy(Asset.Boxes, Boxes, Boxes.Length);

        Array.Clear(CollidedFlags, 0, CollidedFlags.Length);
        Engine_ClothSolver.ComputeNormals(this);
        Engine_ClothSolver.Finalise(this);
        PrevDt = 0.0f;
        XformPrev = XformCur;
    }
}
