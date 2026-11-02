using System;
using System.Globalization;

namespace Refii.Mods;

/// <summary>
/// A CHOSEN ROW'S TYPED SUBJECT — the <c>subject_kind</c>, <c>definition</c> and <c>spell</c> fields of
/// <c>mod_menu_row</c> on every surface — read once, here, off what the engine itself put on the row:
/// the row's own command (the verbs the engine issues, <c>Inventory:use:&lt;record&gt;</c>,
/// <c>Inventory:equip:&lt;record&gt;</c>, <c>Ability:learn|unlearn:&lt;type&gt;</c>, a logbook
/// expression row's id) or a host list's typed row name (<see cref="ItemRow"/>,
/// <see cref="ExpressionRow"/>, <see cref="WillRow"/>: <c>inventory:usable</c>, <c>expressions</c>,
/// <c>spells_known</c>). A handler never parses a verb string again.
/// </summary>
/// <remarks>
/// Kinds: <c>item</c> (a use), <c>weapon</c> (an equip), <c>expression</c>, <c>will</c>; empty for a
/// row that states none. A definition is the record hash as <c>"0x%08x"</c>, exact in Lua where a
/// float would not be. A will's spell is its type 0..7 (0 Lightning .. 7 Dead Rising): the abilities
/// 7..14 are the wills in the same order (Engine_UiAbilitySource, the guest's <c>sub_824E45F8</c>
/// table), so the type is the ability less 7. Pure text work, held by tests.
/// </remarks>
public static class ModRowSubject
{
	public const string Item = "item";
	public const string Weapon = "weapon";
	public const string Expression = "expression";
	public const string Will = "will";

	/// <summary>The first will among the fourteen abilities (Lightning); Dead Rising is <see cref="FirstWillAbility"/> + 7.</summary>
	public const int FirstWillAbility = 7;

	/// <summary>One subject: the kind ("" for none), the definition ("" for none), the spell (-1 for none).</summary>
	public readonly record struct Subject(string Kind, string Definition, int Spell)
	{
		public static readonly Subject None = new(string.Empty, string.Empty, -1);

		public bool Stated => Kind.Length != 0;
	}

	/// <summary>A host list's row name for an item: <c>item:0x%08x</c>.</summary>
	public static string ItemRow(uint record) => "item:" + Hex(record);

	/// <summary>A host list's row name for an expression: <c>expression:0x%08x</c>.</summary>
	public static string ExpressionRow(uint record) => "expression:" + Hex(record);

	/// <summary>A host list's row name for a will: <c>will:&lt;spell&gt;</c>.</summary>
	public static string WillRow(int spell) => "will:" + spell.ToString(CultureInfo.InvariantCulture);

	/// <summary>A record hash as the API writes one.</summary>
	public static string Hex(uint record) => "0x" + record.ToString("x8", CultureInfo.InvariantCulture);

	/// <summary>The subject of a row: its own command first, then its name; <see cref="Subject.None"/> when neither states one.</summary>
	public static Subject Of(string? row, string? command)
	{
		Subject fromCommand = OfCommand(command);
		return fromCommand.Stated ? fromCommand : OfRowName(row);
	}

	/// <summary>The subject a row command states.</summary>
	public static Subject OfCommand(string? command)
	{
		string text = (command ?? string.Empty).Trim();
		if (text.Length == 0)
		{
			return Subject.None;
		}

		if (After(text, "Inventory:use:") is { } use && Record(use) is { } item)
		{
			return new Subject(Item, Hex(item), -1);
		}

		if (After(text, "Inventory:equip:") is { } equip && Record(equip) is { } weapon)
		{
			return new Subject(Weapon, Hex(weapon), -1);
		}

		string? ability = After(text, "Ability:learn:") ?? After(text, "Ability:unlearn:");
		if (ability is not null)
		{
			return int.TryParse(ability, NumberStyles.None, CultureInfo.InvariantCulture, out int type) &&
				type >= FirstWillAbility && type <= FirstWillAbility + 7
					? new Subject(Will, string.Empty, type - FirstWillAbility)
					: Subject.None;
		}

		// A logbook expression row carries no command; the engine states its id (decimal) instead.
		return text.IndexOf(':') < 0 && Record(text) is { } expression
			? new Subject(Expression, Hex(expression), -1)
			: Subject.None;
	}

	/// <summary>The subject a host list's typed row name states.</summary>
	public static Subject OfRowName(string? row)
	{
		string text = (row ?? string.Empty).Trim();
		if (After(text, "item:") is { } item && Record(item) is { } itemRecord)
		{
			return new Subject(Item, Hex(itemRecord), -1);
		}

		if (After(text, "expression:") is { } expression && Record(expression) is { } expressionRecord)
		{
			return new Subject(Expression, Hex(expressionRecord), -1);
		}

		if (After(text, "will:") is { } will &&
			int.TryParse(will, NumberStyles.None, CultureInfo.InvariantCulture, out int spell) && spell is >= 0 and <= 7)
		{
			return new Subject(Will, string.Empty, spell);
		}

		return Subject.None;
	}

	/// <summary>A record hash, "0x…" or decimal, never 0.</summary>
	private static uint? Record(string text)
	{
		string value = text.Trim();
		uint record;
		bool parsed = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
			? uint.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out record)
			: uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out record);
		return parsed && record != 0u ? record : null;
	}

	private static string? After(string text, string prefix) =>
		text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : null;
}
