using GdUnit4;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>A failure reads as a message to the player, with the error's first line and no stack trace.</summary>
[TestSuite]
public sealed class AndroidFailureTextTests
{
    [TestCase]
    public void TestAFailureNamesTheGameTheReportAndOnlyTheErrorsFirstLine()
    {
        string text = AndroidFailureText.For(
            "System.IO.InvalidDataException: No files were mounted from game_root\n   at Refii.AndroidRuntime.AndroidGameHost.OnMountFailed()",
            "Fable 2");
        AssertThat(text).StartsWith("Fable 2 ran into a problem");
        AssertThat(text).Contains("File > Export logs and saves");
        AssertThat(text).EndsWith("Error: System.IO.InvalidDataException: No files were mounted from game_root");
        AssertThat(text).NotContains("   at ");
    }
}
