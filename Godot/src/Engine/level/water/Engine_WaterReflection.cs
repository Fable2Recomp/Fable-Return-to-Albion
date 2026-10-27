using System.Collections.Generic;
using Godot;

namespace Refii.Level;

/// <summary>
/// The planar mirror each water body reflects: a SubViewport over the shared world, rendered from
/// the eye reflected about the body's plane, one mirror per view.
/// </summary>
/// <remarks>
/// A MIRROR PER VIEW. Both water shaders address the mirror by the screen position, so a mirror
/// is only right for the camera it was rendered for. On split screen
/// (<see cref="Refii.Core.Engine_Views"/>) each body gets a second mirror for player two's camera,
/// and player one's follows the camera that actually draws their half (FirstViewport's, at the
/// stated horizontal angle) rather than the window's. A material is shared by both views, so it
/// carries both mirrors and the eyes they were rendered for; water.gdshader takes the mirror of
/// the eye it is drawn from. Each view keeps the full cadence of its own.
/// </remarks>
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

    private sealed class MirrorRig
    {
        public SubViewport Viewport = null!;
        public Camera3D Camera = null!;

        /// <summary>The eye the mirror was last rendered from, and when.</summary>
        public Transform3D RenderedEye;
        public int RenderedFrame = int.MinValue;
    }

    private sealed class Body
    {
        public MeshInstance3D Surface = null!;

        /// <summary>Surfaces on the same plane, drawn with this body's mirror.</summary>
        public readonly List<MeshInstance3D> Sharers = new();

        /// <summary>Every material that reads this body's mirror: its own, its sharers', the vista ocean's.</summary>
        public readonly List<ShaderMaterial> Readers = new();

        /// <summary>The primary view's mirror, then player two's while the screen is split.</summary>
        public readonly MirrorRig?[] Mirrors = new MirrorRig?[2];

        public float Height;
    }

    private static readonly StringName SecondTextureName = "reflection_tex_second";
    private static readonly StringName SecondEnabledName = "second_reflection";
    private static readonly StringName FirstEyeName = "first_eye";
    private static readonly StringName SecondEyeName = "second_eye";

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

    /// <summary>
    /// The mirror's far plane. Under classic it is the view's own, as the title's mirror has none
    /// (`sub_82AA2540` builds it from the camera's near/far and culls with the five-plane
    /// `Render_BuildFrustum5_NearSides`): at 50 m a lake's far shore left the reflection and the
    /// horizon sky stood in for it. Classic casts through its own cascade world, so the full
    /// range carries none of Godot's sun-shadow cost the 50 m cut was made for.
    /// </summary>
    private static float MirrorFar(float viewFar) =>
        Refii.Models.Engine_ClassicMaterial.Active ? viewFar : Mathf.Min(viewFar, ReflectionFar);

    private readonly int[] _turn = new int[2];
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
                Texture2D shared = same.Mirrors[0]!.Viewport.GetTexture();
                material.SetShaderParameter("reflection_tex", shared);
                BindMirror(material, shared);
                material.SetShaderParameter("planar_reflection", true);
                same.Readers.Add(material);
                if (!ocean)
                {
                    same.Sharers.Add(surface);
                }

                ++_shared;
                continue;
            }

            MirrorRig mirror = NewMirror(view);
            material.SetShaderParameter("reflection_tex", mirror.Viewport.GetTexture());

            BindMirror(material, mirror.Viewport.GetTexture());
            material.SetShaderParameter("planar_reflection", true);

            var body = new Body
            {
                Surface = surface,
                Height = height,
            };
            body.Mirrors[0] = mirror;
            body.Readers.Add(material);
            _bodies.Add(body);
        }

        if (_bodies.Count != 0)
        {
            GD.Print($"[water] {_bodies.Count} planar reflection(s) at {TileSize}x{TileSize}, " +
                $"{_shared} surface(s) sharing one");
        }

        SyncViews();
    }

    public override void _EnterTree()
    {
        Refii.Core.Engine_Views.ViewsChanged += SyncViews;
    }

    public override void _ExitTree()
    {
        Refii.Core.Engine_Views.ViewsChanged -= SyncViews;
    }

    /// <summary>One mirror's viewport and camera, parented here; <paramref name="view"/> seeds its range.</summary>
    private MirrorRig NewMirror(Camera3D view)
    {
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
            Far = MirrorFar(view.Far),
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Current = true,

            // No Compositor and no Attributes on purpose — see the class comment. This camera
            // renders linear HDR for the water shader to compose; the main camera's exposure
            // pass tone-maps the result once, with the frame it lands in.
        };

        viewport.AddChild(camera);
        AddChild(viewport);
        return new MirrorRig { Viewport = viewport, Camera = camera };
    }

    /// <summary>
    /// Builds player two's mirror on every body when the screen splits and frees it when the
    /// screen rejoins. A water.gdshader material takes it as its second mirror and picks by eye;
    /// a classic water material takes it as its reflection sampler's second-view twin, which its
    /// composition fetches in player two's view (Engine_ClassicWater).
    /// </summary>
    private void SyncViews()
    {
        bool split = Refii.Core.Engine_Views.Split && _view is not null && GodotObject.IsInstanceValid(_view);
        foreach (Body body in _bodies)
        {
            MirrorRig? second = body.Mirrors[1];
            if (split && second is null)
            {
                second = NewMirror(_view!);
                body.Mirrors[1] = second;
                foreach (ShaderMaterial reader in body.Readers)
                {
                    if (!reader.HasMeta(Engine_ClassicWater.Meta))
                    {
                        reader.SetShaderParameter(SecondTextureName, second.Viewport.GetTexture());
                        reader.SetShaderParameter(SecondEnabledName, true);
                    }
                    else
                    {
                        BindMirror(reader, second.Viewport.GetTexture(), Engine_ClassicWater.ReflectionSamplerSecond);
                    }
                }
            }
            else if (!split && second is not null)
            {
                foreach (ShaderMaterial reader in body.Readers)
                {
                    if (!reader.HasMeta(Engine_ClassicWater.Meta))
                    {
                        reader.SetShaderParameter(SecondEnabledName, false);
                        reader.SetShaderParameter(SecondTextureName, default(Variant));
                    }
                    else
                    {
                        BindMirror(reader, null, Engine_ClassicWater.ReflectionSamplerSecond);
                    }
                }

                if (GodotObject.IsInstanceValid(second.Viewport))
                {
                    second.Viewport.QueueFree();
                }

                body.Mirrors[1] = null;
            }
        }
    }

    /// <summary>Re-applies the mirror cameras' cull mask after the lever moved.</summary>
    public void ApplyCullMask()
    {
        foreach (Body body in _bodies)
        {
            foreach (MirrorRig? mirror in body.Mirrors)
            {
                if (mirror is not null && GodotObject.IsInstanceValid(mirror.Camera))
                {
                    mirror.Camera.CullMask = Engine_ReflectionLayer.MirrorCullMask;
                }
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

        bool split = Refii.Core.Engine_Views.Split;
        if (split)
        {
            PublishEyes();
        }

        if (!Enabled || (++_frame % System.Math.Max(Cadence, 1)) != 0)
        {
            return;
        }

        for (int view = 0; view < (split ? 2 : 1); ++view)
        {
            if (TrySource(view, out Camera3D camera, out Vector2 screen))
            {
                RenderTurn(view, camera, screen);
            }
        }
    }

    /// <summary>
    /// The camera a view is drawn through and its screen size: the window's camera and visible
    /// rect, or on split screen each player's own camera and half.
    /// </summary>
    private bool TrySource(int view, out Camera3D camera, out Vector2 screen)
    {
        camera = null!;
        screen = new Vector2(TileSize, TileSize);
        if (view == 0)
        {
            if (Refii.Core.Engine_Views.FirstViewport is { } first && first.GetCamera3D() is { } own)
            {
                camera = own;
                screen = first.Size;
                return true;
            }

            camera = _view!;
            screen = _view!.GetViewport()?.GetVisibleRect().Size ?? screen;
            return true;
        }

        if (Refii.Core.Engine_Views.Second is not { } second || !GodotObject.IsInstanceValid(second) ||
            Refii.Core.Engine_Views.SecondViewport is not { } secondView)
        {
            return false;
        }

        camera = second;
        screen = secondView.Size;
        return true;
    }

    /// <summary>The eyes the two mirrors are rendered for, which water.gdshader chooses between per draw.</summary>
    private void PublishEyes()
    {
        if (!TrySource(0, out Camera3D first, out _) || !TrySource(1, out Camera3D second, out _))
        {
            return;
        }

        Vector3 firstEye = first.GlobalPosition;
        Vector3 secondEye = second.GlobalPosition;
        foreach (Body body in _bodies)
        {
            if (body.Mirrors[1] is null)
            {
                continue;
            }

            foreach (ShaderMaterial reader in body.Readers)
            {
                if (!reader.HasMeta(Engine_ClassicWater.Meta))
                {
                    reader.SetShaderParameter(FirstEyeName, firstEye);
                    reader.SetShaderParameter(SecondEyeName, secondEye);
                }
            }
        }
    }

    /// <summary>One view's turn: re-renders at most one of its mirrors that is in view, in reach and stale.</summary>
    private void RenderTurn(int view, Camera3D source, Vector2 screen)
    {
        Transform3D eye = source.GlobalTransform;
        float fov = source.Fov;
        Godot.Collections.Array<Plane> frustum = source.GetFrustum();

        // ONE MIRROR PER FRAME, and only one that is in view and in reach. Each is a full
        // scene traversal with the sun's cascades; a region carries several bodies, and
        // rendering them all every frame cost more than the frame itself. `Once` renders on
        // the next frame and keeps the texture, so the others show their last mirror.
        int candidates = 0;
        MirrorRig? chosen = null;
        Body? chosenBody = null;
        for (int i = 0; i < _bodies.Count; ++i)
        {
            Body body = _bodies[(_turn[view] + i) % _bodies.Count];
            if (body.Mirrors[view] is not { } mirror)
            {
                continue;
            }

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
            if (chosen is null && Stale(mirror, eye))
            {
                chosen = mirror;
                chosenBody = body;
            }
        }

        _turn[view] = candidates == 0 ? _turn[view] : (_turn[view] + 1) % _bodies.Count;
        if (chosen is null || chosenBody is null)
        {
            return;
        }

        // THE VIEW'S OWN PROJECTION: its angle, the axis that angle is measured on, and its range.
        chosen.Camera.Fov = fov;
        if (chosen.Camera.KeepAspect != source.KeepAspect)
        {
            chosen.Camera.KeepAspect = source.KeepAspect;
        }

        if (chosen.Camera.Near != source.Near)
        {
            chosen.Camera.Near = source.Near;
        }

        float far = MirrorFar(source.Far);
        if (chosen.Camera.Far != far)
        {
            chosen.Camera.Far = far;
        }

        // THE SCREEN'S ASPECT. Both water shaders address the mirror by the screen position, so
        // it has to cover what the screen covers: square, it held only the middle of a 16:9 view
        // stretched across the whole width. The height stays the title's 256.
        var size = new Vector2I(
            Mathf.Clamp(Mathf.RoundToInt(TileSize * screen.X / Mathf.Max(screen.Y, 1f)), 1, 4 * TileSize), TileSize);
        if (chosen.Viewport.Size != size)
        {
            chosen.Viewport.Size = size;
        }
        chosen.Camera.GlobalTransform = Mirror(eye, chosenBody.Height);
        chosen.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        chosen.RenderedEye = eye;
        chosen.RenderedFrame = _frame;
    }

    private static bool Seen(MeshInstance3D surface, Godot.Collections.Array<Plane> frustum, Vector3 eye) =>
        GodotObject.IsInstanceValid(surface) && surface.IsVisibleInTree() && InView(surface, frustum, eye);

    private bool Stale(MirrorRig mirror, in Transform3D eye)
    {
        if (_frame - mirror.RenderedFrame >= HoldFrames)
        {
            return true;
        }

        Transform3D last = mirror.RenderedEye;
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
    private static void BindMirror(ShaderMaterial material, Texture2D mirror) =>
        BindMirror(material, mirror, Engine_ClassicWater.ReflectionSampler);

    /// <summary>The same for a named sampler: the reflection's second-view twin takes player two's mirror.</summary>
    private static void BindMirror(ShaderMaterial material, Texture2D? mirror, string sampler)
    {
        for (Material? pass = material; pass is ShaderMaterial shader; pass = shader.NextPass)
        {
            shader.SetShaderParameter(sampler, mirror is null ? default(Variant) : mirror);
        }
        // The body's other pair (plain or MISTED): a node switched into a mist volume must not
        // draw with its reflection unbound.
        if (material.HasMeta(Engine_ClassicWater.MistedPairMeta) &&
            material.GetMeta(Engine_ClassicWater.MistedPairMeta).Obj is ShaderMaterial twin)
        {
            for (Material? pass = twin; pass is ShaderMaterial shader; pass = shader.NextPass)
            {
                shader.SetShaderParameter(sampler, mirror is null ? default(Variant) : mirror);
            }
        }
    }
}
