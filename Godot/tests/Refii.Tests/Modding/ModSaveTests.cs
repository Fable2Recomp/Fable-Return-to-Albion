namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;
using Refii.Save;

/// <summary>
/// Mod filtering on the save system, on in-memory data: the sidecar's layered schema round-trips,
/// the stamp diff, the save-stream codec (tokens, chunks, the verbatim tail), the filter of a
/// synthetic inventory with worn indices and weapon ids, its restore, the player store and the
/// box text. No file of the user's, no Godot type.
/// </summary>
[TestSuite]
public class ModSaveTests
{
	private const uint ModSword = 0x11111111u;
	private const uint ModHat = 0x22222222u;
	private const uint GameShirt = 0x33333333u;
	private const uint GamePotion = 0x44444444u;
	private const uint GameBow = 0x55555555u;

	private static readonly string ShaA = new('a', 64);
	private static readonly string ShaB = new('b', 64);

	private static ModFingerprint.Mod Souls => new("souls_combat", "Souls Combat", "0.1.0", ShaA);

	private static ModFingerprint.Mod Hats => new("fancy_hats", "Fancy Hats", "1.2", ShaB);

	private static Dictionary<string, IReadOnlyCollection<uint>> Records => new(StringComparer.OrdinalIgnoreCase)
	{
		["souls_combat"] = new[] { ModSword },
		["fancy_hats"] = new[] { ModHat },
	};

	// ---------------------------------------------------------------- a synthetic save

	private static SaveNode Item(uint record, int count)
	{
		var item = new SaveNode("InventoryItem");
		item.Field("RecordID", record);
		item.Field("Count", count);
		item.Field("Viewed", true);
		item.Field("NumInstanceData", 0u);
		return item;
	}

	private static SaveNode Modifier(int unit)
	{
		var modifier = new SaveNode("Modifier");
		modifier.Field("InventoryIndex", unit);
		modifier.Field("Layer", 2);
		return modifier;
	}

	private static SaveNode Component(string name)
	{
		var ec = new SaveNode("EC");
		ec.Attributes.Add(("Hash", ModSaveFilter.Fnv1(name)));
		return ec;
	}

	/// <summary>
	/// The hero: inventory [potion x3, mod sword, shirt, mod hat x2, bow]; worn = shirt (unit 4)
	/// and the hat (unit 5); melee = the mod sword, ranged = the bow. Plus a chest holding one
	/// mod sword and a game potion.
	/// </summary>
	private static SaveStream Synthetic()
	{
		var header = new SaveNode("Header");
		header.Field("VersionNumber", 29u);
		var manager = new SaveNode("EntityManager");
		manager.Field("CurrentUniqueID", 7u);
		var entities = new SaveNode("Entities");
		manager.Children.Add(entities);

		var hero = new SaveNode("Entity");
		var uid = new SaveNode("UniqueID");
		uid.Field("UIDGameID", 1u);
		uid.Field("UIDEditorID", 0u);
		hero.Children.Add(uid);
		hero.Field("Name", "CreatureHero");
		hero.Field("RecordID", 0x2BBA9A4Au);
		var components = new SaveNode("EntityComponents");
		hero.Children.Add(components);

		SaveNode inventory = Component("InventoryComponent");
		inventory.Children.Add(Item(GamePotion, 3));
		inventory.Children.Add(Item(ModSword, 1));
		inventory.Children.Add(Item(GameShirt, 1));
		inventory.Children.Add(Item(ModHat, 2));
		inventory.Children.Add(Item(GameBow, 1));
		components.Children.Add(inventory);

		SaveNode worn = Component("AppearanceModifierManagerComponent");
		worn.Children.Add(Modifier(4));
		worn.Children.Add(Modifier(5));
		components.Children.Add(worn);

		SaveNode weapons = Component("WeaponSetManagerComponent");
		weapons.Field("LastMeleeWeaponID", ModSword);
		weapons.Field("LastRangedWeaponID", GameBow);
		components.Children.Add(weapons);

		SaveNode stats = Component("StatsComponent");
		stats.Field("Morality", 12.5f);
		stats.Field("Renown", -3);
		components.Children.Add(stats);
		entities.Children.Add(hero);

		var chest = new SaveNode("Entity");
		var chestUid = new SaveNode("UniqueID");
		chestUid.Field("UIDGameID", 2u);
		chestUid.Field("UIDEditorID", 9u);
		chest.Children.Add(chestUid);
		var chestComponents = new SaveNode("EntityComponents");
		SaveNode chestInventory = Component("InventoryComponent");
		chestInventory.Children.Add(Item(ModSword, 1));
		chestInventory.Children.Add(Item(GamePotion, 1));
		chestComponents.Children.Add(chestInventory);
		chest.Children.Add(chestComponents);
		entities.Children.Add(chest);

		var stream = new SaveStream();
		stream.Documents.Add(header);
		stream.Documents.Add(manager);
		stream.Tail = new byte[] { 0x28, 0x00, 0x00, 0x00, 0x08, 0x01, 0xF3, 0xBD, 0x78, 0xDA, 0x01, 0x02 };
		return stream;
	}

	private static SaveNode Hero(SaveStream stream) =>
		stream.Document("EntityManager")!.Child("Entities")!.All("Entity").First(e =>
			e.Child("UniqueID")!.Child("UIDGameID")!.Text is uint g && g == 1u);

	private static SaveNode Ec(SaveNode entity, string name) =>
		entity.Child("EntityComponents")!.All("EC").First(ec =>
			ec.TryGetAttribute("Hash", out object? h) && h is uint k && k == ModSaveFilter.Fnv1(name));

	private static List<(uint Record, int Count)> Inventory(SaveNode entity) =>
		Ec(entity, "InventoryComponent").All("InventoryItem")
			.Select(i => ((uint)i.Child("RecordID")!.Text!, (int)i.Child("Count")!.Text!)).ToList();

	private static List<int> WornUnits(SaveNode entity) =>
		Ec(entity, "AppearanceModifierManagerComponent").All("Modifier").Select(m => (int)m.Child("InventoryIndex")!.Text!).ToList();

	// ---------------------------------------------------------------- the codec

	[TestCase]
	public void TestStreamTokensRoundTripWithTailVerbatim()
	{
		SaveStream original = Synthetic();
		byte[] bytes = original.Serialize();
		SaveStream again = SaveStream.Parse(bytes);

		AssertThat(again.Documents.Count).IsEqual(2);
		AssertThat(again.Documents[0].Name).IsEqual("Header");
		AssertThat(again.Documents[1].Name).IsEqual("EntityManager");
		AssertThat(again.Tail).IsEqual(original.Tail);
		AssertThat(again.Serialize()).IsEqual(bytes);

		SaveNode hero = Hero(again);
		AssertThat(Inventory(hero)).IsEqual(new List<(uint, int)> { (GamePotion, 3), (ModSword, 1), (GameShirt, 1), (ModHat, 2), (GameBow, 1) });
		AssertThat(Ec(hero, "StatsComponent").Child("Morality")!.Text).IsEqual(12.5f);
		AssertThat(Ec(hero, "StatsComponent").Child("Renown")!.Text).IsEqual(-3);
		AssertThat(hero.Child("Name")!.Text).IsEqual("CreatureHero");
	}

	[TestCase]
	public void TestChunksAreTheGuestsShapeAndInflateBack()
	{
		var raw = new byte[20000];
		new Random(7).NextBytes(raw);
		byte[] file = SaveStream.Deflate(raw);

		// Three chunks: 8192, 8192, 3616 raw bytes, each with its compressed size first.
		var sizes = new List<(int Compressed, int Raw)>();
		int pos = 0;
		while (pos + 8 <= file.Length)
		{
			int compressed = (file[pos] << 24) | (file[pos + 1] << 16) | (file[pos + 2] << 8) | file[pos + 3];
			int size = (file[pos + 4] << 24) | (file[pos + 5] << 16) | (file[pos + 6] << 8) | file[pos + 7];
			sizes.Add((compressed, size));
			pos += 8 + compressed;
		}

		AssertThat(pos).IsEqual(file.Length);
		AssertThat(sizes.Select(s => s.Raw).ToList()).IsEqual(new List<int> { 8192, 8192, 3616 });
		AssertThat(SaveStream.Inflate(file)).IsEqual(raw);

		// WHAT THE GUEST'S READER DEPENDS ON (TU1 sub_82B4E000, mode 0): one inflate context fed
		// chunk by chunk with Z_SYNC_FLUSH, each call handed exactly `compressed` bytes and
		// required to produce exactly `raw` (0x82B4E294/0x82B4E2A4). So every chunk must end on a
		// sync point: the bytes of the first N chunks alone inflate to exactly the first N raw
		// sizes, with nothing held back and nothing more.
		int at = 0;
		int rawSoFar = 0;
		for (int i = 0; i < sizes.Count; i++)
		{
			at += 8 + sizes[i].Compressed;
			rawSoFar += sizes[i].Raw;
			byte[] prefix = file.AsSpan(0, at).ToArray();
			byte[] inflated = SaveStream.Inflate(prefix);
			AssertThat(inflated.Length).IsEqual(rawSoFar);
			AssertThat(inflated).IsEqual(raw.AsSpan(0, rawSoFar).ToArray());
		}

		// The zlib header the guest's own writer emits (0x78 0xDA): inflateInit takes it.
		AssertThat(file[8]).IsEqual((byte)0x78);
		AssertThat(file[9]).IsEqual((byte)0xDA);
	}

	[TestCase]
	public void TestFileRoundTripThroughDisk()
	{
		string folder = Path.Combine(Path.GetTempPath(), "refii-modsave-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		try
		{
			string path = Path.Combine(folder, "mainsave.bin");
			Synthetic().WriteFile(path);
			SaveStream back = SaveStream.ReadFile(path);
			AssertThat(back.Serialize()).IsEqual(Synthetic().Serialize());
		}
		finally
		{
			Directory.Delete(folder, true);
		}
	}

	// ---------------------------------------------------------------- the filter

	[TestCase]
	public void TestFilterRemovesModItemsAndRepointsWorn()
	{
		SaveStream stream = Synthetic();
		SaveSidecar.HeldRecords held = ModSaveFilter.Filter(stream, new HashSet<uint> { ModSword, ModHat });

		SaveNode hero = Hero(stream);
		AssertThat(Inventory(hero)).IsEqual(new List<(uint, int)> { (GamePotion, 3), (GameShirt, 1), (GameBow, 1) });
		// The shirt was unit 4 (after 3 potions and the sword); the sword went, so it is unit 3.
		AssertThat(WornUnits(hero)).IsEqual(new List<int> { 3 });
		AssertThat(Ec(hero, "WeaponSetManagerComponent").Child("LastMeleeWeaponID")!.Text).IsEqual(0u);
		AssertThat(Ec(hero, "WeaponSetManagerComponent").Child("LastRangedWeaponID")!.Text).IsEqual(GameBow);

		// The chest lost its mod sword too; nothing of it is held (the hold is the hero's).
		SaveNode chest = stream.Document("EntityManager")!.Child("Entities")!.All("Entity").Last();
		AssertThat(Inventory(chest)).IsEqual(new List<(uint, int)> { (GamePotion, 1) });

		AssertThat(held.Inventory.Select(i => (uint)i.Child("RecordID")!.Text!).ToList()).IsEqual(new List<uint> { ModSword, ModHat });
		AssertThat(held.Worn.Select(w => w.Record).ToList()).IsEqual(new List<uint> { ModHat });
		AssertThat(held.Melee).IsEqual(ModSword);
		AssertThat(held.Ranged).IsEqual(0u);
		AssertThat(ModSaveFilter.RecordsIn(stream).Contains(ModSword)).IsFalse();
	}

	[TestCase]
	public void TestFilterOfNothingChangesNothing()
	{
		SaveStream stream = Synthetic();
		byte[] before = stream.Serialize();
		SaveSidecar.HeldRecords held = ModSaveFilter.Filter(stream, new HashSet<uint> { 0x99999999u });
		AssertThat(held.IsEmpty).IsTrue();
		AssertThat(stream.Serialize()).IsEqual(before);
		AssertThat(ModSaveFilter.Filter(stream, new HashSet<uint>()).IsEmpty).IsTrue();
	}

	[TestCase]
	public void TestRestorePutsHeldBackOnTheHero()
	{
		SaveStream stream = Synthetic();
		SaveSidecar.HeldRecords held = ModSaveFilter.Filter(stream, new HashSet<uint> { ModSword, ModHat });

		// The hold survives the sidecar's JSON.
		var sidecar = new SaveSidecar { Mods = new SaveSidecar.ModsLayer() };
		sidecar.Mods.Held["fancy_hats"] = held;
		SaveSidecar.HeldRecords back = SaveSidecar.Parse(sidecar.ToJson()).Mods!.Held["fancy_hats"];

		AssertThat(ModSaveFilter.Restore(stream, back)).IsTrue();
		SaveNode hero = Hero(stream);
		AssertThat(Inventory(hero)).IsEqual(new List<(uint, int)> { (GamePotion, 3), (GameShirt, 1), (GameBow, 1), (ModSword, 1), (ModHat, 2) });
		// The shirt stays unit 3; the hat is back, worn, at its first unit after potions (3),
		// shirt, bow and sword: 6.
		AssertThat(WornUnits(hero)).IsEqual(new List<int> { 3, 6 });
		AssertThat(Ec(hero, "WeaponSetManagerComponent").Child("LastMeleeWeaponID")!.Text).IsEqual(ModSword);

		// Filtering again removes exactly what came back.
		SaveSidecar.HeldRecords again = ModSaveFilter.Filter(stream, new HashSet<uint> { ModSword, ModHat });
		AssertThat(again.Inventory.Count).IsEqual(2);
		AssertThat(WornUnits(hero)).IsEqual(new List<int> { 3 });
	}

	[TestCase]
	public void TestFilterHeroKitDropsRecordsAndReindexesInstances()
	{
		var hero = new Engine_SaveHero.Hero();
		hero.Inventory.Add((GamePotion, 3));
		hero.Inventory.Add((ModSword, 1));
		hero.Inventory.Add((GameShirt, 1));
		hero.InventoryInstances[2] = new Engine_SaveHero.InventoryInstance { Unsupported = true };
		hero.Worn.Add(GameShirt);
		hero.Worn.Add(ModHat);
		hero.Melee = ModSword;
		hero.Ranged = GameBow;

		int removed = ModSaveFilter.FilterHero(hero, new HashSet<uint> { ModSword, ModHat });

		AssertThat(removed).IsEqual(3);
		AssertThat(hero.Inventory).IsEqual(new List<(uint, int)> { (GamePotion, 3), (GameShirt, 1) });
		AssertThat(hero.InventoryInstances.Keys.ToList()).IsEqual(new List<int> { 1 });
		AssertThat(hero.Worn).IsEqual(new List<uint> { GameShirt });
		AssertThat(hero.Melee).IsEqual(0u);
		AssertThat(hero.Ranged).IsEqual(GameBow);
	}

	// ---------------------------------------------------------------- the player store and Lua

	[TestCase]
	public void TestPlayerStoreFilterAndRestore()
	{
		const string text = "# Refii: this save's hero's mod values (refii_mod_player_*), key=value\nsouls_combat.level=3\nother.thing=1.5\nSOULS_COMBAT.poise=2\n";
		(string kept, Dictionary<string, string> held) = ModSaveFilter.FilterPlayerStore(text, new[] { "souls_combat." });

		AssertThat(held).IsEqual(new Dictionary<string, string> { ["souls_combat.level"] = "3", ["SOULS_COMBAT.poise"] = "2" });
		AssertThat(SaveSidecar.ParsePlayerStore(kept)).IsEqual(new List<(string, string)> { ("other.thing", "1.5") });

		string restored = ModSaveFilter.RestorePlayerStore(kept, held);
		AssertThat(SaveSidecar.ParsePlayerStore(restored).Select(l => l.Key).OrderBy(k => k).ToList())
			.IsEqual(new List<string> { "other.thing", "souls_combat.level", "SOULS_COMBAT.poise" });

		(string same, Dictionary<string, string> none) = ModSaveFilter.FilterPlayerStore(text, new[] { "nobody." });
		AssertThat(none.Count).IsEqual(0);
		AssertThat(same).IsEqual(text);
	}

	[TestCase]
	public void TestLuaClearKeysChunk()
	{
		AssertThat(ModSaveFilter.LuaClearKeys(Array.Empty<string>())).IsEqual(string.Empty);
		string chunk = ModSaveFilter.LuaClearKeys(new[] { "souls_combat.", "it's." });
		AssertThat(chunk.StartsWith("if type(RefiiModSave)=='table' then local p={\"souls_combat.\",\"it's.\",}", StringComparison.Ordinal)).IsTrue();
		AssertThat(chunk.Contains("RefiiModSave[k]=nil", StringComparison.Ordinal)).IsTrue();
		AssertThat(chunk.Contains('\n')).IsFalse();
	}

	// ---------------------------------------------------------------- the stamp

	[TestCase]
	public void TestStampBuildAndSidecarRoundTrip()
	{
		ModFingerprint set = ModFingerprint.From(2, new[] { Souls, Hats });
		SaveSidecar.ModsLayer stamp = ModSaveStamp.Build(set, Records, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fancy_hats" });

		AssertThat(stamp.Api).IsEqual(2);
		AssertThat(stamp.Digest).IsEqual(set.Digest());
		AssertThat(stamp.Set.Select(m => m.Name).ToList()).IsEqual(new List<string> { "souls_combat", "fancy_hats" });
		AssertThat(stamp.Set[0].Order).IsEqual(0);
		AssertThat(stamp.Set[1].Order).IsEqual(1);
		AssertThat(stamp.Set[0].Records).IsEqual(new List<uint> { ModSword });
		AssertThat(stamp.Set[0].Keys).IsEqual(new List<string> { "souls_combat." });
		AssertThat(stamp.Set[0].Session).IsFalse();
		AssertThat(stamp.Set[1].Session).IsTrue();

		var sidecar = new SaveSidecar
		{
			Refii = new SaveSidecar.RefiiLayer { Build = "2", Written = "2026-10-22T00:00:00Z" },
			Mods = stamp,
		};
		byte[] json = sidecar.ToJson();
		SaveSidecar back = SaveSidecar.Parse(json);

		AssertThat(back.Refii!.Build).IsEqual("2");
		AssertThat(back.Refii.PlayerState).IsEqual(new List<string> { "refii_player.txt", "refii_blob_*.bin" });
		AssertThat(back.Mods!.Digest).IsEqual(set.Digest());
		AssertThat(back.Mods.Set.Count).IsEqual(2);
		AssertThat(back.Mods.Set[0].Sha256).IsEqual(ShaA);
		AssertThat(back.Mods.Set[0].Records).IsEqual(new List<uint> { ModSword });
		AssertThat(back.Mods.Set[1].Session).IsTrue();
		AssertThat(back.Modded).IsTrue();
		AssertThat(back.ToJson()).IsEqual(json);

		string text = System.Text.Encoding.UTF8.GetString(json);
		AssertThat(text.Contains("\"schema\": \"refii-save/1\"", StringComparison.Ordinal)).IsTrue();
		AssertThat(text.Contains("\"0x11111111\"", StringComparison.Ordinal)).IsTrue();
	}

	[TestCase]
	public void TestLayersAreIndependent()
	{
		AssertThat(new SaveSidecar().Modded).IsFalse();
		SaveSidecar vanilla = SaveSidecar.Parse(System.Text.Encoding.UTF8.GetBytes("{\"schema\":\"refii-save/1\"}"));
		AssertThat(vanilla.Refii).IsNull();
		AssertThat(vanilla.Mods).IsNull();

		var refiiOnly = new SaveSidecar { Refii = new SaveSidecar.RefiiLayer { Build = "2" } };
		SaveSidecar back = SaveSidecar.Parse(refiiOnly.ToJson());
		AssertThat(back.Refii).IsNotNull();
		AssertThat(back.Mods).IsNull();
		AssertThat(back.Modded).IsFalse();

		// A later layer is ignored by this reader.
		SaveSidecar later = SaveSidecar.Parse(System.Text.Encoding.UTF8.GetBytes("{\"schema\":\"refii-save/2\",\"refii\":{\"build\":\"3\"},\"later\":{\"x\":1}}"));
		AssertThat(later.Refii!.Build).IsEqual("3");
	}

	[TestCase]
	public void TestCompareFindsMissingChangedAndAdded()
	{
		SaveSidecar.ModsLayer stamp = ModSaveStamp.Build(ModFingerprint.From(2, new[] { Souls, Hats }), Records);

		ModSaveStamp.Diff same = ModSaveStamp.Compare(stamp, ModFingerprint.From(2, new[] { Hats, Souls }));
		AssertThat(same.Same).IsTrue();

		var other = new ModFingerprint.Mod("other_mod", "Other", "1.0", new string('c', 64));
		ModSaveStamp.Diff diff = ModSaveStamp.Compare(stamp, ModFingerprint.From(2, new[] { Souls with { Version = "0.2.0" }, other }));
		AssertThat(diff.Missing.Select(m => m.Name).ToList()).IsEqual(new List<string> { "fancy_hats" });
		AssertThat(diff.Changed.Select(c => c.Was.Name).ToList()).IsEqual(new List<string> { "souls_combat" });
		AssertThat(diff.Added.Select(a => a.Name).ToList()).IsEqual(new List<string> { "other_mod" });
		AssertThat(diff.MissingRecords()).IsEqual(new HashSet<uint> { ModHat });
		AssertThat(diff.MissingKeys()).IsEqual(new List<string> { "fancy_hats." });

		// A vanilla save against any set: nothing missing.
		AssertThat(ModSaveStamp.Compare(null, ModFingerprint.From(2, new[] { Souls })).Missing.Count).IsEqual(0);
	}

	[TestCase]
	public void TestSessionModsNeverCountAsMissing()
	{
		SaveSidecar.ModsLayer stamp = ModSaveStamp.Build(
			ModFingerprint.From(2, new[] { Souls, Hats }), Records, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fancy_hats" });
		ModSaveStamp.Diff diff = ModSaveStamp.Compare(stamp, ModFingerprint.From(2, new[] { Souls }));
		AssertThat(diff.Missing.Count).IsEqual(0);
	}

	[TestCase]
	public void TestRestampCarriesHeldOfMissingModsOnly()
	{
		var previous = new SaveSidecar.ModsLayer();
		previous.Held["fancy_hats"] = new SaveSidecar.HeldRecords { Melee = ModHat };
		previous.Held["souls_combat"] = new SaveSidecar.HeldRecords { Melee = ModSword };
		previous.Held["empty_mod"] = new SaveSidecar.HeldRecords();

		SaveSidecar.ModsLayer current = ModSaveStamp.Build(ModFingerprint.From(2, new[] { Souls }), Records);
		SaveSidecar.ModsLayer next = ModSaveStamp.Restamp(previous, current);
		AssertThat(next.Held.Keys.ToList()).IsEqual(new List<string> { "fancy_hats" });
	}

	// ---------------------------------------------------------------- the words

	[TestCase]
	public void TestNoticeListsEveryModOnePerLineAndMarksTheMissing()
	{
		SaveSidecar.ModsLayer stamp = ModSaveStamp.Build(ModFingerprint.From(2, new[] { Souls, Hats }), Records);

		// Nothing missing: the heading and the list, no line about missing mods.
		ModSaveStamp.Diff same = ModSaveStamp.Compare(stamp, ModFingerprint.From(2, new[] { Hats, Souls }));
		AssertThat(ModSaveStamp.NoticeText(stamp, same))
			.IsEqual("This save was made with these mods:\nSouls Combat 0.1.0\nFancy Hats 1.2\n");

		// One missing: marked on its own line, and the one line about what is kept.
		ModSaveStamp.Diff one = ModSaveStamp.Compare(stamp, ModFingerprint.From(2, new[] { Souls }));
		string text = ModSaveStamp.NoticeText(stamp, one);
		AssertThat(text.StartsWith(
			"This save was made with these mods:\nSouls Combat 0.1.0\nFancy Hats 1.2" + ModSaveStamp.NotEnabledMark + "\n",
			StringComparison.Ordinal)).IsTrue();
		AssertThat(text.Contains("comes back when that mod is enabled again", StringComparison.Ordinal)).IsTrue();
		AssertThat(text.Split('\n').Count(line => line.EndsWith(ModSaveStamp.NotEnabledMark, StringComparison.Ordinal))).IsEqual(1);

		// Both missing: both marked.
		ModSaveStamp.Diff none = ModSaveStamp.Compare(stamp, ModFingerprint.From(2, Array.Empty<ModFingerprint.Mod>()));
		AssertThat(ModSaveStamp.NoticeText(stamp, none).Split('\n').Count(line => line.EndsWith(ModSaveStamp.NotEnabledMark, StringComparison.Ordinal))).IsEqual(2);
	}

	[TestCase]
	public void TestNoticeLeavesSessionModsOut()
	{
		SaveSidecar.ModsLayer stamp = ModSaveStamp.Build(
			ModFingerprint.From(2, new[] { Souls, Hats }), Records, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fancy_hats" });
		ModSaveStamp.Diff diff = ModSaveStamp.Compare(stamp, ModFingerprint.From(2, new[] { Souls }));
		AssertThat(ModSaveStamp.NoticeText(stamp, diff))
			.IsEqual("This save was made with these mods:\nSouls Combat 0.1.0\n");
	}


	// ---------------------------------------------------------------- the export

	[TestCase]
	public void TestExportCopiesGuestFilesOnlyAndStripsEveryModRecord()
	{
		string folder = Path.Combine(Path.GetTempPath(), "refii-modsave-" + Guid.NewGuid().ToString("N"));
		string save = Path.Combine(folder, "E0EA", "4D5307F1", "00000001", "Hero000");
		Directory.CreateDirectory(save);
		try
		{
			Synthetic().WriteFile(Path.Combine(save, "mainsave.bin"));
			File.WriteAllText(Path.Combine(save, "herosave.bin"), "hero");
			File.WriteAllText(Path.Combine(save, "refii_player.txt"), "souls_combat.level=3\n");
			File.WriteAllText(Path.Combine(save, SaveSidecar.FileName), "{}");

			string destination = SaveExport.DestinationFor(folder, "Hero000");
			AssertThat(destination.EndsWith(Path.Combine("save-export", "4D5307F1", "00000001", "Hero000"), StringComparison.Ordinal)).IsTrue();
			int removed = SaveExport.Copy(save, destination, new HashSet<uint> { ModSword, ModHat });

			AssertThat(removed).IsEqual(4);
			AssertThat(Directory.GetFiles(destination).Select(Path.GetFileName).OrderBy(n => n).ToList())
				.IsEqual(new List<string> { "herosave.bin", "mainsave.bin" });
			SaveStream copy = SaveStream.ReadFile(Path.Combine(destination, "mainsave.bin"));
			AssertThat(ModSaveFilter.RecordsIn(copy).Overlaps(new[] { ModSword, ModHat })).IsFalse();
			AssertThat(copy.Tail).IsEqual(Synthetic().Tail);
			// The original is as it was.
			AssertThat(ModSaveFilter.RecordsIn(SaveStream.ReadFile(Path.Combine(save, "mainsave.bin"))).Contains(ModSword)).IsTrue();
		}
		finally
		{
			Directory.Delete(folder, true);
		}
	}

	/// <summary>
	/// A real save, when REFII_SAVE_SAMPLE names one: parsed, re-serialised and compared byte for
	/// byte with the guest's own inflated stream. Nothing is written. Passes trivially without one.
	/// </summary>
	[TestCase]
	public void TestRealSaveRoundTripWhenGiven()
	{
		string? sample = Environment.GetEnvironmentVariable("REFII_SAVE_SAMPLE");
		if (string.IsNullOrEmpty(sample) || !File.Exists(sample))
		{
			return;
		}

		byte[] inflated = SaveStream.Inflate(File.ReadAllBytes(sample));
		SaveStream stream = SaveStream.Parse(inflated);
		AssertThat(stream.Documents.Count).IsEqual(2);
		AssertThat(stream.Documents[1].Name).IsEqual("EntityManager");
		byte[] again = stream.Serialize();
		AssertThat(again.Length).IsEqual(inflated.Length);
		AssertThat(again).IsEqual(inflated);

		// The chunks inflate back to the same bytes, and every chunk ends on a sync point: the
		// first half of the chunks alone inflate to exactly their stated raw sizes.
		byte[] file = SaveStream.Deflate(again);
		AssertThat(SaveStream.Inflate(file)).IsEqual(inflated);
		int pos = 0;
		int rawSoFar = 0;
		int chunks = 0;
		while (pos + 8 <= file.Length && rawSoFar < inflated.Length / 2)
		{
			int compressed = (file[pos] << 24) | (file[pos + 1] << 16) | (file[pos + 2] << 8) | file[pos + 3];
			rawSoFar += (file[pos + 4] << 24) | (file[pos + 5] << 16) | (file[pos + 6] << 8) | file[pos + 7];
			pos += 8 + compressed;
			chunks++;
		}

		AssertThat(chunks > 1).IsTrue();
		AssertThat(SaveStream.Inflate(file.AsSpan(0, pos).ToArray()).Length).IsEqual(rawSoFar);
	}

	// ---- the gate's commit --------------------------------------------------------------------

	/// <summary>
	/// The commit the gate posts again on Continue is the start_game event's own `commit` field:
	/// the pump action the start ran under, as the native hook writes it. A start the pump did not
	/// run carries "" and nothing is posted — never a commit remembered from an earlier post.
	/// </summary>
	[TestCase]
	public void GateRepostsTheEventsCommit()
	{
		// The root's "Load Game" row is dispatched by its bare name (one save commits directly); the fan's pick as LoadSave:N.
		var fromRow = RefiiHost.ParseFieldsPlain("save='Hero',level='Bowerstone',new_game=false,player=1,commit='Continue'");
		AssertThat(ModSaveGate.CommitOf(fromRow)).IsEqual("Continue");
		var fromFan = RefiiHost.ParseFieldsPlain("save='Hero',level='Bowerstone',new_game=false,player=1,commit='LoadSave:2'");
		AssertThat(ModSaveGate.CommitOf(fromFan)).IsEqual("LoadSave:2");

		var outsideThePump = RefiiHost.ParseFieldsPlain("save='Hero',level='Bowerstone',new_game=false,player=1,commit=''");
		AssertThat(ModSaveGate.CommitOf(outsideThePump)).IsEqual(string.Empty);
		var noField = RefiiHost.ParseFieldsPlain("save='Hero',level='Bowerstone',new_game=false,player=1");
		AssertThat(ModSaveGate.CommitOf(noField)).IsEqual(string.Empty);
	}
}
