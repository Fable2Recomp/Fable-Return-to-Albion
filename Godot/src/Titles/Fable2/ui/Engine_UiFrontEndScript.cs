using System;
using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// `frontend.lua` translated — the front end's behaviour table: the slide walk between PressStart,
/// the title menu and the five sub-pages, which is the reference the Godot-owned front end
/// (<see cref="Engine_FrontEndMenu"/>, <see cref="Engine_UiFrontEndScreens"/>) mirrors. The
/// `SLIDE` table and `Scene.Layer.*` page paths are
/// <see cref="Engine_UiScreenConstants.FrontEndPage"/> / `FrontEndPagePath`;
/// `getCurrentSlide(SCENE)`/`goToSlide` are modeled as <see cref="CurrentSlide"/>.
///
/// SHIPPED QUIRKS:
/// <list type="bullet">
/// <item><b>Confirm on a SUB-PAGE does nothing.</b> `OnPressConfirm` acts only on `Title`
/// (descend into `TitleItem`, show `pagenav`) and `PressStart` (go to `Title`).</item>
/// <item><b>Back from ANY slide but PressStart returns to Title and hides `pagenav`</b> —
/// including from Title itself, where it re-enters Title and blanks the nav chrome the last
/// confirm showed. On PressStart it does nothing.</item>
/// <item><b>`TitleItem` starts on `NewGame` and wraps within NewGame..Debug.</b> `Initialize`
/// seeds it from `SLIDE.NewGame` (`frontend.dis main.0 pc10..12`) off the table built at
/// `main.0 pc0..8` — PressStart 1, Title 2, NewGame 3, LoadGame 4, Option 5, Multiplayer 6,
/// Debug 7, COUNT 8. `SelectTitleItem` clamps BOTH ends: `main.9 pc2` is `LT A=0 B=2 C=1`,
/// `SLIDE.Debug &lt; item → item = SLIDE.NewGame`; `main.9 pc8` is `LT A=0 B=1 C=2`,
/// `item &lt; SLIDE.NewGame → item = SLIDE.Debug`.</item>
/// <item><b>`OnPressUp`/`OnPressDown` step by one.</b> `frontend.dis main.5 pc9` is
/// `SUB R3 = R3 SUB 1` and `main.6 pc9` is `ADD R3 = R3 ADD 1`, each on `self.TitleItem`
/// loaded at pc8 and handed to `self:SelectTitleItem` (`SELF` at pc7). Both are gated at
/// pc0..6 on `getCurrentSlide(SCENE) == SLIDE.Title`.</item>
/// <item><b>All five highlight arms are present.</b> `main.9 pc17..62`: `local button = nil`
/// (pc17 `LOADNIL`), then five independent `if TitleItem == SLIDE.<i>x</i>` tests resolving
/// `Scene.Layer.newgame` (pc18..26), `loadgame` (pc27..35), `options` (pc36..44),
/// `multiplayer` (pc45..53) and `debug` (pc54..62) — the same five paths `FrontEndPagePath`
/// states. pc63..72 then reads that layer's `position.y` into `self.ButtonY` and writes it to
/// `self.IndicatorButton` UNCONDITIONALLY; the two clamps are what guarantee an arm ran.</item>
/// </list>
/// </summary>
public sealed class Engine_UiFrontEndScript
{
	/// <summary>`FrontEnd.SLIDE` — see <see cref="Engine_UiScreenConstants.FrontEndPage"/>,
	/// which is this table.</summary>
	public const int SlidePressStart = (int)Engine_UiScreenConstants.FrontEndPage.PressStart;

	public const int SlideTitle = (int)Engine_UiScreenConstants.FrontEndPage.Title;

	public const int SlideNewGame = (int)Engine_UiScreenConstants.FrontEndPage.NewGame;

	public const int SlideDebug = (int)Engine_UiScreenConstants.FrontEndPage.Debug;

	public const int SlideCount = (int)Engine_UiScreenConstants.FrontEndPage.Count;

	/// <summary>`getCurrentSlide(SCENE)` / `goToSlide(SCENE, n)`, as state. The scene starts on
	/// PressStart.</summary>
	public int CurrentSlide { get; private set; } = SlidePressStart;

	/// <summary>`self.TitleItem` — which title row is highlighted. Starts on NewGame.</summary>
	public int TitleItem { get; private set; } = SlideNewGame;

	/// <summary>`self.Time`, `self.ButtonY`, `self.StarVel` — initialized and, in the extracted
	/// set, never advanced. UNREACHABLE writers; carried because `Initialize` states them.</summary>
	public double Time;

	public double ButtonY;

	public double StarVel;

	/// <summary>`Scene.Layer.pagenav`'s opacity — 100 after a confirm off the title, 0 after a
	/// back.</summary>
	public double NavUiOpacity { get; private set; }

	/// <summary>`self.IndicatorButton`'s `position.y` — parked on the highlighted row.</summary>
	public double IndicatorButtonY { get; private set; }

	/// <summary>`getAttribute(layer, "position.y")` for a title item's page layer — the caller
	/// resolves the authored art. Paths per `Engine_UiScreenConstants.FrontEndPagePath`.</summary>
	public Func<Engine_UiScreenConstants.FrontEndPage, double>? LayerY { get; set; }

	/// <summary>`FrontEnd.HandleEnter` — SHIPPED EMPTY.</summary>
	public void HandleEnter()
	{
	}

	/// <summary>`FrontEnd.HandleExit` — SHIPPED EMPTY.</summary>
	public void HandleExit()
	{
	}

	/// <summary>`FrontEnd.HandleA` — the manager fan-out target; forwards to confirm.</summary>
	public void HandleA() => OnPressConfirm();

	/// <summary>`FrontEnd.HandleB`.</summary>
	public void HandleB() => OnPressBack();

	/// <summary>`FrontEnd.OnPressUp` — only live on the Title slide; −1, `frontend.dis main.5
	/// pc0..10` (`SUB` at pc9).</summary>
	public void OnPressUp()
	{
		if (CurrentSlide == SlideTitle)
		{
			SelectTitleItem(TitleItem - 1);
		}
	}

	/// <summary>`FrontEnd.OnPressDown` — +1, `frontend.dis main.6 pc0..10` (`ADD` at
	/// pc9).</summary>
	public void OnPressDown()
	{
		if (CurrentSlide == SlideTitle)
		{
			SelectTitleItem(TitleItem + 1);
		}
	}

	/// <summary>`FrontEnd.OnPressConfirm` — Title descends into the highlighted item and shows
	/// the nav chrome; PressStart advances to Title; anything else is ignored.</summary>
	public void OnPressConfirm()
	{
		if (CurrentSlide == SlideTitle)
		{
			GoToSlide(TitleItem);
			NavUiOpacity = 100;
		}
		else if (CurrentSlide == SlidePressStart)
		{
			GoToSlide(SlideTitle);
		}
	}

	/// <summary>`FrontEnd.OnPressBack` — nothing on PressStart; otherwise back to Title with
	/// the nav chrome hidden. Note this fires FROM Title too — see the class note.</summary>
	public void OnPressBack()
	{
		if (CurrentSlide == SlidePressStart)
		{
			// `if slide == PressStart then` — the empty arm, kept. There is nothing behind
			// the press-start screen to back out to.
		}
		else
		{
			GoToSlide(SlideTitle);
			NavUiOpacity = 0;
		}
	}

	/// <summary>
	/// `FrontEnd.SelectTitleItem` — wrap into NewGame..Debug, early-out on no change, then park
	/// the indicator button on the chosen row's layer.
	/// </summary>
	public void SelectTitleItem(int item)
	{
		// `frontend.dis main.9 pc0..5` — `LT A=0 B=2 C=1` against `SLIDE.Debug`: past the last
		// row wraps to the first.
		if (SlideDebug < item)
		{
			item = SlideNewGame;
		}

		// `main.9 pc6..11` — `LT A=0 B=1 C=2` against `SLIDE.NewGame`: below the first row
		// wraps to the last.
		if (item < SlideNewGame)
		{
			item = SlideDebug;
		}

		if (TitleItem == item)
		{
			return;
		}

		TitleItem = item;

		// The five per-item arms, `main.9 pc17..62` — one `getElement` per page layer, then
		// pc63..72 parks the indicator on that layer's `position.y`.
		if (LayerY is not null)
		{
			ButtonY = LayerY((Engine_UiScreenConstants.FrontEndPage)TitleItem);
			IndicatorButtonY = ButtonY;
		}
	}

	/// <summary>`FrontEnd.GoToSlide` → global `ComponentSetSlide` → `goToSlide(SCENE, n)`.</summary>
	public void GoToSlide(int slide)
	{
		CurrentSlide = slide;
	}
}
