using System;
using Godot;
using Godot.Collections;

namespace Refii.Lighting;

[Tool]
public partial class Engine_PostFxEffect : CompositorEffect
{
    public const string ReduceShaderPath = "res://src/Engine/Lighting/fable_postfx_reduce.glsl";
    public const string HistogramShaderPath = "res://src/Engine/Lighting/fable_postfx_histogram.glsl";
    public const string Downscale4ShaderPath = "res://src/Engine/Lighting/fable_postfx_downscale4.glsl";
    public const string RadialShaderPath = "res://src/Engine/Lighting/fable_postfx_radial.glsl";
    public const string BrightcutShaderPath = "res://src/Engine/Lighting/fable_postfx_brightcut.glsl";
    public const string GaussShaderPath = "res://src/Engine/Lighting/fable_postfx_gauss.glsl";
    public const string CompositeShaderPath = "res://src/Engine/Lighting/fable_postfx_composite.glsl";
    public const string SunLayerShaderPath = "res://src/Engine/Lighting/fable_postfx_sunlayer.glsl";
    public const string HaloLayerShaderPath = "res://src/Engine/Lighting/fable_postfx_halolayer.glsl";

    public const string GlobalExposureName = "fable_exposure_tex";
    public const string GlobalExposureValidName = "fable_exposure_valid";
    public const float DisabledMaterialExposure = 1.0f;
    public const float ElementDepthBias = 0.000001f;

    public const int HistogramBins = Engine_PostFxModel.Bins + 1;
    public const int HistogramBytes = HistogramBins * sizeof(uint);
    public const int StateBytes = Engine_PostFxState.Bytes;
    public const int ParamsFloats = 64;
    public const int ParamsBytes = ParamsFloats * sizeof(float);
    public const int SmallPushBytes = 16;
    public const int WidePushBytes = 32;
    public const int GaussPushBytes = 48;

    public const int HaloCapacity = 64;
    public const int HaloFloats = 12;
    public const int HaloBufferBytes = HaloCapacity * HaloFloats * sizeof(float);

    public const float BloomSigmaScale = 2.2f;
    public const float DofSigmaScale = 2.0f;
    public const float SigmaPerQuarterWidth = 0.003125f;
    public const float SigmaPerQuarterHeight = 0.0055556f;

    private static bool _globalsRegistered;
    private static Texture2Drd? _placeholderTexture;
    private static Engine_PostFxEffect? _publisher;

    private sealed class KeyedSet
    {
        private Rid _a;
        private Rid _b;

        public Rid Set { get; private set; }

        public bool Matches(RenderingDevice rd, Rid a, Rid b) =>
            Set.IsValid && a == _a && b == _b && rd.UniformSetIsValid(Set);

        public void Replace(RenderingDevice rd, Rid a, Rid b, Rid shader, Array<RDUniform> uniforms)
        {
            Release(rd);
            Set = rd.UniformSetCreate(uniforms, shader, 0);
            _a = a;
            _b = b;
        }

        public void Release(RenderingDevice rd)
        {
            if (Set.IsValid && rd.UniformSetIsValid(Set))
            {
                rd.FreeRid(Set);
            }

            Set = default;
            _a = default;
            _b = default;
        }
    }

    private readonly int _dumpPeriod;
    private long _frameIndex;

    private RenderingDevice? _rd;
    private Texture2Drd? _exposureTexture;

    private Rid _reduceShader;
    private Rid _reducePipeline;
    private Rid _histogramShader;
    private Rid _histogramPipeline;
    private Rid _downscale4Shader;
    private Rid _downscale4Pipeline;
    private Rid _radialShader;
    private Rid _radialPipeline;
    private Rid _sunlayerShader;
    private Rid _sunlayerPipeline;
    private Rid _halolayerShader;
    private Rid _halolayerPipeline;
    private Rid _brightcutShader;
    private Rid _brightcutPipeline;
    private Rid _gaussShader;
    private Rid _gaussPipeline;
    private Rid _compositeShader;
    private Rid _compositePipeline;

    private Rid _histogramBuffer;
    private Rid _stateBuffer;
    private Rid _paramsBuffer;
    private Rid _haloBuffer;
    private Rid _haloFallbackArray;
    private Rid _linearSampler;
    private Rid _nearestSampler;
    private Rid _exposureRd;

    private Rid _quarter;
    private Rid _occl;
    private readonly Rid[] _rays = new Rid[2];
    private readonly Rid[] _bloom = new Rid[2];
    private readonly Rid[] _dof = new Rid[2];
    private Vector2I _fullSize;
    private Vector2I _quarterSize;

    private Rid _reduceSet;
    private Rid _histogramSet;
    private Rid _brightcutSet;
    private readonly Rid[] _radialSets = new Rid[2];
    private Rid _gaussBloomHSet;
    private Rid _gaussBloomVSet;
    private Rid _gaussDofHSet;
    private Rid _gaussDofVSet;
    private readonly KeyedSet _downscale4Set = new();
    private readonly KeyedSet _compositeSet = new();
    private readonly KeyedSet _sunlayerSet = new();
    private readonly KeyedSet _halolayerSet = new();

    private readonly byte[] _pushSmall = new byte[SmallPushBytes];
    private readonly byte[] _pushWide = new byte[WidePushBytes];
    private readonly byte[] _pushGauss = new byte[GaussPushBytes];

    private readonly float[] _bloomWeightsH = new float[Engine_PostFxModel.GaussianTaps];
    private readonly float[] _bloomWeightsV = new float[Engine_PostFxModel.GaussianTaps];
    private readonly float[] _dofWeightsH = new float[Engine_PostFxModel.GaussianTaps];
    private readonly float[] _dofWeightsV = new float[Engine_PostFxModel.GaussianTaps];
    private readonly float[] _paramsScratch = new float[ParamsFloats];
    private readonly byte[] _paramsBytes = new byte[ParamsBytes];
    private readonly float[] _haloScratch = new float[HaloCapacity * HaloFloats];
    private readonly byte[] _haloBytes = new byte[HaloBufferBytes];
    private readonly System.Collections.Generic.List<Refii.Fx.Engine_PostFxHalos.WorldHalo> _haloSnapshot = new();
    private readonly Engine_PostFxModel.Derived _derived = new();

    private volatile bool _ready;
    private bool _disposed;
    private bool _firstFrame = true;
    private bool _histogramValid;
    private bool _measuredAny;
    private bool _materialDisabledWritten;
    private double _lastFrameSeconds;
    private Rid _checkedColour;
    private bool _colourUsable;

    public static void RegisterGlobals()
    {
        if (_globalsRegistered)
        {
            return;
        }

        _globalsRegistered = true;
        _placeholderTexture ??= new Texture2Drd();
        RenderingServer.GlobalShaderParameterSet(GlobalExposureName, _placeholderTexture);
        RenderingServer.GlobalShaderParameterSet(GlobalExposureValidName, 0.0f);
    }

    public Engine_PostFxEffect()
    {
        EffectCallbackType = EffectCallbackTypeEnum.PostTransparent;

        if (!int.TryParse(OS.GetEnvironment("REFII_EXPOSURE_DUMP"), out int period) || period <= 0)
        {
            try
            {
                period = Refii.Core.Engine_RefiiConfig.Int(
                    Refii.Core.Engine_RefiiConfig.Read(out _), "postfx_dump_period", 0);
            }
            catch (System.Exception e)
            {
                GD.PrintErr($"[postfx] could not read postfx_dump_period: {e.Message}");
                period = 0;
            }
        }

        if (period > 0)
        {
            _dumpPeriod = period;
            GD.Print($"[postfx] dumping the histogram and state every {period} frame(s); " +
                "this reads back from the GPU and stalls the device");
        }

        RegisterGlobals();
        RenderingServer.CallOnRenderThread(Callable.From(Initialise));
    }

    private void Initialise()
    {
        if (_disposed)
        {
            return;
        }

        _rd = RenderingServer.GetRenderingDevice();
        if (_rd is null)
        {
            return;
        }

        if (!TryBuild(ReduceShaderPath, out _reduceShader, out _reducePipeline) ||
            !TryBuild(HistogramShaderPath, out _histogramShader, out _histogramPipeline) ||
            !TryBuild(Downscale4ShaderPath, out _downscale4Shader, out _downscale4Pipeline) ||
            !TryBuild(RadialShaderPath, out _radialShader, out _radialPipeline) ||
            !TryBuild(SunLayerShaderPath, out _sunlayerShader, out _sunlayerPipeline) ||
            !TryBuild(HaloLayerShaderPath, out _halolayerShader, out _halolayerPipeline) ||
            !TryBuild(BrightcutShaderPath, out _brightcutShader, out _brightcutPipeline) ||
            !TryBuild(GaussShaderPath, out _gaussShader, out _gaussPipeline) ||
            !TryBuild(CompositeShaderPath, out _compositeShader, out _compositePipeline))
        {
            return;
        }

        _histogramBuffer = _rd.StorageBufferCreate(HistogramBytes, new byte[HistogramBytes]);
        _stateBuffer = _rd.StorageBufferCreate(StateBytes, Engine_PostFxState.SeedBytes());
        _paramsBuffer = _rd.UniformBufferCreate(ParamsBytes, new byte[ParamsBytes]);
        _haloBuffer = _rd.StorageBufferCreate(HaloBufferBytes, new byte[HaloBufferBytes]);

        // The sprite-array binding must always hold something valid; before the halo feed has
        // resolved any authored sprite it binds this single white texel (every published layer
        // is -1 then, so the shader never actually samples it).
        _haloFallbackArray = _rd.TextureCreate(
            new RDTextureFormat
            {
                Width = 1,
                Height = 1,
                ArrayLayers = 1,
                Format = RenderingDevice.DataFormat.R8G8B8A8Unorm,
                TextureType = RenderingDevice.TextureType.Type2DArray,
                UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                    RenderingDevice.TextureUsageBits.CanUpdateBit,
            },
            new RDTextureView(),
            new Array<byte[]> { new byte[] { 255, 255, 255, 255 } });

        _linearSampler = _rd.SamplerCreate(new RDSamplerState
        {
            MinFilter = RenderingDevice.SamplerFilter.Linear,
            MagFilter = RenderingDevice.SamplerFilter.Linear,
            MipFilter = RenderingDevice.SamplerFilter.Linear,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
        });
        _nearestSampler = _rd.SamplerCreate(new RDSamplerState
        {
            MinFilter = RenderingDevice.SamplerFilter.Nearest,
            MagFilter = RenderingDevice.SamplerFilter.Nearest,
            MipFilter = RenderingDevice.SamplerFilter.Nearest,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
        });

        _exposureRd = CreateTexture(
            1, 1, RenderingDevice.DataFormat.R32Sfloat,
            BitConverter.GetBytes(DisabledMaterialExposure));

        bool ready = _histogramBuffer.IsValid && _stateBuffer.IsValid && _paramsBuffer.IsValid &&
            _haloBuffer.IsValid && _haloFallbackArray.IsValid && _linearSampler.IsValid &&
            _nearestSampler.IsValid && _exposureRd.IsValid;
        if (!ready)
        {
            GD.PrintErr("[postfx] buffer, sampler or exposure texture creation failed; chain disabled");
            return;
        }

        _reduceSet = _rd.UniformSetCreate(new Array<RDUniform>
        {
            Buffer(_histogramBuffer, 0),
            Buffer(_stateBuffer, 1),
            UniformBlock(_paramsBuffer, 2),
            Image(_exposureRd, 3),
        }, _reduceShader, 0);
        if (!_reduceSet.IsValid)
        {
            GD.PrintErr("[postfx] reduce uniform set creation failed; chain disabled");
            return;
        }

        _ready = true;

        Rid exposureRid = _exposureRd;
        Callable.From(() =>
        {
            if (_disposed || !_ready || _exposureRd != exposureRid)
            {
                return;
            }

            _exposureTexture ??= new Texture2Drd();
            _exposureTexture.TextureRdRid = exposureRid;
            _publisher = this;
            RenderingServer.GlobalShaderParameterSet(GlobalExposureName, _exposureTexture);
            RenderingServer.GlobalShaderParameterSet(GlobalExposureValidName, 1.0f);
        }).CallDeferred();
    }

    private bool TryBuild(string path, out Rid shader, out Rid pipeline)
    {
        shader = default;
        pipeline = default;

        var file = GD.Load<RDShaderFile>(path);
        if (file is null)
        {
            GD.PrintErr($"[postfx] could not load {path}");
            return false;
        }

        RDShaderSpirV spirv = file.GetSpirV();
        if (spirv.CompileErrorCompute.Length != 0)
        {
            GD.PrintErr($"[postfx] {path} failed to compile: {spirv.CompileErrorCompute}");
            return false;
        }

        shader = _rd!.ShaderCreateFromSpirV(spirv);
        if (!shader.IsValid)
        {
            GD.PrintErr($"[postfx] {path}: shader creation failed; chain disabled");
            return false;
        }

        pipeline = _rd.ComputePipelineCreate(shader);
        if (!pipeline.IsValid)
        {
            GD.PrintErr($"[postfx] {path}: compute pipeline creation failed; chain disabled");
            return false;
        }

        return true;
    }

    private Rid CreateTexture(int width, int height, RenderingDevice.DataFormat format, byte[]? initial = null)
    {
        var textureFormat = new RDTextureFormat
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = format,
            TextureType = RenderingDevice.TextureType.Type2D,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                RenderingDevice.TextureUsageBits.StorageBit |
                RenderingDevice.TextureUsageBits.CanUpdateBit |
                RenderingDevice.TextureUsageBits.CanCopyToBit,
        };

        Array<byte[]>? data = initial is null ? null : new Array<byte[]> { initial };
        return _rd!.TextureCreate(textureFormat, new RDTextureView(), data);
    }

    private void EnsureTargets(Vector2I size)
    {
        if (_quarter.IsValid && size == _fullSize)
        {
            return;
        }

        FreeTargets();
        _fullSize = size;
        _quarterSize = new Vector2I(Math.Max(size.X / 4, 1), Math.Max(size.Y / 4, 1));

        _quarter = CreateTexture(_quarterSize.X, _quarterSize.Y, RenderingDevice.DataFormat.R16G16B16A16Sfloat);
        _occl = CreateTexture(_quarterSize.X, _quarterSize.Y, RenderingDevice.DataFormat.R16G16B16A16Sfloat);
        for (int i = 0; i < 2; i++)
        {
            _rays[i] = CreateTexture(_quarterSize.X, _quarterSize.Y, RenderingDevice.DataFormat.R16G16B16A16Sfloat);
            _bloom[i] = CreateTexture(_quarterSize.X, _quarterSize.Y, RenderingDevice.DataFormat.R16G16B16A16Sfloat);
            _dof[i] = CreateTexture(_quarterSize.X, _quarterSize.Y, RenderingDevice.DataFormat.R16G16B16A16Sfloat);
        }

        Engine_PostFxModel.FixedGaussianWeights(
            _quarterSize.X * SigmaPerQuarterWidth * BloomSigmaScale, _bloomWeightsH);
        Engine_PostFxModel.FixedGaussianWeights(
            _quarterSize.Y * SigmaPerQuarterHeight * BloomSigmaScale, _bloomWeightsV);
        Engine_PostFxModel.FixedGaussianWeights(
            _quarterSize.X * SigmaPerQuarterWidth * DofSigmaScale, _dofWeightsH);
        Engine_PostFxModel.FixedGaussianWeights(
            _quarterSize.Y * SigmaPerQuarterHeight * DofSigmaScale, _dofWeightsV);

        _histogramSet = _rd!.UniformSetCreate(new Array<RDUniform>
        {
            Image(_quarter, 0),
            Buffer(_histogramBuffer, 1),
        }, _histogramShader, 0);
        _brightcutSet = _rd.UniformSetCreate(new Array<RDUniform>
        {
            Image(_quarter, 0),
            Image(_rays[0], 1),
            Image(_occl, 2),
            Buffer(_stateBuffer, 3),
            UniformBlock(_paramsBuffer, 4),
            Image(_bloom[0], 5),
        }, _brightcutShader, 0);
        for (int i = 0; i < 2; i++)
        {
            _radialSets[i] = _rd.UniformSetCreate(new Array<RDUniform>
            {
                Sampler(_nearestSampler, _rays[i], 0),
                Image(_rays[1 - i], 1),
                UniformBlock(_paramsBuffer, 2),
            }, _radialShader, 0);
        }

        _gaussBloomHSet = GaussSet(_bloom[0], _bloom[1]);
        _gaussBloomVSet = GaussSet(_bloom[1], _bloom[0]);
        _gaussDofHSet = GaussSet(_quarter, _dof[0]);
        _gaussDofVSet = GaussSet(_dof[0], _dof[1]);
    }

    private Rid GaussSet(Rid src, Rid dst) => _rd!.UniformSetCreate(new Array<RDUniform>
    {
        Image(src, 0),
        Image(dst, 1),
    }, _gaussShader, 0);

    private void FreeTargets()
    {
        FreeSet(ref _histogramSet);
        FreeSet(ref _brightcutSet);
        for (int i = 0; i < 2; i++)
        {
            FreeSet(ref _radialSets[i]);
        }

        FreeSet(ref _gaussBloomHSet);
        FreeSet(ref _gaussBloomVSet);
        FreeSet(ref _gaussDofHSet);
        FreeSet(ref _gaussDofVSet);
        if (_rd is not null)
        {
            _downscale4Set.Release(_rd);
            _compositeSet.Release(_rd);
            _sunlayerSet.Release(_rd);
            _halolayerSet.Release(_rd);
        }

        Free(ref _quarter);
        Free(ref _occl);
        for (int i = 0; i < 2; i++)
        {
            Free(ref _rays[i]);
            Free(ref _bloom[i]);
            Free(ref _dof[i]);
        }
    }

    private void FreeSet(ref Rid set)
    {
        if (set.IsValid && _rd is not null && _rd.UniformSetIsValid(set))
        {
            _rd.FreeRid(set);
        }

        set = default;
    }

    private void Free(ref Rid rid)
    {
        if (rid.IsValid && _rd is not null)
        {
            _rd.FreeRid(rid);
        }

        rid = default;
    }

    public override void _Notification(int what)
    {
        if (what != NotificationPredelete)
        {
            return;
        }

        _disposed = true;
        _ready = false;

        if (ReferenceEquals(_publisher, this))
        {
            _publisher = null;
            RenderingServer.GlobalShaderParameterSet(GlobalExposureValidName, 0.0f);
            if (_placeholderTexture is not null)
            {
                RenderingServer.GlobalShaderParameterSet(GlobalExposureName, _placeholderTexture);
            }
            else
            {
                RenderingServer.GlobalShaderParameterSet(GlobalExposureName, default(Variant));
            }
        }

        if (_exposureTexture is not null)
        {
            _exposureTexture.TextureRdRid = default;
            _exposureTexture = null;
        }

        if (_rd is null)
        {
            return;
        }

        if (RenderingServer.IsOnRenderThread())
        {
            ReleaseDeviceObjects();
            return;
        }

        Engine_PostFxEffect self = this;
        RenderingServer.CallOnRenderThread(Callable.From(() => self.ReleaseDeviceObjects()));
        RenderingServer.ForceSync();
    }

    private void ReleaseDeviceObjects()
    {
        if (_rd is null)
        {
            return;
        }

        FreeTargets();
        FreeSet(ref _reduceSet);
        Free(ref _exposureRd);
        Free(ref _nearestSampler);
        Free(ref _linearSampler);
        Free(ref _haloFallbackArray);
        Free(ref _haloBuffer);
        Free(ref _paramsBuffer);
        Free(ref _stateBuffer);
        Free(ref _histogramBuffer);
        Free(ref _compositePipeline);
        Free(ref _gaussPipeline);
        Free(ref _brightcutPipeline);
        Free(ref _radialPipeline);
        Free(ref _sunlayerPipeline);
        Free(ref _halolayerPipeline);
        Free(ref _downscale4Pipeline);
        Free(ref _histogramPipeline);
        Free(ref _reducePipeline);
        Free(ref _compositeShader);
        Free(ref _gaussShader);
        Free(ref _brightcutShader);
        Free(ref _radialShader);
        Free(ref _sunlayerShader);
        Free(ref _halolayerShader);
        Free(ref _downscale4Shader);
        Free(ref _histogramShader);
        Free(ref _reduceShader);
        _rd = null;
    }

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        if (!_ready || _rd is null)
        {
            return;
        }

        if (renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD buffers)
        {
            return;
        }

        Vector2I size = buffers.GetInternalSize();
        if (size.X <= 0 || size.Y <= 0)
        {
            return;
        }

        if (!Engine_PostFxParams.Enabled)
        {
            WriteDisabledMaterialExposure();
            return;
        }

        uint views = buffers.GetViewCount();
        if (views == 0 || !ColourLayerUsable(buffers.GetColorLayer(0)))
        {
            WriteDisabledMaterialExposure();
            return;
        }

        _materialDisabledWritten = false;
        float dt = Elapsed();
        Engine_PostFxParams.Snapshot(_derived);
        EnsureTargets(size);
        Engine_PostFxSunSources.Snapshot(out Engine_PostFxSunSources.Sources sun);

        bool bloomOn = Engine_PostFxParams.Bloom;
        bool raysOn = Engine_PostFxParams.Rays;
        bool dofOn = Engine_PostFxParams.Dof && (_derived.DofNearBlur > 0.0f || _derived.DofFarBlur > 0.0f);

        RenderSceneData? sceneData = renderData.GetRenderSceneData();
        Basis camBasis = Basis.Identity;
        Vector3 camOrigin = Vector3.Zero;
        Projection camProjection = Projection.Identity;
        if (sceneData is not null)
        {
            Transform3D camTransform = sceneData.GetCamTransform();
            camBasis = camTransform.Basis;
            camOrigin = camTransform.Origin;
            camProjection = sceneData.GetCamProjection();
        }

        UpdateParams(dt, sun.SunDirection, camBasis, camProjection, bloomOn, raysOn, dofOn);
        Reduce();
        _rd.BufferClear(_histogramBuffer, 0, HistogramBytes);

        for (uint view = 0; view < views; view++)
        {
            Rid colour = buffers.GetColorLayer(view);
            Rid depth = buffers.GetDepthLayer(view);
            Projection projection = sceneData is not null ? sceneData.GetViewProjection(view) : camProjection;
            bool sunOnScreen = TrySunScreenUv(
                sun.SunDirection, camBasis, projection, out Vector2 sunUv);


            Downscale4(colour, size);

            // THE RAYS ARE A RADIAL BLUR OF THE SUN LAYER — not of the scene, and not of a
            // synthesized beams billboard. Decoded from the frame segment (sub_821CB750): the
            // guest draws the depth-tested sun/moon discs into their own half-res [0,1] UNORM
            // layer (off_8331A9A8), DOWNSCALE2Xes it and runs RADIALBLUR on it TWICE, and that
            // buffer feeds the bright pass. SunLayer is the layer's content with the guest's
            // own per-pixel Z test, so a hill in front of the sun kills the rays exactly as
            // far as it covers.
            if (raysOn && sunOnScreen && sun.Disc.Active && sun.Disc.Texture.IsValid)
            {
                SunLayer(depth, sunUv, sun.Disc, projection);
                Radial(0, sunUv);
                Radial(1, sunUv);
            }
            else
            {
                Clear(_rays[0]);
            }

            // The GLOW LAYER: every stated halo, rasterised with the scene depth in hand. Its
            // only consumer is the brightcut (the guest's off_8331A9CC feeds nothing else), so
            // with bloom off it need not exist at all.
            if (!bloomOn || !HaloLayer(depth, camBasis, camOrigin, projection))
            {
                Clear(_occl);
            }

            if (bloomOn)
            {
                Brightcut();
                Gauss(_gaussBloomHSet, new Vector2I(1, 0), _bloomWeightsH);
                Gauss(_gaussBloomVSet, new Vector2I(0, 1), _bloomWeightsV);
            }
            else
            {
                Clear(_bloom[0]);
            }

            if (dofOn)
            {
                Gauss(_gaussDofHSet, new Vector2I(1, 0), _dofWeightsH);
                Gauss(_gaussDofVSet, new Vector2I(0, 1), _dofWeightsV);
            }

            Composite(colour, depth, size, projection.Inverse());
            Histogram();
        }

        _firstFrame = false;
        _histogramValid = true;

        if (_dumpPeriod > 0 && ++_frameIndex % _dumpPeriod == 0)
        {
            Dump(size, dt);
        }
    }

    private bool ColourLayerUsable(Rid colour)
    {
        if (colour == _checkedColour)
        {
            return _colourUsable;
        }

        _checkedColour = colour;
        RenderingDevice.DataFormat format = _rd!.TextureGetFormat(colour).Format;
        _colourUsable = format == RenderingDevice.DataFormat.R16G16B16A16Sfloat;
        if (!_colourUsable)
        {
            GD.PrintErr($"[postfx] colour layer is {format}; the chain needs rgba16f and is disabled");
        }

        return _colourUsable;
    }

    /// <summary>
    /// True only when the sun is IN FRONT of the camera. ProjectSun's failure value is the
    /// screen centre, so an unguarded caller paints a behind-the-camera sun mid-frame.
    /// </summary>
    private static bool TrySunScreenUv(
        Vector3 direction, Basis basis, Projection projection, out Vector2 uv)
    {
        float px = MathF.Abs(projection.X.X);
        float py = MathF.Abs(projection.Y.Y);
        float tanHalfX = px > 1e-8f ? 1.0f / px : 0.0f;
        float tanHalfY = py > 1e-8f ? 1.0f / py : 0.0f;
        return Engine_PostFxParams.ProjectSun(
            direction, -basis.Z, basis.X, basis.Y, tanHalfX, tanHalfY, out uv, out _);
    }

    private void WriteDisabledMaterialExposure()
    {
        if (_materialDisabledWritten)
        {
            return;
        }

        _materialDisabledWritten = true;
        _rd!.TextureUpdate(_exposureRd, 0, BitConverter.GetBytes(DisabledMaterialExposure));
    }

    private float Elapsed()
    {
        double now = Time.GetTicksUsec() / 1_000_000.0;
        float dt = _measuredAny ? (float)(now - _lastFrameSeconds) : 0.0f;
        _lastFrameSeconds = now;
        _measuredAny = true;
        return dt;
    }

    private void UpdateParams(
        float dt, Vector3 sunDirection, Basis camBasis, Projection camProjection,
        bool bloomOn, bool raysOn, bool dofOn)
    {
        Engine_PostFxModel.Derived d = _derived;
        float[] p = _paramsScratch;
        System.Array.Clear(p, 0, p.Length);

        float lockValue = Engine_PostFxParams.ExposureLock
            ? Math.Clamp(Engine_PostFxParams.ExposureLockValue, Engine_PostFxModel.ExposureMin, Engine_PostFxModel.ExposureMax)
            : 0.0f;
        Set(p, 0, dt, _firstFrame ? 1.0f : 0.0f, _histogramValid ? 1.0f : 0.0f, lockValue);
        Set(p, 1, d.ExposureSpeed, d.BlackMin, d.MidMin, d.WhiteMin);
        Set(p, 2, d.Pct[0], d.Pct[1], d.Pct[2], d.Knee);
        Set(p, 3, d.ContrastEnhancement, d.ContrastExponent, d.ContrastMaxDeviation, d.WhiteFactor);
        Set(p, 4,
            d.BloomThresholdAvg,
            d.BloomFactor * Engine_PostFxParams.BloomGain,
            raysOn ? 6.0f * d.RadialFactor * Engine_PostFxParams.RayGain : 0.0f,
            d.TonemapEnabled ? 1.0f : 0.0f);
        TrySunScreenUv(sunDirection, camBasis, camProjection, out Vector2 sunUv);
        Set(p, 5, sunUv.X, sunUv.Y, d.RadialFalloff, d.RadialMagnification);
        Set(p, 6,
            1.0f / MathF.Max(d.DofNearFocal - d.DofNearPlane, 1e-4f),
            d.DofNearFocal,
            d.DofFarFocal,
            1.0f / MathF.Max(d.DofFarPlane - d.DofFarFocal, 1e-4f));
        Set(p, 7, d.DofFarBlur, d.DofNearBlur, dofOn ? 1.0f : 0.0f, 0.0f);
        Set(p, 8, d.Saturation, d.Brightness + 1.0f, d.SaturationMasked, d.BrightnessMasked);

        float gammaRatio = d.Gamma > 0.0f && MathF.Abs(d.Gamma - 1.0f) > 1e-4f ? d.Gamma : 0.0f;
        Set(p, 9,
            1.0f / MathF.Max(d.ColourSensitivity, 1e-4f),
            gammaRatio,
            Math.Clamp(1.0f - d.Darkening, 0.0f, 1.0f),
            1.0f / Engine_PostFxModel.Range);
        Set(p, 10,
            d.TonemapEnabled ? 1.0f : 0.0f,
            dofOn ? 1.0f : 0.0f,
            Engine_PostFxParams.Saturation ? 1.0f : 0.0f,
            bloomOn ? 1.0f : 0.0f);

        Projection inverse = camProjection.Inverse();
        Set(p, 11, inverse.Z.Z, inverse.W.Z, inverse.Z.W, inverse.W.W);
        Set(p, 12,
            d.BloomFactor * Engine_PostFxParams.BloomGain,
            d.RadialFactor,
            Engine_PostFxParams.MaterialExposureFollowsApplied ? 1.0f : 0.0f,
            Engine_PostFxParams.BloomThresholdScale);

        System.Buffer.BlockCopy(p, 0, _paramsBytes, 0, ParamsBytes);
        _rd!.BufferUpdate(_paramsBuffer, 0, ParamsBytes, _paramsBytes);
    }

    private static void Set(float[] p, int slot, float x, float y, float z, float w)
    {
        int at = slot * 4;
        p[at] = x;
        p[at + 1] = y;
        p[at + 2] = z;
        p[at + 3] = w;
    }

    private void Reduce()
    {
        Dispatch(_reducePipeline, _reduceSet, null, 1, 1);
    }

    private void Downscale4(Rid colour, Vector2I size)
    {
        RenderingDevice rd = _rd!;
        if (!_downscale4Set.Matches(rd, colour, default))
        {
            _downscale4Set.Replace(rd, colour, default, _downscale4Shader, new Array<RDUniform>
            {
                Image(colour, 0),
                Image(_quarter, 1),
                Buffer(_stateBuffer, 2),
            });
        }

        Dispatch(
            _downscale4Pipeline,
            _downscale4Set.Set,
            PushSizes(size, _quarterSize),
            (_quarterSize.X + 7) / 8,
            (_quarterSize.Y + 7) / 8);
    }

    /// <summary>
    /// Synthesizes the guest's SUN LAYER into _rays[0]: the disc's own texture and colour at
    /// its projected position, per-pixel depth-tested against the scene. The guest renders
    /// this into off_8331A9A8 (half-res 2_10_10_10 UNORM, additive, z-tested) and the rays
    /// chain reads NOTHING else — not the scene, not a beams billboard.
    /// </summary>
    private void SunLayer(
        Rid depth, Vector2 sunUv, in Engine_PostFxSunSources.Source disc, Projection projection)
    {
        RenderingDevice rd = _rd!;
        if (!_sunlayerSet.Matches(rd, depth, disc.Texture))
        {
            _sunlayerSet.Replace(rd, depth, disc.Texture, _sunlayerShader, new Array<RDUniform>
            {
                Sampler(_nearestSampler, depth, 0),
                Sampler(_linearSampler, disc.Texture, 1),
                Image(_rays[0], 2),
            });
        }

        // World half-extent at the disc's stated distance, through the projection, into UV
        // units. The guest's draw is the same camera-facing quad; only the rasteriser differs.
        float px = MathF.Abs(projection.X.X);
        float py = MathF.Abs(projection.Y.Y);
        var halfUv = new Vector2(
            disc.Distance > 1e-3f ? disc.Half.X / disc.Distance * px * 0.5f : 0.0f,
            disc.Distance > 1e-3f ? disc.Half.Y / disc.Distance * py * 0.5f : 0.0f);

        // Reversed-Z: the far plane is where the scene wrote nothing. Same convention the
        // celestial overlay derives in-shader from PROJECTION[2][2].
        float farValue = -projection.Z.Z >= 0.5f ? 1.0f : 0.0f;

        var push = new byte[48];
        BitConverter.GetBytes(_quarterSize.X).CopyTo(push, 0);
        BitConverter.GetBytes(_quarterSize.Y).CopyTo(push, 4);
        BitConverter.GetBytes(farValue).CopyTo(push, 8);
        BitConverter.GetBytes(sunUv.X).CopyTo(push, 16);
        BitConverter.GetBytes(sunUv.Y).CopyTo(push, 20);
        BitConverter.GetBytes(halfUv.X).CopyTo(push, 24);
        BitConverter.GetBytes(halfUv.Y).CopyTo(push, 28);
        BitConverter.GetBytes(disc.Colour.X).CopyTo(push, 32);
        BitConverter.GetBytes(disc.Colour.Y).CopyTo(push, 36);
        BitConverter.GetBytes(disc.Colour.Z).CopyTo(push, 40);
        BitConverter.GetBytes(1.0f).CopyTo(push, 44);

        Dispatch(
            _sunlayerPipeline,
            _sunlayerSet.Set,
            push,
            (_quarterSize.X + 7) / 8,
            (_quarterSize.Y + 7) / 8);
    }

    /// <summary>
    /// Rasterises the guest's GLOW LAYER into _occl: every registered halo as a depth-tested
    /// soft disc at its light's projected position. The guest draws these quads into
    /// off_8331A9CC (half-res 2_10_10_10 UNORM, additive, z-tested), whose only reader is the
    /// bright pass — a lamp's glow blooms but never brightens the scene draw, and a wall kills
    /// it per pixel. False = nothing visible; the caller clears the layer instead.
    /// </summary>
    private bool HaloLayer(Rid depth, Basis camBasis, Vector3 camOrigin, Projection projection)
    {
        Refii.Fx.Engine_PostFxHalos.Snapshot(_haloSnapshot, out Rid spriteArray);
        if (_haloSnapshot.Count == 0)
        {
            return false;
        }

        if (!spriteArray.IsValid)
        {
            spriteArray = _haloFallbackArray;
        }

        float px = MathF.Abs(projection.X.X);
        float py = MathF.Abs(projection.Y.Y);
        if (px <= 1e-8f || py <= 1e-8f)
        {
            return false;
        }

        Vector3 forward = -camBasis.Z;
        Vector3 right = camBasis.X;
        Vector3 up = camBasis.Y;

        int count = 0;
        foreach (Refii.Fx.Engine_PostFxHalos.WorldHalo halo in _haloSnapshot)
        {
            // ProjectSun's maths on a POSITION: the ratios are scale-free, so the camera-space
            // offset needs no normalising, and `facing` doubles as the view distance the
            // per-pixel occlusion test compares against.
            Vector3 offset = halo.Position - camOrigin;
            float facing = offset.Dot(forward);
            if (facing <= 0.05f)
            {
                continue;
            }

            float x = ((offset.Dot(right) * px / facing) + 1.0f) * 0.5f;
            float y = (1.0f - (offset.Dot(up) * py / facing)) * 0.5f;
            float hx = halo.WorldHalf / facing * px * 0.5f;
            float hy = halo.WorldHalf / facing * py * 0.5f;
            if (!float.IsFinite(x) || !float.IsFinite(y) ||
                x + hx < 0.0f || x - hx > 1.0f || y + hy < 0.0f || y - hy > 1.0f)
            {
                continue;
            }

            int at = count * HaloFloats;
            _haloScratch[at] = x;
            _haloScratch[at + 1] = y;
            _haloScratch[at + 2] = hx;
            _haloScratch[at + 3] = hy;
            _haloScratch[at + 4] = halo.Colour.X;
            _haloScratch[at + 5] = halo.Colour.Y;
            _haloScratch[at + 6] = halo.Colour.Z;
            _haloScratch[at + 7] = facing;
            _haloScratch[at + 8] = halo.Layer;
            _haloScratch[at + 9] = 0f;
            _haloScratch[at + 10] = 0f;
            _haloScratch[at + 11] = 0f;
            if (++count == HaloCapacity)
            {
                break;
            }
        }

        if (count == 0)
        {
            return false;
        }

        RenderingDevice rd = _rd!;
        uint bytes = (uint)(count * HaloFloats * sizeof(float));
        System.Buffer.BlockCopy(_haloScratch, 0, _haloBytes, 0, (int)bytes);
        rd.BufferUpdate(_haloBuffer, 0, bytes, _haloBytes);

        if (!_halolayerSet.Matches(rd, depth, spriteArray))
        {
            _halolayerSet.Replace(rd, depth, spriteArray, _halolayerShader, new Array<RDUniform>
            {
                Sampler(_nearestSampler, depth, 0),
                Image(_occl, 1),
                Buffer(_haloBuffer, 2),
                Sampler(_linearSampler, spriteArray, 3),
            });
        }

        Projection inverse = projection.Inverse();
        byte[] push = _pushWide;
        BitConverter.TryWriteBytes(push.AsSpan(0), _quarterSize.X);
        BitConverter.TryWriteBytes(push.AsSpan(4), _quarterSize.Y);
        BitConverter.TryWriteBytes(push.AsSpan(8), count);
        BitConverter.TryWriteBytes(push.AsSpan(12), 0.0f);
        BitConverter.TryWriteBytes(push.AsSpan(16), inverse.Z.Z);
        BitConverter.TryWriteBytes(push.AsSpan(20), inverse.W.Z);
        BitConverter.TryWriteBytes(push.AsSpan(24), inverse.Z.W);
        BitConverter.TryWriteBytes(push.AsSpan(28), inverse.W.W);

        Dispatch(
            _halolayerPipeline,
            _halolayerSet.Set,
            push,
            (_quarterSize.X + 7) / 8,
            (_quarterSize.Y + 7) / 8);
        return true;
    }

    private void Radial(int src, Vector2 centre)
    {
        Dispatch(
            _radialPipeline,
            _radialSets[src],
            PushSizeVector(_quarterSize, centre),
            (_quarterSize.X + 7) / 8,
            (_quarterSize.Y + 7) / 8);
    }

    private void Brightcut()
    {
        Dispatch(
            _brightcutPipeline,
            _brightcutSet,
            PushSizeFloats(_quarterSize, 0.0f, 0.0f),
            (_quarterSize.X + 7) / 8,
            (_quarterSize.Y + 7) / 8);
    }

    private void Gauss(Rid set, Vector2I direction, float[] weights)
    {
        Dispatch(
            _gaussPipeline,
            set,
            PushGauss(_quarterSize, direction, weights),
            (_quarterSize.X + 7) / 8,
            (_quarterSize.Y + 7) / 8);
    }

    private void Composite(Rid colour, Rid depth, Vector2I size, Projection inverse)
    {
        RenderingDevice rd = _rd!;
        if (!_compositeSet.Matches(rd, colour, depth))
        {
            _compositeSet.Replace(rd, colour, depth, _compositeShader, new Array<RDUniform>
            {
                Image(colour, 0),
                Buffer(_stateBuffer, 1),
                UniformBlock(_paramsBuffer, 2),
                Sampler(_linearSampler, _bloom[0], 3),
                Sampler(_linearSampler, _dof[1], 4),
                Sampler(_nearestSampler, depth, 5),
            });
        }

        Dispatch(
            _compositePipeline,
            _compositeSet.Set,
            PushSizeDepth(size, inverse),
            (size.X + 7) / 8,
            (size.Y + 7) / 8);
    }

    private void Histogram()
    {
        Dispatch(
            _histogramPipeline,
            _histogramSet,
            PushSizeFloats(_quarterSize, 0.0f, 0.0f),
            (_quarterSize.X + 15) / 16,
            (_quarterSize.Y + 15) / 16);
    }

    private void Clear(Rid texture)
    {
        _rd!.TextureClear(texture, new Color(0.0f, 0.0f, 0.0f, 0.0f), 0, 1, 0, 1);
    }

    private void Dump(Vector2I size, float dt)
    {
        byte[] histogram = _rd!.BufferGetData(_histogramBuffer, 0, HistogramBytes);
        byte[] stateBytes = _rd.BufferGetData(_stateBuffer, 0, StateBytes);
        if (histogram.Length < HistogramBytes || stateBytes.Length < StateBytes)
        {
            GD.PrintErr($"[postfx] readback short: {histogram.Length}/{HistogramBytes} bytes of " +
                $"histogram, {stateBytes.Length}/{StateBytes} of state");
            return;
        }

        var bins = new uint[HistogramBins];
        System.Buffer.BlockCopy(histogram, 0, bins, 0, HistogramBytes);
        var state = new float[Engine_PostFxState.Floats];
        System.Buffer.BlockCopy(stateBytes, 0, state, 0, StateBytes);
        GD.Print(Engine_PostFxReport.Format(bins, state, size.X, size.Y, dt, _frameIndex));
    }

    private void Dispatch(Rid pipeline, Rid uniformSet, byte[]? push, int groupsX, int groupsY)
    {
        long list = _rd!.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, pipeline);
        _rd.ComputeListBindUniformSet(list, uniformSet, 0);
        if (push is not null)
        {
            _rd.ComputeListSetPushConstant(list, push, (uint)push.Length);
        }

        _rd.ComputeListDispatch(list, (uint)groupsX, (uint)groupsY, 1);
        _rd.ComputeListEnd();
    }

    private static RDUniform Image(Rid texture, int binding)
    {
        var uniform = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.Image,
            Binding = binding,
        };
        uniform.AddId(texture);
        return uniform;
    }

    private static RDUniform Buffer(Rid buffer, int binding)
    {
        var uniform = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = binding,
        };
        uniform.AddId(buffer);
        return uniform;
    }

    private static RDUniform UniformBlock(Rid buffer, int binding)
    {
        var uniform = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.UniformBuffer,
            Binding = binding,
        };
        uniform.AddId(buffer);
        return uniform;
    }

    private static RDUniform Sampler(Rid sampler, Rid texture, int binding)
    {
        var uniform = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.SamplerWithTexture,
            Binding = binding,
        };
        uniform.AddId(sampler);
        uniform.AddId(texture);
        return uniform;
    }

    private byte[] PushSizeFloats(Vector2I size, float a, float b)
    {
        byte[] bytes = _pushSmall;
        BitConverter.TryWriteBytes(bytes.AsSpan(0), size.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), size.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), a);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), b);
        return bytes;
    }

    private byte[] PushSizes(Vector2I a, Vector2I b)
    {
        byte[] bytes = _pushSmall;
        BitConverter.TryWriteBytes(bytes.AsSpan(0), a.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), a.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), b.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), b.Y);
        return bytes;
    }

    private byte[] PushSizeVector(Vector2I size, Vector2 v)
    {
        byte[] bytes = _pushWide;
        BitConverter.TryWriteBytes(bytes.AsSpan(0), size.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), size.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), 0.0f);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), 0.0f);
        BitConverter.TryWriteBytes(bytes.AsSpan(16), v.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(20), v.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(24), 0.0f);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), 0.0f);
        return bytes;
    }

    private byte[] PushSizeDepth(Vector2I size, Projection inverse)
    {
        byte[] bytes = _pushWide;
        BitConverter.TryWriteBytes(bytes.AsSpan(0), size.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), size.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), 0.0f);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), 0.0f);
        BitConverter.TryWriteBytes(bytes.AsSpan(16), inverse.Z.Z);
        BitConverter.TryWriteBytes(bytes.AsSpan(20), inverse.W.Z);
        BitConverter.TryWriteBytes(bytes.AsSpan(24), inverse.Z.W);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), inverse.W.W);
        return bytes;
    }

    private byte[] PushGauss(Vector2I size, Vector2I direction, float[] weights)
    {
        byte[] bytes = _pushGauss;
        BitConverter.TryWriteBytes(bytes.AsSpan(0), size.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), size.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), direction.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), direction.Y);
        for (int i = 0; i < Engine_PostFxModel.GaussianTaps; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(16 + (i * 4)), weights[i]);
        }

        BitConverter.TryWriteBytes(bytes.AsSpan(44), 0.0f);
        return bytes;
    }
}
