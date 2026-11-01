using System;
using System.Text.Json;
using Godot;

namespace Refii.Level;

/// <summary>
/// The sky's GPU shortcuts, each its own refii.toml key and debug lever, each read at first use.
/// </summary>
/// <remarks>
/// `sky_glare_cheap`: the sun and moon glares (celestial_overlay) take their occlusion in the
/// vertex stage. It is a 5x5 grid of depth taps around the element's disc whose inputs are
/// uniforms and the view, so every pixel of the quad computed the same answer: 25 taps, each with
/// a projection and a divide, per pixel of a quad that can cover most of the screen. Once per
/// corner (a flat varying) it is 100 taps a frame, and the same picture.
///
/// Live: the debug channel's `render` op (`skyGlareCheap`); the glares change build on the next
/// frame (one pipeline compile the first time).
///
/// `sky_radiance_lean`: the sky's radiance map at Godot's smallest size while nothing in the frame
/// samples it, and the dome's sun written on the atmosphere tick only. Godot re-renders and
/// re-filters that map whenever the sky material's uniforms are set (sky.cpp: any set marks the
/// uniform set updated, whatever the value), and the sun was set every frame. Under the fable
/// lighting model the ambient is off and reflections are off, and Mobile has no SDFGI, so nothing
/// reads the map (<see cref="RadianceUnread"/>); it cannot be turned off while the background is
/// the sky, so it is made 32 texels, Godot's floor. The dome's sun then moves at the atmosphere's
/// 15 Hz, a step of well under a hundredth of a degree. Live (`skyRadianceLean`): the size is
/// checked every frame and goes back to what it was when the map is read again or the key is off.
/// </remarks>
public static class Engine_SkyCosts
{
    public const string GlareCheapKey = "sky_glare_cheap";

    public const string GlareCheapRequestKey = "skyGlareCheap";

    /// <summary>The define celestial_overlay takes its occlusion per vertex under.</summary>
    public const string VertexOcclusionDefine = "CELESTIAL_VERTEX_OCCLUSION";

    private static bool? _glareCheap;

    /// <summary>Main thread. The setting at first use; the debug channel may change it.</summary>
    public static bool GlareCheap
    {
        get => _glareCheap ??= Configured(GlareCheapKey);
        set => _glareCheap = value;
    }

    public const string RadianceLeanKey = "sky_radiance_lean";

    public const string RadianceLeanRequestKey = "skyRadianceLean";

    private static bool? _radianceLean;

    /// <summary>Main thread. The setting at first use; the debug channel may change it.</summary>
    public static bool RadianceLean
    {
        get => _radianceLean ??= Configured(RadianceLeanKey);
        set => _radianceLean = value;
    }

    /// <summary>The size the lean map is held at: Godot's smallest.</summary>
    public const Sky.RadianceSizeEnum LeanRadianceSize = Sky.RadianceSizeEnum.Size32;

    /// <summary>
    /// Whether nothing Godot draws samples the sky's radiance under these environment settings:
    /// no sky reflection or ambient (directly, or through a sky background), no SDFGI (its probes
    /// take the sky's light from it) and no volumetric fog injecting ambient (Forward+ samples the
    /// map for it, by the ambient energy). Pure.
    /// </summary>
    public static bool RadianceUnread(
        Godot.Environment.ReflectionSource reflection, Godot.Environment.AmbientSource ambient,
        Godot.Environment.BGMode background, bool sdfgi, bool volumetricFog, float fogAmbient)
    {
        bool skyBackground = background == Godot.Environment.BGMode.Sky;
        bool reflects = reflection == Godot.Environment.ReflectionSource.Sky ||
            (reflection == Godot.Environment.ReflectionSource.Bg && skyBackground);
        bool lights = ambient == Godot.Environment.AmbientSource.Sky ||
            (ambient == Godot.Environment.AmbientSource.Bg && skyBackground);
        return !reflects && !lights && !sdfgi && !(volumetricFog && fogAmbient > 0f);
    }

    /// <summary>The radiance size a sky is held at when lean: the lean size while the map is
    /// unread and lean is asked for, else the size it had before. Pure.</summary>
    public static Sky.RadianceSizeEnum RadianceSizeFor(bool lean, bool unread, Sky.RadianceSizeEnum before) =>
        lean && unread ? LeanRadianceSize : before;

    private static Sky? _leanSky;
    private static Sky.RadianceSizeEnum _leanBefore;

    /// <summary>
    /// Main thread, once a frame after the lighting has configured <paramref name="environment"/>:
    /// holds its sky's radiance map lean while <see cref="RadianceLean"/> is on and nothing reads
    /// it, and gives it back its own size otherwise. Touches the sky only when the size changes.
    /// </summary>
    public static void ApplyRadiance(Godot.Environment environment)
    {
        if (_leanSky is null && !RadianceLean)
        {
            return;
        }

        Sky? sky = environment.Sky;
        if (_leanSky is not null && (!GodotObject.IsInstanceValid(_leanSky) || !ReferenceEquals(_leanSky, sky)))
        {
            if (GodotObject.IsInstanceValid(_leanSky))
            {
                _leanSky.RadianceSize = _leanBefore;
            }

            _leanSky = null;
        }

        if (sky is null)
        {
            return;
        }

        bool unread = RadianceUnread(
            environment.ReflectedLightSource, environment.AmbientLightSource, environment.BackgroundMode,
            environment.SdfgiEnabled, environment.VolumetricFogEnabled,
            environment.VolumetricFogAmbientInject * environment.AmbientLightEnergy);
        bool lean = RadianceLean && unread;
        if (lean && _leanSky is null)
        {
            _leanSky = sky;
            _leanBefore = sky.RadianceSize;
        }

        if (_leanSky is null)
        {
            return;
        }

        Sky.RadianceSizeEnum want = RadianceSizeFor(lean, unread, _leanBefore);
        if (sky.RadianceSize != want)
        {
            sky.RadianceSize = want;
            GD.Print($"[sky] {RadianceLeanKey}: radiance {(lean ? "held at 32" : "back to " + want)}");
        }

        if (!lean)
        {
            _leanSky = null;
        }
    }

    /// <summary>The lean map's sky and its own size, for the debug channel; null when none is held.</summary>
    public static string? LeanRadianceState =>
        _leanSky is not null && GodotObject.IsInstanceValid(_leanSky) ? $"{_leanSky.RadianceSize} (own {_leanBefore})" : null;

    private static bool Configured(string key)
    {
        try
        {
            bool on = Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), key, false);
            if (on)
            {
                GD.Print($"[sky] {key} on");
            }

            return on;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Validates an optional boolean lever before the debug endpoint mutates rendering state.</summary>
    /// <returns>The requested flag or null without changing process state.</returns>
    internal static bool? ReadRequest(JsonElement request, string key)
    {
        if (!request.TryGetProperty(key, out JsonElement value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException(key + " must be a boolean.");
        return value.GetBoolean();
    }

    private static Shader? _overlayFile;
    private static Shader? _overlayCheap;

    /// <summary>
    /// The glares' shader as the setting asks: the file, or its build with the occlusion per
    /// vertex. Null when the file does not load. Both are made once.
    /// </summary>
    public static Shader? Overlay()
    {
        if (_overlayFile is null || !GodotObject.IsInstanceValid(_overlayFile))
        {
            _overlayFile = GD.Load<Shader>(Engine_CelestialElements.OverlayShaderPath);
            _overlayCheap = null;
        }

        if (_overlayFile is null || !GlareCheap)
        {
            return _overlayFile;
        }

        if (_overlayCheap is null || !GodotObject.IsInstanceValid(_overlayCheap))
        {
            _overlayCheap = new Shader
            {
                Code = Refii.Fx.Engine_FxDepthReads.Compose(_overlayFile.Code, VertexOcclusionDefine),
            };
        }

        return _overlayCheap;
    }
}
