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

    /// <summary>Beyond this, a mirror is a few pixels of sky: leave its last frame.</summary>
    private const float ReachMetres = 160f;

    /// <summary>Mesh LOD threshold in the mirror's own pixels: the guest's switch distances
    /// scaled to 256 lines would be 0.36; this is roughly five times sooner.</summary>
    private const float ReflectionLodPixels = 4f;

    /// <summary>How far a reflection camera sees. Not the main camera's range — see its use.</summary>
    private const float ReflectionFar = 50f;

    private int _turn;
    private int _frame;

    /// <summary>Live levers: off, or one mirror every <see cref="Cadence"/> frames.</summary>
    public static bool Enabled = true;
    public static int Cadence = 4;

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
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
                TransparentBg = false,

                // NO POSITIONAL SHADOWS IN A REFLECTION. Each viewport re-renders the region's
                // eight shadowed lamps for a 256-pixel mirror; measured 2026-09-04 in the
                // market at night, three reflections drew 230M of the frame's 328M primitives
                // and held the game at 8 fps with no water on screen.
                PositionalShadowAtlasSize = 0,
                GuiDisableInput = true,
                HandleInputLocally = false,
                Msaa3D = Viewport.Msaa.Disabled,

                // A 256-pixel mirror: the coarser LOD of anything whose detail is under four
                // of its pixels.
                MeshLodThreshold = ReflectionLodPixels,
            };

            var camera = new Camera3D
            {
                // Everything EXCEPT the water. Without this each body renders itself into its own
                // reflection, and every other body into it too.
                CullMask = ~WaterLayer,
                Near = view.Near,

                // A 256-PIXEL MIRROR DOES NOT NEED THE HORIZON. Inheriting the main camera's
                // far plane made every reflection a full-range scene traversal: measured in the
                // market, one 256x256 mirror drew 8.6M visible primitives and 26M shadow
                // primitives, because the sun's cascades cover whatever the view frustum covers.
                // Everything a player can actually make out in a rippling 256-pixel reflection
                // is within a few dozen metres of the water.
                Far = Mathf.Min(view.Far, ReflectionFar),
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
    	using var _framePhase = Refii.Core.Engine_FramePhases.Measure("waterreflection");
        if (_view is null || _bodies.Count == 0)
        {
            return;
        }

        if (!Enabled || (++_frame % System.Math.Max(Cadence, 1)) != 0)
        {
            return;
        }

        Transform3D eye = _view.GlobalTransform;
        float fov = _view.Fov;
        Godot.Collections.Array<Plane> frustum = _view.GetFrustum();

        // ONE MIRROR PER FRAME, and only one that is in view and in reach. Each is a full
        // scene traversal with the sun's cascades; a region carries several bodies, and
        // rendering them all every frame cost more than the frame itself. `Once` renders on
        // the next frame and keeps the texture, so the others show their last mirror.
        int candidates = 0;
        Body? chosen = null;
        for (int i = 0; i < _bodies.Count; ++i)
        {
            Body body = _bodies[(_turn + i) % _bodies.Count];
            if (!body.Surface.IsVisibleInTree() || !InView(body.Surface, frustum, eye.Origin))
            {
                continue;
            }

            ++candidates;
            chosen ??= body;
        }

        _turn = candidates == 0 ? _turn : (_turn + 1) % _bodies.Count;
        if (chosen is null)
        {
            return;
        }

        chosen.Camera.Fov = fov;
        chosen.Camera.GlobalTransform = Mirror(eye, chosen.Height);
        chosen.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
    }

    /// <summary>Whether a surface's world bounds touch the frustum and lie within reach.</summary>
    private static bool InView(MeshInstance3D surface, Godot.Collections.Array<Plane> frustum, Vector3 eye)
    {
        Aabb bounds = surface.GlobalTransform * surface.GetAabb();
        Vector3 nearest = eye.Clamp(bounds.Position, bounds.End);
        if (nearest.DistanceTo(eye) > ReachMetres)
        {
            return false;
        }

        foreach (Plane plane in frustum)
        {
            bool allOutside = true;
            for (int c = 0; c < 8 && allOutside; ++c)
            {
                if (!plane.IsPointOver(bounds.GetEndpoint(c)))
                {
                    allOutside = false;
                }
            }

            if (allOutside)
            {
                return false;
            }
        }

        return true;
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
