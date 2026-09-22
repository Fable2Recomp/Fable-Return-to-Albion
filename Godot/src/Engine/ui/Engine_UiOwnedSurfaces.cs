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
		if (!Changed($"{title}|{string.Join('\u001F', labels)}|{_box.Count}|{_box.Selected}|" +
			$"{_box.EnabledMask}|{_box.Cancelable}"))
		{
			return;
		}

		View!.ShowStated(title, labels, _box.Count, _box.Selected,
			_box.EnabledMask, _box.Cancelable);
		GD.Print($"[game-ui] menu box up — '{title}', {_box.Count} option(s), " +
			$"selected {_box.Selected}, enabled 0x{_box.EnabledMask:X}");
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

	private uint _dismiss;

	private uint _modalId;

	private uint _screenId;

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
	/// THE PAPER'S SIZE IS A SLIDE, NOT A TRANSFORM. `sub_8236FA68` fits the text into one of
	/// the six MessageText variants and puts the `paper` element on that size's slide;
	/// messageboxmulti.bgf's paper owns `type1`..`type6`, each scaling `paperbox` (3, 4, 5, 5,
	/// 6, 7) and activating exactly one text. The guest never writes a scale, so reading the
	/// transform alone kept the parchment at the authored scale 1 under a full-size text —
	/// the box on the abilities page with its notice spilling past the paper on every side.
	/// </summary>
	private Vector2? PaperScaleFromSlide(int chosen)
	{
		if (_document?.Document is not { } file || _joins is null)
		{
			return null;
		}

		uint scaleX = Refii.Core.Engine_GameHash.AttributeId("scale.x");
		uint scaleY = Refii.Core.Engine_GameHash.AttributeId("scale.y");
		string want = $"type{chosen + 1}";
		foreach (BgfSlide slide in file.Slides)
		{
			if (slide.OwnerElement != _joins.Paper ||
				!slide.Name.Equals(want, System.StringComparison.Ordinal))
			{
				continue;
			}

			foreach (BgfSlideEntry entry in slide.Entries)
			{
				if (entry.ElementOrdinal != _joins.PaperBox)
				{
					continue;
				}

				float? x = null;
				float? y = null;
				foreach (BgfAttribute command in entry.Commands)
				{
					if (command.Type != BgfValueType.Float)
					{
						continue;
					}

					if (command.Id == scaleX)
					{
						x = command.AsFloat();
					}
					else if (command.Id == scaleY)
					{
						y = command.AsFloat();
					}
				}

				if (x is { } sx && y is { } sy && sx > 0f && sy > 0f)
				{
					return new Vector2(sx, sy);
				}
			}
		}

		return null;
	}

	protected override bool IsOpen(Engine_UiModel.PauseState? pause)
	{
		if (pause is not { MessageBoxUp: true } state ||
			Engine_UiTemplates.Modal(state.ModalId, state.ScreenId, dismissal: true)
				is not { } template)
		{
			return false;
		}

		_dismiss = template.Confirm;
		_modalId = state.ModalId;
		_screenId = state.ScreenId;
		return true;
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

		// The nine-slice's resolved size, off the guest's own live transform — on `paperbox`,
		// or on its `paper` parent (the slide's writes may land on either; take whichever
		// states a scale).
		Vector2? paperScale = null;
		float?[]?[]? transform = Engine_UiLiveAttributes.Transform(Address, elements.Count);
		if (transform is not null)
		{
			foreach (int ordinal in new[] { _joins.PaperBox, _joins.Paper })
			{
				if (ordinal < transform.Length &&
					transform[ordinal] is { } stated &&
					stated[3] is { } scaleX && stated[4] is { } scaleY)
				{
					paperScale = new Vector2(scaleX, scaleY);
					break;
				}
			}
		}

		string paperSource = "guest-stated";
		if (paperScale is null)
		{
			paperScale = PaperScaleFromSlide(chosen);
			paperSource = paperScale is null ? "authored" : $"slide type{chosen + 1}";
		}

		View!.ShowStated(body, chosen, paperScale);
		GD.Print($"[game-ui] message box {_modalId} up over screen {_screenId} " +
			$"(size {chosen + 1}, paper {paperSource}) — " + View.LastReport);
	}

	protected override void Route(Engine_UiSurfaceInput input)
	{
		if (!input.Accept)
		{
			return;
		}

		Engine_UiSource.Current.PostFrontEndAction($"QueryAnswer:{_dismiss}");
		GD.Print($"[game-ui] message box {_modalId} dismissed (code {_dismiss})");
	}
}

/// <summary>
/// The owned in-game query box, game-wide. The guest opens one (modal id at guiOwner+44,
/// QueryBox mode at +40) over whatever screen is current and states its text as live element
/// strings; Godot draws it and answers through the guest's own input router. A box whose answer
/// codes are not in the modal table keeps the mirror and its own input — see
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

	protected override bool Joined => _joins is not null;

	protected override Engine_UiQueryBox Build(Engine_UiTheme? art) =>
		Engine_UiQueryBox.FromPatternBook(art);

	protected override void Join(Engine_UiScreen document) =>
		_joins = Engine_UiQueryBox.Resolve(document);

	protected override bool IsOpen(Engine_UiModel.PauseState? pause)
	{
		if (pause is not { QueryBoxUp: true } state ||
			Engine_UiTemplates.Modal(state.ModalId, uint.MaxValue, dismissal: false) is not { } codes)
		{
			_codes = null;
			return false;
		}

		_codes = codes;
		_modalId = state.ModalId;
		_screenId = state.ScreenId;
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
		if (body.Length == 0 || !Changed($"{row.Name}\u0001{body}"))
		{
			return;
		}

		View!.ShowStated(body, row.Name, elements[row.First].Text, elements[row.Second].Text);
		GD.Print($"[game-ui] query box {_modalId} up over screen {_screenId} " +
			$"({row.Name}) — {View.LastReport}");
	}

	protected override void Route(Engine_UiSurfaceInput input)
	{
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
/// The owned numeric adjuster (modal mode 5). The guest owns the value: its widget steps it with
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

	private double _repeat;

	protected override bool Joined => _joins is not null;

	protected override Engine_UiNumericBox Build(Engine_UiTheme? art) =>
		Engine_UiNumericBox.FromPatternBook(art);

	protected override void Join(Engine_UiScreen document) =>
		_joins = Engine_UiNumericBox.Resolve(document);

	protected override bool IsOpen(Engine_UiModel.PauseState? pause)
	{
		if (pause is not { AdjusterUp: true } state)
		{
			return false;
		}

		_modalId = state.ModalId;
		return true;
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
				Engine_UiSource.Current.PostFrontEndAction(
					$"QueryAnswer:{(input.Direction < 0 ? StepIncrement : StepDecrement)}");
			}
		}

		if (input.Accept)
		{
			Engine_UiSource.Current.PostFrontEndAction($"QueryAnswer:{Commit}");
			GD.Print($"[game-ui] numeric adjuster {_modalId} COMMITTED");
		}
		else if (input.Back)
		{
			Engine_UiSource.Current.PostFrontEndAction($"QueryAnswer:{Cancel}");
			GD.Print($"[game-ui] numeric adjuster {_modalId} cancelled");
		}
	}
}

/// <summary>
/// The owned received-item card (screen id 8). The guest pushes it on every pickup; its strings,
/// icon and side are live state, and its buttons go through the handler's own screen-input
/// method, so the answers are `ReceiveItemsAction` rather than `QueryAnswer`.
/// </summary>
public sealed class Engine_UiReceiveItemsSurface : Engine_UiModalSurface<Engine_UiReceiveItems>
{
	/// <summary>The guest's screen id and the modal mode it stands in.</summary>
	private const uint CardScreen = 8;

	private const uint CardMode = 1;

	/// <summary>The screen handler's own codes: done, and turn the card over.</summary>
	private const uint DoneCode = 36;

	private const uint FlipCode = 39;

	/// <summary>X — equip and close, gated by the guest on `+80` (`sub_82537128` code 75).</summary>
	private const uint EquipCode = 75;

	public override string Presentation => Engine_UiReceiveItems.Presentation;

	public override string Label => "received-item card";

	protected override float Depth => PanelDepth;

	private Engine_UiReceiveItems.Joins? _joins;

	protected override bool Joined => _joins is not null;

	protected override Engine_UiReceiveItems Build(Engine_UiTheme? art) =>
		Engine_UiReceiveItems.FromPatternBook(art);

	protected override void Join(Engine_UiScreen document) =>
		_joins = Engine_UiReceiveItems.Resolve(document);

	protected override bool IsOpen(Engine_UiModel.PauseState? pause) =>
		pause is { ScreenId: CardScreen, ModalMode: CardMode };

	protected override void Apply(
		System.Collections.Generic.List<Engine_UiModel.UiElement> elements,
		Engine_UiPresentationHost host)
	{
		string? title = elements[_joins!.Title].Text;
		string? name = elements[_joins.Name].Text;
		string? back = _joins.BackText >= 0 ? elements[_joins.BackText].Text : null;
		bool frontUp = elements[_joins.Front].IsGloballyActive;
		bool backUp = elements[_joins.Back].IsGloballyActive;

		// WHICH SIDE IS UP IS THE ROOT'S STATED SLIDE. `sub_82537128` code 39 runs
		// `sub_82426BA8(root, pres, 3)` to turn the card over and `4` to turn it back, and
		// `Foreground` stays globally active through both — its paper turns edge-on and its
		// contents fade — so the two groups' active flags never say "the back is up".
		int rootSlide = _joins.Root >= 0 ? elements[_joins.Root].Slide : 0;
		bool reverse = rootSlide == Engine_UiReceiveItems.FlipToBackSlide ||
			rootSlide == Engine_UiReceiveItems.FlipOutroSlide;

		// `sub_82538910`: `Buttons` slide = (handler+80 == 0) + 1, written ONLY on the side that
		// is up. The card always opens on its front and is filled there, so the front's slide is
		// this card's answer on both sides; the back's still holds the last card's until the
		// guest's refresh lands, which is the X that flashed on every turn-over.
		int buttons = _joins.FrontButtons;
		bool usable = buttons < 0 ||
			elements[buttons].Slide != Engine_UiReceiveItems.UnusableSlide;

		var labels = new string?[_joins.Labels.Length];
		for (int i = 0; i < labels.Length; ++i)
		{
			labels[i] = _joins.Labels[i] >= 0 ? elements[_joins.Labels[i]].Text : null;
		}

		uint iconHash = 0;
		uint[]? hashes = Engine_UiSource.Current.GetUiTextureHashes(Address, elements.Count);
		if (hashes is not null && _joins.Icon < hashes.Length)
		{
			iconHash = hashes[_joins.Icon];
		}

		if (!Changed($"{title}|{name}|{back}|{frontUp}|{backUp}|{iconHash:X8}|{rootSlide}|" +
			$"{usable}|{string.Join('\u001F', labels)}"))
		{
			return;
		}

		// The icon, resolved the way every other live texture is.
		Texture2D? icon = null;
		if (iconHash != 0 &&
			host.TextureNames is { } names &&
			names.TryResolve(iconHash, out string path))
		{
			icon = host.LoadTexture(path);
		}

		View!.BackClip = _joins.BackClip;
		View!.ShowStated(title, name, back, iconHash, icon, frontUp || backUp, reverse, usable, labels);
		GD.Print($"[game-ui] received-item card up — '{title}' " +
			$"(icon {(icon is null ? $"0x{iconHash:X8} unresolved" : "resolved")}, " +
			$"root slide {rootSlide} = {(reverse ? "back" : "front")}, " +
			$"buttons slide {(buttons < 0 ? -1 : elements[buttons].Slide)} = " +
			$"{(usable ? "usable" : "NOT usable")})");
	}

	protected override void Route(Engine_UiSurfaceInput input)
	{
		View?.ScrollBack(input.Scroll, input.Delta);

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
	/// Boxes the guest stated through `GUI.*`, newest last — the call-driven source. Null or
	/// empty leaves the element read in charge. The newest wins, as the guest's queue shows the
	/// most recent box with the older ones behind it.
	/// </summary>
	public System.Collections.Generic.IReadOnlyList<Engine_UiGuiBoxes.Box>? Stated { get; set; }

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
		string body = elements[_joins.Texts[0]].Text ?? string.Empty;
		bool up = elements[_joins.Texts[0]].IsGloballyActive && body.Length != 0;

		// The stated call wins over the element read; an unrouted native (`DisplayInfoBoxParams`,
		// whose fields live in a Lua table) states no text and falls back to the element read.
		//
		// Only a message box is this art: the list also carries the question, the adjuster and
		// the quest card, each of which has its own presentation.
		if (Stated is { Count: > 0 } stated)
		{
			for (int i = stated.Count - 1; i >= 0; --i)
			{
				if (stated[i].Kind == Engine_UiGuiBoxes.BoxKind.Message)
				{
					body = stated[i].Text;
					up = body.Length != 0;
					break;
				}
			}
		}

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
