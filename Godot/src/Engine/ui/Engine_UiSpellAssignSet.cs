using Godot;

namespace Refii.Ui;

/// <summary>
/// The owned SPELL-ASSIGNMENT widget — the abilities blade and the quick wheel.
///
/// Two instances, one controller, as the original has it. The hosts author the widget in
/// different places — `hud.bgf` at canvas (−315, 9) scaled 0.8 for the wheel,
/// `abilitiesscreen.bgf` inside the book's fourth page — so each clone keeps its own file's
/// authored transform. Behind both is one CECSpellManager, sampled once per frame and shared.
///
/// The vocabulary is the guest's: moving the SLOT writes nothing, moving the CAROUSEL writes
/// immediately (`sub_82541F30` per press), and A is not a confirm — it propagates the current
/// slot's will down into every later slot that offers it.
///
/// B is still the guest's: the blade's open state is its mode byte at `screen14+620`, with
/// exactly two writers, and closing it is `sub_826C25C0` through the screen's own handler.
/// </summary>
public sealed class Engine_UiSpellAssignSet
{
	/// <summary>The host the widgets are parented to and take their art from.</summary>
	public Engine_UiPresentationHost? Host { get; set; }

	/// <summary>The presentation the QUICK WHEEL's copy is authored in.</summary>
	public const string WheelPresentation = "hud";

	/// <summary>The owned assignment widget, one per host — the blade and the quick wheel.</summary>
	private Engine_UiSpellAssign? _wheel;

	/// <summary>This frame's assignment sample, taken once and shared by both hosts.</summary>
	private Engine_UiModel.SpellAssignState _spells;

	/// <summary>Auto-repeat clocks for the blade, one per axis — see <see cref="Engine_UiScreenLevel"/>.</summary>
	private Engine_UiNavigationClock? _vertical;
	private Engine_UiNavigationClock? _horizontal;

	/// <summary>
	/// A clock on the PAGE gate's numbers rather than the menu's — see
	/// <see cref="Engine_UiScreenInput.PageFirstRepeat"/>.
	/// </summary>
	private static Engine_UiNavigationClock PageClock() => new()
	{
		FirstInterval = Engine_UiScreenInput.PageFirstRepeat,
		SubsequentInterval = Engine_UiScreenInput.PageRepeat,
	};

	/// <summary>Builds the owned widget from one host's pattern book, keeping that file's own
	/// authored transform.</summary>
	private Engine_UiSpellAssign? Ensure(string presentation)
	{
		if (Host?.PatternBookFor(presentation) is not { } art)
		{
			return null;
		}

		var widget = new Engine_UiSpellAssign
		{
			Name = $"SpellAssign_{presentation}",
			Book = art,
			Visible = false,
			ResolveTexture = path => Host.LoadTexture(path),
		};

		widget.Build();
		if (!widget.HasArt)
		{
			widget.QueueFree();
			return null;
		}

		Host.AddChild(widget);
		return widget;
	}

	/// <summary>
	/// Stands the owned widget wherever the guest says one is up, and feeds it. The quick-wheel
	/// half is driven here because its open flag is the guest's (`[GUI+28]+36` for player one,
	/// `+37` for two) and the widget is the same one.
	/// </summary>
	private string _gateReport = string.Empty;

	public void Follow(bool revealed)
	{
		if (Host is null || !revealed)
		{
			return;
		}

		_spells = Engine_UiSource.Current.GetSpellAssign();

		// THE BLADE IS THE GUEST'S. Its widget (`screen14+200`, bound by `sub_826C2480` at
		// 0x826c2510) writes every slide, icon texture and adjuster position onto the live
		// presentation, and the abilities book's clone follows those statements; the host only
		// forwards the levers (DriveBlade). The wheel below has no presentation of its own to
		// follow, so that one stays drawn here.
		// The wheel opens over the running game with no screen behind it, so it is gated on its
		// own flag, never a screen id. Both are suppressed inside their books, so one copy draws.
		// WHY THE WHEEL DID NOT OPEN. The right trigger IS the binding — the loaded control
		// scheme `GameControlsCombatOnRelease` is an alias onto a record that overrides the
		// spells-menu action's input to XboxButton 16, the right trigger, over a base scheme
		// that uses the right bumper — and the pad reaches the guest (the input log shows the
		// trigger at full travel). So a refusal is the GUEST'S, and its gates are invisible from
		// here: `sub_826B4448` needs TWO spell TYPES both AWARDED and PURCHASED at POWER
		// LEVEL 0, `sub_8253E220` needs one assigned and magic enabled, and the flow state and
		// player mode must both be right.
		//
		// THE FIVE-WIDE AXIS IS THE POWER (CHARGE) LEVEL, not a ring and not a d-pad slot:
		// the guest's own planes are `mgr+156+5*type+level` (awarded) and `mgr+196+5*type+level`
		// (purchased), and the gate's cursor takes level 0 alone. So `0:1` means one will usable
		// at the first charge level — a correct refusal, because there is nothing to choose
		// between. Five levels each holding the SAME single will would read `1,1,1,1,1` and be
		// refused too; it is spell TYPES at one level that the gate counts.
		//
		// The offers per level are already in the state we read, so the count that gate turns on
		// can be said out loud. Printed only when it changes.
		bool wheelUp = _spells.WheelOpen;
		if (_spells.Known)
		{
			var offers = new System.Collections.Generic.List<string>();
			for (int level = 0; level < Engine_UiModel.SpellAssignState.Slots; ++level)
			{
				offers.Add($"L{level}:{_spells.Choices(level).Count}");
			}

			string report = $"open={wheelUp} offers {string.Join(",", offers)}";
			if (report != _gateReport)
			{
				_gateReport = report;
				GD.Print($"[spell-assign] {report} — offers are per POWER LEVEL; the guest " +
					"refuses below two spell types awarded AND purchased at level 0");
			}
		}

		if (wheelUp && (_wheel is null || !GodotObject.IsInstanceValid(_wheel)))
		{
			_wheel = Ensure(WheelPresentation);
			var offered = new System.Collections.Generic.List<string>();
			var assigned = new System.Collections.Generic.List<string>();
			for (int i = 0; i < Engine_UiModel.SpellAssignState.Slots; ++i)
			{
				offered.Add(_spells.Choices(i).Count.ToString());
				assigned.Add(_spells.On(i)?.ToString() ?? "-");
			}
			GD.Print($"[spell-assign] wheel opened: known={_spells.Known} offered per level=" +
				$"{string.Join(",", offered)} assigned={string.Join(",", assigned)}");
		}

		if (_wheel is not null && GodotObject.IsInstanceValid(_wheel))
		{
			_wheel.Visible = wheelUp;
			if (wheelUp)
			{
				_wheel.SetLive(_spells);
			}
		}
	}

	/// <summary>One frame of the blade: up/down move the slot, left/right move the carousel and
	/// commit, A propagates, B closes the level through the guest's own mode byte.</summary>
	/// <summary>
	/// Forwards the blade's levers to the guest's own handler: `sub_826C2958` routes 28/29 to
	/// the row, 30/31 to the carousel (which commits as it moves, `sub_826B5330`), 36 to the
	/// copy-down (`sub_826B4EF8`) and 37 to the close — and the widget repaints the presentation
	/// the book's clone follows. Repeat cadence is the page clock's, as for every held direction.
	/// </summary>
	public void DriveBlade(Engine_UiSurfaceInput input)
	{
		_vertical ??= PageClock();
		_horizontal ??= PageClock();

		for (int step = _vertical.Steps(input.Direction, input.Delta); step > 0; --step)
		{
			Engine_UiSource.Current.ScreenAction(
				input.Direction < 0 ? Engine_UiScreenInput.Up : Engine_UiScreenInput.Down);
		}

		for (int step = _horizontal.Steps(input.Horizontal, input.Delta); step > 0; --step)
		{
			Engine_UiSource.Current.ScreenAction(
				input.Horizontal < 0 ? Engine_UiScreenInput.Left : Engine_UiScreenInput.Right);
		}

		if (input.Accept)
		{
			Engine_UiSource.Current.ScreenAction(Engine_UiScreenInput.Select);
		}
		else if (input.Back)
		{
			Engine_UiSource.Current.ScreenAction(Engine_UiScreenInput.Back);
		}
	}
}
