using Godot;

namespace Refii.Models;

public static class Engine_MdlPlacement
{
    public static readonly Basis HandednessBasis =
        Basis.Identity.Scaled(new Vector3(1.0f, 1.0f, -1.0f));

    public static Transform3D Handedness => new(HandednessBasis, Vector3.Zero);

    public static Transform3D FromGuest(Transform3D guest) =>
        Handedness * guest * Handedness;
}
