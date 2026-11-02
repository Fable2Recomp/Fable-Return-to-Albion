namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Mods;
using Refii.Ui;

/// <summary>
/// G.18 #5 and #6: the value sources every UI record reads through (number, mod:, game:, player:
/// keyed to the reader's player, hero:), the HUD's widgets as pieces (ids, the player-two copy,
/// their place, hide, scale and copies), the notice channel, the menu box's paging and answers,
/// and the bus requests that carry a notice and a menu box. Pure .NET; no Godot object in the host.
/// </summary>
[TestSuite]
public class HudPieceModelTests
{
    private static void Near(float actual, float expected) =>
        AssertThat(Math.Abs(actual - expected) < 1e-3f).OverrideFailureMessage($"expected {expected}, got {actual}").IsTrue();

    /// <summary>Readers that answer from fixed tables, the player store per player.</summary>
    private static Engine_UiValueSources.Context Readers(int player = 1) => new()
    {
        Player = player,
        Mod = key => key == "m.gold" ? 1234.5f : null,
        GameMeter = name => name == "PlayerHealth" ? 40f : null,
        GameText = name => name == "QuestTitle" ? "The Hero Of Bower Lake" : null,
        PlayerValue = (p, key) => key == "m.stamina" ? (p == 2 ? 20f : 80f) : null,
        Hero = key => key == "health" ? 50f : 200f,
    };

    // ---- value sources ------------------------------------------------------------------------

    [TestCase]
    public void SourcesParseByPrefix()
    {
        AssertThat(Engine_UiValueSources.Parse("12.5").Kind).IsEqual(Engine_UiValueSources.Kind.Number);
        AssertThat(Engine_UiValueSources.Parse(" mod:m.gold ").Key).IsEqual("m.gold");
        AssertThat(Engine_UiValueSources.Parse("game:QuestTitle").Kind).IsEqual(Engine_UiValueSources.Kind.Game);
        AssertThat(Engine_UiValueSources.Parse("player:m.stamina").Kind).IsEqual(Engine_UiValueSources.Kind.Player);
        AssertThat(Engine_UiValueSources.Parse("hero:health_max").Kind).IsEqual(Engine_UiValueSources.Kind.Hero);
        AssertThat(Engine_UiValueSources.Parse("hero:mana").Kind).IsEqual(Engine_UiValueSources.Kind.Other);
        AssertThat(Engine_UiValueSources.Parse(string.Empty).Kind).IsEqual(Engine_UiValueSources.Kind.Empty);
    }

    [TestCase]
    public void NumbersResolveForTheReadersPlayer()
    {
        Engine_UiValueSources.Context one = Readers();
        Near(Engine_UiValueSources.Number("3", one)!.Value, 3f);
        Near(Engine_UiValueSources.Number("mod:m.gold", one)!.Value, 1234.5f);
        Near(Engine_UiValueSources.Number("game:PlayerHealth", one)!.Value, 40f);
        Near(Engine_UiValueSources.Number("hero:health", one)!.Value, 50f);

        // One record, both players: player two's reader reads player two's value under the same key.
        Near(Engine_UiValueSources.Number("player:m.stamina", one)!.Value, 80f);
        Near(Engine_UiValueSources.Number("player:m.stamina", one.ForPlayer(2))!.Value, 20f);

        // Not stated is null — never 0.
        AssertThat(Engine_UiValueSources.Number("mod:m.none", one)).IsNull();
        AssertThat(Engine_UiValueSources.Number("game:Nothing", one)).IsNull();
        AssertThat(Engine_UiValueSources.Number("player:", one)).IsNull();
        AssertThat(Engine_UiValueSources.Number("health", one)).IsNull();
        AssertThat(Engine_UiValueSources.Number("mod:m.gold", Engine_UiValueSources.Context.None)).IsNull();
    }

    [TestCase]
    public void AStandInAnswersFirstByTheWholeSource()
    {
        Engine_UiValueSources.Context preview = Readers() with
        {
            StandIn = source => source == "player:m.stamina" ? 55f : null,
        };
        Near(Engine_UiValueSources.Number("player:m.stamina", preview)!.Value, 55f);
        Near(Engine_UiValueSources.Number("mod:m.gold", preview)!.Value, 1234.5f);
    }

    [TestCase]
    public void TextReadsTheGamesTextFieldThenFormatsValues()
    {
        Engine_UiValueSources.Context one = Readers();
        string? Tag(string tag) => tag == "MY_TAG" ? "From the book" : null;
        AssertThat(Engine_UiValueSources.Text("game:QuestTitle", string.Empty, one, Tag)).IsEqual("The Hero Of Bower Lake");
        AssertThat(Engine_UiValueSources.Text("game:PlayerHealth", "{0:0}%", one, Tag)).IsEqual("40%");
        AssertThat(Engine_UiValueSources.Text("player:m.stamina", "{0:0}", one.ForPlayer(2), Tag)).IsEqual("20");
        AssertThat(Engine_UiValueSources.Text("MY_TAG", string.Empty, one, Tag)).IsEqual("From the book");
        AssertThat(Engine_UiValueSources.Text("Plain words", string.Empty, one, Tag)).IsEqual("Plain words");
        AssertThat(Engine_UiValueSources.Text("game:Nothing", string.Empty, one, Tag)).IsNull();
        AssertThat(Engine_UiValueSources.Text("  ", string.Empty, one, Tag)).IsNull();

        // The mod-only overload still reads exactly what it did.
        AssertThat(Engine_HudModText.Resolve("mod:m.gold", "{0:0} gold", key => key == "m.gold" ? 1234.5f : null, Tag))
            .IsEqual("1235 gold");
    }

    [TestCase]
    public void RatioIsValueOverCap()
    {
        Engine_UiValueSources.Context two = Readers(2);
        Near(Engine_UiValueSources.Ratio("player:m.stamina/80", two)!.Value, 25f);
        Near(Engine_UiValueSources.Ratio("game:PlayerHealth", two)!.Value, 40f);
        AssertThat(Engine_UiValueSources.Ratio("player:m.stamina/0", two)).IsNull();
        AssertThat(Engine_UiValueSources.Ratio("mod:m.none/100", two)).IsNull();
    }

    [TestCase]
    public void PlayerTwosViewAndWidgetsReadPlayerTwo()
    {
        AssertThat(Engine_UiValueSources.PlayerFor(viewForPlayerTwo: false, ownedByPlayerTwo: false)).IsEqual(1);
        AssertThat(Engine_UiValueSources.PlayerFor(viewForPlayerTwo: false, ownedByPlayerTwo: true)).IsEqual(2);
        AssertThat(Engine_UiValueSources.PlayerFor(viewForPlayerTwo: true, ownedByPlayerTwo: false)).IsEqual(2);
    }

    [TestCase]
    public void TheValidatorsRuleIsTheResolvers()
    {
        AssertThat(Engine_UiValueSources.NumberProblem("game:PlayerHealth")).IsNull();
        AssertThat(Engine_UiValueSources.NumberProblem("player:m.stamina")).IsNull();
        AssertThat(Engine_UiValueSources.NumberProblem("game:")).Contains("names no value");
        AssertThat(Engine_UiValueSources.NumberProblem("health")).Contains("is not a value source");
        AssertThat(Engine_UiValueSources.TextProblem("A literal")).IsNull();
        AssertThat(Engine_UiValueSources.TextProblem("player:")).Contains("names no value");
    }

    // ---- widgets as pieces --------------------------------------------------------------------

    [TestCase]
    public void EveryWidgetHasAnIdAndPlayerTwosCopyIsTheTwo()
    {
        AssertThat(Engine_HudWidgets.All.Select(w => w.Id).Distinct().Count()).IsEqual(Engine_HudWidgets.All.Count);
        foreach (string name in new[] { "WorldIcons", "WorldSprites", "Reticule", "SlowTimeDial" })
        {
            AssertThat(Engine_HudWidgets.Find("widget." + name)!.Player).IsEqual(1);
            AssertThat(Engine_HudWidgets.Find("widget." + name + "2")!.Player).IsEqual(2);
        }

        AssertThat(Engine_HudWidgets.Find("widget.AchievementToast")!.Player).IsEqual(0);
        AssertThat(Engine_HudWidgets.IsWidget("group.PlayerHealth.PlayerHealth")).IsFalse();

        // Player two's seat draws the 2; so does an online joiner's whole window; the toast has no 2.
        AssertThat(Engine_HudWidgets.DeclaredId("Reticule", seatTwo: true, localPlayerTwo: false)).IsEqual("widget.Reticule2");
        AssertThat(Engine_HudWidgets.DeclaredId("Reticule", seatTwo: false, localPlayerTwo: true)).IsEqual("widget.Reticule2");
        AssertThat(Engine_HudWidgets.DeclaredId("Reticule", seatTwo: false, localPlayerTwo: false)).IsEqual("widget.Reticule");
        AssertThat(Engine_HudWidgets.DeclaredId("AchievementToast", seatTwo: false, localPlayerTwo: true)).IsEqual("widget.AchievementToast");
    }

    [TestCase]
    public void WidgetsAreLayoutPieces()
    {
        Engine_HudLayout layout = Engine_HudLayout.Shipped().AddWidgets();
        foreach (Engine_HudWidgets.Widget widget in Engine_HudWidgets.All)
        {
            Engine_HudLayout.Piece piece = layout.Find(widget.Id)!.Value;
            AssertThat(piece.Anchor).IsEqual(widget.Home);
            AssertThat(piece.Offset).IsEqual(Vector2.Zero);
        }

        // Added once however often asked, and moved by a layout entry like any piece.
        int count = layout.Pieces.Count;
        AssertThat(layout.AddWidgets().Pieces.Count).IsEqual(count);
        AssertThat(layout.Reposition("widget.Reticule", Engine_HudLayout.Anchor.TopLeft, new Vector2(10f, 20f))).IsTrue();
    }

    [TestCase]
    public void AWidgetsStateIsItsShiftFromHome()
    {
        var extent = new Vector2(1280f, 720f) / Engine_UiSceneBuilder.PixelsPerUnit;
        var none = new HashSet<string>();
        var scales = new Dictionary<string, float>();

        // At home: no shift, no scale.
        Engine_HudWidgets.State home = Engine_HudWidgets.StateOf(
            "widget.Reticule", none, scales, Engine_HudLayout.Anchor.Centre, Vector2.Zero, extent);
        AssertThat(home.Hidden).IsFalse();
        Near(home.Scale, 1f);
        Near(home.Shift.X, 0f);
        Near(home.Shift.Y, 0f);

        // An offset from home is that many design units (+Y down).
        Engine_HudWidgets.State moved = Engine_HudWidgets.StateOf(
            "widget.Reticule", none, scales, Engine_HudLayout.Anchor.Centre, new Vector2(30f, -40f), extent);
        Near(moved.Shift.X, 30f);
        Near(moved.Shift.Y, -40f);

        // The toast's home is the bottom centre: the same entry is no move for it.
        Engine_HudWidgets.State toast = Engine_HudWidgets.StateOf(
            "widget.AchievementToast", none, scales, Engine_HudLayout.Anchor.BottomCentre, Vector2.Zero, extent);
        Near(toast.Shift.Y, 0f);

        // Hidden and scaled by the mods' statements, by id.
        Engine_HudWidgets.State stated = Engine_HudWidgets.StateOf(
            "widget.SlowTimeDial2", new HashSet<string> { "widget.SlowTimeDial2" },
            new Dictionary<string, float> { ["widget.SlowTimeDial2"] = 1.5f }, Engine_HudLayout.Anchor.Centre, Vector2.Zero, extent);
        AssertThat(stated.Hidden).IsTrue();
        Near(stated.Scale, 1.5f);

        // A copy keeps the original's place plus its own offset, and its own scale where it has one.
        Engine_HudWidgets.State copy = Engine_HudWidgets.CopyState(moved, new Vector2(0f, 34f), scales, "my.copy");
        Near(copy.Shift.Y, -6f);
        Near(copy.Scale, 1f);
        Near(Engine_HudWidgets.CopyState(moved, Vector2.Zero, new Dictionary<string, float> { ["my.copy"] = 2f }, "my.copy").Scale, 2f);
    }

    [TestCase]
    public void SourceKeysAreWholeAndNamed()
    {
        var hud = (JsonObject)JsonNode.Parse(
            "{ \"pieces\": [ { \"id\": \"a\", \"value\": \"player:m.stamina\", \"max\": \"100\" }, " +
            "{ \"id\": \"b\", \"kind\": \"text\", \"text\": \"game:QuestTitle\" }, { \"id\": \"c\", \"value\": \"hero:health\" } ], " +
            "\"clones\": [ { \"id\": \"d\", \"of\": \"x\", \"values\": { \"M\": \"mod:m.v/player:m.cap\" } } ] }")!;
        AssertThat(Engine_HudModEdit.SourceKeys(hud).ToArray()).IsEqual(new[]
        {
            "game:QuestTitle", "mod:m.v", "player:m.cap", "player:m.stamina",
        });
    }

    [TestCase]
    public void TheValidatorReadsWidgetsAndSources()
    {
        List<ModIssue> issues = ModValidate.Validate("ui/hud.json",
            "{ \"hide\": [ \"widget.Reticule2\", \"widget.Nope\" ], \"scale\": { \"widget.AchievementToast\": 1.2 }, " +
            "\"layout\": { \"widget.SlowTimeDial\": { \"anchor\": [0.5, 0.5], \"offset\": [0, -40] } }, " +
            "\"clones\": [ { \"id\": \"c\", \"of\": \"widget.Reticule\", \"offset\": [0, 30], \"hue\": 0.3 } ], " +
            "\"pieces\": [ { \"id\": \"p\", \"value\": \"player:m.stamina\", \"max\": \"game:PlayerHealth\" }, " +
            "{ \"id\": \"t\", \"kind\": \"text\", \"text\": \"game:QuestTitle\", \"format\": \"{0}\" }, " +
            "{ \"id\": \"q\", \"value\": \"game:\" } ] }").ToList();
        string lines = string.Join("\n", issues.Select(i => (i.Error ? "error: " : "warning: ") + i));
        AssertThat(issues.Count(i => i.Error)).OverrideFailureMessage(lines).IsEqual(2);
        AssertThat(issues.Any(i => i.Error && i.Message.Contains("'widget.Nope' is not a widget"))).OverrideFailureMessage(lines).IsTrue();
        AssertThat(issues.Any(i => i.Error && i.Pointer == "pieces[2].value" && i.Message.Contains("names no value")))
            .OverrideFailureMessage(lines).IsTrue();
        AssertThat(issues.Any(i => !i.Error && i.Pointer == "clones[0].hue" && i.Message.Contains("copy of a widget")))
            .OverrideFailureMessage(lines).IsTrue();
    }

    // ---- groups and settings -------------------------------------------------------------------

    [TestCase]
    public void SettingSourcesReadTheSettingInForce()
    {
        Engine_UiValueSources.Context readers = Readers() with { Setting = key => key == "m.size" ? 1.4f : null };
        AssertThat(Engine_UiValueSources.Parse("setting:m.size").Kind).IsEqual(Engine_UiValueSources.Kind.Setting);
        AssertThat(Engine_UiValueSources.Parse("setting:m.size").Named).IsTrue();
        AssertThat(Engine_UiValueSources.LooksLikeSource("setting:m.size")).IsTrue();
        Near(Engine_UiValueSources.Number("setting:m.size", readers)!.Value, 1.4f);
        AssertThat(Engine_UiValueSources.Number("setting:m.none", readers)).IsNull();
        AssertThat(Engine_UiValueSources.Number("setting:m.size", Readers())).IsNull();
        AssertThat(Engine_UiValueSources.Text("setting:m.size", "0.0", readers, _ => null)).IsEqual("1.4");
        AssertThat(Engine_UiValueSources.NumberProblem("setting:m.size")).IsNull();
        AssertThat(Engine_UiValueSources.NumberProblem("setting:")).Contains("names no value");
    }

    private static Engine_HudModGroups.Group Bar(string scale, string x, string y) =>
        new("m.bar", "m", Engine_HudLayout.Anchor.BottomCentre, new Vector2(0f, -12f), scale, x, y);

    [TestCase]
    public void AGroupReadsItsValuesForTheMembersPlayer()
    {
        Engine_UiValueSources.Context one = Readers() with { Setting = key => key == "m.size" ? 1.5f : key == "m.lift" ? 40f : null };

        Engine_HudModGroups.Transform moved = Engine_HudModGroups.Resolve(Bar("setting:m.size", "0", "-setting:m.lift"), one);
        Near(moved.Scale, 1.5f);
        Near(moved.Offset.X, 0f);
        Near(moved.Offset.Y, -40f);

        // player: is the member's player's: player two's half reads player two's value.
        Near(Engine_HudModGroups.Resolve(Bar("1", "player:m.stamina", "0"), one).Offset.X, 80f);
        Near(Engine_HudModGroups.Resolve(Bar("1", "player:m.stamina", "0"), one.ForPlayer(2)).Offset.X, 20f);

        // Unstated, or a scale not above 0, is the identity — the pieces still draw where placed.
        Engine_HudModGroups.Transform none = Engine_HudModGroups.Resolve(Bar("setting:m.none", "mod:m.none", string.Empty), one);
        AssertThat(none).IsEqual(Engine_HudModGroups.Transform.Identity);
        Near(Engine_HudModGroups.Resolve(Bar("0", string.Empty, string.Empty), one).Scale, 1f);
        Near(Engine_HudModGroups.Resolve(Bar("-2", string.Empty, string.Empty), one).Scale, 1f);

        // A leading - negates a source; a negative literal is a number.
        Near(Engine_HudModGroups.Number("-mod:m.gold", one)!.Value, -1234.5f);
        Near(Engine_HudModGroups.Number("-12", one)!.Value, -12f);
        AssertThat(Engine_HudModGroups.Number("-mod:m.none", one)).IsNull();
        AssertThat(Engine_HudModGroups.Problem("-setting:m.lift")).IsNull();
        AssertThat(Engine_HudModGroups.Problem("-setting:")).Contains("names no value");
        AssertThat(Engine_HudModGroups.Problem("-lift")).Contains("is not a value source");
    }

    [TestCase]
    public void AGroupScalesAboutItsPivotThenOffsets()
    {
        var extent = new Vector2(1280f, 720f);
        float unit = Engine_HudLayout.Scale(extent);
        Engine_HudModGroups.Group bar = Bar("1", "0", "0");
        Vector2 pivot = Engine_HudLayout.Place(new Engine_HudLayout.Piece("p", string.Empty, Engine_HudLayout.Anchor.BottomCentre, new Vector2(0f, -12f)), extent);
        Vector2 point = Engine_HudLayout.Place(new Engine_HudLayout.Piece("q", string.Empty, Engine_HudLayout.Anchor.BottomCentre, new Vector2(-100f, -60f)), extent);

        // The identity leaves a point where it was; the pivot never moves under a scale.
        Vector2 still = Engine_HudModGroups.Apply(point, bar, Engine_HudModGroups.Transform.Identity, extent);
        Near(still.X, point.X);
        Near(still.Y, point.Y);
        Vector2 fixedPivot = Engine_HudModGroups.Apply(pivot, bar, new Engine_HudModGroups.Transform(1.6f, Vector2.Zero), extent);
        Near(fixedPivot.X, pivot.X);
        Near(fixedPivot.Y, pivot.Y);

        // Twice the size: twice as far from the pivot (canvas +Y up, the layout's +Y down).
        Vector2 grown = Engine_HudModGroups.Apply(point, bar, new Engine_HudModGroups.Transform(2f, Vector2.Zero), extent);
        Near(grown.X - pivot.X, -200f * unit);
        Near(grown.Y - pivot.Y, 96f * unit);

        // The offset is reference units, +Y down: -40 raises the point by 40.
        Vector2 raised = Engine_HudModGroups.Apply(point, bar, new Engine_HudModGroups.Transform(1f, new Vector2(0f, -40f)), extent);
        Near(raised.X, point.X);
        Near(raised.Y - point.Y, 40f * unit);
    }

    [TestCase]
    public void TheValidatorReadsGroupsAndRims()
    {
        List<ModIssue> issues = ModValidate.Validate("ui/hud.json",
            "{ \"groups\": { \"g\": { \"anchor\": [0.5, 1], \"pivot\": [0, -12], \"scale\": \"setting:m.size\", \"offset\": [0, \"-setting:m.lift\"] }, " +
            "\"bad\": { \"scale\": \"size\", \"offset\": [0] } }, " +
            "\"pieces\": [ { \"id\": \"t\", \"kind\": \"text\", \"text\": \"player:m.n\", \"outline\": \"#000000d9\", \"outlineWidth\": 1.5, \"group\": \"g\" }, " +
            "{ \"id\": \"p\", \"kind\": \"panel\", \"group\": \"nope\", \"outline\": \"#000\" }, " +
            "{ \"id\": \"s\", \"kind\": \"text\", \"text\": \"x\", \"shadowOffset\": [1, 1] } ] }").ToList();
        string lines = string.Join("\n", issues.Select(i => (i.Error ? "error: " : "warning: ") + i.Pointer + " " + i.Message));
        AssertThat(issues.Count(i => i.Error)).OverrideFailureMessage(lines).IsEqual(2);
        AssertThat(issues.Any(i => i.Error && i.Pointer == "groups.bad.scale" && i.Message.Contains("is not a value source")))
            .OverrideFailureMessage(lines).IsTrue();
        AssertThat(issues.Any(i => i.Error && i.Pointer == "groups.bad.offset")).OverrideFailureMessage(lines).IsTrue();
        AssertThat(issues.Any(i => !i.Error && i.Pointer == "pieces[1].group" && i.Message.Contains("'nope'"))).OverrideFailureMessage(lines).IsTrue();
        AssertThat(issues.Any(i => !i.Error && i.Pointer == "pieces[1].outline" && i.Message.Contains("\"text\""))).OverrideFailureMessage(lines).IsTrue();
        AssertThat(issues.Any(i => !i.Error && i.Pointer == "pieces[2].shadowOffset" && i.Message.Contains("\"shadow\""))).OverrideFailureMessage(lines).IsTrue();
        AssertThat(issues.Any(i => i.Pointer.StartsWith("pieces[0]", StringComparison.Ordinal) || i.Pointer.StartsWith("groups.g", StringComparison.Ordinal)))
            .OverrideFailureMessage(lines).IsFalse();
    }

    // ---- notices --------------------------------------------------------------------------------

    [TestCase]
    public void NoticesShowOneAtATimeAndSayHowTheyWent()
    {
        Engine_UiNotices.Clear();
        var outcomes = new List<(uint, int)>();
        Engine_UiNotices.Post(1u, 1, "Title", "Text", 0f, done: o => outcomes.Add((1u, o)));
        Engine_UiNotices.Post(2u, 2, string.Empty, "Second", 3f, done: o => outcomes.Add((2u, o)));

        Engine_UiNotices.Notice first = Engine_UiNotices.TakeNext()!;
        AssertThat(first.Body).IsEqual("Title\n\nText");
        Near(first.Seconds, Engine_UiNotices.DefaultSeconds);
        AssertThat(Engine_UiNotices.TakeNext()).IsNull();

        Engine_UiNotices.Finish(0);
        Engine_UiNotices.Notice second = Engine_UiNotices.TakeNext()!;
        Near(second.Seconds, 3f);
        AssertThat(second.Player).IsEqual(2);

        Engine_UiNotices.Clear();
        AssertThat(outcomes.ToArray()).IsEqual(new[] { (1u, 0), (2u, -1) });
        AssertThat(Engine_UiNotices.Current).IsNull();
    }

    // ---- menu boxes -----------------------------------------------------------------------------

    [TestCase]
    public void MenuBoxRowsPagePastTheBox()
    {
        AssertThat(Engine_UiMenuBoxDialog.Page(4, 0, 6).Select(r => r.Option).ToArray()).IsEqual(new[] { 0, 1, 2, 3 });
        AssertThat(Engine_UiMenuBoxDialog.PageCount(6, 6)).IsEqual(1);
        AssertThat(Engine_UiMenuBoxDialog.PageCount(7, 6)).IsEqual(2);
        AssertThat(Engine_UiMenuBoxDialog.PageCount(13, 6)).IsEqual(3);

        // Every option appears exactly once across the pages, in order, whatever the count.
        foreach (int count in new[] { 1, 6, 7, 10, 11, 12, 13, 16 })
        {
            var seen = new List<int>();
            int pages = Engine_UiMenuBoxDialog.PageCount(count, 6);
            for (int page = 0; page < pages; page++)
            {
                List<Engine_UiMenuBoxDialog.Row> rows = Engine_UiMenuBoxDialog.Page(count, page, 6);
                AssertThat(rows.Count <= 6).IsTrue();
                AssertThat(rows.Any(r => r.Previous)).IsEqual(page > 0);
                AssertThat(rows.Any(r => r.Next)).IsEqual(page < pages - 1);
                seen.AddRange(rows.Where(r => !r.Next && !r.Previous).Select(r => r.Option));
            }

            AssertThat(seen.ToArray()).OverrideFailureMessage($"count {count}").IsEqual(Enumerable.Range(0, count).ToArray());
        }
    }

    [TestCase]
    public void AMenuBoxAnswersTheOptionsIndexAcrossPages()
    {
        int answer = -99;
        var options = Enumerable.Range(0, 9).Select(i => $"Option {i}").ToList();
        Engine_UiModMenuBox box = Engine_UiModMenuBox.Open(7u, 2, "Pick one", options, cancelable: true, a => answer = a);
        AssertThat(Engine_UiModMenuBox.Active).IsSame(box);
        AssertThat(box.Menu!.Options.Count).IsEqual(Engine_UiModMenuBox.RowsPerPage);
        AssertThat(box.Menu.Options[^1].Label).IsEqual(Engine_UiModMenuBox.NextPageLabel);

        // Down to "Next page" (wrapping up from the first row), A, then A on the first row there.
        box.Drive(1.0, -1, false, false);
        box.Drive(1.0, 0, true, false);
        AssertThat(box.Menu!.Options[0].Label).IsEqual(Engine_UiModMenuBox.PreviousPageLabel);
        box.Drive(1.0, 1, false, false);
        box.Drive(1.0, 0, true, false);
        AssertThat(answer).IsEqual(Engine_UiModMenuBox.RowsPerPage - 1);
        AssertThat(Engine_UiModMenuBox.Active).IsNull();
        AssertThat(box.IsOpen).IsFalse();
    }

    [TestCase]
    public void AMenuBoxIsCancelledByBAndReplacedByTheNext()
    {
        int first = -99, second = -99;
        Engine_UiModMenuBox one = Engine_UiModMenuBox.Open(1u, 1, "One", new[] { "A", "B" }, cancelable: true, a => first = a);
        Engine_UiModMenuBox two = Engine_UiModMenuBox.Open(2u, 1, "Two", new[] { "A", "B" }, cancelable: false, a => second = a);
        AssertThat(first).IsEqual(-1);
        AssertThat(one.IsOpen).IsFalse();

        // B does nothing on an uncancelable box; Cancel takes it down as -1.
        two.Drive(1.0, 0, false, true);
        AssertThat(second).IsEqual(-99);
        two.Cancel();
        AssertThat(second).IsEqual(-1);

        int third = -99;
        Engine_UiModMenuBox three = Engine_UiModMenuBox.Open(3u, 1, "Three", new[] { "A" }, cancelable: true, a => third = a);
        three.Drive(1.0, 0, false, true);
        AssertThat(third).IsEqual(-1);
    }

    // ---- the bus ------------------------------------------------------------------------------

    private static IEnumerable<KeyValuePair<string, object>> Fields(params (string Key, object Value)[] fields) =>
        fields.Select(f => new KeyValuePair<string, object>(f.Key, f.Value));

    [TestCase]
    public void NoticeAndMenuBoxRequestsParse()
    {
        Engine_UiModRequest.Request notice = Engine_UiModRequest.Parse(Fields(
            ("op", "notice"), ("id", 5L), ("player", 2L), ("title", "T"), ("text", "a\\nb"), ("seconds", 2.5)))!.Value;
        AssertThat(notice.Op).IsEqual(Engine_UiModRequest.Op.Notice);
        AssertThat(notice.Text).IsEqual("a\nb");
        Near(notice.Seconds, 2.5f);
        AssertThat(Engine_UiModRequest.Parse(Fields(("op", "notice"), ("id", 5L)))).IsNull();

        Engine_UiModRequest.Request menu = Engine_UiModRequest.Parse(Fields(
            ("op", "menu_box"), ("id", 9L), ("player", 1L), ("title", "Which?"), ("text", string.Empty), ("cancel", 0L),
            ("count", 3L), ("o0", "Red"), ("o1", "Green"), ("o2", "Blue")))!.Value;
        AssertThat(menu.Op).IsEqual(Engine_UiModRequest.Op.MenuBox);
        AssertThat(menu.Options!.ToArray()).IsEqual(new[] { "Red", "Green", "Blue" });
        AssertThat(menu.Cancelable).IsFalse();
        AssertThat(Engine_UiModRequest.Parse(Fields(("op", "menu_box"), ("id", 9L), ("count", 0L)))).IsNull();

        AssertThat(Engine_UiModRequest.NoticeCommandLine(5u, 0, 2)).IsEqual("notice_done 5 0 2");
        AssertThat(Engine_UiModRequest.MenuBoxCommandLine(9u, -1, 1)).IsEqual("menu_box_answer 9 -1 1");
    }

    [TestCase]
    public void TheBoxEventsAreOnEverySurface()
    {
        foreach ((string name, string field) in new[] { ("mod_notice", "outcome"), ("mod_menu_box", "option") })
        {
            ModApiEvent e = ModApi.Find(name)!;
            AssertThat(e.Fields.Select(f => f.Name).ToArray()).IsEqual(new[] { "id", field, "player" });
            AssertThat(RefiiHost.SignalNames.Contains(name)).IsTrue();
        }

        AssertThat(ModApi.Find("mod_notice")!.Kind).IsEqual(89);
        AssertThat(ModApi.Find("mod_menu_box")!.Kind).IsEqual(90);
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        AssertThat(native.Contains("refii_mod_notice(", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_menu_box(", StringComparison.Ordinal)).IsTrue();
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        AssertThat(lua.Contains("Refii.notice(", StringComparison.Ordinal) && lua.Contains("Refii.menu_box(", StringComparison.Ordinal)).IsTrue();
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        AssertThat(godot.Contains("notice(", StringComparison.Ordinal) && godot.Contains("menu_box(", StringComparison.Ordinal)).IsTrue();
    }
}
