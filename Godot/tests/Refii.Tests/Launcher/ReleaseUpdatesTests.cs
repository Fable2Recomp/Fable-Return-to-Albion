using System;
using System.IO;
using System.IO.Compression;
using GdUnit4;
using Refii.Launcher.Core;
using Refii.Launcher.Updates;
using static GdUnit4.Assertions;

namespace Refii.Tests.Launcher;

/// <summary>
/// The app's own updates: which published build a channel means, when it is another build than
/// the one installed, and what extracting its package may write and remove.
/// </summary>
[TestSuite]
public class ReleaseUpdatesTests
{
    // GitHub lists releases newest first; a draft is not a published build.
    private const string Listing = """
        [
          {"tag_name":"beta-9","name":"Beta 9","draft":true,"assets":[
            {"name":"App-linux-x86_64.zip","browser_download_url":"https://example.invalid/b9","size":9,"updated_at":"2026-12-09T00:00:00Z"}]},
          {"tag_name":"nightly","name":"Nightly dev build 2026-12-02","draft":false,"prerelease":true,"assets":[
            {"name":"App-linux-x86_64.zip","browser_download_url":"https://example.invalid/n-linux","size":5,"updated_at":"2026-12-02T03:30:00Z"},
            {"name":"App-windows-arm64.zip","browser_download_url":"https://example.invalid/n-woa","size":6,"updated_at":"2026-12-02T03:40:00Z"}]},
          {"tag_name":"beta-8","name":"","draft":false,"assets":[
            {"name":"App-windows-x86_64.zip","browser_download_url":"https://example.invalid/b8-win","size":7,"updated_at":"2026-12-01T10:00:00Z"}]},
          {"tag_name":"beta-7","name":"Beta 7","draft":false,"assets":[
            {"name":"App-linux-x86_64.zip","browser_download_url":"https://example.invalid/b7-linux","size":4,"updated_at":"2026-11-22T10:00:00Z"}]}
        ]
        """;

    [TestCase]
    public void EachChannelMeansItsNewestPublishedBuildForThisMachine()
    {
        string linux = ReleaseUpdates.AssetSuffix(windows: false, arm64: false);
        AssertThat(linux).IsEqual("-linux-x86_64.zip");
        AssertThat(ReleaseUpdates.AssetSuffix(windows: true, arm64: true)).IsEqual("-windows-arm64.zip");

        ReleaseBuild? nightly = ReleaseUpdates.Pick(Listing, UpdateChannel.Nightly, linux);
        AssertThat(nightly!.Tag).IsEqual("nightly");
        AssertThat(nightly.AssetUrl).IsEqual("https://example.invalid/n-linux");
        AssertThat(nightly.Title).IsEqual("Nightly dev build 2026-12-02");

        // Beta 9 is a draft and Beta 8 has no Linux package: the release channel is Beta 7 here.
        ReleaseBuild? release = ReleaseUpdates.Pick(Listing, UpdateChannel.Release, linux);
        AssertThat(release!.Tag).IsEqual("beta-7");
        AssertThat(release.AssetSize).IsEqual(4L);

        // A release with no name is shown by its tag.
        ReleaseBuild? windows = ReleaseUpdates.Pick(Listing, UpdateChannel.Release, "-windows-x86_64.zip");
        AssertThat(windows!.Title).IsEqual("beta-8");

        AssertThat(ReleaseUpdates.Pick(Listing, UpdateChannel.Release, "-linux-arm64.zip")).IsNull();
        AssertThat(ReleaseUpdates.Pick("{\"message\":\"Not Found\"}", UpdateChannel.Release, linux)).IsNull();
        AssertThat(ReleaseUpdates.Pick("not json", UpdateChannel.Nightly, linux)).IsNull();
    }

    [TestCase]
    public void ABuildIsNewerByItsStampOrByTimeWhenNoStampIsRecorded()
    {
        var build = new ReleaseBuild("nightly", "Nightly", "App-linux-x86_64.zip", "u", 1, "2026-12-02T03:30:00Z");

        // The nightly's tag is reused, so the upload time is part of what identifies a build.
        AssertThat(ReleaseUpdates.IsNewer(build, build.Stamp, null)).IsFalse();
        AssertThat(ReleaseUpdates.IsNewer(build, "nightly@2026-12-01T03:30:00Z", null)).IsTrue();
        AssertThat(ReleaseUpdates.IsNewer(build, "beta-7@2026-11-22T10:00:00Z", null)).IsTrue();

        // Unpacked by hand: the same run links the executable minutes before it uploads the package.
        var published = new DateTime(2026, 12, 2, 3, 30, 0, DateTimeKind.Utc);
        AssertThat(ReleaseUpdates.IsNewer(build, string.Empty, published.AddMinutes(-25))).IsFalse();
        AssertThat(ReleaseUpdates.IsNewer(build, string.Empty, published.AddDays(-1))).IsTrue();
        AssertThat(ReleaseUpdates.IsNewer(build, string.Empty, published.AddDays(3))).IsFalse();
        AssertThat(ReleaseUpdates.IsNewer(build, string.Empty, null)).IsTrue();
    }

    [TestCase]
    public void APackageEntryLandsUnderTheInstallWithoutItsTopFolder()
    {
        AssertThat(ReleaseInstaller.InstallPath("App Name/bin/tool.so")).IsEqual("bin/tool.so");
        AssertThat(ReleaseInstaller.InstallPath("App Name\\sdk\\a.txt")).IsEqual("sdk/a.txt");
        AssertThat(ReleaseInstaller.InstallPath("App Name/")).IsEqual(string.Empty);
        AssertThat(ReleaseInstaller.InstallPath("App Name/bin/")).IsEqual(string.Empty);
        AssertThat(ReleaseInstaller.InstallPath("loose.txt")).IsEqual(string.Empty);
        AssertThat(ReleaseInstaller.InstallPath("App Name/../outside.txt")).IsEqual(string.Empty);
    }

    [TestCase]
    public void ExtractingReplacesTheAppsFilesAndRemovesOnlyWhatAPackagePutThere()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "refii-update-" + Path.GetRandomFileName());
        string install = Path.Combine(scratch, "install");
        try
        {
            Directory.CreateDirectory(Path.Combine(install, "assets", "saves"));
            Directory.CreateDirectory(Path.Combine(install, "bin", "recompile", "include", "01"));
            File.WriteAllText(Path.Combine(install, "assets", "saves", "hero.sav"), "the player's");
            File.WriteAllText(Path.Combine(install, "refii.toml"), "the player's settings");
            File.WriteAllText(Path.Combine(install, "app.bin"), "old app");
            File.WriteAllText(Path.Combine(install, "bin", "recompile", "include", "01", "stale.h"), "an older build's header");

            string first = Package(scratch, "first.zip",
                ("App/app.bin", "app one"), ("App/bin/tool.so", "tool one"), ("App/sdk/gone-later.txt", "sdk one"));
            AssertThat(ReleaseInstaller.Extract(first, install)).IsEqual(string.Empty);

            AssertThat(File.ReadAllText(Path.Combine(install, "app.bin"))).IsEqual("app one");
            AssertThat(File.ReadAllText(Path.Combine(install, "bin", "tool.so"))).IsEqual("tool one");
            // No record of an earlier package: the numbered include folders do not carry over.
            AssertThat(File.Exists(Path.Combine(install, "bin", "recompile", "include", "01", "stale.h"))).IsFalse();
            AssertThat(File.ReadAllText(Path.Combine(install, "assets", "saves", "hero.sav"))).IsEqual("the player's");
            AssertThat(File.ReadAllText(Path.Combine(install, "refii.toml"))).IsEqual("the player's settings");

            // Something the player adds between two updates is not a package's file.
            File.WriteAllText(Path.Combine(install, "sdk", "mine.txt"), "the player's");

            string second = Package(scratch, "second.zip", ("App/app.bin", "app two"), ("App/bin/tool.so", "tool two"));
            AssertThat(ReleaseInstaller.Extract(second, install)).IsEqual(string.Empty);

            AssertThat(File.ReadAllText(Path.Combine(install, "app.bin"))).IsEqual("app two");
            AssertThat(File.ReadAllText(Path.Combine(install, "bin", "tool.so"))).IsEqual("tool two");
            AssertThat(File.Exists(Path.Combine(install, "sdk", "gone-later.txt"))).IsFalse();
            AssertThat(File.ReadAllText(Path.Combine(install, "sdk", "mine.txt"))).IsEqual("the player's");
            AssertThat(File.ReadAllText(Path.Combine(install, "assets", "saves", "hero.sav"))).IsEqual("the player's");

            string[] recorded = File.ReadAllLines(
                Path.Combine(ReleaseInstaller.WorkDirectory(install), ReleaseInstaller.ManifestName));
            AssertThat(recorded.Length).IsEqual(2);
            AssertThat(Directory.GetFiles(install, "*-update", SearchOption.AllDirectories).Length).IsEqual(0);

            string empty = Package(scratch, "empty.zip", ("loose.txt", "no top folder"));
            AssertThat(ReleaseInstaller.Extract(empty, install).Length).IsGreater(0);
            AssertThat(File.ReadAllText(Path.Combine(install, "app.bin"))).IsEqual("app two");
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    private static string Package(string directory, string name, params (string Path, string Text)[] files)
    {
        string path = Path.Combine(directory, name);
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string entryPath, string text) in files)
        {
            using StreamWriter writer = new(archive.CreateEntry(entryPath).Open());
            writer.Write(text);
        }

        return path;
    }
}
