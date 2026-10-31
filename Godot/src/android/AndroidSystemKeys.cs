namespace Refii.AndroidRuntime;

/// <summary>What the system's back gesture does in the game.</summary>
public enum AndroidBack
{
    /// <summary>Nothing: the game is not up, or a wheel is held open by its latch.</summary>
    Ignore,

    /// <summary>The pad's Start: play pauses into the game's menu.</summary>
    Pause,

    /// <summary>The pad's B: a menu backs out.</summary>
    BackOut,

    /// <summary>Photo mode or the mirror closes as its own B would close it.</summary>
    ClosePanel,
}

/// <summary>
/// The phone's own keys and the app's comings and goings, as the game takes them.
/// </summary>
/// <remarks>
/// THE BACK GESTURE IS NEVER A QUIT. Godot quits on it by default, and a gesture phone sends it on
/// a swipe in from either side edge — where the stick and camera drags start and Spells and Lock
/// stand — so a slip in a fight ended the game unsaved. It is a console's back instead: play
/// pauses, a menu backs out, a host panel closes.
///
/// A PUB GAME'S BOARD IS PLAY OF ITS OWN. Its pad is read apart from the guest's (PubGameInput),
/// so the guest's play state says nothing about it: back opens the board's pause menu (its Start),
/// and with one of its dialogs up (that menu, the bank, a quit or cash-out question) backs out of
/// it (its B). B alone would lower Keystone's bet.
///
/// LEAVING THE APP PAUSES THE GAME. A call, the notification shade or the app switcher lets go of
/// every finger, but the guest's thread kept stepping with the hero stood still in whatever fight
/// he was in. It now stops while the app is away, and play comes back to the pause menu.
/// </remarks>
public static class AndroidSystemKeys
{
    /// <summary>Where the back gesture goes, from what is on screen.</summary>
    /// <param name="gameUp">Whether the game's front end is attached.</param>
    /// <param name="panel">Whether photo mode or the mirror owns the screen.</param>
    /// <param name="playing">Whether the hero is under the player's control (<c>GameInput.Playing</c>).</param>
    /// <param name="overlay">Whether a wheel or the spell menu is held open (<c>GameInput.InOverlay</c>).</param>
    /// <param name="board">Whether a pub game's board is on screen (<c>GamePubGames.Showing</c>).</param>
    /// <param name="boardDialog">Whether one of the board's dialogs owns its pad (<c>GamePubGames.DialogUp</c>).</param>
    public static AndroidBack Back(bool gameUp, bool panel, bool playing, bool overlay,
        bool board = false, bool boardDialog = false) =>
        !gameUp ? AndroidBack.Ignore
        : panel ? AndroidBack.ClosePanel
        : board ? (boardDialog ? AndroidBack.BackOut : AndroidBack.Pause)
        : !playing ? AndroidBack.BackOut
        : overlay ? AndroidBack.Ignore
        : AndroidBack.Pause;

    /// <summary>Whether coming back to the app opens the pause menu: only play left mid-stride does.</summary>
    /// <param name="wasAway">Whether the guest was held for the app's absence (one return of several notices).</param>
    /// <param name="panel">Whether photo mode or the mirror owns the screen.</param>
    /// <param name="playing">Whether the hero is under the player's control.</param>
    /// <param name="overlay">Whether a wheel or the spell menu is held open.</param>
    /// <param name="board">Whether a pub game's board is on screen.</param>
    /// <param name="boardDialog">Whether one of the board's dialogs owns its pad.</param>
    public static bool PauseOnReturn(bool wasAway, bool panel, bool playing, bool overlay,
        bool board = false, bool boardDialog = false) =>
        wasAway && Back(true, panel, playing, overlay, board, boardDialog) == AndroidBack.Pause;
}
