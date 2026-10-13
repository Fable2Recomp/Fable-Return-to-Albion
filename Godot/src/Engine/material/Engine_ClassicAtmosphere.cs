using System;
using Godot;

namespace Refii.Models;

/// <summary>
/// The classic renderer's two atmosphere lookup textures, filled on the CPU the way the title
/// fills them every frame: g_Extinction (256x1, transmittance by distance) and g_InScattering
/// (64x1, sky colour by the cosine between the view ray and the sun).
/// </summary>
/// <remarks>
/// THE TITLE'S OWN ARITHMETIC (`sub_821FE040`, `sub_821F7258`, with the helper block built by
/// `sub_82263570`; quoted instruction by instruction on 2026-10-12), in the same single and
/// double precision and the same operation order, so the tables match the 360's to the texel.
/// Inputs are the theme block and the atmosphere block the frame feed already carries; the
/// textures are rebuilt only when those change, and updated in place so every material keeps
/// its binding.
///
/// Both tables store the sample for the texel's LEFT edge (i/N) and are read with linear
/// filtering at (i+0.5)/N, which is what the shipped picture includes; the shader's
/// coordinates are `(d - FOGGING_START) * 0.0005` and `saturate(dot(viewRay, sunDir))`.
/// </remarks>
public static class Engine_ClassicAtmosphere
{
    private const int ExtinctionWidth = 256;
    private const int InScatteringWidth = 64;

    private static readonly byte[] _extinction = new byte[ExtinctionWidth * 4];
    private static readonly byte[] _inScattering = new byte[InScatteringWidth * 8];
    private static readonly float[] _inputs = new float[32];
    private static readonly float[] _lastInputs = new float[32];
    private static Image? _extinctionImage, _inScatteringImage;
    private static ImageTexture? _extinctionTexture, _inScatteringTexture;
    private static bool _ever;
    private static long _rebuilds;

    /// <summary>The transmittance table, created on first use.</summary>
    public static ImageTexture ExtinctionTexture
    {
        get
        {
            EnsureCreated();
            return _extinctionTexture!;
        }
    }

    /// <summary>The in-scattering table, created on first use.</summary>
    public static ImageTexture InScatteringTexture
    {
        get
        {
            EnsureCreated();
            return _inScatteringTexture!;
        }
    }

    /// <summary>Times the tables were refilled, for the log.</summary>
    public static long Rebuilds => _rebuilds;

    /// <summary>
    /// Refills the tables when their inputs moved. <paramref name="frame"/> is the host bridge's
    /// classic frame (theme block at 36, atmosphere block at 16). Main thread.
    /// </summary>
    public static void Update(float[] frame)
    {
        // The inputs: betaR, betaM, the sun vector (atmosphere rows 0..2) and the theme's sky
        // and fog parameters (7..12, 14..20).
        int n = 0;
        for (int i = 16; i < 28; i++)
        {
            _inputs[n++] = frame[i];
        }
        foreach (int index in new[] { 7, 8, 9, 10, 11, 12, 14, 15, 16, 17, 18, 19, 20 })
        {
            int at = 36 + index * 4;
            _inputs[n++] = frame[at];
            if (index is 10 or 11 or 19)
            {
                _inputs[n++] = frame[at + 1];
                _inputs[n++] = frame[at + 2];
            }
        }
        if (_ever && _inputs.AsSpan().SequenceEqual(_lastInputs))
        {
            return;
        }
        _inputs.AsSpan().CopyTo(_lastInputs);
        _ever = true;
        ++_rebuilds;

        EnsureCreated();
        FillExtinction(frame);
        FillInScattering(frame);
        _extinctionImage!.SetData(ExtinctionWidth, 1, false, Image.Format.Rgba8, _extinction);
        _extinctionTexture!.Update(_extinctionImage);
        _inScatteringImage!.SetData(InScatteringWidth, 1, false, Image.Format.Rgbah, _inScattering);
        _inScatteringTexture!.Update(_inScatteringImage);
    }

    private static void EnsureCreated()
    {
        if (_extinctionTexture is not null)
        {
            return;
        }
        _extinctionImage = Image.CreateEmpty(ExtinctionWidth, 1, false, Image.Format.Rgba8);
        _extinctionTexture = ImageTexture.CreateFromImage(_extinctionImage);
        _inScatteringImage = Image.CreateEmpty(InScatteringWidth, 1, false, Image.Format.Rgbah);
        _inScatteringTexture = ImageTexture.CreateFromImage(_inScatteringImage);
    }

    private static float Theme(float[] frame, int index, int lane = 0) => frame[36 + index * 4 + lane];

    /// <summary>`sub_821FE040`: transmittance over distance from the fog's density profile.</summary>
    private static void FillExtinction(float[] frame)
    {
        float foggingStart = Theme(frame, 14);
        float nearSpan = MathF.Max(Theme(frame, 15) - foggingStart, 1.0f);
        float farSpan = MathF.Max(Theme(frame, 17) - foggingStart, nearSpan + 9.0f);
        float nD = MathF.Max(Theme(frame, 16), 0.0099999998f);
        float fD = MathF.Max(Theme(frame, 18), 0.0099999998f);
        float invFar = 1.0f / farSpan;
        float tauNear = nD * nearSpan;
        float tauFar = MathF.FusedMultiplyAdd(fD, farSpan, MathF.FusedMultiplyAdd(-fD, nearSpan, tauNear));
        float a = tauNear * invFar;
        float b = invFar * tauFar;
        float c = invFar * nearSpan;
        float gamma = (float)Math.Log10(a / b) / (float)Math.Log10(c);

        // betaR + betaM per channel, from the atmosphere block's first two rows.
        float bx = frame[20] + frame[16], by = frame[21] + frame[17], bz = frame[22] + frame[18];

        for (int i = 0; i < ExtinctionWidth; i++)
        {
            float d = i * 7.8125f;
            float p = (float)Math.Pow(d * invFar, gamma);
            float tau = (p * b) * farSpan;
            float ex = Math.Clamp((float)Math.Exp(-(bx * tau)), 0f, 1f);
            float ey = Math.Clamp((float)Math.Exp(-(by * tau)), 0f, 1f);
            float ez = Math.Clamp((float)Math.Exp(-(bz * tau)), 0f, 1f);
            int at = i * 4;
            _extinction[at] = (byte)(int)(ex * 255.0f);
            _extinction[at + 1] = (byte)(int)(ey * 255.0f);
            _extinction[at + 2] = (byte)(int)(ez * 255.0f);
            _extinction[at + 3] = 255;
        }
    }

    /// <summary>`sub_821F7258` with `sub_82263570`'s helper block: the sky by view-sun cosine.</summary>
    private static void FillInScattering(float[] frame)
    {
        // betaR, betaM and the sun vector from the atmosphere block; sky terms from the theme.
        var betaR = new Vector3(frame[16], frame[17], frame[18]);
        var betaM = new Vector3(frame[20], frame[21], frame[22]);
        var sun = new Vector3(frame[24], frame[25], frame[26]);
        float sunIntensity = Theme(frame, 7);
        var sky = new Vector3(Theme(frame, 10), Theme(frame, 10, 1), Theme(frame, 10, 2));
        var complementary = new Vector3(Theme(frame, 11), Theme(frame, 11, 1), Theme(frame, 11, 2));
        float bias = Theme(frame, 12) + 0.5f;

        // `sub_82295468`: the unsigned elevation of the sun vector above the horizontal, radians.
        float elevation = Elevation(sun);
        float cosA = MathF.Cos(elevation);
        // g, exactly as computed: f7 + 0.5*(f9 - f7) in double, from two fused terms.
        float f10 = elevation * 0.0318f;
        float f8 = -f10;
        float f9 = MathF.FusedMultiplyAdd(-elevation, 0.05984f, 0.444f);
        float f7 = f8 + 0.1f;
        float g = (float)((double)(f9 - f7) * 0.5 + (double)f7);
        float g2 = g * g;
        float oneMinusG2 = (float)(1.0 - (double)g2);
        float onePlusG2 = g2 + 1.0f;
        float twoG = g * 2.0f;

        Vector3 br = betaR * 0.05968310311436653f;
        Vector3 bm = betaM * 0.07957746833562851f;
        Vector3 sum = betaR + betaM;
        var inv = new Vector3(
            sum.X != 0f ? 1f / sum.X : 0f, sum.Y != 0f ? 1f / sum.Y : 0f, sum.Z != 0f ? 1f / sum.Z : 0f);
        Vector3 skyMinus = sky - complementary;

        for (int i = 0; i < InScatteringWidth; i++)
        {
            float t = i / 64.0f;
            float oneT2 = (float)((double)(t * t) + 1.0);
            float hg = oneMinusG2 / (float)Math.Pow(MathF.FusedMultiplyAdd(-twoG, t, onePlusG2), 1.5);
            float s = Math.Clamp(MathF.FusedMultiplyAdd(t, 0.5f, bias), 0f, 1f);
            float k = t * cosA;
            var tint = new Vector3(1f, MathF.FusedMultiplyAdd(-0.7f, k, 1f), MathF.FusedMultiplyAdd(-0.9f, k, 1f));
            var phase = new Vector3(
                MathF.FusedMultiplyAdd(br.X, oneT2, bm.X * hg) * inv.X,
                MathF.FusedMultiplyAdd(br.Y, oneT2, bm.Y * hg) * inv.Y,
                MathF.FusedMultiplyAdd(br.Z, oneT2, bm.Z * hg) * inv.Z);
            var skyAt = new Vector3(
                MathF.FusedMultiplyAdd(skyMinus.X, s, complementary.X),
                MathF.FusedMultiplyAdd(skyMinus.Y, s, complementary.Y),
                MathF.FusedMultiplyAdd(skyMinus.Z, s, complementary.Z));
            float rx = ((tint.X * sunIntensity) * phase.X) * skyAt.X;
            float ry = ((tint.Y * sunIntensity) * phase.Y) * skyAt.Y;
            float rz = ((tint.Z * sunIntensity) * phase.Z) * skyAt.Z;
            int at = i * 8;
            WriteHalf(_inScattering, at, rx);
            WriteHalf(_inScattering, at + 2, ry);
            WriteHalf(_inScattering, at + 4, rz);
            WriteHalf(_inScattering, at + 6, 0f);
        }
    }

    /// <summary>`sub_82295468`: angle between -dir and its horizontal projection, in [0, pi/2].</summary>
    private static float Elevation(Vector3 dir)
    {
        var a = -dir;
        var b = new Vector3(a.X, a.Y, 0f);
        if (MathF.Abs(dir.Z - 1f) < 1e-4f)
        {
            b = new Vector3(1f, 0f, 0f);
        }
        float lengths = MathF.Sqrt(a.Dot(a)) * MathF.Sqrt(b.Dot(b));
        if (lengths <= 1e-4f)
        {
            return 0f;
        }
        float cos = a.Dot(b) / lengths;
        if (MathF.Abs(cos - 1f) < 1e-4f)
        {
            return 0f;
        }
        if (MathF.Abs(cos + 1f) < 1e-4f)
        {
            return 3.1415927f;
        }
        return MathF.Acos(cos);
    }

    private static void WriteHalf(byte[] into, int at, float value)
    {
        ushort bits = BitConverter.HalfToUInt16Bits((Half)value);
        into[at] = (byte)(bits & 0xFF);
        into[at + 1] = (byte)(bits >> 8);
    }
}
