using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Refii.Physics;

public struct PhysMask
{
    public bool X;
    public bool Y;
    public bool Z;
    public bool W;

    public bool Any => X;
}

/// <summary>
/// The guest's VMX128 lane vector, backed by <see cref="Vector4"/> so the JIT emits SIMD for
/// the arithmetic. The lane-wise helpers keep the names of the VMX operations the solver was
/// ported from; the hot loops in <see cref="Engine_ClothSolver"/> use <see cref="V"/> directly.
/// </summary>
public struct PhysVec4
{
    public Vector4 V;

    public static readonly Vector4 XyzMask = new(1.0f, 1.0f, 1.0f, 0.0f);

    public PhysVec4(float x, float y, float z, float w)
    {
        V = new Vector4(x, y, z, w);
    }

    public PhysVec4(Vector4 v)
    {
        V = v;
    }

    public float X
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get => V.X;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] set => V.X = value;
    }

    public float Y
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get => V.Y;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] set => V.Y = value;
    }

    public float Z
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get => V.Z;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] set => V.Z = value;
    }

    public float W
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get => V.W;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] set => V.W = value;
    }

    public static PhysVec4 Splat(float s) => new(new Vector4(s));

    public static PhysVec4 Zero => new(Vector4.Zero);

    public static PhysVec4 One => new(Vector4.One);

    public static PhysVec4 Add(in PhysVec4 a, in PhysVec4 b) => new(a.V + b.V);

    public static PhysVec4 Sub(in PhysVec4 a, in PhysVec4 b) => new(a.V - b.V);

    public static PhysVec4 Mul(in PhysVec4 a, in PhysVec4 b) => new(a.V * b.V);

    /// <summary>vmaddfp: a * c + b.</summary>
    public static PhysVec4 MAdd(in PhysVec4 a, in PhysVec4 b, in PhysVec4 c) => new((a.V * c.V) + b.V);

    /// <summary>vnmsubfp: b - a * c.</summary>
    public static PhysVec4 NMSub(in PhysVec4 a, in PhysVec4 b, in PhysVec4 c) => new(b.V - (a.V * c.V));

    /// <summary>vmsum3fp128: the xyz dot in every lane.</summary>
    public static PhysVec4 MSum3(in PhysVec4 a, in PhysVec4 b) =>
        new(new Vector4(Vector4.Dot(a.V * XyzMask, b.V)));

    /// <summary>vmsum4fp128: the xyzw dot in every lane.</summary>
    public static PhysVec4 MSum4(in PhysVec4 a, in PhysVec4 b) =>
        new(new Vector4(Vector4.Dot(a.V, b.V)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot3(in Vector4 a, in Vector4 b) => Vector4.Dot(a * XyzMask, b);

    public static PhysVec4 Abs(in PhysVec4 a) => new(Vector4.Abs(a.V));

    public static PhysVec4 Max(in PhysVec4 a, in PhysVec4 b) => new(Vector4.Max(a.V, b.V));

    public static PhysVec4 Min(in PhysVec4 a, in PhysVec4 b) => new(Vector4.Min(a.V, b.V));

    public static PhysMask CmpGe(in PhysVec4 a, in PhysVec4 b) => new()
    {
        X = a.V.X >= b.V.X,
        Y = a.V.Y >= b.V.Y,
        Z = a.V.Z >= b.V.Z,
        W = a.V.W >= b.V.W,
    };

    public static PhysMask CmpGt(in PhysVec4 a, in PhysVec4 b) => new()
    {
        X = a.V.X > b.V.X,
        Y = a.V.Y > b.V.Y,
        Z = a.V.Z > b.V.Z,
        W = a.V.W > b.V.W,
    };

    public static PhysMask CmpEq(in PhysVec4 a, in PhysVec4 b) => new()
    {
        X = a.V.X == b.V.X,
        Y = a.V.Y == b.V.Y,
        Z = a.V.Z == b.V.Z,
        W = a.V.W == b.V.W,
    };

    public static PhysMask And(in PhysMask a, in PhysMask b) => new()
    {
        X = a.X && b.X,
        Y = a.Y && b.Y,
        Z = a.Z && b.Z,
        W = a.W && b.W,
    };

    public static PhysVec4 Sel(in PhysVec4 a, in PhysVec4 b, in PhysMask c) =>
        new(
            c.X ? b.V.X : a.V.X,
            c.Y ? b.V.Y : a.V.Y,
            c.Z ? b.V.Z : a.V.Z,
            c.W ? b.V.W : a.V.W);

    public static PhysVec4 Sel(in PhysVec4 a, in PhysVec4 b, in PhysVec4 c)
    {
        var mask = new PhysMask
        {
            X = c.V.X != 0.0f,
            Y = c.V.Y != 0.0f,
            Z = c.V.Z != 0.0f,
            W = c.V.W != 0.0f,
        };

        return Sel(a, b, mask);
    }

    public static PhysVec4 KeepXyzTakeW(in PhysVec4 a, in PhysVec4 b) =>
        new(a.V.X, a.V.Y, a.V.Z, b.V.X);

    public static PhysVec4 ReplaceX(in PhysVec4 a, in PhysVec4 b) =>
        new(b.V.X, a.V.Y, a.V.Z, a.V.W);

    public static PhysVec4 ReplaceY(in PhysVec4 a, in PhysVec4 b) =>
        new(a.V.X, b.V.X, a.V.Z, a.V.W);

    public static PhysVec4 ReplaceZ(in PhysVec4 a, in PhysVec4 b) =>
        new(a.V.X, a.V.Y, b.V.X, a.V.W);

    public static PhysVec4 MaskXyz(in PhysVec4 a) => new(a.V * XyzMask);

    public static PhysVec4 Cross3(in PhysVec4 a, in PhysVec4 b) =>
        new(
            (a.V.Y * b.V.Z) - (a.V.Z * b.V.Y),
            (a.V.Z * b.V.X) - (a.V.X * b.V.Z),
            (a.V.X * b.V.Y) - (a.V.Y * b.V.X),
            0.0f);

    public static PhysVec4 RSqrtE(in PhysVec4 a) => new(Vector4.One / Vector4.SquareRoot(a.V));

    public static PhysVec4 RecipE(in PhysVec4 a) => new(Vector4.One / a.V);

    public static PhysVec4 RefineReciprocal(in PhysVec4 denom, in PhysVec4 est)
    {
        PhysVec4 one = One;
        PhysVec4 x0 = est;
        PhysVec4 e0 = NMSub(denom, one, x0);
        PhysVec4 x1 = MAdd(x0, x0, e0);
        PhysVec4 e1 = NMSub(denom, one, x1);
        PhysMask finite = CmpEq(x1, x1);
        PhysVec4 x2 = MAdd(x1, x1, e1);
        return Sel(x0, x2, finite);
    }

    public Godot.Vector3 ToVector3() => new(V.X, V.Y, V.Z);

    public static PhysVec4 FromVector3(Godot.Vector3 v, float w) => new(v.X, v.Y, v.Z, w);
}
