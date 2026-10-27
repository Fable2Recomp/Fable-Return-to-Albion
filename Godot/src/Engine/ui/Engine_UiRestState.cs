using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// A SCREEN'S REST STATE FROM ITS FILE: the slide every component stands on once the screen has
/// opened, without the guest's presentation.
///
/// Measured against the live sample on the item, weapon, clothing, pause, abilities, world-map,
/// options and logbook books (2026-10-26), parents first:
///
/// 1. A component its nearest owning ancestor's CURRENT slide switches off does not play — it
///    stays on its master (0). The options page's unselected pickboxes name their `grp.frame`
///    OFF and the guest holds each frame on its master; the selected one names it on and the
///    frame plays. The logbook's hidden subject pages and the world map's job-page arrows are the
///    same case one level up.
/// 2. Any other component rests on its FIRST slide (the guest's numbering, 1 past the master),
///    except one whose first slide is its open animation, which rests on the slide after it —
///    `Layer.Foreground` runs `PlayIntro` then holds `On`.
/// 3. A slide the host states for a component wins, and its entries decide its children; a host
///    switch on an element is the later `setActive` and decides whether it plays.
///
/// Everything else the sample differed on was a live widget the host already states (scroll
/// arrows, a pickbox's selection, the stars).
/// </summary>
public static class Engine_UiRestState
{
	/// <summary>The open animation an owner plays once and then leaves for the slide after it.</summary>
	private static readonly HashSet<string> OpenAnimations = new(System.StringComparer.Ordinal) { "PlayIntro" };

	/// <summary>
	/// The rest slide per element (0 where the element owns no slide or rests on its master),
	/// sized to the document. <paramref name="stated"/> is the host's slide per element, 0 where
	/// it states none.
	/// </summary>
	public static int[] Slides(Engine_UiScreen screen, IReadOnlyList<int>? stated = null,
		IReadOnlyList<bool?>? switched = null)
	{
		int count = screen.Nodes.Length;
		var slides = new int[count];

		// Each owner's non-master slides, in file order — the guest's numbering.
		var owned = new Dictionary<int, List<BgfSlide>>();
		foreach (BgfSlide slide in screen.Document.Slides)
		{
			if (slide.IsMaster || slide.OwnerElement < 0 || slide.OwnerElement >= count)
			{
				continue;
			}

			if (!owned.TryGetValue(slide.OwnerElement, out List<BgfSlide>? list))
			{
				list = new List<BgfSlide>();
				owned[slide.OwnerElement] = list;
			}

			list.Add(slide);
		}

		// What each owner's chosen slide says about its own subtree's elements, decided parents
		// first (document order is parents-first), so a child reads its ancestors' answers.
		var said = new bool?[count];
		for (int i = 0; i < count; ++i)
		{
			// A host switch is the later `setActive` the guest makes over a slide's entry (the
			// logbook's fold names every subject page off and the chosen one is lit), so it
			// decides whether the element plays.
			if (switched is not null && i < switched.Count && switched[i] is { } lit)
			{
				said[i] = lit;
			}

			if (!owned.TryGetValue(i, out List<BgfSlide>? mine))
			{
				continue;
			}

			// Rule 1: an ancestor's chosen slide switched this element off. Owners are visited in
			// document order, parents first, so the nearest ancestor that named it wrote last.
			bool off = said[i] == false;

			int chosen = stated is not null && i < stated.Count && stated[i] > 0
				? stated[i]
				: off
					? 0
					: mine.Count > 1 && OpenAnimations.Contains(mine[0].Name) ? 2 : 1;
			slides[i] = chosen;
			if (chosen <= 0 || chosen > mine.Count)
			{
				continue;
			}

			foreach (BgfSlideEntry entry in mine[chosen - 1].Entries)
			{
				if (entry.ElementOrdinal > i && entry.ElementOrdinal < count)
				{
					said[entry.ElementOrdinal] = entry.Active;
				}
			}
		}

		return slides;
	}
}
