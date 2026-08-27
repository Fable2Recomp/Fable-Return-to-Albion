using System;
using System.Collections.Generic;
using System.Globalization;
using Refii.Core;

namespace Refii.Gdb;

/// <summary>
/// Ability records from the shipped GDB. Live inputs: per-ability level (hero component 57,
/// `stats + 0x110 + 4*id` = the guest's `4 * (Type + 68)` at `0x82896ad0`) and the orb wallet
/// (component 164). Names run `Ability&lt;X&gt;Base` -> `Ability&lt;X&gt;Level&lt;N&gt;`
/// (`sub_824E45F8`); maxed = the next record is absent (`sub_824E2D40`).
/// </summary>
public static class Engine_AbilityRecords
{
	/// <summary>The suffix every ability's root record carries.</summary>
	private const string BaseSuffix = "Base";

	private const string LevelInfix = "Level";

	/// <summary>An ability's static half, as the detail page needs it.</summary>
	public readonly record struct Ability(
		int Type,
		string NameTag,
		string DescriptionTag,
		string BaseRecord,
		string ImagePath,
		string VideoPath)
	{
		public bool Known => Type != 0;
	}

	/// <summary>The static record behind a spawned row, by the name the guest spawned it with.</summary>
	public static bool TryGet(string recordName, out Ability ability)
	{
		ability = default;
		if (string.IsNullOrEmpty(recordName))
		{
			return false;
		}

		GdbMulti? multi = Database;
		if (multi is null || !multi.TryLookup(Engine_GameHash.Fnv1(recordName), out GdbCursor at))
		{
			return false;
		}

		// `Type` keys every other lookup (stat, discipline, level slot in component 57); the guest
		// (`sub_82896EA8`) leaves `+0` unwritten when the property is absent.
		if (!TryInt(multi, at, "Type", out int type) || type <= 0)
		{
			return false;
		}

		ability = new Ability(
			type,
			TryText(multi, at, "NameTextTag"),
			TryText(multi, at, "DescriptionTextTag"),
			recordName,

			// Base paths, no extension: the guest appends `.tex` to the image and builds the
			// movie as `GAME:\data\` + video + level suffix. See Engine_UiAbilityPage.
			TryText(multi, at, "ImageFilePathAndName"),
			TryText(multi, at, "VideoFilePathAndName"));

		return true;
	}

	/// <summary>
	/// The record holding the stats for a given level: the base record at level 0, otherwise
	/// <c>&lt;prefix&gt;Level&lt;n&gt;</c>. Empty when the ability's name does not end in `Base`.
	/// </summary>
	public static string RecordForLevel(string baseRecord, int level)
	{
		if (!baseRecord.EndsWith(BaseSuffix, StringComparison.Ordinal))
		{
			return string.Empty;
		}

		if (level <= 0)
		{
			return baseRecord;
		}

		string prefix = baseRecord[..^BaseSuffix.Length];
		return $"{prefix}{LevelInfix}{level}";
	}

	/// <summary>Whether a record exists (absent next level = maxed).</summary>
	public static bool Exists(string recordName)
	{
		GdbMulti? multi = Database;
		return multi is not null && recordName.Length != 0 &&
			multi.TryLookup(Engine_GameHash.Fnv1(recordName), out _);
	}

	/// <summary>
	/// The next level's experience cost, walked as `sub_824E48F8` does: the `level`-th entry
	/// under the base's `AbilityGroupRecords`, else the base's own `ExperienceCost` (guards at
	/// `0x824e4960`/`0x824e4974`). Unresolved is false, never a number.
	/// </summary>
	public static bool TryCost(string baseRecord, int level, out int cost)
	{
		cost = 0;
		GdbMulti? multi = Database;
		if (multi is null || baseRecord.Length == 0 ||
			!multi.TryLookup(Engine_GameHash.Fnv1(baseRecord), out GdbCursor at))
		{
			return false;
		}

		if (TryGroupEntry(multi, at, level, out GdbCursor entry) &&
			TryInt(multi, entry, "ExperienceCost", out cost))
		{
			return true;
		}

		return TryInt(multi, at, "ExperienceCost", out cost);
	}

	/// <summary>The `level`-th entry under the base record's `AbilityGroupRecords`.</summary>
	private static bool TryGroupEntry(
		GdbMulti multi, GdbCursor at, int level, out GdbCursor entry)
	{
		entry = default;
		if (level < 0 ||
			!multi.TryFindInherited(
				at, Engine_GameHash.Fnv1("AbilityGroupRecords"),
				(int)GdbFieldType.Reference, out GdbCursor owner, out uint group, out _) ||
			!owner.Valid || !multi.TryLookup(group, out GdbCursor groupAt))
		{
			return false;
		}

		// The `parent` link is a reference field too; excluding it keeps the index unshifted.
		int index = 0;
		foreach ((uint hash, GdbFieldType type, uint raw) in groupAt.View.RawFields(groupAt.Record))
		{
			if (type != GdbFieldType.Reference || hash == GdbView.HashParent)
			{
				continue;
			}

			if (index++ == level && multi.TryLookup(raw, out entry))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// The spell-stat chain `HeroMagicDef` -> family -> `&lt;Family&gt;Targeted`/`&lt;Family&gt;Area` ->
	/// `Power&lt;level&gt;`, each hop a type-6 reference (`sub_8260D580`); all stats live on the
	/// `Power` entry. False at the first missing hop, as `sub_829A79C0`.
	/// </summary>
	public static bool TryPowerEntry(string family, bool area, int level, out GdbCursor entry)
	{
		entry = default;
		GdbMulti? multi = Engine_GameGdb.Database;
		if (multi is null || string.IsNullOrEmpty(family))
		{
			return false;
		}

		return Engine_GameGdb.TryLookup("HeroMagicDef", out GdbCursor root) &&
			TryRef(multi, root, family, out GdbCursor spell) &&
			TryRef(multi, spell, family + (area ? "Area" : "Targeted"), out GdbCursor half) &&
			TryRef(multi, half, "Power" + level.ToString(CultureInfo.InvariantCulture), out entry);
	}

	/// <summary>One type-6 record reference hop, following the parent chain.</summary>
	public static bool TryRef(GdbMulti multi, GdbCursor at, string property, out GdbCursor target)
	{
		target = default;
		return multi.TryFindInherited(
				at, Engine_GameHash.Fnv1(property), (int)GdbFieldType.Reference,
				out _, out uint hash, out _) &&
			multi.TryLookup(hash, out target);
	}

	/// <summary>A float property on a cursor. Absent reads as 0, which is the guest's default.</summary>
	public static float FloatOn(GdbCursor at, string property)
	{
		GdbMulti? multi = Engine_GameGdb.Database;
		return multi is not null &&
			multi.TryFindInherited(
				at, Engine_GameHash.Fnv1(property), (int)GdbFieldType.Float,
				out _, out uint raw, out _)
			? BitConverter.Int32BitsToSingle(unchecked((int)raw))
			: 0f;
	}

	/// <summary>An int property on a cursor, 0 when absent.</summary>
	public static int IntOn(GdbCursor at, string property)
	{
		GdbMulti? multi = Engine_GameGdb.Database;
		return multi is not null &&
			multi.TryFindInherited(
				at, Engine_GameHash.Fnv1(property), (int)GdbFieldType.Int,
				out _, out uint raw, out _)
			? unchecked((int)raw)
			: 0;
	}

	/// <summary>A bool property on a cursor, false when absent.</summary>
	public static bool BoolOn(GdbCursor at, string property)
	{
		GdbMulti? multi = Engine_GameGdb.Database;
		return multi is not null &&
			multi.TryFindInherited(
				at, Engine_GameHash.Fnv1(property), (int)GdbFieldType.Bool,
				out _, out uint raw, out _) &&
			raw != 0u;
	}

	/// <summary>A float property on a named record, following the record's parent chain.</summary>
	public static bool TryFloat(string recordName, string property, out float value)
	{
		value = 0f;
		GdbMulti? multi = Database;
		if (multi is null || recordName.Length == 0 ||
			!multi.TryLookup(Engine_GameHash.Fnv1(recordName), out GdbCursor at) ||
			!multi.TryFindInherited(
				at, Engine_GameHash.Fnv1(property), (int)GdbFieldType.Float,
				out _, out uint raw, out _))
		{
			return false;
		}

		value = BitConverter.Int32BitsToSingle(unchecked((int)raw));
		return true;
	}

	/// <summary>An int property on a cursor, following the parent chain.</summary>
	private static bool TryInt(GdbMulti multi, GdbCursor at, string property, out int value)
	{
		value = 0;
		if (!multi.TryFindInherited(
				at, Engine_GameHash.Fnv1(property), (int)GdbFieldType.Int,
				out _, out uint raw, out _))
		{
			return false;
		}

		value = unchecked((int)raw);
		return true;
	}

	/// <summary>A string property's text, following the parent chain; the raw value is a hash into the owning view's name table.</summary>
	private static string TryText(GdbMulti multi, GdbCursor at, string property)
	{
		if (!multi.TryFindInherited(
				at, Engine_GameHash.Fnv1(property), (int)GdbFieldType.String,
				out GdbCursor owner, out uint raw, out _) ||
			!owner.Valid)
		{
			return string.Empty;
		}

		return owner.View.Names.TryGetValue(raw, out string? text) ? text : string.Empty;
	}

	/// <summary>The shared runtime database; see <see cref="Engine_GameGdb"/>.</summary>
	private static GdbMulti? Database => Engine_GameGdb.Database;
}
