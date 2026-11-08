using System;

namespace Refii.AndroidRuntime;

/// <summary>
/// `hud_half_rate`: the in-game HUD's viewport drawn every other frame while nothing on it
/// follows the world or the camera. Off by default.
/// </summary>
/// <remarks>
/// WHY. The UI viewport is a full Mobile 3D pass of its own (1560x738 on the S10e), measured at
/// 2.0-2.3 ms of GPU a frame with the HUD up. Its picture is kept between draws, so a skipped
/// frame shows the last one.
/// <para>WHEN IT SKIPS. Only the bare HUD, and never two frames running: a menu, a pause screen,
/// a world icon or sprite on screen (they are placed from the world each frame and would wobble
/// against it), a mod's HUD piece (a minimap turns with the camera) or the camera turning
/// draws every frame. What can still step is the guest's own HUD animation (a bar filling, a
/// slide), at half the frame rate.</para>
/// Live: a `cvar_set` of the key applies on the next frame.
/// </remarks>
public static class AndroidHudRate
{
    public const string Key = "hud_half_rate";

    /// <summary>The camera turning more than about a quarter of a degree since the last draw.</summary>
    public const float TurnCosine = 0.99999f;

    private static bool? _enabled;
    private static bool _hooked;

    /// <summary>Whether the key is on, read once and again on each live change.</summary>
    public static bool Enabled
    {
        get
        {
            if (_enabled is { } known) return known;
            bool on = false;
            try
            {
                on = Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), Key, false);
            }
            catch (Exception)
            {
                on = false;
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

    /// <summary>
    /// Whether this frame may skip the HUD's draw. Pure: only the bare HUD, nothing placed from
    /// the world, no mod piece, the camera not turning since the last draw, and the last frame drawn.
    /// </summary>
    public static bool Skip(bool bareHud, int worldMarks, bool modPieces, float turnCosine, bool skippedLast) =>
        bareHud && worldMarks == 0 && !modPieces && turnCosine >= TurnCosine && !skippedLast;
}
