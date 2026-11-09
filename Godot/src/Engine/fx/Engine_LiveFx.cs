using System.Collections.Generic;
using Godot;

namespace Refii.Fx;

/// <summary>
/// Whether the effects the GUEST plays reach the screen, and with it who owns a level's standing
/// effects: the guest's statements or the host's spawn from the level data
/// (<see cref="Refii.Level.Engine_LevelFx"/>). One answer for the live-effect feed and the level
/// build, which runs before the feed's node exists.
/// </summary>
public static class Engine_LiveFx
{
    /// <summary><c>refii.toml</c> key for drawing the effects the guest plays; on by default.</summary>
    public const string DrawSettingKey = "fx_live_effects";

    /// <summary>
    /// Overrides the setting for a headless run, where there may be no <c>refii.toml</c> at all.
    /// <c>1</c> forces drawing on, <c>0</c> forces it off; unset defers to the setting.
    /// </summary>
    public const string DrawEnvironmentVariable = "REFII_FX_LIVE";

    /// <summary>The live-effect feed's own setting, the environment first.</summary>
    public static bool Draws(IReadOnlyDictionary<string, string> config)
    {
        string forced = OS.GetEnvironment(DrawEnvironmentVariable);
        return forced == "1" || forced == "0"
            ? forced == "1"
            : Refii.Core.Engine_RefiiConfig.Bool(config, DrawSettingKey, true);
    }

    /// <summary>
    /// The level's standing effects are the guest's to state, so the level build spawns none: a
    /// guest is running, its title states every effect its level places
    /// (<see cref="Refii.Core.Engine_TitleGame.GuestStatesLevelEffects"/>), and the guest's effects
    /// are drawn — by the title's own particle path under classic, by the live feed otherwise.
    /// The editor and the viewers, with no guest, keep the spawn.
    /// </summary>
    public static bool GuestOwnsLevelEffects(IReadOnlyDictionary<string, string> config) =>
        Refii.Core.Engine_TitleGame.Active.GuestStatesLevelEffects &&
        Refii.Host.HostBridge.TryGetAbiVersion() is not null &&
        (Refii.Models.Engine_ClassicMaterial.Active || Draws(config));
}
