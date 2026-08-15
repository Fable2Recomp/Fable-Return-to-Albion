namespace Refii.FlashGames.PubGames;

public interface IPubOverlayManager
{
    void ActivateNeedChipsAlert();

    void ActivateStarRatingDialog(
        bool table = false, bool concept = false,
        string? gameName = null, int min = 0, int max = 0);

    void ActivateFade();

    void ActivateFadeOut();

    /// <summary>Asks the player to confirm leaving with live bets on the table.</summary>
    void ActivateQuitConfirmation();

    /// <summary>Shows the card that ends the session and settles the chips.</summary>
    void ActivateCashOut();
}

/// <summary>
/// The overlays the scripted tutorial drives.
/// </summary>
/// <remarks>
/// SEPARATE FROM <see cref="IPubOverlayManager"/> ON PURPOSE. Only the tutorial table needs any of
/// this, and only one implementation provides it; keeping it out of the main interface means a
/// stub host — a probe, a check, a game with no tutorial — does not have to answer questions it
/// has no opinion about.
/// </remarks>
public interface IPubTutorialOverlays
{
    /// <summary>Whether the odds table may be opened. The tutorial closes it off.</summary>
    bool AllowQuickTips { get; set; }

    /// <summary>Whether the odds table is currently up.</summary>
    bool QuickTipsShowing { get; }

    void ShowTutorial(
        string? title, string? body, string? button1Frame, string? button1Label,
        string? button2Frame, string? button2Label, bool fade, object? group);

    void HideTutorial(bool fade, object? group);
}
