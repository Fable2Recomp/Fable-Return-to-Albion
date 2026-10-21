namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;

/// <summary>
/// The schema check (Engine_ModValidate) is held to the content that ships: both bundled mods and
/// every starter template validate clean; a broken file of each kind yields the error its loader
/// would refuse it for, at the pointer that names the field; and a field no loader reads is a
/// warning, never an error. Pure .NET throughout — no Godot type reaches the test host.
/// </summary>
[TestSuite]
public class ModValidateTests
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

    private static List<ModIssue> Check(string path, string text) => ModValidate.Validate(path, text).ToList();

    private static string Lines(IEnumerable<ModIssue> issues) =>
        string.Join("\n", issues.Select(i => (i.Error ? "error: " : "warning: ") + i));

    /// <summary>The one error at <paramref name="pointer"/>, asserted to exist.</summary>
    private static void HasError(List<ModIssue> issues, string pointer, string words)
    {
        AssertThat(issues.Any(i => i.Error && i.Pointer == pointer && i.Message.Contains(words, StringComparison.Ordinal)))
            .OverrideFailureMessage($"expected an error at '{pointer}' saying '{words}'; got:\n{Lines(issues)}")
            .IsTrue();
    }

    private static void HasWarning(List<ModIssue> issues, string pointer, string words)
    {
        AssertThat(issues.Any(i => !i.Error && i.Pointer == pointer && i.Message.Contains(words, StringComparison.Ordinal)))
            .OverrideFailureMessage($"expected a warning at '{pointer}' saying '{words}'; got:\n{Lines(issues)}")
            .IsTrue();
    }

    [TestCase]
    public void TheBundledModsValidateClean()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;  // a packaged test run without the source tree
        }

        var expected = new Dictionary<string, int> { ["souls_combat"] = 5, ["spell_weaving"] = 23 };
        foreach ((string mod, int kinds) in expected)
        {
            string folder = Path.Combine(root, "mods-src", mod);
            var issues = new List<ModIssue>();
            int checkedFiles = 0;
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(folder, file);
                if (ModValidate.KindOf(relative).Length == 0)
                {
                    continue;
                }

                checkedFiles++;
                issues.AddRange(ModValidate.Validate(relative, File.ReadAllText(file)));
            }

            AssertThat(checkedFiles).OverrideFailureMessage($"{mod}: content kinds found").IsEqual(kinds);
            AssertThat(issues.Count).OverrideFailureMessage($"{mod}:\n{Lines(issues)}").IsEqual(0);
        }
    }

    [TestCase]
    public void TheStarterTemplatesValidateClean()
    {
        foreach (ModTemplate template in ModTemplates.All)
        {
            foreach ((string path, string text) in template.Render("my_mod", "My Mod"))
            {
                List<ModIssue> issues = Check(path, text);
                AssertThat(issues.Count(i => i.Error))
                    .OverrideFailureMessage($"{template.Id}/{path}:\n{Lines(issues)}")
                    .IsEqual(0);
            }
        }
    }

    [TestCase]
    public void KindsAreChosenByPackagePath()
    {
        AssertThat(ModValidate.KindOf("settings.json")).IsEqual("settings.json");
        AssertThat(ModValidate.KindOf("files\\UI\\Hud.json")).IsEqual("ui/hud.json");
        AssertThat(ModValidate.KindOf("spells/frost.json")).IsEqual(ModValidate.SpellsKind);
        AssertThat(ModValidate.KindOf("spells/deep/frost.json")).IsEqual(string.Empty);
        AssertThat(ModValidate.KindOf("art/gui/hud/x.tex.png")).IsEqual(string.Empty);
        AssertThat(Check("textures/notes.json", "{ not json").Count).IsEqual(0);
    }

    [TestCase]
    public void InvalidJsonIsAnErrorWithItsPlace()
    {
        List<ModIssue> issues = Check("ui/hud.json", "{ \"pieces\": [ { \"id\": \"a\", } ] }");
        AssertThat(issues.Count).IsEqual(1);
        AssertThat(issues[0].Error).IsTrue();
        AssertThat(issues[0].Message).Contains("line 1");

        // spells/*.json is read allowing comments and trailing commas, as ModSpells.TryParse does.
        AssertThat(Check("spells/a.json", "{ // frost\n \"replaces\": \"Chaos\", }").Count).IsEqual(0);
    }

    [TestCase]
    public void UnknownKeysAreWarningsNotErrors()
    {
        List<ModIssue> issues = Check("ui/hud.json",
            "{ \"pieces\": [ { \"id\": \"a\", \"value\": \"mod:a.b\", \"vaule\": 1 } ], \"extra\": true }");
        AssertThat(issues.Any(i => i.Error)).OverrideFailureMessage(Lines(issues)).IsFalse();
        HasWarning(issues, "pieces[0].vaule", "did you mean 'value'");
        HasWarning(issues, "extra", "not a field");
    }

    [TestCase]
    public void BrokenSettings()
    {
        List<ModIssue> issues = Check("settings.json",
            "{ \"m\": { \"cost\": \"lots\", \"deep\": { \"x\": 1 }, \"flag\": true, \"text\": \"2\" } }");
        HasError(issues, "m.cost", "must be a number");
        HasError(issues, "m.deep", "one level only");
        HasWarning(issues, "m.text", "written as text");
        AssertThat(issues.Any(i => i.Pointer == "m.flag")).IsFalse();
        HasError(Check("settings.json", "[1, 2]"), string.Empty, "JSON object");
    }

    [TestCase]
    public void BrokenSettingsUi()
    {
        List<ModIssue> issues = Check("settings.ui.json",
            "{ \"cost\": { \"type\": \"dial\", \"min\": \"0\", \"max\": 10 }, \"woven\": 1 }");
        HasError(issues, "cost.type", "slider, toggle or choice");
        HasError(issues, "cost.min", "must be a number");
        HasError(issues, "woven", "must be an object");
    }

    [TestCase]
    public void BrokenInput()
    {
        List<ModIssue> issues = Check("input.json",
            "{ \"dodge\": { \"buttons\": [\"B\", \"Q\"], \"axis\": \"LeftZ\" }, \"idle\": { \"label\": \"x\" }," +
            " \"roll\": { \"buttons\": \"rt\", \"axis\": \"-lefty\" } }");
        HasError(issues, "dodge.buttons[1]", "'Q' is not a pad button");
        HasError(issues, "dodge.axis", "not a pad axis");
        HasError(issues, "idle", "binds no key, button or axis");
        AssertThat(issues.Any(i => i.Pointer.StartsWith("roll", StringComparison.Ordinal))).OverrideFailureMessage(Lines(issues)).IsFalse();
    }

    [TestCase]
    public void BrokenSpell()
    {
        List<ModIssue> issues = Check("spells/meteor.json",
            "{ \"replaces\": \"Meteor\", \"damage\": -1, \"look\": { \"tint\": [1, 1, 1] } }");
        HasError(issues, "replaces", "'Meteor' is not a spell type");
        HasError(issues, "damage", "at least 0");
        HasError(issues, "look.base", "at least one effect");
        HasError(Check("spells/none.json", "{ \"name\": \"No type\" }"), "replaces", "is required");
    }

    [TestCase]
    public void BrokenRecipes()
    {
        List<ModIssue> issues = Check("fx/recipes.json",
            "{ \"recipes\": [ { \"id\": \"a\", \"when\": \"mod:a.look\", \"base\": [], " +
            "\"pieces\": [ { \"kind\": \"sparks\", \"blend\": \"screen\" } ], \"light\": { \"energy\": \"3\" } }, " +
            "{ \"base\": [\"fxspl_x\"] } ] }");
        HasError(issues, "recipes[0].base", "at least one effect");
        HasError(issues, "recipes[0].pieces[0].kind", "'sparks' is not a piece kind");
        HasError(issues, "recipes[0].pieces[0].blend", "add");
        HasError(issues, "recipes[0].light.energy", "must be a number");
        HasError(issues, "recipes[1].id", "is required");
        HasError(issues, "recipes[1]", "needs \"when\"");
        HasError(Check("fx/recipes.json", "{ \"recipe\": [] }"), string.Empty, "\"recipes\" array");
    }

    [TestCase]
    public void BrokenHud()
    {
        List<ModIssue> issues = Check("ui/hud.json",
            "{ \"pieces\": [ { \"id\": \"a\", \"kind\": \"bar\", \"offset\": [1], \"fill\": \"red\", \"value\": \"health\" }, " +
            "{ \"id\": \"b\", \"kind\": \"picture\" } ], " +
            "\"clones\": [ { \"id\": \"c\", \"values\": { \"M\": \"mod:a.v/mod:a.cap\" }, \"bars\": { \"Bar\": \"N\" } } ] }");
        HasError(issues, "pieces[0].kind", "'bar' is not a piece kind");
        HasError(issues, "pieces[0].offset", "2 numbers");
        HasError(issues, "pieces[0].fill", "'red' is not a colour");
        HasError(issues, "pieces[0].value", "'health' is not a value source");
        HasError(issues, "pieces[1]", "a picture needs \"texture\"");
        HasError(issues, "clones[0].of", "is required");
        HasError(issues, "clones[0].bars.Bar", "names meter 'N'");
    }

    [TestCase]
    public void BrokenTemplates()
    {
        List<ModIssue> issues = Check("ui/templates.json",
            "{ \"menus\": { \"PauseMenu\": { \"patch\": \"true\", \"entries\": [ { \"tag\": \"X\" } ] } }, " +
            "\"screens\": { \"pause\": {}, \"5\": { \"chrome\": \"Frame\", \"statFields\": [ { \"id\": 1 } ] } } }");
        HasError(issues, "menus.PauseMenu.patch", "true or false");
        HasError(issues, "menus.PauseMenu.entries[0].name", "is required");
        HasError(issues, "screens.pause", "not a screen id");
        HasError(issues, "screens.5.chrome", "list of text");
        HasError(issues, "screens.5.statFields[0]", "both \"id\" and \"path\"");
    }

    [TestCase]
    public void BrokenSpellWheel()
    {
        List<ModIssue> issues = Check("ui/spellwheel.json",
            "{ \"picks\": [ { \"store\": \"mod:a.partner_{slot}\", \"inEffect\": \"a.active\", \"scale\": 3, \"exclude\": [9] }, " +
            "{ \"name\": \"b\", \"store\": \"game\" }, { \"name\": \"c\", \"store\": \"a.pick\" } ] }");
        HasError(issues, "picks[0].name", "is required");
        HasError(issues, "picks[0].store", "not a player value key");
        HasError(issues, "picks[0].inEffect", "not a value source");
        HasWarning(issues, "picks[0].scale", "clamped");
        HasWarning(issues, "picks[0].exclude[0]", "no will type");
        HasError(issues, "picks[1].store", "the game's pick");
        HasWarning(issues, "picks[2].store", "has no {slot}");
        HasError(Check("ui/spellwheel.json", "{ \"primary\": {} }"), string.Empty, "\"picks\" list");
        HasError(Check("ui/spellwheel.json", "{ \"secondary\": { \"setting\": \"a.pick\" } }"), "secondary", "old setting-channel form");
    }

    [TestCase]
    public void BrokenAbilities()
    {
        List<ModIssue> issues = Check("ui/abilities.json",
            "{ \"abilities\": [ { \"name\": \"A B\", \"category\": \"Hybrid\", \"level\": \"mod:a.level\", \"maxLevel\": 0, " +
            "\"refund\": \"all\", \"stat\": \"a.next\" }, " +
            "{ \"name\": \"C\", \"category\": \"Will\", \"levelKey\": \"a level\", \"costLike\": 15, \"stats\": [1, 2] } ] }");
        HasError(issues, "abilities[0].levelKey", "is required");
        HasError(issues, "abilities[0].costLike", "is required");
        HasError(issues, "abilities[0].level", "old setting-counter form");
        HasError(issues, "abilities[0].name", "no spaces");
        HasError(issues, "abilities[0].maxLevel", "below the least allowed");
        HasError(issues, "abilities[0].refund", "no refund rule");
        HasError(issues, "abilities[0].stat", "not a value source");
        HasWarning(issues, "abilities[0].category", "names its \"disciplines\"");
        HasError(issues, "abilities[1].levelKey", "not a player value key");
        HasError(issues, "abilities[1].costLike", "above the most allowed");
        HasWarning(issues, "abilities[1].stats", "one per level");
    }

    [TestCase]
    public void BrokenManifest()
    {
        List<ModIssue> issues = Check("mod.toml",
            "name = \"X\" # the title\n" +
            "version = \"1.0\"\n" +
            "requires = [\"foo > 1.0\", \">= 2\", \"bar >= 0.2\"]\n" +
            "after = [\n" +
            "  \"hd-ui\" ]\n" +
            "colour = \"red\"\n");
        HasError(issues, "name (line 1)", "closing quote");
        HasError(issues, "requires (line 3)", "only \"<mod> >= <version>\"");
        HasError(issues, "requires (line 3)", "names no mod");
        HasError(issues, "after (line 4)", "close on the same line");
        HasError(issues, "line 5", "not a key = value line");
        HasWarning(issues, "colour (line 6)", "not a field");
        AssertThat(issues.Any(i => i.Pointer == "version (line 2)")).OverrideFailureMessage(Lines(issues)).IsFalse();
    }
}
