using System;

namespace Refii.Ui;

/// <summary>
/// Which owned books are built ahead of their first open, behind the loading screen
/// (<see cref="Engine_UiBookSet.PrebuildOne"/>). Every one on the desktop; on a phone only the pause
/// book and the Items book, and every other screen's book is built the first time it opens.
/// </summary>
/// <remarks>
/// Prebuilding builds every book with chrome, 24 in Fable 2, whether or not the player ever opens
/// its screen, and a built book stays. At Bower Lake on Balanced that took a Galaxy S25+ from
/// 4.74 GB to 5.74 GB (native heap 1.70 to 2.54 GB), on phones the game is killed on for memory.
/// Built on demand, a book costs its build on the frame its screen first opens: 0.75-0.8 s on the
/// S25+, and 2.0-2.3 s on a Galaxy S10e, where pause and Items had opened in 0.85 s prebuilt.
/// Those two are the screens a player opens first and most (Weapons opens in the Items book), so they are still built ahead.
/// </remarks>
public static class Engine_UiBookPrebuild
{
    private static readonly string[] PhoneBooks = { "pause", "items" };

    /// <summary>True when the book of the screen template <paramref name="name"/> is built behind the loading screen.</summary>
    public static bool Wants(string name) =>
        !Refii.Core.Engine_Platform.IsAndroid || Array.IndexOf(PhoneBooks, name) >= 0;
}
