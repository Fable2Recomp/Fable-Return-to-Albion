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
/// read (<see cref="ModApi.LuaHelpers"/>, the named numbers of <see cref="ModApi.Constants"/>), every
/// event it subscribes is a catalog event by its Lua name (or the runtime's own setting_changed),
/// the fields it reads of each event are that event's, the pools it keeps are the runtime's
/// (<c>Refii.pool</c>) under the keys the HUD reads, its workarounds are gone for the stated
/// primitives, and each chunk compiles with the game's own compiler. The mod is ONE FILE
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

        // The twelve rules, the bars and what they share, each its own file, and init.lua only requires.
        AssertThat(modules).ContainsExactlyInAnyOrder("ai", "bars", "cast_recovery", "commitment", "common", "damage", "guard", "healing", "lockon", "mana", "movement", "poise", "roll", "sprint", "stamina");
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

        foreach ((string name, int _) in ModApi.Constants)
        {
            catalog.Add(name);
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

        // The mod stands on the stated primitives (hard_mods_plan Part I): the runtime's clock,
        // timers, pools and per-entity store, the per-hero locomotion and parry, the timed grant,
        // the sticky wheel, the creature's rank, a record's number and the named constants — and
        // the lock, actions, liveness, abilities, the player store and the published bar cap.
        foreach (string name in new[]
                 {
                     "on", "setting", "log", "publish", "now", "after", "cancel",
                     "player_value", "player_hero", "player_of", "event_replica", "coop_mode",
                     "pool", "pool_spend", "pool_value", "entity_value", "entity_set",
                     "lock_target", "release_lock", "locked_target", "living", "wheel_switches_target", "in_cutscene",
                     "set_locomotion", "set_parry", "hud_hold", "health",
                     "current_action", "action_type", "action_elapsed", "allow_action_interrupt_at",
                     "ability_level", "creature_tier", "record_number", "handle",
                     "REQUEST_MELEE_PRESSED", "ACTION_LOCK_SET", "ABILITY_LIGHTNING", "ABILITY_RAISE_DEAD", "CREATURE_ELITE", "CREATURE_LIEUTENANT",
                 })
        {
            AssertThat(used.Contains(name)).OverrideFailureMessage("the port never calls Refii." + name).IsTrue();
        }

        // The workarounds the primitives replace are gone: the speed plant (cast recovery is the
        // held action), the hand-polled grant, the per-tick wheel statement, the global locomotion
        // and parry tables, the hand-made pools and their rest counters, the press/release pairing,
        // the poise slot table, the block-count poll, the name-guessed tier and the game clock for
        // the mod's own times.
        string allCode = string.Concat(LuaFiles(root).Select(f => Comment.Replace(f.Text, string.Empty) + "\n"));
        foreach (string gone in new[]
                 {
                     "set_speed_multiplier", "clear_speed_multiplier", "Refii.allow_action_interrupt(",
                     "Refii.wheel_switches_target(hero)", "HeroLocomotionDataIndoorsDefault", "HeroCombatBalanceParams.",
                     "player_set(", "player_add(", "kRestCap", "PressRefused", "kPoiseSlots", "GetNumHitsOfTypeInTime",
                     "string.find(name, \"Elite\"", "Timing.GetWorldFrame", "Timing.HaveSecondsElapsedSince",
                     "arrival_slack", "whiff_from", "without + e.seconds",
                 })
        {
            AssertThat(allCode.Contains(gone, StringComparison.Ordinal)).OverrideFailureMessage("the port still has " + gone).IsFalse();
        }

        // The heal punish reads the game's own health change, never an item use; item_used is the
        // healing rule's alone.
        string ai = Comment.Replace(File.ReadAllText(Path.Combine(ModFolder(root), "mod", "ai.lua")), string.Empty);
        AssertThat(ai.Contains("item_used", StringComparison.Ordinal)).OverrideFailureMessage("the heal punish reads item_used again").IsFalse();
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
            if (name == "setting_changed")
            {
                // The runtime's own event, raised by the preamble: a helper, not a bus kind.
                AssertThat(ModApi.LuaHelpers.Any(h => h.Signature.StartsWith("setting_changed", StringComparison.Ordinal))).IsTrue();
                continue;
            }

            AssertThat(events.ContainsKey(name)).OverrideFailureMessage("no event " + name).IsTrue();
        }

        // Every seam the rules sit on, each an event: the stated ends (a strike's hits, the cast's
        // move-cancel window, the lock's drop, a heal), the per-hero arrivals and a live setting.
        // No rule polls the tick.
        foreach (string name in new[]
                 {
                     "ability_request", "melee_swing", "melee_swing_ended", "roll_can_start", "roll_started", "block",
                     "spell_charge", "spell_charge_level", "spell_launch", "spell_launched", "ability_level",
                     "hit_response", "hit_landed", "health_changed", "entity_died", "anim_event", "action_move_interrupt",
                     "game_action", "look_at_button", "cutscene", "lock_lost", "target_changed",
                     "hero_ready", "hero_gone", "setting_changed", "mod_ability",
                 })
        {
            AssertThat(subscribed.Contains(name)).OverrideFailureMessage("the port never subscribes " + name).IsTrue();
        }

        AssertThat(subscribed.Contains("tick")).OverrideFailureMessage("a rule polls the tick").IsFalse();

        // The fields each handler reads are the event's own, by the catalog.
        foreach ((string name, string[] fields) in new[]
                 {
                     ("ability_request", new[] { "player", "type" }),
                     ("melee_swing", new[] { "player", "action" }),
                     ("melee_swing_ended", new[] { "player", "hits" }),
                     ("action_move_interrupt", new[] { "player", "action", "action_type" }),
                     ("lock_lost", new[] { "hero" }),
                     ("target_changed", new[] { "hero", "target" }),
                     ("hero_ready", new[] { "player" }),
                     ("hero_gone", new[] { "player", "hero" }),
                     ("health_changed", new[] { "player", "delta" }),
                     ("hit_landed", new[] { "victim", "victim_player", "attacker_player" }),
                     ("ability_level", new[] { "player", "type" }),
                     ("roll_can_start", new[] { "player" }),
                     ("roll_started", new[] { "player" }),
                     ("block", new[] { "player", "damage", "unblockable" }),
                     ("spell_charge", new[] { "player" }),
                     ("spell_charge_level", new[] { "player", "level" }),
                     ("spell_launch", new[] { "player", "slot", "cascading" }),
                     ("spell_launched", new[] { "player", "slot", "caster" }),
                     ("mod_ability", new[] { "player" }),
                     ("hit_response", new[] { "victim", "damage", "max_health" }),
                     ("entity_died", new[] { "entity" }),
                     ("anim_event", new[] { "name", "player", "action" }),
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

        // The holds are cancels the catalog offers: the strike's grant and the cast's move-cancel.
        AssertThat(events["action_move_interrupt"].Cancel.Length > 0).IsTrue();
        AssertThat(events["anim_event"].Cancel.Length > 0).IsTrue();

        // The two fields the port writes back are writable on their events.
        AssertThat(ModApi.WritableFields.Any(w => w.Kind == events["look_at_button"].Kind && w.Field == "down")).IsTrue();
        AssertThat(ModApi.WritableFields.Any(w => w.Kind == events["spell_charge_level"].Kind && w.Field == "level")).IsTrue();
    }

    [TestCase]
    public void ThePoolsAreRuntimePoolsTheHudReads()
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
        // Each pool is the runtime's, its maximum under the key the HUD's bar reads.
        string stamina = File.ReadAllText(Path.Combine(ModFolder(root), "mod", "stamina.lua"));
        string mana = File.ReadAllText(Path.Combine(ModFolder(root), "mod", "mana.lua"));
        AssertThat(stamina.Contains("Refii.pool(p, kStaminaKey, {", StringComparison.Ordinal)).IsTrue();
        AssertThat(stamina.Contains("max_key = kStaminaMaxKey", StringComparison.Ordinal)).IsTrue();
        AssertThat(mana.Contains("Refii.pool(p, kManaKey, {", StringComparison.Ordinal)).IsTrue();
        AssertThat(mana.Contains("max_key = kManaMaxKey", StringComparison.Ordinal)).IsTrue();
        AssertThat(hud.Contains("mod:souls_combat.bar_cap", StringComparison.Ordinal)).IsTrue();
        // The engine's ability level is only read, never bought here.
        AssertThat(init.Contains("\"souls_combat.stamina_ability\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(init.Contains("spend_experience", StringComparison.Ordinal)).IsFalse();

        // EVERY SETTING IS READ AT USE, never snapshotted at init: no `S.x = Setting(` line and no
        // `field = Setting(...),` table entry in any module, so a live tune (the options page, the
        // Studio) takes effect without a reload; what is stated once (each hero's locomotion and
        // parry, the styles' caps, the camera's fields) is stated again on setting_changed.
        foreach ((string path, string text) in LuaFiles(root))
        {
            if (path.EndsWith("camera.lua", StringComparison.Ordinal))
            {
                continue;  // the camera chunk runs on every camera build and reads there
            }

            AssertThat(Regex.IsMatch(text, @"^\s*S\.\w+\s*=\s*Setting\(", RegexOptions.Multiline)).OverrideFailureMessage(path + " snapshots a setting into S").IsFalse();
            AssertThat(Regex.IsMatch(text, @"^\s*\w+\s*=\s*Setting\([^\n]*\),\s*$", RegexOptions.Multiline)).OverrideFailureMessage(path + " snapshots a setting into a table").IsFalse();
        }

        foreach (string file in new[] { "movement.lua", "ai.lua", "camera.lua" })
        {
            AssertThat(File.ReadAllText(Path.Combine(ModFolder(root), "mod", file)).Contains("Refii.on(\"setting_changed\"", StringComparison.Ordinal))
                .OverrideFailureMessage(file + " never hears a setting change").IsTrue();
        }

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

    /// <summary>
    /// While any of a player's health, mana or stamina is short, that player's health readout is
    /// held on screen (<c>Refii.hud_hold</c>), and the mana and stamina copies follow it: the piece
    /// is the player's own readout of hud.bgf, held on both HUDs, the recheck runs on the runtime's
    /// clock only while a bar is short, and the setting turns it off.
    /// </summary>
    [TestCase]
    public void TheBarsHoldTheHealthReadoutWhileAnyIsShort()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string bars = Comment.Replace(File.ReadAllText(Path.Combine(ModFolder(root), "mod", "bars.lua")), string.Empty);
        AssertThat(bars.Contains("{ \"PlayerHealth\", \"CoopHealth\" }", StringComparison.Ordinal)).IsTrue();
        AssertThat(bars.Contains("Refii.hud_hold(0, kPieces[p], on)", StringComparison.Ordinal)).IsTrue();
        AssertThat(bars.Contains("Refii.health(hero)", StringComparison.Ordinal)).IsTrue();
        AssertThat(bars.Contains("Refii.pool_value(p, key)", StringComparison.Ordinal)).IsTrue();
        AssertThat(bars.Contains("PoolShort(p, kStaminaKey) or PoolShort(p, kManaKey)", StringComparison.Ordinal)).IsTrue();
        AssertThat(bars.Contains("Setting(\"bars_always_when_not_full\", 1) ~= 0", StringComparison.Ordinal)).IsTrue();
        AssertThat(bars.Contains("Refii.after(kRecheckSeconds, function() Recheck(p) end, kTimerKey .. p)", StringComparison.Ordinal)).IsTrue();
        AssertThat(bars.Contains("Refii.cancel(kTimerKey .. p)", StringComparison.Ordinal)).IsTrue();
        foreach (string name in new[]
                 {
                     "melee_swing", "roll_started", "block", "spell_launched", "health_changed", "hit_landed",
                     "hero_ready", "hero_gone", "ability_level", "mod_ability", "setting_changed",
                 })
        {
            AssertThat(Regex.IsMatch(bars, @"Refii\.on\(\s*""" + name + @"""")).OverrideFailureMessage("bars never hears " + name).IsTrue();
        }

        AssertThat(Regex.IsMatch(bars, @"Refii\.on\(\s*""tick""")).IsFalse();

        // Required after every rule that spends, so its handlers run after the spend.
        string init = File.ReadAllText(Path.Combine(ModFolder(root), "mod", "init.lua"));
        int at = init.IndexOf("require(\"bars\")", StringComparison.Ordinal);
        AssertThat(at > 0).IsTrue();
        foreach (string rule in new[] { "stamina", "roll", "guard", "mana" })
        {
            int ruleAt = init.IndexOf("require(\"" + rule + "\")", StringComparison.Ordinal);
            AssertThat(ruleAt >= 0 && ruleAt < at).OverrideFailureMessage(rule + " is required after bars").IsTrue();
        }

        // Both copies are of the held readout, so they show while it is held.
        using (var hud = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(ModFolder(root), "ui", "hud.json"))))
        {
            var clones = hud.RootElement.GetProperty("clones").EnumerateArray().ToList();
            AssertThat(clones.Count).IsEqual(2);
            foreach (var clone in clones)
            {
                AssertThat(clone.GetProperty("of").GetString()).IsEqual("group.PlayerHealth.PlayerHealth");
            }
        }

        // On by default, and offered as a toggle on the mod's options page.
        using (var settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(ModFolder(root), "settings.json"))))
        {
            AssertThat(settings.RootElement.GetProperty("souls_combat").GetProperty("bars_always_when_not_full").GetDouble()).IsEqual(1.0);
        }

        using (var page = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(ModFolder(root), "settings.ui.json"))))
        {
            AssertThat(page.RootElement.GetProperty("bars_always_when_not_full").GetProperty("type").GetString()).IsEqual("toggle");
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
