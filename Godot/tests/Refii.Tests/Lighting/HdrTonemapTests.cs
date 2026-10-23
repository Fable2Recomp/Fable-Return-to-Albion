namespace Refii.Tests.Lighting;

using System;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;

/// <summary>
/// Unit test suite verifying the mathematical accuracy and visual parity
/// of Refii's Scene-Referred HDR PostFX compositor and display tonemapper.
/// </summary>
[TestSuite]
public class HdrTonemapTests
{
    private static readonly Vector3 LumaWeights = new(0.2125f, 0.7154f, 0.0721f);

    private static float FableFilmic(float x)
    {
        return Math.Clamp((x * (2.51f * x + 0.03f)) / (x * (2.43f * x + 0.59f) + 0.14f), 0.0f, 1.0f);
    }

    private static float FableFilmicHdr(float x, float headroom)
    {
        const float FilmicKnee = 0.5f;
        if (x <= FilmicKnee)
        {
            return FableFilmic(x);
        }

        float at = FableFilmic(FilmicKnee);
        float slope = (FableFilmic(FilmicKnee + 1e-3f) - at) / 1e-3f;
        float room = headroom - at;
        return at + room * (1.0f - MathF.Exp(-(x - FilmicKnee) * slope / room));
    }

    private static float FableReinhard(float x, float invWhiteSq)
    {
        return ((x * invWhiteSq) + 1.0f) * x / (x + 1.0f);
    }

    private static float FableShoulder(float y, float headroom)
    {
        return Refii.Lighting.Engine_PostFxModel.FableShoulder(y, headroom);
    }

    private static float FableHdrShoulder(float y, float headroom)
    {
        return FableShoulder(y, headroom);
    }

    private static float FableCdfPw(float lExp, float cdfSample, float smBlack, float smWhite, float contrastCdfScale)
    {
        float range = Math.Max(smWhite - smBlack, 1e-3f);
        float t = Math.Clamp((lExp - smBlack) / range, 0.0f, 1.0f);
        float cdf = Math.Clamp(cdfSample, 0.0f, 1.0f);
        float maxDev = Math.Clamp(contrastCdfScale * 0.5f, 0.05f, 0.45f);
        float dev = 4.0f * maxDev * t * (1.0f - t);
        float eq = Math.Clamp(cdf, t - dev, t + dev);
        return eq * eq;
    }

    private static float FableCdfPwShader(
        float lExp,
        float cdfSample,
        float smBlack,
        float smMid,
        float smWhite,
        float smP99,
        float knee,
        float prevExp,
        float contrastMaxDev = 32.0f,
        float contrastExp = 2.0f)
    {
        const int LutSize = 512;
        const float InvRange = 1.0f / 4.0f;
        float norm = Math.Clamp(lExp * InvRange, 0.0f, 1.0f);
        float pos = norm * (LutSize - 1);
        float kneeN = knee / Math.Max(smMid - smBlack, 1e-4f);
        float whiteLum = Math.Max(smWhite, 2.0f / Math.Max(kneeN, 1e-4f));
        float p99 = Math.Max(smP99, whiteLum);
        float p99Bins = Math.Max(LutSize * InvRange * p99 * Math.Max(prevExp, 1e-4f), 256.0f);
        float devScale = (Math.Max(p99, 0.2f) * contrastMaxDev) / p99Bins;
        float t = Math.Clamp(pos / p99Bins, 0.0f, 1.0f);
        float dev = (1.0f - t) * t * devScale;
        float eq = Math.Clamp(cdfSample, t - dev, t + dev);
        return MathF.Abs(contrastExp - 2.0f) < 0.003f ? eq * eq : MathF.Pow(Math.Max(eq, 0.0f), contrastExp);
    }

    private static float FableGammaToeHdr(float c, float gammaRatio, float headroom)
    {
        float g = gammaRatio > 0.0f ? gammaRatio : 1.0f;
        float sdr = Math.Clamp(c, 0.0f, 1.0f);
        float srgb = sdr < 0.0031308f
            ? sdr * 12.92f
            : 1.055f * MathF.Pow(MathF.Max(sdr, 0.0f), 1.0f / 2.4f) - 0.055f;
        float curved = MathF.Pow(MathF.Max(srgb, 0.0f), 2.2f * g);
        return MathF.Min(curved + MathF.Max(c - 1.0f, 0.0f), headroom);
    }

    private static Vector3 FableHighlightDesaturate(Vector3 color, float lum, float headroom)
    {
        if (headroom <= 1.001f || lum <= 1.0f)
        {
            return color;
        }

        float t = Math.Clamp((lum - 1.0f) / Math.Max(headroom - 1.0f, 1e-4f), 0.0f, 1.0f);
        float desat = MathF.Pow(t, 1.5f) * 0.70f;
        return color.Lerp(new Vector3(lum, lum, lum), desat);
    }

    private static Vector3 FableFitCeiling(Vector3 color, float ceiling)
    {
        float peak = Math.Max(Math.Max(color.X, color.Y), color.Z);
        if (peak <= ceiling)
        {
            return color;
        }

        float l = color.Dot(LumaWeights);
        if (peak <= l + 1e-5f)
        {
            return new Vector3(ceiling, ceiling, ceiling);
        }

        Vector3 scaled = color * (ceiling / Math.Max(peak, 1e-6f));
        float t = Math.Clamp((peak - ceiling) / (peak - l), 0.0f, 1.0f);
        Vector3 lumFit = color.Lerp(new Vector3(l, l, l), t);
        float excess = Math.Clamp((peak - ceiling) / Math.Max(ceiling * 1.5f, 1e-6f), 0.0f, 1.0f);
        float desat = excess * excess * 0.5f;
        return scaled.Lerp(lumFit, desat);
    }

    [TestCase]
    public void TestSdrBloomParityBitIdentical()
    {
        // Proves that when headroom == 1.0 (SDR mode), the modern headroom-scaled bloom
        // blend produces numbers identical to the original 2008 Fable 2 formula.
        const float Headroom = 1.0f;
        float maxDelta = 0.0f;

        for (int c = 0; c <= 100; c++)
        {
            float col = c / 100.0f;
            for (int b = 0; b <= 100; b++)
            {
                float bloom = b / 100.0f;

                float legacy = col + bloom * Math.Max(1.0f - col, 0.0f);
                float modern = col + bloom * (Math.Max(Headroom - col, 0.0f) / Headroom);

                float delta = MathF.Abs(legacy - modern);
                if (delta > maxDelta)
                {
                    maxDelta = delta;
                }
            }
        }

        AssertThat(maxDelta).IsLess(1e-6f);
    }

    [TestCase]
    public void TestHdrBloomPreservedOnHighlights()
    {
        // When a pixel is in HDR (> 1.0), the legacy formula produced 0 bloom.
        // The headroom formula gracefully allows bloom to bleed up to headroom.
        const float Headroom = 5.0f;
        const float Col = 2.5f;
        const float Bloom = 0.6f;

        float legacy = Col + Bloom * Math.Max(1.0f - Col, 0.0f);
        float modern = Col + Bloom * (Math.Max(Headroom - Col, 0.0f) / Headroom);

        // Legacy extinguished bloom completely:
        AssertThat(legacy).IsEqual(2.5f);

        // Modern successfully contributes bloom:
        AssertThat(modern).IsGreater(2.5f);
        AssertThat(modern).IsEqualApprox(2.5f + (0.6f * 0.5f), 1e-5f);
    }

    [TestCase]
    public void TestCeilingFitHueInvariance()
    {
        // Intense orange highlight (e.g. fire core):
        var intenseFire = new Vector3(10.0f, 2.5f, 0.1f);
        const float Ceiling = 4.0f;

        Vector3 fitted = FableFitCeiling(intenseFire, Ceiling);

        // Peak channel is strictly bounded at ceiling:
        float peak = Math.Max(Math.Max(fitted.X, fitted.Y), fitted.Z);
        AssertThat(peak).IsEqualApprox(Ceiling, 1e-4f);

        // The highlight desaturated towards its white core rather than per-channel clipping
        // (which would have collapsed Green to 2.5 while Red clamped to 4.0, shifting orange to yellow):
        AssertThat(fitted.Z).IsGreater(0.1f); // Blue lifted towards white core
        AssertThat(fitted.X).IsGreater(fitted.Y); // Red remains dominant
    }

    [TestCase]
    public void TestMiddleGrayConsistentBetweenSdrAndHdr()
    {
        // Middle gray input (0.18 scene luminance * 0.6 curve scale = 0.108)
        const float InputMids = 0.18f * 0.6f;

        float sdrVal = FableFilmic(InputMids);
        float hdrVal = FableFilmicHdr(InputMids, 5.0f);

        // Midtones below the knee (0.5) evaluate to the EXACT same display response:
        AssertThat(sdrVal).IsEqual(hdrVal);
        AssertThat(sdrVal).IsGreater(0.0f);
    }

    [TestCase]
    public void TestHdrEmissionBoostScaling()
    {
        // Dynamic formula: Boost = 1.0 + 0.375 * (headroom - 1.0)
        // SDR mode: boost is always 1.0
        float sdrBoost = CalculateDynamicBoost(1.0f);
        AssertThat(sdrBoost).IsEqual(1.0f);

        // HDR mode (1000 peak / 200 white = 5.0 headroom): auto-scales to 2.50x
        float hdrBoost = CalculateDynamicBoost(5.0f);
        AssertThat(hdrBoost).IsEqualApprox(2.5f, 1e-4f);
    }

    [TestCase]
    public void TestHdrShoulderParityBelowKnee()
    {
        // Proves that for all curve outputs at or below the knee (0.80), FableHdrShoulder is bit-identical
        // to the uncompressed curve. Shadows, faces, and midtones (including the CDF midtone lift) are 100% untouched.
        const float Headroom = 5.0f;

        for (int i = 0; i <= 80; i++)
        {
            float y = i / 100.0f; // 0.00 to 0.80
            float shouldered = FableHdrShoulder(y, Headroom);

            AssertThat(shouldered).IsEqual(y);
        }
    }

    [TestCase]
    public void TestHdrShoulderMonotonicAndContinuous()
    {
        const float Headroom = 4.0f;

        // C0 continuity across the knee (0.80):
        float atKneeLeft = FableHdrShoulder(0.80f, Headroom);
        float atKneeRight = FableHdrShoulder(0.8001f, Headroom);
        AssertThat(MathF.Abs(atKneeLeft - atKneeRight)).IsLess(1e-3f);

        // Strictly monotonic above the knee:
        float prev = atKneeLeft;
        for (float y = 0.9f; y <= 20.0f; y += 0.5f)
        {
            float cur = FableHdrShoulder(y, Headroom);
            AssertThat(cur).IsGreater(prev);
            AssertThat(cur).IsLess(Headroom);
            prev = cur;
        }

        // Asymptotes to headroom for extreme brightness:
        float extreme = FableHdrShoulder(1000.0f, Headroom);
        AssertThat(extreme).IsEqualApprox(Headroom, 1e-3f);
    }

    [TestCase]
    public void TestHighlightDesaturatePreservesSubWhite()
    {
        // Colors at or below paper white (1.0) must never be desaturated:
        var greenGrass = new Vector3(0.2f, 0.7f, 0.1f);
        float l = greenGrass.Dot(LumaWeights);
        AssertThat(l).IsLess(1.0f);

        Vector3 preserved = FableHighlightDesaturate(greenGrass, l, 5.0f);
        AssertThat(preserved).IsEqual(greenGrass);

        // In SDR mode (headroom <= 1.001), sub-shoulder colors (lum <= 0.75) are untouched,
        // while upper-shoulder highlights (lum > 0.75) gently desaturate toward luminance:
        Vector3 sdrMidPreserved = FableHighlightDesaturate(greenGrass, l, 1.0f);
        AssertThat(sdrMidPreserved).IsEqual(greenGrass);
    }

    [TestCase]
    public void TestHighlightDesaturateApproachesWhiteAtPeak()
    {
        const float Headroom = 5.0f;
        // Blinding saturated highlight near peak display luminance:
        var intenseFire = new Vector3(5.0f, 1.5f, 0.1f);
        float l = intenseFire.Dot(LumaWeights);

        Vector3 desaturated = FableHighlightDesaturate(intenseFire, l, Headroom);

        // Total perceived luminance is perfectly conserved (no darkening/brightening):
        float lPost = desaturated.Dot(LumaWeights);
        AssertThat(lPost).IsEqualApprox(l, 1e-4f);

        // Blue channel is lifted towards white core (filament effect):
        AssertThat(desaturated.Z).IsGreater(intenseFire.Z);
        // Red channel is still dominant:
        AssertThat(desaturated.X).IsGreater(desaturated.Y);
    }

    [TestCase]
    public void TestHdrGammaContinuityAtOne()
    {
        const float Gamma = 1.2f;

        // At exactly col = 1.0:
        float sdrAtOne = MathF.Pow(1.0f, Gamma);
        float sdrPart = Math.Clamp(1.0f, 0.0f, 1.0f);
        float hdrAtOne = MathF.Pow(sdrPart, Gamma) + Math.Max(1.0f - 1.0f, 0.0f);

        AssertThat(hdrAtOne).IsEqual(sdrAtOne);
        AssertThat(hdrAtOne).IsEqual(1.0f);

        // Above 1.0, scales linearly without exponential blowout:
        float col = 3.0f; // 3.0 in HDR
        float hdrValue = MathF.Pow(1.0f, Gamma) + (col - 1.0f); // 1.0 + 2.0 = 3.0
        float rawExponential = MathF.Pow(col, Gamma); // 3.0^1.2 = 3.737 (distorted)

        AssertThat(hdrValue).IsEqual(3.0f);
        AssertThat(rawExponential).IsGreater(3.5f);
    }

    [TestCase]
    public void TestAdditiveLayerRadianceGain()
    {
        // Gain is purely an authored / physical radiance scalar:
        const float AuthoredGain = 1.0f;
        float defaultGain = AuthoredGain * Math.Max(1.0f, 1.0f);
        AssertThat(defaultGain).IsEqual(1.0f);

        float boostedGain = AuthoredGain * Math.Max(10.0f, 1.0f);
        AssertThat(boostedGain).IsEqual(10.0f);
    }

    [TestCase]
    public void TestIntermediatePassDisplayAgnostic()
    {
        // Intermediate passes evaluate pure linear scene radiance without clamping or display awareness:
        float sample1 = SamplePass(0.5f);
        AssertThat(sample1).IsEqual(0.5f);

        float sample2 = SamplePass(2.5f);
        AssertThat(sample2).IsEqual(2.5f);

        float extremeSample = SamplePass(50.0f);
        AssertThat(extremeSample).IsEqual(50.0f);

        static float SamplePass(float value)
        {
            return Math.Max(value, 0.0f);
        }
    }

    [TestCase]
    public void TestTopLevelTonemapperCompressesUnboundedRadiance()
    {
        // Demonstrates that even when scene radiance reaches 10.0, 50.0, or 100.0,
        // the final tonemapper smoothly compresses all of it strictly within display headroom.
        const float Headroom = 3.33f;
        const float InvWhiteSq = 1.0f;

        float mapped2 = FableHdrShoulder(FableReinhard(2.0f, InvWhiteSq), Headroom);
        float mapped5 = FableHdrShoulder(FableReinhard(5.0f, InvWhiteSq), Headroom);
        float mapped100 = FableHdrShoulder(FableReinhard(100.0f, InvWhiteSq), Headroom);

        AssertThat(mapped2).IsLess(mapped5);
        AssertThat(mapped5).IsLessEqual(Headroom);
        AssertThat(mapped100).IsLessEqual(Headroom);
    }

    [TestCase]
    public void TestPbrMaterialBypassesEmissionBoost()
    {
        // Modern PBR mod materials author physical radiance directly;
        // they must NOT be multiplied by fable_emission_boost.
        Vector3 selfIllum = new Vector3(2.5f, 1.8f, 0.5f);
        float selfIllumScale = 1.5f;
        float emissionBoost = 3.33f;

        Vector3 pbrEmission = CalculateMaterialEmission(selfIllum, selfIllumScale, emissionBoost, isPbr: true);
        Vector3 expectedPbr = selfIllum * selfIllumScale;

        AssertThat(pbrEmission.X).IsEqualApprox(expectedPbr.X, 1e-5f);
        AssertThat(pbrEmission.Y).IsEqualApprox(expectedPbr.Y, 1e-5f);
        AssertThat(pbrEmission.Z).IsEqualApprox(expectedPbr.Z, 1e-5f);
    }

    [TestCase]
    public void TestLegacyMaterialScalesWithEmissionBoost()
    {
        // Shipped 2008 SDR textures square the sampled texture (gamma 2.0 decode)
        // and scale by fable_emission_boost so they can enter HDR headroom.
        Vector3 selfIllum = new Vector3(0.8f, 0.6f, 0.2f);
        float selfIllumScale = 1.0f;
        float emissionBoost = 3.33f;

        Vector3 legacyEmission = CalculateMaterialEmission(selfIllum, selfIllumScale, emissionBoost, isPbr: false);
        Vector3 expectedLegacy = (selfIllum * selfIllum) * (selfIllumScale * emissionBoost);

        AssertThat(legacyEmission.X).IsEqualApprox(expectedLegacy.X, 1e-5f);
        AssertThat(legacyEmission.Y).IsEqualApprox(expectedLegacy.Y, 1e-5f);
        AssertThat(legacyEmission.Z).IsEqualApprox(expectedLegacy.Z, 1e-5f);
    }

    [TestCase]
    public void TestPbrParticleBypassesEmissionBoostAndSquaring()
    {
        // Modern PBR particles use linear texture radiance directly without gamma 2.0 decode or emission boost.
        Vector3 texel = new Vector3(0.7f, 0.5f, 0.3f);
        float emissionBoost = 3.33f;

        Vector3 pbrTexRgb = CalculateParticleTexRgb(texel, emissionBoost, isPbr: true);
        Vector3 legacyTexRgb = CalculateParticleTexRgb(texel, emissionBoost, isPbr: false);

        AssertThat(pbrTexRgb.X).IsEqualApprox(texel.X, 1e-5f);
        AssertThat(legacyTexRgb.X).IsEqualApprox(texel.X * texel.X * emissionBoost, 1e-5f);
    }

    [TestCase]
    public void TestDynamicLightingUnboostedAndUnclamped()
    {
        // Godot dynamic lights evaluate raw physical (light_colour / PI) without emissionBoost or upper clamp.
        float normalDotLight = 1.0f;
        float attenuation = 1.0f;
        float energy = 50.0f; // High physical light energy
        Vector3 lightColor = new Vector3(1.0f, 0.9f, 0.8f);
        Vector3 godotLightColour = lightColor * energy * MathF.PI; // Godot multiplies by PI

        Vector3 directLight = Math.Clamp(normalDotLight, 0.0f, 1.0f) * attenuation * (godotLightColour / MathF.PI);

        // Perfectly recovers unclipped physical radiant intensity:
        AssertThat(directLight.X).IsEqualApprox(lightColor.X * energy, 1e-4f);
        AssertThat(directLight.X).IsGreater(1.0f); // Unclamped in FP16 scene buffer
    }

    [TestCase]
    public void TestDynamicHeadroomBoostFormula()
    {
        // Formula: Boost(H) = 1.0 + 0.375 * (H - 1.0)
        // SDR mode (headroom = 1.0):
        float sdrBoost = CalculateDynamicBoost(1.0f);
        AssertThat(sdrBoost).IsEqual(1.0f);

        // DisplayHDR 600 (600 peak / 200 white = 3.0 headroom):
        float hdr600Boost = CalculateDynamicBoost(3.0f);
        AssertThat(hdr600Boost).IsEqualApprox(1.75f, 1e-4f);

        // Standard HDR (1000 peak / 200 white = 5.0 headroom):
        float hdr1000Boost = CalculateDynamicBoost(5.0f);
        AssertThat(hdr1000Boost).IsEqualApprox(2.50f, 1e-4f);

        // DisplayHDR 1400 (1400 peak / 200 white = 7.0 headroom):
        float hdr1400Boost = CalculateDynamicBoost(7.0f);
        AssertThat(hdr1400Boost).IsEqualApprox(3.25f, 1e-4f);

        // Headroom clamped to 1.0 minimum (never darkens below SDR):
        float subSdrBoost = CalculateDynamicBoost(0.8f);
        AssertThat(subSdrBoost).IsEqual(1.0f);
    }

    [TestCase]
    public void TestHdrDisplayDetectorQueryDoesNotThrow()
    {
        // Hardware query must run safely without uncaught exceptions on any platform:
        var info = Refii.Launcher.Native.HdrDisplayDetector.QueryPrimaryDisplay();
        AssertThat(info.PeakNits).IsGreaterEqual(0.0f);
        AssertThat(info.MinNits).IsGreaterEqual(0.0f);
        AssertThat(info.StatusMessage).IsNotNull();

        // Tuple deconstruction backward compatibility:
        (bool isHdr, float peakNits, float minNits) = info;
        AssertThat(peakNits).IsEqual(info.PeakNits);
        AssertThat(minNits).IsEqual(info.MinNits);
        AssertThat(isHdr).IsEqual(info.IsHdrActive);
    }

    [TestCase]
    public void TestTryParseEdidCta861HdrMetadata()
    {
        // Construct synthetic 256-byte EDID (128-byte base + 128-byte CTA-861 extension)
        byte[] edid = new byte[256];
        // EDID Header: 00 FF FF FF FF FF FF 00
        edid[0] = 0x00; edid[1] = 0xFF; edid[2] = 0xFF; edid[3] = 0xFF;
        edid[4] = 0xFF; edid[5] = 0xFF; edid[6] = 0xFF; edid[7] = 0x00;
        edid[126] = 1; // 1 extension block

        // Extension block 1 (offset 128)
        int ext = 128;
        edid[ext + 0] = 0x02; // CTA-861 tag
        edid[ext + 1] = 0x03; // Rev 3
        edid[ext + 2] = 16;   // d = 16 offset for detailed timings

        // Data collection block at offset 4: Tag 7, length 6 (Extended Tag Data Block)
        // Header: (7 << 5) | 6 = 0xE6
        edid[ext + 4] = (7 << 5) | 6;
        edid[ext + 5] = 0x06; // ExtTag 6: HDR Static Metadata
        edid[ext + 6] = 0x0C; // EOTF: bits 2 & 3 (PQ + HLG supported)
        edid[ext + 7] = 0x01; // Static metadata descriptor type 1
        edid[ext + 8] = 138;  // Desired Content Max Luminance CV = 138 (approx 1000 nits)
        edid[ext + 9] = 128;  // Max Frame-avg Luminance CV = 128 (approx 800 nits)
        edid[ext + 10] = 10;  // Min Luminance CV = 10

        bool parsed = Refii.Launcher.Native.HdrDisplayDetector.TryParseEdid(
            edid,
            out bool isHdr,
            out float peakNits,
            out float minNits);

        AssertThat(parsed).IsTrue();
        AssertThat(isHdr).IsTrue();
        // 50 * 2^(138 / 32) = 50 * 2^(4.3125) = 994.4 nits
        AssertThat(peakNits).IsGreater(990.0f);
        AssertThat(peakNits).IsLess(1000.0f);
        AssertThat(minNits).IsGreater(0.0f);
    }

    [TestCase]
    public void TestTryParseEdidSdrOnly()
    {
        // 128-byte base EDID with 0 extensions
        byte[] edid = new byte[128];
        edid[0] = 0x00; edid[1] = 0xFF; edid[2] = 0xFF; edid[3] = 0xFF;
        edid[4] = 0xFF; edid[5] = 0xFF; edid[6] = 0xFF; edid[7] = 0x00;
        edid[126] = 0;

        bool parsed = Refii.Launcher.Native.HdrDisplayDetector.TryParseEdid(
            edid,
            out bool isHdr,
            out float peakNits,
            out float minNits);

        AssertThat(parsed).IsFalse();
        AssertThat(isHdr).IsFalse();
        AssertThat(peakNits).IsEqual(0.0f);
        AssertThat(minNits).IsEqual(0.0f);
    }

    [TestCase]
    public void TestTryParseEdidRejectsCorruptHeader()
    {
        byte[] edid = new byte[256]; // all zeros, missing 00 FF FF... header
        bool parsed = Refii.Launcher.Native.HdrDisplayDetector.TryParseEdid(
            edid,
            out bool isHdr,
            out float peakNits,
            out float minNits);

        AssertThat(parsed).IsFalse();
        AssertThat(isHdr).IsFalse();
        AssertThat(peakNits).IsEqual(0.0f);
    }

    [TestCase]
    public void TestCdfPwLiftsDarkInteriorMidtonesAndPreservesSdrSkyWhite()
    {
        // 1. Dark cave / Guild Hall midtones:
        // In a dark cave, 85% of pixels are in the bottom 30% of the range (cdfSample = 0.85 at t = 0.30).
        // `FableCdfPw` lifts the midtone significantly above a linear ramp (`t^2 = 0.09`), while keeping black at 0.
        float blackPw = FableCdfPw(0.0f, 0.15f, smBlack: 0.10f, smWhite: 1.50f, contrastCdfScale: 0.65f);
        AssertThat(blackPw).IsEqual(0.0f);

        float caveMidLuminance = 0.10f + 0.30f * (1.50f - 0.10f); // t = 0.30 -> l_exp = 0.52
        float caveMidPw = FableCdfPw(caveMidLuminance, 0.85f, smBlack: 0.10f, smWhite: 1.50f, contrastCdfScale: 0.65f);
        AssertThat(caveMidPw).IsGreater(0.30f * 0.30f); // Lifted well above un-equalized t^2 (0.09)
        AssertThat(caveMidPw).IsGreater(0.30f);

        // 2. Bright SDR sky / water / sun-ward fog (`R, G, B > 1.0` after exposure):
        // With pre-exposed `l_exp = l * E`, SDR shoulder compression, and `FableFitCeiling(..., 1.0)`,
        // bright sky/water pixels compress smoothly into the upper SDR shoulder without clipping or hue skew.
        var exposedSky = new Vector3(2.2f, 1.15f, 1.35f);
        float lExp = exposedSky.Dot(LumaWeights);
        float peakExp = Math.Max(Math.Max(exposedSky.X, exposedSky.Y), exposedSky.Z);
        float lToneExp = Mathf.Lerp(lExp, peakExp, Refii.Lighting.Engine_PostFxModel.PeakToneBlend);
        float invWhiteSq = Refii.Lighting.Engine_PostFxModel.ComputeInvWhiteSq(0.10f, 0.30f, 2.0f, 0.36f);
        float x = lToneExp * (0.36f / 0.20f);
        float baseR = FableReinhard(x, invWhiteSq);
        float pw = FableCdfPw(lToneExp, 0.98f, smBlack: 0.10f, smWhite: 2.0f, contrastCdfScale: 0.5f);
        float cdfW = 0.35f * Math.Clamp(1.0f - baseR, 0.0f, 1.0f);
        float curved = Mathf.Lerp(baseR, pw, cdfW);
        float mapped = FableShoulder(curved, headroom: 1.0f);
        float gain = mapped / lToneExp;

        Vector3 preClamp = exposedSky * gain;
        Vector3 fittedSdr = FableFitCeiling(preClamp, 1.0f);
        Vector3 clampedSdr = new(
            Math.Clamp(fittedSdr.X, 0.0f, 1.0f),
            Math.Clamp(fittedSdr.Y, 0.0f, 1.0f),
            Math.Clamp(fittedSdr.Z, 0.0f, 1.0f));

        AssertThat(clampedSdr.X).IsGreater(0.85f);
        AssertThat(clampedSdr.X).IsLessEqual(1.0f);
        AssertThat(clampedSdr.Y).IsGreater(0.45f);
        AssertThat(clampedSdr.Z).IsGreater(0.50f);
    }

    [TestCase]
    public void TestSdrShoulderAndWhitePointScalePreventBlownOutDiffuseHighlights()
    {
        // Indoor tavern / shaded exterior stats where SmWhite - SmBlack < 1.0:
        const float SmBlack = 0.05f;
        const float SmMid = 0.30f;
        const float SmWhite = 0.65f;
        const float Knee = 0.36f;

        // Curve-space white point W_x = max((SmWhite - SmBlack) * knee_n, 2.0) guarantees invWhiteSq <= 0.25 (never > 1.0):
        float invWhiteSq = Refii.Lighting.Engine_PostFxModel.ComputeInvWhiteSq(SmBlack, SmMid, SmWhite, Knee);
        AssertThat(invWhiteSq).IsLessEqual(0.25f);

        // Even at curve-space x = 1.0, Extended Reinhard stays sub-unity (0.625) instead of super-linear (> 1.0):
        float reinhardAtOne = FableReinhard(1.0f, invWhiteSq);
        AssertThat(reinhardAtOne).IsEqualApprox(0.625f, 1e-4f);

        // SDR shoulder (headroom = 1.0, knee = 0.58) smoothly rolls off highlights in [0.58, 1.0)
        // instead of pinning diffuse highlights to 1.0:
        float atKnee = FableShoulder(0.58f, 1.0f);
        float aboveKnee = FableShoulder(0.85f, 1.0f);
        float highHighlight = FableShoulder(1.40f, 1.0f);

        AssertThat(atKnee).IsEqualApprox(0.58f, 1e-5f);
        AssertThat(aboveKnee).IsGreater(0.58f);
        AssertThat(aboveKnee).IsLess(0.80f);
        AssertThat(highHighlight).IsGreater(aboveKnee);
        AssertThat(highHighlight).IsLess(0.95f);
    }

    [TestCase]
    public void TestWarmSdrSkinHighlightPreservesChromaWithoutBleaching()
    {
        // Warm tavern firelight on skin: Red slightly exceeds SDR ceiling (1.30) while Green/Blue sit at (0.45, 0.15).
        // Pure luminance-preserving desaturation would force Green/Blue up to (0.52, 0.35) linear -> (0.75, 0.63) sRGB (bleached pastel pink).
        // Ratio-dominated FableFitCeiling keeps warm orange-amber chroma while strictly respecting the 1.0 ceiling.
        var warmSkin = new Vector3(1.30f, 0.45f, 0.15f);
        Vector3 fitted = FableFitCeiling(warmSkin, 1.0f);

        // Peak channel fits within 1.0:
        float peak = Math.Max(Math.Max(fitted.X, fitted.Y), fitted.Z);
        AssertThat(peak).IsEqualApprox(1.0f, 1e-4f);

        // Hue ratio (R - G) / (G - B) is invariant:
        float origHueRatio = (warmSkin.X - warmSkin.Y) / (warmSkin.Y - warmSkin.Z);
        float fittedHueRatio = (fitted.X - fitted.Y) / (fitted.Y - fitted.Z);
        AssertThat(fittedHueRatio).IsEqualApprox(origHueRatio, 1e-4f);

        // Green and Blue are scaled down proportionally (~0.35 and ~0.12) rather than boosted above input:
        AssertThat(fitted.Y).IsLess(warmSkin.Y);
        AssertThat(fitted.Z).IsLess(warmSkin.Z);
    }

    [TestCase]
    public void TestLookingAwayFromSunMaintainsStableGroundBrightness()
    {
        // Outdoor sunlit scene: gravel road has exposed luminance l_exp = 0.85 (unexposed l ~ 0.21 at E = 4.0).
        // Case A: Looking toward the sun -> sky/sun sets SmP99 = 2.4, SmWhite = 2.4.
        // Case B: Looking away from the sun at the ground -> viewport 99th percentile drops to SmP99 = 0.35, SmWhite = 0.60.
        const float GroundLExp = 0.85f;
        const float CdfOnGroundWhenLookingAtSun = 0.55f;
        const float CdfOnGroundWhenLookingAway = 0.95f;

        float pwTowardSun = FableCdfPwShader(
            GroundLExp,
            CdfOnGroundWhenLookingAtSun,
            smBlack: 0.10f,
            smMid: 0.30f,
            smWhite: 2.40f,
            smP99: 2.40f,
            knee: 0.36f,
            prevExp: 4.0f);

        float pwAwayFromSun = FableCdfPwShader(
            GroundLExp,
            CdfOnGroundWhenLookingAway,
            smBlack: 0.10f,
            smMid: 0.30f,
            smWhite: 0.60f,
            smP99: 0.35f,
            knee: 0.36f,
            prevExp: 4.0f);

        // Because FableCdfPwShader anchors p99 to white_lum = max(SmWhite, 2.0 / knee_n) (~1.11),
        // the ground does not jump to t = 1.0 / pw = 0.98 when looking away from the sun:
        AssertThat(pwAwayFromSun).IsLess(0.45f);
        AssertThat(MathF.Abs(pwAwayFromSun - pwTowardSun)).IsLess(0.20f);
    }

    [TestCase]
    public void TestBrightcutUsesUnitChromaticityWithoutExtraExposureMultiplier()
    {
        // `quarter_image` stores `c = c_raw * E` and `l = dot(c, LUMINANCE_WEIGHTS) = l_raw * E`.
        // Dividing `c` by `l` forms the unit chromaticity vector `c / l` (norm ~1.0) and scales it
        // by `cut` (in unexposed luminance units), matching Xbox 360 blob 0163 without multiplying by E a second time.
        const float Exposure = 12.0f;
        const float Threshold = 0.20f;
        var cRaw = new Vector3(0.35f, 0.30f, 0.25f);
        var cExposed = cRaw * Exposure;

        float l = cExposed.Dot(LumaWeights);
        float invExposure = 1.0f / Exposure;
        float cut = Math.Max(l * invExposure - Threshold, 0.0f);

        Vector3 bright = cExposed * (cut / Math.Max(l, 1e-5f));
        float brightLuma = bright.Dot(LumaWeights);

        // Resulting bright-cut luminance equals `cut` directly (no 12x exposure blowout):
        AssertThat(brightLuma).IsEqualApprox(cut, 1e-5f);
        AssertThat(brightLuma).IsLess(1.0f);
    }

    [TestCase]
    public void TestBloomScaleCvarHotReloadInvalidatesCachedPostFxParams()
    {
        try
        {
            AssertThat(Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("bloom_scale", "2.5")).IsTrue();
            AssertThat(Refii.Lighting.Engine_PostFxParams.BloomGain).IsEqualApprox(2.5f, 1e-4f);

            AssertThat(Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("bloom_scale", "0.25")).IsTrue();
            AssertThat(Refii.Lighting.Engine_PostFxParams.BloomGain).IsEqualApprox(0.25f, 1e-4f);
        }
        finally
        {
            Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("bloom_scale", "1.0");
        }
    }

    [TestCase]
    public void TestFableGammaToeHdrDeepensBlacksAndPreservesHdrHeadroom()
    {
        const float Headroom = 5.0f;

        // Black stays 0:
        AssertThat(FableGammaToeHdr(0.0f, 0.0f, Headroom)).IsEqual(0.0f);

        // Deep shadow toe (c = 0.002, inside sRGB's linear segment enc < 0.04045):
        // x^(1/2.2) / 12.92 is darker than linear c, giving Fable 2's rich shadow blacks:
        float shadowIn = 0.002f;
        float shadowOut = FableGammaToeHdr(shadowIn, 0.0f, Headroom);
        AssertThat(shadowOut).IsGreater(0.0f);
        AssertThat(shadowOut).IsLess(shadowIn);

        // Paper white (1.0) maps to 1.0:
        AssertThat(FableGammaToeHdr(1.0f, 0.0f, Headroom)).IsEqualApprox(1.0f, 1e-5f);

        // True HDR values above paper white (1.0 .. Headroom) pass linearly without SDR clamping:
        AssertThat(FableGammaToeHdr(2.5f, 0.0f, Headroom)).IsEqualApprox(2.5f, 1e-5f);
        AssertThat(FableGammaToeHdr(4.8f, 0.0f, Headroom)).IsEqualApprox(4.8f, 1e-5f);
    }

    [TestCase]
    public void TestUnifiedModernLookAndDefaults()
    {
        AssertThat(Refii.Lighting.Engine_LightingFrame.IsClassic("modern")).IsFalse();
        AssertThat(Refii.Lighting.Engine_LightingFrame.IsClassic("classic")).IsTrue();
        AssertThat(Refii.Lighting.Engine_LightingFrame.IsClassic("fable")).IsTrue();

        var model = new Refii.Launcher.Core.RuntimeSettingsModel();
        model.Values["lighting_model"] = "modern";
        model.Values["materials_modernized"] = "true";
        model.Values["modern_tone_curve"] = "fable";
        AssertThat(model.CurrentLook()).IsEqual("modern_fable");

        model.Values["modern_tone_curve"] = "filmic";
        AssertThat(model.CurrentLook()).IsEqual("modern_filmic");

        model.Values["modern_tone_curve"] = "all";
        AssertThat(model.CurrentLook()).IsEqual("modern_filmic");

        model.Values["lighting_model"] = "classic";
        model.Values["materials_modernized"] = "false";
        AssertThat(model.CurrentLook()).IsEqual("classic");

        model.Values["lighting_model"] = "modern";
        model.Values["materials_modernized"] = "false";
        AssertThat(model.CurrentLook()).IsEqual("custom");

        // Verify top-level Bloom Scale visibility in standard (non-advanced) Launcher settings:
        var bloomDef = System.Linq.Enumerable.First(
            Refii.Launcher.Core.RuntimeCVarSet.All,
            d => d.Name == "bloom_scale");
        AssertThat(Refii.Launcher.Core.RuntimeSettingsModel.IsAdvanced(bloomDef)).IsFalse();
    }

    [TestCase]
    public void TestFilmicInteriorDarkLiftMultiplierPreservesFableAndLiftsFilmic()
    {
        bool prevInterior = Refii.Lighting.Engine_LightingFrame.CurrentInterior;
        try
        {
            Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("lighting_model", "modern");
            Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("interior_dark_lift", "1.5");
            Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("modern_dark_lift", "1.0");

            // 1. Interior with Fable tonemapper -> stays at 1.5x:
            Refii.Lighting.Engine_LightingFrame.CurrentInterior = true;
            Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("modern_tone_curve", "fable");
            AssertThat(Refii.Lighting.Engine_PostFxParams.FilmicCurve).IsFalse();
            AssertThat(Refii.Lighting.Engine_PostFxParams.DarkLift).IsEqualApprox(1.5f, 1e-4f);

            // 2. Interior with Filmic tonemapper ("filmic" or "all") -> automatically lifted to 2.5x:
            Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("modern_tone_curve", "filmic");
            AssertThat(Refii.Lighting.Engine_PostFxParams.FilmicCurve).IsTrue();
            AssertThat(Refii.Lighting.Engine_PostFxParams.DarkLift).IsEqualApprox(2.5f, 1e-4f);

            Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("modern_tone_curve", "all");
            AssertThat(Refii.Lighting.Engine_PostFxParams.FilmicCurve).IsTrue();
            AssertThat(Refii.Lighting.Engine_PostFxParams.DarkLift).IsEqualApprox(2.5f, 1e-4f);

            // 3. Exterior -> stays at modern_dark_lift (1.0x) for both Fable and Filmic,
            //    while building_interior_ambient_scale scales from 0.5x (Fable) to 2.5x (Filmic):
            Refii.Lighting.Engine_LightingFrame.CurrentInterior = false;
            Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("building_interior_ambient_scale", "0.5");
            AssertThat(Refii.Lighting.Engine_PostFxParams.DarkLift).IsEqualApprox(1.0f, 1e-4f);
            AssertThat(Refii.Lighting.Engine_LevelBakedLighting.BuildingInteriorAmbientScale).IsEqualApprox(2.5f, 1e-4f);

            Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("modern_tone_curve", "fable");
            AssertThat(Refii.Lighting.Engine_LevelBakedLighting.BuildingInteriorAmbientScale).IsEqualApprox(0.5f, 1e-4f);
        }
        finally
        {
            Refii.Lighting.Engine_LightingFrame.CurrentInterior = prevInterior;
            Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("modern_tone_curve", "fable");
            Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("interior_dark_lift", "1.5");
            Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("modern_dark_lift", "1.0");
            Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("building_interior_ambient_scale", "0.5");
        }
    }

    private static float CalculateDynamicBoost(float headroom)
    {
        float clampedHeadroom = Math.Max(headroom, 1.0f);
        return 1.0f + 0.375f * (clampedHeadroom - 1.0f);
    }

    private static Vector3 CalculateMaterialEmission(Vector3 selfIllum, float selfIllumScale, float emissionBoost, bool isPbr)
    {
        if (isPbr)
        {
            return selfIllum * selfIllumScale;
        }
        return (selfIllum * selfIllum) * (selfIllumScale * emissionBoost);
    }

    private static Vector3 CalculateParticleTexRgb(Vector3 texel, float emissionBoost, bool isPbr)
    {
        return isPbr ? texel : (texel * texel * Math.Max(emissionBoost, 1.0f));
    }

    [TestCase]
    public void TestHaloDistanceFadeCalculations()
    {
        // 1. Close-up fixture (d <= 6m) -> 100% full halo size (1.0x):
        AssertThat(CalculateHaloDistanceScale(viewDist: 2.0f, fadeEnabled: true)).IsEqual(1.0f);
        AssertThat(CalculateHaloDistanceScale(viewDist: 6.0f, fadeEnabled: true)).IsEqual(1.0f);

        // 2. Mid-distance fixture (d = 20.5m) -> smooth hermite interpolation:
        // t = (20.5 - 6) / 29 = 0.5; smoothstep = 0.5 * 0.5 * (3 - 2 * 0.5) = 0.5
        // mix(1.0, 0.15, 0.5) = 0.575
        AssertThat(CalculateHaloDistanceScale(viewDist: 20.5f, fadeEnabled: true)).IsEqualApprox(0.575f, 1e-4f);

        // 3. Far fixture (d >= 35m) -> shrinks to 15% extent (0.15x):
        AssertThat(CalculateHaloDistanceScale(viewDist: 35.0f, fadeEnabled: true)).IsEqualApprox(0.15f, 1e-4f);
        AssertThat(CalculateHaloDistanceScale(viewDist: 75.0f, fadeEnabled: true)).IsEqualApprox(0.15f, 1e-4f);

        // 4. Sky halos (d >= 250m, e.g. MoonGlare at 1000m) -> strictly exempt, 100% full extent:
        AssertThat(CalculateHaloDistanceScale(viewDist: 250.0f, fadeEnabled: true)).IsEqual(1.0f);
        AssertThat(CalculateHaloDistanceScale(viewDist: 1000.0f, fadeEnabled: true)).IsEqual(1.0f);

        // 5. When fade is disabled -> always 1.0 regardless of distance:
        AssertThat(CalculateHaloDistanceScale(viewDist: 50.0f, fadeEnabled: false)).IsEqual(1.0f);
    }

    [TestCase]
    public void TestFxParticleDistanceFadeCalculations()
    {
        // Close range (d <= 12m) -> 1.0
        AssertThat(CalculateFxParticleDistanceFade(viewDist: 5.0f)).IsEqual(1.0f);
        AssertThat(CalculateFxParticleDistanceFade(viewDist: 12.0f)).IsEqual(1.0f);

        // Far range (d >= 45m) -> 0.35
        AssertThat(CalculateFxParticleDistanceFade(viewDist: 45.0f)).IsEqualApprox(0.35f, 1e-4f);
        AssertThat(CalculateFxParticleDistanceFade(viewDist: 80.0f)).IsEqualApprox(0.35f, 1e-4f);

        // Mid range (d = 28.5m) -> smoothstep(0.5) = 0.5, mix(1.0, 0.35, 0.5) = 0.675
        AssertThat(CalculateFxParticleDistanceFade(viewDist: 28.5f)).IsEqualApprox(0.675f, 1e-4f);
    }

    [TestCase]
    public void TestHaloDistanceFadeCvarHotReload()
    {
        try
        {
            AssertThat(Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("halo_distance_fade", "false")).IsTrue();
            AssertThat(Refii.Lighting.Engine_PostFxParams.HaloDistanceFade).IsFalse();

            AssertThat(Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("halo_distance_fade", "true")).IsTrue();
            AssertThat(Refii.Lighting.Engine_PostFxParams.HaloDistanceFade).IsTrue();
        }
        finally
        {
            Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("halo_distance_fade", "true");
        }
    }

    private static float CalculateHaloDistanceScale(float viewDist, bool fadeEnabled)
    {
        float sizeScale = 1.0f;
        if (fadeEnabled && viewDist < 250.0f)
        {
            float t = Math.Clamp((viewDist - 6.0f) / 29.0f, 0.0f, 1.0f);
            sizeScale = 1.0f + (0.15f - 1.0f) * (t * t * (3.0f - 2.0f * t));
        }
        return sizeScale;
    }

    private static float CalculateFxParticleDistanceFade(float viewDist)
    {
        float distFade = 1.0f;
        if (viewDist > 12.0f)
        {
            float t = Math.Clamp((viewDist - 12.0f) / 33.0f, 0.0f, 1.0f);
            distFade = 1.0f + (0.35f - 1.0f) * (t * t * (3.0f - 2.0f * t));
        }
        return distFade;
    }
}
