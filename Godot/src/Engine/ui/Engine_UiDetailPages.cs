using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// The detail page of every owned-list screen, routed by screen id.
///
/// A book screen fills its detail page on a highlight-change edge, keyed on the GUEST's Lua
/// highlight (`g_Menu:GetHighlightedMenuItemData()`) against a per-screen "last resolved" dword
/// — `screen+624` on abilities, its own offset on each of the others. Godot owns the cursor, so
/// none of those keys ever move and the guest's fill never runs.
///
/// A screen is a PROVIDER, not a branch in the caller: `GameUi` asks this one question for
/// whichever screen is up, so adding a screen is a case here and a class beside it.
/// </summary>
public static class Engine_UiDetailPages
{
	/// <summary>
	/// The item-book family: items, clothing/equipment, gift giving, furniture upgrade and
	/// decoration. Five ids over three `.bgf` files, one skeleton, one provider.
	/// </summary>
	/// 22 (the shop) is here too: it is the same item record behind different element paths,
	/// which <see cref="Engine_UiItemPage"/> carries as a second set of keys.
	private static readonly uint[] ItemBooks = { 15u, 16u, 22u, 25u, 26u, 27u };

	/// <summary>The logbook — subject-named pages, and only its expression detail is ours.</summary>
	private const uint LogbookScreenId = 17u;

	/// <summary>The crime screen — not cursor-driven, but ours to draw all the same.</summary>
	private const uint CrimeScreenId = 21u;

	/// <summary>The world map — its region page is sampled a tick ahead, see the provider.</summary>
	public const uint WorldMapScreenId = 23u;

	/// <summary>The options screen — all four pages; 3 and 4 place pips as well as text.</summary>
	public const uint OptionsScreenId = 18u;

	/// <summary>Screens whose detail page Godot fills.</summary>
	public static bool Owns(uint screenId)
	{
		if (screenId == Engine_UiScreenInput.AbilitiesScreenId ||
			screenId == LogbookScreenId ||
			screenId == CrimeScreenId ||
			screenId == WorldMapScreenId ||
			screenId == OptionsScreenId)
		{
			return true;
		}

		foreach (uint id in ItemBooks)
		{
			if (id == screenId)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// The host-owned field values for this screen's detail page, as final display text. Empty
	/// for a screen with no provider, leaving every field on the guest's own channel.
	/// </summary>
	public static IReadOnlyDictionary<string, string> Values(
		uint screenId,
		Engine_UiExpandableMenu.Item? row,
		Refii.Host.HostBridge.HeroVitals vitals,
		Refii.Host.HostBridge.HeroDerived hero,
		List<Refii.Host.HostBridge.CompletedQuest> quests,
		Refii.Host.HostBridge.CrimeScreen crime,
		Refii.Host.HostBridge.WorldMapRegion worldMap,
		Refii.Host.HostBridge.GameOptions options,
		Refii.Host.HostBridge.OptionSliders sliders,
		Refii.Text.Engine_BabelDatabase? strings,
		bool optionsPageOpen = false)
	{
		if (screenId == Engine_UiScreenInput.AbilitiesScreenId)
		{
			return Engine_UiAbilityPage.Values(
				row, vitals.AbilityLevels, vitals.ExperienceAvailable, hero.SpellDamage, strings);
		}

		if (screenId == LogbookScreenId)
		{
			return Engine_UiLogbookPage.Values(row, hero, quests, strings);
		}

		if (screenId == CrimeScreenId)
		{
			return Engine_UiCrimePage.Values(crime, strings);
		}

		if (screenId == WorldMapScreenId)
		{
			return Engine_UiWorldMapPage.Values(row, worldMap);
		}

		if (screenId == OptionsScreenId)
		{
			return Engine_UiOptionsPage.Values(options, sliders, strings, optionsPageOpen);
		}

		foreach (uint id in ItemBooks)
		{
			if (id == screenId)
			{
				return Engine_UiItemPage.Values(row, strings);
			}
		}

		return EmptyValues;
	}

	/// <summary>
	/// Quads this screen's provider textures itself — see
	/// <see cref="Engine_UiOwnedBook.HostTextures"/>. Empty for a screen with none.
	/// </summary>
	public static IReadOnlyDictionary<string, uint> Textures(
		uint screenId, Engine_UiExpandableMenu.Item? row) =>
		screenId == Engine_UiScreenInput.AbilitiesScreenId
			? Engine_UiAbilityPage.Textures(row)
			: EmptyTextures;

	/// <summary>
	/// Elements this screen's provider PLACES itself — see
	/// <see cref="Engine_UiOwnedBook.HostPositions"/>. Empty for a screen with none.
	/// </summary>
	public static IReadOnlyDictionary<string, Godot.Vector3> Positions(
		uint screenId, Refii.Host.HostBridge.OptionSliders sliders) =>
		screenId == OptionsScreenId
			? Engine_UiOptionsPage.Positions(sliders)
			: EmptyPositions;

	/// <summary>
	/// Components this screen's provider SWITCHES itself — see
	/// <see cref="Engine_UiOwnedBook.HostSlides"/>. Empty for a screen with none.
	/// </summary>
	public static IReadOnlyDictionary<string, int> Slides(
		uint screenId,
		Engine_UiExpandableMenu.Item? row,
		Refii.Host.HostBridge.WorldMapRegion worldMap)
	{
		IReadOnlyDictionary<string, int> own =
			screenId == WorldMapScreenId
				? Engine_UiWorldMapPage.Slides(row, worldMap)
				: screenId == ShopScreenId
					? ShopStarSlides(row)
					: EmptySlides;
		if (!Owns(screenId))
		{
			return own;
		}

		// The book's pages show for a leaf and hide for a folder — `sub_826C3C78` at 0x826c3d0c:
		// Float3 of the highlighted item 301 (submenu) or 23 (spell assign) → `HidePages`
		// (slide 2), anything else → `ShowPages` (slide 1). Keyed on the guest's highlight there,
		// on ours here.
		var slides = new Dictionary<string, int>(own);
		slides["Foreground.Book"] =
			row is not null && row.EventType != SubmenuEventType && row.EventType != SpellAssignEventType
				? 1
				: 2;
		return slides;
	}

	private const int SubmenuEventType = 301;
	private const int SpellAssignEventType = 23;

	private const uint ShopScreenId = 22;

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
			return EmptySlides;
		}

		int stars = System.Math.Clamp((row.RatingSlide + 1) / 2, 1, 5);
		var slides = new Dictionary<string, int>(ShopStarRatings.Length);
		foreach (string path in ShopStarRatings)
		{
			slides[path] = stars;
		}

		return slides;
	}

	private static readonly Dictionary<string, int> EmptySlides = new();

	private static readonly Dictionary<string, Godot.Vector3> EmptyPositions = new();

	private static readonly Dictionary<string, string> EmptyValues = new();

	private static readonly Dictionary<string, uint> EmptyTextures = new();
}
