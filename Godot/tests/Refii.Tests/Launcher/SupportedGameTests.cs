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
    public void TestFable3DiscTakesTitleUpdate3()
    {
        Refii.Core.Engine_Title previous = Refii.Core.Engine_Title.Active;
        Refii.Core.Engine_Title.Active = Refii.Core.Engine_Title.Fable3;
        try
        {
            SupportedGame.Pressing disc = SupportedGame.Fable3UsaEurope;
            AssertThat(disc.ShortMediaIdText).IsEqual("D99E18E7");
            AssertThat(XexIdentity.FormatVersion(disc.BaseVersion)).IsEqual("0.0.0.3");
            AssertThat(XexIdentity.FormatVersion(disc.UpdatedVersion)).IsEqual("0.0.3.3");
            AssertThat(disc.TitleUpdateName).IsEqual("TU_16L626M_000000C000000.00000000000O3");

            // The update's delta descriptor: source 0x3, target 0x303.
            var patch = new XexIdentity
            {
                IsPatch = true,
                TitleId = SupportedGame.TitleId,
                MediaId = (byte[])disc.MediaId.Clone(),
                SourceVersion = 0x00000003,
                Version = 0x00000303,
            };
            bool accepted = SupportedGame.IsTitleUpdate(patch, disc, out string error);
            AssertThat(error).IsEmpty();
            AssertThat(accepted).IsTrue();

            // TU4 (0.0.4.7) patches 0.0.0.7, the 16003BAB pressing, not this disc's 0.0.0.3.
            var tu4 = new XexIdentity
            {
                IsPatch = true,
                TitleId = SupportedGame.TitleId,
                MediaId = (byte[])disc.MediaId.Clone(),
                SourceVersion = 0x00000007,
                Version = 0x00000407,
            };
            AssertThat(SupportedGame.IsTitleUpdate(tu4, disc, out _)).IsFalse();

            // The generator reads the patched executable's own config when the update is installed.
            AssertThat(Refii.Core.Engine_Title.Fable3.VersionFolder(true)).IsEqual("Fable3TU3");
            AssertThat(Refii.Core.Engine_Title.Fable3.VersionFolder(false)).IsEqual("Fable3");
            AssertThat(Refii.Core.Engine_Title.Fable3.IsUnpatched("Fable3")).IsTrue();
            AssertThat(Refii.Core.Engine_Title.Fable3.IsUnpatched("Fable3TU3")).IsFalse();
        }
        finally
        {
            Refii.Core.Engine_Title.Active = previous;
        }
    }

    [TestCase]
    public void TestDiscOfAnotherPressingIsReportedAsWrongPick()
    {
        XexIdentity french = Executable(SupportedGame.France, SupportedGame.France.BaseVersion);
        AssertThat(SupportedGame.IsExecutable(french, SupportedGame.UsaEurope, out string error)).IsFalse();
        AssertThat(error).Contains("Select France (1358B1A4)");
    }
}
