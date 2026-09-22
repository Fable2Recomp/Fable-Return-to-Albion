using System;
using Godot;

namespace Refii.Entities;

/// <summary>
/// The guest's targeting-highlight buffer: every highlighted object redrawn into a cleared
/// half-resolution surface, which the post chain resolves, bleeds and composites around it.
/// </summary>
/// <remarks>
/// `sub_82197AE8` runs the pass. `sub_82A6A1D0` allocates the target at `qword_834A5FE8 / 2` in
/// each axis, clears colour, depth and stencil, and binds a half-res depth-stencil of its own, so
/// the hardware z-test only sorts the highlighted objects against each other; occlusion by the
/// scene is the silhouette shader's clip. `sub_82A6A378` redraws each enrolled object with the
/// pass index at `drawCtx + 0x6FC` set to 5, which `sub_822D7530` maps to bucket 9 and the
/// `OUTLINEMATERIAL` shaders. Enrolment IS the colour: `sub_825738F0` reaching `sub_82A79678`
/// sets `*(proxy + 28) |= 0x80`, and the gather `sub_822975F8(scene, sub_82A6A1C8, 2, 0x80, 0)`
/// selects on it.
///
/// The object draws `g_OutliningColour`, built per draw by `sub_82B24BE0` (binding 166):
/// rgb from the renderable's +0x100 — the stated bytes in the order `(+98, +97, +96) / 255`,
/// packed by `sub_82572C18` at 0x82572d8c..0x82572e58 — and alpha
/// `max(1 - 0.02 * distance, 0.1) * lodFade`, the distance from the camera. The stated fourth
/// byte never reaches the shader. Here the proxies draw a SLOT and the colour and alpha travel in
/// <see cref="Target"/>, so nothing about the look depends on Godot's colour pipeline.
///
/// Native shape, the same as <see cref="Refii.Fx.Engine_FxAdditiveLayer"/>: a SubViewport sharing
/// the world at half the host's size, a camera copying the main camera, a render layer only this
/// viewport's camera sees, and the texture published for the post chain on the render thread.
/// </remarks>
public partial class Engine_EntityHighlightLayer : Node
{
    /// <summary>18: 19 is the lamp host, 20 displacement, 21 the additive effects layer.</summary>
    public const int Layer = 18;

    public const uint LayerMask = 1u << (Layer - 1);

    /// <summary>Highlighted objects drawn at once. The guest has no cap; two players each
    /// fading one target out while another fades in is four.</summary>
    public const int Slots = 8;

    /// <summary>One highlighted object as the post chain needs it.</summary>
    /// <param name="Colour">rgb and alpha of `g_OutliningColour`, quantised to 1/255.</param>
    /// <param name="Min">World-space box, low corner.</param>
    /// <param name="Max">World-space box, high corner.</param>
    public readonly record struct Target(bool Present, Vector4 Colour, Vector3 Min, Vector3 Max);

    /// <summary>`flt_8209CEC4`: the alpha lost per unit of camera distance.</summary>
    private const float AlphaPerUnit = 0.02f;

    /// <summary>`flt_8209B650`: the alpha floor.</summary>
    private const float AlphaFloor = 0.1f;

    private static readonly object Gate = new();
    private static Engine_EntityHighlightLayer? _current;
    private static Rid _publishedRd;
    private static bool _publishedActive;
    private static float _publishedScale = 1f;
    private static readonly Target[] _published = new Target[Slots];

    /// <summary>The guest's blur width this frame, in half-res texels; 0 until it has stated
    /// one. Reported on the debug channel beside the silhouette counts.</summary>
    public static int WidthTexels { get; private set; }

    private SubViewport _viewport = null!;
    private Camera3D _camera = null!;
    private Viewport? _host;
    private Vector2I _size;
    private bool _enabled = true;
    private bool _usable;

    private Rid _layerRd;
    private Vector2I _layerRdSize;

    private bool HostAlive => _host is not null && GodotObject.IsInstanceValid(_host);

    public static Engine_EntityHighlightLayer? Current => _current;

    /// <summary>Whether the viewport texture resolved for the post chain.</summary>
    public bool Usable => _usable;

    public Vector2I Size => _size;

    /// <summary>The layer's pixels, for the debug channel — a GPU readback, main thread.</summary>
    public Image? Capture() => _viewport?.GetTexture()?.GetImage();

    /// <summary>Whether silhouettes should be built on the layer this frame.</summary>
    public static bool Present =>
        _current is not null && GodotObject.IsInstanceValid(_current) && _current.IsInsideTree() &&
        _current._enabled && _current._usable && Refii.Lighting.Engine_PostFxParams.Enabled &&
        RenderingServer.GetRenderingDevice() is not null;


    /// <summary>
    /// The layer texture for the post chain, valid only when something was drawn on it this
    /// frame. Render-thread safe: plain values under a lock.
    /// </summary>
    public static bool TrySnapshot(out Rid layer, out float scale, Target[] targets)
    {
        lock (Gate)
        {
            layer = _publishedRd;
            scale = _publishedScale;
            System.Array.Copy(_published, targets, Slots);
            return layer.IsValid && _publishedActive;
        }
    }

    public void SetEnabled(bool on)
    {
        _enabled = on;
        if (_viewport is not null)
        {
            _viewport.RenderTargetUpdateMode = on ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        }
    }

    public static void Ensure(Node context)
    {
        if (_current is not null && GodotObject.IsInstanceValid(_current)) return;
        if (RenderingServer.GetRenderingDevice() is null) return;
        SceneTree? tree = context.GetTree();
        if (tree is null) return;
        Viewport host = context.GetViewport() ?? tree.Root;
        var node = new Engine_EntityHighlightLayer { Name = "EntityHighlightLayer", _host = host };
        _current = node;
        tree.Root.CallDeferred(Node.MethodName.AddChild, node);
    }

    public override void _Ready()
    {
        _host ??= GetViewport();

        // Cleared to zero, the guest's own clear (`sub_82A6A1D0`, 0x82a6a28c..0x82a6a2a4). Only
        // R and G are read — the slot and the silhouette — so the alpha channel never matters.
        _viewport = new SubViewport
        {
            Name = "HighlightViewport",
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            Msaa3D = Viewport.Msaa.Disabled,
            UseDebanding = false,
            UseHdr2D = true,
            PositionalShadowAtlasSize = 0,
            World3D = _host!.World3D,
        };

        // Linear and unexposed: the slot is an index, and any grading would move it.
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0f, 0f, 0f, 1f),
            TonemapMode = Godot.Environment.ToneMapper.Linear,
            TonemapExposure = 1f,
            AmbientLightSource = Godot.Environment.AmbientSource.Disabled,
            GlowEnabled = false,
        };

        _camera = new Camera3D { Name = "HighlightCamera", CullMask = LayerMask, Environment = env };
        _viewport.AddChild(_camera);
        AddChild(_viewport);
        _camera.Current = true;

        // After the entity walk has placed and posed (GameEntities runs at default priority) and
        // before the FX layers at 900/1000: the texture published here must be this frame's.
        ProcessPriority = 850;
        Resize();
        _usable = LayerRd().IsValid;
        if (!_usable)
        {
            GD.Print("[highlight] layer: the viewport texture has no render-device texture; " +
                "the targeting glow stays off");
        }
    }

    private Rid LayerRd() =>
        RenderingServer.TextureGetRdTexture(RenderingServer.ViewportGetTexture(_viewport.GetViewportRid()));

    /// <summary>The layer's render-device texture, asked for once per size: each query behind
    /// <see cref="LayerRd"/> waits for the render thread.</summary>
    private Rid CachedLayerRd()
    {
        Vector2I size = _viewport.Size;
        if (!_layerRd.IsValid || size != _layerRdSize)
        {
            _layerRd = LayerRd();
            _layerRdSize = size;
        }

        return _layerRd;
    }

    public override void _ExitTree()
    {
        if (_current == this)
        {
            _current = null;
        }

        lock (Gate)
        {
            _publishedRd = default;
            _publishedActive = false;
        }
    }

    /// <summary>Half the host, truncated, which is the size the guest allocates:
    /// `qword_834A5FE8 / 2` in both axes at 0x82a6a214 and 0x82a6a220. The bleed is counted in
    /// texels of THIS buffer, so its size is part of the contract rather than a quality dial.</summary>
    private void Resize()
    {
        if (!HostAlive) return;
        Vector2 v = _host!.GetVisibleRect().Size;
        var size = new Vector2I(
            Math.Max((int)v.X / 2, 1),
            Math.Max((int)v.Y / 2, 1));
        if (size == _size) return;
        _size = size;
        _viewport.Size = size;
    }

    public override void _Process(double delta)
    {
        using var _framePhase = Refii.Core.Engine_FramePhases.Measure("entityhighlightlayer");
        if (!HostAlive) return;
        Resize();
        Camera3D? main = _host!.GetCamera3D();
        if (main is null)
        {
            return;
        }

        if ((main.CullMask & LayerMask) != 0) main.CullMask &= ~LayerMask;

        // Active while any slot has a target, not while the walk last touched one: the walk
        // need not run every frame, and a highlight lasts until the guest clears it.
        var targets = new Target[Slots];
        bool active = _enabled && Gather(main.GlobalPosition, targets);
        _viewport.RenderTargetUpdateMode = active ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;

        // The stated width, read here because this is the main thread; the post chain runs on
        // the render thread and must not reach into the guest.
        if (active)
        {
            int texels = Refii.Host.HostBridge.HighlightWidthTexels();
            if (texels > 0)
            {
                WidthTexels = texels;
            }
        }

        float scale = WidthScale(WidthTexels > 0 ? WidthTexels : 1);

        Rid rd = default;
        if (active)
        {
            _camera.GlobalTransform = main.GlobalTransform;
            _camera.Projection = main.Projection;
            _camera.Fov = main.Fov;
            _camera.Size = main.Size;
            _camera.Near = main.Near;
            _camera.Far = main.Far;
            _camera.KeepAspect = main.KeepAspect;
            _camera.HOffset = main.HOffset;
            _camera.VOffset = main.VOffset;
            rd = CachedLayerRd();
            _usable = rd.IsValid;
        }

        lock (Gate)
        {
            _publishedRd = rd;
            _publishedScale = scale;
            System.Array.Copy(targets, _published, Slots);
            _publishedActive = active && rd.IsValid;
        }
    }

    /// <summary>
    /// `g_OutlineScaleFactor`, the gain both bleed passes apply, from the stated width.
    /// </summary>
    /// <remarks>
    /// 0x82197c08..0x82197c90: `n = min(width, 16)`, `t = (1 - n/16) * 7`, then
    /// `kernel[7] / lerp(kernel[trunc t], kernel[trunc t + 1], frac t)` over the unnormalised
    /// kernel `1 / exp((i - 7)^2 / 49)` — the normalisation cancels in the ratio. Width 4 gives
    /// 1.068, width 16 gives e.
    /// </remarks>
    public static float WidthScale(int width)
    {
        float n = Math.Min(width, 16);
        float t = (1f - n * 0.0625f) * 7f;
        int i = (int)t;
        float frac = t - i;
        float a = Kernel(i);
        float lerp = a + (Kernel(i + 1) - a) * frac;
        return 1f / lerp;
    }

    private static float Kernel(int i) => 1f / MathF.Exp((i - 7) * (i - 7) / 49f);

    private static float Quantise(float v) => MathF.Round(Math.Clamp(v, 0f, 1f) * 255f) / 255f;

    /// <summary>Each lit slot's colour, alpha and world box, for this frame's camera. False
    /// when no slot is lit.</summary>
    private static bool Gather(Vector3 camera, Target[] targets)
    {
        bool any = false;
        foreach (Engine_EntityHighlight.Lit lit in Engine_EntityHighlight.Active())
        {
            if (lit.Slot < 1 || lit.Slot > Slots || !TryBounds(lit.Node, out Aabb box))
            {
                continue;
            }

            uint w = lit.Word;
            float alpha = MathF.Max(1f - AlphaPerUnit * lit.Node.GlobalPosition.DistanceTo(camera), AlphaFloor);
            var colour = new Vector4(
                ((w >> 8) & 0xFF) / 255f,
                ((w >> 16) & 0xFF) / 255f,
                (w >> 24) / 255f,
                Quantise(alpha));
            targets[lit.Slot - 1] = new Target(true, colour, box.Position, box.End);
            any = true;
        }

        return any;
    }

    /// <summary>
    /// The world box of every visible body mesh under <paramref name="node"/> — the host's
    /// equivalent of the owner box `sub_82A69D40` projects (owner+80 min, +96 max).
    /// </summary>
    private static bool TryBounds(Node3D node, out Aabb box)
    {
        box = default;
        bool any = false;
        var stack = new System.Collections.Generic.Stack<Node>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            Node n = stack.Pop();
            if (n is MeshInstance3D mesh && mesh.Mesh is not null && mesh.IsVisibleInTree() &&
                mesh.CastShadow != GeometryInstance3D.ShadowCastingSetting.ShadowsOnly &&
                (mesh.Layers & LayerMask) == 0)
            {
                Aabb world = mesh.GlobalTransform * mesh.GetAabb();
                box = any ? box.Merge(world) : world;
                any = true;
            }

            foreach (Node child in n.GetChildren())
            {
                stack.Push(child);
            }
        }

        return any;
    }
}
