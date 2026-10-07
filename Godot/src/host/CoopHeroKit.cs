using System;
using System.Collections.Generic;

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
/// The wire form and the pump's form are both the HeroState record: <see cref="CoopHeroState"/>
/// serializes a <see cref="Kit"/>, versions it, fragments it and acknowledges it.
/// </remarks>
public static class CoopHeroKit
{
	/// <summary>Whether a kit carries a body — a save-picked hero states none (strength −1).</summary>
	public static bool HasLooks(in Kit kit) => kit.Known && kit.Strength >= 0f;

	/// <summary>Ability ids 1..14, the range <c>HeroVitals.AbilityLevels</c> carries.</summary>
	private const int AbilityCount = 15;

	/// <summary>Everything about a hero that we can both read and re-apply. Weapons are GDB record ids.</summary>
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
	/// GDB record id → def name, supplied by the shell, for the logs. Null until the shell sets
	/// it, or when the instance has no GDB view (a joiner on the front end).
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

	/// <summary>What the peer last stated about theirs — set by <see cref="CoopHeroState.Receive"/>.</summary>
	public static Kit Remote { get; internal set; }

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

		// THE WEAPONS BY RECORD ID, off the two equipped slots.
		uint meleeId = 0u;
		uint rangedId = 0u;
		if (HostBridge.HeroWeapons() is { } slots)
		{
			meleeId = slots.Melee;
			rangedId = slots.Ranged;

			// A PENDING PICK OUTRANKS THE LIVE SLOT until the slot reports it: the live slot
			// keeps the old weapon until the host's equip and the replica land, and a sample in
			// that gap would restate the old record and undo the pick.
			if (_pendingMelee != 0u)
			{
				if (meleeId == _pendingMelee) _pendingMelee = 0u;
				else meleeId = _pendingMelee;
			}

			if (_pendingRanged != 0u)
			{
				if (rangedId == _pendingRanged) _pendingRanged = 0u;
				else rangedId = _pendingRanged;
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

		// An empty worn walk or inventory read on a standing hero is a read that missed (the
		// body mid-rebuild, the component not yet populated), not a hero that owns nothing:
		// the previous capture stands until a non-empty one replaces it.
		if (keep && hashes.Length == 0 && before.Worn is { Length: > 0 })
		{
			hashes = before.Worn;
		}

		if (keep && inventory.Length == 0 && before.Inventory is { Length: > 0 })
		{
			inventory = before.Inventory;
		}

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
			Inventory: inventory,
			Name: LocalName,
			Money: keep ? before.Money : vitals.Gold,
			Experience: keep ? before.Experience : pools,
			TotalExperience: keep ? before.TotalExperience : totals,
			Face: keep && looks.Face == 0u ? before.Face : looks.Face,
			Fat: keep ? before.Fat : looks.Fat,
			Height: keep ? before.Height : looks.Height,
			Tan: keep ? before.Tan : looks.Tan,
			// Appearance is the outfit, the weapons and the inventory; an empty slot keeps the
			// record's weapon (see the parameter note).
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
	/// Whether <see cref="Local"/> is the save the player chose on the co-op screen. A live
	/// capture from before the join is a hero too, but not the one X offers: that comes from
	/// the chosen save or nowhere.
	/// </summary>
	public static bool ChosenSaveRead { get; private set; }

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
			ChosenSaveRead = false;
			return;
		}

		_sampledSave = name;
		ChosenSaveRead = true;
		_pendingMelee = 0u;
		_pendingRanged = 0u;

		Local = new Kit(
			Known: true,
			Female: hero.Female,
			Morality: hero.Morality,
			Purity: hero.Purity,
			Renown: hero.Renown,
			Strength: hero.Strength, Skill: hero.Skill, Will: hero.Will,
			AbilityLevels: hero.AbilityLevels,
			Worn: hero.Worn.ToArray(),
			Inventory: hero.Inventory.ToArray(),
			Name: LocalName,
			Money: hero.Money,
			Experience: hero.Experience,
			TotalExperience: hero.TotalExperience,
			Face: hero.Face, Fat: hero.Fat, Height: hero.Height, Tan: hero.Tan,
			MeleeId: hero.Melee, RangedId: hero.Ranged);

		Godot.GD.Print($"[coop-kit] chosen save '{name}' read: {hero.Worn.Count} worn, " +
			$"{hero.Inventory.Count} inventory record(s), melee '{Name(hero.Melee)}' (0x{hero.Melee:X8}), " +
			$"ranged '{Name(hero.Ranged)}' (0x{hero.Ranged:X8}), " +
			$"money {hero.Money}, renown {hero.Renown}, face 0x{hero.Face:X8}");
	}

	/// <summary>A record's def name for a log line; empty without a GDB view.</summary>
	private static string Name(uint record) =>
		record != 0u && RecordName is { } resolve ? resolve(record) ?? string.Empty : string.Empty;

	/// <summary>
	/// A joiner's weapon pick goes into the record: the host equips it and the replica comes
	/// back (`CoopHeroHeld`). <paramref name="weaponClass"/> is 1 melee, 2 ranged. The changed
	/// kit reaches the peer on the next HeroState tick, as a new version.
	/// </summary>
	public static bool SetLocalWeapon(uint record, int weaponClass)
	{
		if (!Local.Known || record == 0u || (weaponClass != 1 && weaponClass != 2))
		{
			return false;
		}

		if (weaponClass == 1)
		{
			_pendingMelee = record;
			Local = Local with { MeleeId = record };
		}
		else
		{
			_pendingRanged = record;
			Local = Local with { RangedId = record };
		}

		Godot.GD.Print($"[coop-kit] local hero's {(weaponClass == 1 ? "melee" : "ranged")} weapon is " +
			$"now 0x{record:X8} '{Name(record)}' — the host equips it from the record");
		return true;
	}

	/// <summary>
	/// A pick the live weapon slot has not reported yet. <see cref="Sample"/> keeps it over the
	/// slot's record until the slot holds it, then drops it. 0 when nothing is pending.
	/// </summary>
	private static uint _pendingMelee;

	private static uint _pendingRanged;

	/// <summary>Drops everything learned from the peer — on a lost connection, so a later join starts clean.</summary>
	public static void ForgetRemote() => Remote = default;

	/// <summary>
	/// Drops every piece of session state: the peer's kit, the chosen save and the kit read from
	/// it, pending picks. A live capture of this instance's own hero survives — it belongs to
	/// the player, not the session. The protocol's own state is <see cref="CoopHeroState.Reset"/>'s.
	/// </summary>
	public static void ResetSession()
	{
		ForgetRemote();
		if (ChosenSaveRead)
		{
			Local = default;
		}

		_sampledSave = string.Empty;
		ChosenSaveRead = false;
		_pendingMelee = 0u;
		_pendingRanged = 0u;
	}
}
