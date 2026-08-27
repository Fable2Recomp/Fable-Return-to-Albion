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
    public const string SunSourceShaderPath = "res://src/Engine/Lighting/fable_postfx_sunsource.glsl";
    public const string RadialShaderPath = "res://src/Engine/Lighting/fable_postfx_radial.glsl";
    public const string BrightcutShaderPath = "res://src/Engine/Lighting/fable_postfx_brightcut.glsl";
    public const string GaussShaderPath = "res://src/Engine/Lighting/fable_postfx_gauss.glsl";
    public const string CompositeShaderPath = "res://src/Engine/Lighting/fable_postfx_composite.glsl";

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
    public const int SunSourcePushBytes = 64;

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
    private Rid _sunSourceShader;
    private Rid _sunSourcePipeline;
    private Rid _radialShader;
    private Rid _radialPipeline;
    private Rid _brightcutShader;
    private Rid _brightcutPipeline;
    private Rid _gaussShader;
    private Rid _gaussPipeline;
    private Rid _compositeShader;
    private Rid _compositePipeline;

    private Rid _histogramBuffer;
    private Rid _stateBuffer;
    private Rid _paramsBuffer;
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
    private readonly KeyedSet _beamsSet = new();
    private readonly KeyedSet _discSet = new();

    private readonly byte[] _pushSmall = new byte[SmallPushBytes];
    private readonly byte[] _pushWide = new byte[WidePushBytes];
    private readonly byte[] _pushGauss = new byte[GaussPushBytes];
    private readonly byte[] _pushSun = new byte[SunSourcePushBytes];

    private readonly float[] _bloomWeightsH = new float[Engine_PostFxModel.GaussianTaps];
    private readonly float[] _bloomWeightsV = new float[Engine_PostFxModel.GaussianTaps];
    private readonly float[] _dofWeightsH = new float[Engine_PostFxModel.GaussianTaps];
    private readonly float[] _dofWeightsV = new float[Engine_PostFxModel.GaussianTaps];
    private readonly float[] _paramsScratch = new float[ParamsFloats];
    private readonly byte[] _paramsBytes = new byte[ParamsBytes];
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
            !TryBuild(SunSourceShaderPath, out _sunSourceShader, out _sunSourcePipeline) ||
            !TryBuild(RadialShaderPath, out _radialShader, out _radialPipeline) ||
            !TryBuild(BrightcutShaderPath, out _brightcutShader, out _brightcutPipeline) ||
            !TryBuild(GaussShaderPath, out _gaussShader, out _gaussPipeline) ||
            !TryBuild(CompositeShaderPath, out _compositeShader, out _compositePipeline))
        {
            return;
        }

        _histogramBuffer = _rd.StorageBufferCreate(HistogramBytes, new byte[HistogramBytes]);
        _stateBuffer = _rd.StorageBufferCreate(StateBytes, Engine_PostFxState.SeedBytes());
        _paramsBuffer = _rd.UniformBufferCreate(ParamsBytes, new byte[ParamsBytes]);

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
            _linearSampler.IsValid && _nearestSampler.IsValid && _exposureRd.IsValid;
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
            _beamsSet.Release(_rd);
            _discSet.Release(_rd);
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
        Free(ref _paramsBuffer);
        Free(ref _stateBuffer);
        Free(ref _histogramBuffer);
        Free(ref _compositePipeline);
        Free(ref _gaussPipeline);
        Free(ref _brightcutPipeline);
        Free(ref _radialPipeline);
        Free(ref _sunSourcePipeline);
        Free(ref _downscale4Pipeline);
        Free(ref _histogramPipeline);
        Free(ref _reducePipeline);
        Free(ref _compositeShader);
        Free(ref _gaussShader);
        Free(ref _brightcutShader);
        Free(ref _radialShader);
        Free(ref _sunSourceShader);
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
        Projection camProjection = Projection.Identity;
        if (sceneData is not null)
        {
            camBasis = sceneData.GetCamTransform().Basis;
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
            Vector2 sunUv = SunScreenUv(sun.SunDirection, camBasis, projection);

            if (raysOn && ProjectBillboard(sun.Beams, sun.SunDirection, camBasis, projection,
                    out Vector2 beamsCentre, out Vector2 beamsHalf))
            {
                SunSource(sun.Beams, depth, _rays[0], _beamsSet, size, beamsCentre, beamsHalf);
                Radial(0, sunUv);
                Radial(1, sunUv);
            }
            else
            {
                Clear(_rays[0]);
            }

            if (bloomOn && ProjectBillboard(sun.Disc, sun.SunDirection, camBasis, projection,
                    out Vector2 discCentre, out Vector2 discHalf))
            {
                SunSource(sun.Disc, depth, _occl, _discSet, size, discCentre, discHalf);
            }
            else
            {
                Clear(_occl);
            }

            Downscale4(colour, size);

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

    private static Vector2 SunScreenUv(Vector3 direction, Basis basis, Projection projection)
    {
        float px = MathF.Abs(projection.X.X);
        float py = MathF.Abs(projection.Y.Y);
        float tanHalfX = px > 1e-8f ? 1.0f / px : 0.0f;
        float tanHalfY = py > 1e-8f ? 1.0f / py : 0.0f;
        Engine_PostFxParams.ProjectSun(
            direction, -basis.Z, basis.X, basis.Y, tanHalfX, tanHalfY, out Vector2 uv, out _);
        return uv;
    }

    private static bool ProjectBillboard(
        in Engine_PostFxSunSources.Source source, Vector3 direction, Basis basis, Projection projection,
        out Vector2 centre, out Vector2 half)
    {
        centre = default;
        half = default;
        if (!source.Active || !source.Texture.IsValid || !(source.Half.X > 0.0f) || !(source.Half.Y > 0.0f))
        {
            return false;
        }

        Vector3 forward = -basis.Z;
        Vector3 right = basis.X;
        Vector3 up = basis.Y;
        Vector3 world = direction * source.Distance;
        float depth = world.Dot(forward);
        if (!(depth > 0.0f))
        {
            return false;
        }

        if (depth < 0.01f)
        {
            depth = 0.01f;
        }

        float px = MathF.Abs(projection.X.X);
        float py = MathF.Abs(projection.Y.Y);
        if (!(px > 1e-8f) || !(py > 1e-8f))
        {
            return false;
        }

        float scaleX = px / depth;
        float scaleY = py / depth;
        centre = new Vector2(world.Dot(right) * scaleX, world.Dot(up) * scaleY);
        half = new Vector2(source.Half.X * scaleX, source.Half.Y * scaleY);
        return float.IsFinite(centre.X) && float.IsFinite(centre.Y) &&
            float.IsFinite(half.X) && float.IsFinite(half.Y) && half.X > 0.0f && half.Y > 0.0f;
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
            raysOn ? d.RadialFactor * Engine_PostFxParams.RayGain : 0.0f,
            d.TonemapEnabled ? 1.0f : 0.0f);
        Vector2 sunUv = SunScreenUv(sunDirection, camBasis, camProjection);
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

    private void SunSource(
        in Engine_PostFxSunSources.Source source, Rid depth, Rid dst, KeyedSet set,
        Vector2I size, Vector2 centre, Vector2 half)
    {
        RenderingDevice rd = _rd!;
        if (!set.Matches(rd, source.Texture, depth))
        {
            set.Replace(rd, source.Texture, depth, _sunSourceShader, new Array<RDUniform>
            {
                Sampler(_linearSampler, source.Texture, 0),
                Sampler(_nearestSampler, depth, 1),
                Image(dst, 2),
            });
        }

        Dispatch(
            _sunSourcePipeline,
            set.Set,
            PushSunSource(size, _quarterSize, centre, half, source.Colour, source.Mode, source.Exposure),
            (_quarterSize.X + 7) / 8,
            (_quarterSize.Y + 7) / 8);
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

    private byte[] PushSunSource(
        Vector2I full, Vector2I quarter, Vector2 centre, Vector2 half, Vector3 colour, int mode, float exposure)
    {
        byte[] bytes = _pushSun;
        BitConverter.TryWriteBytes(bytes.AsSpan(0), full.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), full.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), quarter.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), quarter.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(16), centre.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(20), centre.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(24), half.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), half.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(32), colour.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(36), colour.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(40), colour.Z);
        BitConverter.TryWriteBytes(bytes.AsSpan(44), 1.0f);
        BitConverter.TryWriteBytes(bytes.AsSpan(48), (float)mode);
        BitConverter.TryWriteBytes(bytes.AsSpan(52), exposure);
        BitConverter.TryWriteBytes(bytes.AsSpan(56), ElementDepthBias);
        BitConverter.TryWriteBytes(bytes.AsSpan(60), 0.0f);
        return bytes;
    }
}
