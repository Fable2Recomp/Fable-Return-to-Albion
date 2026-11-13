using System;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_near_fog` (on by default): under the modern lighting model a phone keeps the
/// atmosphere's fog near the camera, which the surface shaders would otherwise take out for Godot's
/// volumetric fog to put back, when the renderer draws no volumetric fog.
/// </summary>
/// <remarks>
/// <para>WHAT WENT. Engine_LevelLighting publishes <c>fable_volumetric_fog_end</c>, the uniform
/// medium's density it hands Godot's Environment.VolumetricFog*, whenever the modern model runs with
/// <c>volumetric_fog</c> on (the default); fable_atmosphere's <c>fable_atmosphere_terms_for</c> then
/// divides <c>exp(-density x min(distance, 128 m))</c> out of every surface's fog transmittance, lite
/// builds included, so the two fogs do not count the first 128 m twice. Godot's Mobile renderer, every
/// phone's, draws no volumetric fog ("Only supporting normal fog here", scene_forward_mobile.glsl's
/// fog_process; render_forward_mobile.cpp has no volumetric pass), so on a phone the near fog was
/// simply gone: the first 128 m clearer than the fable model and than the desktop.</para>
/// <para>THE FIX. The density is still handed to the Environment and the light shafts; only the
/// published cancel is 0 when the renderer cannot draw it (anything but Forward+), so the surfaces
/// keep the analytic fog the whole way, as the fable model does. Read on each publish (the cached
/// key re-read on a config change), so it applies at the atmosphere's next update.</para>
/// </remarks>
public static class AndroidNearFog
{
    public const string Key = "android_near_fog";

    /// <summary>The density the surface shaders cancel: <paramref name="density"/>, or 0 on a phone whose renderer draws no volumetric fog.</summary>
    public static float Published(float density)
    {
        if (!Engine_Platform.IsAndroid || density <= 0f) return density;
        return Published(density, Keep, RendererDrawsVolumetricFog);
    }

    /// <summary><see cref="Published(float)"/> with the state given. Pure.</summary>
    public static float Published(float density, bool keepNearFog, bool rendererDrawsVolumetricFog) =>
        keepNearFog && !rendererDrawsVolumetricFog ? 0f : density;

    /// <summary>Whether a rendering method draws Godot's volumetric fog: Forward+ only. Pure.</summary>
    public static bool DrawsVolumetricFog(string renderingMethod) =>
        string.Equals(renderingMethod, "forward_plus", StringComparison.Ordinal);

    private static bool? _draws;

    private static bool RendererDrawsVolumetricFog =>
        _draws ??= DrawsVolumetricFog(RenderingServer.GetCurrentRenderingMethod());

    private static int _generation = int.MinValue;
    private static bool _keep = true;

    private static bool Keep
    {
        get
        {
            int generation = Engine_Switches.Generation;
            if (generation == _generation) return _keep;
            try
            {
                _keep = Engine_RefiiConfig.Bool(Engine_RefiiConfig.Read(out _), Key, true);
            }
            catch (Exception)
            {
                _keep = true;
            }

            _generation = Engine_Switches.Generation;
            return _keep;
        }
    }
}
