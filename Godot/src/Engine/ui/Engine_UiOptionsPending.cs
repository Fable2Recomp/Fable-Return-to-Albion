using System;

namespace Refii.Ui;

/// <summary>
/// The options page's UNCOMMITTED choices, kept by the host because the host sends every
/// LEFT/RIGHT the page sees. The guest's pickbox handler `sub_828820B0` moves its own index
/// (`pb+36`: 31 → idx+1 while idx &lt; count-1, 30 → idx-1 while idx &gt; 0, no wrap for these)
/// and rewrites the caption from its Babel table (`sub_828821D0`), committing nothing until
/// code 36 (`sub_829BF698`); 37 re-populates from the stored values. The page's captions are
/// written by the guest with a DIRECT element text write at the screen's Show, which the
/// owned clone does not carry, so the host renders them from this state instead — the same
/// tables, the same arithmetic. Sliders are not tracked: their pips follow the committed
/// values until the page commits.
/// </summary>
public static class Engine_UiOptionsPending
{
	/// <summary>Which category page is open, or empty when the list level is up.</summary>
	public static string Page { get; private set; } = string.Empty;

	public static bool Open => Page.Length != 0;

	/// <summary>The page's row cursor — the guest's `page+40`, 28/29 move it, clamped.</summary>
	public static int Row { get; private set; }

	private static int[] _index = Array.Empty<int>();

	private static int[] _count = Array.Empty<int>();

	/// <summary>
	/// THE SLIDERS' PENDING VALUES. A slider row's value lives at `row+40` on the guest's page
	/// widget and moves by `1 / (row+44)` (steps, ctor 20) on codes 30/31; the export the host
	/// draws pips from reads the PAGE's committed fields (`page4+0x2C…`), which only change
	/// on Accept — so the pip stood still while editing. Tracked here from the same codes the
	/// guest receives, NaN on a row that is not a slider.
	/// </summary>
	private static float[] _slider = Array.Empty<float>();

	private const int SliderSteps = 20;

	/// <summary>A slider row's pending value, or null for a pickbox row / no page.</summary>
	public static float? SliderValue(int row) =>
		row >= 0 && row < _slider.Length && !float.IsNaN(_slider[row]) ? _slider[row] : null;

	/// <summary>The committed sliders with this page's pending values written over them.</summary>
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

	/// <summary>A row's pending index, or -1 for an untracked (slider) row.</summary>
	public static int IndexOf(int row) =>
		row >= 0 && row < _index.Length ? _index[row] : -1;

	/// <summary>
	/// Enter the page named by the list row, seeding every pickbox from the committed values the
	/// guest's populate (`sub_829BE130`) would seed them from.
	/// </summary>
	public static void Enter(
		string page, Refii.Host.HostBridge.GameOptions options,
		Refii.Host.HostBridge.OptionSliders sliders)
	{
		if (string.Equals(Page, page, StringComparison.Ordinal))
		{
			return;
		}

		Page = page;
		Row = 0;
		_slider = Array.Empty<float>();
		switch (page)
		{
			case "Game":
				// Subtitles, Breadcrumb size (5 steps: last table entry the size reaches),
				// Tutorial boxes, Online orbs (3), Auto-joinable.
				_count = new[] { 2, 5, 2, 3, 2 };
				_index = new[]
				{
					options.Subtitles ? 1 : 0,
					BreadcrumbIndex(options.Breadcrumb),
					options.TutorialBoxes ? 1 : 0,
					Math.Clamp(options.MultiplayerOrbs, 0, 2),
					options.AutoJoinable ? 1 : 0,
				};
				break;
			case "Controls":
				_count = new[] { 2 };
				_index = new[] { options.InvertCamera ? 1 : 0 };
				break;
			case "Audio":
				// Three sliders, then the speaker pickbox.
				_count = new[] { 0, 0, 0, 3 };
				_index = new[] { -1, -1, -1, Math.Clamp(sliders.Speaker, 0, 2) };
				_slider = new[] { sliders.Sfx, sliders.Music, sliders.Speech, float.NaN };
				break;
			default:
				// Video: one slider, nothing to track.
				_count = new[] { 0 };
				_index = new[] { -1 };
				_slider = new[] { sliders.Gamma };
				break;
		}
	}

	public static void Leave()
	{
		Page = string.Empty;
		Row = 0;
		_index = Array.Empty<int>();
		_count = Array.Empty<int>();
		_slider = Array.Empty<float>();
	}

	/// <summary>One screen action the host just sent to the page.</summary>
	public static void Track(uint code)
	{
		if (!Open)
		{
			return;
		}

		switch (code)
		{
			case Engine_UiScreenInput.Up:
				Row = Math.Max(0, Row - 1);
				break;
			case Engine_UiScreenInput.Down:
				Row = Math.Min(_index.Length - 1, Row + 1);
				break;
			case Engine_UiScreenInput.Left:
				if (Row < _index.Length && _index[Row] > 0)
				{
					--_index[Row];
				}
				Nudge(-1f / SliderSteps);
				break;
			case Engine_UiScreenInput.Right:
				if (Row < _index.Length && _index[Row] >= 0 && _index[Row] < _count[Row] - 1)
				{
					++_index[Row];
				}
				Nudge(1f / SliderSteps);
				break;
			case Engine_UiScreenInput.Select:
			case Engine_UiScreenInput.Back:
				// 36 commits, 37 re-populates — either way the page closes and the committed
				// values are the truth again.
				Leave();
				break;
		}
	}

	/// <summary>`sub_82881D60` events 30/31 on the cursor's slider row: `value ∓= 1/steps`, clamped [0, 1].</summary>
	private static void Nudge(float by)
	{
		if (Row < _slider.Length && !float.IsNaN(_slider[Row]))
		{
			_slider[Row] = Math.Clamp(_slider[Row] + by, 0f, 1f);
		}
	}

	/// <summary>The guest's `for i: if size >= table[i] → index = i` over {0, .5, 1, 1.5, 2}.</summary>
	public static int BreadcrumbIndex(float size)
	{
		int index = 0;
		float[] least = { 0.0f, 0.5f, 1.0f, 1.5f, 2.0f };
		for (int i = 0; i < least.Length; ++i)
		{
			if (size >= least[i])
			{
				index = i;
			}
		}

		return index;
	}
}
