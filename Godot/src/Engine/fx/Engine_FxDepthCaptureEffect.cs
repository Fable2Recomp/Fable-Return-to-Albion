using System;
using Godot;
using Godot.Collections;

namespace Refii.Fx;

[Tool]
[GlobalClass]
public partial class Engine_FxDepthCaptureEffect : CompositorEffect
{
    public const string ShaderPath = "res://src/Engine/fx/fx_depth_capture.glsl";
    public const string GlobalTextureName = "fx_scene_depth";
    public const string GlobalValidName = "fx_scene_depth_valid";

    /// <summary>
    /// Player two's view's depth on split screen. A global is one value for every view, so each
    /// view's capture publishes its own and the readers pick by the second view's layer
    /// (<see cref="Refii.Models.Engine_ClassicViews.SecondViewLayer"/>).
    /// </summary>
    public const string GlobalTextureSecondName = "fx_scene_depth_second";

    private static bool _globalsRegistered;
    private static readonly Texture2Drd?[] _textures = new Texture2Drd?[2];

    /// <summary>Which view this capture runs in: 0 the window's (player one's on split screen), 1 player two's.</summary>
    public int View { get; set; }

    private static string TextureGlobal(int view) => view == 0 ? GlobalTextureName : GlobalTextureSecondName;

    private RenderingDevice? _rd;
    private Rid _shader;
    private Rid _pipeline;
    private Rid _sampler;
    private Rid _target;
    private Vector2I _targetSize;
    private bool _ready;

    /// <summary>
    /// Publishes the depth capture's two globals.
    /// </summary>
    /// <remarks>
    /// THE NAMES ARE DECLARED IN project.godot's [shader_globals], and must be. Setting a global
    /// that was never declared does not create it — it fails, and every shader referencing the
    /// name then reports "was removed at some point. Material will not display correctly". That
    /// was happening 259 times a run, on both displacement shaders, in the editor and the game.
    ///
    /// NOT PROBED FIRST. Every reader — GlobalShaderParameterGetList, ...Get and ...GetType —
    /// is EDITOR-ONLY: outside the editor each one ERR_FAILs with "This function should never
    /// be used outside the editor, it can severely damage performance" and returns empty. A
    /// probe therefore reports "not declared" in every game build, however well the globals
    /// are working, and returning on it skipped the Sets below. Measured: the run warned here
    /// and logged that ERR once, while the 259 "was removed at some point" material warnings
    /// the declaration was added to fix stayed at zero — the globals were fine, the check was
    /// not. If a name really is missing, Godot says so accurately on the Set, as
    /// `Condition "!global_shader_uniforms.variables.has(p_name)" is true`.
    /// </remarks>
    public static void RegisterGlobals()
    {
        if (_globalsRegistered) return;
        _globalsRegistered = true;

        for (int view = 0; view < _textures.Length; ++view)
        {
            _textures[view] ??= new Texture2Drd();
            RenderingServer.GlobalShaderParameterSet(TextureGlobal(view), _textures[view]!);
        }

        RenderingServer.GlobalShaderParameterSet(GlobalValidName, 0.0f);
    }

    public Engine_FxDepthCaptureEffect()
    {
        EffectCallbackType = EffectCallbackTypeEnum.PostOpaque;
        RegisterGlobals();
        RenderingServer.CallOnRenderThread(Callable.From(Initialise));
    }

    private void Initialise()
    {
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd is null) return;
        var file = GD.Load<RDShaderFile>(ShaderPath);
        if (file is null) return;
        RDShaderSpirV spirv = file.GetSpirV();
        _shader = _rd.ShaderCreateFromSpirV(spirv);
        if (!_shader.IsValid) return;
        _pipeline = _rd.ComputePipelineCreate(_shader);
        var samplerState = new RDSamplerState { MinFilter = RenderingDevice.SamplerFilter.Nearest, MagFilter = RenderingDevice.SamplerFilter.Nearest, RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge, RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge };
        _sampler = _rd.SamplerCreate(samplerState);
        _ready = _pipeline.IsValid && _sampler.IsValid;
    }

    private void EnsureTarget(Vector2I size)
    {
        if (_target.IsValid && size == _targetSize) return;
        // The FX materials sample the old depth until the deferred re-point below lands.
        Refii.Core.Engine_RdRetire.Retire(_target);
        var format = new RDTextureFormat
        {
            Width = (uint)size.X,
            Height = (uint)size.Y,
            Format = RenderingDevice.DataFormat.R32Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.CanUpdateBit,
        };
        _target = _rd!.TextureCreate(format, new RDTextureView());
        _targetSize = size;
        Rid rid = _target;
        int view = View;
        Callable.From(() =>
        {
            Texture2Drd texture = _textures[view] ??= new Texture2Drd();
            texture.TextureRdRid = rid;
            RenderingServer.GlobalShaderParameterSet(TextureGlobal(view), texture);
            if (view == 0)
            {
                RenderingServer.GlobalShaderParameterSet(GlobalValidName, 1.0f);
            }
        }).CallDeferred();
    }

    /// <summary>
    /// Releases the rendering-device objects this effect owns, before the device goes.
    /// </summary>
    /// <remarks>
    /// NOTHING FREED THESE. The shader, pipeline, sampler and target were created once and left
    /// to the process, and the static <c>Texture2Drd</c> holding the target's RID outlives every
    /// node — it is published as a global shader parameter, so the rendering server keeps a
    /// reference to it too. At exit the device is torn down first and those references are
    /// dangling, which is the shape of the shutdown fault: `viewport_remove_canvas` reporting a
    /// null viewport and `_disconnect` reporting a connection that is already gone, then a
    /// segfault inside Godot's own teardown on the main thread with the tree already emptied.
    ///
    /// `NotificationPredelete` rather than `_ExitTree` because a CompositorEffect is a Resource
    /// and never enters the tree. The global parameter is cleared FIRST: dropping the texture
    /// while the server still points at it is the same bug one step later.
    /// </remarks>
    public override void _Notification(int what)
    {
        if (what != NotificationPredelete)
        {
            return;
        }

        if (_globalsRegistered)
        {
            if (View == 0)
            {
                RenderingServer.GlobalShaderParameterSet(GlobalValidName, 0.0f);
            }

            RenderingServer.GlobalShaderParameterSet(TextureGlobal(View), default(Variant));
        }

        if (View is >= 0 and < 2 && _textures[View] is { } texture)
        {
            texture.TextureRdRid = default;
            _textures[View] = null;
        }

        if (_rd is null)
        {
            return;
        }

        // Freed in creation order's reverse, and each guarded: a device that has already gone
        // leaves every RID invalid, and freeing an invalid one is its own error.
        if (_target.IsValid) _rd.FreeRid(_target);
        if (_sampler.IsValid) _rd.FreeRid(_sampler);
        if (_pipeline.IsValid) _rd.FreeRid(_pipeline);
        if (_shader.IsValid) _rd.FreeRid(_shader);

        _target = default;
        _sampler = default;
        _pipeline = default;
        _shader = default;
        _ready = false;
        _rd = null;
    }

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        if (_rd is not null) Refii.Core.Engine_RdRetire.Pump(_rd);
        if (!_ready || _rd is null) return;
        if (renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD buffers) return;
        Vector2I size = buffers.GetInternalSize();
        if (size.X <= 0 || size.Y <= 0) return;
        EnsureTarget(size);
        Rid depth = buffers.GetDepthLayer(0);
        var depthUniform = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 0 };
        depthUniform.AddId(_sampler);
        depthUniform.AddId(depth);
        var outUniform = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 1 };
        outUniform.AddId(_target);
        var uniforms = new Array<RDUniform> { depthUniform, outUniform };
        Rid set = UniformSetCacheRD.GetCache(_shader, 0, uniforms);
        var push = new byte[16];
        BitConverter.TryWriteBytes(push.AsSpan(0), size.X);
        BitConverter.TryWriteBytes(push.AsSpan(4), size.Y);
        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, set, 0);
        _rd.ComputeListSetPushConstant(list, push, (uint)push.Length);
        _rd.ComputeListDispatch(list, (uint)((size.X + 7) / 8), (uint)((size.Y + 7) / 8), 1);
        _rd.ComputeListEnd();
    }
}
