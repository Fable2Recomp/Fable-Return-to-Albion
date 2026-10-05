using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Refii.Bnk;

namespace Refii.Mods;

/// <summary>One enabled mod's assets, as the mount takes them.</summary>
public sealed class ModOverlay
{
	public required string Name { get; init; }

	public string PackagePath { get; init; } = string.Empty;

	public required string AssetRoot { get; init; }
}

/// <summary>What the fold decided about one virtual path.</summary>
public sealed class ResolvedEntry
{
	public string Path = string.Empty;

	/// <summary>The mod that owns it, or empty for the game's own data.</summary>
	public string Owner = string.Empty;

	/// <summary><c>override</c>, <c>addition</c> or <c>rows</c>.</summary>
	public string Kind = string.Empty;

	/// <summary>Everyone the owner shadows, nearest first — mod names, or <c>game</c>.</summary>
	public List<string> Shadowed = new();

	public const string KindOverride = "override";
	public const string KindAddition = "addition";
	public const string KindRows = "rows";
	public const string KindTu = "tu";
}

/// <summary>Per-mod totals, for the panel.</summary>
public sealed class ModTally
{
	public int Overrides;
	public int Additions;
	public int Rows;
	public int Tu;

	/// <summary>Paths or records this mod contributed that another enabled mod also contributed.</summary>
	public int Conflicts;

	/// <summary>Of those, the ones it lost.</summary>
	public int Lost;

	/// <summary>Number of individual GDB records overridden by higher priority mods.</summary>
	public int OverriddenRecords;

	/// <summary>Human-readable collision details for tooltips.</summary>
	public List<string> ConflictDetails { get; } = new();
}

/// <summary>
/// THE MASTER LIST, DERIVED FROM THE MOUNT.
///
/// The mount already decides who owns each path — that is what <see cref="BnkFileSystem"/>'s
/// shadowing does. This walks the finished tree and states the decision: owner, what the owner
/// shadows, and whether it replaced a shipped file or added a new one.
///
/// DERIVED, NEVER TRUSTED AS INPUT. A list the mount believed would drift the moment a mod
/// folder changed under it — the folder would say one thing, the list another, and the failure
/// would be silent. Manifests and load order are the inputs; this is a report, and the panel and
/// the log read it so both describe the same mount.
/// </summary>
public static class ModIndex
{
	public const string ReportFilename = "resolved.json";

	/// <summary>
	/// The overlays for an enabled list, IN MOUNT ORDER — lowest priority first.
	///
	/// <see cref="ModLoadOrder.Enabled"/> is highest-first because that is how the panel shows
	/// it; the mount inserts last-wins, so the list is reversed exactly once, here.
	/// </summary>
	public static List<ModOverlay> ResolveOverlays(
		string modsRoot, ModLoadOrder order, IReadOnlyList<ModInstall> installs)
	{
		var overlays = new List<ModOverlay>();
		if (string.IsNullOrEmpty(modsRoot))
		{
			return overlays;
		}

		for (int i = order.Enabled.Count - 1; i >= 0; i--)
		{
			string name = order.Enabled[i];
			ModInstall? mod = null;
			foreach (ModInstall candidate in installs)
			{
				if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
				{
					mod = candidate;
					break;
				}
			}

			if (mod is null)
			{
				continue;
			}

			string packagePath = mod.PackagePath;
			if (File.Exists(packagePath))
			{
				overlays.Add(new ModOverlay { Name = mod.Name, PackagePath = packagePath, AssetRoot = packagePath });
			}
		}

		return overlays;
	}

	/// <summary>Reads the order and installs, and resolves the overlays in one step.</summary>
	public static List<ModOverlay> ResolveOverlays(string modsRoot, out ModLoadOrder order)
	{
		order = ModLoadOrder.Read(modsRoot);
		return ResolveOverlays(modsRoot, order, ModWorkspace.DiscoverModInstalls(modsRoot));
	}

	/// <summary>The overlay list in the shape <see cref="BnkFileSystem.Mount"/> takes.</summary>
	public static List<(string Name, string AssetRoot)> ToMountOverlays(
		IReadOnlyList<ModOverlay> overlays)
	{
		var mount = new List<(string Name, string AssetRoot)>(overlays.Count);
		foreach (ModOverlay overlay in overlays)
		{
			mount.Add((overlay.Name, overlay.AssetRoot));
		}

		return mount;
	}

	/// <summary>
	/// The same fold, over manifests instead of a mount.
	///
	/// THE PANEL CANNOT MOUNT. Mods are enabled in the launcher, before the game runs, so the
	/// only honest way to answer "do these two conflict" there is to fold what each mod says it
	/// contributes. It agrees with <see cref="Build"/> because both apply the same order to the
	/// same paths; what it cannot know is whether a path also exists in the game's data, so
	/// every entry is reported as a plain contribution rather than an override or an addition.
	/// </summary>
	public static List<ResolvedEntry> Predict(
		ModLoadOrder order, IReadOnlyList<ModInstall> installs)
	{
		var owners = new Dictionary<string, ResolvedEntry>(StringComparer.OrdinalIgnoreCase);

		for (int i = order.Enabled.Count - 1; i >= 0; i--)
		{
			string name = order.Enabled[i];
			ModInstall? mod = null;
			foreach (ModInstall candidate in installs)
			{
				if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
				{
					mod = candidate;
					break;
				}
			}

			if (mod is null)
			{
				continue;
			}

			foreach (ModManifestEntry entry in ModManifest.EnsureCurrent(mod).Entries)
			{
				if (entry.Kind is ModManifestEntry.KindTu or ModManifestEntry.KindRows)
				{
					if (owners.TryGetValue(entry.Path, out ResolvedEntry? existingVirtual))
					{
						existingVirtual.Owner = mod.Name;
						existingVirtual.Kind = entry.Kind;
						// GDB Title Updates and Babel row tables merge at the record/row level;
						// multiple mods modifying the same container is a merge, not a whole-file loss.
						continue;
					}

					owners[entry.Path] = new ResolvedEntry
					{
						Path = entry.Path,
						Owner = mod.Name,
						Kind = entry.Kind,
					};
					continue;
				}

				if (owners.TryGetValue(entry.Path, out ResolvedEntry? existing))
				{
					existing.Shadowed.Insert(0, existing.Owner);
					existing.Owner = mod.Name;
					existing.Kind = entry.Kind == ModManifestEntry.KindRows
						? ResolvedEntry.KindRows
						: existing.Kind;
					continue;
				}

				owners[entry.Path] = new ResolvedEntry
				{
					Path = entry.Path,
					Owner = mod.Name,
					Kind = entry.Kind == ModManifestEntry.KindRows
						? ResolvedEntry.KindRows
						: ResolvedEntry.KindOverride,
				};
			}
		}

		// Pins beat order here exactly as they do in the mount, so the panel's numbers match
		// what the game will do.
		foreach ((string path, string owner) in order.Pins)
		{
			if (!owners.TryGetValue(path, out ResolvedEntry? entry) ||
				string.Equals(entry.Owner, owner, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			int index = entry.Shadowed.FindIndex(
				name => string.Equals(name, owner, StringComparison.OrdinalIgnoreCase));
			if (index < 0)
			{
				continue;
			}

			entry.Shadowed.RemoveAt(index);
			entry.Shadowed.Insert(0, entry.Owner);
			entry.Owner = owner;
		}

		var entries = new List<ResolvedEntry>(owners.Values);
		entries.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
		return entries;
	}

	/// <summary>
	/// Walks the mounted tree and states who won what.
	///
	/// Only paths a mod touched appear: the game's own 125k entries are the baseline, and a
	/// report that listed them would bury the twenty lines that matter.
	/// </summary>
	public static List<ResolvedEntry> Build(BnkFileSystem filesystem)
	{
		var entries = new List<ResolvedEntry>();
		foreach (BnkFile file in filesystem.EnumerateAllFiles())
		{
			bool ownerIsMod = file.SourceMod.Length != 0;
			bool anyMod = ownerIsMod;
			if (!anyMod)
			{
				foreach (BnkFile shadowed in file.Shadowed)
				{
					if (shadowed.SourceMod.Length != 0)
					{
						anyMod = true;
						break;
					}
				}
			}

			if (!anyMod)
			{
				continue;
			}

			var entry = new ResolvedEntry
			{
				Path = file.VirtualPath,
				Owner = file.SourceMod,
				Kind = file.VirtualPath.EndsWith(".gdb.tu", StringComparison.OrdinalIgnoreCase)
					? ResolvedEntry.KindTu
					: (file.SourceArchivePath.StartsWith("<rows:", StringComparison.OrdinalIgnoreCase)
						? ResolvedEntry.KindRows
						: (!ownerIsMod || HasGameSource(file)
							? ResolvedEntry.KindOverride
							: ResolvedEntry.KindAddition)),
			};

			foreach (BnkFile shadowed in file.Shadowed)
			{
				entry.Shadowed.Add(shadowed.SourceMod.Length != 0 ? shadowed.SourceMod : "game");
			}

			entries.Add(entry);
		}

		entries.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
		return entries;
	}

	/// <summary>Per-mod totals over a built report or prediction.</summary>
	public static Dictionary<string, ModTally> Tally(
		IReadOnlyList<ResolvedEntry> entries,
		IReadOnlyList<Refii.Gdb.GdbRecordConflict>? recordConflicts = null)
	{
		var tallies = new Dictionary<string, ModTally>(StringComparer.OrdinalIgnoreCase);

		ModTally For(string name)
		{
			if (!tallies.TryGetValue(name, out ModTally? tally))
			{
				tally = new ModTally();
				tallies[name] = tally;
			}

			return tally;
		}

		foreach (ResolvedEntry entry in entries)
		{
			int modsOnPath = entry.Owner.Length != 0 ? 1 : 0;
			foreach (string shadowed in entry.Shadowed)
			{
				if (shadowed != "game")
				{
					modsOnPath++;
				}
			}

			if (entry.Owner.Length != 0)
			{
				ModTally owner = For(entry.Owner);
				switch (entry.Kind)
				{
					case ResolvedEntry.KindAddition:
						owner.Additions++;
						break;
					case ResolvedEntry.KindRows:
						owner.Rows++;
						break;
					case ResolvedEntry.KindTu:
						owner.Tu++;
						break;
					default:
						owner.Overrides++;
						break;
				}

				if (modsOnPath > 1)
				{
					owner.Conflicts++;
				}
			}

			foreach (string shadowed in entry.Shadowed)
			{
				if (shadowed == "game")
				{
					continue;
				}

				ModTally loser = For(shadowed);
				loser.Conflicts++;
				loser.Lost++;
				loser.ConflictDetails.Add($"File '{entry.Path}' overridden by '{entry.Owner}'");
			}
		}

		if (recordConflicts is not null)
		{
			foreach (Refii.Gdb.GdbRecordConflict conflict in recordConflicts)
			{
				if (!string.IsNullOrEmpty(conflict.WinnerMod))
				{
					ModTally winner = For(conflict.WinnerMod);
					winner.Conflicts++;
				}

				foreach (string loser in conflict.LoserMods)
				{
					ModTally loserTally = For(loser);
					loserTally.Conflicts++;
					loserTally.Lost++;
					loserTally.OverriddenRecords++;
					string desc = !string.IsNullOrEmpty(conflict.PropertyName)
						? $"{conflict.PropertyName} on {conflict.RecordName} in {conflict.DatabaseSubPath} (overridden by '{conflict.WinnerMod}')"
						: $"{conflict.RecordName} in {conflict.DatabaseSubPath} (overridden by '{conflict.WinnerMod}')";
					loserTally.ConflictDetails.Add(desc);
				}
			}
		}

		return tallies;
	}

	/// <summary>
	/// Writes the report beside the player's own data.
	///
	/// The caller supplies the destination because <c>user://</c> is Godot's to resolve; this
	/// stays a plain path so the same code serves the editor, the game and the tools.
	/// </summary>
	public static bool WriteReport(
		string path,
		string gameDirectory,
		IReadOnlyList<ModOverlay> overlays,
		IReadOnlyList<ResolvedEntry> entries,
		IReadOnlyList<RowMerge> merges,
		out string error)
	{
		error = string.Empty;
		try
		{
			string? directory = System.IO.Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			Dictionary<string, ModTally> tallies = Tally(entries);

			using var stream = new MemoryStream();
			using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
			{
				writer.WriteStartObject();
				writer.WriteString("generated", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
				writer.WriteString("game", gameDirectory);

				writer.WriteStartArray("order");
				// Highest priority first, which is the panel's order and the reverse of the
				// order the mount consumed.
				for (int i = overlays.Count - 1; i >= 0; i--)
				{
					ModOverlay overlay = overlays[i];
					writer.WriteStartObject();
					writer.WriteString("name", overlay.Name);
					writer.WriteString("root", overlay.AssetRoot);
					if (tallies.TryGetValue(overlay.Name, out ModTally? tally))
					{
						writer.WriteNumber("overrides", tally.Overrides);
						writer.WriteNumber("additions", tally.Additions);
						writer.WriteNumber("rows", tally.Rows);
						writer.WriteNumber("conflicts", tally.Conflicts);
						writer.WriteNumber("lost", tally.Lost);
					}

					writer.WriteEndObject();
				}

				writer.WriteEndArray();

				writer.WriteStartArray("entries");
				foreach (ResolvedEntry entry in entries)
				{
					writer.WriteStartObject();
					writer.WriteString("path", entry.Path);
					writer.WriteString("owner", entry.Owner.Length != 0 ? entry.Owner : "game");
					writer.WriteString("kind", entry.Kind);
					writer.WriteStartArray("shadowed");
					foreach (string shadowed in entry.Shadowed)
					{
						writer.WriteStringValue(shadowed);
					}

					writer.WriteEndArray();
					writer.WriteEndObject();
				}

				writer.WriteEndArray();

				// ROWS, SEPARATELY FROM FILES. A merged table is one file with one owner, and
				// saying only that hides the thing the player needs: which mods set which values,
				// and whose value lost.
				writer.WriteStartArray("rows");
				foreach (RowMerge merge in merges)
				{
					writer.WriteStartObject();
					writer.WriteString("table", merge.Table);
					writer.WriteNumber("applied", merge.Applied);
					writer.WriteStartArray("contributors");
					foreach (string contributor in merge.Contributors)
					{
						writer.WriteStringValue(contributor);
					}

					writer.WriteEndArray();
					writer.WriteStartArray("conflicts");
					foreach (RowConflict conflict in merge.Conflicts)
					{
						writer.WriteStartObject();
						writer.WriteString("key", conflict.Key);
						writer.WriteString("winner", conflict.Winner);
						writer.WriteStartArray("lost");
						foreach (string loser in conflict.Losers)
						{
							writer.WriteStringValue(loser);
						}

						writer.WriteEndArray();
						writer.WriteEndObject();
					}

					writer.WriteEndArray();
					writer.WriteStartArray("refused");
					foreach (string refused in merge.Errors)
					{
						writer.WriteStringValue(refused);
					}

					writer.WriteEndArray();
					writer.WriteEndObject();
				}

				writer.WriteEndArray();
				writer.WriteEndObject();
			}

			File.WriteAllBytes(path, stream.ToArray());
			return true;
		}
		catch (Exception exception)
		{
			error = exception.Message;
			return false;
		}
	}

	private static bool HasGameSource(BnkFile file)
	{
		foreach (BnkFile shadowed in file.Shadowed)
		{
			if (shadowed.SourceMod.Length == 0)
			{
				return true;
			}
		}

		return false;
	}
}
