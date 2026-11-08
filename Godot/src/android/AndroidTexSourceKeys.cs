using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Godot;
using Refii.Assets;
using Refii.Bnk;
using Refii.Core;
using Refii.Models;
using FileAccess = System.IO.FileAccess;

namespace Refii.Textures;

/// <summary>What a texture's bytes are made of, or which mounted data a map belongs to: a truncated SHA-256.</summary>
internal readonly record struct AndroidTexSourceId(ulong A, ulong B);

/// <summary>An <see cref="Engine_TexCache.BaseKey"/> as its 32 raw bytes, so 14,000 of them are not 14,000 strings.</summary>
internal readonly record struct AndroidTexBaseKey(ulong A, ulong B, ulong C, ulong D)
{
	public string ToHex()
	{
		AndroidTexBaseKey copy = this;
		return Convert.ToHexString(MemoryMarshal.AsBytes(new ReadOnlySpan<AndroidTexBaseKey>(in copy))).ToLowerInvariant();
	}

	/// <summary>False unless <paramref name="hex"/> is exactly 64 hex digits.</summary>
	public static bool TryParse(ReadOnlySpan<byte> hex, out AndroidTexBaseKey key)
	{
		key = default;
		Span<byte> raw = stackalloc byte[32];
		if (!AndroidTexSourceHex.TryDecode(hex, raw))
		{
			return false;
		}

		key = MemoryMarshal.Read<AndroidTexBaseKey>(raw);
		return true;
	}
}

internal static class AndroidTexSourceHex
{
	/// <summary>False unless <paramref name="hex"/> holds exactly two digits per byte of <paramref name="raw"/>.</summary>
	public static bool TryDecode(ReadOnlySpan<byte> hex, Span<byte> raw)
	{
		if (hex.Length != raw.Length * 2)
		{
			return false;
		}

		for (int i = 0; i < raw.Length; ++i)
		{
			int high = Nibble(hex[i * 2]);
			int low = Nibble(hex[i * 2 + 1]);
			if ((high | low) < 0)
			{
				return false;
			}

			raw[i] = (byte)(high << 4 | low);
		}

		return true;
	}

	public static int Encode(ReadOnlySpan<byte> raw, Span<byte> hex)
	{
		for (int i = 0; i < raw.Length; ++i)
		{
			hex[i * 2] = (byte)"0123456789abcdef"[raw[i] >> 4];
			hex[i * 2 + 1] = (byte)"0123456789abcdef"[raw[i] & 15];
		}

		return raw.Length * 2;
	}

	private static int Nibble(byte c) => c switch
	{
		>= (byte)'0' and <= (byte)'9' => c - '0',
		>= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
		_ => -1,
	};
}

/// <summary>Works out the identities the map is keyed on. Nothing here reads a texture.</summary>
internal static class AndroidTexSourceIdentity
{
	/// <summary>Bump when <see cref="BnkFile.ReadAllBytes"/> changes what it returns for the same banks.</summary>
	private const int FormatVersion = 1;

	/// <summary>
	/// One bank file and every part it could be rejoined from. False for anything but shipped bank
	/// entries: an override is read as a whole and can change under any stamp this could take.
	/// </summary>
	public static bool TryCompute(BnkFile file, out AndroidTexSourceId id)
	{
		id = default;
		IReadOnlyList<BnkFile> shadowed = file.Shadowed;
		if (!IsShippedEntry(file))
		{
			return false;
		}

		for (int i = 0; i < shadowed.Count; ++i)
		{
			if (!IsShippedEntry(shadowed[i]))
			{
				return false;
			}
		}

		using Engine_Hash.Incremental sha = Engine_Hash.CreateIncrementalSha256();
		AppendText(sha, file.VirtualPath);
		AppendPart(sha, file);
		for (int i = 0; i < shadowed.Count; ++i)
		{
			AppendPart(sha, shadowed[i]);
		}

		id = Finish(sha);
		return true;
	}

	/// <summary>
	/// Every file on disk a shipped entry's bytes can come from: the mounted banks and the separate
	/// <c>.dat</c> files. A nested entry names its container by virtual path, so which disk file holds
	/// it can't be asked of the entry, and two banks can hold a container of the same name.
	/// </summary>
	public static bool TryComputeGeneration(BnkFileSystem mount, out AndroidTexSourceId generation)
	{
		var paths = new List<string>(mount.Archives.Count);
		foreach (BnkArchive archive in mount.Archives)
		{
			paths.Add(archive.ArchivePath);
		}

		paths.AddRange(mount.DataFilePaths);
		return TryComputeGeneration(paths, out generation);
	}

	/// <summary>False when a file can't be stated, or there are none: nothing could then notice a change.</summary>
	public static bool TryComputeGeneration(List<string> paths, out AndroidTexSourceId generation)
	{
		generation = default;
		if (paths.Count == 0)
		{
			return false;
		}

		try
		{
			paths.Sort(StringComparer.Ordinal);
			using Engine_Hash.Incremental sha = Engine_Hash.CreateIncrementalSha256();
			AppendValue(sha, FormatVersion);
			AppendValue(sha, Engine_TexCache.ConverterVersion);
			AppendValue(sha, typeof(AndroidTexSourceIdentity).Assembly.ManifestModule.ModuleVersionId);
			foreach (string path in paths)
			{
				var info = new FileInfo(path);
				if (!info.Exists)
				{
					return false;
				}

				AppendText(sha, path);
				AppendValue(sha, info.Length);
				AppendValue(sha, info.LastWriteTimeUtc.Ticks);
			}

			generation = Finish(sha);
			return true;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			return false;
		}
	}

	/// <summary>A bank entry of the game's or the title update's. Nothing else has an identity.</summary>
	private static bool IsShippedEntry(BnkFile file) =>
		!file.IsOverride && file.SourceArchivePath.Length != 0 && file.SourceArchivePath[0] != '<';

	private static void AppendPart(Engine_Hash.Incremental sha, BnkFile part)
	{
		AppendText(sha, part.SourceArchivePath);
		AppendValue(sha, part.Entry.Offset);
		AppendValue(sha, part.Entry.Size);
		AppendValue(sha, part.Entry.StoredSize);
		AppendValue(sha, part.Entry.IsCompressed);
	}

	private static void AppendText(Engine_Hash.Incremental sha, string text)
	{
		AppendValue(sha, text.Length);
		sha.AppendData(MemoryMarshal.AsBytes(text.AsSpan()));
	}

	private static void AppendValue<T>(Engine_Hash.Incremental sha, T value) where T : unmanaged =>
		sha.AppendData(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));

	private static AndroidTexSourceId Finish(Engine_Hash.Incremental sha) =>
		MemoryMarshal.Read<AndroidTexSourceId>(sha.GetHashAndReset());
}

/// <summary>
/// Identity to BaseKey, in memory and in one append-only text file. Thread safe.
/// </summary>
/// <remarks>
/// The first line names the mounted data's generation and a different one starts the file again, so
/// the file holds one line per texture rather than one per texture per bank change ever seen. Every
/// later line is 32 + 1 + 64 hex digits and a newline, and the last for an identity wins. A line
/// that isn't exactly that is skipped, which is all a torn write can leave.
/// </remarks>
internal sealed class AndroidTexSourceKeyMap : IDisposable
{
	private const string HeaderPrefix = "refii-tex-source-keys 1 ";
	private const int IdHex = 32;
	private const int KeyHex = 64;
	private const int LineBytes = IdHex + 1 + KeyHex + 1;

	private readonly object _gate = new();
	private readonly string _path;
	private readonly Dictionary<AndroidTexSourceId, AndroidTexBaseKey> _keys = new();
	private FileStream? _log;
	private AndroidTexSourceId _generation;
	private bool _open;
	private bool _logBroken;
	private bool _endsMidLine;

	public AndroidTexSourceKeyMap(string path) => _path = path;

	public int Count
	{
		get
		{
			lock (_gate)
			{
				return _keys.Count;
			}
		}
	}

	/// <summary>Loads the file on the first call; a later generation empties the map and the file.</summary>
	public void Open(AndroidTexSourceId generation)
	{
		lock (_gate)
		{
			if (_open && _generation == generation)
			{
				return;
			}

			_generation = generation;
			_keys.Clear();
			_log?.Dispose();
			_log = null;
			_logBroken = false;
			_endsMidLine = false;

			if (!_open)
			{
				_open = true;
				if (Load(generation))
				{
					return;
				}
			}

			StartFile(generation);
		}
	}

	public bool TryGet(AndroidTexSourceId id, out AndroidTexBaseKey key)
	{
		lock (_gate)
		{
			return _keys.TryGetValue(id, out key);
		}
	}

	/// <summary>Records the key, writing a line only when it is new or has changed.</summary>
	public void Set(AndroidTexSourceId id, AndroidTexBaseKey key)
	{
		lock (_gate)
		{
			if (_keys.TryGetValue(id, out AndroidTexBaseKey known) && known == key)
			{
				return;
			}

			_keys[id] = key;
			Append(id, key);
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			_log?.Dispose();
			_log = null;
		}
	}

	/// <summary>True when the file was this generation's. A missing or foreign file is started again.</summary>
	private bool Load(AndroidTexSourceId generation)
	{
		byte[] bytes;
		try
		{
			if (!File.Exists(_path))
			{
				return false;
			}

			// Shared for writing: this process's own log may still be open on it, and on Windows
			// a read that denies writers fails there, and the file would then be started again.
			using var stream = new FileStream(
				_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			bytes = new byte[stream.Length];
			stream.ReadExactly(bytes);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return false;
		}

		ReadOnlySpan<byte> rest = bytes;
		int end = rest.IndexOf((byte)'\n');
		if (end < 0 || !rest[..end].SequenceEqual(Header(generation)))
		{
			return false;
		}

		rest = rest[(end + 1)..];
		_keys.EnsureCapacity(rest.Length / LineBytes + 256);
		while (!rest.IsEmpty)
		{
			end = rest.IndexOf((byte)'\n');
			if (end < 0)
			{
				_endsMidLine = true;
				break;
			}

			if (TryParseLine(rest[..end], out AndroidTexSourceId id, out AndroidTexBaseKey key))
			{
				_keys[id] = key;
			}

			rest = rest[(end + 1)..];
		}

		return true;
	}

	private void StartFile(AndroidTexSourceId generation)
	{
		try
		{
			string? directory = Path.GetDirectoryName(_path);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			using var file = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
			file.Write(Header(generation));
			file.WriteByte((byte)'\n');
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			_logBroken = true;
		}
	}

	private void Append(AndroidTexSourceId id, AndroidTexBaseKey key)
	{
		if (_logBroken)
		{
			return;
		}

		try
		{
			// No buffer: each line reaches the file in one write, so a kill can only tear the last.
			_log ??= new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 0);

			Span<byte> line = stackalloc byte[LineBytes + 1];
			int at = 0;
			if (_endsMidLine)
			{
				line[at++] = (byte)'\n';
				_endsMidLine = false;
			}

			at += AndroidTexSourceHex.Encode(MemoryMarshal.AsBytes(new ReadOnlySpan<AndroidTexSourceId>(in id)), line[at..]);
			line[at++] = (byte)' ';
			at += AndroidTexSourceHex.Encode(MemoryMarshal.AsBytes(new ReadOnlySpan<AndroidTexBaseKey>(in key)), line[at..]);
			line[at++] = (byte)'\n';
			_log.Write(line[..at]);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			// The map still works from memory; the next launch only learns these again.
			_logBroken = true;
			_log?.Dispose();
			_log = null;
		}
	}

	private static bool TryParseLine(ReadOnlySpan<byte> line, out AndroidTexSourceId id, out AndroidTexBaseKey key)
	{
		id = default;
		key = default;
		if (line.Length != LineBytes - 1 || line[IdHex] != (byte)' ')
		{
			return false;
		}

		Span<byte> raw = stackalloc byte[IdHex / 2];
		if (!AndroidTexSourceHex.TryDecode(line[..IdHex], raw) ||
			!AndroidTexBaseKey.TryParse(line[(IdHex + 1)..], out key))
		{
			return false;
		}

		id = MemoryMarshal.Read<AndroidTexSourceId>(raw);
		return true;
	}

	private static byte[] Header(AndroidTexSourceId generation)
	{
		Span<byte> hex = stackalloc byte[IdHex];
		AndroidTexSourceHex.Encode(MemoryMarshal.AsBytes(new ReadOnlySpan<AndroidTexSourceId>(in generation)), hex);
		return System.Text.Encoding.ASCII.GetBytes(HeaderPrefix + System.Text.Encoding.ASCII.GetString(hex));
	}
}

/// <summary>
/// Lets a phone find a known texture's cache entry without reading and hashing its source.
/// </summary>
/// <remarks>
/// <see cref="Engine_TexLibrary"/> asks here first and tells it the key it worked out otherwise.
///
/// THE ONE EXCEPTION TO "HASH WHAT WAS READ, NEVER WHERE IT CAME FROM" (<see cref="Engine_TexCache.Key"/>),
/// and it holds only while where it came from fixes what was read. A remembered key is the hash of
/// bytes really read, under the same banks: every disk file a shipped entry can come from, unchanged
/// in path, length and mtime, and the same entries in the same order. A mod or loose file has no
/// identity and is always read and hashed. A wrong answer here can't be noticed afterwards, so
/// anything not plainly shipped bank data, or not reached through the mounted game, goes the old way.
/// </remarks>
public static class AndroidTexSourceKeys
{
	/// <summary>What the map needs to hear about a read, once the read has given a key.</summary>
	public readonly struct Ticket
	{
		internal Ticket(string path, Func<string, byte[]?> read, BnkFile file, AndroidTexSourceId id)
		{
			Path = path;
			Read = read;
			File = file;
			Id = id;
		}

		internal string? Path { get; }

		internal Func<string, byte[]?>? Read { get; }

		internal BnkFile? File { get; }

		internal AndroidTexSourceId Id { get; }

		/// <summary>Whether the texture had an identity at all, which a mod's never has.</summary>
		internal bool Issued => File is not null;
	}

	/// <summary>Finds the mounted file a reader would serve this path from, or says it can't be known.</summary>
	internal delegate bool MountLookup(string path, Func<string, byte[]?> read, out BnkFileSystem? mount, out BnkFile? file);

	private sealed class Generation
	{
		public AndroidTexSourceId? Id;
	}

	private static readonly Func<string, byte[]?> MeshRead = Engine_BnkMeshLoader.ReadBytes;
	private static readonly Func<string, byte[]?> TextureRead = Engine_BnkTextureLoader.ReadBytes;
	private static readonly ConditionalWeakTable<BnkFileSystem, Generation> Generations = new();
	private static readonly object MapGate = new();
	private static AndroidTexSourceKeyMap? _map;
	private static Engine_Title? _mapTitle;
	private static long _served;
	private static long _stale;
	private static long _lastReported = -1;

	/// <summary>Phones only; REFII_TEX_SOURCE_KEYS=0 turns it off to compare a load with and without it.</summary>
	internal static bool Enabled { get; set; } =
		Engine_Platform.IsAndroid && System.Environment.GetEnvironmentVariable("REFII_TEX_SOURCE_KEYS") != "0";

	internal static MountLookup Lookup { get; set; } = FromGameAssets;

	/// <summary>Where a test keeps its map, in place of the active title's.</summary>
	internal static string? FilePathOverride { get; set; }

	/// <summary>Cached images handed out without reading the source, since the process started.</summary>
	internal static long Served => Interlocked.Read(ref _served);

	/// <summary>Known keys whose cache entry had gone, each a fall back to the full path.</summary>
	internal static long Stale => Interlocked.Read(ref _stale);

	/// <summary>
	/// The cached image of a texture already seen with these exact banks, or null. Whatever happens
	/// next must go through <see cref="Learn"/>, which also corrects a key whose entry has gone.
	/// </summary>
	public static Image? TryServe(string path, Func<string, byte[]?> read, out Ticket ticket, out bool hasAlpha)
	{
		hasAlpha = false;
		if (Identify(path, read, out ticket) is not { } map || !map.TryGet(ticket.Id, out AndroidTexBaseKey key))
		{
			return null;
		}

		Image? image = Engine_TexCache.TryLoad(key.ToHex(), out hasAlpha);
		if (image is null)
		{
			hasAlpha = false;
			Interlocked.Increment(ref _stale);
			return null;
		}

		Interlocked.Increment(ref _served);
		return image;
	}

	/// <summary>The key of a texture already seen with these exact banks. The ticket is issued whether it was seen or not.</summary>
	internal static bool TryKnownKey(string path, Func<string, byte[]?> read, out Ticket ticket, out string? key)
	{
		key = null;
		if (Identify(path, read, out ticket) is not { } map || !map.TryGet(ticket.Id, out AndroidTexBaseKey known))
		{
			return false;
		}

		key = known.ToHex();
		return true;
	}

	/// <summary>The key of the file a ticket was issued for, if that file is still the mounted one and has been learned.</summary>
	internal static bool TryKeyOf(in Ticket ticket, out string? key)
	{
		key = null;
		if (ticket.File is null ||
			!Lookup(ticket.Path!, ticket.Read!, out _, out BnkFile? current) ||
			!ReferenceEquals(current, ticket.File) ||
			!Map().TryGet(ticket.Id, out AndroidTexBaseKey known))
		{
			return false;
		}

		key = known.ToHex();
		return true;
	}

	/// <summary>The open map and a ticket for this texture, or null if it is not plainly shipped bank data.</summary>
	private static AndroidTexSourceKeyMap? Identify(string path, Func<string, byte[]?> read, out Ticket ticket)
	{
		ticket = default;

		// Enhancement is keyed on the source bytes, so it needs the read this exists to skip.
		if (!Enabled || Engine_TexEnhance.Enabled || !Engine_TexSettings.CacheEnabled ||
			!Lookup(path, read, out BnkFileSystem? mount, out BnkFile? file) ||
			mount is null || file is null ||
			!AndroidTexSourceIdentity.TryCompute(file, out AndroidTexSourceId id) ||
			GenerationOf(mount) is not { } generation)
		{
			return null;
		}

		AndroidTexSourceKeyMap map = Map();
		map.Open(generation);
		ticket = new Ticket(path, read, file, id);
		return map;
	}

	/// <summary>
	/// Records the key hashed from what <see cref="Ticket"/>'s read returned. Nothing is kept if the
	/// mount changed during the read: those bytes may be a mod's, and the identity the game's. Nor
	/// if <paramref name="source"/> is shorter than its header states: a join that lost a part to a
	/// one-off read failure would otherwise be served for good, where the next read would heal.
	/// </summary>
	public static void Learn(in Ticket ticket, string baseKey, byte[] source)
	{
		if (ticket.File is null ||
			(Refii.Textures.TexHeader.TryParse(source, out Refii.Textures.TexHeader header, out _) &&
			 source.Length < (long)header.HeaderSize + header.PayloadSize) ||
			!Lookup(ticket.Path!, ticket.Read!, out _, out BnkFile? current) ||
			!ReferenceEquals(current, ticket.File))
		{
			return;
		}

		Span<byte> ascii = stackalloc byte[baseKey.Length];
		if (System.Text.Encoding.ASCII.GetBytes(baseKey, ascii) == ascii.Length &&
			AndroidTexBaseKey.TryParse(ascii, out AndroidTexBaseKey key))
		{
			Map().Set(ticket.Id, key);
		}
	}

	/// <summary>One log line, when the counts have moved.</summary>
	public static void Report()
	{
		long moved = Served + Stale + (_map?.Count ?? 0);
		if (!Enabled || moved == Interlocked.Exchange(ref _lastReported, moved))
		{
			return;
		}

		GD.Print($"[tex] source keys: {_map?.Count ?? 0:N0} known, {Served:N0} served without a read, {Stale:N0} entries gone");
	}

	/// <summary>Drops the map, so a test starts from its file.</summary>
	internal static void ResetForTests()
	{
		lock (MapGate)
		{
			_map?.Dispose();
			_map = null;
		}

		Interlocked.Exchange(ref _served, 0);
		Interlocked.Exchange(ref _stale, 0);
		Interlocked.Exchange(ref _lastReported, -1);
	}

	/// <summary>
	/// Whether this reader ends at the mount and nowhere else. A caller's own closure, or a loader
	/// with another resolver in place (the prebuild's, the editor's), may read anything.
	/// </summary>
	internal static bool ReadsThroughMount(Func<string, byte[]?> read, Func<string, byte[]?> mountRead)
	{
		Func<string, byte[]?>? resolver =
			read == MeshRead ? Engine_BnkMeshLoader.Resolver :
			read == TextureRead ? Engine_BnkTextureLoader.Resolver :
			null;
		return resolver == mountRead;
	}

	private static bool FromGameAssets(string path, Func<string, byte[]?> read, out BnkFileSystem? mount, out BnkFile? file)
	{
		mount = null;
		file = null;
		return Engine_GameAssets.Instance is { } assets &&
			ReadsThroughMount(read, assets.ReadAllBytes) &&
			assets.TryGetMountedFile(path, out mount, out file);
	}

	internal static AndroidTexSourceId? GenerationOf(BnkFileSystem mount) =>
		Generations.GetValue(mount, static m =>
			new Generation
			{
				Id = AndroidTexSourceIdentity.TryComputeGeneration(m, out AndroidTexSourceId id) ? id : null,
			}).Id;

	private static AndroidTexSourceKeyMap Map()
	{
		Engine_Title title = Engine_Title.Active;
		lock (MapGate)
		{
			if (_map is null || !ReferenceEquals(_mapTitle, title))
			{
				_map?.Dispose();
				_mapTitle = title;

				// Beside the entries, in the epoch directory a converter bump deletes. The sweep only
				// takes .rftx and .tmp.
				_map = new AndroidTexSourceKeyMap(FilePathOverride ?? Path.Combine(
					Engine_TexCache.CacheRoot, $"v{Engine_TexCache.ConverterVersion}", "source-keys.txt"));
			}

			return _map;
		}
	}
}
