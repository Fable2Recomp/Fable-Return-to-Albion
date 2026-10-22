using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Refii.Save;

namespace Refii.Mods;

/// <summary>
/// Takes a set of mods' records out of a save and puts them back: the inventory items whose
/// record id a missing mod added, the worn modifiers that pointed at them, the last-wielded
/// weapon ids among them, and the player store's keys under the mod's prefixes. Pure: works on
/// a <see cref="SaveStream"/>, a store's text and a kit's hero; the files are
/// <see cref="ModSaveGate"/>'s.
/// </summary>
/// <remarks>
/// WHAT THE SAVE HOLDS, as the guest writes it (verified against real saves, see
/// <see cref="Engine_SaveHero"/>): every entity's inventory is `EC[Hash=FNV1("InventoryComponent")]`
/// holding `InventoryItem{RecordID u32, Count i32, …}`; worn clothing is
/// `EC[Hash=FNV1("AppearanceModifierManagerComponent")]` holding `Modifier{InventoryIndex i32}`,
/// a UNIT index into that inventory (cumulative Count); the weapon set manager keeps
/// `LastMeleeWeaponID` / `LastRangedWeaponID` and other u32 record ids.
///
/// Removing an item shifts every unit index after it, so the modifiers are rewritten against the
/// kept list; a modifier whose item went is held with the item and restored pointing at it.
/// Every entity in the world is filtered (a chest can hold a mod's item too); the HERO's removals
/// (UniqueID {1,0}) are the ones held for restore.
/// </remarks>
public static class ModSaveFilter
{
	private static readonly uint InventoryHash = Fnv1("InventoryComponent");
	private static readonly uint ModifierHash = Fnv1("AppearanceModifierManagerComponent");
	private static readonly uint WeaponSetHash = Fnv1("WeaponSetManagerComponent");

	// ---------------------------------------------------------------- the save stream

	/// <summary>
	/// Removes every item, modifier and weapon id among <paramref name="records"/> from every entity
	/// of <paramref name="stream"/>; the hero's removals come back in the result. Nothing is
	/// removed when <paramref name="records"/> is empty.
	/// </summary>
	public static SaveSidecar.HeldRecords Filter(SaveStream stream, ISet<uint> records)
	{
		var held = new SaveSidecar.HeldRecords();
		if (records.Count == 0 || stream.Document("EntityManager")?.Child("Entities") is not { } entities)
		{
			return held;
		}

		foreach (SaveNode entity in entities.All("Entity"))
		{
			bool hero = IsHero(entity);
			if (entity.Child("EntityComponents") is not { } components)
			{
				continue;
			}

			SaveNode? inventory = Component(components, InventoryHash);
			SaveNode? modifiers = Component(components, ModifierHash);
			SaveNode? weapons = Component(components, WeaponSetHash);

			// The inventory: kept items keep their order; a removed item's unit range is noted so
			// the modifiers can be re-pointed.
			var removedUnits = new List<(int First, int Count, SaveNode Item, uint Record)>();
			var unitShift = new List<(int From, int By)>();  // after unit From, indices move back By
			if (inventory is not null)
			{
				int unit = 0;
				int removed = 0;
				var kept = new List<SaveNode>();
				foreach (SaveNode child in inventory.Children)
				{
					if (child.Name != "InventoryItem")
					{
						kept.Add(child);
						continue;
					}

					uint record = child.Child("RecordID")?.Text is uint r ? r : 0u;
					int count = Math.Max(1, child.Child("Count")?.Text is int c ? c : 1);
					if (record != 0u && records.Contains(record))
					{
						removedUnits.Add((unit, count, child, record));
						removed += count;
						if (hero)
						{
							held.Inventory.Add(child);
						}
					}
					else
					{
						kept.Add(child);
					}

					unit += count;
					unitShift.Add((unit, removed));
				}

				if (removedUnits.Count != 0)
				{
					inventory.Children = kept;
				}
			}

			if (modifiers is not null && removedUnits.Count != 0)
			{
				var kept = new List<SaveNode>();
				foreach (SaveNode child in modifiers.Children)
				{
					if (child.Name != "Modifier" || child.Child("InventoryIndex") is not { } indexNode || indexNode.Text is not int index)
					{
						kept.Add(child);
						continue;
					}

					var gone = removedUnits.Find(r => index >= r.First && index < r.First + r.Count);
					if (gone.Item is not null)
					{
						if (hero)
						{
							held.Worn.Add((gone.Record, child));
						}

						continue;
					}

					int shift = 0;
					foreach ((int from, int by) in unitShift)
					{
						if (index >= from)
						{
							shift = by;
						}
					}

					indexNode.Text = index - shift;
					kept.Add(child);
				}

				modifiers.Children = kept;
			}

			if (weapons is not null)
			{
				foreach (SaveNode field in weapons.Children)
				{
					if (field.Text is uint id && id != 0u && records.Contains(id))
					{
						if (hero && field.Name == "LastMeleeWeaponID") held.Melee = id;
						if (hero && field.Name == "LastRangedWeaponID") held.Ranged = id;
						field.Text = 0u;
					}

					// A slot map entry: `…{…, WeaponID}` nested one deeper.
					foreach (SaveNode nested in field.Children)
					{
						if (nested.Text is uint slotId && slotId != 0u && records.Contains(slotId))
						{
							nested.Text = 0u;
						}
					}
				}
			}
		}

		return held;
	}

	/// <summary>
	/// Puts <paramref name="held"/> back on the hero: the items appended to the inventory, the
	/// worn modifiers re-pointed at them, the weapon ids restored where the slot is empty.
	/// False when the stream has no hero to give them to.
	/// </summary>
	public static bool Restore(SaveStream stream, SaveSidecar.HeldRecords held)
	{
		if (stream.Document("EntityManager")?.Child("Entities") is not { } entities)
		{
			return false;
		}

		SaveNode? hero = entities.All("Entity").FirstOrDefault(IsHero);
		if (hero?.Child("EntityComponents") is not { } components)
		{
			return false;
		}

		SaveNode? inventory = Component(components, InventoryHash);
		if (inventory is null && held.Inventory.Count != 0)
		{
			return false;
		}

		var firstUnit = new Dictionary<uint, int>();
		if (inventory is not null)
		{
			int unit = 0;
			foreach (SaveNode child in inventory.Children)
			{
				if (child.Name == "InventoryItem")
				{
					unit += Math.Max(1, child.Child("Count")?.Text is int c ? c : 1);
				}
			}

			foreach (SaveNode item in held.Inventory)
			{
				uint record = item.Child("RecordID")?.Text is uint r ? r : 0u;
				int count = Math.Max(1, item.Child("Count")?.Text is int c ? c : 1);
				firstUnit.TryAdd(record, unit);
				inventory.Children.Add(item.Clone());
				unit += count;
			}
		}

		SaveNode? modifiers = Component(components, ModifierHash);
		if (modifiers is not null)
		{
			foreach ((uint record, SaveNode modifier) in held.Worn)
			{
				if (!firstUnit.TryGetValue(record, out int at))
				{
					continue;
				}

				SaveNode copy = modifier.Clone();
				if (copy.Child("InventoryIndex") is { } index)
				{
					index.Text = at;
				}
				else
				{
					copy.Field("InventoryIndex", at);
				}

				modifiers.Children.Add(copy);
			}
		}

		SaveNode? weapons = Component(components, WeaponSetHash);
		if (weapons is not null)
		{
			if (held.Melee != 0u && weapons.Child("LastMeleeWeaponID") is { Text: uint melee } m && melee == 0u) m.Text = held.Melee;
			if (held.Ranged != 0u && weapons.Child("LastRangedWeaponID") is { Text: uint ranged } g && ranged == 0u) g.Text = held.Ranged;
		}

		return true;
	}

	/// <summary>The record ids of every inventory item any entity in the stream carries.</summary>
	public static HashSet<uint> RecordsIn(SaveStream stream)
	{
		var records = new HashSet<uint>();
		if (stream.Document("EntityManager")?.Child("Entities") is not { } entities)
		{
			return records;
		}

		foreach (SaveNode entity in entities.All("Entity"))
		{
			if (entity.Child("EntityComponents") is not { } components)
			{
				continue;
			}

			foreach (SaveNode ec in components.All("EC"))
			{
				foreach (SaveNode item in ec.All("InventoryItem"))
				{
					if (item.Child("RecordID")?.Text is uint r && r != 0u)
					{
						records.Add(r);
					}
				}
			}
		}

		return records;
	}

	// ---------------------------------------------------------------- the player store

	/// <summary>
	/// The store's text without the lines under <paramref name="prefixes"/>; the removed lines come
	/// back as key → value. Unchanged text when nothing matches.
	/// </summary>
	public static (string Text, Dictionary<string, string> Held) FilterPlayerStore(string text, IReadOnlyList<string> prefixes)
	{
		var held = new Dictionary<string, string>(StringComparer.Ordinal);
		var kept = new List<(string, string)>();
		foreach ((string key, string value) in SaveSidecar.ParsePlayerStore(text))
		{
			if (prefixes.Any(p => p.Length != 0 && key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
			{
				held[key] = value;
			}
			else
			{
				kept.Add((key, value));
			}
		}

		return (held.Count == 0 ? text : SaveSidecar.PlayerStoreText(kept), held);
	}

	/// <summary>The store's text with <paramref name="held"/> back in (a key present keeps its value).</summary>
	public static string RestorePlayerStore(string text, IReadOnlyDictionary<string, string> held)
	{
		var lines = SaveSidecar.ParsePlayerStore(text);
		var present = new HashSet<string>(lines.Select(l => l.Key), StringComparer.Ordinal);
		foreach ((string key, string value) in held)
		{
			if (!present.Contains(key))
			{
				lines.Add((key, value));
			}
		}

		return SaveSidecar.PlayerStoreText(lines);
	}

	// ---------------------------------------------------------------- RefiiModSave

	/// <summary>
	/// The game-VM chunk that drops <c>RefiiModSave</c> keys under <paramref name="prefixes"/>: run
	/// once the save's Lua table is loaded (<c>save_loaded</c>), so a missing mod's values never
	/// sit in the next save.
	/// </summary>
	public static string LuaClearKeys(IReadOnlyList<string> prefixes)
	{
		if (prefixes.Count == 0)
		{
			return string.Empty;
		}

		var chunk = new StringBuilder("if type(RefiiModSave)=='table' then local p={");
		foreach (string prefix in prefixes)
		{
			chunk.Append('"').Append(prefix.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\",");
		}

		chunk.Append("} local gone={} for k in pairs(RefiiModSave) do if type(k)=='string' then for _,q in ipairs(p) do " +
			"if k:sub(1,#q)==q then gone[#gone+1]=k break end end end end for _,k in ipairs(gone) do RefiiModSave[k]=nil end end");
		return chunk.ToString();
	}

	// ---------------------------------------------------------------- a kit's hero

	/// <summary>
	/// Player two's kit read off a picked save: the items, worn records and weapon ids among
	/// <paramref name="records"/> go, and the instance data keyed by inventory index follows the
	/// kept items. Returns how many entries went.
	/// </summary>
	public static int FilterHero(Engine_SaveHero.Hero hero, ISet<uint> records)
	{
		if (records.Count == 0)
		{
			return 0;
		}

		int removed = 0;
		var inventory = new List<(uint Record, int Count)>();
		var instances = new Dictionary<int, Engine_SaveHero.InventoryInstance>();
		for (int i = 0; i < hero.Inventory.Count; ++i)
		{
			if (records.Contains(hero.Inventory[i].Record))
			{
				removed++;
				continue;
			}

			if (hero.InventoryInstances.TryGetValue(i, out Engine_SaveHero.InventoryInstance? instance))
			{
				instances[inventory.Count] = instance;
			}

			inventory.Add(hero.Inventory[i]);
		}

		hero.Inventory = inventory;
		hero.InventoryInstances = instances;
		removed += hero.Worn.RemoveAll(records.Contains);
		removed += hero.DogWorn.RemoveAll(records.Contains);
		if (records.Contains(hero.Melee)) { hero.Melee = 0u; removed++; }
		if (records.Contains(hero.Ranged)) { hero.Ranged = 0u; removed++; }
		return removed;
	}

	// ---------------------------------------------------------------- helpers

	private static bool IsHero(SaveNode entity) =>
		entity.Child("UniqueID") is { } uid &&
		uid.Child("UIDGameID")?.Text is uint game && game == 1u &&
		uid.Child("UIDEditorID")?.Text is uint editor && editor == 0u;

	private static SaveNode? Component(SaveNode components, uint hash)
	{
		foreach (SaveNode ec in components.All("EC"))
		{
			if (ec.TryGetAttribute("Hash", out object? key) && key is uint k && k == hash)
			{
				return ec;
			}
		}

		return null;
	}

	public static uint Fnv1(string text)
	{
		uint hash = 0x811C9DC5u;
		foreach (byte b in Encoding.ASCII.GetBytes(text))
		{
			hash = unchecked(hash * 0x01000193u) ^ b;
		}

		return hash;
	}
}
