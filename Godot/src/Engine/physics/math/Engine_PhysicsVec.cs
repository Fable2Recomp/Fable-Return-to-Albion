using System;

namespace Refii.Physics;

public struct PhysMask
{
    public bool X;
    public bool Y;
    public bool Z;
    public bool W;

    public bool Any => X;
}

public struct PhysVec4
{
    public float X;
    public float Y;
    public float Z;
    public float W;

    public PhysVec4(float x, float y, float z, float w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }

    public static PhysVec4 Splat(float s) => new(s, s, s, s);

    public static PhysVec4 Zero => Splat(0.0f);

    public static PhysVec4 One => Splat(1.0f);

    public static PhysVec4 Add(in PhysVec4 a, in PhysVec4 b) =>
        new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);

    public static PhysVec4 Sub(in PhysVec4 a, in PhysVec4 b) =>
        new(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W);

    public static PhysVec4 Mul(in PhysVec4 a, in PhysVec4 b) =>
        new(a.X * b.X, a.Y * b.Y, a.Z * b.Z, a.W * b.W);

    public static PhysVec4 MAdd(in PhysVec4 a, in PhysVec4 b, in PhysVec4 c) =>
        Add(Mul(a, c), b);

    public static PhysVec4 NMSub(in PhysVec4 a, in PhysVec4 b, in PhysVec4 c) =>
        Sub(b, Mul(a, c));

    public static PhysVec4 MSum3(in PhysVec4 a, in PhysVec4 b) =>
        Splat((a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z));

    public static PhysVec4 MSum4(in PhysVec4 a, in PhysVec4 b) =>
        Splat((a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z) + (a.W * b.W));

    public static PhysVec4 Abs(in PhysVec4 a) =>
        new(MathF.Abs(a.X), MathF.Abs(a.Y), MathF.Abs(a.Z), MathF.Abs(a.W));

    public static PhysVec4 Max(in PhysVec4 a, in PhysVec4 b) =>
        new(
            a.X > b.X ? a.X : b.X,
            a.Y > b.Y ? a.Y : b.Y,
            a.Z > b.Z ? a.Z : b.Z,
            a.W > b.W ? a.W : b.W);

    public static PhysVec4 Min(in PhysVec4 a, in PhysVec4 b) =>
        new(
            a.X < b.X ? a.X : b.X,
            a.Y < b.Y ? a.Y : b.Y,
            a.Z < b.Z ? a.Z : b.Z,
            a.W < b.W ? a.W : b.W);

    public static PhysMask CmpGe(in PhysVec4 a, in PhysVec4 b) => new()
    {
        X = a.X >= b.X,
        Y = a.Y >= b.Y,
        Z = a.Z >= b.Z,
        W = a.W >= b.W,
    };

    public static PhysMask CmpGt(in PhysVec4 a, in PhysVec4 b) => new()
    {
        X = a.X > b.X,
        Y = a.Y > b.Y,
        Z = a.Z > b.Z,
        W = a.W > b.W,
    };

    public static PhysMask CmpEq(in PhysVec4 a, in PhysVec4 b) => new()
    {
        X = a.X == b.X,
        Y = a.Y == b.Y,
        Z = a.Z == b.Z,
        W = a.W == b.W,
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
            c.X ? b.X : a.X,
            c.Y ? b.Y : a.Y,
            c.Z ? b.Z : a.Z,
            c.W ? b.W : a.W);

    public static PhysVec4 Sel(in PhysVec4 a, in PhysVec4 b, in PhysVec4 c)
    {
        var mask = new PhysMask
        {
            X = c.X != 0.0f,
            Y = c.Y != 0.0f,
            Z = c.Z != 0.0f,
            W = c.W != 0.0f,
        };

        return Sel(a, b, mask);
    }

    public static PhysVec4 KeepXyzTakeW(in PhysVec4 a, in PhysVec4 b) =>
        new(a.X, a.Y, a.Z, b.X);

    public static PhysVec4 ReplaceX(in PhysVec4 a, in PhysVec4 b) =>
        new(b.X, a.Y, a.Z, a.W);

    public static PhysVec4 ReplaceY(in PhysVec4 a, in PhysVec4 b) =>
        new(a.X, b.X, a.Z, a.W);

    public static PhysVec4 ReplaceZ(in PhysVec4 a, in PhysVec4 b) =>
        new(a.X, a.Y, b.X, a.W);

    public static PhysVec4 MaskXyz(in PhysVec4 a) => new(a.X, a.Y, a.Z, 0.0f);

    public static PhysVec4 RSqrtE(in PhysVec4 a) =>
        new(
            1.0f / MathF.Sqrt(a.X),
            1.0f / MathF.Sqrt(a.Y),
            1.0f / MathF.Sqrt(a.Z),
            1.0f / MathF.Sqrt(a.W));

    public static PhysVec4 RecipE(in PhysVec4 a) =>
        new(1.0f / a.X, 1.0f / a.Y, 1.0f / a.Z, 1.0f / a.W);

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

    public Godot.Vector3 ToVector3() => new(X, Y, Z);

    public static PhysVec4 FromVector3(Godot.Vector3 v, float w) => new(v.X, v.Y, v.Z, w);
}
