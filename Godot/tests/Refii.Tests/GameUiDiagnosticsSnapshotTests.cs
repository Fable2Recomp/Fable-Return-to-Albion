using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GdUnit4;
using Refii.Game;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Verifies detached diagnostic serialization across frontend, pause, submenu and return lifecycles.</summary>
[TestSuite]
public sealed class GameUiDiagnosticsSnapshotTests
{
    /// <summary>An absent model or native sample stays absent instead of synthesizing a pause root.</summary>
    [TestCase]
    public void AbsentModelsAndNativeSamplesRemainNull()
    {
        AssertThat(GameUiDiagnosticsSnapshot.CaptureMenu(null, false, false, false)).IsNull();
        AssertThat(GameUiDiagnosticsSnapshot.CaptureScreenMenu(null, uint.MaxValue, false, false, false)).IsNull();
        AssertThat(GameUiDiagnosticsSnapshot.CaptureNativePause(null)).IsNull();
    }

    /// <summary>The report keeps snake-case field compatibility and distinguishes local from ancestor visibility.</summary>
    [TestCase]
    public void SerializedMenuKeepsIdentityAvailabilityAndVisibilitySeparate()
    {
        var menu = new Engine_FrontEndMenu();
        menu.Add(new Engine_UiExpandableMenu.Item("Load Game", "load")
            { StatedLabel = "Load saved hero", Command = "LoadGame", Ghosted = true });
        var snapshot = GameUiDiagnosticsSnapshot.CaptureMenu(menu, true, true, false)!;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
        AssertThat(json.RootElement.GetProperty("highlighted").GetString()).IsEqual("Load Game");
        AssertThat(json.RootElement.GetProperty("highlighted_index").GetInt32()).IsEqual(0);
        AssertThat(json.RootElement.GetProperty("highlighted_label").GetString()).IsEqual("Load saved hero");
        AssertThat(json.RootElement.GetProperty("view_visible").GetBoolean()).IsTrue();
        AssertThat(json.RootElement.GetProperty("view_visible_in_tree").GetBoolean()).IsFalse();
        AssertThat(json.RootElement.GetProperty("can_activate").GetBoolean()).IsFalse();
        var row = json.RootElement.GetProperty("visible_rows")[0];
        AssertThat(row.GetProperty("command").GetString()).IsEqual("LoadGame");
        AssertThat(row.GetProperty("ghosted").GetBoolean()).IsTrue();
        AssertThat(row.GetProperty("depth").GetInt32()).IsEqual(1);
        AssertThat(json.RootElement.TryGetProperty("save_state", out _)).IsTrue();
    }

    /// <summary>Large lists are bounded while their actual row count and off-bound highlight remain explicit.</summary>
    [TestCase]
    public void BoundedRowsDoNotLoseTheActualHighlightOrRetainMutableItems()
    {
        var menu = new Engine_FrontEndMenu();
        for (int i = 0; i < 40; ++i) menu.Add(new Engine_UiExpandableMenu.Item("row-" + i, "tag"));
        menu.Model.Highlight(40);
        var snapshot = GameUiDiagnosticsSnapshot.CaptureMenu(menu, true, true, true)!;
        string before = JsonSerializer.Serialize(snapshot);
        AssertThat(snapshot.VisibleRows.Count).IsEqual(GameUiDiagnosticsSnapshot.RowLimit);
        AssertThat(snapshot.RowCount).IsEqual(40);
        AssertThat(snapshot.RowsTruncated).IsTrue();
        AssertThat(snapshot.HighlightedIndex).IsEqual(39);
        AssertThat(snapshot.Highlighted).IsEqual("row-39");
        menu.Visible[0].Name = "changed later";
        menu.Visible[0].Ghosted = true;
        menu.Model.Highlight(1);
        AssertThat(JsonSerializer.Serialize(snapshot)).IsEqual(before);
        bool immutable = false;
        try { ((IList<GameUiDiagnosticsSnapshot.Row>)snapshot.VisibleRows)[0] = snapshot.VisibleRows[1]; }
        catch (NotSupportedException) { immutable = true; }
        AssertThat(immutable).IsTrue();
    }

    /// <summary>Pause open, category fold, submenu, modal and return snapshots follow real model changes only.</summary>
    [TestCase]
    public void PauseAndSubmenuSnapshotsRemainStableAcrossTheCompleteReadOnlyLifecycle()
    {
        var pause = new Engine_FrontEndMenu();
        var category = new Engine_UiExpandableMenu.Item("Map/Quests", "map");
        category.Children.Add(new Engine_UiExpandableMenu.Item("Bowerstone Market", "market"));
        pause.Add(category);
        pause.Add(new Engine_UiExpandableMenu.Item("Options", "options"));
        var closed = GameUiDiagnosticsSnapshot.CaptureMenu(pause, true, false, false)!;
        var nativeClosed = GameUiDiagnosticsSnapshot.CaptureNativePause(null);
        AssertThat(nativeClosed).IsNull();
        var root = GameUiDiagnosticsSnapshot.CaptureNativePause(new Engine_UiModel.PauseState(0, 0, 1, 1))!;
        var opened = GameUiDiagnosticsSnapshot.CaptureMenu(pause, true, true, true)!;
        AssertThat(root.RootActive).IsTrue();
        AssertThat(root.ModalUp).IsFalse();
        AssertThat(opened.CanCollapse).IsFalse();
        string beforeFold = JsonSerializer.Serialize(opened);
        AssertThat(pause.Activate()).IsNull();
        var folded = GameUiDiagnosticsSnapshot.CaptureMenu(pause, true, true, true)!;
        AssertThat(folded.Expanded).IsEqual("Map/Quests");
        AssertThat(folded.Highlighted).IsEqual("Bowerstone Market");
        AssertThat(folded.CanCollapse).IsTrue();
        AssertThat(folded.VisibleRows[1].Depth).IsEqual(2);
        AssertThat(JsonSerializer.Serialize(opened)).IsEqual(beforeFold);
        var screen = new Engine_UiScreenMenu { ScreenId = 23 };
        screen.Menu.Add(new Engine_UiExpandableMenu.Item("Current Quests", "quests"));
        var list = GameUiDiagnosticsSnapshot.CaptureScreenMenu(screen, 23, true, true, true)!;
        var nativeScreen = GameUiDiagnosticsSnapshot.CaptureNativePause(new Engine_UiModel.PauseState(23, 0, 1, 2))!;
        AssertThat(nativeScreen.RootActive).IsFalse();
        AssertThat(list.ScreenId).IsEqual(23u);
        AssertThat(list.Menu!.Highlighted).IsEqual("Current Quests");
        var modal = GameUiDiagnosticsSnapshot.CaptureNativePause(new Engine_UiModel.PauseState(23, 0, 3, 2))!;
        AssertThat(modal.ModalUp).IsTrue();
        AssertThat(modal.RootActive).IsFalse();
        AssertThat(modal.ModalId).IsEqual(0u);
        AssertThat(pause.Back()).IsTrue();
        var returned = GameUiDiagnosticsSnapshot.CaptureMenu(pause, true, true, true)!;
        AssertThat(returned.CanCollapse).IsFalse();
        AssertThat(returned.Highlighted).IsEqual("Map/Quests");
        AssertThat(closed.ViewVisible).IsFalse();
        AssertThat(opened.ViewVisibleInTree).IsTrue();
        AssertThat(folded.VisibleRows.Count).IsEqual(3);
        AssertThat(list.Menu.VisibleRows.Count).IsEqual(1);
    }

    /// <summary>Screen binding and model identity stay distinct, and later model mutations cannot change a snapshot.</summary>
    [TestCase]
    public void ScreenIdentityAndActivationGateAreSampledWithoutInvokingActions()
    {
        var screen = new Engine_UiScreenMenu { ScreenId = 14 };
        screen.Menu.Add(new Engine_UiExpandableMenu.Item("Strength", "strength") { AutoGhosted = true });
        var idle = GameUiDiagnosticsSnapshot.CaptureScreenMenu(screen, 18, false, true, true)!;
        AssertThat(idle.ScreenId).IsEqual(18u);
        AssertThat(idle.ModelScreenId).IsEqual(14u);
        AssertThat(idle.GuestHighlight).IsEqual(0);
        AssertThat(idle.Menu!.ViewAvailable).IsFalse();
        AssertThat(idle.Menu.ViewVisible).IsFalse();
        AssertThat(idle.Menu.ViewVisibleInTree).IsFalse();
        AssertThat(idle.Menu.CanActivate).IsTrue();
        screen.Script.Animating = true;
        var animating = GameUiDiagnosticsSnapshot.CaptureScreenMenu(screen, 14, true, true, true)!;
        AssertThat(animating.Menu!.Animating).IsTrue();
        AssertThat(animating.Menu.CanActivate).IsFalse();
        AssertThat(idle.Menu.Animating).IsFalse();
        AssertThat(idle.Menu.VisibleRows[0].AutoGhosted).IsTrue();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { screen_menu = idle,
            native_pause = GameUiDiagnosticsSnapshot.CaptureNativePause(new Engine_UiModel.PauseState(14, 0, 1, 3)) }));
        AssertThat(json.RootElement.GetProperty("screen_menu").GetProperty("model_screen_id").GetUInt32()).IsEqual(14u);
        AssertThat(json.RootElement.GetProperty("native_pause").GetProperty("screen_id").GetUInt32()).IsEqual(14u);
    }
}
