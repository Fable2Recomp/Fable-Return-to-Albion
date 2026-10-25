using System;
using Godot;

namespace Refii.Fx;

/// <summary>
/// The guest's ADDITIVE EFFECTS surface, <c>off_8331AA14</c>: every ONE/ONE particle draws
/// into this half-res layer instead of the scene, and the layer joins the scene once, clamped
/// to [0,1] per channel, before the bloom extract and the tonemap.
/// </summary>
/// <remarks>
/// The guest's particle submit (<c>sub_822749E0</c>, 0x82274b88) routes a material whose
/// SrcBlend and DestBlend are both ONE to registry bucket 5; <c>Render_ResolveGlobalTargetPass</c>
/// (0x822C4290) binds the surface <c>sub_8227A440</c> allocated at half size in format code 2 —
/// D3DFMT_A8R8G8B8, UNORM — clears it to (0,0,0,0) each frame and drains the bucket into it
/// with ZEnable on and ZWrite off. The UNORM target saturates, so stacked quads cap at 1.0
/// rather than accumulating. Everything else (mix, SRCALPHA:ONE, the displacement pass) draws
/// into the FP16 scene.
///
/// Native shape, the same as <see cref="Engine_FxDisplacement"/>: a SubViewport sharing the
/// world, a camera that copies the main camera each frame, a render layer only this viewport's
/// camera sees, and the scene depth from <see cref="Engine_FxDepthCaptureEffect"/> for the
/// z-test. The post-FX chain reads the viewport's texture on the render thread
/// (<see cref="TrySnapshot"/>) and adds it into the colour buffer with
/// <c>fable_postfx_addlayer.glsl</c>. Without the post chain the layer would never reach the
/// screen, so <see cref="Present"/> is false then and FableFx draws ONE/ONE particles into
/// the scene.
///
/// ONE INSTANCE PER VIEW (<see cref="Refii.Core.Engine_Views"/>). View 0 mirrors the window's
/// camera, or player one's view while the screen is split; view 1 exists only while split and
/// mirrors player two's. Both draw the one layer world — the FX instances live in view 0's
/// scenario — each through its own camera at half its own view's size, and each view's post
/// chain reads its own texture (<see cref="TrySnapshot(int, out Rid)"/>).
/// </remarks>
public partial class Engine_FxAdditiveLayer : Node
{
    public const int Layer = 21;
    public const uint LayerMask = 1u << (Layer - 1);

    /// <summary>The scalar the layer joins the scene with (`fx_additive_gain`). 1.0: the layer
    /// is radiance, like a mix-blended particle in the scene; see the addlayer shader.</summary>
    public static float Gain = 1f;

    public const string GainSettingKey = "fx_additive_gain";

    /// <summary>Views a layer can be drawn for: the window or player one's, and player two's.</summary>
    public const int Views = 2;

    private static readonly object Gate = new();
    private static Engine_FxAdditiveLayer? _current;
    private static readonly Rid[] _publishedRd = new Rid[Views];
    private static readonly bool[] _publishedActive = new bool[Views];
    private static ulong _activeFrame;

    private SubViewport _viewport = null!;
    private Camera3D _camera = null!;
    private Viewport? _host;

    /// <summary>The viewport Ensure was called from; view 0's host whenever the screen is whole.</summary>
    private Viewport? _windowHost;

    /// <summary>Which view this instance draws for.</summary>
    private int _view;

    /// <summary>View 1's layer world: view 0's, so both views draw the same instances.</summary>
    private World3D? _world;

    /// <summary>View 1's instance while the screen is split; held by view 0's.</summary>
    private Engine_FxAdditiveLayer? _second;
    private Vector2I _size;
    private bool _enabled = true;
    private bool _active;

    /// <summary>Whether the viewport's texture resolved to a render-device texture the post
    /// chain can sample; without it nothing routed here would ever reach the screen.</summary>
    private bool _usable;

    private bool HostAlive => _host is not null && GodotObject.IsInstanceValid(_host);

    public static Engine_FxAdditiveLayer? Current => _current;

    /// <summary>The layer world's scenario for its instances; invalid until the layer is up.</summary>
    public static Rid Scenario =>
        _current is not null && GodotObject.IsInstanceValid(_current) && _current._viewport is not null
            ? _current._viewport.World3D.Scenario
            : default;

    /// <summary>Whether the viewport texture resolved for the post chain.</summary>
    public bool Usable => _usable;

    public Vector2I Size => _size;

    /// <summary>
    /// The layer's pixels as an image (main thread; a GPU readback), for the debug channel —
    /// mean and peak tell whether the particles reach the layer at the values expected.
    /// </summary>
    public Image? Capture() => _viewport?.GetTexture()?.GetImage();

    /// <summary>Whether ONE/ONE particles should be drawn on the layer this frame.</summary>
    public static bool Present =>
        _current is not null && GodotObject.IsInstanceValid(_current) && _current.IsInsideTree() &&
        _current._enabled && _current._usable && Refii.Lighting.Engine_PostFxParams.Enabled &&
        RenderingServer.GetRenderingDevice() is not null;

    /// <summary>Called by a system that put ONE/ONE geometry on the layer this frame.</summary>
    public static void NotifyActive() => _activeFrame = Engine.GetProcessFrames();

    /// <summary>
    /// The layer texture for the post chain, valid only when something was drawn on it this
    /// frame. Render-thread safe: plain values under a lock.
    /// </summary>
    public static bool TrySnapshot(out Rid layer) => TrySnapshot(0, out layer);

    /// <summary>
    /// <paramref name="view"/>'s layer texture for that view's post chain: 0 the window or player
    /// one's view, 1 player two's. Render-thread safe.
    /// </summary>
    public static bool TrySnapshot(int view, out Rid layer)
    {
        lock (Gate)
        {
            if (view < 0 || view >= Views)
            {
                layer = default;
                return false;
            }

            layer = _publishedRd[view];
            return layer.IsValid && _publishedActive[view];
        }
    }

    public void SetEnabled(bool on)
    {
        _enabled = on;
        if (_viewport is not null)
        {
            _viewport.RenderTargetUpdateMode = on ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        }

        if (_second is not null && GodotObject.IsInstanceValid(_second))
        {
            _second.SetEnabled(on);
        }
    }

    public static void Ensure(Node context)
    {
        if (_current is not null && GodotObject.IsInstanceValid(_current)) return;
        if (RenderingServer.GetRenderingDevice() is null) return;
        SceneTree? tree = context.GetTree();
        if (tree is null) return;
        Viewport host = context.GetViewport() ?? tree.Root;
        var node = new Engine_FxAdditiveLayer { Name = "FxAdditiveLayer", _host = host, _windowHost = host };
        _current = node;
        tree.Root.CallDeferred(Node.MethodName.AddChild, node);
    }

    public override void _Ready()
    {
        _host ??= GetViewport();
        _windowHost ??= _host;
        // Opaque black, not transparent: only colour is read (the guest clears to (0,0,0,0) and
        // adds into it), and blend_add never writes alpha, so a transparent viewport would only
        // bring in Godot's alpha handling of the 3D-to-texture copy.
        _viewport = new SubViewport
        {
            Name = "AdditiveViewport",
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            Msaa3D = Viewport.Msaa.Disabled,
            UseDebanding = false,
            UseHdr2D = true,
            PositionalShadowAtlasSize = 0,
            // ITS OWN WORLD. The layer's surfaces are rendering-server instances in this world
            // (Engine_FxLayerInstance), so the main scene's index never holds them and this
            // viewport culls only them; the scene depth arrives through the depth-capture global.
            // View 1 draws view 0's world.
            World3D = _world ?? new World3D(),
        };
        if (_view == 0)
        {
            Gain = Math.Clamp(
                Refii.Core.Engine_RefiiConfig.Float(Refii.Core.Engine_RefiiConfig.Read(out _), GainSettingKey, 1f),
                0f, 64f);
        }

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0f, 0f, 0f, 1f),
            TonemapMode = Godot.Environment.ToneMapper.Linear,
            TonemapExposure = 1f,
            AmbientLightSource = Godot.Environment.AmbientSource.Disabled,
            GlowEnabled = false,
        };
        _camera = new Camera3D { Name = "AdditiveCamera", CullMask = LayerMask | (_view == 1 ? Refii.Models.Engine_ClassicViews.SecondViewLayer : 0u), Environment = env };
        _viewport.AddChild(_camera);
        AddChild(_viewport);
        _camera.Current = true;

        // After the systems have emitted (default priority under GameFx) and before the
        // displacement pass at 1000: the published texture must be this frame's.
        ProcessPriority = 900;
        Resize();
        _usable = LayerRd().IsValid;
        if (!_usable)
        {
            GD.Print("[fx] additive layer: the viewport texture has no render-device texture; ONE/ONE particles stay in the scene");
        }

        if (_view == 0)
        {
            Refii.Core.Engine_Views.ViewsChanged += FollowViews;
            FollowViews();
        }
    }

    /// <summary>
    /// View 0's: hosts this instance on player one's view and builds view 1's while the screen is
    /// split; back on the window, and view 1's freed, when it rejoins.
    /// </summary>
    private void FollowViews()
    {
        if (!GodotObject.IsInstanceValid(this) || !IsInsideTree())
        {
            return;
        }

        if (Refii.Core.Engine_Views.Split &&
            Refii.Core.Engine_Views.FirstViewport is { } first && GodotObject.IsInstanceValid(first) &&
            Refii.Core.Engine_Views.SecondViewport is { } second && GodotObject.IsInstanceValid(second))
        {
            _host = first;
            if (_second is null || !GodotObject.IsInstanceValid(_second))
            {
                _second = new Engine_FxAdditiveLayer
                {
                    Name = "FxAdditiveLayerSecondView",
                    _view = 1,
                    _host = second,
                    _world = _viewport.World3D,
                    _enabled = _enabled,
                };
                AddChild(_second);
            }
            else
            {
                _second._host = second;
            }

            return;
        }

        _host = _windowHost;
        if (_second is not null && GodotObject.IsInstanceValid(_second))
        {
            _second.QueueFree();
        }

        _second = null;
        lock (Gate)
        {
            _publishedRd[1] = default;
            _publishedActive[1] = false;
        }
    }

    private Rid LayerRd() =>
        RenderingServer.TextureGetRdTexture(RenderingServer.ViewportGetTexture(_viewport.GetViewportRid()));

    private Rid _layerRd;
    private Vector2I _layerRdSize;

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

        if (_view == 0)
        {
            Refii.Core.Engine_Views.ViewsChanged -= FollowViews;
        }

        lock (Gate)
        {
            _publishedRd[_view] = default;
            _publishedActive[_view] = false;
        }
    }

    private void Resize()
    {
        if (!HostAlive) return;
        Vector2 v = _host!.GetVisibleRect().Size;
        var size = new Vector2I(Math.Max((int)MathF.Ceiling(v.X * 0.5f), 1), Math.Max((int)MathF.Ceiling(v.Y * 0.5f), 1));
        if (size == _size) return;
        _size = size;
        _viewport.Size = size;
    }

    public override void _Process(double delta) => RefreshView(false);

    public void RefreshView(bool frozen)
    {
        using var _framePhase = Refii.Core.Engine_FramePhases.Measure("fxadditivelayer");
        if (!HostAlive) return;
        Resize();
        Camera3D? main = _host!.GetCamera3D();
        if (main is null)
        {
            return;
        }

        if ((main.CullMask & LayerMask) != 0) main.CullMask &= ~LayerMask;

        if (!frozen) _active = Engine.GetProcessFrames() - _activeFrame <= 1;
        bool active = _enabled && _active;
        _viewport.RenderTargetUpdateMode = active ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;

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
            _publishedRd[_view] = rd;
            _publishedActive[_view] = active && rd.IsValid;
        }
    }
}
