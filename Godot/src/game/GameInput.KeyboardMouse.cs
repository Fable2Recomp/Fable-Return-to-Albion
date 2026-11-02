using System.Collections.Generic;
using Godot;
using Refii.Core;
using Refii.Host;
using Refii.Ui;

namespace Refii.Game;

/// <summary>
/// The keyboard and mouse, as one more device on pad 0: keys become the buttons and stick the
/// guest already reads, and the mouse turns the camera through the camera's own rotate path.
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
///
/// THE 360 HAS NO KEYBOARD TO REUSE. The control-scheme loader (`sub_82883298`) still builds the
/// PC era's KeyboardKey (InputType 1), MouseButton (2) and mouse-movement (6) bindings, and the
/// GDB's Base scheme still binds keys, but the device scan (`sub_821A8CC8`) recognises an XInput
/// keyboard (capabilities Type 2) and builds no device for it, and the binary imports no
/// keystroke API, so no keyboard or mouse event is ever produced. The mouse-movement binding's
/// value (`sub_829C2358`) is the PC's counts / 64 clamped to unit length — a stick again. So keys
/// press the pad, and the mouse is handed to the camera directly (<see cref="FeedMouseMotion"/>).
/// </remarks>
public partial class GameInput
{
	/// <summary>
	/// One rebindable action: its setting key, default keys, and what it presses. <c>Superseded</c>
	/// lists the action's former defaults: the launcher writes every setting it offers, so an install
	/// holds the default of its day, and a stored value equal to a former default reads as today's.
	/// </summary>
	public readonly record struct KeyAction(string Name, string Defaults, string Description, string[]? Superseded = null)
	{
		/// <summary>Whether <paramref name="stored"/> means "the default": empty, today's, or a former one.</summary>
		public bool IsDefault(string? stored)
		{
			string value = Normalised(stored);
			if (value.Length == 0 || value == Normalised(Defaults))
			{
				return true;
			}

			foreach (string former in Superseded ?? System.Array.Empty<string>())
			{
				if (value == Normalised(former))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>The keys in force for <paramref name="stored"/>: today's defaults when it means the default.</summary>
		public string Bound(string? stored) => IsDefault(stored) ? Defaults : stored!;

		private static string Normalised(string? list) =>
			string.Join(",", (list ?? string.Empty).Trim().Trim('"').Split(',',
				System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)).ToLowerInvariant();
	}

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
		new("target_lock", "MouseRight,T", "Hold to lock on to a target (left trigger); flick the " +
			"mouse to switch target (a mod's lock may switch by the wheel instead: up left, down right). " +
			"T is the key for it where the mouse buttons mean pointer clicks (menus, the pub games' odds).",
			new[] { "MouseRight" }),
		new("first_person", "MouseMiddle,Z", "Hold for the first-person view (left bumper)."),
		new("spells_menu", "Ctrl", "Hold to choose a spell (right trigger)."),
		new("expression_wheel", "Q", "Hold for the expression wheel."),
		new("camera_mode", "V", "Toggle the camera mode (right stick click)."),
		new("photo_mode", "F11", "Open or close Photo Mode (L3 + R3 on controller)."),
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

	/// <summary>The mode last stated to the guest tick (<see cref="StateAttackMode"/>), 0 for none since it came up.</summary>
	private static int _attackModeStated;

	/// <summary>
	/// The attack mode for <paramref name="player"/> (1 or 2) as the mods read it — 1 melee, 2 ranged,
	/// 3 magic (REFII_ATTACK_MODE_*) — for the player the keyboard's pad plays here (pad 0's
	/// <c>ModPlayer</c>, the player its claims and presses are), else 0.
	/// </summary>
	public static int AttackModeOf(int player) =>
		player is 1 or 2 && player == Engine_TitleGame.Active.Input.ModPlayer(0) ? (int)_attackMode + 1 : 0;

	/// <summary>
	/// States the attack mode to the guest tick (the command <c>attack_mode P MODE</c>, which raises
	/// <c>attack_mode_changed</c> and answers <c>refii_mod_attack_mode</c>) when it differs from the last
	/// statement, and again whenever the guest has come up since.
	/// </summary>
	private static void StateAttackMode()
	{
		if (!HostBridge.IsGuestUp)
		{
			_attackModeStated = 0;
			return;
		}

		int player = Engine_TitleGame.Active.Input.ModPlayer(0);
		int mode = AttackModeOf(player);
		if (player != 0 && mode != _attackModeStated)
		{
			_attackModeStated = mode;
			HostBridge.PostModCommand($"attack_mode {player} {mode}");
		}
	}

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

	/// <summary>The mouse buttons a press made in play holds until released (<see cref="Refii.Core.Engine_PlayButtons"/>).</summary>
	private static readonly Refii.Core.Engine_PlayButtons PlayButtons = new((int)MouseButton.Xbutton2 + 1);

	private static readonly MouseButton[] BindableButtons =
	{
		MouseButton.Left, MouseButton.Right, MouseButton.Middle, MouseButton.Xbutton1, MouseButton.Xbutton2,
	};

	/// <summary>Escape and Tab as Start and B, each press keeping the mode it started in.</summary>
	private static readonly Refii.Core.Engine_PauseBackKeys PauseBackKeys = new();

	/// <summary>Reads `key_*` from the values the settings pass already loaded.</summary>
	private static void ReadKeyBindings(IReadOnlyDictionary<string, string> values)
	{
		foreach (KeyAction action in KeyActions)
		{
			string raw = action.Bound(Refii.Core.Engine_RefiiConfig.String(values, "key_" + action.Name,
				action.Defaults));
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

		// The controller's remap and the prompts' glyph settings come from the same read.
		ReadControllerSettings(values);
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

	/// <summary>Whether any input bound to an action is down. A mouse button counts from a press
	/// made while the mouse was captured until its release (<see cref="PlayButtons"/>) — a free
	/// cursor's clicks belong to the menus.</summary>
	private static bool Down(string action)
	{
		if (!_bindings.TryGetValue(action, out List<BoundInput>? inputs))
		{
			return false;
		}

		foreach (BoundInput input in inputs)
		{
			// A key a mod has CLAIMED (ModInput.Claim) is the mod's: it no longer presses the pad.
			if (input.Key != Key.None && _withheldKeys.Contains(input.Key))
			{
				continue;
			}

			if (input.Key != Key.None
				? Godot.Input.IsKeyPressed(input.Key) || Godot.Input.IsPhysicalKeyPressed(input.Key)
				: MouseInput && PlayButtons.Held((int)input.Mouse))
			{
				return true;
			}
		}

		return false;
	}

	public static bool PhotoModeKeyHeld => Down("photo_mode");

	/// <summary>
	/// A title overlay is pointed at (Fable 2's expression wheel and spell blade): the cursor is
	/// free while one is held, and the camera stands still under it.
	/// </summary>
	public static bool InOverlay => Engine_TitleGame.Active.Input.PointerOverlay;

	/// <summary>
	/// The play state, the mouse capture and the attack mode, once per frame. The cursor is
	/// captured for the camera only in play with the window focused; anything that takes the
	/// input frees it for the menus.
	/// </summary>
	private void UpdateKeyboardMouse(bool devices)
	{
		Playing = Engine_TitleGame.Active.Input.WorldHasInput;

		// The open dev menu (F6, the one embedded window; its Tracy panels are its tabs) wants
		// the cursor. Its own flag, not `GetEmbeddedSubwindows()`: that builds a Godot Array
		// every frame, a finalizable handle for the collector.
		bool windowOpen = GameDevMenu.WindowOpen;
		// A finger is not a mouse to capture: the touch controls own the screen in play.
		bool capture = MouseInput && MouseLock && devices && Playing && !InOverlay && !windowOpen && !GameDevMenu.KeyboardCaptured &&
			!Engine_UiInputMode.Touch;
		Godot.Input.MouseModeEnum wanted =
			capture ? Godot.Input.MouseModeEnum.Captured : Godot.Input.MouseModeEnum.Visible;
		if (Godot.Input.MouseMode != wanted)
		{
			Godot.Input.MouseMode = wanted;
		}

		if (devices)
		{
			bool captured = !Engine_UiPointer.Free;
			foreach (MouseButton button in BindableButtons)
			{
				PlayButtons.Update((int)button, Godot.Input.IsMouseButtonPressed(button), captured);
			}
		}
		else
		{
			PlayButtons.ReleaseAll();
		}

		// The look device, for the guest's camera: while it is the mouse, the camera keeps its
		// heading as the hero moves and a target lock takes the mouse as flicks
		// (Fable2/src/vfs/src/hooks/mouse_look.cpp); a pad keeps the shipped camera, and so does touch,
		// whose drag is posted as the mouse's turn into that camera (GameInput.Touch.cs).
		HostBridge.SetMouseLookDevice(Engine_UiInputMode.Keycaps && MouseInput);

		Engine_UiKeyGlyphs.LabelFor ??= KeyLabel;
		using (Refii.Core.Engine_FramePhases.Measure("input.glyphs"))
		{
			UpdateGlyphDevice();
			Engine_UiKeyGlyphs.Update();
		}

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

		StateAttackMode();
	}

	/// <summary>
	/// The key a pad button stands for right now, for the keycap prompts: the first key bound to
	/// the action that presses that button, in the context that has the input — B is magic in
	/// the world and back in a menu.
	/// </summary>
	public static string? KeyLabel(string button) => GamePhotoMode.Active ? GamePhotoMode.KeyLabel(button) :
		Engine_TitleGame.Active.InteractionOwnsDevices ? Engine_TitleGame.Active.KeyLabel(button) : button switch
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

	/// <summary>
	/// The first input bound to an action that works now, in its short printed form: mouse buttons
	/// press the pad only in play (elsewhere they are the pointer's clicks), so outside play the first
	/// KEY is shown, and a mouse button only when the action has no key.
	/// </summary>
	private static string Shown(string action)
	{
		if (!_bindings.TryGetValue(action, out List<BoundInput>? inputs) || inputs.Count == 0)
		{
			return "-";
		}

		BoundInput first = inputs[0];
		if (!Playing)
		{
			foreach (BoundInput input in inputs)
			{
				if (input.Mouse == MouseButton.None)
				{
					first = input;
					break;
				}
			}
		}
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

	/// <summary>
	/// The captured mouse as the camera: each movement's turn, in degrees at the profile's middle
	/// camera sensitivity, posted as it arrives — Godot's relative motion, summed per frame
	/// (<c>Input.UseAccumulatedInput</c>), with no curve, no averaging and no rate ceiling. The
	/// guest hands everything posted since its last camera update to the active camera, whole, in
	/// that update (Fable2/src/vfs/src/hooks/mouse_look.cpp), with the profile's sensitivity and vertical
	/// invert applied as to the stick; Godot draws the part no update has taken yet the frame the
	/// mouse moves (<see cref="Refii.Core.Engine_MouseLook"/>). The cursor is captured only in play
	/// (<see cref="UpdateKeyboardMouse"/>), so motion here is always the camera's; horizontal
	/// invert is ours, as for the stick.
	/// </summary>
	private static void FeedMouseMotion(InputEvent @event)
	{
		if (!MouseInput)
		{
			return;
		}

		// The cursor is captured in play, or free by choice (mouse_lock off) and still the camera's.
		if (@event is not InputEventMouseMotion motion ||
			(Godot.Input.MouseMode != Godot.Input.MouseModeEnum.Captured && !(!MouseLock && Playing)))
		{
			return;
		}

		Vector2 degrees = Refii.Core.Engine_MouseLook.Degrees(motion.Relative, MouseSensitivity, InvertCameraX);
		if (degrees != Vector2.Zero)
		{
			HostBridge.PostMouseLook(degrees.X, degrees.Y);
		}
	}

	/// <summary>
	/// The captured mouse's wheel: each notch posted to the guest, down +1 and up -1 (rightward
	/// positive). A notch switches the target of a lock a mod states switches by the wheel
	/// (refii_mod_wheel_switches_target), to the next target right or left (Fable2/src/vfs/src/hooks/mouse_look.cpp); the
	/// guest drops it otherwise. A free cursor's wheel is the menus' (<see cref="Engine_UiPointer"/>),
	/// and nothing else in play reads the wheel.
	/// </summary>
	private static void FeedMouseWheel(InputEvent @event)
	{
		if (@event is InputEventMouseButton { Pressed: true } button &&
			Godot.Input.MouseMode == Godot.Input.MouseModeEnum.Captured)
		{
			HostBridge.PostMouseWheel(button.ButtonIndex switch
			{
				MouseButton.WheelDown => 1,
				MouseButton.WheelUp => -1,
				_ => 0,
			});
		}
	}

	/// <summary>The keys the mods' claims withhold from the pad mapping this frame (ModInput.WithheldKeys).</summary>
	private static IReadOnlySet<Key> _withheldKeys = new HashSet<Key>();

	/// <summary>The keyboard as pad 0's buttons, triggers and left stick.</summary>
	private static void KeyboardMouseState(ref PadState state)
	{
		// Typing into the dev menu is not play; nor is the key a rebind row is capturing.
		if (GameDevMenu.KeyboardCaptured || Engine_UiInputCapture.Blocking)
		{
			return;
		}

		_withheldKeys = Refii.Mods.ModInput.WithheldKeys();
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

		// Escape pauses in play and backs out in a menu; a held press keeps the meaning it started
		// with (Engine_PauseBackKeys).
		(bool start, bool back) = PauseBackKeys.Map(Playing, Down("pause"), Down("back"));
		if (start) buttons |= Start;
		if (back) buttons |= ButtonB;

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
	}
}
