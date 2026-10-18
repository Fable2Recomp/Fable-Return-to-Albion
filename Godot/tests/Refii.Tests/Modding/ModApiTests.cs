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
