using System;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_pose_full_rate_m`: the radius inside which a character is posed on every walk
/// (GameEntities.PoseFullRateRadius, the game's 55 m); 0, the default, keeps 55. Off by default.
/// </summary>
/// <remarks>
/// WHY. A posed rig is one engine call per moved bone and, after the frame's processing, Godot's
/// own update of the whole skeleton (counted in "draw.deferred"). Beyond the radius a rig is
/// written only at the far rate: on walks that complete a guest tick (15 Hz), or at
/// `far_rig_pose_hz` when that is set. The closer the radius, the more of the street is on the
/// far rate. Changes how smoothly mid-distance people move. Live: a `cvar_set` of the key
/// applies at once.
/// </remarks>
public static class AndroidPoseRate
{
    public const string Key = "android_pose_full_rate_m";

    /// <summary>The game's own radius, which 0 keeps.</summary>
    public const float GameRadius = 55f;

    private static bool _hooked;

    /// <summary>The radius a setting means: the game's for 0, negatives and non-numbers. Pure.</summary>
    public static float Radius(float metres) => metres > 0f && float.IsFinite(metres) ? metres : GameRadius;

    /// <summary>Sets the radius from the config, and follows live changes from then on. Main thread.</summary>
    public static void Apply()
    {
        float metres = 0f;
        try
        {
            metres = Refii.Core.Engine_RefiiConfig.Float(Refii.Core.Engine_RefiiConfig.Read(out _), Key, 0f);
        }
        catch (Exception)
        {
            metres = 0f;
        }

        Refii.Game.GameEntities.PoseFullRateRadius = Radius(metres);
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
