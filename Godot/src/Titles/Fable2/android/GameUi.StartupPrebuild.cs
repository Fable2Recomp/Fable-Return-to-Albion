namespace Refii.Titles.Fable2;

/// <summary>
/// THE MAIN MENU, BUILT IN THE STARTUP PAUSE. On a phone the title's own prebuild
/// (<see cref="PrebuildMenuAtTitle"/>) was two long frames over the animated title, about 2.5 s after
/// it appeared (0.3-0.6 s each). The game UI is attached in one long frame between the startup videos,
/// over the static startup screen; the host builds the main menu in that same frame
/// (AndroidGameHost.AttachFrontEnd), and the title finds it done.
/// </summary>
public partial class GameUi : Refii.AndroidRuntime.IAndroidMenuPrebuild
{
    /// <summary>The template tree, the pattern book and the book; a book whose art is not readable
    /// yet is left to the title, which asks again.</summary>
    public void PrebuildMenuNow()
    {
        if (_presentation is null || _menuPrebuild >= 3)
        {
            return;
        }

        using (Refii.Core.Engine_FramePhases.Measure("ui.menu.prebuild"))
        {
            if (_menuPrebuild == 0)
            {
                _presentation.DocumentFor(FrontEndMenuPresentation);
                _menuPrebuild = 1;
            }

            if (_menuPrebuild == 1)
            {
                _presentation.PatternBookFor(FrontEndMenuPresentation);
                _menuPrebuild = 2;
            }

            if (_books.PrebuildFrontEnd(FrontEndMenuPresentation))
            {
                _menuPrebuild = 3;
            }
        }

        Godot.GD.Print($"[game-ui] main menu built in the startup pause ({(_menuPrebuild >= 3 ? "whole" : "its book left to the title")})");
    }
}
