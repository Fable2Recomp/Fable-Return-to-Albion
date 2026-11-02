namespace Refii.Tests.Modding;

using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// A row mod content puts on one of the game's lists is never stated to the guest as its
/// highlighted row (<see cref="Engine_UiExpandableMenu.Item.FromMod"/>): a patch entry, the rows
/// of a source a mod's entry names, a script-registered source's rows, a mod list's rows. A row
/// the game's own file and sources build keeps being stated. Pure .NET; no Godot type in the host.
/// </summary>
[TestSuite]
public class ModRowProvenanceTests
{
    private const string GameSource = "test.provenance.game";
    private const string ScriptSource = "test.provenance.script";

    private static IReadOnlyList<Engine_UiMenuSources.Row> GuestRows(string argument) => new[]
    {
        new Engine_UiMenuSources.Row("Physique", "Physique", string.Empty, 130, 7u, 0, Committable: false),
    };

    private static IReadOnlyList<Engine_UiMenuSources.Row> ScriptRows(string argument) => new[]
    {
        new Engine_UiMenuSources.Row("my_mod.row", "Mine", string.Empty, 0, 0u, 0, Committable: false),
    };

    /// <summary>A shipped list as the game's file states it, then one mod's patch over it.</summary>
    private static Engine_UiTemplateSet Composed(string modPatchEntries)
    {
        Engine_UiMenuSources.RegisterGame(GameSource, GuestRows);
        var set = new Engine_UiTemplateSet();
        set.ApplyText("{ \"menus\": { \"TestScreenMenu\": { \"entries\": [" +
            "{ \"name\": \"Strength\", \"tag\": \"STR\", \"source\": \"" + GameSource + "\" }," +
            "{ \"name\": \"Back\", \"tag\": \"BACK\" } ] } } }",
            owner: string.Empty, "game", shipped: true);
        set.ApplyText("{ \"menus\": { \"TestScreenMenu\": { \"patch\": true, \"entries\": [" + modPatchEntries + "] } } }",
            "my_mod", "my_mod:ui/templates.json");
        return set;
    }

    private static Engine_UiExpandableMenu.Item Row(Engine_FrontEndMenu menu, string name) =>
        menu.Model.SpawnedItems.Single(i => i.Name == name);

    [TestCase]
    public void AModPatchedRowOnAShippedListIsNeverStated()
    {
        // The mod says hostOnly is false and states an event and no command: it is still the mod's.
        Engine_UiTemplateSet set = Composed(
            "{ \"name\": \"my_mod.extra\", \"tag\": \"EXTRA\", \"eventType\": 130, \"hostOnly\": false, \"after\": \"Strength\" }");
        Engine_UiMenuTemplate template = set.Menus["TestScreenMenu"];
        AssertThat(template.Entry("my_mod.extra")!.FromMod).IsTrue();
        AssertThat(template.Entry("Strength")!.FromMod).IsFalse();

        Engine_FrontEndMenu menu = template.Build();
        Engine_UiExpandableMenu.Item extra = Row(menu, "my_mod.extra");
        AssertThat(extra.FromMod).IsTrue();
        AssertThat(extra.HostOnly).IsFalse();
        AssertThat(extra.GuestHighlightable).IsFalse();

        // The game's rows are untouched, the guest's sourced row among them.
        AssertThat(Row(menu, "Back").GuestHighlightable).IsTrue();
        AssertThat(Row(menu, "Physique").GuestHighlightable).IsTrue();
    }

    [TestCase]
    public void ARestyledGameRowStaysTheGamesAChangedEventDoesNot()
    {
        Engine_UiTemplateSet restyled = Composed("{ \"name\": \"Back\", \"tag\": \"MY_BACK\", \"icon\": \"x.tex\" }");
        AssertThat(restyled.Menus["TestScreenMenu"].Entry("Back")!.FromMod).IsFalse();

        Engine_UiTemplateSet retyped = Composed("{ \"name\": \"Back\", \"tag\": \"BACK\", \"eventType\": 131 }");
        AssertThat(retyped.Menus["TestScreenMenu"].Entry("Back")!.FromMod).IsTrue();
    }

    [TestCase]
    public void AModEntrysSourcedRowsAreTheModsEvenWithRealIds()
    {
        // The same game source the shipped Strength names, put on the list by the mod.
        Engine_UiTemplateSet set = Composed(
            "{ \"name\": \"my_mod.more\", \"tag\": \"MORE\", \"source\": \"" + GameSource + ":more\" }");
        Engine_FrontEndMenu menu = set.Menus["TestScreenMenu"].Build();
        List<Engine_UiExpandableMenu.Item> physique = menu.Model.SpawnedItems.Where(i => i.Name == "Physique").ToList();
        AssertThat(physique.Count).IsEqual(2);
        AssertThat(physique.Single(i => i.Parent?.Name == "Strength").GuestHighlightable).IsTrue();
        AssertThat(physique.Single(i => i.Parent?.Name == "my_mod.more").GuestHighlightable).IsFalse();

        // A mod changing the game's own source makes that source's rows the mod's.
        Engine_UiTemplateSet swapped = Composed(
            "{ \"name\": \"Strength\", \"tag\": \"STR\", \"source\": \"" + GameSource + ":other\" }");
        Engine_UiMenuEntry strength = swapped.Menus["TestScreenMenu"].Entry("Strength")!;
        AssertThat(strength.FromMod).IsFalse();
        AssertThat(strength.SourceFromMod).IsTrue();
    }

    [TestCase]
    public void AScriptRegisteredSourcesRowsAreTheModsWhateverItSays()
    {
        Engine_UiMenuSources.Register(ScriptSource, ScriptRows);
        AssertThat(Engine_UiMenuSources.Rows(ScriptSource, "x").All(r => r.FromMod)).IsTrue();

        Engine_UiMenuSources.RegisterGame(GameSource, GuestRows);
        AssertThat(Engine_UiMenuSources.Rows(GameSource, "x").Any(r => r.FromMod)).IsFalse();

        // On a shipped entry of the game's file, too: the rows are the script's, not the guest's.
        var set = new Engine_UiTemplateSet();
        set.ApplyText("{ \"menus\": { \"TestScriptMenu\": { \"entries\": [" +
            "{ \"name\": \"Mods\", \"tag\": \"MODS\", \"source\": \"" + ScriptSource + "\" } ] } } }",
            owner: string.Empty, "game", shipped: true);
        Engine_FrontEndMenu menu = set.Menus["TestScriptMenu"].Build();
        AssertThat(Row(menu, "Mods").GuestHighlightable).IsTrue();
        AssertThat(Row(menu, "my_mod.row").GuestHighlightable).IsFalse();
    }

    [TestCase]
    public void AModListsRowsAreTheMods()
    {
        List<Engine_UiMenuSources.Row> rows = Engine_UiModRows.ToRows(new[]
        {
            new Engine_UiModRows.Stated("a", "A", "Mod:lua:a", Ghosted: false),
        });
        AssertThat(rows.Single().FromMod).IsTrue();
        AssertThat(rows.Single().Command).IsEqual("Mod:lua:a");
    }

    [TestCase]
    public void TheGamesOwnFileStatesNoModRow()
    {
        Engine_UiTemplateSet game = Engine_UiTemplates.ComposeGame();
        static IEnumerable<Engine_UiMenuEntry> All(IEnumerable<Engine_UiMenuEntry> entries) =>
            entries.SelectMany(e => All(e.Children).Prepend(e));

        AssertThat(game.Menus.Values.SelectMany(m => All(m.Entries)).Any(e => e.FromMod || e.SourceFromMod)).IsFalse();
        AssertThat(game.Menus.Values.Any(m => m.SourceFromMod)).IsFalse();
    }

    [TestCase]
    public void AModRowStillActivatesAsItDidBefore()
    {
        // Provenance changes only the statement: the press dispatches the command, else the name.
        var menu = new Engine_FrontEndMenu();
        menu.Add(new Engine_UiExpandableMenu.Item("my_mod.go", "Go") { Command = "Mod:lua:go", FromMod = true });
        AssertThat(menu.Activate()).IsEqual("Mod:lua:go");

        var bare = new Engine_FrontEndMenu();
        bare.Add(new Engine_UiExpandableMenu.Item("my_mod.bare", "Bare") { FromMod = true });
        AssertThat(bare.Activate()).IsEqual("my_mod.bare");
    }
}
