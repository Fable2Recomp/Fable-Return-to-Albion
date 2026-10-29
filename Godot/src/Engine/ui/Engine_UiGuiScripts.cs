using System;
using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// `guisetup.lua` translated — the load manifest every presentation runs first: `dofile` calls off
/// `g_GUIScriptDirectory`, most gated on a contract global (`g_HUD`, `g_Menu`, `g_FrontEnd`, …)
/// the C++ defines only when the presentation carries that contract, so the manifest is also the
/// census of contract classes. The order is the initialization order: state and constants, the
/// libraries, the per-contract scripts, then the frame/page widgets unconditionally last.
///
/// SHIPPED QUIRKS:
/// <list type="bullet">
/// <item><b>`QBText.lua` loads only inside Anark Studio</b> (`GUI.RunningInAnarkStudio`), which
/// is why the in-game `GUI.GetText` is the `"..."` stub in <see cref="Engine_UiContractLib"/> —
/// the game never sources text from `book.xml`.</item>
/// <item><b>The ExpandableMenu block is double-gated</b>: `g_Menu` must exist AND its element's
/// `_AKContractClass` attribute must equal `"ExpandableMenuContract"`.</item>
/// </list>
/// </summary>
public static class Engine_UiGuiSetup
{
	/// <summary>
	/// The manifest, in `dofile` order. Guard `""` is unconditional; otherwise the global (or
	/// the attribute test) that must hold for the group to load.
	/// </summary>
	public static readonly (string Guard, string[] Scripts)[] LoadOrder =
	{
		("", new[] { "GUIStateAndConstants.lua", "UIContractLib.lua", "UIContractEventsLib.lua",
			"GUIInput.lua", "GUIEventQueue.lua" }),
		("g_HUD", new[] { @"HUD\HUD.lua", @"HUD\HUDPositioningAndScaling.lua",
			@"HUD\HUDQuarterbackEventButtons.lua" }),
		("g_Minimap", new[] { @"HUD\Minimap.lua" }),
		("GUI.RunningInAnarkStudio", new[] { "QBText.lua" }),
		("g_Menu and _AKContractClass == \"ExpandableMenuContract\"", new[]
		{
			@"ExpandableMenu\ExpandableMenuInput.lua",
			@"ExpandableMenu\ExpandableMenuStateAndConstants.lua",
			@"ExpandableMenu\ExpandableMenu.lua",
			@"ExpandableMenu\ExpandableMenuDebugging.lua",
			@"ExpandableMenu\ExpandableMenuEmphasisAndGhosting.lua",
			@"ExpandableMenu\ExpandableMenuAnimation.lua",
			@"ExpandableMenu\ExpandableMenuFormatting.lua",
			@"ExpandableMenu\ExpandableMenuPopulationScript.lua",
			@"ExpandableMenu\ExpandableMenuSpawning.lua",
		}),
		("g_QBEventTranslator", new[] { "QBEventTranslator.lua" }),
		("g_ItemInfo", new[] { "ItemInfo.lua" }),
		("g_FrontEnd", new[] { "FrontEnd.lua" }),
		("g_InfoWindow", new[] { "InfoWindow.lua" }),
		("g_QuestsManager", new[] { @"Managers\QuestsManager.lua" }),
		("g_CrimeScreenManager", new[] { @"Managers\CrimeScreenManager.lua" }),
		("g_AbilitiesManager", new[] { @"Managers\AbilitiesManager.lua" }),
		("g_ShopScreenManager", new[] { @"Managers\ShopScreenManager.lua" }),
		("g_MessageBox", new[] { @"DisplayBoxes\MessageBox.lua" }),
		("g_DisplayBox", new[] { @"DisplayBoxes\DisplayBox.lua" }),
		("g_QueryBox", new[] { @"DisplayBoxes\QueryBox.lua" }),
		("g_Theme", new[] { @"Theme\ThemeFrames.lua", @"Theme\ThemeText.lua" }),
		("g_ExampleData", new[] { "ExampleData.lua" }),
		("", new[] { @"Frame\Frame.lua", @"Frame\FrameCloning.lua",
			@"Frame\FrameSizeAndPositioning.lua", @"Frame\FrameStateAndConstants.lua",
			"Page.lua" }),
	};
}

/// <summary>
/// `guistateandconstants.lua`'s MUTABLE half — the `GUI.State`, `GUI.Timing` and `GUI.Input`
/// blocks every other GUI script reads and writes. The chunk's constants live in
/// <see cref="Engine_UiGuiConstants"/>; the two timing values `GUIInput.lua` rewrites at runtime
/// (`NavigateInterval`, `SUBSEQUENT_NAVIGATE_INTERVAL`) are here.
///
/// SHIPPED QUIRKS:
/// <list type="bullet">
/// <item><b>The health/mana numbers are Anark Studio placeholders</b> — `MaxHealth = 200,
/// CurrHealth = 120, MaxMana = 250, CurrMana = 150` — the running game overwrites them through
/// the value channels; see the HUD contract notes.</item>
/// <item><b>`TimeSinceNavigate` and `TimeSinceAdjust` START at 10</b>, the
/// `INPUT_INTERVAL_CLAMP` ceiling, so the very first press of a screen is never swallowed.
/// `Timing.TimeSinceLastNavigate` starts at `FIRST_NAVIGATE_INTERVAL` for the same reason.</item>
/// <item><b>`PageTitleColour = Color:new(255, 255, 128, 255)`</b> — four literal `LOADK`s,
/// `guistateandconstants.dis main pc107..114`.</item>
/// </list>
/// </summary>
public sealed class Engine_UiGuiState
{
	/// <summary>`GUI.Timing.NavigateInterval` — starts at the FIRST interval; `DoNavigate` drops
	/// it to the subsequent one after the first repeat.</summary>
	public double NavigateInterval = Engine_UiGuiConstants.FirstNavigateInterval;

	/// <summary>`GUI.Timing.TimeSinceLastNavigate` — starts AT the interval, so the first press
	/// passes `CanNavigate` immediately.</summary>
	public double TimeSinceLastNavigate = Engine_UiGuiConstants.FirstNavigateInterval;

	/// <summary>`GUI.Timing.SUBSEQUENT_NAVIGATE_INTERVAL` — a MUTABLE copy of the 0.14 constant,
	/// because `GUI:CycleNavigationSpeed()` rewrites it at runtime.</summary>
	public double SubsequentNavigateInterval = Engine_UiGuiConstants.SubsequentNavigateInterval;

	/// <summary>`GUI.Input.Pushing*Held` — the four direction latches C++ sets by name.</summary>
	public bool PushingUpHeld;

	public bool PushingDownHeld;

	public bool PushingLeftHeld;

	public bool PushingRightHeld;

	/// <summary>`GUI.Input.NavigationCount` — repeats taken during the current hold.</summary>
	public int NavigationCount;

	/// <summary>
	/// The field `GUI:CycleNavigationSpeed()`'s first arm writes —
	/// `GUI.Input.SUBSEQUENT_NAVIGATE_INTERVAL`, the INPUT table, not the Timing table the other
	/// two arms and every reader use. Shipped bug, kept: cycling away from 0.2 changes nothing.
	/// </summary>
	public double StraySubsequentNavigateInterval;

	/// <summary>`GUI.Const.PageTitleColour` — `Color:new(255, 255, 128, 255)`, four literal
	/// `LOADK`s at `guistateandconstants.dis main pc107..114`.</summary>
	public const int PageTitleColourRed = 255;

	public const int PageTitleColourGreen = 255;

	public const int PageTitleColourBlue = 128;

	public const int PageTitleColourAlpha = 255;

	/// <summary>`GUI.State` — the minimap feed.</summary>
	public double PlayerPosX;

	public double PlayerPosY;

	public double ViewOrientation;

	public bool DoesRotateMap;

	/// <summary>`GUI.State` — the Anark placeholder vitals (see the class note).</summary>
	public double MaxHealth = 200;

	public double BaseHealth = 100;

	public double CurrHealth = 120;

	public double MaxMana = 250;

	public double BaseMana = 100;

	public double CurrMana = 150;

	/// <summary>`GUI.State` — the frame clock `UIContractLib`'s `onUpdate` maintains.</summary>
	public double TimeElapsed;

	public double LastUpdateTime;

	public double TimeSinceNavigate = Engine_UiGuiConstants.InputIntervalClamp;

	public double TimeSinceAdjust = Engine_UiGuiConstants.InputIntervalClamp;

	public int FirstFewUpdates;

	public bool IsWidescreen;

	/// <summary>`GUI.State.LeftThumbStick*` — raw, as C++ stores them via
	/// <see cref="Engine_UiGuiInputScript.StoreLeftThumbstickValues"/>.</summary>
	public float LeftThumbStickX;

	public float LeftThumbStickY;

	public float RightThumbStickX;

	public float RightThumbStickY;

	/// <summary>`GUI.State.NormStick*` — see the normalization quirk on
	/// <see cref="Engine_UiContractLib.OnUpdate"/>.</summary>
	public float NormStickX;

	public float NormStickY;

	public bool HaveAppliedLocalisedText;

	/// <summary>`GUI.State.TilReadyToUpdate` — set to 0 by `GUI.Initialize` and read by nothing
	/// in the extracted set. UNREACHABLE consumer; carried because the chunk states it.</summary>
	public double TilReadyToUpdate;

	/// <summary>`GUI.State.FileContents` — the localisation payload `ApplyLocalisedText` takes
	/// off `Parameters.String1` in-game.</summary>
	public string? FileContents;

	/// <summary>`GUI.FLAG_WheelGraphIsAtRoot` — sits on GUI itself, not on State.</summary>
	public bool FlagWheelGraphIsAtRoot;

	/// <summary>
	/// `GUI.State.EventQueue` — the four parallel arrays, held as the one record list
	/// <see cref="Engine_UiGuiEventQueue"/> already translates. `guistateandconstants.dis
	/// main pc191..217`: `EventQueue = {}` (pc191..192), then a `NEWTABLE`+`SETTABLE` per array
	/// — `Type` (pc196..197), `Name` (pc201..202), `Param1Low` (pc206..207), `Param1High`
	/// (pc211..212) — an empty queue. pc216..217 re-runs `Type = {}` a second time, a shipped
	/// redundancy with no effect. `EventQueueSize` is not seeded here; the four pushes and the
	/// pull are its only writers.
	/// </summary>
	public readonly Engine_UiGuiEventQueue EventQueue = new();
}

/// <summary>
/// `uicontractlib.lua` translated — the base `GUI` behaviour table every presentation gets: the
/// frame clock, the stick normalization, and localised-text application. The other two contract
/// scripts (event translator, named entry points) are <see cref="Engine_UiContract"/>.
///
/// SHIPPED QUIRKS:
/// <list type="bullet">
/// <item><b>The stick normalization is SYMMETRIC.</b> `uicontractlib.dis main.1 pc73..104`
/// calls `math.sqrt` ONCE into R2 (`pc90`) and divides both axes by that same R2 (`pc96`,
/// `pc103`); the guard at `pc63..72` skips it only when both axes are zero.</item>
/// <item><b>The "time since" accumulators only advance while UNDER the clamp</b> — `if t &lt;
/// INPUT_INTERVAL_CLAMP then t = t + elapsed` — so they can end one frame ABOVE 10 and then
/// freeze, rather than saturating at exactly 10.</item>
/// <item><b>`GUI.GetText` returns `"..."`.</b> The real lookup is
/// <see cref="Engine_UiQbText"/>, which only exists inside Anark Studio (see
/// <see cref="Engine_UiGuiSetup"/>); in-game, authored `textstring` values stand and `TextID`
/// resolution is the C++ side's (Babel).</item>
/// </list>
/// </summary>
public sealed class Engine_UiContractLib
{
	public required Engine_UiGuiState State { get; init; }

	public required Engine_UiGuiInputScript Input { get; init; }

	/// <summary>
	/// `GUI.Initialize` — resets `State.TilReadyToUpdate`, fetches `Manager` (modeled by the
	/// caller wiring <see cref="Engine_UiGuiInputScript"/>'s Handle* delegates), and applies
	/// localised text inside Anark Studio only (dead in-game). `LastUpdateTime` starts at the
	/// clock, not zero — `guistateandconstants.dis main pc151..155` seeds it with
	/// `getElapsedTime()`, so the first `OnUpdate` reports one frame's delta.
	/// </summary>
	public void Initialize(double elapsedTime)
	{
		State.TilReadyToUpdate = 0;
		State.LastUpdateTime = elapsedTime;
	}

	/// <summary>
	/// `GUI.onUpdate` — the per-frame clock. `getElapsedTime()` is modeled as the caller's
	/// monotonic seconds.
	/// </summary>
	public void OnUpdate(double elapsedTime)
	{
		State.TimeElapsed = elapsedTime - State.LastUpdateTime;
		State.LastUpdateTime = elapsedTime;

		Input.UpdateInput();

		if (State.TimeSinceNavigate < Engine_UiGuiConstants.InputIntervalClamp)
		{
			State.TimeSinceNavigate += State.TimeElapsed;
		}

		if (State.TimeSinceAdjust < Engine_UiGuiConstants.InputIntervalClamp)
		{
			State.TimeSinceAdjust += State.TimeElapsed;
		}

		State.NormStickX = State.LeftThumbStickX;
		State.NormStickY = State.LeftThumbStickY;
		if (State.NormStickX != 0f || State.NormStickY != 0f)
		{
			// ONE magnitude, both axes — `uicontractlib.dis main.1 pc90` is the only `sqrt`.
			float magnitude = MathF.Sqrt(
				(State.NormStickX * State.NormStickX) + (State.NormStickY * State.NormStickY));
			State.NormStickX /= magnitude;
			State.NormStickY /= magnitude;
		}
	}

	/// <summary>
	/// `GUI.ApplyLocalisedText` — every `text` element under `Scene` that carries a non-nil
	/// `TextID` gets its `textstring` replaced by `g_QBText:GetText(TextID)`.
	///
	/// In-game the chunk first takes `State.FileContents` off `Parameters.String1`; inside the
	/// studio it does not (`if GUI.RunningInAnarkStudio then else FileContents = ... end`). The
	/// resolver is <see cref="Engine_UiQbText.GetText"/> in the studio and the `"..."` stub
	/// in-game, so the game never runs this.
	/// </summary>
	public void ApplyLocalisedText(
		IEnumerable<(string? TextId, Action<string> SetTextString)> textElements,
		Func<string, string?> getText,
		string? parametersString1,
		bool runningInAnarkStudio)
	{
		if (!runningInAnarkStudio)
		{
			State.FileContents = parametersString1;
		}

		foreach ((string? textId, Action<string> setter) in textElements)
		{
			if (textId is not null && getText(textId) is { } text)
			{
				setter(text);
			}
		}

		State.HaveAppliedLocalisedText = true;
	}

	/// <summary>`GUI.GetText` — the shipped stub. `return "..."`, verbatim.</summary>
	public string GetText(string tag) => "...";

	/// <summary>`GUI.SetToDisplayInWidescreen`. NOTE the standing rule: the guest stays 4:3 and
	/// its layout is never consumed as screen positions — this flag is state, not a licence.</summary>
	public void SetToDisplayInWidescreen()
	{
		State.IsWidescreen = true;
	}
}

/// <summary>
/// `uicontracteventslib.lua` translated — the bodies of the named entry points C++ fires by name
/// (<see cref="Engine_UiContract.NamedEvents"/> catalogues the six names and their argument
/// conventions). Each handler reads its arguments off `g_Param` (`String1`/`String2`), modeled as
/// method parameters; the touched attributes are modeled as state.
///
/// SHIPPED QUIRKS:
/// <list type="bullet">
/// <item><b>`Quests_RemoveAllCurrent` and `Quests_AddCurrent` are EMPTY.</b> The names exist
/// for C++ to fire and shipped as no-ops — the quest list is rebuilt through
/// `QuestsSingleQuestSetup` instead.</item>
/// <item><b>The guards SPEAK, they do not throw, and there are TWO of them</b> —
/// `uicontracteventslib.dis main.2 pc0..6` is `if nil == Manager then output('No Manager
/// found'); return end` and `pc7..15` is the separate `if nil == Manager.State.Book then
/// output('No Manager.State.Book reference set up yet'); return end`. `HUD_ToggleSafeFrame`
/// asserts on a missing `g_HUD` ("No g_HUD found").</item>
/// </list>
/// </summary>
public sealed class Engine_UiContractEvents
{
	/// <summary>`g_ItemInfo.State.Money`'s `opacity` — 100 shown, 0 hidden.</summary>
	public double MoneyOpacity { get; private set; }

	/// <summary>`Text` under `Group` under the money element — the amount string.</summary>
	public string MoneyText { get; private set; } = string.Empty;

	/// <summary>`g_HUD.State.SafeFrame`'s `active` — a development overlay, not a game
	/// surface.</summary>
	public bool SafeFrameActive { get; private set; }

	/// <summary>`Manager` — the global `QuestsSingleQuestSetup` tests FIRST
	/// (`uicontracteventslib.dis main.2 pc0..6`). False models the global being unset, which is
	/// a different failure from the book being unset and carries its own message.</summary>
	public bool HasManager { get; set; } = true;

	/// <summary>`Manager.State.Book` — the three calls `QuestsSingleQuestSetup` makes, in
	/// order. Null models the book reference not being set up yet.</summary>
	public Action? BookRemoveAllPages { get; set; }

	public Action<string, string>? BookAddPage { get; set; }

	public Action<int>? BookSetCurrentPage { get; set; }

	/// <summary>`output(...)` / `assert(..., msg)` — the guard messages.</summary>
	public Action<string>? Output { get; set; }

	/// <summary>`ItemInfoShowMoney` — opacity 100 and the amount from `g_Param.String1`.</summary>
	public void ItemInfoShowMoney(string string1)
	{
		MoneyOpacity = 100;
		MoneyText = string1;
	}

	/// <summary>`ItemInfoHideMoney` — opacity 0. The text is left standing.</summary>
	public void ItemInfoHideMoney()
	{
		MoneyOpacity = 0;
	}

	/// <summary>
	/// `QuestsSingleQuestSetup` — a full rebuild: remove all pages, add ONE page titled
	/// `String1` with body `String2`, show page 1.
	/// </summary>
	public void QuestsSingleQuestSetup(string string1, string string2)
	{
		// TWO guards, two messages — `uicontracteventslib.dis main.2 pc0..6` and `pc7..15`.
		if (!HasManager)
		{
			Output?.Invoke("No Manager found");
			return;
		}

		if (BookRemoveAllPages is null || BookAddPage is null || BookSetCurrentPage is null)
		{
			Output?.Invoke("No Manager.State.Book reference set up yet");
			return;
		}

		BookRemoveAllPages();
		BookAddPage(string1, string2);
		BookSetCurrentPage(1);
	}

	/// <summary>`Quests_RemoveAllCurrent` — SHIPPED EMPTY. Kept as the no-op it is.</summary>
	public void Quests_RemoveAllCurrent()
	{
	}

	/// <summary>`Quests_AddCurrent` — SHIPPED EMPTY. Kept as the no-op it is.</summary>
	public void Quests_AddCurrent()
	{
	}

	/// <summary>`HUD_ToggleSafeFrame` — flips `SafeFrame.active`.</summary>
	public void HUD_ToggleSafeFrame()
	{
		SafeFrameActive = !SafeFrameActive;
	}
}

/// <summary>
/// `guiinput.lua` translated — the input half of the base GUI table: the thumbstick store, the
/// direction latches C++ fires, the `IsPushing*` tests the menu polls, the manager fan-out and the
/// debug speed cycler, over the one shared <see cref="Engine_UiGuiState"/>.
/// `CanNavigate`/`DoNavigate` and `UpdateInput`'s release-reset are the running implementation as
/// <see cref="Engine_UiNavigationClock"/>; thresholds live in <see cref="Engine_UiGuiConstants"/>.
///
/// SHIPPED QUIRKS:
/// <list type="bullet">
/// <item><b>`CycleNavigationSpeed`'s first arm writes the WRONG TABLE</b> —
/// `GUI.Input.SUBSEQUENT_NAVIGATE_INTERVAL = 0.15` instead of `GUI.Timing.…` — so cycling away
/// from 0.2 does nothing visible. And its cycle set is {0.2, 0.15, 0.1}, which does not include
/// the shipped default 0.14: the first press always falls to the else-arm and sets 0.2.</item>
/// <item><b>`ResetInputState` clears the four held flags and touches nothing else.</b>
/// `guiinput.dis main.5 pc0..11` is four `GETGLOBAL 'GUI'` / `GETTABLE 'Input'` /
/// `SETTABLE <i>flag</i> = False` triples — `PushingUpHeld`, `PushingDownHeld`,
/// `PushingLeftHeld`, `PushingRightHeld`. No `NEWTABLE`: the `Input` table is never replaced,
/// so the thresholds and `NavigationCount` survive it.</item>
/// <item><b>`IsPushingUp` is positive Y</b>; down/left negate. The release reset in
/// `UpdateInput` requires BOTH axes under `THUMBSTICK_RELEASE_THRESHOLD` AND all four latches
/// off before the repeat re-arms — hysteresis against a stick resting near the edge.</item>
/// </list>
/// </summary>
public sealed class Engine_UiGuiInputScript
{
	public required Engine_UiGuiState State { get; init; }

	/// <summary>`fireEvent(name, GUI.element)` — `OnEnter`/`OnExit` go out through this.</summary>
	public Action<string>? FireEvent { get; set; }

	/// <summary>`GUI.Manager.HandleA/B/X/Y` — null models a manager without that handler
	/// (`if GUI.Manager.HandleA then`).</summary>
	public Action? HandleA { get; set; }

	public Action? HandleB { get; set; }

	public Action? HandleX { get; set; }

	public Action? HandleY { get; set; }

	/// <summary>`GUI.StoreLeftThumbstickValues` — `g_Param.Float1`/`Float2`, raw.</summary>
	public void StoreLeftThumbstickValues(float float1, float float2)
	{
		State.LeftThumbStickX = float1;
		State.LeftThumbStickY = float2;
	}

	/// <summary>`GUI.StoreRightThumbstickValues`.</summary>
	public void StoreRightThumbstickValues(float float1, float float2)
	{
		State.RightThumbStickX = float1;
		State.RightThumbStickY = float2;
	}

	/// <summary>`GUI.SetDirectionToHeld` — `g_Param.String1` is `"Up"`/`"Down"`/`"Left"`/
	/// `"Right"`, anything else is ignored.</summary>
	public void SetDirectionToHeld(string string1)
	{
		switch (string1)
		{
			case "Up": State.PushingUpHeld = true; break;
			case "Down": State.PushingDownHeld = true; break;
			case "Left": State.PushingLeftHeld = true; break;
			case "Right": State.PushingRightHeld = true; break;
		}
	}

	/// <summary>`GUI.SetDirectionToReleased`.</summary>
	public void SetDirectionToReleased(string string1)
	{
		switch (string1)
		{
			case "Up": State.PushingUpHeld = false; break;
			case "Down": State.PushingDownHeld = false; break;
			case "Left": State.PushingLeftHeld = false; break;
			case "Right": State.PushingRightHeld = false; break;
		}
	}

	/// <summary>
	/// `GUI.CycleNavigationSpeed` — a debug cycler over {0.2, 0.15, 0.1}. THE FIRST ARM'S WRITE
	/// LANDS ON THE WRONG TABLE (see the class note); kept, via
	/// <see cref="Engine_UiGuiState.StraySubsequentNavigateInterval"/>.
	/// </summary>
	public void CycleNavigationSpeed()
	{
		if (State.SubsequentNavigateInterval == 0.20000000298023224)
		{
			// `GUI.Input.SUBSEQUENT_NAVIGATE_INTERVAL = 0.15` — Input, not Timing. SHIPPED BUG.
			State.StraySubsequentNavigateInterval = 0.15000000596046448;
		}
		else if (State.SubsequentNavigateInterval == 0.15000000596046448)
		{
			State.SubsequentNavigateInterval = 0.10000000149011612;
		}
		else
		{
			State.SubsequentNavigateInterval = 0.20000000298023224;
		}
	}

	/// <summary>`GUI.ResetInputState` — the four latches off, `guiinput.dis main.5 pc0..11`:
	/// four field writes on `GUI.Input`, not a table replacement.</summary>
	public void ResetInputState()
	{
		State.PushingUpHeld = false;
		State.PushingDownHeld = false;
		State.PushingLeftHeld = false;
		State.PushingRightHeld = false;
	}

	/// <summary>
	/// `GUI.UpdateInput` — advances the repeat clock and re-arms it on full release; the same
	/// machine <see cref="Engine_UiNavigationClock"/> runs for the owned screens.
	/// </summary>
	public void UpdateInput()
	{
		if (State.TimeSinceLastNavigate < Engine_UiGuiConstants.InputIntervalClamp)
		{
			State.TimeSinceLastNavigate += State.TimeElapsed;
		}

		if (Math.Abs(State.LeftThumbStickX) < Engine_UiGuiConstants.ThumbstickReleaseThreshold &&
			Math.Abs(State.LeftThumbStickY) < Engine_UiGuiConstants.ThumbstickReleaseThreshold &&
			!State.PushingUpHeld && !State.PushingDownHeld &&
			!State.PushingLeftHeld && !State.PushingRightHeld)
		{
			State.NavigationCount = 0;
			State.NavigateInterval = Engine_UiGuiConstants.FirstNavigateInterval;
			State.TimeSinceLastNavigate = Engine_UiGuiConstants.FirstNavigateInterval;
		}
	}

	/// <summary>`GUI.CanNavigate` — `NavigateInterval &lt;= TimeSinceLastNavigate`.</summary>
	public bool CanNavigate() => State.NavigateInterval <= State.TimeSinceLastNavigate;

	/// <summary>`GUI.DoNavigate` — zero the clock, count the step, accelerate after the
	/// first.</summary>
	public void DoNavigate()
	{
		State.TimeSinceLastNavigate = 0;
		++State.NavigationCount;
		if (State.NavigationCount > 1)
		{
			State.NavigateInterval = State.SubsequentNavigateInterval;
		}
	}

	/// <summary>`GUI.OnEnter` — reset the latches, then announce.</summary>
	public void OnEnter()
	{
		ResetInputState();
		FireEvent?.Invoke("OnEnter");
	}

	/// <summary>`GUI.OnExit`.</summary>
	public void OnExit()
	{
		FireEvent?.Invoke("OnExit");
	}

	/// <summary>`GUI.OnA` .. `GUI.OnY` — fan out to the manager, if it has the handler.</summary>
	public void OnA() => HandleA?.Invoke();

	public void OnB() => HandleB?.Invoke();

	public void OnX() => HandleX?.Invoke();

	public void OnY() => HandleY?.Invoke();

	/// <summary>`GUI.IsPushingUp` — up is POSITIVE stick Y.</summary>
	public bool IsPushingUp() =>
		Engine_UiGuiConstants.ThumbstickPushThreshold < State.LeftThumbStickY;

	public bool IsPushingDown() =>
		Engine_UiGuiConstants.ThumbstickPushThreshold < State.LeftThumbStickY * -1f;

	public bool IsPushingLeft() =>
		Engine_UiGuiConstants.ThumbstickPushThreshold < State.LeftThumbStickX * -1f;

	public bool IsPushingRight() =>
		Engine_UiGuiConstants.ThumbstickPushThreshold < State.LeftThumbStickX;

	/// <summary>`GUI.Navigate` — a navigation happened somewhere; restart the adjust-vs-navigate
	/// spacing clock.</summary>
	public void Navigate()
	{
		State.TimeSinceNavigate = 0;
	}

	/// <summary>`GUI.Adjust`.</summary>
	public void Adjust()
	{
		State.TimeSinceAdjust = 0;
	}
}

/// <summary>
/// `guieventqueue.lua` — the translation is <see cref="Engine_UiGuiEventQueue"/>: the four
/// parallel arrays as one record list, push-front/pop-front, the five functions
/// (`PushButtonEvent`, `PushNavigationEvent`, `PushRemoveButtonEvent`, `PushSetButtonEvent`,
/// `PullEvent`) and the `NO_VALID_EVENT` empty answer. This shell adds the `g_Param` attribute
/// writes `PullEvent` makes, and two contracts the record API cannot state:
///
/// <list type="bullet">
/// <item><b>Every PUSH also ends with
/// `setAttribute(g_Param.element, "EventQueueSize", #Name)`</b> — not only the pull.
/// `guieventqueue.dis main.0 pc36..45`, `main.1 pc38..47`, `main.2 pc38..47` and
/// `main.3 pc38..47`, each ending `GETTABLE 'Name'` / `LEN` / `CALL`. All four also push with
/// `table.insert(t, 1, v)` (the `LOADK 1` position argument), so the queue is a STACK: last
/// pushed is first pulled.</item>
/// <item><b>`PullEvent` writes the size BEFORE its own pops.</b> `main.4 pc0..10` sets
/// `EventQueueSize = #Name`; pc11..15 reads it back; pc16 `LT A=0 B=265 C=2` branches on
/// `0 &lt; it`. Only then does the non-empty arm run four `setAttribute`/`table.remove` pairs
/// — `String1 = Name[1]` (pc18..35), `Float1 = Param1Low[1]` (pc36..53),
/// `Float2 = Param1High[1]` (pc54..71), `Float3 = Type[1]` (pc72..89). The size is never
/// rewritten, so the depth the C++ reader `sub_826C1810` sees still counts the event just
/// taken. The empty arm (pc91..114) writes `String1 = "NO_VALID_EVENT"` and
/// `Float1`/`Float2`/`Float3` = 0.</item>
/// </list>
/// </summary>
public sealed class Engine_UiGuiEventQueueScript
{
	public required Engine_UiGuiEventQueue Queue { get; init; }

	/// <summary>What `PullEvent` leaves on `g_Param` for `sub_826C1810` to read.</summary>
	public readonly record struct Pulled(
		string String1, double Float1, double Float2, double Float3, int EventQueueSize);

	/// <summary>`GUI:PullEvent()`, down to the attribute writes. The size reported is the depth
	/// BEFORE the pop, exactly as <see cref="Engine_UiGuiEventQueue.PullEvent"/> states.</summary>
	public Pulled PullEvent()
	{
		(Engine_UiGuiEventQueue.Event? front, int sizeBefore) = Queue.PullEvent();
		if (front is not { } evt)
		{
			return new Pulled("NO_VALID_EVENT", 0, 0, 0, 0);
		}

		return new Pulled(evt.Name, evt.Param1Low, evt.Param1High, evt.Type, sizeBefore);
	}
}

/// <summary>
/// `qbtext.lua` translated — the Anark Studio text lookup: scan `book.xml` for `nm="&lt;tag&gt;"`
/// and return the following `tx="…"` value. Dev-only by construction:
/// <see cref="Engine_UiGuiSetup"/> loads the chunk under `GUI.RunningInAnarkStudio` alone, its
/// last line rebinding `GUI.GetText = g_QBText.GetText` over the in-game `"..."` stub, and it
/// reads a developer-machine path. In the shipped game authored text and the C++ Babel resolver
/// own localisation.
///
/// SHIPPED QUIRKS:
/// <list type="bullet">
/// <item><b>`GetText` FALLS BACK TO THE TAG</b> — `qbtext.dis main.0 pc0..7`: `TEST A=2 C=1`
/// sends a falsy `GetGUIText` result to `pc6`, which copies the ARGUMENT into the return
/// register. It is `return self:GetGUIText(tag) or tag`, so a miss renders the tag.</item>
/// <item><b>The file is opened ONCE, lazily.</b> `qbtext.dis main.1 pc2..16`: `TEST` on
/// `g_QBText.fileContents` skips the load when it is already set; otherwise one `io.open`
/// into R3 (pc5..9), `R3:read("*a")` stored on `g_QBText.fileContents` (pc11..14), then
/// `R3:close()` on that same handle (pc15..16). Modeled by <see cref="FileContents"/> being
/// supplied by the caller.</item>
/// <item><b>The value scan is capped at 151 characters.</b> `main.1 pc57..59` increments the
/// counter then breaks on `150 &lt; n`, so the cursor can advance 151 times past `tx="` before
/// the walk gives up.</item>
/// </list>
/// </summary>
public sealed class Engine_UiQbText
{
	/// <summary>The path the chunk hardcodes, kept verbatim as documentation.</summary>
	public const string DevBookXmlPath =
		@"..\..\..\..\..\Dev\Fable2\MainBranch\SourceAssets\book.xml";

	/// <summary>The scan's runaway guard — the walk breaks once the step counter EXCEEDS this,
	/// `qbtext.dis main.1 pc58` (`LT A=0 B=273 C=5`, RK 273 = 150).</summary>
	public const int MaxTextLength = 150;

	/// <summary>`g_QBText.fileContents` — loaded lazily by the chunk, supplied by the caller
	/// here.</summary>
	public string? FileContents { get; set; }

	/// <summary>`g_QBText.GetText` — the bound `GUI.GetText`. `return GetGUIText(tag) or tag`;
	/// the tag itself is the miss value, `qbtext.dis main.0 pc0..7`.</summary>
	public string GetText(string tag) => GetGuiText(tag) ?? tag;

	/// <summary>
	/// `g_QBText.GetGUIText` — `qbtext.dis main.1 pc17..70`.
	/// `string.find(fileContents, 'nm="'..tag..'"')` (pc18..26), nil on a miss (pc28..29);
	/// `string.find(fileContents, 'tx="', <i>nmEnd</i>)` (pc30..36), the search RESUMING AT THE
	/// END of the name match (R4, find's second return); cursor one past that match
	/// (pc37..38); walk while `string.sub(s, at, at) ~= '"'` (pc46), breaking on `150 &lt; n`
	/// (pc57..59); answer `string.sub(s, start, at - 1)` (pc62..69), closing quote excluded.
	///
	/// The `at &lt; Length` guard is ours. Lua's `string.sub` past the end answers `""`, which
	/// is never `'"'`, so the shipped walk runs to the cap instead of faulting.
	/// </summary>
	public string? GetGuiText(string tag)
	{
		if (FileContents is null)
		{
			return null;
		}

		string needle = "nm=\"" + tag + "\"";
		int name = FileContents.IndexOf(needle, StringComparison.Ordinal);
		if (name < 0)
		{
			return null;
		}

		int text = FileContents.IndexOf(
			"tx=\"", name + needle.Length, StringComparison.Ordinal);
		if (text < 0)
		{
			return null;
		}

		int start = text + 4;
		int at = start;
		int steps = 0;
		while (at < FileContents.Length && FileContents[at] != '"')
		{
			++at;
			++steps;
			if (steps > MaxTextLength)
			{
				break;
			}
		}

		return FileContents[start..at];
	}
}

/// <summary>
/// `qbeventtranslator.lua` — FULLY COVERED ELSEWHERE. <see cref="Engine_UiContract"/> carries
/// the whole script as <see cref="Engine_UiContract.PressTranslation"/>: `onInitialize` finds
/// `Scene.UIContract` and registers four closures, each a single
/// `fireEvent("OnPress&lt;X&gt;", UIContract)` — `onSelect`→`OnPressA`, `onBack`→`OnPressB`,
/// `onUp`→`OnPressUp`, `onDown`→`OnPressDown`. Nothing else is in the file. The one method here is
/// the registration's runtime effect, answered from that shared table.
/// </summary>
public static class Engine_UiQbEventTranslator
{
	/// <summary>An abstract contract event arrives: fire its concrete press back on the same
	/// contract element. False when the event is not one of the four registered.</summary>
	public static bool Translate(string abstractEvent, Action<string> fireOnContract)
	{
		if (Engine_UiContract.ConcreteFor(abstractEvent) is not { } concrete)
		{
			return false;
		}

		fireOnContract(concrete);
		return true;
	}
}
