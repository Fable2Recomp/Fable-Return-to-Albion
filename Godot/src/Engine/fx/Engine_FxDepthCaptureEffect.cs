using System;
using Godot;
using Godot.Collections;

namespace Refii.Fx;

[GlobalClass]
public partial class Engine_FxDepthCaptureEffect : CompositorEffect
{
    public const string ShaderPath = "res://src/Engine/fx/fx_depth_capture.glsl";
    public const string GlobalTextureName = "fx_scene_depth";
    public const string GlobalValidName = "fx_scene_depth_valid";

    private static bool _globalsRegistered;
    private static Texture2Drd? _texture;

    private RenderingDevice? _rd;
    private Rid _shader;
    private Rid _pipeline;
    private Rid _sampler;
    private Rid _target;
    private Vector2I _targetSize;
    private bool _ready;

    public static void RegisterGlobals()
    {
        if (_globalsRegistered) return;
        _globalsRegistered = true;
        _texture ??= new Texture2Drd();
        RenderingServer.GlobalShaderParameterSet(GlobalTextureName, _texture);
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
        if (_target.IsValid) _rd!.FreeRid(_target);
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
        Callable.From(() =>
        {
            _texture ??= new Texture2Drd();
            _texture.TextureRdRid = rid;
            RenderingServer.GlobalShaderParameterSet(GlobalTextureName, _texture);
            RenderingServer.GlobalShaderParameterSet(GlobalValidName, 1.0f);
        }).CallDeferred();
    }

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
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
