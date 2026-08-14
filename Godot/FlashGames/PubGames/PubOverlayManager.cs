namespace Refii.FlashGames.PubGames;

public interface IPubOverlayManager
{
    void ActivateNeedChipsAlert();

    void ActivateStarRatingDialog(
        bool table = false, bool concept = false,
        string? gameName = null, int min = 0, int max = 0);

    void ActivateFade();

    void ActivateFadeOut();
}
