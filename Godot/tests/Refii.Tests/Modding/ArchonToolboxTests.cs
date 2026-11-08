namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;
using Refii.Save;
using Refii.Titles.Fable2;

/// <summary>
/// Archon's Toolbox on the save system and in co-op: the pure parsers over the toolbox's own
/// file shapes (installedmods.lua, dir.manifest, runner.lua, the bank hashes), detection on a
/// synthetic install, the sidecar's archon layer round trip, the comparison and the notice, and
/// the co-op set entries with the sync plan's verdicts. No file of the user's, no Godot type.
/// </summary>
[TestSuite]
public class ArchonToolboxTests
{
	private const string ShippedTemplate =
		"-- Limitations: \n" +
		"-- \t\t\t\tNone of the following is allowed: \n" +
		"--\t\t\t\t\tMulti-line comments. Hex values. Functions. Operations. Variable values.  Return marks the end of the file. \n" +
		"\n" +
		"local runnerversion = 1.04\n" +
		"local installedmods = {\n" +
		"}\n" +
		"\n" +
		"local installedmodules = {}\n" +
		"return installedmods, runnerversion\n";

	/// <summary>Two mods the way the manager writes them (ConvertJsonManifestIntoTable + SetModBool).</summary>
	private const string TwoMods =
		"local runnerversion = 1.04\n" +
		"local installedmods = {\n" +
		"DebugMenu = {\n" +
		"Enabled = true, Installed = true, ClearData = false,\n" +
		"NameID = \"DebugMenu\",\n" +
		"VersionMajor = 1,\n" +
		"VersionMinor = 3,\n" +
		"StartScript = \"scripts/Mods/DebugMenu/debugmenu.lua\", -- a -- inside a string stays\n" +
		"Nested = { A = 1, B = { C = \"}\" } },\n" +
		"},\n" +
		"Fishing = {\n" +
		"Enabled = false, Installed = true, ClearData = false,\n" +
		"NameID = \"Fishing\",\n" +
		"VersionMajor = 2,\n" +
		"VersionMinor = 0,\n" +
		"},\n" +
		"OrbSucker = {\n" +
		"Enabled = true, Installed = false, ClearData = true,\n" +
		"VersionMajor = 1,\n" +
		"},\n" +
		"}\n" +
		"local installedmodules = {}\n" +
		"return installedmods, runnerversion\n";

	private const string Manifest =
		"worlds\\albion\\demondoors\\wraithmarshdd\\defaultscenario\\level.vfsconfig\r\n" +
		"scripts_r\\startup\\startup.lua\r\n" +
		"scripts\\Quests\\MultipageMenu.lua\r\n" +
		"scripts\\Mod Manager\\init.lua\r\n" +
		"scripts\\Mod Manager\\runner.lua\r\n" +
		"scripts/Mods/DebugMenu/debugmenu.lua\r\n" +
		"scripts\\Mods\\DebugMenu\\modmanifest.json\r\n" +
		"\r\n" +
		"scripts\\quests\\qc010_childhood.lua\r\n";

	private static readonly string ShaA = new('a', 64);

	// ---------------------------------------------------------------- the parsers

	[TestCase]
	public void TestShippedTemplateHasNoMods()
	{
		AssertThat(ArchonToolbox.ParseInstalledMods(ShippedTemplate)).IsEmpty();
		AssertThat(ArchonToolbox.ParseInstalledMods(string.Empty)).IsEmpty();
		AssertThat(ArchonToolbox.ParseInstalledMods("return nil")).IsEmpty();
	}

	[TestCase]
	public void TestInstalledModsParsesNamesVersionsAndFlags()
	{
		List<ArchonToolbox.Mod> mods = ArchonToolbox.ParseInstalledMods(TwoMods);

		AssertThat(mods.Select(m => m.Name).ToList()).IsEqual(new List<string> { "DebugMenu", "Fishing", "OrbSucker" });
		AssertThat(mods[0].Version).IsEqual("1.3");
		AssertThat(mods[0].Enabled).IsTrue();
		AssertThat(mods[0].Installed).IsTrue();
		AssertThat(mods[0].Active).IsTrue();
		AssertThat(mods[0].Label).IsEqual("DebugMenu 1.3");
		AssertThat(mods[1].Enabled).IsFalse();
		AssertThat(mods[1].Active).IsFalse();
		AssertThat(mods[2].Version).IsEqual("1");
		AssertThat(mods[2].Installed).IsFalse();
		AssertThat(mods[2].Active).IsFalse();
	}

	[TestCase]
	public void TestManifestLinesNameOnlyTheToolboxFiles()
	{
		List<string> lines = ArchonToolbox.ManifestScriptLines(Manifest);
		AssertThat(lines).IsEqual(new List<string>
		{
			"scripts\\Quests\\MultipageMenu.lua",
			"scripts\\Mod Manager\\init.lua",
			"scripts\\Mod Manager\\runner.lua",
			"scripts/Mods/DebugMenu/debugmenu.lua",
			"scripts\\Mods\\DebugMenu\\modmanifest.json",
		});
		AssertThat(ArchonToolbox.ManifestScriptLines("scripts_r\\startup\\startup.lua\n")).IsEmpty();
	}

	[TestCase]
	public void TestBankHashesClassify()
	{
		AssertThat(ArchonToolbox.ClassifyBank(ArchonToolbox.BankOriginal10)).IsEqual(ArchonToolbox.BankState.Original);
		AssertThat(ArchonToolbox.ClassifyBank(ArchonToolbox.BankOriginal1.ToUpperInvariant())).IsEqual(ArchonToolbox.BankState.Original);
		AssertThat(ArchonToolbox.ClassifyBank(ArchonToolbox.BankPatched10)).IsEqual(ArchonToolbox.BankState.Patched);
		AssertThat(ArchonToolbox.ClassifyBank(ArchonToolbox.BankPatched1)).IsEqual(ArchonToolbox.BankState.Patched);
		AssertThat(ArchonToolbox.ClassifyBank(ShaA)).IsEqual(ArchonToolbox.BankState.Modified);
		AssertThat(ArchonToolbox.ClassifyBank(string.Empty)).IsEqual(ArchonToolbox.BankState.Missing);
	}

	[TestCase]
	public void TestRunnerVersionLine()
	{
		AssertThat(ArchonToolbox.RunnerVersion("-- runner\nVersion = 1.04\nlocal x = 1\n")).IsEqual("1.04");
		AssertThat(ArchonToolbox.RunnerVersion("no version here")).IsEqual(string.Empty);
	}

	// ---------------------------------------------------------------- a synthetic install

	private static string Scratch()
	{
		string root = Path.Combine(Refii.Tests.TestTemp.Root, "refii-scratch", "archon", "test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	[TestCase]
	public void TestDetectOnASyntheticInstall()
	{
		string root = Scratch();
		try
		{
			// A vanilla install: a data folder with a manifest and an unpatched (here: unknown) bank.
			string data = Path.Combine(root, "data");
			Directory.CreateDirectory(data);
			File.WriteAllText(Path.Combine(data, ArchonToolbox.ManifestFile), "scripts_r\\startup\\startup.lua\r\n");
			File.WriteAllBytes(Path.Combine(data, ArchonToolbox.BankFile), new byte[] { 1, 2, 3 });

			ArchonToolbox.Report vanilla = ArchonToolbox.Detect(root);
			AssertThat(vanilla.DataDirectory).IsEqual(data);
			AssertThat(vanilla.Present).IsFalse();
			AssertThat(vanilla.Bank).IsEqual(ArchonToolbox.BankState.Modified);
			AssertThat(vanilla.BankSha256.Length).IsEqual(64);
			AssertThat(ArchonToolbox.Stamp(vanilla)).IsNull();
			AssertThat(ArchonToolbox.FingerprintEntries(vanilla)).IsEmpty();

			// The toolbox applied: runner, installedmods, a mod folder, the manifest lines.
			string runner = Path.Combine(data, "scripts", "Mod Manager");
			Directory.CreateDirectory(runner);
			File.WriteAllText(Path.Combine(runner, ArchonToolbox.RunnerFile), "Version = 1.04\n");
			File.WriteAllText(Path.Combine(runner, ArchonToolbox.InstalledModsFile), TwoMods);
			Directory.CreateDirectory(Path.Combine(data, "scripts", "Mods", "DebugMenu"));
			File.WriteAllText(Path.Combine(data, "scripts", "Mods", "DebugMenu", ArchonToolbox.ModManifestFile), "{}");
			Directory.CreateDirectory(Path.Combine(data, "scripts", "Mods", "NoManifest"));
			File.WriteAllText(Path.Combine(data, ArchonToolbox.ManifestFile), Manifest);

			ArchonToolbox.Report applied = ArchonToolbox.Detect(root);
			AssertThat(applied.Present).IsTrue();
			AssertThat(applied.Runner).IsTrue();
			AssertThat(applied.RunnerVersion).IsEqual("1.04");
			AssertThat(applied.Mods.Count).IsEqual(3);
			AssertThat(applied.Active.Select(m => m.Name).ToList()).IsEqual(new List<string> { "DebugMenu" });
			AssertThat(applied.Folders).IsEqual(new List<string> { "DebugMenu" });
			AssertThat(applied.ManifestLines.Count).IsEqual(5);
			AssertThat(applied.Describe()).Contains("DebugMenu 1.3");

			// Cached by stamp: the same report object until a file changes.
			AssertThat(ReferenceEquals(ArchonToolbox.Detect(root), applied)).IsTrue();
			File.WriteAllText(Path.Combine(runner, ArchonToolbox.InstalledModsFile), ShippedTemplate + "\n");
			File.SetLastWriteTimeUtc(Path.Combine(runner, ArchonToolbox.InstalledModsFile), DateTime.UtcNow.AddMinutes(1));
			ArchonToolbox.Report again = ArchonToolbox.Detect(root);
			AssertThat(ReferenceEquals(again, applied)).IsFalse();
			AssertThat(again.Present).IsTrue();
			AssertThat(again.Active).IsEmpty();

			// The data directory itself is accepted too; nothing is accepted elsewhere.
			AssertThat(ArchonToolbox.Detect(data).DataDirectory).IsEqual(data);
			AssertThat(ArchonToolbox.Detect(Path.Combine(root, "nowhere")).Present).IsFalse();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	// ---------------------------------------------------------------- the save layer

	private static ArchonToolbox.Report Applied(params ArchonToolbox.Mod[] mods)
	{
		var report = new ArchonToolbox.Report { Runner = true, RunnerVersion = "1.04", Bank = ArchonToolbox.BankState.Patched, BankSha256 = ArchonToolbox.BankPatched10 };
		report.Mods.AddRange(mods);
		return report;
	}

	private static ArchonToolbox.Mod Debug => new("DebugMenu", "1.3", true, true);

	private static ArchonToolbox.Mod Fishing => new("Fishing", "2.0", true, true);

	/// <summary>The external-manager diff the save gate builds from the toolbox's own answer.</summary>
	private static Engine_ExternalModManager.Diff External(ArchonLayer stamp, ArchonToolbox.Report now)
	{
		ArchonToolbox.Diff diff = ArchonToolbox.Compare(stamp, now);
		return new Engine_ExternalModManager.Diff(diff.Missing.Count, diff.Present, ArchonToolbox.NoticeText(stamp, diff));
	}

	[TestCase]
	public void TestStampAndSidecarRoundTrip()
	{
		ArchonLayer? stamp = ArchonToolbox.Stamp(Applied(Debug, new ArchonToolbox.Mod("Fishing", "2.0", false, true)));
		AssertThat(stamp).IsNotNull();
		AssertThat(stamp!.Patched).IsTrue();
		AssertThat(stamp.Bank).IsEqual(ArchonToolbox.BankPatched10);
		AssertThat(stamp.Runner).IsEqual("1.04");
		AssertThat(stamp.Mods.Select(m => m.Label).ToList()).IsEqual(new List<string> { "DebugMenu 1.3" });

		var sidecar = new SaveSidecar { Refii = new SaveSidecar.RefiiLayer { Build = "2" } };
		sidecar.Layers[ArchonLayer.Key] = stamp.ToJson();
		byte[] json = sidecar.ToJson();
		SaveSidecar back = SaveSidecar.Parse(json);
		AssertThat(back.Mods).IsNull();
		ArchonLayer? backLayer = ArchonLayer.FromJson(back.Layers[ArchonLayer.Key]);
		AssertThat(backLayer).IsNotNull();
		AssertThat(backLayer!.Patched).IsTrue();
		AssertThat(backLayer.Find("debugmenu")!.Version).IsEqual("1.3");
		AssertThat(back.Modded).IsTrue();
		AssertThat(back.ToJson()).IsEqual(json);

		string text = System.Text.Encoding.UTF8.GetString(json);
		AssertThat(text.Contains("\"archon\"", StringComparison.Ordinal)).IsTrue();
		AssertThat(text.Contains("\"held\"", StringComparison.Ordinal)).IsFalse();

		// The layer is independent: a reader without it, and a save with the toolbox but no mods, is not modded.
		SaveSidecar none = SaveSidecar.Parse(System.Text.Encoding.UTF8.GetBytes("{\"schema\":\"refii-save/1\",\"refii\":{\"build\":\"2\"}}"));
		AssertThat(none.Layers.ContainsKey(ArchonLayer.Key)).IsFalse();
		ArchonLayer? empty = ArchonToolbox.Stamp(Applied());
		AssertThat(empty).IsNotNull();
		var emptySidecar = new SaveSidecar();
		emptySidecar.Layers[ArchonLayer.Key] = empty!.ToJson();
		AssertThat(emptySidecar.Modded).IsFalse();
	}

	[TestCase]
	public void TestCompareAgainstTheInstallNow()
	{
		ArchonLayer stamp = ArchonToolbox.Stamp(Applied(Debug, Fishing))!;

		ArchonToolbox.Diff same = ArchonToolbox.Compare(stamp, Applied(Fishing, Debug));
		AssertThat(same.Same).IsTrue();
		AssertThat(same.Present).IsTrue();

		ArchonToolbox.Diff one = ArchonToolbox.Compare(stamp, Applied(Debug, new ArchonToolbox.Mod("OrbSucker", "1", true, true)));
		AssertThat(one.Missing.Select(m => m.Name).ToList()).IsEqual(new List<string> { "Fishing" });
		AssertThat(one.Added.Select(m => m.Name).ToList()).IsEqual(new List<string> { "OrbSucker" });

		ArchonToolbox.Diff gone = ArchonToolbox.Compare(stamp, new ArchonToolbox.Report());
		AssertThat(gone.Present).IsFalse();
		AssertThat(gone.Missing.Count).IsEqual(2);

		// A save without the layer against an install with the toolbox: only additions.
		ArchonToolbox.Diff fresh = ArchonToolbox.Compare(null, Applied(Debug));
		AssertThat(fresh.Missing).IsEmpty();
		AssertThat(fresh.Added.Count).IsEqual(1);
	}

	[TestCase]
	public void TestNoticeListsTheToolboxModsUnderTheirOwnHeading()
	{
		ArchonLayer stamp = ArchonToolbox.Stamp(Applied(Debug, Fishing))!;

		// Present and the same: the heading and the list, nothing marked.
		string same = ArchonToolbox.NoticeText(stamp, ArchonToolbox.Compare(stamp, Applied(Debug, Fishing)));
		AssertThat(same).IsEqual(ArchonToolbox.NoticeHeading + "\nDebugMenu 1.3\nFishing 2.0\n");

		// Present, one not enabled: marked, and the line about its state staying in the save.
		string one = ArchonToolbox.NoticeText(stamp, ArchonToolbox.Compare(stamp, Applied(Debug)));
		AssertThat(one.StartsWith(ArchonToolbox.NoticeHeading + "\nDebugMenu 1.3\nFishing 2.0" + ArchonToolbox.NotEnabledMark + "\n", StringComparison.Ordinal)).IsTrue();
		AssertThat(one.Contains("keeps its state inside the save", StringComparison.Ordinal)).IsTrue();

		// The toolbox not applied at all: every mod marked so, and the statement that nothing is filtered.
		string gone = ArchonToolbox.NoticeText(stamp, ArchonToolbox.Compare(stamp, new ArchonToolbox.Report()));
		AssertThat(gone.Split('\n').Count(line => line.EndsWith(ArchonToolbox.NotPresentMark, StringComparison.Ordinal))).IsEqual(2);
		AssertThat(gone.Contains("stay inside the save as they were", StringComparison.Ordinal)).IsTrue();

		// On the page with Refii's own mods: Refii's part first, then the toolbox's, one blank line between.
		var souls = new ModFingerprint.Mod("souls_combat", "Souls Combat", "0.1.0", ShaA);
		SaveSidecar.ModsLayer mods = ModSaveStamp.Build(ModFingerprint.From(2, new[] { souls }), new Dictionary<string, IReadOnlyCollection<uint>>());
		ModSaveStamp.Diff diff = ModSaveStamp.Compare(mods, ModFingerprint.From(2, new[] { souls }));
		string page = ModSaveStamp.NoticeText(mods, diff, External(stamp, Applied(Debug, Fishing)));
		AssertThat(page).IsEqual("This save was made with these mods:\nSouls Combat 0.1.0\n\n" + same);

		// A save made under the toolbox alone: no Refii heading, only the toolbox's part.
		string alone = ModSaveStamp.NoticeText(new SaveSidecar.ModsLayer(), ModSaveStamp.Compare(null, ModFingerprint.From(2, Array.Empty<ModFingerprint.Mod>())), External(stamp, Applied(Debug, Fishing)));
		AssertThat(alone).IsEqual(same);

		// Without the toolbox arguments the page reads as before.
		AssertThat(ModSaveStamp.NoticeText(mods, diff)).IsEqual("This save was made with these mods:\nSouls Combat 0.1.0\n");
	}

	// ---------------------------------------------------------------- co-op

	[TestCase]
	public void TestFingerprintEntriesAreSortedPrefixedAndNative()
	{
		List<ModFingerprint.Mod> entries = ArchonToolbox.FingerprintEntries(Applied(Fishing, Debug, new ArchonToolbox.Mod("Off", "1", false, true)));

		AssertThat(entries.Select(m => m.Name).ToList()).IsEqual(new List<string> { "archon:DebugMenu", "archon:Fishing" });
		AssertThat(entries[0].Title).IsEqual("Archon's Toolbox: DebugMenu");
		AssertThat(entries[0].Version).IsEqual("1.3");
		AssertThat(entries[0].Sha256).IsEqual(ArchonToolbox.BankPatched10);
		AssertThat(entries[0].Native).IsTrue();
		AssertThat(ArchonToolbox.IsFingerprintEntry(entries[0])).IsTrue();
		AssertThat(ArchonToolbox.IsFingerprintEntry(new ModFingerprint.Mod("souls", "Souls", "1", ShaA))).IsFalse();

		// They count in the digest and the diff, after the player's own mods, and survive the wire.
		var souls = new ModFingerprint.Mod("souls_combat", "Souls Combat", "0.1.0", ShaA);
		ModFingerprint plain = ModFingerprint.From(2, new[] { souls });
		ModFingerprint with = ModFingerprint.WithExternal(plain, entries);
		AssertThat(with.Mods.Count).IsEqual(3);
		AssertThat(with.Mods[0].Name).IsEqual("souls_combat");
		AssertThat(with.Digest()).IsNotEqual(plain.Digest());
		AssertThat(ReferenceEquals(ModFingerprint.WithExternal(plain, new List<ModFingerprint.Mod>()), plain)).IsTrue();
		AssertThat(ModFingerprint.TryDecode(with.Encode(), out ModFingerprint? wire)).IsTrue();
		AssertThat(wire!.Digest()).IsEqual(with.Digest());
		AssertThat(wire.Mods[1].Native).IsTrue();
		AssertThat(plain.Describe(wire, "host", "joiner")).Contains("host lacks Archon's Toolbox: DebugMenu 1.3");
	}

	[TestCase]
	public void TestSyncPlanAgreesOnTheSameToolboxModsAndBlocksAMismatch()
	{
		var souls = new ModFingerprint.Mod("souls_combat", "Souls Combat", "0.1.0", ShaA, 1024);
		List<ModFingerprint.Mod> archon = ArchonToolbox.FingerprintEntries(Applied(Debug));
		ModFingerprint hostSet = ModFingerprint.WithExternal(ModFingerprint.From(2, new[] { souls }), archon);
		AssertThat(ModFingerprint.TryDecode(hostSet.Encode(), out ModFingerprint? host)).IsTrue();
		var installed = new List<ModSync.Item> { new(souls, ModSync.Source.Installed, "/mods/souls.f2mod") };

		// Same toolbox mod on both sides: agreed on, never a session entry.
		ModSync.Plan same = ModSync.Resolve(host!, ModFingerprint.WithExternal(ModFingerprint.From(2, Array.Empty<ModFingerprint.Mod>()), archon), installed, new List<ModSync.Item>());
		AssertThat(same.Ok).IsTrue();
		AssertThat(same.Order.Select(i => i.Mod.Name).ToList()).IsEqual(new List<string> { "souls_combat" });
		AssertThat(same.Suspend).IsEmpty();
		AssertThat(same.Replaced).IsEmpty();

		// The joiner lacks it: blocked, naming the toolbox and why it cannot be synced.
		ModSync.Plan lacking = ModSync.Resolve(host!, ModFingerprint.From(2, Array.Empty<ModFingerprint.Mod>()), installed, new List<ModSync.Item>());
		AssertThat(lacking.Ok).IsFalse();
		AssertThat(lacking.Blocked).Contains("Archon's Toolbox: DebugMenu 1.3 is an Archon's Toolbox mod");
		AssertThat(lacking.Blocked).Contains("cannot be packaged or synced");
		AssertThat(lacking.Blocked).Contains("install it with Archon's Toolbox");

		// The joiner has it against another bank: blocked as a differing copy.
		List<ModFingerprint.Mod> other = ArchonToolbox.FingerprintEntries(
			new ArchonToolbox.Report { Runner = true, Bank = ArchonToolbox.BankState.Patched, BankSha256 = ArchonToolbox.BankPatched1, Mods = { Debug } });
		ModSync.Plan differs = ModSync.Resolve(host!, ModFingerprint.WithExternal(ModFingerprint.From(2, Array.Empty<ModFingerprint.Mod>()), other), installed, new List<ModSync.Item>());
		AssertThat(differs.Ok).IsFalse();
		AssertThat(differs.Blocked).Contains("your copy differs from the host's");

		// The joiner's own toolbox mod the host does not run: blocked, never suspended.
		ModFingerprint plainHost = ModFingerprint.From(2, new[] { souls });
		AssertThat(ModFingerprint.TryDecode(plainHost.Encode(), out ModFingerprint? host2)).IsTrue();
		ModSync.Plan mine = ModSync.Resolve(host2!, ModFingerprint.WithExternal(ModFingerprint.From(2, new[] { souls }), archon), installed, new List<ModSync.Item>());
		AssertThat(mine.Ok).IsFalse();
		AssertThat(mine.Blocked).Contains("your Archon's Toolbox: DebugMenu 1.3 is an Archon's Toolbox mod");
		AssertThat(mine.Blocked).Contains("disable it in Archon's Toolbox");
		AssertThat(mine.Suspend).IsEmpty();
	}
}
