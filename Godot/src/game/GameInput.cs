using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Feeds Godot's controller state to the guest, once per frame.
///
/// Godot owns input the same way it owns the window and the world. The guest asks for
/// controllers through XInput; this reads the real device through Godot and publishes a
/// snapshot the guest's driver serves from. Translation happens ONCE, here, into the
/// Xbox's own vocabulary, because the guest is the consumer and converting on every read
/// would be both slower and lossier.
/// </summary>
public partial class GameInput : Node
{
	/// <summary>Xbox 360 supports four pads, and so does the guest.</summary>
	private const int MaxControllers = 4;

	/// <summary>
	/// XInput button bits. These are the guest's values, not Godot's — see
	/// <c>X_INPUT_GAMEPAD_BUTTON</c>.
	/// </summary>
	private const uint DpadUp = 0x0001;
	private const uint DpadDown = 0x0002;
	private const uint DpadLeft = 0x0004;
	private const uint DpadRight = 0x0008;
	private const uint Start = 0x0010;
	private const uint Back = 0x0020;
	private const uint LeftThumb = 0x0040;
	private const uint RightThumb = 0x0080;
	private const uint LeftShoulder = 0x0100;
	private const uint RightShoulder = 0x0200;
	private const uint ButtonA = 0x1000;
	private const uint ButtonB = 0x2000;
	private const uint ButtonX = 0x4000;
	private const uint ButtonY = 0x8000;

	public override void _Process(double delta)
	{
		// EVERYTHING the machine has goes to pad 0, and the other three report disconnected.
		//
		// Not a simplification — it is what the guest's own rule requires. The front end
		// latches the pad that pressed A (`dword_83496AF8`, written at 0x826E30DC) and then
		// ignores every other one (`a3 == dword_83496AF8` at 0x826DEE64). So input split
		// across devices is input the guest throws away: press A on a pad that is not the
		// latched one and nothing happens, with no error anywhere.
		//
		// Device INDEX cannot be trusted to pick the right one either. On this machine
		// Godot's joypad 0 is the Corsair keyboard's mouse interface — the kernel exposes it
		// as js0 — and the real Xbox pad is joypad 1. Publishing by index put a keyboard's
		// mouse on the pad the guest listens to and the actual controller on one it does
		// not, and it also killed the keyboard fallback, which only ran when NO joypad was
		// present. Aggregating sidesteps device enumeration entirely.
		Publish0();

		for (int pad = 1; pad < MaxControllers; ++pad)
		{
			HostBridge.SetControllerState(pad, connected: false, 0, 0, 0, 0, 0, 0, 0);
		}
	}

	/// <summary>
	/// Pad 0: the keyboard and every connected joypad, merged.
	///
	/// Buttons are OR'd, triggers and sticks take whichever device is pushing hardest, so a
	/// pad at rest never cancels one being used.
	/// </summary>
	private static void Publish0()
	{
		uint buttons = KeyboardButtons() | AutoStartButtons();
		uint leftTrigger = 0;
		uint rightTrigger = 0;
		int leftX = 0, leftY = 0, rightX = 0, rightY = 0;

		foreach (int pad in Input.GetConnectedJoypads())
		{
			buttons |= JoypadButtons(pad);

			leftTrigger = System.Math.Max(leftTrigger, Trigger(pad, JoyAxis.TriggerLeft));
			rightTrigger = System.Math.Max(rightTrigger, Trigger(pad, JoyAxis.TriggerRight));

			leftX = Dominant(leftX, Axis(pad, JoyAxis.LeftX));
			// NEGATED. Godot's stick Y is screen-space — down is positive — and the Xbox's
			// is up-positive. Miss this and the character walks backwards, which reads as a
			// broken control scheme rather than a sign error.
			leftY = Dominant(leftY, -Axis(pad, JoyAxis.LeftY));
			rightX = Dominant(rightX, Axis(pad, JoyAxis.RightX));
			rightY = Dominant(rightY, -Axis(pad, JoyAxis.RightY));
		}

		HostBridge.SetControllerState(
			0, connected: true, buttons, leftTrigger, rightTrigger,
			leftX, leftY, rightX, rightY);
	}

	/// <summary>Whichever axis is further from centre — a pad at rest reads 0 and loses.</summary>
	private static int Dominant(int a, int b) =>
		System.Math.Abs(b) > System.Math.Abs(a) ? b : a;

	/// <summary>Triggers are 0..1 in Godot and 0..255 on the Xbox.</summary>
	private static uint Trigger(int pad, JoyAxis axis) => (uint)Mathf.Clamp(
		Mathf.RoundToInt(Input.GetJoyAxis(pad, axis) * 255.0f), 0, 255);

	private static uint JoypadButtons(int pad)
	{
		uint buttons = 0;
		buttons |= Bit(pad, JoyButton.DpadUp, DpadUp);
		buttons |= Bit(pad, JoyButton.DpadDown, DpadDown);
		buttons |= Bit(pad, JoyButton.DpadLeft, DpadLeft);
		buttons |= Bit(pad, JoyButton.DpadRight, DpadRight);
		buttons |= Bit(pad, JoyButton.Start, Start);
		buttons |= Bit(pad, JoyButton.Back, Back);
		buttons |= Bit(pad, JoyButton.LeftStick, LeftThumb);
		buttons |= Bit(pad, JoyButton.RightStick, RightThumb);
		buttons |= Bit(pad, JoyButton.LeftShoulder, LeftShoulder);
		buttons |= Bit(pad, JoyButton.RightShoulder, RightShoulder);
		buttons |= Bit(pad, JoyButton.A, ButtonA);
		buttons |= Bit(pad, JoyButton.B, ButtonB);
		buttons |= Bit(pad, JoyButton.X, ButtonX);
		buttons |= Bit(pad, JoyButton.Y, ButtonY);
		return buttons;
	}

	/// <summary>
	/// The keyboard, in Xbox terms. Always live, whatever else is plugged in.
	///
	/// The bindings are the conventional ones for a console front end: Enter/Space is A,
	/// Escape/Backspace is B, the arrows are the D-pad.
	/// </summary>
	private static uint KeyboardButtons()
	{
		uint buttons = 0;
		if (Input.IsKeyPressed(Key.Up)) buttons |= DpadUp;
		if (Input.IsKeyPressed(Key.Down)) buttons |= DpadDown;
		if (Input.IsKeyPressed(Key.Left)) buttons |= DpadLeft;
		if (Input.IsKeyPressed(Key.Right)) buttons |= DpadRight;
		if (Input.IsKeyPressed(Key.Enter) || Input.IsKeyPressed(Key.Space)) buttons |= ButtonA;
		if (Input.IsKeyPressed(Key.Escape) || Input.IsKeyPressed(Key.Backspace)) buttons |= ButtonB;
		if (Input.IsKeyPressed(Key.Tab)) buttons |= Start;
		return buttons;
	}

	/// <summary>
	/// The unattended A press, for runs with nobody at the keyboard.
	///
	/// Held for a few frames and then RELEASED, because the guest edge-detects: it compares
	/// the current buttons against the previous frame's, so a permanently-held button is
	/// one press and never repeats.
	///
	/// It presses whenever the title says it will accept A, not once. A single press is not
	/// enough — the title screen has its own idle timeout into attract mode, so a run that
	/// pressed once and stopped kept sliding back to the attract movie and never reached
	/// the menu. The cooldown keeps it from hammering: one press, then a pause long enough
	/// for the screen to act on it.
	/// </summary>
	private static uint AutoStartButtons()
	{
		if (OS.GetEnvironment("REFII_AUTO_START") != "1")
		{
			return 0;
		}

		if (_autoStartHeld > 0)
		{
			return --_autoStartHeld > 0 ? ButtonA : 0;
		}

		if (_autoStartCooldown > 0)
		{
			--_autoStartCooldown;
			return 0;
		}

		if (!HostBridge.TryGetFrontEnd(out HostBridge.FrontEndScreen screen,
				out uint state, out bool canPressA, out uint cursor,
				out int slide, out _))
		{
			return 0;
		}

		// The TITLE takes A only when it is idle and armed.
		// The MAIN MENU has no such flag, and it does not start on its first page — it comes
		// up on slide 1 (PressStart) and needs a press to reach the menu proper, then another
		// to take the highlighted item. So it is pressed on a cooldown until the front end
		// hands over, which is the guest telling us the sequence is done: the active screen
		// stops being one of the two front-end screens.
		bool ready = screen switch
		{
			HostBridge.FrontEndScreen.Title => canPressA && state == TitleIdleState,
			HostBridge.FrontEndScreen.MainMenu => true,
			_ => false,
		};

		if (!ready)
		{
			return 0;
		}

		GD.Print($"[game-input] pressing A on {screen} (state {state} slide {slide} " +
			$"cursor {cursor})");
		_autoStartHeld = AutoStartFrames;
		_autoStartCooldown = AutoStartCooldownFrames;
		return ButtonA;
	}

	/// <summary>
	/// The title screen's idle state.
	///
	/// What actually gates A is the ARMED BYTE, `titleScreen+80`, which is what
	/// <c>canPressA</c> reports: `if (a2 == 36 &amp;&amp; *(BYTE *)(a1 + 80))` at 0x826E3078, armed
	/// only by the presentation's CAN_PRESS_A event at 0x826E2E38. States 0, 9, 10 and 11
	/// all reach that branch — they are self-limiting only because the byte is 0 in the
	/// others. So this is not "the only state that starts the game".
	///
	/// It is still the state to wait for, because of the ONE state that takes A somewhere
	/// else: in state 8 the attract movie is up and A goes down a different branch
	/// (0x826E3030) that only sets the movie's skip flag and can never start anything.
	/// Waiting for idle keeps every press on the branch that starts the game.
	/// </summary>
	private const uint TitleIdleState = 0;

	private const int AutoStartFrames = 8;
	private const int AutoStartCooldownFrames = 120;
	private static int _autoStartHeld;
	private static int _autoStartCooldown;

	private static uint Bit(int pad, JoyButton button, uint mask) =>
		Input.IsJoyButtonPressed(pad, button) ? mask : 0u;

	/// <summary>
	/// Godot reports -1..1; the guest wants -32768..32767. Scaled by 32767 in both
	/// directions rather than 32768 downward, so a full deflection is symmetric and can
	/// never overflow the positive end.
	/// </summary>
	private static int Axis(int pad, JoyAxis axis) =>
		Mathf.Clamp(Mathf.RoundToInt(Input.GetJoyAxis(pad, axis) * 32767.0f), -32767, 32767);
}
