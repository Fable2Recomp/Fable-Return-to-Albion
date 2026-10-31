using System;
using Godot;

namespace Refii.Level;

/// <summary>Where a celestial_overlay quad lands on a view, as its vertex stage places it.</summary>
public static class Engine_CelestialOverlayReach
{
    /// <summary>
    /// Whether a celestial_overlay quad can put a pixel inside the view widened by
    /// <paramref name="margin"/> NDC units on every side, placed as its vertex stage places it:
    /// collapsed when the element is behind the eye, else the quad of half size
    /// <paramref name="half"/> at <paramref name="distance"/> along the view-space direction, its
    /// depth held in front of the eye. Conservative where a corner does not project. Pure.
    /// </summary>
    /// <param name="dirView">The element's unit direction in view space.</param>
    /// <param name="facingMargin">How far behind the eye (in -z of the unit direction) still counts.</param>
    public static bool MayReach(
        Vector3 dirView, float distance, Vector2 half, Projection projection, float margin, float facingMargin)
    {
        float facing = -dirView.Z;
        if (!float.IsFinite(facing))
        {
            return true;
        }

        if (facing <= 0f)
        {
            return facing > -facingMargin;
        }

        Vector3 centre = dirView * distance;
        float z = MathF.Min(centre.Z, -0.01f);
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        for (int corner = 0; corner < 4; corner++)
        {
            float sx = (corner & 1) == 0 ? -1f : 1f;
            float sy = (corner & 2) == 0 ? -1f : 1f;
            Vector4 clip = projection * new Vector4(centre.X + sx * half.X, centre.Y + sy * half.Y, z, 1f);
            if (!(clip.W > 0f))
            {
                return true;
            }

            float x = clip.X / clip.W, y = clip.Y / clip.W;
            if (!float.IsFinite(x) || !float.IsFinite(y))
            {
                return true;
            }

            minX = MathF.Min(minX, x);
            maxX = MathF.Max(maxX, x);
            minY = MathF.Min(minY, y);
            maxY = MathF.Max(maxY, y);
        }

        float edge = 1f + margin;
        return maxX >= -edge && minX <= edge && maxY >= -edge && minY <= edge;
    }
}
