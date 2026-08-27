using System.Collections.Generic;
using System.Globalization;
using Refii.Equipment;
using Godot;
using Refii.Gdb;

namespace Refii.Ui;

/// <summary>
/// The item detail page, owned — the right-hand page of the item, clothing, gift, furniture and
/// decoration books (screens 15, 16, 25, 26 and 27, one skeleton over three `.bgf`).
///
/// Same cause as <see cref="Engine_UiAbilityPage"/>: the guest fills this page on a
/// highlight-change edge keyed off ITS Lua highlight, Godot owns the cursor, so the key never
/// moves. See <see cref="Engine_UiDetailPages"/>.
///
/// No new reader — `Engine_ItemDetails.BuildOne` already resolves an item's GDB record (name
/// tag, description tag, value) and is what the bank browser reads.
///
/// Adopted field by field: only the four named here are host-owned; `ItemValueName`,
/// `ItemValue2` and `Info` still come from the guest.
/// </summary>
public static class Engine_UiItemPage
{
	/// <summary>
	/// Keyed without a page, so the one entry reaches both copies — the item books put the
	/// name on Page1 and Page2 exactly as the abilities book does.
	/// </summary>
	private const string ItemName = "Contents.ItemName";

	private const string ItemDescription = "Contents.ItemDescription";

	/// <summary>
	/// The item's value. Matching is on whole trailing segments, and neither `ItemValueName` nor
	/// `ItemValue2` ends with `Contents.ItemValue`, so this reaches only the element it names.
	/// </summary>
	private const string ItemValue = "Contents.ItemValue";

	/// <summary>
	/// The shop spells the same two values differently — `shopscreen.bgf` hangs the description
	/// under `Text.Group.Group` and calls the item's worth `ValueValue` — so both spellings ride
	/// in one set; a key matching nothing on a screen is never used.
	///
	/// `PriceValue` is deliberately absent: what the shop CHARGES is the shopkeeper's number
	/// after markup, not the item's worth.
	/// </summary>
	private const string ShopDescription = "Text.Group.Group.ItemDescription";

	private const string ShopValue = "TextElements.ValueValue";

	/// <summary>
	/// `CreateMenuItem` event types that mean "category, not an item" — the same two the
	/// abilities fill checks (`sub_826C3C78` at `0x826c3d0c`).
	/// </summary>
	private const int SubmenuEventType = 301;

	private const int SpellAssignEventType = 23;

	/// <summary>The detail values for the highlighted row, or blanks.</summary>
	public static IReadOnlyDictionary<string, string> Values(
		Engine_UiExpandableMenu.Item? row,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		var values = new Dictionary<string, string>(3)
		{
			[ItemName] = string.Empty,
			[ItemDescription] = string.Empty,
			[ItemValue] = string.Empty,
			[ShopDescription] = string.Empty,
			[ShopValue] = string.Empty,
		};

		if (row is null || string.IsNullOrEmpty(row.Name) ||
			row.EventType == SubmenuEventType || row.EventType == SpellAssignEventType)
		{
			return values;
		}

		GdbMulti? multi = Engine_GameGdb.Database;
		if (multi is null)
		{
			return values;
		}

		// Two candidate keys, because a row's id means something different per screen: the
		// logbook's expression rows carry a GDB record HASH (`sub_829BCDF0` feeds it to
		// `sub_821DA348`), the abilities rows a record NAME, and these books resolve theirs
		// through a `std::map` at `menuMgr+4` into a 16-byte ENTITY HANDLE (`sub_826D1698` at
		// `0x826d1754`). Both are probed; a lookup succeeds only on an exact record-hash match.
		uint id = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		ItemDetail? detail = Engine_ItemDetails.BuildOne(multi, id)
			?? Engine_ItemDetails.BuildOne(multi, Refii.Core.Engine_GameHash.Fnv1(row.Name))
			?? ByNameTag(multi, row.Name);

		if (detail is null)
		{
			// The identity currency is not settled here. Abilities rows carry a GDB record name
			// (`AbilityPhysiqueBase`) and resolve by `Fnv1(name)`; the item books' own filler
			// resolves `(Float2<<16)|Float1` through the `menuMgr+4` map into an entity handle
			// and reads the live entity's def, never the spawn name. The first miss per row name
			// is reported so the answer comes from a run rather than an assumption.
			if (_unresolved.Add(row.Name))
			{
				GD.Print($"[item-page] '{row.Name}' (type {row.EventType}, label " +
					$"'{row.StatedLabel}') is not a GDB record — the item books may key on the " +
					"entity handle rather than the spawn name");
			}

			// The row's own label, already localised by the guest at spawn: an unresolved record
			// costs the description and the value, not the name.
			values[ItemName] = row.StatedLabel ?? string.Empty;
			return values;
		}

		string name = Babel(strings, detail.NameTagName);
		values[ItemName] = name.Length != 0 ? name : row.StatedLabel ?? string.Empty;
		values[ItemDescription] = Babel(strings, detail.DescTagName);
		values[ShopDescription] = values[ItemDescription];

		// `Money` is -1 when unset, not 0 — only an unresolved value is blank. The prefix is the
		// guest's: `sub_829B6B50` writes `ItemValue` as the wide literal at `0x820CEE80`
		// (`L"$"`) followed by `itoa(BasePrice)`; the shop's `ValueValue` takes the same form.
		values[ItemValue] = detail.Money >= 0
			? "$" + detail.Money.ToString(CultureInfo.InvariantCulture)
			: string.Empty;
		values[ShopValue] = values[ItemValue];

		return values;
	}

	/// <summary>
	/// The item whose NAME TAG the row carries. The item books spawn rows with the record's
	/// `NameTag` as `String1` (`INV_ITEM_BOWER_LAKE_TOMB_SEAL_NAME`), not the record name, so
	/// `Fnv1(row.Name)` is no record's hash; the tag indexes every record once per database.
	/// </summary>
	private static ItemDetail? ByNameTag(GdbMulti multi, string nameTag)
	{
		if (nameTag.Length == 0)
		{
			return null;
		}

		if (!ReferenceEquals(_indexedFor, multi))
		{
			_byNameTag.Clear();
			foreach (ItemDetail item in Engine_ItemDetails.Build(multi))
			{
				if (item.NameTagName.Length > 0)
				{
					_byNameTag.TryAdd(item.NameTagName, item);
				}
			}

			_indexedFor = multi;
		}

		return _byNameTag.TryGetValue(nameTag, out ItemDetail? found) ? found : null;
	}

	private static GdbMulti? _indexedFor;
	private static readonly Dictionary<string, ItemDetail> _byNameTag = new(System.StringComparer.Ordinal);

	/// <summary>Row names already reported as unresolved — one line each, not one per frame.</summary>
	private static readonly HashSet<string> _unresolved = new();

	private static string Babel(Refii.Text.Engine_BabelDatabase? strings, string tag) =>
		strings is not null && !string.IsNullOrEmpty(tag) &&
		strings.TryGetString(tag, out string value)
			? value
			: string.Empty;
}
