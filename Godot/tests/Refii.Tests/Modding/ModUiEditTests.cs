namespace Refii.Tests.Modding;

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The shared UI editors' pure halves (Part G.8): HUD placement — presets, re-anchoring in place,
/// a drag, the "what changed" diff and the player file's round-trip — and menu editing — deep
/// equality as the loaders read JSON, the row tree's moves, changed-record detection, the
/// player file's merge, the row command check, and the patch diff, applied back with the engine's
/// documented patch rules to prove it reproduces the edit. Pure .NET; no Godot type in the host.
/// </summary>
[TestSuite]
public class ModUiEditTests
{
    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static JsonArray Arr(string json) => (JsonArray)JsonNode.Parse(json)!;

    private static void Near(float actual, float expected) =>
        AssertThat(System.Math.Abs(actual - expected) < 1e-3f)
            .OverrideFailureMessage($"expected {expected}, got {actual}")
            .IsTrue();

    // ---- HUD placement ------------------------------------------------------------------------

    [TestCase]
    public void PresetsAreTheNineAnchors()
    {
        AssertThat(Engine_HudLayoutEdit.Presets.Length).IsEqual(9);
        AssertThat(Engine_HudLayoutEdit.PresetOf(1f, 1f)).IsEqual(8);
        AssertThat(Engine_HudLayoutEdit.PresetOf(0.5f, 0f)).IsEqual(1);
        AssertThat(Engine_HudLayoutEdit.PresetOf(0.3f, 0f)).IsEqual(-1);
        AssertThat(Engine_HudLayoutEdit.NearestPreset(0.9f, 0.1f)).IsEqual(2);
    }

    [TestCase]
    public void RebaseKeepsThePieceWhereItStands()
    {
        var move = new Engine_HudModLayout.Move(0f, 0f, 100f, 50f);
        const float width = 1266f, height = 712f, scale = 1f;
        Engine_HudModLayout.Move rebased = Engine_HudLayoutEdit.Rebase(move, 1f, 1f, width, height, scale);
        Near(rebased.AnchorX, 1f);
        Near(rebased.OffsetX, 100f - width);
        Near(rebased.OffsetY, 50f - height);

        // The same point: anchor × extent + offset × scale.
        Near(rebased.AnchorX * width + rebased.OffsetX * scale, move.AnchorX * width + move.OffsetX * scale);
        Near(rebased.AnchorY * height + rebased.OffsetY * scale, move.AnchorY * height + move.OffsetY * scale);
    }

    [TestCase]
    public void SnapTakesTheNearestAnchorInPlace()
    {
        // A piece dragged near the bottom-right corner follows that corner.
        var move = new Engine_HudModLayout.Move(0f, 0f, 1200f, 680f);
        Engine_HudModLayout.Move snapped = Engine_HudLayoutEdit.Snap(move, 1266f, 712f, 1f);
        Near(snapped.AnchorX, 1f);
        Near(snapped.AnchorY, 1f);
        Near(snapped.OffsetX, 1200f - 1266f);
        Near(snapped.OffsetY, 680f - 712f);
    }

    [TestCase]
    public void DragMovesTheOffsetByTheScale()
    {
        Engine_HudModLayout.Move dragged = Engine_HudLayoutEdit.Drag(new Engine_HudModLayout.Move(0.5f, 1f, 10f, -20f), 30f, -12f, 0.5f);
        Near(dragged.AnchorX, 0.5f);
        Near(dragged.OffsetX, 70f);
        Near(dragged.OffsetY, -44f);
    }

    [TestCase]
    public void LayoutDiffNamesOnlyWhatChanged()
    {
        var loaded = new Dictionary<string, Engine_HudModLayout.Move>
        {
            ["kept"] = new(0f, 0f, 1f, 2f),
            ["moved"] = new(0f, 0f, 1f, 2f),
            ["dropped"] = new(1f, 1f, 0f, 0f),
        };
        var edited = new Dictionary<string, Engine_HudModLayout.Move>
        {
            ["kept"] = new(0f, 0f, 1.0001f, 2f),
            ["moved"] = new(0f, 0f, 5f, 2f),
            ["added"] = new(0.5f, 0f, 0f, 30f),
        };
        Engine_HudLayoutEdit.Changes changes = Engine_HudLayoutEdit.Diff(loaded, edited);
        AssertThat(changes.Set.Keys.OrderBy(k => k).ToArray()).IsEqual(new[] { "added", "moved" });
        AssertThat(changes.Removed.ToArray()).IsEqual(new[] { "dropped" });
        AssertThat(Engine_HudLayoutEdit.Diff(loaded, loaded).Empty).IsTrue();
    }

    [TestCase]
    public void ThePlayersFileKeepsEveryOtherEntryAndField()
    {
        string file = "{ \"note\": \"mine\", \"pieces\": { " +
            "\"untouched\": { \"anchor\": [0, 0], \"offset\": [1, 1], \"art\": \"X\" }, " +
            "\"moved\": { \"anchor\": [0, 0], \"offset\": [1, 1], \"art\": \"Y\" }, " +
            "\"dropped\": { \"anchor\": [1, 1], \"offset\": [0, 0] } } }";
        var changes = new Engine_HudLayoutEdit.Changes(
            new Dictionary<string, Engine_HudModLayout.Move> { ["moved"] = new(1f, 0f, -64f, 32f), ["new"] = new(0.5f, 1f, 0f, -80f) },
            new[] { "dropped" });
        string written = Engine_HudLayoutEdit.Apply(file, Engine_HudModLayout.PlayerSection, changes);

        JsonObject root = Obj(written);
        AssertThat(root["note"]!.GetValue<string>()).IsEqual("mine");
        JsonObject pieces = root["pieces"]!.AsObject();
        AssertThat(pieces.ContainsKey("dropped")).IsFalse();
        AssertThat(Engine_UiMenuEdit.Same(pieces["untouched"], Obj("{ \"anchor\": [0, 0], \"offset\": [1, 1], \"art\": \"X\" }"))).IsTrue();
        AssertThat(pieces["moved"]!["art"]!.GetValue<string>()).IsEqual("Y");

        // What the game reads back is exactly the moves.
        Dictionary<string, Engine_HudModLayout.Move> read = Engine_HudModLayout.ParsePlayerFile(written);
        AssertThat(read["moved"]).IsEqual(new Engine_HudModLayout.Move(1f, 0f, -64f, 32f));
        AssertThat(read["new"]).IsEqual(new Engine_HudModLayout.Move(0.5f, 1f, 0f, -80f));
        AssertThat(read.Count).IsEqual(3);

        // A mod's file takes the same entries under "layout".
        string mod = Engine_HudLayoutEdit.Apply("{ \"pieces\": [] }", Engine_HudModLayout.Section, changes);
        AssertThat(Engine_HudModLayout.ParseModFile(mod)["new"]).IsEqual(new Engine_HudModLayout.Move(0.5f, 1f, 0f, -80f));
        AssertThat(Obj(mod)["pieces"] is JsonArray).IsTrue();
    }

    // ---- the mod's ui/hud.json as editor-API calls -------------------------------------------

    [TestCase]
    public void HudDiffMakesOneCallPerChangedEntry()
    {
        JsonObject before = Obj("{ \"pieces\": [ { \"id\": \"a\", \"kind\": \"meter\" }, { \"id\": \"b\", \"kind\": \"text\" } ], " +
            "\"clones\": [ { \"id\": \"c\", \"of\": \"x\" } ], \"hide\": [\"h1\", \"h2\"], \"scale\": { \"s\": 1.5, \"t\": 2 }, " +
            "\"layout\": { \"l\": { \"anchor\": [0, 0], \"offset\": [1, 1] } } }");
        JsonObject after = Obj("{ \"pieces\": [ { \"id\": \"a\", \"kind\": \"meter\" }, { \"id\": \"n\", \"kind\": \"panel\" } ], " +
            "\"clones\": [ { \"id\": \"c\", \"of\": \"x\", \"hue\": 0.5 } ], \"hide\": [\"h2\", \"h3\"], \"scale\": { \"s\": 1.5 }, " +
            "\"layout\": { \"l\": { \"anchor\": [0, 0], \"offset\": [1, 1.0] }, \"m\": { \"anchor\": [1, 0], \"offset\": [0, 0] } } }");
        string[] ops = Engine_HudModEdit.Diff(before, after).Select(op => $"{op.Kind}:{op.Id}").ToArray();
        AssertThat(ops).IsEqual(new[]
        {
            "RemoveEntry:b", "Show:h1", "RemoveScale:t",
            "SetPiece:n", "SetClone:c", "Hide:h3", "SetLayout:m",
        });
        AssertThat(Engine_HudModEdit.Diff(before, (JsonObject)before.DeepClone()).Count).IsEqual(0);
    }

    [TestCase]
    public void ModKeysAreEveryModSourceThePiecesRead()
    {
        JsonObject hud = Obj("{ \"pieces\": [ { \"id\": \"a\", \"value\": \"mod:m.stamina\", \"max\": \"mod:m.stamina_max\" }, " +
            "{ \"id\": \"t\", \"kind\": \"text\", \"text\": \"mod:m.count\" }, { \"id\": \"p\", \"index\": \" mod:m.glyph \", \"value\": \"hero:health\" } ], " +
            "\"clones\": [ { \"id\": \"c\", \"of\": \"x\", \"values\": { \"Health\": \"mod:m.mana/mod:m.mana_max\", \"Other\": \"none\" } } ] }");
        AssertThat(Engine_HudModEdit.ModKeys(hud).ToArray()).IsEqual(new[]
        {
            "m.count", "m.glyph", "m.mana", "m.mana_max", "m.stamina", "m.stamina_max",
        });
    }

    // ---- menus: equality, fields, records ----------------------------------------------------

    [TestCase]
    public void SameReadsJsonAsTheLoadersDo()
    {
        AssertThat(Engine_UiMenuEdit.Same(Obj("{ \"a\": 301, \"b\": [1, 2] }"), Obj("{ \"b\": [1.0, 2.0], \"a\": 301.0 }"))).IsTrue();
        AssertThat(Engine_UiMenuEdit.Same(Obj("{ \"a\": [1, 2] }"), Obj("{ \"a\": [2, 1] }"))).IsFalse();
        AssertThat(Engine_UiMenuEdit.Same(Obj("{ \"a\": \"1\" }"), Obj("{ \"a\": 1 }"))).IsFalse();
        AssertThat(Engine_UiMenuEdit.Same(Obj("{ \"a\": null }"), Obj("{ }"))).IsFalse();
    }

    [TestCase]
    public void AFieldAtItsDefaultIsLeftOutAndOthersAreUntouched()
    {
        JsonObject row = Obj("{ \"name\": \"R\", \"tag\": \"T\", \"custom\": { \"x\": 1 }, \"eventType\": 301 }");
        Engine_UiMenuEdit.SetField(row, "eventType", JsonValue.Create(0));
        Engine_UiMenuEdit.SetField(row, "counted", JsonValue.Create(true));
        Engine_UiMenuEdit.SetField(row, "rating", JsonValue.Create(Engine_UiMenuConstants.EmptyRatingSlide));
        Engine_UiMenuEdit.SetField(row, "command", JsonValue.Create(string.Empty));
        Engine_UiMenuEdit.SetField(row, "drop", JsonValue.Create(true));
        AssertThat(Engine_UiMenuEdit.Same(row, Obj("{ \"name\": \"R\", \"tag\": \"T\", \"custom\": { \"x\": 1 }, \"drop\": true }"))).IsTrue();
    }

    [TestCase]
    public void OnlyChangedRecordsAreSaved()
    {
        var loaded = new Dictionary<string, JsonObject>
        {
            ["same"] = Obj("{ \"highlight\": \"A\", \"entries\": [ { \"name\": \"A\", \"eventType\": 301 } ] }"),
            ["edited"] = Obj("{ \"entries\": [] }"),
        };
        var edited = new Dictionary<string, JsonObject>
        {
            ["same"] = Obj("{ \"entries\": [ { \"eventType\": 301.0, \"name\": \"A\" } ], \"highlight\": \"A\" }"),
            ["edited"] = Obj("{ \"entries\": [ { \"name\": \"New\" } ] }"),
            ["new"] = Obj("{ \"entries\": [] }"),
        };
        AssertThat(Engine_UiMenuEdit.Changed(loaded, edited).ToArray()).IsEqual(new[] { "edited", "new" });

        string file = "{ \"screens\": { \"22\": { \"name\": \"shop\" } }, \"modals\": { \"13\": null }, " +
            "\"menus\": { \"kept\": { \"entries\": [ { \"name\": \"K\", \"x-extra\": 7 } ] }, \"edited\": { \"entries\": [] } } }";
        JsonObject written = Obj(Engine_UiMenuEdit.ApplyMenus(file, new Dictionary<string, JsonObject> { ["edited"] = edited["edited"] }));
        AssertThat(Engine_UiMenuEdit.Same(written["screens"], Obj("{ \"22\": { \"name\": \"shop\" } }"))).IsTrue();
        AssertThat(written["modals"]!.AsObject().ContainsKey("13")).IsTrue();
        AssertThat(Engine_UiMenuEdit.Same(written["menus"]!["kept"], Obj("{ \"entries\": [ { \"name\": \"K\", \"x-extra\": 7 } ] }"))).IsTrue();
        AssertThat(Engine_UiMenuEdit.Same(written["menus"]!["edited"], edited["edited"])).IsTrue();
    }

    // ---- menus: the row tree -----------------------------------------------------------------

    [TestCase]
    public void TheRowTreeMovesNestsAndPromotes()
    {
        JsonArray entries = Arr("[ { \"name\": \"A\" }, { \"name\": \"B\" }, { \"name\": \"C\", \"children\": [ { \"name\": \"C1\" } ] } ]");

        AssertThat(Engine_UiMenuEdit.Move(entries, new[] { 0 }, -1) is null).IsTrue();
        AssertThat(Engine_UiMenuEdit.Move(entries, new[] { 0 }, 1)).IsEqual(new[] { 1 });
        AssertThat(Names(entries)).IsEqual("B,A,C[C1]");

        // B nests under nothing (it is first); A nests under B.
        AssertThat(Engine_UiMenuEdit.Nest(entries, new[] { 0 }) is null).IsTrue();
        AssertThat(Engine_UiMenuEdit.Nest(entries, new[] { 1 })).IsEqual(new[] { 0, 0 });
        AssertThat(Names(entries)).IsEqual("B[A],C[C1]");

        // C1 lifts out after C, and C's emptied children go.
        AssertThat(Engine_UiMenuEdit.Promote(entries, new[] { 1, 0 })).IsEqual(new[] { 2 });
        AssertThat(Names(entries)).IsEqual("B[A],C,C1");
        AssertThat(entries[1]!.AsObject().ContainsKey("children")).IsFalse();

        AssertThat(Engine_UiMenuEdit.Add(entries, new[] { 0, 0 }, Obj("{ \"name\": \"D\" }"), child: true)).IsEqual(new[] { 0, 0, 0 });
        AssertThat(Engine_UiMenuEdit.Add(entries, new[] { 1 }, Obj("{ \"name\": \"E\" }"), child: false)).IsEqual(new[] { 2 });
        AssertThat(Engine_UiMenuEdit.Add(entries, null, Obj("{ \"name\": \"F\" }"), child: false)).IsEqual(new[] { 4 });
        AssertThat(Names(entries)).IsEqual("B[A[D]],C,E,C1,F");

        AssertThat(Engine_UiMenuEdit.Remove(entries, new[] { 0, 0, 0 })).IsTrue();
        AssertThat(Names(entries)).IsEqual("B[A],C,E,C1,F");
        AssertThat(entries[0]!["children"]![0]!.AsObject().ContainsKey("children")).IsFalse();
        AssertThat(Engine_UiMenuEdit.NameTaken(entries, "A")).IsTrue();
        AssertThat(Engine_UiMenuEdit.NameTaken(entries, "Z")).IsFalse();
    }

    private static string Names(JsonArray list) => string.Join(",", list.OfType<JsonObject>().Select(row =>
        Engine_UiMenuEdit.Name(row) + (row["children"] is JsonArray { Count: > 0 } children ? "[" + Names(children) + "]" : string.Empty)));

    // ---- menus: row commands -----------------------------------------------------------------

    [TestCase]
    public void RowCommandsAreChecked()
    {
        AssertThat(Engine_UiMenuEdit.Check("Opens:my_mod.menu")).IsEqual(string.Empty);
        AssertThat(Engine_UiMenuEdit.Check("Opens:  ")).Contains("names no menu");
        AssertThat(Engine_UiMenuEdit.Check("Mod:lua:my_mod.on_row:7")).IsEqual(string.Empty);
        AssertThat(Engine_UiMenuEdit.Check("Mod:lua:on_row")).IsEqual(string.Empty);
        AssertThat(Engine_UiMenuEdit.Check("Mod:lua:")).Contains("names no function");
        AssertThat(Engine_UiMenuEdit.Check("Mod:lua:my-mod.on_row")).Contains("not a Lua function name");
        AssertThat(Engine_UiMenuEdit.Check("Mod:lua:1st")).Contains("not a Lua function name");
        AssertThat(Engine_UiMenuEdit.Check("Mod:lua:a..b")).Contains("not a Lua function name");
        AssertThat(Engine_UiMenuEdit.Check("options:mod:x")).IsEqual(string.Empty);
        AssertThat(Engine_UiMenuEdit.KindOf("Opens:x")).IsEqual(Engine_UiMenuEdit.CommandKind.Opens);
        AssertThat(Engine_UiMenuEdit.KindOf("Mod:lua:f")).IsEqual(Engine_UiMenuEdit.CommandKind.Lua);
        AssertThat(Engine_UiMenuEdit.KindOf("Quit")).IsEqual(Engine_UiMenuEdit.CommandKind.Other);
        AssertThat(Engine_UiMenuEdit.KindOf(null)).IsEqual(Engine_UiMenuEdit.CommandKind.None);
    }

    [TestCase]
    public void TheEditedFileStandsInForTheMod()
    {
        var enabled = new List<(string Mod, string Text)> { ("low", "1"), ("Mine", "2"), ("high", "3") };
        AssertThat(Engine_UiMenuEdit.Substitute(enabled, "mine", "X").Select(f => f.Mod + "=" + f.Text).ToArray())
            .IsEqual(new[] { "low=1", "Mine=X", "high=3" });
        AssertThat(Engine_UiMenuEdit.Substitute(enabled, "mine", null).Select(f => f.Mod).ToArray()).IsEqual(new[] { "low", "high" });
        AssertThat(Engine_UiMenuEdit.Substitute(enabled, "other", "Y").Select(f => f.Mod).ToArray())
            .IsEqual(new[] { "low", "Mine", "high", "other" });
    }

    // ---- menus: the patch diff, applied back ---------------------------------------------------

    private const string Base =
        "{ \"highlight\": \"Map\", \"title\": \"PAUSE\", \"entries\": [ " +
        "{ \"name\": \"Map\", \"tag\": \"MAP\" }, " +
        "{ \"name\": \"Items\", \"tag\": \"ITEMS\", \"eventType\": 301, \"children\": [ " +
        "  { \"name\": \"Potions\", \"tag\": \"POTIONS\" }, { \"name\": \"Food\", \"tag\": \"FOOD\" } ] }, " +
        "{ \"name\": \"Options\", \"tag\": \"OPTIONS\" }, " +
        "{ \"name\": \"Quit\", \"tag\": \"QUIT\", \"command\": \"Quit\" } ] }";

    [TestCase]
    public void AnUnchangedMenuNeedsNoPatch()
    {
        AssertThat(Engine_UiMenuEdit.Patch(Obj(Base), Obj(Base)) is null).IsTrue();
    }

    [TestCase]
    public void AChangedRowIsReplacedInPlace()
    {
        JsonObject edited = Obj(Base);
        edited["entries"]![2]!["tag"] = "MY_OPTIONS";
        JsonObject patch = Engine_UiMenuEdit.Patch(Obj(Base), edited)!;
        AssertThat(patch["patch"]!.GetValue<bool>()).IsTrue();
        AssertThat(patch["entries"]!.AsArray().Count).IsEqual(1);
        AssertThat(Engine_UiMenuEdit.Same(patch["entries"]![0], Obj("{ \"name\": \"Options\", \"tag\": \"MY_OPTIONS\" }"))).IsTrue();
        AssertThat(patch.ContainsKey("title")).IsFalse();
        Reproduces(Base, edited);
    }

    [TestCase]
    public void NewRowsArePlacedAfterTheirSiblingAndUnderTheirParent()
    {
        JsonObject edited = Obj(Base);
        edited["entries"]!.AsArray().Insert(0, Obj("{ \"name\": \"First\", \"command\": \"Opens:my_mod.menu\" }"));
        edited["entries"]!.AsArray().Insert(3, Obj("{ \"name\": \"Lua\", \"command\": \"Mod:lua:my_mod.on_row:1\" }"));
        edited["entries"]![2]!["children"]!.AsArray().Add(Obj("{ \"name\": \"Drinks\", \"tag\": \"DRINKS\" }"));
        JsonArray ops = Engine_UiMenuEdit.Patch(Obj(Base), edited)!["entries"]!.AsArray();
        AssertThat(ops.Count).IsEqual(3);
        AssertThat(ops[0]!["before"]!.GetValue<string>()).IsEqual("Map");
        AssertThat(ops[1]!["parent"]!.GetValue<string>()).IsEqual("Items");
        AssertThat(ops[1]!["after"]!.GetValue<string>()).IsEqual("Food");
        AssertThat(ops[2]!["after"]!.GetValue<string>()).IsEqual("Items");
        Reproduces(Base, edited);
    }

    [TestCase]
    public void RemovedRowsAreRemovedByNameUnderTheirParent()
    {
        JsonObject edited = Obj(Base);
        edited["entries"]!.AsArray().RemoveAt(3);
        edited["entries"]![1]!["children"]!.AsArray().RemoveAt(0);
        JsonArray ops = Engine_UiMenuEdit.Patch(Obj(Base), edited)!["entries"]!.AsArray();
        AssertThat(Engine_UiMenuEdit.Same(ops, Arr(
            "[ { \"name\": \"Potions\", \"remove\": true, \"parent\": \"Items\" }, { \"name\": \"Quit\", \"remove\": true } ]"))).IsTrue();
        Reproduces(Base, edited);
    }

    [TestCase]
    public void MovedRowsAreRemovedThenReinserted()
    {
        // Reorder at the top, and move a child out to the top level.
        JsonObject edited = Obj("{ \"highlight\": \"Map\", \"title\": \"MY PAUSE\", \"entries\": [ " +
            "{ \"name\": \"Options\", \"tag\": \"OPTIONS\" }, " +
            "{ \"name\": \"Map\", \"tag\": \"MAP\" }, " +
            "{ \"name\": \"Items\", \"tag\": \"ITEMS\", \"eventType\": 301, \"children\": [ { \"name\": \"Food\", \"tag\": \"FOOD\" } ] }, " +
            "{ \"name\": \"Potions\", \"tag\": \"POTIONS\" }, " +
            "{ \"name\": \"Quit\", \"tag\": \"QUIT\", \"command\": \"Quit\" } ] }");
        JsonObject patch = Engine_UiMenuEdit.Patch(Obj(Base), edited)!;
        AssertThat(patch["title"]!.GetValue<string>()).IsEqual("MY PAUSE");
        Reproduces(Base, edited);
    }

    [TestCase]
    public void NestingAndAReplacedParentReproduce()
    {
        JsonObject edited = Obj(Base);
        JsonArray top = edited["entries"]!.AsArray();
        int[] nested = Engine_UiMenuEdit.Nest(top, new[] { 2 })!;
        AssertThat(nested).IsEqual(new[] { 1, 2 });
        top[1]!["tag"] = "BAG";
        Reproduces(Base, edited);

        JsonObject swapped = Obj(Base);
        Engine_UiMenuEdit.Move(swapped["entries"]![1]!["children"]!.AsArray(), new[] { 0 }, 1);
        JsonArray ops = Engine_UiMenuEdit.Patch(Obj(Base), swapped)!["entries"]!.AsArray();
        AssertThat(ops.All(op => op!["parent"]?.GetValue<string>() == "Items")).IsTrue();
        Reproduces(Base, swapped);
    }

    /// <summary>
    /// Applies the patch the way <c>Engine_UiTemplates.PatchMenu</c> documents it — per entry by
    /// name under <c>parent</c>: <c>remove</c> deletes, a known name is replaced where it stands, a
    /// new one is inserted <c>after</c> / <c>before</c> a sibling or at the end — and checks the
    /// result is the edit.
    /// </summary>
    private static void Reproduces(string baseText, JsonObject edited)
    {
        JsonObject result = Obj(baseText);
        JsonObject? patch = Engine_UiMenuEdit.Patch(Obj(baseText), edited);
        if (patch is not null)
        {
            foreach (string field in Engine_UiMenuEdit.MenuFields)
            {
                if (patch[field] is { } value)
                {
                    result[field] = value.DeepClone();
                }
            }

            JsonArray entries = result["entries"]!.AsArray();
            foreach (JsonObject op in patch["entries"]!.AsArray().OfType<JsonObject>())
            {
                string name = Engine_UiMenuEdit.Name(op);
                string parent = Engine_UiMenuEdit.Text(op, "parent");
                JsonArray? siblings = parent.Length == 0 ? entries : Find(entries, parent) is { } owner ? Children(owner) : null;
                if (siblings is null)
                {
                    continue;
                }

                int at = Index(siblings, name);
                if (Engine_UiMenuEdit.Flag(op, "remove", false))
                {
                    if (at >= 0)
                    {
                        siblings.RemoveAt(at);
                    }

                    continue;
                }

                JsonObject row = Strip(op);
                if (at >= 0)
                {
                    siblings[at] = row;
                    continue;
                }

                int index = siblings.Count;
                if (op["after"] is { } after && Index(siblings, after.GetValue<string>()) is >= 0 and var a)
                {
                    index = a + 1;
                }
                else if (op["before"] is { } before && Index(siblings, before.GetValue<string>()) is >= 0 and var b)
                {
                    index = b;
                }

                siblings.Insert(index, row);
            }

            Prune(entries);
        }

        AssertThat(Engine_UiMenuEdit.Same(result, edited))
            .OverrideFailureMessage($"the patch\n{patch?.ToJsonString()}\napplied to the base gives\n{result.ToJsonString()}\nnot the edit\n{edited.ToJsonString()}")
            .IsTrue();
    }

    private static JsonObject? Find(JsonArray list, string name)
    {
        foreach (JsonObject row in list.OfType<JsonObject>())
        {
            if (Engine_UiMenuEdit.Name(row) == name)
            {
                return row;
            }

            if (row["children"] is JsonArray children && Find(children, name) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static JsonArray Children(JsonObject row)
    {
        if (row["children"] is not JsonArray children)
        {
            children = new JsonArray();
            row["children"] = children;
        }

        return children;
    }

    private static int Index(JsonArray list, string name)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is JsonObject row && Engine_UiMenuEdit.Name(row) == name)
            {
                return i;
            }
        }

        return -1;
    }

    private static JsonObject Strip(JsonObject op)
    {
        JsonObject row = Engine_UiMenuEdit.OwnFields(op);
        if (op["children"] is JsonArray children)
        {
            var copy = new JsonArray();
            foreach (JsonObject child in children.OfType<JsonObject>())
            {
                copy.Add(Strip(child));
            }

            row["children"] = copy;
        }

        return row;
    }

    /// <summary>The engine model has no "no children" vs "empty children" difference; neither does the comparison.</summary>
    private static void Prune(JsonArray list)
    {
        foreach (JsonObject row in list.OfType<JsonObject>())
        {
            if (row["children"] is JsonArray children)
            {
                if (children.Count == 0)
                {
                    row.Remove("children");
                }
                else
                {
                    Prune(children);
                }
            }
        }
    }
}
