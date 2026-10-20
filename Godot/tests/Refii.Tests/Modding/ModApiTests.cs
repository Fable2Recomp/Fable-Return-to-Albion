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
        foreach (string verb in verbs)
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
        foreach (string call in new[] { "Refii.player_for(", "Refii.acting_player(", "Refii.run_as_player(", "Refii.combat_state(", "Refii.world_here(", "Refii.player_of(", "Refii.event_replica(" })
        {
            AssertThat(lua.Contains(call, StringComparison.Ordinal)).OverrideFailureMessage($"{call} is not in ModApi.LuaHelpers").IsTrue();
        }

        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        foreach (string call in new[] { "refii_mod_player_for(", "refii_mod_world_here(", "refii_mod_player_of(", "refii_mod_event_replica(" })
        {
            AssertThat(native.Contains(call, StringComparison.Ordinal)).OverrideFailureMessage($"{call} is not in ModApi.Helpers").IsTrue();
        }

        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        foreach (string verb in new[] { "player_for", "combat_state", "world_here", "replica" })
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
        foreach (string call in new[] { "refii_mod_player_for(PPCContext* ctx, uint8_t* base, uint32_t entity)", "refii_mod_world_here(uint8_t* base)" })
        {
            AssertThat(header.Contains("extern \"C\" int32_t " + call, StringComparison.Ordinal))
                .OverrideFailureMessage($"{call} is not declared in core/refii_mod.h").IsTrue();
        }
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
}
