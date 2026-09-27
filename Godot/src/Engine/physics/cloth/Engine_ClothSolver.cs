using System;
using System.Numerics;

namespace Refii.Physics;

/// <summary>
/// The guest's cloth step (<c>sub_82A94EE0</c>), pass for pass: LUT rebuild, root-motion
/// follow, Verlet integration, skinning and skin-retention blend, then a relaxation loop of one
/// distance-constraint pass and the four collider passes until the residual falls under 0.01 or
/// 31 iterations, then the welds and the bounds. Every constant is the binary's; the comments
/// name the guest function each pass is ported from.
/// </summary>
public static class Engine_ClothSolver
{
    public const float DampRateHz = 30.0f;
    public const float DtRatioClamp = 2.0f;
    public const float ConvergeEps = 0.01f;
    public const float DegenerateEps = 1.0e-7f;
    public const float CollisionSkin = 0.001f;
    public const float Inv64 = 0.015625f;
    public const float FloatMax = 3.402823466e+38f;

    public const int MaxRelaxIterations = 30;
    public const int ForceWeightEntries = 67;
    public const int BlendWeightEntries = 65;

    /// <summary>
    /// The guest keeps two global LUTs and rebuilds them whenever an item's dt or scale
    /// differs from the last (<c>sub_82A94818</c>); ours live on the instance so bodies can
    /// step on worker threads at once. A[k] = k * scale / 64 weights the forces;
    /// B[k] = pow(k * scale / 64, dt) is the per-step retention against the skinned pose.
    /// </summary>
    public static void UpdateTimeLuts(ClothInstance inst, float dt, float attrScale)
    {
        if (inst.ForceWeight.Length != ForceWeightEntries)
        {
            inst.ForceWeight = new PhysVec4[ForceWeightEntries];
            inst.BlendWeight = new PhysVec4[BlendWeightEntries];
            inst.LutScale = float.NaN;
            inst.LutDt = float.NaN;
        }

        if (attrScale != inst.LutScale)
        {
            for (int k = 0; k < ForceWeightEntries; k++)
            {
                inst.ForceWeight[k] = PhysVec4.Splat(k * attrScale * Inv64);
            }
        }
        else if (dt == inst.LutDt)
        {
            return;
        }

        for (int k = 0; k < BlendWeightEntries; k++)
        {
            inst.BlendWeight[k] = PhysVec4.Splat(MathF.Pow(k * attrScale * Inv64, dt));
        }

        inst.LutScale = attrScale;
        inst.LutDt = dt;
    }

    /// <summary>
    /// <c>sub_82A950B8</c>: one Gauss-Seidel pass over every edge. err = |b - a| - rest;
    /// a += err * d̂ * wA * k; b -= err * d̂ * wB * k; returns 0.5 * max|err|.
    /// </summary>
    public static float SolveConstraints(ClothInstance inst)
    {
        ClothAsset? asset = inst.Asset;
        if (asset is null)
        {
            return 0.0f;
        }

        PhysVec4[] pos = inst.PosCur;
        ushort[] edges = asset.Edges;
        PhysVec4[] param = asset.EdgeParams;
        int edgeCount = Math.Min(asset.EdgeCount, param.Length);
        if (edgeCount <= 0)
        {
            return 0.0f;
        }

        float maxErr = 0.0f;
        int count = pos.Length;

        for (int e = 0; e < edgeCount; e++)
        {
            int ia = edges[e * 2];
            int ib = edges[(e * 2) + 1];
            if (ia >= count || ib >= count)
            {
                continue;
            }

            Vector4 p = param[e].V;
            Vector4 a = pos[ia].V;
            Vector4 b = pos[ib].V;
            // Three lanes, as vmsum3fp128: the box pass leaves w = 0 in a position it moved.
            Vector4 d = (b - a) * PhysVec4.XyzMask;

            float lenSq = Vector4.Dot(d, d);
            if (lenSq <= DegenerateEps * DegenerateEps)
            {
                continue;
            }

            float len = MathF.Sqrt(lenSq);
            float err = len - p.X;
            float absErr = MathF.Abs(err);
            if (absErr > maxErr)
            {
                maxErr = absErr;
            }

            Vector4 corr = d * (err / len);
            pos[ia].V = a + (corr * (p.Z * p.Y));
            pos[ib].V = b - (corr * (p.W * p.Y));
        }

        return maxErr * 0.5f;
    }

    public static void ApplyForces(ClothInstance inst)
    {
        ClothAsset? asset = inst.Asset;
        if (asset is null || !asset.FollowRotation)
        {
            return;
        }

        float force = asset.Inertia;
        if (force <= 0.0f)
        {
            return;
        }

        PhysMat4 invCur = inst.XformCur;
        Engine_PhysicsMatrix.FastAffineInverse(ref invCur);

        var frameDelta = default(PhysMat4);
        Engine_PhysicsMatrix.Multiply(ref frameDelta, inst.XformPrev, invCur);
        Engine_PhysicsMatrix.LoadIdentity(ref invCur);

        var delta = default(PhysMat4);
        Engine_PhysicsMatrix.Lerp(ref delta, invCur, frameDelta, force);

        var row3 = new PhysVec4(0.0f, 0.0f, 0.0f, 1.0f);

        int begin = asset.SimBegin;
        int end = asset.SimEnd;
        if (begin >= end)
        {
            return;
        }

        var col0 = new PhysVec4(delta.R0.X, delta.R1.X, delta.R2.X, row3.X);
        var col1 = new PhysVec4(delta.R0.Y, delta.R1.Y, delta.R2.Y, row3.Y);
        var col2 = new PhysVec4(delta.R0.Z, delta.R1.Z, delta.R2.Z, row3.Z);
        var col3 = new PhysVec4(delta.R0.W, delta.R1.W, delta.R2.W, row3.W);

        for (int b = 0; b < 2; b++)
        {
            PhysVec4[] pos = b == 0 ? inst.PosPrev : inst.PosCur;

            for (int i = begin; i < end && i < pos.Length; i++)
            {
                PhysVec4 p = pos[i];
                pos[i] = new PhysVec4(
                    PhysVec4.MSum4(p, col0).X,
                    PhysVec4.MSum4(p, col1).X,
                    PhysVec4.MSum4(p, col2).X,
                    PhysVec4.MSum4(p, col3).X);
            }
        }
    }

    /// <summary>
    /// <c>sub_82A964C0</c>: Verlet over the sim range. new = cur + (collided ? 0 : vel * min(dt/prevDt, 2)
    /// * decay + (wind * dt² * |n·ŵ| * windStrength + gravity * gravityStrength * dt²) * A[attr] * decay),
    /// decay = pow(1 - damping, dt * 30); wind and gravity brought into the model's space by the
    /// inverse of the current transform.
    /// </summary>
    public static void Integrate(ClothInstance inst, float dt)
    {
        ClothAsset? asset = inst.Asset;
        if (asset is null)
        {
            return;
        }

        float d = MathF.Pow(1.0f - asset.Damping, dt * DampRateHz);
        float dt2 = dt * dt;

        PhysVec4[] swapA = inst.PosPrev;
        PhysVec4[] swapB = inst.PosCur;
        inst.PosCur = swapA;
        inst.PosPrev = swapB;

        PhysVec4[] posNew = inst.PosCur;
        PhysVec4[] posCur = swapB;

        var inv = default(PhysMat4);
        Engine_PhysicsMatrix.AffineInverse(ref inv, inst.XformCur);

        float ratio = dt / inst.PrevDt;
        inst.PrevDt = dt;
        ratio = ratio - DtRatioClamp > 0.0f ? DtRatioClamp : ratio;

        PhysVec4 windLocal = PhysVec4.MAdd(
            PhysVec4.Splat(inst.WindDirWorld.X),
            PhysVec4.MAdd(
                PhysVec4.Splat(inst.WindDirWorld.Y),
                PhysVec4.Mul(inv.R2, PhysVec4.Splat(inst.WindDirWorld.Z)),
                inv.R1),
            inv.R0);

        PhysVec4 gravLocal = PhysVec4.MAdd(
            PhysVec4.Splat(inst.GravityDirWorld.X),
            PhysVec4.MAdd(
                PhysVec4.Splat(inst.GravityDirWorld.Y),
                PhysVec4.Mul(inv.R2, PhysVec4.Splat(inst.GravityDirWorld.Z)),
                inv.R1),
            inv.R0);

        PhysVec4 windLenSq = PhysVec4.MSum3(windLocal, windLocal);
        var fallback = new PhysVec4(1.0f, 0.0f, 0.0f, 0.0f);
        PhysMask nonDegenerate = PhysVec4.CmpGt(windLenSq, PhysVec4.Splat(DegenerateEps));
        PhysVec4 windDir = PhysVec4.Sel(
            fallback, PhysVec4.Mul(windLocal, PhysVec4.RSqrtE(windLenSq)), nonDegenerate);

        PhysVec4 windTerm = PhysVec4.Mul(windLocal, PhysVec4.Splat(dt2));
        PhysVec4 gravTerm = PhysVec4.Mul(
            PhysVec4.Mul(gravLocal, PhysVec4.Splat(asset.GravityStrength)), PhysVec4.Splat(dt2));
        PhysVec4 velScale = PhysVec4.Mul(PhysVec4.Splat(ratio), PhysVec4.Splat(d));
        PhysVec4 dampSplat = PhysVec4.Splat(d);
        PhysVec4 windStrength = PhysVec4.Splat(asset.WindStrength);
        PhysVec4 homogeneousW = PhysVec4.One;
        PhysVec4 zero = PhysVec4.Zero;

        byte[] attr = asset.VertexAttr;
        int begin = asset.SimBegin;
        int end = asset.SimEnd;

        Vector4 windDirV = windDir.V;
        Vector4 windTermV = windTerm.V;
        Vector4 gravTermV = gravTerm.V;
        float velScaleF = ratio * d;
        float windStrengthF = asset.WindStrength;
        PhysVec4[] normals = inst.VertexNormals;
        PhysVec4[] flags = inst.CollidedFlags;
        PhysVec4[] force = inst.ForceWeight;

        float maxMoveSq = 0.0f;
        for (int i = begin; i < end && i < posNew.Length; i++)
        {
            Vector4 cur = posCur[i].V;
            Vector4 vel = cur - posNew[i].V;
            maxMoveSq = MathF.Max(maxMoveSq, Vector4.Dot(vel, vel));

            float facing = MathF.Abs(Vector4.Dot(windDirV, normals[i].V));
            Vector4 accel = ((windTermV * (facing * windStrengthF)) + gravTermV) *
                force[Weight(attr, i, ForceWeightEntries)].V;

            Vector4 step = flags[i].V.X != 0.0f
                ? Vector4.Zero
                : (accel * d) + (vel * velScaleF);

            Vector4 next = cur + step;
            next.W = 1.0f;
            posNew[i].V = next;
        }

        inst.LastMoveSquared = maxMoveSq;
    }

    /// <summary>
    /// <c>sub_82B8EE48</c>: out_k = Σ_j w_j · dot4(bind, bone_j.row_k) for the three rows.
    /// </summary>
    public static void SkinDriverPositions(
        ClothSkinDriver driver, PhysVec4[] output, PhysVec4[] bind, PhysVec4[] bones)
    {
        if (driver.Count <= 0 || bones.Length == 0)
        {
            return;
        }

        int stride = driver.IndexStride;
        int lanes = Math.Min(4, stride);
        byte[] ids = driver.BoneIndices;
        PhysVec4[] weights = driver.Weights;

        for (int i = 0; i < driver.Count && i < bind.Length && i < output.Length; i++)
        {
            Vector4 p = bind[i].V;
            Vector4 w = i < weights.Length ? weights[i].V : Vector4.Zero;
            int indexBase = stride * i;

            float x = 0.0f;
            float y = 0.0f;
            float z = 0.0f;
            for (int b = 0; b < lanes; b++)
            {
                int slot = indexBase + b;
                if (slot >= ids.Length)
                {
                    break;
                }

                int bone = ids[slot] * 3;
                if (bone + 2 >= bones.Length)
                {
                    continue;
                }

                float wb = b switch { 0 => w.X, 1 => w.Y, 2 => w.Z, _ => w.W };
                x += Vector4.Dot(p, bones[bone].V) * wb;
                y += Vector4.Dot(p, bones[bone + 1].V) * wb;
                z += Vector4.Dot(p, bones[bone + 2].V) * wb;
            }

            output[i].V = new Vector4(x, y, z, p.W);
        }
    }

    /// <summary><c>sub_82A962D8</c>: pos = skin + (pos - skin) * B[attr] over every vertex, w = 1.</summary>
    public static void BlendSkinnedTarget(ClothInstance inst, PhysVec4[] pos, PhysVec4[] skinTarget)
    {
        ClothAsset? asset = inst.Asset;
        if (asset is null)
        {
            return;
        }

        byte[] attr = asset.VertexAttr;
        PhysVec4[] blend = inst.BlendWeight;
        int vertexCount = Math.Min(asset.VertexCount, Math.Min(pos.Length, skinTarget.Length));

        for (int i = 0; i < vertexCount; i++)
        {
            Vector4 target = skinTarget[i].V;
            Vector4 blended = target + ((pos[i].V - target) * blend[Weight(attr, i, BlendWeightEntries)].V);
            blended.W = 1.0f;
            pos[i].V = blended;
        }
    }

    /// <summary>
    /// <c>sub_82A951F8</c>: a vertex inside a sphere is pushed to radius + 0.001 along its
    /// offset and flagged; the flag is overwritten per sphere, as the guest's is.
    /// </summary>
    public static void CollideSpheres(ClothInstance inst)
    {
        ClothAsset? asset = inst.Asset;
        if (asset is null || inst.Spheres.Length == 0)
        {
            return;
        }

        int begin = asset.SimBegin;
        int end = Math.Min(asset.SimEnd, inst.PosCur.Length);
        PhysVec4[] pos = inst.PosCur;
        PhysVec4[] flags = inst.CollidedFlags;

        for (int s = 0; s < inst.Spheres.Length; s++)
        {
            Vector4 center = inst.Spheres[s].Center.V * PhysVec4.XyzMask;
            float radius = inst.Spheres[s].Radius;
            float radiusSq = radius * radius;
            float pushRadius = radius + CollisionSkin;

            for (int i = begin; i < end; i++)
            {
                Vector4 p = pos[i].V;
                Vector4 d = (p - center) * PhysVec4.XyzMask;
                float distSq = Vector4.Dot(d, d);
                bool hit = radiusSq >= distSq;
                flags[i].V = hit ? Vector4.One : Vector4.Zero;
                if (!hit || distSq <= DegenerateEps * DegenerateEps)
                {
                    continue;
                }

                Vector4 pushed = center + (d * (pushRadius / MathF.Sqrt(distSq)));
                pushed.W = 1.0f;
                pos[i].V = pushed;
            }
        }
    }

    public static void CollidePlanes(ClothInstance inst)
    {
        ClothAsset? asset = inst.Asset;
        if (asset is null || inst.Planes.Length == 0)
        {
            return;
        }

        int begin = asset.SimBegin;
        int end = asset.SimEnd;

        PhysVec4[] pos = inst.PosCur;
        PhysVec4[] flags = inst.CollidedFlags;

        for (int pl = 0; pl < inst.Planes.Length; pl++)
        {
            PlaneCollider plane = inst.Planes[pl];

            PhysVec4 point = plane.Point;
            PhysVec4 normal = plane.Normal;

            for (int i = begin; i < end && i < pos.Length; i++)
            {
                PhysVec4 p = pos[i];
                PhysVec4 rel = PhysVec4.Sub(p, point);
                float signedDist = PhysVec4.MSum3(normal, rel).X;

                if (signedDist < 0.0f)
                {
                    pos[i] = PhysVec4.Sub(p, PhysVec4.Mul(normal, PhysVec4.Splat(signedDist)));
                    flags[i] = PhysVec4.One;
                }
            }
        }
    }

    /// <summary>
    /// <c>sub_82A95AC0</c>: a finite cylinder — t along the axis must lie in [0, 1] (tested, not
    /// clamped: no end caps) and the vertex within the radius; pushed to radius + 0.001 from the
    /// axis point and flagged.
    /// </summary>
    public static void CollideCapsules(ClothInstance inst)
    {
        ClothAsset? asset = inst.Asset;
        if (asset is null || inst.Capsules.Length == 0)
        {
            return;
        }

        int begin = asset.SimBegin;
        int end = Math.Min(asset.SimEnd, inst.PosCur.Length);
        PhysVec4[] pos = inst.PosCur;
        PhysVec4[] flags = inst.CollidedFlags;

        for (int c = 0; c < inst.Capsules.Length; c++)
        {
            Vector4 p0 = inst.Capsules[c].P0.V * PhysVec4.XyzMask;
            Vector4 axis = inst.Capsules[c].Axis.V * PhysVec4.XyzMask;
            float axisLenSq = Vector4.Dot(axis, axis);
            if (axisLenSq <= DegenerateEps)
            {
                continue;
            }

            Vector4 projector = axis / axisLenSq;
            float radius = inst.Capsules[c].Radius;
            float radiusSq = radius * radius;
            float pushRadius = radius + CollisionSkin;

            for (int i = begin; i < end; i++)
            {
                Vector4 p = pos[i].V;
                Vector4 rel = (p - p0) * PhysVec4.XyzMask;
                float t = Vector4.Dot(projector, rel);
                bool onSegment = t >= 0.0f && t <= 1.0f;
                Vector4 closest = p0 + (axis * t);
                Vector4 d = (p - closest) * PhysVec4.XyzMask;
                float distSq = onSegment ? Vector4.Dot(d, d) : 0.0f;
                bool hit = onSegment && radiusSq >= distSq;
                flags[i].V = hit ? Vector4.One : Vector4.Zero;
                if (!hit || distSq <= DegenerateEps * DegenerateEps)
                {
                    continue;
                }

                Vector4 pushed = closest + (d * (pushRadius / MathF.Sqrt(distSq)));
                pushed.W = 1.0f;
                pos[i].V = pushed;
            }
        }
    }

    public static void CollideBoxes(ClothInstance inst)
    {
        ClothAsset? asset = inst.Asset;
        if (asset is null || inst.Boxes.Length == 0)
        {
            return;
        }

        int begin = asset.SimBegin;
        int end = asset.SimEnd;

        PhysVec4[] pos = inst.PosCur;
        PhysVec4[] flags = inst.CollidedFlags;

        for (int b = 0; b < inst.Boxes.Length; b++)
        {
            BoxCollider box = inst.Boxes[b];
            Engine_PhysicsMatrix.Columns(
                box.WorldToLocal,
                out PhysVec4 col0, out PhysVec4 col1, out PhysVec4 col2, out PhysVec4 col3);

            for (int i = begin; i < end && i < pos.Length; i++)
            {
                PhysVec4 p = PhysVec4.KeepXyzTakeW(pos[i], PhysVec4.One);

                var local = new PhysVec4(
                    PhysVec4.MSum4(p, col0).X,
                    PhysVec4.MSum4(p, col1).X,
                    PhysVec4.MSum4(p, col2).X,
                    PhysVec4.MSum4(p, col3).X);

                float ax = MathF.Abs(local.X);
                float ay = MathF.Abs(local.Y);
                float az = MathF.Abs(local.Z);

                if (ax >= 1.0f || ay >= 1.0f || az >= 1.0f)
                {
                    continue;
                }

                float best = -1.0f;
                int axis = 0;
                if (ax > best)
                {
                    best = ax;
                }

                if (ay > best)
                {
                    axis = 1;
                    best = ay;
                }

                if (az > best)
                {
                    axis = 2;
                }

                float component = axis switch
                {
                    0 => local.X,
                    1 => local.Y,
                    _ => local.Z,
                };

                float pushTo = component > 0.0f ? 1.0f : -1.0f;
                PhysVec4 splat = PhysVec4.Splat(pushTo);

                PhysVec4 snapped = axis switch
                {
                    0 => PhysVec4.ReplaceX(local, splat),
                    1 => PhysVec4.ReplaceY(local, splat),
                    _ => PhysVec4.ReplaceZ(local, splat),
                };

                Engine_PhysicsMatrix.TransformHomogeneous(ref snapped, box.LocalToWorld);

                pos[i] = PhysVec4.MaskXyz(snapped);
                flags[i] = PhysVec4.One;
            }
        }
    }

    public static void ComputeBoundingSphere(ref PhysVec4 output, PhysVec4[] pos, int count)
    {
        PhysVec4 lo = PhysVec4.Splat(FloatMax);
        PhysVec4 hi = PhysVec4.Splat(-FloatMax);

        for (int i = 0; i < count && i < pos.Length; i++)
        {
            lo = PhysVec4.Min(lo, pos[i]);
            hi = PhysVec4.Max(hi, pos[i]);
        }

        PhysVec4 center = PhysVec4.Mul(PhysVec4.Add(lo, hi), PhysVec4.Splat(0.5f));

        PhysVec4 maxDistSq = PhysVec4.Zero;
        for (int i = 0; i < count && i < pos.Length; i++)
        {
            PhysVec4 d = PhysVec4.Sub(pos[i], center);
            maxDistSq = PhysVec4.Max(maxDistSq, PhysVec4.MSum3(d, d));
        }

        output = PhysVec4.KeepXyzTakeW(center, PhysVec4.Splat(MathF.Sqrt(maxDistSq.X)));
    }

    public static void ComputeAabb(
        ref PhysVec4 boundsMin, ref PhysVec4 boundsMax, PhysVec4[] pos, int count)
    {
        if (count == 0 || pos.Length == 0)
        {
            boundsMin = PhysVec4.Zero;
            boundsMax = PhysVec4.Zero;
            return;
        }

        PhysVec4 lo = pos[0];
        PhysVec4 hi = pos[0];
        for (int i = 0; i < count && i < pos.Length; i++)
        {
            lo = PhysVec4.Min(lo, pos[i]);
            hi = PhysVec4.Max(hi, pos[i]);
        }

        boundsMin = PhysVec4.MaskXyz(lo);
        boundsMax = PhysVec4.MaskXyz(hi);
    }

    public static void Finalise(ClothInstance inst)
    {
        ClothAsset? asset = inst.Asset;
        if (asset is null)
        {
            return;
        }

        int vertexCount = asset.VertexCount;
        ComputeBoundingSphere(ref inst.BoundsSphere, inst.PosCur, vertexCount);
        ComputeAabb(ref inst.BoundsMin, ref inst.BoundsMax, inst.PosCur, vertexCount);
    }

    /// <summary>One step with <paramref name="dt"/> — the guest's substep, at most 50 ms.</summary>
    public static void SimulateInstance(ClothInstance inst, float dt)
    {
        ClothAsset? asset = inst.Asset;
        if (asset is null)
        {
            return;
        }

        UpdateTimeLuts(inst, dt, asset.AttrScale);

        if (dt > 0.0f)
        {
            ApplyForces(inst);
            Integrate(inst, dt);
        }

        // The skin table alone decides whether the retention blend runs (0x82a94f40). With no
        // palette the target stays the rest pose, which is what the guest skins to as well
        // when its palette is the identity.
        ClothSkinDriver? driver = asset.Driver;
        if (driver is not null && driver.IndexStride > 0)
        {
            if (inst.HasBones)
            {
                SkinDriverPositions(driver, inst.SkinTarget, asset.BindPos, inst.BoneMatrices);
            }

            BlendSkinnedTarget(inst, inst.PosCur, inst.SkinTarget);
        }

        inst.RelaxIterations = 0;
        inst.Residual = 0.0f;

        if (dt > 0.0f)
        {
            int iter = 0;
            float residual;
            do
            {
                Array.Clear(inst.CollidedFlags, 0, inst.CollidedFlags.Length);

                residual = SolveConstraints(inst);
                CollideSpheres(inst);
                CollidePlanes(inst);
                CollideCapsules(inst);
                CollideBoxes(inst);

                iter++;
            }
            while (residual > ConvergeEps && iter <= MaxRelaxIterations);

            inst.RelaxIterations = iter;
            inst.Residual = residual;
        }

        uint[] weld = asset.Weld;
        int weldCount = asset.WeldCount;
        PhysVec4[] pos = inst.PosCur;

        for (int i = 0; i < weldCount; i++)
        {
            uint src = weld[i * 2];
            uint dst = weld[(i * 2) + 1];
            if (src < pos.Length && dst < pos.Length)
            {
                pos[dst] = pos[src];
            }
        }

        Finalise(inst);
    }

    /// <summary>
    /// <c>sub_822BA0B0</c>: face normals summed onto the vertices from the cloth's own triangle
    /// list and normalised. The guest does it at copy-out; the next step's wind term reads it.
    /// </summary>
    public static void ComputeNormals(ClothInstance inst) => ComputeNormals(inst, inst.PosCur);

    public static void ComputeNormals(ClothInstance inst, PhysVec4[] pos)
    {
        ClothAsset? asset = inst.Asset;
        if (asset is null)
        {
            return;
        }

        PhysVec4[] normals = inst.VertexNormals;
        ushort[] indices = asset.Indices;
        Array.Clear(normals, 0, normals.Length);

        int triangles = indices.Length - (indices.Length % 3);
        for (int i = 0; i < triangles; i += 3)
        {
            int a = indices[i];
            int b = indices[i + 1];
            int c = indices[i + 2];
            if (a >= pos.Length || b >= pos.Length || c >= pos.Length)
            {
                continue;
            }

            PhysVec4 face = PhysVec4.Cross3(
                PhysVec4.Sub(pos[b], pos[a]), PhysVec4.Sub(pos[c], pos[a]));
            normals[a] = PhysVec4.Add(normals[a], face);
            normals[b] = PhysVec4.Add(normals[b], face);
            normals[c] = PhysVec4.Add(normals[c], face);
        }

        for (int v = 0; v < normals.Length; v++)
        {
            PhysVec4 n = normals[v];
            float lenSq = PhysVec4.MSum3(n, n).X;
            normals[v] = lenSq > 1.0e-12f
                ? PhysVec4.MaskXyz(PhysVec4.Mul(n, PhysVec4.Splat(1.0f / MathF.Sqrt(lenSq))))
                : PhysVec4.Zero;
        }
    }

    /// <summary>
    /// The tangent basis the guest rebuilds beside the normals: per triangle from the UV
    /// derivatives, summed per vertex, orthogonalised against the normal and normalised; the
    /// handedness is the file's (<see cref="ClothAsset.TangentW"/>).
    /// </summary>
    public static void ComputeTangents(ClothInstance inst, PhysVec4[] pos)
    {
        ClothAsset? asset = inst.Asset;
        if (asset is null)
        {
            return;
        }

        PhysVec4[] tangents = inst.Tangents;
        PhysVec4[] normals = inst.VertexNormals;
        float[] uv = asset.Uvs;
        ushort[] indices = asset.Indices;
        Array.Clear(tangents, 0, tangents.Length);

        if (uv.Length < pos.Length * 2)
        {
            for (int v = 0; v < tangents.Length; v++)
            {
                tangents[v] = new PhysVec4(1.0f, 0.0f, 0.0f, 1.0f);
            }

            return;
        }

        int triangles = indices.Length - (indices.Length % 3);
        for (int i = 0; i < triangles; i += 3)
        {
            int a = indices[i];
            int b = indices[i + 1];
            int c = indices[i + 2];
            if (a >= pos.Length || b >= pos.Length || c >= pos.Length)
            {
                continue;
            }

            PhysVec4 e1 = PhysVec4.Sub(pos[b], pos[a]);
            PhysVec4 e2 = PhysVec4.Sub(pos[c], pos[a]);
            float du1 = uv[b * 2] - uv[a * 2];
            float dv1 = uv[(b * 2) + 1] - uv[(a * 2) + 1];
            float du2 = uv[c * 2] - uv[a * 2];
            float dv2 = uv[(c * 2) + 1] - uv[(a * 2) + 1];
            float det = (du1 * dv2) - (du2 * dv1);
            if (MathF.Abs(det) < 1.0e-12f)
            {
                continue;
            }

            float r = 1.0f / det;
            var t = new PhysVec4(
                ((e1.X * dv2) - (e2.X * dv1)) * r,
                ((e1.Y * dv2) - (e2.Y * dv1)) * r,
                ((e1.Z * dv2) - (e2.Z * dv1)) * r,
                0.0f);
            tangents[a] = PhysVec4.Add(tangents[a], t);
            tangents[b] = PhysVec4.Add(tangents[b], t);
            tangents[c] = PhysVec4.Add(tangents[c], t);
        }

        for (int v = 0; v < tangents.Length; v++)
        {
            PhysVec4 t = tangents[v];
            PhysVec4 n = v < normals.Length ? normals[v] : PhysVec4.Zero;
            t = PhysVec4.Sub(t, PhysVec4.Mul(n, PhysVec4.Splat(PhysVec4.MSum3(n, t).X)));
            float lenSq = PhysVec4.MSum3(t, t).X;
            float w = v < asset.TangentW.Length ? asset.TangentW[v] : 1.0f;
            tangents[v] = lenSq > 1.0e-12f
                ? PhysVec4.KeepXyzTakeW(PhysVec4.Mul(t, PhysVec4.Splat(1.0f / MathF.Sqrt(lenSq))), PhysVec4.Splat(w))
                : new PhysVec4(1.0f, 0.0f, 0.0f, w);
        }
    }

    /// <summary>
    /// The pose the guest copies to its vertex buffer (<c>sub_8229F0B8</c>): the simulated
    /// positions, lerped toward the skinned ones by the distance fade when it is under 1.
    /// </summary>
    public static void DrawnPositions(ClothInstance inst, PhysVec4[] output)
    {
        PhysVec4[] sim = inst.PosCur;
        PhysVec4[] skin = inst.SkinTarget;
        float weight = inst.LodWeight;
        int count = Math.Min(output.Length, sim.Length);

        if (weight >= 1.0f || skin.Length < count)
        {
            Array.Copy(sim, output, count);
            return;
        }

        PhysVec4 w = PhysVec4.Splat(weight);
        for (int i = 0; i < count; i++)
        {
            output[i] = PhysVec4.MAdd(PhysVec4.Sub(sim[i], skin[i]), skin[i], w);
        }
    }

    public static void TransformCollidersToWorld(ClothInstance inst)
    {
        PhysMat4[] poses = inst.BonePoses;
        if (poses.Length == 0)
        {
            return;
        }

        for (int i = 0; i < inst.Spheres.Length; i++)
        {
            if (!TryBonePose(inst, inst.Spheres[i].BoneIndex, out PhysMat4 m))
            {
                continue;
            }

            inst.Spheres[i].Center =
                Engine_PhysicsMatrix.TransformPoint(m, inst.Spheres[i].LocalCenter);
        }

        for (int i = 0; i < inst.Capsules.Length; i++)
        {
            if (!TryBonePose(inst, inst.Capsules[i].BoneIndex, out PhysMat4 m))
            {
                continue;
            }

            inst.Capsules[i].P0 =
                Engine_PhysicsMatrix.TransformPoint(m, inst.Capsules[i].LocalP0);
            inst.Capsules[i].Axis =
                Engine_PhysicsMatrix.TransformDirection(m, inst.Capsules[i].LocalAxis);
        }

        for (int i = 0; i < inst.Planes.Length; i++)
        {
            if (!TryBonePose(inst, inst.Planes[i].BoneIndex, out PhysMat4 m))
            {
                continue;
            }

            inst.Planes[i].Normal =
                Engine_PhysicsMatrix.TransformDirection(m, inst.Planes[i].LocalNormal);
            inst.Planes[i].Point =
                Engine_PhysicsMatrix.TransformPoint(m, inst.Planes[i].LocalPoint);
        }

        for (int i = 0; i < inst.Boxes.Length; i++)
        {
            if (!TryBonePose(inst, inst.Boxes[i].BoneIndex, out PhysMat4 m))
            {
                continue;
            }

            var l2w = default(PhysMat4);
            Engine_PhysicsMatrix.Multiply(ref l2w, inst.Boxes[i].LocalMatrix, m);
            inst.Boxes[i].LocalToWorld = l2w;

            var w2l = default(PhysMat4);
            Engine_PhysicsMatrix.AffineInverse(ref w2l, l2w);
            inst.Boxes[i].WorldToLocal = w2l;
        }
    }

    private static bool TryBonePose(ClothInstance inst, int boneIndex, out PhysMat4 pose)
    {
        pose = default;
        if (boneIndex < 0 || boneIndex >= inst.BonePoses.Length)
        {
            return false;
        }

        pose = inst.BonePoses[boneIndex];
        return true;
    }

    private static int Weight(byte[] attr, int index, int entries)
    {
        if (index >= attr.Length)
        {
            return entries - 1;
        }

        int value = attr[index];
        return value >= entries ? entries - 1 : value;
    }
}
