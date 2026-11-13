using System;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_offscreen_rig_walks`: a person or creature out of every view is walked by the entity
/// feed on one walk in this many (2-8); 1, the default, walks every one every walk. Off by default.
/// </summary>
/// <remarks>
/// WHY. Every character's statement changes on nearly every guest frame, so each one takes the
/// walk's full path (its prologue, its placement and its pose gate) on every walk, seen or not;
/// only its bone writes stop off screen. A deferred rig keeps its node where it was last drawn and
/// is walked again, as changed, on its next visit or the first walk its last place comes within
/// reach of a view (GameEntities.TryDeferOffscreen has the exclusions). Only while the sun casts
/// no shadow (sun_shadow_distance 0, as Low and Medium set it). LOSSY: a light or effect carried by
/// an off-screen body follows it at the reduced rate. Live: a `cvar_set` of the key applies at once.
/// </remarks>
public static class AndroidOffscreenRigs
{
    public const string Key = "android_offscreen_rig_walks";

    /// <summary>The longest gap: eight walks, about a quarter second on a phone at 30 fps.</summary>
    public const int MaxWalks = 8;

    private static bool _hooked;

    /// <summary>The walks a setting means: 1 (off) below 2 or for a non-number, at most <see cref="MaxWalks"/>. Pure.</summary>
    public static int Walks(double value) =>
        !double.IsFinite(value) || value < 2.0 ? 1 : (int)Math.Min(MaxWalks, Math.Floor(value));

    /// <summary>Sets the feed's rate from the config, and follows live changes from then on. Main thread.</summary>
    public static void Apply()
    {
        double value = 1.0;
        try
        {
            value = Refii.Core.Engine_RefiiConfig.Float(Refii.Core.Engine_RefiiConfig.Read(out _), Key, 1f);
        }
        catch (Exception)
        {
            value = 1.0;
        }

        Refii.Game.GameEntities.OffscreenRigWalks = Walks(value);
        if (!_hooked)
        {
            _hooked = true;
            Refii.Core.Engine_RefiiConfig.OnConfigChanged += (name, _) =>
            {
                if (string.Equals(name, Key, StringComparison.OrdinalIgnoreCase))
                {
                    Apply();
                }
            };
        }
    }
}
