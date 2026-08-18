using Godot;

namespace Refii.Physics;

public struct PhysMat4
{
    public PhysVec4 R0;
    public PhysVec4 R1;
    public PhysVec4 R2;
    public PhysVec4 R3;

    public PhysVec4 this[int row]
    {
        get => row switch
        {
            0 => R0,
            1 => R1,
            2 => R2,
            _ => R3,
        };

        set
        {
            switch (row)
            {
                case 0: R0 = value; break;
                case 1: R1 = value; break;
                case 2: R2 = value; break;
                default: R3 = value; break;
            }
        }
    }
}

public static class Engine_PhysicsMatrix
{
    public static PhysMat4 Identity()
    {
        var m = default(PhysMat4);
        LoadIdentity(ref m);
        return m;
    }

    public static void LoadIdentity(ref PhysMat4 m)
    {
        m.R0 = new PhysVec4(1.0f, 0.0f, 0.0f, 0.0f);
        m.R1 = new PhysVec4(0.0f, 1.0f, 0.0f, 0.0f);
        m.R2 = new PhysVec4(0.0f, 0.0f, 1.0f, 0.0f);
        m.R3 = new PhysVec4(0.0f, 0.0f, 0.0f, 1.0f);
    }

    public static void LoadIdentityTranslation(ref PhysMat4 m, in PhysVec4 translation)
    {
        LoadIdentity(ref m);
        m.R3 = PhysVec4.KeepXyzTakeW(translation, PhysVec4.One);
    }

    public static void Transpose(
        ref PhysMat4 outMatrix,
        in PhysVec4 a, in PhysVec4 b, in PhysVec4 c, in PhysVec4 d)
    {
        outMatrix.R0 = new PhysVec4(a.X, b.X, c.X, d.X);
        outMatrix.R1 = new PhysVec4(a.Y, b.Y, c.Y, d.Y);
        outMatrix.R2 = new PhysVec4(a.Z, b.Z, c.Z, d.Z);
        outMatrix.R3 = new PhysVec4(a.W, b.W, c.W, d.W);
    }

    public static void AffineInverse(ref PhysMat4 outMatrix, in PhysMat4 src)
    {
        PhysVec4 i0 = PhysVec4.Mul(src.R0, PhysVec4.RecipE(PhysVec4.MSum3(src.R0, src.R0)));
        PhysVec4 i1 = PhysVec4.Mul(src.R1, PhysVec4.RecipE(PhysVec4.MSum3(src.R1, src.R1)));
        PhysVec4 i2 = PhysVec4.Mul(src.R2, PhysVec4.RecipE(PhysVec4.MSum3(src.R2, src.R2)));
        var i3 = new PhysVec4(0.0f, 0.0f, 0.0f, 1.0f);

        i0.W = -PhysVec4.MSum3(src.R3, i0).X;
        i1.W = -PhysVec4.MSum3(src.R3, i1).X;
        i2.W = -PhysVec4.MSum3(src.R3, i2).X;

        Transpose(ref outMatrix, i0, i1, i2, i3);
    }

    public static void FastAffineInverse(ref PhysMat4 m)
    {
        PhysMat4 src = m;
        AffineInverse(ref m, src);
    }

    public static void Multiply(ref PhysMat4 outMatrix, in PhysMat4 a, in PhysMat4 b)
    {
        Columns(b, out PhysVec4 c0, out PhysVec4 c1, out PhysVec4 c2, out PhysVec4 c3);

        for (int i = 0; i < 4; i++)
        {
            PhysVec4 row = a[i];
            outMatrix[i] = new PhysVec4(
                PhysVec4.MSum4(row, c0).X,
                PhysVec4.MSum4(row, c1).X,
                PhysVec4.MSum4(row, c2).X,
                PhysVec4.MSum4(row, c3).X);
        }
    }

    public static void Lerp(ref PhysMat4 outMatrix, in PhysMat4 a, in PhysMat4 b, float t)
    {
        PhysVec4 ts = PhysVec4.Splat(t);
        for (int i = 0; i < 4; i++)
        {
            outMatrix[i] = PhysVec4.MAdd(PhysVec4.Sub(b[i], a[i]), a[i], ts);
        }
    }

    public static void Columns(
        in PhysMat4 m,
        out PhysVec4 c0, out PhysVec4 c1, out PhysVec4 c2, out PhysVec4 c3)
    {
        c0 = new PhysVec4(m.R0.X, m.R1.X, m.R2.X, m.R3.X);
        c1 = new PhysVec4(m.R0.Y, m.R1.Y, m.R2.Y, m.R3.Y);
        c2 = new PhysVec4(m.R0.Z, m.R1.Z, m.R2.Z, m.R3.Z);
        c3 = new PhysVec4(m.R0.W, m.R1.W, m.R2.W, m.R3.W);
    }

    public static void TransformHomogeneous(ref PhysVec4 p, in PhysMat4 m)
    {
        PhysVec4 h = PhysVec4.KeepXyzTakeW(p, PhysVec4.One);
        Columns(m, out PhysVec4 c0, out PhysVec4 c1, out PhysVec4 c2, out PhysVec4 c3);

        p = new PhysVec4(
            PhysVec4.MSum4(h, c0).X,
            PhysVec4.MSum4(h, c1).X,
            PhysVec4.MSum4(h, c2).X,
            PhysVec4.MSum4(h, c3).X);
    }

    public static PhysVec4 TransformPoint(in PhysMat4 m, in PhysVec4 localPoint)
    {
        PhysVec4 p = localPoint;
        TransformHomogeneous(ref p, m);
        return PhysVec4.MaskXyz(p);
    }

    public static PhysVec4 TransformDirection(in PhysMat4 m, in PhysVec4 localDir)
    {
        PhysVec4 acc = PhysVec4.MAdd(
            PhysVec4.Splat(localDir.X),
            PhysVec4.MAdd(
                PhysVec4.Splat(localDir.Y),
                PhysVec4.Mul(m.R2, PhysVec4.Splat(localDir.Z)),
                m.R1),
            m.R0);

        return PhysVec4.MaskXyz(acc);
    }

    public static PhysMat4 FromTransform3D(Transform3D transform)
    {
        Basis basis = transform.Basis;
        Vector3 origin = transform.Origin;

        var m = default(PhysMat4);
        m.R0 = new PhysVec4(basis.X.X, basis.Y.X, basis.Z.X, origin.X);
        m.R1 = new PhysVec4(basis.X.Y, basis.Y.Y, basis.Z.Y, origin.Y);
        m.R2 = new PhysVec4(basis.X.Z, basis.Y.Z, basis.Z.Z, origin.Z);
        m.R3 = new PhysVec4(0.0f, 0.0f, 0.0f, 1.0f);
        return m;
    }
}
