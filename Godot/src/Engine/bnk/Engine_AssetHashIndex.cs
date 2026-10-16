using System;
using System.Collections.Generic;

namespace Refii.Bnk;

/// <summary>Resolves the guest's 32-bit resource hash to a path in Godot's mount, over the same
/// file list as <see cref="BnkFileSystem"/>.</summary>
public static class AssetHashIndex
{
	/// <summary>
	/// FNV-1 32-bit (multiply then xor, not FNV-1a), the guest's <c>0x822CA588</c>. One
	/// implementation, in <see cref="Refii.Core.Engine_GameHash"/>: the guest hashes the low byte
	/// of each UTF-16 unit (<c>clrlwi r8, r10, 24</c> at 822ca65c), which for the ASCII paths the
	/// game ships is the same walk as over UTF-8 bytes.
	/// </summary>
	public static uint Hash(string value) => Refii.Core.Engine_GameHash.Fnv1(value);

	private static Dictionary<uint, string>? _byHash;

	/// <summary>How many distinct hashes the current index answers for.</summary>
	public static int Count => _byHash?.Count ?? 0;

	/// <summary>Builds the index over the mount, keyed by both spellings the guest may hash (with
	/// and without <c>data/</c>). First writer wins on a collision.</summary>
	public static void Build(IEnumerable<BnkFile> files)
	{
		var index = new Dictionary<uint, string>();

		foreach (BnkFile file in files)
		{
			string path = MountKey(file.VirtualPath);
			if (path.Length == 0)
			{
				continue;
			}

			// Key hashed in the guest's spelling (backslashes); value stored in the mount's.
			Add(index, GuestSpelling(path), path);

			// The guest hashes relative to its data root: `sub_82C6EFC0` strips `game:\` then
			// `cache:\`; `GameInit_MountDataFileSystems` (0x822F2868) sets that root to `"data\"`.
			const string dataPrefix = "data/";
			if (path.StartsWith(dataPrefix, StringComparison.Ordinal))
			{
				Add(index, GuestSpelling(path[dataPrefix.Length..]), path);
			}
			else
			{
				Add(index, GuestSpelling(dataPrefix + path), path);
			}

			// No bare-leaf key: every hashed call site in the binary hashes a full relative path.
		}

		_byHash = index;
	}

	/// <summary>Drops the index.</summary>
	public static void Clear() => _byHash = null;

	/// <summary>Registers a single virtual path into the live hash index.</summary>
	public static void RegisterPath(string virtualPath)
	{
		if (string.IsNullOrWhiteSpace(virtualPath))
		{
			return;
		}

		string path = MountKey(virtualPath);
		if (path.Length == 0)
		{
			return;
		}

		_byHash ??= new Dictionary<uint, string>();
		_byHash[Hash(GuestSpelling(path))] = path;

		const string dataPrefix = "data/";
		if (path.StartsWith(dataPrefix, StringComparison.Ordinal))
		{
			_byHash[Hash(GuestSpelling(path[dataPrefix.Length..]))] = path;
		}
		else
		{
			_byHash[Hash(GuestSpelling(dataPrefix + path))] = path;
		}
	}

	/// <summary>The mounted path for <paramref name="hash"/>; null if absent or before <see cref="Build"/>.</summary>
	public static string? PathForHash(uint hash) =>
		_byHash is { } index && index.TryGetValue(hash, out string? path) ? path : null;

	private static void Add(Dictionary<uint, string> index, string spelling, string path)
	{
		if (spelling.Length == 0)
		{
			return;
		}

		index.TryAdd(Hash(spelling), path);
	}

	/// <summary>The native <c>NormalizeLooseAssetKey</c> spelling the mount is keyed by: forward
	/// slashes, lower case, no <c>game:</c> scheme, no leading slash.</summary>
	private static string MountKey(string path)
	{
		string key = Fold(path.Replace('\\', '/'));

		if (key.StartsWith("game:/", StringComparison.Ordinal))
		{
			key = key[6..];
		}
		else if (key.StartsWith("game:", StringComparison.Ordinal))
		{
			key = key[5..];
		}

		return key.TrimStart('/');
	}

	/// <summary>The spelling the guest hashes: <c>sub_8217AF70</c> (before
	/// <c>CResourceName::GetHash</c>) lower-cases, then '/' becomes '\' at 8217b050.</summary>
	private static string GuestSpelling(string mountKey) => mountKey.Replace('/', '\\');

	/// <summary>The guest's case fold, <c>sub_8217AED0</c>: A-Z only (not <c>ToLowerInvariant</c>).</summary>
	private static string Fold(string value)
	{
		Span<char> folded = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
		for (int i = 0; i < value.Length; i++)
		{
			char ch = value[i];
			folded[i] = ch >= 'A' && ch <= 'Z' ? (char)(ch + 32) : ch;
		}

		return new string(folded);
	}
}
