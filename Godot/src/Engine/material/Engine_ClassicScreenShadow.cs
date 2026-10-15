using System;
using Godot;
using Godot.Collections;

namespace Refii.Models;

/// <summary>
/// The classic look's screen-space sun shadow mask: the texture the title binds frame-globally
/// at s8 (g_ScreenShadowMap) and its SCR1 materials multiply into the sun.
/// </summary>
/// <remarks>
/// THE TITLE'S PASS (`Render_ShadowCascadeRenderLoop_INFERRED` -> `sub_821AF248`, shader
/// PSHADER_SHADOWMAP_POISSON): after the Z-only replay of the opaque list and before its colour
/// replay, one full-resolution k_8 lit fraction per pixel from the frame's depth and the two sun
/// cascades (classic_screen_shadow.glsl). Here: the compositor's pre-opaque callback, after
/// Godot's depth prepass, which the classic opaque materials are in. Until the first mask is
/// written, and whenever there is no sun caster, the texture is white — the title's own
/// fallback (`sub_822965E0`, the 16x16 white texture).
/// </remarks>
[Tool]
[GlobalClass]
public partial class Engine_ClassicScreenShadow : CompositorEffect
{
    public const string ShaderPath = "res://src/Engine/material/classic_screen_shadow.glsl";
    private const int ParamsBytes = 4 * 16 * 4 + 3 * 16;

    private static Texture2Drd? _mask;
    private static Rid _whiteRid;

    private static ImageTexture? _white;

    /// <summary>
    /// The title's "no sun caster" mask, 16 x 16 white (`sub_822965E0`): for a draw outside the
    /// main view (an impostor, a prelit texture), which cannot read the main view's mask.
    /// </summary>
    public static ImageTexture White
    {
        get
        {
            if (_white is null)
            {
                var image = Image.CreateEmpty(16, 16, false, Image.Format.L8);
                image.Fill(Colors.White);
                _white = ImageTexture.CreateFromImage(image);
            }
            return _white;
        }
    }

    /// <summary>The mask as the classic materials bind it; white until the first pass.</summary>
    public static Texture2Drd Mask
    {
        get
        {
            if (_mask is null)
            {
                _mask = new Texture2Drd();
                if (RenderingServer.GetRenderingDevice() is { } rd)
                {
                    _whiteRid = rd.TextureCreate(new RDTextureFormat
                    {
                        Width = 1, Height = 1, Format = RenderingDevice.DataFormat.R8Unorm,
                        UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit,
                    }, new RDTextureView(), new Array<byte[]> { new byte[] { 255 } });
                    _mask.TextureRdRid = _whiteRid;
                }
            }
            return _mask;
        }
    }

    /// <summary>The mask's size, the frame the materials address it at (PS c0 = (0, 0, 1/W, 1/H)).</summary>
    public static Vector2I Size { get; private set; }

    private RenderingDevice? _rd;
    private Rid _shader, _pipeline, _nearest, _target, _params;
    private Vector2I _targetSize;
    private bool _ready;
    private readonly byte[] _bytes = new byte[ParamsBytes];
    private readonly float[] _floats = new float[ParamsBytes / 4];

    public Engine_ClassicScreenShadow()
    {
        EffectCallbackType = EffectCallbackTypeEnum.PreOpaque;
        _ = Mask;
        RenderingServer.CallOnRenderThread(Callable.From(Initialise));
    }

    private void Initialise()
    {
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd is null || GD.Load<RDShaderFile>(ShaderPath) is not { } file)
        {
            GD.PushError("[classic] screen shadow mask did not initialise (" + ShaderPath + ")");
            return;
        }
        _shader = _rd.ShaderCreateFromSpirV(file.GetSpirV());
        if (!_shader.IsValid)
        {
            return;
        }
        _pipeline = _rd.ComputePipelineCreate(_shader);
        _nearest = _rd.SamplerCreate(new RDSamplerState
        {
            MinFilter = RenderingDevice.SamplerFilter.Nearest, MagFilter = RenderingDevice.SamplerFilter.Nearest,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge, RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
        });
        _params = _rd.UniformBufferCreate(ParamsBytes);
        _ready = _pipeline.IsValid && _nearest.IsValid && _params.IsValid;
    }

    private void EnsureTarget(Vector2I size)
    {
        if (_target.IsValid && size == _targetSize)
        {
            return;
        }
        if (_target.IsValid)
        {
            _rd!.FreeRid(_target);
        }
        _target = _rd!.TextureCreate(new RDTextureFormat
        {
            Width = (uint)size.X, Height = (uint)size.Y, Format = RenderingDevice.DataFormat.R8Unorm,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit
                | RenderingDevice.TextureUsageBits.CanCopyFromBit,
        }, new RDTextureView());
        _targetSize = size;
        Rid rid = _target;
        Callable.From(() =>
        {
            Mask.TextureRdRid = rid;
            Size = size;
        }).CallDeferred();
    }

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        if (!_ready || _rd is null || !Engine_ClassicMaterial.Active || Engine_ClassicShadows.Instance is not { } shadows)
        {
            return;
        }
        if (renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD buffers ||
            renderData.GetRenderSceneData() is not RenderSceneDataRD scene)
        {
            return;
        }
        if (shadows.CascadeTexture(0) is not Texture2Drd c0 || shadows.CascadeTexture(1) is not Texture2Drd c1 ||
            !c0.TextureRdRid.IsValid || !c1.TextureRdRid.IsValid)
        {
            return;
        }
        Vector2I size = buffers.GetInternalSize();
        if (size.X <= 0 || size.Y <= 0)
        {
            return;
        }
        EnsureTarget(size);

        // Godot view -> Godot world -> guest world ((x, y, z) -> (-x, z, y)).
        Projection inverse = scene.GetCamProjection().Inverse();
        Transform3D camera = scene.GetCamTransform();
        var toGuest = new Transform3D(new Basis(new Vector3(-1f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(0f, 1f, 0f)), Vector3.Zero);
        Transform3D viewToGuest = toGuest * camera;
        int at = 0;
        void Column(float x, float y, float z, float w)
        {
            _floats[at++] = x; _floats[at++] = y; _floats[at++] = z; _floats[at++] = w;
        }
        Column(inverse.X.X, inverse.X.Y, inverse.X.Z, inverse.X.W);
        Column(inverse.Y.X, inverse.Y.Y, inverse.Y.Z, inverse.Y.W);
        Column(inverse.Z.X, inverse.Z.Y, inverse.Z.Z, inverse.Z.W);
        Column(inverse.W.X, inverse.W.Y, inverse.W.Z, inverse.W.W);
        Column(viewToGuest.Basis.Column0.X, viewToGuest.Basis.Column0.Y, viewToGuest.Basis.Column0.Z, 0f);
        Column(viewToGuest.Basis.Column1.X, viewToGuest.Basis.Column1.Y, viewToGuest.Basis.Column1.Z, 0f);
        Column(viewToGuest.Basis.Column2.X, viewToGuest.Basis.Column2.Y, viewToGuest.Basis.Column2.Z, 0f);
        Column(viewToGuest.Origin.X, viewToGuest.Origin.Y, viewToGuest.Origin.Z, 1f);
        // The cascade rows exactly as the materials read them (PS c36..c43), and c12, c19.
        float[] cascades = Engine_ClassicConstants.ReadFrame(true, Engine_ClassicRegisters.Ps.DirectionalLightShadowTransform0, 8);
        System.Array.Copy(cascades, 0, _floats, at, 32);
        at += 32;
        float[] eye = Engine_ClassicConstants.ReadFrame(true, Engine_ClassicRegisters.Ps.EyePosition, 1);
        Column(eye[0], eye[1], eye[2], 1f);
        float[] ranges = Engine_ClassicConstants.ReadFrame(true, Engine_ClassicRegisters.Ps.ShadowRangeConstants, 1);
        Column(ranges[0], ranges[1], ranges[2], ranges[3]);
        Column(size.X, size.Y, 1f / Engine_ClassicShadows.MapSize, 0f);
        Buffer.BlockCopy(_floats, 0, _bytes, 0, ParamsBytes);
        _rd.BufferUpdate(_params, 0, ParamsBytes, _bytes);

        var uniforms = new Array<RDUniform>();
        RDUniform Sampled(int binding, Rid texture)
        {
            var u = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = binding };
            u.AddId(_nearest);
            u.AddId(texture);
            return u;
        }
        uniforms.Add(Sampled(0, buffers.GetDepthLayer(0)));
        uniforms.Add(Sampled(1, c0.TextureRdRid));
        uniforms.Add(Sampled(2, c1.TextureRdRid));
        var image = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 3 };
        image.AddId(_target);
        uniforms.Add(image);
        var block = new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = 4 };
        block.AddId(_params);
        uniforms.Add(block);
        Rid set = UniformSetCacheRD.GetCache(_shader, 0, uniforms);

        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, set, 0);
        _rd.ComputeListDispatch(list, (uint)((size.X + 7) / 8), (uint)((size.Y + 7) / 8), 1);
        _rd.ComputeListEnd();
    }


    public override void _Notification(int what)
    {
        if (what != NotificationPredelete || _rd is null)
        {
            return;
        }
        if (_target.IsValid) _rd.FreeRid(_target);
        if (_params.IsValid) _rd.FreeRid(_params);
        if (_nearest.IsValid) _rd.FreeRid(_nearest);
        if (_pipeline.IsValid) _rd.FreeRid(_pipeline);
        if (_shader.IsValid) _rd.FreeRid(_shader);
        _target = _params = _nearest = _pipeline = _shader = default;
        _ready = false;
        _rd = null;
    }
}
