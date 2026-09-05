using System.Collections.Generic;
using System.Globalization;
using Refii.Core;
using Refii.Gdb;

namespace Refii.Ui;

/// <summary>
/// THE STAT LINES ON AN ITEM'S PAGE — what a weapon does, what a shirt changes about you, what a
/// pie heals.
///
/// The item book's Page2 is not name-description-value: the shipped filler concatenates a block
/// of `label + " " + value` lines, then an augments list, and only then the description. None of
/// that was crossing, so every item read as three lines regardless of what it was.
///
/// `sub_824698D8` builds it. It reads the item's `Category` (1..23) and branches through a jump
/// table at 0x824699F0 to one handler per family; each handler fills two PARALLEL vectors —
/// labels at `details+0x24`, values at `details+0x34` — plus a "primary" pair at `+0x08`/`+0x0C`
/// that Page1's `Info` shows on its own. Every source is a named GDB property on a named
/// sub-record, so all of it is readable here without the guest.
///
/// Formatting is the guest's, from the same function: an int prints bare; a float prints as-is
/// when |v| &gt;= 1 and as `v * 100` followed by `%` otherwise; a zero row is skipped entirely.
/// </summary>
public static class Engine_UiItemStats
{
	/// <summary>One `label value` line, already localised.</summary>
	public readonly record struct Line(string Label, string Value);

	/// <summary>What an item's page has to say beyond its name and description.</summary>
	public readonly record struct Stats(
		IReadOnlyList<Line> Lines,
		IReadOnlyList<string> Augments,
		Line Primary,
		int Rating,
		bool Usable,
		bool Priced)
	{
		public bool Any => Lines.Count > 0 || Augments.Count > 0;
	}

	private static readonly Line NoLine = new(string.Empty, string.Empty);

	public static readonly Stats None =
		new(System.Array.Empty<Line>(), System.Array.Empty<string>(), NoLine, 0, false, false);

	private const string InventoryItemComponent = "InventoryItemComponent";
	private const string ShopItemComponent = "ShopItemComponent";
	private const string WeaponComponent = "WeaponComponent";
	private const string FirearmComponent = "FirearmComponent";
	private const string AugmentableComponent = "AugmentableComponent";
	private const string AppearanceModifier = "AppearanceModifierComponent";
	private const string TrophyExpressionData = "TrophyExpressionDataComponent";
	private const string FurnitureUnlock = "FurnitureUnlockComponent";
	private const string BonusEffects = "BonusEffects";
	private const string DefaultMaleEffects = "DefaultMaleEffects";
	private const string GiftComponent = "GiftComponent";
	private const string Effects = "Effects";

	private const string AugmentsHeaderTag = "GUI_SCREEN_AUGMENT_AUGMENTS";
	private const string AugmentEmptyTag = "GUI_SCREEN_AUGMENT_EMPTY";

	/// <summary>The bullet the guest prefixes each augment with — `L"&lt;bullet.img&gt;"`.</summary>
	public const string Bullet = "<bullet.img>";

	/// <summary>
	/// `sub_8246A558`'s twelve rows, in its own order. The last five are floats; the rest ints —
	/// the flags live in `byte_8209E664` beside the table.
	/// </summary>
	private static readonly (string Property, string Tag, bool Float)[] Bonuses =
	{
		("MaxHealthModifier", "GUI_SCREEN_INVENTORY_MAXHEALTH", false),
		("HealthModifier", "GUI_SCREEN_INVENTORY_HEALTH", false),
		("ManaModifier", "GUI_SCREEN_INVENTORY_MANA", false),
		("GeneralExpModifier", "GUI_SCREEN_INVENTORY_EXP", false),
		("StrengthExpModifier", "GUI_SCREEN_INVENTORY_EXP_STRENGTH", false),
		("SkillExpModifier", "GUI_SCREEN_INVENTORY_EXP_SKILL", false),
		("WillExpModifier", "GUI_SCREEN_INVENTORY_EXP_WILL", false),
		("MoralityModifier", "GUI_SCREEN_INVENTORY_MORALITY", true),
		("PurityModifier", "GUI_SCREEN_INVENTORY_PURITY", true),
		("FatModifier", "GUI_SCREEN_INVENTORY_FAT", true),
		("AgeModifier", "GUI_SCREEN_INVENTORY_AGE", true),
		("AlcoholLevel", "GUI_SCREEN_INVENTORY_ALCOHOL", true),
	};

	/// <summary>`sub_8246A808`'s twenty-one appearance rows — all floats, all shown as percents.</summary>
	private static readonly string[] Appearance =
	{
		"Attractiveness", "Poshness", "Friendliness", "Politeness", "Ridiculousness",
		"Entertainment", "Romance", "Extroversion", "Morality", "Scariness", "Crudeness",
		"Raunchiness", "Purity", "Funny", "Aggressiveness", "Childishness", "MoralityGood",
		"MoralityEvil", "Rudeness", "Ugliness", "Transvestism",
	};

	/// <summary>
	/// Everything the page shows for one item, or <see cref="None"/> when the record does not
	/// resolve. `strings` is only needed for the labels, which are Babel tags.
	/// </summary>
	public static Stats For(uint record, Refii.Text.Engine_BabelDatabase? strings)
	{
		GdbMulti? multi = Engine_GameGdb.Database;
		if (multi is null || record == 0u || !multi.TryLookup(record, out GdbCursor at))
		{
			return None;
		}

		var lines = new List<Line>();
		var augments = new List<string>();
		Line primary = NoLine;

		int rating = 0;
		bool usable = false;
		bool priced = Engine_AbilityRecords.TryRef(multi, at, ShopItemComponent, out _);
		int category = 0;

		if (Engine_AbilityRecords.TryRef(multi, at, InventoryItemComponent, out GdbCursor inv))
		{
			// STRAIGHT THROUGH, NOT BINNED. `*(a1 + 68) = sub_823BF820(entry, "Rating")` — the
			// GDB integer IS the slide index. (The building page's strip really does bin a
			// value; this one does not, and using that rule here would be wrong.)
			rating = Engine_AbilityRecords.IntOrEnumOn(inv, "Rating");
			usable = Engine_AbilityRecords.BoolOn(inv, "UsableFromInventory");
			category = Engine_AbilityRecords.IntOrEnumOn(inv, "Category");
		}

		switch (category)
		{
			case 1:
				Weapon(multi, at, lines, augments, ref primary, strings);
				break;

			case 3:
				Effect(multi, at, GiftComponent, Effects, lines, ref primary, strings, percent: true);
				break;

			case 4:
				if (Engine_AbilityRecords.TryRef(multi, at, TrophyExpressionData, out GdbCursor trophy) &&
					Engine_AbilityRecords.TryRef(multi, trophy, "ExpressionData", out GdbCursor expression))
				{
					Add(lines, ref primary, Babel(strings, "GUI_SCREEN_INVENTORY_RENOWN"),
						Engine_AbilityRecords.IntOn(expression, "RenownBonus"));
				}

				break;

			case 5:
			case 6:
				if (Engine_AbilityRecords.TryRef(multi, at, BonusEffects, out GdbCursor bonus))
				{
					foreach ((string property, string tag, bool asFloat) in Bonuses)
					{
						Number(bonus, property, Babel(strings, tag), asFloat, lines, ref primary);
					}
				}

				break;

			case 15:
				if (Engine_AbilityRecords.TryRef(multi, at, FurnitureUnlock, out GdbCursor furniture))
				{
					Add(lines, ref primary,
						Babel(strings, "GUI_SCREEN_INVENTORY_FURNITURE_UPGRADEPRICE"),
						Engine_AbilityRecords.IntOn(furniture, "Level"));
				}

				break;

			default:
				// 9..13 and 17..23 are the clothing families.
				if (category is (>= 9 and <= 13) or (>= 17 and <= 23))
				{
					Effect(multi, at, AppearanceModifier, DefaultMaleEffects, lines, ref primary,
						strings, percent: true);
				}

				// 25 is a SUIT — a whole outfit. Its `AppearanceModifierComponent` carries no
				// effects table of its own; it carries `SuitParts`, a record of references to
				// the pieces, and what the outfit does to the hero is what its pieces do
				// added together (the bandit outfit: 0.10 + 0.05 + 0.05 + 0.025 + 0.025
				// Aggressiveness = 25%). Leaving suits out is why every outfit page listed no
				// effect lines.
				if (category == 25)
				{
					Suit(multi, at, lines, ref primary, strings);
				}

				break;
		}

		return new Stats(lines, augments, primary, rating, usable, priced);
	}

	/// <summary>
	/// `sub_82469AE8` — damage, damage types, attack speed, the firearm block, and the augment
	/// slots. The slots are suppressed for augmentations themselves (`Category == 7`), which
	/// this reaches by never being called for that category.
	/// </summary>
	private static void Weapon(
		GdbMulti multi, GdbCursor at, List<Line> lines, List<string> augments, ref Line primary,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		if (Engine_AbilityRecords.TryRef(multi, at, WeaponComponent, out GdbCursor weapon))
		{
			Number(weapon, "DamageMultiplier",
				Babel(strings, "GUI_SCREEN_INVENTORY_WEAPON_DAMAGE"), true, lines, ref primary);

			// `|v - 1| < 0.003` is NORMAL, above is FAST, below SLOW — the guest's own bands.
			float speed = Engine_AbilityRecords.FloatOn(weapon, "AttackSpeedMultiplier");
			if (speed != 0f)
			{
				string band = System.Math.Abs(speed - 1f) < 0.003f
					? "GUI_SCREEN_EQUIPMENT_NORMAL"
					: speed > 1f ? "GUI_SCREEN_EQUIPMENT_FAST" : "GUI_SCREEN_EQUIPMENT_SLOW";
				lines.Add(new Line(
					Babel(strings, "GUI_SCREEN_INVENTORY_WEAPON_ATTACK_SPEED"),
					Babel(strings, band)));
			}
		}

		if (Engine_AbilityRecords.TryRef(multi, at, FirearmComponent, out GdbCursor firearm))
		{
			Number(firearm, "BulletCapacity",
				Babel(strings, "GUI_SCREEN_INVENTORY_FIREARM_AMMO"), false, lines, ref primary);
			Number(firearm, "ReloadTime",
				Babel(strings, "GUI_SCREEN_INVENTORY_FIREARM_RELOAD_TIME"), true, lines, ref primary);
			Number(firearm, "Range",
				Babel(strings, "GUI_SCREEN_INVENTORY_FIREARM_RANGE"), true, lines, ref primary);
		}

		// The slots, filled or `GUI_SCREEN_AUGMENT_EMPTY`. The names of what is IN a slot are
		// live inventory state rather than GDB, so an unaugmented weapon reads correctly and an
		// augmented one under-reports until that crosses.
		if (Engine_AbilityRecords.TryRef(multi, at, AugmentableComponent, out GdbCursor augmentable))
		{
			int slots = Engine_AbilityRecords.IntOrEnumOn(augmentable, "NumberOfSlots");
			for (int slot = 0; slot < slots && slot < 8; ++slot)
			{
				augments.Add(Babel(strings, AugmentEmptyTag));
			}
		}
	}

	/// <summary>
	/// The gift and clothing families: one sub-record of named float properties, each shown as a
	/// percentage. `OverrideFemaleEffects` is deliberately not read — it needs the hero's sex,
	/// which is live state, and the male table is what the guest falls back to.
	/// </summary>
	private static void Effect(
		GdbMulti multi, GdbCursor at, string component, string list, List<Line> lines,
		ref Line primary, Refii.Text.Engine_BabelDatabase? strings, bool percent)
	{
		if (!Engine_AbilityRecords.TryRef(multi, at, component, out GdbCursor holder) ||
			!Engine_AbilityRecords.TryRef(multi, holder, list, out GdbCursor effects))
		{
			return;
		}

		float best = 0f;
		foreach (string property in Appearance)
		{
			float value = Engine_AbilityRecords.FloatOn(effects, property);
			if (value == 0f)
			{
				continue;
			}

			var line = new Line(
				Babel(strings, "GUI_SCREEN_APPEARANCE_" + property.ToUpperInvariant()),
				Format(value, percent));
			lines.Add(line);

			// The guest's primary is the row with the LARGEST magnitude, not the first.
			if (System.Math.Abs(value) > best)
			{
				best = System.Math.Abs(value);
				primary = line;
			}
		}
	}

	private const string SuitParts = "SuitParts";

	/// <summary>The outfit's effects: the sum over its parts' `DefaultMaleEffects` — see the
	/// category 25 note in <see cref="For"/>. Primary is the largest magnitude, as for a piece.</summary>
	private static void Suit(
		GdbMulti multi, GdbCursor at, List<Line> lines, ref Line primary,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		if (!Engine_AbilityRecords.TryRef(multi, at, AppearanceModifier, out GdbCursor holder) ||
			!Engine_AbilityRecords.TryRef(multi, holder, SuitParts, out GdbCursor parts))
		{
			return;
		}

		var totals = new Dictionary<string, float>();
		var seen = new HashSet<uint>();
		foreach ((uint hash, GdbFieldType type, uint raw) in multi.InheritedFields(parts))
		{
			if (hash == GdbView.HashParent ||
				type is not (GdbFieldType.Reference or GdbFieldType.ResourceReference) ||
				raw == 0u || raw == GdbHash.Null || !seen.Add(raw) ||
				!multi.TryLookup(raw, out GdbCursor part) ||
				!Engine_AbilityRecords.TryRef(multi, part, AppearanceModifier, out GdbCursor modifier) ||
				!Engine_AbilityRecords.TryRef(multi, modifier, DefaultMaleEffects, out GdbCursor effects))
			{
				continue;
			}

			foreach (string property in Appearance)
			{
				float value = Engine_AbilityRecords.FloatOn(effects, property);
				if (value != 0f)
				{
					totals[property] = totals.GetValueOrDefault(property) + value;
				}
			}
		}

		float best = 0f;
		foreach (string property in Appearance)
		{
			if (!totals.TryGetValue(property, out float value) || value == 0f)
			{
				continue;
			}

			var line = new Line(
				Babel(strings, "GUI_SCREEN_APPEARANCE_" + property.ToUpperInvariant()),
				Format(value, true));
			lines.Add(line);
			if (System.Math.Abs(value) > best)
			{
				best = System.Math.Abs(value);
				primary = line;
			}
		}
	}

	private static void Number(
		GdbCursor at, string property, string label, bool asFloat, List<Line> lines,
		ref Line primary)
	{
		if (label.Length == 0)
		{
			return;
		}

		if (asFloat)
		{
			float value = Engine_AbilityRecords.FloatOn(at, property);
			if (value == 0f)
			{
				return;
			}

			Add(lines, ref primary, label, Format(value, true));
			return;
		}

		int whole = Engine_AbilityRecords.IntOn(at, property);
		if (whole != 0)
		{
			Add(lines, ref primary, label, whole);
		}
	}

	private static void Add(List<Line> lines, ref Line primary, string label, int value) =>
		Add(lines, ref primary, label, value.ToString(CultureInfo.InvariantCulture));

	private static void Add(List<Line> lines, ref Line primary, string label, string value)
	{
		var line = new Line(label, value);
		lines.Add(line);
		if (primary.Label.Length == 0)
		{
			primary = line;
		}
	}

	/// <summary>
	/// The guest's float rule: print as-is at a magnitude of 1 or more, otherwise as a
	/// percentage — `v * 100` with a trailing `%`.
	/// </summary>
	private static string Format(float value, bool percent)
	{
		if (!percent || System.Math.Abs(value) >= 1f)
		{
			return value.ToString("0.##", CultureInfo.InvariantCulture);
		}

		return (value * 100f).ToString("0.##", CultureInfo.InvariantCulture) + "%";
	}

	private static string Babel(Refii.Text.Engine_BabelDatabase? strings, string tag) =>
		strings is { } book && book.TryGetString(tag, out string resolved) && resolved.Length != 0
			? resolved
			: string.Empty;
}
