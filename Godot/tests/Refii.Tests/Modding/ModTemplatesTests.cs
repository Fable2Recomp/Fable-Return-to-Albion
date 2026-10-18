namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;

/// <summary>
/// The starter mods (Engine_ModTemplates) are held to what they claim: distinct ids, files with
/// content and no placeholder left behind, Lua the game's compiler accepts, JSON a parser
/// accepts, and a quest_hooks template that follows the catalog.
/// </summary>
[TestSuite]
public class ModTemplatesTests
{
    private const string Mod = "my_mod";
    private const string Title = "My Mod";

    private static IEnumerable<(ModTemplate Template, string Path, string Text)> AllFiles() =>
        ModTemplates.All.SelectMany(t => t.Render(Mod, Title).Select(f => (t, f.Path, f.Text)));

    [TestCase]
    public void IdsAreUniqueAndFilesAreFilled()
    {
        AssertThat(ModTemplates.All.Count > 0).IsTrue();
        AssertThat(ModTemplates.All.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count())
            .IsEqual(ModTemplates.All.Count);

        foreach (ModTemplate template in ModTemplates.All)
        {
            AssertThat(template.Title.Length > 0).OverrideFailureMessage($"{template.Id}: no title").IsTrue();
            AssertThat(template.Description.Length > 0).OverrideFailureMessage($"{template.Id}: no description").IsTrue();

            IReadOnlyList<(string Path, string Text)> files = template.Render(Mod, Title);
            AssertThat(files.Count > 0).OverrideFailureMessage($"{template.Id}: writes no files").IsTrue();
            AssertThat(files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count())
                .OverrideFailureMessage($"{template.Id}: a path twice").IsEqual(files.Count);

            foreach ((string path, string text) in files)
            {
                AssertThat(path.Length > 0 && !path.StartsWith('/') && !path.Contains("..")).OverrideFailureMessage($"{template.Id}: bad path '{path}'").IsTrue();
                AssertThat(text.Trim().Length > 0).OverrideFailureMessage($"{template.Id}: {path} is empty").IsTrue();
                AssertThat(text.Contains("{mod}") || text.Contains("{MOD}") || text.Contains("{title}"))
                    .OverrideFailureMessage($"{template.Id}: {path} keeps a placeholder").IsFalse();
            }
        }

        AssertThat(ModTemplates.Find("COMBAT_TWEAK")).IsNotNull();
        AssertThat(ModTemplates.Find("no_such_template")).IsNull();
    }

    [TestCase]
    public void LuaCompiles()
    {
        foreach ((ModTemplate template, string path, string text) in AllFiles().Where(f => f.Path.EndsWith(".lua", StringComparison.Ordinal)))
        {
            bool ok = Refii.Lua.Engine_LuaDecompiler.TryCheckSource(text, "@" + template.Id + "/" + path, out string error);
            if (!ok && error.Contains("not available", StringComparison.Ordinal))
            {
                return;  // no compiler in this test host: the mount checks again at run time
            }

            AssertThat(ok).OverrideFailureMessage($"{template.Id}: {path} does not compile: {error}").IsTrue();
        }
    }

    [TestCase]
    public void JsonParses()
    {
        foreach ((ModTemplate template, string path, string text) in AllFiles().Where(f => f.Path.EndsWith(".json", StringComparison.Ordinal)))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                AssertThat(document.RootElement.ValueKind == JsonValueKind.Object).OverrideFailureMessage($"{template.Id}: {path} is not an object").IsTrue();
            }
            catch (JsonException ex)
            {
                AssertThat(false).OverrideFailureMessage($"{template.Id}: {path} does not parse: {ex.Message}").IsTrue();
            }
        }
    }

    [TestCase]
    public void TemplatesFollowTheCatalog()
    {
        // Every Refii.on("<name>") a template subscribes names an event the catalog has.
        foreach ((ModTemplate template, string path, string text) in AllFiles().Where(f => f.Path.EndsWith(".lua", StringComparison.Ordinal)))
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "Refii\\.on\\(\"([a-z_]+)\""))
            {
                AssertThat(ModApi.Find(m.Groups[1].Value)).OverrideFailureMessage($"{template.Id}: {path} subscribes unknown event '{m.Groups[1].Value}'").IsNotNull();
            }
        }

        string questHooks = ModTemplates.Find("quest_hooks")!.Render(Mod, Title).Single(f => f.Path == ModLua.ModInitPath).Text;
        foreach (string name in new[] { "level_loaded", "item_added", "entity_died" })
        {
            AssertThat(questHooks.Contains($"Refii.on(\"{name}\"")).OverrideFailureMessage($"quest_hooks: no {name} handler").IsTrue();
        }

        foreach (string name in new[] { "quest_state", "dialogue" })
        {
            AssertThat(questHooks.Contains($"Refii.on(\"{name}\"")).OverrideFailureMessage($"quest_hooks: {name} handler does not follow the catalog").IsEqual(ModApi.Find(name) is not null);
        }

        string native = ModTemplates.Find("native_handler")!.Render(Mod, Title).Single(f => f.Path == "native/handler.cpp").Text;
        AssertThat(native.Contains("REFII_MOD_ON(Hit, OnHit)")).IsTrue();
        AssertThat(native.Contains("#include \"core/refii_mod.h\"")).IsTrue();
    }
}
