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
/// Quickslots is pure Lua on the mod API: the D-pad arrives as input actions and claims, the
/// slots live in the player store, the HUD's D-pad is told what to show, the books' rows reach
/// the mod through row actions, and the press runs the quick menu's own actions. It ships no
/// <c>native/</c>, every <c>Refii.*</c> name it calls is in the catalog, every event it subscribes
/// is a catalog event with the fields it reads, its data files are the shapes the engine reads,
/// and each chunk compiles with the game's own compiler.
/// </summary>
[TestSuite]
public class QuickslotsLuaTests
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

    private static string ModFolder(string root) => Path.Combine(root, "Fable2", "mods-src", "quickslots");

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
            return;
        }

        AssertThat(Directory.Exists(Path.Combine(ModFolder(root), "native"))).IsFalse();
        AssertThat(Directory.EnumerateFiles(ModFolder(root), "*.cpp", SearchOption.AllDirectories).Any()).IsFalse();
        AssertThat(Directory.EnumerateFiles(ModFolder(root), "*.h", SearchOption.AllDirectories).Any()).IsFalse();

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

        AssertThat(modules).ContainsExactlyInAnyOrder("assign", "common", "slots");
        string init = File.ReadAllText(Path.Combine(ModFolder(root), "mod", "init.lua"));
        AssertThat(Regex.IsMatch(init, @"Refii\.on\(")).IsFalse();

        string toml = File.ReadAllText(Path.Combine(ModFolder(root), "mod.toml"));
        AssertThat(toml.Contains("native", StringComparison.OrdinalIgnoreCase)).IsFalse();
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
            string code = Comment.Replace(text, string.Empty);
            foreach (Match m in RefiiCall.Matches(code))
            {
                string name = m.Groups[1].Value;
                used.Add(name);
                AssertThat(catalog.Contains(name)).OverrideFailureMessage($"{path} calls Refii.{name}, which the catalog does not offer").IsTrue();
            }
        }

        // The families the feature needs: the store, the catalog of things, the quick menu's
        // actions, the claims, the HUD's D-pad and the menus.
        foreach (string name in new[]
                 {
                     "on", "setting", "log", "hero",
                     "player_value", "player_save",
                     "inventory", "expressions", "spells_known", "selected_expression", "record_string", "record_int",
                     "use_item", "equip_weapon", "perform_expression", "assign_spell",
                     "input_claim", "hud_icon", "hud_slide", "hud_hold",
                     "set_menu_rows", "open_menu",
                 })
        {
            AssertThat(used.Contains(name)).OverrideFailureMessage("the mod never calls Refii." + name).IsTrue();
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
        string lua = AllLua(root);
        var subscribed = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Subscribe.Matches(lua))
        {
            string name = m.Groups[1].Value;
            subscribed.Add(name);
            AssertThat(events.ContainsKey(name)).OverrideFailureMessage("no event " + name).IsTrue();
        }

        foreach (string name in new[] { "tick", "input_action", "mod_menu_row", "save_loaded" })
        {
            AssertThat(subscribed.Contains(name)).OverrideFailureMessage("the mod never subscribes " + name).IsTrue();
        }

        foreach ((string name, string[] fields) in new[]
                 {
                     ("tick", new[] { "seconds" }),
                     ("input_action", new[] { "name", "player", "pressed" }),
                     ("mod_menu_row", new[] { "menu", "row", "player", "command" }),
                 })
        {
            var known = new HashSet<string>(events[name].Fields.Select(f => f.Name), StringComparer.Ordinal);
            foreach (string field in fields)
            {
                AssertThat(known.Contains(field)).OverrideFailureMessage($"{name} has no field {field}").IsTrue();
            }
        }
    }

    [TestCase]
    public void TheDataFilesNameWhatTheLuaHandles()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string lua = AllLua(root);
        string input = File.ReadAllText(Path.Combine(ModFolder(root), "input.json"));
        // Every action input.json declares is one the Lua reads back (as "quickslots.<action>").
        foreach (Match m in Regex.Matches(input, @"^\s*""([a-z_]+)""\s*:", RegexOptions.Multiline))
        {
            string action = m.Groups[1].Value;
            bool handled = action is "lt" or "rt" or "assign"
                || Regex.IsMatch(action, @"^(lt_|rt_)?(up|down|left|right)$");
            AssertThat(handled).OverrideFailureMessage("input.json declares " + action + ", which slots.lua does not handle").IsTrue();
        }
        AssertThat(input.Contains("\"chord\": \"LT\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(input.Contains("\"chord\": \"RT\"", StringComparison.Ordinal)).IsTrue();

        // Every Mod:lua: row the templates and the Lua state names a function the Lua defines.
        string templates = File.ReadAllText(Path.Combine(ModFolder(root), "ui", "templates.json"));
        var defined = new HashSet<string>(
            Regex.Matches(lua, @"function quickslots\.([a-z_]+)").Select(m => m.Groups[1].Value), StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(templates + lua, @"Mod:lua:quickslots\.([a-z_]+)"))
        {
            AssertThat(defined.Contains(m.Groups[1].Value)).OverrideFailureMessage("no Lua function quickslots." + m.Groups[1].Value).IsTrue();
        }

        // Every menu the Lua opens or fills is a template menu, and every row action names a book.
        foreach (Match m in Regex.Matches(lua, @"""(quickslots\.(?:menu|assign|choose))"""))
        {
            AssertThat(templates.Contains("\"" + m.Groups[1].Value + "\"", StringComparison.Ordinal)).OverrideFailureMessage("templates.json has no menu " + m.Groups[1].Value).IsTrue();
        }
        AssertThat(templates.Contains("\"rowActions\"", StringComparison.Ordinal)).IsTrue();

        // Every text tag the Lua or the templates draw is in the language file.
        var tags = new HashSet<string>(
            File.ReadLines(Path.Combine(ModFolder(root), "language", "en-uk", "text", "book.babel.csv"))
                .Skip(1).Select(l => l.Split(',')[0]), StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(templates + lua, @"QUICKSLOTS_[A-Z_]+"))
        {
            string tag = m.Value;
            if (tag == "QUICKSLOTS_SLOT_")
            {
                continue;
            }
            AssertThat(tags.Contains(tag)).OverrideFailureMessage("no text for " + tag).IsTrue();
        }
        for (int i = 1; i <= 12; i++)
        {
            AssertThat(tags.Contains("QUICKSLOTS_SLOT_" + i)).IsTrue();
        }

        // The settings the Lua reads are the ones settings.json states and the page offers.
        string settings = File.ReadAllText(Path.Combine(ModFolder(root), "settings.json"));
        string page = File.ReadAllText(Path.Combine(ModFolder(root), "settings.ui.json"));
        foreach (Match m in Regex.Matches(lua, @"Setting\(""([a-z_]+)"""))
        {
            string key = m.Groups[1].Value;
            AssertThat(settings.Contains("\"" + key + "\"", StringComparison.Ordinal)).OverrideFailureMessage("settings.json lacks " + key).IsTrue();
            AssertThat(page.Contains("\"" + key + "\"", StringComparison.Ordinal)).OverrideFailureMessage("settings.ui.json lacks " + key).IsTrue();
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
            string composed = ModLua.TryModuleName(path, out string name)
                ? ModLua.WrapModule("quickslots", name, text)
                : ModLua.Wrap("quickslots", text);
            bool ok = Refii.Lua.Engine_LuaDecompiler.TryCheckSource(composed, "@quickslots/" + path, out string error);
            if (!ok && error.Contains("not available", StringComparison.Ordinal))
            {
                return;
            }

            AssertThat(ok).OverrideFailureMessage($"{path} does not compile: {error}").IsTrue();
        }
    }
}
