namespace Refii.Ui;

using System;
using Godot;
using Refii.Game;

/// <summary>
/// The host's text entry: what a title shows when the guest asks the platform for text
/// (XamShowKeyboardUI). Typed from the keyboard into the line, or from the pad on the key grid —
/// the d-pad or left stick moves, A types the key, X deletes, Y is a space, LB shifts, START
/// accepts and B cancels — so a player on a controller alone can answer it. The pad is read from
/// <see cref="GameInput.Pad0"/> (the game's buttons, before any title mask), which a title must
/// keep from the guest while this is open.
/// </summary>
public partial class Engine_UiTextEntry : Control
{
	private const float ReferenceHeight = 720f;
	private const float PanelWidth = 560f;
	private const double RepeatDelay = 0.4;
	private const double RepeatEvery = 0.1;
	private const int StickDeadZone = 16000;

	private static readonly string[] Rows = { "1234567890", "qwertyuiop", "asdfghjkl'", "zxcvbnm-.!" };

	/// <summary>The action row under the letters: shift, space, delete, accept.</summary>
	private static readonly string[] Actions = { "Shift", "Space", "Del", "OK" };

	private PanelContainer _panel = null!;
	private Label _title = null!;
	private Label _description = null!;
	private LineEdit _line = null!;
	private readonly Button[,] _keys = new Button[Rows.Length + 1, 10];
	private int _row;
	private int _column;
	private bool _shift;
	private uint _wasDown;
	private Vector2I _heldDirection;
	private double _repeat;

	/// <summary>The accepted text.</summary>
	public event Action<string>? Accepted;

	/// <summary>Cancelled: the guest is answered with nothing.</summary>
	public event Action? Cancelled;

	public bool IsOpen => _panel.Visible;

	/// <summary>
	/// An entry is open somewhere: the keyboard is typing into its line, so the core stops reading
	/// keys as the pad (GameInput.KeyboardMouseState) — otherwise E/1/Space typed the grid's key as
	/// well, 3 deleted, 2 spaced and WASD moved the grid cursor. A controller still drives the grid.
	/// </summary>
	public static bool AnyOpen => _open > 0;

	private static int _open;

	public override void _ExitTree()
	{
		if (_panel.Visible)
		{
			--_open;
			_panel.Visible = false;
		}
	}

	public override void _Ready()
	{
		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;

		_panel = new PanelContainer { Name = "TextEntry", Visible = false };
		AddChild(_panel);
		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 10);
		_panel.AddChild(column);
		_title = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_description = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_line = new LineEdit { CaretBlink = true };
		_line.TextSubmitted += _ => Accept();
		column.AddChild(_title);
		column.AddChild(_description);
		column.AddChild(_line);

		var grid = new GridContainer { Columns = 10 };
		grid.AddThemeConstantOverride("h_separation", 4);
		grid.AddThemeConstantOverride("v_separation", 4);
		column.AddChild(grid);
		for (int row = 0; row < Rows.Length; ++row)
		{
			for (int key = 0; key < 10; ++key)
			{
				(int r, int k) = (row, key);
				Button button = Key(string.Empty, () => Press(r, k));
				grid.AddChild(button);
				_keys[row, key] = button;
			}
		}

		// The action row: each action spans the grid's width share it is laid out in.
		var actions = new HBoxContainer();
		actions.AddThemeConstantOverride("separation", 4);
		column.AddChild(actions);
		for (int action = 0; action < Actions.Length; ++action)
		{
			int a = action;
			Button button = Key(Actions[action], () => Press(Rows.Length, a));
			button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			actions.AddChild(button);
			_keys[Rows.Length, action] = button;
		}

		Relabel();
	}

	/// <summary>Opens the entry with the guest's title, description and default text, capped at <paramref name="maxLength"/> characters.</summary>
	public void Open(string title, string description, string text, int maxLength)
	{
		_title.Text = title;
		_description.Text = description;
		_description.Visible = description.Length > 0;
		_line.MaxLength = Math.Max(1, maxLength);
		_line.Text = text;
		_line.CaretColumn = text.Length;
		(_row, _column, _shift) = (1, 0, false);
		// Whatever the pad held when the entry opened (the A that asked for it) is not a key press.
		_wasDown = GameInput.Pad0.Buttons;
		_heldDirection = Vector2I.Zero;
		Relabel();
		if (!_panel.Visible)
		{
			++_open;
		}

		_panel.Visible = true;
		MouseFilter = MouseFilterEnum.Stop;
		_line.GrabFocus();
	}

	public override void _Process(double delta)
	{
		if (!_panel.Visible)
		{
			return;
		}

		Layout();
		ReadPad(delta);
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (_panel.Visible && @event is InputEventKey { Pressed: true, Keycode: Godot.Key.Escape })
		{
			Cancel();
			GetViewport().SetInputAsHandled();
		}
	}

	private void Layout()
	{
		// Legible at any window: the panel keeps its share of the screen's height.
		Vector2 size = GetViewportRect().Size;
		float scale = size.Y / ReferenceHeight;
		float width = Mathf.Min(PanelWidth * scale, size.X - (32f * scale));
		int fontSize = Mathf.RoundToInt(20f * scale);
		foreach (Control control in new Control[] { _title, _description, _line })
		{
			control.AddThemeFontSizeOverride("font_size", fontSize);
		}

		foreach (Button key in _keys)
		{
			if (key is not null)
			{
				key.AddThemeFontSizeOverride("font_size", fontSize);
				key.CustomMinimumSize = new Vector2(0f, 34f * scale);
			}
		}

		_panel.CustomMinimumSize = new Vector2(width, 0f);
		_panel.Size = new Vector2(width, _panel.GetCombinedMinimumSize().Y);
		_panel.Position = (size - _panel.Size) * 0.5f;
	}

	private void ReadPad(double delta)
	{
		uint down = GameInput.Pad0.Buttons;
		uint pressed = down & ~_wasDown;
		_wasDown = down;

		Vector2I direction = Direction(down, GameInput.Pad0.LeftX, GameInput.Pad0.LeftY);
		if (direction != _heldDirection)
		{
			_heldDirection = direction;
			_repeat = RepeatDelay;
			Move(direction);
		}
		else if (direction != Vector2I.Zero && (_repeat -= delta) <= 0.0)
		{
			_repeat = RepeatEvery;
			Move(direction);
		}

		if ((pressed & GameInput.ButtonA) != 0)
		{
			Press(_row, _column);
		}
		else if ((pressed & GameInput.ButtonX) != 0)
		{
			Delete();
		}
		else if ((pressed & GameInput.ButtonY) != 0)
		{
			Type(" ");
		}
		else if ((pressed & GameInput.LeftShoulder) != 0)
		{
			Shift();
		}
		else if ((pressed & GameInput.Start) != 0)
		{
			Accept();
		}
		else if ((pressed & GameInput.ButtonB) != 0)
		{
			Cancel();
		}
	}

	private static Vector2I Direction(uint buttons, int stickX, int stickY)
	{
		int x = (buttons & GameInput.DpadLeft) != 0 || stickX < -StickDeadZone ? -1 :
			(buttons & GameInput.DpadRight) != 0 || stickX > StickDeadZone ? 1 : 0;
		// Stick Y is up-positive, as XInput states it.
		int y = (buttons & GameInput.DpadUp) != 0 || stickY > StickDeadZone ? -1 :
			(buttons & GameInput.DpadDown) != 0 || stickY < -StickDeadZone ? 1 : 0;
		return new Vector2I(x, y);
	}

	private void Move(Vector2I direction)
	{
		if (direction == Vector2I.Zero)
		{
			return;
		}

		int rows = Rows.Length + 1;
		int columnsWas = Columns(_row);
		_row = (_row + direction.Y + rows) % rows;
		if (direction.Y != 0 && Columns(_row) != columnsWas)
		{
			// Between the letters and the action row, keep the same share of the width.
			_column = Math.Min(Columns(_row) - 1, _column * Columns(_row) / columnsWas);
		}

		_column = (_column + direction.X + Columns(_row)) % Columns(_row);
		Relabel();
	}

	private static int Columns(int row) => row < Rows.Length ? Rows[row].Length : Actions.Length;

	private void Press(int row, int column)
	{
		(_row, _column) = (row, column);
		if (row < Rows.Length)
		{
			string key = Rows[row][column].ToString();
			Type(_shift ? key.ToUpperInvariant() : key);
			if (_shift)
			{
				Shift();
			}

			Relabel();
			return;
		}

		switch (column)
		{
			case 0: Shift(); break;
			case 1: Type(" "); break;
			case 2: Delete(); break;
			default: Accept(); break;
		}
	}

	private void Type(string text)
	{
		if (_line.Text.Length + text.Length > _line.MaxLength)
		{
			return;
		}

		int caret = Math.Clamp(_line.CaretColumn, 0, _line.Text.Length);
		_line.Text = _line.Text.Insert(caret, text);
		_line.CaretColumn = caret + text.Length;
	}

	private void Delete()
	{
		int caret = Math.Clamp(_line.CaretColumn, 0, _line.Text.Length);
		if (caret > 0)
		{
			_line.Text = _line.Text.Remove(caret - 1, 1);
			_line.CaretColumn = caret - 1;
		}
	}

	private void Shift()
	{
		_shift = !_shift;
		Relabel();
	}

	private void Accept()
	{
		if (!_panel.Visible)
		{
			return;
		}

		Close();
		Accepted?.Invoke(_line.Text);
	}

	private void Cancel()
	{
		if (!_panel.Visible)
		{
			return;
		}

		Close();
		Cancelled?.Invoke();
	}

	private void Close()
	{
		if (_panel.Visible)
		{
			--_open;
		}

		_panel.Visible = false;
		MouseFilter = MouseFilterEnum.Ignore;
		_line.ReleaseFocus();
	}

	private void Relabel()
	{
		for (int row = 0; row < Rows.Length; ++row)
		{
			for (int key = 0; key < 10; ++key)
			{
				string letter = Rows[row][key].ToString();
				_keys[row, key].Text = _shift ? letter.ToUpperInvariant() : letter;
			}
		}

		for (int row = 0; row <= Rows.Length; ++row)
		{
			for (int key = 0; key < Columns(row); ++key)
			{
				// The pad's cursor: drawn as the pressed look, not Godot's focus, so the line keeps
				// the keyboard's focus and the pad never drives Godot's own focus navigation.
				_keys[row, key].ButtonPressed = row == _row && key == _column;
			}
		}

		_keys[Rows.Length, 0].Text = _shift ? "SHIFT" : "Shift";
	}

	private static Button Key(string text, Action pressed)
	{
		var button = new Button { Text = text, FocusMode = FocusModeEnum.None, ToggleMode = true };
		button.Pressed += pressed;
		return button;
	}
}
