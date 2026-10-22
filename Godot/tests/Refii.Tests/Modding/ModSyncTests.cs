namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;

/// <summary>
/// Co-op mod sync's pure parts: the plan a joiner makes from the host's set (use, download,
/// suspend, block), the consent text, the phase statements, and the transfer's framing with its
/// reassembly and hash check. In-memory only — no file, no stream, no Godot type.
/// </summary>
[TestSuite]
public class ModSyncTests
{
	private static readonly string ShaA = new('a', 64);
	private static readonly string ShaB = new('b', 64);
	private static readonly string ShaC = new('c', 64);
	private static readonly string ShaD = new('d', 64);

	private static ModFingerprint.Mod Souls => new("souls-combat", "Souls Combat", "0.1.0", ShaA, 5 * 1024 * 1024);

	private static ModFingerprint.Mod MyMod => new("my-mod", "My Mod", "1.2", ShaB, 300 * 1024);

	private static ModFingerprint.Mod HdUi => new("hd-ui", "HD UI", "2.0", ShaC, 40 * 1024 * 1024);

	private static ModFingerprint.Mod Hard => new("hard-mod", "Hard Mod", "1.0", ShaD, 1024, Native: true);

	private static ModFingerprint Set(params ModFingerprint.Mod[] mods) => ModFingerprint.From(2, mods);

	/// <summary>The host's set as the joiner receives it: short hashes, from the wire.</summary>
	private static ModFingerprint Wire(params ModFingerprint.Mod[] mods)
	{
		AssertThat(ModFingerprint.TryDecode(Set(mods).Encode(), out ModFingerprint? decoded)).IsTrue();
		return decoded!;
	}

	private static ModSync.Item Installed(ModFingerprint.Mod mod) =>
		new(mod, ModSync.Source.Installed, "/mods/" + mod.Name + ".f2mod");

	private static ModSync.Item Cached(ModFingerprint.Mod mod) =>
		new(mod with { Name = mod.Title }, ModSync.Source.Cached, "/cache/" + mod.Sha256 + ".f2mod");

	private static readonly List<ModSync.Item> None = new();

	// ---------------------------------------------------------------- the wire's new fields

	[TestCase]
	public void TestWireCarriesSizeAndNativeAndReadsOldEntries()
	{
		ModFingerprint host = Wire(Souls, Hard);
		AssertThat(host.Mods[0].Size).IsEqual(5L * 1024 * 1024);
		AssertThat(host.Mods[0].Native).IsFalse();
		AssertThat(host.Mods[1].Native).IsTrue();
		AssertThat(host.Digest()).IsEqual(Set(Souls, Hard).Digest());

		// A build before sizes were stated: four fields, still read, size 0 and not native.
		AssertThat(ModFingerprint.TryDecode("mods=abc/2/1:souls-combat\t\t0.1.0\taaaaaaaaaaaa", out ModFingerprint? old)).IsTrue();
		AssertThat(old!.Mods[0].Size).IsEqual(0L);
		AssertThat(old.Mods[0].Native).IsFalse();

		AssertThat(ModFingerprint.TryDecode("mods=abc/2/1:a\tb\tc\td\te", out _)).IsFalse();
		AssertThat(ModFingerprint.TryDecode("mods=abc/2/1:a\tb\tc\td\t-1\t0", out _)).IsFalse();
		AssertThat(ModFingerprint.TryDecode("mods=abc/2/1:a\tb\tc\td\t5\t2", out _)).IsFalse();
	}

	// ---------------------------------------------------------------- the plan

	[TestCase]
	public void TestPlanUsesDownloadsAndSuspendsInHostOrder()
	{
		ModFingerprint host = Wire(Souls, HdUi);
		ModFingerprint own = Set(MyMod, Souls);
		ModSync.Plan plan = ModSync.Resolve(host, own, new List<ModSync.Item> { Installed(Souls), Installed(MyMod) }, None);

		AssertThat(plan.Ok).IsTrue();
		AssertThat(plan.Order.Count).IsEqual(2);
		AssertThat(plan.Order[0].Mod.Name).IsEqual("souls-combat");
		AssertThat(plan.Order[0].From).IsEqual(ModSync.Source.Installed);
		AssertThat(plan.Order[0].Path).IsEqual("/mods/souls-combat.f2mod");
		AssertThat(plan.Order[0].Mod.Sha256).IsEqual(ShaA);  // the full local hash, not the wire's short one
		AssertThat(plan.Order[1].Mod.Name).IsEqual("hd-ui");
		AssertThat(plan.Order[1].From).IsEqual(ModSync.Source.Download);
		AssertThat(plan.DownloadCount).IsEqual(1);
		AssertThat(plan.DownloadBytes).IsEqual(40L * 1024 * 1024);
		AssertThat(plan.Suspend.Count).IsEqual(1);
		AssertThat(plan.Suspend[0].Name).IsEqual("my-mod");
		AssertThat(plan.Replaced.Count).IsEqual(0);
		AssertThat(plan.HostDigest).IsEqual(Set(Souls, HdUi).Digest());
	}

	[TestCase]
	public void TestPlanHostWithoutModsSuspendsEverything()
	{
		ModSync.Plan plan = ModSync.Resolve(Wire(), Set(Souls, MyMod), new List<ModSync.Item> { Installed(Souls), Installed(MyMod) }, None);
		AssertThat(plan.Ok).IsTrue();
		AssertThat(plan.Order.Count).IsEqual(0);
		AssertThat(plan.Suspend.Count).IsEqual(2);
		AssertThat(plan.Trivial).IsFalse();
		AssertThat(plan.ToSession("/cache").Count).IsEqual(0);
	}

	[TestCase]
	public void TestPlanTakesDisabledInstallAndCacheWithoutDownloading()
	{
		// Souls is installed but disabled here; HD UI was received from this host before.
		ModFingerprint host = Wire(Souls, HdUi);
		ModSync.Plan plan = ModSync.Resolve(host, Set(), new List<ModSync.Item> { Installed(Souls) }, new List<ModSync.Item> { Cached(HdUi) });

		AssertThat(plan.Ok).IsTrue();
		AssertThat(plan.DownloadCount).IsEqual(0);
		AssertThat(plan.Order[0].From).IsEqual(ModSync.Source.Installed);
		AssertThat(plan.Order[1].From).IsEqual(ModSync.Source.Cached);
		AssertThat(plan.Order[1].Mod.Name).IsEqual("hd-ui");  // the host's load-order name, not the cache's title
		AssertThat(plan.Order[1].Path).IsEqual("/cache/" + ShaC + ".f2mod");

		List<ModSession.Entry> set = plan.ToSession("/cache");
		AssertThat(set[0].PackagePath).IsEqual("/mods/souls-combat.f2mod");
		AssertThat(set[1].PackagePath).IsEqual("/cache/" + ShaC + ".f2mod");
		AssertThat(set[1].Name).IsEqual("hd-ui");
	}

	[TestCase]
	public void TestPlanReplacesAnotherBuildOfTheSameMod()
	{
		ModFingerprint.Mod newer = Souls with { Version = "0.2.0", Sha256 = ShaD };
		ModSync.Plan plan = ModSync.Resolve(Wire(newer), Set(Souls), new List<ModSync.Item> { Installed(Souls) }, None);

		AssertThat(plan.Ok).IsTrue();
		AssertThat(plan.Order[0].From).IsEqual(ModSync.Source.Download);
		AssertThat(plan.Order[0].Mod.Sha256).IsEqual(ShaD[..ModFingerprint.WireShaChars]);  // the wire's short hash until it lands
		AssertThat(plan.Replaced.Count).IsEqual(1);
		AssertThat(plan.Suspend.Count).IsEqual(0);

		// Once received, the entry carries the package's full hash and its cache path.
		plan.Received("souls-combat", ShaD.ToUpperInvariant(), "/cache/" + ShaD + ".f2mod");
		AssertThat(plan.Order[0].From).IsEqual(ModSync.Source.Cached);
		AssertThat(plan.DownloadCount).IsEqual(0);
		List<ModSession.Entry> set = plan.ToSession("/cache");
		AssertThat(set[0].PackagePath).IsEqual("/cache/" + ShaD + ".f2mod");
		AssertThat(set[0].Sha256).IsEqual(ShaD);
		AssertThat(set[0].Name).IsEqual("souls-combat");
		AssertThat(set[0].Version).IsEqual("0.2.0");
	}

	[TestCase]
	public void TestPlanBlocksHardModsOnEitherSide()
	{
		// The host's hard mod is not here: install it.
		ModSync.Plan missing = ModSync.Resolve(Wire(Hard), Set(), None, None);
		AssertThat(missing.Ok).IsFalse();
		AssertThat(missing.Blocked).Contains("Hard Mod 1.0 is a hard mod");
		AssertThat(missing.Blocked).Contains("relaunch");

		// Here, enabled, same bytes: fine.
		ModSync.Plan same = ModSync.Resolve(Wire(Hard), Set(Hard), new List<ModSync.Item> { Installed(Hard) }, None);
		AssertThat(same.Ok).IsTrue();
		AssertThat(same.Order[0].From).IsEqual(ModSync.Source.Installed);

		// Here, enabled, another build: blocked, never downloaded.
		ModSync.Plan differs = ModSync.Resolve(Wire(Hard), Set(Hard with { Sha256 = ShaA }), new List<ModSync.Item> { Installed(Hard with { Sha256 = ShaA }) }, None);
		AssertThat(differs.Ok).IsFalse();
		AssertThat(differs.Blocked).Contains("your build of it differs");

		// The joiner's own hard mod the host does not run cannot be suspended: it is compiled in.
		ModSync.Plan mine = ModSync.Resolve(Wire(Souls), Set(Souls, Hard), new List<ModSync.Item> { Installed(Souls), Installed(Hard) }, None);
		AssertThat(mine.Ok).IsFalse();
		AssertThat(mine.Blocked).Contains("your hard mod Hard Mod 1.0 is compiled into your game");
	}

	[TestCase]
	public void TestPlanBlocksApiIncompleteListAndCeiling()
	{
		ModSync.Plan api = ModSync.Resolve(ModFingerprint.From(3, new[] { Souls }), Set(Souls), new List<ModSync.Item> { Installed(Souls) }, None);
		AssertThat(api.Blocked).IsEqual("the host runs mod API 3, you run mod API 2");

		var many = new List<ModFingerprint.Mod>();
		for (int i = 0; i < 100; i++)
		{
			many.Add(new ModFingerprint.Mod($"mod-number-{i:D3}", $"Mod Number {i}", "1.0.0", ShaA, 10));
		}

		AssertThat(ModFingerprint.TryDecode(ModFingerprint.From(2, many).Encode(), out ModFingerprint? partial)).IsTrue();
		ModSync.Plan incomplete = ModSync.Resolve(partial!, Set(), None, None);
		AssertThat(incomplete.Blocked).Contains("more than one message can list");

		ModSync.Plan big = ModSync.Resolve(Wire(HdUi, Souls), Set(), None, None, ceiling: 32L * 1024 * 1024);
		AssertThat(big.Blocked).IsEqual("the host's mods you lack total 45 MB, over the 32 MB a session may download");
	}

	// ---------------------------------------------------------------- the consent text

	[TestCase]
	public void TestConsentTextNamesEverythingAndSaysWhereScriptsComeFrom()
	{
		ModFingerprint host = Wire(Souls, HdUi);
		ModSync.Plan plan = ModSync.Resolve(host, Set(MyMod, Souls with { Version = "0.0.9", Sha256 = ShaD }),
			new List<ModSync.Item> { Installed(Souls with { Version = "0.0.9", Sha256 = ShaD }), Installed(MyMod) }, None);
		string text = ModSync.ConsentText(plan, "Curious Cat");

		AssertThat(text).StartsWith("Curious Cat runs 2 mods. To play together, your game uses Curious Cat's mods, in Curious Cat's order, for this session.");
		AssertThat(text).Contains("Download from Curious Cat's machine (45 MB of 64 MB allowed):");
		AssertThat(text).Contains("  • Souls Combat 0.1.0 (5 MB)");
		AssertThat(text).Contains("  • HD UI 2.0 (40 MB)");
		AssertThat(text).Contains("Yours, replaced by the host's version for this session:\n  • Souls Combat 0.0.9");
		AssertThat(text).Contains("Yours, suspended for this session:\n  • My Mod 1.2");
		AssertThat(text).Contains("Mods contain scripts that run inside your game. These come from Curious Cat's machine");
		AssertThat(text).Contains("Your own mod list and settings are not changed and come back when the session ends.");
		AssertThat(text).EndsWith("Accept to continue, or Decline to leave the session.");

		string none = ModSync.ConsentText(ModSync.Resolve(Wire(), Set(MyMod), new List<ModSync.Item> { Installed(MyMod) }, None), string.Empty);
		AssertThat(none).StartsWith("The host runs no mods. To play together, your mods are suspended for this session.");
		AssertThat(none.Contains("Download")).IsFalse();
		AssertThat(none).Contains("  • My Mod 1.2");
	}

	[TestCase]
	public void TestProgressAndBytes()
	{
		AssertThat(ModSync.Bytes(512)).IsEqual("512 B");
		AssertThat(ModSync.Bytes(300 * 1024)).IsEqual("300 KB");
		AssertThat(ModSync.Bytes(5 * 1024 * 1024 + 300 * 1024)).IsEqual("5.3 MB");
		AssertThat(ModSync.ProgressText("Curious Cat", Souls, 1, 3, 1024 * 1024, Souls.Size))
			.IsEqual("Receiving Souls Combat 0.1.0 from Curious Cat… 1 MB / 5 MB (mod 2 of 3)");
	}

	// ---------------------------------------------------------------- the phase statement

	[TestCase]
	public void TestPhaseRoundTripAndHostWaits()
	{
		string message = ModSync.EncodePhase(ModSync.Phase.Fetching, "1/3\nx");
		AssertThat(message).IsEqual("modsync=fetching:1/3 x");
		AssertThat(ModSync.TryDecodePhase(message, out ModSync.Phase phase, out string detail)).IsTrue();
		AssertThat(phase).IsEqual(ModSync.Phase.Fetching);
		AssertThat(detail).IsEqual("1/3 x");

		AssertThat(ModSync.TryDecodePhase("modsync=ready:abc:def", out phase, out detail)).IsTrue();
		AssertThat(phase).IsEqual(ModSync.Phase.Ready);
		AssertThat(detail).IsEqual("abc:def");

		AssertThat(ModSync.TryDecodePhase("modsync=none:", out _, out _)).IsFalse();
		AssertThat(ModSync.TryDecodePhase("modsync=dancing", out _, out _)).IsFalse();
		AssertThat(ModSync.TryDecodePhase("mods=abc/2/0:", out _, out _)).IsFalse();

		AssertThat(ModSync.HostWaits(ModSync.Phase.Asking)).IsTrue();
		AssertThat(ModSync.HostWaits(ModSync.Phase.Fetching)).IsTrue();
		AssertThat(ModSync.HostWaits(ModSync.Phase.Ready)).IsTrue();
		AssertThat(ModSync.HostWaits(ModSync.Phase.Declined)).IsFalse();
		AssertThat(ModSync.HostWaits(ModSync.Phase.Blocked)).IsFalse();
		AssertThat(ModSync.HostWaits(ModSync.Phase.Failed)).IsFalse();
	}

	// ---------------------------------------------------------------- the frames

	[TestCase]
	public void TestFramesRoundTripThroughArbitraryPieces()
	{
		byte[] get = ModSync.EncodeFrame(ModSync.Kind.Get, "aaaaaaaaaaaa\tsouls-combat");
		byte[] chunk = ModSync.EncodeChunk(7, new byte[] { 1, 2, 3 });
		byte[] ack = ModSync.EncodeAck(8);
		var stream = new List<byte>();
		stream.AddRange(get);
		stream.AddRange(chunk);
		stream.AddRange(ack);

		byte[] all = stream.ToArray();
		AssertThat(get.Length).IsEqual(ModSync.FrameHeader + 25);

		var reader = new ModSync.FrameReader();
		AssertThat(reader.Next()).IsNull();
		reader.Feed(all.AsSpan(0, 3));                 // part of the header
		AssertThat(reader.Next()).IsNull();
		reader.Feed(all.AsSpan(3, get.Length - 4));    // one byte short of the frame
		AssertThat(reader.Next()).IsNull();
		reader.Feed(all.AsSpan(get.Length - 1, 5));    // the last byte, and part of the next header
		var first = reader.Next();
		AssertThat(first).IsNotNull();
		AssertThat(first!.Value.Kind).IsEqual(ModSync.Kind.Get);
		AssertThat(Encoding.UTF8.GetString(first.Value.Payload)).IsEqual("aaaaaaaaaaaa\tsouls-combat");
		AssertThat(reader.Next()).IsNull();

		reader.Feed(all.AsSpan(get.Length + 4));
		var second = reader.Next();
		AssertThat(second!.Value.Kind).IsEqual(ModSync.Kind.Chunk);
		AssertThat(BitConverter.ToUInt32(second.Value.Payload, 0)).IsEqual(7u);
		AssertThat(second.Value.Payload.Length).IsEqual(7);
		var third = reader.Next();
		AssertThat(third!.Value.Kind).IsEqual(ModSync.Kind.Ack);
		AssertThat(BitConverter.ToUInt32(third.Value.Payload, 0)).IsEqual(8u);
		AssertThat(reader.Next()).IsNull();
		AssertThat(reader.Error).IsEqual(string.Empty);
	}

	[TestCase]
	public void TestFrameReaderRefusesUnknownKindsAndOversizedPayloads()
	{
		var reader = new ModSync.FrameReader();
		reader.Feed(new byte[] { 9, 0, 0, 0, 0 });
		AssertThat(reader.Next()).IsNull();
		AssertThat(reader.Error).Contains("kind 9");

		var big = new ModSync.FrameReader();
		var header = new byte[5];
		header[0] = (byte)ModSync.Kind.Chunk;
		BitConverter.TryWriteBytes(header.AsSpan(1), (uint)(ModSync.MaxPayload + 1));
		big.Feed(header);
		AssertThat(big.Next()).IsNull();
		AssertThat(big.Error.Length).IsGreater(0);
	}

	[TestCase]
	public void TestHeadRoundTripAndChecks()
	{
		string head = ModSync.HeadText("souls-combat", ShaA, 150_000);
		var parsed = ModSync.ParseHead(head);
		AssertThat(parsed).IsNotNull();
		AssertThat(parsed!.Value.Name).IsEqual("souls-combat");
		AssertThat(parsed.Value.Sha256).IsEqual(ShaA);
		AssertThat(parsed.Value.Size).IsEqual(150_000L);
		AssertThat(parsed.Value.ChunkBytes).IsEqual(ModSync.ChunkBytes);
		AssertThat(parsed.Value.ChunkCount).IsEqual(3);

		AssertThat(ModSync.ChunkCount(0)).IsEqual(0);
		AssertThat(ModSync.ChunkCount(ModSync.ChunkBytes)).IsEqual(1);
		AssertThat(ModSync.ChunkCount(ModSync.ChunkBytes + 1)).IsEqual(2);

		AssertThat(ModSync.ParseHead("a\tshort\t1\t1\t1")).IsNull();
		AssertThat(ModSync.ParseHead($"a\t{ShaA}\t100\t65536\t2")).IsNull();   // one chunk too many
		AssertThat(ModSync.ParseHead($"a\t{ShaA}\t100000\t65536\t1")).IsNull(); // one too few
		AssertThat(ModSync.ParseHead($"a\t{ShaA}\t1\t999999\t1")).IsNull();    // chunk over the limit
	}

	[TestCase]
	public void TestFeederAndAssemblerDeliverAndVerify()
	{
		var random = new Random(7);
		var bytes = new byte[ModSync.ChunkBytes * 11 + 123];
		random.NextBytes(bytes);
		string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

		var feeder = new ModSync.Feeder("souls-combat", sha, bytes);
		AssertThat(feeder.ChunkCount).IsEqual(12);

		var reader = new ModSync.FrameReader();
		reader.Feed(feeder.Head());
		var head = ModSync.ParseHead(Encoding.UTF8.GetString(reader.Next()!.Value.Payload));
		var assembler = new ModSync.Assembler(head!.Value.Name, head.Value.Sha256, head.Value.Size, head.Value.ChunkBytes, head.Value.ChunkCount);

		// The window: eight chunks go out before any ack, then no more until one arrives.
		List<byte[]> first = feeder.Next();
		AssertThat(first.Count).IsEqual(ModSync.WindowChunks);
		AssertThat(feeder.Next().Count).IsEqual(0);

		foreach (byte[] frame in first)
		{
			reader.Feed(frame);
			var chunk = reader.Next()!.Value;
			AssertThat(assembler.Take(BitConverter.ToUInt32(chunk.Payload, 0), chunk.Payload.AsSpan(4))).IsTrue();
		}

		AssertThat(assembler.Received).IsEqual(8);
		AssertThat(assembler.Verify(sha)).IsNull();  // not complete yet

		feeder.Ack(8);
		List<byte[]> rest = feeder.Next();
		AssertThat(rest.Count).IsEqual(4);
		foreach (byte[] frame in rest)
		{
			reader.Feed(frame);
			var chunk = reader.Next()!.Value;
			AssertThat(assembler.Take(BitConverter.ToUInt32(chunk.Payload, 0), chunk.Payload.AsSpan(4))).IsTrue();
		}

		AssertThat(assembler.Complete).IsTrue();
		AssertThat(assembler.BytesReceived).IsEqual(bytes.LongLength);
		feeder.Ack(12);
		AssertThat(feeder.Done).IsTrue();

		// A repeat is harmless; a skip is refused; the wrong length is refused.
		AssertThat(assembler.Take(3, new byte[ModSync.ChunkBytes])).IsTrue();
		AssertThat(assembler.Take(20, new byte[ModSync.ChunkBytes])).IsFalse();

		byte[]? verified = assembler.Verify(sha[..ModFingerprint.WireShaChars]);
		AssertThat(verified).IsNotNull();
		AssertThat(verified!.AsSpan().SequenceEqual(bytes)).IsTrue();

		// The short hash the host's set carried must agree too.
		AssertThat(assembler.Verify(ShaB[..ModFingerprint.WireShaChars])).IsNull();
	}

	[TestCase]
	public void TestAssemblerRefusesTamperedBytesAndWrongOrder()
	{
		var bytes = new byte[1000];
		for (int i = 0; i < bytes.Length; i++)
		{
			bytes[i] = (byte)i;
		}

		string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
		var assembler = new ModSync.Assembler("m", sha, bytes.Length, 400, 3);
		AssertThat(assembler.Take(1, bytes.AsSpan(400, 400))).IsFalse();  // out of order
		AssertThat(assembler.Take(0, bytes.AsSpan(0, 399))).IsFalse();    // wrong length
		AssertThat(assembler.Take(0, bytes.AsSpan(0, 400))).IsTrue();
		AssertThat(assembler.Take(1, bytes.AsSpan(400, 400))).IsTrue();
		var tampered = bytes.AsSpan(800, 200).ToArray();
		tampered[5] ^= 0xFF;
		AssertThat(assembler.Take(2, tampered)).IsTrue();
		AssertThat(assembler.Complete).IsTrue();
		AssertThat(assembler.Verify(sha)).IsNull();
	}

	[TestCase]
	public void TestSessionResolveWithoutASessionIsTheLaunchersList()
	{
		AssertThat(ModSession.IsActive).IsFalse();
		AssertThat(ModSession.Active).IsNull();
		(ModLoadOrder order, List<ModInstall> installs) = ModSession.Resolve(string.Empty);
		AssertThat(order.Enabled.Count).IsEqual(0);
		AssertThat(installs.Count).IsEqual(0);
	}
}
