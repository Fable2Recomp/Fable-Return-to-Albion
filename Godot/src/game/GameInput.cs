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
		// Everything goes to pad 0; the rest report disconnected. The front end latches the
		// pad that pressed A (dword_83496AF8, 0x826E30DC) and ignores every other one
		// (0x826DEE64), so input split across devices is input the guest discards. Device
		// index cannot pick the right one either — Godot's joypad 0 here is the keyboard's
		// mouse interface, and the real pad is joypad 1.
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

		// TWO CONSUMERS, AND ONLY ONE OF THEM OWNS THE MENU.
		//
		// The guest gets a filtered set; the host-owned menu below gets the real one. See
		// <see cref="WithoutMenuInput"/> — the guest is still running its own front-end menu,
		// and handing it the same press drives two menus at once.
		bool masked = MaskMenuInput;
		HostBridge.SetControllerState(
			0, connected: true, masked ? WithoutMenuInput(buttons) : buttons,
			leftTrigger, rightTrigger,
			// The STICK is masked with the buttons, not separately. `sub_826E39C0` dead-zones
			// the left stick against ±0.75 and emits the very same button codes the D-pad does
			// — 28/29 for Y at 0x826E3C04/0x826E3C58 and 30/31 for X at 0x826E3B50/0x826E3BAC
			// — so leaving it live would move the guest's highlight with the D-pad blocked.
			masked ? 0 : leftX, masked ? 0 : leftY, rightX, rightY);

		PublishToHost(buttons, leftX, leftY);
	}

	/// <summary>
	/// Whether the guest's own front-end menu should be shielded from this frame's input.
	///
	/// TRUE WHILE THE GUEST'S ACTIVE SCREEN IS THE MAIN MENU, because that is the screen Godot
	/// took over. Nothing else is masked: the TITLE screen keeps every button, since A there is
	/// the guest's own documented contract (`if (a2 == 36 &amp;&amp; *(BYTE*)(a1+80))` at 0x826E3078)
	/// and it is how the front end is reached at all.
	///
	/// AND ONLY IN ITS ROOT SUBSTATE, which is the guest telling us the list is what is up.
	/// `screen+24` is 0 for the menu list and anything else means the guest owns the screen: 1
	/// is its own "swallow all input" lock, 2/3/4 the gender card, 5 the load-game carousel,
	/// 6/7 a modal. Masking through those would be a deadlock rather than a nuisance — every
	/// failure path in `sub_826DFD58` raises a modal that parks the screen in substate 1 until
	/// an A press arrives, and DLC with no profile signed in raises one immediately. Hold A back
	/// there and the dialog can never be dismissed.
	///
	/// THE VTABLE CHECK IS LOAD-BEARING, not belt and braces. `dword_83496B08` has two writers
	/// through the same base ctor, and once the game has taken over it holds a BASE-ONLY object
	/// whose +180/+184/+188 are not screen pointers — so `screen` is then decoded from fields
	/// that mean nothing and can read as MainMenu at any moment. Masking on that would take the
	/// D-pad and the left stick away from the player in the middle of the game.
	/// </summary>
	private static bool MaskMenuInput =>
		HostBridge.FrontEndGuiIsUp &&
		HostBridge.TryGetFrontEnd(out HostBridge.FrontEndScreen screen, out uint state, out _,
			out _, out _, out _) &&
		screen == HostBridge.FrontEndScreen.MainMenu && state == MenuRootState;

	/// <summary>
	/// `screen+24` while the menu LIST is the thing on screen. Every other value is a substate
	/// the guest drives itself — see <see cref="MaskMenuInput"/>.
	/// </summary>
	private const uint MenuRootState = 0;

	/// <summary>
	/// The buttons with the front end's navigation taken out.
	///
	/// WHY THIS EXISTS. Godot owns the main menu — its list, its order and its highlight — and
	/// dispatches the result to the guest BY NAME, which is the guest's own contract. But the
	/// guest never stopped running its own menu: its shipped `g_Menu` Lua still holds a list and
	/// a highlight, and `sub_826DEE40` still forwards A and B to it through `OnPressA` /
	/// `OnPressB` while `screen+24` is 0. Feeding it the same press ran both menus, and their
	/// highlights are not the same object — a press on Godot's "Downloadable Content" put the
	/// guest into substate 5, its LOAD-GAME carousel, because that is what ITS list had
	/// selected. Two menus acting on one press is not a cosmetic problem: the guest was also
	/// setting slides on `choosecard` behind the panel Godot had just put up.
	///
	/// B IS DELIBERATELY LEFT THROUGH. On the root menu it is the guest's business — B there
	/// leaves for the title screen, and that path runs entirely inside the guest. Masking it
	/// would need a host route back to the title that does not exist yet, so the entry that
	/// works today keeps working.
	/// </summary>
	private static uint WithoutMenuInput(uint buttons) =>
		buttons & ~(DpadUp | DpadDown | DpadLeft | DpadRight | ButtonA);

	/// <summary>
	/// The same frame's input, in the terms a HOST-OWNED menu needs.
	///
	/// Godot draws the front end itself now, so the highlight lives on this side and something
	/// here has to read the stick and the d-pad. This deliberately reuses the buttons already
	/// gathered for the guest rather than polling the devices a second time: two readers of
	/// one pad drift apart the moment either grows a deadzone or a repeat rule, and then the
	/// menu and the guest disagree about what the player did.
	///
	/// The stick is folded into the direction using the SHIPPED threshold — Fable 2's own
	/// `THUMBSTICK_RELEASE_THRESHOLD` is 0.65, out of `guistateandconstants.lua`, and it is
	/// high on purpose: a low threshold makes a resting stick creep through a menu.
	/// </summary>
	private static void PublishToHost(uint buttons, int leftX, int leftY)
	{
		int direction = 0;
		if ((buttons & DpadUp) != 0)
		{
			direction = -1;
		}
		else if ((buttons & DpadDown) != 0)
		{
			direction = 1;
		}
		else
		{
			// Axis() returns the Xbox's signed range, and up is positive after the negation
			// above — so a positive Y is "up", which is -1 in list order.
			double stick = leftY / 32767.0;
			if (Held(ref _pushingUp, stick))
			{
				direction = -1;
			}
			else if (Held(ref _pushingDown, -stick))
			{
				direction = 1;
			}
		}

		MenuDirection = direction;

		// The same fold across X, for the panels the front end navigates SIDEWAYS — the gender
		// card and the load-game carousel. The guest reads one stick for both axes and turns
		// each into its own button code: `sub_826E39C0` compares the dead-zoned X against
		// ±0.75 at 0x826E3B3C / 0x826E3B98 and emits code 30 for left and 31 for right, exactly
		// as it emits 28 and 29 off Y. Its threshold is 0.75 rather than the scripts' 0.65,
		// which is the guest's own number for a stick-as-button and not the menu's — but using
		// two different thresholds on one stick is how a diagonal push scrolls a menu and
		// flips a card at the same time, so the shipped menu threshold governs both here.
		int horizontal = 0;
		if ((buttons & DpadLeft) != 0)
		{
			horizontal = -1;
		}
		else if ((buttons & DpadRight) != 0)
		{
			horizontal = 1;
		}
		else
		{
			double stick = leftX / 32767.0;
			if (Held(ref _pushingLeft, -stick))
			{
				horizontal = -1;
			}
			else if (Held(ref _pushingRight, stick))
			{
				horizontal = 1;
			}
		}

		MenuHorizontal = horizontal;

		// EDGE-detected, because a menu acts on a press and the guest edge-detects the same
		// way. Holding A must not activate the highlighted entry every frame.
		bool a = (buttons & ButtonA) != 0;
		bool b = (buttons & ButtonB) != 0;
		MenuAcceptPressed = a && !_acceptWasDown;
		MenuBackPressed = b && !_backWasDown;
		_acceptWasDown = a;
		_backWasDown = b;
	}

	/// <summary>
	/// One direction's stick hysteresis, as `guistateandconstants.lua` states it.
	///
	/// <paramref name="magnitude"/> is the stick along that direction, already signed so that
	/// pushing THIS way is positive. The direction engages above
	/// `THUMBSTICK_PUSH_THRESHOLD` (0.70) and stays engaged until the stick falls back under
	/// `THUMBSTICK_RELEASE_THRESHOLD` (0.65) — which is why the shipped script keeps a
	/// `Pushing<Direction>Held` boolean per direction rather than recomputing from one cutoff.
	/// </summary>
	private static bool Held(ref bool held, double magnitude)
	{
		held = held
			? magnitude > Refii.Ui.Engine_FrontEndMenu.ThumbstickReleaseThreshold
			: magnitude >= Refii.Ui.Engine_FrontEndMenu.ThumbstickPushThreshold;
		return held;
	}

	/// <summary>The four `Pushing*Held` flags the shipped input model keeps.</summary>
	private static bool _pushingUp;
	private static bool _pushingDown;
	private static bool _pushingLeft;
	private static bool _pushingRight;

	/// <summary>-1 up, +1 down, 0 centred — for a host-owned menu.</summary>
	public static int MenuDirection { get; private set; }

	/// <summary>-1 left, +1 right, 0 centred — for a host-owned panel navigated sideways.</summary>
	public static int MenuHorizontal { get; private set; }

	/// <summary>True on the frame A went down.</summary>
	public static bool MenuAcceptPressed { get; private set; }

	/// <summary>True on the frame B went down.</summary>
	public static bool MenuBackPressed { get; private set; }

	private static bool _acceptWasDown;

	private static bool _backWasDown;

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
