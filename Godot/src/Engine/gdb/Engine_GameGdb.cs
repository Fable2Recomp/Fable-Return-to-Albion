using System;
using System.Collections.Generic;

using Godot;

namespace Refii.Gdb;

/// <summary>
/// The merged supplemental GDB every runtime reader shares, built once. Whoever mounts binds the
/// resolver (`Engine_GameAssets` in the game, `addons/bnk_mount` in the editor); unbound, every
/// lookup silently answers "no such record".
/// </summary>
public static class Engine_GameGdb
{
	private static readonly object BuildLock = new();

	private static Func<string, byte[]?>? _resolver;

	private static GdbMulti? _multi;

	private static bool _built;

	/// <summary>Where to read the GDB blobs from. Clears anything already built.</summary>
	public static void SetResolver(Func<string, byte[]?>? resolver)
	{
		lock (BuildLock)
		{
			_resolver = resolver;
			_multi = null;
			_built = false;
		}
	}

	/// <summary>The merged database, built on first use; null before a resolver is bound.</summary>
	public static GdbMulti? Database
	{
		get
		{
			lock (BuildLock)
			{
				if (_built || _resolver is null)
				{
					return _multi;
				}

				var blobs = new List<byte[]?>();
				var absent = new List<string>();
				foreach (string path in Refii.Level.Engine_LevelProps.SupplementalGdbPaths)
				{
					// Both spellings the mount answers to; a null blob is skipped by the merge.
					byte[]? blob = _resolver(path) ?? _resolver("data/" + path);
					blobs.Add(blob);
					if (blob is null)
					{
						absent.Add(path);
					}
				}

				_multi = GdbMulti.Build(blobs);
				_built = true;

				GD.Print($"[game-gdb] {blobs.Count - absent.Count}/{blobs.Count} database(s) read, " +
					$"{_multi?.RecordCount ?? 0} record(s)" +
					(absent.Count == 0 ? string.Empty : $" — NOT FOUND: {string.Join(", ", absent)}") +
					(_multi is null || _multi.RecordCount == 0
						? "  *** every GDB-backed field will be empty ***"
						: string.Empty));

				return _multi;
			}
		}
	}

	/// <summary>A record by its hash, for ids the guest already resolved.</summary>
	public static bool TryLookupHash(uint hash, out GdbCursor cursor)
	{
		cursor = default;
		GdbMulti? multi = Database;
		return multi is not null && hash != 0u && multi.TryLookup(hash, out cursor);
	}

	/// <summary>A record by its authored name, via FNV-1.</summary>
	public static bool TryLookup(string recordName, out GdbCursor cursor)
	{
		cursor = default;
		GdbMulti? multi = Database;
		return multi is not null && !string.IsNullOrEmpty(recordName) &&
			multi.TryLookup(Refii.Core.Engine_GameHash.Fnv1(recordName), out cursor);
	}
}
