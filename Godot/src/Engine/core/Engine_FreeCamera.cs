using System;
using Godot;

namespace Refii.Core;

public static class Engine_FreeCamera
{
    public static Transform3D Step(ref Vector3 position, ref float yaw, ref float pitch,
        Vector3 movement, Vector2 turn, Vector2 mouse, float seconds, float speed, float roll = 0)
    {
        seconds = float.IsFinite(seconds) ? Math.Clamp(seconds, 0, 0.05f) : 0;
        yaw -= turn.X * seconds * 1.8f + mouse.X * 0.0025f;
        pitch = Math.Clamp(pitch - turn.Y * seconds * 1.8f - mouse.Y * 0.0025f, -1.55f, 1.55f);
        Basis basis = Basis.FromEuler(new Vector3(pitch, yaw, roll));
        if (movement.LengthSquared() > 1) movement = movement.Normalized();
        position += (basis.X * movement.X + Vector3.Up * movement.Y + basis.Z * movement.Z) * speed * seconds;
        return new Transform3D(basis, position);
    }
}
