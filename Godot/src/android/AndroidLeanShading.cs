using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `lite_fog` and `lite_lean_darkening`: the phone's lite surfaces work out the atmosphere's fog and
/// the ambient's normal-map darkening with less per-pixel work (fable_lite_lean.gdshaderinc beside
/// this file, included by fable_atmosphere.gdshaderinc). Composed into every lite build
/// (Engine_ShaderVariant), the model surfaces and the ground alike; read once per process.
/// </summary>
/// <remarks>
/// <para>THE FOG (`lite_fog`). full: as before. lean: the same terms and the same picture with one
/// inverse square root for the distance and the view direction, and the sun's length, the
/// in-scatter table's size and the two fog factors as the constants they are; the default.
/// table: lean, with the transmittance fetched
/// from a table this class builds whenever the atmosphere changes (<see cref="FogTable"/>), instead
/// of a pow and three exps per pixel; the guest's own arrangement (its g_Extinction), here in 32-bit
/// float at a quarter of its step. Nearly the same picture: within 0.2/255 of the evaluated curve on
/// the shipped outdoor themes. Under the modern lighting model only (<see cref="FogFor"/>): under the
/// fable model a process asking for it draws lean, as with the key unset.</para>
/// <para>THE DARKENING (`lite_lean_darkening`): the bent-normal term in view space, from the
/// normals the body already has, with the bent normal's length in closed form. The same picture; on by default.</para>
/// </remarks>
public static class AndroidLeanShading
{
    /// <summary>The fog's refii.toml setting.</summary>
    public const string FogKey = "lite_fog";

    /// <summary>The darkening's refii.toml setting.</summary>
    public const string DarkeningKey = "lite_lean_darkening";

    /// <summary>The define of `lite_fog = "lean"` (and of "table", which builds on it).</summary>
    public const string FogLeanDefine = "FABLE_LITE_FOG_LEAN";

    /// <summary>The define of `lite_fog = "table"`.</summary>
    public const string FogTableDefine = "FABLE_LITE_FOG_TABLE";

    /// <summary>The define of `lite_lean_darkening`.</summary>
    public const string DarkeningDefine = "FABLE_LITE_LEAN_DARKEN";

    /// <summary>The table's global (declared in project.godot), set once and then updated in place.</summary>
    public const string TableGlobal = "atmos_extinction";

    /// <summary>The fog curve's knot, the guest's LUT step (Engine_NearFogKnots.Spacing).</summary>
    public const float Knot = 7.8125f;

    /// <summary>Texels per knot: FABLE_FOG_TABLE_PER_KNOT in the include.</summary>
    public const int PerKnot = 4;

    /// <summary>The table's width: 256 knots (Engine_SkyAtmosphere.MaxFogDistance) at <see cref="PerKnot"/>, and the end texel. FABLE_FOG_TABLE_TEXELS.</summary>
    public const int TableTexels = (256 * PerKnot) + 1;

    /// <summary>How far the volumetric cancel reaches (fable_atmosphere's 128 m froxel range).</summary>
    public const float VolumetricRange = 128.0f;

    /// <summary>The values of <see cref="FogKey"/>.</summary>
    public enum FogPath
    {
        Full,
        Lean,
        Table,
    }

    /// <summary>The key's value; anything unknown is <see cref="FogPath.Full"/>. Pure.</summary>
    public static FogPath ParseFog(string value) => value.Trim().Trim('"').ToLowerInvariant() switch
    {
        "lean" => FogPath.Lean,
        "table" => FogPath.Table,
        _ => FogPath.Full,
    };

    /// <summary>The key's default, lean: the same picture.</summary>
    public const string FogDefault = "lean";

    /// <summary>The fog path the values ask for; unset is lean (the same picture). Pure.</summary>
    public static FogPath FogWanted(IReadOnlyDictionary<string, string> values) =>
        ParseFog(Engine_RefiiConfig.String(values, FogKey, FogDefault));

    /// <summary>
    /// The fog path a process under the given lighting model draws for <paramref name="wanted"/>: the
    /// table only under the modern model; under the fable model it is the key's default, so a build there
    /// composes exactly as with the key unset. Pure.
    /// </summary>
    /// <remarks>The table cost a Galaxy S10e 8% more GPU work under the fable model and saved 8% under the
    /// modern one (2026-11-12). Full and lean are the same sums under either.</remarks>
    public static FogPath FogFor(FogPath wanted, bool modernModel) =>
        wanted == FogPath.Table && !modernModel ? ParseFog(FogDefault) : wanted;

    /// <summary>Whether the values ask for the lean darkening; unset is on (the same picture). Pure.</summary>
    public static bool DarkeningWanted(IReadOnlyDictionary<string, string> values) =>
        Engine_RefiiConfig.Bool(values, DarkeningKey, true);

    /// <summary>The defines a lite build takes for the two settings, in order. Pure.</summary>
    public static string[] DefinesFor(FogPath fog, bool leanDarkening)
    {
        var defines = new List<string>(3);
        if (fog != FogPath.Full)
        {
            defines.Add(FogLeanDefine);
        }

        if (fog == FogPath.Table)
        {
            defines.Add(FogTableDefine);
        }

        if (leanDarkening)
        {
            defines.Add(DarkeningDefine);
        }

        return defines.ToArray();
    }

    private static readonly Lazy<(FogPath Fog, bool Darkening)> Chosen = new(() =>
    {
        if (!Engine_Platform.IsAndroid)
        {
            return (FogPath.Full, false);
        }

        try
        {
            IReadOnlyDictionary<string, string> values = Engine_RefiiConfig.Read(out _);
            return (FogFor(FogWanted(values), Refii.Lighting.Engine_LightingFrame.ModernModel), DarkeningWanted(values));
        }
        catch (Exception)
        {
            return (FogPath.Full, false);
        }
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>This process's fog path (read once, as the variant is, under this process's lighting model).</summary>
    public static FogPath FogOn => Chosen.Value.Fog;

    /// <summary>Whether this process's lite builds take the lean darkening (read once).</summary>
    public static bool DarkeningOn => Chosen.Value.Darkening;

    /// <summary>This process's defines, for Engine_ShaderVariant's process-wide list.</summary>
    public static string[] Defines() => DefinesFor(FogOn, DarkeningOn);

    /// <summary>The atmosphere's inputs to the table, as fable_atmosphere's globals carry them.</summary>
    public readonly record struct FogInputs(
        bool HasFog, Vector3 Beta, float DepthAtFar, float RecipFar, float Exponent, float MaxDistance, float Start,
        bool NearReady, Vector3 Knot0, Vector3 Knot1, Vector3 Knot2, Vector3 Knot3, Vector3 Knot4,
        float FogScale, float VolumetricDensity)
    {
        /// <summary>Knot <paramref name="index"/> (0..4); the shader's fable_atmosphere_near_knot.</summary>
        public Vector3 KnotAt(int index) => index switch
        {
            0 => Knot0, 1 => Knot1, 2 => Knot2, 3 => Knot3, _ => Knot4,
        };
    }

    /// <summary>
    /// The table: texel j holds the lite shader's transmittance before the volumetric cancel's clamp
    /// (and before the blend factor, which the shader keeps) at dl = j * Knot / PerKnot past
    /// FoggingStart, so at d = dl + Knot / 2: the near knots, interpolated as the shader does, while
    /// they are ready and dl is within four knots; the far curve otherwise; the fog scale; the
    /// volumetric cancel <c>exp(density * clamp(d + start, 0, 128))</c>. RGBA, alpha 1. The shader
    /// clamps the fetched value to 1, so the cancel's kink is not interpolated across. Pure.
    /// </summary>
    public static float[] FogTable(in FogInputs fog)
    {
        var rgba = new float[TableTexels * 4];
        const float step = Knot / PerKnot;
        bool rescale = MathF.Abs(fog.FogScale - 1.0f) > 1e-4f;
        for (int j = 0; j < TableTexels; j++)
        {
            float dl = j * step;
            Vector3 raw;
            if (dl < Knot * 4.0f && fog.NearReady)
            {
                int index = (int)MathF.Floor(dl / Knot);
                float below = index * Knot;
                raw = fog.KnotAt(index).Lerp(fog.KnotAt(index + 1), (dl - below) / Knot);
                if (rescale)
                {
                    raw = new Vector3(
                        MathF.Pow(MathF.Max(raw.X, 1e-6f), fog.FogScale),
                        MathF.Pow(MathF.Max(raw.Y, 1e-6f), fog.FogScale),
                        MathF.Pow(MathF.Max(raw.Z, 1e-6f), fog.FogScale));
                }
            }
            else
            {
                float t = MathF.Min(dl, fog.MaxDistance) * fog.RecipFar;
                float depth = fog.DepthAtFar * MathF.Pow(MathF.Max(t, 0.0f), fog.Exponent) * fog.FogScale;
                raw = new Vector3(MathF.Exp(-fog.Beta.X * depth), MathF.Exp(-fog.Beta.Y * depth), MathF.Exp(-fog.Beta.Z * depth));
            }

            if (fog.VolumetricDensity > 0.0f)
            {
                float radial = dl + (0.5f * Knot) + fog.Start;
                raw *= MathF.Exp(fog.VolumetricDensity * Math.Clamp(radial, 0.0f, VolumetricRange));
            }

            rgba[(j * 4) + 0] = raw.X;
            rgba[(j * 4) + 1] = raw.Y;
            rgba[(j * 4) + 2] = raw.Z;
            rgba[(j * 4) + 3] = 1.0f;
        }

        return rgba;
    }

    private static readonly object Gate = new();
    private static FogInputs _inputs = new(false, Vector3.Zero, 0f, 0f, 1f, 2000f, 0f, false,
        Vector3.One, Vector3.One, Vector3.One, Vector3.One, Vector3.One, 1.0f, 0.0f);
    private static FogInputs? _built;
    private static byte[]? _bytes;
    private static Image? _image;
    private static ImageTexture? _texture;

    /// <summary>
    /// The atmosphere's hook (Engine_SkyEnvironment.ApplyAtmosphereGlobals), with the values it
    /// publishes: the fog switch, beta, the curve, atmos_range, the near knots. Nothing unless the
    /// table is on.
    /// </summary>
    internal static void Atmosphere(bool hasFog, Vector3 beta, Vector3 curve, Vector4 range, NearFogSnapshot near)
    {
        if (FogOn != FogPath.Table)
        {
            return;
        }

        lock (Gate)
        {
            NearFogRgb k0 = near.At(0), k1 = near.At(1), k2 = near.At(2), k3 = near.At(3), k4 = near.At(4);
            _inputs = _inputs with
            {
                HasFog = hasFog, Beta = beta, DepthAtFar = curve.X, RecipFar = curve.Y, Exponent = curve.Z,
                MaxDistance = range.Y, Start = range.X, NearReady = near.Ready,
                Knot0 = new(k0.Red, k0.Green, k0.Blue), Knot1 = new(k1.Red, k1.Green, k1.Blue),
                Knot2 = new(k2.Red, k2.Green, k2.Blue), Knot3 = new(k3.Red, k3.Green, k3.Blue),
                Knot4 = new(k4.Red, k4.Green, k4.Blue),
            };
            Rebuild();
        }
    }

    /// <summary>
    /// The lighting's hook (Engine_LevelLighting), with what it publishes as fable_interior_fog and
    /// fable_volumetric_fog_end. Nothing unless the table is on.
    /// </summary>
    public static void LevelFog(float fogScale, float volumetricDensity)
    {
        if (FogOn != FogPath.Table)
        {
            return;
        }

        lock (Gate)
        {
            _inputs = _inputs with { FogScale = MathF.Max(fogScale, 0.0f), VolumetricDensity = volumetricDensity };
            Rebuild();
        }
    }

    // ONLY WHEN SOMETHING CHANGED, and in place: setting a sampler global queues every material that
    // reads it for a uniform-set rebuild (Engine_SkyEnvironment), so the texture is set once.
    private static void Rebuild()
    {
        if (!_inputs.HasFog || _built == _inputs)
        {
            return;
        }

        _built = _inputs;
        float[] rgba = FogTable(_inputs);
        _bytes ??= new byte[rgba.Length * sizeof(float)];
        Buffer.BlockCopy(rgba, 0, _bytes, 0, _bytes.Length);
        if (_texture is null)
        {
            _image = Image.CreateFromData(TableTexels, 1, false, Image.Format.Rgbaf, _bytes);
            _texture = ImageTexture.CreateFromImage(_image);
            RenderingServer.GlobalShaderParameterSet(TableGlobal, _texture);
        }
        else
        {
            _image = Image.CreateFromData(TableTexels, 1, false, Image.Format.Rgbaf, _bytes);
            _texture.Update(_image);
        }
    }
}
