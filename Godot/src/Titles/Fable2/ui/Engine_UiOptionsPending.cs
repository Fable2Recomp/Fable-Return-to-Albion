using System;
using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The open options page's row cursor and row values, for the host's captions, pips and pointer.
///
/// A GUEST page (Game, Controls, Video, Audio) owns all of it. Its handler moves the cursor
/// (`page+40`) on 28/29 and hands 30/31 to the cursor's widget, whose pickbox index (`widget+36`,
/// `sub_828820B0`: 31 → idx+1 while idx &lt; count-1, 30 → idx-1 while idx &gt; 0) or slider value
/// (`widget+40`) moves at once and the caption is rewritten from its Babel table
/// (`sub_828821D0`); 36 commits the page (`sub_829BF698` and its siblings), 37 re-populates.
/// Nothing here steps on a code sent to a guest page: <see cref="Follow"/> reads the guest's own
/// state back every frame (`RefiiHostGetOptionsPage`), so the captions painted from
/// <see cref="IndexOf"/> never disagree with the highlight the guest draws, and A commits exactly
/// what is shown. The guest's captions are a DIRECT element text write that the owned clone does
/// not carry, which is why the host paints them at all — from the same tables, at the guest's
/// index. The page closes when the guest's slide does, never on a code the host sent.
///
/// A HOST page (<see cref="Engine_UiHostOptionsPage"/>) has no guest behind it, so the record
/// kept here IS its state: <see cref="Track"/> steps the cursor and the indices with the
/// pickbox's own arithmetic, and 36/37 close it.
/// </summary>
public static class Engine_UiOptionsPending
{
	/// <summary>Which category page is open, or empty when the list level is up.</summary>
	public static string Page { get; private set; } = string.Empty;

	public static bool Open => Page.Length != 0;

	/// <summary>The page's row cursor — the guest's `page+40` on a guest page, the host's on a host page.</summary>
	public static int Row { get; private set; }

	/// <summary>Each row's pickbox index; -1 on a slider row or one the guest has not stated yet.</summary>
	private static int[] _index = Array.Empty<int>();

	/// <summary>Each row's choice count — a HOST page's, the only one the host steps.</summary>
	private static int[] _count = Array.Empty<int>();

	/// <summary>
	/// Each row's LIVE slider value, the widget's own `widget+40` read back from the guest; NaN on
	/// a row that is not a slider. Page 4 copies these into the fields `RefiiHostGetOptionsSliders`
	/// reads on every step (`sub_829C1488(page, 0)`), page 3 only on commit (`sub_829C07D0`) —
	/// so the pip drawn while editing comes from here, not from the page fields.
	/// </summary>
	private static float[] _slider = Array.Empty<float>();

	/// <summary>
	/// The guest's pages, by the name the list row carries: the page's index in the screen's page
	/// vector and, per row in the guest's order, whether the row is a pickbox (else a slider).
	/// </summary>
	private static readonly Dictionary<string, (int Index, bool[] Pickbox)> GuestPages = new(StringComparer.Ordinal)
	{
		// Subtitles, Breadcrumb size, Tutorial boxes, Online orbs, Auto-joinable.
		["Game"] = (0, new[] { true, true, true, true, true }),
		["Controls"] = (1, new[] { true }),
		["Video"] = (2, new[] { false }),
		// Three sliders, then the speaker pickbox.
		["Audio"] = (3, new[] { false, false, false, true }),
	};

	/// <summary>The open guest page's index in the screen's page vector, or -1 for a host page or none.</summary>
	public static int GuestPageIndex =>
		Engine_UiHostOptionsPage.Current is null && GuestPages.TryGetValue(Page, out var page)
			? page.Index
			: -1;

	/// <summary>A slider row's live value, or null for a pickbox row / no page.</summary>
	public static float? SliderValue(int row) =>
		row >= 0 && row < _slider.Length && !float.IsNaN(_slider[row]) ? _slider[row] : null;

	/// <summary>The committed sliders with the open page's live widget values written over them.</summary>
	public static Refii.Host.HostBridge.OptionSliders Pending(Refii.Host.HostBridge.OptionSliders committed)
	{
		if (!Open || !committed.Known)
		{
			return committed;
		}

		return Page switch
		{
			"Video" => committed with { Gamma = SliderValue(0) ?? committed.Gamma },
			"Audio" => committed with
			{
				Sfx = SliderValue(0) ?? committed.Sfx,
				Music = SliderValue(1) ?? committed.Music,
				Speech = SliderValue(2) ?? committed.Speech,
			},
			_ => committed,
		};
	}

	/// <summary>A row's pickbox index, or -1 for a slider row or one not yet stated.</summary>
	public static int IndexOf(int row) =>
		row >= 0 && row < _index.Length ? _index[row] : -1;

	/// <summary>
	/// Enter the guest page named by the list row. Nothing is seeded: the rows are the guest's
	/// and arrive with the first <see cref="Follow"/>. An empty name is no page and is refused —
	/// the list's highlight can be null for a frame, and that must not close or reopen the page.
	/// </summary>
	public static void Enter(string page)
	{
		if (page.Length == 0 || string.Equals(Page, page, StringComparison.Ordinal))
		{
			return;
		}

		Page = page;
		Row = 0;
		_count = Array.Empty<int>();
		if (GuestPages.TryGetValue(page, out var layout))
		{
			_index = new int[layout.Pickbox.Length];
			_slider = new float[layout.Pickbox.Length];
			Array.Fill(_index, -1);
			Array.Fill(_slider, float.NaN);
		}
		else
		{
			_index = Array.Empty<int>();
			_slider = Array.Empty<float>();
		}
	}

	/// <summary>
	/// The guest's own state for the open guest page, read this frame: its cursor becomes
	/// <see cref="Row"/>, each pickbox row's `widget+36` its <see cref="IndexOf"/>, each slider
	/// row's `widget+40` its <see cref="SliderValue"/>. An unknown reading (screen not live yet)
	/// keeps the last. Nothing on a host page.
	/// </summary>
	public static void Follow(Fable2Bridge.GuestOptionsPage guest)
	{
		if (!guest.Known || Engine_UiHostOptionsPage.Current is not null ||
			!GuestPages.TryGetValue(Page, out var layout))
		{
			return;
		}

		bool[] pickbox = layout.Pickbox;
		Row = Math.Clamp(guest.Cursor, 0, pickbox.Length - 1);
		for (int row = 0; row < pickbox.Length; ++row)
		{
			bool stated = guest.Indices is not null && row < guest.Indices.Length;
			_index[row] = pickbox[row] && stated ? guest.Indices![row] : -1;
			_slider[row] = !pickbox[row] && stated && guest.Values is not null && row < guest.Values.Length
				? guest.Values[row]
				: float.NaN;
		}
	}

	public static void Leave()
	{
		Page = string.Empty;
		Row = 0;
		_index = Array.Empty<int>();
		_count = Array.Empty<int>();
		_slider = Array.Empty<float>();
		Engine_UiHostOptionsPage.Current = null;
	}

	/// <summary>
	/// Enter a HOST page — one with no guest page behind it (<see cref="Engine_UiHostOptionsPage"/>).
	/// Seeded from the page's committed indices; the codes that follow are tracked here and never
	/// sent to the guest.
	/// </summary>
	public static void EnterHost(Engine_UiHostOptionsPage page)
	{
		if (Engine_UiHostOptionsPage.Current == page) return;
		Page = page.Name;
		Row = 0;
		_index = page.Read();
		_count = new int[_index.Length];
		for (int i = 0; i < _count.Length; i++) _count[i] = page.Choices(i);
		_slider = Array.Empty<float>();
		Engine_UiHostOptionsPage.Current = page;
	}

	/// <summary>Sets a HOST page row's pending index (a rebind row's captured press, a reset); nothing on a guest page.</summary>
	public static void SetIndex(int row, int index)
	{
		if (Engine_UiHostOptionsPage.Current is not { } host || row < 0 || row >= _index.Length || index < 0)
		{
			return;
		}

		_index[row] = Math.Min(index, Math.Max(0, host.Choices(row) - 1));
	}

	/// <summary>
	/// One screen action the host just sent to a HOST page. A guest page's codes move the
	/// guest's own record, which <see cref="Follow"/> reads back — nothing is stepped here for one.
	/// </summary>
	public static void Track(uint code)
	{
		if (Engine_UiHostOptionsPage.Current is not { } host)
		{
			return;
		}

		if (code == Engine_UiScreenInput.Select && !host.Commit(IndexOf))
		{
			return;
		}

		switch (code)
		{
			case Engine_UiScreenInput.Up:
				Row = Math.Max(0, Row - 1);
				break;
			case Engine_UiScreenInput.Down:
				Row = Math.Min(host.Count - 1, Row + 1);
				break;
			case Engine_UiScreenInput.Left:
				if (Row < _index.Length && _index[Row] > 0)
				{
					--_index[Row];
					host.Changed(Row, _index[Row]);
				}
				break;
			case Engine_UiScreenInput.Right:
				if (Row < _index.Length && _index[Row] >= 0 && _index[Row] < _count[Row] - 1)
				{
					++_index[Row];
					host.Changed(Row, _index[Row]);
				}
				break;
			case Engine_UiScreenInput.Select:
			case Engine_UiScreenInput.Back:
				// 36 commits, 37 discards — either way the page closes.
				Leave();
				break;
		}
	}
}
