namespace Refii.Tests.Launcher;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Launcher;
using Refii.Launcher.Core;

/// <summary>
/// Verifies unified <c>Fable: Return to Albion</c> branding constants and headless card state
/// transitions in <see cref="GameLaunchDock.EvaluateCardState"/> across Fable 2 and Fable 3.
/// </summary>
[TestSuite]
public class GameLaunchDockStateTests
{
    [TestCase]
    public void TestUnifiedLauncherBrandingAndChildWindowRemakeNames()
    {
        AssertThat(LauncherTheme.DefaultWindowTitle).IsEqual("Fable: Return to Albion");
        AssertThat(LauncherTitleScreen.UnifiedWordmark).Contains("FABLE");
        AssertThat(LauncherTitleScreen.UnifiedWordmark).Contains("RETURN TO ALBION");

        AssertThat(Engine_Title.Fable2.RemakeName).IsEqual("Fable 2: Return to Albion");
        AssertThat(Engine_Title.Fable3.RemakeName).IsEqual("Fable 3: Return to Albion");

        LauncherTitleScreen f2Screen = LauncherTitleScreen.For(Engine_Title.Fable2);
        LauncherTitleScreen f3Screen = LauncherTitleScreen.For(Engine_Title.Fable3);

        AssertThat(f2Screen.Wordmark).IsEqual(LauncherTitleScreen.UnifiedWordmark);
        AssertThat(f3Screen.Wordmark).IsEqual(LauncherTitleScreen.UnifiedWordmark);
        AssertThat(f2Screen.StatusBadge).IsNull();
        AssertThat(f3Screen.StatusBadge).IsEqual("Experimental");
        AssertThat(f3Screen.StatusTooltip).IsNotEmpty();
    }

    [TestCase]
    public void TestCardStateMatrixForPlayInstallAndBuildCode()
    {
        // Fable 2 installed & ready while Fable 3 is not installed:
        TitleCardState f2Ready = GameLaunchDock.EvaluateCardState(
            title: Engine_Title.Fable2,
            assetsExist: true,
            titleRuntimeExists: true,
            needsBuild: false);

        TitleCardState f3Uninstalled = GameLaunchDock.EvaluateCardState(
            title: Engine_Title.Fable3,
            assetsExist: false,
            titleRuntimeExists: false,
            needsBuild: false);

        AssertThat(f2Ready.Action).IsEqual(TitleCardActionKind.Play);
        AssertThat(f2Ready.ButtonLabel).IsEqual("Play");
        AssertThat(f2Ready.PrimaryEnabled).IsTrue();
        AssertThat(f2Ready.BadgeText).IsNull();

        AssertThat(f3Uninstalled.Action).IsEqual(TitleCardActionKind.Install);
        AssertThat(f3Uninstalled.ButtonLabel).IsEqual("Install");
        AssertThat(f3Uninstalled.PrimaryEnabled).IsTrue();
        AssertThat(f3Uninstalled.BadgeText).IsEqual("Experimental");

        // Needs game code build:
        TitleCardState f2NeedsBuild = GameLaunchDock.EvaluateCardState(
            title: Engine_Title.Fable2,
            assetsExist: true,
            titleRuntimeExists: true,
            needsBuild: true,
            needsBuildReason: "Native mod changed");

        AssertThat(f2NeedsBuild.Action).IsEqual(TitleCardActionKind.BuildGameCode);
        AssertThat(f2NeedsBuild.ButtonLabel).IsEqual("Build Code");
        AssertThat(f2NeedsBuild.StatusText).IsEqual("Native mod changed");
        AssertThat(f2NeedsBuild.PrimaryEnabled).IsTrue();

        // Installed assets, but title runtime is not built yet:
        TitleCardState f3NoRuntime = GameLaunchDock.EvaluateCardState(
            title: Engine_Title.Fable3,
            assetsExist: true,
            titleRuntimeExists: false,
            needsBuild: false);

        AssertThat(f3NoRuntime.Action).IsEqual(TitleCardActionKind.None);
        AssertThat(f3NoRuntime.ButtonLabel).IsEqual("Unavailable");
        AssertThat(f3NoRuntime.StatusText).IsEqual("Runtime not built");
        AssertThat(f3NoRuntime.PrimaryEnabled).IsFalse();
    }

    [TestCase]
    public void TestPipelineAndRunningGameDisableBothCardsCleanly()
    {
        // Pipeline running for Fable 2: Fable 2 shows progress, Fable 3 waits cleanly
        TitleCardState f2Building = GameLaunchDock.EvaluateCardState(
            title: Engine_Title.Fable2,
            assetsExist: true,
            titleRuntimeExists: true,
            needsBuild: true,
            pipelineRunning: true,
            pipelineTitle: Engine_Title.Fable2,
            pipelineLabel: "Fable 2: Building game code",
            pipelineStatus: "Compiling 40/100",
            pipelineFraction: 0.4f);

        TitleCardState f3Waiting = GameLaunchDock.EvaluateCardState(
            title: Engine_Title.Fable3,
            assetsExist: false,
            titleRuntimeExists: false,
            needsBuild: false,
            pipelineRunning: true,
            pipelineTitle: Engine_Title.Fable2,
            pipelineLabel: "Fable 2: Building game code",
            pipelineStatus: "Compiling 40/100",
            pipelineFraction: 0.4f);

        AssertThat(f2Building.PrimaryEnabled).IsFalse();
        AssertThat(f2Building.ButtonLabel).IsEqual("Working…");
        AssertThat(f2Building.StatusText).IsEqual("Compiling 40/100");
        AssertThat(f2Building.CardProgressFraction).IsEqualApprox(0.4f, 0.001f);

        AssertThat(f3Waiting.PrimaryEnabled).IsFalse();
        AssertThat(f3Waiting.StatusText).Contains("Fable 2");
        AssertThat(f3Waiting.CardProgressFraction).IsEqual(0.0f);

        // Fable 2 running in child process:
        TitleCardState f2Running = GameLaunchDock.EvaluateCardState(
            title: Engine_Title.Fable2,
            assetsExist: true,
            titleRuntimeExists: true,
            needsBuild: false,
            gameRunning: true,
            runningGameTitle: Engine_Title.Fable2);

        TitleCardState f3WhileF2Running = GameLaunchDock.EvaluateCardState(
            title: Engine_Title.Fable3,
            assetsExist: true,
            titleRuntimeExists: true,
            needsBuild: false,
            gameRunning: true,
            runningGameTitle: Engine_Title.Fable2);

        AssertThat(f2Running.PrimaryEnabled).IsFalse();
        AssertThat(f2Running.ButtonLabel).IsEqual("Running");
        AssertThat(f3WhileF2Running.PrimaryEnabled).IsFalse();
        AssertThat(f3WhileF2Running.StatusText).Contains("Fable 2");
    }
}
