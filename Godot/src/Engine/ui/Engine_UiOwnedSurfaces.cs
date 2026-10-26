using Godot;

namespace Refii.Ui;

// The concrete owned surfaces — one per presentation Godot draws instead of mirroring. Each
// states which presentation it owns, how to build its view, how to resolve its positional join,
// and what to do with a sample. Frame: Engine_UiSurface.cs; views: Engine_UiBoxes.cs.

/// <summary>
/// The owned MenuBox (screen id 9) — the Lua-opened option list. The guest's Lua opens the list
/// and owns the outcome; Godot draws it, moves the highlight, and answers through the guest's
/// own accept.
///
/// Navigation moves the guest's own selection, not a host copy: its selector reslides the two
/// affected buttons, so the accept lever and the drawn card always agree.
/// </summary>
public sealed class Engine_UiMenuBoxSurface : Engine_UiModalSurface<Engine_UiMenuBox>
{
	/// <summary>
	/// The guest's throttle: `sub_823DBC70` keeps one timestamp at `box+36` and refuses up, down,
	/// A and B alike until 0.3 s have passed since the last action taken.
	/// </summary>
	private const double StepInterval = 0.3;

	public override string Presentation => Engine_UiMenuBox.Presentation;

	public override string Label => "menu box";

	protected override float Depth => PanelDepth;

	private Engine_UiMenuBox.Joins? _joins;

	private Engine_UiModel.MenuBoxState _box;

	private double _repeat;

	protected override bool Joined => _joins is not null;

	protected override Engine_UiMenuBox Build(Engine_UiTheme? art) =>
		Engine_UiMenuBox.FromPatternBook(art);

	protected override void Join(Engine_UiScreen document) =>
		_joins = Engine_UiMenuBox.Resolve(document);

	/// <summary>
	/// Whether the player card is up: active in the guest AND not at opacity 0 — the file
	/// authors it at 0, so an unstated opacity means hidden, which is every box but the Orb's.
	/// </summary>
	private bool GamerTagShown(System.Collections.Generic.List<Engine_UiModel.UiElement> elements)
	{
		int card = _joins!.GamerTag;
		if (card < 0 || card >= elements.Count || !elements[card].IsGloballyActive)
		{
			return false;
		}

		float opacity = _joins.GamerTagAuthoredOpacity;
		Engine_UiModel.LiveAttribute[,]? live = Engine_UiElementState.Current.Attributes(
			Engine_UiLiveAttributes.OpacityIds, Address);
		if (live is not null && card < live.GetLength(0) &&
			live[card, Engine_UiLiveAttributes.OpacitySlot].Present)
		{
			opacity = live[card, Engine_UiLiveAttributes.OpacitySlot].AsFloat;
		}

		return opacity > 1f;
	}

	protected override bool IsOpen(Engine_UiModel.PauseState? pause)
	{
		if (Engine_UiSource.Current.GetMenuBox() is not { } box)
		{
			return false;
		}

		_box = box;
		return true;
	}

	protected override void Released()
	{
		// Nothing to reset: `box+36` is a wall-clock stamp, not a release latch — a fresh press
		// within 0.3 s is refused whether or not the stick recentred.
	}

	protected override void Apply(
		System.Collections.Generic.List<Engine_UiModel.UiElement> elements,
		Engine_UiPresentationHost host)
	{
		var labels = new string?[Engine_UiMenuBox.MaxOptions];
		for (int i = 0; i < labels.Length && i < _box.Count; ++i)
		{
			labels[i] = elements[_joins!.Labels[i]].Text;
		}

		string? title = elements[_joins!.Title].Text;
		bool gamerTag = GamerTagShown(elements);
		if (!Changed($"{title}|{string.Join('\u001F', labels)}|{_box.Count}|{_box.Selected}|" +
			$"{_box.EnabledMask}|{_box.Cancelable}|{gamerTag}"))
		{
			return;
		}

		View!.ShowStated(title, labels, _box.Count, _box.Selected,
			_box.EnabledMask, _box.Cancelable, gamerTag);
		GD.Print($"[game-ui] menu box up — '{title}', {_box.Count} option(s), " +
			$"selected {_box.Selected}, enabled 0x{_box.EnabledMask:X}");
	}

	/// <summary>
	/// A click on an option answers it, the way a select then A would; the cancel prompt is B.
	/// Disabled options are offered to the guest regardless — it re-checks the enabled byte.
	/// </summary>
	protected override Engine_UiSurfaceInput Point(Engine_UiSurfaceInput input, Vector2 at)
	{
		int option = View?.OptionAt(at) ?? -1;
		if (option == -2)
		{
			return input with { Back = true };
		}

		if (option >= 0 && option < _box.Count)
		{
			Engine_UiSource.Current.PostFrontEndAction($"MenuBoxSelect:{option}");
			Engine_UiSource.Current.PostFrontEndAction($"MenuBoxAnswer:{option + 1}");
			GD.Print($"[game-ui] menu box option {option} clicked");
		}

		return input;
	}

	protected override void Route(Engine_UiSurfaceInput input)
	{
		// One clock for every action, as `sub_823DBC70` keeps it: nothing — step, A or B — is
		// honoured until `_repeat` reaches zero. The pump's cases 109/114 (up/down) wrap:
		// `sel - 1 < 0 → count - 1`, `sel + 1 >= count → 0`.
		_repeat -= input.Delta;
		if (_repeat > 0.0)
		{
			return;
		}

		if (input.Direction != 0 && _box.Count > 0)
		{
			_repeat = StepInterval;
			int next = (_box.Selected + input.Direction + _box.Count) % _box.Count;
			Engine_UiSource.Current.PostFrontEndAction($"MenuBoxSelect:{next}");
			return;
		}

		if (input.Accept)
		{
			// The guest re-checks the enabled byte; a disabled row does not answer.
			_repeat = StepInterval;
			Engine_UiSource.Current.PostFrontEndAction($"MenuBoxAnswer:{_box.Selected + 1}");
			GD.Print($"[game-ui] menu box option {_box.Selected} chosen" +
				(_box.IsEnabled(_box.Selected)
					? string.Empty
					: " — DISABLED, the guest will refuse"));
		}
		else if (input.Back && _box.Cancelable)
		{
			_repeat = StepInterval;
			Engine_UiSource.Current.PostFrontEndAction("MenuBoxAnswer:0");
			GD.Print("[game-ui] menu box cancelled");
		}
	}
}

/// <summary>
/// The owned MessageBox (modal mode 2) — the dismiss-only sibling of the query box.
///
/// The guest fits its text into one of six authored size variants and states the choice as the
/// variant carrying live text, plus the nine-slice's live scale. The dismiss code is per (box
/// id, underlying screen): query id 0 dismisses on 36 at the shop and 46 at the real-estate
/// screen; PHOTOMODE's copy has no dismiss (the upload closes it), so it stays unowned and
/// unmasked.
/// </summary>
public sealed class Engine_UiMessageSurface : Engine_UiModalSurface<Engine_UiMessageBox>
{
	public override string Presentation => Engine_UiMessageBox.Presentation;

	public override string Label => "message box";

	private Engine_UiMessageBox.Joins? _joins;

	/// <summary>The router box's dismiss code, null when the table states none.</summary>
	private uint? _dismiss;

	protected override bool TakesInput => _display != 0 || _dismiss is not null;

	private uint _modalId;

	private uint _screenId;

	/// <summary>
	/// The display MessageBox the guest stated open (vtable 0x820AE668), 0 when none — set by the
	/// owner each frame. The same `messageboxmulti` art as the router's box, but it holds no modal
	/// id: it answers action 36 through its own handler, so it is answered by address.
	/// </summary>
	public uint StatedDisplayBox { get; set; }

	/// <summary>The display box this surface opened for, 0 when it is the router's.</summary>
	private uint _display;

	protected override bool Joined => _joins is not null;

	protected override Engine_UiMessageBox Build(Engine_UiTheme? art) =>
		Engine_UiMessageBox.FromPatternBook(art);

	private Engine_UiScreen? _document;

	protected override void Join(Engine_UiScreen document)
	{
		_document = document;
		_joins = Engine_UiMessageBox.Resolve(document);
	}

	/// <summary>
	/// THE PAPER'S SIZE IS A SLIDE, NOT A TRANSFORM. The guest's fitter puts the `paper`
	/// element on one of its `type1`..`type6` slides and writes no scale or position; this
	/// is that slide, from the file.
	/// </summary>
	private BgfSlide? SizeSlide(int chosen)
	{
		if (_document?.Document is not { } file || _joins is null)
		{
			return null;
		}

		string want = $"type{chosen + 1}";
		foreach (BgfSlide slide in file.Slides)
		{
			if (slide.OwnerElement == _joins.Paper &&
				slide.Name.Equals(want, System.StringComparison.Ordinal))
			{
				return slide;
			}
		}

		return null;
	}

	protected override bool IsOpen(Engine_UiModel.PauseState? pause)
	{
		// DRAWN WHETHER OR NOT ITS CODE IS KNOWN: an unlisted box keeps the guest's own input
		// (see Engine_UiModals), not the guest's hidden tree.
		if (pause is { MessageBoxUp: true } state)
		{
			_dismiss = Engine_UiTemplates.Modal(state.ModalId, state.ScreenId, dismissal: true)
				?.Confirm;
			_modalId = state.ModalId;
			_screenId = state.ScreenId;
			_display = 0;
			return true;
		}

		// A HOST notice is not this box's: it stands on the query box's paper, which is sized to
		// its text (Engine_UiQuerySurface), as the front end's dialog is (GameUi.DriveFrontEndHostBox).
		_display = StatedDisplayBox;
		return _display != 0;
	}

	protected override void Apply(
		System.Collections.Generic.List<Engine_UiModel.UiElement> elements,
		Engine_UiPresentationHost host)
	{
		// The guest's chosen size is the variant its slide ACTIVATES; the text may be written
		// into more than one. The first non-empty text stands in when none is active.
		int chosen = -1;
		string body = string.Empty;
		for (int i = 0; i < _joins!.Texts.Length; ++i)
		{
			Engine_UiModel.UiElement element = elements[_joins.Texts[i]];
			if (element.IsActive && !string.IsNullOrEmpty(element.Text))
			{
				chosen = i;
				body = element.Text;
				break;
			}
		}

		if (chosen < 0)
		{
			for (int i = 0; i < _joins.Texts.Length; ++i)
			{
				string? text = elements[_joins.Texts[i]].Text;
				if (!string.IsNullOrEmpty(text))
				{
					chosen = i;
					body = text;
					break;
				}
			}
		}

		if (chosen < 0 || !Changed($"{chosen}:{body}"))
		{
			return;
		}

		BgfSlide? size = SizeSlide(chosen);
		string paperSource = size is null ? "authored" : $"slide type{chosen + 1}";

		View!.ShowStated(body, chosen, size);
		GD.Print((_display != 0
				? $"[game-ui] display message box {_display:X8} up "
				: $"[game-ui] message box {_modalId} up over screen {_screenId} ") +
			$"(size {chosen + 1}, paper {paperSource}) — " + View.LastReport);
	}

	protected override void Route(Engine_UiSurfaceInput input)
	{
		if (!input.Accept)
		{
			return;
		}

		if (_display != 0)
		{
			Engine_UiSource.Current.PostFrontEndAction(
				$"BoxAction:{(int)Engine_UiSource.BoxAction.Dismiss}@{_display:X8}");
			GD.Print($"[game-ui] display message box {_display:X8} dismissed (code 36)");
			return;
		}

		if (_dismiss is not { } code)
		{
			return;
		}

		Engine_UiSource.Current.PostFrontEndAction($"QueryAnswer:{code}");
		GD.Print($"[game-ui] message box {_modalId} dismissed (code {code})");
	}
}

/// <summary>
/// The owned in-game query box, game-wide. The guest opens one (modal id at guiOwner+44,
/// QueryBox mode at +40) over whatever screen is current and states its text as live element
/// strings; Godot draws it and answers through the guest's own input router. A box whose answer
/// codes are not in the modal table is still drawn but keeps its own input — see
/// <see cref="Engine_UiTemplates.Modal"/>.
/// </summary>
public sealed class Engine_UiQuerySurface : Engine_UiModalSurface<Engine_UiQueryBox>
{
	public override string Presentation => Engine_UiQueryBox.Presentation;

	public override string Label => "query box";

	private Engine_UiQueryBox.Joins? _joins;

	/// <summary>The codes the box that is up answers with, latched at the gate so the routing
	/// does not look them up again.</summary>
	private Engine_UiModalTemplate? _codes;

	private uint _modalId;

	private uint _screenId;

	/// <summary>
	/// The box up is a DISPLAY question, not the modal router's. Same presentation, different
	/// answer: its own handler `sub_823CF3B0` takes 46/47 and posts reply 1/0, reached through
	/// <see cref="Engine_UiSource.DisplayBoxAction"/>.
	/// </summary>
	private bool _display;

	protected override bool TakesInput => _display || _codes is not null || _host;

	/// <summary>
	/// The box up is the HOST's — a question (<see cref="Engine_UiHostBoxes.Question"/>), or a
	/// notice (<see cref="Engine_UiHostBoxes.Notice"/>) when no question is up: one paper for both
	/// in game, sized to the text, the notice with its first choice alone.
	/// </summary>
	private bool _host;

	/// <summary>The host box up is a notice: one answer, A or B dismiss it.</summary>
	private bool _notice;

	protected override bool Joined => _joins is not null;

	protected override Engine_UiQueryBox Build(Engine_UiTheme? art) =>
		Engine_UiQueryBox.FromPatternBook(art);

	protected override void Join(Engine_UiScreen document) =>
		_joins = Engine_UiQueryBox.Resolve(document);

	/// <summary>The paper's scroll bar: the received-item card's `Scrollbar`, a box's own.</summary>
	protected override void Dress(Engine_UiPresentationHost host)
	{
		if (View?.Paper is { } paper)
		{
			paper.BarArt = host.PatternBookFor(Engine_UiReceiveItems.Presentation);
		}
	}

	protected override void Released() => View?.Release();

	protected override bool IsOpen(Engine_UiModel.PauseState? pause)
	{
		// DRAWN WHETHER OR NOT ITS CODES ARE KNOWN — see the message surface.
		// The guest's box outranks a host question, and the flag is re-derived on every path: one
		// left over from a host question routed every press of the guest's next box to nothing.
		_host = false;
		if (pause is { QueryBoxUp: true } state)
		{
			_codes = Engine_UiTemplates.Modal(state.ModalId, uint.MaxValue, dismissal: false);
			_display = false;
			_modalId = state.ModalId;
			_screenId = state.ScreenId;
			return true;
		}

		_codes = null;
		_display = Engine_UiSource.Current.DisplayQueryUp();
		// A host box needs the box's join to be shown; without one it must not hold the frame.
		// In game only: out of it the front end's own dialog draws it (GameUi.DriveFrontEndHostBox).
		// A question stands before a notice, as on the front end.
		_notice = Engine_UiHostBoxes.Question is null;
		_host = !_display && (Engine_UiHostBoxes.Question ?? Engine_UiHostBoxes.Notice) is not null &&
			Refii.Host.HostBridge.IsInGame && (View is null || _joins is not null);
		return _display || _host;
	}

	protected override bool ApplyHost(Engine_UiPresentationHost host)
	{
		if (!_host || (Engine_UiHostBoxes.Question ?? Engine_UiHostBoxes.Notice) is not { } body || _joins is null)
		{
			return false;
		}

		// The default A/B row; its captions the question's own where it states them, else the
		// authored ones. A notice stands its A alone, captioned as the message box's dismiss.
		string? accept = _notice ? View?.Theme?.Resolve("GUI_CONTINUE") : Engine_UiHostBoxes.AcceptCaption;
		string? cancel = _notice ? null : Engine_UiHostBoxes.CancelCaption;
		if (Changed($"host:{(_notice ? "N" : "Q")}\u0001{body}\u0001{accept}\u0001{cancel}"))
		{
			View!.ShowStated(body, _joins.Rows[0].Name, accept, cancel, single: _notice);
			GD.Print($"[game-ui] host {(_notice ? "notice" : "query")} up — {View.LastReport}");
		}

		return true;
	}

	protected override void Apply(
		System.Collections.Generic.List<Engine_UiModel.UiElement> elements,
		Engine_UiPresentationHost host)
	{
		// The row the guest stood up; the default A/B pair when none reads active (the flags
		// can lag the opener by a frame).
		Engine_UiQueryBox.RowJoin row = _joins!.Rows[0];
		foreach (Engine_UiQueryBox.RowJoin candidate in _joins.Rows)
		{
			if (elements[candidate.Row].IsGloballyActive)
			{
				row = candidate;
				break;
			}
		}

		string body = elements[_joins.Body].Text ?? string.Empty;
		// The captions ride in the key: the guest may write them a frame after the body.
		if (body.Length == 0 || !Changed(
			$"{row.Name}\u0001{body}\u0001{elements[row.First].Text}\u0001{elements[row.Second].Text}"))
		{
			return;
		}

		View!.ShowStated(body, row.Name, elements[row.First].Text, elements[row.Second].Text);
		GD.Print((_display ? "[game-ui] display query up " : $"[game-ui] query box {_modalId} up over screen {_screenId} ") +
			$"({row.Name}) — {View.LastReport}");
	}

	/// <summary>A click on the A choice confirms, on the B choice cancels, on the scroll bar
	/// drags it; elsewhere nothing.</summary>
	protected override Engine_UiSurfaceInput Point(Engine_UiSurfaceInput input, Vector2 at) =>
		View is not null && View.PressBar(at)
			? input
			: View?.AnswerAt(at) switch
			{
				1 => input with { Accept = true },
				-1 => input with { Back = true },
				_ => input,
			};

	protected override void Route(Engine_UiSurfaceInput input)
	{
		// The body's scroll, for a text taller than the paper's window: the right stick and the
		// d-pad at the page rate, the wheel and the bar through the pointer.
		View?.Scroll(input.Scroll, input.Direction, input.Delta);

		if (_host)
		{
			if (_notice && (input.Accept || input.Back))
			{
				// One answer: B clears a notice as A does — a box only one key could close is a deadlock.
				GD.Print("[game-ui] host notice dismissed");
				Engine_UiHostBoxes.Dismiss();
			}
			else if (input.Accept || input.Back)
			{
				GD.Print($"[game-ui] host query {(input.Accept ? "CONFIRMED" : "cancelled")}");
				Engine_UiHostBoxes.Answer(input.Accept);
			}

			return;
		}

		if (_display)
		{
			if (input.Accept || input.Back)
			{
				Engine_UiSource.Current.DisplayBoxAction(input.Accept
					? Engine_UiSource.BoxAction.Confirm
					: Engine_UiSource.BoxAction.Cancel);
				GD.Print($"[game-ui] display query {(input.Accept ? "CONFIRMED" : "cancelled")}");
			}

			return;
		}

		if (_codes is not { } codes)
		{
			return;
		}

		if (input.Accept)
		{
			Engine_UiSource.Current.PostFrontEndAction($"QueryAnswer:{codes.Confirm}");
			GD.Print($"[game-ui] query box {_modalId} CONFIRMED (code {codes.Confirm})");
		}
		else if (input.Back && codes.Cancel is { } cancel)
		{
			Engine_UiSource.Current.PostFrontEndAction($"QueryAnswer:{cancel}");
			GD.Print($"[game-ui] query box {_modalId} cancelled (code {cancel})");
		}
	}
}

/// <summary>
/// The owned numeric adjuster (modal mode 5, or a DISPLAY adjuster from
/// `GUI.DisplayNumericAdjusterParams`). The guest owns the value: its widget steps it with
/// accelerating increments, formats it (the % and + templates) into live text, and the
/// committing handler reads it off the widget. This forwards steps as the guest's own codes and
/// displays what the guest states back.
/// </summary>
public sealed class Engine_UiNumericSurface : Engine_UiModalSurface<Engine_UiNumericBox>
{
	/// <summary>
	/// The step auto-repeat clock, host-side: the guest's own repeat lives in its stick
	/// translator, which the masked D-pad never reaches.
	/// </summary>
	private const double StepInterval = 0.15;

	/// <summary>
	/// The guest's step codes — `sub_826B8EB0`: 28 → `sub_826B8CF0` increment, 29 → decrement.
	/// D-pad up is 28.
	/// </summary>
	private const uint StepIncrement = 28;

	private const uint StepDecrement = 29;

	/// <summary>Commit and cancel, as the adjuster's handler reads them.</summary>
	private const uint Commit = 46;

	private const uint Cancel = 47;

	public override string Presentation => Engine_UiNumericBox.Presentation;

	public override string Label => "numeric adjuster";

	private Engine_UiNumericBox.Joins? _joins;

	private uint _modalId;

	/// <summary>
	/// The adjuster up is a DISPLAY box, not the router's. Same codes, different lever: its own
	/// handler `sub_823CF8A8` takes them — 28/29 step, 46 commits and posts 37, 47 cancels and
	/// posts 38 — reached through <see cref="Engine_UiSource.DisplayBoxAction"/>.
	/// </summary>
	private bool _display;

	private double _repeat;

	protected override bool Joined => _joins is not null;

	protected override Engine_UiNumericBox Build(Engine_UiTheme? art) =>
		Engine_UiNumericBox.FromPatternBook(art);

	protected override void Join(Engine_UiScreen document) =>
		_joins = Engine_UiNumericBox.Resolve(document);

	protected override bool IsOpen(Engine_UiModel.PauseState? pause)
	{
		if (pause is { AdjusterUp: true } state)
		{
			_modalId = state.ModalId;
			_display = false;
			return true;
		}

		_display = Engine_UiSource.Current.DisplayAdjusterUp();
		return _display;
	}

	protected override void Released() => _repeat = 0.0;

	protected override void Apply(
		System.Collections.Generic.List<Engine_UiModel.UiElement> elements,
		Engine_UiPresentationHost host)
	{
		string body = elements[_joins!.Body].Text ?? string.Empty;
		string value = elements[_joins.Value].Text ?? string.Empty;
		bool up = elements[_joins.ArrowUp].IsGloballyActive;
		bool down = elements[_joins.ArrowDown].IsGloballyActive;

		if (value.Length == 0 || !Changed($"{body}|{value}|{up}|{down}"))
		{
			return;
		}

		View!.ShowStated(body, value, up, down);
	}

	/// <summary>A click on an arrow steps; anywhere else on the box commits the value.</summary>
	protected override Engine_UiSurfaceInput Point(Engine_UiSurfaceInput input, Vector2 at) =>
		View?.ArrowAt(at) is { } step && step != 0
			? input with { Direction = step }
			: base.Point(input, at);

	protected override void Route(Engine_UiSurfaceInput input)
	{
		if (input.Direction == 0)
		{
			_repeat = 0.0;
		}
		else
		{
			_repeat -= input.Delta;
			if (_repeat <= 0.0)
			{
				_repeat = StepInterval;
				Send(input.Direction < 0 ? StepIncrement : StepDecrement);
			}
		}

		string which = _display ? "display numeric adjuster" : $"numeric adjuster {_modalId}";
		if (input.Accept)
		{
			Send(Commit);
			GD.Print($"[game-ui] {which} COMMITTED");
		}
		else if (input.Back)
		{
			Send(Cancel);
			GD.Print($"[game-ui] {which} cancelled");
		}
	}

	private void Send(uint code) =>
		Engine_UiSource.Current.PostFrontEndAction(
			_display ? $"BoxAction:{code}" : $"QueryAnswer:{code}");
}

/// <summary>
/// The owned received-item card (screen id 8), drawn from what the guest STATES at its own
/// entry points. The handler's set-item (`sub_82537F38`, and `sub_82538268` for the co-op
/// partner's card), its input handler and its leave are hooked (receive_items_hooks.cpp) and post
/// the handler's fields as a host-only record: the title and description TEXT KEYS, the icon's
/// resource hash, the NameAndIcon line, the usable flag, the side and the peer flag. Nothing here
/// reads the presentation's elements — the guest rebuilds that presentation under a sampler
/// between the items of one chest, which is how the card stood invisible. The answers go through
/// the handler's own screen-input method (`ReceiveItemsAction`), as before.
/// </summary>
public sealed class Engine_UiReceiveItemsSurface : Engine_UiModalSurface<Engine_UiReceiveItems>
{
	/// <summary>The host-only record kind the hooks post (receive_items_hooks.cpp kCardKind).</summary>
	public const int StateKind = 1006;

	/// <summary>The guest's screen id for the card, read beside the stated record as the sanity
	/// that the screen the record belongs to is still the current one.</summary>
	private const uint CardScreen = 8;

	/// <summary>The screen handler's own codes: done, turn the card over, equip.</summary>
	private const uint DoneCode = 36;

	private const uint FlipCode = 39;

	private const uint EquipCode = 75;

	/// <summary>The prompt tags the card's file authors (`receiveitemsscreen.bgf`: GUI_USE,
	/// GUI_INFO, GUI_FLIP, GUI_TAKE); the partner's card captions Done as GUI_DONE (+324).</summary>
	private const string UseTag = "GUI_USE";

	private const string InfoTag = "GUI_INFO";

	private const string FlipTag = "GUI_FLIP";

	private const string TakeTag = "GUI_TAKE";

	private const string DoneTag = "GUI_DONE";

	/// <summary>One stated card: the handler's fields after set-item, the side after each input.</summary>
	public sealed record CardState(
		bool Up, bool Peer, bool Front, bool Usable,
		uint Record, uint Model, uint Icon, uint Title, uint Description, uint Extra, uint Amount,
		string Name);

	private static readonly CardState Down = new(false, false, true, false, 0, 0, 0, 0, 0, 0, 0, string.Empty);

	/// <summary>What the guest last stated; read by the drive each frame.</summary>
	public static CardState State { get; private set; } = Down;

	/// <summary>A record from the hooks: `up=1,peer=,front=,usable=,record=,model=,icon=,title=,desc=,extra=,amount=,name='…'` or `up=0`.</summary>
	public static void Stated(string fields)
	{
		var values = new System.Collections.Generic.Dictionary<string, object>(System.StringComparer.Ordinal);
		foreach (System.Collections.Generic.KeyValuePair<string, object> pair in Refii.Mods.RefiiHost.ParseFieldsPlain(fields))
		{
			values[pair.Key] = pair.Value;
		}

		uint U(string key) => values.TryGetValue(key, out object? v) && v is long n ? unchecked((uint)n) : 0u;
		bool B(string key) => U(key) != 0u;
		if (!B("up"))
		{
			State = Down;
			GD.Print("[game-ui] received-item card stated DOWN");
			return;
		}

		State = new CardState(
			true, B("peer"), B("front"), B("usable"),
			U("record"), U("model"), U("icon"), U("title"), U("desc"), U("extra"), U("amount"),
			values.TryGetValue("name", out object? name) ? name as string ?? name?.ToString() ?? string.Empty : string.Empty);
		GD.Print($"[game-ui] received-item card stated UP — record 0x{State.Record:X8} title 0x{State.Title:X8} " +
			$"icon 0x{State.Icon:X8} {(State.Front ? "front" : "back")} {(State.Usable ? "usable" : "NOT usable")}" +
			$"{(State.Peer ? " (partner's)" : string.Empty)}");
	}

	public override string Presentation => Engine_UiReceiveItems.Presentation;

	public override string Label => "received-item card";

	protected override float Depth => PanelDepth;

	private Engine_UiReceiveItems.Joins? _joins;

	protected override bool Joined => _joins is not null;

	protected override Engine_UiReceiveItems Build(Engine_UiTheme? art) =>
		Engine_UiReceiveItems.FromPatternBook(art);

	protected override void Join(Engine_UiScreen document) =>
		_joins = Engine_UiReceiveItems.Resolve(document);

	/// <summary>Open while the guest's set-item has stated a card and its leave has not taken it
	/// down, and the card is the current screen: a latch over it (a box) does not close it.</summary>
	protected override bool IsOpen(Engine_UiModel.PauseState? pause) =>
		State.Up && pause is { ScreenId: CardScreen };

	/// <summary>The stated card on screen: the strings by their text keys, the icon by its
	/// resource hash, the prompts by the file's tags.</summary>
	protected override bool ApplyHost(Engine_UiPresentationHost host)
	{
		CardState s = State;
		if (!s.Up || View is null)
		{
			return false;
		}

		if (!Changed($"stated:{s.Peer}|{s.Front}|{s.Usable}|{s.Icon:X8}|{s.Title:X8}|{s.Description:X8}|" +
			$"{s.Extra:X8}|{s.Amount}|{s.Name}"))
		{
			return true;
		}

		Engine_UiTheme? theme = View.Theme;
		string? title = Text(theme, s.Title);
		string? back = Text(theme, s.Description);
		// The discount card's line is its tag and amount (`+68`, `+72`); every other card's line
		// is the string the guest composed (`+76`), empty for most items.
		string name = s.Name.Length == 0 && s.Extra != 0
			? $"{Text(theme, s.Extra)} {s.Amount}"
			: s.Name;

		Texture2D? icon = null;
		if (s.Icon != 0 && host.TextureNames is { } names && names.TryResolve(s.Icon, out string path))
		{
			icon = host.LoadTexture(path);
		}

		var labels = new string?[]
		{
			theme?.Resolve(UseTag),
			theme?.Resolve(s.Front ? InfoTag : FlipTag),
			theme?.Resolve(s.Peer ? DoneTag : TakeTag),
		};

		View.BackClip = _joins?.BackClip;
		View.ShowStated(title, name, back, s.Icon, icon, up: true, reverse: !s.Front, usable: s.Usable, labels);
		GD.Print($"[game-ui] received-item card up — '{title}' " +
			$"(icon {(icon is null ? $"0x{s.Icon:X8} unresolved" : "resolved")}, " +
			$"{(s.Front ? "front" : "back")}, {(s.Usable ? "usable" : "NOT usable")})");
		return true;
	}

	/// <summary>A text by the key hash the guest holds (`Text_HashKeyNameFnv1`), or null.</summary>
	private static string? Text(Engine_UiTheme? theme, uint key) =>
		key != 0 && theme?.Strings is { } strings && strings.TryGetString(key, out string value) ? value : null;

	/// <summary>Never reached: the card is host-stated (<see cref="ApplyHost"/>) and the elements
	/// are not read.</summary>
	protected override void Apply(
		System.Collections.Generic.List<Engine_UiModel.UiElement> elements,
		Engine_UiPresentationHost host)
	{
	}

	/// <summary>A click on a prompt is its button; the card itself answers nothing.</summary>
	protected override Engine_UiSurfaceInput Point(Engine_UiSurfaceInput input, Vector2 at) =>
		View?.PromptAt(at) switch
		{
			"Done" => input with { Accept = true },
			"Info" => input with { Flip = true },
			"Action" => input with { Equip = true },
			_ => input,
		};

	protected override void Route(Engine_UiSurfaceInput input)
	{
		// The wheel scrolls the description, a notch a step; the card takes every notch while it
		// stands, so none falls through to the guest as a D-pad press.
		int notches = Engine_UiPointer.TakeWheel();
		View?.ScrollBack(input.Scroll, input.Delta, notches);

		if (input.Accept)
		{
			Engine_UiSource.Current.PostFrontEndAction($"ReceiveItemsAction:{DoneCode}");
			GD.Print("[game-ui] received-item card DONE");
		}
		else if (input.Flip)
		{
			Engine_UiSource.Current.PostFrontEndAction($"ReceiveItemsAction:{FlipCode}");
			GD.Print("[game-ui] received-item card flipped");
		}
		else if (input.Equip)
		{
			// `sub_82537128` code 75: honoured only while `+80` (equippable) is set — it equips
			// through `sub_8246FFF0` and then takes the same close path as 36.
			Engine_UiSource.Current.PostFrontEndAction($"ReceiveItemsAction:{EquipCode}");
			GD.Print("[game-ui] received-item card EQUIP");
		}
	}
}

/// <summary>
/// A display-only owned surface: the intermission strip and the info/acceptance display box.
/// Both are box-queue requests the guest opens, writes and closes itself. They take no input and
/// have no modal state, so they are driven at the presentation follower's sampling cadence — the
/// follower hands the address in.
///
/// One class for both: they differ only by table — which authored groups to clone, which paths
/// carry the live text, and, for the display box, the group whose presence vetoes ownership.
/// </summary>
public sealed class Engine_UiStatedSurface : Engine_UiSurface<Engine_UiStatedPanel>
{
	/// <summary>
	/// The intermission strip: one group, one line of text, nothing to veto. `intermissionbox.bgf`
	/// carries the `MessageBox` behaviour, the `MessageBoxContract` and `MessageBox.lua`; its root
	/// authors the `Intro` and `Outro` slides. `Open` (`messagebox.dis main.2`) writes only
	/// `textstring`, so the strip takes no theme colour.
	/// </summary>
	public static Engine_UiStatedSurface Intermission() => new(
		"intermission strip",
		"Intermission",
		new Engine_UiStatedPanel.Config
		{
			Presentation = "intermissionbox",
			Groups = new[] { "Group" },
			HudStyle = true,
			Texts = new[] { new Engine_UiStatedPanel.TextBinding("Group", "Text") },
		},
		veto: null,
		describeSlide: DescribeIntermission);

	/// <summary>
	/// The info/acceptance display box: body plus an optional title.
	///
	/// Slides 7 and 8 stay the mirror's: the hold-to-accept card is `AcceptanceNewInt` /
	/// `AcceptanceNewOut`, the only two slides that set the `Paper` group active, so the veto is
	/// that group's name.
	///
	/// The two colours are `PrepareDisplayBox`'s (`displaybox.dis main.6 pc3..56`): subheading on
	/// the title, normal text on the body.
	/// </summary>
	public static Engine_UiStatedSurface DisplayBox() => new(
		"display box",
		"DisplayBox",
		new Engine_UiStatedPanel.Config
		{
			Presentation = "displaybox",
			Groups = new[] { "DefaultGroup" },
			HudStyle = true,
			Texts = new[]
			{
				new Engine_UiStatedPanel.TextBinding(
					"DefaultGroup", "FramePositioningGroup/Text",
					Engine_UiBoxConstants.NormalText),
				new Engine_UiStatedPanel.TextBinding(
					"DefaultGroup", "FramePositioningGroup/Title",
					Engine_UiBoxConstants.SubheadingText),
			},
		},
		veto: "Paper",
		describeSlide: DescribeDisplayBox);

	private Engine_UiStatedSurface(
		string label, string nodeName, Engine_UiStatedPanel.Config config, string? veto,
		System.Func<int, string>? describeSlide = null)
	{
		Label = label;
		_nodeName = nodeName;
		_config = config;
		_veto = veto;
		_describeSlide = describeSlide;
	}

	public override string Presentation => _config.Presentation;

	public override string Label { get; }

	protected override float Depth => PanelDepth;

	private readonly string _nodeName;

	private readonly Engine_UiStatedPanel.Config _config;

	/// <summary>The authored group whose live-active state means this style is not ours.</summary>
	private readonly string? _veto;

	private Engine_UiStatedPanel.Joins? _joins;

	/// <summary>The veto group's document ordinal, -1 when the surface has none.</summary>
	private int _vetoOrdinal = -1;

	/// <summary>
	/// How this surface's SCENE slide reads, or null where the surface states none.
	///
	/// The slide is the scene root's: every `OpenAs*Box` ends on `goToSlide(SCENE, n)`
	/// (`displaybox.dis main.2/3/4 pc12..17`) and `Close` tests `getCurrentSlide(SCENE)`
	/// (`main.5 pc3..5`); `MessageBox`'s `:GoToSlide(n)` is the same call.
	///
	/// `SCENE` is element ordinal 0, the root `Component`, where `displaybox.bgf` (master plus
	/// eight slides) and `intermissionbox.bgf` (master plus Intro and Outro) hang their numbered
	/// sets. No other element's slide byte is written.
	/// </summary>
	private readonly System.Func<int, string>? _describeSlide;

	protected override bool Joined => _joins is not null;

	protected override Engine_UiStatedPanel Build(Engine_UiTheme? art) =>
		Engine_UiStatedPanel.FromPatternBook(art, _config, _nodeName);

	protected override void Join(Engine_UiScreen document)
	{
		_joins = Engine_UiStatedPanel.Resolve(document, _config);

		if (_veto is not null)
		{
			_vetoOrdinal =
				Engine_UiStatedPanel.FindByName(document.Roots, _veto)?.Ordinal ?? -1;
		}
	}

	/// <summary>
	/// One sample of a live presentation. True when the owned panel stands in for the mirror;
	/// false leaves the guest's own tree to be drawn — the fallback for an unowned style and for
	/// a build that produced no art.
	/// </summary>
	public bool Sample(Engine_UiPresentationHost? host, uint address)
	{
		if (host is null)
		{
			return false;
		}

		Ensure(host);

		if (!View!.HasShippedArt || _joins is null || Elements(address) is not { } elements)
		{
			return false;
		}

		if (_vetoOrdinal >= 0 && elements[_vetoOrdinal].IsGloballyActive)
		{
			View.Visible = false;
			return false;
		}

		// The first text is the body and decides whether the panel is up: the guest clears it
		// rather than tearing the presentation down. Later texts are optional.
		//
		// The body is the element the guest wrote, never a `GUI.*` call record: this art is only
		// the acceptance and button boxes (`sub_823D0B90` kinds 6..8 → screen 7). Info boxes go
		// to the HUD TopBox, messages to `messageboxmulti`, intermissions to `intermissionbox`.
		string body = elements[_joins.Texts[0]].Text ?? string.Empty;
		bool up = elements[_joins.Texts[0]].IsGloballyActive && body.Length != 0;

		if (up)
		{
			var texts = new string?[_joins.Texts.Length];
			texts[0] = body;
			for (int i = 1; i < texts.Length; ++i)
			{
				texts[i] = elements[_joins.Texts[i]].IsGloballyActive
					? elements[_joins.Texts[i]].Text
					: null;
			}

			View.ShowStated(texts);
		}

		View.Visible = up;

		// Which slide the scene root is on, and so which box this is and whether it is coming or
		// going. Reported on change.
		if (_describeSlide is not null && up &&
			Engine_BgfDocument.SceneOwnerElement < elements.Count)
		{
			int slide = elements[Engine_BgfDocument.SceneOwnerElement].Slide;
			string kind = _describeSlide(slide);
			if (kind != _kindReported)
			{
				_kindReported = kind;
				GD.Print($"[ui-surface] the {Label} is {kind}");
			}
		}

		return true;
	}

	/// <summary>
	/// What a display box's scene slide means — see
	/// <see cref="Engine_UiBoxConstants.DisplayBox"/>.
	///
	/// Eight numbered slides: Info/Objective/Acceptance in and out, then `AcceptanceNewInt` and
	/// `AcceptanceNewOut` — the `Paper` card, vetoed above, so those two arrive here only if the
	/// veto did not see the group active.
	/// </summary>
	private static string DescribeDisplayBox(int slide)
	{
		foreach (Engine_UiBoxConstants.DisplayBox kind in
			System.Enum.GetValues<Engine_UiBoxConstants.DisplayBox>())
		{
			if (slide == Engine_UiBoxConstants.DisplayBoxIntro(kind))
			{
				return $"{kind} (slide {slide}, coming in)";
			}

			if (slide == Engine_UiBoxConstants.DisplayBoxOutro(kind))
			{
				return $"{kind} (slide {slide}, going out)";
			}
		}

		return slide switch
		{
			Engine_UiBoxConstants.DisplayBoxAcceptanceNewIntro =>
				$"AcceptanceNew (slide {slide}, coming in)",
			Engine_UiBoxConstants.DisplayBoxAcceptanceNewOutro =>
				$"AcceptanceNew (slide {slide}, going out)",
			_ => $"on slide {slide}, which is not one of the eight the root authors",
		};
	}

	/// <summary>
	/// What the intermission strip's scene slide means — `MessageBox.lua`'s `INTRO_SLIDE = 1`
	/// and `OUTRO_SLIDE = 2` (`messagebox.dis main pc5, pc8`), which are the two numbered
	/// slides `intermissionbox.bgf`'s root authors.
	/// </summary>
	private static string DescribeIntermission(int slide) => slide switch
	{
		Engine_UiBoxConstants.MessageBoxIntroSlide => $"coming in (slide {slide})",
		Engine_UiBoxConstants.MessageBoxOutroSlide => $"going out (slide {slide})",
		_ => $"on slide {slide}, which is neither of the two the root authors",
	};

	private string _kindReported = string.Empty;

	/// <summary>Takes the panel down when its presentation has left the live set.</summary>
	public void Withdraw()
	{
		Close();
		_kindReported = string.Empty;
	}
}
