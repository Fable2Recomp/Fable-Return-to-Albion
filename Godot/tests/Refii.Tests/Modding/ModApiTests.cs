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
/// The catalog (Engine_ModApi) is held to the header (core/refii_mod_events.h) and to the two
/// build configs: every event's kind and name agree with the enum, every seam function is named
/// in both builds, and the Lua names are distinct identifiers.
/// </summary>
[TestSuite]
public class ModApiTests
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

    [TestCase]
    public void KindsAreDenseAndNamesUnique()
    {
        for (int i = 0; i < ModApi.Events.Count; i++)
        {
            AssertThat(ModApi.Events[i].Kind).IsEqual(i);
        }

        AssertThat(ModApi.Events.Select(e => e.Name).Distinct().Count()).IsEqual(ModApi.Events.Count);
        AssertThat(ModApi.Events.Select(e => e.LuaName).Distinct().Count()).IsEqual(ModApi.Events.Count);
        foreach (ModApiEvent e in ModApi.Events)
        {
            AssertThat(Regex.IsMatch(e.LuaName, "^[a-z][a-z0-9_]*$")).IsTrue();
            AssertThat(e.Fields.Count > 0).IsTrue();
        }
    }

    [TestCase]
    public void HeaderAgreesWithCatalog()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;  // a packaged test run without the source tree
        }

        string header = File.ReadAllText(Path.Combine(root, "src", "vfs", "include", "core", "refii_mod_events.h"));
        Match version = Regex.Match(header, @"#define REFII_MOD_API_VERSION (\d+)");
        AssertThat(version.Success).IsTrue();
        AssertThat(int.Parse(version.Groups[1].Value)).IsEqual(ModApi.Version);

        var kinds = new Dictionary<string, int>();
        foreach (Match m in Regex.Matches(header, @"REFII_EVENT_([A-Z_]+) = (\d+)"))
        {
            kinds[m.Groups[1].Value] = int.Parse(m.Groups[2].Value);
        }

        AssertThat(kinds["COUNT"]).IsEqual(ModApi.Events.Count);
        foreach (ModApiEvent e in ModApi.Events)
        {
            string enumName = e.LuaName.ToUpperInvariant();
            AssertThat(kinds.ContainsKey(enumName)).OverrideFailureMessage($"{e.Name}: no REFII_EVENT_{enumName} in the header").IsTrue();
            AssertThat(kinds[enumName]).OverrideFailureMessage($"{e.Name}: kind differs from the header").IsEqual(e.Kind);

            // The struct, with the catalog's kind and every field it lists (ctx/base/cancel are the base's).
            Match structMatch = Regex.Match(header, @"struct " + e.Name + @"Event : (\w+) \{(.*?)\};", RegexOptions.Singleline);
            AssertThat(structMatch.Success).OverrideFailureMessage($"{e.Name}: no struct {e.Name}Event in the header").IsTrue();
            AssertThat(structMatch.Groups[2].Value.Contains($"kKind = REFII_EVENT_{enumName};")).IsTrue();
            string body = structMatch.Groups[2].Value;
            string parent = structMatch.Groups[1].Value;
            string parentBody = parent == "Event"
                ? string.Empty
                : Regex.Match(header, @"struct " + parent + @" : \w+ \{(.*?)\};", RegexOptions.Singleline).Groups[1].Value;
            foreach (ModApiField f in e.Fields)
            {
                bool declared = Regex.IsMatch(body + parentBody, @"\b" + f.Name + @";");
                AssertThat(declared).OverrideFailureMessage($"{e.Name}.{f.Name}: not a field of the struct").IsTrue();
            }
        }
    }

    [TestCase]
    public void SeamsAreNamedInBothBuilds()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        foreach (string build in new[] { "Fable2", "Fable2TU1" })
        {
            string config = File.ReadAllText(Path.Combine(root, "versions", build, "refii_config.toml"));
            foreach (string function in ModApi.Events.SelectMany(e => e.SeamFunctions).Distinct())
            {
                AssertThat(config.Contains("\"" + function + "\""))
                    .OverrideFailureMessage($"{function} is not named in versions/{build}/refii_config.toml")
                    .IsTrue();
            }
        }
    }

    [TestCase]
    public void LuaAndGodotReceiveEveryCatalogField()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        // The table text the bus builds per kind (mod_events.cpp LuaFields) is what both Lua and
        // Godot scripts receive: every field the catalog offers them must be in it.
        string bus = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_events.cpp"));
        foreach (ModApiEvent e in ModApi.Events)
        {
            string label = "case REFII_EVENT_" + e.LuaName.ToUpperInvariant() + ":";
            int at = bus.IndexOf(label, StringComparison.Ordinal);
            AssertThat(at >= 0).OverrideFailureMessage($"{e.Name}: no LuaFields case").IsTrue();
            int end = bus.IndexOf("return;", at, StringComparison.Ordinal);
            string body = bus[at..end];
            foreach (ModApiField f in e.Fields.Where(f => !f.NativeOnly && !f.Type.Contains('*') || f.Type.Contains("string")))
            {
                AssertThat(body.Contains(f.Name + "=", StringComparison.Ordinal))
                    .OverrideFailureMessage($"{e.Name}.{f.Name}: the catalog offers it to Lua and Godot, the bus never sends it")
                    .IsTrue();
            }
        }
    }

    [TestCase]
    public void CoopEventsStateTheirPlayerLast()
    {
        // Added after the API shipped: a trailing field, so a mod built against the old struct
        // keeps every offset it reads.
        foreach (string name in new[]
                 {
                     "dog_trick", "opinion_modified", "sleep_menu", "travel", "fast_travel", "menu_opened",
                     "menu_closed", "menu_screen", "weather_region", "sleep_chosen",
                 })
        {
            ModApiEvent e = ModApi.Find(name)!;
            AssertThat(e.Fields[^1].Name).OverrideFailureMessage($"{e.Name}: player is not the last field").IsEqual("player");
            AssertThat(e.Fields[^1].Type).IsEqual("int32_t");
        }

        ModApiEvent weather = ModApi.Find("dynamic_weather")!;
        AssertThat(weather.Kind).IsEqual(86);
        AssertThat(string.Join(",", weather.Fields.Select(f => f.Name))).IsEqual("level,weather,previous,seconds,from_host");
    }

    /// <summary>
    /// THE MASKS HOLD EVERY KIND, on every surface a subscription crosses as bits: Lua's groups
    /// (float32-exact: never more than 20 significant bits in one number), the host's words
    /// (Godot's event and verdict masks). The catalog's constants are the payload's, and the kind
    /// count stays under each capacity — the next kind past a mask fails here, not silently.
    /// </summary>
    [TestCase]
    public void TheMasksHoldEveryKindOnEverySurface()
    {
        AssertThat(ModApi.LuaMaskBits <= 20).OverrideFailureMessage("a game Lua number is a float32: at most 20 significant bits per group").IsTrue();
        AssertThat(ModApi.LuaMaskCapacity).IsEqual(ModApi.LuaMaskBits * ModApi.LuaMaskGroups);
        AssertThat(ModApi.HostMaskCapacity).IsEqual(64 * ModApi.HostMaskWords);
        AssertThat(ModApi.LuaMaskCapacity >= 200).IsTrue();
        AssertThat(ModApi.HostMaskCapacity >= 200).IsTrue();
        AssertThat(ModApi.Events.Count <= ModApi.LuaMaskCapacity)
            .OverrideFailureMessage($"{ModApi.Events.Count} kinds exceed the Lua masks' {ModApi.LuaMaskCapacity}: raise kLuaMaskGroups (mod_events.cpp) and ModApi.LuaMaskGroups")
            .IsTrue();
        AssertThat(ModApi.Events.Count <= ModApi.HostMaskCapacity)
            .OverrideFailureMessage($"{ModApi.Events.Count} kinds exceed the host masks' {ModApi.HostMaskCapacity}: raise REFII_MOD_HOST_MASK_WORDS (payload_bridge.h), ModApi.HostMaskWords and ModEventMask's words")
            .IsTrue();

        // The host's mask type holds its whole capacity, word by word.
        AssertThat(ModEventMask.Capacity).IsEqual(ModApi.HostMaskCapacity);
        AssertThat(ModEventMask.Words).IsEqual(ModApi.HostMaskWords);
        var empty = new ModEventMask();
        AssertThat(empty.IsZero).IsTrue();
        for (int word = 0; word < ModEventMask.Words; word++)
        {
            ModEventMask low = empty.With(64 * word);
            ModEventMask high = empty.With(64 * word + 63);
            AssertThat(low.Word(word)).OverrideFailureMessage($"word {word} bit 0").IsEqual(1ul);
            AssertThat(high.Word(word)).OverrideFailureMessage($"word {word} bit 63").IsEqual(1ul << 63);
            AssertThat(high.Has(64 * word + 63)).IsTrue();
            AssertThat(high.Has(64 * word + 62)).IsFalse();
            AssertThat(low.IsZero).IsFalse();
            AssertThat(low == high).IsFalse();
        }

        AssertThat(empty.With(ModEventMask.Capacity).IsZero).OverrideFailureMessage("a kind past the capacity is never in the set").IsTrue();
        AssertThat(empty.With(-1).IsZero).IsTrue();
        AssertThat(empty.With(ModEventMask.Capacity - 1).Has(ModEventMask.Capacity - 1)).IsTrue();
        AssertThat(empty.With(ModApi.Events.Count - 1).Has(ModApi.Events.Count - 1)).IsTrue();

        // A full group is exact as the game Lua's float32 number (the reader clamps to it).
        int fullGroup = (1 << ModApi.LuaMaskBits) - 1;
        AssertThat((int)(float)fullGroup).IsEqual(fullGroup);
        AssertThat((int)(float)(fullGroup + 1)).IsEqual(fullGroup + 1);

        if (RepoRoot() is not { } root)
        {
            return;
        }

        // The preamble's Refii.on groups by the catalog's bit count (the composed preamble is not
        // built here: it folds in ModSettings, which is Godot-backed, and the test host has no Godot).
        string preamble = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_ModLua.cs"));
        AssertThat(preamble.Contains("\"\\tlocal group = math.floor(kind / \" + ModApi.LuaMaskBits + \") + 1\\n\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(preamble.Contains("\"\\tlocal bit = 2 ^ (kind % \" + ModApi.LuaMaskBits + \")\\n\"", StringComparison.Ordinal)).IsTrue();

        // The payload's constants are the catalog's: the Lua reader's groups, the host's words, and
        // the header's count under both (the static_asserts beside them say the same at compile time).
        string bus = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_events.cpp"));
        Match bits = Regex.Match(bus, @"constexpr int32_t kLuaMaskBits = (\d+);");
        Match groups = Regex.Match(bus, @"constexpr int32_t kLuaMaskGroups = (\d+);");
        AssertThat(bits.Success && groups.Success).IsTrue();
        AssertThat(int.Parse(bits.Groups[1].Value)).OverrideFailureMessage("kLuaMaskBits != ModApi.LuaMaskBits").IsEqual(ModApi.LuaMaskBits);
        AssertThat(int.Parse(groups.Groups[1].Value)).OverrideFailureMessage("kLuaMaskGroups != ModApi.LuaMaskGroups").IsEqual(ModApi.LuaMaskGroups);
        AssertThat(bus.Contains("static_assert(REFII_EVENT_COUNT <= kLuaMaskBits * kLuaMaskGroups", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("refii_mod_game_lua_int(&ctx, base, expr, 0, (1 << kLuaMaskBits) - 1, &mask)", StringComparison.Ordinal))
            .OverrideFailureMessage("the Lua reader clamps each group to its bit count").IsTrue();

        string bridge = File.ReadAllText(Path.Combine(root, "src", "vfs", "include", "core", "payload_bridge.h"));
        Match words = Regex.Match(bridge, @"#define REFII_MOD_HOST_MASK_WORDS (\d+)");
        AssertThat(words.Success).IsTrue();
        AssertThat(int.Parse(words.Groups[1].Value)).OverrideFailureMessage("REFII_MOD_HOST_MASK_WORDS != ModApi.HostMaskWords").IsEqual(ModApi.HostMaskWords);
        string values = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "core", "mod_values.cpp"));
        AssertThat(values.Contains("static_assert(REFII_EVENT_COUNT <= refii::mods::kHostMaskCapacity", StringComparison.Ordinal)).IsTrue();

        string header = File.ReadAllText(Path.Combine(root, "src", "vfs", "include", "core", "refii_mod_events.h"));
        Match count = Regex.Match(header, @"REFII_EVENT_COUNT = (\d+)");
        AssertThat(count.Success).IsTrue();
        int kinds = int.Parse(count.Groups[1].Value);
        AssertThat(kinds <= ModApi.LuaMaskCapacity).OverrideFailureMessage($"REFII_EVENT_COUNT {kinds} exceeds the Lua masks' {ModApi.LuaMaskCapacity}").IsTrue();
        AssertThat(kinds <= ModApi.HostMaskCapacity).OverrideFailureMessage($"REFII_EVENT_COUNT {kinds} exceeds the host masks' {ModApi.HostMaskCapacity}").IsTrue();

        // Every word of the host's masks has a way across: 0 and 1 by the exports, the rest by the lane.
        string hostBridge = File.ReadAllText(Path.Combine(root, "Godot", "src", "host", "HostBridge.cs"));
        AssertThat(hostBridge.Contains("mask_word event {word} {mask.Word(word):x}", StringComparison.Ordinal)).IsTrue();
        AssertThat(hostBridge.Contains("mask_word verdict {word} {mask.Word(word):x}", StringComparison.Ordinal)).IsTrue();
        string commands = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("op == \"mask_word\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("refii_bridge_mod_set_verdict_mask_word(word, bits)", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("refii_bridge_mod_set_host_event_mask_word(word, bits)", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void GodotHelpersNameEveryCommandAndQuery()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        // Every verb the guest tick answers (mod_commands.cpp) is in the catalog's Godot section,
        // which the Guide and RefiiModApi.ListGodotHelpers show.
        string grammar = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        List<string> verbs = Regex.Matches(grammar, "(?:what|op) == \"([a-z_]+)\"")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        AssertThat(verbs.Count > 30).IsTrue();
        // menu_row and box_answer are the host's own: Godot's menu lists and host boxes post them
        // when a mod's row is chosen or its box answered (Engine_UiModMenus, Engine_UiModBoxes), so
        // no script calls them; notice_done and menu_box_answer likewise for a notice gone and a menu
        // box answered. fx_play and fx_stop likewise: Engine_FxPlayer posts them for a
        // play_fx under the classic look; a script calls play_fx / stop_fx.
        // fx_reload is the remount's (Engine_ModRemount -> Engine_FxModEffects.ReloadGuestBank), and
        // so are gdb_reload and gdb_revert (Engine_ModRemount -> GdbHotReload.Apply).
        // mask_word is HostBridge.SetModEventMask / SetModVerdictMask's: the words of the host's
        // masks past the two the exports carry.
        foreach (string verb in verbs.Where(v => v is not ("menu_row" or "box_answer" or "notice_done" or "menu_box_answer" or "fx_play" or "fx_stop" or "fx_reload" or "gdb_reload" or "gdb_revert" or "mask_word")))
        {
            AssertThat(Regex.IsMatch(godot, @"\b" + verb + @"\b"))
                .OverrideFailureMessage($"'{verb}' is answered by mod_commands.cpp but missing from ModApi.GodotHelpers")
                .IsTrue();
        }
    }

    [TestCase]
    public void CatalogListsTheSharedPlayerPrimitives()
    {
        // The co-op primitives the core layer and the mods share (Forge/Fabric style): each is
        // documented on every surface that has it.
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        foreach (string call in new[] { "Refii.player_for(", "Refii.acting_player(", "Refii.run_as_player(", "Refii.combat_state(", "Refii.world_here(", "Refii.player_of(", "Refii.event_replica(", "Refii.show_to_player(", "Refii.forgive_crimes(", "Refii.discovered_crime(", "Refii.perceived_renown(" })
        {
            AssertThat(lua.Contains(call, StringComparison.Ordinal)).OverrideFailureMessage($"{call} is not in ModApi.LuaHelpers").IsTrue();
        }

        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        foreach (string call in new[] { "refii_mod_player_for(", "refii_mod_world_here(", "refii_mod_player_of(", "refii_mod_event_replica(", "refii_mod_forgive_crimes(", "refii_mod_discovered_crime(", "refii_mod_perceived_renown(" })
        {
            AssertThat(native.Contains(call, StringComparison.Ordinal)).OverrideFailureMessage($"{call} is not in ModApi.Helpers").IsTrue();
        }

        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        foreach (string verb in new[] { "player_for", "combat_state", "world_here", "replica", "forgive_crimes", "discovered_crime", "perceived_renown" })
        {
            AssertThat(Regex.IsMatch(godot, @"\b" + verb + @"\b")).OverrideFailureMessage($"'{verb}' is not in ModApi.GodotHelpers").IsTrue();
        }

        AssertThat(ModApi.PlayerAgnostic.Contains("NEVER ASSUME PLAYER ONE", StringComparison.Ordinal)).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = File.ReadAllText(Path.Combine(root, "src", "vfs", "include", "core", "refii_mod.h"));
        foreach (string call in new[] { "refii_mod_player_for(PPCContext* ctx, uint8_t* base, uint32_t entity)", "refii_mod_world_here(uint8_t* base)",
                     "refii_mod_forgive_crimes(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero)",
                     "refii_mod_discovered_crime(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero)" })
        {
            AssertThat(header.Contains("extern \"C\" int32_t " + call, StringComparison.Ordinal))
                .OverrideFailureMessage($"{call} is not declared in core/refii_mod.h").IsTrue();
        }
    }

    [TestCase]
    public void TheRouteIsTheCoreLuasAndSaveSafe()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        // The route (player two's boxes, questions, cameras and Player calls on player two's screen)
        // is defined once, in the core co-op Lua, and exposed as Refii.show_to_player; a question
        // waits through the frozen trampolines only (a saved coroutine resumes into them by name).
        string lua = File.ReadAllText(Path.Combine(root, "Godot", "src", "host", "CoopHeroLua.cs"));
        AssertThat(lua.Contains("\"Api.show_to_player\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("C.Call1 = C.Permanent(function(after, state, fn, ...) return after(state, fn(...)) end, \"Call1\")", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("C.Wait1 = C.Permanent(function(poll, state, yield) while true do local done, value = poll(state) if done then return value end yield() end end, \"Wait1\")", StringComparison.Ordinal)).IsTrue();
        AssertThat(Regex.Matches(lua, @"return C\.Call1\(C\.RouteUnpack, nil, C\.Wait1, C\.RouteAnswerPoll, out, coroutine\.yield\)").Count).IsEqual(2);

        string wire = File.ReadAllText(Path.Combine(root, "src", "vfs", "include", "core", "event_relay.h"));
        AssertThat(wire.Contains("ps.r=", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void RelayedEventsAgreeWithTheBus()
    {
        // The catalog marks exactly the kinds mod_events.cpp's relay table carries.
        foreach (string name in new[] { "MeleeSwing", "ItemUsed", "RollStarted", "HitLanded" })
        {
            AssertThat(ModApi.Find(name)!.IsRelayed).OverrideFailureMessage($"{name} is not marked relayed").IsTrue();
        }

        foreach (ModApiEvent e in ModApi.Events.Where(e => e.IsRelayed))
        {
            AssertThat(e.Relayed.StartsWith("RELAYED:", StringComparison.Ordinal)).IsTrue();
        }

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string bus = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_events.cpp"));
        string[] rows = Regex.Matches(bus, @"REFII_RELAY\((\w+), k(?:Player|World)\)")
            .Select(m => m.Groups[1].Value).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        AssertThat(rows).IsEqual(ModApi.Events.Where(e => e.IsRelayed).Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [TestCase]
    public void TheWheelSwitchedLockIsOnTheTickSurfaces()
    {
        // One primitive on the surfaces that run on the guest tick: a lock stated each tick to
        // switch by the mouse wheel. Souls Combat states it for its toggled lock.
        ModApiHelper native = ModApi.Helpers.Single(h => h.Signature.Contains("refii_mod_wheel_switches_target(base, hero)", StringComparison.Ordinal));
        ModApiHelper lua = ModApi.LuaHelpers.Single(h => h.Signature.Contains("Refii.wheel_switches_target(e)", StringComparison.Ordinal));
        AssertThat(native.Meaning).IsEqual(lua.Meaning);
        AssertThat(native.Meaning.Contains("CheckForBetterTargetInDirection", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Meaning.Contains("down the next target right, up the next left", StringComparison.Ordinal)).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = File.ReadAllText(Path.Combine(root, "src", "vfs", "include", "core", "refii_mod.h"));
        AssertThat(header.Contains("extern \"C\" void refii_mod_wheel_switches_target(uint8_t* base, uint32_t hero);", StringComparison.Ordinal)).IsTrue();
        string natives = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        AssertThat(natives.Contains("{\"wheel_switches_target\", &NativeWheelSwitchesTarget, 0}", StringComparison.Ordinal)).IsTrue();
        // Souls Combat's toggled lock, now Lua (mod/lockon.lua), states it each tick the lock holds.
        string souls = string.Concat(Directory.EnumerateFiles(Path.Combine(root, "mods-src", "souls_combat", "mod"), "*.lua").OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText));
        AssertThat(souls.Contains("Refii.wheel_switches_target(hero)", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheEntityTintIsOnEverySurface()
    {
        // One primitive, three surfaces: native, Lua and Godot all reach refii_mod_entity_tint, the
        // game's own vector tint setter (sub_82573770), and its untint.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        AssertThat(native.Contains("refii_mod_entity_tint(ctx, base, entity, r, g, b, a)", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_entity_untint(ctx, base, entity)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.entity_tint(e, r, g, b [, a])", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.entity_untint(e)", StringComparison.Ordinal)).IsTrue();
        ModApiHelper tint = ModApi.GodotHelpers.Single(h => h.Signature.StartsWith("entity_tint(", StringComparison.Ordinal));
        AssertThat(ModApi.GodotMethodNames(tint.Signature).ToArray()).IsEqual(new[] { "entity_tint", "entity_untint" });
        AssertThat(tint.Meaning.Contains("opacity is not the tint's", StringComparison.Ordinal)).IsTrue();
        AssertThat(Regex.IsMatch(godot, @"\bentity_untint\b")).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = File.ReadAllText(Path.Combine(root, "src", "vfs", "include", "core", "refii_mod.h"));
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_entity_tint(PPCContext* ctx, uint8_t* base, uint32_t entity, float r, float g, float b,", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_entity_untint(PPCContext* ctx, uint8_t* base, uint32_t entity);", StringComparison.Ordinal)).IsTrue();

        string natives = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        AssertThat(natives.Contains("{\"entity_tint\", &NativeEntityTint, 0}", StringComparison.Ordinal)).IsTrue();
        AssertThat(natives.Contains("{\"entity_untint\", &NativeEntityUntint, 0}", StringComparison.Ordinal)).IsTrue();

        // The setter is called by its TU1 address, so the disc build needs its row (same 48
        // instructions at 0x82548418).
        string rows = File.ReadAllText(Path.Combine(root, "versions", "Fable2", "guest_addresses.inc"));
        AssertThat(rows.Contains("{0x82573770u, 0x82548418u},", StringComparison.Ordinal)).IsTrue();
        string api = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_api.cpp"));
        AssertThat(api.Contains("kSetTintVector = 0x82573770u", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheGuestSideAdditionsAreOnEverySurface()
    {
        // Part H's guest-side items: the appearance setters beside the tint (H.1), the target lock
        // (H.3), statuses (H.7) and knockback/stagger (H.8) — native, Lua and Godot each name every
        // call, and every guest function called by its TU1 address has its disc row.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        string[] calls =
        {
            "entity_alpha", "entity_scale", "entity_saturation_mask", "entity_drawable",
            "lock_target", "release_lock", "switch_target",
            "apply_status", "clear_status",
            "knockback", "stagger",
        };
        foreach (string call in calls)
        {
            AssertThat(native.Contains("refii_mod_" + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("native " + call).IsTrue();
            AssertThat(lua.Contains("Refii." + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("lua " + call).IsTrue();
            AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(call))).OverrideFailureMessage("godot " + call).IsTrue();
        }
        // The reads are Lua natives and Godot queries.
        AssertThat(lua.Contains("Refii.locked_target(p)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.has_status(e, kind)", StringComparison.Ordinal)).IsTrue();
        AssertThat(godot.Contains("query locked_target P / has_status E KIND", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_locked_target(ctx, base, player)", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_has_status(base, entity, kind)", StringComparison.Ordinal)).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = File.ReadAllText(Path.Combine(root, "src", "vfs", "include", "core", "refii_mod.h"));
        string natives = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        string commands = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        foreach (string call in calls)
        {
            AssertThat(header.Contains("refii_mod_" + call + "(PPCContext* ctx, uint8_t* base, ", StringComparison.Ordinal)).OverrideFailureMessage("header " + call).IsTrue();
            AssertThat(Regex.IsMatch(natives, "\\{\"" + call + "\", &Native\\w+, 0\\}")).OverrideFailureMessage("natives " + call).IsTrue();
            AssertThat(commands.Contains("op == \"" + call + "\"", StringComparison.Ordinal)).OverrideFailureMessage("command " + call).IsTrue();
        }
        AssertThat(commands.Contains("what == \"locked_target\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("what == \"has_status\"", StringComparison.Ordinal)).IsTrue();

        // Every guest function the helpers call by TU1 address, with the disc address found by its
        // instruction pattern (same size on both builds).
        string rows = File.ReadAllText(Path.Combine(root, "versions", "Fable2", "guest_addresses.inc"));
        string[] expected =
        {
            "{0x82573220u, 0x82547EC8u},",  // SetAlpha
            "{0x82573078u, 0x82547D20u},",  // SetScale
            "{0x822B8188u, 0x822B7EA8u},",  // SaturationMask
            "{0x825725A8u, 0x82547250u},",  // SetAsDrawable
            "{0x8290F078u, 0x82455F10u},",  // SetForceTargetLock (the contract; the byte is stored directly)
            "{0x821E8B90u, 0x821E8870u},",  // the player handle's HasMode
            "{0x825C4958u, 0x824831E8u},",  // the hit record's constructor
            "{0x82275438u, 0x82275368u},",  // the hit's response-name setter
            "{0x823919C0u, 0x8237D608u},",  // the hit record's destructor
            "{0x827427B8u, 0x827178B8u},",  // remove CECElectrified
            "{0x82749F38u, 0x8271F038u},",  // remove CECInChaos
            "{0x827469A8u, 0x8271BAA8u},",  // remove CECOnFire
        };
        foreach (string row in expected)
        {
            AssertThat(rows.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage(row).IsTrue();
        }

        // Souls Combat's lock is the API's now, in Lua (mod/lockon.lua): no raw force-byte store of its own.
        string souls = string.Concat(Directory.EnumerateFiles(Path.Combine(root, "mods-src", "souls_combat", "mod"), "*.lua").OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText));
        AssertThat(souls.Contains("Refii.lock_target(", StringComparison.Ordinal)).IsTrue();
        AssertThat(souls.Contains("Refii.release_lock(", StringComparison.Ordinal)).IsTrue();
        AssertThat(souls.Contains("Refii.locked_target(", StringComparison.Ordinal)).IsTrue();
        AssertThat(souls.Contains("kForceLock", StringComparison.Ordinal)).IsFalse();
    }

    [TestCase]
    public void TheSoundAnimationAndOpinionSettersAreOnEverySurface()
    {
        // Part H's H.6 (sound through the game's own cue player and music box), H.9 (a named
        // animation as the shipped AI's scripted action) and H.10 (opinion and deeds through the
        // game's own setters) — native, Lua, Godot and the command lines each name every call, and
        // every guest function called by its TU1 address has its disc row.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        string[] calls =
        {
            "play_sound", "play_sound_at", "stop_sound", "play_music", "stop_music",
            "play_animation", "stop_animation",
            "modify_opinion", "add_deed",
        };
        foreach (string call in calls)
        {
            string nativeName = call == "play_sound" ? "play_sound_on" : call;
            AssertThat(native.Contains("refii_mod_" + nativeName + "(", StringComparison.Ordinal)).OverrideFailureMessage("native " + call).IsTrue();
            AssertThat(lua.Contains("Refii." + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("lua " + call).IsTrue();
            AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(call))).OverrideFailureMessage("godot " + call).IsTrue();
        }
        // Both players are principals: the opinion setters take the hero, never assume player one.
        AssertThat(native.Contains("refii_mod_modify_opinion(ctx, base, villager, hero, axis, delta)", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_add_deed(ctx, base, villager, hero, deed)", StringComparison.Ordinal)).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = File.ReadAllText(Path.Combine(root, "src", "vfs", "include", "core", "refii_mod.h"));
        string natives = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        string commands = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        foreach (string call in calls)
        {
            string nativeName = call == "play_sound" ? "play_sound_on" : call;
            AssertThat(header.Contains("refii_mod_" + nativeName + "(PPCContext* ctx, uint8_t* base, ", StringComparison.Ordinal)).OverrideFailureMessage("header " + call).IsTrue();
            AssertThat(Regex.IsMatch(natives, "\\{\"" + call + "\", &Native\\w+, 0\\}")).OverrideFailureMessage("natives " + call).IsTrue();
            AssertThat(commands.Contains("op == \"" + call + "\"", StringComparison.Ordinal)).OverrideFailureMessage("command " + call).IsTrue();
        }

        // The opinion setter and the deed poster are the hooked names (the events fire); the sound
        // bodies are called by TU1 address, each with its disc row (same instruction sequence,
        // only the relocated globals differ).
        string api = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_api.cpp"));
        AssertThat(api.Contains("rex_CECOpinionReaction_ModifyAxisValue(work, base);", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("refii::guest::Address(0x82279B80u)", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("Action.SetCurrentAction(e, a)", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("Action.FinishScriptedActions(e)", StringComparison.Ordinal)).IsTrue();
        string rows = File.ReadAllText(Path.Combine(root, "versions", "Fable2", "guest_addresses.inc"));
        string[] expected =
        {
            "{0x821D9608u, 0x821D9258u},",  // the cue player on a sound component (Sound.PlayEvent's body)
            "{0x821E0F48u, 0x821E0B08u},",  // the cue player on an emitter
            "{0x822671A8u, 0x82267168u},",  // the cue record's constructor
            "{0x82287DE0u, 0x822878C8u},",  // is the cue hash in a loaded bank
            "{0x82289A48u, 0x82289530u},",  // the cue record's destructor
            "{0x8255BB28u, 0x825204B0u},",  // SoundTools.PlayMusic
            "{0x8255BC20u, 0x825205A8u},",  // SoundTools.StopMusic
            "{0x829FDEF0u, 0x829F9128u},",  // stop an emitter's voices by tag
            "{0x829FD018u, 0x829F8250u},",  // the sound manager's emitter factory
            "{0x829FD168u, 0x829F83A0u},",  // the sound manager's emitter release
            "{0x82C05EF8u, 0x822ADE08u},",  // the voice handle's release
        };
        foreach (string row in expected)
        {
            AssertThat(rows.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage(row).IsTrue();
        }
    }

    [TestCase]
    public void GodotDeclaresASignalPerEvent()
    {
        AssertThat(string.Join(",", RefiiHost.SignalNames)).IsEqual(string.Join(",", ModApi.Events.Select(e => e.LuaName)));
    }

    [TestCase]
    public void TemplatesNameTheEvent()
    {
        ModApiEvent hit = ModApi.Find("hit")!;
        string native = ModApi.NativeTemplate(hit, "my_mod");
        AssertThat(native.Contains("REFII_MOD_ON(Hit, OnHit);")).IsTrue();
        AssertThat(native.Contains("refii::mods::HitEvent& e")).IsTrue();
        AssertThat(native.Contains("e.damage")).IsTrue();

        string lua = ModApi.LuaTemplate(hit);
        AssertThat(lua.StartsWith("Refii.on(\"hit\", function(e)")).IsTrue();
        AssertThat(lua.Contains("return true")).IsTrue();
        AssertThat(ModApi.LuaTemplate(ModApi.Find("Tick")!).Contains("return true")).IsFalse();
    }

    [TestCase]
    public void TheLightAndPartnerCallsAreOnEverySurface()
    {
        // Part H.11, H.12: a mod-placed light and the co-op message channel — one meaning each,
        // stated on native, Lua and Godot; the partner message is an event of the dense table.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature + " " + h.Meaning));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature + " " + h.Meaning));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature + " " + h.Meaning));
        foreach (string surface in new[] { native, lua, godot })
        {
            AssertThat(surface.Contains(ModApi.LightMeaning, StringComparison.Ordinal)).IsTrue();
            AssertThat(surface.Contains(ModApi.PartnerMeaning, StringComparison.Ordinal)).IsTrue();
        }

        AssertThat(native.Contains("refii_mod_light(entity, xyz, rgb, energy, range, options)", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_send_to_partner(mod, name, payload)", StringComparison.Ordinal)).IsTrue();
        // H.16: the spot form is one meaning with the omni's, stated on every surface's options.
        AssertThat(ModApi.LightMeaning.Contains("A SPOT when a cone is stated", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("direction, cone, attenuation for a spot", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("direction = {x, y, z}, cone = degrees, attenuation = n", StringComparison.Ordinal)).IsTrue();
        AssertThat(godot.Contains("options { offset, at, flicker, direction, cone, attenuation }", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.light(target, color, energy, range [, o])", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.send_to_partner(mod, name, payload)", StringComparison.Ordinal)).IsTrue();
        foreach (string name in new[] { "light", "light_remove", "lights", "send_to_partner" })
        {
            AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(name)))
                .OverrideFailureMessage($"'{name}' is not a Godot helper of the catalog").IsTrue();
        }

        ModApiEvent partner = ModApi.Find("partner_message")!;
        AssertThat(partner.Kind).IsEqual(93);
        AssertThat(string.Join(",", partner.Fields.Select(f => f.Name))).IsEqual("mod,name,payload,from");
        AssertThat(partner.Cancellable).IsFalse();
        AssertThat(partner.IsRelayed).IsFalse();  // the route carries it; it is the receiver's own event, not a replica

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = File.ReadAllText(Path.Combine(root, "src", "vfs", "include", "core", "refii_mod.h"));
        foreach (string declared in new[]
                 {
                     "extern \"C\" uint32_t refii_mod_light(uint32_t entity, const float* xyz, const float* rgb, float energy, float range,",
                     "extern \"C\" void refii_mod_light_remove(uint32_t id);",
                     // The spot fields trail the options (a native mod is rebuilt from source, so a trailing field is safe).
                     "  float direction[3];\n  float cone;\n",
                     "  float attenuation;\n} refii_mod_light_options;",
                     "extern \"C\" int32_t refii_mod_send_to_partner(const char* mod, const char* name, const char* payload);",
                     "#define REFII_MOD_PARTNER_PAYLOAD_MAX 256",
                 })
        {
            AssertThat(header.Contains(declared, StringComparison.Ordinal)).OverrideFailureMessage($"{declared} is not in core/refii_mod.h").IsTrue();
        }

        string natives = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        foreach (string row in new[] { "{\"_light\", &NativeLight, 0}", "{\"light_remove\", &NativeLightRemove, 0}", "{\"send_to_partner\", &NativeSendToPartner, 0}" })
        {
            AssertThat(natives.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage($"{row} is not registered").IsTrue();
        }

        string preamble = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_ModLua.cs"));
        // The Lua form carries the spot fields to the native on both branches (handle and point):
        // nineteen arguments, direction then cone then attenuation after flicker.
        AssertThat(preamble.Contains("\"\\tlocal d = o.direction or {}\\n\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(Regex.Matches(preamble, @"o\.flicker or 0,\\n"" \+\s+""(\\t)+d\[1\] or d\.x or 0, d\[2\] or d\.y or 0, d\[3\] or d\.z or 0, o\.cone or 0, o\.attenuation or 0\)").Count).IsEqual(2);
        AssertThat(natives.Contains("options.direction[0] = static_cast<float>(lua.Number(15));", StringComparison.Ordinal)).IsTrue();
        AssertThat(natives.Contains("options.cone = static_cast<float>(lua.Number(18));", StringComparison.Ordinal)).IsTrue();
        AssertThat(natives.Contains("options.attenuation = static_cast<float>(lua.Number(19));", StringComparison.Ordinal)).IsTrue();
        AssertThat(preamble.Contains("function Refii.light(target, color, energy, range, o)", StringComparison.Ordinal)).IsTrue();
        string api = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_api.cpp"));
        foreach (string op in new[] { "op='light'", "op='light_off'", "dx=%.9g,dy=%.9g,dz=%.9g,cone=%.9g,atten=%.9g", "\"pm:\" + std::to_string(owned)" })
        {
            AssertThat(api.Contains(op, StringComparison.Ordinal)).OverrideFailureMessage($"{op} is never posted").IsTrue();
        }

        // The route's mod lane: the receiver keeps pm: lines apart and the bus drains them on the tick.
        string relay = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "core", "event_relay.cpp"));
        AssertThat(relay.Contains("constexpr std::string_view kModLane = \"pm:\";", StringComparison.Ordinal)).IsTrue();
        AssertThat(relay.Contains("extern \"C\" int32_t refii_bridge_route_take_mod(char* out, uint32_t capacity)", StringComparison.Ordinal)).IsTrue();
        string bus = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_events.cpp"));
        AssertThat(bus.Contains("DrainPartnerMessages(ctx, base);", StringComparison.Ordinal)).IsTrue();
        string commands = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("op == \"partner_send\"", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheWheelSwitchedLockIsOnEverySurface()
    {
        // H.16: Refii.wheel_switches_target restates itself each Lua tick; a Godot script runs off
        // the tick, so RefiiHost.wheel_switches_target(handle, on) is a STICKY command line the tick
        // restates (mod_commands.cpp RestateWheelSwitches) until off or the lock is released.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        AssertThat(native.Contains("refii_mod_wheel_switches_target(base, hero)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.wheel_switches_target(e)", StringComparison.Ordinal)).IsTrue();
        AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains("wheel_switches_target"))).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string commands = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("op == \"wheel_switches_target\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("RestateWheelSwitches(work, base);", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("refii_mod_locked_target(&ctx, base, player) == 0u", StringComparison.Ordinal)).IsTrue();
        string host = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs"));
        AssertThat(host.Contains("Post($\"wheel_switches_target {handle} {(on ? 1 : 0)}\")", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheExamplesGoNativeFree()
    {
        // Part H.15: what the two example mods still did by raw memory or by a hook of their own
        // is a primitive or an event on every surface — the speed override, the action
        // commitment, the look-at button, the slot table, a live cast's parts and its lifecycle.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        foreach (string call in new[] { "set_speed_multiplier", "clear_speed_multiplier", "allow_action_interrupt" })
        {
            AssertThat(native.Contains("refii_mod_" + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("native " + call).IsTrue();
            AssertThat(lua.Contains("Refii." + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("lua " + call).IsTrue();
            AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(call))).OverrideFailureMessage("godot " + call).IsTrue();
        }
        foreach (string read in new[]
                 {
                     "refii_mod_speed_multiplier(base, player, float* out)", "refii_mod_current_action(base, entity)",
                     "refii_mod_action_elapsed(ctx, base, action, float* out)", "refii_mod_action_interruptible(base, action)",
                     "refii_mod_entity_living(base, entity)", "refii_mod_spell_in_slot(base, player, slot)",
                     "refii_mod_spell_parts(ctx, base, serial, refii_mod_cast_parts* out)",
                 })
        {
            AssertThat(native.Contains(read, StringComparison.Ordinal)).OverrideFailureMessage("native " + read).IsTrue();
        }
        foreach (string read in new[]
                 {
                     "Refii.speed_multiplier(p) -> overridden, factor", "Refii.current_action(e) -> action", "Refii.action_elapsed(a) -> seconds",
                     "Refii.action_interruptible(a)", "Refii.living(e)", "Refii.spell_in_slot(p, slot) -> type", "Refii.spell_parts(serial) -> table",
                 })
        {
            AssertThat(lua.Contains(read, StringComparison.Ordinal)).OverrideFailureMessage("lua " + read).IsTrue();
        }
        foreach (string query in new[] { "query speed_multiplier P / current_action E", "query living E / spell_in_slot P SLOT", "query spell_parts SERIAL" })
        {
            AssertThat(godot.Contains(query, StringComparison.Ordinal)).OverrideFailureMessage("godot " + query).IsTrue();
        }

        ModApiEvent lookAt = ModApi.Find("look_at_button")!;
        AssertThat(lookAt.Kind).IsEqual(94);
        AssertThat(string.Join(",", lookAt.Fields.Select(f => f.Name))).IsEqual("hero,player,down,component");
        AssertThat(lookAt.Fields.Single(f => f.Name == "down").Writable).IsTrue();
        AssertThat(lookAt.Cancellable).IsTrue();
        ModApiEvent started = ModApi.Find("vortex_started")!;
        AssertThat(started.Kind).IsEqual(95);
        AssertThat(string.Join(",", started.Fields.Select(f => f.Name))).IsEqual("vortex,entity,caster,caster_player,target,serial,level,x,y,z");
        AssertThat(started.Cancellable).IsFalse();
        ModApiEvent ended = ModApi.Find("vortex_ended")!;
        AssertThat(ended.Kind).IsEqual(96);
        AssertThat(string.Join(",", ended.Fields.Select(f => f.Name))).IsEqual("vortex,entity,caster,caster_player,serial,x,y,z");
        ModApiEvent impact = ModApi.Find("fireball_impact")!;
        AssertThat(impact.Kind).IsEqual(97);
        AssertThat(string.Join(",", impact.Fields.Select(f => f.Name))).IsEqual("spell,serial,level,caster,caster_player,struck,struck_player,x,y,z,nx,ny,nz,detonated,hits");
        AssertThat(impact.Cancellable).IsFalse();
        AssertThat(ModApi.Events.Count).IsEqual(100);  // 98 paused, 99 start_game (H.16)

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = File.ReadAllText(Path.Combine(root, "src", "vfs", "include", "core", "refii_mod.h"));
        foreach (string declared in new[]
                 {
                     "extern \"C\" int32_t refii_mod_set_speed_multiplier(uint8_t* base, int32_t player, float factor);",
                     "extern \"C\" int32_t refii_mod_clear_speed_multiplier(uint8_t* base, int32_t player);",
                     "extern \"C\" int32_t refii_mod_speed_multiplier(uint8_t* base, int32_t player, float* out);",
                     "extern \"C\" uint32_t refii_mod_current_action(uint8_t* base, uint32_t entity);",
                     "extern \"C\" int32_t refii_mod_action_elapsed(PPCContext* ctx, uint8_t* base, uint32_t action, float* out);",
                     "extern \"C\" int32_t refii_mod_action_interruptible(uint8_t* base, uint32_t action);",
                     "extern \"C\" int32_t refii_mod_allow_action_interrupt(uint8_t* base, uint32_t action);",
                     "extern \"C\" int32_t refii_mod_entity_living(uint8_t* base, uint32_t entity);",
                     "extern \"C\" int32_t refii_mod_spell_in_slot(uint8_t* base, int32_t player, int32_t slot);",
                     "struct refii_mod_cast_parts {",
                     "extern \"C\" int32_t refii_mod_spell_parts(PPCContext* ctx, uint8_t* base, uint32_t serial, refii_mod_cast_parts* out);",
                 })
        {
            AssertThat(header.Contains(declared, StringComparison.Ordinal)).OverrideFailureMessage($"{declared} is not in core/refii_mod.h").IsTrue();
        }

        string natives = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        foreach (string name in new[]
                 {
                     "set_speed_multiplier", "clear_speed_multiplier", "speed_multiplier", "current_action", "action_elapsed",
                     "action_interruptible", "allow_action_interrupt", "living", "spell_in_slot", "spell_parts",
                 })
        {
            AssertThat(Regex.IsMatch(natives, "\\{\"" + name + "\", &Native\\w+, 0\\}")).OverrideFailureMessage("natives " + name).IsTrue();
        }
        string commands = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        foreach (string op in new[] { "set_speed_multiplier", "clear_speed_multiplier", "allow_action_interrupt" })
        {
            AssertThat(commands.Contains("op == \"" + op + "\"", StringComparison.Ordinal)).OverrideFailureMessage("command " + op).IsTrue();
        }
        foreach (string what in new[] { "speed_multiplier", "current_action", "living", "spell_in_slot", "spell_parts" })
        {
            AssertThat(commands.Contains("what == \"" + what + "\"", StringComparison.Ordinal)).OverrideFailureMessage("query " + what).IsTrue();
        }

        // The four seams are refii's now: hooked once in the bus, the mods subscribe.
        string bus = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_events.cpp"));
        foreach (string seam in new[] { "rex_CPlayer_SetLookAtButtonDown", "rex_VortexComponent_Init", "rex_VortexComponent_Shutdown", "rex_TargetedFireball_Impact" })
        {
            AssertThat(bus.Contains("REX_EXTERN(" + seam + ")", StringComparison.Ordinal)).OverrideFailureMessage("bus " + seam).IsTrue();
        }
        AssertThat(bus.Contains("extern \"C\" int32_t refii_mod_spell_parts(", StringComparison.Ordinal)).IsTrue();
        // Souls Combat is pure Lua now (SoulsCombatLuaTests): the look-at seam reaches it as an event.
        AssertThat(Directory.Exists(Path.Combine(root, "mods-src", "souls_combat", "native"))).IsFalse();
        // Souls Combat's Lua is one file per rule (mod/*.lua, composed through require): read as one.
        string souls = string.Concat(Directory.EnumerateFiles(Path.Combine(root, "mods-src", "souls_combat", "mod"), "*.lua").OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText));
        AssertThat(souls.Contains("Refii.on(\"look_at_button\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(souls.Contains("REX_EXTERN(", StringComparison.Ordinal)).IsFalse();
        // Spell Weaving is pure Lua now (SpellWeavingLuaTests): the three seams reach it as events.
        AssertThat(Directory.Exists(Path.Combine(root, "mods-src", "spell_weaving", "native"))).IsFalse();
        string weaving = File.ReadAllText(Path.Combine(root, "mods-src", "spell_weaving", "mod", "init.lua"));
        foreach (string on in new[] { "Refii.on(\"vortex_started\"", "Refii.on(\"vortex_ended\"", "Refii.on(\"fireball_impact\"", "Refii.spell_parts(" })
        {
            AssertThat(weaving.Contains(on, StringComparison.Ordinal)).OverrideFailureMessage(on).IsTrue();
        }

        // Every guest function the helpers cite as the contract, with the disc address found by
        // its instruction pattern (same size on both builds).
        string rows = File.ReadAllText(Path.Combine(root, "versions", "Fable2", "guest_addresses.inc"));
        foreach (string row in new[]
                 {
                     "{0x823DFF78u, 0x823BE7D0u},",  // Player.SetPlayerMovementSpeedOverridden (stb r4,0x1E(r3))
                     "{0x8283ECC8u, 0x827EDB38u},",  // Player.SetOverriddenMovementSpeedMultiplier (stfs f1,0xCC(r3))
                     "{0x823E3258u, 0x823C1C20u},",  // Player.GetOverriddenMovementSpeedMultiplier
                     "{0x822B1F78u, 0x822B1DB0u},",  // the locomotion update
                     "{0x82709938u, 0x826DF198u},",  // the action's animation-event handler
                     "{0x822CB150u, 0x822CB140u},",  // the interrupt rule
                     "{0x822A4888u, 0x822A4648u},",  // the manager's start (current action -> +0x10)
                     "{0x82709FD8u, 0x826DF838u},",  // the action's Stop
                     "{0x8226D418u, 0x8226D3D8u},",  // the action's clock, slot 7
                     "{0x823A3148u, 0x82431700u},",  // CECCreature constructor
                     "{0x82844950u, 0x8277B2B0u},",  // the target selector
                     "{0x82740568u, 0x8294A2F8u},",  // the fireball's shot getter
                     "{0x825A2188u, 0x82552F70u},",  // Blades targeted Start
                     "{0x825A2720u, 0x82553508u},",  // Blades area Start
                     "{0x825A37E0u, 0x825545C8u},",  // Blades record add
                     "{0x82759928u, 0x82731308u},",  // the vortex update (twister point)
                     "{0x828E5020u, 0x828BE468u},",  // the spell base constructor
                 })
        {
            AssertThat(rows.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage(row).IsTrue();
        }
    }

    [TestCase]
    public void TheHudCallsAreOnEverySurface()
    {
        // Part H.2, H.4, H.5: a play at a model helper, world markers, the per-player fade and the
        // HUD meters — one meaning each, stated on native, Lua and Godot.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature + " " + h.Meaning));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature + " " + h.Meaning));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature + " " + h.Meaning));
        foreach (string surface in new[] { native, lua, godot })
        {
            AssertThat(surface.Contains("at (a model helper", StringComparison.Ordinal) || surface.Contains("at = \"Weapon.Weapon.Top\"", StringComparison.Ordinal)).IsTrue();
            AssertThat(surface.Contains(ModApi.MarkMeaning, StringComparison.Ordinal)).IsTrue();
            AssertThat(surface.Contains(ModApi.FadeMeaning, StringComparison.Ordinal)).IsTrue();
            AssertThat(surface.Contains(ModApi.MeterMeaning, StringComparison.Ordinal)).IsTrue();
        }

        AssertThat(native.Contains("refii_mod_mark(player, entity, xyz, icon, text)", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_fade(player, to_black, seconds)", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_meter(player, id, label, value, max, style)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.mark(target, icon, text [, p])", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.fade(p, dir [, seconds])", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.meter(id, label, value, max [, p [, style]])", StringComparison.Ordinal)).IsTrue();
        foreach (string name in new[] { "mark", "unmark", "markers", "fade", "fade_state", "meter", "meter_remove", "meters" })
        {
            AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(name)))
                .OverrideFailureMessage($"'{name}' is not a Godot helper of the catalog").IsTrue();
        }

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = File.ReadAllText(Path.Combine(root, "src", "vfs", "include", "core", "refii_mod.h"));
        foreach (string declared in new[]
                 {
                     "const char* at;",
                     "extern \"C\" uint32_t refii_mod_play_fx_at(const char* name, uint32_t entity, const char* helper,",
                     "extern \"C\" uint32_t refii_mod_mark(int32_t player, uint32_t entity, const float* xyz, const char* icon,",
                     "extern \"C\" void refii_mod_unmark(uint32_t id);",
                     "extern \"C\" int32_t refii_mod_fade(int32_t player, int32_t to_black, float seconds);",
                     "extern \"C\" int32_t refii_mod_meter(int32_t player, const char* id, const char* label, float value, float max,",
                     "extern \"C\" int32_t refii_mod_meter_remove(const char* id);",
                 })
        {
            AssertThat(header.Contains(declared, StringComparison.Ordinal)).OverrideFailureMessage($"{declared} is not in core/refii_mod.h").IsTrue();
        }

        string natives = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        foreach (string row in new[] { "{\"_mark\", &NativeMark, 0}", "{\"unmark\", &NativeUnmark, 0}", "{\"fade\", &NativeFade, 0}", "{\"meter\", &NativeMeter, 0}", "{\"meter_remove\", &NativeMeterRemove, 0}" })
        {
            AssertThat(natives.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage($"{row} is not registered").IsTrue();
        }

        // The Lua preamble unpacks `at` as the sixteenth _play_fx argument and builds mark from a handle or a point.
        string preamble = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_ModLua.cs"));
        AssertThat(preamble.Contains("f[3] or f.z or 0, o.at or ''", StringComparison.Ordinal)).IsTrue();
        AssertThat(preamble.Contains("function Refii.mark(target, icon, text, p)", StringComparison.Ordinal)).IsTrue();
        string api = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_api.cpp"));
        AssertThat(api.Contains(",at='%s'\"", StringComparison.Ordinal)).IsTrue();
        foreach (string op in new[] { "op='mark'", "op='unmark'", "op='fade'", "op='meter'", "op='meter_remove'" })
        {
            AssertThat(api.Contains(op, StringComparison.Ordinal)).OverrideFailureMessage($"{op} is never posted").IsTrue();
        }
    }

    [TestCase]
    public void TheNpcTimeAndWeatherCallsAreOnEverySurface()
    {
        // Part H's H.13 (NPC orders through the quests' own entries in the game VM) and H.14 (the
        // game clock's own setter, the quests' environment-theme blend) — native, Lua, Godot and
        // the command lines each name every call, one meaning per item on every surface, the read
        // backs as a Lua native and a Godot query, and the one guest function called by its TU1
        // address has its disc row.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature + " " + h.Meaning));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature + " " + h.Meaning));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature + " " + h.Meaning));
        string[] calls =
        {
            "npc_follow", "npc_go_to", "npc_flee", "npc_attack", "npc_set_hostile", "npc_stop",
            "set_time_of_day", "set_weather",
        };
        foreach (string call in calls)
        {
            AssertThat(native.Contains("refii_mod_" + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("native " + call).IsTrue();
            AssertThat(lua.Contains("Refii." + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("lua " + call).IsTrue();
            AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(call))).OverrideFailureMessage("godot " + call).IsTrue();
        }
        foreach (string surface in new[] { native, lua, godot })
        {
            AssertThat(surface.Contains(ModApi.NpcMeaning, StringComparison.Ordinal)).IsTrue();
            AssertThat(surface.Contains(ModApi.TimeWeatherMeaning, StringComparison.Ordinal)).IsTrue();
        }
        AssertThat(lua.Contains("Refii.time_of_day() -> hours", StringComparison.Ordinal)).IsTrue();
        AssertThat(godot.Contains("query time_of_day", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_time_of_day(base, hours)", StringComparison.Ordinal)).IsTrue();
        // Both players are principals: hostility is against an entity (a hero is its own), never a player number.
        AssertThat(native.Contains("refii_mod_npc_set_hostile(ctx, base, entity, other, on)", StringComparison.Ordinal)).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = File.ReadAllText(Path.Combine(root, "src", "vfs", "include", "core", "refii_mod.h"));
        string natives = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        string commands = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        foreach (string call in calls)
        {
            AssertThat(header.Contains("refii_mod_" + call + "(PPCContext* ctx, uint8_t* base, ", StringComparison.Ordinal)).OverrideFailureMessage("header " + call).IsTrue();
            AssertThat(Regex.IsMatch(natives, "\\{\"" + call + "\", &Native\\w+, 0\\}")).OverrideFailureMessage("natives " + call).IsTrue();
            AssertThat(commands.Contains("op == \"" + call + "\"", StringComparison.Ordinal)).OverrideFailureMessage("command " + call).IsTrue();
        }
        AssertThat(header.Contains("refii_mod_time_of_day(uint8_t* base, float* hours)", StringComparison.Ordinal)).IsTrue();
        AssertThat(Regex.IsMatch(natives, "\\{\"time_of_day\", &NativeTimeOfDay, 0\\}")).IsTrue();
        AssertThat(commands.Contains("what == \"time_of_day\"", StringComparison.Ordinal)).IsTrue();

        // The orders are the shipped scripts' own calls, run in the game VM; the clock's setter is
        // called by TU1 address with its disc row.
        string api = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_api.cpp"));
        foreach (string entry in new[]
                 {
                     "Follow.FollowEntity(e, t, %.3f)",
                     "ScriptFunction.StartScriptControlledMovement(e, CVector3(%.3f, %.3f, %.3f), false)",
                     "Navigation.FleeFromEntity(e, t, ENavigationSpeed.NAV_SPEED_SPRINT, true)",
                     "CombatRegister:SetScriptTarget(e, t, true, false)",
                     "Faction.AddTemporaryEntityRelationship(e, t, EFactionStatus.FACTION_STATUS_ENEMY)",
                     "Faction.RemoveTemporaryEntityRelationship(e, t)",
                     "Follow.StopFollowing(e)",
                     "ScriptFunction.StopScriptControlledMovement(e) CombatRegister:ClearScriptTarget(e)",
                     "EnvironmentTheme.BlendToEnvironmentTheme(\\\"%s\\\", %.4f, %.4f)",
                     "refii::guest::Address(kSetTimeOfDayAbsolute)",
                 })
        {
            AssertThat(api.Contains(entry, StringComparison.Ordinal)).OverrideFailureMessage(entry).IsTrue();
        }
        string rows = File.ReadAllText(Path.Combine(root, "versions", "Fable2", "guest_addresses.inc"));
        AssertThat(rows.Contains("{0x82515AA0u, 0x8249A790u},", StringComparison.Ordinal)).IsTrue();  // Timing.SetTimeOfDayAbsolute's body
    }

    [TestCase]
    public void TheLuaSurfaceMatchesNativeForTheExamples()
    {
        // The seven places the two pure-Lua ports stopped at (hard_mods_plan H.15): each is closed in
        // core, on every surface, and both mods' Lua stands on it.
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature + " " + h.Meaning));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        // 1 + 2: a position cast from Lua and Godot, and the cast's serial.
        AssertThat(lua.Contains("Refii.cast_spell(caster, type, targeted, target, level [, pos [, dir]]) -> spell, serial", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.spell_serial(spell) -> serial", StringComparison.Ordinal)).IsTrue();
        AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains("cast_spell_at"))).IsTrue();
        AssertThat(godot.Contains("cast_spell_at(caster, type, targeted, target, level, pos, dir)", StringComparison.Ordinal)).IsTrue();
        // 2: events raised inside a running chunk are deferred, not dropped.
        AssertThat(ModApi.LuaHelpers.Single(h => h.Signature.StartsWith("Refii.on(", StringComparison.Ordinal)).Meaning.Contains("e.deferred = true", StringComparison.Ordinal)).IsTrue();
        // 5: the action handle on both events, no longer native-only.
        foreach (string name in new[] { "melee_swing", "anim_event" })
        {
            ModApiField action = ModApi.Find(name)!.Fields.Single(f => f.Name == "action");
            AssertThat(action.Meaning.Contains("(native", StringComparison.Ordinal)).OverrideFailureMessage(name + " action is still native-only").IsFalse();
            AssertThat(action.Meaning.Contains("handle", StringComparison.Ordinal)).IsTrue();
        }
        // 3 + 7 + 4: the charge level every frame, the tick every guest tick.
        AssertThat(ModApi.Find("spell_charge_level")!.Summary.Contains("every surface alike", StringComparison.Ordinal)).IsTrue();
        AssertThat(ModApi.Find("spell_charge_level")!.Summary.Contains("verdict holds between", StringComparison.Ordinal)).IsFalse();
        AssertThat(ModApi.Find("tick")!.Summary.Contains("every guest tick", StringComparison.Ordinal)).IsTrue();
        AssertThat(ModApi.Find("tick")!.Summary.Contains("ten times", StringComparison.Ordinal)).IsFalse();
        // 6: the wheel statement cannot lapse under that cadence.
        AssertThat(ModApi.LuaHelpers.Single(h => h.Signature.Contains("Refii.wheel_switches_target(e)", StringComparison.Ordinal)).Meaning.Contains("never lapses", StringComparison.Ordinal)).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        // The preamble's wrapper (read from the source: the composed preamble reads mod settings
        // through Godot's FileAccess, a native crash in the test host).
        string preamble = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_ModLua.cs"));
        AssertThat(preamble.Contains("function Refii.cast_spell(caster, spell_type, targeted, target, level, pos, dir)", StringComparison.Ordinal)).IsTrue();
        AssertThat(preamble.Contains("return Refii._cast_spell(caster, spell_type, t, target or 0, level or 0, pos ~= nil and 1 or 0,", StringComparison.Ordinal)).IsTrue();
        string natives = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        AssertThat(natives.Contains("{\"_cast_spell\", &NativeCastSpell, 0}", StringComparison.Ordinal)).IsTrue();
        AssertThat(natives.Contains("{\"spell_serial\", &NativeSpellSerial, 0}", StringComparison.Ordinal)).IsTrue();
        AssertThat(natives.Contains("{\"cast_spell\", &NativeCastSpell, 0}", StringComparison.Ordinal)).IsFalse();
        string commands = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("w.size() >= 9 ? pos : nullptr", StringComparison.Ordinal)).IsTrue();
        string bus = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_events.cpp"));
        AssertThat(bus.Contains("kLuaTickPeriod", StringComparison.Ordinal)).OverrideFailureMessage("the Lua tick is throttled again").IsFalse();
        AssertThat(bus.Contains("last_verdict", StringComparison.Ordinal)).OverrideFailureMessage("the charge-level memo is back").IsFalse();
        AssertThat(bus.Contains("struct LuaDeferred", StringComparison.Ordinal)).IsTrue();
        // 2, the live half: a writable event raised inside a chunk nests once, live; the cancel is
        // per depth; the drain waits for the outermost chunk; the gate lists exactly the kinds a
        // handler can write back to.
        AssertThat(bus.Contains("constexpr int kLuaMaxDepth = 2;", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("if (g_lua_depth >= kLuaMaxDepth) {", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("const bool nested_live = g_lua_depth > 0 && HasWritableFields(kind) && g_lua_depth < kLuaMaxDepth;", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("const bool outer_cancelled = g_lua_cancelled;", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("g_lua_cancelled = outer_cancelled;", StringComparison.Ordinal)).IsTrue();
        AssertThat(Regex.IsMatch(bus, @"if \(g_lua_depth == 0\) \{\s*DrainLuaDeferred\(ctx, base\);")).IsTrue();
        AssertThat(bus.Contains("g_lua_running", StringComparison.Ordinal)).IsFalse();
        string gate = Regex.Match(bus, @"bool HasWritableFields\(int32_t kind\) \{.*?\n\}", RegexOptions.Singleline).Value;
        string writer = Regex.Match(bus, @"bool WriteEventField\(int32_t kind, void\* event, const char\* field, double value\) \{.*?\n\}", RegexOptions.Singleline).Value;
        AssertThat(gate.Length > 0 && writer.Length > 0).IsTrue();
        string[] gated = Regex.Matches(gate, @"case (REFII_EVENT_\w+):").Select(m => m.Groups[1].Value).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        string[] written = Regex.Matches(writer, @"case (REFII_EVENT_\w+):").Select(m => m.Groups[1].Value).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
        AssertThat(gated).IsEqual(written);
        // ... and those are the catalog's writable kinds (by the header's numbering).
        string header = File.ReadAllText(Path.Combine(root, "src", "vfs", "include", "core", "refii_mod_events.h"));
        var kinds = Regex.Matches(header, @"(REFII_EVENT_\w+) = (\d+),").ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value));
        int[] gatedKinds = gated.Select(n => kinds[n]).OrderBy(k => k).ToArray();
        int[] catalogKinds = ModApi.WritableFields.Select(w => w.Kind).Distinct().OrderBy(k => k).ToArray();
        AssertThat(gatedKinds).IsEqual(catalogKinds);
        AssertThat(bus.Contains("\"entity=%u,player=%d,action=%u,action_type=%d,action_name='%s'\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("name='%s',action=%u,action_type=%d,action_name='%s'\"", StringComparison.Ordinal)).IsTrue();
        string mouse = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mouse_look.cpp"));
        AssertThat(mouse.Contains("constexpr uint32_t kWheelStatementTicks = 2u;", StringComparison.Ordinal)).IsTrue();
        // Both mods stand on the closed items.
        string weaving = File.ReadAllText(Path.Combine(root, "mods-src", "spell_weaving", "mod", "init.lua"));
        AssertThat(weaving.Contains("local spell, serial = Refii.cast_spell(hero, cast.type, true, cast.victim, cast.level, start, facing)", StringComparison.Ordinal)).IsTrue();
        AssertThat(weaving.Contains("spell_parts(serial).spell", StringComparison.Ordinal)).IsFalse();
        AssertThat(weaving.Contains("partner_casts", StringComparison.Ordinal)).IsFalse();
        AssertThat(weaving.Contains("kChargeHoldSeconds = 0.3", StringComparison.Ordinal)).IsTrue();
        // The bolt landed inside the partner's own factory call is scaled live, as the native's OnHit did.
        AssertThat(weaving.Contains("casting = { type = cast.type, hero = hero }", StringComparison.Ordinal)).IsTrue();
        AssertThat(weaving.Contains("if e.spell_type ~= casting.type or e.attacker ~= casting.hero or e.spell_serial == nil or e.spell_serial == 0 then", StringComparison.Ordinal)).IsTrue();
        string souls = string.Concat(Directory.EnumerateFiles(Path.Combine(root, "mods-src", "souls_combat", "mod"), "*.lua").OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText));
        AssertThat(souls.Contains("S.CommittedStrike[p] = e.action", StringComparison.Ordinal)).IsTrue();
        AssertThat(souls.Contains("if strike == nil or e.action ~= strike then", StringComparison.Ordinal)).IsTrue();
        AssertThat(souls.Contains("ten times a second", StringComparison.Ordinal)).IsFalse();
    }

    [TestCase]
    public void TheActionKindThePauseAndTheFrontEndAreOnEverySurface()
    {
        // Part H.16: an action's kind (its definition is a script table; its Type is
        // EScriptableAction), the game's pause state as an event, and the front end's own surface.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        foreach (string entry in new[]
                 {
                     "refii_mod_action_type(base, action)", "refii_mod_action_name(base, action, char* out, size)",
                     "refii_mod_scriptable_action_name(type)", "refii_mod_signed_in_user(base)", "refii_mod_save_count(ctx, base)",
                     "refii_mod_frontend_screen(ctx, base, char* out, size)",
                 })
        {
            AssertThat(native.Contains(entry, StringComparison.Ordinal)).OverrideFailureMessage(entry).IsTrue();
        }
        foreach (string entry in new[] { "Refii.action_type(a)", "Refii.action_name(a)", "Refii.signed_in()", "Refii.save_count()", "Refii.screen()" })
        {
            AssertThat(lua.Contains(entry, StringComparison.Ordinal)).OverrideFailureMessage(entry).IsTrue();
        }
        AssertThat(godot.Contains("query action A / frontend", StringComparison.Ordinal)).IsTrue();

        // The two events, their kinds (98 and 99: the last two of the hundred; the masks' capacity
        // is TheMasksHoldEveryKindOnEverySurface's), and the action kind on the two action events.
        ModApiEvent paused = ModApi.Events.Single(e => e.Name == "Paused");
        AssertThat(paused.Kind).IsEqual(98);
        AssertThat(paused.LuaName).IsEqual("paused");
        AssertThat(paused.Fields.Select(f => f.Name).ToArray()).IsEqual(new[] { "paused", "mode", "player", "screen" });
        AssertThat(paused.SeamFunctions.Contains("rex_lhGameFlowGuiMode_Apply")).IsTrue();
        AssertThat(paused.SeamFunctions.Contains("rex_lhGameFlowGuiMode_Remove")).IsTrue();
        ModApiEvent start = ModApi.Events.Single(e => e.Name == "StartGame");
        AssertThat(start.Kind).IsEqual(99);
        AssertThat(start.Fields.Select(f => f.Name).ToArray()).IsEqual(new[] { "save", "level", "new_game", "player" });
        AssertThat(start.SeamFunctions.Contains("rex_lhFrontEnd_SetStartGameTarget")).IsTrue();
        AssertThat(ModApi.Events.Count).IsEqual(100);
        foreach (string name in new[] { "MeleeSwing", "AnimEvent" })
        {
            ModApiEvent e = ModApi.Events.Single(x => x.Name == name);
            AssertThat(e.Fields.Any(f => f.Name == "action_type")).OverrideFailureMessage(name + ".action_type").IsTrue();
            AssertThat(e.Fields.Any(f => f.Name == "action_name")).OverrideFailureMessage(name + ".action_name").IsTrue();
        }

        // The front-end surface: every native the payload registers in the Gameface state is under
        // Refii in the preamble, and the doc's front-end table names each call and each event.
        foreach (string entry in new[] { "Refii.hash = __refii_hash", "Refii.signed_in = __refii_signed_in", "Refii.save_count = __refii_save_count", "Refii.screen = __refii_screen" })
        {
            AssertThat(ModLua.FrontendNatives.Contains(entry, StringComparison.Ordinal)).OverrideFailureMessage(entry).IsTrue();
        }
        if (RepoRoot() is not { } root)
        {
            return;
        }

        string doc = File.ReadAllText(Path.Combine(root, "docs", "mod_api.md"));
        int table = doc.IndexOf("### The front-end surface", StringComparison.Ordinal);
        AssertThat(table >= 0).IsTrue();
        string section = doc[table..];
        foreach (string call in new[]
                 {
                     "`Refii.hash(name)`", "`Refii.setting(key, default)`", "`Refii.log(text)`", "`Refii.publish(key, n)`", "`Refii.value(key)`",
                     "`Refii.notice(", "`Refii.message_box(", "`Refii.menu_box(", "`Refii.open_menu(", "`Refii.menu_player()`",
                     "`Refii.signed_in()`", "`Refii.save_count()`", "`Refii.screen()`", "`start_game`", "`mod_menu_row`", "`mod_message_box`",
                     "`mod_notice`", "`mod_menu_box`",
                 })
        {
            AssertThat(section.Contains(call, StringComparison.Ordinal)).OverrideFailureMessage("front-end table: " + call).IsTrue();
        }
        AssertThat(doc.Contains("A pause/unpause event: no single function was found", StringComparison.Ordinal)).IsFalse();

        // The seams: the three new hooks named in both builds, the type reader's row.
        foreach (string build in new[] { "Fable2", "Fable2TU1" })
        {
            string config = File.ReadAllText(Path.Combine(root, "versions", build, "refii_config.toml"));
            foreach (string function in new[] { "rex_CActionFactory_CreateFromDefinition", "rex_lhGameFlowGuiMode_Apply", "rex_lhGameFlowGuiMode_Remove", "rex_lhFrontEnd_SetStartGameTarget" })
            {
                AssertThat(config.Contains("\"" + function + "\"", StringComparison.Ordinal)).OverrideFailureMessage(build + " " + function).IsTrue();
            }
        }
        string rows = File.ReadAllText(Path.Combine(root, "versions", "Fable2", "guest_addresses.inc"));
        AssertThat(rows.Contains("{0x8229DDF0u, 0x8229DB28u},", StringComparison.Ordinal)).IsTrue();  // the definition's field reader
        AssertThat(rows.Contains("{0x8219CD00u, 0x8219CDA8u},", StringComparison.Ordinal)).IsTrue();  // Gameface's lua_pushlstring
        string bus = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_events.cpp"));
        AssertThat(bus.Contains("static_assert(kScriptableActionCount == 89", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("REX_EXTERN(rex_lhFrontEnd_SetStartGameTarget)", StringComparison.Ordinal)).IsTrue();
        string frontend = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_frontend_lua.cpp"));
        foreach (string registered in new[] { "__refii_hash", "__refii_signed_in", "__refii_save_count", "__refii_screen" })
        {
            AssertThat(frontend.Contains("{\"" + registered + "\", &Native", StringComparison.Ordinal)).OverrideFailureMessage(registered).IsTrue();
        }
    }
}
