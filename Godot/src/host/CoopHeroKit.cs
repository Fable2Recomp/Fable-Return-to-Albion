using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Refii.Host;

/// <summary>
/// Player two's own hero, as a thing that can cross the wire.
/// </summary>
/// <remarks>
/// THE LIVE HERO IS THE SOURCE, NOT THE SAVE FILE. The guest reads a save through its own
/// content path and hands us nothing on the way past; every field below is already exported off
/// the hero's live components, so capturing from the running character costs no save parsing and
/// no compatibility risk. `docs/coop_native_hero_plan.md` weighs both routes and lands here.
///
/// CAPTURE IS CONTINUOUS AND CACHED, because the moment that matters has already gone by the
/// time it is needed: joining tears the joiner's own world down, so their hero is not there to
/// read when the co-op screen asks for it. <see cref="Sample"/> keeps the last good capture
/// instead — whatever they were last playing.
///
/// The wire form is one short ASCII record, sized to fit a single datagram. It is versioned
/// because the two instances can be built from different trees; a reader that does not know the
/// version drops the record rather than guessing at its fields.
/// </remarks>
public static class CoopHeroKit
{
	/// <summary>Whether a kit carries a body — a save-picked hero states none (strength −1).</summary>
	public static bool HasLooks(in Kit kit) => kit.Known && kit.Strength >= 0f;

	/// <summary>The wire version. BUMP IT whenever a field's meaning changes.</summary>
	private const int Version = 1;

	/// <summary>Prefix of the wire record, so the channel can tell it from the toggle traffic.</summary>
	public const string Prefix = "coop.hero.kit=";

	/// <summary>
	/// Datagrams are not fragmented for us, so the record has a ceiling and the worn list is what
	/// gives way — it is the only part that grows without bound.
	/// </summary>
	private const int WireLimit = 1100;

	/// <summary>Ability ids 1..14, the range <c>HeroVitals.AbilityLevels</c> carries.</summary>
	private const int AbilityCount = 15;

	/// <summary>Everything about a hero that we can both read and re-apply.</summary>
	public readonly record struct Kit(
		bool Known,
		bool Female,
		float Morality,
		float Purity,
		int Renown,
		float Strength,
		float Skill,
		float Will,
		int[] AbilityLevels,
		uint[] Worn,
		string Melee,
		string Ranged,
		(uint Record, int Count)[] Inventory,
		string Name,
		int Money,
		float[] Experience,
		float[] TotalExperience,
		uint Face,
		float Fat,
		float Height,
		float Tan,
		uint MeleeId = 0u,
		uint RangedId = 0u)
	{
		/// <summary>The creature this hero's body is, in the guest's own naming.</summary>
		public string Creature => Female ? "CreatureHeroFemale" : "CreatureHero";
	}

	/// <summary>
	/// The two weapon def names the guest's tables take, resolved on WHICHEVER side has a GDB
	/// view. The sender may not: a joiner builds its kit on the front end, before any world has
	/// loaded a database, so its names come through empty and the record ids ride beside them.
	/// The receiver, in a running world, turns the ids into names itself.
	/// </summary>
	public static (string Melee, string Ranged) WeaponNames(in Kit kit)
	{
		string melee = kit.Melee ?? string.Empty;
		string ranged = kit.Ranged ?? string.Empty;
		if (RecordName is { } name)
		{
			if (melee.Length == 0 && kit.MeleeId != 0u) melee = name(kit.MeleeId) ?? string.Empty;
			if (ranged.Length == 0 && kit.RangedId != 0u) ranged = name(kit.RangedId) ?? string.Empty;
		}

		return (melee, ranged);
	}

	/// <summary>
	/// GDB record id → def name, supplied by the shell: the weapon tables the guest equips from
	/// take NAMES, and the equipped slots hold record ids, which only the host's GDB view can
	/// turn back into names. Null until the shell sets it; weapons then go uncaptured.
	/// </summary>
	public static Func<uint, string>? RecordName { get; set; }

	/// <summary>
	/// This instance's display name, off the node — independent of whether a hero was ever
	/// captured. A joiner that comes in from the title has a name and no hero.
	/// </summary>
	public static string LocalName =>
		NetBridge.Self() is { } self
			? (self.DisplayName.Length != 0 ? self.DisplayName
				: self.FriendCode.Length != 0 ? self.FriendCode
				: "…" + self.PeerId[^Math.Min(8, self.PeerId.Length)..])
			: string.Empty;

	/// <summary>The last hero seen alive on this instance, or an unknown kit.</summary>
	public static Kit Local { get; private set; }

	/// <summary>What the peer last told us about theirs.</summary>
	public static Kit Remote { get; private set; }

	/// <summary>
	/// Takes a capture if a hero is up. Cheap enough to call on a timer; does nothing and keeps
	/// the previous capture when no hero is loaded, which is every moment in the front end.
	/// </summary>
	/// <param name="appearanceOnly">
	/// Keep the standing kit's stats (money, experience, ranks, morality…) and take only what the
	/// body shows and carries — outfit, weapons, inventory, looks. The joiner's copy of its own
	/// hero is not the authority for its stats once the host's world owns it, and its money read
	/// there came back as 32768. The weapons and the inventory ARE taken live: a change of sword
	/// or coat on the joiner is stated here or nowhere, and the host's copy only ever re-equips
	/// from this kit. An empty weapon slot is never taken — the joiner's own guest drops its
	/// weapons on a client until the host's replicas are attached, and a kit sampled in that gap
	/// would unarm player two on the host.
	/// </param>
	public static void Sample(bool appearanceOnly = false)
	{
		uint entity = HostBridge.HeroEntityAddress();
		if (entity == 0u)
		{
			return;
		}

		Kit before = Local;

		HostBridge.HeroVitals vitals = HostBridge.GetHeroVitals();
		if (!vitals.Known)
		{
			return;
		}

		List<HostBridge.GuestWornItem> worn = HostBridge.GetEntityWornItems(entity);

		// Gender comes off the worn walk rather than a component read of our own: the same
		// component-12 test the guest uses is already folded into every item's flag there.
		bool female = worn.Count > 0 && worn[0].Female;

		var hashes = new uint[worn.Count];
		for (int i = 0; i < worn.Count; ++i)
		{
			hashes[i] = worn[i].Hash;
		}

		var abilities = new int[AbilityCount];
		int[]? levels = vitals.AbilityLevels;
		if (levels is not null)
		{
			for (int id = 0; id < AbilityCount && id < levels.Length; ++id)
			{
				abilities[id] = levels[id];
			}
		}

		// THE WEAPONS BY NAME. `sub_8233D090` equips player two from two name tables, so the kit
		// carries the def names the host's GDB gives for the two equipped record ids.
		string melee = string.Empty;
		string ranged = string.Empty;
		uint meleeId = 0u;
		uint rangedId = 0u;
		if (HostBridge.HeroWeapons() is { } slots)
		{
			meleeId = slots.Melee;
			rangedId = slots.Ranged;
			if (RecordName is { } name)
			{
				melee = slots.Melee != 0u ? name(slots.Melee) ?? string.Empty : string.Empty;
				ranged = slots.Ranged != 0u ? name(slots.Ranged) ?? string.Empty : string.Empty;
			}
		}

		// THE WHOLE INVENTORY, as record id and count — the currency the giver keys on. A
		// record id repeats when the first still holds instance data, so counts are summed.
		var counts = new Dictionary<uint, int>();
		foreach (HostBridge.InventoryRecord record in HostBridge.GetHeroInventory())
		{
			counts[record.RecordId] = counts.GetValueOrDefault(record.RecordId) + Math.Max(1, record.Count);
		}

		var inventory = new (uint, int)[counts.Count];
		int at = 0;
		foreach (KeyValuePair<uint, int> pair in counts)
		{
			inventory[at++] = (pair.Key, pair.Value);
		}

		// THE REST OF THE HERO. Money and the two experience pools are already in the vitals;
		// the face record and the three body scalars come off the looks export.
		float[] pools = new float[4];
		float[] totals = new float[4];
		for (int i = 0; i < 4; ++i)
		{
			pools[i] = vitals.ExperienceAvailable is { Length: > 0 } avail && i < avail.Length ? avail[i] : 0f;
			totals[i] = vitals.ExperienceEarned is { Length: > 0 } earned && i < earned.Length ? earned[i] : 0f;
		}

		(uint Face, float Fat, float Height, float Tan) looks = HostBridge.HeroLooks() ?? (0u, 0f, 0f, 0f);

		bool keep = appearanceOnly && before.Known;
		Local = new Kit(
			Known: true,
			Female: keep ? before.Female : female,
			Morality: keep ? before.Morality : vitals.MoralityBase,
			Purity: keep ? before.Purity : vitals.PurityBase,
			Renown: keep ? before.Renown : vitals.RenownBase,
			Strength: keep ? before.Strength : vitals.Strength,
			Skill: keep ? before.Skill : vitals.Skill,
			Will: keep ? before.Will : vitals.Will,
			AbilityLevels: keep ? before.AbilityLevels : abilities,
			Worn: hashes,
			// Appearance is the outfit, the weapons and the inventory; an empty slot keeps the
			// record's weapon (see the parameter note).
			Melee: keep && meleeId == 0u ? before.Melee : melee,
			Ranged: keep && rangedId == 0u ? before.Ranged : ranged,
			Inventory: inventory,
			Name: LocalName,
			Money: keep ? before.Money : vitals.Gold,
			Experience: keep ? before.Experience : pools,
			TotalExperience: keep ? before.TotalExperience : totals,
			Face: keep && looks.Face == 0u ? before.Face : looks.Face,
			Fat: keep ? before.Fat : looks.Fat,
			Height: keep ? before.Height : looks.Height,
			Tan: keep ? before.Tan : looks.Tan,
			MeleeId: keep && meleeId == 0u ? before.MeleeId : meleeId,
			RangedId: keep && rangedId == 0u ? before.RangedId : rangedId);
	}

	/// <summary>
	/// Where the game's saves live — `user_data_root`, set by the shell. Null until it is.
	/// </summary>
	public static string? UserDataRoot { get; set; }

	/// <summary>The save name a full kit was last read for, so a pick is read once.</summary>
	private static string _sampledSave = string.Empty;

	/// <summary>
	/// Builds <see cref="Local"/> from the save the player chose on the co-op screen — read off
	/// that save's OWN FILES by <see cref="Refii.Save.Engine_SaveHero"/>, never by loading its
	/// world: body, clothes, weapons, inventory, stats, experience, money, face. A different pick
	/// replaces it; the same pick is not re-read.
	/// </summary>
	public static void SampleChosenSave()
	{
		string? name = HostBridge.CoopHeroSaveName();
		if (name is null || name == _sampledSave || UserDataRoot is null)
		{
			return;
		}

		string? directory = Refii.Save.Engine_SaveHero.FindSaveDirectory(UserDataRoot, name);
		Refii.Save.Engine_SaveHero.Hero? hero = directory is null ? null : Refii.Save.Engine_SaveHero.Read(directory);
		if (hero is null)
		{
			Godot.GD.Print($"[coop-kit] chosen save '{name}' could not be read " +
				$"({(directory is null ? "no directory under " + UserDataRoot : directory)})");
			_sampledSave = name;
			return;
		}

		_sampledSave = name;
		string melee = hero.Melee != 0u && RecordName is { } n1 ? n1(hero.Melee) : string.Empty;
		string ranged = hero.Ranged != 0u && RecordName is { } n2 ? n2(hero.Ranged) : string.Empty;

		Local = new Kit(
			Known: true,
			Female: hero.Female,
			Morality: hero.Morality,
			Purity: hero.Purity,
			Renown: hero.Renown,
			Strength: hero.Strength, Skill: hero.Skill, Will: hero.Will,
			AbilityLevels: hero.AbilityLevels,
			Worn: hero.Worn.ToArray(),
			Melee: melee,
			Ranged: ranged,
			Inventory: hero.Inventory.ToArray(),
			Name: LocalName,
			Money: hero.Money,
			Experience: hero.Experience,
			TotalExperience: hero.TotalExperience,
			Face: hero.Face, Fat: hero.Fat, Height: hero.Height, Tan: hero.Tan,
			MeleeId: hero.Melee, RangedId: hero.Ranged);

		Godot.GD.Print($"[coop-kit] chosen save '{name}' read: {hero.Worn.Count} worn, " +
			$"{hero.Inventory.Count} inventory record(s), melee '{melee}' (0x{hero.Melee:X8}), " +
			$"ranged '{ranged}' (0x{hero.Ranged:X8}), " +
			$"money {hero.Money}, renown {hero.Renown}, face 0x{hero.Face:X8}");
	}

	/// <summary>
	/// The wire record for <see cref="Local"/>, or null when nothing has been captured.
	///
	/// Fields are single letters and values are hex or invariant decimals, so the whole hero fits
	/// a datagram with room for a long wardrobe. Worn items are dropped from the END when the
	/// record would overrun — a hero missing a hat is better than a hero that never arrives.
	/// </summary>
	public static string? Encode()
	{
		Kit kit = Local;
		var text = new StringBuilder(WireLimit);
		text.Append(Prefix).Append(Version);

		// A NAME TRAVELS WITHOUT A HERO. `h=0` says "no hero behind this", so the peer takes the
		// name and nothing else — it must not mistake a hero-less kit for a captured one.
		text.Append(";h=").Append(kit.Known ? '1' : '0');
		string name = LocalName;
		if (name.Length != 0 && name.IndexOfAny(new[] { ';', ',', '=' }) < 0)
		{
			text.Append(";n=").Append(name);
		}

		if (!kit.Known)
		{
			return text.ToString();
		}

		text.Append(";g=").Append(kit.Female ? '1' : '0');
		Number(text, ";m=", kit.Morality);
		Number(text, ";p=", kit.Purity);
		text.Append(";r=").Append(kit.Renown.ToString(CultureInfo.InvariantCulture));
		Number(text, ";s=", kit.Strength);
		Number(text, ";k=", kit.Skill);
		Number(text, ";l=", kit.Will);

		text.Append(";a=");
		for (int i = 0; i < kit.AbilityLevels.Length; ++i)
		{
			if (i != 0)
			{
				text.Append(',');
			}

			text.Append(kit.AbilityLevels[i].ToString("X", CultureInfo.InvariantCulture));
		}

		// Names are plain identifiers with no ';' or ','; anything else is dropped rather than
		// allowed to break the record.
		if (kit.Melee.Length != 0 && kit.Melee.IndexOfAny(new[] { ';', ',', '=' }) < 0)
		{
			text.Append(";wm=").Append(kit.Melee);
		}

		if (kit.Ranged.Length != 0 && kit.Ranged.IndexOfAny(new[] { ';', ',', '=' }) < 0)
		{
			text.Append(";wr=").Append(kit.Ranged);
		}

		// The record ids too: the receiver resolves a name the sender could not.
		if (kit.MeleeId != 0u) text.Append(";wmi=").Append(kit.MeleeId.ToString("X8", CultureInfo.InvariantCulture));
		if (kit.RangedId != 0u) text.Append(";wri=").Append(kit.RangedId.ToString("X8", CultureInfo.InvariantCulture));

		text.Append(";$=").Append(kit.Money.ToString(CultureInfo.InvariantCulture));
		Number(text, ";fa=", kit.Fat);
		Number(text, ";he=", kit.Height);
		Number(text, ";ta=", kit.Tan);
		if (kit.Face != 0u)
		{
			text.Append(";f=").Append(kit.Face.ToString("X8", CultureInfo.InvariantCulture));
		}

		text.Append(";e=").Append(Floats(kit.Experience));
		text.Append(";t=").Append(Floats(kit.TotalExperience));

		text.Append(";w=");
		int kept = 0;
		foreach (uint hash in kit.Worn)
		{
			// +9 covers the separator and the eight hex digits about to be written.
			if (text.Length + 9 > WireLimit)
			{
				break;
			}

			if (kept++ != 0)
			{
				text.Append(',');
			}

			text.Append(hash.ToString("X8", CultureInfo.InvariantCulture));
		}

		return text.ToString();
	}

	/// <summary>
	/// Reads a peer's record into <see cref="Remote"/>. Returns false and changes nothing for
	/// anything malformed or of another version — the bytes come off the network, so a bad record
	/// is a thing to drop, never a thing to throw on.
	/// </summary>
	public static bool Decode(string message)
	{
		if (message is null || !message.StartsWith(Prefix, StringComparison.Ordinal))
		{
			return false;
		}

		string[] fields = message[Prefix.Length..].Split(';');
		if (fields.Length == 0 || !int.TryParse(fields[0], NumberStyles.Integer,
			CultureInfo.InvariantCulture, out int version) || version != Version)
		{
			return false;
		}

		bool female = false;
		float morality = 0f, purity = 0f, strength = 0f, skill = 0f, will = 0f;
		int renown = 0;
		var abilities = new int[AbilityCount];
		uint[] worn = Array.Empty<uint>();
		string melee = string.Empty;
		string ranged = string.Empty;
		string peer = string.Empty;
		bool hero = true;
		int money = 0;
		float fat = 0f, height = 0f, tan = 0f;
		uint face = 0u;
		float[] pools = new float[4];
		float[] totals = new float[4];
		uint meleeId = 0u;
		uint rangedId = 0u;

		for (int i = 1; i < fields.Length; ++i)
		{
			string field = fields[i];

			// KEYS ARE ONE TO THREE LETTERS. A guard that admitted only `x=` rejected every kit
			// carrying `wm=`, `fa=`, `he=`, `ta=` — the whole record, names included — and the
			// peer's name never landed.
			int eq = field.IndexOf('=');
			if (eq < 1 || eq > 3)
			{
				return false;
			}

			string value = field[(eq + 1)..];
			if (field.StartsWith("wmi=", StringComparison.Ordinal)) { uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out meleeId); continue; }
			if (field.StartsWith("wri=", StringComparison.Ordinal)) { uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rangedId); continue; }
			if (field.StartsWith("wm=", StringComparison.Ordinal)) { melee = field[3..]; continue; }
			if (field.StartsWith("wr=", StringComparison.Ordinal)) { ranged = field[3..]; continue; }
			if (field.StartsWith("n=", StringComparison.Ordinal)) { peer = field[2..]; continue; }
			if (field.StartsWith("h=", StringComparison.Ordinal)) { hero = field[2..] == "1"; continue; }
			if (field.StartsWith("$=", StringComparison.Ordinal)) { int.TryParse(field[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out money); continue; }
			if (field.StartsWith("fa=", StringComparison.Ordinal)) { Real(field[3..], out fat); continue; }
			if (field.StartsWith("he=", StringComparison.Ordinal)) { Real(field[3..], out height); continue; }
			if (field.StartsWith("ta=", StringComparison.Ordinal)) { Real(field[3..], out tan); continue; }
			if (field.StartsWith("f=", StringComparison.Ordinal)) { uint.TryParse(field[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out face); continue; }
			if (field.StartsWith("e=", StringComparison.Ordinal)) { FloatList(field[2..], pools); continue; }
			if (field.StartsWith("t=", StringComparison.Ordinal)) { FloatList(field[2..], totals); continue; }
			if (eq != 1)
			{
				// A longer key this version does not know: skipped, not fatal.
				continue;
			}

			switch (field[0])
			{
				case 'g': female = value == "1"; break;
				case 'm': if (!Real(value, out morality)) return false; break;
				case 'p': if (!Real(value, out purity)) return false; break;
				case 'r':
					if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
						out renown))
					{
						return false;
					}

					break;
				case 's': if (!Real(value, out strength)) return false; break;
				case 'k': if (!Real(value, out skill)) return false; break;
				case 'l': if (!Real(value, out will)) return false; break;
				case 'a':
					if (!Levels(value, abilities))
					{
						return false;
					}

					break;
				case 'w':
					if (!Hashes(value, out worn))
					{
						return false;
					}

					break;

				// An unknown letter is a FIELD FROM A NEWER BUILD, not a broken record. The
				// version already gates incompatible meanings, so skipping is the right answer.
				default: break;
			}
		}

		// The inventory arrives in its own chunked messages and is merged in by DecodeInventory;
		// a fresh kit keeps whatever chunks have already landed for this peer.
		Remote = new Kit(hero, female, morality, purity, renown, strength, skill, will,
			abilities, worn, melee, ranged, Remote.Inventory ?? Array.Empty<(uint, int)>(), peer,
			money, pools, totals, face, fat, height, tan, meleeId, rangedId);
		return true;
	}

	/// <summary>
	/// The record's generation: an FNV-1a hash over everything the pump applies — outfit,
	/// inventory, weapon ids, stats, face, build. The pump applies a generation once per body;
	/// a changed record is a new number, a restated one is the same number and does nothing.
	/// Never 0, which the pump reads as "nothing to apply".
	/// </summary>
	public static uint Generation(in Kit kit)
	{
		if (!kit.Known)
		{
			return 0u;
		}

		uint h = 2166136261u;
		void Mix(uint v)
		{
			for (int i = 0; i < 4; ++i)
			{
				h ^= (v >> (8 * i)) & 0xFFu;
				h *= 16777619u;
			}
		}

		Mix(kit.Female ? 1u : 0u);
		foreach (uint id in kit.Worn ?? Array.Empty<uint>()) Mix(id);
		foreach ((uint record, int count) in kit.Inventory ?? Array.Empty<(uint, int)>()) { Mix(record); Mix((uint)count); }
		Mix(kit.MeleeId); Mix(kit.RangedId);
		Mix((uint)kit.Money); Mix((uint)kit.Renown);
		Mix(BitConverter.SingleToUInt32Bits(kit.Morality)); Mix(BitConverter.SingleToUInt32Bits(kit.Purity));
		Mix(BitConverter.SingleToUInt32Bits(kit.Strength)); Mix(BitConverter.SingleToUInt32Bits(kit.Skill)); Mix(BitConverter.SingleToUInt32Bits(kit.Will));
		Mix(BitConverter.SingleToUInt32Bits(kit.Fat)); Mix(BitConverter.SingleToUInt32Bits(kit.Height)); Mix(BitConverter.SingleToUInt32Bits(kit.Tan));
		Mix(kit.Face);
		foreach (int level in kit.AbilityLevels ?? Array.Empty<int>()) Mix((uint)level);
		return h == 0u ? 1u : h;
	}

	/// <summary>
	/// A joiner's weapon pick goes into the record: the host equips it and the replica comes
	/// back (`CoopHeroHeld`). <paramref name="weaponClass"/> is 1 melee, 2 ranged.
	/// </summary>
	public static bool SetLocalWeapon(uint record, int weaponClass)
	{
		if (!Local.Known || record == 0u || (weaponClass != 1 && weaponClass != 2))
		{
			return false;
		}

		string name = RecordName is { } resolve ? resolve(record) ?? string.Empty : string.Empty;
		Local = weaponClass == 1
			? Local with { MeleeId = record, Melee = name }
			: Local with { RangedId = record, Ranged = name };
		Godot.GD.Print($"[coop-kit] local hero's {(weaponClass == 1 ? "melee" : "ranged")} weapon is " +
			$"now 0x{record:X8} '{name}' — the host equips it from the record");
		return true;
	}

	/// <summary>Drops everything learned from the peer — on a lost connection, so a later join starts clean.</summary>
	public static void ForgetRemote()
	{
		Remote = default;
		_inventoryChunks.Clear();
		_inventoryTotal = 0;
	}

	/// <summary>Prefix of one inventory chunk: <c>coop.hero.inv=&lt;i&gt;/&lt;n&gt;:&lt;hex&gt;:&lt;count&gt;,…</c>.</summary>
	public const string InventoryPrefix = "coop.hero.inv=";

	/// <summary>Records per chunk — keeps a chunk inside one datagram at ~12 bytes a record.</summary>
	private const int InventoryChunk = 80;

	/// <summary>
	/// The local inventory as chunk messages. Each is restated on its own, so a lost datagram
	/// costs one chunk's restatement; the receiver applies nothing until every chunk is in.
	/// </summary>
	public static List<string> EncodeInventory()
	{
		var chunks = new List<string>();
		Kit kit = Local;
		if (!kit.Known || kit.Inventory is null || kit.Inventory.Length == 0)
		{
			return chunks;
		}

		int total = (kit.Inventory.Length + InventoryChunk - 1) / InventoryChunk;
		for (int i = 0; i < total; ++i)
		{
			var text = new StringBuilder(WireLimit);
			text.Append(InventoryPrefix).Append(i).Append('/').Append(total).Append(':');
			int from = i * InventoryChunk;
			int to = Math.Min(kit.Inventory.Length, from + InventoryChunk);
			for (int k = from; k < to; ++k)
			{
				if (k != from)
				{
					text.Append(',');
				}

				text.Append(kit.Inventory[k].Record.ToString("X8", CultureInfo.InvariantCulture))
					.Append(':').Append(kit.Inventory[k].Count.ToString(CultureInfo.InvariantCulture));
			}

			chunks.Add(text.ToString());
		}

		return chunks;
	}

	private static readonly Dictionary<int, (uint, int)[]> _inventoryChunks = new();
	private static int _inventoryTotal;

	/// <summary>
	/// Merges one chunk of the peer's inventory. Returns true when the whole inventory has just
	/// become complete, so the caller can hand it on once rather than per chunk.
	/// </summary>
	public static bool DecodeInventory(string message)
	{
		if (!message.StartsWith(InventoryPrefix, StringComparison.Ordinal))
		{
			return false;
		}

		string body = message[InventoryPrefix.Length..];
		int colon = body.IndexOf(':');
		int slash = body.IndexOf('/');
		if (colon < 0 || slash < 0 || slash > colon ||
			!int.TryParse(body[..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ||
			!int.TryParse(body[(slash + 1)..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out int total) ||
			total <= 0 || index < 0 || index >= total)
		{
			return false;
		}

		if (total != _inventoryTotal)
		{
			_inventoryChunks.Clear();
			_inventoryTotal = total;
		}

		var records = new List<(uint, int)>();
		string list = body[(colon + 1)..];
		if (list.Length != 0)
		{
			foreach (string entry in list.Split(','))
			{
				int at = entry.IndexOf(':');
				if (at <= 0 ||
					!uint.TryParse(entry[..at], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint record) ||
					!int.TryParse(entry[(at + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
				{
					return false;
				}

				records.Add((record, count));
			}
		}

		bool wasComplete = _inventoryChunks.Count == _inventoryTotal;
		_inventoryChunks[index] = records.ToArray();
		if (_inventoryChunks.Count != _inventoryTotal || wasComplete)
		{
			return false;
		}

		var all = new List<(uint, int)>();
		for (int i = 0; i < _inventoryTotal; ++i)
		{
			all.AddRange(_inventoryChunks[i]);
		}

		Remote = Remote with { Inventory = all.ToArray() };
		return true;
	}

	/// <summary>
	/// The inventory as the pump's <c>CoopHeroGive:</c> wants it: ONE list, the whole set. The
	/// action is in-process and uncapped; the wire's chunks are <see cref="EncodeInventory"/>'s.
	/// Two give actions in one tick each REPLACED the pump's set, so only the last chunk was
	/// ever given and player two's ranged weapon never reached their inventory on the host.
	/// </summary>
	public static string? GiveList(in Kit kit)
	{
		if (kit.Inventory is null)
		{
			return null;
		}

		// THE DRAWN WEAPONS TOO. An equipped weapon is a HELD ITEM ENTITY, not an inventory
		// record — the save's InventoryComponent and the live inventory walk both leave it out —
		// so the give never put it in player two's component 13, the equip found nothing held,
		// and the weapons fold had nothing to list. Given here as one of each, unless the list
		// already carries the record.
		var records = new List<(uint Record, int Count)>(kit.Inventory);
		foreach (uint weapon in new[] { kit.MeleeId, kit.RangedId })
		{
			if (weapon != 0u && !records.Exists(r => r.Record == weapon))
			{
				records.Add((weapon, 1));
			}
		}

		if (records.Count == 0)
		{
			return null;
		}

		var text = new StringBuilder();
		for (int k = 0; k < records.Count; ++k)
		{
			if (k != 0)
			{
				text.Append(',');
			}

			text.Append(records[k].Record.ToString("X8", CultureInfo.InvariantCulture))
				.Append(':').Append(records[k].Count.ToString(CultureInfo.InvariantCulture));
		}

		return text.ToString();
	}

	/// <summary>
	/// The ability ranks as the pump's <c>CoopHeroSkills:</c> action wants them — ids 0..14 in
	/// order, hex, comma separated. Null when nothing has been captured.
	/// </summary>
	public static string? Skills(in Kit kit)
	{
		if (!kit.Known || kit.AbilityLevels.Length == 0)
		{
			return null;
		}

		var text = new StringBuilder(64);
		for (int i = 0; i < kit.AbilityLevels.Length; ++i)
		{
			if (i != 0)
			{
				text.Append(',');
			}

			text.Append(kit.AbilityLevels[i].ToString("X", CultureInfo.InvariantCulture));
		}

		return text.ToString();
	}

	/// <summary>
	/// The body shape as the pump's <c>CoopHeroBuild:</c> action wants it — strength, skill, will
	/// as invariant decimals. These are the MORPH percentages in [0,1], how the hero looks; the
	/// experience pools are a different thing entirely.
	/// </summary>
	public static string Build(in Kit kit) =>
		string.Create(CultureInfo.InvariantCulture,
			$"{kit.Strength:0.####},{kit.Skill:0.####},{kit.Will:0.####},{kit.Fat:0.####},{kit.Height:0.####},{kit.Tan:0.####}");

	/// <summary>The worn def hashes as the pump's <c>CoopHeroWear:</c> action wants them.</summary>
	public static string WornList(in Kit kit)
	{
		var text = new StringBuilder(kit.Worn.Length * 9);
		for (int i = 0; i < kit.Worn.Length; ++i)
		{
			if (i != 0)
			{
				text.Append(',');
			}

			text.Append(kit.Worn[i].ToString("X8", CultureInfo.InvariantCulture));
		}

		return text.ToString();
	}

	private static string Floats(float[]? values)
	{
		if (values is null)
		{
			return string.Empty;
		}

		var text = new StringBuilder(48);
		for (int i = 0; i < values.Length; ++i)
		{
			if (i != 0)
			{
				text.Append(',');
			}

			text.Append(values[i].ToString("0.###", CultureInfo.InvariantCulture));
		}

		return text.ToString();
	}

	private static void FloatList(string text, float[] into)
	{
		if (text.Length == 0)
		{
			return;
		}

		string[] parts = text.Split(',');
		for (int i = 0; i < parts.Length && i < into.Length; ++i)
		{
			Real(parts[i], out into[i]);
		}
	}

	/// <summary>
	/// The HeroSave block as the pump's <c>CoopHeroStats:</c> wants it — the fields
	/// `sub_82440F78` reads, in its order: money, gender (2 = male), morality, purity, renown,
	/// fifteen ability ranks, four experience pools, four totals.
	/// </summary>
	public static string Stats(in Kit kit)
	{
		var text = new StringBuilder(160);
		text.Append(kit.Money.ToString(CultureInfo.InvariantCulture));
		text.Append(',').Append(kit.Female ? '1' : '2');
		text.Append(',').Append(kit.Morality.ToString("0.###", CultureInfo.InvariantCulture));
		text.Append(',').Append(kit.Purity.ToString("0.###", CultureInfo.InvariantCulture));
		text.Append(',').Append(kit.Renown.ToString(CultureInfo.InvariantCulture));
		for (int i = 0; i < AbilityCount; ++i)
		{
			text.Append(',').Append((i < kit.AbilityLevels.Length ? kit.AbilityLevels[i] : 0).ToString(CultureInfo.InvariantCulture));
		}

		for (int i = 0; i < 4; ++i)
		{
			text.Append(',').Append((kit.Experience is { Length: > 0 } e && i < e.Length ? e[i] : 0f).ToString("0.###", CultureInfo.InvariantCulture));
		}

		for (int i = 0; i < 4; ++i)
		{
			text.Append(',').Append((kit.TotalExperience is { Length: > 0 } t && i < t.Length ? t[i] : 0f).ToString("0.###", CultureInfo.InvariantCulture));
		}

		return text.ToString();
	}

	private static void Number(StringBuilder text, string tag, float value) =>
		text.Append(tag).Append(value.ToString("0.###", CultureInfo.InvariantCulture));

	private static bool Real(string text, out float value) =>
		float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

	private static bool Levels(string text, int[] into)
	{
		if (text.Length == 0)
		{
			return true;
		}

		string[] parts = text.Split(',');
		for (int i = 0; i < parts.Length && i < into.Length; ++i)
		{
			if (!int.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture,
				out int level))
			{
				return false;
			}

			into[i] = level;
		}

		return true;
	}

	private static bool Hashes(string text, out uint[] into)
	{
		if (text.Length == 0)
		{
			into = Array.Empty<uint>();
			return true;
		}

		string[] parts = text.Split(',');
		into = new uint[parts.Length];
		for (int i = 0; i < parts.Length; ++i)
		{
			if (!uint.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture,
				out into[i]))
			{
				into = Array.Empty<uint>();
				return false;
			}
		}

		return true;
	}
}
