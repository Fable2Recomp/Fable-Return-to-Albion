using System;
using System.Globalization;
using Godot;
using Refii.Core;

namespace Refii.Level;

public static class Engine_DecorateControls
{
    public static float Time(float seconds) => float.IsFinite(seconds) ? Math.Clamp(seconds, 0, 0.05f) : 0;

    public static Transform3D Pose(Vector3 position, float yaw) => new(new Basis(Vector3.Up, -yaw), position);

    public static Transform3D Pose(Vector3 position, Vector3 angles) => new(Basis.FromEuler(new Vector3(angles.X, -angles.Y, angles.Z)), position);

    public static Vector3 InterpolateAngles(Vector3 from, Vector3 to, float weight) => from + new Vector3(
        Mathf.Wrap(to.X - from.X, -Mathf.Pi, Mathf.Pi), Mathf.Wrap(to.Y - from.Y, -Mathf.Pi, Mathf.Pi),
        Mathf.Wrap(to.Z - from.Z, -Mathf.Pi, Mathf.Pi)) * weight;

    public static Quaternion GuestRotation(Vector3 angles)
    {
        Quaternion q = Pose(Vector3.Zero, angles).Basis.GetRotationQuaternion();
        return new Quaternion(-q.X, q.Z, q.Y, q.W);
    }

    public static Vector3 Angles(Quaternion guest)
    {
        if (!guest.IsFinite() || guest.LengthSquared() < 0.000001f) return Vector3.Zero;
        Vector3 euler = new Basis(new Quaternion(-guest.X, guest.Z, guest.Y, guest.W).Normalized()).GetEuler();
        return new Vector3(euler.X, -euler.Y, euler.Z);
    }

    public static bool DragPoint(Vector3 origin, Vector3 direction, Vector3 anchor, Vector3 normal, out Vector3 point)
    {
        point = default;
        float denominator = direction.Dot(normal);
        if (!origin.IsFinite() || !direction.IsFinite() || !anchor.IsFinite() || Math.Abs(denominator) < 0.001f) return false;
        float distance = (anchor - origin).Dot(normal) / denominator;
        if (!float.IsFinite(distance) || distance < 0 || distance > Engine_DecoratePicking.Range) return false;
        point = origin + direction * distance;
        return point.IsFinite();
    }

    public static float RotationDegrees(float yaw, float origin) => Mathf.RadToDeg(Mathf.Wrap(yaw - origin, -Mathf.Pi, Mathf.Pi));

    public static bool TryRotation(string text, float origin, out float yaw)
    {
        yaw = origin;
        if (!float.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out float degrees) || !float.IsFinite(degrees) || !float.IsFinite(origin)) return false;
        yaw = Mathf.Wrap(origin + Mathf.DegToRad(degrees % 360), -Mathf.Pi, Mathf.Pi);
        return true;
    }

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
