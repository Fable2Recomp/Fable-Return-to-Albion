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
            // Nothing of the staging or the backup outlives a finished update.
            AssertThat(Directory.GetFileSystemEntries(ReleaseInstaller.WorkDirectory(install)).Length).IsEqual(1);

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

    [TestCase]
    public void AFailedApplyPutsThePreviousBuildBack()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "refii-update-" + Path.GetRandomFileName());
        string install = Path.Combine(scratch, "install");
        try
        {
            Directory.CreateDirectory(install);
            string first = Package(scratch, "first.zip",
                ("App/a.bin", "a one"), ("App/sdk/dropped.txt", "dropped one"), ("App/z.bin", "z one"));
            AssertThat(ReleaseInstaller.Extract(first, install)).IsEqual(string.Empty);

            // The second package's last file cannot land: a folder the player made has its name.
            Directory.CreateDirectory(Path.Combine(install, "zz-blocked"));
            File.WriteAllText(Path.Combine(install, "zz-blocked", "keep.txt"), "the player's");
            string second = Package(scratch, "second.zip",
                ("App/a.bin", "a two"), ("App/new.bin", "new two"), ("App/z.bin", "z two"), ("App/zz-blocked", "in the way"));
            string error = ReleaseInstaller.Extract(second, install);

            AssertThat(error).Contains("previous build was put back");
            AssertThat(File.ReadAllText(Path.Combine(install, "a.bin"))).IsEqual("a one");
            AssertThat(File.ReadAllText(Path.Combine(install, "z.bin"))).IsEqual("z one");
            AssertThat(File.ReadAllText(Path.Combine(install, "sdk", "dropped.txt"))).IsEqual("dropped one");
            AssertThat(File.Exists(Path.Combine(install, "new.bin"))).IsFalse();
            AssertThat(File.ReadAllText(Path.Combine(install, "zz-blocked", "keep.txt"))).IsEqual("the player's");
            string work = ReleaseInstaller.WorkDirectory(install);
            AssertThat(File.ReadAllLines(Path.Combine(work, ReleaseInstaller.ManifestName)).Length).IsEqual(3);
            AssertThat(Directory.GetFileSystemEntries(work).Length).IsEqual(1);

            // The install still updates once nothing is in the way.
            Directory.Delete(Path.Combine(install, "zz-blocked"), recursive: true);
            AssertThat(ReleaseInstaller.Extract(second, install)).IsEqual(string.Empty);
            AssertThat(File.ReadAllText(Path.Combine(install, "a.bin"))).IsEqual("a two");
            AssertThat(File.Exists(Path.Combine(install, "sdk", "dropped.txt"))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    [TestCase]
    public void AnApplyCutShortIsUndoneAtTheNextStart()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "refii-update-" + Path.GetRandomFileName());
        string install = Path.Combine(scratch, "install");
        try
        {
            // The state a power cut leaves: the plan written, one file swapped, one added, one
            // moved to the backup with nothing yet in its place, one still staged.
            string work = ReleaseInstaller.WorkDirectory(install);
            string backup = Path.Combine(work, "backup-12345678");
            Directory.CreateDirectory(Path.Combine(backup, "sdk"));
            Directory.CreateDirectory(Path.Combine(work, "staged"));
            File.WriteAllText(Path.Combine(install, "a.bin"), "a two");
            File.WriteAllText(Path.Combine(backup, "a.bin"), "a one");
            File.WriteAllText(Path.Combine(install, "new.bin"), "new two");
            File.WriteAllText(Path.Combine(backup, "sdk", "b.txt"), "b one");
            File.WriteAllText(Path.Combine(work, "staged", "late.bin"), "late two");
            File.WriteAllText(Path.Combine(install, "mine.txt"), "the player's");
            File.WriteAllLines(Path.Combine(work, ReleaseInstaller.ManifestName), new[] { "a.bin", "sdk/b.txt" });
            File.WriteAllLines(Path.Combine(work, ReleaseInstaller.PlanName),
                new[] { "backup-12345678", "=a.bin", "+new.bin", "=sdk/b.txt", "+late.bin" });

            AssertThat(ReleaseInstaller.Recover(install)).Contains("put back");

            AssertThat(File.ReadAllText(Path.Combine(install, "a.bin"))).IsEqual("a one");
            AssertThat(File.ReadAllText(Path.Combine(install, "sdk", "b.txt"))).IsEqual("b one");
            AssertThat(File.Exists(Path.Combine(install, "new.bin"))).IsFalse();
            AssertThat(File.Exists(Path.Combine(install, "late.bin"))).IsFalse();
            AssertThat(File.ReadAllText(Path.Combine(install, "mine.txt"))).IsEqual("the player's");
            AssertThat(Directory.GetFileSystemEntries(work).Length).IsEqual(1);
            // Nothing left to settle: a second start says nothing and changes nothing.
            AssertThat(ReleaseInstaller.Recover(install)).IsEqual(string.Empty);
            AssertThat(File.ReadAllText(Path.Combine(install, "a.bin"))).IsEqual("a one");
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    [TestCase]
    public void WhatWouldStopAnUpdateIsNamed()
    {
        AssertThat(ReleaseInstaller.SpaceError(500L << 20, 100L << 20)).Contains("Not enough free disk space");
        AssertThat(ReleaseInstaller.SpaceError(500L << 20, 600L << 20)).IsEqual(string.Empty);
        // A system that does not state its free space does not block the update.
        AssertThat(ReleaseInstaller.SpaceError(500L << 20, null)).IsEqual(string.Empty);

        // GitHub's refusal of an unsigned client past its hourly allowance is told apart from any other.
        AssertThat(GitHubApi.DescribeFailure(403, "0", "1795000000")).Contains("hourly request limit");
        AssertThat(GitHubApi.DescribeFailure(429, "0", "")).Contains("within the hour");
        AssertThat(GitHubApi.DescribeFailure(403, "12", "1795000000")).Contains("HTTP 403");
        AssertThat(GitHubApi.DescribeFailure(404, "", "")).Contains("404");
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
