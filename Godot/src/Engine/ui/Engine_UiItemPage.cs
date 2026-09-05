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
	/// The `GUI_SCREEN_SHOP_VALUE` caption beside the price. Hidden WITH the price when the
	/// record has no `ShopItemComponent` — an item with no shop price has no value line at all,
	/// and leaving the caption up with nothing under it is what the guest avoids here.
	/// </summary>
	private const string ItemValueName = "Contents.ItemValueName";

	/// <summary>
	/// THE EQUIPMENT BOOK SPELLS IT DIFFERENTLY. `equipmentscreen.bgf` names the same caption
	/// `ItemValue2`, so writing only `ItemValueName` left screen 16's value line with no host
	/// source at all — one of the four keys a run reported as matching no element there.
	/// </summary>
	private const string EquipmentValueName = "Contents.ItemValue2";

	/// <summary>Page1's one-line summary — the primary stat's label and value, space-joined.</summary>
	private const string Info = "Contents.Info";

	/// <summary>The equipment book's own Info, under `TypeSpecific`.</summary>
	private const string EquipmentInfo = "TypeSpecific.Info";

	/// <summary>The star strip. The GDB `Rating` integer IS the slide index — it is not binned.</summary>
	private const string StarRating = "Contents.StarRating";

	private const string ValueCaptionTag = "GUI_SCREEN_SHOP_VALUE";

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
	/// What the shop CHARGES, which is a different number from what the item is worth —
	/// `sub_82992F90` writes this element and `ValueValue` from two different functions.
	/// </summary>
	private const string ShopAskingPrice = "TextElements.PriceValue";

	/// <summary>The shop page's damage row — value and its label, written together.</summary>
	private const string ShopDamageValue = "TextElements.DamageValue";

	private const string ShopDamageLabel = "TextElements.Damage";

	/// <summary>The guest's own UI codes for the two shop transactions (`sub_826AE5C0`).</summary>
	private const int BuyEventType = 30;

	private const int SellEventType = 31;

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
			[ShopAskingPrice] = string.Empty,
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

		// THE ASKING PRICE, before the record lookup — a shop row whose GDB record does not
		// resolve still has a price, and it is the one number on that page the item book
		// cannot answer. On a shop row the id IS the per-population ticket, and the event
		// type is the transaction: 30 buy, 31 sell. Asked for now and read a tick later, so
		// the reply is checked against the row that asked for it.
		if (row.EventType == BuyEventType || row.EventType == SellEventType)
		{
			Refii.Host.HostBridge.SetShopPrice(id, row.EventType == SellEventType);

			Refii.Host.HostBridge.ShopPrice asking = Refii.Host.HostBridge.GetShopPrice();
			values[ShopAskingPrice] = asking.IsFor(id)
				? "$" + asking.Price.ToString(CultureInfo.InvariantCulture)
				: string.Empty;
		}

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
		int worth = detail.BasePrice >= 0 ? detail.BasePrice : detail.Money;
		values[ItemValue] = worth >= 0
			? "$" + worth.ToString(CultureInfo.InvariantCulture)
			: string.Empty;
		values[ShopValue] = values[ItemValue];

		// THE STAT LINES, THE AUGMENTS AND THE SUMMARY — everything Page2 says beyond the
		// description. `sub_829B6EAC` builds one blob and puts it in `ItemDescription`:
		//
		//     for each stat:   label + " " + value + "\n"
		//     if augments:     Babel("GUI_SCREEN_AUGMENT_AUGMENTS") + "\n"
		//     for each:        "<bullet.img>" + name + "\n"
		//     then:            the description itself
		//
		// Without it every item read as three lines whatever it was — a sword and a pie looked
		// the same. See Engine_UiItemStats for where each number comes from.
		Engine_UiItemStats.Stats stats = Engine_UiItemStats.For(detail.DefinitionHash != 0 ? detail.DefinitionHash : detail.RecordHash, strings);

		// THE SHOP'S DAMAGE ROW. `shopscreen.bgf` authors `Damage:` / `10000` on every item
		// page and the guest writes the value only for a weapon; a food item was showing the
		// file's 10000. The label and the value are stated together: a weapon's damage line,
		// or nothing for anything else — a blank host value is a blanking order.
		if (row.EventType == BuyEventType || row.EventType == SellEventType)
		{
			string damage = string.Empty;
			foreach (Engine_UiItemStats.Line line in stats.Lines)
			{
				if (line.Label.StartsWith("Damage", System.StringComparison.OrdinalIgnoreCase))
				{
					damage = line.Value;
					break;
				}
			}

			values[ShopDamageValue] = damage;
			values[ShopDamageLabel] = damage.Length != 0 ? Babel(strings, "GUI_SCREEN_SHOP_DAMAGE") : string.Empty;
		}
		if (stats.Any)
		{
			var body = new System.Text.StringBuilder();
			foreach (Engine_UiItemStats.Line line in stats.Lines)
			{
				body.Append(line.Label).Append(' ').Append(line.Value).Append('\n');
			}

			if (stats.Augments.Count > 0)
			{
				body.Append(Babel(strings, "GUI_SCREEN_AUGMENT_AUGMENTS")).Append('\n');
				foreach (string augment in stats.Augments)
				{
					body.Append(Engine_UiItemStats.Bullet).Append(augment).Append('\n');
				}
			}

			body.Append(values[ItemDescription]);
			values[ItemDescription] = body.ToString();
			values[ShopDescription] = values[ItemDescription];
		}

		// Page1's one-liner — the primary stat, which is the largest appearance change on a
		// garment and the damage on a weapon.
		if (stats.Primary.Label.Length != 0)
		{
			string summary = $"{stats.Primary.Label} {stats.Primary.Value}";
			values[Info] = summary;
			values[EquipmentInfo] = summary;
		}

		// The caption only stands when there is a price to caption — the VALUE's own emptiness
		// decides, not `stats.Priced`: an item without a `ShopItemComponent` has no worth to
		// show (`Money` is −1 and the guest skips the line), and a bare "Base Value:" over
		// nothing is what every weapon page showed.
		values[ItemValueName] = values[ItemValue].Length != 0
			? Babel(strings, ValueCaptionTag)
			: string.Empty;
		values[EquipmentValueName] = values[ItemValueName];

		// One line per row, change-keyed: a page that fills is as worth seeing as one that does
		// not. The provider was silently absent for the whole item-book family, so "the page is
		// blank" and "the page is not ours" looked identical from a log.
		string filled = $"{row.Name}|{values[ItemName]}|{values[ItemValue]}";
		if (_reported != filled)
		{
			_reported = filled;
			GD.Print($"[item-page] '{values[ItemName]}' — value {(values[ItemValue].Length == 0
				? "unstated" : values[ItemValue])}, description " +
				$"{(values[ItemDescription].Length == 0 ? "MISSING" : "resolved")}, " +
				$"record {detail.RecordHash:X8} definition {detail.DefinitionHash:X8} " +
				$"({(stats.Lines.Count)} stat line(s), {stats.Augments.Count} slot(s)), " +
				$"preview mesh {detail.PreviewMesh:X8} worn {detail.WornModelPathHash:X8} " +
				$"model {detail.ModelPathHash:X8}");
		}

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

	/// <summary>
	/// THE ITEM'S PICTURE, on Page1. Dumped from the shipped files, the three item books put it
	/// at `Page1.Contents.ItemImage` (gift, shop) and `Page1.Contents.TypeSpecific.ItemImage`
	/// (equipment), so both tails are stated and a key matching nothing is simply unused.
	///
	/// The channel is a RESOURCE-PATH HASH, as the abilities page's tarot frame is. `IconGraphic`
	/// resolves in the shipped databases for 561 of its 562 records and already carries the
	/// converted extension — `art\gui\wheelicons\icon_quest_giver.tex` — so the extension is
	/// REPLACED rather than appended, which leaves a `.tex` value untouched and still corrects
	/// the handful authored against a source file.
	/// </summary>
	public static IReadOnlyDictionary<string, uint> Textures(Engine_UiExpandableMenu.Item? row)
	{
		var textures = new Dictionary<string, uint>(2);
		if (row is null || string.IsNullOrEmpty(row.Name) ||
			row.EventType == SubmenuEventType || row.EventType == SpellAssignEventType ||
			Engine_GameGdb.Database is not { } multi)
		{
			return textures;
		}

		uint id = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		ItemDetail? detail = Engine_ItemDetails.BuildOne(multi, id)
			?? ByNameTag(multi, row.Name);
		if (detail is null || detail.IconTex.Length == 0)
		{
			return textures;
		}

		string path = detail.IconTex;
		int dot = path.LastIndexOf('.');
		if (dot > 0)
		{
			path = path[..dot];
		}

		uint hash = Refii.Core.Engine_GameHash.ResourcePath(path + ".tex");
		textures[ItemImage] = hash;
		textures[EquipmentItemImage] = hash;
		return textures;
	}

	/// <summary>
	/// THE SPINNING PREVIEW, resolved from the item's own record instead of from the guest.
	///
	/// The bridge's `RefiiHostGetUiPreviewObject` reads a struct the guest fills when ITS
	/// highlight changes — and that highlight stops moving the moment Godot owns the list, so
	/// the ring showed whatever was under the guest's cursor when the screen opened, or nothing
	/// at all (`base[preview + 64] == 0`, "nothing spawned"). The placement is shipped data, so
	/// the host can state it for the row the player is actually on.
	///
	/// Returns null when the item names no mesh — most consumables do not, and an empty ring is
	/// correct for them.
	/// </summary>
	public static Refii.Host.HostBridge.UiPreviewObject? Preview(
		uint screenId, Engine_UiExpandableMenu.Item? row)
	{
		if (row is null || string.IsNullOrEmpty(row.Name) ||
			row.EventType == SubmenuEventType || row.EventType == SpellAssignEventType ||
			Engine_GameGdb.Database is not { } multi)
		{
			ReportPreview($"'{row?.Name}' type {row?.EventType}: no candidate");
			return null;
		}

		uint id = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		ItemDetail? detail = Engine_ItemDetails.BuildOne(multi, id) ?? ByNameTag(multi, row.Name);
		if (detail is null)
		{
			ReportPreview($"'{row.Name}' id {id:X8}: no record");
			return null;
		}

		// WHICH MESH: `GUIScreenMesh` when the item states one, else the item's OWN model. The
		// weapon and clothing components state `GUIScreenMesh` as the null tag (0x811C9DC5,
		// FNV-1 of ""), so every weapon page drew an empty ring — while the definition's
		// `GraphicAppearanceStaticMeshComponent.ModelFile` (read into `WornModelPathHash`) is
		// the resource hash of the very model the guest spins there. The artist's offsets
		// (`GUIScreen*` on the component chain) apply to either.
		uint mesh = detail.PreviewMesh != 0
			? detail.PreviewMesh
			: detail.WornModelPathHash != 0
				? detail.WornModelPathHash
				: detail.ModelPathHash;

		ReportPreview($"'{row.Name}' → mesh 0x{mesh:X8} " +
			$"(stated {detail.PreviewMesh:X8}, worn {detail.WornModelPathHash:X8}, " +
			$"model {detail.ModelPathHash:X8}) scale {detail.PreviewScale} axis {detail.PreviewRotationAxis} " +
			$"at {detail.PreviewTranslation}");

		if (mesh == 0)
		{
			return null;
		}

		return new Refii.Host.HostBridge.UiPreviewObject(
			screenId, mesh,

			// No guest entity behind it — this one is ours.
			Entity: 0,
			detail.PreviewTranslation,
			detail.PreviewRotation,
			detail.PreviewScale,
			detail.PreviewRotationAxis);
	}

	private const string ItemImage = "Contents.ItemImage";

	private const string EquipmentItemImage = "TypeSpecific.ItemImage";

	private static GdbMulti? _indexedFor;
	private static readonly Dictionary<string, ItemDetail> _byNameTag = new(System.StringComparer.Ordinal);

	/// <summary>Row names already reported as unresolved — one line each, not one per frame.</summary>
	private static readonly HashSet<string> _unresolved = new();

	/// <summary>The last fill reported, so a steady cursor does not print every frame.</summary>
	private static string _reported = string.Empty;

	private static string _previewReported = string.Empty;

	/// <summary>One line per change of the preview decision, whatever it was.</summary>
	private static void ReportPreview(string decided)
	{
		if (_previewReported != decided)
		{
			_previewReported = decided;
			GD.Print("[item-preview] " + decided);
		}
	}

	private static string Babel(Refii.Text.Engine_BabelDatabase? strings, string tag) =>
		strings is not null && !string.IsNullOrEmpty(tag) &&
		strings.TryGetString(tag, out string value)
			? value
			: string.Empty;

	/// <summary>
	/// THE STAR STRIP. `sub_82426BA8(elem, ctx, details+0x44)` where `+0x44` is
	/// `sub_823BF820(entry, "Rating")` — the GDB integer goes STRAIGHT to the slide index, with
	/// no compare, divide or clamp anywhere on the item path. (The real-estate page's strip does
	/// bin a value; applying that rule here would be wrong, and only the shop range-checks it.)
	/// </summary>
	public static IReadOnlyDictionary<string, int> Slides(
		Engine_UiExpandableMenu.Item? row,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		if (row is null || string.IsNullOrEmpty(row.Name))
		{
			return Engine_UiPageProviders.NoSlides;
		}

		GdbMulti? multi = Engine_GameGdb.Database;
		if (multi is null)
		{
			return Engine_UiPageProviders.NoSlides;
		}

		uint id = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		ItemDetail? detail = Engine_ItemDetails.BuildOne(multi, id)
			?? Engine_ItemDetails.BuildOne(multi, Refii.Core.Engine_GameHash.Fnv1(row.Name))
			?? ByNameTag(multi, row.Name);
		if (detail is null)
		{
			return Engine_UiPageProviders.NoSlides;
		}

		int rating = Engine_UiItemStats.For(detail.DefinitionHash != 0 ? detail.DefinitionHash : detail.RecordHash, strings).Rating;
		return rating > 0
			? new Dictionary<string, int>(1) { [StarRating] = rating }
			: Engine_UiPageProviders.NoSlides;
	}
}
