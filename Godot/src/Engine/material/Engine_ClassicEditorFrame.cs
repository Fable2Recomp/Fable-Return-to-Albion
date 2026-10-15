using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// The classic frame where no guest runs (the Godot editor): the 148-float frame and the light
/// list the native exports read from a running title, computed from the level's own themes at
/// a time of day the way the title computes them.
/// </summary>
/// <remarks>
/// Every term is the title's, verified in IDA:
/// THE THEME BLOCK is <see cref="Refii.Level.Engine_EnvironmentTheme.TryEvaluateClassic"/>.
/// THE MAIN LIGHT (`sub_821DF670`): s = tod x T58; the elevation eases between the four keys
/// T53..T56, elev = (a[i] + a[i+1]) / 2 + cos((s - i / 4) x 4pi) x (a[i] - a[i+1]) / 2 with
/// i = (int)(4s); its azimuth is the x axis through RotZ(2pi s) . RotY(T59) . RotZ(T57) with z
/// dropped; the light travels along -(cos e h.x, cos e h.y, sin e). Its colour is T0, shadowed
/// when T1.x > 0.5; the rig's second directional light carries T2 (AMBIENT_COLOUR) on the same
/// direction.
/// THE SKY'S SUN is the x axis through RotZ(2pi s) . RotY(T59) . RotZ(T61 + T57), plus T60 in z,
/// normalised (row vectors, RotZ = [c s 0; -s c 0; 0 0 1], RotY = [c 0 -s; 0 1 0; s 0 c]).
/// THE ATMOSPHERE BLOCK (`sub_82263570`): Rayleigh = 0.0005800841^2 x 248.0502341502772 x
/// (0.007337032921, 0.009459203185, 0.025727610717) x T8 x 500; Mie = (0.0056148912,
/// 0.0063754139, 0.010514311) x T9; the sky's sun; (T14, 1900, T20 > 0 ? 1 / T20 : 1, 0.0005);
/// (T19.xyz, 0).
/// The lightmap factor is (T3.xyz, 0) and the darkening colour (T4.xyz, 0) (`sub_821D1CA0`).
/// Not stated by the themes and set to the live title's resting values: intensity 1, global
/// mist strength 1, no wind.
/// </remarks>
public static class Engine_ClassicEditorFrame
{
    private const int FrameFloats = 148;
    private const int LightFloats = 20;
    private static readonly float[] _theme = new float[Refii.Level.Engine_ClassicThemeParams.Count * 4];
    private static float _lastTime = float.NaN;
    private static IReadOnlyList<byte[]>? _lastGdbs;

    /// <summary>The theme block the last <see cref="Build"/> evaluated, 194 float4s.</summary>
    public static ReadOnlySpan<float> Theme => _theme;

    /// <summary>Whether a <see cref="Build"/> has evaluated a theme block.</summary>
    public static bool HasTheme { get; private set; }

    /// <summary>The sky's sun of the last <see cref="Build"/> (guest axes).</summary>
    public static ReadOnlySpan<float> SkySun => _skySun;
    private static readonly float[] _skySun = new float[3];

    /// <summary>Feeds the classic path from the level's environment node at its time of day.</summary>
    public static void Update(Node level)
    {
        Engine_ClassicPlacedLights.Level = level;
        if (FindSky(level) is not { } sky || sky.LightingGdbs.Count == 0)
        {
            return;
        }
        float dayFraction = Mathf.PosMod(sky.TimeOfDay / 24f, 1f);
        if (dayFraction == _lastTime && ReferenceEquals(_lastGdbs, sky.LightingGdbs))
        {
            return;
        }
        var frame = new float[FrameFloats];
        var lights = new float[LightFloats];
        if (Build(sky.LightingGdbs, dayFraction, frame, lights))
        {
            Engine_ClassicFeed.Replay = frame;
            Engine_ClassicLights.Replay = lights;
            Engine_ClassicMist.Replay = Array.Empty<float>();
            _lastTime = dayFraction;
            _lastGdbs = sky.LightingGdbs;
        }
    }

    private static Refii.Level.Engine_SkyEnvironmentNode? FindSky(Node level)
    {
        foreach (Node node in level.FindChildren("*", nameof(WorldEnvironment), true, false))
        {
            if (node is Refii.Level.Engine_SkyEnvironmentNode sky)
            {
                return sky;
            }
        }
        return null;
    }

    /// <summary>The frame and the light list at <paramref name="dayFraction"/> (0..1).</summary>
    public static bool Build(IReadOnlyList<byte[]> gdbs, float dayFraction, float[] frame, float[] lights)
    {
        if (!Refii.Level.Engine_EnvironmentTheme.TryEvaluateClassic(gdbs, dayFraction, _theme))
        {
            return false;
        }
        float T(int index, int lane = 0) => _theme[index * 4 + lane];

        // The main light and the sky's sun.
        float s = dayFraction * T(58);
        int i = Math.Clamp((int)(s * 4f), 0, 3);
        float a0 = T(53 + i), a1 = T(53 + (i + 1) % 4);
        float elevation = (0.5f * (a0 + a1) + 0.5f * MathF.Cos((s - 0.25f * i) * 12.566371f) * (a0 - a1)) * 0.017453292f;
        float theta = s * 6.2831855f;
        Vector3 Axis(float yDegrees, float zDegrees)
        {
            float ct = MathF.Cos(theta), st = MathF.Sin(theta);
            float phi = yDegrees * 0.017453292f, psi = zDegrees * 0.017453292f;
            var v2 = new Vector3(ct * MathF.Cos(phi), st, -ct * MathF.Sin(phi));
            return new Vector3(v2.X * MathF.Cos(psi) - v2.Y * MathF.Sin(psi), v2.X * MathF.Sin(psi) + v2.Y * MathF.Cos(psi), v2.Z);
        }
        Vector3 sun = Axis(T(59), T(61) + T(57)) + new Vector3(0f, 0f, T(60));
        sun = sun.Length() < 1e-4f ? Vector3.Zero : sun.Normalized();
        _skySun[0] = sun.X;
        _skySun[1] = sun.Y;
        _skySun[2] = sun.Z;
        HasTheme = true;
        Vector3 rig = Axis(T(59), T(57));
        var h = new Vector3(rig.X, rig.Y, 0f);
        h = h.Length() < 1e-4f ? Vector3.Zero : h.Normalized();
        Vector3 travel = -new Vector3(MathF.Cos(elevation) * h.X, MathF.Cos(elevation) * h.Y, MathF.Sin(elevation));

        Array.Clear(frame);
        void Put(int at, float x, float y, float z, float w)
        {
            frame[at] = x; frame[at + 1] = y; frame[at + 2] = z; frame[at + 3] = w;
        }
        Put(0, T(2), T(2, 1), T(2, 2), T(2, 3));
        Put(4, travel.X, travel.Y, travel.Z, 0f);
        Put(8, T(3), T(3, 1), T(3, 2), 0f);
        Put(12, T(4), T(4, 1), T(4, 2), 0f);

        double rayleigh = Math.Pow(0.0005800841, 2.0) * 248.0502341502772;
        float scale = T(8) * 500f;
        Put(16, (float)(rayleigh * 0.007337032921211229) * scale, (float)(rayleigh * 0.009459203185216139) * scale,
            (float)(rayleigh * 0.02572761071664139) * scale, 0f);
        Put(20, 0.0056148912f * T(9), 0.0063754139f * T(9), 0.010514311f * T(9), 0f);
        Put(24, sun.X, sun.Y, sun.Z, 0f);
        Put(28, T(14), 1900f, T(20) > 0f ? 1f / T(20) : 1f, 0.0005f);
        Put(32, T(19), T(19, 1), T(19, 2), 0f);

        Array.Copy(_theme, 0, frame, 36, 96);
        Put(132, T(0), T(0, 1), T(0, 2), T(0, 3));
        Put(136, travel.X, travel.Y, travel.Z, 0f);
        bool shadowed = T(1) > 0.5f;
        frame[140] = shadowed ? 1f : 0f;
        frame[141] = 1f;
        frame[142] = 1f;
        frame[143] = 1f;
        Put(144, 1f, 0f, 0f, 0f);

        // The light list: the main light, as the native export lays a directional light out.
        Array.Clear(lights);
        lights[3] = 1e10f;
        lights[4] = T(0); lights[5] = T(0, 1); lights[6] = T(0, 2); lights[7] = 1f;
        lights[8] = travel.X; lights[9] = travel.Y; lights[10] = travel.Z;
        lights[11] = 10f;
        lights[12] = 1e10f;
        lights[14] = 1f;
        lights[15] = 11f;
        return true;
    }
}
