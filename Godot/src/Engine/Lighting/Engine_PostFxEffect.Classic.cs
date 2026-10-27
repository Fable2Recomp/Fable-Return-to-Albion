using System;
using Godot;
using Godot.Collections;

namespace Refii.Lighting;

/// <summary>
/// The classic look's post chain on the title's own translated passes (Engine_ClassicPostPass),
/// in the order the chain already runs them (`sub_822767E0`): DOWNSCALE4 into the quarter
/// target, RADIALBLUR twice over the sun layer, BRIGHTNESSCUTOFF, the fixed gaussians H and V for
/// the bloom and the depth of field, the POSTPROCESSINGMATERIAL variant the frame's flags pick,
/// and CONVERTTOLUMINANCE for the histogram the reduce pass bins. The reduce pass stays: it is
/// the title's CPU work (histogram stats, adaptation, the T(L)/L table, the exposure), not a
/// shader.
/// </summary>
/// <remarks>
/// Constants by name, from what the title binds (`sub_821E5DB8`, `sub_821B6730`, the agent pass
/// recorded in refii-classic-post-processing): g_BloomFactor = P+0x4C in the cutoff and twice it
/// in the composite; g_RadialBlurFactor = P+0x6C; g_BloomRescaleFactor = 2.0 (flt_8209B480);
/// g_BrightnessThresholdAndScale = (threshold, 1 / E) from the reduce state; g_Center, the
/// falloff and the magnification of the radial blur; g_FixedGuassBlurWeights the seven
/// normalised taps; g_RecipMaxLuminance 1/4; g_ColourSensitivityThreshold its reciprocal;
/// g_SaturationBrightnessBaseAndOffset (Saturation, Brightness + 1, SaturationMasked,
/// BrightnessMasked); g_GlobalGammaAdjustment GAMMA x 0.45454544; the DOF planes and caps; the
/// camera's depth terms. The additive layer is already in the colour (AddLayer), so the
/// composite never takes an ADDITIVEEFFECTS variant; no displacement buffer is drawn yet, so it
/// never takes a DISPLACEMENT one either.
/// </remarks>
public partial class Engine_PostFxEffect
{
    private const string ClassicLutShader =
        "#version 450\n" +
        "layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;\n" +
        "layout(r32f, set = 0, binding = 0) uniform restrict writeonly image2D lut;\n" +
        "layout(set = 0, binding = 1, std430) restrict readonly buffer StateBuffer { float v[]; } state;\n" +
        "void main() {\n" +
        "\tint i = int(gl_GlobalInvocationID.x);\n" +
        "\tif (i >= {SIZE}) { return; }\n" +
        "\timageStore(lut, ivec2(i, 0), vec4(state.v[{LUT} + i]));\n" +
        "}\n";

    /// <summary>The scene into the chain's own HDR copy: the composite samples it while it writes the scene.</summary>
    private const string ClassicCopyShader =
        "#version 450\n" +
        "layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;\n" +
        "layout(rgba16f, set = 0, binding = 0) uniform restrict readonly image2D src;\n" +
        "layout(rgba16f, set = 0, binding = 1) uniform restrict writeonly image2D dst;\n" +
        "void main() {\n" +
        "\tivec2 p = ivec2(gl_GlobalInvocationID.xy);\n" +
        "\tif (any(greaterThanEqual(p, imageSize(dst)))) { return; }\n" +
        "\timageStore(dst, p, imageLoad(src, p));\n" +
        "}\n";

    private static readonly (string Name, string Value)[] NoFixups = System.Array.Empty<(string, string)>();

    private const string CompositeStore = "vec4(xr_srgb_decode(clamp((c).rgb, 0.0, 1.0)), 1.0)";

    private Engine_ClassicPostPass? _cDownscale4;
    private Engine_ClassicPostPass? _cLuminance;
    private Engine_ClassicPostPass? _cRadial;
    private Engine_ClassicPostPass? _cBrightcut;
    private Engine_ClassicPostPass? _cGaussH;
    private Engine_ClassicPostPass? _cGaussV;
    private readonly System.Collections.Generic.Dictionary<string, Engine_ClassicPostPass?> _cComposite = new();
    private bool _classicTried;
    private bool _classicReady;
    private Rid _cLutShader;
    private Rid _cLutPipeline;
    private Rid _cLutSet;
    private Rid _cLut;
    private Rid _cLum;
    private Rid _cHdr;
    private Rid _cCopyShader;
    private Rid _cCopyPipeline;
    private readonly KeyedSet _cCopySet = new();
    private Rid _cHistogramSet;
    private Vector2I _cSize;
    private readonly (Rid Sampler, Rid Texture)[] _cTextures = new (Rid, Rid)[8];
    private readonly float[] _cWeights = new float[8];
    private Rid _cDisplacement;

    // This frame's state for the classic passes, set by the render callback.
    private Projection _frameProjection = Projection.Identity;
    private bool _frameDof;
    private bool _classicDofLogged;

    /// <summary>The classic look with its translated passes built.</summary>
    private bool ClassicPost => Refii.Models.Engine_ClassicMaterial.Active && EnsureClassicPost();

    private bool EnsureClassicPost()
    {
        if (_classicReady)
        {
            EnsureClassicTargets();
            return true;
        }
        if (_classicTried)
        {
            return false;
        }
        _classicTried = true;
        RenderingDevice rd = _rd!;

        string error = string.Empty;
        _cDownscale4 = Engine_ClassicPostPass.Build(rd, "PSHADER_POSTPROCESSING_DOWNSCALE4", NoFixups, string.Empty, out error);
        _cLuminance = _cDownscale4 is null ? null : Engine_ClassicPostPass.Build(rd, "PSHADER_POSTPROCESSING_CONVERTTOLUMINANCE", NoFixups, string.Empty, out error);
        _cRadial = _cLuminance is null ? null : Engine_ClassicPostPass.Build(rd, "PSHADER_POSTPROCESSING_RADIALBLUR", NoFixups, string.Empty, out error);
        // The cutoff's (threshold, scale) pair is last frame's statistics and exposure
        // (`sub_821B6730`: scale = 1 / P+0x41A8), which the reduce pass keeps.
        _cBrightcut = _cRadial is null ? null : Engine_ClassicPostPass.Build(rd, "PSHADER_POSTPROCESSING_BRIGHTNESSCUTOFF",
            new[]
            {
                ("ps_g_BrightnessThresholdAndScale",
                    $"vec4(xr_state.v[{Engine_PostFxState.Threshold}], 1.0 / max(xr_state.v[{Engine_PostFxState.ExposureFrame}], 1e-4), 0.0, 0.0)"),
            },
            // The cut writes an A2B10G10R10 UNORM target (`sub_8229F7E8` 0x8229F800, format 15),
            // which clamps to [0, 1] before the blurs; our rgba16f target does not.
            "clamp((c), 0.0, 1.0)", out error);
        _cGaussH = _cBrightcut is null ? null : Engine_ClassicPostPass.Build(rd, "PSHADER_POSTPROCESSING_HORIZONTAL_GAUSSBLUR", NoFixups, string.Empty, out error);
        _cGaussV = _cGaussH is null ? null : Engine_ClassicPostPass.Build(rd, "PSHADER_POSTPROCESSING_VERTICAL_GAUSSBLUR", NoFixups, string.Empty, out error);

        string lut = ClassicLutShader
            .Replace("{SIZE}", Engine_PostFxModel.CurveSize.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{LUT}", Engine_PostFxState.Lut.ToString(System.Globalization.CultureInfo.InvariantCulture));
        RDShaderSpirV spirv = rd.ShaderCompileSpirVFromSource(new RDShaderSource { SourceCompute = lut, Language = RenderingDevice.ShaderLanguage.Glsl });
        if (spirv.CompileErrorCompute.Length == 0)
        {
            _cLutShader = rd.ShaderCreateFromSpirV(spirv, "classic_tone_lut");
            _cLutPipeline = _cLutShader.IsValid ? rd.ComputePipelineCreate(_cLutShader) : default;
        }
        else if (error.Length == 0)
        {
            error = "the tone table copy failed to compile: " + spirv.CompileErrorCompute;
        }

        spirv = rd.ShaderCompileSpirVFromSource(new RDShaderSource { SourceCompute = ClassicCopyShader, Language = RenderingDevice.ShaderLanguage.Glsl });
        if (spirv.CompileErrorCompute.Length == 0)
        {
            _cCopyShader = rd.ShaderCreateFromSpirV(spirv, "classic_hdr_copy");
            _cCopyPipeline = _cCopyShader.IsValid ? rd.ComputePipelineCreate(_cCopyShader) : default;
        }
        else if (error.Length == 0)
        {
            error = "the HDR copy failed to compile: " + spirv.CompileErrorCompute;
        }

        if (_cGaussV is null || !_cLutPipeline.IsValid || !_cCopyPipeline.IsValid)
        {
            GD.Print($"[postfx] the classic look keeps the native chain: {error}");
            return false;
        }
        _classicReady = true;
        EnsureClassicTargets();
        GD.Print("[postfx] the classic look runs the title's translated post passes");
        return true;
    }

    /// <summary>The tone table, the luminance target and the composite's output, at this frame's size.</summary>
    private void EnsureClassicTargets()
    {
        RenderingDevice rd = _rd!;
        if (!_cLut.IsValid)
        {
            _cLut = CreateTexture(Engine_PostFxModel.CurveSize, 1, RenderingDevice.DataFormat.R32Sfloat);
            _cLutSet = rd.UniformSetCreate(new Array<RDUniform> { Image(_cLut, 0), Buffer(_stateBuffer, 1) }, _cLutShader, 0);
        }
        if (_cSize == _fullSize && _cHdr.IsValid)
        {
            return;
        }
        FreeClassicTargets();
        _cSize = _fullSize;
        _cHdr = CreateTexture(_fullSize.X, _fullSize.Y, RenderingDevice.DataFormat.R16G16B16A16Sfloat);
        _cLum = CreateTexture(_quarterSize.X, _quarterSize.Y, RenderingDevice.DataFormat.R16G16B16A16Sfloat);
        _cHistogramSet = rd.UniformSetCreate(new Array<RDUniform> { Image(_cLum, 0), Buffer(_histogramBuffer, 1) }, _histogramShader, 0);
    }

    private void FreeClassicTargets()
    {
        if (_rd is null)
        {
            return;
        }
        foreach (Engine_ClassicPostPass? pass in ClassicPasses())
        {
            pass?.Unbind(_rd);
        }
        foreach (Engine_ClassicPostPass? pass in new[] { _cSunBeams, _cMoonBloom, _cSunDisc })
        {
            pass?.Unbind(_rd);
        }
        FreeSet(ref _cHistogramSet);
        _cCopySet.Release(_rd);
        Free(ref _cHdr);
        Free(ref _cLum);
        _cSize = default;
    }

    private void FreeClassicPost()
    {
        if (_rd is null)
        {
            return;
        }
        FreeClassicSky();
        FreeSceneDepth();
        FreeClassicDecals();
        FreeClassicTargets();
        foreach (Engine_ClassicPostPass? pass in ClassicPasses())
        {
            pass?.Free(_rd);
        }
        _cComposite.Clear();
        _cDownscale4 = _cLuminance = _cRadial = _cBrightcut = _cGaussH = _cGaussV = null;
        FreeSet(ref _cLutSet);
        Free(ref _cLut);
        Free(ref _cLutPipeline);
        Free(ref _cLutShader);
        Free(ref _cCopyPipeline);
        Free(ref _cCopyShader);
        _classicReady = false;
        _classicTried = false;
    }

    private System.Collections.Generic.IEnumerable<Engine_ClassicPostPass?> ClassicPasses()
    {
        yield return _cDownscale4;
        yield return _cLuminance;
        yield return _cRadial;
        yield return _cBrightcut;
        yield return _cGaussH;
        yield return _cGaussV;
        foreach (Engine_ClassicPostPass? pass in _cComposite.Values)
        {
            yield return pass;
        }
    }

    /// <summary>DOWNSCALE4 (0161): the scene into the quarter target, four taps of texel centres.</summary>
    private void ClassicDownscale4(Rid colour)
    {
        _cTextures[0] = (Filter(_cDownscale4!, 0, _linearSampler), colour);
        _cDownscale4!.Run(_rd!, _quarter, _quarterSize, _stateBuffer, _cTextures.AsSpan(0, 1));
    }

    /// <summary>RADIALBLUR (0170) from one ray buffer into the other, toward the sun's screen position.</summary>
    private void ClassicRadial(int src, Vector2 centre)
    {
        Engine_ClassicPostPass pass = _cRadial!;
        pass.Set("ps_g_Center", centre.X, centre.Y);
        pass.Set("ps_g_FalloffMultiplier", _derived.RadialFalloff, _derived.RadialFalloff, _derived.RadialFalloff, _derived.RadialFalloff);
        pass.Set("ps_g_MagnificationFactor", _derived.RadialMagnification, _derived.RadialMagnification, _derived.RadialMagnification, _derived.RadialMagnification);
        _cTextures[0] = (Filter(pass, 0, _linearSampler), _rays[src]);
        pass.Run(_rd!, _rays[1 - src], _quarterSize, _stateBuffer, _cTextures.AsSpan(0, 1));
    }

    /// <summary>BRIGHTNESSCUTOFF (0163): the bloom source from the quarter scene, the rays and the glow layer.</summary>
    private void ClassicBrightcut()
    {
        Engine_ClassicPostPass pass = _cBrightcut!;
        float bloom = _derived.BloomFactor;
        float radial = _derived.RadialFactor;
        pass.Set("ps_g_BloomFactor", bloom, bloom, bloom, bloom);
        pass.Set("ps_g_RadialBlurFactor", radial, radial, radial, radial);
        pass.Set("ps_g_BloomRescaleFactor", 2f, 2f, 2f, 2f);
        Bind(pass, default, default);
        pass.Run(_rd!, _bloom[0], _quarterSize, _stateBuffer, _cTextures.AsSpan(0, pass.Samplers.Count));
    }

    /// <summary>The fixed gaussian (0166 / 0167): seven normalised taps either side of the texel.</summary>
    private void ClassicGauss(Rid src, Rid dst, Vector2I direction, float[] weights)
    {
        Engine_ClassicPostPass pass = direction.X != 0 ? _cGaussH! : _cGaussV!;
        System.Array.Clear(_cWeights);
        System.Array.Copy(weights, _cWeights, Math.Min(weights.Length, 7));
        pass.Set("ps_g_FixedGuassBlurWeights", _cWeights);
        _cTextures[0] = (Filter(pass, 0, _linearSampler), src);
        pass.Run(_rd!, dst, _quarterSize, _stateBuffer, _cTextures.AsSpan(0, 1));
    }

    /// <summary>
    /// The POSTPROCESSINGMATERIAL variant for this frame over the colour, sampling the scene from
    /// the chain's own copy of it: the variant reads around the pixel it writes, and Godot's
    /// scene colour takes storage writes but no copies.
    /// </summary>
    /// <summary>
    /// The title's <c>g_PerspectiveConstants</c>, c18 = (q, -near·q), for this frame's projection:
    /// its post shaders turn Godot's reversed depth into view distance as
    /// <c>c18.y / (1 - c18.x - z)</c>, the console's own <c>1 - z_d3d</c> map.
    /// </summary>
    /// <remarks>
    /// The render data's projection arrives in the device's reversed [0, 1] form, where Godot's
    /// <c>GetZNear</c>/<c>GetZFar</c> read the wrong planes: a 0.05 near camera gave near -0.05,
    /// far -0.1, q -50000, so every pixel's distance came out at -0.05 and the whole frame took
    /// the near blur, hero included (Howling Halls, 2026-10-25). In that form the clip z row IS
    /// the map — <c>z = -Z.Z + W.Z / d</c> — so <c>q = Z.Z + 1</c> and <c>near·q = W.Z</c>, the
    /// infinite far (Z.Z = 0) included. The camera's own GL form (Z.Z &lt; 0) keeps the accessors.
    /// </remarks>
    private static (float Q, float NearQ) PerspectiveConstants(in Projection projection)
    {
        if (projection.Z.Z < 0f)
        {
            float near = projection.GetZNear(), far = projection.GetZFar();
            float q = far / MathF.Max(far - near, 1e-6f);
            return (q, near * q);
        }

        return (projection.Z.Z + 1f, projection.W.Z);
    }

    private void ClassicComposite(Rid colour, Rid depth, Vector2I size)
    {
        Engine_PostFxModel.Derived d = _derived;
        bool tonemap = d.TonemapEnabled;
        // The DISPLACEMENT bit (`sub_821E5DB8`: 4 x byte_83496E23): anything was drawn into the
        // displacement target this frame.
        Rid displacement = Refii.Models.Engine_ClassicDisplacement.Drawn > 0
            ? RenderingServer.TextureGetRdTexture(Refii.Models.Engine_ClassicDisplacement.TextureFor(View))
            : default;
        bool displaced = displacement.IsValid;
        if (Refii.Models.Engine_ClassicDisplacement.Drawn > 0)
        {
            CopySceneDepth(depth, size);
        }
        string entry = !tonemap
            ? "PSHADER_POSTPROCESSINGMATERIAL" + (displaced ? "_DISPLACEMENT1" : string.Empty)
            : "PSHADER_POSTPROCESSINGMATERIAL" + (_frameDof ? "_DOF1" : string.Empty) +
                (displaced ? "_DISPLACEMENT1" : string.Empty) +
                (_maskWanted ? "_SATURATIONMASK1" : string.Empty) + "_TONEMAP1";
        if (!_cComposite.TryGetValue(entry, out Engine_ClassicPostPass? pass))
        {
            // g_ActualFramebufferBrightness is P+0x41AC: the exposure the materials took.
            pass = Engine_ClassicPostPass.Build(_rd!, entry,
                new[] { ("ps_g_ActualFramebufferBrightness", $"vec4(xr_state.v[{Engine_PostFxState.ExposureFrame}])") },
                CompositeStore, out string error);
            _cComposite[entry] = pass;
            if (pass is null)
            {
                GD.Print($"[postfx] classic composite {entry} unavailable: {error}");
            }
        }
        if (pass is null)
        {
            return;
        }

        float gamma = (d.Gamma > 0f ? d.Gamma : 1f) * 0.45454544f;
        float cst = 1f / MathF.Max(d.ColourSensitivity, 1e-4f);
        float range = 1f / Engine_PostFxModel.Range;
        float bloom = 2f * d.BloomFactor;
        pass.Set("ps_g_BloomFactor", bloom, bloom, bloom, bloom);
        pass.Set("ps_g_RecipMaxLuminance", range, range, range, range);
        pass.Set("ps_g_ColourSensitivityThreshold", cst, cst, cst, cst);
        pass.Set("ps_g_SaturationBrightnessBaseAndOffset", d.Saturation, d.Brightness + 1f, d.SaturationMasked, d.BrightnessMasked);
        pass.Set("ps_g_GlobalGammaAdjustment", gamma, gamma, gamma, gamma);
        pass.Set("ps_g_DepthOfFieldPlanes",
            1f / MathF.Max(d.DofNearFocal - d.DofNearPlane, 1e-4f), d.DofNearFocal,
            d.DofFarFocal, 1f / MathF.Max(d.DofFarPlane - d.DofFarFocal, 1e-4f));
        pass.Set("ps_g_DepthOfFieldUnitMaxBlurNearFar", d.DofNearBlur, d.DofFarBlur);
        (float q, float nearQ) = PerspectiveConstants(_frameProjection);
        pass.Set("ps_g_PerspectiveConstants", q, -nearQ);
        if (!_classicDofLogged)
        {
            // Once per run: the depth terms and the depth-of-field planes the title's shader is
            // handed, so a whole-frame blur can be read against the theme's numbers.
            _classicDofLogged = true;
            GD.Print($"[postfx] classic composite: q {q:0.####} near*q {nearQ:0.####} " +
                $"(projection Z.Z {_frameProjection.Z.Z:0.####} W.Z {_frameProjection.W.Z:0.####}); dof on {_frameDof}: " +
                $"planes near {d.DofNearPlane:0.##} nearFocal {d.DofNearFocal:0.##} farFocus {d.DofFarFocal:0.##} " +
                $"far {d.DofFarPlane:0.##}, blur near {d.DofNearBlur:0.###} far {d.DofFarBlur:0.###}");
        }
        // The displacement terms over the composite's input (a2[0], the HDR scene): offsets
        // stored in bytes, a maximum that scales with the target against 1280 x 720.
        float w = size.X, h = size.Y;
        pass.Set("ps_g_DisplacementParams", 255f / w - 1f, 255f / h - 1f, w / 255f, h / 255f);
        float maximum = 255f * 0.5f * (w / 1280f + h / 720f);
        pass.Set("ps_g_MaximumDisplacement", maximum, maximum, maximum, maximum);
        pass.Set("ps_g_InputResolution", 1f / w, 1f / h);
        _cDisplacement = displaced ? displacement : _exposureRd;

        long list = _rd!.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _cLutPipeline);
        _rd.ComputeListBindUniformSet(list, _cLutSet, 0);
        _rd.ComputeListDispatch(list, (uint)((Engine_PostFxModel.CurveSize + 63) / 64), 1, 1);
        _rd.ComputeListEnd();

        if (!_cCopySet.Matches(_rd, colour, _cHdr))
        {
            _cCopySet.Replace(_rd, colour, _cHdr, _cCopyShader, new Array<RDUniform> { Image(colour, 0), Image(_cHdr, 1) });
        }
        Dispatch(_cCopyPipeline, _cCopySet.Set, null, (size.X + 7) / 8, (size.Y + 7) / 8);

        Bind(pass, _cHdr, depth);
        pass.Run(_rd, colour, size, _stateBuffer, _cTextures.AsSpan(0, pass.Samplers.Count));
    }

    /// <summary>CONVERTTOLUMINANCE (0159) of the quarter scene, then the histogram of it.</summary>
    private void ClassicHistogram()
    {
        _cTextures[0] = (Filter(_cLuminance!, 0, _nearestSampler), _quarter);
        _cLuminance!.Run(_rd!, _cLum, _quarterSize, _stateBuffer, _cTextures.AsSpan(0, 1));
        Dispatch(_histogramPipeline, _cHistogramSet, PushSizeFloats(_quarterSize, 0f, 0f),
            (_quarterSize.X + 15) / 16, (_quarterSize.Y + 15) / 16);
    }

    /// <summary>The pass's samplers by name: the cutoff's and the composite's inputs.</summary>
    private void Bind(Engine_ClassicPostPass pass, Rid colour, Rid depth)
    {
        for (int i = 0; i < pass.Samplers.Count && i < _cTextures.Length; i++)
        {
            _cTextures[i] = pass.Samplers[i] switch
            {
                "ps_g_TextureSampler" => (_nearestSampler, _quarter),
                "ps_g_RadialBlurTextureSampler" => (_linearSampler, _rays[0]),
                "ps_g_AltBloomTextureSampler" => (_linearSampler, _occl),
                "ps_g_HDRSampler" => (_nearestSampler, colour),
                "ps_g_ToneMapSampler" => (_nearestSampler, _cLut),
                "ps_g_BloomSampler" => (_linearSampler, _bloom[0]),
                "ps_g_DepthSampler" => (_nearestSampler, depth),
                "ps_g_DepthOfFieldBlurSampler" => (_linearSampler, _dof[1]),
                "ps_g_SaturationMaskSampler" => (_nearestSampler, _satMask?.Texture ?? _exposureRd),
                "ps_g_DisplacementSampler" => (_linearSampler, _cDisplacement.IsValid ? _cDisplacement : _exposureRd),
                _ => (_linearSampler, _exposureRd),
            };
            _cTextures[i].Sampler = Filter(pass, i, _cTextures[i].Sampler);
        }
    }

    /// <summary>The filter the pass's stage states for its sampler <paramref name="index"/>, else <paramref name="fallback"/>.</summary>
    private Rid Filter(Engine_ClassicPostPass pass, int index, Rid fallback) =>
        index < pass.Samplers.Count ? pass.Filter(pass.Samplers[index], _nearestSampler, _linearSampler, fallback) : fallback;

    // ---- The classic sky's layer draws (Engine_ClassicSkyLayers) ----

    private const string ClassicVisibilityShader =
        "#version 450\n" +
        "layout(local_size_x = 2, local_size_y = 1, local_size_z = 1) in;\n" +
        "layout(r32f, set = 0, binding = 0) uniform restrict writeonly image2D visibility;\n" +
        "layout(set = 0, binding = 1, std430) restrict readonly buffer Counters { uint n[]; } counters;\n" +
        "layout(push_constant, std430) uniform Push { vec4 divisor; } push;\n" +
        "void main() {\n" +
        "\tint i = int(gl_GlobalInvocationID.x);\n" +
        "\tif (i > 1) { return; }\n" +
        "\tfloat d = i == 0 ? push.divisor.x : push.divisor.y;\n" +
        "\timageStore(visibility, ivec2(i, 0), vec4(clamp(float(counters.n[i]) * push.divisor.z / max(d, 1e-6), 0.0, 1.0)));\n" +
        "}\n";

    private readonly Engine_ClassicSkyLayers.Frame _skyFrame = new();
    private Engine_ClassicPostPass? _cSunBeams;
    private Engine_ClassicPostPass? _cMoonBloom;
    private Engine_ClassicPostPass? _cSunDisc;
    private bool _skyTried;
    private Rid _cFrameBuffer;
    private Rid _cCounters;
    private Rid _cVisibility;
    private Texture2Drd? _cVisibilityTexture;
    private Rid _cVisShader;
    private Rid _cVisPipeline;
    private Rid _cVisSet;
    private readonly float[] _cFrameFloats = new float[Refii.Models.Engine_ClassicConstants.Floats];
    private readonly byte[] _cFrameBytes = new byte[Refii.Models.Engine_ClassicConstants.Floats * 4];
    private readonly byte[] _cVisPush = new byte[16];
    private readonly (Rid Sampler, Rid Texture)[] _cSpriteTexture = new (Rid, Rid)[1];

    private bool EnsureClassicSky()
    {
        if (_cSunDisc is not null)
        {
            return true;
        }
        if (_skyTried)
        {
            return false;
        }
        _skyTried = true;
        RenderingDevice rd = _rd!;
        string error = string.Empty;
        _cSunBeams = Engine_ClassicPostPass.Build(rd, "PSHADER_SUNBEAMS", NoFixups, string.Empty, out error, Engine_ClassicPostPass.Kind.Sprite);
        _cMoonBloom = _cSunBeams is null ? null : Engine_ClassicPostPass.Build(rd, "PSHADER_MOONBLOOM", NoFixups, string.Empty, out error, Engine_ClassicPostPass.Kind.Sprite);
        Engine_ClassicPostPass? disc = _cMoonBloom is null ? null : Engine_ClassicPostPass.Build(rd, "PSHADER_SUNDISC", NoFixups, string.Empty, out error, Engine_ClassicPostPass.Kind.Sprite);
        RDShaderSpirV spirv = rd.ShaderCompileSpirVFromSource(new RDShaderSource { SourceCompute = ClassicVisibilityShader, Language = RenderingDevice.ShaderLanguage.Glsl });
        if (spirv.CompileErrorCompute.Length == 0)
        {
            _cVisShader = rd.ShaderCreateFromSpirV(spirv, "classic_sky_visibility");
            _cVisPipeline = _cVisShader.IsValid ? rd.ComputePipelineCreate(_cVisShader) : default;
        }
        if (disc is null || !_cVisPipeline.IsValid)
        {
            GD.Print($"[postfx] the classic sky layers stay off: {(error.Length != 0 ? error : spirv.CompileErrorCompute)}");
            return false;
        }
        _cFrameBuffer = rd.StorageBufferCreate((uint)_cFrameBytes.Length, _cFrameBytes);
        _cCounters = rd.StorageBufferCreate(8, new byte[8]);
        var one = new byte[8];
        BitConverter.TryWriteBytes(one.AsSpan(0), 1f);
        BitConverter.TryWriteBytes(one.AsSpan(4), 1f);
        _cVisibility = CreateTexture(2, 1, RenderingDevice.DataFormat.R32Sfloat, one);
        _cVisSet = rd.UniformSetCreate(new Array<RDUniform> { Image(_cVisibility, 0), Buffer(_cCounters, 1) }, _cVisShader, 0);
        _cSunDisc = disc;

        // Each view's chain counts its own sun and moon, so each publishes into its view's global.
        Rid visibility = _cVisibility;
        int view = View;
        Callable.From(() =>
        {
            if (_disposed || _cVisibility != visibility)
            {
                return;
            }
            _cVisibilityTexture = new Texture2Drd { TextureRdRid = visibility };
            Engine_ClassicSkyLayers.PublishVisibility(view, _cVisibilityTexture);
        }).CallDeferred();
        GD.Print("[postfx] the classic sky draws its beams, disc and moon bloom into the post layers");
        return true;
    }

    /// <summary>The sun beams into the sun layer; the screen position the radial blur centres on.</summary>
    private bool ClassicBeams(Rid depth, Basis basis, Vector3 origin, Projection projection, out Vector2 centre)
    {
        centre = new Vector2(0.5f, 0.5f);
        if (!EnsureClassicSky() || !_skyFrame.SunBeams.Active)
        {
            return false;
        }
        Engine_ClassicPostPass pass = _cSunBeams!;
        Vector4 c = _skyFrame.SunBeams.Colour;
        pass.Set("ps_g_SunColour", c.X, c.Y, c.Z, c.W);
        return Sprite(pass, _skyFrame.SunBeams, _rays[0], depth, basis, origin, projection, false, -1f, -1, out centre);
    }

    /// <summary>The moon bloom, then the sun disc, into the glow layer, counted; then the visibilities.</summary>
    private void ClassicGlow(Rid depth, Basis basis, Vector3 origin, Projection projection)
    {
        if (!EnsureClassicSky())
        {
            return;
        }
        RenderingDevice rd = _rd!;
        Refii.Models.Engine_ClassicConstants.CopyFrameView(View, true, _cFrameFloats);
        System.Buffer.BlockCopy(_cFrameFloats, 0, _cFrameBytes, 0, _cFrameBytes.Length);
        rd.BufferUpdate(_cFrameBuffer, 0, (uint)_cFrameBytes.Length, _cFrameBytes);
        rd.BufferClear(_cCounters, 0, 8);

        // `Render_QueryGatedEffectDraw_INFERRED` (0x82279410): SRCALPHA / INVSRCALPHA, alpha ref 10.
        if (_skyFrame.MoonBloom.Active)
        {
            Sprite(_cMoonBloom!, _skyFrame.MoonBloom, _occl, depth, basis, origin, projection, true, 10f / 255f, 1, out _);
        }
        // `0x821D8658`: ONE / ONE, alpha tested, c245 the disc colour.
        if (_skyFrame.SunDisc.Active)
        {
            Vector4 c = _skyFrame.SunDisc.Colour;
            _cSunDisc!.Set("ps_g_SunDiscColour", c.X, c.Y, c.Z, c.W);
            LastSunDisc = Sprite(_cSunDisc, _skyFrame.SunDisc, _occl, depth, basis, origin, projection, false, 0.5f / 255f, 0, out Vector2 discUv)
                ? new Vector3(discUv.X, discUv.Y, 0f) : new Vector3(-1f, -1f, -1f);
        }

        // sun = saturate(px / (SUN_DISC_SIZE^2 x 4000)), moon = saturate(px / (MOON_SIZE^2 x 6000)),
        // px on the title's 640 x 360 target.
        Span<float> push = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(_cVisPush.AsSpan());
        push[0] = _skyFrame.SunDiscSize * _skyFrame.SunDiscSize * 4000f;
        push[1] = _skyFrame.MoonSize * _skyFrame.MoonSize * 6000f;
        push[2] = Engine_ClassicSkyLayers.QueryPixels / Math.Max(_quarterSize.X * _quarterSize.Y, 1);
        push[3] = 0f;
        Dispatch(_cVisPipeline, _cVisSet, _cVisPush, 1, 1);
    }

    /// <summary>
    /// One sky quad into a layer: the guest's billboard (`sub_82A63D40`: centre = direction x
    /// distance + eye, corners along the view's right and up) projected to the layer's pixels.
    /// </summary>
    private bool Sprite(Engine_ClassicPostPass pass, in Engine_ClassicSkyLayers.Sprite sprite, Rid layer, Rid depth,
        Basis basis, Vector3 origin, Projection projection, bool mix, float alphaRef, int counter, out Vector2 centreUv)
    {
        centreUv = new Vector2(0.5f, 0.5f);
        Rid texture = RenderingServer.TextureGetRdTexture(sprite.Texture);
        if (!texture.IsValid)
        {
            return false;
        }
        Transform3D view = new Transform3D(basis, origin).AffineInverse();
        Vector3 centre = origin + sprite.Direction.Normalized() * sprite.Distance;
        Vector3 right = basis.X.Normalized() * sprite.Half.X, up = basis.Y.Normalized() * sprite.Half.Y;
        if (!Project(projection, view, centre, out Vector3 c) ||
            !Project(projection, view, centre - right + up, out Vector3 a) ||
            !Project(projection, view, centre + right - up, out Vector3 b))
        {
            return false;
        }
        centreUv = new Vector2(c.X, c.Y);
        var size = new Vector2(_quarterSize.X, _quarterSize.Y);
        var draw = new Engine_ClassicPostPass.SpriteDraw
        {
            Rect = new Vector4(Math.Min(a.X, b.X) * size.X, Math.Min(a.Y, b.Y) * size.Y,
                Math.Max(a.X, b.X) * size.X, Math.Max(a.Y, b.Y) * size.Y),
            Uv = sprite.Uv,
            Depth = Math.Max(c.Z, 0f),
            DepthScale = (float)_fullSize.X / Math.Max(_quarterSize.X, 1),
            AlphaRef = alphaRef,
            Mix = mix,
            Counter = counter,
        };
        _cSpriteTexture[0] = (_linearSampler, texture);
        pass.RunSprite(_rd!, layer, _stateBuffer, _cFrameBuffer, _cCounters, (_nearestSampler, depth), _cSpriteTexture, draw);
        return true;
    }

    /// <summary>
    /// A world point to (u, v) on the screen (v down) and its depth as Godot stores it (reversed).
    /// The render data's projection comes in either form: the camera's own (y up, depth in
    /// [-1, 1] near to far, Z.Z negative) or the device's (y flipped, reversed depth in [0, 1],
    /// Z.Z positive); both are read here, as the sign-free sun and halo projections do.
    /// </summary>
    private static bool Project(Projection projection, Transform3D view, Vector3 world, out Vector3 screen)
    {
        Vector3 v = view * world;
        Vector4 clip = projection * new Vector4(v.X, v.Y, v.Z, 1f);
        screen = default;
        if (clip.W <= 1e-6f)
        {
            return false;
        }
        float x = clip.X / clip.W, y = clip.Y / clip.W, z = clip.Z / clip.W;
        if (projection.Y.Y < 0f)
        {
            y = -y;
        }
        float depth = projection.Z.Z < 0f ? 0.5f - 0.5f * z : z;
        screen = new Vector3(x * 0.5f + 0.5f, 0.5f - y * 0.5f, depth);
        LastProjection = new Vector4(projection.X.X, projection.Y.Y, projection.Z.Z, projection.W.Z);
        return true;
    }

    /// <summary>For the debug channel: the projection's (X.X, Y.Y, Z.Z, W.Z) the sky sprites last saw.</summary>
    public static Vector4 LastProjection { get; private set; }

    /// <summary>For the debug channel: the sun disc's last screen (u, v, depth), or -1s.</summary>
    public static Vector3 LastSunDisc { get; private set; } = new(-1f, -1f, -1f);

    /// <summary>Puts this chain's view's visibility global back on the placeholder. Main thread, at predelete.</summary>
    private void WithdrawClassicSky()
    {
        if (_cVisibilityTexture is { } texture)
        {
            _cVisibilityTexture = null;
            Engine_ClassicSkyLayers.WithdrawVisibility(View, texture);
        }
    }

    private void FreeClassicSky()
    {
        if (_rd is null)
        {
            return;
        }
        foreach (Engine_ClassicPostPass? pass in new[] { _cSunBeams, _cMoonBloom, _cSunDisc })
        {
            pass?.Free(_rd);
        }
        _cSunBeams = _cMoonBloom = _cSunDisc = null;
        FreeSet(ref _cVisSet);
        Free(ref _cVisibility);
        Free(ref _cVisPipeline);
        Free(ref _cVisShader);
        Free(ref _cCounters);
        Free(ref _cFrameBuffer);
        _skyTried = false;
    }

    // ---- The scene depth for the displacement view (Engine_ClassicDisplacement) ----

    private const string ClassicDepthCopyShader =
        "#version 450\n" +
        "layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;\n" +
        "layout(set = 0, binding = 0) uniform sampler2D depth;\n" +
        "layout(r32f, set = 0, binding = 1) uniform restrict writeonly image2D copy;\n" +
        "void main() {\n" +
        "\tivec2 p = ivec2(gl_GlobalInvocationID.xy);\n" +
        "\tif (any(greaterThanEqual(p, imageSize(copy)))) { return; }\n" +
        "\timageStore(copy, p, vec4(texelFetch(depth, p, 0).r));\n" +
        "}\n";

    private Rid _cDepthShader;
    private Rid _cDepthPipeline;
    private Rid _cSceneDepth;
    private Vector2I _cSceneDepthSize;
    private readonly KeyedSet _cDepthSet = new();
    private bool _cDepthTried;

    /// <summary>The main view's depth into a texture the displacement materials read next frame.</summary>
    private void CopySceneDepth(Rid depth, Vector2I size)
    {
        RenderingDevice rd = _rd!;
        if (!_cDepthPipeline.IsValid)
        {
            if (_cDepthTried)
            {
                return;
            }
            _cDepthTried = true;
            RDShaderSpirV spirv = rd.ShaderCompileSpirVFromSource(new RDShaderSource { SourceCompute = ClassicDepthCopyShader, Language = RenderingDevice.ShaderLanguage.Glsl });
            if (spirv.CompileErrorCompute.Length != 0)
            {
                GD.Print($"[postfx] the scene depth copy failed to compile: {spirv.CompileErrorCompute}");
                return;
            }
            _cDepthShader = rd.ShaderCreateFromSpirV(spirv, "classic_scene_depth");
            _cDepthPipeline = rd.ComputePipelineCreate(_cDepthShader);
        }
        if (!_cSceneDepth.IsValid || _cSceneDepthSize != size)
        {
            _cDepthSet.Release(rd);
            Free(ref _cSceneDepth);
            _cSceneDepth = CreateTexture(size.X, size.Y, RenderingDevice.DataFormat.R32Sfloat);
            _cSceneDepthSize = size;
            Rid published = _cSceneDepth;
            Callable.From(() =>
            {
                if (_disposed || _cSceneDepth != published)
                {
                    return;
                }
                Refii.Models.Engine_ClassicDisplacement.EnsureRegistered();
                RenderingServer.GlobalShaderParameterSet(Refii.Models.Engine_ClassicDisplacement.SceneDepthFor(View),
                    new Texture2Drd { TextureRdRid = published });
            }).CallDeferred();
        }
        if (!_cDepthSet.Matches(rd, depth, _cSceneDepth))
        {
            _cDepthSet.Replace(rd, depth, _cSceneDepth, _cDepthShader,
                new Array<RDUniform> { Sampler(_nearestSampler, depth, 0), Image(_cSceneDepth, 1) });
        }
        Dispatch(_cDepthPipeline, _cDepthSet.Set, null, (size.X + 7) / 8, (size.Y + 7) / 8);
    }

    private void FreeSceneDepth()
    {
        if (_rd is null)
        {
            return;
        }
        _cDepthSet.Release(_rd);
        Free(ref _cSceneDepth);
        Free(ref _cDepthPipeline);
        Free(ref _cDepthShader);
        _cDepthTried = false;
    }

    // ---- The decals' projected grids (Engine_ClassicDecals) ----

    /// <summary>
    /// VSHADER_DECAL_MEMEXPORT's work (blob 0148, agent spec): each slot's 17 x 17 grid on the
    /// decal's plane, pushed onto the surface along its axis by two refinements against the depth
    /// (the title's projector depth; here the main view's), valid when the last step is within
    /// 0.4; the normal from the triangle fan of valid neighbours. Row 2s positions (NaN when not
    /// valid), row 2s+1 normals, guest axes.
    /// </summary>
    private const string ClassicDecalShader =
        "#version 450\n" +
        "layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;\n" +
        "layout(rgba32f, set = 0, binding = 0) uniform restrict writeonly image2D points;\n" +
        "layout(set = 0, binding = 1) uniform sampler2D depth;\n" +
        "layout(set = 0, binding = 2, std430) restrict readonly buffer Decals { vec4 v[]; } decals;\n" +
        "layout(push_constant, std430) uniform Push { mat4 view_proj; vec4 forward; vec4 terms; } push;\n" +
        "#define NAN uintBitsToFloat(0x7FC00000u)\n" +
        "bool project(vec3 p, out vec2 uv, out float view_depth) {\n" +
        "\tvec4 c = push.view_proj * vec4(p, 1.0);\n" +
        "\tuv = vec2(0.0); view_depth = 0.0;\n" +
        "\tif (c.w <= 1e-4) { return false; }\n" +
        "\tvec2 n = c.xy / c.w;\n" +
        "\tuv = vec2(n.x * 0.5 + 0.5, 0.5 - n.y * 0.5);\n" +
        "\tview_depth = c.w;\n" +
        "\treturn all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0)));\n" +
        "}\n" +
        // Godot's depth is reversed: d = 1 - z_d3d. The terms are the title's c18, (q, near·q)
        // (PerspectiveConstants), so the distance is near·q / (q - z_d3d), infinite far included.
        "float scene_view_depth(vec2 uv) {\n" +
        "\tfloat d = textureLod(depth, uv, 0.0).r;\n" +
        "\tif (d <= 0.0) { return -1.0; }\n" +
        "\treturn push.terms.y / max(push.terms.x - 1.0 + d, 1e-6);\n" +
        "}\n" +
        "bool refine(inout vec3 p, vec3 axis, out float delta) {\n" +
        "\tvec2 uv; float vd; delta = 0.0;\n" +
        "\tif (!project(p, uv, vd)) { return false; }\n" +
        "\tfloat sd = scene_view_depth(uv);\n" +
        "\tif (sd < 0.0) { return false; }\n" +
        "\tdelta = sd - vd;\n" +
        // A step along the axis moves the view depth by dot(axis, forward) per unit.
        "\tfloat along = dot(axis, push.forward.xyz);\n" +
        "\tif (abs(along) < 0.2) { along = along < 0.0 ? -0.2 : 0.2; }\n" +
        "\tp += axis * (delta / along);\n" +
        "\treturn true;\n" +
        "}\n" +
        "bool grid_point(int slot, int c, int r, out vec3 p) {\n" +
        "\tvec4 a = decals.v[slot * 3], b = decals.v[slot * 3 + 1], s = decals.v[slot * 3 + 2];\n" +
        "\tvec3 dir = normalize(b.xyz);\n" +
        "\tvec3 right = cross(dir, vec3(0.0, 0.0, 1.0));\n" +
        "\tright = length(right) < 1e-4 ? vec3(0.0) : normalize(right);\n" +
        "\tvec3 up = cross(right, dir);\n" +
        "\tup = length(up) < 1e-4 ? vec3(0.0) : normalize(up);\n" +
        "\tp = a.xyz + right * (float(c - 8) / 8.0 * s.x) + up * (-float(r - 8) / 8.0 * s.y);\n" +
        "\tfloat d1, d2;\n" +
        "\tif (!refine(p, dir, d1) || !refine(p, dir, d2)) { return false; }\n" +
        "\treturn abs(d2) <= 0.4;\n" +
        "}\n" +
        "void main() {\n" +
        "\tint i = int(gl_GlobalInvocationID.x), slot = int(gl_GlobalInvocationID.y);\n" +
        "\tif (i >= 289) { return; }\n" +
        "\tivec2 at = ivec2(i, slot * 2);\n" +
        "\tif (decals.v[slot * 3].w < 0.5) {\n" +
        "\t\timageStore(points, at, vec4(NAN));\n" +
        "\t\timageStore(points, at + ivec2(0, 1), vec4(0.0));\n" +
        "\t\treturn;\n" +
        "\t}\n" +
        "\tint c = i % 17, r = i / 17;\n" +
        "\tvec3 p;\n" +
        "\tif (!grid_point(slot, c, r, p)) {\n" +
        "\t\timageStore(points, at, vec4(NAN));\n" +
        "\t\timageStore(points, at + ivec2(0, 1), vec4(0.0));\n" +
        "\t\treturn;\n" +
        "\t}\n" +
        // The fan: ordered neighbour pairs (A, B), N += (B - P) x (A - P) where both are valid.
        "\tivec2 pairs[6] = ivec2[6](ivec2(-1, -17), ivec2(-17, -16), ivec2(-16, 1), ivec2(1, 17), ivec2(17, 16), ivec2(16, -1));\n" +
        "\tvec3 normal = vec3(0.0);\n" +
        "\tfor (int k = 0; k < 6; ++k) {\n" +
        "\t\tvec3 pa, pb;\n" +
        "\t\tint ia = i + pairs[k].x, ib = i + pairs[k].y;\n" +
        "\t\tint ca = ia % 17, cb = ib % 17;\n" +
        "\t\tif (ia < 0 || ib < 0 || ia >= 289 || ib >= 289 || abs(ca - c) > 1 || abs(cb - c) > 1) { continue; }\n" +
        "\t\tif (!grid_point(slot, ca, ia / 17, pa) || !grid_point(slot, cb, ib / 17, pb)) { continue; }\n" +
        "\t\tnormal += cross(pb - p, pa - p);\n" +
        "\t}\n" +
        "\tnormal = length(normal) > 1e-8 ? normalize(normal) : -normalize(decals.v[slot * 3 + 1].xyz);\n" +
        // The points sit on last frame's depth, and VS 0149's own bias (oPos.z - 0.002) moves
        // away from the eye under Godot's reversed depth: they are lifted 0.02 back along the
        // axis, the side the decal was projected from, so the surface does not fight them.
        "\tp -= normalize(decals.v[slot * 3 + 1].xyz) * 0.02;\n" +
        "\timageStore(points, at, vec4(p, 1.0));\n" +
        "\timageStore(points, at + ivec2(0, 1), vec4(normal, 0.0));\n" +
        "}\n";

    private Rid _cDecalShader;
    private Rid _cDecalPipeline;
    private Rid _cDecalPoints;
    private Rid _cDecalParams;
    private readonly KeyedSet _cDecalSet = new();
    private bool _cDecalTried;
    private readonly Refii.Models.Engine_ClassicDecals.Projection[] _cDecals = new Refii.Models.Engine_ClassicDecals.Projection[Refii.Models.Engine_ClassicDecals.Slots];
    private readonly float[] _cDecalFloats = new float[Refii.Models.Engine_ClassicDecals.Slots * 12];
    private readonly byte[] _cDecalBytes = new byte[Refii.Models.Engine_ClassicDecals.Slots * 48];
    private readonly byte[] _cDecalPush = new byte[96];

    /// <summary>Projects every live decal's grid onto this frame's depth. Render thread.</summary>
    private void ClassicDecals(Rid depth, Basis basis, Vector3 origin, Projection projection)
    {
        Refii.Models.Engine_ClassicDecals.Snapshot(_cDecals);
        bool any = false;
        for (int s = 0; s < _cDecals.Length; s++)
        {
            Refii.Models.Engine_ClassicDecals.Projection d = _cDecals[s];
            int o = s * 12;
            _cDecalFloats[o] = d.Position.X; _cDecalFloats[o + 1] = d.Position.Y; _cDecalFloats[o + 2] = d.Position.Z;
            _cDecalFloats[o + 3] = d.Active ? 1f : 0f;
            _cDecalFloats[o + 4] = d.Direction.X; _cDecalFloats[o + 5] = d.Direction.Y; _cDecalFloats[o + 6] = d.Direction.Z;
            _cDecalFloats[o + 7] = 0f;
            _cDecalFloats[o + 8] = d.Half.X; _cDecalFloats[o + 9] = d.Half.Y; _cDecalFloats[o + 10] = 0f; _cDecalFloats[o + 11] = 0f;
            any |= d.Active;
        }
        if (!any || !EnsureClassicDecals())
        {
            return;
        }
        RenderingDevice rd = _rd!;
        System.Buffer.BlockCopy(_cDecalFloats, 0, _cDecalBytes, 0, _cDecalBytes.Length);
        rd.BufferUpdate(_cDecalParams, 0, (uint)_cDecalBytes.Length, _cDecalBytes);
        if (!_cDecalSet.Matches(rd, depth, _cDecalPoints))
        {
            _cDecalSet.Replace(rd, depth, _cDecalPoints, _cDecalShader, new Array<RDUniform>
            {
                Image(_cDecalPoints, 0),
                Sampler(_nearestSampler, depth, 1),
                Buffer(_cDecalParams, 2),
            });
        }

        // Guest world -> Godot clip (GL, y up): the camera's projection and view after the
        // guest-to-Godot axes ((x, y, z) -> (-x, z, y)).
        var guestToGodot = new Projection(
            new Vector4(-1f, 0f, 0f, 0f), new Vector4(0f, 0f, 1f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(0f, 0f, 0f, 1f));
        Projection viewProj = projection * new Projection(new Transform3D(basis, origin).AffineInverse()) * guestToGodot;
        Span<float> push = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(_cDecalPush.AsSpan());
        for (int c = 0; c < 4; c++)
        {
            Vector4 column = c switch { 0 => viewProj.X, 1 => viewProj.Y, 2 => viewProj.Z, _ => viewProj.W };
            push[c * 4] = column.X; push[c * 4 + 1] = column.Y; push[c * 4 + 2] = column.Z; push[c * 4 + 3] = column.W;
        }
        Vector3 forward = -basis.Z;
        push[16] = -forward.X; push[17] = forward.Z; push[18] = forward.Y; push[19] = 0f;
        (float decalQ, float decalNearQ) = PerspectiveConstants(projection);
        push[20] = decalQ; push[21] = decalNearQ; push[22] = 0f; push[23] = 0f;
        Dispatch(_cDecalPipeline, _cDecalSet.Set, _cDecalPush, (289 + 63) / 64, Refii.Models.Engine_ClassicDecals.Slots);
    }

    private bool EnsureClassicDecals()
    {
        if (_cDecalPipeline.IsValid)
        {
            return true;
        }
        if (_cDecalTried)
        {
            return false;
        }
        _cDecalTried = true;
        RenderingDevice rd = _rd!;
        RDShaderSpirV spirv = rd.ShaderCompileSpirVFromSource(new RDShaderSource { SourceCompute = ClassicDecalShader, Language = RenderingDevice.ShaderLanguage.Glsl });
        if (spirv.CompileErrorCompute.Length != 0)
        {
            GD.Print($"[postfx] the decal projection failed to compile: {spirv.CompileErrorCompute}");
            return false;
        }
        _cDecalShader = rd.ShaderCreateFromSpirV(spirv, "classic_decals");
        _cDecalPipeline = rd.ComputePipelineCreate(_cDecalShader);
        _cDecalPoints = CreateTexture(Refii.Models.Engine_ClassicDecals.Points, Refii.Models.Engine_ClassicDecals.Slots * 2,
            RenderingDevice.DataFormat.R32G32B32A32Sfloat);
        _cDecalParams = rd.StorageBufferCreate((uint)_cDecalBytes.Length, _cDecalBytes);
        Rid published = _cDecalPoints;
        Callable.From(() =>
        {
            if (_disposed || _cDecalPoints != published)
            {
                return;
            }
            Refii.Models.Engine_ClassicDecals.EnsureRegistered();
            RenderingServer.GlobalShaderParameterSet(Refii.Models.Engine_ClassicDecals.PointsFor(View),
                new Texture2Drd { TextureRdRid = published });
        }).CallDeferred();
        return true;
    }

    private void FreeClassicDecals()
    {
        if (_rd is null)
        {
            return;
        }
        _cDecalSet.Release(_rd);
        Free(ref _cDecalPoints);
        Free(ref _cDecalParams);
        Free(ref _cDecalPipeline);
        Free(ref _cDecalShader);
        _cDecalTried = false;
    }
}
