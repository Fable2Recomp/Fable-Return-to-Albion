using System;

namespace Refii.Physics;

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

    private static readonly PhysVec4[] ForceWeight = new PhysVec4[ForceWeightEntries];

    private static readonly PhysVec4[] BlendWeight = new PhysVec4[BlendWeightEntries];

    private static float _cachedAttrScale;

    private static float _cachedDt;

    public static void UpdateTimeLuts(float dt, float attrScale)
    {
        if (attrScale != _cachedAttrScale)
        {
            for (int k = 0; k < ForceWeightEntries; k++)
            {
                ForceWeight[k] = PhysVec4.Splat(k * attrScale * Inv64);
            }
        }
        else if (dt == _cachedDt)
        {
            return;
        }

        for (int k = 0; k < BlendWeightEntries; k++)
        {
            BlendWeight[k] = PhysVec4.Splat(MathF.Pow(k * attrScale * Inv64, dt));
        }

        _cachedAttrScale = attrScale;
        _cachedDt = dt;
    }

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

        PhysVec4 stiff = PhysVec4.One;
        PhysVec4 maxErr = PhysVec4.Zero;

        if (edgeCount <= 0)
        {
            return 0.0f;
        }

        for (int e = 0; e < edgeCount; e++)
        {
            int ia = edges[e * 2];
            int ib = edges[(e * 2) + 1];

            if (ia >= pos.Length || ib >= pos.Length)
            {
                continue;
            }

            PhysVec4 rest = PhysVec4.Splat(param[e].X);
            PhysVec4 k = PhysVec4.Splat(param[e].Y);
            PhysVec4 wA = PhysVec4.Splat(param[e].Z);
            PhysVec4 wB = PhysVec4.Splat(param[e].W);

            PhysVec4 a = pos[ia];
            PhysVec4 b = pos[ib];
            PhysVec4 d = PhysVec4.Sub(b, a);

            PhysVec4 lenSq = PhysVec4.MSum3(d, d);
            PhysVec4 rlen0 = PhysVec4.RSqrtE(lenSq);
            PhysVec4 len = PhysVec4.RecipE(rlen0);
            PhysVec4 invLen = PhysVec4.RefineReciprocal(len, PhysVec4.RecipE(len));

            PhysVec4 err = PhysVec4.Sub(len, rest);
            maxErr = PhysVec4.Max(maxErr, PhysVec4.Abs(err));

            PhysVec4 corr = PhysVec4.Mul(
                PhysVec4.Mul(PhysVec4.Mul(err, d), invLen), stiff);
            PhysVec4 dA = PhysVec4.Mul(corr, wA);
            PhysVec4 dB = PhysVec4.Mul(corr, wB);

            pos[ia] = PhysVec4.MAdd(dA, a, k);
            pos[ib] = PhysVec4.Sub(pos[ib], PhysVec4.Mul(dB, k));
        }

        return maxErr.X * 0.5f;
    }

    public static void ApplyForces(ClothInstance inst)
    {
        ClothAsset? asset = inst.Asset;
        if (asset is null)
        {
            return;
        }

        if (!inst.WindEnabled && !inst.AnchorEnabled)
        {
            return;
        }

        float force = inst.InertiaStrength;
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

        if (!inst.WindEnabled)
        {
            Engine_PhysicsMatrix.LoadIdentityTranslation(ref delta, row3);
        }

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

    public static void Integrate(ClothInstance inst, float dt, float damping)
    {
        ClothAsset? asset = inst.Asset;
        if (asset is null)
        {
            return;
        }

        float d = MathF.Pow(1.0f - damping, dt * DampRateHz);
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
            PhysVec4.Mul(gravLocal, PhysVec4.Splat(inst.GravityStrength)), PhysVec4.Splat(dt2));
        PhysVec4 velScale = PhysVec4.Mul(PhysVec4.Splat(ratio), PhysVec4.Splat(d));
        PhysVec4 dampSplat = PhysVec4.Splat(d);
        PhysVec4 windStrength = PhysVec4.Splat(inst.WindStrength);
        PhysVec4 homogeneousW = PhysVec4.One;
        PhysVec4 zero = PhysVec4.Zero;

        byte[] attr = asset.VertexAttr;
        int begin = asset.SimBegin;
        int end = asset.SimEnd;

        for (int i = begin; i < end && i < posNew.Length; i++)
        {
            PhysVec4 vel = PhysVec4.Sub(posCur[i], posNew[i]);
            PhysVec4 facing = PhysVec4.Abs(PhysVec4.MSum3(windDir, inst.VertexNormals[i]));

            PhysVec4 accel = PhysVec4.MAdd(
                PhysVec4.Mul(windTerm, facing), gravTerm, windStrength);
            accel = PhysVec4.Mul(accel, ForceWeight[Weight(attr, i, ForceWeightEntries)]);

            PhysVec4 step = PhysVec4.MAdd(accel, PhysVec4.Mul(vel, velScale), dampSplat);
            step = PhysVec4.Sel(step, zero, inst.CollidedFlags[i]);

            posNew[i] = PhysVec4.KeepXyzTakeW(
                PhysVec4.Add(posCur[i], step), homogeneousW);
        }
    }

    public static void SkinDriverPositions(
        ClothSkinDriver driver, PhysVec4[] output, PhysVec4[] bind, PhysVec4[] bones)
    {
        if (driver.Count <= 0 || bones.Length == 0)
        {
            return;
        }

        for (int i = 0; i < driver.Count && i < bind.Length && i < output.Length; i++)
        {
            PhysVec4 p = bind[i];
            PhysVec4 w = i < driver.Weights.Length ? driver.Weights[i] : PhysVec4.Zero;

            int indexBase = driver.IndexStride * i;

            PhysVec4 acc0 = PhysVec4.Zero;
            PhysVec4 acc1 = PhysVec4.Zero;
            PhysVec4 acc2 = PhysVec4.Zero;

            for (int b = 0; b < 4; b++)
            {
                int slot = indexBase + b;
                if (b >= driver.IndexStride || slot >= driver.BoneIndices.Length)
                {
                    break;
                }

                int bone = driver.BoneIndices[slot] * 3;
                if (bone + 2 >= bones.Length)
                {
                    continue;
                }

                float lane = b switch
                {
                    0 => w.X,
                    1 => w.Y,
                    2 => w.Z,
                    _ => w.W,
                };

                PhysVec4 wb = PhysVec4.Splat(lane);
                acc0 = PhysVec4.Add(acc0, PhysVec4.Mul(PhysVec4.MSum4(p, bones[bone]), wb));
                acc1 = PhysVec4.Add(acc1, PhysVec4.Mul(PhysVec4.MSum4(p, bones[bone + 1]), wb));
                acc2 = PhysVec4.Add(acc2, PhysVec4.Mul(PhysVec4.MSum4(p, bones[bone + 2]), wb));
            }

            output[i] = new PhysVec4(acc0.X, acc1.X, acc2.X, p.W);
        }
    }

    public static void BlendSkinnedTarget(ClothInstance inst, PhysVec4[] pos, PhysVec4[] skinTarget)
    {
        ClothAsset? asset = inst.Asset;
        if (asset is null)
        {
            return;
        }

        byte[] attr = asset.VertexAttr;
        int vertexCount = asset.VertexCount;
        PhysVec4 homogeneousW = PhysVec4.One;

        for (int i = 0; i < vertexCount && i < pos.Length && i < skinTarget.Length; i++)
        {
            PhysVec4 target = skinTarget[i];
            PhysVec4 free = pos[i];
            PhysVec4 weight = BlendWeight[Weight(attr, i, BlendWeightEntries)];

            PhysVec4 blended = PhysVec4.MAdd(PhysVec4.Sub(free, target), target, weight);
            pos[i] = PhysVec4.KeepXyzTakeW(blended, homogeneousW);
        }
    }

    public static void CollideSpheres(ClothInstance inst)
    {
        ClothAsset? asset = inst.Asset;
        if (asset is null || inst.Spheres.Length == 0)
        {
            return;
        }

        int begin = asset.SimBegin;
        int end = asset.SimEnd;

        PhysVec4 homogeneousW = PhysVec4.One;
        PhysVec4[] pos = inst.PosCur;
        PhysVec4[] flags = inst.CollidedFlags;

        for (int s = 0; s < inst.Spheres.Length; s++)
        {
            SphereCollider sphere = inst.Spheres[s];

            PhysVec4 center = sphere.Center;
            PhysVec4 radius = PhysVec4.Splat(sphere.Radius);
            PhysVec4 pushRadius = PhysVec4.Splat(sphere.Radius + CollisionSkin);

            for (int i = begin; i < end && i < pos.Length; i++)
            {
                PhysVec4 p = pos[i];
                PhysVec4 d = PhysVec4.Sub(p, center);

                PhysVec4 distSq = PhysVec4.MSum3(d, d);
                PhysVec4 rdist = PhysVec4.RSqrtE(distSq);
                PhysVec4 dist = PhysVec4.RecipE(rdist);

                PhysMask hit = PhysVec4.CmpGe(radius, dist);
                flags[i] = PhysVec4.Sel(PhysVec4.Zero, PhysVec4.One, hit);

                PhysVec4 invDist = PhysVec4.RefineReciprocal(dist, PhysVec4.RecipE(dist));
                PhysVec4 pushed = PhysVec4.MAdd(
                    d, center, PhysVec4.Mul(pushRadius, invDist));

                PhysVec4 result = PhysVec4.Sel(p, pushed, hit);
                pos[i] = PhysVec4.KeepXyzTakeW(result, homogeneousW);
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

    public static void CollideCapsules(ClothInstance inst)
    {
        ClothAsset? asset = inst.Asset;
        if (asset is null || inst.Capsules.Length == 0)
        {
            return;
        }

        int begin = asset.SimBegin;
        int end = asset.SimEnd;

        PhysVec4 one = PhysVec4.One;
        PhysVec4 zero = PhysVec4.Zero;
        PhysVec4[] pos = inst.PosCur;
        PhysVec4[] flags = inst.CollidedFlags;

        for (int c = 0; c < inst.Capsules.Length; c++)
        {
            CapsuleCollider cap = inst.Capsules[c];

            PhysVec4 p0 = cap.P0;
            PhysVec4 axis = cap.Axis;

            PhysVec4 axisLenSq = PhysVec4.MSum3(axis, axis);
            PhysVec4 invAxisLen2 = PhysVec4.RefineReciprocal(
                axisLenSq, PhysVec4.RecipE(axisLenSq));
            PhysVec4 projector = PhysVec4.Mul(axis, invAxisLen2);

            PhysVec4 radiusSq = PhysVec4.Splat(cap.Radius * cap.Radius);
            PhysVec4 pushRadius = PhysVec4.Splat(cap.Radius + CollisionSkin);

            for (int i = begin; i < end && i < pos.Length; i++)
            {
                PhysVec4 p = pos[i];
                PhysVec4 rel = PhysVec4.Sub(p, p0);

                PhysVec4 t = PhysVec4.MSum3(projector, rel);
                PhysVec4 closest = PhysVec4.MAdd(axis, p0, t);

                PhysMask withinStart = PhysVec4.CmpGe(t, zero);
                PhysMask withinEnd = PhysVec4.CmpGe(one, t);
                PhysMask onSegment = PhysVec4.And(withinStart, withinEnd);

                PhysVec4 d = PhysVec4.Sub(p, closest);
                PhysVec4 distSq = PhysVec4.MSum3(d, d);
                PhysVec4 gated = PhysVec4.Sel(zero, distSq, onSegment);
                PhysMask inRadius = PhysVec4.CmpGe(radiusSq, gated);
                PhysMask hit = PhysVec4.And(onSegment, inRadius);

                flags[i] = PhysVec4.Sel(zero, one, hit);

                PhysVec4 dir = PhysVec4.Mul(d, PhysVec4.RSqrtE(distSq));
                PhysVec4 pushed = PhysVec4.MAdd(dir, closest, pushRadius);

                PhysVec4 result = PhysVec4.Sel(p, pushed, hit);
                pos[i] = PhysVec4.KeepXyzTakeW(result, one);
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

    public static void SimulateInstance(ClothInstance inst, float dt)
    {
        ClothAsset? asset = inst.Asset;
        if (asset is null)
        {
            return;
        }

        UpdateTimeLuts(dt, inst.AttrScale);

        if (dt > 0.0f)
        {
            ApplyForces(inst);
            Integrate(inst, dt, inst.Damping);
        }

        ClothSkinDriver? driver = asset.Driver;
        if (driver is not null && driver.IndexStride > 0)
        {
            SkinDriverPositions(driver, inst.SkinTarget, asset.BindPos, inst.BoneMatrices);
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

        if (boneIndex < 0)
        {
            return false;
        }

        int bone = boneIndex;
        if (boneIndex < inst.BoneRemap.Length)
        {
            bone = inst.BoneRemap[boneIndex];
        }

        if (bone < 0 || bone >= inst.BonePoses.Length)
        {
            return false;
        }

        pose = inst.BonePoses[bone];
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
