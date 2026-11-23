namespace Refii.Tests.Launcher;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Launcher.Core;

[TestSuite]
public class SupportedGameTests
{
    // A pressing known only by its short id still names a 16-byte media id on the disc.
    private static byte[] FullMediaId(SupportedGame.Pressing pressing)
    {
        byte[] full = new byte[16];
        pressing.MediaId.CopyTo(full, 16 - pressing.MediaId.Length);
        return full;
    }

    private static XexIdentity Executable(SupportedGame.Pressing pressing, uint version) => new()
    {
        TitleId = SupportedGame.TitleId,
        MediaId = FullMediaId(pressing),
        Version = version,
        SourceVersion = version,
        ImageDigest = SupportedGame.Fable2ImageDigest,
        FileKey = SupportedGame.Fable2FileKey,
    };

    private static XexIdentity Update(SupportedGame.Pressing pressing) => new()
    {
        IsPatch = true,
        TitleId = SupportedGame.TitleId,
        MediaId = FullMediaId(pressing),
        SourceVersion = pressing.BaseVersion,
        Version = pressing.UpdatedVersion,
        DeltaDigest = SupportedGame.Fable2DeltaDigest,
    };

    [TestCase]
    public void TestADiscOrUpdateWithAnotherProgramIsRefusedWhateverItsMediaId()
    {
        foreach (SupportedGame.Pressing pressing in SupportedGame.Pressings)
        {
            XexIdentity disc = Executable(pressing, pressing.BaseVersion);
            disc.ImageDigest = new byte[20];
            AssertThat(SupportedGame.IsExecutable(disc, out string error)).IsFalse();
            AssertThat(error).Contains("not the program this build was made from");

            disc = Executable(pressing, pressing.BaseVersion);
            disc.FileKey = new byte[16];
            AssertThat(SupportedGame.IsExecutable(disc, out _)).IsFalse();

            XexIdentity update = Update(pressing);
            AssertThat(SupportedGame.IsTitleUpdate(update, pressing, out error))
                .OverrideFailureMessage(pressing.Name + ": " + error).IsTrue();
            update.DeltaDigest = new byte[20];
            AssertThat(SupportedGame.IsTitleUpdate(update, pressing, out error)).IsFalse();
            AssertThat(error).Contains("does not carry the changes");
        }
    }

    [TestCase]
    public void TestGermanDiscIsMatchedByItsShortMediaId()
    {
        SupportedGame.Pressing germany = SupportedGame.Germany;
        AssertThat(germany.ShortMediaIdText).IsEqual("1D1F480C");
        AssertThat(germany.DisplayName).IsEqual("Germany (1D1F480C)");
        AssertThat(XexIdentity.FormatVersion(germany.BaseVersion)).IsEqual("0.0.0.28");
        AssertThat(germany.TitleUpdateName).IsEqual("TU_16L61VH_000003G000000.000000000008S");

        // Whatever the first twelve bytes are, the last dword names the pressing.
        XexIdentity disc = Executable(germany, germany.BaseVersion);
        disc.MediaId[0] = 0xAB;
        AssertThat(SupportedGame.IsExecutable(disc, germany, out string error))
            .OverrideFailureMessage("refused: " + error).IsTrue();
        disc.MediaId[15] ^= 1;
        AssertThat(SupportedGame.IsExecutable(disc, out _)).IsFalse();
    }

    [TestCase]
    public void TestTheHeaderReaderTakesTheDigestsFromTheirFields()
    {
        // Base image: security block at 0x40, no optional headers but the execution info.
        byte[] image = new byte[0x400];
        "XEX2"u8.CopyTo(image);
        image[0x13] = 0x40;
        image[0x17] = 1;
        new byte[] { 0x00, 0x04, 0x00, 0x06, 0x00, 0x00, 0x03, 0x00 }.CopyTo(image, 0x18);
        image[0x307] = 0x1C;
        new byte[] { 0x4D, 0x53, 0x07, 0xF1 }.CopyTo(image, 0x30C);
        SupportedGame.Fable2ImageDigest.CopyTo(image, 0x40 + 0x114);
        SupportedGame.Germany.MediaId.CopyTo(image, 0x40 + 0x140 + 12);
        SupportedGame.Fable2FileKey.CopyTo(image, 0x40 + 0x150);

        AssertThat(XexIdentity.TryRead(image, out XexIdentity disc, out string error))
            .OverrideFailureMessage("refused: " + error).IsTrue();
        AssertThat(SupportedGame.IsExecutable(disc, SupportedGame.Germany, out error))
            .OverrideFailureMessage("refused: " + error).IsTrue();

        // Patch: file format info at 0x200 (hash at +0x10), delta descriptor at 0x300.
        byte[] patch = new byte[0x400];
        "XEX2"u8.CopyTo(patch);
        patch[0x13] = 0x40;
        patch[0x17] = 2;
        new byte[] { 0x00, 0x00, 0x03, 0xFF, 0x00, 0x00, 0x02, 0x00 }.CopyTo(patch, 0x18);
        new byte[] { 0x00, 0x00, 0x05, 0xFF, 0x00, 0x00, 0x03, 0x00 }.CopyTo(patch, 0x20);
        SupportedGame.Fable2DeltaDigest.CopyTo(patch, 0x210);
        patch[0x306] = 0x01;
        patch[0x307] = 0x1C;
        patch[0x30B] = 0x1C;
        SupportedGame.Germany.MediaId.CopyTo(patch, 0x40 + 0x140 + 12);

        AssertThat(XexIdentity.TryRead(patch, out XexIdentity update, out error))
            .OverrideFailureMessage("refused: " + error).IsTrue();
        AssertThat(update.IsPatch).IsTrue();
        AssertThat(SupportedGame.IsTitleUpdate(update, SupportedGame.Germany, out error))
            .OverrideFailureMessage("refused: " + error).IsTrue();
    }

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
    public void TestFrenchDiscIsVersion27WithTitleUpdate08R()
    {
        SupportedGame.Pressing france = SupportedGame.France;
        AssertThat(france.ShortMediaIdText).IsEqual("1358B1A4");
        AssertThat(XexIdentity.FormatVersion(france.BaseVersion)).IsEqual("0.0.0.27");
        AssertThat(XexIdentity.FormatVersion(france.UpdatedVersion)).IsEqual("0.0.1.27");
        AssertThat(france.TitleUpdateName).IsEqual("TU_16L61VH_000003C000000.000000000008R");

        // The base build's version on the French media id is a wrong disc, not a wrong pick.
        AssertThat(SupportedGame.IsExecutable(Executable(france, SupportedGame.UsaEurope.BaseVersion), out _)).IsFalse();

        AssertThat(SupportedGame.IsTitleUpdate(Update(france), france, out string error))
            .OverrideFailureMessage("refused: " + error).IsTrue();
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
            DeltaDigest = SupportedGame.Fable2DeltaDigest,
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
    public void TestItalianDiscIsVersion30WithTitleUpdate08U()
    {
        SupportedGame.Pressing italy = SupportedGame.Italy;
        AssertThat(italy.ShortMediaIdText).IsEqual("51F48E54");
        AssertThat(XexIdentity.FormatVersion(italy.BaseVersion)).IsEqual("0.0.0.30");
        AssertThat(XexIdentity.FormatVersion(italy.UpdatedVersion)).IsEqual("0.0.1.30");
        AssertThat(italy.TitleUpdateName).IsEqual("TU_16L61VH_000003O000000.000000000008U");

        var patch = new XexIdentity
        {
            IsPatch = true,
            TitleId = SupportedGame.TitleId,
            MediaId = (byte[])italy.MediaId.Clone(),
            SourceVersion = italy.BaseVersion,
            Version = italy.UpdatedVersion,
            DeltaDigest = SupportedGame.Fable2DeltaDigest,
        };
        bool accepted = SupportedGame.IsTitleUpdate(patch, italy, out string error);
        AssertThat(error).IsEmpty();
        AssertThat(accepted).IsTrue();

        // The Spanish update names another media id: a wrong pick for Italy.
        var spain = new XexIdentity
        {
            IsPatch = true,
            TitleId = SupportedGame.TitleId,
            MediaId = (byte[])SupportedGame.Spain.MediaId.Clone(),
            SourceVersion = SupportedGame.Spain.BaseVersion,
            Version = SupportedGame.Spain.UpdatedVersion,
        };
        AssertThat(SupportedGame.IsTitleUpdate(spain, italy, out _)).IsFalse();
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
