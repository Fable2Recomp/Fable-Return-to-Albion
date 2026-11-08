namespace Refii.Tests.Launcher;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Launcher.Core;

[TestSuite]
public class SupportedGameTests
{
    private static XexIdentity Executable(SupportedGame.Pressing pressing, uint version) => new()
    {
        TitleId = SupportedGame.TitleId,
        MediaId = (byte[])pressing.MediaId.Clone(),
        Version = version,
        SourceVersion = version,
    };

    [TestCase]
    public void TestEveryPressingAcceptsItsOwnDisc()
    {
        foreach (SupportedGame.Pressing pressing in SupportedGame.Pressings)
        {
            AssertThat(SupportedGame.IsExecutable(Executable(pressing, pressing.BaseVersion), pressing, out string error))
                .OverrideFailureMessage(pressing.Name + ": " + error).IsTrue();
        }
    }

    [TestCase]
    public void TestFrenchDiscIsVersion27WithoutTitleUpdate()
    {
        SupportedGame.Pressing france = SupportedGame.France;
        AssertThat(france.ShortMediaIdText).IsEqual("1358B1A4");
        AssertThat(XexIdentity.FormatVersion(france.BaseVersion)).IsEqual("0.0.0.27");
        AssertThat(france.HasTitleUpdate).IsFalse();

        // The base build's version on the French media id is a wrong disc, not a wrong pick.
        AssertThat(SupportedGame.IsExecutable(Executable(france, SupportedGame.UsaEurope.BaseVersion), out _)).IsFalse();

        // A patch naming the French media id is refused: none is supported for it.
        var patch = new XexIdentity
        {
            IsPatch = true,
            MediaId = (byte[])france.MediaId.Clone(),
            SourceVersion = france.BaseVersion,
            Version = 0x0000011B,
        };
        AssertThat(SupportedGame.IsTitleUpdate(patch, france, out string error)).IsFalse();
        AssertThat(error).Contains("No title update is supported for the France pressing");
    }

    [TestCase]
    public void TestSpanishDiscIsVersion29WithTitleUpdate08T()
    {
        SupportedGame.Pressing spain = SupportedGame.Spain;
        AssertThat(spain.ShortMediaIdText).IsEqual("772E8EE8");
        AssertThat(XexIdentity.FormatVersion(spain.BaseVersion)).IsEqual("0.0.0.29");
        AssertThat(XexIdentity.FormatVersion(spain.UpdatedVersion)).IsEqual("0.0.1.29");
        AssertThat(spain.TitleUpdateName).IsEqual("TU_16L61VH_000003K000000.000000000008T");

        var patch = new XexIdentity
        {
            IsPatch = true,
            TitleId = SupportedGame.TitleId,
            MediaId = (byte[])spain.MediaId.Clone(),
            SourceVersion = spain.BaseVersion,
            Version = spain.UpdatedVersion,
        };
        bool accepted = SupportedGame.IsTitleUpdate(patch, spain, out string error);
        AssertThat(error).IsEmpty();
        AssertThat(accepted).IsTrue();

        // The USA/Europe update names another media id: a wrong pick for Spain.
        var usa = new XexIdentity
        {
            IsPatch = true,
            TitleId = SupportedGame.TitleId,
            MediaId = (byte[])SupportedGame.UsaEurope.MediaId.Clone(),
            SourceVersion = SupportedGame.UsaEurope.BaseVersion,
            Version = SupportedGame.UsaEurope.UpdatedVersion,
        };
        AssertThat(SupportedGame.IsTitleUpdate(usa, spain, out _)).IsFalse();
    }

    [TestCase]
    public void TestDiscOfAnotherPressingIsReportedAsWrongPick()
    {
        XexIdentity french = Executable(SupportedGame.France, SupportedGame.France.BaseVersion);
        AssertThat(SupportedGame.IsExecutable(french, SupportedGame.UsaEurope, out string error)).IsFalse();
        AssertThat(error).Contains("Select France (1358B1A4)");
    }
}
