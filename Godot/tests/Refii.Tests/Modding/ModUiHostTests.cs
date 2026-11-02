namespace Refii.Tests.Modding;

using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;
using Refii.Ui;

/// <summary>
/// The host-owned UI a mod reaches from every surface (Part G.14–G.21): the native and Lua UI
/// requests as they cross the bus, which machine shows a player's menu or box, the box answer's
/// guest command, the ModMessageBox event in the catalog, the options page as the options book and
/// the Studio both paint it, and the HUD snapshot's layer. Pure .NET; no Godot type in the host.
/// </summary>
[TestSuite]
public class ModUiHostTests
{
    private static Engine_UiModRequest.Request? Parse(string fields) =>
        Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain(fields));

    [TestCase]
    public void OpenMenuRequestsParse()
    {
        Engine_UiModRequest.Request? open = Parse("op='open_menu',player=2,name='my_mod.menu'");
        AssertThat(open.HasValue).IsTrue();
        AssertThat(open!.Value.Op).IsEqual(Engine_UiModRequest.Op.OpenMenu);
        AssertThat(open.Value.Player).IsEqual(2);
        AssertThat(open.Value.Name).IsEqual("my_mod.menu");

        // Player 0 is whoever owns the menus here; anything else is clamped into 0..2.
        AssertThat(Parse("op='open_menu',player=0,name='m'")!.Value.Player).IsEqual(0);
        AssertThat(Parse("op='open_menu',player=7,name='m'")!.Value.Player).IsEqual(2);
        AssertThat(Parse("op='open_menu',player=1,name=''").HasValue).IsFalse();
        AssertThat(Parse("op='close',name='m'").HasValue).IsFalse();
        AssertThat(Parse(string.Empty).HasValue).IsFalse();
    }

    [TestCase]
    public void MessageBoxRequestsParse()
    {
        Engine_UiModRequest.Request? notice = Parse(
            "op='message_box',id=12,player=1,title='Saved',text='Line one\\nLine, two',accept='',cancel='',buttons=1");
        AssertThat(notice.HasValue).IsTrue();
        AssertThat(notice!.Value.Op).IsEqual(Engine_UiModRequest.Op.MessageBox);
        AssertThat(notice.Value.Id).IsEqual(12u);
        AssertThat(notice.Value.Question).IsFalse();
        AssertThat(notice.Value.Text).IsEqual("Line one\nLine, two");
        AssertThat(notice.Value.Accept).IsNull();
        AssertThat(notice.Value.Cancel).IsNull();

        Engine_UiModRequest.Request? question = Parse(
            "op='message_box',id=13,player=0,title='',text='Sure?',accept='Yes',cancel='',buttons=2");
        AssertThat(question!.Value.Question).IsTrue();
        AssertThat(question.Value.Accept).IsEqual("Yes");
        AssertThat(question.Value.Cancel).IsNull();

        // A notice drops a cancel caption; a box needs an id and some text.
        AssertThat(Parse("op='message_box',id=14,title='t',text='',cancel='No',buttons=1")!.Value.Cancel).IsNull();
        AssertThat(Parse("op='message_box',id=0,title='t',text='x',buttons=1").HasValue).IsFalse();
        AssertThat(Parse("op='message_box',id=15,title='',text='',buttons=1").HasValue).IsFalse();
        AssertThat(Parse("op='message_box',id=16777216,title='t',buttons=1").HasValue).IsFalse();
    }

    [TestCase]
    public void TheBodyAndTheAnswerCommand()
    {
        AssertThat(Engine_UiModRequest.Body("Title", "Text")).IsEqual("Title\n\nText");
        AssertThat(Engine_UiModRequest.Body(string.Empty, "Text")).IsEqual("Text");
        AssertThat(Engine_UiModRequest.Body("Title", string.Empty)).IsEqual("Title");
        AssertThat(Engine_UiModRequest.AnswerCommandLine(8388609u, -1, 2)).IsEqual("box_answer 8388609 -1 2");

        // The two sides' ids never collide and stay exact as a Lua number (a 32-bit float).
        AssertThat(Engine_UiModRequest.FirstHostBoxId).IsEqual(1u << 23);
        AssertThat(Engine_UiModRequest.LastBoxId).IsEqual((1u << 24) - 1);
        AssertThat((uint)(float)Engine_UiModRequest.LastBoxId).IsEqual(Engine_UiModRequest.LastBoxId);
    }

    [TestCase]
    public void EachMachineShowsThePlayersItPlays()
    {
        // Alone, hosting with nobody joined, and in local co-op: both players are here.
        foreach (int p in new[] { 0, 1, 2 })
        {
            AssertThat(Engine_UiModRequest.PlayedHere(p, client: false, peerStanding: false)).IsTrue();
        }

        // An online host plays player one; the joiner player two. 0 is always this machine's owner.
        AssertThat(Engine_UiModRequest.PlayedHere(1, client: false, peerStanding: true)).IsTrue();
        AssertThat(Engine_UiModRequest.PlayedHere(2, client: false, peerStanding: true)).IsFalse();
        AssertThat(Engine_UiModRequest.PlayedHere(2, client: true, peerStanding: true)).IsTrue();
        AssertThat(Engine_UiModRequest.PlayedHere(1, client: true, peerStanding: true)).IsFalse();
        AssertThat(Engine_UiModRequest.PlayedHere(0, client: true, peerStanding: true)).IsTrue();
    }

    [TestCase]
    public void TheMessageBoxEventIsOnEverySurface()
    {
        ModApiEvent box = ModApi.Find("mod_message_box")!;
        AssertThat(box.Name).IsEqual("ModMessageBox");
        AssertThat(box.Kind).IsEqual(88);
        AssertThat(string.Join(",", box.Fields.Select(f => f.Name))).IsEqual("id,button,player");
        AssertThat(RefiiHost.SignalNames.Contains("mod_message_box")).IsTrue();

        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        AssertThat(native.Contains("refii_mod_open_menu(", System.StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_message_box(", System.StringComparison.Ordinal)).IsTrue();
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        AssertThat(lua.Contains("Refii.open_menu(", System.StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.message_box(", System.StringComparison.Ordinal)).IsTrue();
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        AssertThat(godot.Contains("open_menu(name, player", System.StringComparison.Ordinal)).IsTrue();
        AssertThat(godot.Contains("message_box(title, text, buttons, player", System.StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>A page of seven pickbox rows, three captions each: longer than the five-row window.</summary>
    private sealed class SevenRows : Engine_UiHostOptionsPage
    {
        public override string Name => "test:seven";
        public override string Title => "Seven";
        public override int Count => 7;
        public override string RowTitle(int row) => $"Row {row}";
        public override int Choices(int row) => 3;
        public override string Caption(int row, int index) => $"{row}.{index}";
        public override int[] Read() => new int[7];
    }

    [TestCase]
    public void TheOptionsPagePaintsOneWayForTheGameAndTheStudio()
    {
        var page = new SevenRows();

        // The list level's preview: the window at the top, no prompts, every row lit.
        Engine_UiHostOptionsPage.Painted preview = page.Paint(row => row % 3, open: false, cursor: 0);
        AssertThat(preview.Values[Engine_UiHostOptionsPage.TitleField]).IsEqual("Seven (1-5/7)");
        AssertThat(preview.Values["Options.Subtitles"]).IsEqual("Row 0");
        AssertThat(preview.Values["Options.Subtitles_pickbox.grp.text"]).IsEqual("0.0");
        AssertThat(preview.Active[Engine_UiHostOptionsPage.Buttons]).IsFalse();
        AssertThat(preview.Active["Foreground.Book.Pages.Page1"]).IsTrue();
        AssertThat(Engine_UiHostOptionsPage.Rows.All(r => preview.Opacity[r] == 1f)).IsTrue();

        // The open page with the cursor on the last row: the window scrolled so it is the fifth,
        // the prompts up, every row but the cursor's dimmed.
        Engine_UiHostOptionsPage.Painted open = page.Paint(row => row % 3, open: true, cursor: 6);
        AssertThat(open.Values[Engine_UiHostOptionsPage.TitleField]).IsEqual("Seven (3-7/7)");
        AssertThat(open.Values["Options.Subtitles"]).IsEqual("Row 2");
        AssertThat(open.Values["Options.Auto_joinable_pickbox.grp.text"]).IsEqual("6.0");
        AssertThat(open.Active[Engine_UiHostOptionsPage.Buttons]).IsTrue();
        AssertThat(open.Active[Engine_UiHostOptionsPage.Buttons + ".Learn.Done"]).IsTrue();
        AssertThat(open.Opacity[Engine_UiHostOptionsPage.Rows[4]]).IsEqual(1f);
        AssertThat(open.Opacity[Engine_UiHostOptionsPage.Rows[0]]).IsEqual(0.6f);
    }

    [TestCase]
    public void TheHudSnapshotStatesItsLayer()
    {
        AssertThat(Engine_HudLayoutEdit.SnapshotIsBeneath("{\"layer\":\"shipped+mods\",\"pieces\":{}}")).IsTrue();
        AssertThat(Engine_HudLayoutEdit.SnapshotIsBeneath("{\"pieces\":{}}")).IsFalse();
        AssertThat(Engine_HudLayoutEdit.SnapshotIsBeneath("{\"layer\":3}")).IsFalse();
        AssertThat(Engine_HudLayoutEdit.SnapshotIsBeneath(string.Empty)).IsFalse();
        AssertThat(Engine_HudLayout.SnapshotLayer).IsEqual("shipped+mods");
    }

    [TestCase]
    public void AMenuRowsLuaCallCarriesItsPlayer()
    {
        Engine_UiModMenuCommand.Parsed command = Engine_UiModMenuCommand.Parse("Mod:lua:my_mod.on_row:7");
        string chunk = Engine_UiModMenuCommand.LuaCall(command, "my_menu", "Row", 2, "my_mod");
        AssertThat(chunk.Contains(",player=2,", System.StringComparison.Ordinal)).IsTrue();
        AssertThat(Engine_UiModMenuCommand.RowCommandLine("my menu", "Row", 2, string.Empty))
            .IsEqual("menu_row 2 my_menu Row - - - - - -1");
    }
}
