using System;
using System.IO;
using System.Text.Json;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Verifies explicit Android paths and non-destructive save-import feature lifecycles.</summary>
[TestSuite]
public sealed class AndroidRuntimeOptionsTests
{
    /// <summary>Minimal input resolves only private defaults and leaves the supplied game untouched.</summary>
    [TestCase]
    public void MinimalConfigurationUsesSeparatePrivateRuntimePaths()
    {
        string user = Path.GetFullPath(Path.Combine(Refii.Tests.TestTemp.Root, "refii-user"));
        string game = Path.GetFullPath(Path.Combine(Refii.Tests.TestTemp.Root, "refii-game"));
        string json = JsonSerializer.Serialize(new { schema = 1, game_root = game });
        var options = AndroidRuntimeOptions.Parse(json, user);
        AssertThat(options.GameRoot).IsEqual(game);
        AssertThat(options.ModulePath).IsEqual(Path.Combine(user, "module", "librefii_generated.so"));
        AssertThat(options.SaveImportRoot).IsNull();
        AssertThat(options.SkipMovies).IsTrue();
        AssertThat(options.MovieDwellSeconds).IsEqual(2.0);
        AssertThat(Engine_AppRoot.AndroidPrivateRoot(user)).IsEqual(user);
    }

    /// <summary>Explicit physical module/save paths and a disabled movie policy are preserved.</summary>
    [TestCase]
    public void ExplicitConfigurationDoesNotChooseAnImplicitImportOrMovieAction()
    {
        string user = Path.GetFullPath(Path.Combine(Refii.Tests.TestTemp.Root, "refii-user"));
        string game = Path.Combine(user, "game");
        string module = Path.Combine(user, "supplied", "module.so");
        string save = Path.Combine(user, "supplied-save");
        var options = AndroidRuntimeOptions.Parse(JsonSerializer.Serialize(new
        {
            schema = 1, game_root = game, module_path = module, save_import_root = save,
            skip_movies = false, movie_dwell_seconds = 60
        }), user);
        AssertThat(options.ModulePath).IsEqual(module);
        AssertThat(options.SaveImportRoot).IsEqual(save);
        AssertThat(options.SkipMovies).IsFalse();
        AssertThat(options.MovieDwellSeconds).IsEqual(60.0);
    }

    /// <summary>Mistyped/duplicate fields, URIs and relative paths fail before native loading or copying.</summary>
    [TestCase]
    public void StrictConfigurationRejectsAmbiguousInputs()
    {
        string user = Refii.Tests.TestTemp.Root;
        string game = JsonSerializer.Serialize(Path.GetFullPath(Path.Combine(user, "game")));
        foreach (string json in new[]
        {
            "[]", "{}", "{\"schema\":\"1\",\"game_root\":" + game + "}",
            "{\"schema\":true,\"game_root\":" + game + "}",
            "{\"schema\":2,\"game_root\":" + game + "}",
            "{\"schema\":1,\"schema\":1,\"game_root\":" + game + "}",
            "{\"schema\":1,\"game_root\":\"content://tree/game\"}",
            "{\"schema\":1,\"game_root\":\"relative/game\"}",
            "{\"schema\":1,\"game_root\":" + game + ",\"module_path\":\"res://game.so\"}",
            "{\"schema\":1,\"game_root\":" + game + ",\"unknown\":0}",
            "{\"schema\":1,\"game_root\":" + game + ",\"skip_movies\":1}",
            "{\"schema\":1,\"game_root\":" + game + ",\"movie_dwell_seconds\":\"2\"}",
            "{\"schema\":1,\"game_root\":" + game + ",\"movie_dwell_seconds\":false}",
            "{\"schema\":1,\"game_root\":" + game + ",\"movie_dwell_seconds\":0.49}",
            "{\"schema\":1,\"game_root\":" + game + ",\"movie_dwell_seconds\":61}"
        }) AssertThat(IsInvalid(() => AndroidRuntimeOptions.Parse(json, user))).IsTrue();
    }

    /// <summary>Migrates a synthetic content root into the profile once, never changing the source or a newer private save.</summary>
    [TestCase]
    public void SaveImportLifecycleNeverOverwritesAnExistingProfile()
    {
        using var tree = new TemporaryTree();
        string source = Path.Combine(tree.Root, "source");
        string hero = Path.Combine(source, "E0C3DF3F20120108", "4D5307F1", "00000001", "Hero000");
        Directory.CreateDirectory(hero);
        byte[] expected = { 0, 128, 255, 1, 2 };
        File.WriteAllBytes(Path.Combine(hero, "mainsave.bin"), expected);
        string target = Path.Combine(tree.Root, "runtime", "assets", "saves");
        string profile = Refii.Save.Engine_SaveProfiles.Ensure(target);
        string placed = Path.Combine(profile, "Fable 2", "00000001", "Hero000", "mainsave.bin");

        var imported = AndroidSaveImporter.ImportIfEmpty(source, target);
        AssertThat(imported.Items).IsEqual(1);
        AssertThat(File.ReadAllBytes(placed)).IsEqual(expected);
        AssertThat(File.ReadAllBytes(Path.Combine(hero, "mainsave.bin"))).IsEqual(expected);

        File.WriteAllText(placed, "new private progress");
        var preserved = AndroidSaveImporter.ImportIfEmpty(source, target);
        AssertThat(preserved.Status).IsEqual("supplied save tree already migrated");
        AssertThat(File.ReadAllText(placed)).IsEqual("new private progress");
        AssertThat(File.ReadAllBytes(Path.Combine(hero, "mainsave.bin"))).IsEqual(expected);
    }

    /// <summary>Optional imports are harmless and overlapping source/destination trees are refused.</summary>
    [TestCase]
    public void SaveImportRejectsRecursiveAndMissingSources()
    {
        using var tree = new TemporaryTree();
        string target = Path.Combine(tree.Root, "private-save");
        AssertThat(AndroidSaveImporter.ImportIfEmpty(null, target).Items).IsEqual(0);
        Directory.CreateDirectory(target);
        AssertThat(IsInvalid(() => AndroidSaveImporter.ImportIfEmpty(target, target))).IsTrue();
        AssertThat(IsInvalid(() => AndroidSaveImporter.ImportIfEmpty(tree.Root, target))).IsTrue();
        bool missing = false;
        try { AndroidSaveImporter.ImportIfEmpty(Path.Combine(tree.Root, "missing"), target); }
        catch (DirectoryNotFoundException) { missing = true; }
        AssertThat(missing).IsTrue();
        using var remaining = Directory.EnumerateFileSystemEntries(target).GetEnumerator();
        AssertThat(remaining.MoveNext()).IsFalse();
    }

    /// <summary>Checks the documented configuration failure type without constructing native Godot objects.</summary>
    private static bool IsInvalid(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
    }

    /// <summary>Owns an isolated synthetic test directory; never names a real game or save.</summary>
    private sealed class TemporaryTree : IDisposable
    {
        /// <summary>The unique absolute directory owned entirely by this test.</summary>
        internal string Root { get; } = Path.Combine(Refii.Tests.TestTemp.Root, "refii-android-test-" + Guid.NewGuid().ToString("N"));
        /// <summary>Creates the synthetic source/destination parent.</summary>
        internal TemporaryTree() => Directory.CreateDirectory(Root);
        /// <summary>Removes only this test's uniquely allocated synthetic tree.</summary>
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
