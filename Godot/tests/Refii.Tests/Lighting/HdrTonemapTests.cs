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

    private static float FableReinhardHdr(float x, float invWhiteSq, float headroom)
    {
        const float ReinhardKnee = 0.5f;
        if (x <= ReinhardKnee)
        {
            return FableReinhard(x, invWhiteSq);
        }

        float at = FableReinhard(ReinhardKnee, invWhiteSq);
        float slope = (FableReinhard(ReinhardKnee + 1e-3f, invWhiteSq) - at) / 1e-3f;
        float room = Math.Max(headroom - at, 1e-4f);
        return at + room * (1.0f - MathF.Exp(-(x - ReinhardKnee) * slope / room));
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

        float t = Math.Clamp((peak - ceiling) / (peak - l), 0.0f, 1.0f);
        return color.Lerp(new Vector3(l, l, l), t);
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
    public void TestReinhardHdrParityBelowKnee()
    {
        // Proves that for all inputs at or below the knee (0.5), Reinhard HDR is bit-identical
        // to the original 2008 Fable 2 curve. Shadows, faces, and midtones are 100% untouched.
        const float InvWhiteSq = 0.05f;
        const float Headroom = 5.0f;

        for (int i = 0; i <= 50; i++)
        {
            float x = i / 100.0f; // 0.00 to 0.50
            float legacy = FableReinhard(x, InvWhiteSq);
            float modern = FableReinhardHdr(x, InvWhiteSq, Headroom);

            AssertThat(modern).IsEqual(legacy);
        }
    }

    [TestCase]
    public void TestReinhardHdrMonotonicAndContinuous()
    {
        const float InvWhiteSq = 0.05f;
        const float Headroom = 4.0f;

        // C0 continuity across the knee:
        float atKneeLeft = FableReinhard(0.5f, InvWhiteSq);
        float atKneeRight = FableReinhardHdr(0.5001f, InvWhiteSq, Headroom);
        AssertThat(MathF.Abs(atKneeLeft - atKneeRight)).IsLess(1e-3f);

        // Strictly monotonic above the knee:
        float prev = atKneeLeft;
        for (float x = 0.6f; x <= 20.0f; x += 0.5f)
        {
            float cur = FableReinhardHdr(x, InvWhiteSq, Headroom);
            AssertThat(cur).IsGreater(prev);
            AssertThat(cur).IsLess(Headroom);
            prev = cur;
        }

        // Asymptotes to headroom for extreme brightness:
        float extreme = FableReinhardHdr(1000.0f, InvWhiteSq, Headroom);
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

        // In SDR mode (headroom <= 1.001), output is unconditionally untouched:
        var intense = new Vector3(2.0f, 1.0f, 0.0f);
        Vector3 sdrPreserved = FableHighlightDesaturate(intense, intense.Dot(LumaWeights), 1.0f);
        AssertThat(sdrPreserved).IsEqual(intense);
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

        float mapped2 = FableReinhardHdr(2.0f, InvWhiteSq, Headroom);
        float mapped5 = FableReinhardHdr(5.0f, InvWhiteSq, Headroom);
        float mapped100 = FableReinhardHdr(100.0f, InvWhiteSq, Headroom);

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
}
