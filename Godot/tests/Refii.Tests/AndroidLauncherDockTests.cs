using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Core;
using Refii.Launcher;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The phone's Fable 2 and Fable 3 cards (AndroidLauncherDock).</summary>
[TestSuite]
public class AndroidLauncherDockTests
{
    [TestCase]
    public void InstalledTitlePlays()
    {
        TitleCardState state = AndroidLauncherDock.State(Engine_Title.Fable2, active: true, installing: false, installed: true,
            needsBuild: false, status: "", fraction: 0f);
        AssertThat(state.Action).IsEqual(TitleCardActionKind.Play);
        AssertThat(state.PrimaryEnabled).IsTrue();
    }

    [TestCase]
    public void MissingTitleInstalls()
    {
        TitleCardState state = AndroidLauncherDock.State(Engine_Title.Fable3, active: false, installing: false, installed: false,
            needsBuild: false, status: "", fraction: 0f);
        AssertThat(state.Action).IsEqual(TitleCardActionKind.Install);
        AssertThat(state.ButtonLabel).IsEqual("Install");
    }

    [TestCase]
    public void ActiveTitleWithStaleCodeBuilds()
    {
        TitleCardState state = AndroidLauncherDock.State(Engine_Title.Fable2, active: true, installing: false, installed: true,
            needsBuild: true, status: "", fraction: 0f);
        AssertThat(state.Action).IsEqual(TitleCardActionKind.BuildGameCode);
    }

    [TestCase]
    public void OtherTitleWithStaleCodeBuildsToo()
    {
        TitleCardState state = AndroidLauncherDock.State(Engine_Title.Fable3, active: false, installing: false, installed: true,
            needsBuild: true, status: "", fraction: 0f);
        AssertThat(state.Action).IsEqual(TitleCardActionKind.BuildGameCode);
    }

    [TestCase]
    public void AMissingTitleInstallsWhateverElseIsSaid()
    {
        TitleCardState state = AndroidLauncherDock.State(Engine_Title.Fable3, active: false, installing: false, installed: false,
            needsBuild: true, status: "", fraction: 0f);
        AssertThat(state.Action).IsEqual(TitleCardActionKind.Install);
    }

    [TestCase]
    public void InstallingDisablesBothCardsAndShowsProgressOnTheActiveOne()
    {
        TitleCardState active = AndroidLauncherDock.State(Engine_Title.Fable2, active: true, installing: true, installed: false,
            needsBuild: false, status: "Compiling game code 5/10", fraction: 0.4f);
        TitleCardState other = AndroidLauncherDock.State(Engine_Title.Fable3, active: false, installing: true, installed: true,
            needsBuild: false, status: "Compiling game code 5/10", fraction: 0.4f);
        AssertThat(active.PrimaryEnabled).IsFalse();
        AssertThat(active.CardProgressFraction).IsEqual(0.4f);
        AssertThat(other.PrimaryEnabled).IsFalse();
        AssertThat(other.CardProgressFraction).IsEqual(0f);
    }

    [TestCase]
    public void AnEmptyFolderIsNotInstalled()
    {
        string app = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "refii-dock-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(app);
        try
        {
            AssertThat(AndroidLauncherDock.InstallOf(app, Engine_Title.Fable3, "bundle")).IsEqual(AndroidLauncherDock.Install.Missing);
        }
        finally
        {
            System.IO.Directory.Delete(app, true);
        }
    }
}
