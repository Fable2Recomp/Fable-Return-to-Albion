using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Refii.Bnk;
using Refii.Text;

namespace Refii.Mods;

/// <summary>One table several mods contributed rows to.</summary>
public sealed class RowMerge
{
	public string Table = string.Empty;

	/// <summary>Mods that set at least one value, lowest priority first.</summary>
	public List<string> Contributors = new();

	public int Applied;

	/// <summary>Values more than one mod set: the key, the winner, and who it beat.</summary>
	public List<RowConflict> Conflicts = new();

	public List<string> Errors = new();
}

/// <summary>One value two or more mods both set.</summary>
public sealed class RowConflict
{
	public string Key = string.Empty;

	public string Winner = string.Empty;

	public List<string> Losers = new();
}

/// <summary>
/// Composes row overlays (such as Babel text translations) across mods into one table in memory.
/// </summary>
public static class ModRowOverlays
{
	/// <summary>
	/// Applies every enabled mod's row overlays to the mount.
	/// </summary>
	public static List<RowMerge> Apply(
		BnkFileSystem mounted, IReadOnlyList<ModOverlay> overlays)
	{
		var babelByTable = new Dictionary<string, List<BabelOverlayRow>>(StringComparer.OrdinalIgnoreCase);
		var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		var contributors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

		foreach (ModOverlay overlay in overlays)
		{
			string sourcePath = !string.IsNullOrEmpty(overlay.PackagePath) ? overlay.PackagePath : overlay.AssetRoot;
			if (File.Exists(sourcePath) && sourcePath.EndsWith(".f2mod", StringComparison.OrdinalIgnoreCase))
			{
				if (F2ModPackage.TryOpen(sourcePath, out F2ModPackage? pkg, out string openErr) && pkg is not null)
				{
					using (pkg)
					{
						foreach (F2ModEntry entry in pkg.Entries)
						{
							if (!entry.Path.EndsWith(BabelOverlay.CsvSuffix, StringComparison.OrdinalIgnoreCase))
							{
								continue;
							}

							string text;
							try
							{
								byte[] bytes = pkg.ReadEntryBytes(entry);
								text = Encoding.UTF8.GetString(bytes);
							}
							catch (Exception exception)
							{
								Add(errors, overlay.Name, $"{overlay.Name}: {exception.Message}");
								continue;
							}

							string relative = entry.Path.Replace('\\', '/').TrimStart('/');
							if (relative.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
							{
								relative = relative["files/".Length..];
							}
							string table = ModLoadOrder.Normalize(relative);
							table = table[..^4];

							var parseErrors = new List<string>();
							List<BabelOverlayRow> rows = BabelOverlay.ParseCsv(text, parseErrors);
							foreach (BabelOverlayRow row in rows)
							{
								row.Mod = overlay.Name;
							}

							int added = rows.Count;
							if (added != 0)
							{
								if (!babelByTable.TryGetValue(table, out List<BabelOverlayRow>? existing))
								{
									existing = new List<BabelOverlayRow>();
									babelByTable[table] = existing;
								}

								existing.AddRange(rows);
								Add(contributors, table, overlay.Name);
							}

							foreach (string error in parseErrors)
							{
								Add(errors, table, $"{overlay.Name}: {error}");
							}
						}
					}
				}
				else
				{
					Add(errors, overlay.Name, $"{overlay.Name}: {openErr}");
				}
				continue;
			}

			if (!Directory.Exists(overlay.AssetRoot))
			{
				continue;
			}

			foreach (string file in ModManifest.EnumerateAssets(overlay.AssetRoot))
			{
				if (!file.EndsWith(BabelOverlay.CsvSuffix, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				string relative;
				string text;
				try
				{
					relative = Path.GetRelativePath(overlay.AssetRoot, file);
					text = File.ReadAllText(file);
				}
				catch (Exception exception)
				{
					Add(errors, overlay.Name, $"{overlay.Name}: {exception.Message}");
					continue;
				}

				string normRel = relative.Replace('\\', '/').TrimStart('/');
				if (normRel.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
				{
					normRel = normRel["files/".Length..];
				}
				string table = ModLoadOrder.Normalize(normRel);
				table = table[..^4];

				var parseErrors = new List<string>();
				List<BabelOverlayRow> rows = BabelOverlay.ParseCsv(text, parseErrors);
				foreach (BabelOverlayRow row in rows)
				{
					row.Mod = overlay.Name;
				}

				int added = rows.Count;
				if (added != 0)
				{
					if (!babelByTable.TryGetValue(table, out List<BabelOverlayRow>? existing))
					{
						existing = new List<BabelOverlayRow>();
						babelByTable[table] = existing;
					}

					existing.AddRange(rows);
					Add(contributors, table, overlay.Name);
				}

				foreach (string error in parseErrors)
				{
					Add(errors, table, $"{overlay.Name}: {error}");
				}
			}
		}

		var merges = new List<RowMerge>();

		foreach ((string table, List<BabelOverlayRow> rows) in babelByTable)
		{
			var keys = new List<(string Key, string Mod)>(rows.Count);
			foreach (BabelOverlayRow row in rows)
			{
				keys.Add((row.Key, row.Mod));
			}

			Merge(mounted, merges, contributors, errors, table, keys, original =>
			{
				BabelOverlayResult result = BabelOverlay.Apply(original, rows);
				return (result.Bytes, result.Applied, result.Errors);
			});
		}

		if (merges.Exists(m => m.Table.EndsWith("book.babel", StringComparison.OrdinalIgnoreCase) && m.Applied > 0))
		{
			Refii.Gdb.Engine_BnkTextBankLoader.Invalidate();
		}

		merges.Sort((left, right) => string.CompareOrdinal(left.Table, right.Table));
		return merges;
	}

	/// <summary>
	/// One table's merge, whatever kind of rows it holds.
	///
	/// The two formats differ only in how a row is applied; finding the base, reporting who
	/// contributed and who lost, and installing the result are the same for both.
	/// </summary>
	private static void Merge(
		BnkFileSystem mounted,
		List<RowMerge> merges,
		Dictionary<string, List<string>> contributors,
		Dictionary<string, List<string>> errors,
		string table,
		IReadOnlyList<(string Key, string Mod)> keys,
		Func<byte[], (byte[] Bytes, int Applied, List<string> Errors)> apply)
	{
		var merge = new RowMerge
		{
			Table = table,
			Contributors = contributors.TryGetValue(table, out List<string>? names)
				? names
				: new List<string>(),
			Errors = errors.TryGetValue(table, out List<string>? found)
				? found
				: new List<string>(),
			Conflicts = FindConflicts(keys),
		};

		if (!mounted.TryGetFile(table, out BnkFile file))
		{
			bool foundAlias = false;
			foreach (string alias in BnkFileSystem.AliasesFor(table))
			{
				if (mounted.TryGetFile(alias, out file))
				{
					table = alias;
					foundAlias = true;
					break;
				}
			}

			if (!foundAlias)
			{
				merge.Errors.Add(
					$"no '{table}' to edit — the rows name a file this install does not have.");
				merges.Add(merge);
				return;
			}
		}

		byte[] original;
		try
		{
			original = file.ReadAllBytes();
		}
		catch (Exception exception)
		{
			merge.Errors.Add($"'{table}' could not be read: {exception.Message}");
			merges.Add(merge);
			return;
		}

		(byte[] bytes, int applied, List<string> applyErrors) = apply(original);
		merge.Applied = applied;
		merge.Errors.AddRange(applyErrors);

		if (applied != 0)
		{
			// Attributed to the highest-priority contributor: it is the one whose values survive
			// a disagreement, and the full list is in the merge report.
			string owner = merge.Contributors.Count != 0 ? merge.Contributors[^1] : string.Empty;
			mounted.AddVirtualFile(table, $"<rows:{table}>", bytes.LongLength, () => bytes, owner);
		}

		merges.Add(merge);
	}

	/// <summary>
	/// Values more than one mod sets.
	///
	/// Order already decided the winner — the last write stands — so this reports rather than
	/// resolves. A conflict between two mods is not an error: it is the thing the player needs
	/// told, because the mod they installed second silently stopped working otherwise.
	/// </summary>
	private static List<RowConflict> FindConflicts(IReadOnlyList<(string Key, string Mod)> rows)
	{
		var byKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		foreach ((string key, string mod) in rows)
		{
			if (!byKey.TryGetValue(key, out List<string>? mods))
			{
				mods = new List<string>();
				byKey[key] = mods;
			}

			if (!mods.Contains(mod))
			{
				mods.Add(mod);
			}
		}

		var conflicts = new List<RowConflict>();
		foreach ((string key, List<string> mods) in byKey)
		{
			if (mods.Count < 2)
			{
				continue;
			}

			var conflict = new RowConflict { Key = key, Winner = mods[^1] };
			for (int i = 0; i < mods.Count - 1; i++)
			{
				conflict.Losers.Add(mods[i]);
			}

			conflicts.Add(conflict);
		}

		conflicts.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
		return conflicts;
	}

	private static void Add(Dictionary<string, List<string>> map, string key, string value)
	{
		if (!map.TryGetValue(key, out List<string>? list))
		{
			list = new List<string>();
			map[key] = list;
		}

		if (!list.Contains(value))
		{
			list.Add(value);
		}
	}

	/// <summary>
	/// Detects row conflicts across enabled mods without mounting.
	/// </summary>
	public static List<(string Table, RowConflict Conflict)> DetectRowConflicts(
		ModLoadOrder order,
		IReadOnlyList<ModInstall> installs)
	{
		var results = new List<(string Table, RowConflict Conflict)>();
		if (order.Enabled.Count < 2)
		{
			return results;
		}

		// Gather enabled mods in mount order (lowest priority first -> highest priority last):
		var enabledInMountOrder = new List<ModInstall>();
		for (int i = order.Enabled.Count - 1; i >= 0; i--)
		{
			string name = order.Enabled[i];
			ModInstall? mod = installs.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
			if (mod is not null)
			{
				enabledInMountOrder.Add(mod);
			}
		}

		var babelByTable = new Dictionary<string, List<BabelOverlayRow>>(StringComparer.OrdinalIgnoreCase);

		foreach (ModInstall mod in enabledInMountOrder)
		{
			if (File.Exists(mod.PackagePath) && mod.PackagePath.EndsWith(".f2mod", StringComparison.OrdinalIgnoreCase))
			{
				if (F2ModPackage.TryOpen(mod.PackagePath, out F2ModPackage? pkg, out _) && pkg is not null)
				{
					using (pkg)
					{
						foreach (F2ModEntry entry in pkg.Entries)
						{
							if (!entry.Path.EndsWith(BabelOverlay.CsvSuffix, StringComparison.OrdinalIgnoreCase))
							{
								continue;
							}

							string text;
							try
							{
								byte[] bytes = pkg.ReadEntryBytes(entry);
								text = Encoding.UTF8.GetString(bytes);
							}
							catch
							{
								continue;
							}

							string relative = entry.Path.Replace('\\', '/').TrimStart('/');
							if (relative.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
							{
								relative = relative["files/".Length..];
							}
							string table = ModLoadOrder.Normalize(relative);
							table = table[..^4];

							List<BabelOverlayRow> rows = BabelOverlay.ParseCsv(text, new List<string>());
							foreach (BabelOverlayRow row in rows)
							{
								row.Mod = mod.Name;
							}

							if (rows.Count > 0)
							{
								if (!babelByTable.TryGetValue(table, out List<BabelOverlayRow>? existing))
								{
									existing = new List<BabelOverlayRow>();
									babelByTable[table] = existing;
								}
								existing.AddRange(rows);
							}
						}
					}
				}
				continue;
			}

			string assetRoot = ModWorkspace.GetModAssetRoot(mod);
			if (!Directory.Exists(assetRoot))
			{
				continue;
			}

			foreach (string file in ModManifest.EnumerateAssets(assetRoot))
			{
				if (!file.EndsWith(BabelOverlay.CsvSuffix, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				string relative;
				string text;
				try
				{
					relative = Path.GetRelativePath(assetRoot, file);
					text = File.ReadAllText(file);
				}
				catch
				{
					continue;
				}

				string normRel = relative.Replace('\\', '/').TrimStart('/');
				if (normRel.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
				{
					normRel = normRel["files/".Length..];
				}
				string table = ModLoadOrder.Normalize(normRel);
				table = table[..^4];

				List<BabelOverlayRow> rows = BabelOverlay.ParseCsv(text, new List<string>());
				foreach (BabelOverlayRow row in rows)
				{
					row.Mod = mod.Name;
				}

				if (rows.Count > 0)
				{
					if (!babelByTable.TryGetValue(table, out List<BabelOverlayRow>? existing))
					{
						existing = new List<BabelOverlayRow>();
						babelByTable[table] = existing;
					}
					existing.AddRange(rows);
				}
			}
		}

		foreach ((string table, List<BabelOverlayRow> rows) in babelByTable)
		{
			var keys = new List<(string Key, string Mod)>(rows.Count);
			foreach (BabelOverlayRow row in rows)
			{
				keys.Add((row.Key, row.Mod));
			}

			List<RowConflict> conflicts = FindConflicts(keys);
			foreach (RowConflict conflict in conflicts)
			{
				results.Add((table, conflict));
			}
		}

		return results;
	}

	/// <summary>A one-line summary per merged table, for the log.</summary>
	public static string Describe(RowMerge merge)
	{
		var text = new StringBuilder();
		text.Append($"{merge.Table}: {merge.Applied} value(s) from ")
			.Append(string.Join(", ", merge.Contributors));
		if (merge.Conflicts.Count != 0)
		{
			text.Append($"; {merge.Conflicts.Count} row(s) set by more than one mod");
		}

		if (merge.Errors.Count != 0)
		{
			text.Append($"; {merge.Errors.Count} row(s) refused");
		}

		return text.ToString();
	}
}
