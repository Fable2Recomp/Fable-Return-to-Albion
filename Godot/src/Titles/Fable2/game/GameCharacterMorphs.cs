using Godot;
using System.Collections.Generic;
using Refii.Core;
using Refii.Hero;
using Refii.Host;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Game;

/// <summary>
/// Murgo's Mirror for both players, each frame (<see cref="Engine_CharacterMorphs"/>):
/// <list type="bullet">
/// <item>each settings slot's look (its settings, or the Mirror's preview) is kept in step with its
/// guest player's look in the player store (<see cref="Engine_MorphLookSync"/>) — the slot's player is
/// what the morph hook publishes from the store's owners (<c>refii.character_morphs.slot1/2</c>);</item>
/// <item>each guest player's stored look — this machine's or the other machine's — is published for
/// the morph hook as weights and judged inputs when it changes.</item>
/// </list>
/// So the Mirror, a mod's <c>set_appearance</c> and the other machine all reach the hook one way.
/// </summary>
public partial class GameCharacterMorphs : Node
{
    public const string SlotKeyPrefix = "refii.character_morphs.slot";

    private static readonly Engine_MorphLookSync[] Syncs = { new(), new() };
    private static readonly Dictionary<string, string>?[] Previews = new Dictionary<string, string>?[2];
    private static readonly Dictionary<string, float>?[] Published = new Dictionary<string, float>?[2];
    private static readonly bool[] PublishedOnce = new bool[2];
    private static readonly float[] Tans = { 0.5f, 0.5f };
    private static int _published;
    private static int _morphRegistry = -1;
    private static int _morphsPublished;

    public static int PreviewGeneration { get; private set; }

    /// <summary>
    /// The guest player whose body is to be rebuilt with <see cref="PreviewGeneration"/>: the one the
    /// Mirror is previewing, or whose published look last changed; 0 for none.
    /// </summary>
    public static int PreviewPlayer { get; private set; }

    /// <summary>The skin tan player <paramref name="player"/>'s look states for its composites; 0.5 the guest's own.</summary>
    public static float ManualTanOf(int player) => player is 1 or 2 ? Tans[player - 1] : 0.5f;

    /// <summary>The guest player settings slot <paramref name="slot"/> belongs to on this machine, 0 for none.</summary>
    public static int PlayerOfSlot(int slot) =>
        HostBridge.TryGetModValue(SlotKeyPrefix + slot.ToString(System.Globalization.CultureInfo.InvariantCulture), out float player) &&
        player is 1 or 2 ? (int)player : 0;

    /// <summary>The settings slot guest player <paramref name="player"/> edits on this machine, 0 when it is played elsewhere.</summary>
    public static int SlotOfPlayer(int player) =>
        player is not (1 or 2) ? 0 : PlayerOfSlot(1) == player ? 1 : PlayerOfSlot(2) == player ? 2 : 0;

    /// <summary>The Mirror's preview of slot <paramref name="slot"/>'s look (slot-1-keyed values), or null to end it.</summary>
    public static void Preview(int slot, IReadOnlyDictionary<string, string>? values)
    {
        if (slot is not (1 or 2)) return;
        Previews[slot - 1] = values is null ? null : new Dictionary<string, string>(values);
        if (PlayerOfSlot(slot) is var player and not 0) PreviewPlayer = player;
        ++PreviewGeneration;
        Step();
    }

    public override void _Process(double delta) => Step();

    private static void Step()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        // The mods' own morphs (Engine_ModMorphs), for the morph hook to answer: again after each mount.
        if (_morphRegistry != Engine_ModMorphs.Generation)
        {
            var morphs = Engine_ModMorphs.All;
            _morphRegistry = Engine_ModMorphs.Generation;
            foreach (string problem in Engine_ModMorphs.Problems) GD.PushWarning("[mods] morphs " + problem);
            Engine_ModMorphs.Publish(morphs, HostBridge.SetModSetting, ++_morphsPublished);
        }

        var stored = new Dictionary<string, float>?[2];
        for (int player = 1; player <= 2; player++)
            stored[player - 1] = Engine_CharacterMorphs.StoredLook(key => HostBridge.GetPlayerValue(player, key));

        for (int slot = 1; slot <= 2; slot++)
        {
            int player = PlayerOfSlot(slot);
            Dictionary<string, string>? preview = Previews[slot - 1];
            System.Func<string, string> read = preview is not null
                ? key => preview.TryGetValue(key, out string? value) ? value : string.Empty
                : Engine_CharacterMorphs.ForSlot(key => Engine_Switches.String(key, string.Empty), slot);
            var local = Engine_CharacterMorphs.StoreValues(read);
            switch (Syncs[slot - 1].Next(player, local, player != 0 ? stored[player - 1] : null, now, out var values))
            {
                case Engine_MorphLookSync.Step.Push:
                    HostBridge.PostModCommand(Engine_CharacterMorphs.StateCommand(player, values));
                    break;
                case Engine_MorphLookSync.Step.Persist:
                    Engine_UiScreenActions.WriteSettings(Engine_CharacterMorphs.SettingsOf(values, slot));
                    break;
            }
        }

        for (int player = 1; player <= 2; player++)
        {
            var look = stored[player - 1];
            if (PublishedOnce[player - 1] && Engine_CharacterMorphs.SameLook(look, Published[player - 1])) continue;
            PublishedOnce[player - 1] = true;
            Published[player - 1] = look;
            Tans[player - 1] = Engine_CharacterMorphs.LookTan(look);
            Engine_CharacterMorphs.Publish(look, HostBridge.SetModSetting, player, ++_published);
            PreviewPlayer = player;
            ++PreviewGeneration;
        }
    }
}
