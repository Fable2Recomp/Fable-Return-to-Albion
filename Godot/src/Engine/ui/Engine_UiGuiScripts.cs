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

/// <summary>
/// `spellassigning.lua`, TRANSLATED — the will-assignment carousel's own state machine: the
/// queued row/column moves, the fade-then-move row change, the scroll-then-commit column
/// change, and the six scroller bubbles' show/hide choreography.
///
/// The constants (paths, 5×8 dimensions, slide numbers) are
/// <see cref="Engine_UiScreenConstants"/> and the running widget is the owned
/// <see cref="Engine_UiSpellAssign"/>/<see cref="Engine_UiSpellAssignSet"/>; this class is the
/// reference translation, with the engine natives modeled as state: `goToSlide` on the four
/// animated components becomes their `*Slide` fields,
/// `setAttribute(..., "active"/"opacity"/"position.y"/"ResourceIDString")` becomes the arrays
/// below, and `doneFadingOut`/`doneFadingIn`/`doneScrolling` become the public `OnDone*` methods
/// a driver calls at completion.
///
/// ALL INDICES ARE 1-BASED, as the script's. `CurrentColPosition` IS THE SPELL TYPE (1..8), not
/// an index into the row's available list — `setCurrentAssignedSpell` copies the assigned
/// spell straight into it, and the left/right walks scan TYPES for the next one whose
/// `maxAvailableSpellLevels` clears the row.
///
/// SHIPPED QUIRKS:
/// <list type="bullet">
/// <item><b>Moving the column COMMITS.</b> `onDoneHorizontalScrolAnim` calls
/// `assignCurrentSpellInRow()` — there is no confirm step, matching the guest's
/// `sub_826B5330` behaviour the owned widget reproduces.</item>
/// <item><b>A row change assigns BEFORE it queues.</b> `onUp`/`onDown`/`onGoToRow` call
/// `assignCurrentSpellInRow()` first, so leaving a row locks in whatever the carousel sat
/// on.</item>
/// <item><b>Arriving on a never-assigned row puts the cursor on spell 0.</b> `setRow` copies
/// `assignedSpells[row]` (initially 0) into `CurrentColPosition`; the refresh then draws no
/// centre icon. Kept.</item>
/// <item><b>`onUpdate`'s second arm is gated by a LIVE local, not a dead constant.</b>
/// `spellassigning.dis main.9` loads `R1 = false` at `pc0`, sets `R1 = true` at `pc12` right
/// after `startFadeOut()`, and tests it at `pc16` — mutual exclusion, so a frame that starts a
/// row fade never also starts a column scroll.</item>
/// <item><b>Scroller bubble 6 is the wrap cell</b>: `GoingLeft` shows it on the LEFT flank,
/// `GoingRight` on the RIGHT, and the static refresh never touches it.</item>
/// <item><b>The demo tables are dead in-game</b> — `setupDemoTable1` is empty,
/// `setupDemoTable2`/`setupDebuggingStuff` are studio scaffolding. Kept as shipped.</item>
/// <item><b>`onUp`/`onDown`/`onLeft`/`onRight` are DEBUG-ONLY.</b> `onInitializeReal`
/// (`spellassigning.dis main.7 pc85..154`) registers fourteen events and not one direction;
/// the four directional handlers are registered only by `setupDebuggingStuff`
/// (`main.2 pc7..34`, which then fires `refreshRow` with `parent` and `currentColPosition`).
/// The shipped widget is driven through `goToRow`/`goToCol`.</item>
/// </list>
///
/// The scroller cannot bind: `main.7 pc74..83` fetches the six bubbles as `'H' .. tostring(i)` —
/// `H1`..`H6` — and no such element exists in `hud.bgf` or `abilitiesscreen.bgf`, which carry
/// `HL2`/`HL1`/`HC`/`HR1`/`HR2`.
///
/// The bytecode states all three of the places a decompiler dropped:
/// <list type="bullet">
/// <item>`onUp` queues `currentRowPosition + 1` (`main.3 pc8`, `ADD R3 = R3 ADD 1`) under
/// `row &lt; maxAssignableRows`; `onDown` queues `currentRowPosition − 1` (`main.4 pc7`,
/// `SUB R3 = R3 SUB 1`) under `1 &lt; row`.</item>
/// <item>`setIconImages_Row`'s loops are legible in full at `main.19 pc40..60` — the left
/// flank's `3 − i` wrap and the right flank's `3 + i` are both in the instructions.</item>
/// <item>`getNumSpellsAtLevel*Of` initialise their counter — `main.29 pc0` and `main.30 pc0`
/// are both `LOADK 0`.</item>
/// </list>
/// </summary>
public sealed class Engine_UiSpellAssigningScript
{
	/// <summary>`self.ICON_TEXTURES` — the chunk's own icon list, kept verbatim. The BINARY's
	/// table (with Babel tags, and `.tex` extensions) is
	/// <see cref="Engine_UiScreenConstants.Wills"/>; the two agree on the eight stems.</summary>
	public static readonly string[] IconTextures =
	{
		@"Art\GUI\WheelIcons\Abilities\will_shock.tga",
		@"Art\GUI\WheelIcons\Abilities\will_blast.tga",
		@"Art\GUI\WheelIcons\Abilities\icon_will_time_control.tga",
		@"Art\GUI\WheelIcons\Abilities\will_sword_of_damocles.tga",
		@"Art\GUI\WheelIcons\Abilities\will_vortex.tga",
		@"Art\GUI\WheelIcons\Abilities\will_chaos.tga",
		@"Art\GUI\WheelIcons\Abilities\will_force_push.tga",
		@"Art\GUI\WheelIcons\Abilities\will_dead_rising.tga",
	};

	private const int MaxSpellLevels = Engine_UiScreenConstants.MaxSpellLevels;

	private const int MaxSpellTypes = Engine_UiScreenConstants.MaxSpellTypes;

	private const int NumScrollerBubbles = Engine_UiScreenConstants.SpellScrollerBubbles;

	/// <summary>`self.rowYPositions` — read off the authored art at initialize
	/// (`getAttribute(mainBubbles[n], "position.y")`); the hud.bgf values, index 1..5.</summary>
	public double[] RowYPositions = { 0, -160, -80, 0, 80, 160 };

	/// <summary>`self.maxAssignableRows` — the highest row any spell reaches; rows above it are
	/// inert and unhighlighted.</summary>
	public int MaxAssignableRows { get; private set; }

	/// <summary>`self.assignedSpells[1..5]` — the spell TYPE on each row, 0 = none.</summary>
	public int[] AssignedSpells { get; } = new int[MaxSpellLevels + 1];

	/// <summary>`self.maxAvailableSpellLevels[1..8]` — the highest row each spell type may be
	/// assigned to, 0 = not offered at all.</summary>
	public int[] MaxAvailableSpellLevels { get; } = new int[MaxSpellTypes + 1];

	/// <summary>`self.currentRowPosition` / `self.currentColPosition` — the cursor. The column
	/// IS a spell type; see the class note.</summary>
	public int CurrentRowPosition { get; private set; } = 1;

	public int CurrentColPosition { get; private set; } = 1;

	/// <summary>`self.waitingForFade` / `self.waitingForScroll` — the two animation locks
	/// `onUpdate` serializes moves behind.</summary>
	public bool WaitingForFade { get; private set; }

	public bool WaitingForScroll { get; private set; }

	/// <summary>`self.queuedRow` / `self.queuedCol` — at most one pending move each.</summary>
	public int? QueuedRow { get; private set; }

	public int? QueuedCol { get; private set; }

	/// <summary>`self.adjusterComponent`'s `position.y` and current slide
	/// (static/left/right).</summary>
	public double AdjusterY { get; private set; }

	public int AdjusterSlide { get; private set; } = Engine_UiScreenConstants.SpellSlideStatic;

	/// <summary>`self.rowFadeComponent`'s slide (no-fade/in/out).</summary>
	public int RowFadeSlide { get; private set; } = Engine_UiScreenConstants.SpellRowNoFadeSlide;

	/// <summary>`self.parent`'s slide — the whole component's fade in/out.</summary>
	public int ComponentSlide { get; private set; }

	/// <summary>`self.mainBubbles[1..5]` — icon (0 = authored placeholder untouched), opacity,
	/// and active/inactive slide per row bubble.</summary>
	public int[] MainBubbleIcon { get; } = new int[MaxSpellLevels + 1];

	public double[] MainBubbleOpacity { get; } =
		new double[] { 0, 100, 100, 100, 100, 100 };

	public int[] MainBubbleSlide { get; } = new int[]
	{
		0,
		Engine_UiScreenConstants.SpellBubbleInactiveSlide,
		Engine_UiScreenConstants.SpellBubbleInactiveSlide,
		Engine_UiScreenConstants.SpellBubbleInactiveSlide,
		Engine_UiScreenConstants.SpellBubbleInactiveSlide,
		Engine_UiScreenConstants.SpellBubbleInactiveSlide,
	};

	/// <summary>`self.scrollerBubbles[1..6]` — `active` and each `Icon`'s texture index into
	/// <see cref="IconTextures"/> (1-based, 0 = untouched).</summary>
	public bool[] ScrollerBubbleActive { get; } =
		new bool[] { false, true, true, true, true, true, true };

	public int[] ScrollerBubbleIcon { get; } = new int[NumScrollerBubbles + 1];

	/// <summary>
	/// `self.onInitializeReal` — paths and element fetches are
	/// <see cref="Engine_UiScreenConstants"/>' constants and the art's job; the behavioural
	/// remainder is: capture `rowYPositions` off the five bubbles, register the fourteen event
	/// handlers (all internal here — `fireEvent` on the parent reaches this same table), and
	/// `onReset()`.
	/// </summary>
	public void Initialize() => OnReset();

	/// <summary>`self.onReset` — everything to zero, cursor to (1,1), adjuster parked on row
	/// 1's authored y.</summary>
	public void OnReset()
	{
		MaxAssignableRows = 0;
		Array.Clear(AssignedSpells);
		Array.Clear(MaxAvailableSpellLevels);
		CurrentRowPosition = 1;
		CurrentColPosition = 1;
		AdjusterY = RowYPositions[CurrentRowPosition];
		WaitingForFade = false;
		WaitingForScroll = false;
		QueuedRow = null;
		QueuedCol = null;
	}

	/// <summary>
	/// `self.onUpdate` — starts at most one queued move per frame, row moves first, each behind
	/// both animation locks. The two arms are mutually exclusive: `spellassigning.dis main.9`
	/// sets a local true in the row arm (`pc12`, after `startFadeOut()`) and tests it in the
	/// column arm (`pc16`), the same condition `WaitingForFade` states.
	/// </summary>
	public void OnUpdate()
	{
		if (QueuedRow is not null && !WaitingForScroll && !WaitingForFade)
		{
			StartFadeOut();
		}

		if (QueuedCol is { } queuedCol && !WaitingForScroll && !WaitingForFade)
		{
			if (queuedCol < CurrentColPosition)
			{
				StartScrollLeft(queuedCol);
			}
			else
			{
				StartScrollRight(queuedCol);
			}

			WaitingForScroll = true;
		}
	}

	/// <summary>`self.onUp` — queue row+1, `spellassigning.dis main.3 pc8`. Assigns first, as
	/// shipped. DEBUG-ONLY entry point; see the class note.</summary>
	public void OnUp()
	{
		if (CurrentRowPosition < MaxAssignableRows)
		{
			AssignCurrentSpellInRow();
			QueueRow(CurrentRowPosition + 1);
		}
	}

	/// <summary>`self.onDown` — queue row−1, `spellassigning.dis main.4 pc7`. DEBUG-ONLY entry
	/// point; see the class note.</summary>
	public void OnDown()
	{
		if (CurrentRowPosition > 1)
		{
			AssignCurrentSpellInRow();
			QueueRow(CurrentRowPosition - 1);
		}
	}

	/// <summary>`self.onLeft` — queue the next offered spell type to the left, if any.
	/// DEBUG-ONLY entry point; see the class note.</summary>
	public void OnLeft()
	{
		int left = GetSpellAtLevelLeftOf(CurrentRowPosition, CurrentColPosition);
		if (left > 0)
		{
			QueueCol(left);
		}
	}

	/// <summary>`self.onRight`. DEBUG-ONLY entry point; see the class note.</summary>
	public void OnRight()
	{
		int right = GetSpellAtLevelRightOf(CurrentRowPosition, CurrentColPosition);
		if (right > 0)
		{
			QueueCol(right);
		}
	}

	/// <summary>`self.onGoToRow` — the external row command: assign, then queue.</summary>
	public void OnGoToRow(int row)
	{
		AssignCurrentSpellInRow();
		QueueRow(row);
	}

	/// <summary>`self.onGoToCol`.</summary>
	public void OnGoToCol(int col) => QueueCol(col);

	private void HideElement(int bubble) => ScrollerBubbleActive[bubble] = false;

	private void ShowElement(int bubble) => ScrollerBubbleActive[bubble] = true;

	/// <summary>
	/// `self.onSetInactiveColBubblesGoingLeft` — the flank visibility for a LEFTWARD move,
	/// centre-to-be at `col`. Bubble 6 wraps onto the left flank here. Branch-for-branch.
	/// </summary>
	public void OnSetInactiveColBubblesGoingLeft(int col)
	{
		int left = GetNumSpellsAtLevelLeftOf(CurrentRowPosition, col);
		if (left == 0)
		{
			HideElement(6);
			HideElement(1);
			ShowElement(2);
		}
		else if (left == 1)
		{
			HideElement(6);
			ShowElement(1);
			ShowElement(2);
		}
		else
		{
			ShowElement(6);
			ShowElement(1);
			ShowElement(2);
		}

		int right = GetNumSpellsAtLevelRightOf(CurrentRowPosition, col);
		if (right <= 1)
		{
			HideElement(4);
			HideElement(5);
		}
		else if (right == 2)
		{
			ShowElement(4);
			HideElement(5);
		}
		else
		{
			ShowElement(4);
			ShowElement(5);
		}

		SetIconImagesRow(CurrentRowPosition, col + 1);
	}

	/// <summary>
	/// `self.onSetInactiveColBubblesGoingRight` — bubble 6 wraps onto the RIGHT flank. Not the
	/// mirror of GoingLeft: the left flank hides both bubbles at `left &lt;= 1` where GoingLeft's
	/// right flank shows one at `right == 2`. Shipped asymmetry, kept.
	/// </summary>
	public void OnSetInactiveColBubblesGoingRight(int col)
	{
		int left = GetNumSpellsAtLevelLeftOf(CurrentRowPosition, col);
		if (left <= 1)
		{
			HideElement(1);
			HideElement(2);
		}
		else if (left == 2)
		{
			HideElement(1);
			ShowElement(2);
		}
		else
		{
			ShowElement(1);
			ShowElement(2);
		}

		int right = GetNumSpellsAtLevelRightOf(CurrentRowPosition, col);
		if (right == 0)
		{
			ShowElement(4);
			HideElement(5);
			HideElement(6);
		}
		else if (right == 1)
		{
			ShowElement(4);
			ShowElement(5);
			HideElement(6);
		}
		else
		{
			ShowElement(4);
			ShowElement(5);
			ShowElement(6);
		}

		SetIconImagesRow(CurrentRowPosition, col - 1);
	}

	/// <summary>
	/// `self.onSetInactiveColBubblesStatic` — the settled refresh (`refreshRow`). Never touches
	/// bubble 6, and repaints the assigned column icons too.
	/// </summary>
	public void OnSetInactiveColBubblesStatic(int col)
	{
		int left = GetNumSpellsAtLevelLeftOf(CurrentRowPosition, col);
		int right = GetNumSpellsAtLevelRightOf(CurrentRowPosition, col);

		if (left == 0)
		{
			HideElement(1);
			HideElement(2);
		}
		else if (left == 1)
		{
			HideElement(1);
			ShowElement(2);
		}
		else
		{
			ShowElement(1);
			ShowElement(2);
		}

		if (right == 0)
		{
			HideElement(4);
			HideElement(5);
		}
		else if (right == 1)
		{
			ShowElement(4);
			HideElement(5);
		}
		else
		{
			ShowElement(4);
			ShowElement(5);
		}

		SetIconImagesAssigned();
		SetIconImagesRow(CurrentRowPosition, col);
	}

	/// <summary>`self.setRowIcon` — a scroller bubble's `Icon.ResourceIDString`.</summary>
	public void SetRowIcon(int bubble, int spell)
	{
		ScrollerBubbleIcon[bubble] = spell;
	}

	/// <summary>`self.setColIcon` — a row bubble's `Icon.ResourceIDString`. Spell 0 indexes
	/// nothing in `ICON_TEXTURES` (Lua nil write); modeled as icon 0 = none.</summary>
	public void SetColIcon(int row, int spell)
	{
		MainBubbleIcon[row] = spell;
	}

	/// <summary>
	/// `self.setIconImages_Row` — paint the scroller bubbles with the offered spells around
	/// `centerCol`, `spellassigning.dis main.19`.
	///
	/// The right-flank bound comes from the RAW LEFT COUNT: `pc7..11` is
	/// `numRight = #available − getNumSpellsAtLevelLeftOf(row, centerCol)`, which with the centre
	/// column in the list is (spells to the right) + 1. Both counts clamp to 3 (`pc23..34`).
	///
	/// The left wrap spends a right slot: at `pc40..44`, when `3 − i` reaches 0 the bubble becomes
	/// 6 and `numRight` is decremented, so the flanks cannot both claim the wrap cell. The right
	/// loop is inclusive, `for i = 0, numRight` (`pc51..54`), so its last step indexes one past
	/// the last offered spell — Lua's nil write, which clears that bubble; modeled as icon 0.
	/// </summary>
	public void SetIconImagesRow(int row, int centerCol)
	{
		List<int> available = GetRowAvailableSpells(row);
		int center = available.IndexOf(centerCol);
		if (center < 0)
		{
			return;
		}

		int rawLeft = GetNumSpellsAtLevelLeftOf(row, centerCol);
		int numLeft = Math.Min(rawLeft, 3);
		int numRight = Math.Min(available.Count - rawLeft, 3);

		for (int step = 1; step <= numLeft; ++step)
		{
			int bubble = 3 - step;
			if (bubble <= 0)
			{
				bubble = 6;
				--numRight;
			}

			SetRowIcon(bubble, SpellAt(available, center - step));
		}

		for (int step = 0; step <= numRight; ++step)
		{
			SetRowIcon(3 + step, SpellAt(available, center + step));
		}
	}

	/// <summary>Indexing past the list is Lua's nil, which clears the bubble — icon 0.</summary>
	private static int SpellAt(List<int> available, int index) =>
		index >= 0 && index < available.Count ? available[index] : 0;

	/// <summary>`self.onRefreshCol` (`refreshCol`).</summary>
	public void OnRefreshCol() => SetIconImagesAssigned();

	/// <summary>`self.setIconImages_Assigned` — each assignable row bubble shows its assigned
	/// spell's icon.</summary>
	public void SetIconImagesAssigned()
	{
		for (int row = 1; row <= MaxAssignableRows; ++row)
		{
			SetColIcon(row, AssignedSpells[row]);
		}
	}

	/// <summary>`self.onFadeComponentIn` — the parent's fade-in slide.</summary>
	public void OnFadeComponentIn()
	{
		ComponentSlide = Engine_UiScreenConstants.SpellComponentFadeInSlide;
	}

	/// <summary>`self.onFadeComponentOut`.</summary>
	public void OnFadeComponentOut()
	{
		ComponentSlide = Engine_UiScreenConstants.SpellComponentFadeOutSlide;
	}

	/// <summary>`self.getRowAvailableSpells` — the spell types whose max level clears `row`,
	/// in type order.</summary>
	public List<int> GetRowAvailableSpells(int row)
	{
		var available = new List<int>();
		for (int spell = 1; spell <= MaxSpellTypes; ++spell)
		{
			if (row <= MaxAvailableSpellLevels[spell])
			{
				available.Add(spell);
			}
		}

		return available;
	}

	/// <summary>`self.startScrollLeft` — hide the row bubble, run the adjuster's RIGHT slide
	/// (the strip moves right so the selection moves left), pre-paint the flanks.</summary>
	public void StartScrollLeft(int col)
	{
		MainBubbleOpacity[CurrentRowPosition] = 0;
		AdjusterSlide = Engine_UiScreenConstants.SpellSlideRight;
		OnSetInactiveColBubblesGoingLeft(col);
	}

	/// <summary>`self.startScrollRight` — the LEFT slide, same inversion.</summary>
	public void StartScrollRight(int col)
	{
		MainBubbleOpacity[CurrentRowPosition] = 0;
		AdjusterSlide = Engine_UiScreenConstants.SpellSlideLeft;
		OnSetInactiveColBubblesGoingRight(col);
	}

	/// <summary>`self.getSpellAtLevelRightOf` — the next offered TYPE above `col`, 0 for
	/// none.</summary>
	public int GetSpellAtLevelRightOf(int row, int col)
	{
		if (MaxSpellTypes <= col)
		{
			return 0;
		}

		for (int spell = col + 1; spell <= MaxSpellTypes; ++spell)
		{
			if (row <= MaxAvailableSpellLevels[spell])
			{
				return spell;
			}
		}

		return 0;
	}

	/// <summary>`self.getSpellAtLevelLeftOf`.</summary>
	public int GetSpellAtLevelLeftOf(int row, int col)
	{
		if (col <= 1)
		{
			return 0;
		}

		for (int spell = col - 1; spell >= 1; --spell)
		{
			if (row <= MaxAvailableSpellLevels[spell])
			{
				return spell;
			}
		}

		return 0;
	}

	/// <summary>`self.getNumSpellsAtLevelLeftOf` — counts from zero, `spellassigning.dis
	/// main.29 pc0` (`LOADK 0`).</summary>
	public int GetNumSpellsAtLevelLeftOf(int row, int col)
	{
		if (col <= 1)
		{
			return 0;
		}

		int count = 0;
		for (int spell = 1; spell <= col - 1; ++spell)
		{
			if (row <= MaxAvailableSpellLevels[spell])
			{
				++count;
			}
		}

		return count;
	}

	/// <summary>`self.getNumSpellsAtLevelRightOf` — counts from zero, `spellassigning.dis
	/// main.30 pc0` (`LOADK 0`).</summary>
	public int GetNumSpellsAtLevelRightOf(int row, int col)
	{
		if (MaxSpellTypes <= col)
		{
			return 0;
		}

		int count = 0;
		for (int spell = col + 1; spell <= MaxSpellTypes; ++spell)
		{
			if (row <= MaxAvailableSpellLevels[spell])
			{
				++count;
			}
		}

		return count;
	}

	/// <summary>`self.highlightAssignableBubbles` — every assignable row bubble to the ACTIVE
	/// slide. Bubbles above `MaxAssignableRows` are never reverted by the script.</summary>
	public void HighlightAssignableBubbles()
	{
		for (int row = 1; row <= MaxAssignableRows; ++row)
		{
			MainBubbleSlide[row] = Engine_UiScreenConstants.SpellBubbleActiveSlide;
		}
	}

	/// <summary>`self.setMaxAvailableSpellLevel` — raising any spell's ceiling can raise
	/// `MaxAssignableRows`, re-highlighting.</summary>
	public void SetMaxAvailableSpellLevel(int spell, int maxLevel)
	{
		MaxAvailableSpellLevels[spell] = maxLevel;
		if (MaxAssignableRows < maxLevel)
		{
			MaxAssignableRows = maxLevel;
			HighlightAssignableBubbles();
		}
	}

	/// <summary>`self.onSetMaxAvailableSpellLevel` — the event form; the extra leading
	/// parameter is the event's element argument, dropped as the Lua drops it.</summary>
	public void OnSetMaxAvailableSpellLevel(int spell, int maxLevel) =>
		SetMaxAvailableSpellLevel(spell, maxLevel);

	/// <summary>`self.setCurrentAssignedSpell` — writes the row's assignment; on the CURRENT
	/// row it also moves the column cursor onto the assigned type.</summary>
	public void SetCurrentAssignedSpell(int row, int spell)
	{
		AssignedSpells[row] = spell;
		if (row == CurrentRowPosition)
		{
			CurrentColPosition = spell;
		}
	}

	/// <summary>`self.onSetCurrentAssignedSpell`.</summary>
	public void OnSetCurrentAssignedSpell(int row, int spell) =>
		SetCurrentAssignedSpell(row, spell);

	/// <summary>`self.assignCurrentSpellInRow` — the commit: cursor position into the
	/// assignment table.</summary>
	public void AssignCurrentSpellInRow() =>
		SetCurrentAssignedSpell(CurrentRowPosition, CurrentColPosition);

	/// <summary>`self.queueRow` — no-op on the current row.</summary>
	public void QueueRow(int row)
	{
		if (row != CurrentRowPosition)
		{
			QueuedRow = row;
		}
	}

	/// <summary>`self.queueCol`.</summary>
	public void QueueCol(int col)
	{
		if (col != CurrentColPosition)
		{
			QueuedCol = col;
		}
	}

	/// <summary>
	/// `self.onDoneHorizontalScrolAnim` [sic] — the scroll animation finished: adjuster back to
	/// static, the row bubble back on, land the cursor, COMMIT, and refresh.
	/// </summary>
	public void OnDoneHorizontalScrollAnim()
	{
		AdjusterSlide = Engine_UiScreenConstants.SpellSlideStatic;
		MainBubbleOpacity[CurrentRowPosition] = 100;
		WaitingForScroll = false;

		// HOST GUARD: `main.39 pc12..16` writes `currentColPosition = queuedCol` with no nil
		// test. Unreachable with a nil queue in practice; the guard is ours.
		if (QueuedCol is { } queuedCol)
		{
			CurrentColPosition = queuedCol;
		}

		AssignCurrentSpellInRow();
		QueuedCol = null;

		// `fireEvent("refreshRow", self.parent, self.currentColPosition)` — self-dispatch; the
		// handler registered for `refreshRow` is onSetInactiveColBubblesStatic.
		OnSetInactiveColBubblesStatic(CurrentColPosition);
	}

	/// <summary>`self.setRow` — land the row cursor mid-fade: column onto the row's assigned
	/// spell (0 on a virgin row — see the class note), refresh, park the adjuster.</summary>
	public void SetRow(int row)
	{
		CurrentRowPosition = row;
		CurrentColPosition = AssignedSpells[row];
		OnSetInactiveColBubblesStatic(CurrentColPosition);
		AdjusterY = RowYPositions[CurrentRowPosition];
	}

	/// <summary>`self.startFadeOut` — lock, and run the row fade-out slide.</summary>
	public void StartFadeOut()
	{
		WaitingForFade = true;
		RowFadeSlide = Engine_UiScreenConstants.SpellRowFadeOutSlide;
	}

	/// <summary>`self.onDoneFadeOut` — faded to nothing: move the row, fade back in.</summary>
	public void OnDoneFadeOut()
	{
		// HOST GUARD: `main.42 pc0..2` calls `setRow(queuedRow)` unguarded. Ours, not the
		// script's.
		if (QueuedRow is { } queuedRow)
		{
			SetRow(queuedRow);
		}

		RowFadeSlide = Engine_UiScreenConstants.SpellRowFadeInSlide;
	}

	/// <summary>`self.onDoneFadeIn` — unlock; clear the queue only if it still names the row we
	/// are on (a newer queued row starts the next cycle from `onUpdate`).</summary>
	public void OnDoneFadeIn()
	{
		WaitingForFade = false;
		if (CurrentRowPosition == QueuedRow)
		{
			QueuedRow = null;
		}

		RowFadeSlide = Engine_UiScreenConstants.SpellRowNoFadeSlide;
	}

	/// <summary>`self.setupDemoTable1` — SHIPPED EMPTY.</summary>
	public void SetupDemoTable1()
	{
	}

	/// <summary>`self.setupDemoTable2` — studio demo data; dead in-game (only
	/// `setupDebuggingStuff` calls it). Kept as shipped.</summary>
	public void SetupDemoTable2()
	{
		OnSetMaxAvailableSpellLevel(1, 3);
		OnSetMaxAvailableSpellLevel(2, 1);
		OnSetMaxAvailableSpellLevel(6, 2);
		OnSetCurrentAssignedSpell(1, 1);
		OnSetCurrentAssignedSpell(2, 1);
		OnSetCurrentAssignedSpell(3, 1);
		OnFadeComponentIn();
	}
}
