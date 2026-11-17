using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Refii.Titles.Fable2;

using Refii.Host;

/// <summary>
/// The HeroState protocol: one versioned record of a player's hero, serialized to bytes, carried
/// over the co-op channel as fragments, acknowledged once the receiver holds it whole.
/// </summary>
/// <remarks>
/// The record (`HST1`, little-endian) is the whole <see cref="CoopHeroKit.Kit"/>: identity, looks,
/// outfit, inventory, equipped records, stats, the dog, then the looks extension
/// (<see cref="LooksMagic"/>): worn dyes and scars. The pump takes the same bytes, base64, as
/// <c>HeroState:&lt;player&gt;:&lt;base64&gt;</c>.
///
/// Wire grammar, one datagram each, on <see cref="HostBridge.CoopSend"/>:
/// <list type="bullet">
/// <item><c>hs.frag=&lt;version&gt;/&lt;index&gt;/&lt;total&gt;:&lt;base64 slice&gt;</c> — the base64 of
/// the record, cut into <see cref="SliceChars"/>-character slices.</item>
/// <item><c>hs.ack=&lt;version&gt;</c> — the receiver's acknowledgement, sent once that version is
/// complete and decoded; the record is applied whenever its body stands.</item>
/// </list>
/// Versions are monotonic for the life of the process and bump whenever the serialized record
/// changes. The sender resends the latest unacked version every <see cref="ResendSeconds"/> and
/// stops on its ack; an older unacked version is abandoned the moment a newer one exists. The
/// receiver keeps one reassembly buffer, for the newest version seen; fragments of an older
/// version are dropped.
/// </remarks>
public static class CoopHeroState
{
	/// <summary>"HST1".</summary>
	public const uint Magic = 0x31545348u;

	/// <summary>
	/// "HSL1" — the looks extension after the dog: the dye on each worn item and the scars. An
	/// older reader stops at the dog name and never sees it; this reader takes a record without it
	/// as no dyes and no scars. The same bytes reach the pump (DispatchHeroState).
	/// </summary>
	public const uint LooksMagic = 0x314C5348u;

	/// <summary>Longest name carried, in UTF-8 bytes.</summary>
	public const int NameLimit = 48;

	public const string FragmentPrefix = "hs.frag=";

	public const string AckPrefix = "hs.ack=";

	/// <summary>One datagram's ceiling on the channel.</summary>
	public const int DatagramLimit = 1100;

	/// <summary>
	/// Base64 characters per fragment. The head is at most 29 bytes
	/// (<c>hs.frag=</c> + u32 + '/' + u16 + '/' + u16 + ':'), and base64 is ASCII, so 1024 + 29
	/// stays under <see cref="DatagramLimit"/>.
	/// </summary>
	public const int SliceChars = 1024;

	/// <summary>Seconds between resends of an unacked version.</summary>
	public const double ResendSeconds = 1.0;

	// ---------------------------------------------------------------- record

	/// <summary>The record for a kit, stamped with <paramref name="version"/>.</summary>
	public static byte[] Serialize(in CoopHeroKit.Kit kit, uint version)
	{
		using var stream = new MemoryStream(256);
		using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

		w.Write(Magic);
		w.Write(version);

		byte flags = 0;
		if (kit.Female) flags |= 1;
		if (CoopHeroKit.HasLooks(kit)) flags |= 2;
		if (kit.Known) flags |= 4;
		w.Write(flags);

		byte[] name = NameBytes(kit.Name ?? string.Empty);
		w.Write((byte)name.Length);
		w.Write(name);

		w.Write(kit.Face);
		w.Write(kit.Fat); w.Write(kit.Height); w.Write(kit.Tan);
		w.Write(kit.Strength); w.Write(kit.Skill); w.Write(kit.Will);
		w.Write(kit.Morality); w.Write(kit.Purity);
		w.Write(kit.Renown); w.Write(kit.Money);

		int[] ranks = kit.AbilityLevels ?? Array.Empty<int>();
		w.Write((byte)Math.Min(ranks.Length, byte.MaxValue));
		for (int i = 0; i < ranks.Length && i < byte.MaxValue; ++i) w.Write(ranks[i]);

		float[] pools = kit.Experience ?? Array.Empty<float>();
		w.Write((byte)Math.Min(pools.Length, byte.MaxValue));
		for (int i = 0; i < pools.Length && i < byte.MaxValue; ++i) w.Write(pools[i]);

		float[] totals = kit.TotalExperience ?? Array.Empty<float>();
		w.Write((byte)Math.Min(totals.Length, byte.MaxValue));
		for (int i = 0; i < totals.Length && i < byte.MaxValue; ++i) w.Write(totals[i]);

		w.Write(kit.MeleeId); w.Write(kit.RangedId);

		uint[] worn = kit.Worn ?? Array.Empty<uint>();
		w.Write((ushort)Math.Min(worn.Length, ushort.MaxValue));
		for (int i = 0; i < worn.Length && i < ushort.MaxValue; ++i) w.Write(worn[i]);

		(uint Record, int Count)[] inventory = kit.Inventory ?? Array.Empty<(uint, int)>();
		w.Write((ushort)Math.Min(inventory.Length, ushort.MaxValue));
		for (int i = 0; i < inventory.Length && i < ushort.MaxValue; ++i)
		{
			w.Write(inventory[i].Record);
			w.Write((ushort)Math.Clamp(inventory[i].Count, 0, ushort.MaxValue));
		}

		// The dog: definition (0 none), the two levels, then the tricks by name.
		w.Write(kit.DogDefinition);
		w.Write((byte)Math.Clamp(kit.DogSearching, 0, byte.MaxValue));
		w.Write((byte)Math.Clamp(kit.DogCombat, 0, byte.MaxValue));
		string[] tricks = kit.DogTricks ?? Array.Empty<string>();
		int trickCount = Math.Min(tricks.Length, byte.MaxValue);
		w.Write((byte)trickCount);
		for (int i = 0; i < trickCount; ++i)
		{
			byte[] trick = Encoding.UTF8.GetBytes(tricks[i]);
			int length = Math.Min(trick.Length, byte.MaxValue);
			w.Write((byte)length);
			w.Write(trick, 0, length);
		}

		uint[] dogWorn = kit.DogWorn ?? Array.Empty<uint>();
		int dogWornCount = Math.Min(dogWorn.Length, byte.MaxValue);
		w.Write((byte)dogWornCount);
		for (int i = 0; i < dogWornCount; ++i) w.Write(dogWorn[i]);

		byte[] dogName = NameBytes(kit.DogName ?? string.Empty);
		w.Write((byte)dogName.Length);
		w.Write(dogName);

		// The looks extension: u16 dyed worn items {u32 record, u8 slots (bit 0 slot 1, bit 1
		// slot 2), per set bit u8 red, green, blue, f32 brightness}, then u8 scars {u32 area,
		// u32 record, u8 permanent}.
		w.Write(LooksMagic);
		Fable2Bridge.WornDye[] dyes = kit.WornDyes ?? Array.Empty<Fable2Bridge.WornDye>();
		int dyeCount = Math.Min(dyes.Length, ushort.MaxValue);
		w.Write((ushort)dyeCount);
		for (int i = 0; i < dyeCount; ++i)
		{
			Fable2Bridge.WornDye dye = dyes[i];
			w.Write(dye.Record);
			w.Write((byte)((dye.Primary is null ? 0 : 1) | (dye.Secondary is null ? 0 : 2)));
			foreach (Fable2Bridge.SlotDye? slot in new[] { dye.Primary, dye.Secondary })
			{
				if (slot is { } value)
				{
					w.Write(value.Red); w.Write(value.Green); w.Write(value.Blue);
					w.Write(value.Brightness);
				}
			}
		}

		Fable2Bridge.Scar[] scars = kit.Scars ?? Array.Empty<Fable2Bridge.Scar>();
		int scarCount = Math.Min(scars.Length, byte.MaxValue);
		w.Write((byte)scarCount);
		for (int i = 0; i < scarCount; ++i)
		{
			w.Write(scars[i].Area);
			w.Write(scars[i].Record);
			w.Write((byte)(scars[i].Permanent ? 1 : 0));
		}

		w.Flush();
		return stream.ToArray();
	}

	/// <summary>The kit and version in a record, or null for a short buffer or a wrong magic.</summary>
	public static (CoopHeroKit.Kit Kit, uint Version)? Deserialize(byte[] payload)
	{
		if (payload is null || payload.Length < 8)
		{
			return null;
		}

		try
		{
			using var stream = new MemoryStream(payload, writable: false);
			using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

			if (r.ReadUInt32() != Magic)
			{
				return null;
			}

			uint version = r.ReadUInt32();
			byte flags = r.ReadByte();
			bool female = (flags & 1) != 0;
			bool hasLooks = (flags & 2) != 0;
			bool known = (flags & 4) != 0;

			int nameLen = r.ReadByte();
			string name = Encoding.UTF8.GetString(r.ReadBytes(nameLen), 0, nameLen);

			uint face = r.ReadUInt32();
			float fat = r.ReadSingle(), height = r.ReadSingle(), tan = r.ReadSingle();
			float strength = r.ReadSingle(), skill = r.ReadSingle(), will = r.ReadSingle();
			float morality = r.ReadSingle(), purity = r.ReadSingle();
			int renown = r.ReadInt32(), money = r.ReadInt32();

			int rankCount = r.ReadByte();
			var ranks = new int[rankCount];
			for (int i = 0; i < rankCount; ++i) ranks[i] = r.ReadInt32();

			int expCount = r.ReadByte();
			var pools = new float[expCount];
			for (int i = 0; i < expCount; ++i) pools[i] = r.ReadSingle();

			int totalCount = r.ReadByte();
			var totals = new float[totalCount];
			for (int i = 0; i < totalCount; ++i) totals[i] = r.ReadSingle();

			uint melee = r.ReadUInt32(), ranged = r.ReadUInt32();

			int wornCount = r.ReadUInt16();
			var worn = new uint[wornCount];
			for (int i = 0; i < wornCount; ++i) worn[i] = r.ReadUInt32();

			int invCount = r.ReadUInt16();
			var inventory = new (uint, int)[invCount];
			for (int i = 0; i < invCount; ++i)
			{
				uint record = r.ReadUInt32();
				int count = r.ReadUInt16();
				inventory[i] = (record, count);
			}

			uint dogDefinition = r.ReadUInt32();
			int dogSearching = r.ReadByte();
			int dogCombat = r.ReadByte();
			var dogTricks = new string[r.ReadByte()];
			for (int i = 0; i < dogTricks.Length; ++i)
			{
				int length = r.ReadByte();
				dogTricks[i] = Encoding.UTF8.GetString(r.ReadBytes(length), 0, length);
			}

			var dogWorn = new uint[r.ReadByte()];
			for (int i = 0; i < dogWorn.Length; ++i) dogWorn[i] = r.ReadUInt32();

			int dogNameLength = r.ReadByte();
			string dogName = Encoding.UTF8.GetString(r.ReadBytes(dogNameLength), 0, dogNameLength);

			(Fable2Bridge.WornDye[] Dyes, Fable2Bridge.Scar[] Scars) looks = ReadLooks(r);

			var kit = new CoopHeroKit.Kit(
				Known: known,
				Female: female,
				Morality: morality,
				Purity: purity,
				Renown: renown,
				// A record without looks states strength −1, which is what HasLooks reads.
				Strength: hasLooks ? strength : -1f,
				Skill: skill,
				Will: will,
				AbilityLevels: ranks,
				Worn: worn,
				Inventory: inventory,
				Name: name,
				Money: money,
				Experience: pools,
				TotalExperience: totals,
				Face: face,
				Fat: fat,
				Height: height,
				Tan: tan,
				MeleeId: melee,
				RangedId: ranged,
				DogDefinition: dogDefinition,
				DogSearching: dogSearching,
				DogCombat: dogCombat,
				DogTricks: dogTricks,
				DogWorn: dogWorn,
				DogName: dogName,
				WornDyes: looks.Dyes,
				Scars: looks.Scars);
			return (kit, version);
		}
		catch (EndOfStreamException)
		{
			return null;
		}
		catch (IOException)
		{
			return null;
		}
	}

	/// <summary>
	/// The looks extension at the reader's position, or none: a record from a peer that predates
	/// it ends at the dog name, and one whose extension is cut short or carries another magic
	/// keeps the rest of the record and drops only the extension.
	/// </summary>
	private static (Fable2Bridge.WornDye[] Dyes, Fable2Bridge.Scar[] Scars) ReadLooks(BinaryReader r)
	{
		var none = (Array.Empty<Fable2Bridge.WornDye>(), Array.Empty<Fable2Bridge.Scar>());
		Stream stream = r.BaseStream;
		if (stream.Length - stream.Position < 4 || r.ReadUInt32() != LooksMagic)
		{
			return none;
		}

		try
		{
			var dyes = new List<Fable2Bridge.WornDye>();
			int dyeCount = r.ReadUInt16();
			for (int i = 0; i < dyeCount; ++i)
			{
				uint record = r.ReadUInt32();
				byte slots = r.ReadByte();
				Fable2Bridge.SlotDye? primary = (slots & 1) != 0 ? ReadSlot(r) : null;
				Fable2Bridge.SlotDye? secondary = (slots & 2) != 0 ? ReadSlot(r) : null;
				if (record != 0u && (primary is not null || secondary is not null))
				{
					dyes.Add(new Fable2Bridge.WornDye(record, primary, secondary));
				}
			}

			var scars = new List<Fable2Bridge.Scar>();
			int scarCount = r.ReadByte();
			for (int i = 0; i < scarCount; ++i)
			{
				uint area = r.ReadUInt32();
				uint record = r.ReadUInt32();
				bool permanent = r.ReadByte() != 0;
				if (area != 0u && record != 0u)
				{
					scars.Add(new Fable2Bridge.Scar(area, record, permanent));
				}
			}

			return (dyes.ToArray(), scars.ToArray());
		}
		catch (EndOfStreamException)
		{
			return none;
		}

		static Fable2Bridge.SlotDye? ReadSlot(BinaryReader r)
		{
			byte red = r.ReadByte(), green = r.ReadByte(), blue = r.ReadByte();
			float brightness = r.ReadSingle();
			return float.IsFinite(brightness) ? new Fable2Bridge.SlotDye(red, green, blue, brightness) : null;
		}
	}

	/// <summary>UTF-8 of a name, cut to <see cref="NameLimit"/> bytes on a scalar boundary.</summary>
	private static byte[] NameBytes(string name)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(name);
		if (bytes.Length <= NameLimit)
		{
			return bytes;
		}

		// A continuation byte is 10xxxxxx; the cut goes before the first byte of the scalar
		// that straddles the limit. Surrogate pairs are one four-byte scalar in UTF-8, so a
		// scalar boundary is a char boundary too.
		int cut = NameLimit;
		while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
		{
			--cut;
		}

		return bytes.AsSpan(0, cut).ToArray();
	}

	// ---------------------------------------------------------------- sender

	/// <summary>The local record's version; 0 until the first send. Monotonic for the process.</summary>
	public static uint Version { get; private set; }

	/// <summary>The newest version the peer has acknowledged.</summary>
	public static uint AckedVersion { get; private set; }

	/// <summary>Whether the peer holds the local record as it stands: sent and acknowledged.</summary>
	public static bool Delivered => Version != 0u && AckedVersion >= Version;

	/// <summary>Fragments in the version being sent; 0 before the first send.</summary>
	public static int SendingFragments => _fragments.Length;

	/// <summary>The base64 of the local record at <see cref="Version"/>, as the pump takes it. Null before the first send.</summary>
	public static string? LocalBase64 { get; private set; }

	/// <summary>The serialized local record, stamped version 0 — what a change is measured against.</summary>
	private static byte[]? _local;

	private static string[] _fragments = Array.Empty<string>();

	private static double _sinceSend;

	/// <summary>Whether the last send was refused by the bridge, reported on each change of outcome.</summary>
	private static bool? _refusedReported;

	/// <summary>The last send was refused in part or whole.</summary>
	private static bool _refused;

	/// <summary>
	/// One tick of the sender, <paramref name="delta"/> seconds after the last. A kit whose record
	/// differs from the last sent one bumps the version and goes out as fragments; otherwise the
	/// latest version is resent every <see cref="ResendSeconds"/> until the peer acks it.
	/// </summary>
	public static void Send(in CoopHeroKit.Kit kit, double delta)
	{
		byte[] bytes = Serialize(kit, 0u);
		bool changed = _local is null || !bytes.AsSpan().SequenceEqual(_local);
		if (changed)
		{
			_local = bytes;
			++Version;
			byte[] stamped = (byte[])bytes.Clone();
			BitConverter.TryWriteBytes(stamped.AsSpan(4, 4), Version);
			LocalBase64 = Convert.ToBase64String(stamped);
			_fragments = Fragment(Version, LocalBase64);
			_sinceSend = 0.0;
			int sent = SendFragments();
			Godot.GD.Print($"[hero-state] v{Version} sent: {kit.Worn?.Length ?? 0} worn, " +
				$"{kit.Inventory?.Length ?? 0} records, {_fragments.Length} fragments" +
				$"{(sent == _fragments.Length ? string.Empty : $" ({sent} accepted by the bridge)")}");
			return;
		}

		if (Version == 0u || AckedVersion >= Version)
		{
			return;
		}

		_sinceSend += delta;
		if (_sinceSend < ResendSeconds && !_refused)
		{
			return;
		}

		_sinceSend = 0.0;
		int resent = SendFragments();
		if (resent != 0)
		{
			Godot.GD.Print($"[hero-state] v{Version} resent: {resent}/{_fragments.Length} fragments " +
				$"(peer acked v{AckedVersion})");
		}
	}

	/// <summary>Sends every fragment of the current version; returns how many the bridge took.</summary>
	private static int SendFragments()
	{
		int sent = 0;
		foreach (string fragment in _fragments)
		{
			if (HostBridge.CoopSend(fragment))
			{
				++sent;
			}
		}

		bool refused = sent != _fragments.Length;

		// A send the bridge refused (no peer yet) is retried on the very next tick, not a whole
		// resend interval later: the record should cross the moment the peer is there.
		_refused = refused;
		if (_refusedReported != refused)
		{
			_refusedReported = refused;
			Godot.GD.Print(refused
				? "[hero-state] the bridge refused a send (no peer yet or no net)"
				: "[hero-state] the bridge is taking sends");
		}

		return sent;
	}

	private static string[] Fragment(uint version, string base64)
	{
		int total = Math.Max(1, (base64.Length + SliceChars - 1) / SliceChars);
		var fragments = new string[total];
		for (int i = 0; i < total; ++i)
		{
			int at = i * SliceChars;
			string slice = base64.Substring(at, Math.Min(SliceChars, base64.Length - at));
			fragments[i] = FragmentPrefix +
				version.ToString(CultureInfo.InvariantCulture) + "/" +
				i.ToString(CultureInfo.InvariantCulture) + "/" +
				total.ToString(CultureInfo.InvariantCulture) + ":" + slice;
		}

		return fragments;
	}

	/// <summary>Takes a peer's <c>hs.ack=</c>. Returns false for anything else.</summary>
	public static bool Acknowledge(string message)
	{
		if (message is null || !message.StartsWith(AckPrefix, StringComparison.Ordinal) ||
			!uint.TryParse(message.AsSpan(AckPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint version))
		{
			return false;
		}

		if (version > AckedVersion)
		{
			AckedVersion = version;
			Godot.GD.Print($"[hero-state] peer acked v{version}" +
				$"{(version == Version ? string.Empty : $" (local is v{Version})")}");
		}

		return true;
	}

	// ---------------------------------------------------------------- receiver

	/// <summary>The newest complete version from the peer; 0 until one lands.</summary>
	public static uint ReceivedVersion { get; private set; }

	/// <summary>The base64 of the received record at <see cref="ReceivedVersion"/>, as the pump takes it.</summary>
	public static string? ReceivedBase64 { get; private set; }

	/// <summary>The version this side last acknowledged to the peer.</summary>
	public static uint AckedToPeer { get; private set; }

	/// <summary>
	/// Set when fragments of the already-complete version arrive again — the peer did not get the
	/// ack — and cleared by <see cref="SendAck"/>.
	/// </summary>
	public static bool AckDue { get; private set; }

	private static uint _rxVersion;

	private static string?[] _rxParts = Array.Empty<string?>();

	private static int _rxHave;

	/// <summary>
	/// The peer's record part-way across: fragments held and expected, or (0, 0) when nothing is
	/// arriving. A version completes and decodes the moment the last one lands.
	/// </summary>
	public static (int Have, int Total) Receiving => (_rxHave, _rxParts.Length);

	/// <summary>
	/// Takes one <c>hs.frag=</c>. Returns true the moment a NEW version is complete and decoded —
	/// <see cref="CoopHeroKit.Remote"/> then holds it. Returns false for anything else, a
	/// malformed fragment and an older version included.
	/// </summary>
	public static bool Receive(string message)
	{
		if (message is null || !message.StartsWith(FragmentPrefix, StringComparison.Ordinal))
		{
			return false;
		}

		ReadOnlySpan<char> head = message.AsSpan(FragmentPrefix.Length);
		int colon = head.IndexOf(':');
		if (colon < 0)
		{
			return false;
		}

		ReadOnlySpan<char> ids = head[..colon];
		int slash1 = ids.IndexOf('/');
		int slash2 = slash1 < 0 ? -1 : ids[(slash1 + 1)..].IndexOf('/');
		if (slash1 < 0 || slash2 < 0 ||
			!uint.TryParse(ids[..slash1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint version) ||
			!int.TryParse(ids[(slash1 + 1)..(slash1 + 1 + slash2)], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ||
			!int.TryParse(ids[(slash1 + 2 + slash2)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int total) ||
			total <= 0 || index < 0 || index >= total || version == 0u)
		{
			return false;
		}

		if (version <= ReceivedVersion)
		{
			// Already complete: the peer is resending because our ack did not reach it.
			if (version == ReceivedVersion)
			{
				AckDue = true;
			}

			return false;
		}

		if (version < _rxVersion)
		{
			return false;
		}

		if (version != _rxVersion || _rxParts.Length != total)
		{
			_rxVersion = version;
			_rxParts = new string?[total];
			_rxHave = 0;
		}

		if (_rxParts[index] is null)
		{
			++_rxHave;
		}

		_rxParts[index] = head[(colon + 1)..].ToString();
		if (_rxHave != total)
		{
			return false;
		}

		string base64 = string.Concat(_rxParts);
		byte[] payload;
		try
		{
			payload = Convert.FromBase64String(base64);
		}
		catch (FormatException)
		{
			Godot.GD.Print($"[hero-state] v{version} REJECTED: fragments did not form base64");
			_rxHave = 0;
			_rxParts = Array.Empty<string?>();
			return false;
		}

		(CoopHeroKit.Kit Kit, uint Version)? decoded = Deserialize(payload);
		if (decoded is not { } record || record.Version != version)
		{
			Godot.GD.Print($"[hero-state] v{version} REJECTED: {payload.Length} bytes " +
				(decoded is { } other ? $"carry v{other.Version}" : "did not decode"));
			_rxHave = 0;
			_rxParts = Array.Empty<string?>();
			return false;
		}

		ReceivedVersion = version;
		ReceivedBase64 = base64;
		AckDue = false;
		CoopHeroKit.Remote = record.Kit;
		_rxParts = Array.Empty<string?>();
		_rxHave = 0;

		CoopHeroKit.Kit kit = record.Kit;
		Godot.GD.Print($"[hero-state] v{version} received: '{kit.Name}', hero {(kit.Known ? "yes" : "no")}, " +
			$"{(kit.Female ? "female" : "male")}, {kit.Worn.Length} worn ({kit.WornDyes?.Length ?? 0} dyed), " +
			$"{kit.Scars?.Length ?? 0} scar(s), {kit.Inventory.Length} records, " +
			$"melee 0x{kit.MeleeId:X8}, ranged 0x{kit.RangedId:X8}, {payload.Length} bytes in {total} fragments");
		return true;
	}

	/// <summary>Acknowledges <see cref="ReceivedVersion"/> to the peer. Call once that version is applied.</summary>
	public static void SendAck()
	{
		if (ReceivedVersion == 0u || !HostBridge.CoopSend(AckPrefix + ReceivedVersion.ToString(CultureInfo.InvariantCulture)))
		{
			return;
		}

		bool again = AckedToPeer == ReceivedVersion;
		AckedToPeer = ReceivedVersion;
		AckDue = false;
		Godot.GD.Print($"[hero-state] acked v{ReceivedVersion} to the peer{(again ? " (again)" : string.Empty)}");
	}

	/// <summary>
	/// Drops everything that belongs to a session: acks both ways, the reassembly buffer, the
	/// received record and the "last sent" bytes, so the next tick sends the local record afresh
	/// under a new version. <see cref="Version"/> itself is never wound back.
	/// </summary>
	public static void Reset()
	{
		AckedVersion = 0u;
		_local = null;
		_fragments = Array.Empty<string>();
		_sinceSend = 0.0;
		_refusedReported = null;
		_refused = false;
		LocalBase64 = null;
		ReceivedVersion = 0u;
		ReceivedBase64 = null;
		AckedToPeer = 0u;
		AckDue = false;
		_rxVersion = 0u;
		_rxParts = Array.Empty<string?>();
		_rxHave = 0;
	}
}
