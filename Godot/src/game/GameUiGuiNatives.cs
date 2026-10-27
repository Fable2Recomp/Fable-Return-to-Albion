using Godot;
using Refii.Core;
using Refii.Ui;

namespace Refii.Game;

/// <summary>
/// The routing table for the UI cut: what Godot does with each of the guest's 120 `GUI.*`
/// natives.
/// </summary>
/// <remarks>
/// The cut is at the semantic natives the game VM calls, not at the Gameface DOM below them.
/// Every call arrives here with its arguments (docs/ui_native_cut.md) and is answered one of
/// three ways:
///
/// <list type="bullet">
/// <item><c>Register</c> — Godot owns it and draws the screen it affects.</item>
/// <item><c>Observe</c> — deliberately not owned, with the reason; the census counts these
/// apart from the unrouted.</item>
/// <item>Neither — the six natives whose implementation is a bare `blr`. They cannot arrive:
/// interception keys on the implementation pointer, all six share one, and the payload refuses
/// to record a stub.</item>
/// </list>
///
/// Taking a call does not suppress it: the guest's implementation still runs, so predicates
/// keep answering out of guest state, and the guest's own copy of every widget is still built.
///
/// Suppression, when a family is complete, must NOT repoint an implementation at `nullsub_1`.
/// The shared thunk hands the implementation `L` in r3 and returns whatever comes back as the
/// LUA RESULT COUNT (`82227d44 mr r3, r5`; `82227d50 bctrl`; r3 untouched to `blr`), so a bare
/// `blr` returns a pointer as a result count. Hook the THUNK instead (`sub_82227D28` for shape
/// (a), the per-arity thunks for shape (b)) and set `r3 = 0` — right for the 100 of 120 that
/// push no value.
///
/// The unit of suppression is a FAMILY, not a native: fifteen predicates read state the setters
/// beside them write, so a suppressed setter freezes its predicate. Owning the family is
/// necessary but not sufficient — the side effects must be inert too. Opening any display box
/// pushes a GameFlow command that suspends gameplay, the minigame tally opens a screen and runs
/// `GUI:OnEnter()`, the golf meter plays sounds, and the expression pip posts an event when a
/// second player is armed.
/// </remarks>
public partial class GameUi
{
	/// <summary>
	/// The routing table — which `GUI.*` natives Godot answers, and which it deliberately does
	/// not.
	/// </summary>
	/// <remarks>
	/// Built lazily because the surfaces it routes into are built lazily; registering against a
	/// null HUD would capture null in a closure and never recover. Argument orders and key
	/// hashes here are decompiled facts — see docs/ui_native_cut.md.
	/// </remarks>
	private void EnsureGuiNativeRoutes()
	{
		if (_guiRoutesReady || _hud is null)
		{
			return;
		}

		_guiRoutesReady = true;

		// The fade family is deliberately not routed: the host draws the pixels from the guest's
		// own state object at `*(GUI+64)` — `+0` progress `t`, `+4` duration in seconds,
		// `+8..11` from-colour, `+12..15` to-colour, `+0x21` lock byte, `+0x28` draw-quad
		// handle. The on-screen colour is never stored; `sub_822C7970` lerps it per read.
		// `sub_822C1FC0` advances `t` from the 15 Hz game tick by a fixed `1/(15 * duration)`.
		//
		// Four guest consumers hard-gate on it, two of which deadlock rather than glitch:
		//   `CmdLoadSaveGame_Update` (0x823D7778) stays in state 1 while it fades and does NOT
		//                            check the lock — a `t` that stops never completes.
		//   `CmdLevelChange_Update`  (0x823D5AA8) gates twice but honours the lock;
		//                            `LockScreenFade` is the shipped mid-fade escape hatch.
		//   `sub_8230E788`           skips its entire body until `IsScreenFullyFadedIn`.
		//   `sub_82311EE8`           routes the signed-out warning differently.
		//
		// Shipped bug not to reproduce: `FadeScreenOutToColour(r, g, b, seconds)` passes only
		// three colour bytes; the fourth (alpha) is whatever the caller's stack slot held.
		const string fade = "OWN THE PIXELS, NOT THE STATE — four guest consumers HARD-GATE on " +
			"the fade object at *(GUI+64), and two of them deadlock rather than glitch";
		foreach (string name in new[]
		{
			"FadeScreenIn", "FadeScreenOut", "FadeScreenOutToColour",
			"IsScreenFading", "IsScreenFullyFaded", "IsScreenFullyFadedIn",
			"IsScreenFadeLocked", "LockScreenFade", "UnlockScreenFade",
		})
		{
			_guiNatives.Observe(name, fade);
		}

		// `SetGUIText(element, tag)` — arg 1 is an element name, arg 2 a Babel tag; only the
		// second is localised. The key is FNV-1 of the RAW element name, into the map at
		// `elemMgr + 28`.
		_guiNatives.Register("SetGUIText", call =>
		{
			string element = call.Argument(0).Text;
			if (element.Length == 0)
			{
				return;
			}

			string display = Engine_UiGuiText.Resolve(call.Argument(1).Text, _presentation?.Strings);
			_hud.Values.StateText(Engine_GameHash.Fnv1(element), display);
		});

		// `ToggleElement(element, visible)` — same key rule, no Babel path on either argument.
		// A missing second argument reads false, the guest's own convention.
		_guiNatives.Register("ToggleElement", call =>
		{
			string element = call.Argument(0).Text;
			if (element.Length != 0)
			{
				_hud.Values.StateVisible(Engine_GameHash.Fnv1(element), call.Argument(1).Boolean);
			}
		});

		RouteMeters();
		RouteDisplayBoxes();
		RouteTimers();
		RoutePredicates();
		RouteNotYetOwned();
	}

	/// <summary>The two meter natives — see the section note.</summary>
	private void RouteMeters()
	{
		// ---- THE METERS ---------------------------------------------------------------------
		//
		// Most of the HUD's meters travel on this channel, not on the registry `GetHudMeters`
		// polls: only `PlayerHealth` and `PlayerHealthMax` persist there through gameplay.
		//
		// One implementation, two units: `SetMeterPercentOverTime` (`sub_823069A8`) takes an
		// integer percent and tail-calls the ratio form with `percent * 0.01`. Both are
		// converted to the 0..100 the rest of the HUD speaks, on the way in.
		//
		// Key is `Fnv1(rawElementName)` (basis 0x811C9DC5, `sub_82306928`), into the store at
		// `*(*(GUI+32)+108)` — the same key `SetGUIText` and `ToggleElement` use.
		// Argument 3 is the seek time in seconds.
		_guiNatives.Register("SetMeterRatioOverTime", call =>
		{
			string element = call.Argument(0).Text;
			if (element.Length != 0)
			{
				_hud?.Values.StateMeter(
					Engine_GameHash.Fnv1(element), call.Argument(1).Number * 100f,
					call.Argument(2).Number);
			}
		});

		_guiNatives.Register("SetMeterPercentOverTime", call =>
		{
			string element = call.Argument(0).Text;
			if (element.Length != 0)
			{
				_hud?.Values.StateMeter(
					Engine_GameHash.Fnv1(element), call.Argument(1).Number,
					call.Argument(2).Number);
			}
		});

	}

	/// <summary>Boxes, counters and the two table natives.</summary>
	private void RouteDisplayBoxes()
	{
		// ---- THE DISPLAY BOXES -------------------------------------------------------------
		//
		// Each takes a tag plus substitutions: `sub_82302270` resolves the tag, then walks the
		// remaining stack slots as `%1`, `%2`, … — numbers integer-formatted, strings resolved
		// as tags in their own right. `Engine_UiGuiText` implements that rule.
		_guiNatives.Register("DisplayMessageBox", call =>
			_boxes.MessageBox(Localise(call, 0)));

		// The MessageBox CLASS stating itself, not a native: its Open and Close are hooked
		// (hooks/display_box_hooks.cpp) because most boxes of the class are built natively and
		// never pass a `GUI.*` call. Argument 0 is the box's address, argument 1 its identity.
		_guiNatives.Register("@MessageBox.Open", call =>
		{
			if (GuestAddress(call) is { } box)
			{
				_boxes.GuestMessageOpened(box, call.Argument(1).Text, call.Argument(2).Text ?? string.Empty);
			}
		});

		// The registry slide writers (gui_registry_hooks.cpp): registry index, name hash, slide,
		// and whether the write played it.
		_guiNatives.Register("@Gui.Slide", call =>
		{
			if (int.TryParse(call.Argument(0).Text, out int registry) &&
				uint.TryParse(call.Argument(1).Text, out uint hash) &&
				int.TryParse(call.Argument(2).Text, out int slide))
			{
				_hud.Values.StateRegistrySlide(registry, hash, slide, call.Argument(3).Text == "1");
			}
		});

		_guiNatives.Register("@Gui.Image", call =>
		{
			if (uint.TryParse(call.Argument(0).Text, out uint hash) &&
				uint.TryParse(call.Argument(1).Text, out uint texture))
			{
				_hud.Values.StateRegistryImage(hash, texture);
			}
		});

		// The modal router's opener stating its box: kind, modal id, text (display_box_hooks.cpp).
		_guiNatives.Register("@Modal.Open", call =>
		{
			if (uint.TryParse(call.Argument(0).Text, out uint kind) &&
				uint.TryParse(call.Argument(1).Text, out uint id))
			{
				Engine_UiModalText.Stated(kind, id, call.Argument(2).Text ?? string.Empty);
				if (kind == 5u && call.Arguments.Count >= 8 &&
					int.TryParse(call.Argument(3).Text, out int value) &&
					int.TryParse(call.Argument(4).Text, out int min) &&
					int.TryParse(call.Argument(5).Text, out int max) &&
					int.TryParse(call.Argument(6).Text, out int step) &&
					int.TryParse(call.Argument(7).Text, out int flags))
				{
					Engine_UiModalText.StatedAdjuster(id, value, min, max, step, flags);
				}
			}
		});

		_guiNatives.Register("@MessageBox.Close", call =>
		{
			if (GuestAddress(call) is { } box)
			{
				_boxes.GuestMessageClosed(box, call.Argument(1).Text);
			}
		});

		// A box the core co-op Lua built for one player on split screen (display_box_hooks.cpp):
		// argument 0 the box's address, argument 1 the player. It stands in their half, on their pad.
		_guiNatives.Register("@Box.Seat", call =>
		{
			if (GuestAddress(call) is { } box && int.TryParse(call.Argument(1).Text, out int player) &&
				player is 1 or 2)
			{
				_boxes.SeatBox(box, player);
			}
		});

		// The aim reticule's setter stating which object a player aims with — not a native
		// (hooks/reticule_hooks.cpp). Arguments: player 0/1, the object's address, active. The
		// record's own active byte decides what is drawn; the object is kept for its aim point.
		_guiNatives.Register("@Reticule", call =>
		{
			if (call.Argument(0).Text is "0" or "1" &&
				uint.TryParse(call.Argument(1).Text, System.Globalization.NumberStyles.HexNumber,
					System.Globalization.CultureInfo.InvariantCulture, out uint aiming))
			{
				_reticuleObject[call.Argument(0).Text == "0" ? 0 : 1] = aiming;
			}
		});

		// `DisplayInfoBox` is the odd one: its id is a LITERAL 0, and no string hashes to 0, so
		// it is unreachable from RemoveDisplayBox by construction. Six seconds, fire and forget.
		_guiNatives.Register("DisplayInfoBox", call =>
			_boxes.InfoBox(Localise(call, 0)));

		// `DisplayInfoBoxEx(name, seconds, tag, …)` — the removable one. Arg 1 is an identity,
		// not a tag, and must not be localised; arg 2 is seconds (the guest stores ms).
		_guiNatives.Register("DisplayInfoBoxEx", call =>
			_boxes.InfoBoxEx(
				call.Argument(0).Text, call.Argument(1).Number, Localise(call, 2)));

		_guiNatives.Register("RemoveDisplayBox", call =>
			_boxes.Remove(call.Argument(0).Text));

		// `SetCounter(name, tag, %1, %2)` — arg 1 is the identity. A repeat call with the same
		// name updates in place and keeps the widget, as the guest's pool walk does. The values
		// are truncated, not rounded — the guest applies `fctiwz`.
		_guiNatives.Register("SetCounter", call =>
		{
			string name = call.Argument(0).Text;
			Engine_UiGuiBoxes.Counter counter = _boxes.SetCounter(
				name, call.Argument(1).Text,
				(int)call.Argument(2).Number, (int)call.Argument(3).Number);

			counter.Text = Engine_UiGuiText.Substitute(
				Engine_UiGuiText.Resolve(counter.Tag, _presentation?.Strings),
				new[] { counter.First.ToString(), counter.Second.ToString() });
		});

		// `RemoveCounter` and `RemoveTimer` are the SAME guest function (`sub_82305918`
		// registered under both names), so they share one pool and one namespace here too.
		_guiNatives.Register("RemoveCounter", call => _boxes.RemoveCounter(call.Argument(0).Text));
		_guiNatives.Register("RemoveTimer", call => _boxes.RemoveCounter(call.Argument(0).Text));

		// `DisplayReceivedItem(entity)` takes a userdata — a two-level entity handle — not a
		// value readable off the stack. Screen 8 is already owned by
		// Engine_UiReceiveItemsSurface, which reads the item from the screen the guest fills in.
		_guiNatives.Observe("DisplayReceivedItem",
			"its argument is an entity userdata, not a value; screen 8 is already owned by " +
			"Engine_UiReceiveItemsSurface");

		// ---- THE TWO TABLE NATIVES ---------------------------------------------------------
		//
		// `DisplayInfoBoxParams(table, tag, …)` reads eleven fields out of a Lua table through
		// `lua_getfield` (payload ABI 84). Arg 2 is the tag, not arg 1 — the guest's gate is
		// `(top - base) & ~7 < 0x10` and both branches read index 2 — so substitutions start
		// at arg 3.
		//
		// The guest's defaults are not uniform: `ShowAButton`/`ShowYButton` default TRUE while
		// every other boolean defaults false; `LifeTime` defaults to -1.0 (persistent); `Name`
		// to the literal "unnamed"; `AcceptText`/`HelpText` to the tags `GUI_DIALOG_ACCEPT` and
		// `GUI_DIALOG_HELP`.
		_guiNatives.Register("DisplayInfoBoxParams", call =>
		{
			Engine_UiModel.GuiNativeArgument table = call.Argument(0);
			string Tag(string field, string fallback) => Engine_UiGuiText.Resolve(
				table.FieldText(field, fallback), _presentation?.Strings);

			_boxes.InfoBoxParams(
				table.FieldText("Name", Engine_UiGuiBoxes.ParamsDefaultName),
				table.Fields is null ? string.Empty : Tag("Title", string.Empty),
				table.FieldNumber("LifeTime", -1f),
				Localise(call, 1),
				(int)table.FieldNumber("DisplayBoxStyle", 0f),
				new Engine_UiGuiBoxes.BoxButtons(
					ShowA: table.FieldBoolean("ShowAButton", true),
					HoldA: table.FieldBoolean("IsHoldAButton", false),
					HoldAReduceDialogue:
						table.FieldBoolean("IsHoldAButtonReduceDialogue", false),
					ShowY: table.FieldBoolean("ShowYButton", true),
					HoldY: table.FieldBoolean("IsHoldYButton", false),
					AcceptText: Tag("AcceptText", "GUI_DIALOG_ACCEPT"),
					HelpText: Tag("HelpText", "GUI_DIALOG_HELP")));
		});

		// `DisplayMenuBox(body, option…)` — a body plus one to five options, each either a
		// string or a table carrying `TextTag` and `Enabled`.
		//
		// The body is resolved WITHOUT substitution, unlike every other box here: the trailing
		// arguments are options, not `%N` values.
		//
		// A plain string option is enabled. The table form calls
		// `sub_823DBBD0(box, index, Enabled, 0)` with `Enabled` defaulting to false; the string
		// form calls only `sub_823DBA40` and never touches the flag.
		void MenuBox(Engine_UiModel.GuiNativeCall call, bool cancelable)
		{
			var options = new System.Collections.Generic.List<Engine_UiGuiBoxes.MenuOption>();
			if (call.Arguments is { Count: > 1 } arguments)
			{
				for (int i = 1; i < arguments.Count; ++i)
				{
					Engine_UiModel.GuiNativeArgument option = arguments[i];
					if (option.Kind == Engine_UiModel.GuiNativeArgumentKind.Table)
					{
						options.Add(new Engine_UiGuiBoxes.MenuOption(
							Engine_UiGuiText.Resolve(
								option.FieldText("TextTag", string.Empty),
								_presentation?.Strings),
							option.FieldBoolean("Enabled", false)));
						continue;
					}

					if (option.Kind != Engine_UiModel.GuiNativeArgumentKind.String)
					{
						continue;
					}

					options.Add(new Engine_UiGuiBoxes.MenuOption(
						Engine_UiGuiText.Resolve(option.Text, _presentation?.Strings), true));
				}
			}

			// The guest returns before building the box outside 2..6 arguments.
			if (options.Count == 0 || options.Count > 5)
			{
				return;
			}

			_boxes.ShowMenuBox(
				Engine_UiGuiText.Resolve(call.Argument(0).Text, _presentation?.Strings),
				options, cancelable);
		}

		_guiNatives.Register("DisplayMenuBox", call => MenuBox(call, cancelable: true));
		_guiNatives.Register("DisplayUncancelableMenuBox",
			call => MenuBox(call, cancelable: false));

	}

	/// <summary>Timers, which share the counter pool.</summary>
	private void RouteTimers()
	{
		// ---- THE MOVIES ---------------------------------------------------------------------
		//
		// Five natives, one implementation: all are thunks into
		// `sub_823C0D90(player, name, gendered, localised, sound)` differing in three `li`
		// immediates. They play the SAME `.bik` and choose a different audio track within it —
		// 1 male / 2 female for en-uk, 3 and 4 for any other language with a mounted `videos`
		// package. Worth taking even though the host already plays by name: `player+8` is
		// written synchronously inside the Lua call, before playback waits on a fade and a
		// gameflow push, and the flags carry the track, which no filename can.
		//
		// Playback is deliberately NOT taken over: the guest's movie state machine advances
		// 3->4 off the Bink "finished" byte (`823c18d0 lbz r11, 0xDD`), so a host owning
		// playback would leave gameflow state 10 pushed and `IsMoviePlaying` true for ever.
		void Movie(Engine_UiModel.GuiNativeCall call, bool gendered, bool localised, bool sound)
		{
			string name = call.Argument(0).Text;
			if (name.Length == 0)
			{
				return;
			}

			Engine_UiGuiMovie.Request request = _movies.State(name, gendered, localised, sound);
			GD.Print($"[game-ui] the guest asked for a movie: {_movies.Describe()} " +
				$"(request {_movies.Asked})");
			_ = request;
		}

		_guiNatives.Register("PlayMovieInternal",
			call => Movie(call, gendered: false, localised: false, sound: true));
		_guiNatives.Register("PlaySilentMovieInternal",
			call => Movie(call, gendered: false, localised: false, sound: false));
		_guiNatives.Register("PlayLocalisedMovieInternal",
			call => Movie(call, gendered: false, localised: true, sound: true));
		_guiNatives.Register("PlayMovieWithGenderedVoiceInternal",
			call => Movie(call, gendered: true, localised: false, sound: true));
		_guiNatives.Register("PlayLocalisedMovieWithGenderedVoiceInternal",
			call => Movie(call, gendered: true, localised: true, sound: true));

		// `SetDPadCenter(bool)` does two things: it stores the bool at `dpadObject+0x22`, whose
		// one reader (`sub_8299EC60` at 0x8299ecd0) nudges a `Group.Text` element's Y and is
		// inert once Godot draws the ring; and it fires a Gameface logic event on the
		// `UIContract` presentation — `setCenterOn`/`setCenterOff`, `dword_83497B78` /
		// `dword_83497B7C`, hashes initialised at 0x8324CF9C — which is the visible effect.
		_guiNatives.Register("SetDPadCenter", call =>
			_hud?.Values.StateDpadCentered(call.Argument(0).Boolean));

		// ---- THE TIMERS ---------------------------------------------------------------------
		//
		// One pool with the counters, because the guest has one: `RemoveTimer` and
		// `RemoveCounter` are the same function (`sub_82305918`) under two names.
		//
		// `SetTimer(name, seconds)` carries no text — `sub_82309540` marshals exactly
		// (string, number) — so the widget draws the clock alone.
		_guiNatives.Register("SetTimer", call =>
			_boxes.SetTimer(call.Argument(0).Text, string.Empty,
				call.Argument(1).Number, countsUp: false));

		_guiNatives.Register("PauseTimer", call =>
			_boxes.HoldTimer(call.Argument(0).Text, paused: true));

		_guiNatives.Register("PlayTimer", call =>
			_boxes.HoldTimer(call.Argument(0).Text, paused: false));

		// Both text forms are `(name, seconds, tag)`, shape (a) — two-instruction thunks into one
		// body differing by a literal: 0 for `SetTimerWithText` (count down), 1 for
		// `SetCountUpTimerWithText` (count up). Arg 1 is an identity, hashed through
		// `Fnv1HashWithBasis(.., 0x811C9DC5)` at 0x82305868; arg 3 is a Babel tag, copied
		// verbatim and resolved at display time with `%1`/`%2` both fixed at 0. `SetTimer` is
		// the same function with an empty tag.
		_guiNatives.Register("SetTimerWithText", call =>
			_boxes.SetTimer(call.Argument(0).Text, call.Argument(2).Text,
				call.Argument(1).Number, countsUp: false));

		_guiNatives.Register("SetCountUpTimerWithText", call =>
			_boxes.SetTimer(call.Argument(0).Text, call.Argument(2).Text,
				call.Argument(1).Number, countsUp: true));

	}

	/// <summary>
	/// Would answer the predicates whose setters this file owns, instead of the guest.
	/// Nothing is suppressed or answered today.
	/// </summary>
	private void PublishPredicates()
	{
		Engine_UiSource source = Engine_UiSource.Current;

		// The rule: own the SETTERS and the SURFACE before answering a predicate. The
		// expression / job-fill family has no surface yet, so it runs in the guest and its
		// writes reach the HUD through the element channels. `_widgets` is the translation of
		// record.
		_ = source;
	}

	/// <summary>The 20 natives that return a value.</summary>
	private void RoutePredicates()
	{
		// ---- THE HEADLINE, THE TOP BOX AND THE JOB MULTIPLIER --------------------------------
		//
		// `DisplayHeadlineText(tag, seconds, mode)` — arg 3 is a boolean (`lua_toboolean`).
		// One headline at a fixed slot; no pool, no key. An unresolved tag puts up NOTHING
		// (the guest clears the duration and mode byte again), unlike the box family, which
		// falls back to showing the tag.
		_guiNatives.Register("DisplayHeadlineText", call =>
		{
			string tag = call.Argument(0).Text;
			string display = Engine_UiGuiText.Resolve(tag, _presentation?.Strings);

			// Resolve falls back to the literal, so a tag returning itself is a miss.
			bool resolved = display.Length != 0 &&
				!string.Equals(display, tag, System.StringComparison.Ordinal);
			_boxes.ShowHeadline(resolved ? display : string.Empty,
				call.Argument(1).Number, call.Argument(2).Boolean);
		});

		_guiNatives.Register("ClearHeadlineText", _ => _boxes.ClearHeadline());

		// `SetTopBoxHidden(hidden)` — Lua-truthy (`lua_toboolean`), not an int: the number 0
		// hides the box, because 0 is true in Lua.
		_guiNatives.Register("SetTopBoxHidden", call =>
			_boxes.SetTopBoxHidden(call.Argument(0).Boolean));

		// `SetJobMultiplierLevel(value)` — the value, not an index: printed with `%d` onto
		// `JobMultiplier.Group.Text` and stored verbatim. Which slide plays depends on how it
		// moved against the cached one — see the model.
		_guiNatives.Register("SetJobMultiplierLevel", call =>
			_boxes.SetJobMultiplier((int)call.Argument(0).Number));

		_guiNatives.Register("TurnJobMulitplierOff", call =>
			_boxes.TurnJobMultiplierOff(call.Argument(0).Boolean));

		// `SetShouldDrawPlayerIcons(bool)` suppresses exactly two icons: the offscreen hero and
		// henchman markers (request types 52 and 53), the only records in the vector at
		// `elemMgr+100`. Nothing else in the world-icon manager reads the flag.
		_guiNatives.Register("SetShouldDrawPlayerIcons", call =>
			_hud?.Values.StatePlayerIconsDrawn(call.Argument(0).Boolean));

		// ---- THE EXPRESSION METER ------------------------------------------------------------
		//
		// OBSERVED, NOT OWNED. The meter is the guest's: `StartExpressionMeterPip` and the fill
		// starters write the object at `*(*(GUI+32)+44)`, the GUI tick advances the pip
		// (`sub_822ACBF8`), the guest's own A press judges it (`sub_823D21C0`) and the four
		// predicates answer from it. A host copy here judged nothing and answered nothing; the
		// HUD draws the object instead — RefiiHostGetExpressionMeter and
		// Engine_UiHudExpressionMeter.
		foreach (string meterNative in new[]
			{
				"TurnOnExpressionMeter", "TurnOffExpressionMeter", "StartExpressionMeterPip",
				"StartBarJobFill", "TurnOnExpressionStaticFill", "TurnOnExpressionDynamicFill",
				"SetLowFillColour", "SetMedFillColour", "SetHighFillColour",
				"SetFillColourPercentages",
			})
		{
			_guiNatives.Observe(meterNative,
				"the meter object is the guest's and is read back for drawing");
		}

		// ---- THE GOLF SWING METER ------------------------------------------------------------
		//
		// Six table keys, all angles: the names say pixels and percent, but the guest scales
		// them into a 1.1707964-radian span before storing. The defaults below are the guest's,
		// taken when a key is missing, nil, or the wrong type — no coercion on this path.
		_guiNatives.Register("DisplayGolfSwingMeter", call =>
		{
			Engine_UiModel.GuiNativeArgument table = call.Argument(0);
			_widgets.ConfigureGolf(
				table.FieldNumber("BarSizePixels", 1.0f),
				table.FieldNumber("SweetSpotMinSizePercentage", 0.02f),
				table.FieldNumber("SweetSpotInitialSizePercentage", 0.02f),
				table.FieldNumber("SweetSpotShrinkPixelsPerSecond", 0.05f),
				table.FieldNumber("SweetSpotMovementPixelsPerSecond", 1.0f),
				table.FieldBoolean("IsPressToClose", false));
		});

		_guiNatives.Register("CloseGolfSwingMeter", _ => _widgets.CloseGolf());

		// Golf is not suppressed: with `IsPressToClose` false (the common case) `Display` only
		// CONFIGURES, and the meter is opened later by the guest's own input hooks
		// (`sub_82405538` on press, `sub_82403DD0` on release), which the host has no route to.
		// Its sounds are reproduced host-side in Engine_UiGuiWidgets.

		// ---- THE MINIGAME TALLY --------------------------------------------------------------
		//
		// "Add" means add a ROW, not add to a total: the amount is assigned to the row this call
		// claims. Five rows; a sixth is dropped silently.
		_guiNatives.Register("TurnOnMiniGameComplete", call =>
		{
			_widgets.TurnOnMiniGame();
			_screens.StateText("MiniGameTitle", Localise(call, 0));
			_screens.StateText("MiniGameButton",
				Engine_UiGuiText.Resolve(call.Argument(1).Text, _presentation?.Strings));
		});

		_guiNatives.Register("TurnOffMiniGameComplete", _ => _widgets.TurnOffMiniGame());

		_guiNatives.Register("AddAmountToMiniGameComplete", call =>
			_widgets.AddMiniGameAmount(
				Engine_UiGuiText.Resolve(call.Argument(0).Text, _presentation?.Strings),
				(int)call.Argument(1).Number));

		// ---- THE EPILOGUE, THE NEXT EPISODE AND THE LOADING TIPS -----------------------------
		//
		// `SetGoldRewardInEpilogueScreen(gold)` writes TWO elements at once, and the label
		// element is called `EpilogueRenown` while the tag it carries is GOLD. A zero blanks
		// both rather than printing "0".
		_guiNatives.Register("SetGoldRewardInEpilogueScreen", call =>
			_screens.SetEpilogueGold((int)call.Argument(0).Number,
				Engine_UiGuiText.Resolve("GUI_QUEST_COMPLETION_GOLD", _presentation?.Strings)));

		// The tag is resolved AT CALL TIME, not at draw — what the screen stores is already
		// localised text. Two slots; the third and later overwrite the second.
		_guiNatives.Register("AddItemRewardToEpilogueScreen", call =>
			_screens.AddEpilogueReward(
				Engine_UiGuiText.Resolve(call.Argument(0).Text, _presentation?.Strings)));

		// `TurnOnNextEpisode` — EIGHT arguments. 1..5 are Babel tags, 6 is a RESOURCE identity
		// (lowercased and resource-hashed, never Babel-resolved), 7 is ignored outright, and 8
		// is a number the screen keeps. Argument 5 doubles as the offer selector: the guest
		// compares it against the four `GUI_SCREEN_EPISODE_BUY_EPISODE_*` tags.
		_guiNatives.Register("TurnOnNextEpisode", call =>
		{
			string Tag(int index) =>
				Engine_UiGuiText.Resolve(call.Argument(index).Text, _presentation?.Strings);

			_screens.ShowEpisode(
				Tag(0), Tag(1), Tag(2), Tag(3), Tag(4),
				call.Argument(4).Text, call.Argument(5).Text,
				Engine_UiGuiText.Resolve("GUI_SCREEN_EPISODE_DONT_BUY", _presentation?.Strings));
		});

		_guiNatives.Register("TurnOffNextEpisode", _ => _screens.HideEpisode());

		// A tag GROUP name, not a tag — its members become the rotating tips.
		_guiNatives.Register("SetLoadingScreenTextTagGroupName", call =>
			_screens.SetLoadingTipGroup(call.Argument(0).Text));

		// ---- THE REMAINING BOXES AND TOASTS --------------------------------------------------
		//
		// `DisplayIntermissionScreen(tag, ...)` — six seconds hardcoded, then the usual 0.75 s
		// close fade. The `WithLifetime` form's leading number never reaches the local box: it
		// goes only into a co-op network message, so the two behave identically here.
		_guiNatives.Register("DisplayIntermissionScreen", call =>
			_boxes.InfoBoxEx(string.Empty, Engine_UiGuiBoxes.InfoBoxSeconds, Localise(call, 0)));

		_guiNatives.Register("DisplayIntermissionScreenWithLifetime", call =>
			_boxes.InfoBoxEx(string.Empty, Engine_UiGuiBoxes.InfoBoxSeconds, Localise(call, 1)));

		// `DisplayDownloadMessageBox(tag, buttonCaption, offerId)` — three strings; only the
		// first is localised, the caption is verbatim, the third is a decimal offer id. The
		// box is owned; the marketplace purchase is stated, not performed.
		_guiNatives.Register("DisplayDownloadMessageBox", call =>
		{
			Engine_UiGuiBoxes.Box box = _boxes.MessageBoxNamed(
				"DownloadMsgBox", Engine_UiGuiText.Resolve(
					call.Argument(0).Text, _presentation?.Strings));
			box.AcceptCaption = call.Argument(1).Text;
			box.MarketplaceOffer = call.Argument(2).Text;
		});

		// `DisplayReceivedMoney(amount)` / `DisplayReceivedDiscount(percent)` — one toast, two
		// differences: the discount displays `100 - argument`, and the money line is hardcoded
		// and unlocalised ("+N Gold") while the discount goes through Babel.
		_guiNatives.Register("DisplayReceivedMoney", call =>
			_boxes.InfoBoxEx("ReceivedMoney", Engine_UiGuiBoxes.InfoBoxSeconds,
				$"+{(int)call.Argument(0).Number} Gold"));

		_guiNatives.Register("DisplayReceivedDiscount", call =>
			_boxes.InfoBoxEx("ReceivedDiscount", Engine_UiGuiBoxes.InfoBoxSeconds,
				Engine_UiGuiText.Substitute(
					Engine_UiGuiText.Resolve("INV_ITEM_DISCOUNT", _presentation?.Strings),
					new[] { (100 - (int)call.Argument(0).Number).ToString() })));

		// `DisplayCornerBoxTradeItemInfo(name, ...)` is not a box: it publishes three corner-text
		// records keyed by the name and two suffixed variants. Persistent — no lifetime float,
		// so only its Remove takes it down.
		_guiNatives.Register("DisplayCornerBoxTradeItemInfo", call =>
		{
			string name = call.Argument(0).Text;
			if (name.Length == 0)
			{
				return;
			}

			_screens.StateText(name, Engine_UiGuiText.Resolve(
				call.Argument(1).Text, _presentation?.Strings));
			_screens.StateText(name + "TRADE_ITEM_TYPE_TITLE", Engine_UiGuiText.Resolve(
				"TEXT_DLC1_QUEST_QO750_TRADE_ITEM_TYPE", _presentation?.Strings));
			_screens.StateText(name + "ITEMS_OWNED_AND_NEEDED_COUNTER", Engine_UiGuiText.Resolve(
				"TEXT_DLC1_QUEST_QO750_TRADE_ITEM_NEEDED", _presentation?.Strings));
		});

		_guiNatives.Register("RemoveCornerBoxTradeItemInfo", call =>
		{
			string name = call.Argument(0).Text;
			if (name.Length != 0)
			{
				_screens.StateText(name, string.Empty);
				_screens.StateText(name + "TRADE_ITEM_TYPE_TITLE", string.Empty);
				_screens.StateText(name + "ITEMS_OWNED_AND_NEEDED_COUNTER", string.Empty);
			}
		});

		// `PlaySound(name)` — a sound by name, which is the whole contract.
		_guiNatives.Register("PlaySound", call => _screens.PlaySound(call.Argument(0).Text));

		// ---- THE PREDICATES -----------------------------------------------------------------
		//
		// Only 20 of the 120 push a value, and none is a `Display*` — so swallowing a `Display*`
		// cannot change script control flow.
		//
		// The first five answer from GAME state (the gameflow stack, a gameplay global, the
		// camera frustum), which no UI decision touches.
		const string gameState =
			"a predicate answered from GAME state (the gameflow stack, a gameplay global, or the " +
			"camera frustum) — nothing the host does can change its answer";

		// All three read the gameflow stack's top state id, through
		// `*(*(*(0x83496AB8+12)+0x80)+4)` and its vtable slot +4. Ids 7 = full cutscene,
		// 9 and 10 = movie, 1 and 8 = the pair the fade's hold gate also tests. They TRAP on an
		// empty gameflow stack rather than answering false.
		_guiNatives.Observe("IsFullCutscenePlaying", gameState + " (gameflow id 7)");
		_guiNatives.Observe("IsMoviePlaying", gameState + " (gameflow id 9 or 10)");
		_guiNatives.Observe("IsUIActive",
			"gameflow id 1 or 8, OR IsMoviePlaying, OR the movie player's own state — it reads " +
			"NO GUI-manager and no renderer state, so drawing the UI elsewhere cannot change it");
		_guiNatives.Observe("IsEntityOnScreen",
			"pure spatial — the camera frustum against the entity's bounds and its visibility " +
			"bit; owning it would mean reproducing the guest's camera exactly, for no gain");
		_guiNatives.Observe("IsDeathMingameEnabled",
			"a flat gameplay global, byte_83496B58 — nothing to do with the UI at all");

		// The other fifteen read the guest's OWN UI state, written by the natives beside them,
		// and stay correct only while those setters keep executing inside the guest. This is the
		// list of setters that may never be suppressed while their predicate is live.
		const string uiState =
			"a predicate over the guest's OWN UI state, written by the setters beside it — " +
			"correct only while those setters still run, so neither may be suppressed alone";
		// `IsCounterShown` cannot be answered here even though we own the counter pool: its
		// answer depends on its ARGUMENT, and the suppression lever holds one answer per native.
		_guiNatives.Observe("IsCounterShown",
			uiState + " — and its answer depends on the NAME it is passed, which one stored " +
			"answer per native cannot serve");

		// The box family cannot be suppressed: a box's appearance is ours, its modality is game
		// state. Opening one runs `sub_823D0A70(box, 0)`, which closes the quick wheel
		// (`sub_8236F340`), quiets two element-manager channels, stands the context D-pad down,
		// and calls `GameFlow_QueueCommand` — which suspends the incumbent gameflow state and is
		// why the game pauses under a dialogue.
		_guiNatives.Observe("IsDisplayBoxActive",
			"ORs the message-box manager at *(GUI+0x1C)+52 with a 12-channel vector at " +
			"*(GUI+0x20)+0x58 — and the family cannot be suppressed anyway, because opening a " +
			"box pushes a GameFlow command that suspends gameplay");

		_guiNatives.Observe("IsQuickMenuOpen",
			"a raw byte at *(GUI+0x1C)+0x23, returned UNNORMALISED so the Lua truth value is " +
			"the byte itself; its writers have not been located, so what would keep it correct " +
			"is unknown");

		// Two are already broken in the shipped TU: their only writers
		// (`SetTemporaryDeathScreenEnabled`, `ModifyTemporaryDeathScreenPercentage`) are stubs
		// in this build, so the fields they read — fade+16 and fade+20 — are never written.
		const string deadWriter =
			"reads a field whose ONLY writer is a stub in this build, so it answers the same " +
			"thing for ever — shipped behaviour, not ours";
		_guiNatives.Observe("IsTemporaryDeathScreenEnabled", deadWriter);
		_guiNatives.Observe("DidHeroSurviveTemporaryDeathScreen", deadWriter);

		// `GetFillPercent` requires the vector at `*(*(GUI+32)+32)+28` to hold at least two
		// elements and executes `__trap()` when it does not, so anything that stops the fill
		// natives running has to account for it first.
		_guiNatives.Observe("GetFillPercent",
			"returns a FLOAT, and executes __trap() if the fill vector was never populated — " +
			"the one predicate here that fails hard rather than answering wrong");

	}

	/// <summary>Everything refused, each with its reason.</summary>
	private void RouteNotYetOwned()
	{
		// ---- THE QUESTION BOXES --------------------------------------------------------------
		//
		// A question box returns nothing and stores nothing a host can read: its answer travels
		// as a MESSAGE EVENT the scripts collect with `MessageEvents.IsMessagePosted` — type 36
		// carrying 1 or 0 for a yes/no, 37 or 38 carrying the chosen number for the adjuster.
		// So the host draws the box and `Engine_UiSource.DisplayBoxAction` runs the GUEST's own
		// handler, which posts the message; closing our own copy would strand the caller.
		//
		// All three query forms are one class: `DisplayYesNoQuestion`,
		// `DisplayPubGameYesNoQuestion` and `DisplayQuestPrimaryQuestion` build the identical
		// 24-byte object on the identical vtable and differ only in key and body.
		_guiNatives.Register("DisplayYesNoQuestion", call =>
			_boxes.Question(Engine_UiGuiBoxes.MessageBoxName, Localise(call, 0)));

		_guiNatives.Register("DisplayPubGameYesNoQuestion", call =>
			_boxes.Question("PubGameQuery", Localise(call, 0)));

		// The only native here that substitutes `%s` rather than `%1`: its body is the fixed tag
		// `GUI_SCREEN_WORLD_MAP_QUESTS_PRIMARY_QUESTION` and its optional argument, itself a
		// tag, replaces the `%s`. An absent argument leaves the marker in place.
		_guiNatives.Register("DisplayQuestPrimaryQuestion", call =>
		{
			string body = Engine_UiGuiText.Resolve(
				"GUI_SCREEN_WORLD_MAP_QUESTS_PRIMARY_QUESTION", _presentation?.Strings);
			if (call.Argument(0).Text is { Length: > 0 } inner)
			{
				body = body.Replace("%s",
					Engine_UiGuiText.Resolve(inner, _presentation?.Strings),
					System.StringComparison.Ordinal);
			}

			_boxes.Question(Engine_UiGuiBoxes.MessageBoxName, body);
		});

		// `DisplayNumericAdjusterParams(table, tag, ...)` — six keys, and the defaults are not
		// zeroes: the guest ships 111/666/33/55.
		_guiNatives.Register("DisplayNumericAdjusterParams", call =>
		{
			Engine_UiModel.GuiNativeArgument table = call.Argument(0);
			_boxes.Adjuster(
				Localise(call, 1),
				(int)table.FieldNumber("MinVal", 111f),
				(int)table.FieldNumber("MaxVal", 666f),
				(int)table.FieldNumber("Increment", 33f),
				(int)table.FieldNumber("DefaultVal", 55f),
				(int)table.FieldNumber("Type", 0f),
				table.FieldBoolean("ShowSign", false));
		});

		// `DisplayQuestBoxParams` is not a question despite the family: its handler answers only
		// action 36 and posts nothing.
		_guiNatives.Register("DisplayQuestBoxParams", call =>
		{
			Engine_UiModel.GuiNativeArgument table = call.Argument(0);
			string text = Localise(call, 1);
			int type = (int)table.FieldNumber("Type", 0f);
			int risk = (int)table.FieldNumber("RiskLevel", 1f);
			_boxes.QuestBox(table.FieldText("Name", "Unnamed Quest"), text, type, risk);

			// The page's whole statement (`sub_82304920`): the fields localised as the filler
			// `sub_823CFBE0` localises them, the condition count as 0x82304a44..7c counts it.
			string Field(string key) =>
				Engine_UiGuiText.Resolve(table.FieldText(key, string.Empty), _presentation?.Strings);
			string condition1 = table.FieldText("Condition1", "nocondition");
			string condition2 = table.FieldText("Condition2", "nocondition");
			int conditions = condition2 != "nocondition" ? 2 : condition1 != "nocondition" ? 1 : 0;
			Engine_UiQuestBoxPage.Stated = new Engine_UiQuestBoxPage.Statement(
				type, risk, text,
				Field("Occupation"), Field("Appearance"), Field("Features"), Field("TargetName"),
				Field("Whereabouts"), Field("Reason"),
				conditions > 0 ? Engine_UiGuiText.Resolve(condition1, _presentation?.Strings) : string.Empty,
				conditions > 1 ? Engine_UiGuiText.Resolve(condition2, _presentation?.Strings) : string.Empty,
				conditions);
		});

		// The acceptance box has `li r3,0; blr` in both input slots — its whole interaction is
		// in the Gameface Lua behind `g_DisplayBox:OpenAsAcceptanceBox()`. The button box takes
		// input at another vtable slot as an input-phase record, and its enum is unresolved.
		// Their TEXT is the call's (`DisplayAcceptanceBox(name, text)`, `DisplayButtonBox(name,
		// text)`: name → ctrl+4, text → ctrl+8, which the open `sub_8299EE40` writes into
		// `FramePositioningGroup.Text`) — recorded for the display-box surface. Their INPUT stays
		// the guest's: the acceptance box's input slots are both `li r3,0; blr` (the interaction
		// is in the Gameface Lua), and the button box takes an input-phase record at vtable slot 4
		// whose 0x0B..0x0E enum is unresolved.
		_guiNatives.Register("DisplayAcceptanceBox", call =>
			Engine_UiStatedSurface.StatedDisplayText = Localise(call, 1));
		_guiNatives.Register("DisplayButtonBox", call =>
			Engine_UiStatedSurface.StatedDisplayText = Localise(call, 1));

		// The full-screen screens are already owned as screens, each with a surface driven by
		// the screen the guest stands up; the native still opens it.
		const string ownedScreen =
			"opens a screen that is ALREADY owned by its own surface — the native still opens " +
			"it, and routing this would stand a second copy up beside the first";
		foreach (string name in new[]
		{
			"DisplayCrimeScreen", "DisplayShopScreen", "DisplayRealEstateScreen",
			"DisplayReceivedItem", "ShowCreditsScreen",
		})
		{
			_guiNatives.Observe(name, ownedScreen);
		}

		// `SetLoadingScreenBackgroundName` is write-only in this build: its global's whole
		// cross-reference set is the setter, the save serializer and a static destructor. The
		// loading screen's frame comes from a different hardcoded global with no writer.
		_guiNatives.Observe("SetLoadingScreenBackgroundName",
			"stored and round-tripped through the savegame, and read by NOTHING — the loading " +
			"screen's frame comes from a different hardcoded global");

		// ---- THE ELEMENT FADES ----------------------------------------------------------------
		//
		// These are `GotoSlide` calls: each hashes the element name as `SetGUIText` does and
		// posts a slide index — 1 in, 2 out fast, 3 out slow on the `SlideFaderName` channel
		// (one higher on `SlideFader2Name`, whose elements carry an extra leading slide).
		// There is no duration to port: the timing is the slide record's own
		// `endTime - startTime` in the `.bgf` and the curve is its authored Bézier keyframes.
		void Fade(Engine_UiModel.GuiNativeCall call, int slide)
		{
			string element = call.Argument(0).Text;
			if (element.Length != 0)
			{
				_hud?.Values.StateFade(Engine_GameHash.Fnv1(element), slide);
			}
		}

		_guiNatives.Register("FadeElementIn", call => Fade(call, 1));
		_guiNatives.Register("FadeElementOutFast", call => Fade(call, 2));
		_guiNatives.Register("FadeElementOutSlow", call => Fade(call, 3));

		// Everything else, each with the reason it is not owned.
		var pending = new System.Collections.Generic.Dictionary<string, string>(
			System.StringComparer.Ordinal)
		{
			["ShowGDCUI"] =
				"a PERMANENT NO-OP in this build — it stores through byte_83496AEF, which is 0 in " +
				"the image and whose only cross-reference in the whole binary is that read, so the " +
				"store never executes",
			["DisplayDivorceMessage"] =
				"takes an ENTITY userdata, not a value — the same reason DisplayReceivedItem " +
				"cannot be routed off its arguments",
			["SetGhosting"] =
				"(screenId, itemName, ghosted) — it stages String1/Float1 and runs the chunk " +
				"`g_Menu:SetItemGhosting()`, which the ABI 82 chunk capture ALREADY carries, so " +
				"routing it here would apply the same statement twice",
			["SetHUDSuggestionMode"] =
				"NOT a suggestion channel despite the name — it writes a 32-bit mode at +0 of the " +
				"`HUDDPadMenu` widget (`stb`/`stw r3, 0(r9)` at 0x82306518, reached GUI+0x20 " +
				"then +0x38), which the guest itself names in its save serialiser. Only TWO " +
				"readers exist and both test `== 2`: `sub_823D2A68` forces the context-sensitive " +
				"D-pad OPEN even when IsEnabled is false, and `sub_82413BB8` makes an expression " +
				"start queue a GUI event and slam the D-pad shut. Every other value, the ctor " +
				"default of 1 included, behaves identically. The D-pad quick menu " +
				"(`dpad.dpad.dpadmenu`, slide 2 open / 1 closed) has no owned surface, so owning " +
				"this would change nothing on screen",
			["SetDPadEnabled"] =
				"writes dpadObject+0x20 AND tail-calls sub_823D2898, which closes an open ring and " +
				"RELEASES guest input-blocks 185 and 186; three other guest sites write the same " +
				"byte, so a host cannot hold this flag without writing guest memory",
			["PlayFullCutscene"] =
				"pushes a gameflow command (id 7); the push SUSPENDS the incumbent state, which is " +
				"how starting a cutscene aborts a playing movie — a consequence a host reproducing " +
				"only the picture would lose",
			["StopFullCutscene"] =
				"sub_82318800(list, 7) — removes the cutscene state by id and resumes whatever it " +
				"suspended; the gameflow stack is the guest's and nothing host-side stands in for it",
			["SetInteractiveCutsceneMode"] =
				"one byte, `stb r3, 9(elemMgr)` at 0x8230615C — but NOTHING that reads it has been " +
				"found, so what owning it would change is unknown",
			["SetInteractiveCutsceneBorder"] =
				"one byte, `stb r3, 0xA(elemMgr)` at 0x82306174 — same as the mode byte beside it, " +
				"and with the same gap: no reader located",
			["AddWorldIcon"] = "world icons are read from the guest's icon LIST, not from the calls",
			["AddWorldIconWithOffset"] = "world icons are read from the guest's icon LIST",
			["RemoveWorldIcon"] = "world icons are read from the guest's icon LIST",
			["FillWorldIconSetEyeIsOpen"] = "world icons are read from the guest's icon LIST",
			["SetExperienceShouldShowAbilities"] =
				"a BOOL at +0 of the `HUDExperience` widget (`stb r3, 0(r9)` at 0x82307510), not " +
				"a flag on any screen — the guest's save serialiser names it " +
				"`HUDExperience/ShouldShowAbilities`, so it also round-trips through the save. " +
				"It is NOT a dead write: `lbz r11, 0(r30)` at 0x822CF40C gates the " +
				"`HUDSuggestionLevelUp` publish, so clearing it suppresses the spend-experience " +
				"prompt. That prompt goes to binding registry 8 and we walk 1 and 6 only, so " +
				"there is nothing host-side for it to reach yet",
		};

		foreach ((string name, string reason) in pending)
		{
			_guiNatives.Observe(name, reason);
		}

		// The six stubs below all bind to one empty function — a bare `blr` — in this TU.
		// Interception keys on the implementation pointer, the only identity a call carries, so
		// a call through it cannot be attributed to any one of the six; the payload detects the
		// `blr` and records no name, which is why none appears in the census. They are named
		// anyway so that a build where the detection does not fire arrives explained. Fast
		// travel is dispatched as a world-map event, not through `DisplayFastTravelScreen`.
		const string stub =
			"its implementation is a bare `blr` in this build — the native does nothing, and " +
			"all six stubs share that one function so a call through it cannot be attributed";
		foreach (string name in new[]
		{
			"DisplayFastTravelScreen", "AddHealthBar", "RemoveHealthBar",
			"SetTemporaryDeathScreenEnabled", "ModifyTemporaryDeathScreenPercentage",
			"SetCutsceneVignette",
		})
		{
			_guiNatives.Observe(name, stub);
		}
	}


	/// <summary>A stated guest address — argument 0 of the `@` events, eight hex digits.</summary>
	private static uint? GuestAddress(Engine_UiModel.GuiNativeCall call) =>
		uint.TryParse(call.Argument(0).Text, System.Globalization.NumberStyles.HexNumber,
			System.Globalization.CultureInfo.InvariantCulture, out uint address) && address != 0u
			? address
			: null;

	/// <summary>
	/// A call's text argument, resolved and with its trailing arguments substituted.
	/// </summary>
	/// <remarks>
	/// The substitutions are every remaining argument in order, as `sub_82302270` does: numbers
	/// integer-formatted, strings resolved as tags in their own right.
	/// </remarks>
	private string Localise(Engine_UiModel.GuiNativeCall call, int textIndex)
	{
		string text = Engine_UiGuiText.Resolve(
			call.Argument(textIndex).Text, _presentation?.Strings);

		if (call.Arguments is not { Count: > 0 } arguments || arguments.Count <= textIndex + 1)
		{
			return text;
		}

		var values = new System.Collections.Generic.List<string>();
		for (int i = textIndex + 1; i < arguments.Count; ++i)
		{
			Engine_UiModel.GuiNativeArgument argument = arguments[i];
			values.Add(argument.Kind switch
			{
				// Integer-formatted, matching `sub_8230AE78`.
				Engine_UiModel.GuiNativeArgumentKind.Number => ((int)argument.Number).ToString(),
				Engine_UiModel.GuiNativeArgumentKind.String =>
					Engine_UiGuiText.Resolve(argument.Text, _presentation?.Strings),
				_ => argument.Text,
			});
		}

		return Engine_UiGuiText.Substitute(text, values);
	}
}
