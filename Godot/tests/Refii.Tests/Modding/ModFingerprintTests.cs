namespace Refii.Tests.Modding;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;

/// <summary>
/// The co-op mod fingerprint's list logic: digest stability, the diff a player reads, and the
/// wire form. In-memory lists only — nothing here hashes a file or touches a Godot type.
/// </summary>
[TestSuite]
public class ModFingerprintTests
{
	private static readonly string ShaA = new('a', 64);
	private static readonly string ShaB = new('b', 64);
	private static readonly string ShaC = new('c', 64);

	private static ModFingerprint.Mod Souls => new("souls-combat", "Souls Combat", "0.1.0", ShaA);

	private static ModFingerprint.Mod MyMod => new("my-mod", "My Mod", "1.2", ShaB);

	private static ModFingerprint.Mod HdUi => new("hd-ui", "HD UI", "2.0", ShaC);

	private static ModFingerprint Set(params ModFingerprint.Mod[] mods) => ModFingerprint.From(2, mods);

	[TestCase]
	public void TestDigestIsStableAndShort()
	{
		string first = Set(Souls, MyMod).Digest();
		string second = Set(Souls, MyMod).Digest();

		AssertThat(first).IsEqual(second);
		AssertThat(first.Length).IsEqual(16);
		AssertThat(first).IsEqual(first.ToLowerInvariant());
	}

	[TestCase]
	public void TestDigestIgnoresNameCaseAndTitle()
	{
		var renamed = new ModFingerprint.Mod("SOULS-COMBAT", "A Different Title", "0.1.0", ShaA.ToUpperInvariant());
		AssertThat(Set(renamed).Digest()).IsEqual(Set(Souls).Digest());
	}

	[TestCase]
	public void TestDigestCountsOrderBytesVersionAndApi()
	{
		string baseline = Set(Souls, MyMod).Digest();

		AssertThat(Set(MyMod, Souls).Digest()).IsNotEqual(baseline);
		AssertThat(Set(Souls with { Sha256 = ShaC }, MyMod).Digest()).IsNotEqual(baseline);
		AssertThat(Set(Souls with { Version = "0.1.1" }, MyMod).Digest()).IsNotEqual(baseline);
		AssertThat(ModFingerprint.From(3, new[] { Souls, MyMod }).Digest()).IsNotEqual(baseline);
		AssertThat(Set().Digest()).IsNotEqual(ModFingerprint.From(3, new List<ModFingerprint.Mod>()).Digest());
	}

	[TestCase]
	public void TestDescribeAgreeingSetsIsEmpty()
	{
		AssertThat(Set(Souls, MyMod).Describe(Set(Souls, MyMod), "host", "joiner")).IsEqual(string.Empty);
	}

	[TestCase]
	public void TestDescribeMissingOnEachSide()
	{
		string diff = Set(Souls).Describe(Set(MyMod), "host", "joiner");
		AssertThat(diff).IsEqual("joiner lacks Souls Combat 0.1.0; host lacks My Mod 1.2");
	}

	[TestCase]
	public void TestDescribeVersionBytesApiAndOrder()
	{
		AssertThat(Set(Souls).Describe(Set(Souls with { Version = "0.2.0" }), "host", "joiner"))
			.IsEqual("Souls Combat: host has 0.1.0, joiner has 0.2.0");

		AssertThat(Set(Souls).Describe(Set(Souls with { Sha256 = ShaC }), "joiner", "host"))
			.IsEqual("Souls Combat 0.1.0 is a different package on each side");

		AssertThat(ModFingerprint.From(2, new[] { Souls }).Describe(ModFingerprint.From(3, new[] { Souls }), "host", "joiner"))
			.IsEqual("host runs mod API 2, joiner runs mod API 3");

		AssertThat(Set(Souls, MyMod).Describe(Set(MyMod, Souls), "host", "joiner"))
			.IsEqual("the load order differs (host: Souls Combat, My Mod; joiner: My Mod, Souls Combat)");
	}

	[TestCase]
	public void TestWireRoundTrip()
	{
		ModFingerprint local = Set(Souls, MyMod, HdUi);
		string message = local.Encode();

		AssertThat(message.StartsWith(ModFingerprint.WirePrefix)).IsTrue();
		AssertThat(ModFingerprint.TryDecode(message, out ModFingerprint? decoded)).IsTrue();
		AssertThat(decoded).IsNotNull();
		AssertThat(decoded!.Digest()).IsEqual(local.Digest());
		AssertThat(decoded.ApiVersion).IsEqual(2);
		AssertThat(decoded.Total).IsEqual(3);
		AssertThat(decoded.Complete).IsTrue();
		AssertThat(decoded.Mods[0].Name).IsEqual("souls-combat");
		AssertThat(decoded.Mods[0].Title).IsEqual("Souls Combat");
		AssertThat(decoded.Mods[0].Version).IsEqual("0.1.0");
		AssertThat(decoded.Mods[0].Sha256).IsEqual(ShaA[..ModFingerprint.WireShaChars]);

		// A decoded peer diffs against the full local set: the short hash still matches.
		AssertThat(local.Describe(decoded, "host", "joiner")).IsEqual(string.Empty);
		AssertThat(Set(Souls, MyMod).Describe(decoded, "host", "joiner")).IsEqual("host lacks HD UI 2.0");
	}

	[TestCase]
	public void TestWireEmptySet()
	{
		ModFingerprint none = Set();
		AssertThat(ModFingerprint.TryDecode(none.Encode(), out ModFingerprint? decoded)).IsTrue();
		AssertThat(decoded!.Total).IsEqual(0);
		AssertThat(decoded.Digest()).IsEqual(none.Digest());
		AssertThat(Set(Souls).Describe(decoded, "joiner", "host")).IsEqual("host lacks Souls Combat 0.1.0");
	}

	[TestCase]
	public void TestWireTruncatesToBudgetAndSaysSo()
	{
		var many = new List<ModFingerprint.Mod>();
		for (int i = 0; i < 100; i++)
		{
			many.Add(new ModFingerprint.Mod($"mod-number-{i:D3}", $"Mod Number {i}", "1.0.0", ShaA));
		}

		ModFingerprint large = ModFingerprint.From(2, many);
		string message = large.Encode();
		AssertThat(System.Text.Encoding.UTF8.GetByteCount(message)).IsLessEqual(ModFingerprint.WireBudget);

		AssertThat(ModFingerprint.TryDecode(message, out ModFingerprint? decoded)).IsTrue();
		AssertThat(decoded!.Total).IsEqual(100);
		AssertThat(decoded.Complete).IsFalse();
		AssertThat(decoded.Digest()).IsEqual(large.Digest());

		// A partial list cannot prove a mod absent, and the diff says the list was partial.
		string diff = Set(Souls).Describe(decoded, "host", "joiner");
		AssertThat(diff.Contains("joiner lacks")).IsFalse();
		AssertThat(diff.Contains("joiner has 100 mods enabled")).IsTrue();
	}

	[TestCase]
	public void TestWireSeparatorsInFieldsBecomeSpaces()
	{
		var odd = new ModFingerprint.Mod("odd", "Tab\tAnd\nNewline", "1", ShaA);
		AssertThat(ModFingerprint.TryDecode(Set(odd).Encode(), out ModFingerprint? decoded)).IsTrue();
		AssertThat(decoded!.Mods.Count).IsEqual(1);
		AssertThat(decoded.Mods[0].Title).IsEqual("Tab And Newline");
	}

	[TestCase]
	public void TestTryDecodeRefusesOtherMessages()
	{
		AssertThat(ModFingerprint.TryDecode("hs.ack=3", out _)).IsFalse();
		AssertThat(ModFingerprint.TryDecode("mods=", out _)).IsFalse();
		AssertThat(ModFingerprint.TryDecode("mods=abc/2:", out _)).IsFalse();
		AssertThat(ModFingerprint.TryDecode("mods=abc/x/0:", out _)).IsFalse();
		AssertThat(ModFingerprint.TryDecode("mods=abc/2/0:one\tfield", out _)).IsFalse();
		AssertThat(ModFingerprint.TryDecode("mods=abc/2/0:a\tb\tc\td", out _)).IsFalse();
	}
}
