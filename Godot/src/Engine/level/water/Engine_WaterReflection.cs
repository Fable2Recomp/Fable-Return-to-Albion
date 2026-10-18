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

        /// <summary>Surfaces on the same plane, drawn with this body's mirror.</summary>
        public readonly List<MeshInstance3D> Sharers = new();

        public SubViewport Viewport = null!;
        public Camera3D Camera = null!;
        public float Height;

        /// <summary>The eye the mirror was last rendered from, and when.</summary>
        public Transform3D RenderedEye;
        public int RenderedFrame = int.MinValue;
    }

    private readonly List<Body> _bodies = new();

    /// <summary>Metres between two water planes that one mirror serves.</summary>
    private const float SharePlaneMetres = 0.25f;

    private int _shared;

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
    /// A mirror holds its image while the eye stands still: the reflected world is the static
    /// world and the sky (Engine_ReflectionLayer), so the same eye gives the same picture. It
    /// re-renders when the eye moves or turns by more than these, or after HoldFrames regardless,
    /// so cloud drift and lamp flicker in the reflection stay alive.
    /// </summary>
    private const float MoveMetres = 0.02f;
    private const float TurnCosine = 0.9999985f;
    public static int HoldFrames = 30;

    /// <summary>Mirror renders skipped because the eye had not moved, for the debug channel.</summary>
    public static long Held;

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

            // ONLY A SURFACE THAT SAMPLES A MIRROR GETS ONE: the classic water and the guest-mode
            // branch of water.gdshader. The modern branch reflects the dome and the screen and never
            // reads `reflection_tex`, so a mirror for it re-rendered the world for nothing.
            if (!material.HasMeta(Engine_ClassicWater.Meta) && !material.GetShaderParameter("guest_water").AsBool())
            {
                continue;
            }

            // ONE MIRROR PER PLANE. A planar reflection depends only on the plane, so a surface
            // within SharePlaneMetres of a mirror already made reads that one — the vista ocean
            // sits 0.1 below the body it extends, and a level's bodies often share a level.
            Body? same = _bodies.Find(b => System.MathF.Abs(b.Height - height) <= SharePlaneMetres);

            // THE VISTA OCEAN NEVER DRIVES A MIRROR. It spans the horizon, so it is always "in
            // view" even where the ground hides all of it — a mirror of its own, or a vote to
            // refresh a shared one, would re-render the scene for water nobody sees. It reads the
            // mirror of a body on its plane as that body last left it, or none at all.
            bool ocean = surface.Name == Refii.Level.Engine_LevelScene.VistaOceanNodeName;
            if (ocean && same is null)
            {
                material.SetShaderParameter("planar_reflection", false);
                continue;
            }

            if (same is not null)
            {
                material.SetShaderParameter("reflection_tex", same.Viewport.GetTexture());
                BindMirror(material, same.Viewport.GetTexture());
                material.SetShaderParameter("planar_reflection", true);
                if (!ocean)
                {
                    same.Sharers.Add(surface);
                }

                ++_shared;
                continue;
            }

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

                // LINEAR, NOT DISPLAY-ENCODED. An LDR viewport's 3D output is clamped to 1 and
                // sRGB-encoded into RGBA8; the water samples it as the linear colour the title's
                // reflection tile holds, so every reflected mid-tone read about 2.4x too bright and
                // the classic water's `oC0 *= c20.w` took it to white.
                UseHdr2D = true,

                // A 256-pixel mirror: the coarser LOD of anything whose detail is under four
                // of its pixels.
                MeshLodThreshold = ReflectionLodPixels,
            };

            var camera = new Camera3D
            {
                // ONLY THE REFLECTED LAYER (Engine_ReflectionLayer): terrain, vista, props, lamps,
                // clouds and the celestial elements opt in; entities, FX, HUD sprites and the
                // water itself never do, and no light's caster mask includes the bit, so a mirror
                // renders no sun cascades. The A/B lever restores "everything except the water".
                CullMask = Engine_ReflectionLayer.MirrorCullMask,
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

            BindMirror(material, viewport.GetTexture());
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
            GD.Print($"[water] {_bodies.Count} planar reflection(s) at {TileSize}x{TileSize}, " +
                $"{_shared} surface(s) sharing one");
        }
    }

    /// <summary>Re-applies the mirror cameras' cull mask after the lever moved.</summary>
    public void ApplyCullMask()
    {
        foreach (Body body in _bodies)
        {
            if (GodotObject.IsInstanceValid(body.Camera))
            {
                body.Camera.CullMask = Engine_ReflectionLayer.MirrorCullMask;
            }
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
            bool seen = Seen(body.Surface, frustum, eye.Origin);
            if (!seen)
            {
                for (int s = 0; s < body.Sharers.Count; ++s)
                {
                    if (Seen(body.Sharers[s], frustum, eye.Origin))
                    {
                        seen = true;
                        break;
                    }
                }
            }
            if (!seen)
            {
                continue;
            }

            ++candidates;
            if (chosen is null && Stale(body, eye))
            {
                chosen = body;
            }
        }

        _turn = candidates == 0 ? _turn : (_turn + 1) % _bodies.Count;
        if (chosen is null)
        {
            return;
        }

        chosen.Camera.Fov = fov;
        // THE SCREEN'S ASPECT. Both water shaders address the mirror by the screen position, so
        // it has to cover what the screen covers: square, it held only the middle of a 16:9 view
        // stretched across the whole width. The height stays the title's 256.
        Vector2 screen = _view.GetViewport()?.GetVisibleRect().Size ?? new Vector2(TileSize, TileSize);
        var size = new Vector2I(
            Mathf.Clamp(Mathf.RoundToInt(TileSize * screen.X / Mathf.Max(screen.Y, 1f)), 1, 4 * TileSize), TileSize);
        if (chosen.Viewport.Size != size)
        {
            chosen.Viewport.Size = size;
        }
        chosen.Camera.GlobalTransform = Mirror(eye, chosen.Height);
        chosen.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        chosen.RenderedEye = eye;
        chosen.RenderedFrame = _frame;
    }

    private static bool Seen(MeshInstance3D surface, Godot.Collections.Array<Plane> frustum, Vector3 eye) =>
        GodotObject.IsInstanceValid(surface) && surface.IsVisibleInTree() && InView(surface, frustum, eye);

    private bool Stale(Body body, in Transform3D eye)
    {
        if (_frame - body.RenderedFrame >= HoldFrames)
        {
            return true;
        }

        Transform3D last = body.RenderedEye;
        bool moved = last.Origin.DistanceSquaredTo(eye.Origin) > MoveMetres * MoveMetres ||
            last.Basis.Column2.Dot(eye.Basis.Column2) < TurnCosine ||
            last.Basis.Column1.Dot(eye.Basis.Column1) < TurnCosine;
        if (!moved)
        {
            ++Held;
        }

        return moved;
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

    /// <summary>
    /// The mirror as g_ReflectionSampler on every pass of a classic water material: the core
    /// and the blended fringe it chains (Engine_ClassicWater) both sample it.
    /// </summary>
    private static void BindMirror(ShaderMaterial material, Texture2D mirror)
    {
        for (Material? pass = material; pass is ShaderMaterial shader; pass = shader.NextPass)
        {
            shader.SetShaderParameter("ps_g_ReflectionSampler", mirror);
        }
    }
}
