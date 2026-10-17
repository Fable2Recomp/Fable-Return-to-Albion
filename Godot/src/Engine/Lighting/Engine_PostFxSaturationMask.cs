using Godot;
using Godot.Collections;

namespace Refii.Lighting;

/// <summary>
/// The title's g_SaturationMaskSampler: which pixels belong to an entity carrying
/// `GraphicAppearance.SetSaturationMask` (comp3+196). Shader 0505 gives those pixels
/// `Saturation + SaturationMasked` and `Brightness + BrightnessMasked`; everything else takes the
/// plain pair. The entities mark their own visible pixels with stencil bit 0x80
/// (entity_saturation_mask.gdshader, drawn as their material overlay); this pass turns that bit
/// into an R8 texture the composite samples, with one stencil-tested triangle over the screen.
/// </summary>
public sealed class Engine_PostFxSaturationMask
{
    public const string ShaderPath = "res://src/Engine/Lighting/fable_postfx_satmask.glsl";
    private const uint MaskBit = 0x80;

    private readonly RenderingDevice _rd;
    private Rid _shader;
    private Rid _pipeline;
    private long _pipelineFormat = RenderingDevice.InvalidFormatId;
    private Rid _framebuffer;
    private Rid _framebufferMask;
    private Rid _framebufferDepth;
    private bool _warned;

    public Engine_PostFxSaturationMask(RenderingDevice rd)
    {
        _rd = rd;
    }

    /// <summary>The R8 mask texture the composite binds, made at the frame's size.</summary>
    public Rid Texture { get; private set; }

    public bool Initialise()
    {
        var file = GD.Load<RDShaderFile>(ShaderPath);
        RDShaderSpirV? spirv = file?.GetSpirV();
        if (spirv is null || spirv.CompileErrorVertex.Length != 0 || spirv.CompileErrorFragment.Length != 0)
        {
            GD.PrintErr($"[postfx] {ShaderPath} failed to compile: " +
                $"{spirv?.CompileErrorVertex}{spirv?.CompileErrorFragment}");
            return false;
        }

        _shader = _rd.ShaderCreateFromSpirV(spirv);
        return _shader.IsValid;
    }

    public void Resize(Vector2I size)
    {
        ReleaseTargets();
        Texture = _rd.TextureCreate(
            new RDTextureFormat
            {
                Width = (uint)size.X,
                Height = (uint)size.Y,
                Format = RenderingDevice.DataFormat.R8Unorm,
                TextureType = RenderingDevice.TextureType.Type2D,
                UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                    RenderingDevice.TextureUsageBits.ColorAttachmentBit |
                    RenderingDevice.TextureUsageBits.CanCopyToBit,
            },
            new RDTextureView());
    }

    /// <summary>
    /// Fills <see cref="Texture"/> from the stencil of <paramref name="depth"/>. False when the
    /// depth carries no stencil the pass can test (an MSAA resolve does not), and the composite
    /// then treats every pixel as unmasked.
    /// </summary>
    public bool Build(Rid depth)
    {
        if (!_shader.IsValid || !Texture.IsValid || !depth.IsValid)
        {
            return false;
        }

        RDTextureFormat depthFormat = _rd.TextureGetFormat(depth);
        bool stencil = depthFormat.Format is RenderingDevice.DataFormat.D24UnormS8Uint
            or RenderingDevice.DataFormat.D32SfloatS8Uint
            or RenderingDevice.DataFormat.D16UnormS8Uint;
        if (!stencil || depthFormat.Samples != RenderingDevice.TextureSamples.Samples1)
        {
            if (!_warned)
            {
                _warned = true;
                GD.PrintErr($"[postfx] the scene depth is {depthFormat.Format} x{depthFormat.Samples}: " +
                    "no stencil to read the saturation mask from; masked entities take the plain saturation");
            }

            return false;
        }

        if (!_framebuffer.IsValid || _framebufferMask != Texture || _framebufferDepth != depth ||
            !_rd.FramebufferIsValid(_framebuffer))
        {
            FreeFramebuffer();
            _framebuffer = _rd.FramebufferCreate(new Array<Rid> { Texture, depth });
            _framebufferMask = Texture;
            _framebufferDepth = depth;
            if (!_framebuffer.IsValid)
            {
                return false;
            }
        }

        long format = _rd.FramebufferGetFormat(_framebuffer);
        if (!_pipeline.IsValid || format != _pipelineFormat)
        {
            Free(ref _pipeline);
            var test = new RDPipelineDepthStencilState
            {
                EnableDepthTest = false,
                EnableDepthWrite = false,
                EnableStencil = true,
                FrontOpCompare = RenderingDevice.CompareOperator.Equal,
                FrontOpCompareMask = MaskBit,
                FrontOpReference = MaskBit,
                FrontOpWriteMask = 0,
                FrontOpFail = RenderingDevice.StencilOperation.Keep,
                FrontOpPass = RenderingDevice.StencilOperation.Keep,
                FrontOpDepthFail = RenderingDevice.StencilOperation.Keep,
                BackOpCompare = RenderingDevice.CompareOperator.Equal,
                BackOpCompareMask = MaskBit,
                BackOpReference = MaskBit,
                BackOpWriteMask = 0,
                BackOpFail = RenderingDevice.StencilOperation.Keep,
                BackOpPass = RenderingDevice.StencilOperation.Keep,
                BackOpDepthFail = RenderingDevice.StencilOperation.Keep,
            };
            var blend = new RDPipelineColorBlendState();
            blend.Attachments.Add(new RDPipelineColorBlendStateAttachment());
            _pipeline = _rd.RenderPipelineCreate(
                _shader, format, RenderingDevice.InvalidFormatId, RenderingDevice.RenderPrimitive.Triangles,
                new RDPipelineRasterizationState(), new RDPipelineMultisampleState(), test, blend);
            _pipelineFormat = format;
            if (!_pipeline.IsValid)
            {
                return false;
            }
        }

        long list = _rd.DrawListBegin(
            _framebuffer, RenderingDevice.DrawFlags.ClearColor0, new[] { new Color(0f, 0f, 0f, 0f) });
        _rd.DrawListBindRenderPipeline(list, _pipeline);
        _rd.DrawListDraw(list, false, 1, 3);
        _rd.DrawListEnd();
        return true;
    }

    public void Release()
    {
        ReleaseTargets();
        Free(ref _pipeline);
        Free(ref _shader);
    }

    /// <summary>A framebuffer goes with either texture it holds; the scene's depth can take it first.</summary>
    private void FreeFramebuffer()
    {
        if (_framebuffer.IsValid && _rd.FramebufferIsValid(_framebuffer))
        {
            _rd.FreeRid(_framebuffer);
        }

        _framebuffer = default;
    }

    private void ReleaseTargets()
    {
        FreeFramebuffer();
        _framebufferMask = default;
        _framebufferDepth = default;
        Rid texture = Texture;
        Free(ref texture);
        Texture = default;
    }

    private void Free(ref Rid rid)
    {
        if (rid.IsValid)
        {
            _rd.FreeRid(rid);
        }

        rid = default;
    }
}
