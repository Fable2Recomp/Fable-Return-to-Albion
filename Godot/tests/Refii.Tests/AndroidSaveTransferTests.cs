namespace Refii.Tests;

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.AndroidRuntime;

/// <summary>Import save: what an export must hold, and what an import changes and keeps.</summary>
[TestSuite]
public class AndroidSaveTransferTests
{
    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "refii-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Save(string user) => Path.Combine(user, "runtime", "assets", "save");

    private static string MainSave(string user) =>
        Path.Combine(Save(user), "E0C3DF3F20120108", "4D5307F1", "00000001", "Hero000", "mainsave.bin");

    /// <summary>A zip holding <paramref name="entries"/> (name, text).</summary>
    private static string Zip(string folder, params (string Name, string Text)[] entries)
    {
        string path = Path.Combine(folder, "export-" + Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string name, string text) in entries)
        {
            using Stream stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(text));
        }
        return path;
    }

    /// <summary>The export another phone made of its save marked <paramref name="marker"/>.</summary>
    private static string Export(string folder, string marker)
    {
        string other = TempDirectory();
        try
        {
            AndroidLogBundleTests.WriteSave(other, marker);
            return AndroidLogBundle.Write(folder, other, "", new DateTime(2026, 10, 30), contents: AndroidLogBundle.Contents.Saves);
        }
        finally { Directory.Delete(other, true); }
    }

    private static string[] Leftovers(string user) =>
        Directory.EnumerateDirectories(Path.Combine(user, "runtime", "assets"))
            .Select(Path.GetFileName).OfType<string>().Where(name => name != "save").ToArray();

    [TestCase]
    public void TestOnlySafeSaveEntriesAreTaken()
    {
        AssertThat(AndroidSaveTransfer.SaveRelativePath("device.txt")).IsNull();
        AssertThat(AndroidSaveTransfer.SaveRelativePath("files/logs/godot.log")).IsNull();
        AssertThat(AndroidSaveTransfer.SaveRelativePath("files/runtime/assets/save/")).IsNull();
        AssertThat(AndroidSaveTransfer.SaveRelativePath("files/runtime/assets/save/rexnet/identity.key")).IsNull();
        AssertThat(AndroidSaveTransfer.SaveRelativePath("files/runtime/assets/save/achievements.refii")).IsEqual("achievements.refii");
        foreach (string unsafeName in new[]
        {
            "files/runtime/assets/save/../../../evil", "files/runtime/assets/save/a//b",
            "files/runtime/assets/save//etc/passwd", "files/runtime/assets/save/a\\b", "files/runtime/assets/save/./a",
            "files/runtime/assets/save/C:x",
        })
        {
            bool refused = false;
            try { AndroidSaveTransfer.SaveRelativePath(unsafeName); }
            catch (InvalidDataException) { refused = true; }
            AssertThat(refused).OverrideFailureMessage(unsafeName + " was taken").IsTrue();
        }
    }

    [TestCase]
    public void TestAnExportWithoutAHeroIsRefusedWithAReason()
    {
        string folder = TempDirectory();
        try
        {
            string logs = Zip(folder, ("device.txt", "x"), ("files/logs/godot.log", "log"));
            string noHero = Zip(folder, ("files/runtime/assets/save/achievements.refii", "a"));
            string twice = Zip(folder,
                ("files/runtime/assets/save/E0C3DF3F20120108/4D5307F1/Headers/00000001/Hero000.header", "h"),
                ("files/runtime/assets/save/achievements.refii", "a"), ("files/runtime/assets/save/Achievements.refii", "b"));
            string Reason(string path)
            {
                using ZipArchive zip = ZipFile.OpenRead(path);
                try { AndroidSaveTransfer.Inspect(zip); return ""; }
                catch (InvalidDataException error) { return error.Message; }
            }
            AssertThat(Reason(logs)).Contains("no save in it");
            AssertThat(Reason(noHero)).Contains("no hero");
            AssertThat(Reason(twice)).Contains("twice");

            using ZipArchive valid = ZipFile.OpenRead(Export(folder, "b"));
            AndroidSaveTransfer.Inspection inspection = AndroidSaveTransfer.Inspect(valid);
            AssertThat(inspection.Files).IsEqual(3);
            AssertThat(inspection.Heroes).IsEqual(1);
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestCase]
    public void TestImportReplacesTheSaveKeepsTheIdentityAndBacksUpFirst()
    {
        string user = TempDirectory(), folder = TempDirectory();
        try
        {
            AndroidLogBundleTests.WriteSave(user, "mine");
            File.WriteAllText(Path.Combine(Save(user), "only-mine.bin"), "x");
            string export = Export(folder, "theirs");
            int backups = 0;
            string MakeBackup()
            {
                backups++;
                // The backup sees the save as it was.
                AssertThat(File.ReadAllText(MainSave(user))).IsEqual("main mine");
                return AndroidLogBundle.Write(folder, user, "", new DateTime(2026, 10, 30, 12, 0, 0),
                    contents: AndroidLogBundle.Contents.Saves, label: AndroidSaveTransfer.BackupLabel);
            }

            AndroidSaveTransfer.Result result = AndroidSaveTransfer.Import(export, user, MakeBackup);

            AssertThat(backups).IsEqual(1);
            AssertThat(Path.GetFileName(result.Backup)).IsEqual("Fable2-saves-before-import-2026-10-30_12-00-00.zip");
            AssertThat(result.Imported.Heroes).IsEqual(1);
            AssertThat(File.ReadAllText(MainSave(user))).IsEqual("main theirs");
            AssertThat(File.ReadAllText(Path.Combine(Save(user), "achievements.refii"))).IsEqual("achievements theirs");
            AssertThat(File.Exists(Path.Combine(Save(user), "only-mine.bin"))).IsFalse();
            // The phone keeps its own RexNet identity.
            AssertThat(File.ReadAllText(Path.Combine(Save(user), "rexnet", "identity.key"))).IsEqual("key mine");
            AssertThat(Leftovers(user).Length).IsEqual(0);

            // The backup is itself an export that imports the old save back.
            AndroidSaveTransfer.Import(result.Backup!, user, () => Path.Combine(folder, "unused.zip"));
            AssertThat(File.ReadAllText(MainSave(user))).IsEqual("main mine");
            AssertThat(File.Exists(Path.Combine(Save(user), "only-mine.bin"))).IsTrue();
            AssertThat(File.ReadAllText(Path.Combine(Save(user), "rexnet", "identity.key"))).IsEqual("key mine");
        }
        finally
        {
            Directory.Delete(user, true);
            Directory.Delete(folder, true);
        }
    }

    [TestCase]
    public void TestAPhoneWithoutASaveTakesTheImportWithoutABackup()
    {
        string user = TempDirectory(), folder = TempDirectory();
        try
        {
            string export = Export(folder, "theirs");
            AndroidSaveTransfer.Result result = AndroidSaveTransfer.Import(export, user, () => throw new InvalidOperationException("no backup expected"));
            AssertThat(result.Backup).IsNull();
            AssertThat(File.ReadAllText(MainSave(user))).IsEqual("main theirs");
            AssertThat(Directory.Exists(Path.Combine(Save(user), "rexnet"))).IsFalse();
        }
        finally
        {
            Directory.Delete(user, true);
            Directory.Delete(folder, true);
        }
    }

    [TestCase]
    public void TestAFailedImportLeavesTheSaveAsItWas()
    {
        string user = TempDirectory(), folder = TempDirectory();
        try
        {
            AndroidLogBundleTests.WriteSave(user, "mine");
            string traversal = Zip(folder,
                ("files/runtime/assets/save/E0C3DF3F20120108/4D5307F1/Headers/00000001/Hero000.header", "h"),
                ("files/runtime/assets/save/../../escape.txt", "x"));
            string export = Export(folder, "theirs");
            foreach ((string zip, Func<string> backup) in new (string, Func<string>)[]
            {
                (traversal, () => "unused"),
                (export, () => throw new IOException("Download is full")),
            })
            {
                bool failed = false;
                try { AndroidSaveTransfer.Import(zip, user, backup); }
                catch (Exception error) when (error is InvalidDataException or IOException) { failed = true; }
                AssertThat(failed).IsTrue();
                AssertThat(File.ReadAllText(MainSave(user))).IsEqual("main mine");
                AssertThat(Leftovers(user).Length).IsEqual(0);
            }
            AssertThat(File.Exists(Path.Combine(user, "runtime", "escape.txt"))).IsFalse();
        }
        finally
        {
            Directory.Delete(user, true);
            Directory.Delete(folder, true);
        }
    }

    [TestCase]
    public void TestNoImportOnceTheGameHasStarted()
    {
        string user = TempDirectory(), folder = TempDirectory();
        try
        {
            AndroidLogBundleTests.WriteSave(user, "mine");
            string export = Export(folder, "theirs");
            AndroidSaveTransfer.GameStarted = true;
            bool refused = false;
            try { AndroidSaveTransfer.Import(export, user, () => "unused"); }
            catch (InvalidOperationException) { refused = true; }
            AssertThat(refused).IsTrue();
            AssertThat(File.ReadAllText(MainSave(user))).IsEqual("main mine");
        }
        finally
        {
            AndroidSaveTransfer.GameStarted = false;
            Directory.Delete(user, true);
            Directory.Delete(folder, true);
        }
    }
}
