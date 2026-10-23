namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The mouse on the controller's carousel (<see cref="Engine_UiCarouselBar"/>): a wheel notch is
/// the pad's step, the scroll bar is the highlight over the count, and the bar appears only for a
/// list longer than the carousel draws. Pure arithmetic and the menu model — no Godot type is built.
/// </summary>
[TestSuite]
public class CarouselBarTests
{
	private static Engine_UiExpandableMenu ListOf(int rows)
	{
		var model = new Engine_UiExpandableMenu();
		for (int i = 0; i < rows; ++i)
		{
			model.Add($"row{i + 1}");
		}

		return model;
	}

	/// <summary>A wheel notch is a D-pad step: the model's own ScrollDown/ScrollUp, clamped, never wrapped.</summary>
	[TestCase]
	public void WheelNotchesClampAtTheEndsAndNeverWrap()
	{
		Engine_UiExpandableMenu model = ListOf(5);
		AssertThat(model.CurrentHighlightIdx).IsEqual(1);

		for (int notch = 0; notch < 7; ++notch)
		{
			model.ScrollDown();
		}

		AssertThat(model.CurrentHighlightIdx).IsEqual(5);
		AssertThat(model.ScrollDown()).IsFalse();

		for (int notch = 0; notch < 9; ++notch)
		{
			model.ScrollUp();
		}

		AssertThat(model.CurrentHighlightIdx).IsEqual(1);
		AssertThat(model.ScrollUp()).IsFalse();
	}

	/// <summary>The bar's walk is the same step: one row per call toward the target, stopping on it.</summary>
	[TestCase]
	public void TheBarWalksTheHighlightOneStepAtATime()
	{
		Engine_UiExpandableMenu model = ListOf(30);
		int steps = 0;
		while (Engine_UiCarouselBar.StepToward(model, 17))
		{
			++steps;
		}

		AssertThat(steps).IsEqual(16);
		AssertThat(model.CurrentHighlightIdx).IsEqual(17);

		AssertThat(Engine_UiCarouselBar.StepToward(model, 16)).IsTrue();
		AssertThat(model.CurrentHighlightIdx).IsEqual(16);

		// A target past the end stops at the last row, as the pad does.
		while (Engine_UiCarouselBar.StepToward(model, 99))
		{
		}

		AssertThat(model.CurrentHighlightIdx).IsEqual(30);
	}

	/// <summary>A list whose up/down input the guest disabled refuses the bar as it refuses the pad.</summary>
	[TestCase]
	public void TheBarHonoursDisabledInput()
	{
		Engine_UiExpandableMenu model = ListOf(20);
		model.SetDisabled(true);
		AssertThat(Engine_UiCarouselBar.StepToward(model, 10)).IsFalse();
		AssertThat(model.CurrentHighlightIdx).IsEqual(1);
	}

	[TestCase]
	public void TheThumbIsTheHighlightOverTheCount()
	{
		AssertThat(Engine_UiCarouselBar.Position(1, 25)).IsEqual(0f);
		AssertThat(Engine_UiCarouselBar.Position(25, 25)).IsEqual(1f);
		AssertThat(Engine_UiCarouselBar.Position(13, 25)).IsEqual(0.5f);
		AssertThat(Engine_UiCarouselBar.Position(1, 1)).IsEqual(0f);
	}

	[TestCase]
	public void ThePlaceOnTheBarNamesTheNearestRow()
	{
		AssertThat(Engine_UiCarouselBar.IndexAt(0f, 25)).IsEqual(1);
		AssertThat(Engine_UiCarouselBar.IndexAt(1f, 25)).IsEqual(25);
		AssertThat(Engine_UiCarouselBar.IndexAt(0.5f, 25)).IsEqual(13);
		AssertThat(Engine_UiCarouselBar.IndexAt(-3f, 25)).IsEqual(1);
		AssertThat(Engine_UiCarouselBar.IndexAt(7f, 25)).IsEqual(25);
		AssertThat(Engine_UiCarouselBar.IndexAt(0.5f, 0)).IsEqual(1);
	}

	/// <summary>Grabbing the thumb where it stands sends the highlight nowhere.</summary>
	[TestCase]
	public void ThumbAndRowRoundTrip()
	{
		for (int count = 1; count <= 60; ++count)
		{
			for (int highlight = 1; highlight <= count; ++highlight)
			{
				AssertThat(Engine_UiCarouselBar.IndexAt(Engine_UiCarouselBar.Position(highlight, count), count))
					.IsEqual(highlight);
			}
		}
	}

	/// <summary>
	/// The bar shows exactly when no single highlight lets the carousel draw every row: checked
	/// against the shipped window arithmetic (`GetVisibleIndices`) and the end-slot fades.
	/// </summary>
	[TestCase]
	public void TheBarShowsOnlyForAListLongerThanTheCarouselDraws()
	{
		foreach (bool top in new[] { false, true })
		{
			foreach (bool bottom in new[] { false, true })
			{
				int capacity = Engine_UiCarouselBar.Capacity(top, bottom);
				for (int total = 1; total <= 30; ++total)
				{
					AssertThat(Engine_UiCarouselBar.Shown(total, capacity))
						.IsEqual(MostDrawn(total, top, bottom) < total);
				}
			}
		}

		AssertThat(Engine_UiCarouselBar.Capacity(false, false)).IsEqual(10);
		AssertThat(Engine_UiCarouselBar.Capacity(true, true)).IsEqual(8);
	}

	/// <summary>The most rows the carousel draws visibly at any one highlight.</summary>
	private static int MostDrawn(int total, bool fadesTop, bool fadesBottom)
	{
		int most = 0;
		for (int highlight = 1; highlight <= total; ++highlight)
		{
			(int first, int last) = Engine_UiExpandableMenu.Window(highlight, total);
			int drawn = 0;
			for (int display = first; display <= last; ++display)
			{
				int slot = Engine_UiExpandableMenu.HighlightSlot + (display - highlight);
				if (slot is < 2 or > 11 || (slot == 2 && fadesTop) || (slot == 11 && fadesBottom))
				{
					continue;
				}

				++drawn;
			}

			most = System.Math.Max(most, drawn);
		}

		return most;
	}

	/// <summary>The guest's cursor is sent the steps from where the grab found it, never twice.</summary>
	[TestCase]
	public void TheGuestsCursorIsSentEachStepOnce()
	{
		int sent = 0;
		foreach (int target in new[] { 9, 14, 14, 6, 9 })
		{
			sent += Engine_UiCarouselBar.StepsToSend(start: 4, target, sent);
			AssertThat(4 + sent).IsEqual(target);
		}

		AssertThat(Engine_UiCarouselBar.StepsToSend(4, 9, 5)).IsEqual(0);
	}
}
