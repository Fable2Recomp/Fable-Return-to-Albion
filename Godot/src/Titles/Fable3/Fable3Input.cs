namespace Refii.Titles.Fable3;

using System.Linq;
using Refii.Game;
using KeyAction = Refii.Game.GameInput.KeyAction;
using KeyContext = Refii.Game.GameInput.KeyContext;
using KeyPress = Refii.Game.GameInput.KeyPress;

/// <summary>
/// Fable 3's part in the pads: the guest sees every pad as it is, but for the host's text entry
/// (<see cref="Fable3Keyboard"/>) — while the console's keyboard is up the game gets no input, so
/// every pad is published neutral, and on the hand-back the buttons still down stay hidden until
/// released, so the A that accepted is not a fresh press in the game. And the keyboard and mouse
/// as Fable III's PC release binds them (<see cref="KeyActions"/>). The host's Controls page
/// (<see cref="Fable3HostOptions"/>) holds the guest's pad neutral the same way.
/// </summary>
public sealed class Fable3Input : GameTitleInput
{
	public static Fable3Input Instance { get; } = new();

	/// <summary>Per pad: buttons hidden from the guest until they are released.</summary>
	private readonly uint[] _held = new uint[4];
	private readonly bool[] _holdNext = new bool[4];

	public override Published PublishPad(int pad, bool connected, GameInput.PadState seen, GameInput.PadState guest)
	{
		if (Fable3Keyboard.IsOpen || Fable3HostOptions.IsOpen)
		{
			return new(new GameControllerPacket(pad, connected, 0u, 0u, 0u, 0, 0, 0, 0), guest.Buttons, 0u, 0u);
		}

		if (_holdNext[pad])
		{
			_holdNext[pad] = false;
			_held[pad] = guest.Buttons;
		}

		_held[pad] &= guest.Buttons;
		guest.Buttons &= ~_held[pad];
		guest.PhysicalButtons &= ~_held[pad];
		guest.OtherButtons &= ~_held[pad];

		// The mouse's pick on a guest menu, walked by the guest's own cursor (Fable3PointerSteps).
		if (pad == 0)
		{
			(int stickY, bool accept) = Fable3PointerSteps.Held();
			if (stickY != 0)
			{
				guest.LeftY = stickY;
				guest.LeftX = 0;
			}

			if (accept)
			{
				guest.Buttons |= GameInput.ButtonA;
			}
		}

		return base.PublishPad(pad, connected, seen, guest);
	}

	public override void HoldUntilReleased()
	{
		for (int pad = 0; pad < _holdNext.Length; ++pad)
		{
			_holdNext[pad] = true;
		}
	}

	/// <summary>
	/// The world has the input in play with nothing of the guest's or ours over it: the HUD root at
	/// state 2 is the guest's own word that a box-manager screen has the pad (the HUD screen's tick
	/// sub_825C3A78 sets it while BM+24 shows, 0x825c3d5c, and back to 1 at 0x825c3d8c) — Options among
	/// them. Under one the keys take their menu meanings and the cursor is free for the menus.
	/// </summary>
	public override bool WorldHasInput => base.WorldHasInput && !Fable3Elements.HudHidden && !Fable3MapTable.Presented &&
		!Fable3Keyboard.IsOpen && !Fable3HostOptions.IsOpen;

	public override bool PointerOverlay => Fable3Keyboard.IsOpen || Fable3HostOptions.IsOpen;

	/// <summary>Expression choices take the keys only while the world has the input: a screen over them has its own.</summary>
	public override bool ChoicesUp => Fable3WorldIcons.ChoicesUp && WorldHasInput;

	public override bool TitleScreenUp => Fable3Splash.Showing;

	/// <summary>
	/// THE KEYBOARD AND MOUSE AS FABLE III PC BINDS THEM, each pressing the 360 button the shipped
	/// scheme gives that action. The PC defaults are the PC build's own (its globals.gdb,
	/// `PCKeyboardAndMouse` / `GameControlsPC`, DirectInput codes): WASD, LShift run, E interact,
	/// Space roll, the left button attacking with the mode 1/2/3 picked, the right button the
	/// secondary, LCtrl first person, Esc the Sanctuary, F1-F4 the d-pad shortcuts; in menus 1/2/3
	/// are A/X/Y (its prompts show those keycaps) with Esc back; while expression choices are up
	/// 1-5 pick choices 1-5 and the left button accepts the first. The pad side is the 360 scheme
	/// the guest runs (GDB 0x50ac87b1 over 0x8c1585e4 over Base 0xcb96641a): A interact / sprint /
	/// dive and choice 1, X melee and block (choice 2), Y ranged aim and fire (choice 3), B magic,
	/// RB the quick menu (choice 4), LB first person (choice 5), LT target lock and zoom aim, RT
	/// held, RS the camera mode, Start pause, Back the nickname; the roll is A in combat.
	/// </summary>
	private static readonly KeyAction[] Actions =
	{
		new("move_forward", "W", "Move forward (left stick up).") { Press = KeyPress.MoveForward },
		new("move_back", "S", "Move back (left stick down).") { Press = KeyPress.MoveBack },
		new("move_left", "A", "Move left (left stick left).") { Press = KeyPress.MoveLeft },
		new("move_right", "D", "Move right (left stick right).") { Press = KeyPress.MoveRight },
		new("walk", "Alt", "Hold to walk: the stick at half deflection.") { Press = KeyPress.Walk },
		new("sprint", "Shift", "Hold to run (A held).") { Press = KeyPress.A, When = KeyContext.Play },
		new("interact", "E", "Interact, talk, pick up (A).") { Press = KeyPress.A, When = KeyContext.Play },
		new("roll", "Space", "Roll in combat (A).") { Press = KeyPress.A, When = KeyContext.Play },
		new("attack", "MouseLeft", "Attack with the mode 1/2/3 picked: melee, ranged or magic. Hold to block, " +
			"aim or charge.") { Press = KeyPress.Attack, When = KeyContext.PlayNoChoice },
		new("melee_mode", "1", "Put the attack button on melee.") { Press = KeyPress.MeleeMode, When = KeyContext.PlayNoChoice },
		new("ranged_mode", "2", "Put the attack button on the ranged weapon.") { Press = KeyPress.RangedMode, When = KeyContext.PlayNoChoice },
		new("magic_mode", "3", "Put the attack button on magic.") { Press = KeyPress.MagicMode, When = KeyContext.PlayNoChoice },
		new("melee", "F", "Melee directly, whatever the mode (X).") { Press = KeyPress.X, When = KeyContext.Play },
		new("ranged", "R", "Ranged directly, whatever the mode (Y).") { Press = KeyPress.Y, When = KeyContext.Play },
		new("magic", "C", "Magic directly, whatever the mode (B).") { Press = KeyPress.B, When = KeyContext.Play },
		new("target_lock", "MouseRight,T", "Hold to lock on to a target, or to zoom-aim the ranged weapon (left trigger).")
			{ Press = KeyPress.LeftTrigger, When = KeyContext.Play },
		new("first_person", "Ctrl", "Hold for the first-person view (left bumper).") { Press = KeyPress.LeftShoulder, When = KeyContext.Play },
		// Fable III PC's Tab looks at what the game points out: on the 360 that is the right trigger held
		// (sub_8276A038 at 0x8276a9b0 starts the look on raw button 16; action 95 ends it at 0x828d9c18).
		new("look_at", "Tab", "Hold to look at what the game points out (right trigger).") { Press = KeyPress.RightTrigger, When = KeyContext.Play },
		new("quick_menu", "Q", "The right bumper in play (the quick menu).", new[] { "Tab" }) { Press = KeyPress.RightShoulder, When = KeyContext.Play },
		new("camera_mode", "V", "Toggle the camera mode (right stick click).") { Press = KeyPress.RightThumb, When = KeyContext.Play },
		new("nickname", "Backspace", "The Back button: change your nickname; in co-op, return to the host.")
			{ Press = KeyPress.Back, When = KeyContext.Play },
		new("photo_mode", "F11", "Open or close Photo Mode (L3 + R3 on controller).") { Press = KeyPress.PhotoMode },
		new("pause", "Escape", "The Sanctuary (Start). Escape is back in menus.") { Press = KeyPress.Pause },
		new("shortcut_up", "F1", "D-pad up: its shortcut.") { Press = KeyPress.DpadUp, When = KeyContext.Play },
		new("shortcut_left", "F2", "D-pad left: its shortcut.") { Press = KeyPress.DpadLeft, When = KeyContext.Play },
		new("shortcut_down", "F3", "D-pad down: its shortcut.") { Press = KeyPress.DpadDown, When = KeyContext.Play },
		new("shortcut_right", "F4", "D-pad right: its shortcut.") { Press = KeyPress.DpadRight, When = KeyContext.Play },

		new("press_start", "Enter,Space,Escape", "Press START on the title screen.") { Press = KeyPress.Start, When = KeyContext.TitleScreen },
		new("menu_accept", "1,Enter,E,Space", "Accept in a menu (A).") { Press = KeyPress.A, When = KeyContext.Menu },
		// The PC swaps them on the keyboard (sub_1359C50: GUIX 97 <-> GUIY 98 when the last device
		// was not the pad), so its prompts' keycaps read 3 on X and 2 on Y; these match them.
		new("menu_x", "3", "A menu's X.") { Press = KeyPress.X, When = KeyContext.Menu },
		new("menu_y", "2", "A menu's Y.") { Press = KeyPress.Y, When = KeyContext.Menu },
		new("back", "Escape", "Back out of a menu (B).") { Press = KeyPress.BackOut },
		new("menu_up", "Up", "Menu up (D-pad up).") { Press = KeyPress.DpadUp, When = KeyContext.Menu },
		new("menu_down", "Down", "Menu down (D-pad down).") { Press = KeyPress.DpadDown, When = KeyContext.Menu },
		new("menu_left", "Left", "Menu left (D-pad left).") { Press = KeyPress.DpadLeft, When = KeyContext.Menu },
		new("menu_right", "Right", "Menu right (D-pad right).") { Press = KeyPress.DpadRight, When = KeyContext.Menu },
		new("tab_prev", "Minus,PageUp", "Previous category in a menu (left bumper).") { Press = KeyPress.LeftShoulder, When = KeyContext.Menu },
		new("tab_next", "Equal,PageDown", "Next category in a menu (right bumper).") { Press = KeyPress.RightShoulder, When = KeyContext.Menu },
		new("scroll_prev", "BracketLeft", "Scroll back in a menu (left trigger).") { Press = KeyPress.LeftTrigger, When = KeyContext.Menu },
		new("scroll_next", "BracketRight", "Scroll on in a menu (right trigger).") { Press = KeyPress.RightTrigger, When = KeyContext.Menu },

		new("choice_1", "1,MouseLeft", "The first expression choice (A).") { Press = KeyPress.A, When = KeyContext.Choice },
		new("choice_2", "2", "The second expression choice (X).") { Press = KeyPress.X, When = KeyContext.Choice },
		new("choice_3", "3", "The third expression choice (Y).") { Press = KeyPress.Y, When = KeyContext.Choice },
		new("choice_4", "4", "The fourth expression choice (right bumper).") { Press = KeyPress.RightShoulder, When = KeyContext.Choice },
		new("choice_5", "5", "The fifth expression choice (left bumper).") { Press = KeyPress.LeftShoulder, When = KeyContext.Choice },
	};

	public override KeyAction[] KeyActions => _actions ??= WithShellDefaultsSuperseded(Actions);

	private static KeyAction[]? _actions;

	/// <summary>
	/// The launcher writes every setting it offers, so a title config saved before this table holds
	/// the shell's (Fable 2's) defaults under the shared names — `key_interact = "E,Space,Enter"`. A
	/// stored value equal to the shell's default of the same name reads as this table's default.
	/// </summary>
	private static KeyAction[] WithShellDefaultsSuperseded(KeyAction[] actions)
	{
		var shell = new System.Collections.Generic.Dictionary<string, KeyAction>();
		foreach (KeyAction action in GameInput.DefaultKeyActions)
		{
			shell[action.Name] = action;
		}

		var result = new KeyAction[actions.Length];
		for (int i = 0; i < actions.Length; ++i)
		{
			KeyAction action = actions[i];
			if (!shell.TryGetValue(action.Name, out KeyAction former))
			{
				result[i] = action;
				continue;
			}

			// The shell's default of the day and every one it superseded read as this table's default.
			var superseded = new System.Collections.Generic.List<string>(action.Superseded ?? System.Array.Empty<string>());
			foreach (string old in new[] { former.Defaults }.Concat(former.Superseded ?? System.Array.Empty<string>()))
			{
				if (old != action.Defaults && !superseded.Contains(old))
				{
					superseded.Add(old);
				}
			}

			result[i] = action with { Superseded = superseded.ToArray() };
		}

		return result;
	}

	/// <summary>
	/// The key a prompt shows, in the context that has the input: a held choice's own 1-5, a menu's
	/// 1/2/3 and Esc (as the PC build's own prompts show), and in play the action the game gives
	/// the button — the attack button on the mode's X, Y or B.
	/// </summary>
	public override string? KeyLabel(string button, System.Func<string, string> shown, bool playing)
	{
		if (TitleScreenUp && button == "Start")
		{
			return shown("press_start");
		}

		if (ChoicesUp)
		{
			string? choice = button switch
			{
				"A" => shown("choice_1"),
				"X" => shown("choice_2"),
				"Y" => shown("choice_3"),
				"RB" => shown("choice_4"),
				"LB" => shown("choice_5"),
				_ => null,
			};
			if (choice is not null)
			{
				return choice;
			}
		}

		if (!playing)
		{
			return button switch
			{
				"A" => shown("menu_accept"),
				"B" => shown("back"),
				"X" => shown("menu_x"),
				"Y" => shown("menu_y"),
				"LB" => shown("tab_prev"),
				"RB" => shown("tab_next"),
				"LT" => shown("scroll_prev"),
				"RT" => shown("scroll_next"),
				"Start" => shown("pause"),
				"Back" => shown("back"),
				"DPad" => "Arrows",
				"LS" => string.Empty,
				"RS" => "Wheel",
				_ => null,
			};
		}

		int mode = GameInput.AttackModeOf(ModPlayer(0));
		return button switch
		{
			"A" => shown("interact"),
			"X" => mode == 1 ? shown("attack") : shown("melee"),
			"Y" => mode == 2 ? shown("attack") : shown("ranged"),
			"B" => mode == 3 ? shown("attack") : shown("magic"),
			"LB" => shown("first_person"),
			"RB" => shown("quick_menu"),
			"LT" => shown("target_lock"),
			"RT" => shown("look_at"),
			"Start" => shown("pause"),
			"Back" => shown("nickname"),
			"DPad" => "F1-F4",
			"LS" => "WASD",
			"RS" => "Mouse",
			_ => null,
		};
	}
}
