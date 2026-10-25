namespace Refii.Tests.Modding;

using System;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;
using Refii.Save;

/// <summary>
/// A mod's save snapshots (RefiiHost save_snapshot / load_snapshot / snapshot_time, and the
/// save_written signal's fields): taken beside the save, staged into the folder for the guest's
/// fail-quest reload with the main save as the fail-quest save, and put back after — a file the
/// game wrote meanwhile kept, a crash's partial hold never deleting. On a temporary save root.
/// </summary>
[TestSuite]
public class SaveSnapshotsTests
{
	private const string Key = "quicksave.quick";

	private static string NewSave(out string root)
	{
		root = Path.Combine(Path.GetTempPath(), "refii-snapshots-" + Guid.NewGuid().ToString("N"));
		string save = Path.Combine(root, "E06FD4BA20120108", "4D5307F1", "00000001", "Hero000");
		Directory.CreateDirectory(save);
		Write(save, "mainsave.bin", "main-1");
		Write(save, "failquestsave.bin", "quest-fail");
		Write(save, "herosave.bin", "hero-1");
		Write(save, "refii_player.txt", "player-1");
		return save;
	}

	private static void Write(string directory, string name, string text) =>
		File.WriteAllText(Path.Combine(directory, name), text);

	private static string Read(string directory, string name) => File.ReadAllText(Path.Combine(directory, name));

	[TestCase]
	public void KeysAreAModsOwnName()
	{
		AssertThat(SaveSnapshots.ValidKey("quicksave.quick")).IsTrue();
		AssertThat(SaveSnapshots.ValidKey("my_mod.slot-2")).IsTrue();
		AssertThat(SaveSnapshots.ValidKey("quick")).IsFalse();
		AssertThat(SaveSnapshots.ValidKey("../x.y")).IsFalse();
		AssertThat(SaveSnapshots.ValidKey("a..b")).IsFalse();
		AssertThat(SaveSnapshots.ValidKey("a/b.c")).IsFalse();
		AssertThat(SaveSnapshots.ValidKey("mod.")).IsFalse();
		AssertThat(SaveSnapshots.ValidKey(string.Empty)).IsFalse();
		AssertThat(SaveSnapshots.ValidKey(null)).IsFalse();
	}

	[TestCase]
	public void ASnapshotIsKeptBesideTheTitleNotInTheLoadList()
	{
		string save = NewSave(out string root);
		try
		{
			string? directory = SaveSnapshots.SnapshotDirectory(save, Key);
			AssertThat(directory).IsEqual(Path.Combine(root, "E06FD4BA20120108", "refii-snapshots", "Hero000", Key));
			AssertThat(SaveSnapshots.SnapshotDirectory(save, "bad")).IsNull();
			AssertThat(SaveSnapshots.SnapshotDirectory(Path.Combine(root, "elsewhere", "Hero000"), Key)).IsNull();

			var when = new DateTime(2026, 10, 24, 10, 30, 0, DateTimeKind.Utc);
			AssertThat(SaveSnapshots.Taken(save, Key)).IsNull();
			AssertThat(SaveSnapshots.Take(save, Key, when, out string error)).IsTrue();
			AssertThat(error).IsEqual(string.Empty);
			AssertThat(SaveSnapshots.Taken(save, Key)).IsEqual(when);
			AssertThat(Read(directory!, "mainsave.bin")).IsEqual("main-1");
			AssertThat(Read(directory!, "refii_player.txt")).IsEqual("player-1");

			// Taken again: the copy is replaced whole, nothing of the last one left.
			Write(save, "mainsave.bin", "main-2");
			File.Delete(Path.Combine(save, "refii_player.txt"));
			AssertThat(SaveSnapshots.Take(save, Key, when.AddMinutes(5), out _)).IsTrue();
			AssertThat(Read(directory!, "mainsave.bin")).IsEqual("main-2");
			AssertThat(File.Exists(Path.Combine(directory!, "refii_player.txt"))).IsFalse();
			AssertThat(SaveSnapshots.Taken(save, Key)).IsEqual(when.AddMinutes(5));
			AssertThat(Directory.Exists(directory + ".partial")).IsFalse();
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[TestCase]
	public void StagingPutsTheSnapshotInWithItsMainSaveAsTheFailQuestSave()
	{
		string save = NewSave(out string root);
		try
		{
			AssertThat(SaveSnapshots.Take(save, Key, DateTime.UtcNow, out _)).IsTrue();

			// Played on and saved since: the folder moved on, and a blob appeared.
			Write(save, "mainsave.bin", "main-later");
			Write(save, "herosave.bin", "hero-later");
			Write(save, "refii_player.txt", "player-later");
			Write(save, "refii_blob_new.bin", "blob");

			AssertThat(SaveSnapshots.Stage(save, Key, out string error)).IsTrue();
			AssertThat(error).IsEqual(string.Empty);
			AssertThat(SaveSnapshots.IsStaged(save)).IsTrue();
			AssertThat(Read(save, "mainsave.bin")).IsEqual("main-1");
			AssertThat(Read(save, "failquestsave.bin")).IsEqual("main-1");
			AssertThat(Read(save, "herosave.bin")).IsEqual("hero-1");
			AssertThat(Read(save, "refii_player.txt")).IsEqual("player-1");
			AssertThat(File.Exists(Path.Combine(save, "refii_blob_new.bin"))).IsFalse();
			AssertThat(File.Exists(Path.Combine(save, SaveSnapshots.TakenName))).IsFalse();

			// The level stands: the folder is the player's own again, the quest's fail save too.
			AssertThat(SaveSnapshots.Restore(save) > 0).IsTrue();
			AssertThat(SaveSnapshots.IsStaged(save)).IsFalse();
			AssertThat(Read(save, "mainsave.bin")).IsEqual("main-later");
			AssertThat(Read(save, "failquestsave.bin")).IsEqual("quest-fail");
			AssertThat(Read(save, "herosave.bin")).IsEqual("hero-later");
			AssertThat(Read(save, "refii_player.txt")).IsEqual("player-later");
			AssertThat(Read(save, "refii_blob_new.bin")).IsEqual("blob");
			AssertThat(SaveSnapshots.Restore(save)).IsEqual(0);
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[TestCase]
	public void AFileTheGameWroteDuringTheLoadIsKept()
	{
		string save = NewSave(out string root);
		try
		{
			AssertThat(SaveSnapshots.Take(save, Key, DateTime.UtcNow, out _)).IsTrue();
			Write(save, "mainsave.bin", "main-later");
			AssertThat(SaveSnapshots.Stage(save, Key, out _)).IsTrue();

			// The game saved after the load (a new write time): that save is the player's newest.
			string main = Path.Combine(save, "mainsave.bin");
			File.WriteAllText(main, "main-after-load");
			File.SetLastWriteTimeUtc(main, DateTime.UtcNow.AddMinutes(1));

			SaveSnapshots.Restore(save);
			AssertThat(Read(save, "mainsave.bin")).IsEqual("main-after-load");
			AssertThat(Read(save, "failquestsave.bin")).IsEqual("quest-fail");
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[TestCase]
	public void AHoldWithoutItsManifestIsPutBackWithoutDeletingAnything()
	{
		string save = NewSave(out string root);
		try
		{
			// A crash while the hold was being copied: only part of it there, no manifest.
			string held = Path.Combine(SaveSnapshots.SaveFolder(save)!, SaveSnapshots.HeldName);
			Directory.CreateDirectory(held);
			Write(held, "mainsave.bin", "main-1");

			AssertThat(SaveSnapshots.IsStaged(save)).IsTrue();
			SaveSnapshots.Restore(save);
			AssertThat(SaveSnapshots.IsStaged(save)).IsFalse();
			AssertThat(Directory.GetFiles(save).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray())
				.IsEqual(new[] { "failquestsave.bin", "herosave.bin", "mainsave.bin", "refii_player.txt" });
			AssertThat(Read(save, "herosave.bin")).IsEqual("hero-1");
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[TestCase]
	public void NoSnapshotStagesNothing()
	{
		string save = NewSave(out string root);
		try
		{
			AssertThat(SaveSnapshots.Stage(save, Key, out string error)).IsFalse();
			AssertThat(error.Length > 0).IsTrue();
			AssertThat(SaveSnapshots.IsStaged(save)).IsFalse();
			AssertThat(Read(save, "failquestsave.bin")).IsEqual("quest-fail");
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[TestCase]
	public void SaveWrittenCarriesTheSaveAndThePlayer()
	{
		AssertThat(RefiiHost.ParseSaveWritten("player=1,name='Hero000'")).IsEqual(("Hero000", 1));
		AssertThat(RefiiHost.ParseSaveWritten("player=2,name='Hero001'")).IsEqual(("Hero001", 2));
		AssertThat(RefiiHost.ParseSaveWritten(string.Empty)).IsEqual((string.Empty, 0));
	}

	[TestCase]
	public void TheCatalogAndTheGuideNameTheCalls()
	{
		string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
		foreach (string call in new[] { "save_snapshot(key)", "snapshot_time(key)", "load_snapshot(key)", "signal save_written(e)" })
		{
			AssertThat(godot.Contains(call, StringComparison.Ordinal)).IsTrue();
		}

		string guide = File.ReadAllText(Path.Combine(GuideDirectory(), "reference-calls.md"));
		foreach (string call in new[] { "`save_snapshot(key) -> bool`", "`load_snapshot(key) -> bool`", "`snapshot_time(key) -> int`", "signal `save_written(e)`" })
		{
			AssertThat(guide.Contains(call, StringComparison.Ordinal)).IsTrue();
		}
	}

	private static string GuideDirectory()
	{
		for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			string candidate = Path.Combine(dir.FullName, "docs", "modding");
			if (Directory.Exists(candidate))
			{
				return candidate;
			}
		}

		throw new DirectoryNotFoundException("docs/modding above " + AppContext.BaseDirectory);
	}
}
