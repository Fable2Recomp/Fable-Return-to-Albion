using Refii.Ui;
using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Feeds Godot's controller state to the guest, once per frame: reads the real device and
/// publishes a snapshot the guest's XInput driver serves from. Translation into the Xbox's
/// vocabulary happens once, here.
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
		// The guest can end while this node is up; nothing is published into a torn-down runtime.
		if (HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		// Everything goes to pad 0; the rest report disconnected. The front end latches the
		// pad that pressed A (dword_83496AF8, 0x826E30DC) and ignores every other one
		// (0x826DEE64), so input split across devices is input the guest discards.
		Publish0();

		for (int pad = 1; pad < MaxControllers; ++pad)
		{
			HostBridge.SetControllerState(pad, connected: false, 0, 0, 0, 0, 0, 0, 0);
		}
	}

	/// <summary>
	/// Pad 0: the keyboard and every connected joypad, merged. Buttons are OR'd; triggers and
	/// sticks take whichever device is pushing hardest, so a pad at rest never cancels one in use.
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
			// Negated: Godot's stick Y is screen-space (down positive), the Xbox's is up-positive.
			leftY = Dominant(leftY, -Axis(pad, JoyAxis.LeftY));
			rightX = Dominant(rightX, Axis(pad, JoyAxis.RightX));
			rightY = Dominant(rightY, -Axis(pad, JoyAxis.RightY));
		}

		LeftStick = new Vector2(leftX / 32767f, leftY / 32767f);

		// Diagnostic: `REFII_FAKE_STICK=1` holds the left stick forward so the input chain can
		// be exercised with nobody at the controller.
		if (System.Environment.GetEnvironmentVariable("REFII_FAKE_STICK") == "1")
		{
			leftY = 24000;
		}

		// Logged on the threshold crossing only (30/255, the XInput convention).
		uint triggersHeld = (leftTrigger > 30 ? 1u : 0u) | (rightTrigger > 30 ? 2u : 0u);
		if (triggersHeld != _reportedTriggers)
		{
			_reportedTriggers = triggersHeld;
			GD.Print($"[input] triggers LT={leftTrigger} RT={rightTrigger}");
		}

		// Two consumers: the guest gets a filtered set, the host-owned menu below gets the real
		// one. The guest still runs its own front-end menu — see <see cref="WithoutMenuInput"/>.
		bool masked = MaskMenuInput;
		bool pauseMasked = !masked && MaskPauseInput;
		bool queryMasked = !masked && !pauseMasked && MaskPauseQueryInput;
		// The card's mask keeps the sticks and the D-pad live: its screen does not pause the
		// game, so the stick-zeroing below must not apply to it.
		bool cardMasked = !masked && !pauseMasked && !queryMasked && MaskReceiveItemsInput;
		bool boxMasked = !masked && !pauseMasked && !queryMasked && !cardMasked &&
			MaskMenuBoxInput;
		bool listMasked = !masked && !pauseMasked && !queryMasked && !cardMasked &&
			!boxMasked && MaskScreenMenuInput;
		bool menuMask = masked || pauseMasked || queryMasked || boxMasked || listMasked;
		HostBridge.SetControllerState(
			0, connected: true,
			masked ? WithoutMenuInput(buttons)
			: pauseMasked ? WithoutPauseInput(buttons)
			: queryMasked ? WithoutQueryInput(buttons)
			: cardMasked ? WithoutCardInput(buttons)
			: boxMasked ? WithoutQueryInput(buttons)
			: listMasked ? WithoutQueryInput(buttons)
			: buttons,
			leftTrigger, rightTrigger,
			// The stick is masked with the buttons: `sub_826E39C0` dead-zones the left stick
			// against ±0.75 and emits the same codes the D-pad does — 28/29 for Y at
			// 0x826E3C04/0x826E3C58, 30/31 for X at 0x826E3B50/0x826E3BAC.
			menuMask ? 0 : leftX, menuMask ? 0 : leftY,
			rightX, rightY);

		// The deadzone is ours here: `page.lua` reads `GUI.State.RightThumbStickY` raw (page.dis
		// main.1 pc18..21) because the guest has already dead-zoned it before firing the contract
		// event, and our page reads the pad instead. XInput's right-thumb constant is 8689 of
		// 32767, rescaled so travel starts at zero on the deadzone edge.
		const float RightThumbDeadzone = 8689f;
		float magnitude = System.Math.Abs(rightY);
		RightStickY = magnitude <= RightThumbDeadzone
			? 0f
			: System.Math.Sign(rightY) *
				((magnitude - RightThumbDeadzone) / (32767f - RightThumbDeadzone));

		PublishToHost(buttons, leftX, leftY);
	}

	/// <summary>
	/// Whether the guest's own front-end menu should be shielded from this frame's input.
	/// True only while the guest's active screen is MainMenu in its root substate; the title
	/// keeps every button (A there is the guest's contract, `a2 == 36 &amp;&amp;
	/// *(BYTE*)(a1+80)` at 0x826E3078). `screen+24` is 0 for the menu list; 1 is the guest's own
	/// input lock, 2/3/4 the gender card, 5 the load-game carousel, 6/7 a modal — masking
	/// through those deadlocks, since a modal parks the screen in substate 1 until an A press.
	/// The vtable check is load-bearing: `dword_83496B08` has two writers through the same base
	/// ctor, and the game's base-only object has no screen pointers at +180/+184/+188.
	/// </summary>
	private static bool MaskMenuInput
	{
		get
		{
			bool wouldMask =
				HostBridge.FrontEndGuiIsUp &&
				HostBridge.TryGetFrontEnd(out Engine_UiModel.FrontEndScreen screen, out uint state,
					out _, out _, out _, out _) &&
				screen == Engine_UiModel.FrontEndScreen.MainMenu && state == MenuRootState;

			// Never mask once a world is up, whatever the front-end decode says: masking exists
			// only to stop Godot's main menu and the guest's from acting on one press, and a
			// misdecoded MainMenu here would zero the left stick and freeze the player.
			if (wouldMask && (HostBridge.IsInGame || HostBridge.WorldIsActive()))
			{
				if (!_reportedInGameMask)
				{
					_reportedInGameMask = true;
					GD.Print("[game-input] the front end decoded as MainMenu while a world is up — " +
						"NOT masking input. Left unmasked the player would have been unable to " +
						"move, and the camera with them.");
				}

				return false;
			}

			return wouldMask;
		}
	}

	private static bool _reportedInGameMask;

	/// <summary>Set while the owned pause menu has a group expanded: B then collapses the
	/// group instead of reaching the guest, whose own B on the pause root resumes the game.</summary>
	public static bool PauseMenuSwallowsBack { get; set; }

	/// <summary>
	/// Set while the owned front end answers B itself — a folder open in the list, or the gender
	/// card or warning box up. The guest's B on the root list leaves for the title, and its Lua
	/// highlight always reads "at root" here (28/29 are masked), so B must not reach it.
	/// </summary>
	public static bool FrontEndSwallowsBack { get; set; }

	/// <summary>
	/// The right stick's Y, −1..1, up positive — what `page.lua` scrolls a book page with
	/// (`Page.onUpdate` reads `GUI.State.RightThumbStickY`, page.dis main.1 pc18..21).
	/// </summary>
	public static float RightStickY { get; private set; }

	/// <summary>
	/// Set by the <c>GameUi</c> modal-box drives while an owned box view is standing (query box,
	/// message box, numeric adjuster). A, B and the adjuster's D-pad are then the box's
	/// vocabulary, dispatched through the guest's router; the guest must not also see the press
	/// or the box answers twice. Re-derived every frame, and ANDed with the guest's own state.
	/// </summary>
	public static bool ModalBoxOwnsInput { get; set; }

	/// <summary>Masks the box's buttons only while the guest still says a modal dialog is
	/// pending (the mode latch, over ANY screen — the openers never change the screen id).
	/// Fails open.</summary>
	private static bool MaskPauseQueryInput =>
		ModalBoxOwnsInput && HostBridge.GetPauseState() is { ModalUp: true };

	/// <summary>
	/// Set by <c>GameUi.DriveReceiveItems</c> while the owned received-item card is standing.
	/// The card does not pause the game, so only A (done) and Y (flip) are taken.
	/// </summary>
	public static bool ReceiveItemsOwnsInput { get; set; }

	private static bool MaskReceiveItemsInput =>
		ReceiveItemsOwnsInput &&
		HostBridge.GetPauseState() is { ScreenId: 8, ModalMode: 1 };

	private static uint WithoutCardInput(uint buttons) => buttons & ~(ButtonA | ButtonY);

	/// <summary>
	/// Set by <c>GameUi.DriveMenuBox</c> while the owned option list is standing. Its whole
	/// vocabulary is taken: the D-pad moves the guest's selection through our lever, so a press
	/// the guest also saw would double every step.
	/// </summary>
	public static bool MenuBoxOwnsInput { get; set; }

	private static bool MaskMenuBoxInput => MenuBoxOwnsInput;

	/// <summary>
	/// Set by <c>GameUi.DriveScreenMenu</c> while the owned in-game list is standing. The whole
	/// vocabulary goes, stick included: every press is forwarded to the guest as its own lever
	/// (a direction becomes <c>g_Menu:ScrollUp/ScrollDown()</c>, A/B become action codes 36/37),
	/// and `sub_826E39C0` turns the left stick into the same 28..31 codes the D-pad emits.
	/// Re-derived every frame and ANDed with the guest's own state. Fails open.
	/// </summary>
	public static bool ScreenMenuOwnsInput { get; set; }

	private static bool MaskScreenMenuInput =>
		ScreenMenuOwnsInput && HostBridge.GetPauseState() is { ModalMode: 1, ScreenId: not 0 };

	/// <summary>The buttons with the owned query box's whole vocabulary taken out — A and B
	/// both, unlike the pause root where B is the guest's resume.</summary>
	private static uint WithoutQueryInput(uint buttons) =>
		buttons & ~(DpadUp | DpadDown | DpadLeft | DpadRight | ButtonA | ButtonB);

	/// <summary>
	/// Whether the guest's own pause menu should be shielded from this frame's input. Gated on
	/// the guest's own statement, not the UI's: the mask engages the frame the guest says the
	/// pause root is the active screen (id 0, no modal pending), because the owned view appears
	/// only on the mirror's sampling cadence. The bridge admits only the in-game GUI object
	/// (vtable 0x82001610), so this can never read "pause root" over the front end. Every pause
	/// modal changes the screen id or sets the modal id (query boxes 23/24/25/28), so a dialog
	/// unmasks the frame it opens. Fails open: no pause state, no mask.
	/// </summary>
	private static bool MaskPauseInput =>
		HostBridge.GetPauseState() is { RootActive: true };

	/// <summary>
	/// The buttons with the pause menu's navigation taken out: the guest's pause list is still
	/// populated and `expandablemenuinput.lua` still binds A. LB/RB stay through — the guest's
	/// own tab switch is the lever our tab entries write. B is conditional, see
	/// <see cref="PauseMenuSwallowsBack"/>.
	/// </summary>
	private static uint WithoutPauseInput(uint buttons)
	{
		uint filtered = buttons & ~(DpadUp | DpadDown | DpadLeft | DpadRight | ButtonA);
		if (PauseMenuSwallowsBack)
		{
			filtered &= ~ButtonB;
		}

		return filtered;
	}

	/// <summary>
	/// `screen+24` while the menu LIST is the thing on screen. Every other value is a substate
	/// the guest drives itself — see <see cref="MaskMenuInput"/>.
	/// </summary>
	private const uint MenuRootState = 0;

	/// <summary>
	/// The buttons with the front end's navigation taken out. Godot owns the main menu and
	/// dispatches by name, but the guest never stopped running its own: `sub_826DEE40` still
	/// forwards A and B to `g_Menu` through `OnPressA`/`OnPressB` while `screen+24` is 0, and
	/// the two highlights are different objects. B is left through at the root, where it is the
	/// guest's own path to the title; elsewhere <see cref="FrontEndSwallowsBack"/> masks it.
	/// </summary>
	private static uint WithoutMenuInput(uint buttons)
	{
		uint filtered = buttons & ~(DpadUp | DpadDown | DpadLeft | DpadRight | ButtonA);
		if (FrontEndSwallowsBack)
		{
			filtered &= ~ButtonB;
		}

		return filtered;
	}

	/// <summary>
	/// The same frame's input, in the terms a host-owned menu needs. Reuses the buttons already
	/// gathered for the guest rather than polling the devices a second time, so the two readers
	/// cannot drift. The stick folds into the direction on the shipped threshold —
	/// `THUMBSTICK_RELEASE_THRESHOLD` 0.65, from `guistateandconstants.lua`.
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

		// The same fold across X, for the panels navigated sideways (gender card, load-game
		// carousel). `sub_826E39C0` compares the dead-zoned X against ±0.75 at
		// 0x826E3B3C/0x826E3B98 and emits code 30 left, 31 right, as it emits 28/29 off Y. Its
		// 0.75 is a stick-as-button threshold; one threshold governs both axes here, or a
		// diagonal push scrolls a menu and flips a card at once.
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

		// Edge-detected, as the guest does: holding A must not activate the entry every frame.
		bool a = (buttons & ButtonA) != 0;
		bool b = (buttons & ButtonB) != 0;
		bool y = (buttons & ButtonY) != 0;
		bool lb = (buttons & LeftShoulder) != 0;
		bool rb = (buttons & RightShoulder) != 0;
		bool wheel = (buttons & Back) != 0;
		bool x = (buttons & ButtonX) != 0;
		MenuAcceptPressed = a && !_acceptWasDown;
		MenuAcceptReleased = !a && _acceptWasDown;
		MenuBackPressed = b && !_backWasDown;
		MenuFlipPressed = y && !_flipWasDown;
		MenuTabPrevPressed = lb && !_tabPrevWasDown;
		MenuTabNextPressed = rb && !_tabNextWasDown;
		MenuEquipPressed = x && !_equipWasDown;
		QuickWheelPressed = wheel && !_wheelWasDown;
		QuickWheelReleased = !wheel && _wheelWasDown;
		QuickWheelHeld = wheel;
		_acceptWasDown = a;
		_backWasDown = b;
		_flipWasDown = y;
		_tabPrevWasDown = lb;
		_tabNextWasDown = rb;
		_wheelWasDown = wheel;
		_equipWasDown = x;
	}

	/// <summary>
	/// One direction's stick hysteresis, as `guistateandconstants.lua` states it: engages above
	/// `THUMBSTICK_PUSH_THRESHOLD` (0.70), stays engaged until below
	/// `THUMBSTICK_RELEASE_THRESHOLD` (0.65), hence one held flag per direction.
	/// </summary>
	/// <param name="magnitude">The stick along that direction, signed so pushing this way is
	/// positive.</param>
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

	/// <summary>True on the frame A came up — the expression wheel's held-expression end.</summary>
	public static bool MenuAcceptReleased { get; private set; }

	/// <summary>
	/// The left stick in the Xbox frame (x right, y UP, ±1) — the raw vector the expression
	/// wheel's cursor is computed from.
	/// </summary>
	public static Vector2 LeftStick { get; private set; }

	/// <summary>True on the frame B went down.</summary>
	public static bool MenuBackPressed { get; private set; }

	/// <summary>
	/// True on the frame LB / RB went down — the tab cycle. Forwarded as action codes 72/73 by
	/// the owned drives, because the guest's route from the shoulder bits to those codes is the
	/// GDB-driven action-consumer chain, which does not fire here. The screen handler keeps its
	/// own gates: an open submenu eats the cycle (`screen+420`), `sub_826B83D0` skips
	/// unavailable tabs.
	/// </summary>
	public static bool MenuTabPrevPressed { get; private set; }

	public static bool MenuTabNextPressed { get; private set; }

	private static bool _tabPrevWasDown;
	private static bool _tabNextWasDown;

	/// <summary>True on the frame Y went down — the received-item card's flip.</summary>
	public static bool MenuFlipPressed { get; private set; }

	/// <summary>True on the frame X went down — the received-item card's equip (code 75).</summary>
	public static bool MenuEquipPressed { get; private set; }

	private static bool _equipWasDown;

	private static bool _acceptWasDown;

	private static bool _backWasDown;

	private static bool _flipWasDown;

	/// <summary>
	/// The expression wheel's hold — GameAction 40 on press, 41 on release. A host binding: the
	/// shipped control scheme binds GameAction 40 to `XboxButton` code 15, and that code →
	/// button table is not decoded, so the wheel rides the pad's Back button and keyboard Q.
	/// </summary>
	public static bool QuickWheelPressed { get; private set; }

	public static bool QuickWheelReleased { get; private set; }

	public static bool QuickWheelHeld { get; private set; }

	private static bool _wheelWasDown;

	/// <summary>Whichever axis is further from centre — a pad at rest reads 0 and loses.</summary>
	private static int Dominant(int a, int b) =>
		System.Math.Abs(b) > System.Math.Abs(a) ? b : a;

	/// <summary>Triggers are 0..1 in Godot and 0..255 on the Xbox.</summary>
	private static uint Trigger(int pad, JoyAxis axis) => (uint)Mathf.Clamp(
		Mathf.RoundToInt(Input.GetJoyAxis(pad, axis) * 255.0f), 0, 255);

	/// <summary>Which triggers were last reported held.</summary>
	private static uint _reportedTriggers;

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
	/// The keyboard, in Xbox terms. Always live: Enter/Space is A, Escape/Backspace is B,
	/// the arrows are the D-pad.
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
		if (Input.IsKeyPressed(Key.Q)) buttons |= Back;
		return buttons;
	}

	/// <summary>
	/// The unattended A press, for runs with nobody at the keyboard. Held a few frames then
	/// released, because the guest edge-detects. Repeats on a cooldown rather than pressing
	/// once: the title has an idle timeout into attract mode.
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

		if (!HostBridge.TryGetFrontEnd(out Engine_UiModel.FrontEndScreen screen,
				out uint state, out bool canPressA, out uint cursor,
				out int slide, out _))
		{
			return 0;
		}

		// The title takes A only when idle and armed. The main menu has no such flag and comes
		// up on slide 1 (PressStart), needing a press to reach the menu and another to take the
		// highlighted item, so it is pressed on a cooldown until the front end hands over.
		bool ready = screen switch
		{
			Engine_UiModel.FrontEndScreen.Title => canPressA && state == TitleIdleState,
			Engine_UiModel.FrontEndScreen.MainMenu => true,
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
	/// The title screen's idle state. A is gated by the armed byte `titleScreen+80` (what
	/// <c>canPressA</c> reports): `a2 == 36 &amp;&amp; *(BYTE *)(a1 + 80)` at 0x826E3078, armed by
	/// the presentation's CAN_PRESS_A event at 0x826E2E38; states 0, 9, 10 and 11 all reach it.
	/// Waiting for state 0 avoids state 8, where the attract movie takes A down 0x826E3030 and
	/// only sets its skip flag.
	/// </summary>
	private const uint TitleIdleState = 0;

	private const int AutoStartFrames = 8;
	private const int AutoStartCooldownFrames = 120;
	private static int _autoStartHeld;
	private static int _autoStartCooldown;

	private static uint Bit(int pad, JoyButton button, uint mask) =>
		Input.IsJoyButtonPressed(pad, button) ? mask : 0u;

	/// <summary>
	/// Godot reports -1..1; the guest wants -32768..32767. Scaled by 32767 both ways so a full
	/// deflection is symmetric and cannot overflow the positive end.
	/// </summary>
	private static int Axis(int pad, JoyAxis axis) =>
		Mathf.Clamp(Mathf.RoundToInt(Input.GetJoyAxis(pad, axis) * 32767.0f), -32767, 32767);
}
