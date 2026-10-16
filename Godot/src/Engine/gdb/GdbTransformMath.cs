using System;
using Godot;

namespace Refii.Gdb;

/// <summary>
/// Centralizes all coordinate and basis conversions between Fable 2 GDB placement transforms
/// (<c>Position</c> X/Y/Z, Euler/Yaw <c>Rotation</c>, and <c>Scale</c>) and Godot 3D <see cref="Transform3D"/>.
/// Shared across <c>Engine_LevelProps</c>, <c>Engine_LevelEntities</c>, <c>Engine_LevelMarkers</c>,
/// and <c>LevelGdbPatchCompiler</c>.
/// </summary>
public static class GdbTransformMath
{
    public const float DefaultPositionEpsilon = 0.001f;
    public const float DefaultBasisEpsilon = 0.001f;

    public static Transform3D BuildTransform(GdbPlacement placement)
    {
        float scale = float.IsFinite(placement.Scale) &&
                      placement.Scale > 0.01f && placement.Scale < 100f
            ? placement.Scale
            : 1f;

        Basis basis = placement.HasRotation
            ? RotationBasis(placement.RotX, placement.RotY, placement.RotZ, scale)
            : YawBasis(placement.Yaw, scale);

        return new Transform3D(basis, new Vector3(placement.X, placement.Z, placement.Y));
    }

    public static Vector3 ToGodotPosition(float fableX, float fableY, float fableZ) =>
        new(fableX, fableZ, fableY);

    public static (float X, float Y, float Z) ToFablePosition(Vector3 godotOrigin) =>
        (godotOrigin.X, godotOrigin.Z, godotOrigin.Y);

    public static Basis RotationBasis(float rx, float ry, float rz, float scale = 1f)
    {
        if (!float.IsFinite(rx)) rx = 0f;
        if (!float.IsFinite(ry)) ry = 0f;
        if (!float.IsFinite(rz)) rz = 0f;

        float sx = MathF.Sin(rx);
        float cx = MathF.Cos(rx);
        float sy = MathF.Sin(ry);
        float cy = MathF.Cos(ry);
        float sz = MathF.Sin(rz);
        float cz = MathF.Cos(rz);

        Span<float> game = stackalloc float[9];
        game[0] = cy * cx;
        game[1] = sx;
        game[2] = -sy * cx;
        game[3] = (sy * sz) - (cy * cz * sx);
        game[4] = cz * cx;
        game[5] = (sy * cz * sx) + (cy * sz);
        game[6] = (cy * sz * sx) + (sy * cz);
        game[7] = -sz * cx;
        game[8] = (cy * cz) - (sy * sz * sx);

        ReadOnlySpan<int> map = stackalloc int[] { 0, 2, 1 };
        Span<float> m = stackalloc float[9];
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                m[(row * 3) + col] = game[(map[row] * 3) + map[col]] * scale;
            }
        }

        return ToGodotBasis(m);
    }

    public static Basis YawBasis(float yaw, float scale = 1f)
    {
        float s = MathF.Sin(yaw);
        float c = MathF.Cos(yaw);

        if (!float.IsFinite(s) || !float.IsFinite(c))
        {
            s = 0f;
            c = 1f;
        }

        return YawScaleBasis(s, c, scale, scale, scale);
    }

    public static Basis YawScaleBasis(float s, float c, float sx, float sy, float sz)
    {
        if (!float.IsFinite(s) || !float.IsFinite(c))
        {
            s = 0f;
            c = 1f;
        }

        Span<float> m = stackalloc float[]
        {
            c * sx, 0f, s * sy,
            0f, sz, 0f,
            -s * sx, 0f, c * sy,
        };
        return ToGodotBasis(m);
    }

    private static Basis ToGodotBasis(ReadOnlySpan<float> m) => new(
        new Vector3(m[0], m[3], m[6]),
        new Vector3(m[1], m[4], m[7]),
        new Vector3(-m[2], -m[5], -m[8]));

    /// <summary>
    /// Exact algebraic inverse of <see cref="BuildTransform"/> / <see cref="RotationBasis"/>.
    /// Extracts Fable 2 GDB coordinates <c>(X, Y, Z)</c>, Euler angles <c>(RotX, RotY, RotZ)</c>,
    /// and uniform <c>Scale</c> from a Godot region-space <see cref="Transform3D"/>.
    /// </summary>
    public static void ExtractGdbTransform(
        Transform3D godotTransform,
        out float x,
        out float y,
        out float z,
        out float rx,
        out float ry,
        out float rz,
        out float scale)
    {
        (x, y, z) = ToFablePosition(godotTransform.Origin);

        Basis b = godotTransform.Basis;
        Span<float> m = stackalloc float[9];
        m[0] = b.Column0.X;
        m[3] = b.Column0.Y;
        m[6] = b.Column0.Z;

        m[1] = b.Column1.X;
        m[4] = b.Column1.Y;
        m[7] = b.Column1.Z;

        m[2] = -b.Column2.X;
        m[5] = -b.Column2.Y;
        m[8] = -b.Column2.Z;

        float col0Len = MathF.Sqrt((m[0] * m[0]) + (m[3] * m[3]) + (m[6] * m[6]));
        scale = float.IsFinite(col0Len) ? col0Len : 1f;
        float invScale = scale > 0.0001f ? (1f / scale) : 0f;

        ReadOnlySpan<int> map = stackalloc int[] { 0, 2, 1 };
        Span<float> game = stackalloc float[9];
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                game[(map[row] * 3) + map[col]] = m[(row * 3) + col] * invScale;
            }
        }

        // game[1] = sin(rx)
        // game[0] = cy * cx, game[2] = -sy * cx
        // game[4] = cz * cx, game[7] = -sz * cx
        float sinX = Math.Clamp(game[1], -1f, 1f);
        rx = MathF.Asin(sinX);
        float cosX = MathF.Cos(rx);

        if (MathF.Abs(cosX) > 1e-5f)
        {
            ry = MathF.Atan2(-game[2], game[0]);
            rz = MathF.Atan2(-game[7], game[4]);
        }
        else
        {
            // Gimbal lock (|cos(rx)| ~ 0): set rz = 0 -> sz = 0, cz = 1 -> game[6] = sy, game[8] = cy
            rz = 0f;
            ry = MathF.Atan2(game[6], game[8]);
        }
    }

    /// <summary>
    /// Compares two region-space transforms within epsilon so organizational grouping or float
    /// round-tripping never triggers a false modification diff.
    /// </summary>
    public static bool AreTransformsEquivalent(
        Transform3D a,
        Transform3D b,
        float positionEpsilon = DefaultPositionEpsilon,
        float basisEpsilon = DefaultBasisEpsilon)
    {
        if (a.Origin.DistanceTo(b.Origin) > positionEpsilon)
        {
            return false;
        }

        return a.Basis.Column0.DistanceTo(b.Basis.Column0) <= basisEpsilon &&
               a.Basis.Column1.DistanceTo(b.Basis.Column1) <= basisEpsilon &&
               a.Basis.Column2.DistanceTo(b.Basis.Column2) <= basisEpsilon;
    }
}
