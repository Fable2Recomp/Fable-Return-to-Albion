using System.Collections.Generic;
using Godot;

namespace Refii.Level;

public sealed partial class Engine_WaterReflection : Node
{
    public const string NodeName = "WaterReflections";

    /// <summary>The guest's own reflection tile size — `XGSurfaceSize(256, 256, ...)`.</summary>
    private const int TileSize = 256;

    /// <summary>
    /// The visual layer the water surfaces live on, so a reflection camera can exclude them.
    ///
    /// Layer 2 (bit 1) is free by inspection: nothing in the project assigns `VisualInstance3D.Layers`
    /// at all, so every other mesh sits on the default layer 1 and no convention can collide with
    /// this one. Water must be excluded or each body reflects itself — and with several bodies in a
    /// region, each other.
    /// </summary>
    public const uint WaterLayer = 2;

    private sealed class Body
    {
        public MeshInstance3D Surface = null!;
        public SubViewport Viewport = null!;
        public Camera3D Camera = null!;
        public float Height;
    }

    private readonly List<Body> _bodies = new();

    private Camera3D? _view;

    /// <summary>
    /// Mounts one mirrored viewport per water body. `water` is the container `Engine_LevelScene`
    /// builds; `view` is the camera the frame is drawn from.
    /// </summary>
    public void Mount(Node3D water, Camera3D view)
    {
        _view = view;

        foreach (Node child in water.GetChildren())
        {
            if (child is not MeshInstance3D surface ||
                surface.MaterialOverride is not ShaderMaterial material)
            {
                continue;
            }

            // The surface is flat, so any vertex gives the plane. The mesh is built in region
            // space and the container carries no transform, but the LEVEL ROOT mirrors X — so the
            // height has to be read through the node's own global transform rather than from the
            // authored BaseHeight, or a level whose root flips would place the mirror wrong.
            Aabb bounds = surface.GetAabb();
            float height = surface.GlobalTransform.Origin.Y +
                (bounds.Position.Y + (bounds.Size.Y * 0.5f));

            surface.Layers = WaterLayer;

            var viewport = new SubViewport
            {
                Size = new Vector2I(TileSize, TileSize),

                // SHARED WORLD, which is the opposite of what the front end's viewport wants. The
                // UI sets OwnWorld3D so its cameras cannot see the world; a reflection is nothing
                // BUT the world, so it must render into the same World3D the main camera does.
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
                TransparentBg = false,
                GuiDisableInput = true,
                HandleInputLocally = false,
                Msaa3D = Viewport.Msaa.Disabled,
            };

            var camera = new Camera3D
            {
                // Everything EXCEPT the water. Without this each body renders itself into its own
                // reflection, and every other body into it too.
                CullMask = ~WaterLayer,
                Near = view.Near,
                Far = view.Far,
                KeepAspect = Camera3D.KeepAspectEnum.Height,
                Current = true,

                // No Compositor and no Attributes on purpose — see the class comment. This camera
                // renders linear HDR for the water shader to compose; the main camera's exposure
                // pass tone-maps the result once, with the frame it lands in.
            };

            viewport.AddChild(camera);
            AddChild(viewport);

            material.SetShaderParameter("reflection_tex", viewport.GetTexture());
            material.SetShaderParameter("planar_reflection", true);

            _bodies.Add(new Body
            {
                Surface = surface,
                Viewport = viewport,
                Camera = camera,
                Height = height,
            });
        }

        if (_bodies.Count != 0)
        {
            GD.Print($"[water] {_bodies.Count} planar reflection(s) at {TileSize}x{TileSize}");
        }
    }

    public override void _Process(double delta)
    {
        if (_view is null || _bodies.Count == 0)
        {
            return;
        }

        Transform3D eye = _view.GlobalTransform;
        float fov = _view.Fov;

        foreach (Body body in _bodies)
        {
            // SKIP WHAT NOBODY CAN SEE. Each of these is a full scene traversal, and a region
            // carries several. `Always` keeps the target live only while we leave it that way.
            if (!body.Surface.IsVisibleInTree())
            {
                body.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
                continue;
            }

            body.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            body.Camera.Fov = fov;
            body.Camera.GlobalTransform = Mirror(eye, body.Height);
        }
    }

    /// <summary>
    /// The Householder reflection of a camera about the horizontal plane `y = height`.
    ///
    /// `sub_82AA22B0` builds `row_i = e_i - 2 * n_i * n`, which for a horizontal plane leaves X and
    /// Z alone and negates Y. The origin flips to `2h - y`.
    ///
    /// THE BASIS IS REBUILT, NOT NEGATED. Applying the mirror to the basis directly gives an
    /// IMPROPER basis — determinant -1 — because a reflection reverses handedness, and Godot
    /// derives the view matrix from that transform: triangle winding reverses, every backface
    /// becomes a front face, and the reflection renders inside-out. The guest flips its cull mode
    /// for the pass instead.
    ///
    /// Reflecting the FORWARD and UP vectors and rebuilding through LookingAt gives the identical
    /// mirrored view with a proper rotation, because `cross(mirror(a), mirror(b))` is
    /// `-mirror(cross(a, b))` — the two sign flips cancel and the frame comes back right-handed.
    /// Winding is then untouched and no cull state has to change. The image is therefore
    /// horizontally flipped against the guest's improper-basis render, and the shader samples the
    /// tile at 1-u to undo it.
    /// </summary>
    private static Transform3D Mirror(Transform3D eye, float height)
    {
        Vector3 origin = eye.Origin;
        origin.Y = (2f * height) - origin.Y;

        // Godot cameras look down their own -Z.
        Vector3 forward = -eye.Basis.Column2;
        Vector3 up = eye.Basis.Column1;

        forward.Y = -forward.Y;
        up.Y = -up.Y;

        // Degenerate when the camera looks straight down the mirror normal; hold the eye's own up.
        if (Mathf.Abs(forward.Normalized().Dot(up.Normalized())) > 0.999f)
        {
            up = Vector3.Up;
        }

        return new Transform3D(Basis.LookingAt(forward, up), origin);
    }
}
