using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Mods;
using Refii.Save;

/// <summary>
/// Player two's kit read off a picked save, without the records of the mods its stamp names that
/// are not enabled now (the shell's <see cref="ModSaveStamp"/>, applied to Fable 2's hero model).
/// </summary>
public static class Fable2KitFilter
{
	/// <summary>
	/// Player two's hero read off a picked save: the records of the mods its stamp names that are
	/// not enabled now go. Returns how many entries went.
	/// </summary>
	public static int FilterKit(string saveDirectory, Engine_SaveHero.Hero hero)
	{
		SaveSidecar sidecar = SaveSidecar.Read(saveDirectory);
		if (sidecar.Mods is not { } stamp)
		{
			return 0;
		}

		ModSaveStamp.Diff diff = ModSaveStamp.Compare(stamp, ModFingerprint.Current());
		return diff.Missing.Count == 0 ? 0 : FilterHero(hero, diff.MissingRecords());
	}

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
}
