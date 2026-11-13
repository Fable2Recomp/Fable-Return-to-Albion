using System;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_targeting_highlight`: whether the phone draws the guest's targeting highlight (the glow
/// around the person or object the hero faces) at all. On by default, the picture as it is.
/// </summary>
/// <remarks>
/// WHY. While anything is highlighted the phone's highlight (Engine_MobileHighlight) renders three
/// viewports of its own every frame — the half-size silhouette scene and two half-size bleeds —
/// and puts a full-view composite quad in the main view's transparent pass. GPU profiles in the
/// Bowerstone market by day put the three at 1.58 ms on the Galaxy S10e (0.50 + 0.63 + 0.45) and
/// 1.43 ms on the OnePlus 7 Pro (0.39 + 0.61 + 0.44), and the quad reads the scene depth, which on
/// the Mobile renderer ends the opaque pass and copies depth for it (Engine_FxDepthReads explains
/// the split). Hiding every entity turns all of it off too, which is part of what the shader
/// ceiling's entities `hide` saves; its `highlight` switch prices it alone.
/// Off: <see cref="Engine_MobileHighlight.Composites"/> is false, so the layer never activates, no
/// silhouette proxy is built and no pass runs; the outline is gone. Live: a `cvar_set` of the key
/// applies on the next frame.
/// </remarks>
public static class AndroidTargetingHighlight
{
    public const string Key = "android_targeting_highlight";

    private static bool? _enabled;
    private static bool _hooked;

    /// <summary>Whether the key is on, read once and again on each live change.</summary>
    public static bool Enabled
    {
        get
        {
            if (_enabled is { } known) return known;
            bool on = true;
            try
            {
                on = Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), Key, true);
            }
            catch (Exception)
            {
                on = true;
            }

            if (!_hooked)
            {
                _hooked = true;
                Refii.Core.Engine_RefiiConfig.OnConfigChanged += (name, _) =>
                {
                    if (string.Equals(name, Key, StringComparison.OrdinalIgnoreCase)) _enabled = null;
                };
            }

            _enabled = on;
            return on;
        }
    }

    /// <summary>Whether the phone composites the highlight: the setting on and the post chain not compositing it. Pure.</summary>
    public static bool Composites(bool enabled, bool postChainComposites) => enabled && !postChainComposites;

    /// <summary>
    /// `android_highlight_depth_test`: the composite tests each halo against the scene with the
    /// depth buffer's own test instead of reading Godot's depth copy (Engine_MobileHighlight).
    /// </summary>
    /// <remarks>
    /// WHY. The composite's `hint_depth_texture` puts a depth reader in the main view's render list
    /// whenever a target is lit, and on the Mobile renderer one reader ends the opaque pass, copies
    /// depth and reopens a transparent pass that loads colour and depth back
    /// (render_forward_mobile.cpp: "Render Opaque" and "Render Transparent" instead of
    /// "Render Opaque + Transparent"). On: one full-view quad per lit object, placed at the depth
    /// its halo is tested at, drawn with the depth test (Mobile's reversed-Z GREATER_OR_EQUAL:
    /// passes where the scene is at or beyond it, the composite's `tests[k] &lt;= scene`) and no
    /// depth texture; each quad blends its object's glow once, which is the composite's n blends.
    /// The same picture. Live.
    /// </remarks>
    public const string DepthTestKey = "android_highlight_depth_test";

    private static bool? _depthTest;
    private static bool _depthTestHooked;

    /// <summary>Whether <see cref="DepthTestKey"/> is on, read once and again on each live change.</summary>
    public static bool DepthTest
    {
        get
        {
            if (_depthTest is { } known) return known;
            bool on = false;
            try
            {
                on = Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), DepthTestKey, false);
            }
            catch (Exception)
            {
                on = false;
            }

            if (!_depthTestHooked)
            {
                _depthTestHooked = true;
                Refii.Core.Engine_RefiiConfig.OnConfigChanged += (name, _) =>
                {
                    if (string.Equals(name, DepthTestKey, StringComparison.OrdinalIgnoreCase)) _depthTest = null;
                };
            }

            _depthTest = on;
            return on;
        }
    }

    /// <summary>
    /// The view distance a halo's quad is placed at: the composite's test distance kept inside the
    /// camera's clip range, so a target the camera stands in (tested at 0, passing everywhere) sits
    /// just past the near plane, where it still passes everywhere, rather than being clipped. Pure.
    /// </summary>
    public static float QuadDistance(float test, float near, float far)
    {
        float lo = near * 1.001f;
        float hi = MathF.Max(lo, far * 0.999f);
        return Math.Clamp(test, lo, hi);
    }
}
