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

	/// <summary>
	/// Hash to the path AS THE MOUNT SPELLS IT, the string its file already holds. The folded
	/// mount key is made on lookup (<see cref="PathForHash"/>), not stored: a stored key was a
	/// second copy of every path in the game (235k of them, ~96 characters each, about 50 MB of
	/// gen2), and the guest asks for a name only when it opens a resource.
	/// </summary>
	private static Dictionary<uint, string>? _byHash;

	/// <summary>How many distinct hashes the current index answers for.</summary>
	public static int Count => _byHash?.Count ?? 0;

	/// <summary>Builds the index over the mount, keyed by both spellings the guest may hash (with
	/// and without <c>data/</c>). First writer wins on a collision.</summary>
	public static void Build(IEnumerable<BnkFile> files)
	{
		var index = new Dictionary<uint, string>();

		// The two spellings built in one buffer: this runs over every file of the mount on the
		// main thread, and a string per spelling was 156 MB of garbage.
		const string dataPrefix = "data\\";
		Span<char> shared = stackalloc char[256];
		dataPrefix.CopyTo(shared);
		foreach (BnkFile file in files)
		{
			string virtualPath = file.VirtualPath;
			Span<char> chars = shared;
			if (virtualPath.Length + dataPrefix.Length > shared.Length)
			{
				chars = new char[virtualPath.Length + dataPrefix.Length];
				dataPrefix.CopyTo(chars);
			}

			ReadOnlySpan<char> spelling = GuestSpelling(virtualPath, chars[dataPrefix.Length..]);
			if (spelling.Length == 0)
			{
				continue;
			}

			// Key hashed in the guest's spelling (backslashes); value the mount's own string.
			Add(index, spelling, virtualPath);

			// The guest hashes relative to its data root: `sub_82C6EFC0` strips `game:\` then
			// `cache:\`; `GameInit_MountDataFileSystems` (0x822F2868) sets that root to `"data\"`.
			if (spelling.StartsWith(dataPrefix, StringComparison.Ordinal))
			{
				Add(index, spelling[dataPrefix.Length..], virtualPath);
			}
			else
			{
				// The spelling sits at the start of its buffer's tail, right after the prefix.
				Add(index, chars[..(dataPrefix.Length + spelling.Length)], virtualPath);
			}

			// No bare-leaf key: every hashed call site in the binary hashes a full relative path.
		}

		_byHash = index;
	}

	/// <summary>
	/// <see cref="GuestSpelling(string)"/> of <see cref="MountKey"/> without the strings, written
	/// to the start of <paramref name="buffer"/>, which is as long as <paramref name="path"/>.
	/// </summary>
	internal static ReadOnlySpan<char> GuestSpelling(string path, Span<char> buffer)
	{
		for (int i = 0; i < path.Length; i++)
		{
			char ch = path[i];
			buffer[i] = ch == '/' ? '\\' : ch >= 'A' && ch <= 'Z' ? (char)(ch + 32) : ch;
		}

		ReadOnlySpan<char> key = buffer[..path.Length];
		int start = 0;
		if (key.StartsWith("game:\\", StringComparison.Ordinal))
		{
			start = 6;
		}
		else if (key.StartsWith("game:", StringComparison.Ordinal))
		{
			start = 5;
		}

		while (start < key.Length && key[start] == '\\')
		{
			start++;
		}

		if (start == 0)
		{
			return key;
		}

		// To the start: the caller relies on the spelling following its prefix.
		int length = key.Length - start;
		buffer.Slice(start, length).CopyTo(buffer);
		return buffer[..length];
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
		_byHash[Hash(GuestSpelling(path))] = virtualPath;

		const string dataPrefix = "data/";
		if (path.StartsWith(dataPrefix, StringComparison.Ordinal))
		{
			_byHash[Hash(GuestSpelling(path[dataPrefix.Length..]))] = virtualPath;
		}
		else
		{
			_byHash[Hash(GuestSpelling(dataPrefix + path))] = virtualPath;
		}
	}

	/// <summary>The mounted path for <paramref name="hash"/>; null if absent or before <see cref="Build"/>.</summary>
	public static string? PathForHash(uint hash) =>
		_byHash is { } index && index.TryGetValue(hash, out string? path) ? MountKey(path) : null;

	private static void Add(Dictionary<uint, string> index, ReadOnlySpan<char> spelling, string path)
	{
		if (spelling.Length == 0)
		{
			return;
		}

		index.TryAdd(Refii.Core.Engine_GameHash.Fnv1(spelling), path);
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
