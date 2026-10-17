using System.Collections.Generic;
using Godot;
using Refii.Host;
using Refii.Ui;

namespace Refii.Game;

/// <summary>
/// The keyboard and mouse, as one more device on pad 0: keys become the buttons and stick the
/// guest already reads, and the mouse turns the camera through the right stick.
/// </summary>
/// <remarks>
/// THE GUEST HAS ONE CONTROL SCHEME AND IT IS THE PAD'S. `GameControlsCombatOnRelease` over
/// `GameControlsBase` binds every GameAction to an XboxButton (decoded from `sub_821894A8`), and
/// each receiver filters by context — so a key that presses the pad's button reaches exactly
/// what the button reaches, hold semantics included: holding melee blocks and charges a
/// flourish (`FlourishChargeTime`), holding magic charges the spell (`QuickCastTimeSecs`), holding
/// ranged aims and the release fires.
///
/// Fable III's PC scheme where it is known: 1/2/3 put the left mouse button on melee, ranged or
/// magic, and the items sit on the function keys. The rest are the usual PC keys. Every action
/// is rebindable as `key_&lt;action&gt;` in refii.toml — a comma list of Godot key names
/// (`OS.FindKeycodeFromString`) or MouseLeft / MouseRight / MouseMiddle / Mouse4 / Mouse5.
/// </remarks>
public partial class GameInput
{
	/// <summary>One rebindable action: its setting key, default keys, and what it presses.</summary>
	public readonly record struct KeyAction(string Name, string Defaults, string Description);

	/// <summary>Every action, in the order the launcher lists them.</summary>
	public static readonly KeyAction[] KeyActions =
	{
		new("move_forward", "W", "Move forward (left stick up)."),
		new("move_back", "S", "Move back (left stick down)."),
		new("move_left", "A", "Move left (left stick left)."),
		new("move_right", "D", "Move right (left stick right)."),
		new("walk", "Alt", "Hold to walk: the stick at half deflection."),
		new("sprint", "Shift", "Hold to sprint (A held)."),
		new("interact", "E,Space,Enter", "Interact, talk, pick up; accept in menus (A)."),
		new("attack", "MouseLeft", "Attack with the selected mode: melee, ranged or magic. Hold " +
			"to block and charge a flourish, to aim, or to charge a spell."),
		new("melee_mode", "1", "Put the attack button on melee (Fable III's 1)."),
		new("ranged_mode", "2", "Put the attack button on the ranged weapon (Fable III's 2)."),
		new("magic_mode", "3", "Put the attack button on magic (Fable III's 3)."),
		new("melee", "F", "Melee directly, whatever the mode (X)."),
		new("ranged", "R", "Ranged directly, whatever the mode (Y)."),
		new("magic", "C", "Magic directly, whatever the mode (B)."),
		new("target_lock", "MouseRight", "Hold to lock on to a target (left trigger)."),
		new("first_person", "MouseMiddle,Z", "Hold for the first-person view (left bumper)."),
		new("spells_menu", "Ctrl", "Hold to choose a spell (right trigger)."),
		new("expression_wheel", "Q", "Hold for the expression wheel."),
		new("camera_mode", "V", "Toggle the camera mode (right stick click)."),
		new("pause", "Escape,Tab", "Pause and open the book (Start). Escape is back in menus."),
		new("back", "Escape,Backspace", "Back out of a menu (B). Only in menus."),
		new("item_up", "F1,Up", "Item slot up / menu up (D-pad up)."),
		new("item_down", "F2,Down", "Item slot down / menu down (D-pad down)."),
		new("item_left", "F3,Left", "Item slot left / menu left (D-pad left)."),
		new("item_right", "F4,Right", "Item slot right / menu right (D-pad right)."),
		new("tab_prev", "PageUp,BracketLeft", "Previous tab in a menu (left bumper)."),
		new("tab_next", "PageDown,BracketRight", "Next tab in a menu (right bumper)."),
	};

	/// <summary>The attack button's mode — Fable III's 1, 2 and 3.</summary>
	private enum AttackMode { Melee, Ranged, Magic }

	private static AttackMode _attackMode = AttackMode.Melee;

	/// <summary>Each action's bound inputs, parsed from refii.toml with the defaults beneath.</summary>
	private static readonly Dictionary<string, List<BoundInput>> _bindings = new();

	/// <summary>One bound input: a key or a mouse button.</summary>
	private readonly record struct BoundInput(Key Key, MouseButton Mouse);

	/// <summary>
	/// Whether the player is in the world with nothing holding the input: no front end, pause
	/// book, owned screen, box or card. Decides what Escape and the mouse buttons mean, and
	/// whether the mouse is captured for the camera. Once per frame.
	/// </summary>
	public static bool Playing { get; private set; }

	/// <summary>Mouse movement since the last frame, while captured.</summary>
	private static Vector2 _mouseMotion;

	/// <summary>
	/// The mouse's recent movement, timestamped, for the average over one camera tick. A box,
	/// not an exponential: every count is spent exactly once, within one tick of its arrival,
	/// with no tail — the guest's own camera already smooths, and a second filter on top of it
	/// is what read as acceleration.
	/// </summary>
	private static readonly Queue<(double At, Vector2 Motion)> _mouseWindow = new();

	private static double _mouseClock;

	/// <summary>One SACCamera tick: `Timing.GetTickRate()` is 15, and the camera keeps only the
	/// last ROTATE action of a tick (`ProcessGameAction` assigns `RotateRight`).</summary>
	private const double CameraTick = 1.0 / 15.0;

	/// <summary>
	/// Degrees of camera turn per mouse count at sensitivity 1. 0.15 is the common shooter
	/// default (about 2400 counts — 3 inches at 800 DPI — per turn).
	/// </summary>
	private const float DegreesPerCount = 0.15f;

	/// <summary>
	/// The guest's full-stick turn rate: SACCamera's `RotateRight = -x * (RotateSpeed / 15) *
	/// (0.5 + CameraSensitivity)` degrees per tick at 15 ticks a second (stringandcagecamera.lua
	/// 2977), with the profile's sensitivity at its middle, 0.5. RotateSpeed ships at 120; the
	/// pointer feel (`CameraFeel:pointer` in the tick pump) raises it to 720.
	/// </summary>
	private const float PadStickDegreesPerSecond = 120f;

	private const float PointerStickDegreesPerSecond = 720f;

	/// <summary>The rate full deflection turns at now: the pointer's only once the game VM has
	/// confirmed it took the pointer feel, so a refused feel never leaves the mouse six times
	/// too slow.</summary>
	private static float _stickDegreesPerSecond = PadStickDegreesPerSecond;

	/// <summary>The feel last posted (true = pointer), or null when none stands.</summary>
	private static bool? _cameraFeelPosted;

	private static bool _cameraFeelPending;
	private static ulong _cameraFeelSeq;
	private static double _cameraFeelRetry;

	/// <summary>Reads `key_*` from the values the settings pass already loaded.</summary>
	private static void ReadKeyBindings(IReadOnlyDictionary<string, string> values)
	{
		foreach (KeyAction action in KeyActions)
		{
			string raw = Refii.Core.Engine_RefiiConfig.String(values, "key_" + action.Name,
				action.Defaults);
			var inputs = new List<BoundInput>();
			foreach (string part in raw.Split(','))
			{
				if (ParseInput(part.Trim()) is { } input)
				{
					inputs.Add(input);
				}
			}

			_bindings[action.Name] = inputs;
		}
	}

	private static BoundInput? ParseInput(string name)
	{
		switch (name.ToLowerInvariant())
		{
			case "":
				return null;
			case "mouseleft":
			case "mouse1":
				return new BoundInput(Key.None, MouseButton.Left);
			case "mouseright":
			case "mouse2":
				return new BoundInput(Key.None, MouseButton.Right);
			case "mousemiddle":
			case "mouse3":
				return new BoundInput(Key.None, MouseButton.Middle);
			case "mouse4":
				return new BoundInput(Key.None, MouseButton.Xbutton1);
			case "mouse5":
				return new BoundInput(Key.None, MouseButton.Xbutton2);
		}

		Key key = OS.FindKeycodeFromString(name);
		return key == Key.None ? null : new BoundInput(key, MouseButton.None);
	}

	/// <summary>Whether any input bound to an action is down. Mouse buttons count only while the
	/// mouse is captured — a free cursor's clicks belong to the menus.</summary>
	private static bool Down(string action)
	{
		if (!_bindings.TryGetValue(action, out List<BoundInput>? inputs))
		{
			return false;
		}

		foreach (BoundInput input in inputs)
		{
			if (input.Key != Key.None
				? Godot.Input.IsKeyPressed(input.Key) || Godot.Input.IsPhysicalKeyPressed(input.Key)
				: !Engine_UiPointer.Free && Godot.Input.IsMouseButtonPressed(input.Mouse))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// The play state, the mouse capture and the attack mode, once per frame. The cursor is
	/// captured for the camera only in play with the window focused; anything that takes the
	/// input frees it for the menus.
	/// </summary>
	private void UpdateKeyboardMouse(bool devices)
	{
		bool menuOwns = ScreenMenuOwnsInput || ModalBoxOwnsInput || MenuBoxOwnsInput ||
			ReceiveItemsOwnsInput || DisplayMessageBoxUp || HostBridge.FrontEndGuiIsUp;
		Playing = HostBridge.IsInGame && !menuOwns && !MaskPauseInput &&
			HostBridge.GetPauseState() is not { ModalMode: not 1 };

		// An open embedded window (the F4 dev menu, its Tracy panels) wants the cursor.
		bool windowOpen = GetTree()?.Root?.GetEmbeddedSubwindows().Count > 0;
		// The expression wheel and the spell blade are pointed at: the cursor is free while one
		// is held, and the camera stands still under it.
		bool overlay = QuickWheelHeld || SpellsMenuHeld;
		bool capture = devices && Playing && !overlay && !windowOpen && !GameDevMenu.KeyboardCaptured;
		Godot.Input.MouseModeEnum wanted =
			capture ? Godot.Input.MouseModeEnum.Captured : Godot.Input.MouseModeEnum.Visible;
		if (Godot.Input.MouseMode != wanted)
		{
			Godot.Input.MouseMode = wanted;
			_mouseMotion = Vector2.Zero;
			_mouseWindow.Clear();
		}

		UpdateCameraFeel();

		Engine_UiKeyGlyphs.LabelFor ??= KeyLabel;
		Engine_UiKeyGlyphs.Update();

		if (Down("melee_mode"))
		{
			_attackMode = AttackMode.Melee;
		}
		else if (Down("ranged_mode"))
		{
			_attackMode = AttackMode.Ranged;
		}
		else if (Down("magic_mode"))
		{
			_attackMode = AttackMode.Magic;
		}
	}

	/// <summary>
	/// The camera's feel follows the device: the mouse turns it unsmoothed and with room for a
	/// flick, the pad as shipped. Posted on every device switch and on entering a game (the game
	/// VM, and the scripts' constants with it, are rebuilt per game); a refusal is retried.
	/// </summary>
	private void UpdateCameraFeel()
	{
		if (!HostBridge.IsInGame)
		{
			_cameraFeelPosted = null;
			_cameraFeelPending = false;
			_stickDegreesPerSecond = PadStickDegreesPerSecond;
			return;
		}

		bool pointer = Engine_UiInputMode.KeyboardMouse;
		_cameraFeelRetry -= GetProcessDeltaTime();
		if (_cameraFeelPosted != pointer && _cameraFeelRetry <= 0.0)
		{
			_cameraFeelSeq = HostBridge.DebugCallResult()?.Seq ?? 0;
			HostBridge.PostFrontEndAction(pointer ? "CameraFeel:pointer" : "CameraFeel:pad");
			_cameraFeelPosted = pointer;
			_cameraFeelPending = true;
			_stickDegreesPerSecond = PadStickDegreesPerSecond;
		}

		if (_cameraFeelPending && HostBridge.DebugCallResult() is { } result &&
			result.Seq != _cameraFeelSeq)
		{
			_cameraFeelPending = false;
			bool took = result.R3 == 1;
			_stickDegreesPerSecond = took && _cameraFeelPosted == true
				? PointerStickDegreesPerSecond
				: PadStickDegreesPerSecond;
			if (!took)
			{
				_cameraFeelPosted = null;
				_cameraFeelRetry = 1.0;
			}
		}
	}

	/// <summary>
	/// The key a pad button stands for right now, for the keycap prompts: the first key bound to
	/// the action that presses that button, in the context that has the input — B is magic in
	/// the world and back in a menu.
	/// </summary>
	public static string? KeyLabel(string button) => button switch
	{
		"A" => Shown("interact"),
		"B" => Shown(Playing ? "magic" : "back"),
		"X" => Shown("melee"),
		"Y" => Shown("ranged"),
		"LB" => Shown(Playing ? "first_person" : "tab_prev"),
		"RB" => Shown("tab_next"),
		"LT" => Shown("target_lock"),
		"RT" => Shown("spells_menu"),
		"Start" => Shown("pause"),
		"Back" => Shown("expression_wheel"),
		"DPad" => Playing ? "F1-F4" : "Arrows",
		// In a menu the stick prompt sits beside arrows the mouse clicks: no key to show.
		"LS" => Playing ? "WASD" : string.Empty,
		"RS" => Playing ? "Mouse" : "Wheel",
		_ => null,
	};

	/// <summary>The first input bound to an action, in its short printed form.</summary>
	private static string Shown(string action)
	{
		if (!_bindings.TryGetValue(action, out List<BoundInput>? inputs) || inputs.Count == 0)
		{
			return "-";
		}

		BoundInput first = inputs[0];
		return first.Mouse switch
		{
			MouseButton.Left => "LMB",
			MouseButton.Right => "RMB",
			MouseButton.Middle => "MMB",
			MouseButton.Xbutton1 => "M4",
			MouseButton.Xbutton2 => "M5",
			_ => first.Key switch
			{
				Key.Escape => "Esc",
				Key.Backspace => "Bksp",
				Key.Pageup => "PgUp",
				Key.Pagedown => "PgDn",
				Key.Bracketleft => "[",
				Key.Bracketright => "]",
				Key.Enter => "Enter",
				_ => OS.GetKeycodeString(first.Key),
			},
		};
	}

	/// <summary>Captured mouse motion, for the camera.</summary>
	private static void FeedMouseMotion(InputEvent @event)
	{
		if (@event is InputEventMouseMotion motion &&
			Godot.Input.MouseMode == Godot.Input.MouseModeEnum.Captured)
		{
			_mouseMotion += motion.Relative;
		}
	}

	/// <summary>The keyboard and mouse as pad 0's buttons, triggers and sticks.</summary>
	private static void KeyboardMouseState(ref PadState state, double delta)
	{
		// Typing into the dev menu is not play.
		if (GameDevMenu.KeyboardCaptured)
		{
			_mouseMotion = Vector2.Zero;
			return;
		}

		uint buttons = 0;
		if (Down("interact") || Down("sprint")) buttons |= ButtonA;
		if (Down("melee")) buttons |= ButtonX;
		if (Down("ranged")) buttons |= ButtonY;
		if (Down("magic")) buttons |= ButtonB;
		if (Down("attack"))
		{
			buttons |= _attackMode switch
			{
				AttackMode.Ranged => ButtonY,
				AttackMode.Magic => ButtonB,
				_ => ButtonX,
			};
		}

		if (Down("first_person")) buttons |= LeftShoulder;
		if (Down("tab_prev")) buttons |= LeftShoulder;
		if (Down("tab_next")) buttons |= RightShoulder;
		if (Down("expression_wheel")) buttons |= Back;
		if (Down("camera_mode")) buttons |= RightThumb;
		if (Down("item_up")) buttons |= DpadUp;
		if (Down("item_down")) buttons |= DpadDown;
		if (Down("item_left")) buttons |= DpadLeft;
		if (Down("item_right")) buttons |= DpadRight;

		// Escape is bound to both: in play it pauses, in a menu it backs out. Tab pauses in both,
		// which on the pause book is the guest's own Start-to-resume.
		if (Playing)
		{
			if (Down("pause")) buttons |= Start;
		}
		else
		{
			if (Down("back")) buttons |= ButtonB;
			if (Down("pause") && !Down("back")) buttons |= Start;
		}

		state.Buttons |= buttons;
		if (Down("target_lock")) state.LeftTrigger = 255;
		if (Down("spells_menu")) state.RightTrigger = 255;

		// THE KEYS AS THE LEFT STICK: full deflection, the diagonal kept to unit length, half
		// with walk held.
		var move = new Vector2(
			(Down("move_right") ? 1f : 0f) - (Down("move_left") ? 1f : 0f),
			(Down("move_forward") ? 1f : 0f) - (Down("move_back") ? 1f : 0f));
		if (move != Vector2.Zero)
		{
			move = move.Normalized() * (Down("walk") ? 0.5f : 1f);
			state.LeftX = Dominant(state.LeftX, Mathf.RoundToInt(move.X * 32767f));
			state.LeftY = Dominant(state.LeftY, Mathf.RoundToInt(move.Y * 32767f));
		}

		MouseCamera(ref state, delta);
	}

	/// <summary>
	/// The mouse as the camera. The guest keeps no yaw angle — SACCamera turns by a rate per tick
	/// from the stick — so the mouse's turn this frame becomes the stick deflection that turns
	/// the camera by the same amount: rate / full-stick rate, then the guest's stick curve undone
	/// Capped at full deflection: 720°/s under the pointer feel, the stick's 120°/s without it.
	/// Capped at full deflection: a flick faster than the stick's 120°/s is spread over frames.
	/// Averaged over the last camera tick, since the camera takes one ROTATE per tick.
	/// </summary>
	private static void MouseCamera(ref PadState state, double delta)
	{
		Vector2 motion = _mouseMotion;
		_mouseMotion = Vector2.Zero;
		if (delta <= 0.0)
		{
			return;
		}

		_mouseClock += delta;
		if (motion != Vector2.Zero)
		{
			_mouseWindow.Enqueue((_mouseClock, motion));
		}

		Vector2 recent = Vector2.Zero;
		while (_mouseWindow.Count > 0 && _mouseWindow.Peek().At <= _mouseClock - CameraTick)
		{
			_mouseWindow.Dequeue();
		}

		foreach ((double _, Vector2 counts) in _mouseWindow)
		{
			recent += counts;
		}

		Vector2 rate = recent * (DegreesPerCount * MouseSensitivity / (float)CameraTick);

		// Right is the stick's right; up (a negative screen y) is the stick's up. Vertical is no
		// rate but `ManualHeightOffset += -y * 0.05 * (0.5 + s)` behind a Lua deadzone of 0.1
		// (stringandcagecamera.lua 2929..2938), so a slow pull is lifted past it.
		int x = StickFor(rate.X / _stickDegreesPerSecond);
		// Height has no degrees and RotateSpeed does not touch it, so it keeps the stick's scale.
		int y = StickFor(LiftPastDeadzone(-rate.Y / PadStickDegreesPerSecond, 0.1f));
		state.RightX = Dominant(state.RightX, x);
		state.RightY = Dominant(state.RightY, y);
	}

	/// <summary>Maps a nonzero value onto (deadzone, 1], keeping its sign and order.</summary>
	private static float LiftPastDeadzone(float v, float deadzone)
	{
		float magnitude = Mathf.Min(Mathf.Abs(v), 1f);
		return magnitude < 0.002f ? 0f : Mathf.Sign(v) * (deadzone + 0.001f + ((1f - deadzone) * magnitude));
	}

	/// <summary>The raw stick value the guest's deadzone curve maps to <paramref name="v"/>.</summary>
	private static int StickFor(float v)
	{
		float magnitude = Mathf.Min(Mathf.Abs(v), 1f);
		if (magnitude < 0.002f)
		{
			return 0;
		}

		return Mathf.RoundToInt(Mathf.Sign(v) * (0.3f + (0.7f * magnitude)) * 32767f);
	}
}
