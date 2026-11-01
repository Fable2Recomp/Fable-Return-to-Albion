using System;

namespace Refii.Physics;

/// <summary>
/// A cloth pose the guest states (Fable 3: its draw's publish, Fable3/src/hooks/src/cloth_hooks.cpp),
/// brought into the drawn mesh's space and sampled at the shared draw tick. Pure: no engine type.
/// </summary>
/// <remarks>
/// The guest publishes each piece once per render frame, stamped with the world's game tick; the
/// bridge keeps the newest pose of the current tick and the last pose of the tick before. The
/// shell draws every tick-stamped thing at one point on that timeline
/// (<see cref="Refii.Entities.Engine_DrawnFrame.DrawTick"/>), so the cloth is blended between those
/// two poses there, as a body's placement and its bone pose are.
/// </remarks>
public static class Engine_ClothStated
{
    /// <summary>
    /// The record a stated piece belongs to, in the model's record order (rigid records first,
    /// then skinned, as <see cref="Refii.Models.Engine_MdlEngine47"/> numbers them): the slot in
    /// the instance's rigid list, or past the rigid records in its skinned list.
    /// </summary>
    public static int Record(bool skinned, int slot, int rigidRecords) =>
        skinned ? rigidRecords + slot : slot;

    /// <summary>
    /// How far from the previous tick's pose toward the current one the draw tick falls, clamped
    /// to [0, 1]. 1 (the current pose) without a previous pose or a draw tick.
    /// </summary>
    public static float Fraction(double drawTick, uint previousTick, uint currentTick)
    {
        if (previousTick == 0u || currentTick <= previousTick || double.IsNaN(drawTick))
        {
            return 1.0f;
        }

        double t = (drawTick - previousTick) / (currentTick - previousTick);
        return (float)Math.Clamp(t, 0.0, 1.0);
    }

    /// <summary>
    /// Writes <paramref name="count"/> vertices — <paramref name="previous"/> blended toward
    /// <paramref name="current"/> by <paramref name="t"/> (previous may be empty: the current pose)
    /// — into the drawn mesh's space, (x, y, z) → (x, z, -y) as the mesh reader maps every vertex
    /// (<see cref="Engine_ClothBuilder.ToMeshSpace(in PhysVec4)"/>). Normals are blended and left
    /// unnormalised (the packer's octahedral encode divides by their length); a tangent's
    /// handedness is the current pose's. Returns the bounds of the positions.
    /// </summary>
    public static (PhysVec4 Min, PhysVec4 Max) Blend(
        ReadOnlySpan<float> previous, ReadOnlySpan<float> current, float t, int count,
        PhysVec4[] positions, PhysVec4[] normals, PhysVec4[] tangents)
    {
        const int Stride = 12;
        count = Math.Min(count, Math.Min(positions.Length, Math.Min(normals.Length, tangents.Length)));
        count = Math.Min(count, current.Length / Stride);
        bool blend = t < 1.0f && previous.Length >= count * Stride;

        var min = new PhysVec4(float.MaxValue, float.MaxValue, float.MaxValue, 0.0f);
        var max = new PhysVec4(float.MinValue, float.MinValue, float.MinValue, 0.0f);
        for (int v = 0; v < count; v++)
        {
            ReadOnlySpan<float> c = current.Slice(v * Stride, Stride);
            PhysVec4 p = Lane(c, 0, 1.0f);
            PhysVec4 n = Lane(c, 4, 0.0f);
            PhysVec4 tan = Lane(c, 8, c[11]);
            if (blend)
            {
                ReadOnlySpan<float> o = previous.Slice(v * Stride, Stride);
                p = Lerp(Lane(o, 0, 1.0f), p, t);
                n = Lerp(Lane(o, 4, 0.0f), n, t);
                tan = Lerp(Lane(o, 8, c[11]), tan, t);
            }

            p = Engine_ClothBuilder.ToMeshSpace(p);
            positions[v] = p;
            normals[v] = Engine_ClothBuilder.ToMeshSpace(n);
            tangents[v] = Engine_ClothBuilder.ToMeshSpace(tan);
            min = new PhysVec4(MathF.Min(min.X, p.X), MathF.Min(min.Y, p.Y), MathF.Min(min.Z, p.Z), 0.0f);
            max = new PhysVec4(MathF.Max(max.X, p.X), MathF.Max(max.Y, p.Y), MathF.Max(max.Z, p.Z), 0.0f);
        }

        return (min, max);
    }

    private static PhysVec4 Lane(ReadOnlySpan<float> vertex, int at, float w) =>
        new(vertex[at], vertex[at + 1], vertex[at + 2], w);

    private static PhysVec4 Lerp(in PhysVec4 a, in PhysVec4 b, float t) =>
        new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t), b.W);
}
