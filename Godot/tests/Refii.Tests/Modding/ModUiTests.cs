namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;
using Refii.Ui;

/// <summary>
/// The UI surfaces a mod authors (Part G): the HUD's <c>layout</c> block and its precedence under
/// the player's file, <c>text</c> pieces, composable modal records, a mod menu's row commands,
/// settings <c>choice</c> rows, and the schema check for every new key. Pure .NET throughout — the
/// parsers and the composition are the pure halves of their loaders; no Godot type reaches the host.
/// </summary>
[TestSuite]
public class ModUiTests
{
    private static List<ModIssue> Check(string path, string text) => ModValidate.Validate(path, text).ToList();

    private static string Lines(IEnumerable<ModIssue> issues) =>
        string.Join("\n", issues.Select(i => (i.Error ? "error: " : "warning: ") + i));

    private static void HasError(List<ModIssue> issues, string pointer, string words) =>
        AssertThat(issues.Any(i => i.Error && i.Pointer == pointer && i.Message.Contains(words, StringComparison.Ordinal)))
            .OverrideFailureMessage($"expected an error at '{pointer}' saying '{words}'; got:\n{Lines(issues)}")
            .IsTrue();

    private static void HasWarning(List<ModIssue> issues, string pointer, string words) =>
        AssertThat(issues.Any(i => !i.Error && i.Pointer == pointer && i.Message.Contains(words, StringComparison.Ordinal)))
            .OverrideFailureMessage($"expected a warning at '{pointer}' saying '{words}'; got:\n{Lines(issues)}")
            .IsTrue();

    private static void Clean(List<ModIssue> issues) =>
        AssertThat(issues.Count(i => i.Error)).OverrideFailureMessage("expected no error; got:\n" + Lines(issues)).IsEqual(0);

    // ---- H.16 a copy's own slide --------------------------------------------------------------

    [TestCase]
    public void ACopyStatesItsOwnSlide()
    {
        // "slides": element → slide number (1 past the master), a number or the Studio's numeric
        // string; anything else is warned and ignored, and a widget copy takes none.
        List<ModIssue> issues = Check("ui/hud.json",
            "{ \"clones\": [ { \"id\": \"c\", \"of\": \"slidefader2.GenericMeter.GenericMeter\", \"slides\": { \"GenericMeter\": 2, \"Bar\": \"3\" } }," +
            " { \"id\": \"d\", \"of\": \"slidefader2.CrucibleMeter.CrucibleMeter\", \"slides\": { \"CrucibleMeter\": 0, \"Bar\": \"On\", \"Fill\": 1.5 } }," +
            " { \"id\": \"w\", \"of\": \"widget.Reticule\", \"slides\": { \"x\": 1 } }," +
            " { \"id\": \"e\", \"of\": \"slidefader2.PriceWidget.PriceWidget\", \"slides\": [1, 2] } ] }");
        AssertThat(issues.Any(i => i.Pointer.StartsWith("clones[0]", StringComparison.Ordinal)))
            .OverrideFailureMessage("a well-formed slides block raised an issue:\n" + Lines(issues)).IsFalse();
        HasWarning(issues, "clones[1].slides.CrucibleMeter", "slide number");
        HasWarning(issues, "clones[1].slides.Bar", "slide number");
        HasWarning(issues, "clones[1].slides.Fill", "slide number");
        HasWarning(issues, "clones[2].slides", "copy of a widget");
        HasError(issues, "clones[3].slides", "must be an object");
        AssertThat(issues.Count(i => i.Error)).OverrideFailureMessage(Lines(issues)).IsEqual(1);
    }

    // ---- G.5 the HUD layout block ----------------------------------------------------------

    [TestCase]
    public void LayoutEntriesTakeThePlayerFilesShape()
    {
        Dictionary<string, Engine_HudModLayout.Move> moves = Engine_HudModLayout.ParseModFile(
            "{ \"layout\": { \"health\": { \"anchor\": [0, 1], \"offset\": [64, -96], \"art\": \"PlayerHealth\", \"placed\": true }," +
            " \"half\": { \"anchor\": [0.5, 0] }, \"short\": { \"anchor\": [1], \"offset\": [1, 2] } } }");
        AssertThat(moves.Count).IsEqual(1);
        AssertThat(moves["health"]).IsEqual(new Engine_HudModLayout.Move(0f, 1f, 64f, -96f));

        // The player's file is the same entry under "pieces".
        Dictionary<string, Engine_HudModLayout.Move> player = Engine_HudModLayout.ParsePlayerFile(
            "{ \"pieces\": { \"health\": { \"anchor\": [0, 1], \"offset\": [64, -96] } } }");
        AssertThat(player["health"]).IsEqual(moves["health"]);

        AssertThat(Engine_HudModLayout.ParseModFile("{ not json").Count).IsEqual(0);
        AssertThat(Engine_HudModLayout.ParseModFile("{ \"pieces\": [] }").Count).IsEqual(0);
    }

    [TestCase]
    public void LayoutComposesInLoadOrderAndThePlayerWins()
    {
        Engine_HudModLayout.Composed mods = Engine_HudModLayout.Compose(new[]
        {
            ("low", "{ \"layout\": { \"a\": { \"anchor\": [0, 0], \"offset\": [1, 1] }, \"b\": { \"anchor\": [0, 0], \"offset\": [2, 2] } } }"),
            ("high", "{ \"layout\": { \"a\": { \"anchor\": [1, 1], \"offset\": [9, 9] } } }"),
        });
        AssertThat(mods.Moves["a"].OffsetX).IsEqual(9f);
        AssertThat(mods.Owners["a"]).IsEqual("high");
        AssertThat(mods.Moves["b"].OffsetX).IsEqual(2f);
        AssertThat(mods.Conflicts.Count).IsEqual(1);
        AssertThat(mods.Conflicts[0]).IsEqual(new Engine_HudModLayout.Conflict("a", "high", "low"));

        var playerFile = new Dictionary<string, Engine_HudModLayout.Move>
        {
            ["b"] = new(0.5f, 0.5f, 0f, 0f),
        };
        Dictionary<string, Engine_HudModLayout.Move> standing = Engine_HudModLayout.Resolve(mods, playerFile);
        AssertThat(standing["a"].OffsetX).IsEqual(9f);
        AssertThat(standing["b"]).IsEqual(playerFile["b"]);
        AssertThat(Engine_HudModLayout.Resolve(mods, null).Count).IsEqual(2);
    }

    // ---- G.5 text pieces -------------------------------------------------------------------

    [TestCase]
    public void TextResolvesLiteralTagAndValue()
    {
        Func<string, string?> book = tag => tag == "MY_TAG" ? "From the book" : null;
        Func<string, float?> values = key => key == "m.gold" ? 1234.5f : null;

        AssertThat(Engine_HudModText.Resolve("Hello", string.Empty, values, book)).IsEqual("Hello");
        AssertThat(Engine_HudModText.Resolve("MY_TAG", string.Empty, values, book)).IsEqual("From the book");
        AssertThat(Engine_HudModText.Resolve("NOT_A_TAG", string.Empty, values, book)).IsEqual("NOT_A_TAG");
        AssertThat(Engine_HudModText.Resolve("mod:m.gold", "{0:0} gold", values, book)).IsEqual("1235 gold");
        AssertThat(Engine_HudModText.Resolve("mod:m.gold", "N1", values, book)).IsEqual("1,234.5");
        AssertThat(Engine_HudModText.Resolve("mod:m.gold", string.Empty, values, book)).IsEqual("1234.5");

        // Not stated draws nothing, never a zero.
        AssertThat(Engine_HudModText.Resolve("mod:m.other", "{0}", values, book)).IsNull();
        AssertThat(Engine_HudModText.Resolve("mod:", string.Empty, values, book)).IsNull();
        AssertThat(Engine_HudModText.Resolve("  ", string.Empty, values, book)).IsNull();

        AssertThat(Engine_HudModText.Format("{1} broken", 2f)).IsEqual("2");
        AssertThat(Engine_HudModText.Align("right")).IsEqual(2);
        AssertThat(Engine_HudModText.Align("Centre")).IsEqual(1);
        AssertThat(Engine_HudModText.Align("center")).IsEqual(1);
        AssertThat(Engine_HudModText.Align("left")).IsEqual(0);
        AssertThat(Engine_HudModText.Align("diagonal")).IsEqual(0);
    }

    [TestCase]
    public void HudValidatorCoversLayoutAndText()
    {
        Clean(Check("ui/hud.json",
            "{ \"layout\": { \"group.PlayerHealth.PlayerHealth\": { \"anchor\": [0, 1], \"offset\": [64, -96] } }," +
            " \"pieces\": [ { \"id\": \"m.t\", \"kind\": \"text\", \"anchor\": [1, 0], \"offset\": [-48, 40]," +
            " \"text\": \"mod:m.gold\", \"format\": \"{0:0} gold\", \"size\": 18, \"colour\": \"#d8b23a\", \"align\": \"right\" } ] }"));

        List<ModIssue> issues = Check("ui/hud.json",
            "{ \"layout\": { \"a\": { \"anchor\": [0, 1] }, \"b\": { \"anchor\": [0, \"x\"], \"offset\": [0, 0] }, \"c\": 4," +
            " \"d\": { \"anchor\": [2, 0], \"offset\": [0, 0] } }," +
            " \"pieces\": [ { \"id\": \"t1\", \"kind\": \"text\" }, { \"id\": \"t2\", \"kind\": \"text\", \"text\": \"mod:\", \"size\": [10, 10]," +
            " \"align\": \"up\", \"value\": \"mod:x.y\" }, { \"id\": \"m\", \"kind\": \"meter\", \"value\": \"mod:x.y\", \"align\": \"left\" } ] }");
        HasError(issues, "layout.a", "needs \"offset\"");
        HasError(issues, "layout.b.anchor", "two numbers");
        HasError(issues, "layout.c", "must be an object");
        HasWarning(issues, "layout.d.anchor", "fraction of the screen");
        HasError(issues, "pieces[0].text", "required");
        HasError(issues, "pieces[1].text", "names no value");
        HasError(issues, "pieces[1].size", "point size");
        HasWarning(issues, "pieces[1].align", "not left, centre or right");
        HasWarning(issues, "pieces[1].value", "not read by a \"text\" piece");
        HasWarning(issues, "pieces[2].align", "read only by a \"text\" piece");
    }

    // ---- G.1 modals --------------------------------------------------------------------------

    [TestCase]
    public void ShippedModalsAnswerAsTheTableDid()
    {
        var table = new Engine_UiModals();
        AssertThat(table.Count).IsEqual(Engine_UiModals.Shipped().Count);
        AssertThat(table.Find(0, 22, dismissal: true)!.Confirm).IsEqual(36u);
        AssertThat(table.Find(0, 28, dismissal: true)!.Confirm).IsEqual(46u);
        AssertThat(table.Find(0, 5, dismissal: true)).IsNull();
        AssertThat(table.Find(13, uint.MaxValue, dismissal: false)!.Confirm).IsEqual(39u);
        AssertThat(table.Find(20, uint.MaxValue, dismissal: false)!.Cancel).IsNull();
        AssertThat(table.Find(2, uint.MaxValue, dismissal: true)).IsNull();
        AssertThat(table.Find(42, uint.MaxValue, dismissal: false)).IsNull();
    }

    [TestCase]
    public void ModalsComposeWholeRecordsWithOwners()
    {
        var table = new Engine_UiModals();
        var owners = new List<string>();
        var warnings = new List<string>();
        int taken = table.ApplyText(
            "{ \"modals\": { \"13\": { \"kind\": \"query\", \"confirm\": 36, \"cancel\": 37 }, \"20\": null," +
            " \"42\": { \"kind\": \"message\", \"confirm\": 36 }, \"7@9\": { \"confirm\": 46 }," +
            " \"bad\": { \"confirm\": 1 }, \"43\": { \"kind\": \"message\", \"confirm\": 36, \"cancel\": 37 } } }",
            owners.Add, warnings.Add);
        AssertThat(taken).IsEqual(4);
        AssertThat(table.Find(13, uint.MaxValue, false)!.Confirm).IsEqual(36u);
        AssertThat(table.Find(20, uint.MaxValue, false)).IsNull();
        AssertThat(table.Find(42, uint.MaxValue, true)!.Confirm).IsEqual(36u);
        AssertThat(table.Find(7, 9, false)!.Confirm).IsEqual(46u);
        AssertThat(table.Find(7, 8, false)).IsNull();
        AssertThat(owners).Contains("modal:13", "modal:20", "modal:42", "modal:7@9");
        AssertThat(warnings.Count).IsEqual(2);

        // A file with no modals, or no JSON, changes nothing.
        AssertThat(table.ApplyText("{ \"menus\": {} }", owners.Add, warnings.Add)).IsEqual(0);
        AssertThat(table.ApplyText("{ nope", owners.Add, warnings.Add)).IsEqual(0);
    }

    [TestCase]
    public void ModalsRoundTripThroughTheirJson()
    {
        var table = new Engine_UiModals();
        table.ApplyText("{ \"modals\": { \"13\": { \"kind\": \"query\", \"confirm\": 36 }, \"0@22\": null } }", _ => { }, _ => { });
        string json = table.ToJson();

        var copy = new Engine_UiModals();
        copy.ApplyText("{ \"modals\": " + json + " }", _ => { }, _ => { });
        foreach (Engine_UiModalTemplate shipped in Engine_UiModals.Shipped())
        {
            // The removed row survives in the copy (it ships), so compare only what the file states.
            if (Engine_UiModals.KeyOf(shipped) == "0@22")
            {
                continue;
            }

            string key = Engine_UiModals.KeyOf(shipped);
            Engine_UiModalTemplate a = table.Rows.Single(r => r.Key == key).Value;
            Engine_UiModalTemplate b = copy.Rows.Single(r => r.Key == key).Value;
            AssertThat((b.ModalId, b.ScreenId, b.Confirm, b.Cancel, b.Dismissal))
                .IsEqual((a.ModalId, a.ScreenId, a.Confirm, a.Cancel, a.Dismissal));
        }

        AssertThat(copy.Find(13, uint.MaxValue, false)!.Cancel).IsNull();
        AssertThat(json.Contains("\"0@22\"", StringComparison.Ordinal)).IsFalse();
        AssertThat(json.Contains("\"0@28\"", StringComparison.Ordinal)).IsTrue();
    }

    // ---- G.3 / G.4 mod menu commands --------------------------------------------------------

    [TestCase]
    public void MenuCommandsParse()
    {
        Engine_UiModMenuCommand.Parsed opens = Engine_UiModMenuCommand.Parse("Opens:my_mod.menu");
        AssertThat(opens.Verb).IsEqual(Engine_UiModMenuCommand.Verb.Opens);
        AssertThat(opens.Menu).IsEqual("my_mod.menu");

        Engine_UiModMenuCommand.Parsed bare = Engine_UiModMenuCommand.Parse("Mod:lua:my_mod_menu.greet");
        AssertThat(bare.Verb).IsEqual(Engine_UiModMenuCommand.Verb.Lua);
        AssertThat(bare.Function).IsEqual("my_mod_menu.greet");
        AssertThat(bare.Argument).IsNull();

        Engine_UiModMenuCommand.Parsed argued = Engine_UiModMenuCommand.Parse("Mod:lua:give:100:extra");
        AssertThat(argued.Function).IsEqual("give");
        AssertThat(argued.Argument).IsEqual("100:extra");
        AssertThat(Engine_UiModMenuCommand.Parse("Mod:lua:give:").Argument).IsEqual(string.Empty);

        foreach (string notOurs in new[] { "", "Opens:", "Opens:  ", "Mod:lua:", "Mod:lua:9lives", "Mod:lua:a..b", "Mod:lua:a.", "Mod:lua:os.execute()", "Mod:buy:x", "setting:toggle:x" })
        {
            AssertThat(Engine_UiModMenuCommand.Parse(notOurs).Verb)
                .OverrideFailureMessage($"'{notOurs}' parsed as ours").IsEqual(Engine_UiModMenuCommand.Verb.None);
        }

        AssertThat(Engine_UiModMenuCommand.Parse(null).Verb).IsEqual(Engine_UiModMenuCommand.Verb.None);
    }

    [TestCase]
    public void MenuRowsReachTheGuestAsText()
    {
        AssertThat(Engine_UiModMenuCommand.LuaLiteral("it's a \\ path\nnext\u0001")).IsEqual("'it\\'s a \\\\ path\\nnext\\001'");

        string call = Engine_UiModMenuCommand.LuaCall(
            Engine_UiModMenuCommand.Parse("Mod:lua:my_mod_menu.give_gold:100"), "my_mod.menu", "gold", 2, "my_mod");
        AssertThat(call).IsEqual(
            "if Refii and Refii._menu_row then Refii._menu_row('my_mod_menu.give_gold', '100', " +
            "{menu='my_mod.menu',row='gold',player=2,mod='my_mod',command='',button='',subject_kind='',definition='',spell=-1}) end");
        AssertThat(Engine_UiModMenuCommand.LuaCall(Engine_UiModMenuCommand.Parse("Mod:lua:f"), "m", "r", 1, string.Empty))
            .Contains("Refii._menu_row('f', nil, ");

        AssertThat(Engine_UiModMenuCommand.RowCommandLine("my menu", "row", 1, string.Empty))
            .IsEqual("menu_row 1 my_menu row - - - - - -1");
    }

    [TestCase]
    public void MenuRowLuaCompiles()
    {
        string call = Engine_UiModMenuCommand.LuaCall(
            Engine_UiModMenuCommand.Parse("Mod:lua:my_mod_menu.give_gold:it's 100\\n"), "my_mod.menu", "gold", 1, "my_mod");
        foreach ((string label, string source) in new[] { ("runtime", ModLua.MenuRowRuntime), ("call", call) })
        {
            bool ok = Refii.Lua.Engine_LuaDecompiler.TryCheckSource(source, "@" + label, out string error);
            if (!ok && error.Contains("not available", StringComparison.Ordinal))
            {
                return;  // no compiler in this test host: the mount checks the init chunk at run time
            }

            AssertThat(ok).OverrideFailureMessage($"{label} does not compile: {error}").IsTrue();
        }
    }

    // ---- G.6 choice rows ---------------------------------------------------------------------

    [TestCase]
    public void ChoiceRowsStepThroughTheirOptions()
    {
        var options = new[]
        {
            new ModSettingsUi.Option(0f, "Story"),
            new ModSettingsUi.Option(2f, "Hard"),
            new ModSettingsUi.Option(1f, "Normal"),
        };
        var row = new ModSettingsUi.Row("m", "m.difficulty", "Difficulty", ModSettingsUi.Kind.Choice, 0f, 2f, 1f, string.Empty, options);
        AssertThat(row.Choices).IsEqual(3);
        AssertThat(row.ValueAt(1)).IsEqual(2f);
        AssertThat(row.Caption(2)).IsEqual("Normal");
        AssertThat(row.IndexOf(1f)).IsEqual(2);
        AssertThat(row.IndexOf(1.8f)).IsEqual(1);
        AssertThat(row.IndexOf(-5f)).IsEqual(0);
        AssertThat(row.ValueAt(9)).IsEqual(1f);
    }

    [TestCase]
    public void SettingsValidatorCoversChoice()
    {
        Clean(Check("settings.ui.json",
            "{ \"difficulty\": { \"label\": \"Difficulty\", \"type\": \"choice\"," +
            " \"options\": [ { \"value\": 0, \"label\": \"Story\" }, { \"value\": 1, \"label\": \"Hard\" } ] } }"));

        List<ModIssue> issues = Check("settings.ui.json",
            "{ \"a\": { \"type\": \"choice\" }, \"b\": { \"type\": \"choice\", \"options\": [ { \"label\": \"x\" }, 3 ] }," +
            " \"c\": { \"type\": \"choice\", \"min\": 0, \"options\": [ { \"value\": 1 }, { \"value\": 1, \"label\": \"y\" } ] }," +
            " \"d\": { \"type\": \"slider\", \"options\": [] } }");
        HasError(issues, "a", "needs \"options\"");
        HasError(issues, "b.options[0].value", "must be a number");
        HasError(issues, "b.options[1]", "must be an object");
        HasError(issues, "b.options", "no usable option");
        HasWarning(issues, "c", "min, max and step are ignored");
        HasWarning(issues, "c.options[0]", "captioned by its number");
        HasWarning(issues, "c.options[1].value", "a second option");
        HasWarning(issues, "d.options", "only for \"type\": \"choice\"");
    }

    // ---- the templates validator: modals and row commands -----------------------------------

    [TestCase]
    public void TemplatesValidatorCoversModalsAndCommands()
    {
        Clean(Check("ui/templates.json",
            "{ \"modals\": { \"13\": { \"kind\": \"query\", \"confirm\": 39, \"cancel\": 37 }, \"0@22\": { \"kind\": \"message\", \"confirm\": 36 }, \"42\": null }," +
            " \"menus\": { \"m.menu\": { \"title\": \"T\", \"entries\": [ { \"name\": \"a\", \"command\": \"Mod:lua:m.go:1\" }," +
            " { \"name\": \"b\", \"command\": \"Opens:m.sub\" }, { \"name\": \"c\", \"opens\": \"m.sub\" } ] } } }"));

        List<ModIssue> issues = Check("ui/templates.json",
            "{ \"modals\": { \"x\": { \"confirm\": 1 }, \"1\": { \"kind\": \"box\", \"confirm\": 1 }, \"2\": { \"cancel\": 3 }," +
            " \"3\": { \"kind\": \"message\", \"confirm\": 1, \"cancel\": 2 }, \"4\": [], \"5\": { \"confirm\": 1, \"answer\": 2 } }," +
            " \"menus\": { \"m\": { \"rowCommand\": \"Opens:\", \"entries\": [ { \"name\": \"a\", \"command\": \"Mod:lua:not a name\" }," +
            " { \"name\": \"b\", \"opens\": \" \" } ] } } }");
        HasError(issues, "modals.x", "not a modal key");
        HasError(issues, "modals.1", "not a modal kind");
        HasError(issues, "modals.2", "\"confirm\" must be an input code");
        HasError(issues, "modals.3", "one dismiss code");
        HasError(issues, "modals.4", "must be an object");
        HasWarning(issues, "modals.5.answer", "not a field");
        HasError(issues, "menus.m.rowCommand", "names no menu");
        HasError(issues, "menus.m.entries[0].command", "does not name a Lua function");
        HasError(issues, "menus.m.entries[1].opens", "names no menu");
    }

    // ---- the API surface ----------------------------------------------------------------------

    [TestCase]
    public void TheMenuRowEventIsInTheCatalog()
    {
        ModApiEvent row = ModApi.Find("mod_menu_row")!;
        AssertThat(row).IsNotNull();
        AssertThat(row.Name).IsEqual("ModMenuRow");
        AssertThat(row.Cancellable).IsFalse();
        AssertThat(row.Fields.Select(f => f.Name).ToArray()).IsEqual(new[] { "menu", "row", "player", "mod", "command", "button", "subject_kind", "definition", "spell" });
        AssertThat(RefiiHost.SignalNames.Contains("mod_menu_row")).IsTrue();
    }

    [TestCase]
    public void GodotHelpersAreRefiiHostMethods()
    {
        // Each method a helper's signature names (ModApi.GodotMethodNames: signals, query verbs and
        // e.fields are not methods) must be a public method of the singleton under that exact name.
        foreach (ModApiHelper helper in ModApi.GodotHelpers)
        {
            foreach (string name in ModApi.GodotMethodNames(helper.Signature))
            {
                // By name, not GetMethod: a name may carry overloads of different arity
                // (use_item(definition) and use_item(player, definition)), which Godot's
                // bindings tell apart by argument count and GetMethod calls ambiguous.
                AssertThat(typeof(RefiiHost).GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(m => m.Name == name))
                    .OverrideFailureMessage($"RefiiHost has no public method '{name}' ({helper.Signature})")
                    .IsTrue();
            }
        }
    }

    [TestCase]
    public void NewStarterTemplatesValidateClean()
    {
        foreach (string id in new[] { "mod_menu", "hud_text" })
        {
            ModTemplate template = ModTemplates.Find(id)!;
            AssertThat(template).IsNotNull();
            foreach ((string path, string text) in template.Render("my-mod", "My Mod"))
            {
                Clean(Check(path, text));
            }
        }
    }
}
