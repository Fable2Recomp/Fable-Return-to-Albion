using System.IO;
using GdUnit4;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests.Launcher;

/// <summary>
/// The bundled config is the app's own values under the player's file: read when the player's
/// file does not state a key, never written into it.
/// </summary>
[TestSuite]
public class IniDefaultsTests
{
    private const string Bundled =
        "[Github]\nFlag=true\nName=Owner/Repo\n\n[About]\nProjectPage=https://example.invalid/page\n";

    [TestCase]
    public void AKeyThePlayersFileDoesNotStateReadsTheBundledValue()
    {
        string path = Path.Combine(Path.GetTempPath(), "refii-ini-" + Path.GetRandomFileName(), "config.cfg");
        try
        {
            Ini.LoadDefaultsFromText(Bundled);
            Ini.LoadFromText("[Launcher]\nActiveTitle=refii\n", path);

            AssertThat(Ini.GetBool("Flag", false, "Github")).IsTrue();
            AssertThat(Ini.GetString("Name", "fallback", "Github")).IsEqual("Owner/Repo");
            AssertThat(Ini.GetString("ActiveTitle", "none", "Launcher")).IsEqual("refii");
            AssertThat(Ini.GetString("Missing", "fallback", "Github")).IsEqual("fallback");
        }
        finally
        {
            Ini.LoadDefaultsFromText(string.Empty);
            Ini.LoadFromText(string.Empty, path);
        }
    }

    [TestCase]
    public void ThePlayersValueWinsAndTheBundledOnesAreNotSaved()
    {
        string directory = Path.Combine(Path.GetTempPath(), "refii-ini-" + Path.GetRandomFileName());
        string path = Path.Combine(directory, "config.cfg");
        try
        {
            Ini.LoadDefaultsFromText(Bundled);
            Ini.LoadFromText("[Github]\nFlag=false\n", path);

            AssertThat(Ini.GetBool("Flag", true, "Github")).IsFalse();
            AssertThat(Ini.GetSection("Github")["Name"]).IsEqual("Owner/Repo");
            AssertThat(Ini.GetSection("Github")["Flag"]).IsEqual("false");

            Ini.Save();
            string saved = File.ReadAllText(path);
            AssertThat(saved.Contains("Flag=false")).IsTrue();
            AssertThat(saved.Contains("Owner/Repo")).IsFalse();
            AssertThat(saved.Contains("ProjectPage")).IsFalse();
        }
        finally
        {
            Ini.LoadDefaultsFromText(string.Empty);
            Ini.LoadFromText(string.Empty, path);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
