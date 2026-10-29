using System;
using Godot;
using Refii.Core;

namespace Refii.Level;

public static class Engine_DecorateControls
{
    public static float Time(float seconds) => float.IsFinite(seconds) ? Math.Clamp(seconds, 0, 0.05f) : 0;

    public static Transform3D Camera(ref Vector3 position, ref float yaw, ref float pitch,
        Vector3 movement, Vector2 look, Vector2 mouse, float seconds, bool fine, bool fast)
    {
        return Engine_FreeCamera.Step(ref position, ref yaw, ref pitch, movement, look, mouse,
            seconds, fine ? 0.6f : fast ? 8 : 3);
    }

    public static Vector3 Carry(Transform3D before, Transform3D after, Vector3 piece, float depth)
    {
        if (before.IsEqualApprox(after) && depth == 0) return piece;
        Vector3 offset = before.AffineInverse() * piece;
        if (depth != 0) offset.Z = -Math.Clamp(-offset.Z + depth, 0.5f, 30);
        return after * offset;
    }

    public static float Rotation(bool left, bool right, float seconds, bool fine)
    {
        return ((right ? 1 : 0) - (left ? 1 : 0)) * Time(seconds) * (fine ? 0.25f : Mathf.Pi / 2);
    }

    public static bool PlanePoint(Vector3 origin, Vector3 direction, float height, out Vector3 point)
    {
        point = default;
        if (!origin.IsFinite() || !direction.IsFinite() || !float.IsFinite(height) || Math.Abs(direction.Y) < 0.001f) return false;
        float distance = (height - origin.Y) / direction.Y;
        if (distance < 0 || distance > Engine_DecoratePicking.Range) return false;
        point = origin + direction * distance;
        return point.IsFinite();
    }
}
