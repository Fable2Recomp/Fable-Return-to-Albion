using System;
using Godot;
using Godot.Collections;
using Refii.Core;

namespace Refii.Models;

/// <summary>
/// The impostor atlas with the title's four mips: at the end of the capture's own render, the
/// atlas it drew is copied into mip 0 of a texture of its own and each further mip is the box of
/// the one above (<c>classic_impostor_mips.glsl</c>). The billboard material samples it through
/// <see cref="Texture"/> (one per atlas: split screen's second view captures its own).
/// </summary>
/// <remarks>
/// It runs on the capture camera's compositor, not the main view's: the main view's depth
/// pre-pass draws before its pre-opaque effects, so mips made there reached the colour pass
/// only — the pre-pass cut the cards against the previous frame's impostors, and every pixel
/// that passed then but not now kept depth with no colour (black specks across the canopies).
/// The atlas is a child viewport, drawn before the main one, so its mips stand for both passes.
/// </remarks>
public partial class Engine_ClassicImpostorMips : CompositorEffect
{
    /// <summary>Each mip's uniform set, by the views it reads and writes.</summary>
    private readonly Refii.Core.Engine_UniformSetKey?[] _sets = new Refii.Core.Engine_UniformSetKey?[Mips];

    public const string ShaderPath = "res://src/Engine/material/classic_impostor_mips.glsl";
    private const int Mips = 4;

    /// <summary>What the billboards sample; empty until the first frame that captured.</summary>
    public Texture2Drd Texture { get; } = new();

    private RenderingDevice? _rd;
    private Rid _shader, _pipeline, _linear, _target;
    private Vector2I _targetSize;
    private readonly Rid[] _views = new Rid[Mips];
    private bool _ready;

    public Engine_ClassicImpostorMips()
    {
        EffectCallbackType = EffectCallbackTypeEnum.PostTransparent;
        RenderingServer.CallOnRenderThread(Callable.From(Initialise));
    }

    private void Initialise()
    {
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd is null || GD.Load<RDShaderFile>(ShaderPath) is not { } file)
        {
            GD.PushError("[classic] impostor mips did not initialise (" + ShaderPath + ")");
            return;
        }
        _shader = _rd.Shader(file.GetSpirV());
        if (!_shader.IsValid)
        {
            return;
        }
        _pipeline = _rd.ComputePipeline(_shader);
        _linear = _rd.Sampler(new RDSamplerState
        {
            MinFilter = RenderingDevice.SamplerFilter.Linear, MagFilter = RenderingDevice.SamplerFilter.Linear,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge, RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
        });
        _ready = _pipeline.IsValid && _linear.IsValid;
    }

    private void EnsureTarget(Vector2I size)
    {
        if (_target.IsValid && size == _targetSize)
        {
            return;
        }
        // The cards still sample the old atlas until the deferred re-point below lands.
        for (int mip = 0; mip < Mips; mip++)
        {
            Refii.Core.Engine_RdRetire.Retire(_views[mip]);
            _views[mip] = default;
        }
        Refii.Core.Engine_RdRetire.Retire(_target);
        _target = _rd!.Texture(new RDTextureFormat
        {
            Width = (uint)size.X, Height = (uint)size.Y, Mipmaps = Mips,
            Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit,
        }, new RDTextureView());
        ulong bytes = 0;
        for (int mip = 0; mip < Mips; mip++)
        {
            bytes += (ulong)System.Math.Max(1, size.X >> mip) * (ulong)System.Math.Max(1, size.Y >> mip) * 8;  // RGBA16F
        }

        Refii.Core.Engine_TracedMemory.Alloc(Refii.Core.Engine_RdRetire.TracedPool, _target.Id, bytes);
        for (int mip = 0; mip < Mips; mip++)
        {
            _views[mip] = _rd!.TextureSharedFromSlice(new RDTextureView(), _target, 0, (uint)mip, 1,
                RenderingDevice.TextureSliceType.Slice2D);
        }
        _targetSize = size;
        Rid rid = _target;
        Texture2Drd texture = Texture;
        Callable.From(() => texture.TextureRdRid = rid).CallDeferred();
    }

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        if (_rd is not null)
        {
            Refii.Core.Engine_RdRetire.Pump(_rd);
        }
        if (!_ready || _rd is null || renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD buffers)
        {
            return;
        }
        // The capture's own colour, linear and unexposed (the atlas renders without MSAA).
        Rid source = buffers.GetColorLayer(0);
        if (!source.IsValid || _rd.TextureGetFormat(source) is not { } format ||
            format.Format != RenderingDevice.DataFormat.R16G16B16A16Sfloat)
        {
            return;
        }
        var size = new Vector2I((int)format.Width, (int)format.Height);
        EnsureTarget(size);
        // Mip 0 sampled from the capture's colour at its own texel centres (a render buffer is not
        // copyable), each further mip the box of the one above.
        Span<Rid> rids = stackalloc Rid[2];
        for (int mip = 0; mip < Mips; mip++)
        {
            int w = System.Math.Max(size.X >> mip, 1), h = System.Math.Max(size.Y >> mip, 1);
            Rid read = mip == 0 ? source : _views[mip - 1];
            Refii.Core.Engine_UniformSetKey key = _sets[mip] ??= new Refii.Core.Engine_UniformSetKey(2);
            rids[0] = read;
            rids[1] = _views[mip];
            Rid set = key.For(_rd, rids);
            if (!set.IsValid)
            {
                var above = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 0 };
                above.AddId(_linear);
                above.AddId(read);
                var below = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 1 };
                below.AddId(_views[mip]);
                set = key.Hold(Engine_RdCalls.CachedUniformSet(_shader, 0, above, below));
            }
            long list = _rd.ComputeListBegin();
            _rd.ComputeListBindComputePipeline(list, _pipeline);
            _rd.ComputeListBindUniformSet(list, set, 0);
            _rd.ComputeListDispatch(list, (uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);
            _rd.ComputeListEnd();
        }
    }

    private void FreeTarget(RenderingDevice rd)
    {
        for (int mip = 0; mip < Mips; mip++)
        {
            if (_views[mip].IsValid)
            {
                rd.FreeRid(_views[mip]);
                _views[mip] = default;
            }
        }
        if (_target.IsValid)
        {
            Refii.Core.Engine_TracedMemory.Free(Refii.Core.Engine_RdRetire.TracedPool, _target.Id);
            rd.FreeRid(_target);
            _target = default;
        }
    }

    public override void _Notification(int what)
    {
        if (what != NotificationPredelete)
        {
            return;
        }
        Engine_ClassicImpostorMips self = this;
        Refii.Core.Engine_RdRetire.OnRenderThread(() => self.ReleaseDeviceObjects());
    }

    /// <summary>Frees the device objects. Render thread (<see cref="Refii.Core.Engine_RdRetire.OnRenderThread"/>).</summary>
    private void ReleaseDeviceObjects()
    {
        _ready = false;
        RenderingDevice? rd = RenderingServer.GetRenderingDevice();
        if (_rd is null || rd is null)
        {
            _rd = null;
            return;
        }
        FreeTarget(rd);
        if (_linear.IsValid) rd.FreeRid(_linear);
        if (_pipeline.IsValid) rd.FreeRid(_pipeline);
        if (_shader.IsValid) rd.FreeRid(_shader);
        _linear = _pipeline = _shader = default;
        _rd = null;
    }
}
