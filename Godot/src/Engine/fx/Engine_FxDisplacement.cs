using System;
using Godot;

namespace Refii.Fx;

public partial class Engine_FxDisplacement : Node
{
    public const int Layer = 20;
    public const uint LayerMask = 1u << (Layer - 1);
    public const string ApplyShaderPath = "res://src/Engine/fx/fx_displacement_apply.gdshader";

    private static Engine_FxDisplacement? _current;

    private SubViewport _viewport = null!;
    private Camera3D _camera = null!;
    private CanvasLayer _layer = null!;
    private ColorRect _rect = null!;
    private ShaderMaterial? _apply;
    private Viewport? _host;

    /// <summary>True only when the host window is BOTH assigned and still alive.</summary>
    /// <remarks>
    /// A FREED GODOT OBJECT IS NOT NULL. The C# wrapper outlives the native object, so
    /// `_host is null` is false for a window that has already been torn down and the next
    /// call throws ObjectDisposedException — 90 times in one run here, every frame after a
    /// level change took the window away. `IsInstanceValid` is the only test that sees it,
    /// and it is what the entity code already uses on its own nodes.
    /// </remarks>
    private bool HostAlive => _host is not null && GodotObject.IsInstanceValid(_host);
    private Vector2I _size;

    public static float Strength = 1f;

    public static Engine_FxDisplacement? Current => _current;

    /// <summary>Live lever: stop or resume the displacement pass.</summary>
    public void SetEnabled(bool on)
    {
        if (_viewport is not null) _viewport.RenderTargetUpdateMode = on ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        if (_layer is not null) _layer.Visible = on;
    }

    private static ulong _activeFrame;

    public static void NotifyActive() => _activeFrame = Engine.GetProcessFrames();

    public Image? CaptureMap() => _viewport?.GetTexture()?.GetImage();

    public static void Ensure(Node context)
    {
        if (_current is not null && GodotObject.IsInstanceValid(_current)) return;
        SceneTree? tree = context.GetTree();
        if (tree is null) return;
        Viewport host = context.GetViewport() ?? tree.Root;
        var node = new Engine_FxDisplacement { Name = "FxDisplacement", _host = host };
        _current = node;
        tree.Root.CallDeferred(Node.MethodName.AddChild, node);
    }

    public override void _Ready()
    {
        _host ??= GetViewport();
        _viewport = new SubViewport
        {
            Name = "DispViewport",
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            Msaa3D = Viewport.Msaa.Disabled,
            UseDebanding = false,
            UseHdr2D = true,
            PositionalShadowAtlasSize = 0,
            World3D = _host!.World3D,
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0f, 0f, 0f, 0f),
            TonemapMode = Godot.Environment.ToneMapper.Linear,
            TonemapExposure = 1f,
            AmbientLightSource = Godot.Environment.AmbientSource.Disabled,
            GlowEnabled = false,
        };
        _camera = new Camera3D { Name = "DispCamera", CullMask = LayerMask, Environment = env };
        _viewport.AddChild(_camera);
        AddChild(_viewport);
        _camera.Current = true;

        _apply = new ShaderMaterial { Shader = GD.Load<Shader>(ApplyShaderPath) };
        _apply.SetShaderParameter("disp_tex", _viewport.GetTexture());
        _layer = new CanvasLayer { Name = "DispApply", Layer = -100 };
        _rect = new ColorRect { Name = "DispRect", Material = _apply, MouseFilter = Control.MouseFilterEnum.Ignore };
        _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(_rect);
        AddChild(_layer);
        _layer.CustomViewport = _host as Viewport;
        ProcessPriority = 1000;
        Resize();
    }

    private void Resize()
    {
        if (!HostAlive) return;
        Vector2 v = _host!.GetVisibleRect().Size;
        var size = new Vector2I((int)MathF.Ceiling(v.X), (int)MathF.Ceiling(v.Y));
        if (size.X <= 0 || size.Y <= 0) return;
        if (size == _size) return;
        _size = size;
        _viewport.Size = size;
        float s = 0.5f * ((size.X / 1280f) + (size.Y / 720f)) * 255f * Strength;
        _apply?.SetShaderParameter("disp_pixel_scale", new Vector2(s, s));
    }

    public override void _Process(double delta)
    {
    	using var _framePhase = Refii.Core.Engine_FramePhases.Measure("fxdisplacement");
        if (!HostAlive || _apply is null) return;
        Resize();
        Camera3D? main = _host!.GetCamera3D();
        if (main is null)
        {
            _rect.Visible = false;
            return;
        }

        if ((main.CullMask & LayerMask) != 0) main.CullMask &= ~LayerMask;

        bool active = Engine.GetProcessFrames() - _activeFrame <= 1;
        _rect.Visible = active;
        _viewport.RenderTargetUpdateMode = active ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        if (!active) return;
        _camera.GlobalTransform = main.GlobalTransform;
        _camera.Projection = main.Projection;
        _camera.Fov = main.Fov;
        _camera.Size = main.Size;
        _camera.Near = main.Near;
        _camera.Far = main.Far;
        _camera.KeepAspect = main.KeepAspect;
        _camera.HOffset = main.HOffset;
        _camera.VOffset = main.VOffset;
    }
}
