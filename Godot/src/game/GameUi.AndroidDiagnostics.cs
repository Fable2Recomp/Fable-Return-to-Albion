using Godot;

namespace Refii.Game;

/// <summary>Bounded read-only diagnostics for Android's production frontend, pause root and screen menus.</summary>
public partial class GameUi
{
    /// <summary>Reports actual menu highlights and visibility, plus the existing native pause sample.</summary>
    /// <returns>A detached bounded main-thread snapshot; reading it cannot navigate or activate a menu.</returns>
    public object DescribeInteractiveUi()
    {
        var frontView = DiagnosticViewState(_menuView);
        var pauseView = DiagnosticViewState(_pauseView);
        var screenView = DiagnosticViewState(_screenMenuView);
        var pauseBook = DiagnosticViewState(_pauseBook);
        var saveCards = DiagnosticViewState(_saveCardView);
        return new
        {
            built = IsBuilt, revealed = IsRevealed, startup_screen = Screen,
            current_presentation = _showing ?? Screen, has_in_game_ui = HasInGameUi,
            menu = GameUiDiagnosticsSnapshot.CaptureMenu(_menu,
                frontView.Available, frontView.Visible, frontView.InTree),
            pause_menu = GameUiDiagnosticsSnapshot.CaptureMenu(_pauseMenu,
                pauseView.Available, pauseView.Visible, pauseView.InTree),
            pause = new
            {
                tree_up = _pauseTreeUp, presentation_address = _pauseAddress,
                book_available = pauseBook.Available, book_visible = pauseBook.Visible,
                book_visible_in_tree = pauseBook.InTree, joiner = _pauseMenuJoiner,
                tabs_available = _pauseTabsMask,
                achievements_open = _hostScreen is { IsOpen: true, ScreenId: Refii.Ui.Engine_UiAchievementPage.ScreenId }, multiplayer_open = _multiplayer.IsOpen
            },
            screen_menu = GameUiDiagnosticsSnapshot.CaptureScreenMenu(_screenMenu, _screenMenuScreen,
                screenView.Available, screenView.Visible, screenView.InTree),
            // _modal is the existing production guest-frame sample. Diagnostics must not poll
            // native memory again or pretend this is a newer/coherent guest transaction.
            native_pause = GameUiDiagnosticsSnapshot.CaptureNativePause(_modal),
            native_pause_source = "last GameUi guest-frame sample",
            save_cards = !saveCards.Available ? null : new
            {
                visible = saveCards.Visible, visible_in_tree = saveCards.InTree,
                cursor = _saveCardView!.Cursor
            },
            input_drive = GameInput.FrontEndDrive
        };
    }

    /// <summary>Samples only a live view on its owning Godot main thread, including ancestor visibility.</summary>
    /// <param name="view">The optional production view or owned book.</param>
    /// <returns>Whether the native object exists, its local visibility and its actual tree visibility.</returns>
    private static (bool Available, bool Visible, bool InTree) DiagnosticViewState(Node3D? view) =>
        view is not null && GodotObject.IsInstanceValid(view)
            ? (true, view.Visible, view.IsVisibleInTree()) : (false, false, false);
}
