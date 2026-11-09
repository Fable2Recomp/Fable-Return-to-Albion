using System.Collections.Generic;
using Refii.Core;
using Refii.Lighting;

namespace Refii.AndroidRuntime;

/// <summary>
/// The launcher's "Baked lamp shadows" option: the desktop's static shadow sweep
/// (<see cref="Engine_LevelShadowPrebuild"/>), which a phone's startup does not run, offered as a
/// choice. On, Play bakes what is missing first; a bake stopped part way carries on at the next Play,
/// and an area with no bake plays as it does with the option off.
/// </summary>
public static class AndroidBakedShadows
{
    /// <summary>The refii.toml setting. Off unless the player turns it on.</summary>
    public const string Key = "android_baked_shadows";

    /// <summary>Free storage a bake asks for: Fable 2's areas came to 1.7 GB at the default resolution.</summary>
    public const long StorageNeeded = 2_500_000_000;

    /// <summary>Whether the player has the option on.</summary>
    /// <param name="values">The runtime configuration's values.</param>
    public static bool Wanted(IReadOnlyDictionary<string, string> values) => Engine_RefiiConfig.Bool(values, Key, false);

    /// <summary>Whether a level load ignores any baked file: a phone with the option off, whatever is on disk.</summary>
    public static bool LevelsIgnoreBakes => Engine_Platform.IsAndroid && !Wanted(Engine_RefiiConfig.Read(out _));

    /// <summary>Whether the sweep keeps only its latest bake for reuse. Scenarios sharing one are neighbours, and on
    /// a phone every atlas kept is tens of megabytes of heap for the rest of the sweep.</summary>
    public static bool KeepsOneBake => Engine_Platform.IsAndroid;

    /// <summary>Between two areas of a sweep on a phone, gives back the area's memory. Every model's triangles
    /// stay cached otherwise, and the S25+ ran out of its 11 GB partway through the sweep.</summary>
    public static void AfterArea()
    {
        if (!Engine_Platform.IsAndroid) return;
        Engine_LevelShadowMaps.ClearBakeTriangles();
        System.GC.Collect();
    }

    /// <summary>Whether Play has to run the sweep first. Only Fable 2, the title the bake was made for.</summary>
    /// <param name="wanted">The option's value.</param>
    /// <param name="title">The title about to start.</param>
    /// <param name="settled">The sweep already finished, stopped or failed since the launcher opened.</param>
    public static bool ShouldBake(bool wanted, Engine_Title title, bool settled) =>
        wanted && !settled && ReferenceEquals(title, Engine_Title.Fable2);

    /// <summary>The launcher's line for the sweep's progress.</summary>
    /// <param name="progress">The sweep's latest report.</param>
    public static string Line(Engine_LevelShadowPrebuild.Progress progress) =>
        progress.Total == 0 || progress.Built == 0 && progress.Done == progress.UpToDate
            ? "Checking baked lamp shadows..."
            : $"Baking lamp shadows: {progress.Done:N0} of {progress.Total:N0} areas. "
              + "This can take a long time. You can cancel from the File menu and carry on next time.";

    /// <summary>The launcher's line after a sweep that did not reach the game.</summary>
    /// <param name="progress">The sweep's result.</param>
    public static string Stopped(Engine_LevelShadowPrebuild.Progress progress) =>
        $"Baking stopped at {progress.Done:N0} of {progress.Total:N0} areas. "
        + "Press Play to start without the rest, or reopen the launcher to carry on baking.";
}
