#if TOOLS
using System.Collections.Generic;
using Godot;
using Refii.Ui;

namespace Refii.Assets.UI;

/// <summary>
/// The UI tab's Screens page: any of the game's screens as the running game draws it — its owned
/// book stood on the preview's host by the game's own code (<see cref="Engine_UiScreenStand"/>), the
/// file's rest state and its slides playing. Nothing comes from a guest, so what a value-filled
/// screen shows here is its file and its statements: a component's slide can be stated, the open
/// replayed, slowed, paused and stepped. A presentation exported from the Presentations page is
/// what the mount serves, so its edit is what stands here once the host reloads.
/// </summary>
[Tool]
public partial class UiStudioScreensView : VBoxContainer
{
	private readonly UiStudioPreview _preview;
	private Engine_UiScreenStand? _stand;
	private bool _active;

	private OptionButton _screens = null!;
	private OptionButton _owners = null!;
	private OptionButton _slide = null!;
	private Label _detail = null!;
	private HSlider? _speed;
	private Button? _pause;
	private readonly List<Engine_UiScreenTemplate> _list = new();
	private IReadOnlyList<Engine_UiScreenStand.Owner> _ownerList = System.Array.Empty<Engine_UiScreenStand.Owner>();
	private bool _filling;

	public UiStudioScreensView(UiStudioPreview preview)
	{
		_preview = preview;
		_preview.HostLoaded += () =>
		{
			// A reloaded host serves new files: books built from the old ones go.
			_stand?.Clear();
			_stand = null;
			if (_active)
			{
				StandSelected();
			}
		};
	}

	public override void _Ready()
	{
		AddThemeConstantOverride("separation", 8);
		AddChild(ModStudioUiStyles.Note("Every screen as the game draws it: its book built by the game's own code from its file and " +
			"template, its slides playing as they do in the game. Pick a screen, replay its open, slow or pause it, and state a " +
			"component's slide the way a page provider does. Values the game fills at run time (names, prices) show the file's own text."));

		_screens = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_screens.ItemSelected += _ => StandSelected();
		AddChild(Row("Screen", _screens));

		_owners = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "A component that owns slides" };
		_owners.ItemSelected += _ => FillSlides();
		AddChild(Row("Component", _owners));

		_slide = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "The slide this component stands on" };
		_slide.ItemSelected += _ => StateSlide();
		AddChild(Row("Slide", _slide));

		_detail = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		AddChild(_detail);
	}

	private static HBoxContainer Row(string label, Control control)
	{
		var row = new HBoxContainer();
		row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(90, 0) });
		row.AddChild(control);
		return row;
	}

	public void Activate()
	{
		_active = true;
		_preview.EnsureHost();

		var replay = new Button { Text = "⟲ Replay", TooltipText = "Open the screen again: every slide plays from its start" };
		replay.Pressed += () => _stand?.Replay();
		_preview.Toolbar.AddChild(replay);

		_pause = new Button { Text = "⏸ Pause", ToggleMode = true };
		_pause.Toggled += paused =>
		{
			if (_stand?.Book is { } book)
			{
				book.PlaybackSpeed = paused ? 0 : (_speed?.Value ?? 1);
			}
		};
		_preview.Toolbar.AddChild(_pause);

		var step = new Button { Text = "▸ Step", TooltipText = "Move a paused screen on by one frame (33 ms)" };
		step.Pressed += () => _stand?.Book?.StepPlayback(33);
		_preview.Toolbar.AddChild(step);

		_preview.Toolbar.AddChild(new Label { Text = "Speed" });
		_speed = new HSlider { MinValue = 0.05, MaxValue = 2, Step = 0.05, Value = 1, CustomMinimumSize = new Vector2(120, 0) };
		_speed.ValueChanged += value =>
		{
			if (_stand?.Book is { } book && _pause?.ButtonPressed != true)
			{
				book.PlaybackSpeed = value;
			}
		};
		_preview.Toolbar.AddChild(_speed);

		var openers = new CheckBox
		{
			Text = "Openers play",
			ButtonPressed = true,
			TooltipText = "On: every slide plays, as the screen's own code plays what it shows when it opens. " +
				"Off: an authored-paused slide waits at its start, where the game's walk leaves it until something plays it.",
		};
		openers.Toggled += on =>
		{
			if (_stand is { } stand)
			{
				stand.PlayEntries = on;
				stand.Replay();
			}
		};
		_preview.Toolbar.AddChild(openers);

		var clear = new Button { Text = "Rest slides", TooltipText = "Drop every stated slide: the file's rest state" };
		clear.Pressed += () =>
		{
			_stand?.Slides.Clear();
			FillSlides();
		};
		_preview.Toolbar.AddChild(clear);

		FillScreens();
		StandSelected();
	}

	public void Deactivate()
	{
		_active = false;
		_stand?.Clear();
		_stand = null;
		foreach (Node child in _preview.Toolbar.GetChildren())
		{
			child.QueueFree();
		}

		foreach (Node child in _preview.Below.GetChildren())
		{
			child.QueueFree();
		}

		_speed = null;
		_pause = null;
	}

	private void FillScreens()
	{
		_filling = true;
		int selected = _screens.Selected;
		_screens.Clear();
		_list.Clear();
		foreach (Engine_UiScreenTemplate template in Engine_UiScreenStand.Screens())
		{
			_list.Add(template);
			_screens.AddItem($"{template.ScreenId} · {template.Name} ({template.Presentation})");
		}

		_screens.Selected = selected >= 0 && selected < _list.Count ? selected : (_list.Count > 0 ? 0 : -1);
		_filling = false;
	}

	private void StandSelected()
	{
		if (_filling || !_active || !_preview.Loaded || _preview.Host is not { } host ||
			_screens.Selected < 0 || _screens.Selected >= _list.Count)
		{
			return;
		}

		_stand ??= new Engine_UiScreenStand(host);
		Engine_UiScreenTemplate template = _list[_screens.Selected];
		_stand.Stand(template.ScreenId);
		_ownerList = System.Array.Empty<Engine_UiScreenStand.Owner>();
		_owners.Clear();
		_slide.Clear();
		_preview.SetStatus($"Screen {template.ScreenId} '{template.Name}' — building its book from '{template.Presentation}'…");
	}

	public override void _Process(double delta)
	{
		if (!_active || _stand is null)
		{
			return;
		}

		_stand.Tick();
		if (_stand.Book is not { } book)
		{
			return;
		}

		if (_pause is not null && !_pause.ButtonPressed && _speed is not null)
		{
			book.PlaybackSpeed = _speed.Value;
		}

		if (_ownerList.Count == 0)
		{
			_ownerList = _stand.Owners();
			FillOwners();
			Engine_UiScreenTemplate template = _list[_screens.Selected];
			_preview.SetStatus($"Screen {template.ScreenId} '{template.Name}' as the game draws it — " +
				$"{_ownerList.Count} component(s) with slides" +
				(template.RestFromFile ? "." : "; this screen does not stand on its file in the game yet."));
		}
	}

	private void FillOwners()
	{
		_owners.Clear();
		foreach (Engine_UiScreenStand.Owner owner in _ownerList)
		{
			_owners.AddItem($"{owner.Path} #{owner.Ordinal} ({owner.SlideNames.Count})");
		}

		if (_ownerList.Count > 0)
		{
			_owners.Selected = 0;
		}

		FillSlides();
	}

	private void FillSlides()
	{
		_filling = true;
		_slide.Clear();
		_detail.Text = string.Empty;
		if (_stand is { } stand && _owners.Selected >= 0 && _owners.Selected < _ownerList.Count)
		{
			Engine_UiScreenStand.Owner owner = _ownerList[_owners.Selected];
			_slide.AddItem("(rest — the file's own)");
			foreach (string name in owner.SlideNames)
			{
				_slide.AddItem(name);
			}

			_slide.Selected = stand.Slides.TryGetValue("#" + owner.Ordinal, out int stated) && stated > 0 &&
				stated <= owner.SlideNames.Count
					? stated
					: 0;
			_detail.Text = $"{owner.Path}: {string.Join(", ", owner.SlideNames)}";
		}

		_filling = false;
	}

	private void StateSlide()
	{
		if (_filling || _stand is not { } stand || _owners.Selected < 0 || _owners.Selected >= _ownerList.Count)
		{
			return;
		}

		string at = "#" + _ownerList[_owners.Selected].Ordinal;
		if (_slide.Selected <= 0)
		{
			stand.Slides.Remove(at);
		}
		else
		{
			stand.Slides[at] = _slide.Selected;
		}
	}
}
#endif
