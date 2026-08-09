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
		for (int pad = 0; pad < MaxControllers; ++pad)
		{
			Publish(pad);
		}
	}

	/// <summary>
	/// Presents the keyboard as controller 0, in Xbox terms.
	///
	/// The bindings are the conventional ones for a console front end: Enter/Space is A,
	/// Escape/Backspace is B, the arrows are the D-pad. <c>REFII_AUTO_START</c> also
	/// synthesises the A press once the title screen says it will accept one, which is how
	/// an unattended run gets to the main menu.
	/// </summary>
	private static void PublishKeyboard()
	{
		uint buttons = 0;
		if (Input.IsKeyPressed(Key.Up)) buttons |= DpadUp;
		if (Input.IsKeyPressed(Key.Down)) buttons |= DpadDown;
		if (Input.IsKeyPressed(Key.Left)) buttons |= DpadLeft;
		if (Input.IsKeyPressed(Key.Right)) buttons |= DpadRight;
		if (Input.IsKeyPressed(Key.Enter) || Input.IsKeyPressed(Key.Space)) buttons |= ButtonA;
		if (Input.IsKeyPressed(Key.Escape) || Input.IsKeyPressed(Key.Backspace)) buttons |= ButtonB;
		if (Input.IsKeyPressed(Key.Tab)) buttons |= Start;

		buttons |= AutoStartButtons();

		HostBridge.SetControllerState(
			0, connected: true, buttons, 0, 0, 0, 0, 0, 0);
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
				out uint state, out bool canPressA, out _) ||
			screen != HostBridge.FrontEndScreen.Title || !canPressA)
		{
			return 0;
		}

		GD.Print($"[game-input] title is ready for A (state {state}); pressing it");
		_autoStartHeld = AutoStartFrames;
		_autoStartCooldown = AutoStartCooldownFrames;
		return ButtonA;
	}

	private const int AutoStartFrames = 8;
	private const int AutoStartCooldownFrames = 120;
	private static int _autoStartHeld;
	private static int _autoStartCooldown;

	private static void Publish(int pad)
	{
		// Godot enumerates connected pads by device id; an absent pad is reported as
		// disconnected rather than as an all-zero connected one, because the guest
		// distinguishes the two — a connected pad at rest still gets polled.
		if (!Input.IsJoyKnown(pad) && !Input.GetConnectedJoypads().Contains(pad))
		{
			// Pad 0 falls back to the KEYBOARD rather than reporting nothing. The guest's
			// front end only accepts input from a pad it considers signed in, so with no
			// hardware attached the title screen can never be got past at all — and Godot
			// owns input now, so where the button came from is our business, not the
			// guest's.
			if (pad == 0)
			{
				PublishKeyboard();
				return;
			}

			HostBridge.SetControllerState(pad, connected: false, 0, 0, 0, 0, 0, 0, 0);
			return;
		}

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

		// Triggers are 0..1 in Godot and 0..255 on the Xbox.
		uint leftTrigger = (uint)Mathf.Clamp(
			Mathf.RoundToInt(Input.GetJoyAxis(pad, JoyAxis.TriggerLeft) * 255.0f), 0, 255);
		uint rightTrigger = (uint)Mathf.Clamp(
			Mathf.RoundToInt(Input.GetJoyAxis(pad, JoyAxis.TriggerRight) * 255.0f), 0, 255);

		HostBridge.SetControllerState(
			pad,
			connected: true,
			buttons,
			leftTrigger,
			rightTrigger,
			Axis(pad, JoyAxis.LeftX),
			// NEGATED. Godot's stick Y is screen-space — down is positive — and the
			// Xbox's is up-positive. Miss this and the character walks backwards, which
			// reads as a broken control scheme rather than a sign error.
			-Axis(pad, JoyAxis.LeftY),
			Axis(pad, JoyAxis.RightX),
			-Axis(pad, JoyAxis.RightY));
	}

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
