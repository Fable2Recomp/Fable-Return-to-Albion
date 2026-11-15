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
/// Fable 3's mod events are the catalog's: every kind the Fable 3 hooks raise is one the catalog
/// lists in <see cref="ModApi.Fable3Raised"/>, by the same number, and every field they send is a
/// field the catalog offers for that kind.
/// </summary>
[TestSuite]
public class Fable3ModEventsTests
{
    private static string? RepoRoot()
    {
        string? at = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && at != null; i++)
        {
            if (File.Exists(Path.Combine(at, "Fable3", "src", "hooks", "src", "mod_events_hooks.cpp")))
            {
                return at;
            }

            at = Path.GetDirectoryName(at);
        }

        return null;
    }

    /// <summary>The kinds the hooks declare: every int32 constant of the events file, the kEvent* of the runtime's.</summary>
    private static IEnumerable<(string Name, int Kind)> DeclaredKinds(string root)
    {
        string events = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "mod_events_hooks.cpp"));
        foreach (Match m in Regex.Matches(events, @"constexpr int32_t k(\w+) = (\d+);"))
        {
            yield return (m.Groups[1].Value, int.Parse(m.Groups[2].Value));
        }

        string runtime = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "mod_lua_hooks.cpp"));
        foreach (Match m in Regex.Matches(runtime, @"constexpr int32_t kEvent(\w+) = (\d+);"))
        {
            yield return (m.Groups[1].Value, int.Parse(m.Groups[2].Value));
        }
    }

    [TestCase]
    public void EveryRaisedKindIsCatalogued()
    {
        foreach (int kind in ModApi.Fable3Raised.Keys)
        {
            AssertThat(ModApi.Events.Any(e => e.Kind == kind))
                .OverrideFailureMessage($"Fable 3 raises kind {kind}, which the catalog does not list").IsTrue();
        }
    }

    [TestCase]
    public void TheHooksKindsAreTheCatalogs()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        var declared = DeclaredKinds(root).ToList();

        AssertThat(declared.Count >= 20).OverrideFailureMessage($"only {declared.Count} kinds parsed").IsTrue();
        foreach ((string name, int kind) in declared)
        {
            AssertThat(ModApi.Fable3Raised.ContainsKey(kind))
                .OverrideFailureMessage($"Fable 3 declares {name} = {kind}, absent from ModApi.Fable3Raised").IsTrue();
            ModApiEvent? e = ModApi.Events.FirstOrDefault(x => x.Kind == kind);
            string expected = Regex.Replace(e?.Name ?? string.Empty, "Event$", string.Empty);
            AssertThat(string.Equals(name, expected, StringComparison.Ordinal))
                .OverrideFailureMessage($"Fable 3's k{name} = {kind} is the catalog's {expected}").IsTrue();
        }
    }

    [TestCase]
    public void TheFieldsSentAreTheCatalogs()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string text = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "mod_events_hooks.cpp"));
        var names = new Dictionary<string, int>();
        foreach (Match m in Regex.Matches(text, @"constexpr int32_t k(\w+) = (\d+);"))
        {
            names[m.Groups[1].Value] = int.Parse(m.Groups[2].Value);
        }

        int checkedEvents = 0;
        foreach (Match m in Regex.Matches(text, @"(?:QueueEvent|Decide)\(\s*(?:ctx,\s*base,\s*)?k(\w+),\s*Fields\(\s*""([^""]*)"""))
        {
            if (!names.TryGetValue(m.Groups[1].Value, out int kind))
            {
                continue;  // a kind passed in a variable (the level edges), checked by the declarations test
            }

            ModApiEvent e = ModApi.Events.First(x => x.Kind == kind);
            foreach (Match field in Regex.Matches(m.Groups[2].Value, @"(\w+)="))
            {
                AssertThat(e.Fields.Any(f => f.Name == field.Groups[1].Value))
                    .OverrideFailureMessage($"{e.LuaName}: Fable 3 sends {field.Groups[1].Value}, the catalog has no such field")
                    .IsTrue();
            }

            checkedEvents++;
        }

        AssertThat(checkedEvents >= 10).OverrideFailureMessage($"only {checkedEvents} field lists parsed").IsTrue();
    }

    [TestCase]
    public void AFable3ModHearsWhatFable3DoesNotRaise()
    {
        const string lua = "Refii.on(\"gold\", function(e) end)\nRefii.on(\"hit\", function(e) end)\nRefii.on('hit', f)";
        IReadOnlyList<ModIssue> issues = ModValidate.TitleIssues("mod/init.lua", () => lua, "Fable3");
        AssertThat(issues.Count).IsEqual(1);
        AssertThat(issues[0].Pointer).IsEqual("hit");
        AssertThat(issues[0].Error).IsFalse();
        AssertThat(ModValidate.TitleIssues("mod/init.lua", () => lua, "Fable2").Count).IsEqual(0);

        const string gd = "func _ready():\n\thost.tick.connect(_t)\n\thost.on_verdict(\"block\", _b)\n\tbutton.pressed.connect(_p)";
        IReadOnlyList<ModIssue> godot = ModValidate.TitleIssues("godot/x.gd", () => gd, "Fable3");
        AssertThat(string.Join(",", godot.Select(i => i.Pointer))).IsEqual("block");

        AssertThat(ModValidate.TitleIssues("native/handler.cpp", null, "Fable3").Count).IsEqual(0);
        AssertThat(ModValidate.TitleIssues("ui/spellwheel.json", null, "Fable3").Count).IsEqual(1);
        AssertThat(ModValidate.TitleIssues("data/scripts/startup/MyStartup.lua", null, "Fable3").Count).IsEqual(0);
    }

    [TestCase]
    public void TheNativeBusNamesAreTheCatalogs()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string runtime = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "mod_lua_hooks.cpp"));
        var named = Regex.Matches(runtime, @"\{""(\w+)"", (\d+)\}").Select(m => (Name: m.Groups[1].Value, Kind: int.Parse(m.Groups[2].Value))).ToList();
        AssertThat(named.Select(n => n.Kind).OrderBy(k => k).ToArray()).ContainsExactly(ModApi.Fable3Raised.Keys.OrderBy(k => k).ToArray());
        foreach ((string name, int kind) in named)
        {
            AssertThat(ModApi.Find(name)?.Kind).OverrideFailureMessage($"native bus name {name} = {kind}").IsEqual(kind);
        }
    }

    [TestCase]
    public void Fable2NamesEveryCatalogEventForTheNativeBus()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        int start = bus.IndexOf("kBusNames[] = {", StringComparison.Ordinal);
        AssertThat(start >= 0).IsTrue();
        string table = bus[start..bus.IndexOf("};", start, StringComparison.Ordinal)];
        var named = Regex.Matches(table, @"\{""(\w+)"", (\d+)\}").Select(m => $"{m.Groups[1].Value}={m.Groups[2].Value}");
        AssertThat(string.Join(",", named)).IsEqual(string.Join(",", ModApi.Events.Select(e => $"{e.LuaName}={e.Kind}")));
    }
}
