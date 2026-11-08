namespace Refii.Tests.Launcher;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Bnk;
using Refii.Launcher.Core;

[TestSuite]
public class InstallerTests
{
    [TestCase]
    public void TestInstallTitleUpdateWithEmptySourceOnMissingPathsDoesNotThrow()
    {
        string nonExistentRoot = Path.Combine(Refii.Tests.TestTemp.Root, "refii_test_nonexistent_" + Guid.NewGuid().ToString("N"));
        string patchPath = Path.Combine(nonExistentRoot, "game", "default.xexp");

        // When sourcePath is empty (unpatched), InstallTitleUpdate deletes previous patch/bank files.
        // It must safely succeed without throwing DirectoryNotFoundException even when the directory does not exist.
        // Note: SafePath check will validate against install base path.
        bool ok = Installer.InstallTitleUpdate(string.Empty, patchPath, out string error);
        // It returns false or true depending on SafePath, but must never throw an unhandled DirectoryNotFoundException.
        AssertThat(error).IsNotEqual("Could not find a part of the path");
    }

    [TestCase]
    public void TestHasExtractedAssetsFailsWhenInstallFailedMarkerExists()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_test_assets_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string marker = Path.Combine(tempDir, "default.xex");
            File.WriteAllText(marker, "stub_marker");
            string bank = Path.Combine(tempDir, "test.bnk");
            File.WriteAllText(bank, "stub_bnk");

            AssertThat(Installer.HasExtractedAssets(tempDir, marker)).IsTrue();

            // When .install-failed marker is present, HasExtractedAssets must return false
            string failedMarker = Path.Combine(tempDir, Installer.InstallFailedFileName);
            File.WriteAllText(failedMarker, "Extraction failed on xyz.bnk");

            AssertThat(Installer.HasExtractedAssets(tempDir, marker)).IsFalse();

            // When removed, it returns true again
            File.Delete(failedMarker);
            AssertThat(Installer.HasExtractedAssets(tempDir, marker)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [TestCase]
    public void TestBnkArchiveFileSharingAllowsConcurrentWriter()
    {
        string tempFile = Path.Combine(Refii.Tests.TestTemp.Root, "refii_share_test_" + Guid.NewGuid().ToString("N") + ".bnk");
        // Write minimal V3 BNK file: 16-byte header
        byte[] minimalBnk = new byte[32];
        minimalBnk[3] = 0x10; // header pointer = 16
        minimalBnk[7] = 0x03; // format version = 3
        File.WriteAllBytes(tempFile, minimalBnk);

        try
        {
            // Open with BnkArchive
            bool opened = BnkArchive.TryOpen(tempFile, out BnkArchive? archive, out string openErr);
            // Even if parsing fails or succeeds, test file stream sharing
            using var fileStream = new FileStream(
                tempFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            // A writer specifying FileShare.ReadWrite | FileShare.Delete must be able to open/create the file
            // without being blocked by an IOException
            using var writer = new FileStream(
                tempFile,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);

            AssertThat(writer.CanWrite).IsTrue();
            archive?.Dispose();
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
