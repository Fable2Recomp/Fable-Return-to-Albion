namespace Refii.Tests;

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.AndroidRuntime;

[TestSuite]
public class AndroidLogBundleTests
{
    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "refii-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [TestCase]
    public void TestArchiveIsNamedByTheLocalSecond()
    {
        AssertThat(AndroidLogBundle.ArchiveName(new DateTime(2026, 10, 29, 7, 5, 3)))
            .IsEqual("Fable2-logs-2026-10-29_07-05-03.zip");
    }

    [TestCase]
    public void TestTheFourNewestRotatedLogsAreChosen()
    {
        string[] names =
        {
            "godot.log", "godot2026-10-27T09.00.00.log", "godot2026-10-29T08.15.42.log",
            "godot2026-10-28T23.59.59.log", "refii_flight.jsonl", "godot2026-10-29T08.15.42.txt", "last-exit.txt",
            "godot2026-10-25T12.00.00.log", "godot2026-10-26T12.00.00.log",
        };
        AssertThat(AndroidLogBundle.RecentRotatedLogs(names).ToArray()).IsEqual(new[]
        {
            "godot2026-10-29T08.15.42.log", "godot2026-10-28T23.59.59.log", "godot2026-10-27T09.00.00.log",
            "godot2026-10-26T12.00.00.log",
        });
        // The live log is never a rotated one, and none is none.
        AssertThat(AndroidLogBundle.IsRotatedLog("godot.log")).IsFalse();
        AssertThat(AndroidLogBundle.RecentRotatedLogs(new[] { "godot.log" }).Count).IsEqual(0);
    }

    [TestCase]
    public void TestEntriesAreTheLogsFlightConfigAndExitThatExist()
    {
        string user = "/data/user/0/org.refii.test/files";
        string[] present =
        {
            "logs/godot.log", "logs/godot2026-10-28T10.00.00.log", "logs/godot2026-10-29T10.00.00.log",
            "logs/godot2026-10-26T10.00.00.log", "logs/refii_flight.jsonl", "runtime/refii.toml",
            "logs/last-exit.txt", "logs/last-exit-trace.bin", "runtime/logs/refii.log", "runtime/logs/refii_crash.log",
            "android-runtime.json", "phone-installer-state.json",
        };
        var existing = present.Select(path => Path.Combine(user, path)).ToHashSet();
        var entries = AndroidLogBundle.SelectEntries(user, existing.Contains,
            present.Where(path => path.StartsWith("logs/")).Select(Path.GetFileName)!);
        AssertThat(entries.Select(entry => entry.ZipPath).ToArray()).IsEqual(new[]
        {
            "files/logs/godot.log", "files/logs/godot2026-10-29T10.00.00.log", "files/logs/godot2026-10-28T10.00.00.log",
            "files/logs/godot2026-10-26T10.00.00.log", "files/logs/refii_flight.jsonl", "files/runtime/refii.toml", "files/logs/last-exit.txt",
            "files/logs/last-exit-trace.bin", "files/runtime/logs/refii.log", "files/runtime/logs/refii_crash.log",
            "files/android-runtime.json", "files/phone-installer-state.json",
        });
        AssertThat(entries[0].Source).IsEqual(Path.Combine(user, "logs", "godot.log"));
    }

    [TestCase]
    public void TestZipHoldsDeviceAndLogsWhileTheLogIsOpenForWriting()
    {
        string user = TempDirectory(), downloads = TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(user, "logs"));
            Directory.CreateDirectory(Path.Combine(user, "runtime"));
            File.WriteAllText(Path.Combine(user, "runtime", "refii.toml"), "max_fps = 30\n");
            File.WriteAllText(Path.Combine(user, "logs", "refii_flight.jsonl"), "{\"t\":\"1\"}\n");
            File.WriteAllText(Path.Combine(user, "logs", "godot2026-10-29T08.00.00.log"), "crashed run\n");
            // As Godot holds it: open for writing, shared for reading.
            using var live = new FileStream(Path.Combine(user, "logs", "godot.log"), FileMode.Create,
                FileAccess.Write, FileShare.ReadWrite);
            live.Write(Encoding.UTF8.GetBytes("this run\n"));
            live.Flush();

            var now = new DateTime(2026, 10, 29, 9, 0, 0);
            string first = AndroidLogBundle.Write(downloads, user, "Model: test\n", now);
            string second = AndroidLogBundle.Write(downloads, user, "Model: test\n", now);
            AssertThat(Path.GetFileName(first)).IsEqual("Fable2-logs-2026-10-29_09-00-00.zip");
            AssertThat(Path.GetFileName(second)).IsEqual("Fable2-logs-2026-10-29_09-00-00-2.zip");

            using var zip = ZipFile.OpenRead(first);
            string Read(string name)
            {
                using var reader = new StreamReader(zip.GetEntry(name)!.Open());
                return reader.ReadToEnd();
            }
            AssertThat(zip.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray())
                .IsEqual(new[]
                {
                    "device.txt", "files/logs/godot.log", "files/logs/godot2026-10-29T08.00.00.log",
                    "files/logs/refii_flight.jsonl", "files/runtime/refii.toml", "install.txt",
                });
            AssertThat(Read("device.txt")).IsEqual("Model: test\n");
            AssertThat(Read("files/logs/godot.log")).IsEqual("this run\n");
            AssertThat(Read("files/logs/godot2026-10-29T08.00.00.log")).IsEqual("crashed run\n");
        }
        finally
        {
            Directory.Delete(user, true);
            Directory.Delete(downloads, true);
        }
    }

    [TestCase]
    public void TestALongLogKeepsItsEnd()
    {
        string user = TempDirectory(), downloads = TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(user, "logs"));
            File.WriteAllText(Path.Combine(user, "logs", "godot.log"), "0123456789");
            string path = AndroidLogBundle.Write(downloads, user, "", new DateTime(2026, 1, 1), maxBytesPerFile: 4);
            using var zip = ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("files/logs/godot.log")!.Open());
            AssertThat(reader.ReadToEnd()).IsEqual("[Save logs: the first 6 of 10 bytes were left out]\n6789");
        }
        finally
        {
            Directory.Delete(user, true);
            Directory.Delete(downloads, true);
        }
    }

    [TestCase]
    public void TestInstallSaysWhetherTheModuleIsFromThisApksBundleAndWhichCompileDidNotFinish()
    {
        string user = TempDirectory();
        try
        {
            string build = Path.Combine(user, "phone-builds", "phone-build-old");
            string module = Path.Combine(build, "librefii_generated.so");
            Directory.CreateDirectory(build);
            File.WriteAllBytes(module, new byte[3 * 1024 * 1024]);
            File.WriteAllText(Path.Combine(build, "completed.json"), "{}");
            File.SetLastWriteTimeUtc(module, new DateTime(2026, 10, 28, 20, 0, 0, DateTimeKind.Utc));
            string Json(string path) => path.Replace("\\", "\\\\");
            File.WriteAllText(Path.Combine(user, "android-runtime.json"), "{\"schema\":1,\"module_path\":\"" + Json(module) + "\"}");
            void Compiled(string? bundle) => File.WriteAllText(Path.Combine(user, "phone-installer-state.json"),
                "{\"Game\":null,\"Compiled\":{\"Directory\":\"" + Json(build) + "\",\"Module\":\"" + Json(module)
                + "\",\"Sha256\":\"x\",\"Version\":\"Fable2TU1\",\"Bundle\":" + (bundle is null ? "null" : "\"" + bundle + "\"") + "}}");
            string done = Path.Combine(user, "phone-jobs", "job-done"), cut = Path.Combine(user, "phone-jobs", "job-cut");
            Directory.CreateDirectory(done);
            Directory.CreateDirectory(cut);
            File.WriteAllText(Path.Combine(done, "finished"), "finished");
            File.WriteAllText(Path.Combine(done, "progress"), "Removing temporary build files");
            File.WriteAllText(Path.Combine(cut, "progress"), "Compiling 37 of 379\n");
            Directory.SetLastWriteTimeUtc(done, new DateTime(2026, 10, 28, 20, 0, 0, DateTimeKind.Utc));
            Directory.SetLastWriteTimeUtc(cut, new DateTime(2026, 10, 29, 22, 23, 0, DateTimeKind.Utc));
            var installed = new DateTime(2026, 10, 29, 22, 20, 0, DateTimeKind.Utc);
            string apk = "ca9f11f9127352c3dd80693ac4252ea7";

            Compiled("20e28d58aaaabbbbccccdddd");
            string text = AndroidLogBundle.DescribeInstall(user, installed, apk);
            AssertThat(text).StartsWith("APK installed: 2026-10-29 22:20:00Z\nActive module: " + module + " (written 2026-10-28 20:00:00Z)\n"
                + "Compiled game code: NOT from this APK's bundle (20e28d58aaaa, this APK's ca9f11f91273): the launcher recompiles it");
            AssertThat(text).Contains("Compile jobs, newest first:\n  job-cut  2026-10-29 22:23:00Z  NOT FINISHED  last step: Compiling 37 of 379\n"
                + "  job-done  2026-10-28 20:00:00Z  finished  last step: Removing temporary build files\n");
            AssertThat(text).Contains("  phone-build-old  ");
            AssertThat(text).Contains("module 3 MB, written 2026-10-28 20:00:00Z, completed\n");

            // The same bundle is fine however old the module; one from before bundles were recorded is stale.
            Compiled(apk);
            AssertThat(AndroidLogBundle.DescribeInstall(user, installed, apk)).Contains("\nCompiled game code: from this APK's bundle (ca9f11f91273)\n");
            Compiled(null);
            AssertThat(AndroidLogBundle.DescribeInstall(user, installed, apk)).Contains("\nCompiled game code: NOT from this APK's bundle (unknown, this APK's ca9f11f91273)");
            AssertThat(AndroidLogBundle.DescribeInstall(TempDirectory(), null)).StartsWith("APK installed: unknown\nActive module: none recorded\nCompiled game code: none recorded\n");
        }
        finally { Directory.Delete(user, true); }
    }

    /// <summary>A save folder as the phone holds one: a hero, the achievements and the RexNet identity.</summary>
    internal static void WriteSave(string user, string marker)
    {
        string save = Path.Combine(user, "runtime", "assets", "save");
        string hero = Path.Combine(save, "E0C3DF3F20120108", "4D5307F1");
        Directory.CreateDirectory(Path.Combine(hero, "Headers", "00000001"));
        Directory.CreateDirectory(Path.Combine(hero, "00000001", "Hero000"));
        Directory.CreateDirectory(Path.Combine(save, "rexnet"));
        File.WriteAllText(Path.Combine(hero, "Headers", "00000001", "Hero000.header"), "header " + marker);
        File.WriteAllText(Path.Combine(hero, "00000001", "Hero000", "mainsave.bin"), "main " + marker);
        File.WriteAllText(Path.Combine(save, "achievements.refii"), "achievements " + marker);
        File.WriteAllText(Path.Combine(save, "rexnet", "identity.key"), "key " + marker);
    }

    [TestCase]
    public void TestEachExportIsNamedByWhatItHolds()
    {
        var now = new DateTime(2026, 10, 30, 7, 5, 3);
        AssertThat(AndroidLogBundle.ArchiveName(AndroidLogBundle.Contents.Logs, now)).IsEqual("Fable2-logs-2026-10-30_07-05-03.zip");
        AssertThat(AndroidLogBundle.ArchiveName(AndroidLogBundle.Contents.Saves, now)).IsEqual("Fable2-saves-2026-10-30_07-05-03.zip");
        AssertThat(AndroidLogBundle.ArchiveName(AndroidLogBundle.Contents.LogsAndSaves, now)).IsEqual("Fable2-logs-and-saves-2026-10-30_07-05-03.zip");
        AssertThat(AndroidLogBundle.ArchiveName(AndroidLogBundle.Contents.Saves, now, "saves-before-import"))
            .IsEqual("Fable2-saves-before-import-2026-10-30_07-05-03.zip");
    }

    [TestCase]
    public void TestSavesExportHoldsTheSaveButNotTheRexNetIdentityOrTheLogs()
    {
        string user = TempDirectory(), downloads = TempDirectory();
        try
        {
            WriteSave(user, "a");
            Directory.CreateDirectory(Path.Combine(user, "logs"));
            File.WriteAllText(Path.Combine(user, "logs", "godot.log"), "this run\n");
            string path = AndroidLogBundle.Write(downloads, user, "Model: test\n", new DateTime(2026, 10, 30, 9, 0, 0),
                contents: AndroidLogBundle.Contents.Saves);
            AssertThat(Path.GetFileName(path)).IsEqual("Fable2-saves-2026-10-30_09-00-00.zip");
            using var zip = ZipFile.OpenRead(path);
            AssertThat(zip.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray())
                .IsEqual(new[]
                {
                    "device.txt", "export.json",
                    "files/runtime/assets/save/E0C3DF3F20120108/4D5307F1/00000001/Hero000/mainsave.bin",
                    "files/runtime/assets/save/E0C3DF3F20120108/4D5307F1/Headers/00000001/Hero000.header",
                    "files/runtime/assets/save/achievements.refii",
                });
            using var manifest = new StreamReader(zip.GetEntry("export.json")!.Open());
            string text = manifest.ReadToEnd();
            AssertThat(text).Contains("\"contents\": \"saves\"");
            AssertThat(text).Contains("\"save_files\": 3");
        }
        finally
        {
            Directory.Delete(user, true);
            Directory.Delete(downloads, true);
        }
    }

    [TestCase]
    public void TestLogsAndSavesExportHoldsBoth()
    {
        string user = TempDirectory(), downloads = TempDirectory();
        try
        {
            WriteSave(user, "a");
            Directory.CreateDirectory(Path.Combine(user, "logs"));
            File.WriteAllText(Path.Combine(user, "logs", "godot.log"), "this run\n");
            string path = AndroidLogBundle.Write(downloads, user, "", new DateTime(2026, 10, 30),
                contents: AndroidLogBundle.Contents.LogsAndSaves);
            using var zip = ZipFile.OpenRead(path);
            string[] names = zip.Entries.Select(entry => entry.FullName).ToArray();
            AssertThat(names.Contains("files/logs/godot.log")).IsTrue();
            AssertThat(names.Contains("files/runtime/assets/save/achievements.refii")).IsTrue();
            AssertThat(names.Contains("export.json")).IsTrue();
            AssertThat(names.Any(name => name.Contains("rexnet"))).IsFalse();
            // A logs export carries no manifest, so it is never taken for a save.
            string logs = AndroidLogBundle.Write(downloads, user, "", new DateTime(2026, 10, 30));
            using var logZip = ZipFile.OpenRead(logs);
            AssertThat(logZip.Entries.Any(entry => entry.FullName == "export.json" || entry.FullName.StartsWith("files/runtime/assets/save/"))).IsFalse();
        }
        finally
        {
            Directory.Delete(user, true);
            Directory.Delete(downloads, true);
        }
    }

    [TestCase]
    public void TestAnUnreadableSaveFileFailsTheExportAndLeavesNothing()
    {
        if (OperatingSystem.IsWindows()) return;
        string user = TempDirectory(), downloads = TempDirectory();
        string main = "";
        try
        {
            WriteSave(user, "a");
            main = Path.Combine(user, "runtime", "assets", "save", "E0C3DF3F20120108", "4D5307F1", "00000001", "Hero000", "mainsave.bin");
            File.SetUnixFileMode(main, UnixFileMode.None);
            bool failed = false;
            try { AndroidLogBundle.Write(downloads, user, "", new DateTime(2026, 10, 30), contents: AndroidLogBundle.Contents.Saves); }
            catch (UnauthorizedAccessException) { failed = true; }
            catch (IOException) { failed = true; }
            AssertThat(failed).IsTrue();
            AssertThat(Directory.EnumerateFiles(downloads).Count()).IsEqual(0);
        }
        finally
        {
            if (main.Length > 0) File.SetUnixFileMode(main, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(user, true);
            Directory.Delete(downloads, true);
        }
    }
}
