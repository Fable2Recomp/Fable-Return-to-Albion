using Refii.Ui;

namespace Refii.AndroidRuntime;

/// <summary>Determines visibility of production UI without synthesizing any guest action.</summary>
public static class InteractiveFrontEndPolicy
{
    /// <summary>Reveals the armed title, main menu or in-game UI; title state zero is valid.</summary>
    /// <param name="found">Whether the native frontend query found a live screen.</param>
    /// <param name="screen">The native screen identity.</param>
    /// <param name="guiUp">Whether the real frontend GUI is active.</param>
    /// <param name="canPressA">The title's input-ready flag, independent of its numeric state.</param>
    /// <param name="hasInGameUi">Whether GameUi currently has an in-game presentation.</param>
    /// <returns>True when actual game UI should be visible.</returns>
    public static bool ShouldReveal(bool found, Engine_UiModel.FrontEndScreen screen,
        bool guiUp, bool canPressA, bool hasInGameUi) => hasInGameUi ||
        (found && guiUp && (screen == Engine_UiModel.FrontEndScreen.MainMenu ||
            screen == Engine_UiModel.FrontEndScreen.Title && canPressA));

    /// <summary>Whether UI already on screen stays up although <see cref="ShouldReveal"/> no longer holds.</summary>
    /// <remarks>As on the desktop (GameWindow): revealed UI goes only once the front end's GUI is gone
    /// and no in-game presentation stands. After A on the title the title stops taking input while
    /// the guest builds the main menu, and hiding the UI then left the screen black for about 2.4 s.</remarks>
    /// <param name="revealed">Whether GameUi is revealed now.</param>
    /// <param name="guiUp">Whether the real frontend GUI is active.</param>
    /// <param name="hasInGameUi">Whether GameUi currently has an in-game presentation.</param>
    /// <returns>True when the UI should stay as it is.</returns>
    public static bool StaysRevealed(bool revealed, bool guiUp, bool hasInGameUi) =>
        revealed && (guiUp || hasInGameUi);

    /// <summary>Describes only observed frontend readiness, so failed queries cannot imply a loaded title.</summary>
    /// <param name="found">Whether the native frontend query found a live screen.</param>
    /// <param name="screen">The native screen identity; ignored when the query failed.</param>
    /// <param name="guiUp">Whether the real frontend GUI is active.</param>
    /// <param name="canPressA">Whether the title currently accepts controller input.</param>
    /// <returns>A status message that does not synthesize a guest action or screen.</returns>
    public static string Status(bool found, Engine_UiModel.FrontEndScreen screen,
        bool guiUp, bool canPressA)
    {
        if (!found || screen == Engine_UiModel.FrontEndScreen.None)
            return "waiting for native frontend";
        if (!guiUp) return "waiting for native frontend GUI";
        if (screen == Engine_UiModel.FrontEndScreen.MainMenu)
            return "main menu — select Load Game with the controller";
        return canPressA ? "title — press controller A" : "waiting for native title input readiness";
    }
}
