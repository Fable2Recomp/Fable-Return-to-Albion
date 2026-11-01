namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Ui;

/// <summary>
/// The UI cut's routing table for Fable 3: what Godot does with each `GUI.*` native its game VM
/// calls (the seam: Fable3/src/hooks/src/gui_natives.cpp posts, the shared core drains,
/// <see cref="Engine_UiGuiNatives"/> dispatches).
/// </summary>
/// <remarks>
/// A call arrives with the name the script used and its arguments, BEFORE the guest's own
/// implementation runs; taking it does not suppress it, so every predicate the scripts read
/// back still answers out of the guest's own state and the guest's own box still takes the pad.
///
/// Three answers, as Fable 2's table has them: <c>Register</c> (Godot draws the surface it
/// affects), <c>Observe</c> (deliberately not owned here, with the reason — the census counts
/// these apart from the unrouted), neither (the census reports it with its arguments, so the
/// table is built from what the running game calls).
///
/// Argument shapes are decompiled facts of the Fable 3 IDB (disc build), per native below. The
/// text argument of every box native is resolved by one helper, <c>sub_82516418(out, L, first)</c>:
/// the tag as given through the text manager, then with a <c>TEXT_</c> prefix, else the literal;
/// each following argument substitutes <c>%N</c>, numbers as integers (sub_82300020), strings
/// resolved the same way — the shape the counter and timer routes below resolve by.
/// </remarks>
public partial class Fable3GuiNatives : Node
{
	/// <summary>The box the message natives name — `unk_835582B8`, "Unnamed", as Fable 2's.</summary>
	private const string UnnamedBox = Engine_UiGuiBoxes.MessageBoxName;

	private readonly Engine_UiGuiNatives _router = new();

	/// <summary>The boxes, counters and menu box the natives put up; <see cref="Fable3Boxes"/> draws it.</summary>
	public Engine_UiGuiBoxes Boxes { get; } = new();

	public override void _Ready() => Route();

	public override void _Process(double delta)
	{
		_router.Drive();
		Boxes.Tick(delta);
	}

	private void Route()
	{
		// The Gameface element writes (gui_element_hooks.cpp), by presentation and path.
		Fable3Elements.Register(_router);

		// ---- THE BOXES, AS THE BOX MANAGER SHOWS AND DROPS THEM (display_box_hooks.cpp) ------
		//
		// The natives only QUEUE a box; the moment it is on screen is the box manager's: its queue
		// tick sub_822878B0 promotes the front pending box into the showing slot (queue+20) and
		// calls Show, and releases it when its Update returns 0 — lifetime, Hide or
		// RemoveDisplayBox alike. Many boxes are queued by C++ and never pass a native, so the
		// statements are taken there, with the box's class (its vtable), its resolved text and the
		// per-class fields. The guest routes the pad to the showing box itself (41/42, 73, 31/32),
		// so Godot draws and never answers.
		_router.Register("@Box.Open", BoxOpened);
		_router.Register("@Box.Close", call => BoxClosed(Parse(call.Argument(1).Text)));
		_router.Register("@Box.Value", call =>
		{
			if (Boxes.Adjusting is { } adjusting)
			{
				adjusting.Value = (int)call.Argument(1).Number;
			}
		});

		const string queued = "queues a box; it is stated when the box manager shows it (@Box.Open) and when it drops it (@Box.Close)";
		foreach (string name in new[] { "DisplayMessageBox", "DisplayYesNoQuestion", "DisplayYesNoQuestionWithTextOverride", "DisplayQuestPrimaryQuestion", "DisplayNumericAdjusterParams", "DisplayQuestBoxParams", "DisplayButtonBox", "DisplayFoodItemQueryBox", "DisplayNatalQueryBox", "DisplayDivorceMessage", "DisplayIntermissionScreen", "DisplayIntermissionScreenWithLifetime", "DisplayItemNameWithCount", "DisplayNameFromItem", "RemoveDisplayBox", "RemoveAllDisplayBoxesWithName" })
		{
			_router.Observe(name, queued);
		}

		// ---- COUNTERS AND TIMERS: the registry at `*(H+0x48)` ---------------------------------
		//
		// `SetCounter(name, tag, first, second, flag)` (0x8251AA38): sub_825E7860(H+72,
		// FNV1(name), tag, (int)first, (int)second, flag) — the values are truncated (`fctiwz`).
		_router.Register("SetCounter", call =>
		{
			Engine_UiGuiBoxes.Counter counter = Boxes.SetCounter(
				call.Argument(0).Text, call.Argument(1).Text,
				(int)call.Argument(2).Number, (int)call.Argument(3).Number);
			counter.Text = Engine_UiGuiText.Substitute(
				Engine_UiGuiText.Resolve(counter.Tag, Fable3Strings.Current),
				new[] { counter.First.ToString(), counter.Second.ToString() });
		});

		// `SetTimer(name, seconds)` (0x8251AF38, void(str,num)); `SetTimerWithText(name, seconds,
		// tag)` (0x8251B158 -> sub_8251B000(L, 0): string at base+0, number at base+8, string at
		// base+16 -> sub_825E7AF8(H+72, FNV1(name), tag, seconds)); `SetCountUpTimerWithText` is the
		// same body counting up (0x8251B180).
		_router.Register("SetTimer", call =>
			Boxes.SetTimer(call.Argument(0).Text, string.Empty, call.Argument(1).Number, countsUp: false));
		_router.Register("SetTimerWithText", call => Timer(call, countsUp: false));
		_router.Register("SetCountUpTimerWithText", call => Timer(call, countsUp: true));
		_router.Register("PauseTimer", call => Boxes.HoldTimer(call.Argument(0).Text, paused: true));
		_router.Register("PlayTimer", call => Boxes.HoldTimer(call.Argument(0).Text, paused: false));

		// `RemoveCounter` and `RemoveTimer` are ONE implementation (0x8251B1A8 under both names).
		_router.Register("RemoveCounter", call => Boxes.RemoveCounter(call.Argument(0).Text));
		_router.Register("RemoveTimer", call => Boxes.RemoveCounter(call.Argument(0).Text));
		_router.Observe("RemoveCounterIfFastTravelOrChangeLevel",
			"marks the counter for removal on the guest's next travel; the removal itself is the guest's");

		// ---- THE MENU BOX: `*(SM+0x34)`, stated by its own open, input and close ---------------
		//
		// sub_825FA220 opens it with the rows DisplayMenuBox filled (TextTag, Enabled), the title and
		// the cancelable flag; sub_825FA030 moves the selection on the guest's own pad handling;
		// sub_825FA420 closes it and posts MESSAGE_EVENT_MENUBOX (65) with the 1-based row or 0.
		_router.Register("@MenuBox.Open", MenuBoxOpened);
		_router.Register("@MenuBox.Select", call =>
		{
			// Re-shown with the new row: the model bumps its generation on a show, not on a cursor move.
			if (Boxes.Menu is { } menu)
			{
				Boxes.ShowMenuBox(menu.Text, menu.Options, menu.Cancelable);
				Boxes.Menu!.Highlight = (int)call.Argument(0).Number;
			}
		});
		_router.Register("@MenuBox.Close", _ => Boxes.CloseMenuBox());
		_router.Observe("DisplayMenuBox", "fills the menu box's rows; stated when it opens (@MenuBox.Open)");
		_router.Observe("DisplayUncancelableMenuBox", "fills the menu box's rows; stated when it opens (@MenuBox.Open)");

		// ---- STATED ELSEWHERE: drawn from the guest's own state, not from the call --------------
		//
		// Fable 3's info boxes are TOPBOX messages: `DisplayInfoBoxEx(name, seconds, tag, …)`
		// (0x82518168) builds sub_824B4840(msg, FNV1(name), "", seconds*1000) and posts it on
		// channel 3 of the TopBox manager `*(H+0x4C)`; `DisplayInfoBoxParams` with a non-zero
		// DisplayBoxStyle posts channel 1/2/3 by style. Fable3TopBox draws the manager's front
		// record (RefiiHostGetFable3TopBox) and the strings its publisher wrote.
		const string topBox = "a TopBox channel message — Fable3TopBox draws the manager's state (RefiiHostGetFable3TopBox)";
		foreach (string name in new[] { "DisplayInfoBox", "DisplayInfoBoxEx", "DisplayInfoBoxParams", "ShowTopBoxMessage", "TurnOffTopBox", "ClearTopBox", "SetTopBoxHidden", "HideInfoBox" })
		{
			_router.Observe(name, topBox);
		}

		// The fader `*(GUI+0x3C)`: the natives write its target and duration (sub_825D5700) and
		// the guest polls IsScreenFully* — progress stays the guest's; Engine_UiScreenFade draws
		// it from RefiiHostGetScreenFade.
		const string fade = "the fader's own state is drawn (RefiiHostGetScreenFade); the guest polls it, so it stays the guest's";
		foreach (string name in new[] { "FadeScreenIn", "FadeScreenOut", "FadeScreenOutToColour", "FadeScreenOutIfEntityIsVisible", "IsScreenFading", "IsScreenFullyFaded", "IsScreenFullyFadedIn", "IsScreenFadeLocked", "LockScreenFade" })
		{
			_router.Observe(name, fade);
		}

		// The HUD element registries at `*(*(H+0x24)+0x18)`: the writers publish by name hash and
		// hud_feed.cpp reads them in place (texts, meters) — the same channel the subtitler and
		// the TopBox publisher write through.
		const string registry = "a HUD registry write (sub_825E4028 / sub_825E4120 / meters) read in place by hud_feed.cpp";
		foreach (string name in new[] { "SetGUIText", "ToggleElement", "SetMeterRatioOverTime", "SetMeterPercentOverTime", "SetMeterPosition", "ResetMeterPosition", "FadeElementIn", "FadeElementOutFast", "FadeElementOutSlow" })
		{
			_router.Observe(name, registry);
		}

		// Movies: the Bink hooks state the open, rect and close (Fable3/src/hooks/src/movie_player.cpp).
		foreach (string name in new[] { "PlayMovieInternal", "PlaySilentMovieInternal", "PlayLocalisedMovieInternal", "PlayMovieWithGenderedVoiceInternal", "PlayLocalisedMovieWithGenderedVoiceInternal" })
		{
			_router.Observe(name, "the Bink player's open states the movie (movie_player.cpp)");
		}

		// The loading screen: level_feed.cpp states the screen, its fade and the art it picked.
		foreach (string name in new[] { "SetLoadingScreenBackgroundName", "SetLoadingScreenTextTagGroupName", "SetOpeningLoadingScreen" })
		{
			_router.Observe(name, "the loading screen object states itself (RefiiHostGetLoadingScreen / Art)");
		}
	}

	/// <summary>`SetTimerWithText` / `SetCountUpTimerWithText`: (name, seconds, tag).</summary>
	private void Timer(Engine_UiModel.GuiNativeCall call, bool countsUp)
	{
		Engine_UiGuiBoxes.Counter counter = Boxes.SetTimer(
			call.Argument(0).Text, call.Argument(2).Text, call.Argument(1).Number, countsUp);
		counter.Text = Engine_UiGuiText.Resolve(counter.Tag, Fable3Strings.Current);
	}

	/// <summary>The boxes on screen, by the guest's box address, so a close drops the right one.</summary>
	private readonly Dictionary<uint, Engine_UiGuiBoxes.Box> _shown = new();

	/// <summary>
	/// `@Box.Open(queue, box, kind, name, text, …)`: question boxes carry their yes/no tags, the
	/// adjuster its min/max/increment/value, an info card its title, accept and help tags and the
	/// two button flags. Every box stays until `@Box.Close` — the lifetime is the guest's.
	/// </summary>
	private void BoxOpened(Engine_UiModel.GuiNativeCall call)
	{
		uint address = Parse(call.Argument(1).Text);
		string kind = call.Argument(2).Text;
		string name = call.Argument(3).Text is { Length: > 0 } stated ? stated : UnnamedBox;
		string text = call.Argument(4).Text;
		BoxClosed(address);

		Engine_UiGuiBoxes.Box box;
		switch (kind)
		{
			case "question":
				box = Boxes.Question(name, text);
				if (call.Argument(5).Text.Length > 0 || call.Argument(6).Text.Length > 0)
				{
					box.Buttons = new Engine_UiGuiBoxes.BoxButtons(true, false, false, true, false, call.Argument(5).Text, call.Argument(6).Text);
				}
				break;
			case "adjuster":
				box = Boxes.Adjuster(text, (int)call.Argument(5).Number, (int)call.Argument(6).Number,
					(int)call.Argument(7).Number, (int)call.Argument(8).Number, 0, false);
				break;
			case "quest":
				box = Boxes.QuestBox(name, text, 0, 1);
				break;
			case "card":
				box = Boxes.InfoBoxParams(name, Engine_UiGuiText.Resolve(call.Argument(5).Text, Fable3Strings.Current), -1f, text, 1,
					new Engine_UiGuiBoxes.BoxButtons(call.Argument(8).Boolean, false, false, call.Argument(9).Boolean, false,
						call.Argument(6).Text, call.Argument(7).Text));
				break;
			default:
				box = Boxes.MessageBoxNamed(name, text);
				break;
		}

		if (address != 0u)
		{
			_shown[address] = box;
		}
	}

	private void BoxClosed(uint address)
	{
		if (address != 0u && _shown.Remove(address, out Engine_UiGuiBoxes.Box? box))
		{
			Boxes.Remove(box);
		}
	}

	/// <summary>`@MenuBox.Open(title, cancelable, selected, row…)`: a row is its TextTag, prefixed `+` or `-` for enabled.</summary>
	private void MenuBoxOpened(Engine_UiModel.GuiNativeCall call)
	{
		var options = new List<Engine_UiGuiBoxes.MenuOption>();
		if (call.Arguments is { Count: > 3 } arguments)
		{
			for (int i = 3; i < arguments.Count; ++i)
			{
				string row = arguments[i].Text;
				if (row.Length == 0)
				{
					continue;
				}

				options.Add(new Engine_UiGuiBoxes.MenuOption(
					Engine_UiGuiText.Resolve(row[1..], Fable3Strings.Current), row[0] == '+'));
			}
		}

		Boxes.ShowMenuBox(Engine_UiGuiText.Resolve(call.Argument(0).Text, Fable3Strings.Current), options, call.Argument(1).Boolean);
		Boxes.Menu!.Highlight = (int)call.Argument(2).Number;
	}

	private static uint Parse(string hex) =>
		uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint value) ? value : 0u;
}
