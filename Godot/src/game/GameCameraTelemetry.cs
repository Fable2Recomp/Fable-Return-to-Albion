using Godot;
using System;
using System.Collections.Generic;

namespace Refii.Game;

/// <summary>Describes a drawn camera without changing its transform or publishing guest input.</summary>
/// <remarks>Guest world (X right, Y forward, Z up) maps to Godot (-X,Z,Y). The mounted region
/// adds its own X reflection, so its local axes normally read (guest X,height,guest Y).
/// Actual region inverse transforms are used rather than assuming that reflection at every call.</remarks>
internal static class GameCameraTelemetry
{
    /// <summary>Builds finite, numeric six-decimal camera axes, with explicit unavailable local mappings.</summary>
    /// <param name="camera">Actual current Camera3D global transform, or null when none is active.</param>
    /// <param name="fovDegrees">The drawn camera's vertical FOV; unavailable values become null.</param>
    /// <param name="region">Actual active region global transform; a singular or nonfinite one is unavailable.</param>
    /// <param name="usesGuestWorldMap">Whether the active camera and region belong to the same production GameWorld.</param>
    /// <returns>A detached JSON-ready snapshot, or null for an absent/invalid camera.</returns>
    internal static Dictionary<string, object?>? Describe(Transform3D? camera, float? fovDegrees,
        Transform3D? region, bool usesGuestWorldMap)
    {
        if (camera is not { } at || !at.Origin.IsFinite() || !at.Basis.IsFinite() ||
            !TryDirection(-at.Basis.Z, out Vector3 forward) ||
            !TryDirection(at.Basis.X, out Vector3 right)) return null;

        Dictionary<string, object?>? local = null;
        string? regionError = null;
        if (region is { } frame)
        {
            float determinant = frame.Basis.Determinant();
            if (frame.Origin.IsFinite() && frame.Basis.IsFinite() &&
                float.IsFinite(determinant) && determinant != 0f)
            {
                Transform3D inverse = frame.AffineInverse();
                Vector3 position = inverse * at.Origin;
                if (position.IsFinite() && TryDirection(inverse.Basis * forward, out Vector3 localForward) &&
                    TryDirection(inverse.Basis * right, out Vector3 localRight))
                    local = Pose(position, localForward, localRight);
            }
            if (local is null) regionError = "non-finite or singular region transform";
        }

        return new Dictionary<string, object?>
        {
            ["global"] = Pose(at.Origin, forward, right),
            ["guestWorld"] = usesGuestWorldMap && local is not null
                ? Pose(ToGuest(at.Origin), ToGuest(forward), ToGuest(right)) : null,
            ["regionLocal"] = local,
            ["regionAvailable"] = local is not null,
            ["regionError"] = regionError,
            ["fovDegrees"] = fovDegrees is { } fov && float.IsFinite(fov) ? Math.Round((double)fov, 6) : null,
            ["source"] = "current viewport Camera3D drawn GlobalTransform; region point uses affine inverse and directions use inverse basis",
            ["axes"] = "Godot world; guestWorld=(-globalX,globalZ,globalY); regionLocal uses actual region inverse, normally (guestX,height,guestY)",
        };
    }

    /// <summary>Inverts the production GameWorld.GuestToGodot rotation for a point or direction.</summary>
    private static Vector3 ToGuest(Vector3 value) => new(-value.X, value.Z, value.Y);

    /// <summary>Normalizes a finite nonzero direction without allowing overflow to appear as a valid zero.</summary>
    private static bool TryDirection(Vector3 value, out Vector3 unit)
    {
        unit = Vector3.Zero;
        float length = value.Length();
        if (!value.IsFinite() || !float.IsFinite(length) || length <= 0f) return false;
        unit = value / length;
        return unit.IsFinite();
    }

    /// <summary>Snapshots one coordinate frame using JSON objects with numeric components.</summary>
    private static Dictionary<string, object?> Pose(Vector3 position, Vector3 forward, Vector3 right) => new()
    {
        ["position"] = Components(position), ["forward"] = Components(forward), ["right"] = Components(right),
    };

    /// <summary>Rounds finite components without replacing numeric values with formatted strings.</summary>
    private static Dictionary<string, double> Components(Vector3 value) => new()
    {
        ["x"] = Math.Round((double)value.X, 6),
        ["y"] = Math.Round((double)value.Y, 6),
        ["z"] = Math.Round((double)value.Z, 6),
    };
}
