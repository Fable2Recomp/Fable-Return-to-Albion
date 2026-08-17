namespace Refii.FlashGames.Framework;

public interface IPubOverlayManager
{
    void ActivateNeedChipsAlert();

    void ActivateStarRatingDialog(
        bool table = false, bool concept = false,
        string? gameName = null, int min = 0, int max = 0);

    void ActivateFade();

    void ActivateFadeOut();

    void ActivateQuitConfirmation();

    void ActivateCashOut();
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
