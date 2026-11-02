namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// Hotbar is a Godot script on the mod API: a spell slot charges through <c>charge_spell</c> (its
/// answering form), an expression slot holds through <c>perform_expression</c> /
/// <c>release_expression</c>, an item slot uses through the ungated <c>use_item</c>, the slots are typed
/// hashes in the player store, the pick lists are host lists whose rows run the script's verbs
/// (<c>register_command</c> with the row, so the pick arrives typed) and the bar is <c>ui/hud.json</c>
/// pieces filled with <c>hud_icon</c> and <c>player_set</c> values. It ships no Lua and no native code,
/// every RefiiHost member it uses exists with that argument count, every event and answer field it
/// reads is stated, its data files validate, the HUD reads exactly the values the script states, and
/// the old canvas, faked key and polled bus are gone.
/// </summary>
[TestSuite]
public class HotbarGodotTests
{
    private const string Mod = "hotbar";
    private const string File = "hotbar.gd";

    private static string Read(string root, params string[] parts) =>
        System.IO.File.ReadAllText(Path.Combine(new[] { GodotModScripts.ModFolder(root, Mod) }.Concat(parts).ToArray()));

    [TestCase]
    public void ShipsOneGodotScriptAndNoLua()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        GodotModScripts.AssertShips(GodotModScripts.ModFolder(root, Mod), Array.Empty<string>(), new[] { "godot/" + File });
    }

    [TestCase]
    public void EveryHostMemberExistsAndTheWorkaroundsAreGone()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string text = GodotModScripts.Script(root, Mod, File);
        string code = GodotModScripts.Code(text);
        var called = GodotModScripts.AssertHostContract(root, File, text);
        foreach (string name in new[]
                 {
                     "charge_spell", "perform_expression", "release_expression", "use_item", "ask",
                     "player_save_hash", "player_value_hash", "player_value", "player_save", "player_set",
                     "spell_info", "attack_mode", "in_play", "in_cutscene", "player_here", "hud_icon", "set_menu_rows",
                     "register_command", "after", "now", "hash", "hero", "setting", "constants",
                 })
        {
            AssertThat(called.Contains(name)).OverrideFailureMessage(File + " never calls host." + name).IsTrue();
        }

        // The answering forms: a charge and an expression the game took, nothing else lit or released.
        AssertThat(GodotModScripts.Calls(code).Contains(("charge_spell", 4))).IsTrue();
        AssertThat(GodotModScripts.Calls(code).Contains(("charge_spell", 3))).IsTrue();
        AssertThat(GodotModScripts.Calls(code).Contains(("perform_expression", 5))).IsTrue();
        AssertThat(code.Contains("{ \"ungated\": true }", StringComparison.Ordinal)).IsTrue();
        AssertThat(code.Contains("host.register_command(\"Hotbar:assign\", _assign, true)", StringComparison.Ordinal)).IsTrue();
        AssertThat(code.Contains("host.register_command(\"Hotbar:clear\", _clear_slot, true)", StringComparison.Ordinal)).IsTrue();
        AssertThat(GodotModScripts.Parameters(code, "_assign")).IsEqual(2);
        AssertThat(GodotModScripts.Parameters(code, "_clear_slot")).IsEqual(2);
        AssertThat(GodotModScripts.Constants(code).ToList())
            .ContainsExactlyInAnyOrder("ATTACK_MODE_MELEE", "ATTACK_MODE_RANGED", "ATTACK_MODE_MAGIC");

        // 0.1's workarounds: its own CanvasLayer and atlas rects, a tree walk for the texture loader,
        // a faked magic key and refii.toml parse, icon paths packed into floats on a polled bus,
        // English names, the shipped spell table and inventory walked by hand.
        foreach (string gone in new[]
                 {
                     "CanvasLayer", "Rect2(", "parse_input_event", "InputEventKey", "refii.toml", "key_magic",
                     "get_value", "publish", "LoadTexture", "run_lua", "call_lua", "_process", "Shock", "Inferno",
                     "SpellManager.", "Mod:lua",
                 })
        {
            AssertThat(code.Contains(gone, StringComparison.Ordinal)).OverrideFailureMessage(File + " still uses " + gone).IsFalse();
        }
    }

    [TestCase]
    public void EveryEventAndAnswerFieldIsStated()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string code = GodotModScripts.Code(GodotModScripts.Script(root, Mod, File));
        GodotModScripts.AssertEvents(File, code,
            ("input_action", new[] { "name", "player", "pressed" }),
            ("mod_menu_row", new[] { "menu", "row", "player" }),
            ("item_added", new[] { "player" }),
            ("item_removed", new[] { "player" }),
            ("item_used", new[] { "player" }),
            ("weapon_equipped", new[] { "player" }),
            ("ability_level", new[] { "player" }),
            ("spell_pick", new[] { "player" }),
            ("attack_mode_changed", new[] { "player" }),
            ("spell_charge_ended", new[] { "player", "released" }),
            ("spell_ended", new[] { "caster_player" }),
            ("hero_ready", new[] { "player" }),
            ("hero_gone", new[] { "player" }),
            ("level_loaded", Array.Empty<string>()));
        AssertThat(GodotModScripts.Signals(code).Contains("setting_changed")).IsTrue();

        // The row the verbs are handed is the mod_menu_row table, typed subject and all.
        var row = Refii.Mods.ModApi.Events.Single(e => e.LuaName == "mod_menu_row").Fields.Select(f => f.Name).ToHashSet();
        foreach (string field in new[] { "player", "subject_kind", "definition", "spell" })
        {
            AssertThat(row.Contains(field)).IsTrue();
            AssertThat(code.Contains("e.get(\"" + field + "\"", StringComparison.Ordinal)).OverrideFailureMessage(File + " never reads the row's " + field).IsTrue();
        }

        GodotModScripts.AssertAnswers(root, File, code,
            ("spells_known", new[] { "ok", "mask" }),
            ("expressions", new[] { "count", "e" }),
            ("inventory", new[] { "ok", "count", "next", "total", "d", "n", "u" }),
            ("equipped", new[] { "ok", "melee", "ranged" }),
            ("spell_in_slot", new[] { "type" }),
            ("item_count", new[] { "count" }),
            ("record_string", new[] { "ok", "value" }),
            ("record_ref", new[] { "ok", "value" }));
    }

    [TestCase]
    public void TheDataFilesValidateAndNameWhatTheScriptHandles()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string mod = GodotModScripts.ModFolder(root, Mod);
        (int checkedFiles, List<Refii.Mods.ModIssue> warnings) = GodotModScripts.AssertDataValidates(mod);
        // input.json, settings.json, settings.ui.json, ui/hud.json, ui/templates.json, mod.toml.
        AssertThat(checkedFiles).IsEqual(6);
        // The only warnings are the icon pictures, which hud_icon fills.
        foreach (Refii.Mods.ModIssue issue in warnings)
        {
            AssertThat(issue.Message.Contains("hud_icon", StringComparison.Ordinal))
                .OverrideFailureMessage($"{issue.Path} {issue.Pointer}: {issue.Message}").IsTrue();
        }

        string code = GodotModScripts.Code(GodotModScripts.Script(root, Mod, File));
        string input = Read(root, "input.json");
        var actions = Regex.Matches(input, @"^\s*""([a-z0-9_]+)""\s*:", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        AssertThat(actions).ContainsExactlyInAnyOrder("slot4", "slot5", "slot6", "slot7", "slot8", "slot9", "slot0");
        AssertThat(code.Contains("name.begins_with(\"hotbar.slot\")", StringComparison.Ordinal)).IsTrue();

        // Every row verb the templates name is one the script registers; no Lua function is named.
        string templates = Read(root, "ui", "templates.json");
        var registered = Regex.Matches(code, @"host\.register_command\(""([^""]+)""").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var named = Regex.Matches(templates, @"""(?:command|rowCommand)"":\s*""(Hotbar:[a-z_]+)""").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        AssertThat(named.OrderBy(n => n, StringComparer.Ordinal).ToList()).IsEqual(new List<string> { "Hotbar:assign", "Hotbar:clear" });
        AssertThat(named.IsSubsetOf(registered)).OverrideFailureMessage("not registered: " + string.Join(", ", named.Except(registered))).IsTrue();
        AssertThat(templates.Contains("Mod:lua:", StringComparison.Ordinal)).IsFalse();

        foreach (Match m in Regex.Matches(code, @"""(hotbar\.(?:slots|kind))"""))
        {
            AssertThat(templates.Contains("\"" + m.Groups[1].Value + "\"", StringComparison.Ordinal))
                .OverrideFailureMessage("templates.json has no menu " + m.Groups[1].Value).IsTrue();
        }

        foreach (string source in new[] { "\"spells_known\"", "\"expressions\"", "\"inventory:usable:0:5\"", "\"inventory:usable:0:6\"" })
        {
            AssertThat(templates.Contains(source, StringComparison.Ordinal)).OverrideFailureMessage("templates.json lacks " + source).IsTrue();
        }

        var tags = new HashSet<string>(
            System.IO.File.ReadLines(Path.Combine(mod, "language", "en-uk", "text", "book.babel.csv")).Skip(1).Select(l => l.Split(',')[0]),
            StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(templates + code, @"HOTBAR_[A-Z_]+"))
        {
            AssertThat(tags.Contains(m.Value)).OverrideFailureMessage("no text for " + m.Value).IsTrue();
        }

        string settings = Read(root, "settings.json");
        string page = Read(root, "settings.ui.json");
        string hud = Read(root, "ui", "hud.json");
        var keys = Regex.Matches(code, @"host\.setting\(""hotbar\.([a-z_]+)""")
            .Concat(Regex.Matches(hud, @"setting:hotbar\.([a-z_]+)")).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        AssertThat(keys.IsSupersetOf(new[] { "show", "size", "lift" })).OverrideFailureMessage("settings read: " + string.Join(", ", keys)).IsTrue();
        foreach (string key in keys)
        {
            AssertThat(settings.Contains("\"" + key + "\"", StringComparison.Ordinal)).OverrideFailureMessage("settings.json lacks " + key).IsTrue();
            AssertThat(page.Contains("\"" + key + "\"", StringComparison.Ordinal)).OverrideFailureMessage("settings.ui.json lacks " + key).IsTrue();
        }

        AssertThat(Read(root, "README.md").Contains("written as a Godot script", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheHudReadsWhatTheScriptStates()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string hud = Read(root, "ui", "hud.json");
        string code = GodotModScripts.Code(GodotModScripts.Script(root, Mod, File));
        var ids = Regex.Matches(hud, @"""id"":\s*""([a-z0-9_.]+)""").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var read = Regex.Matches(hud, @"""player:(hotbar\.[a-z_]+)\d*""").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        AssertThat(Regex.Matches(hud, @"""each"":\s*true").Count).IsEqual(ids.Count);

        // The whole bar is one group, sized and raised by the settings; every number is ringed.
        AssertThat(Regex.Matches(hud, @"""group"":\s*""hotbar\.bar""").Count).IsEqual(ids.Count);
        AssertThat(hud.Contains("\"scale\": \"setting:hotbar.size\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(hud.Contains("\"offset\": [0, \"-setting:hotbar.lift\"]", StringComparison.Ordinal)).IsTrue();
        AssertThat(Regex.Matches(hud, @"""outline"":\s*""#").Count).IsEqual(Regex.Matches(hud, @"""kind"":\s*""text""").Count);

        // Each slot's pictures hud_icon fills, and the frame around them.
        for (int s = 1; s <= 10; s++)
        {
            foreach (string piece in new[] { "glow", "orb", "icon", "dim", "ring", "ring_lit", "key", "key_lit" })
            {
                AssertThat(ids.Contains($"hotbar.{piece}{s}")).OverrideFailureMessage($"hud.json has no hotbar.{piece}{s}").IsTrue();
            }

            foreach (string piece in new[] { "badge", "badge_ring", "count", "count_none" })
            {
                AssertThat(ids.Contains($"hotbar.{piece}{s}")).IsEqual(s >= 4);
            }
        }

        AssertThat(code.Contains("\"hotbar.icon%d\" % s", StringComparison.Ordinal)).IsTrue();
        AssertThat(code.Contains("\"hotbar.dim%d\" % s", StringComparison.Ordinal)).IsTrue();

        // Every player: value a piece reads is one the script states, and the other way round.
        var stated = Regex.Matches(code, @"_state_value\(p, ""(hotbar\.[a-z_]+)(?:%d)?""").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        AssertThat(read.OrderBy(k => k, StringComparer.Ordinal).ToList())
            .IsEqual(stated.OrderBy(k => k, StringComparer.Ordinal).ToList());
    }

    [TestCase]
    public void TheTextFormatsDrawOnlyTheirOwnStates()
    {
        // hotbar.key<s>: 1 plain, -1 lit, 0 hidden — a piece each for plain and lit.
        AssertThat(Engine_UiValueSources.Format("'4';'';''", 1f)).IsEqual("4");
        AssertThat(Engine_UiValueSources.Format("'4';'';''", -1f)).IsEqual(string.Empty);
        AssertThat(Engine_UiValueSources.Format("'4';'';''", 0f)).IsEqual(string.Empty);
        AssertThat(Engine_UiValueSources.Format("'';'4';''", -1f)).IsEqual("4");
        AssertThat(Engine_UiValueSources.Format("'';'4';''", 1f)).IsEqual(string.Empty);

        // hotbar.count<s>: the stack, red at 0, nothing for -1.
        AssertThat(Engine_UiValueSources.Format("0;'';''", 12f)).IsEqual("12");
        AssertThat(Engine_UiValueSources.Format("0;'';''", 0f)).IsEqual(string.Empty);
        AssertThat(Engine_UiValueSources.Format("0;'';''", -1f)).IsEqual(string.Empty);
        AssertThat(Engine_UiValueSources.Format("'';'';0", 0f)).IsEqual("0");
        AssertThat(Engine_UiValueSources.Format("'';'';0", 3f)).IsEqual(string.Empty);
        AssertThat(Engine_UiValueSources.Format("'';'';0", -1f)).IsEqual(string.Empty);
    }

    [TestCase]
    public void TheMigrationReadsTheOldSlotsOnce()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string code = GodotModScripts.Code(GodotModScripts.Script(root, Mod, File));
        // 0.1: an item as two 16-bit halves, an expression as an index into the 38 names.
        AssertThat(code.Contains("\"0x%04x%04x\" % [int(a), int(b)]", StringComparison.Ordinal)).IsTrue();
        AssertThat(code.Contains("host.hash(\"Expression\" + LEGACY_EXPRESSIONS[index - 1])", StringComparison.Ordinal)).IsTrue();
        Match names = Regex.Match(code, @"const LEGACY_EXPRESSIONS := \[([^\]]*)\]");
        AssertThat(names.Success).IsTrue();
        AssertThat(Regex.Matches(names.Groups[1].Value, "\"[A-Za-z]+\"").Count).IsEqual(38);
        // Written once in the new form: the typed hash, the halves cleared.
        AssertThat(code.Contains("host.player_save_hash(p, \"hotbar.h%d\" % key, hash)", StringComparison.Ordinal)).IsTrue();
        AssertThat(code.Contains("host.player_save(p, \"hotbar.b%d\" % key, 0.0)", StringComparison.Ordinal)).IsTrue();
    }
}
