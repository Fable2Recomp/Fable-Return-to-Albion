using Godot;

namespace Refii.Models;

/// <summary>
/// The one place that converts Fable 2's coordinate system into Godot's.
///
/// The guest is Direct3D on Xbox 360 and therefore LEFT-handed; Godot is right-handed.
/// Ignore that and every model renders mirrored — which is easy to miss, because a
/// mirrored chair still looks like a chair. It shows up much later as text reading
/// backwards on a signpost, or a level whose doors are on the wrong side.
///
/// The flip belongs in the TRANSFORM, never in the vertex data. Baking a negation into
/// positions leaves the winding order reversed, so every triangle faces backwards and
/// gets backface-culled; fixing that in turn means reversing every index buffer, and any
/// UV or tangent written alongside has to be corrected to match. Godot already handles
/// all of it for a negative-determinant basis. An earlier pass baked handedness into the
/// UVs instead and produced exactly that class of silent mirroring, which is why this is
/// a shared helper and not a line repeated at each call site.
/// </summary>
public static class Engine_MdlPlacement
{
    /// <summary>
    /// Basis that maps guest space to Godot space. Negating Z is the standard
    /// left-to-right-handed conversion and keeps the up axis and ground plane intact, so
    /// heights and floor placement carry over unchanged.
    /// </summary>
    public static readonly Basis HandednessBasis =
        Basis.Identity.Scaled(new Vector3(1.0f, 1.0f, -1.0f));

    /// <summary>The guest-to-Godot transform with no additional placement applied.</summary>
    public static Transform3D Handedness => new(HandednessBasis, Vector3.Zero);

    /// <summary>
    /// Wraps a transform expressed in GUEST space so it can be applied to a Godot node.
    /// Use this for anything positioned by the guest — models, lights, cameras — so the
    /// conversion stays identical across all of them.
    /// </summary>
    public static Transform3D FromGuest(Transform3D guest) =>
        Handedness * guest * Handedness;
}
