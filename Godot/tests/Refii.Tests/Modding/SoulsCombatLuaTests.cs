namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;

/// <summary>
/// Souls Combat is pure Lua: the proof that the mod API is complete for a hard mod. The bundled
/// mod ships no <c>native/</c> folder (so the launcher treats it as a soft mod and compiles
/// nothing), every <c>Refii.*</c> name its Lua calls is in the catalog the Guide and RefiiModApi
/// read (<see cref="ModApi.LuaHelpers"/>), every event it subscribes is a catalog event by its
/// Lua name, the fields it reads of each event are that event's, the pools it publishes are the
/// ones the HUD reads, and each chunk compiles with the game's own compiler. The mod is ONE FILE
/// PER RULE: init.lua requires each (<see cref="ModLua.TryModuleName"/>), and every module is
/// required by one of the mod's files, so nothing it ships is dead.
/// </summary>
[TestSuite]
public class SoulsCombatLuaTests
{
    private static readonly Regex RefiiCall = new(@"\bRefii\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex Subscribe = new(@"Refii\.on\(\s*""([a-z_]+)""", RegexOptions.Compiled);
    private static readonly Regex CatalogName = new(@"\bRefii\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex Comment = new(@"--[^\n]*", RegexOptions.Compiled);

    private static string? RepoRoot()
    {
        string? at = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && at != null; i++)
        {
            if (File.Exists(Path.Combine(at, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h")))
            {
                return at;
            }

            at = Path.GetDirectoryName(at);
        }

        return null;
    }

    private static string ModFolder(string root) => Path.Combine(root, "Fable2", "mods-src", "souls_combat");

    /// <summary>Every file of the mod's Lua as one text: what the composed chunk holds.</summary>
    private static string AllLua(string root) => string.Concat(LuaFiles(root).Select(f => f.Text + "\n"));

    private static IEnumerable<(string Path, string Text)> LuaFiles(string root)
    {
        string mod = Path.Combine(ModFolder(root), "mod");
        foreach (string file in Directory.EnumerateFiles(mod, "*.lua", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal))
        {
            yield return (Path.GetRelativePath(ModFolder(root), file), File.ReadAllText(file));
        }
    }

    [TestCase]
    public void ShipsNoNativeCode()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;  // a packaged test run without the source tree
        }

        AssertThat(Directory.Exists(Path.Combine(ModFolder(root), "native"))).IsFalse();
        AssertThat(Directory.EnumerateFiles(ModFolder(root), "*.cpp", SearchOption.AllDirectories).Any()).IsFalse();
        AssertThat(Directory.EnumerateFiles(ModFolder(root), "*.h", SearchOption.AllDirectories).Any()).IsFalse();
        // The loader composes every mod/*.lua: the three entry points by role, the rest as modules
        // reached by require. Every Lua file the mod ships is one of those, and every module is
        // required somewhere in the mod (an orphan would never run).
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string path, string text) in LuaFiles(root))
        {
            AssertThat(ModLua.IsModLuaPath(path)).OverrideFailureMessage(path + " is never composed").IsTrue();
            foreach (Match m in Regex.Matches(Comment.Replace(text, string.Empty), @"require\(""([^""]+)""\)"))
            {
                required.Add(m.Groups[1].Value.Replace('.', '/').ToLowerInvariant());
            }
        }

        var modules = new List<string>();
        foreach ((string path, _) in LuaFiles(root))
        {
            if (ModLua.TryModuleName(path, out string name))
            {
                modules.Add(name);
                AssertThat(required.Contains(name)).OverrideFailureMessage(path + " is never required").IsTrue();
            }
        }

        foreach (string name in required)
        {
            AssertThat(modules.Contains(name)).OverrideFailureMessage("require(\"" + name + "\") names no mod/" + name + ".lua").IsTrue();
        }

        // The nine rules and what they share, each its own file, and init.lua only requires.
        AssertThat(modules).ContainsExactlyInAnyOrder("ai", "cast_recovery", "commitment", "common", "guard", "lockon", "mana", "movement", "poise", "roll", "stamina");
        string init = File.ReadAllText(Path.Combine(ModFolder(root), "mod", "init.lua"));
        AssertThat(Regex.IsMatch(init, @"Refii\.on\(")).IsFalse();

        string toml = File.ReadAllText(Path.Combine(ModFolder(root), "mod.toml"));
        AssertThat(toml.Contains("native", StringComparison.OrdinalIgnoreCase)).IsFalse();
        string readme = File.ReadAllText(Path.Combine(ModFolder(root), "README.md"));
        AssertThat(readme.Contains("native/", StringComparison.Ordinal)).IsFalse();
        AssertThat(readme.Contains(".cpp", StringComparison.Ordinal)).IsFalse();
    }

    [TestCase]
    public void EveryRefiiCallIsInTheCatalog()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        var catalog = new HashSet<string>(StringComparer.Ordinal);
        foreach (ModApiHelper helper in ModApi.LuaHelpers)
        {
            foreach (Match m in CatalogName.Matches(helper.Signature))
            {
                catalog.Add(m.Groups[1].Value);
            }
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string path, string text) in LuaFiles(root))
        {
            // Code only: a comment may name a family ("Refii.player_*").
            string code = Comment.Replace(text, string.Empty);
            foreach (Match m in RefiiCall.Matches(code))
            {
                string name = m.Groups[1].Value;
                used.Add(name);
                AssertThat(catalog.Contains(name)).OverrideFailureMessage($"{path} calls Refii.{name}, which the catalog does not offer").IsTrue();
            }
        }

        // The port reaches every family of the API the native code reached: the player store,
        // the lock, movement speed, actions, liveness, abilities, the published bar cap.
        foreach (string name in new[]
                 {
                     "on", "setting", "log", "publish",
                     "player_value", "player_set", "player_add", "player_here", "player_hero", "player_of", "event_replica", "coop_mode",
                     "lock_target", "release_lock", "locked_target", "living", "wheel_switches_target", "in_cutscene",
                     "set_speed_multiplier", "clear_speed_multiplier", "speed_multiplier",
                     "current_action", "action_elapsed", "allow_action_interrupt",
                     "ability_level",
                 })
        {
            AssertThat(used.Contains(name)).OverrideFailureMessage("the port never calls Refii." + name).IsTrue();
        }
    }

    [TestCase]
    public void EveryEventSubscribedIsACatalogEventWithThoseFields()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        var events = ModApi.Events.ToDictionary(e => e.LuaName, e => e, StringComparer.Ordinal);
        string init = AllLua(root);
        var subscribed = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Subscribe.Matches(init))
        {
            string name = m.Groups[1].Value;
            subscribed.Add(name);
            AssertThat(events.ContainsKey(name)).OverrideFailureMessage("no event " + name).IsTrue();
        }

        // The seams the native code sat on, every one an event now (the look-at writer was the last hook).
        foreach (string name in new[]
                 {
                     "tick", "ability_request", "melee_swing", "roll_can_start", "roll_started", "block",
                     "spell_charge", "spell_charge_level", "spell_launch", "spell_launched",
                     "hit_response", "entity_died", "anim_event", "game_action", "look_at_button", "cutscene",
                 })
        {
            AssertThat(subscribed.Contains(name)).OverrideFailureMessage("the port never subscribes " + name).IsTrue();
        }

        // The fields each handler reads are the event's own, by the catalog.
        foreach ((string name, string[] fields) in new[]
                 {
                     ("tick", new[] { "seconds" }),
                     ("ability_request", new[] { "player", "type" }),
                     ("melee_swing", new[] { "player", "entity" }),
                     ("roll_can_start", new[] { "player" }),
                     ("roll_started", new[] { "player" }),
                     ("block", new[] { "player", "damage", "unblockable" }),
                     ("spell_charge", new[] { "player" }),
                     ("spell_charge_level", new[] { "player", "level" }),
                     ("spell_launch", new[] { "player", "slot", "cascading" }),
                     ("spell_launched", new[] { "player", "slot", "caster" }),
                     ("hit_response", new[] { "victim", "damage", "max_health" }),
                     ("entity_died", new[] { "entity" }),
                     ("anim_event", new[] { "name", "player", "entity" }),
                     ("game_action", new[] { "action", "hero" }),
                     ("look_at_button", new[] { "hero", "down" }),
                     ("cutscene", new[] { "active", "player" }),
                 })
        {
            var known = new HashSet<string>(events[name].Fields.Select(f => f.Name), StringComparer.Ordinal);
            foreach (string field in fields)
            {
                AssertThat(known.Contains(field)).OverrideFailureMessage($"{name} has no field {field}").IsTrue();
            }
        }

        // The two fields the port writes back are writable on their events.
        AssertThat(ModApi.WritableFields.Any(w => w.Kind == events["look_at_button"].Kind && w.Field == "down")).IsTrue();
        AssertThat(ModApi.WritableFields.Any(w => w.Kind == events["spell_charge_level"].Kind && w.Field == "level")).IsTrue();
    }

    [TestCase]
    public void ThePoolsArePlayerValuesTheHudReads()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string init = AllLua(root);
        string hud = File.ReadAllText(Path.Combine(ModFolder(root), "ui", "hud.json"));
        foreach (string key in new[] { "souls_combat.stamina", "souls_combat.stamina_max", "souls_combat.mana", "souls_combat.mana_max" })
        {
            AssertThat(init.Contains("\"" + key + "\"", StringComparison.Ordinal)).OverrideFailureMessage(key).IsTrue();
            AssertThat(hud.Contains("player:" + key, StringComparison.Ordinal)).OverrideFailureMessage("hud " + key).IsTrue();
        }

        AssertThat(init.Contains("Refii.publish(\"souls_combat.bar_cap\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(hud.Contains("mod:souls_combat.bar_cap", StringComparison.Ordinal)).IsTrue();
        // The engine's ability level is only read, never bought here.
        AssertThat(init.Contains("\"souls_combat.stamina_ability\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(init.Contains("spend_experience", StringComparison.Ordinal)).IsFalse();

        // EVERY SETTING IS READ AT USE, never snapshotted at init: no `S.x = Setting(` line and no
        // `field = Setting(...),` table entry in any module, so a live tune (the options page, the
        // Studio) takes effect without a reload; the table patches re-apply on the tick a value
        // changes (movement.lua ApplyIfChanged, ai.lua ApplyTierCapsIfChanged).
        foreach ((string path, string text) in LuaFiles(root))
        {
            if (path.EndsWith("camera.lua", StringComparison.Ordinal))
            {
                continue;  // the camera chunk runs on every camera build and reads there
            }

            AssertThat(Regex.IsMatch(text, @"^\s*S\.\w+\s*=\s*Setting\(", RegexOptions.Multiline)).OverrideFailureMessage(path + " snapshots a setting into S").IsFalse();
            AssertThat(Regex.IsMatch(text, @"^\s*\w+\s*=\s*Setting\([^\n]*\),\s*$", RegexOptions.Multiline)).OverrideFailureMessage(path + " snapshots a setting into a table").IsFalse();
        }

        AssertThat(File.ReadAllText(Path.Combine(ModFolder(root), "mod", "movement.lua")).Contains("Refii.on(\"tick\", ApplyIfChanged)", StringComparison.Ordinal)).IsTrue();
        AssertThat(File.ReadAllText(Path.Combine(ModFolder(root), "mod", "ai.lua")).Contains("Refii.on(\"tick\", ApplyTierCapsIfChanged)", StringComparison.Ordinal)).IsTrue();

        // Every tunable is read by its settings.json key — every key the file states, the camera's
        // in camera.lua and the rest in the modules.
        string settings = File.ReadAllText(Path.Combine(ModFolder(root), "settings.json"));
        string camera = File.ReadAllText(Path.Combine(ModFolder(root), "mod", "camera.lua"));
        foreach (Match m in Regex.Matches(settings, @"""([a-z_]+)"":"))
        {
            string key = m.Groups[1].Value;
            if (key == "souls_combat")
            {
                continue;
            }

            // By name at the call, or by the tier table's literal key (ai.lua reads ai_gap_<tier>
            // and ai_block_chance_<tier> through TierGap / TierBlock).
            string where = key.StartsWith("camera_", StringComparison.Ordinal) ? camera : init;
            AssertThat(where.Contains("Setting(\"" + key + "\"", StringComparison.Ordinal) || where.Contains("\"" + key + "\"", StringComparison.Ordinal))
                .OverrideFailureMessage("never read: " + key).IsTrue();
        }

        foreach (string key in new[]
                 {
                     "swing_cost", "roll_cost", "block_cost_per_damage", "stamina_floor", "stamina_regen_delay", "stamina_regen_per_second",
                     "mana_per_level", "mana_regen_delay", "mana_regen_per_second", "poise_fraction", "poise_window_seconds",
                     "commit_extra_seconds", "lock_drop_seconds", "cast_recovery_seconds",
                 })
        {
            AssertThat(settings.Contains("\"" + key + "\"", StringComparison.Ordinal)).OverrideFailureMessage("settings " + key).IsTrue();
            AssertThat(init.Contains("Setting(\"" + key + "\"", StringComparison.Ordinal)).OverrideFailureMessage("init " + key).IsTrue();
        }
    }

    [TestCase]
    public void EveryChunkCompilesWithTheGamesCompiler()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        foreach ((string path, string text) in LuaFiles(root))
        {
            // Each file as the composer wraps it: an entry point as the body of the protected call,
            // a module as the function the registry holds.
            string composed = ModLua.TryModuleName(path, out string name)
                ? ModLua.WrapModule("souls_combat", name, text)
                : ModLua.Wrap("souls_combat", text);
            bool ok = Refii.Lua.Engine_LuaDecompiler.TryCheckSource(composed, "@souls_combat/" + path, out string error);
            if (!ok && error.Contains("not available", StringComparison.Ordinal))
            {
                return;  // no compiler in this test host: the mount checks the chunk at run time
            }

            AssertThat(ok).OverrideFailureMessage($"{path} does not compile: {error}").IsTrue();
        }
    }
}
