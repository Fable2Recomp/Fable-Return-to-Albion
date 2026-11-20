using System.IO;
using Refii.Core;
using Refii.Launcher;
using Refii.Launcher.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// The phone's Fable 2 and Fable 3 cards in the launch dock (<see cref="GameLaunchDock"/>), side by
/// side as on desktop, instead of one Play button with the title chooser on the Install tab.
/// </summary>
/// <remarks>
/// The phone's installer (<see cref="AndroidPhoneLauncher"/>) only knows the active title. The other
/// card reads whether its title is installed from that title's own folder, and a tap on it switches
/// titles first.
/// </remarks>
public static class AndroidLauncherDock
{
    /// <summary>A title's install as its card shows it.</summary>
    public enum Install
    {
        Missing,
        Ready,

        /// <summary>Installed, but its game code was built from another APK's bundle (<see cref="AndroidPhoneLauncher.IsStale"/>).</summary>
        NeedsBuild,
    }

    /// <summary>What <paramref name="title"/> has in the app's data folder, against this APK's bundle.</summary>
    /// <param name="apkBundle">This APK's bundle identity (<see cref="AndroidPhoneLauncher.ApkBundleIdentity"/>).</param>
    /// <remarks>Switched to, the installer may still find the old build current and keep it; this is the card's guess before that.</remarks>
    public static Install InstallOf(string appDirectory, Engine_Title title, string apkBundle)
    {
        string user = AndroidRuntimeOptions.TitleDirectory(appDirectory, title);
        try
        {
            if (!Directory.Exists(user)) return Install.Missing;
            AndroidPhoneCompiler.Result? compiled = AndroidInstallState.Load(user).Compiled;
            if (compiled == null && !File.Exists(Path.Combine(user, AndroidRuntimeOptions.FileName))) return Install.Missing;
            return AndroidPhoneLauncher.IsStale(compiled, apkBundle) ? Install.NeedsBuild : Install.Ready;
        }
        catch (System.Exception)
        {
            return Install.Missing;
        }
    }

    /// <summary>One card's state.</summary>
    /// <param name="title">The card's title.</param>
    /// <param name="active">Whether it is the active title, the one the installer below runs for.</param>
    /// <param name="installing">Whether the installer is working (for the active title).</param>
    /// <param name="installed">Whether the title can be played.</param>
    /// <param name="needsBuild">Whether the title's game code must be built first.</param>
    /// <param name="status">The installer's current status line.</param>
    /// <param name="fraction">The installer's progress, 0-1.</param>
    public static TitleCardState State(Engine_Title title, bool active, bool installing, bool installed,
        bool needsBuild, string status, float fraction)
    {
        if (installing)
        {
            return active
                ? Card(title, TitleCardActionKind.None, ProgressLabel.BuildButtonLabel("Installing", status), status, false, true, fraction)
                : Card(title, TitleCardActionKind.None, installed ? "Play" : "Install", $"Busy ({Engine_Title.Active.Name})", false, false, 0f);
        }

        if (installed && needsBuild)
            return Card(title, TitleCardActionKind.BuildGameCode, "Build Game Code", "This update changes the game's code", true, true, 0f);
        return installed
            ? Card(title, TitleCardActionKind.Play, "Play", "Ready", true, false, 0f)
            : Card(title, TitleCardActionKind.Install, "Install", "Not installed", true, false, 0f);
    }

    private static TitleCardState Card(Engine_Title title, TitleCardActionKind action, string label, string status,
        bool enabled, bool compact, float fraction) =>
        new(title, action, label, status, enabled, compact, ShowRunAsAdminOption: false, BadgeText: null,
            BadgeTooltip: string.Empty, CardProgressFraction: fraction);
}
