using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Refii.Ui;

namespace Refii.Game;

/// <summary>Copies bounded menu diagnostics into detached values without reading native nodes or dispatching input.</summary>
internal static class GameUiDiagnosticsSnapshot
{
    /// <summary>The maximum number of visible model rows included in one diagnostic menu.</summary>
    internal const int RowLimit = 32;

    /// <summary>Detached menu-row identity and fold/availability state, without references to mutable items.</summary>
    /// <param name="Name">The production dispatch name.</param>
    /// <param name="Label">The guest-stated display text when supplied.</param>
    /// <param name="Command">The host dispatch command, when one exists.</param>
    /// <param name="Depth">The model's nesting depth; root rows have depth one.</param>
    /// <param name="ChildCount">The number of children, including currently folded rows.</param>
    /// <param name="Ghosted">The explicit activation refusal flag.</param>
    /// <param name="AutoGhosted">The separate visual empty-category flag.</param>
    /// <param name="IsOpen">Whether the model currently expands this row.</param>
    internal sealed record Row(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("command")] string? Command,
        [property: JsonPropertyName("depth")] int Depth,
        [property: JsonPropertyName("child_count")] int ChildCount,
        [property: JsonPropertyName("ghosted")] bool Ghosted,
        [property: JsonPropertyName("auto_ghosted")] bool AutoGhosted,
        [property: JsonPropertyName("is_open")] bool IsOpen);

    /// <summary>Detached shared-widget state for a frontend, pause root or in-game submenu.</summary>
    /// <param name="HighlightedIndex">The zero-based highlight in the full visible model.</param>
    /// <param name="Highlighted">The highlighted dispatch name, independent of the row bound.</param>
    /// <param name="HighlightedLabel">The highlighted row's guest-stated display text.</param>
    /// <param name="VisibleRows">An immutable bounded row copy.</param>
    /// <param name="RowCount">The full model's visible count.</param>
    /// <param name="RowsTruncated">Whether the row copy omits later visible rows.</param>
    /// <param name="SaveState">The model's existing storage availability snapshot.</param>
    /// <param name="CanCollapse">Whether B would fold the highlighted row's parent.</param>
    /// <param name="CanActivate">Whether the model currently accepts activation.</param>
    /// <param name="Animating">Whether a fold animation currently refuses activation.</param>
    /// <param name="Expanded">The outermost expanded root, if any.</param>
    /// <param name="ViewAvailable">Whether the caller found a live view.</param>
    /// <param name="ViewVisible">The view's local Visible flag.</param>
    /// <param name="ViewVisibleInTree">The view's actual visibility through its ancestor chain.</param>
    internal sealed record Menu(
        [property: JsonPropertyName("highlighted_index")] int HighlightedIndex,
        [property: JsonPropertyName("highlighted")] string? Highlighted,
        [property: JsonPropertyName("highlighted_label")] string? HighlightedLabel,
        [property: JsonPropertyName("visible_rows")] IReadOnlyList<Row> VisibleRows,
        [property: JsonPropertyName("row_count")] int RowCount,
        [property: JsonPropertyName("rows_truncated")] bool RowsTruncated,
        [property: JsonPropertyName("save_state")] Engine_UiModel.SaveState SaveState,
        [property: JsonPropertyName("can_collapse")] bool CanCollapse,
        [property: JsonPropertyName("can_activate")] bool CanActivate,
        [property: JsonPropertyName("animating")] bool Animating,
        [property: JsonPropertyName("expanded")] string? Expanded,
        [property: JsonPropertyName("view_available")] bool ViewAvailable,
        [property: JsonPropertyName("view_visible")] bool ViewVisible,
        [property: JsonPropertyName("view_visible_in_tree")] bool ViewVisibleInTree);

    /// <summary>The native pause router's already sampled scalar state, distinct from host visibility.</summary>
    /// <param name="ScreenId">The underlying native screen; zero identifies the pause root.</param>
    /// <param name="ModalId">The guest's current modal identity.</param>
    /// <param name="ModalMode">The guest's modal gate mode, where one is idle.</param>
    /// <param name="CurrentTab">The guest's selected pause tab.</param>
    /// <param name="RootActive">Whether the sampled router is on an interactive pause root.</param>
    /// <param name="ModalUp">Whether a sampled modal owns input.</param>
    internal sealed record NativePause(
        [property: JsonPropertyName("screen_id")] uint ScreenId,
        [property: JsonPropertyName("modal_id")] uint ModalId,
        [property: JsonPropertyName("modal_mode")] uint ModalMode,
        [property: JsonPropertyName("current_tab")] uint CurrentTab,
        [property: JsonPropertyName("root_active")] bool RootActive,
        [property: JsonPropertyName("modal_up")] bool ModalUp);

    /// <summary>A detached in-game screen list with its bound screen/presentation identity.</summary>
    /// <param name="ScreenId">The screen to which the standing view was bound.</param>
    /// <param name="ModelScreenId">The list model's own declared screen identity.</param>
    /// <param name="Presentation">The guest presentation containing the model.</param>
    /// <param name="PopulationScript">The production population script name.</param>
    /// <param name="Stated">Whether rows came from the guest's stated model.</param>
    /// <param name="Menu">The shared widget's detached state.</param>
    internal sealed record ScreenMenu(
        [property: JsonPropertyName("screen_id")] uint ScreenId,
        [property: JsonPropertyName("model_screen_id")] uint ModelScreenId,
        [property: JsonPropertyName("presentation")] uint Presentation,
        [property: JsonPropertyName("population_script")] string PopulationScript,
        [property: JsonPropertyName("stated")] bool Stated,
        [property: JsonPropertyName("menu")] Menu? Menu);

    /// <summary>Copies current model values without navigating, rebuilding or retaining mutable menu items.</summary>
    /// <param name="menu">The real production model, or null before construction.</param>
    /// <param name="viewAvailable">Whether a live native view exists.</param>
    /// <param name="viewVisible">The view's local visibility sampled by its main-thread owner.</param>
    /// <param name="viewVisibleInTree">Ancestor-aware visibility sampled by its main-thread owner.</param>
    /// <returns>A detached snapshot, or null when no model exists.</returns>
    internal static Menu? CaptureMenu(Engine_FrontEndMenu? menu, bool viewAvailable,
        bool viewVisible, bool viewVisibleInTree)
    {
        if (menu is null) return null;
        var visible = menu.Visible;
        var rows = visible.Take(RowLimit).Select(row => new Row(row.Name, row.StatedLabel, row.Command,
            row.Depth, row.Children.Count, row.Ghosted, row.AutoGhosted, row.IsOpen)).ToArray();
        var highlighted = menu.Highlighted;
        return new(menu.HighlightIndex, highlighted?.Name, highlighted?.StatedLabel,
            Array.AsReadOnly(rows), visible.Count, visible.Count > rows.Length, menu.Saves,
            menu.CanCollapse, menu.CanActivate, menu.Model.Animating, menu.Expanded?.Name,
            viewAvailable, viewAvailable && viewVisible, viewAvailable && viewVisibleInTree);
    }

    /// <summary>Copies the existing native sample without issuing a new native call.</summary>
    /// <param name="pause">The last native pause state sampled by the production UI owner.</param>
    /// <returns>Detached router values, or null when that sample is absent.</returns>
    internal static NativePause? CaptureNativePause(Engine_UiModel.PauseState? pause) =>
        pause is { } value ? new(value.ScreenId, value.ModalId, value.ModalMode, value.CurrentTab,
            value.RootActive, value.ModalUp) : null;

    /// <summary>Copies an in-game list and its shared widget without calling guest menu actions.</summary>
    /// <param name="menu">The production screen list, or null before construction.</param>
    /// <param name="screenId">The identity to which the current view was bound.</param>
    /// <param name="viewAvailable">Whether a live native view exists.</param>
    /// <param name="viewVisible">The view's local Visible flag.</param>
    /// <param name="viewVisibleInTree">Ancestor-aware visibility sampled on the main thread.</param>
    /// <returns>A detached bounded screen-list snapshot, or null when the model is absent.</returns>
    internal static ScreenMenu? CaptureScreenMenu(Engine_UiScreenMenu? menu, uint screenId,
        bool viewAvailable, bool viewVisible, bool viewVisibleInTree) => menu is null ? null :
        new(screenId, menu.ScreenId, menu.Presentation, menu.PopulationScript,
            menu.Stated,
            CaptureMenu(menu.Menu, viewAvailable, viewVisible, viewVisibleInTree));
}
