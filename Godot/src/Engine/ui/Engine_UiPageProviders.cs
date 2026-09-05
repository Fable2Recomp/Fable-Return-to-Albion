using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// The providers a screen template can name, and the registry that resolves the name.
///
/// Each is a thin adapter over the page class that already computes the screen's values — the
/// computation stays where it was, and only the ROUTING moves into data. That keeps this change
/// to the one thing it is meant to fix: which code runs for which screen.
/// </summary>
public static class Engine_UiPageProviders
{
	public static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();
	public static readonly IReadOnlyDictionary<string, uint> NoTextures = new Dictionary<string, uint>();
	public static readonly IReadOnlyDictionary<string, bool> NoActive = new Dictionary<string, bool>();
	public static readonly IReadOnlyDictionary<string, Vector3> NoPositions = new Dictionary<string, Vector3>();
	public static readonly IReadOnlyDictionary<string, int> NoSlides = new Dictionary<string, int>();

	public const string Abilities = "abilities";
	public const string Logbook = "logbook";
	public const string Crime = "crime";
	public const string WorldMap = "worldmap";
	public const string Options = "options";
	public const string ItemBook = "item-book";
	public const string Achievements = "achievements";
	public const string RealEstate = "realestate";

	private static readonly Dictionary<string, Engine_UiPageProvider> Registry = new()
	{
		[Abilities] = new AbilitiesProvider(),
		[Logbook] = new LogbookProvider(),
		[Crime] = new CrimeProvider(),
		[WorldMap] = new WorldMapProvider(),
		[Options] = new OptionsProvider(),
		[ItemBook] = new ItemBookProvider(),
		[Achievements] = new AchievementsProvider(),
		[RealEstate] = new RealEstateProvider(),
	};

	/// <summary>The provider a template names, or null when it names none or an unknown one.</summary>
	public static Engine_UiPageProvider? Find(string name) =>
		name.Length != 0 && Registry.TryGetValue(name, out Engine_UiPageProvider? provider)
			? provider
			: null;

	public static IEnumerable<string> Names => Registry.Keys;

	private sealed class AbilitiesProvider : Engine_UiPageProvider
	{
		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
			Engine_UiAbilityPage.Values(
				page.Row, page.Vitals.AbilityLevels, page.Vitals.ExperienceAvailable,
				page.Hero.SpellDamage, page.Strings);

		public IReadOnlyDictionary<string, uint> Textures(in Engine_UiPageContext page) =>
			Engine_UiAbilityPage.Textures(page.Row);

		/// <summary>
		/// The abilities book authors `Page1`, `Page2` and `Page4` on one leaf. A CATEGORY row
		/// (`Strength`) shows the category page; an ability under it pops its own page out.
		/// </summary>
		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page)
		{
			// THE SPELL BLADE LIVES ON PAGE 4, and opening it is a PAGE PUSH.
			//
			// `sub_826C2480` does five things at once, and this was the missing one:
			// `sub_82990E48(screen+48, presentation, 3)` pushes book page index 3 — `Page4` —
			// alongside hiding the row list and routing input to the widget. Godot did the
			// first two and not the third, so activating `Spell Selector` took the list away
			// and left the page it was replaced by switched OFF: an empty leaf with the orbs
			// still on it, which is exactly what the screen showed.
			//
			// The blade's own drawing and input already work; it simply had no page to stand on.
			string[] pages = PagesOf(page, AbilityPageElements);
			if (Refii.Host.HostBridge.AbilitiesAssignmentOpen())
			{
				var blade = new Dictionary<string, bool>(pages.Length);
				foreach (string path in pages)
				{
					blade[path] = path.EndsWith(BladePage, System.StringComparison.Ordinal);
				}

				return blade;
			}

			return BookPages(BookPage, page.Row, pages);
		}

		/// <summary>The page the spell blade is authored on — `sub_826C2480` pushes index 3.</summary>
		private const string BladePage = "Page4";

		/// <summary>
		/// THE PAGE THAT IS UP SITS IN THE VISIBLE SLOT — the same parking as the item books:
		/// `abilitiesscreen.bgf` authors `Page1`, `Page2` and `Page4` on one leaf, and the
		/// `Intro` slide of `Page2` and of `Page4` writes `Contents position.x = 430` (off the
		/// 4:3 canvas) while their `Outro` writes 0; `Page1`'s writes 0 both ways. The guest
		/// played `Outro` on its own highlight edge, which no longer fires under an owned cursor,
		/// so a page flipped to (or the blade pushed) stayed parked off-screen. Godot owns the
		/// page: the one that is up is placed at the `Outro` numbers; the others are switched
		/// off by <see cref="Active"/>.
		/// </summary>
		public IReadOnlyDictionary<string, Vector3> Positions(in Engine_UiPageContext page)
		{
			string[] pages = PagesOf(page, AbilityPageElements);
			string? up = null;
			if (Refii.Host.HostBridge.AbilitiesAssignmentOpen())
			{
				foreach (string path in pages)
				{
					if (path.EndsWith(BladePage, System.StringComparison.Ordinal))
					{
						up = path;
					}
				}
			}
			else if (BookPage >= 0 && BookPage < pages.Length)
			{
				up = pages[BookPage];
			}

			return up is null
				? NoPositions
				: new Dictionary<string, Vector3>(1) { [$"{up}.Contents"] = Vector3.Zero };
		}

		/// <summary>Which experience orb is lit — see <see cref="Engine_UiAbilityPage.Slides"/>.</summary>
		public IReadOnlyDictionary<string, int> Slides(in Engine_UiPageContext page) =>
			Engine_UiAbilityPage.Slides(
				page.Row, page.Vitals.AbilityLevels, page.Vitals.ExperienceAvailable);
	}

	private sealed class LogbookProvider : Engine_UiPageProvider
	{
		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
			Engine_UiLogbookPage.Values(page.Row, page.Hero, page.Quests, page.Strings);

		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page) =>
			Engine_UiLogbookPage.Active(page.Row);

		public IReadOnlyDictionary<string, int> Slides(in Engine_UiPageContext page) =>
			Engine_UiLogbookPage.Slides(page.Row, page.Hero);

		public IReadOnlyDictionary<string, Vector3> Positions(in Engine_UiPageContext page) =>
			Engine_UiLogbookPage.Positions(page.Row, page.Hero);
	}

	/// <summary>
	/// The achievements screen — see <see cref="Engine_UiAchievementPage"/>. It states NO slides
	/// on purpose; the page class says why.
	/// </summary>
	private sealed class AchievementsProvider : Engine_UiPageProvider
	{
		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
			Engine_UiAchievementPage.Values(page.Row);

		public IReadOnlyDictionary<string, uint> Textures(in Engine_UiPageContext page) =>
			Engine_UiAchievementPage.Textures(page.Row);

		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page) =>
			Engine_UiAchievementPage.Active(page.Row);
	}

	/// <summary>
	/// Screen 28 — see <see cref="Engine_UiRealEstatePage"/>. NOT cursor-driven: the left column
	/// is a list of commands and there is one building, so the row is ignored entirely and both
	/// pages describe the same record.
	/// </summary>
	private sealed class RealEstateProvider : Engine_UiPageProvider
	{
		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
			Engine_UiRealEstatePage.Values(Refii.Host.HostBridge.GetRealEstate(), page.Strings);

		public IReadOnlyDictionary<string, int> Slides(in Engine_UiPageContext page) =>
			Engine_UiRealEstatePage.Slides(Refii.Host.HostBridge.GetRealEstate());
	}

	private sealed class CrimeProvider : Engine_UiPageProvider
	{
		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
			Engine_UiCrimePage.Values(page.Crime, page.Strings);

		/// <summary>
		/// `crimescreen.bgf` authors THREE pages of identical shape, one per row, and
		/// `Engine_UiCrimePage` already writes all three texts — nothing chose between them, so
		/// the page shown was whichever the guest last left.
		///
		/// The mapping is taken from what each page SAYS, not from row order: Page1 carries the
		/// fine, and Page3's terms branch is gated on the same flag that gates the
		/// CommunityService row, which leaves the bare-charges Page2 for ResistArrest.
		/// </summary>
		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page) =>
			BookPagesByOpacity(
				page.Row?.Name switch
				{
					"PayFine" => 0,
					"ResistArrest" => 1,
					"CommunityService" => 2,
					_ => 0,
				},
				PagesOf(page, CrimePageElements));
	}

	private sealed class WorldMapProvider : Engine_UiPageProvider
	{
		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
			Engine_UiWorldMapPage.Values(page.Row, page.WorldMap, page.Strings);

		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page) =>
			Engine_UiWorldMapPage.Active(page.Row);

		public IReadOnlyDictionary<string, int> Slides(in Engine_UiPageContext page) =>
			Engine_UiWorldMapPage.Slides(page.Row, page.WorldMap);

		/// <summary>Name the highlighted region so the pump's next answer is this cursor's.</summary>
		public void Prepare(in Engine_UiPageContext page)
		{
			int region = Engine_UiWorldMapPage.RegionOf(page.Row);
			if (region >= 0)
			{
				Refii.Host.HostBridge.SetWorldMapRegion((uint)region);
			}

			// Change-keyed: which row Prepare saw and what it asked for. A blank region page
			// cannot say whether the request fired; this can.
			string saw = page.Row is { } row
				? $"'{row.Name}' type={row.EventType} id={(uint)row.IdHigh << 16 | (uint)row.IdLow & 0xFFFFu}"
				: "no row";
			if (saw != _preparedFor)
			{
				_preparedFor = saw;
				Godot.GD.Print($"[worldmap-prepare] {saw} -> region {region}");
			}
		}

		private string _preparedFor = string.Empty;
	}

	private sealed class OptionsProvider : Engine_UiPageProvider
	{
		/// <summary>
		/// The options screen is not a two-page book — `optionsscreen.bgf` authors FOUR pages,
		/// one per row, and the row picks it. Pinned by this file's own field constants rather
		/// than by row order, which differs: `Page2.Contents.pickbox1` is the invert-camera
		/// control, `Page3.Contents.slider1` is Gamma, and `Page4`'s three sliders are the
		/// volumes — so Game is Page1, Controls Page2, Video Page3, Audio Page4, while the rows
		/// read Game, Video, Controls, Audio.
		///
		/// Nothing selected between them before, so the right page was whatever the guest last
		/// left up — which on this screen is a page of the wrong category.
		/// </summary>
		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page) =>
			BookPagesByOpacity(PageIndexOf(PickedRow(page)), PagesOf(page, OptionsPageElements));

		/// <summary>
		/// The row that picks the page: the highlighted list row, or — once code 36 has opened
		/// the page level and the list is dormant with no highlight — the page the pending
		/// tracker entered. Without the second half the page level pinned Page1 while Page2's
		/// contents stayed parked at x 430, off the leaf.
		/// </summary>
		private static string? PickedRow(in Engine_UiPageContext page) =>
			Engine_UiOptionsPending.Open ? Engine_UiOptionsPending.Page : page.Row?.Name;

		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
			Engine_UiOptionsPage.Values(
				page.Options, page.Sliders, page.Strings, page.OptionsPageOpen);

		public IReadOnlyDictionary<string, Vector3> Positions(in Engine_UiPageContext page)
		{
			// THE PAGE'S CONTENTS SIT ON THE LEAF. `optionsscreen.bgf` parks each page's
			// `Contents` for the fold (Page1/2 at 0, Page3/4 at -400) and the stack itself at
			// 405 — the row picks the page, so the picked page's contents are pinned where the
			// pinned stack shows them, as the item books pin theirs.
			var placed = new Dictionary<string, Vector3>(Engine_UiOptionsPage.Positions(page.Sliders));
			string[] pages = PagesOf(page, OptionsPageElements);
			int index = PageIndexOf(PickedRow(page));
			if (index >= 0 && index < pages.Length)
			{
				placed[$"{pages[index]}.Contents"] = Vector3.Zero;
			}

			return placed;
		}

		private static int PageIndexOf(string? row) => row switch
		{
			"Game" => 0,
			"Controls" => 1,
			"Video" => 2,
			"Audio" => 3,
			_ => 0,
		};

		public bool NeedsOptionSliders => true;
	}

	/// <summary>
	/// Items, clothing, gift giving, the shop, furniture and decoration: five ids over three
	/// `.bgf` files, one skeleton, one provider. The shop's star rating is the one thing only it
	/// has, keyed on the row rather than the screen.
	/// </summary>
	private sealed class ItemBookProvider : Engine_UiPageProvider
	{
		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
			Engine_UiItemPage.Values(page.Row, page.Strings);

		public IReadOnlyDictionary<string, uint> Textures(in Engine_UiPageContext page) =>
			Engine_UiItemPage.Textures(page.Row);


		/// <summary>The page that is up, plus the star strip.</summary>
		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page)
		{
			Dictionary<string, bool> active = BookPages(
				BookPage, page.Row,
				page.ScreenId == ShopScreenId ? ShopPageElements : BookPageElements);
			AddItemStars(active, page.Row);

			// THE SHOP'S FRONT PAGE IS ITS OWN UNIT. `Foreground.FrontPage` (name, owner,
			// keeper, discount, wealth) is not one of the book's pages; the guest shows it on
			// a category row (type 301, the front page) and hides it on an item. Without the
			// switch it stayed drawn under every item page.
			if (page.ScreenId == ShopScreenId)
			{
				active["Foreground.FrontPage"] =
					page.Row is null ||
					page.Row.EventType == (int)Engine_UiMenuConstants.EventType.Submenu;
			}

			return active;
		}

		/// <summary>
		/// THE PAGE THAT IS UP SITS IN THE VISIBLE SLOT. `giftgivingscreen.bgf` authors the two
		/// right-hand pages on one leaf and PARKS the second: `Page2`'s own `Intro` slide writes
		/// `Page2.Contents position.x = 430` (past the 4:3 canvas) and its `Outro` writes
		/// `x = 0`; `Page1`'s `Intro`/`Outro` both write 0. The guest played `Outro` on a
		/// highlight-change edge keyed off ITS Lua cursor, which stops moving once Godot owns
		/// the list — so `Page2` stayed parked at 430 and every description, stat and value on
		/// it rendered off the right edge of the screen (mesh built, visible, full opacity, at
		/// x 637). Godot owns the page, so the page that is up is placed at the slide's own
		/// numbers here; the other page is switched off by <see cref="Active"/>, so where it
		/// parks is immaterial.
		/// </summary>
		public IReadOnlyDictionary<string, Vector3> Positions(in Engine_UiPageContext page)
		{
			string[] pages = page.ScreenId == ShopScreenId ? ShopPageElements : BookPageElements;
			if (BookPage < 0 || BookPage >= pages.Length)
			{
				return NoPositions;
			}

			var positions = new Dictionary<string, Vector3>(5)
			{
				[$"{pages[BookPage]}.Contents"] = Vector3.Zero,

				// THE RING OVER THE PAGE. `ModelHider` is authored at (200, 0, 0) with its ring
				// 39 units AWAY (behind the parchment, which sits 1 unit in front of
				// `Contents`): Gameface paints in tree order and never depth-tests, so the
				// artist could park the ring back there for the 3D entity's sake and still see
				// it. Our quads depth-test, so the group comes 41 units toward the viewer —
				// the ring lands 2 in front of the parchment, the page texture on it, and the
				// proxy oval stays far behind where its depth keeps it out of the way.
				[$"{pages[BookPage]}.{HiderOf(page.ScreenId)}"] = new Vector3(200f, 0f, -41f),
				// The curtain is kept as the page's parchment (see Slides), so it goes BEHIND the
				// page's text and the ring instead of over them: authored (-204, 0, +41) under the
				// hider puts it on the text's plane (readout z 2250), where it wins by draw order.
				// It cannot go far back either: the book's cloth `background_green` is a
				// screen-wide quad at readout z 2220 (`refii_elements paint:217,120`), so the
				// parchment lives between the cloth and the page's shadow (2246) and frame (2249).
				[$"{pages[BookPage]}.{HiderOf(page.ScreenId)}.PageTexture"] = new Vector3(-204f, 0f, ModelHiderParchmentDepth),
			};

			if (page.ScreenId == EquipmentScreenId)
			{
				string hider = $"{pages[BookPage]}.{HiderOf(page.ScreenId)}";
				// The equipment book keeps its pale oval INSIDE the pinned group (the item books
				// author it beside it), so it would ride 41 units forward over the ring: it goes
				// back to just in front of the parchment. And the proxy — the 3D preview's anchor
				// — stays where the guest's Hero slide put it (20, −170: a mannequin's feet),
				// because a stated transform outranks the host slide's own command; the item
				// slides' anchor (30, 30, +350) is stated outright so the item hangs in the ring.
				positions[$"{hider}.MapHighlight"] = new Vector3(16f, 50f, ModelHiderParchmentDepth - 1f);
				positions[$"{hider}.model_proxy"] = new Vector3(30f, 30f, 350f);
			}

			return positions;
		}

		/// <summary>
		/// The star strip's own slides — a rating, not a page. The SHOP takes it from the row's
		/// `Float4`, which its populator fills and its filler range-checks; every other item
		/// book reads the record's GDB `Rating` straight through. Neither path bins anything.
		/// </summary>
		public IReadOnlyDictionary<string, int> Slides(in Engine_UiPageContext page)
		{
			IReadOnlyDictionary<string, int> stars = page.ScreenId == ShopScreenId
				? ShopStarSlides(page.Row)
				: Engine_UiItemPage.Slides(page.Row, page.Strings);

			// THE PARCHMENT STAYS. `ModelHider` authors two slides: `Show` (1) fades
			// `PageTexture.opacity` 100→0 — the page opens as a window for the 3D entity the
			// guest draws BEHIND it — and `Hide` (2) fades it back to 100. The guest puts the
			// group on `Show` on every item row (`sub_82992998` → `sub_82426D18(…ModelHider, 1)`),
			// so its own final state is the cloth showing through the page. Godot draws the
			// model OVER the page, the window buys nothing, and the remaster keeps the
			// parchment: the up page's hider is stated on `Hide`, which also releases the
			// guest's stale opacity on everything that slide commands.
			string[] pages = page.ScreenId == ShopScreenId ? ShopPageElements : BookPageElements;
			if (BookPage < 0 || BookPage >= pages.Length)
			{
				return stars;
			}

			var slides = new Dictionary<string, int>(stars.Count + 1);
			foreach ((string key, int value) in stars)
			{
				slides[key] = value;
			}

			slides[$"{pages[BookPage]}.{HiderOf(page.ScreenId)}"] = HideSlideOf(page.ScreenId);
			return slides;
		}

		/// <summary>
		/// THE SAME CURTAIN UNDER TWO NAMES. `equipmentscreen.bgf` (screen 16) authors the
		/// item books' `ModelHider` group as `TypeSpecific`, with the same children at the same
		/// local offsets (parchment at (-204, 0, +41), ring at (14, 30, +39), proxy at
		/// (20, -170, +350)) and FOUR slides instead of two: `HeroOn` (1) and `HeroOff` (3)
		/// are the mannequin mode (ring off, proxy low), `ItemOn` (2) and `ItemOff` (4) the
		/// item mode (ring on, proxy centred); the `On` slides fade the parchment out, the
		/// `Off` slides fade it back. The guest parks it on a Hero slide; the remaster shows
		/// the item in its ring on the parchment like every other book — `ItemOff`.
		/// </summary>
		private static string HiderOf(uint screenId) =>
			screenId == EquipmentScreenId ? "Contents.TypeSpecific" : "Contents.ModelHider";

		private static int HideSlideOf(uint screenId) =>
			screenId == EquipmentScreenId ? TypeSpecificItemOffSlide : ModelHiderHideSlide;

		private const int ModelHiderHideSlide = 2;
		private const int TypeSpecificItemOffSlide = 4;
		private const float ModelHiderParchmentDepth = 43f;
	}

	private const uint ShopScreenId = 22;
	private const uint EquipmentScreenId = 16;

	/// <summary>
	/// Which right-hand page of the book that is up is showing — 0 Page1, 1 Page2. Reset with
	/// the screen; moved by the page-turn lever.
	///
	/// Shared, because every book screen is the same shape: two authored pages on one leaf, and
	/// the guest slides between them. Only one screen is up at a time, so one index serves.
	/// </summary>
	public static int BookPage { get; set; }

	/// <summary>
	/// Which right-hand page is up — as a plain switch, because GODOT OWNS THE PAGE.
	///
	/// This used to state a SLIDE on `Foreground.Book` and on each `PageN`, reproducing the
	/// mechanism the shipped book uses to park a page at `position.x = 430` and slide it back.
	/// That was the wrong shape twice over. It did not work — `Page1`, `Page2` and `Pages` own
	/// no slides at all, and the numbers differ per book (`ShowPages` is slide 1 in
	/// `abilitiesscreen`, 7 in `giftgivingscreen`, 2 in `equipmentscreen`), so both pages stayed
	/// on the leaf and drew over each other. And it was the wrong JOB: Godot builds and draws
	/// this book, so where a page sits is ours to decide, not something to recover from the
	/// original's authored slides.
	///
	/// So the page is switched, the way the crime and options screens already switch theirs.
	/// One mechanism for every book, and a modded book needs no `ShowPages` slide to work.
	///
	/// A FOLDER SHOWS NO RIGHT PAGE — the shipped rule, kept because it is about the DATA (this
	/// row has children, so there is nothing of its own to show), not about presentation.
	/// </summary>
	private static Dictionary<string, bool> BookPages(
		int page, Engine_UiExpandableMenu.Item? row, params string[] pages)
	{
		// 301 OR 23 — `sub_826C3C78` at 0x826c3d0c hides the pages for both: a category and the
		// spell-selector row alike show no right page. The shipped mechanism is the Book's
		// `HidePages` slide, which HIDES NOTHING — it moves `Pages` to x 0, a position the host's
		// own `PageStackAt` pins anyway — so the page switch here is the one that must say it.
		bool folder = row is { Children.Count: > 0 } ||
			row?.EventType == (int)Engine_UiMenuConstants.EventType.Submenu ||
			row?.EventType == (int)Engine_UiMenuConstants.EventType.AssignSpellsAbilityItem;

		var active = new Dictionary<string, bool>(pages.Length);
		for (int i = 0; i < pages.Length; ++i)
		{
			active[pages[i]] = !folder && i == page;
		}

		return active;
	}

	/// <summary>
	/// The same switch for a screen whose rows are all leaves — crime, options — so there is no
	/// folder rule to apply. One mechanism; the only difference is that nothing here can be a
	/// folder.
	/// </summary>
	private static Dictionary<string, bool> BookPagesByOpacity(int page, params string[] pages) =>
		BookPages(page, row: null, pages);

	/// <summary>
	/// WHICH PAGE IS UP IS STATED, both halves of it, and that is the fix for a right page that
	/// "appeared and then hid".
	///
	/// `giftgivingscreen.bgf` authors Page1 and Page2 on the SAME right-hand leaf — Page2 parked
	/// at x 430, beyond the 4:3 canvas the console showed — and the guest slides one in as it
	/// slides the other out. It does that on a highlight-change edge keyed off ITS Lua cursor,
	/// which stops moving the moment Godot owns the list, so whichever page the guest last left
	/// up stayed up and nothing ever turned Page1 back on. Saying only "Page2 off" left the
	/// other half to a writer that had stopped writing.
	///
	/// Both are named now, so the page the host is filling is the page that draws.
	/// </summary>
	/// <summary>
	/// HOW MANY PAGES A BOOK HAS IS PER BOOK, and every one of them must be named here.
	///
	/// | screen | pages |
	/// |---|---|
	/// | items / gift / equipment 15, 16, 25, 26, 27 | Page1, Page2 |
	/// | shop 22 | Page1, Page2, **Page3** |
	/// | abilities 14 | Page1, Page2, **Page4** (there is no Page3) |
	/// | world map 23 | Page1, Page2 + five named |
	/// | options 18 | Page1..Page4 |
	/// | crime 21 | Page1..Page3 |
	///
	/// OMITTING A PAGE IS NOT NEUTRAL: a page nobody switches off keeps drawing, over whichever
	/// page is up. That is the shop drawing three layouts at once and the abilities book drawing
	/// over itself.
	/// </summary>
	private static readonly string[] BookPageElements =
	{
		"Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2",
	};

	private static readonly string[] OptionsPageElements =
	{
		"Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2",
		"Foreground.Book.Pages.Page3", "Foreground.Book.Pages.Page4",
	};

	private static readonly string[] CrimePageElements =
	{
		"Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2",
		"Foreground.Book.Pages.Page3",
	};

	/// <summary>The shop authors a third page.</summary>
	private static readonly string[] ShopPageElements =
	{
		"Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2",
		"Foreground.Book.Pages.Page3",
	};

	/// <summary>
	/// The abilities book authors `Page4` — there is no `Page3` — and leaving it out draws its
	/// blade over whichever page is up.
	/// </summary>
	private static readonly string[] AbilityPageElements =
	{
		"Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2",
		"Foreground.Book.Pages.Page4",
	};

	/// <summary>
	/// THE ITEM BOOKS' STAR STRIP, which is not the shop's.
	///
	/// `shopscreen.bgf` builds its rating as a component with five SLIDES; `equipmentscreen.bgf`
	/// and `giftgivingscreen.bgf` build theirs as two sibling groups of five elements —
	/// `StarRating/staron/staron1..5` over `StarRating/staroff/staroff1..5` — and show N stars by
	/// lighting the first N of one and the last 5-N of the other. Dumped from the shipped files
	/// rather than assumed; the two spellings are why the shop keeps its own path.
	///
	/// The row's rating is in HALF-star units (1..10), the same number `sub_826CE230` passes as
	/// `Float4`, so the whole-star count is half of it — matching `ShopStarSlides`.
	/// </summary>
	/// <summary>
	/// ONE BUTTON PROMPT, NOT FOUR. `PageButtons` authors a DIAMOND of four — `A` at (0, -30),
	/// `B` at (30, 0), `X` at (-30, 0), `Y` at (0, 30) — each with its own `Text`, and every one
	/// authored `act=1`. Drawing them all put `Select`, `Cancel`, `Use` and `Remove` on top of
	/// each other and over the item's own value line, because each `Text` sits at the same
	/// (-24, 1) offset inside its parent and the words are wider than the 30-unit spacing.
	///
	/// Only `A` is offered. That is a statement about what THIS book does — one action, taken
	/// with A — not a reproduction of the original's per-item prompt logic; a book that offers
	/// more switches more on.
	/// </summary>
	/// <summary>
	/// The pages a SCREEN RECORD names, or the built-in set when it names none yet.
	///
	/// The record is the source of truth — see <see cref="Engine_UiScreenTemplate.Pages"/> — so
	/// a modded screen declares its own pages instead of matching a table in here. The arrays
	/// remain only for screens nobody has authored a record for.
	/// </summary>
	private static string[] PagesOf(in Engine_UiPageContext page, string[] fallback) =>
		Engine_UiTemplates.Screen(page.ScreenId) is { Pages.Count: > 0 } screen
			? screen.Pages.ToArray()
			: fallback;

	private static void AddPagePrompts(Dictionary<string, bool> active, string[] pages)
	{
		foreach (string page in pages)
		{
			active[$"{page}.Contents.PageButtons.A"] = true;
			foreach (string unused in PromptsNotOffered)
			{
				active[$"{page}.Contents.PageButtons.{unused}"] = false;
			}
		}
	}

	private static readonly string[] PromptsNotOffered = { "B", "X", "Y" };


	private static void AddItemStars(
		Dictionary<string, bool> active, Engine_UiExpandableMenu.Item? row)
	{
		if (row is null || row.RatingSlide < Engine_UiMenuConstants.OneStarEmptyRatingSlide ||
			row.RatingSlide > Engine_UiMenuConstants.FiveStarFullRatingSlide)
		{
			// An item that states no rating draws no stars at all rather than one — the same
			// choice `Engine_UiInventorySource.RatingOf` makes for the row itself.
			for (int star = 1; star <= 5; ++star)
			{
				active[$"StarRating.staron.staron{star}"] = false;
				active[$"StarRating.staroff.staroff{star}"] = false;
			}

			return;
		}

		int stars = System.Math.Clamp((row.RatingSlide + 1) / 2, 1, 5);
		for (int star = 1; star <= 5; ++star)
		{
			active[$"StarRating.staron.staron{star}"] = star <= stars;
			active[$"StarRating.staroff.staroff{star}"] = star > stars;
		}
	}

	/// <summary>
	/// The shop's `StarRating` components — one per page, five slides `1star`..`5star` after the
	/// master in `shopscreen.bgf`. The row's `RatingSlide` is the same number in half-star units
	/// (1..10), so the whole-star slide is half of it, rounded.
	/// </summary>
	private static readonly string[] ShopStarRatings =
	{
		"FrontPage.Contents.StarRating",
		"Page1.Contents.StarRating",
		"Page2.Contents.StarRating",
		"Page3.Contents.StarRating",
	};

	private static IReadOnlyDictionary<string, int> ShopStarSlides(Engine_UiExpandableMenu.Item? row)
	{
		if (row is null || row.RatingSlide < Engine_UiMenuConstants.OneStarEmptyRatingSlide ||
			row.RatingSlide > Engine_UiMenuConstants.FiveStarFullRatingSlide)
		{
			return NoSlides;
		}

		int stars = System.Math.Clamp((row.RatingSlide + 1) / 2, 1, 5);
		var slides = new Dictionary<string, int>(ShopStarRatings.Length);
		foreach (string path in ShopStarRatings)
		{
			slides[path] = stars;
		}

		return slides;
	}
}
