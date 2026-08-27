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
	public static IReadOnlyDictionary<string, int> Slides(
		Engine_UiExpandableMenu.Item? row,
		Refii.Host.HostBridge.WorldMapRegion region)
	{
		var slides = new Dictionary<string, int>(1);
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
		Refii.Host.HostBridge.WorldMapRegion region)
	{
		var values = new Dictionary<string, string>(11)
		{
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
			[SilverKeys] = string.Empty,
		};

		// The sample must be this row's: the pump answers one tick behind, so on the frame the
		// cursor moves the store still holds the previous region. Blank rather than mismatch.
		if (!region.IsFor(RegionOf(row)))
		{
			return values;
		}

		values[PageTitle] = region.Name;
		values[Distance] = region.Distance;
		values[Travel] = region.Travel;
		values[Fine] = region.Fine;
		values[Merchants] = region.Merchants;
		values[Owned] = region.Owned;
		values[Chests] = region.Chests;
		values[Digspots] = region.Digspots;
		values[Divespots] = region.Divespots;
		values[Gargoyles] = region.Gargoyles;
		values[SilverKeys] = region.SilverKeys;
		return values;
	}
}
