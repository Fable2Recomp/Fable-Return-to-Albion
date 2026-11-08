using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Refii.Save;

/// <summary>
/// A mod's copies of a hero's save, taken and loaded back mid-game: what a quicksave and a
/// quickload are made of (RefiiHost <c>save_snapshot</c> / <c>load_snapshot</c>).
/// </summary>
/// <remarks>
/// WHERE THEY ARE: `&lt;profile&gt;/&lt;game&gt;/refii-snapshots/&lt;save&gt;/&lt;key&gt;/` — in the title's own
/// folder beside its `00000001/`, so the load list (which enumerates `00000001/`) never shows one,
/// and per save, so a key loads back only into the hero it was taken from. A key is a
/// mod's own name: `"&lt;mod&gt;.&lt;name&gt;"`. A snapshot is every file of the save folder as it
/// stood, plus <see cref="TakenName"/>.
///
/// HOW ONE LOADS IN GAME: the guest's own fail-quest reload. `LoadFailQuestSave()` (the native
/// gameflow's region-lock box calls) loads `failquestsave.bin` in the running game — the same
/// load-from-save command a Continue runs, with the loading screen and no trip to the title — and
/// the file is a whole main save: `SaveFailQuestSave()` writes it byte for byte equal to
/// `mainsave.bin`. So <see cref="Stage"/> puts the snapshot's files into the save folder, its main
/// save also as the fail-quest save, and <see cref="Restore"/> puts the folder back once the level
/// stands: the quest's own fail-quest save and the player's main save are untouched by a load.
///
/// THE HOLD: the folder's own files wait under `refii-snapshots/&lt;save&gt;/.held/` with a
/// manifest of what was staged. A file the game wrote again after staging (a save made since) is
/// the game's and stays; a staged file still as staged goes back to the held one. A hold left by
/// a crash mid-load is put back before that save next starts (<see cref="Restore"/> on start_game).
/// </remarks>
public static class SaveSnapshots
{
	public const string FolderName = "refii-snapshots";
	public const string HeldName = ".held";

	/// <summary>A snapshot's own file: when it was taken, unix seconds. Never staged.</summary>
	public const string TakenName = "refii_snapshot.txt";

	/// <summary>The hold's record of the staged files: `name, length, write ticks` per line.</summary>
	public const string ManifestName = "staged.tsv";

	public const string MainSave = "mainsave.bin";
	public const string FailQuestSave = "failquestsave.bin";

	private static readonly Regex KeyPattern = new(@"^[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-.]+$", RegexOptions.CultureInvariant);

	/// <summary>A key a snapshot can be kept under: `&lt;mod&gt;.&lt;name&gt;`, letters, digits, `_ - .`, no `..`.</summary>
	public static bool ValidKey(string? key) =>
		key is { Length: > 0 and <= 128 } && KeyPattern.IsMatch(key) && !key.Contains("..", StringComparison.Ordinal) &&
		!key.EndsWith('.');

	/// <summary>`&lt;game&gt;/refii-snapshots/&lt;save&gt;/` for a save folder `&lt;game&gt;/00000001/&lt;save&gt;`; null otherwise.</summary>
	public static string? SaveFolder(string saveDirectory)
	{
		string save = Path.GetFileName(Path.TrimEndingDirectorySeparator(saveDirectory));
		DirectoryInfo? content = Directory.GetParent(Path.TrimEndingDirectorySeparator(saveDirectory));
		DirectoryInfo? game = content?.Parent;
		if (save.Length == 0 || game is null ||
			!string.Equals(content!.Name, Engine_SaveLayout.SaveGameType, StringComparison.Ordinal))
		{
			return null;
		}

		return Path.Combine(game.FullName, FolderName, save);
	}

	/// <summary>Where the snapshot under <paramref name="key"/> of this save is kept; null for a bad key or layout.</summary>
	public static string? SnapshotDirectory(string saveDirectory, string key) =>
		ValidKey(key) && SaveFolder(saveDirectory) is { } folder ? Path.Combine(folder, key) : null;

	/// <summary>When the snapshot was taken (UTC), or null when there is none.</summary>
	public static DateTime? Taken(string saveDirectory, string key)
	{
		if (SnapshotDirectory(saveDirectory, key) is not { } directory ||
			!File.Exists(Path.Combine(directory, MainSave)) ||
			!File.Exists(Path.Combine(directory, TakenName)))
		{
			return null;
		}

		return long.TryParse(File.ReadAllText(Path.Combine(directory, TakenName)).Trim(), NumberStyles.Integer,
				CultureInfo.InvariantCulture, out long seconds)
			? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
			: null;
	}

	/// <summary>
	/// Copies the save folder's files into the snapshot under <paramref name="key"/>, replacing the
	/// last one only once the new copy is whole.
	/// </summary>
	public static bool Take(string saveDirectory, string key, DateTime now, out string error)
	{
		error = string.Empty;
		if (SnapshotDirectory(saveDirectory, key) is not { } directory)
		{
			error = $"'{key}' is not a snapshot key, or '{saveDirectory}' is not a save folder";
			return false;
		}

		if (!File.Exists(Path.Combine(saveDirectory, MainSave)))
		{
			error = $"no {MainSave} in '{saveDirectory}'";
			return false;
		}

		string partial = directory + ".partial";
		try
		{
			if (Directory.Exists(partial))
			{
				Directory.Delete(partial, true);
			}

			Directory.CreateDirectory(partial);
			foreach (string file in Directory.EnumerateFiles(saveDirectory))
			{
				File.Copy(file, Path.Combine(partial, Path.GetFileName(file)));
			}

			long seconds = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)).ToUnixTimeSeconds();
			File.WriteAllText(Path.Combine(partial, TakenName), seconds.ToString(CultureInfo.InvariantCulture));
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, true);
			}

			Directory.Move(partial, directory);
			return true;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			error = e.Message;
			return false;
		}
	}

	/// <summary>Whether a staged load is held for this save folder (its own files wait in the hold).</summary>
	public static bool IsStaged(string saveDirectory) =>
		SaveFolder(saveDirectory) is { } folder && Directory.Exists(Path.Combine(folder, HeldName));

	/// <summary>
	/// Puts the snapshot under <paramref name="key"/> into the save folder for the guest's
	/// fail-quest reload: the folder's own files into the hold, the snapshot's in, and its main save
	/// as the fail-quest save too. A hold already there is put back first.
	/// </summary>
	public static bool Stage(string saveDirectory, string key, out string error)
	{
		error = string.Empty;
		if (SnapshotDirectory(saveDirectory, key) is not { } snapshot || SaveFolder(saveDirectory) is not { } folder)
		{
			error = $"'{key}' is not a snapshot key, or '{saveDirectory}' is not a save folder";
			return false;
		}

		if (!File.Exists(Path.Combine(snapshot, MainSave)))
		{
			error = $"no snapshot '{key}' for '{Path.GetFileName(saveDirectory)}'";
			return false;
		}

		Restore(saveDirectory);
		string held = Path.Combine(folder, HeldName);
		try
		{
			// The hold first, whole, before anything in the folder changes: a crash from here on
			// leaves the folder's own files where Restore finds them.
			Directory.CreateDirectory(held);
			foreach (string file in Directory.EnumerateFiles(saveDirectory))
			{
				File.Copy(file, Path.Combine(held, Path.GetFileName(file)), true);
			}

			var wanted = new HashSet<string>(StringComparer.Ordinal);
			foreach (string file in Directory.EnumerateFiles(snapshot))
			{
				string name = Path.GetFileName(file);
				if (name == TakenName)
				{
					continue;
				}

				wanted.Add(name);
				File.Copy(file, Path.Combine(saveDirectory, name), true);
			}

			wanted.Add(FailQuestSave);
			File.Copy(Path.Combine(snapshot, MainSave), Path.Combine(saveDirectory, FailQuestSave), true);

			// A file the snapshot did not have (a player-store blob written since) is the hold's.
			foreach (string file in Directory.EnumerateFiles(saveDirectory))
			{
				if (!wanted.Contains(Path.GetFileName(file)))
				{
					File.Delete(file);
				}
			}

			var manifest = new List<string>();
			foreach (string file in Directory.EnumerateFiles(saveDirectory))
			{
				var info = new FileInfo(file);
				manifest.Add(string.Join('\t', info.Name, info.Length.ToString(CultureInfo.InvariantCulture),
					info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)));
			}

			File.WriteAllLines(Path.Combine(held, ManifestName), manifest);
			return true;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			error = e.Message;
			Restore(saveDirectory);
			return false;
		}
	}

	/// <summary>
	/// Puts a save folder back from its hold: a staged file the game has not written since goes
	/// back to the held one (or away, when the folder had none), a file the game wrote since stays,
	/// a held file missing from the folder comes back. Returns how many files changed; 0 with no
	/// hold.
	/// </summary>
	public static int Restore(string saveDirectory)
	{
		if (SaveFolder(saveDirectory) is not { } folder)
		{
			return 0;
		}

		string held = Path.Combine(folder, HeldName);
		if (!Directory.Exists(held))
		{
			return 0;
		}

		// No manifest: staging stopped before the guest could load, so nothing in the folder is the game's.
		Dictionary<string, (long Length, long Ticks)>? staged = null;
		string manifestPath = Path.Combine(held, ManifestName);
		if (File.Exists(manifestPath))
		{
			staged = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
			foreach (string line in File.ReadAllLines(manifestPath))
			{
				string[] parts = line.Split('\t');
				if (parts.Length == 3 &&
					long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long length) &&
					long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
				{
					staged[parts[0]] = (length, ticks);
				}
			}
		}

		int restored = 0;
		foreach (string file in Directory.EnumerateFiles(saveDirectory))
		{
			string name = Path.GetFileName(file);
			var info = new FileInfo(file);
			bool asStaged = staged is null ||
				(staged.TryGetValue(name, out var s) && s.Length == info.Length && s.Ticks == info.LastWriteTimeUtc.Ticks);
			if (!asStaged)
			{
				continue;
			}

			string original = Path.Combine(held, name);
			if (File.Exists(original))
			{
				File.Copy(original, file, true);
				restored++;
			}
			else if (staged is not null)
			{
				// Without a manifest the hold may be partial (a crash while it was copied): never delete then.
				File.Delete(file);
				restored++;
			}
		}

		foreach (string original in Directory.EnumerateFiles(held))
		{
			string name = Path.GetFileName(original);
			string file = Path.Combine(saveDirectory, name);
			if (name != ManifestName && !File.Exists(file))
			{
				File.Copy(original, file);
				restored++;
			}
		}

		Directory.Delete(held, true);
		return restored;
	}
}
