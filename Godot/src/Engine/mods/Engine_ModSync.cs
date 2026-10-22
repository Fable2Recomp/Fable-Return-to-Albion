using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Refii.Mods;

/// <summary>
/// Mod sync on a co-op join: the host's enabled set and order is the session's. The joiner uses
/// what it has with the same package bytes, downloads what it lacks from the host's machine,
/// and suspends its own mods that are not in the host's set — for the session only, with the
/// player's consent once per join, and never for a hard mod (native code is compiled in at
/// launch, so it can be neither taken on nor put down live).
/// </summary>
/// <remarks>
/// PURE. The plan (<see cref="Resolve"/>), the consent text, the phase statements the joiner
/// restates on the co-op channel and the transfer's framing run on in-memory values; the cache
/// (<see cref="ModSyncCache"/>) is the one part that touches the disk. <c>GameUiCoopMods</c> drives
/// it, <see cref="ModSession"/> holds the resulting set apart from the player's own.
/// </remarks>
public static class ModSync
{
	/// <summary>The most a session may download in all. Stated in the consent box.</summary>
	public const long MaxSessionBytes = 64L * 1024 * 1024;

	/// <summary>Bytes per transfer chunk.</summary>
	public const int ChunkBytes = 64 * 1024;

	/// <summary>Chunks the sender keeps in flight beyond the receiver's last acknowledgement.</summary>
	public const int WindowChunks = 8;

	/// <summary>Where one mod of the session set comes from.</summary>
	public enum Source
	{
		/// <summary>The joiner's own installed package, same bytes.</summary>
		Installed,

		/// <summary>Received from this host in an earlier session, kept in the session cache.</summary>
		Cached,

		/// <summary>Not on this machine: downloaded from the host.</summary>
		Download,
	}

	/// <summary>One mod of the host's set, as the joiner will provide it.</summary>
	public sealed record Item(ModFingerprint.Mod Mod, Source From, string Path);

	/// <summary>What a join needs: the session order and what has to happen to run it.</summary>
	public sealed class Plan
	{
		/// <summary>The host's set, in the host's order, each resolved; the session order.</summary>
		public List<Item> Order { get; } = new();

		/// <summary>The joiner's own enabled mods the host does not run: suspended for the session.</summary>
		public List<ModFingerprint.Mod> Suspend { get; } = new();

		/// <summary>The joiner's own enabled mods the host runs in another version or build: the host's replaces them.</summary>
		public List<ModFingerprint.Mod> Replaced { get; } = new();

		/// <summary>Why the set cannot be synced; empty when it can.</summary>
		public string Blocked = string.Empty;

		/// <summary>The host's digest, which the session set reproduces once applied.</summary>
		public string HostDigest = string.Empty;

		public bool Ok => Blocked.Length == 0;

		public IEnumerable<Item> Downloads
		{
			get
			{
				foreach (Item item in Order)
				{
					if (item.From == Source.Download)
					{
						yield return item;
					}
				}
			}
		}

		public long DownloadBytes
		{
			get
			{
				long total = 0;
				foreach (Item item in Downloads)
				{
					total += item.Mod.Size;
				}

				return total;
			}
		}

		public int DownloadCount
		{
			get
			{
				int count = 0;
				foreach (Item _ in Downloads)
				{
					++count;
				}

				return count;
			}
		}

		/// <summary>Whether anything changes for the joiner at all.</summary>
		public bool Trivial => Suspend.Count == 0 && Replaced.Count == 0 && DownloadCount == 0;

		/// <summary>
		/// A download has landed in the cache: the entry takes the package's full SHA-256 (the
		/// wire carried the short one) and its path, and counts as cached from now on.
		/// </summary>
		public void Received(string name, string sha256, string path)
		{
			for (int i = 0; i < Order.Count; i++)
			{
				Item item = Order[i];
				if (item.From == Source.Download && string.Equals(item.Mod.Name, name, StringComparison.OrdinalIgnoreCase))
				{
					Order[i] = new Item(item.Mod with { Sha256 = sha256.ToLowerInvariant() }, Source.Cached, path);
					return;
				}
			}
		}

		/// <summary>The session set for <see cref="ModSession.Apply"/>, once every download is in the cache.</summary>
		public List<ModSession.Entry> ToSession(string cacheDir)
		{
			var set = new List<ModSession.Entry>(Order.Count);
			foreach (Item item in Order)
			{
				string path = item.From == Source.Download ? ModSyncCache.PathFor(cacheDir, item.Mod.Sha256) : item.Path;
				set.Add(new ModSession.Entry(item.Mod.Name, path, item.Mod.Title, item.Mod.Version, item.Mod.Sha256));
			}

			return set;
		}
	}

	/// <summary>
	/// The plan for joining <paramref name="host"/>'s session from a machine running
	/// <paramref name="own"/> (its enabled set) with <paramref name="installed"/> (every package it
	/// has, enabled or not, full hashes) and <paramref name="cached"/> (the session cache's
	/// packages, full hashes, names as the cache recorded them). Blocked when the API differs, the
	/// host's list did not fit one message, a hard mod is missing on either side, or the downloads
	/// exceed <paramref name="ceiling"/>.
	/// </summary>
	public static Plan Resolve(
		ModFingerprint host, ModFingerprint own, IReadOnlyList<Item> installed,
		IReadOnlyList<Item> cached, long ceiling = MaxSessionBytes)
	{
		var plan = new Plan { HostDigest = host.Digest() };
		var blocked = new List<string>();
		if (host.ApiVersion != own.ApiVersion)
		{
			blocked.Add($"the host runs mod API {host.ApiVersion}, you run mod API {own.ApiVersion}");
		}

		if (!host.Complete)
		{
			blocked.Add($"the host has {host.Total} mods enabled, more than one message can list");
		}

		var ownByName = new Dictionary<string, ModFingerprint.Mod>(StringComparer.OrdinalIgnoreCase);
		foreach (ModFingerprint.Mod mod in own.Mods)
		{
			ownByName[mod.Name] = mod;
		}

		var hostNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (ModFingerprint.Mod theirs in host.Mods)
		{
			hostNames.Add(theirs.Name);
			bool enabledHere = ownByName.TryGetValue(theirs.Name, out ModFingerprint.Mod mine) && SameBytes(mine.Sha256, theirs.Sha256);
			if (theirs.Native)
			{
				// Compiled into the game at launch: only a joiner that launched with the same one can play.
				if (enabledHere)
				{
					Item? built = Find(installed, mine);
					plan.Order.Add(new Item(built?.Mod ?? mine, Source.Installed, built?.Path ?? string.Empty));
				}
				else
				{
					blocked.Add(ownByName.ContainsKey(theirs.Name)
						? $"{theirs.Label} is a hard mod and your build of it differs; install the host's version, enable it and relaunch"
						: $"{theirs.Label} is a hard mod with native code; install it, enable it and relaunch to join");
				}

				continue;
			}

			Item? have = Find(installed, theirs);
			if (have is not null)
			{
				plan.Order.Add(new Item(have.Mod with { Title = theirs.Title, Native = false }, Source.Installed, have.Path));
				continue;
			}

			Item? kept = FindCached(cached, theirs);
			if (kept is not null)
			{
				plan.Order.Add(new Item(kept.Mod with { Name = theirs.Name, Title = theirs.Title, Version = theirs.Version, Native = false }, Source.Cached, kept.Path));
				continue;
			}

			plan.Order.Add(new Item(theirs with { Native = false }, Source.Download, string.Empty));
		}

		foreach (ModFingerprint.Mod mine in own.Mods)
		{
			if (!hostNames.Contains(mine.Name))
			{
				if (mine.Native)
				{
					blocked.Add($"your hard mod {mine.Label} is compiled into your game and the host does not run it; disable it and relaunch to join");
				}
				else
				{
					plan.Suspend.Add(mine);
				}

				continue;
			}

			bool same = false;
			foreach (Item item in plan.Order)
			{
				if (string.Equals(item.Mod.Name, mine.Name, StringComparison.OrdinalIgnoreCase) &&
					item.From == Source.Installed && SameBytes(item.Mod.Sha256, mine.Sha256))
				{
					same = true;
					break;
				}
			}

			if (!same && !mine.Native)
			{
				plan.Replaced.Add(mine);
			}
		}

		long bytes = plan.DownloadBytes;
		if (bytes > ceiling)
		{
			blocked.Add($"the host's mods you lack total {Bytes(bytes)}, over the {Bytes(ceiling)} a session may download");
		}

		plan.Blocked = string.Join("; ", blocked);
		return plan;
	}

	/// <summary>The installed package whose name and bytes match <paramref name="wanted"/> (full or short hash).</summary>
	private static Item? Find(IReadOnlyList<Item> installed, ModFingerprint.Mod wanted)
	{
		foreach (Item item in installed)
		{
			if (string.Equals(item.Mod.Name, wanted.Name, StringComparison.OrdinalIgnoreCase) && SameBytes(item.Mod.Sha256, wanted.Sha256))
			{
				return item;
			}
		}

		return null;
	}

	private static Item? FindCached(IReadOnlyList<Item> cached, ModFingerprint.Mod wanted)
	{
		foreach (Item item in cached)
		{
			if (SameBytes(item.Mod.Sha256, wanted.Sha256) && item.Mod.Sha256.Length == 64)
			{
				return item;
			}
		}

		return null;
	}

	/// <summary>Package hashes agree on the characters both sides carry; an empty one never agrees.</summary>
	public static bool SameBytes(string left, string right)
	{
		int length = Math.Min(left.Length, right.Length);
		return length != 0 &&
			string.Compare(left, 0, right, 0, length, StringComparison.OrdinalIgnoreCase) == 0;
	}

	// ---------------------------------------------------------------- consent

	public const string AcceptCaption = "Accept";

	public const string DeclineCaption = "Decline";

	/// <summary>
	/// The question put to the joiner once per join, before any byte is downloaded: what will be
	/// fetched (with version and size), used as-is, replaced and suspended, that the mods carry
	/// scripts from the host's machine, and that nothing of theirs is rewritten.
	/// </summary>
	public static string ConsentText(Plan plan, string hostName)
	{
		string host = hostName.Length == 0 ? "The host" : hostName;
		string whose = hostName.Length == 0 ? "the host's" : hostName + "'s";
		var text = new StringBuilder();
		int count = plan.Order.Count;
		text.Append(count == 0
			? $"{host} runs no mods. To play together, your mods are suspended for this session."
			: $"{host} runs {count} mod{(count == 1 ? string.Empty : "s")}. To play together, your game uses {whose} mods, in {whose} order, for this session.");

		if (plan.DownloadCount != 0)
		{
			text.Append($"\n\nDownload from {whose} machine ({Bytes(plan.DownloadBytes)} of {Bytes(MaxSessionBytes)} allowed):");
			foreach (Item item in plan.Downloads)
			{
				text.Append($"\n  • {item.Mod.Label} ({Bytes(item.Mod.Size)})");
			}
		}

		var asIs = new List<string>();
		foreach (Item item in plan.Order)
		{
			if (item.From != Source.Download)
			{
				asIs.Add(item.Mod.Label + (item.From == Source.Cached ? " (received before)" : string.Empty));
			}
		}

		if (asIs.Count != 0)
		{
			text.Append("\n\nAlready on this machine, used as-is:");
			foreach (string label in asIs)
			{
				text.Append("\n  • ").Append(label);
			}
		}

		if (plan.Replaced.Count != 0)
		{
			text.Append("\n\nYours, replaced by the host's version for this session:");
			foreach (ModFingerprint.Mod mod in plan.Replaced)
			{
				text.Append("\n  • ").Append(mod.Label);
			}
		}

		if (plan.Suspend.Count != 0)
		{
			text.Append("\n\nYours, suspended for this session:");
			foreach (ModFingerprint.Mod mod in plan.Suspend)
			{
				text.Append("\n  • ").Append(mod.Label);
			}
		}

		if (plan.DownloadCount != 0)
		{
			text.Append($"\n\nMods contain scripts that run inside your game. These come from {whose} machine, not from a store, and are kept only for playing with {host}.");
		}

		text.Append("\n\nYour own mod list and settings are not changed and come back when the session ends.");
		text.Append($"\n\n{AcceptCaption} to continue, or {DeclineCaption} to leave the session.");
		return text.ToString();
	}

	/// <summary>The progress line shown while a package is received.</summary>
	public static string ProgressText(string hostName, ModFingerprint.Mod mod, int index, int count, long done, long total) =>
		$"Receiving {mod.Label} from {(hostName.Length == 0 ? "the host" : hostName)}… " +
		$"{Bytes(done)} / {Bytes(total)} (mod {index + 1} of {count})";

	public static string Bytes(long bytes) => bytes switch
	{
		< 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
		< 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
		_ => (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
	};

	// ---------------------------------------------------------------- the joiner's statement

	/// <summary>
	/// The joiner's phase, restated on the co-op channel beside its mods record while the sets
	/// differ: what the host waits on, and what it refuses.
	/// </summary>
	public enum Phase
	{
		None,
		Asking,
		Declined,
		Blocked,
		Fetching,
		Applying,
		Ready,
		Failed,
	}

	public const string PhasePrefix = "modsync=";

	/// <summary>Encodes a phase statement: <c>modsync=&lt;phase&gt;:&lt;detail&gt;</c>, the detail free text without newlines.</summary>
	public static string EncodePhase(Phase phase, string detail = "")
	{
		var clean = new StringBuilder(detail.Length);
		foreach (char c in detail)
		{
			clean.Append(char.IsControl(c) ? ' ' : c);
		}

		return PhasePrefix + phase.ToString().ToLowerInvariant() + ":" + clean;
	}

	public static bool TryDecodePhase(string message, out Phase phase, out string detail)
	{
		phase = Phase.None;
		detail = string.Empty;
		if (!message.StartsWith(PhasePrefix, StringComparison.Ordinal))
		{
			return false;
		}

		string rest = message[PhasePrefix.Length..];
		int colon = rest.IndexOf(':');
		string name = colon < 0 ? rest : rest[..colon];
		detail = colon < 0 ? string.Empty : rest[(colon + 1)..];
		return Enum.TryParse(name, ignoreCase: true, out phase) && phase != Phase.None;
	}

	/// <summary>Whether the host keeps the session up while the joiner is in <paramref name="phase"/>.</summary>
	public static bool HostWaits(Phase phase) => phase is Phase.Asking or Phase.Fetching or Phase.Applying or Phase.Ready;

	// ---------------------------------------------------------------- frames

	/// <summary>A frame on the transfer stream: one byte of kind, four of payload length (little-endian), the payload.</summary>
	public enum Kind : byte
	{
		/// <summary>Joiner → host: <c>&lt;short sha&gt;\t&lt;name&gt;</c>.</summary>
		Get = 1,

		/// <summary>Host → joiner: <c>&lt;name&gt;\t&lt;sha256&gt;\t&lt;size&gt;\t&lt;chunk bytes&gt;\t&lt;chunk count&gt;</c>.</summary>
		Head = 2,

		/// <summary>Host → joiner: four bytes of chunk index, then the chunk.</summary>
		Chunk = 3,

		/// <summary>Joiner → host: four bytes, the count of chunks received so far.</summary>
		Ack = 4,

		/// <summary>Host → joiner: a reason the request is not served.</summary>
		Refuse = 5,
	}

	public const int FrameHeader = 5;

	/// <summary>The largest payload a frame may carry: a chunk and its index.</summary>
	public const int MaxPayload = ChunkBytes + 4;

	public static byte[] EncodeFrame(Kind kind, ReadOnlySpan<byte> payload)
	{
		if (payload.Length > MaxPayload)
		{
			throw new ArgumentException($"a frame carries at most {MaxPayload} bytes, not {payload.Length}");
		}

		var frame = new byte[FrameHeader + payload.Length];
		frame[0] = (byte)kind;
		BitConverter.TryWriteBytes(frame.AsSpan(1, 4), (uint)payload.Length);
		payload.CopyTo(frame.AsSpan(FrameHeader));
		return frame;
	}

	public static byte[] EncodeFrame(Kind kind, string text) => EncodeFrame(kind, Encoding.UTF8.GetBytes(text));

	public static byte[] EncodeChunk(uint index, ReadOnlySpan<byte> bytes)
	{
		var payload = new byte[4 + bytes.Length];
		BitConverter.TryWriteBytes(payload.AsSpan(0, 4), index);
		bytes.CopyTo(payload.AsSpan(4));
		return EncodeFrame(Kind.Chunk, payload);
	}

	public static byte[] EncodeAck(uint received)
	{
		var payload = new byte[4];
		BitConverter.TryWriteBytes(payload, received);
		return EncodeFrame(Kind.Ack, payload);
	}

	public static string HeadText(string name, string sha256, long size) =>
		string.Join('\t', name, sha256, size.ToString(CultureInfo.InvariantCulture),
			ChunkBytes.ToString(CultureInfo.InvariantCulture), ChunkCount(size).ToString(CultureInfo.InvariantCulture));

	public static int ChunkCount(long size) => (int)((size + ChunkBytes - 1) / ChunkBytes);

	/// <summary>A decoded <see cref="Kind.Head"/>, or null when it does not read.</summary>
	public static (string Name, string Sha256, long Size, int ChunkBytes, int ChunkCount)? ParseHead(string text)
	{
		string[] fields = text.Split('\t');
		if (fields.Length != 5 || fields[0].Length == 0 || fields[1].Length != 64 ||
			!long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long size) || size < 0 ||
			!int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int chunk) || chunk <= 0 || chunk > ChunkBytes ||
			!int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 0 ||
			(long)count * chunk < size || (count != 0 && (long)(count - 1) * chunk >= size))
		{
			return null;
		}

		return (fields[0], fields[1].ToLowerInvariant(), size, chunk, count);
	}

	/// <summary>
	/// Reassembles frames from a byte stream delivered in arbitrary pieces. A payload over
	/// <see cref="MaxPayload"/> or an unknown kind poisons the reader (<see cref="Error"/>).
	/// </summary>
	public sealed class FrameReader
	{
		private readonly List<byte> _buffer = new();

		public string Error { get; private set; } = string.Empty;

		public void Feed(ReadOnlySpan<byte> bytes)
		{
			if (Error.Length == 0)
			{
				_buffer.AddRange(bytes.ToArray());
			}
		}

		/// <summary>The next complete frame, or null when none is whole yet.</summary>
		public (Kind Kind, byte[] Payload)? Next()
		{
			if (Error.Length != 0 || _buffer.Count < FrameHeader)
			{
				return null;
			}

			byte kind = _buffer[0];
			uint length = BitConverter.ToUInt32(new[] { _buffer[1], _buffer[2], _buffer[3], _buffer[4] }, 0);
			if (!Enum.IsDefined(typeof(Kind), kind) || length > MaxPayload)
			{
				Error = $"a frame of kind {kind} with {length} bytes does not read";
				return null;
			}

			if (_buffer.Count < FrameHeader + (int)length)
			{
				return null;
			}

			byte[] payload = _buffer.GetRange(FrameHeader, (int)length).ToArray();
			_buffer.RemoveRange(0, FrameHeader + (int)length);
			return ((Kind)kind, payload);
		}
	}

	/// <summary>
	/// One package received: chunks taken in order into a buffer of the stated size; the bytes
	/// are accepted only when their SHA-256 is the one the host stated AND it matches the short
	/// hash the host's set carried.
	/// </summary>
	public sealed class Assembler
	{
		public string Name { get; }

		public string Sha256 { get; }

		public long Size { get; }

		public int ChunkSize { get; }

		public int ChunkCount { get; }

		public int Received { get; private set; }

		public long BytesReceived { get; private set; }

		private readonly byte[] _bytes;

		public Assembler(string name, string sha256, long size, int chunkSize, int chunkCount)
		{
			Name = name;
			Sha256 = sha256.ToLowerInvariant();
			Size = size;
			ChunkSize = chunkSize;
			ChunkCount = chunkCount;
			_bytes = new byte[size];
		}

		public bool Complete => Received == ChunkCount;

		/// <summary>Takes chunk <paramref name="index"/>; false for one out of order, too long or repeated.</summary>
		public bool Take(uint index, ReadOnlySpan<byte> chunk)
		{
			if (index != (uint)Received)
			{
				return index < (uint)Received;  // a repeat is harmless; a skip is not
			}

			long offset = (long)index * ChunkSize;
			long expected = Math.Min(ChunkSize, Size - offset);
			if (chunk.Length != expected)
			{
				return false;
			}

			chunk.CopyTo(_bytes.AsSpan((int)offset, chunk.Length));
			++Received;
			BytesReceived += chunk.Length;
			return true;
		}

		/// <summary>The bytes, when complete and their hash is the stated one (and agrees with <paramref name="shortSha"/>); else null.</summary>
		public byte[]? Verify(string shortSha)
		{
			if (!Complete)
			{
				return null;
			}

			string actual = Convert.ToHexString(SHA256.HashData(_bytes)).ToLowerInvariant();
			return actual == Sha256 && SameBytes(actual, shortSha) ? _bytes : null;
		}
	}

	/// <summary>
	/// One package sent: the head, then chunks in order, at most <see cref="WindowChunks"/> beyond
	/// the receiver's last acknowledged count.
	/// </summary>
	public sealed class Feeder
	{
		private readonly byte[] _bytes;

		public string Name { get; }

		public string Sha256 { get; }

		public int ChunkCount { get; }

		public int Sent { get; private set; }

		public uint Acked { get; private set; }

		public Feeder(string name, string sha256, byte[] bytes)
		{
			Name = name;
			Sha256 = sha256;
			_bytes = bytes;
			ChunkCount = ModSync.ChunkCount(bytes.LongLength);
		}

		public byte[] Head() => EncodeFrame(Kind.Head, HeadText(Name, Sha256, _bytes.LongLength));

		public void Ack(uint received) => Acked = Math.Max(Acked, Math.Min(received, (uint)ChunkCount));

		public bool Done => Acked >= (uint)ChunkCount;

		/// <summary>The chunk frames that fit the window now, in order.</summary>
		public List<byte[]> Next()
		{
			var frames = new List<byte[]>();
			while (Sent < ChunkCount && Sent < (int)Acked + WindowChunks)
			{
				long offset = (long)Sent * ChunkBytes;
				int length = (int)Math.Min(ChunkBytes, _bytes.LongLength - offset);
				frames.Add(EncodeChunk((uint)Sent, _bytes.AsSpan((int)offset, length)));
				++Sent;
			}

			return frames;
		}
	}
}

/// <summary>
/// The session cache: packages received from a host, under Godot's <c>user://</c>, keyed by
/// their SHA-256 — never the player's mods folder, never the repo, never shared on. Reused the
/// next time the same host's set names them; the oldest are evicted past <see cref="KeepBytes"/>.
/// </summary>
public static class ModSyncCache
{
	/// <summary>The cache's folder under <c>user://</c>.</summary>
	public const string Folder = "mods-sync";

	/// <summary>How much the cache keeps across sessions.</summary>
	public const long KeepBytes = 256L * 1024 * 1024;

	public const string Extension = ".f2mod";

	public static string PathFor(string cacheDir, string sha256) =>
		Path.Combine(cacheDir, sha256.ToLowerInvariant() + Extension);

	/// <summary>Every package held, hashed by its file name, named by the package's own metadata.</summary>
	public static List<ModSync.Item> List(string cacheDir)
	{
		var items = new List<ModSync.Item>();
		if (string.IsNullOrEmpty(cacheDir) || !Directory.Exists(cacheDir))
		{
			return items;
		}

		foreach (string file in Directory.GetFiles(cacheDir, "*" + Extension))
		{
			string sha = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
			if (sha.Length != 64 || !F2ModPackage.TryReadMetadata(file, out ModMetadata meta, out _))
			{
				continue;
			}

			long size = 0;
			try
			{
				size = new FileInfo(file).Length;
			}
			catch (Exception)
			{
			}

			items.Add(new ModSync.Item(new ModFingerprint.Mod(meta.Title, meta.Title, meta.Version, sha, size), ModSync.Source.Cached, file));
		}

		return items;
	}

	/// <summary>Writes verified bytes under their hash; false with the reason when the disk refuses.</summary>
	public static bool Store(string cacheDir, string sha256, byte[] bytes, out string error)
	{
		error = string.Empty;
		try
		{
			Directory.CreateDirectory(cacheDir);
			string path = PathFor(cacheDir, sha256);
			string temp = path + ".part";
			File.WriteAllBytes(temp, bytes);
			File.Move(temp, path, overwrite: true);
			return true;
		}
		catch (Exception exception)
		{
			error = exception.Message;
			return false;
		}
	}

	/// <summary>Drops the oldest packages until the cache holds at most <paramref name="keepBytes"/>, sparing <paramref name="inUse"/>.</summary>
	public static int Evict(string cacheDir, long keepBytes, IReadOnlySet<string> inUse)
	{
		if (string.IsNullOrEmpty(cacheDir) || !Directory.Exists(cacheDir))
		{
			return 0;
		}

		var files = new List<FileInfo>();
		long total = 0;
		foreach (string file in Directory.GetFiles(cacheDir, "*" + Extension))
		{
			var info = new FileInfo(file);
			files.Add(info);
			total += info.Length;
		}

		files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
		int evicted = 0;
		foreach (FileInfo info in files)
		{
			if (total <= keepBytes)
			{
				break;
			}

			string sha = Path.GetFileNameWithoutExtension(info.Name).ToLowerInvariant();
			if (inUse.Contains(sha))
			{
				continue;
			}

			try
			{
				info.Delete();
				total -= info.Length;
				++evicted;
			}
			catch (Exception)
			{
			}
		}

		return evicted;
	}
}
