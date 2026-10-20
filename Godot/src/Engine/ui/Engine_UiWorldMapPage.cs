using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// Screen 23's right-hand region info page, owned. Values are the guest's own finished
/// strings from eleven `(LHString* out, LHString* regionName)` providers; the pump samples on
/// the guest thread and answers one tick later. The row names the region — `sub_829AB0E0`
/// computes `regionIndex = key - 999` from the row's `(Float1, Float2)`.
/// </summary>
public static class Engine_UiWorldMapPage
{
	/// <summary>Row key of the first region: `sub_829AB0E0` subtracts 999, not 1000.</summary>
	private const int RegionKeyBase = 999;

	/// <summary>The mode this page answers to — `sub_826C5E70` dispatches 41 to the stats widget.</summary>
	private const int RegionInfoMode = 41;

	private const string PageTitle = "Page1.Contents.PageTitle";

	/// <summary>The value rows, all under `…Page1.Contents.Text.`.</summary>
	private const string Distance = "Text.DistanceAmount";

	private const string Travel = "Text.TraveltimeAmount";

	private const string Fine = "Text.FineAmount";

	private const string Merchants = "Text.MerchantsList";

	private const string Owned = "Text.OwnedAmount";

	private const string Chests = "Text.ChestAmount";

	private const string Digspots = "Text.DigspotAmount";

	private const string Divespots = "Text.DivespotsSearchedAmount";

	private const string Gargoyles = "Text.GargoylesDestroyedAmount";

	private const string SilverKeys = "Text.SilverKeysFoundAmount";

	/// <summary>
	/// The silver-keys CAPTION. Authored blank, because the guest writes it: `sub_829AB0E0`
	/// resolves this element and sets its text with `sub_82425F40` before filling the amount
	/// beside it. Nothing wrote it here, so the count stood on the page with no label.
	/// </summary>
	private const string SilverKeysLabel = "Text.SilverKeysFound";

	/// <summary>The gargoyle row — label and amount. Mystery Island only; see the values.</summary>
	private const string GargoylesLabel = "Text.GargoylesDestroyed";

	/// <summary>
	/// The gargoyle caption, authored blank like the silver-keys one beside it. Supplied only
	/// on Mystery Island, where the row stands at all.
	/// </summary>
	private const string GargoylesTag = "GUI_SCREEN_WORLD_MAP_REGIONS_INFO_PAGE_GARGOYLES_DESTROYED";

	/// <summary>
	/// Silver BOOKS on Mystery Island, silver KEYS everywhere else. The guest's own branch is a
	/// `strcmp` against the region name, read through `_cntlzw`: equal (result 0) counts 32
	/// leading zeros, so bit 5 set is the MATCH, and that arm takes the books tag.
	/// </summary>
	private const string SilverKeysTag = "GUI_SCREEN_WORLD_MAP_REGIONS_INFO_PAGE_SILVERKEYS_FOUND";

	private const string SilverBooksTag = "GUI_SCREEN_WORLD_MAP_REGIONS_INFO_PAGE_SILVERBOOKS_FOUND";

	/// <summary>The one region the gargoyle and silver-book rows belong to.</summary>
	private const string MysteryIsland = "MysteryIsland";

	/// <summary>
	/// The Economy row's five-star strip — a component with six slides (`Master Slide`,
	/// `1star`..`5star`). 0x829AB374 does `goToSlide(strip, rating + 1)`, so the index is one-based.
	/// </summary>
	private const string StarRating = "Page1.Contents.Text.StarRating";

	/// <summary>How many `Nstar` slides the strip owns, after the master.</summary>
	private const int StarSlides = 5;

	/// <summary>Which region the highlighted row names, or -1. Call before <see cref="Values"/>.</summary>
	public static int RegionOf(Engine_UiExpandableMenu.Item? row)
	{
		if (row is null || row.EventType != RegionInfoMode)
		{
			return -1;
		}

		uint key = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		long index = (long)key - RegionKeyBase;
		return index >= 0 && index < int.MaxValue ? (int)index : -1;
	}

	/// <summary>
	/// The Economy strip's slide, or nothing when there is no fresh sample. Clamped to `5star`:
	/// `sub_8246FE20` can return 5, and the guest's slide 6 is refused by `sub_82BB5760`, which
	/// would leave the previous region's rating on screen.
	/// </summary>
	/// <summary>
	/// The named category pages — `QuestsPage`, `JobsPage`, `SalesPage`, `FamilyPage`, and
	/// `FriendsPage` (never shown) — which the guest shows one of by the highlighted row's
	/// category (`sub_826C6698`) and hides all of otherwise (`sub_826C6840`), keyed on ITS
	/// highlight, which never moves on this host-driven list. Stated here by the row's root
	/// category instead: the root heading names the page.
	/// </summary>
	public static IReadOnlyDictionary<string, bool> Active(Engine_UiExpandableMenu.Item? row)
	{
		var active = new System.Collections.Generic.Dictionary<string, bool>();
		foreach (string page in CategoryPages)
		{
			active[page] = false;
		}

		Engine_UiExpandableMenu.Item? root = row;
		while (root?.Parent is { } up && up.Parent is not null)
		{
			root = up;
		}

		// THE TWO NUMBERED PAGES ARE PART OF THE SET, and leaving them out is why three layouts
		// drew at once. `worldmapscreen.bgf` authors SEVEN pages on the leaf: `Page1` (the
		// region readout — Distance, Travel Time, Outstanding Fines, Economy, Merchants),
		// `Page2` (the minimap) and the five named category pages. Only the named five were
		// being switched, and `Page1` states NO `position.x` in either of its slides, so nothing
		// ever moved it — it simply drew underneath whichever category page was up.
		//
		// `Page1` is the REGION page: it is what a Regions row shows, and what shows when the
		// row names no category. `Page2` is the map view, which nothing reaches yet.
		active[RegionPage] = false;
		active[MapPage] = false;

		// THE PAGE FOLLOWS THE ROW'S TYPE, NOT ITS PLACE IN THE TREE. The old rule showed a
		// page only for rows INSIDE a category folder — but the quests sit at the ROOT as
		// leaves, beside the Jobs/Sales/Families/Regions folders, so every quest row read as a
		// heading and showed a blank leaf. The row already says what it is: the sources spawn
		// quests 42, jobs 43, sales 44, family 45, regions 41 — the guest's own event types.
		// A FOLDER (a row with children) shows no page; that is data, the same rule every book
		// uses.
		bool folder = row is null || row.Children.Count > 0 ||
			row.EventType == (int)Engine_UiMenuConstants.EventType.Submenu;
		if (folder)
		{
			return active;
		}

		string? shown = row!.EventType switch
		{
			QuestEventType => QuestsPage,
			JobEventType => JobsPage,
			SalesEventType => SalesPage,
			FamilyEventType => FamilyPage,
			RegionInfoMode => RegionPage,    // 41 — the region readout is Page1
			_ => null,
		};

		// A leaf the types do not name falls back to its category folder's page, and to the
		// region page when it has none — the pre-existing behaviour, kept for rows a mod adds.
		if (shown is null && !ReferenceEquals(root, row) && root is not null)
		{
			shown = CategoryPageOf(root.Name) ?? RegionPage;
		}

		// THE MAP IS THE REGION PAGE'S OTHER LEAF: a stick-right turns the book from the
		// readout (`Page1`) to the minimap (`Page2`, `sub_82990E48` pushes page index 1), left
		// turns it back. The turn is host-owned — see Engine_UiMinimapPage.DriveFlip.
		if (ReferenceEquals(shown, RegionPage) && Engine_UiMinimapPage.Shown)
		{
			shown = MapPage;
		}

		if (shown is not null)
		{
			active[shown] = true;
		}

		// The sales page's A-button travels to a sale; a Properties row's A is its repair, so the
		// travel prompt stands only for a sale.
		if (ReferenceEquals(shown, SalesPage))
		{
			active[SaleAButton] = row.EventType == SalesEventType;
		}

		// The job page's A-button is shown or hidden outright by `sub_82426328`, and two of
		// `sub_824708D8`'s branches hide it. Stated only for the job page — naming it anywhere
		// else would gate an element that page does not own.
		if (ReferenceEquals(shown, JobsPage))
		{
			uint definition = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
			active[Engine_UiQuestPages.JobAButton] =
				Engine_UiQuestPages.JobPromptShown(definition);
		}

		return active;
	}

	/// <summary>The guest's own spawn types — `sub_829A3BA0` 42, `sub_829A3E98` 43, and the
	/// sales/family emitters' 44 and 45. The row's type IS its page.</summary>
	private const int QuestEventType = 42;

	private const int JobEventType = 43;

	private const int SalesEventType = 44;

	private const int FamilyEventType = 45;

	private const string QuestsPage = "Foreground.Book.Pages.QuestsPage";

	private const string QuestTitle = "QuestsPage.Contents.PageTitle";

	private const string QuestDescription = "QuestsPage.Contents.Description";

	private const string JobTitle = "JobsPage.Contents.PageTitle";

	private const string JobDescription = "JobsPage.Contents.Description";

	private const string JobsPage = "Foreground.Book.Pages.JobsPage";

	private const string SalesPage = "Foreground.Book.Pages.SalesPage";

	private const string SaleAButton = "SalesPage.Contents.AButton";

	private const string FamilyPage = "Foreground.Book.Pages.FamilyPage";

	/// <summary>`Page1` — the region readout the `Text.*Amount` fields are written into.</summary>
	private const string RegionPage = "Foreground.Book.Pages.Page1";

	/// <summary>`Page2` — `MiniMap`, `Legend` and `Bowerstone`; no row reaches it yet.</summary>
	private const string MapPage = "Foreground.Book.Pages.Page2";

	private static readonly string[] CategoryPages =
	{
		"Foreground.Book.Pages.QuestsPage", "Foreground.Book.Pages.JobsPage",
		"Foreground.Book.Pages.SalesPage", "Foreground.Book.Pages.FamilyPage",
		"Foreground.Book.Pages.FriendsPage",
	};

	private static string? CategoryPageOf(string rootName) => rootName switch
	{
		"Quests" => "Foreground.Book.Pages.QuestsPage",
		"Jobs" => "Foreground.Book.Pages.JobsPage",
		"Sales" => "Foreground.Book.Pages.SalesPage",

		// Our Properties category takes the sales page's title-and-text shape (Engine_UiPropertySource.FillPage).
		"Properties" => "Foreground.Book.Pages.SalesPage",
		"Family" or "Families" => "Foreground.Book.Pages.FamilyPage",
		_ => null,
	};

	public static IReadOnlyDictionary<string, int> Slides(
		Engine_UiExpandableMenu.Item? row,
		Refii.Host.HostBridge.WorldMapRegion region)
	{
		var slides = new Dictionary<string, int>(2);

		// THE JOB PAGE'S STAR STRIPS — `sub_829AFE58`: `StarRating` takes the quest's own
		// `Rating` (quest+28) as its slide unconditionally, `Stars` only for 1..5. Nothing wrote
		// either, so the strip showed whatever the book last held.
		if (row is { EventType: JobEventType } jobRow)
		{
			uint definition = ((uint)jobRow.IdHigh << 16) | ((uint)jobRow.IdLow & 0xFFFFu);
			foreach (Refii.Host.HostBridge.QuestEntry entry in
				Refii.Host.HostBridge.GetQuestLog())
			{
				if (entry.RecordId != definition)
				{
					continue;
				}

				slides[Engine_UiQuestPages.JobStarRating] = entry.Rating;
				if (entry.Rating >= 1 && entry.Rating <= 5)
				{
					slides[Engine_UiQuestPages.JobStars] = entry.Rating;
				}

				break;
			}
		}

		if (!region.IsFor(RegionOf(row)) || region.Stars < 0)
		{
			return slides;
		}

		int stars = region.Stars > StarSlides ? StarSlides : region.Stars;
		slides[StarRating] = stars + 1;
		return slides;
	}

	/// <summary>The region page's values, or blanks when no region row is highlighted.</summary>
	public static IReadOnlyDictionary<string, string> Values(
		Engine_UiExpandableMenu.Item? row,
		Refii.Host.HostBridge.WorldMapRegion region,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		var values = new Dictionary<string, string>(14)
		{
			// THE QUEST AND JOB PAGES, filled from the row's own definition: the title is the
			// resolved label the list already shows, and the description is the record's
			// `DescriptionTextTag` through Babel — the same read the label itself uses. Blanked
			// every sample so the page never keeps another row's story.
			[PageTitle] = string.Empty,
			[Distance] = string.Empty,
			[Travel] = string.Empty,
			[Fine] = string.Empty,
			[Merchants] = string.Empty,
			[Owned] = string.Empty,
			[Chests] = string.Empty,
			[Digspots] = string.Empty,
			[Divespots] = string.Empty,
			[Gargoyles] = string.Empty,
			[GargoylesLabel] = string.Empty,
			[SilverKeys] = string.Empty,
			[SilverKeysLabel] = string.Empty,
		};

		// THE QUEST AND JOB PAGES ARE THEIR OWN CONTRACT — see Engine_UiQuestPages. The quest
		// page is ONE concatenated string carrying the banner, the rewards, the current task,
		// the completed tasks and the description; the job page is six separate lines. Writing
		// only a title and a description here left both nearly blank.
		Engine_UiQuestPages.Blank(values);

		// THE SALES PAGE joins by NAME KEY: the row carries the hash the sale published, which
		// is what the guest's own list is keyed on. Sales come back in the guest's order —
		// sales first, then shortages — so the row's id indexes it directly.
		if (row is { EventType: SalesEventType } sale)
		{
			uint key = ((uint)sale.IdHigh << 16) | ((uint)sale.IdLow & 0xFFFFu);
			foreach (Refii.Host.HostBridge.Sale live in Refii.Host.HostBridge.GetSaleDetails())
			{
				if (live.NameKey == key)
				{
					Engine_UiQuestPages.FillSale(values, live, strings);
					break;
				}
			}
		}

		if (row is { } quest &&
			(quest.EventType == QuestEventType || quest.EventType == JobEventType))
		{
			uint definition = ((uint)quest.IdHigh << 16) | ((uint)quest.IdLow & 0xFFFFu);
			Engine_UiQuestPages.Fill(
				values, definition, quest.EventType == JobEventType,
				quest.StatedLabel ?? quest.Name, strings);
		}

		// A PROPERTIES ROW — a property's sign, or what a partnership row does.
		if (row is { } property)
		{
			Engine_UiPropertySource.FillPage(values, property.Name, strings);
		}

		// THE FAMILY PAGE indexes by ORDINAL — `sub_829B04E0` takes `52 * n + begin` straight
		// off the collector's vector, and the row's id is that same loop index.
		if (row is { EventType: FamilyEventType } member)
		{
			uint ordinal = ((uint)member.IdHigh << 16) | ((uint)member.IdLow & 0xFFFFu);
			Engine_UiFamilyPage.Fill(values, ordinal, strings);
		}

		// The sample must be this row's: the pump answers one tick behind, so on the frame the
		// cursor moves the store still holds the previous region. Blank rather than mismatch.
		if (!region.IsFor(RegionOf(row)))
		{
			return values;
		}

		// The sample's name is the region's own Babel tag (`TEXT_REGION_BRIGHTWOOD`) — the
		// export's contract says the names ARE their own Babel keys — so it resolves through
		// the book like any other tag, and falls back to the tag when the book lacks it.
		values[PageTitle] = strings is not null &&
			strings.TryGetString(region.Name, out string title) && title.Length != 0
				? title
				: region.Name;
		values[Distance] = region.Distance;
		values[Travel] = region.Travel;
		values[Fine] = region.Fine;
		values[Merchants] = region.Merchants;
		values[Owned] = region.Owned;
		values[Chests] = region.Chests;
		values[Digspots] = region.Digspots;
		values[Divespots] = region.Divespots;

		// THE GARGOYLE ROW IS MYSTERY ISLAND'S. `sub_829AB0E0` shows its three elements only
		// when the region matches, and hides them otherwise — every other region left a bare
		// count under a caption nothing had written. Blank here, so the draw gate takes it out
		// with the label rather than stranding the number.
		bool mysteryIsland = string.Equals(region.Name, MysteryIsland,
			System.StringComparison.OrdinalIgnoreCase) ||
			region.Name.EndsWith(MysteryIsland, System.StringComparison.OrdinalIgnoreCase);

		values[Gargoyles] = mysteryIsland ? region.Gargoyles : string.Empty;
		values[GargoylesLabel] = mysteryIsland ? Babel(strings, GargoylesTag) : string.Empty;

		// The silver caption is the guest's, not the file's — see SilverKeysLabel.
		values[SilverKeys] = region.SilverKeys;
		values[SilverKeysLabel] = Babel(strings, mysteryIsland ? SilverBooksTag : SilverKeysTag);
		return values;
	}

	private static string Babel(Refii.Text.Engine_BabelDatabase? strings, string tag) =>
		strings is { } book && book.TryGetString(tag, out string resolved) && resolved.Length != 0
			? resolved
			: string.Empty;
}
