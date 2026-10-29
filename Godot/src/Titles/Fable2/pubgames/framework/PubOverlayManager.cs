namespace Refii.Titles.Fable2.PubGames.Framework;

public interface IPubOverlayManager
{
    /// <summary>Opens the session — see <c>OverlayManager.Init</c>.</summary>
    void Init();

    void ActivateNeedChipsAlert();

    void ActivateStarRatingDialog(
        bool table = false, bool concept = false,
        string? gameName = null, int min = 0, int max = 0);

    void ActivateFade();

    void ActivateFadeOut();

    void ActivateQuitConfirmation();

    void ActivateCashOut();

    void ActivateGameHelp();

    void ActivateShimPauseMenu();

    void ShowSaveIcon(bool show);

    /// <summary><c>OverlayManager::CleanUp</c>, from <c>PubGame::CleanUpGame</c> (0x001dde).</summary>
    void CleanUp();
}

public interface IPubTutorialOverlays
{
    bool AllowQuickTips { get; set; }

    bool QuickTipsShowing { get; }

    void ShowTutorial(
        string? title, string? body, string? button1Frame, string? button1Label,
        string? button2Frame, string? button2Label, bool fade, object? group);

    void HideTutorial(bool fade, object? group);
}
