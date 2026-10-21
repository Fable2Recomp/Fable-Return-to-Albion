namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Mods;
using Refii.Ui;

/// <summary>
/// G.17 / G.18 #8 and #10: mod abilities and spell slot picks on the ENGINE path — what the book
/// and the selectors post (Engine_UiModAbilities, Engine_UiSpellPicks), what the payload answers
/// (src/vfs/src/hooks/mod_abilities.cpp), the two events (ModAbility 91, SpellPick 92), the HUD's
/// each-player copies (Engine_HudModTwins), and the bundled mods built on them (no purchase code,
/// no settings as a command channel, no "…2" publishes). Pure .NET; no Godot object in the host.
/// </summary>
[TestSuite]
public class ModAbilityPickTests
{
    private static string? RepoRoot()
    {
        string? at = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && at != null; i++)
        {
            if (File.Exists(Path.Combine(at, "src", "vfs", "include", "core", "refii_mod_events.h")))
            {
                return at;
            }

            at = Path.GetDirectoryName(at);
        }

        return null;
    }

    private static Engine_UiModAbilities.Ability Weaving() => new(
        "SpellWeaving", "spell_weaving", "Hybrid", "N", "D", 5, "spell_weaving.ability", 8,
        CostLikeSetting: "spell_weaving.cost_like", Refunds: true, LegacySaveKey: "SpellWeaving",
        Stats: new float[] { 0, 1, 2, 3, 4, 5 }, Disciplines: new[] { "Will", "Skill" });

    [TestCase]
    public void TheBookDeclaresWhatTheEngineRuns()
    {
        Engine_UiModAbilities.Ability weaving = Weaving();
        AssertThat(string.Join(',', Engine_UiModAbilities.PoolsOf(weaving))).IsEqual("3,2");
        AssertThat(Engine_UiModAbilities.DeclarationLine(weaving)).IsEqual(
            "ability_declare SpellWeaving spell_weaving 5 8 spell_weaving.cost_like spell_weaving.ability 3,2 half SpellWeaving");

        var stamina = new Engine_UiModAbilities.Ability(
            "SoulsCombatStamina", "souls_combat", "Strength", "N", "D", 5, "souls_combat.stamina_ability", 3);
        AssertThat(Engine_UiModAbilities.DeclarationLine(stamina)).IsEqual(
            "ability_declare SoulsCombatStamina souls_combat 5 3 - souls_combat.stamina_ability 1 none -");

        var general = stamina with { Category = "Mystery" };
        AssertThat(Engine_UiModAbilities.DeclarationLine(general)).Contains(" - none -");
        AssertThat(Engine_UiModAbilities.PressLine("ability_buy", 2, weaving)).IsEqual("ability_buy 2 SpellWeaving");
    }

    [TestCase]
    public void ThePayloadAnswersEveryVerbTheBookAndSelectorsPost()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string engine = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_abilities.cpp"));
        foreach (string verb in new[] { "ability_clear", "ability_declare", "ability_buy", "ability_refund", "spell_pick" })
        {
            AssertThat(engine.Contains($"op == \"{verb}\"", StringComparison.Ordinal))
                .OverrideFailureMessage($"mod_abilities.cpp does not answer {verb}").IsTrue();
        }

        string commands = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("RunModAbilityCommand(ctx, base, w)", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("PumpModAbilities(work, base)", StringComparison.Ordinal)).IsTrue();
        AssertThat(File.ReadAllText(Path.Combine(root, "src", "vfs", "CMakeLists.txt")).Contains("src/hooks/mod_abilities.cpp")).IsTrue();

        // The derived values the page reads are the ones the payload states.
        foreach (string suffix in new[] { ".cost", ".refund", ".afford" })
        {
            AssertThat(engine.Contains($"(key + \"{suffix}\")", StringComparison.Ordinal))
                .OverrideFailureMessage($"mod_abilities.cpp states no <levelKey>{suffix}").IsTrue();
        }
    }

    [TestCase]
    public void TheTwoEventsAreInTheCatalog()
    {
        ModApiEvent ability = ModApi.Find("mod_ability")!;
        AssertThat(ability.Kind).IsEqual(91);
        AssertThat(ability.Cancellable).IsTrue();
        AssertThat(string.Join(",", ability.Fields.Select(f => f.Name))).IsEqual("ability,mod,refund,previous,level,experience,player");

        ModApiEvent pick = ModApi.Find("spell_pick")!;
        AssertThat(pick.Kind).IsEqual(92);
        AssertThat(pick.Cancellable).IsTrue();
        AssertThat(pick.Fields.Single(f => f.Name == "spell").Writable).IsTrue();
        AssertThat(pick.Fields[^1].Name).IsEqual("player");
        AssertThat(RefiiHost.SignalNames[91]).IsEqual("mod_ability");
        AssertThat(RefiiHost.SignalNames[92]).IsEqual("spell_pick");
    }

    [TestCase]
    public void APickIsPostedWithItsSlotFilledIn()
    {
        var pick = new Engine_UiSpellPicks.Pick("spell_weaving.partner", "spell_weaving", "spell_weaving.partner_{slot}");
        AssertThat(Engine_UiSpellPicks.KeyFor(pick.Store, 2)).IsEqual("spell_weaving.partner_3");
        AssertThat(Engine_UiSpellPicks.PickLine(pick, 2, 2, 4))
            .IsEqual("spell_pick 2 2 4 spell_weaving.partner_3 spell_weaving.partner spell_weaving");
        AssertThat(Engine_UiSpellPicks.PickLine(pick with { Mod = string.Empty }, 1, 0, -1))
            .IsEqual("spell_pick 1 0 -1 spell_weaving.partner_1 spell_weaving.partner -");
        AssertThat(Engine_UiSpellPicks.Game.IsGame).IsTrue();
        AssertThat(pick.IsGame).IsFalse();
        AssertThat(pick.Excludes(2)).IsFalse();
        AssertThat((pick with { Excluded = new HashSet<int> { 2, 7 } }).Excludes(7)).IsTrue();
    }

    private static Engine_HudModPieces.Piece Glyph(string id, string follow, string index) => new(
        id, "m", Engine_HudModPieces.Kind.Picture, default, Vector2.Zero, new Vector2(30f, 30f), string.Empty,
        string.Empty, default, default, string.Empty, Follow: follow, Index: index);

    [TestCase]
    public void APlayerPieceFollowsBothPlayersWidgets()
    {
        List<Engine_HudModPieces.Piece> composed = Engine_HudModTwins.WithPlayerTwoTwins(new List<Engine_HudModPieces.Piece>
        {
            Glyph("m.partner", "group.SpellCharging.SpellCharging", "player:m.partner"),
            Glyph("m.shared", "group.SpellCharging.SpellCharging", "mod:m.partner"),
            Glyph("m.own", "group.SpellCharging.SpellCharging", "player:m.own"),
            Glyph("m.own2", "group.SpellCharging2.SpellCharging2", "player:m.own"),
        });
        AssertThat(string.Join(",", composed.Select(p => p.Id))).IsEqual("m.partner,m.partner2,m.shared,m.own,m.own2");
        AssertThat(composed[1].Follow).IsEqual("group.SpellCharging2.SpellCharging2");
        AssertThat(composed[1].Index).IsEqual("player:m.partner");
    }

    [TestCase]
    public void APlayerCopyIsRenamedForPlayerTwosWidget()
    {
        var clone = new Engine_HudModPieces.Clone(
            "m.stamina", "m", "group.PlayerHealth.PlayerHealth", new Vector2(0f, 68f), 0.33f, new List<string> { "Bar" },
            "Heart", new List<string>(),
            new Dictionary<string, string>
            {
                ["PlayerHealth"] = "player:m.stamina/mod:m.cap",
                ["PlayerLowHealthPulser"] = "none",
            },
            new Dictionary<string, string> { ["Bar"] = "PlayerHealth" });
        AssertThat(Engine_HudModTwins.EachPlayer(clone)).IsTrue();
        AssertThat(Engine_HudModTwins.EachPlayer(clone with
        {
            Values = new Dictionary<string, string> { ["PlayerHealth"] = "mod:m.stamina/mod:m.cap" },
        })).IsFalse();

        var names = new Dictionary<string, string>
        {
            ["PlayerHealth"] = "CoopHealth",
            ["PlayerLowHealthPulser"] = "CoopLowHealthPulser",
        };
        Engine_HudModPieces.Clone two = Engine_HudModTwins.TwinForPlayerTwo(clone, "group.CoopHealth.CoopHealth", names);
        AssertThat(two.Id).IsEqual("m.stamina2");
        AssertThat(two.Of).IsEqual("group.CoopHealth.CoopHealth");
        AssertThat(two.Values["CoopHealth"]).IsEqual("player:m.stamina/mod:m.cap");
        AssertThat(two.Values["CoopLowHealthPulser"]).IsEqual("none");
        AssertThat(two.Bars["Bar"]).IsEqual("CoopHealth");
        AssertThat(two.Offset).IsEqual(clone.Offset);
    }

    [TestCase]
    public void TheBundledModsUseTheEnginePaths()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string mods = Path.Combine(root, "mods-src");
        string Read(string path) => File.ReadAllText(Path.Combine(mods, path));

        // The purchase is the engine's: no price table, Spend or refund in the mods' native code.
        AssertThat(File.Exists(Path.Combine(mods, "souls_combat", "native", "stamina_ability.cpp"))).IsFalse();
        AssertThat(File.Exists(Path.Combine(mods, "spell_weaving", "native", "weaving_ability.cpp"))).IsFalse();
        foreach (string file in Directory.EnumerateFiles(mods, "*.cpp", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            AssertThat(text.Contains("refii_mod_spend_experience", StringComparison.Ordinal) ||
                    text.Contains("refii_mod_ability_cost", StringComparison.Ordinal))
                .OverrideFailureMessage($"{file}: buys an ability itself").IsFalse();
            // One record per player: no "…2" publishes, no settings as a command channel.
            AssertThat(Regex.IsMatch(text, "Publish\\(\"[a-z_]+\\.[a-z_]*2[\"_]"))
                .OverrideFailureMessage($"{file}: publishes a player-two copy of a value").IsFalse();
            AssertThat(Regex.IsMatch(text, "Setting\\(\"spell_weaving\\.partner|Setting\\(\"[a-z_]+\\.(buy|refund)"))
                .OverrideFailureMessage($"{file}: takes a choice through a setting").IsFalse();
        }

        using JsonDocument souls = JsonDocument.Parse(Read("souls_combat/ui/abilities.json"));
        AssertThat(souls.RootElement.GetProperty("abilities")[0].GetProperty("levelKey").GetString())
            .IsEqual("souls_combat.stamina_ability");
        AssertThat(Read("souls_combat/native/souls_combat.h").Contains("\"souls_combat.stamina_ability\"")).IsTrue();

        using JsonDocument weaving = JsonDocument.Parse(Read("spell_weaving/ui/abilities.json"));
        AssertThat(weaving.RootElement.GetProperty("abilities")[0].GetProperty("levelKey").GetString())
            .IsEqual("spell_weaving.ability");
        AssertThat(Read("spell_weaving/native/spell_weaving.h").Contains("\"spell_weaving.ability\"")).IsTrue();

        // The partner pick's store is the key weave.cpp reads.
        using JsonDocument wheel = JsonDocument.Parse(Read("spell_weaving/ui/spellwheel.json"));
        AssertThat(wheel.RootElement.GetProperty("picks")[0].GetProperty("store").GetString())
            .IsEqual("spell_weaving.partner_{slot}");
        AssertThat(Read("spell_weaving/native/weave.cpp").Contains("\"spell_weaving.partner_%d\"")).IsTrue();

        // One copy per bar and one glyph: each player's from one record.
        using JsonDocument hud = JsonDocument.Parse(Read("souls_combat/ui/hud.json"));
        AssertThat(hud.RootElement.GetProperty("clones").GetArrayLength()).IsEqual(2);
        using JsonDocument glyph = JsonDocument.Parse(Read("spell_weaving/ui/hud.json"));
        AssertThat(glyph.RootElement.GetProperty("pieces").GetArrayLength()).IsEqual(1);
        // The title menu is the game's: a mod's help is its description on the Active Mods page.
        AssertThat(Read("spell_weaving/ui/templates.json").Contains("frontend-root")).IsFalse();
    }
}
