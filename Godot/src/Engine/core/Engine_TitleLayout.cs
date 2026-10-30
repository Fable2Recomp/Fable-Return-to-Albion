using System;
using System.Collections.Generic;
using System.IO;

namespace Refii.Core;

/// <summary>
/// Moves an install made before titles had folders into Fable 2's: <c>assets/game</c> becomes
/// <c>assets/Fable2/game</c>, the recompiled module in <c>bin/</c> becomes <c>bin/Fable2/</c>'s.
/// </summary>
/// <remarks>
/// ONE MOVE PER ITEM, AND ONLY INTO EMPTY ROOM. An item moves when it exists in the old place and
/// nothing is in the new one; a half-migrated install finishes on the next start and a migrated
/// one is left alone. A move is a rename on one filesystem, so a player's saves are never copied
/// and never exist twice.
///
/// The launcher's own config (<c>assets/launcher</c>) is shared by every title and stays put, as
/// does anything this list does not name.
/// </remarks>
public static class Engine_TitleLayout
{
	/// <summary>What a Fable 2 install kept directly under <c>assets/</c>.</summary>
	private static readonly string[] Fable2AssetItems =
	{
		"game", "update", "unpacked", "generated", "mods", "save", "metadata",
	};

	/// <summary>What the game-code build left directly in <c>bin/</c>: the module and its record.</summary>
	private static readonly string[] Fable2BinItems =
	{
		"librefii_generated.so", "refii_generated.dll", "librefii_generated.dylib", "build-info.json",
	};

	/// <summary>What Fable 2 cached directly under <c>cache/</c> before each title had its own.</summary>
	private static readonly string[] Fable2CacheItems =
	{
		"textures", "shadows", "swf",
	};

	/// <summary>
	/// Moves the old layout under the app root into the title folders, and returns one line per
	/// move (or failure) for the log. Safe to call on every start.
	/// </summary>
	public static List<string> MigrateLegacyLayout()
	{
		string projectDir = Engine_AppRoot.IsExported ? string.Empty : Engine_AppRoot.SafeGetProjectDir();
		string? repoAssets = projectDir.Length == 0
			? null
			: Path.GetFullPath(Path.Combine(projectDir, "..", "assets"));
		return MigrateLegacyLayout(
			Engine_AppRoot.AssetsDirectory, Engine_AppRoot.BinDirectory, repoAssets, Engine_AppRoot.CacheDirectory);
	}

	/// <summary>
	/// The move over explicit roots: the app's <paramref name="assets"/> and <paramref name="bin"/>,
	/// a dev tree's repository <paramref name="repoAssets"/> (null when exported), and the app's
	/// <paramref name="cache"/> (null to leave it).
	/// </summary>
	public static List<string> MigrateLegacyLayout(string assets, string bin, string? repoAssets, string? cache = null)
	{
		var report = new List<string>();
		string fable2Assets = Path.Combine(assets, Engine_Title.Fable2.Folder);

		foreach (string item in Fable2AssetItems)
		{
			Move(Path.Combine(assets, item), Path.Combine(fable2Assets, item), report);
		}

		// The interim Fable 3 folder, named for its key before titles had folders of their own.
		Move(Path.Combine(assets, Engine_Title.Fable3.Key), Path.Combine(assets, Engine_Title.Fable3.Folder), report);

		// A DEV TREE keeps the game data in the repository's own assets/, which Engine_GameData
		// searches after the app root's; it moves into the same title folder there.
		if (repoAssets is not null)
		{
			if (!string.Equals(Path.GetFullPath(repoAssets), Path.GetFullPath(assets), StringComparison.Ordinal))
			{
				foreach (string item in new[] { "game", "update", "unpacked" })
				{
					Move(Path.Combine(repoAssets, item),
						Path.Combine(repoAssets, Engine_Title.Fable2.Folder, item), report);
				}
			}
		}

		foreach (string item in Fable2BinItems)
		{
			Move(Path.Combine(bin, item), Path.Combine(bin, Engine_Title.Fable2.Folder, item), report);
		}

		if (cache is not null)
		{
			foreach (string item in Fable2CacheItems)
			{
				Move(Path.Combine(cache, item), Path.Combine(cache, Engine_Title.Fable2.Folder, item), report);
			}
		}

		return report;
	}

	private static void Move(string from, string to, List<string> report)
	{
		try
		{
			bool isDirectory = Directory.Exists(from);
			if (!isDirectory && !File.Exists(from))
			{
				return;
			}

			if (Directory.Exists(to) || File.Exists(to))
			{
				return;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(to)!);
			if (isDirectory)
			{
				Directory.Move(from, to);
			}
			else
			{
				File.Move(from, to);
			}

			report.Add("moved " + from + " -> " + to);
		}
		catch (Exception exception)
		{
			report.Add("could not move " + from + " -> " + to + ": " + exception.Message);
		}
	}
}
