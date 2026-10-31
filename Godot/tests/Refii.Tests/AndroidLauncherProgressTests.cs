using GdUnit4;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Shared launcher actions across the real Android installation lifecycle.</summary>
[TestSuite]
public sealed class AndroidLauncherProgressTests
{
    /// <summary>Preparation, generation, measured compilation, failure and playable completion have honest display states.</summary>
    [TestCase]
    public void InstallProgressFailureAndPlayLifecycle()
    {
        AssertThat(AndroidLauncherProgress.Label(false, false, "Choose ISO")).IsEqual("Install Fable 2");
        AssertThat(AndroidLauncherProgress.Fraction("Extracting game files: 40% (4/10)")).IsEqual(0.4f);
        AssertThat(AndroidLauncherProgress.Fraction("Generating game code on this phone")).IsEqual(0f);
        AssertThat(AndroidLauncherProgress.Label(true, true, "Compiling game code 10/20")).Contains("Compiling game code 10/20");
        AssertThat(AndroidLauncherProgress.Fraction("Compiling game code 10/20")).IsEqual(0.45f);
        AssertThat(AndroidLauncherProgress.Fraction("Compiling game code 9/0")).IsEqual(0f);
        AssertThat(AndroidLauncherProgress.Fraction("Copying selected file: 999%")).IsEqual(1f);
        AssertThat(AndroidLauncherProgress.Label(false, false, "Compilation stopped")).IsEqual("Install Fable 2");
        AssertThat(AndroidLauncherProgress.Label(false, true, "Cleanup finished")).IsEqual("Play Fable 2");
    }
}
