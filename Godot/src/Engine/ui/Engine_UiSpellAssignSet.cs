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
	private Engine_UiSpellAssign? _blade;
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
	public void Follow(bool revealed)
	{
		if (Host is null || !revealed)
		{
			return;
		}

		_spells = Engine_UiSource.Current.GetSpellAssign();

		bool bladeUp = _spells.AbilitiesMode == 1;
		if (bladeUp && (_blade is null || !GodotObject.IsInstanceValid(_blade)))
		{
			_blade = Ensure(Engine_UiScreenInput.AbilitiesPresentation);
		}

		if (_blade is not null && GodotObject.IsInstanceValid(_blade))
		{
			_blade.Visible = bladeUp;
			if (bladeUp)
			{
				_blade.SetLive(_spells);
			}
		}

		// The wheel opens over the running game with no screen behind it, so it is gated on its
		// own flag, never a screen id. Both are suppressed inside their books, so one copy draws.
		bool wheelUp = _spells.WheelOpen;
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
	public void Drive(Engine_UiSurfaceInput input)
	{
		if (_blade is null || !GodotObject.IsInstanceValid(_blade))
		{
			return;
		}

		_vertical ??= PageClock();
		_horizontal ??= PageClock();

		// The blade's row index runs UPWARD where `GameInput.Direction` is a list direction (up
		// -1, down +1): `abilitiesscreen.bgf` and `hud.bgf` both author `Spell1` at y -160 and
		// `Spell5` at y +160, so slot 0 is the bottom and `Place` puts the frame at
		// `-160 + 80*Row`. `spellassigning.dis` agrees — main.3 onUp `row + 1`, main.4 onDown
		// `row - 1`. Negated here, not in `Move`, whose sign is already the blade's.
		int vertical = -input.Direction;
		for (int step = _vertical.Steps(input.Direction, input.Delta);
			step > 0; --step)
		{
			_blade.Move(vertical);
		}

		int horizontal = input.Horizontal;
		for (int step = _horizontal.Steps(horizontal, input.Delta);
			step > 0; --step)
		{
			_blade.Choose(horizontal);
		}

		if (input.Accept)
		{
			_blade.Propagate();
		}
		else if (input.Back)
		{
			Engine_UiSource.Current.ScreenAction(Engine_UiScreenInput.Back);
		}
	}
}
