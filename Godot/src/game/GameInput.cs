using Refii.Ui;
using Godot;
using Refii.Core;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Feeds Godot's controller state to the guest, once per frame: reads the real device and
/// publishes a snapshot the guest's XInput driver serves from. Translation into the Xbox's
/// vocabulary happens once, here.
/// </summary>
public partial class GameInput : Node
{
	private readonly GameJoypadButtonCapture _buttonCapture = new();
	private GameInputButtonRoute _captureRoute;
	private long _deliveryEpoch, _deliveryCaptureGeneration = -1;
	private uint _deliveryMask = uint.MaxValue;
	private int _deliveryPad0 = int.MinValue;

	/// <summary>Native-fixture metadata override only; production reads actual Android/window/device state.</summary>
	internal GameInputDevicePolicy? DevicePolicyOverride { get; set; }

	/// <summary>Android retains event edges through slow frame polling; desktop keeps its original sampler.</summary>
	private bool CapturePhysicalButtons => DevicePolicyOverride?.CapturePhysicalButtons ?? OS.HasFeature("android");

	/// <summary>Captures mapped physical button events for Android without dispatching actions; called from <see cref="_Input"/>.</summary>
	/// <param name="inputEvent">The real Godot joypad event; axes, keyboard and other event types are unchanged.</param>
	private void CapturePhysicalButton(InputEvent inputEvent)
	{
		if (!CapturePhysicalButtons || inputEvent is not InputEventJoypadButton button) return;
		_buttonCapture.RefreshDevices(ConnectedJoypadIds());
		_buttonCapture.SetContext(HasInputFocus(), _captureRoute);
		_buttonCapture.Capture(button.Device, JoyButtonBit(button.ButtonIndex), button.Pressed);
	}

	/// <summary>Retires pending physical identities when this input owner leaves the scene.</summary>
	public override void _ExitTree()
	{
		SilenceRumble();
		_buttonCapture.RefreshDevices(System.Array.Empty<int>());
		if (CapturePhysicalButtons)
			ControllerStateSink?.Invoke(new GameControllerPacket(0, false, 0, 0, 0, 0, 0, 0, 0));
	}
	/// <summary>Optional embedded-owner mailbox; when set, the direct native writer is not called.</summary>
	public System.Action<GameControllerPacket>? ControllerStateSink { get; set; }

	/// <summary>External application-focus gate; keep processing neutral samples so menu edges reset.</summary>
	public bool DevicesEnabled { get; set; } = true;

	/// <summary>The latest raw player-one sample before production menu ownership masks.</summary>
	public GameControllerPacket? LastRawPacket { get; private set; }

	/// <summary>The latest player-one sample after masks and held-across-unmask protection.</summary>
	public GameControllerPacket? LastFilteredPacket { get; private set; }

	/// <summary>A bounded main-thread snapshot of device identities, raw axes, assignments and masks.</summary>
	/// <returns>Observed physical state and the actual production packet; no input is changed.</returns>
	public object DescribeControllerInput()
	{
		var devices = new System.Collections.Generic.List<object>();
		foreach (int id in Input.GetConnectedJoypads())
		{
			if (devices.Count >= 8) break;
			devices.Add(new
			{
				id, name = Input.GetJoyName(id), guid = Input.GetJoyGuid(id), known = Input.IsJoyKnown(id),
				selected_guest_pad = System.Array.IndexOf(_padDevice, id), buttons = JoypadButtons(id),
				left_x = Input.GetJoyAxis(id, JoyAxis.LeftX), left_y = Input.GetJoyAxis(id, JoyAxis.LeftY),
				right_x = Input.GetJoyAxis(id, JoyAxis.RightX), right_y = Input.GetJoyAxis(id, JoyAxis.RightY),
				left_trigger = Input.GetJoyAxis(id, JoyAxis.TriggerLeft),
				right_trigger = Input.GetJoyAxis(id, JoyAxis.TriggerRight)
			});
		}
		return new
		{
			devices, selected_pad0_device = _padDevice[0], focused = HasInputFocus(),
			raw_packet = LastRawPacket, filtered_packet = LastFilteredPacket,
			menu_mask = MaskMenuInput, pause_mask = MaskPauseInput, query_mask = MaskPauseQueryInput,
			card_mask = MaskReceiveItemsInput, box_mask = MaskMenuBoxInput, screen_mask = MaskScreenMenuInput,
			denied_button_bits = _maskBits[0], held_across_unmask = _heldAcrossUnmask[0],
			menu_direction = MenuDirection, menu_horizontal = MenuHorizontal,
			accept_edge = MenuAcceptPressed, back_edge = MenuBackPressed,
			physical_button_capture = CapturePhysicalButtons, captured_button_overflows = _buttonCapture.Overflows,
			touch_mode = Engine_UiInputMode.Touch, touch_hold = Touch,
			pointer_free = Engine_UiPointer.Free, pointer_canvas = Engine_UiPointer.Canvas?.ToString(),
			mouse_mode = Input.MouseMode.ToString(), playing = Playing, in_overlay = InOverlay,
			transport = ControllerStateSink == null ? "direct desktop bridge" : "single-player guest-owner mailbox"
		};
	}

	/// <summary>Xbox 360 supports four pads, and so does the guest.</summary>
	internal const int MaxControllers = 4;

	/// <summary>
	/// XInput button bits. These are the guest's values, not Godot's — see
	/// <c>X_INPUT_GAMEPAD_BUTTON</c>.
	/// </summary>
	internal const uint DpadUp = 0x0001;
	internal const uint DpadDown = 0x0002;
	internal const uint DpadLeft = 0x0004;
	internal const uint DpadRight = 0x0008;
	internal const uint Start = 0x0010;
	internal const uint Back = 0x0020;
	internal const uint LeftThumb = 0x0040;
	internal const uint RightThumb = 0x0080;
	internal const uint LeftShoulder = 0x0100;
	internal const uint RightShoulder = 0x0200;
	internal const uint ButtonA = 0x1000;
	internal const uint ButtonB = 0x2000;
	internal const uint ButtonX = 0x4000;
	internal const uint ButtonY = 0x8000;

	/// <summary>Whether A is down on any source this frame, as published to the guest.</summary>
	public static bool AHeld { get; private set; }

	public static bool YHeld { get; private set; }

	/// <summary>Samples devices once, preserving production menu navigation and packet masking.</summary>
	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("gameinput");
		// The guest can end while this node is up; nothing is published into a torn-down runtime.
		if (HostBridge.TryGetAbiVersion() is null)
		{
			// A joypad left buzzing by a guest that is no longer there would buzz until unplugged.
			SilenceRumble();
			return;
		}

		// INPUT REACHES THE GUEST ONLY WHILE THE GAME WINDOW HAS FOCUS.
		//
		// Godot polls joypads from the DEVICE, not from window events — so an unfocused game
		// still saw every stick and button, and a controller left alone on the desk drove the
		// hero while its owner was in another application. The keyboard does not have this
		// problem (no focus, no key events), which is why it only ever showed up as the pad.
		//
		// Unfocused publishes a NEUTRAL pad rather than nothing. Skipping the publish would
		// leave the guest holding whatever was last sent — a stick mid-push stays pushed, a
		// held button stays down — and the guest edge-detects, so it would never see the
		// release. Same rule as the menu stick: every press must get its release.
		//
		// The DEVICES are what focus gates — the keyboard and the joypads. The scripted
		// sources (the debug channel's presses, sticks and replay tapes, the auto driver) are
		// not devices and reach the guest focused or not: an unattended run is driven by them
		// with nobody at the keyboard and a window that may never take focus, and a
		// controller playing another game must not steer this one meanwhile.
		//
		// One gamepad per guest pad. The guest polls all four (`InputDeviceMgr_EnumeratePads`
		// 0x821A8CC8 loops XamInputGetCapabilities 0..3) and tags every event with its pad
		// (`ev+40`). A START on a pad that is not player one's is the local drop-in
		// (`sub_82402F70` 0x824031ec → `Coop_SetSharedScreenMode(game+140, 1, pad)`), and player
		// two's handler takes only `ev.pad == Player+40` (`sub_829C1C08` 0x829c1c88).
		// Each step under its own phase, so a slow frame here names the step on the debug channel.
		using (Refii.Core.Engine_FramePhases.Measure("input.settings"))
		{
			ReadControlSettings(delta);
			AdvancePointerButtons(delta);
		}

		bool devices = HasInputFocus();
		if (CapturePhysicalButtons)
		{
			_buttonCapture.RefreshDevices(ConnectedJoypadIds());
			_captureRoute = ReadButtonRoute();
			_buttonCapture.SetContext(devices, _captureRoute);
		}
		using (Refii.Core.Engine_FramePhases.Measure("input.kbm"))
		{
			UpdateKeyboardMouse(devices);
		}

		using (Refii.Core.Engine_FramePhases.Measure("input.pads"))
		{
			AssignPads(devices);
			for (int pad = 0; pad < MaxControllers; ++pad)
			{
				PublishPad(pad, devices);
			}
		}

		using (Refii.Core.Engine_FramePhases.Measure("input.rumble"))
		{
			DriveRumble(devices);
		}

		// The mods' own actions, on the keyboard and on the pads as just assigned, under the
		// same focus gate: edges only, posted to the mod event bus. Photo mode has every key and
		// button while it is up, so the mods see releases then.
		using (Refii.Core.Engine_FramePhases.Measure("input.mods"))
		{
			Refii.Mods.ModInput.Pump(devices && !GamePhotoMode.Active && !Engine_TitleGame.Active.InteractionOwnsDevices);
		}
	}

	public static void ReleaseForPhotoMode()
	{
		SilenceRumble();
		PlayButtons.ReleaseAll();
		Engine_TitleGame.Active.Input.HoldUntilReleased();
		for (int pad = 0; pad < MaxControllers; pad++)
		{
			HostBridge.SetControllerState(pad, pad == 0 || _padDevice[pad] >= 0 || GameDebugServer.PadConnected(pad), 0, 0, 0, 0, 0, 0, 0);
		}
	}

	/// <summary>Each guest pad's rumble as last applied to its joypad.</summary>
	private static readonly Engine_Rumble[] _rumble =
	{
		new Engine_Rumble(), new Engine_Rumble(), new Engine_Rumble(), new Engine_Rumble(),
	};

	/// <summary>The joypad each pad's running effect was started on, or -1.</summary>
	private static readonly int[] _rumbleJoypad = { -1, -1, -1, -1 };

	/// <summary>Pad 0's rumble as last applied to the phone's own vibrator.</summary>
	private static readonly Engine_HandheldRumble _handheldRumble = new();

	/// <summary>
	/// RUMBLE, THE OTHER WAY DOWN THE SAME SEAM. The guest states each pad's motors through
	/// <c>XamInputSetState</c> every frame the pad is connected (<c>sub_822B38C0</c>: the effect
	/// mixer at <c>pad+72</c> sums into <c>pad+84/+88</c>, ×65535 into the <c>XINPUT_VIBRATION</c>
	/// at <c>pad+92</c>, then <c>XInputSetState(pad+4, …)</c>). The kernel routes user N to our
	/// driver's slot N, <see cref="HostBridge.TryGetVibration"/> reads it, and the joypad holding
	/// guest pad N is the one that buzzes — so on split screen each player's hits reach their own
	/// pad, since the guest picks the pad object off the hit entity's player (<c>sub_823EAA58</c>,
	/// <c>*(player+40)</c>).
	/// </summary>
	/// <remarks>
	/// <para>Change-driven through <see cref="Engine_Rumble"/>: Godot restarts the effect on every
	/// <c>StartJoyVibration</c>, so a steady pair is told once and silence stops it once, and the
	/// pair is compared at the pad's own byte resolution so a decay restarts the effect only when
	/// the motor would move. The effect runs until stopped (duration 0); the guest's own decay is
	/// what ends it, frame by frame, the way it ended on the console.</para>
	/// <para>None of this blocks: <c>Input::start_joy_vibration</c> takes the Input mutex and
	/// writes a struct (core/input/input.cpp), and the device write — <c>SDL_RumbleJoystick</c>
	/// in <c>JoypadSDL::process_events</c> — runs in <c>OS_LinuxBSD::run</c> before
	/// <c>Main::iteration</c>, outside every node's <c>_Process</c>.</para>
	/// <para>Under the focus rule: an unfocused window publishes a neutral pad and must not keep a
	/// controller buzzing on the desk either, so <paramref name="devices"/> false silences every
	/// pad, and the guest's pair is picked up again on the first focused frame.</para>
	/// <para>Two switches, both required: the guest's own (<c>byte_8331924F</c>, the Controls
	/// fold's Controller Vibration page — zero and no effect is ever stated) and the launcher's
	/// <see cref="ControllerVibration"/>, which silences the joypad whatever is stated.</para>
	/// </remarks>
	private static void DriveRumble(bool devices)
	{
		for (int pad = 0; pad < MaxControllers; ++pad)
		{
			int joypad = _padDevice[pad];
			uint left = 0;
			uint right = 0;
			bool stated = devices && ControllerVibration && joypad >= 0 &&
				HostBridge.TryGetVibration(pad, out left, out right);
			Engine_Rumble.Verdict verdict = stated ? _rumble[pad].Observe(left, right) : _rumble[pad].Silence();
			switch (verdict)
			{
				case Engine_Rumble.Verdict.Start:
					// The pad may have moved joypads while running (a reconnect takes a new id).
					if (_rumbleJoypad[pad] >= 0 && _rumbleJoypad[pad] != joypad)
					{
						Input.StopJoyVibration(_rumbleJoypad[pad]);
					}

					_rumbleJoypad[pad] = joypad;
					Input.StartJoyVibration(joypad, _rumble[pad].Weak, _rumble[pad].Strong, 0f);
					break;
				case Engine_Rumble.Verdict.Stop:
					if (_rumbleJoypad[pad] >= 0)
					{
						Input.StopJoyVibration(_rumbleJoypad[pad]);
					}

					_rumbleJoypad[pad] = -1;
					break;
			}
		}

		DriveHandheldRumble(devices);
	}

	/// <summary>
	/// Pad 0's rumble on the phone itself, while the touchscreen is the device on Android and no
	/// controller holds pad 0 — a touch player has no joypad for <see cref="DriveRumble"/> to tell.
	/// The same gates as the joypad's (focus, the guest's switch, <see cref="ControllerVibration"/>);
	/// pulses and stops as <see cref="Engine_HandheldRumble"/> decides, so the Java bridge hears only
	/// a change or a renewal. A controller taking the pad, or a key press, stops the phone at once.
	/// </summary>
	private static void DriveHandheldRumble(bool devices)
	{
		uint left = 0;
		uint right = 0;
		bool stated = devices && ControllerVibration && Engine_Platform.IsAndroid && Engine_UiInputMode.Touch &&
			_padDevice[0] < 0 && HostBridge.TryGetVibration(0, out left, out right);
		ApplyHandheldRumble(stated ? _handheldRumble.Observe(left, right, Time.GetTicksMsec()) : _handheldRumble.Silence());
	}

	/// <summary>Tells the phone's vibrator a verdict; <c>vibrate_handheld</c> has no stop, so a stop is a pulse too short to feel.</summary>
	private static void ApplyHandheldRumble(Engine_HandheldRumble.Verdict verdict)
	{
		switch (verdict)
		{
			case Engine_HandheldRumble.Verdict.Pulse:
				Input.VibrateHandheld(Engine_HandheldRumble.PulseMs, _handheldRumble.Amplitude);
				break;
			case Engine_HandheldRumble.Verdict.Stop:
				Input.VibrateHandheld(Engine_HandheldRumble.StopMs, Engine_HandheldRumble.StopAmplitude);
				break;
		}
	}

	/// <summary>Stops every running effect: the guest is down or this node is leaving.</summary>
	private static void SilenceRumble()
	{
		for (int pad = 0; pad < MaxControllers; ++pad)
		{
			if (_rumble[pad].Silence() == Engine_Rumble.Verdict.Stop && _rumbleJoypad[pad] >= 0)
			{
				Input.StopJoyVibration(_rumbleJoypad[pad]);
			}

			_rumbleJoypad[pad] = -1;
		}

		ApplyHandheldRumble(_handheldRumble.Silence());
	}

	/// <summary>Pad 0's rumble on the phone for the debug channel: whether a pulse runs, and its strength.</summary>
	public static (bool Running, float Amplitude) HandheldRumbleReadout() =>
		(_handheldRumble.Running, _handheldRumble.Amplitude);

	/// <summary>
	/// One pad's rumble for the debug channel: the joypad holding it, the pair the guest states
	/// now, and what the joypad was last told.
	/// </summary>
	public static (int Joypad, uint StatedLeft, uint StatedRight, bool Running, float Weak, float Strong)
		RumbleReadout(int pad)
	{
		if (pad < 0 || pad >= MaxControllers)
		{
			return (-1, 0, 0, false, 0f, 0f);
		}

		uint left = 0;
		uint right = 0;
		if (!HostBridge.TryGetVibration(pad, out left, out right))
		{
			left = 0;
			right = 0;
		}

		Engine_Rumble rumble = _rumble[pad];
		return (_padDevice[pad], left, right, rumble.Running, rumble.Weak, rumble.Strong);
	}

	/// <summary>The joypad holding guest pad <paramref name="pad"/> (0..3), or -1 while it is free.</summary>
	public static int JoypadForPad(int pad) =>
		pad >= 0 && pad < MaxControllers ? _padDevice[pad] : -1;

	/// <summary>Mouse events go to the UI pointer, which resolves them against the UI camera.</summary>
	public override void _Input(InputEvent @event)
	{
		// A joypad the cached list has never seen is read in now, whether or not Godot's connection
		// signal reached us (OnJoyConnectionChanged): a controller paired after the game started
		// otherwise switched the menus to it and drove nothing.
		if (@event is InputEventJoypadButton or InputEventJoypadMotion && DevicePolicyOverride is null &&
			_joypadsHooked && !IsJoypadConnected(_connectedJoypads, @event.Device))
		{
			RefreshConnectedJoypads();
		}

		if (@event is InputEventMouse)
		{
			if (!MouseInput)
			{
				return;
			}

			Engine_UiPointer.Feed(@event);
			if (!IsTouchPointer(@event))
			{
				FeedMouseMotion(@event);
			}

			DetectDevice(@event);
			return;
		}

		Engine_UiPointer.Feed(@event);
		FeedMouseMotion(@event);
		FeedMouseWheel(@event);
		DetectDevice(@event);
		CapturePhysicalButton(@event);
	}

	/// <summary>
	/// THE DEVICE SWITCH. Whatever was touched last is the device: a key, a mouse button or a
	/// real mouse movement makes it the keyboard and mouse; a pad button or a stick or trigger
	/// pushed past a deliberate threshold makes it the controller, at once. Joypads Godot has no
	/// mapping for are ignored — a keyboard's or mouse's own HID interface can enumerate as one.
	/// </summary>
	private static void DetectDevice(InputEvent @event)
	{
		switch (@event)
		{
			// Android's back gesture arrives as the Back key: the touchscreen's own, not a keyboard,
			// so it leaves the touch controls on screen.
			case InputEventKey { Pressed: true, Keycode: Key.Back }:
				break;
			case InputEventKey { Pressed: true }:
				Engine_UiInputMode.KeyboardMouse = true;
				break;
			// A finger is the touchscreen, never the mouse: the menus take its taps, the prompts
			// keep the pad's art.
			case InputEventMouseButton { Pressed: true } when MouseInput && IsTouchPointer(@event):
				Engine_UiInputMode.Touch = true;
				break;
			case InputEventMouseMotion when IsTouchPointer(@event):
				break;
			case InputEventMouseButton { Pressed: true } when MouseInput:
			case InputEventMouseMotion motion when MouseInput && motion.Relative.LengthSquared() > 4f:
				Engine_UiInputMode.KeyboardMouse = true;
				break;
			case InputEventJoypadButton { Pressed: true } button when Input.IsJoyKnown(button.Device):
				Engine_UiInputMode.KeyboardMouse = false;
				break;
			case InputEventJoypadMotion axis when Input.IsJoyKnown(axis.Device) &&
				Mathf.Abs(axis.AxisValue) > 0.5f:
				Engine_UiInputMode.KeyboardMouse = false;
				break;
		}
	}

	/// <summary>
	/// How long a pointer-made press is held. The guest reads the pad on its own 30 Hz step and
	/// edge-detects, so a one-frame pulse at a high display rate can fall between two reads:
	/// three guest steps is a press it cannot miss.
	/// </summary>
	private const double PointerHold = 0.1;

	private static double _pointerB;

	/// <summary>Any other pad buttons a pointer target presses (a tab arrow is a shoulder), and how
	/// long they stay held.</summary>
	private static uint _pointerExtra;
	private static double _pointerExtraFor;

	/// <summary>Presses pad buttons for a clicked target — held long enough for the guest's read.</summary>
	public static void QueuePointerPress(uint buttons)
	{
		_pointerExtra |= buttons;
		_pointerExtraFor = PointerHold;
	}

	/// <summary>The XInput bits a pointer target can press, for callers outside this class.</summary>
	public const uint PadLeftShoulder = LeftShoulder;
	public const uint PadRightShoulder = RightShoulder;
	public const uint PadA = ButtonA;
	public const uint PadB = ButtonB;
	public const uint PadX = ButtonX;
	public const uint PadY = ButtonY;
	public const uint PadLeft = DpadLeft;
	public const uint PadRight = DpadRight;
	public const uint PadUp = DpadUp;
	public const uint PadDown = DpadDown;

	/// <summary>Wheel notches still to be played out as D-pad steps, signed (negative up).</summary>
	private static int _wheelPending;

	/// <summary>
	/// Steps a list's scroll bar asked of a cursor that is the guest's, played out exactly as the
	/// wheel's notches are but uncapped: a bar names how far the cursor must go.
	/// </summary>
	private static int _stepsPending;
	private static double _wheelClock;
	private static uint _wheelBit;

	/// <summary>The pad bits the pointer holds this frame.</summary>
	private static uint _pointerBits;

	/// <summary>
	/// The UI's leftovers: a right click and wheel notches, played out as B and D-pad steps so a
	/// screen the guest drives itself answers them like the pad. A stray left click is not one.
	/// </summary>
	public static void QueuePointerButtons(bool right, int wheel)
	{
		if (right)
		{
			_pointerB = PointerHold;
		}

		_wheelPending = System.Math.Clamp(_wheelPending + wheel, -8, 8);
	}

	/// <summary>Queues signed highlight steps (negative up) to be played out as D-pad presses.</summary>
	public static void QueuePointerSteps(int steps) => _stepsPending += steps;

	/// <summary>Drops the scroll bar's steps not yet played out: a new grab counts from scratch.</summary>
	public static void ClearPointerSteps() => _stepsPending = 0;

	private static void AdvancePointerButtons(double delta)
	{
		_pointerB = System.Math.Max(0.0, _pointerB - delta);

		// One notch = a held step, then a released gap of the same length: every press needs its
		// release, or the guest sees one long hold.
		_wheelClock -= delta;
		if (_wheelClock <= 0.0)
		{
			if (_wheelBit != 0u)
			{
				_wheelBit = 0u;
				_wheelClock = PointerHold;
			}
			else if (_wheelPending != 0)
			{
				_wheelBit = _wheelPending < 0 ? DpadUp : DpadDown;
				_wheelPending -= System.Math.Sign(_wheelPending);
				_wheelClock = PointerHold;
			}
			else if (_stepsPending != 0)
			{
				_wheelBit = _stepsPending < 0 ? DpadUp : DpadDown;
				_stepsPending -= System.Math.Sign(_stepsPending);
				_wheelClock = PointerHold;
			}
		}

		_pointerExtraFor = System.Math.Max(0.0, _pointerExtraFor - delta);
		if (_pointerExtraFor <= 0.0)
		{
			_pointerExtra = 0u;
		}

		_pointerBits = (_pointerB > 0.0 ? ButtonB : 0u) |
			_wheelBit | _pointerExtra;
	}

	/// <summary>`camera_invert_x` in refii.toml — the horizontal invert the guest lacks.</summary>
	public static bool InvertCameraX { get; private set; }

	/// <summary>`mouse_sensitivity` in refii.toml, 1.0 by default.</summary>
	public static float MouseSensitivity { get; private set; } = 1f;

	/// <summary>`mouse_input` in refii.toml — whether mouse input is processed at all.</summary>
	public static bool MouseInput { get; private set; } = true;

	/// <summary>`mouse_lock` in refii.toml — whether the mouse cursor is captured during play.</summary>
	public static bool MouseLock { get; private set; } = true;

	/// <summary>`controller_vibration` in refii.toml, on by default — the host's rumble gate, for a joypad and for the phone played by touch.</summary>
	public static bool ControllerVibration { get; private set; } = true;

	private static double _settingsClock;

	/// <summary>Rereads refii.toml on the next frame: a setting the player just changed in game takes effect now.</summary>
	public static void RereadControlSettings() => _settingsClock = 0.0;
	private static System.Collections.Generic.IReadOnlyDictionary<string, string>? _lastControlValues;

	/// <summary>
	/// The player's control settings, re-checked periodically: the launcher writes refii.toml
	/// while the game runs, and the reader caches by write time, so key bindings are only
	/// re-parsed when the returned dictionary instance actually changes.
	/// </summary>
	private static void ReadControlSettings(double delta)
	{
		_settingsClock -= delta;
		if (_settingsClock > 0.0)
		{
			return;
		}

		_settingsClock = 2.0;
		var values = Refii.Core.Engine_RefiiConfig.Read(out _);
		if (ReferenceEquals(values, _lastControlValues))
		{
			return;
		}
		bool prevMouseInput = MouseInput;
		_lastControlValues = values;
		InvertCameraX = Refii.Core.Engine_RefiiConfig.Bool(values, "camera_invert_x", false);
		MouseInput = Refii.Core.Engine_RefiiConfig.Bool(values, "mouse_input", true);
		MouseLock = Refii.Core.Engine_RefiiConfig.Bool(values, "mouse_lock", true);
		if (prevMouseInput && !MouseInput)
		{
			Engine_UiInputMode.KeyboardMouse = false;
		}
		ControllerVibration = Refii.Core.Engine_RefiiConfig.Bool(values, "controller_vibration", true);
		ReadKeyBindings(values);
		// The mods' actions take their rebinds (key_<mod>.<action>, pad_<mod>.<action>) from the same read.
		Refii.Mods.ModInput.ReadBindings(values);
		MouseSensitivity = Mathf.Clamp(
			Refii.Core.Engine_RefiiConfig.Float(values, "mouse_sensitivity", 1f), 0.05f, 10f);
	}

	/// <summary>
	/// The Godot joypad holding each guest pad, −1 free. A gamepad takes the lowest free pad
	/// on its first press and keeps it until it disconnects, so whoever presses A on the title
	/// is pad 0 — the pad the front end latches (dword_83496AF8, 0x826E30DC) and binds to
	/// player one. Pad 0 also carries the keyboard, the debug channel and every joypad Godot
	/// has no gamepad mapping for (a keyboard's or mouse's HID interface can enumerate as one),
	/// so an unmapped device never claims a pad.
	/// </summary>
	private static readonly int[] _padDevice = { -1, -1, -1, -1 };

	private static (int Id, bool Known)[] _connectedJoypads = System.Array.Empty<(int, bool)>();
	private static bool _joypadsHooked;

	private static void EnsureJoypadsHooked()
	{
		if (_joypadsHooked)
		{
			return;
		}

		_joypadsHooked = true;
		Input.JoyConnectionChanged += OnJoyConnectionChanged;
		RefreshConnectedJoypads();
	}

	private static void OnJoyConnectionChanged(long device, bool connected)
	{
		RefreshConnectedJoypads();
		GD.Print($"[input] joypad {device} {(connected ? "connected" : "disconnected")}");
	}

	private static void RefreshConnectedJoypads()
	{
		Godot.Collections.Array<int> connected = Input.GetConnectedJoypads();
		try
		{
			if (connected.Count == 0)
			{
				_connectedJoypads = System.Array.Empty<(int, bool)>();
				return;
			}

			var list = new (int Id, bool Known)[connected.Count];
			for (int i = 0; i < connected.Count; ++i)
			{
				int id = connected[i];
				list[i] = (id, Input.IsJoyKnown(id));
			}

			if (list.Length != _connectedJoypads.Length)
			{
				GD.Print($"[input] {list.Length} joypad(s) connected: {string.Join(", ", System.Array.ConvertAll(list, j => $"{j.Id}{(j.Known ? "" : " (unmapped)")}"))}");
			}

			_connectedJoypads = list;
		}
		finally
		{
			((Godot.Collections.Array)connected).Dispose();
		}
	}

	/// <summary>Serves the cached Godot enumeration unless a native fixture supplied device metadata.</summary>
	/// <returns>Each connected identity with whether it has a real mapping.</returns>
	private (int Id, bool Known)[] ConnectedJoypadStates()
	{
		if (DevicePolicyOverride is { } policy)
		{
			var states = new (int Id, bool Known)[policy.Connected.Count];
			for (int i = 0; i < states.Length; ++i)
			{
				states[i] = (policy.Connected[i], policy.Known(policy.Connected[i]));
			}

			return states;
		}

		EnsureJoypadsHooked();
		return _connectedJoypads;
	}

	private static bool IsJoypadConnected((int Id, bool Known)[] connected, int id)
	{
		foreach ((int connectedId, _) in connected)
		{
			if (connectedId == id)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>Preserves first-press assignment, including an Android tap already released before this frame.</summary>
	/// <param name="devices">Whether real devices have application/window focus.</param>
	private void AssignPads(bool devices)
	{
		(int Id, bool Known)[] connected = ConnectedJoypadStates();
		for (int pad = 0; pad < MaxControllers; ++pad)
		{
			if (_padDevice[pad] >= 0 && !IsJoypadConnected(connected, _padDevice[pad]))
			{
				GD.Print($"[input] joypad {_padDevice[pad]} disconnected — guest pad {pad} is free");
				_padDevice[pad] = -1;
			}
		}

		if (!devices)
		{
			return;
		}

		foreach ((int joypad, bool known) in connected)
		{
			if (!known || System.Array.IndexOf(_padDevice, joypad) >= 0 ||
				(JoypadButtons(joypad) | (CapturePhysicalButtons ? _buttonCapture.PendingButtons(joypad) : 0)) == 0)
			{
				continue;
			}

			int free = System.Array.IndexOf(_padDevice, -1);
			if (free < 0)
			{
				return;
			}

			_padDevice[free] = joypad;
			GD.Print($"[input] joypad {joypad} '{Input.GetJoyName(joypad)}' is guest pad {free}");
		}
	}

	/// <summary>One pad's raw state, before the menu masks.</summary>
	public struct PadState
	{
		public uint Buttons, PhysicalButtons, OtherButtons, LeftTrigger, RightTrigger;
		public int LeftX, LeftY, RightX, RightY;

		/// <summary>Merges unchanged analog polling with the caller's physical button sample.</summary>
		/// <param name="joypad">The device whose current analog values are read.</param>
		/// <param name="buttons">Ordinary desktop polling or the Android event-retained sample.</param>
		public void Merge(int joypad, uint buttons)
		{
			Buttons |= buttons;
			PhysicalButtons |= buttons;
			LeftTrigger = System.Math.Max(LeftTrigger, Trigger(joypad, JoyAxis.TriggerLeft));
			RightTrigger = System.Math.Max(RightTrigger, Trigger(joypad, JoyAxis.TriggerRight));
			LeftX = Dominant(LeftX, Axis(joypad, JoyAxis.LeftX));
			// Negated: Godot's stick Y is screen-space (down positive), the Xbox's is up-positive.
			LeftY = Dominant(LeftY, -Axis(joypad, JoyAxis.LeftY));
			RightX = Dominant(RightX, Axis(joypad, JoyAxis.RightX));
			RightY = Dominant(RightY, -Axis(joypad, JoyAxis.RightY));
		}
	}

	/// <summary>
	/// Pads 1-3: the gamepad holding the pad and the debug channel's presses aimed at it.
	/// Connected while either holds it — a debug press connects the pad for the rest of the
	/// run, since a pad that vanished between presses would read to the guest as unplugged.
	/// The menu masks apply as on pad 0: an owned menu's vocabulary must not reach the guest
	/// from any pad.
	/// </summary>
	private void PublishPad(int pad, bool devices)
	{
		if (pad == 0)
		{
			Publish0(devices);
			return;
		}

		var state = new PadState { Buttons = GameDebugServer.Buttons(pad) };
		state.OtherButtons = state.Buttons;
		if (GameDebugServer.Sticks(pad, out int debugLX, out int debugLY, out int debugRX,
				out int debugRY, out uint debugLT, out uint debugRT))
		{
			state.LeftX = debugLX;
			state.LeftY = debugLY;
			state.RightX = debugRX;
			state.RightY = debugRY;
			state.LeftTrigger = debugLT;
			state.RightTrigger = debugRT;
		}

		int joypad = _padDevice[pad];
		if (devices && joypad >= 0)
		{
			state.Merge(joypad, PhysicalButtons(joypad));
		}

		bool connected = joypad >= 0 || GameDebugServer.PadConnected(pad);
		// A MOD'S CLAIMED ACTIONS leave this pad before the guest reads it (ModInput.Withhold),
		// for the player the title says the pad is.
		int player = Engine_TitleGame.Active.Input.ModPlayer(pad);
		if (player != 0)
		{
			Refii.Mods.ModInput.Withhold(player, ref state.Buttons, ref state.LeftTrigger, ref state.RightTrigger);
		}

		Engine_TitleGame.Active.Input.PublishPad(pad, connected, state, state);
	}

	/// <summary>

	/// <summary>
	/// Whether the DEVICES reach the guest this frame. No exemption for unattended runs: they
	/// are driven by the scripted sources, which <see cref="Publish0"/> publishes regardless.
	/// </summary>
	private bool HasInputFocus()
	{
		// `GetWindow()` is the nearest Window ancestor — the game's own window, not the
		// launcher's, so a focused launcher does not count as a focused game.
		Window? window = IsInsideTree() ? GetWindow() : null;
		return GameControllerTransport.DevicesHaveFocus(DevicesEnabled,
			DevicePolicyOverride?.WindowFocused ?? (window is null || window.HasFocus()));
	}

	private readonly System.Collections.Generic.List<int> _connectedJoypadIds = new();

	/// <summary>
	/// The connected identities for the capture lifetime: the same cached enumeration pad
	/// assignment uses (refreshed on Godot's connection signal), unless a native fixture supplied
	/// metadata.
	/// </summary>
	/// <remarks>
	/// Android refreshes the capture every frame. Asking Godot each time made a Godot array (a
	/// finalizable wrapper) per frame, and the capture a set and a list besides.
	/// </remarks>
	private System.Collections.Generic.List<int> ConnectedJoypadIds()
	{
		_connectedJoypadIds.Clear();
		foreach ((int id, _) in ConnectedJoypadStates())
		{
			_connectedJoypadIds.Add(id);
		}

		return _connectedJoypadIds;
	}

	/// <summary>Augments only Android physical buttons before the existing aggregation and ownership masks.</summary>
	/// <param name="device">The assigned or unmapped device sampled once this frame.</param>
	/// <returns>The current physical hold plus a bounded unsampled event edge.</returns>
	private uint PhysicalButtons(int device) => CapturePhysicalButtons
		? _buttonCapture.Sample(device, JoypadButtons(device)) : JoypadButtons(device);

	/// <summary>Keys pending taps to actual native screens/modals and host ownership, without bypassing input gates.</summary>
	/// <returns>A changes-only identity; highlights and animation progress deliberately do not invalidate a held input.</returns>
	private GameInputButtonRoute ReadButtonRoute()
	{
		if (DevicePolicyOverride is { } policy) return policy.Route;
		bool found = HostBridge.TryGetFrontEnd(out var screen, out uint state, out _, out _, out _, out _);
		Engine_UiModel.PauseState? pause = HostBridge.GetPauseState();
		uint flags = (PauseMenuSwallowsBack ? 1u : 0) | (FrontEndSwallowsBack ? 2u : 0) |
			(ModalBoxOwnsInput ? 4u : 0) | (DisplayMessageBoxUp ? 8u : 0) |
			(ReceiveItemsOwnsInput ? 16u : 0) | (MenuBoxOwnsInput ? 32u : 0) |
			(ScreenMenuOwnsInput ? 64u : 0) | (FrontEndDrive.ListUp ? 128u : 0) |
			(FrontEndDrive.CardUp ? 256u : 0) | (FrontEndDrive.WarningUp ? 512u : 0);
		return new(found, found ? (uint)screen : 0, found ? state : 0,
			pause?.ScreenId ?? uint.MaxValue, pause?.ModalId ?? 0, pause?.ModalMode ?? 0,
			pause?.CurrentTab ?? 0, flags);
	}

	/// <summary>Said once per focus change, not once per frame.</summary>
	private static bool _reportedUnfocused;

	/// <summary>
	/// Pad 0: the keyboard, the touch controls, the gamepad holding pad 0 and every unmapped joypad, merged, plus
	/// the scripted sources. Buttons are OR'd; triggers and sticks take whichever device is
	/// pushing hardest, so a pad at rest never cancels one in use. With
	/// <paramref name="devices"/> false the keyboard and joypads are muted (the window is
	/// unfocused) and the pad is still published every frame, so anything a device held is
	/// released exactly once and stays released — the guest edge-detects, and a publish skipped
	/// would leave a stick mid-push forever.
	/// </summary>
	private void Publish0(bool devices)
	{
		if (devices == _reportedUnfocused)
		{
			_reportedUnfocused = !devices;
			GD.Print(devices
				? "[input] the game window has focus — the devices reach the guest again"
				: "[input] the game window lost focus — the devices are muted; only the debug channel and the auto driver reach the guest");
		}

		// NO AUTO-DRIVER. The debug channel (the MCP) presses the pad when a run has to be
		// driven; the title-to-world and menu-walking scripts that lived here are gone.
		var state = new PadState
		{
			Buttons = (devices ? _pointerBits : 0u) | GameDebugServer.Buttons(0),
		};
		state.OtherButtons = state.Buttons;

		if (devices)
		{
			KeyboardMouseState(ref state);
			TouchState(ref state);
		}

		if (devices)
		{
			foreach ((int joypad, bool known) in ConnectedJoypadStates())
			{
				if (joypad == _padDevice[0] || !known)
				{
					state.Merge(joypad, PhysicalButtons(joypad));
				}
			}
		}

		// The debug channel's held sticks and triggers merge like one more device.
		if (GameDebugServer.Sticks(0, out int debugLX, out int debugLY, out int debugRX,
				out int debugRY, out uint debugLT, out uint debugRT))
		{
			state.LeftX = Dominant(state.LeftX, debugLX);
			state.LeftY = Dominant(state.LeftY, debugLY);
			state.RightX = Dominant(state.RightX, debugRX);
			state.RightY = Dominant(state.RightY, debugRY);
			state.LeftTrigger = System.Math.Max(state.LeftTrigger, debugLT);
			state.RightTrigger = System.Math.Max(state.RightTrigger, debugRT);
		}

		// A replay tape overrides the devices wholesale; the state masks below still apply,
		// because they are the game's, not the recording's.
		if (GameDebugServer.ReplayFrame(out GameDebugServer.PadFrame replay))
		{
			state.Buttons |= replay.Buttons;
			state.OtherButtons |= replay.Buttons;
			state.LeftTrigger = replay.Lt;
			state.RightTrigger = replay.Rt;
			state.LeftX = replay.Lx;
			state.LeftY = replay.Ly;
			state.RightX = replay.Rx;
			state.RightY = replay.Ry;
		}

		PadState seen = state;
		uint buttons = state.Buttons;
		int leftX = state.LeftX, leftY = state.LeftY, rightX = state.RightX, rightY = state.RightY;
		GameDebugServer.RecordInputFrame(
			buttons, state.LeftTrigger, state.RightTrigger, leftX, leftY, rightX, rightY);

		// The mask as the guest receives it, for host widgets that run their own hold timers
		// (the TopBox's hold-to-accept) — every source folded, focus gate included.
		AHeld = (buttons & ButtonA) != 0;
		YHeld = (buttons & ButtonY) != 0;

		LeftStick = new Vector2(leftX / 32767f, leftY / 32767f);

		// Logged on the threshold crossing only (30/255, the XInput convention).
		uint triggersHeld =
			(state.LeftTrigger > 30 ? 1u : 0u) | (state.RightTrigger > 30 ? 2u : 0u);
		if (triggersHeld != _reportedTriggers)
		{
			_reportedTriggers = triggersHeld;
			GD.Print($"[input] triggers LT={state.LeftTrigger} RT={state.RightTrigger}");
		}

		// A MOD'S CLAIMED ACTIONS leave pad 0 — player one's, keyboard folded in — before the
		// guest reads it (ModInput.Withhold); the mod's own input_action events still fire.
		int player = Engine_TitleGame.Active.Input.ModPlayer(0);
		if (player != 0)
		{
			Refii.Mods.ModInput.Withhold(player, ref state.Buttons, ref state.LeftTrigger, ref state.RightTrigger);
		}

		// The title takes it from here: what the guest sees, and what its own host menus read
		// (`seen` is every source folded, before the mods withhold anything).
		Engine_TitleGame.Active.Input.PublishPad(0, connected: true, seen, state);
	}

	/// <summary>

	/// The left stick in the Xbox frame (x right, y UP, ±1) — the raw vector the expression
	/// wheel's cursor is computed from.
	/// </summary>
	public static Vector2 LeftStick { get; private set; }

	/// The right stick's Y, −1..1, up positive and dead-zoned, as the title's host menu last read
	/// it — what `page.lua` scrolls a book page with
	/// (`Page.onUpdate` reads `GUI.State.RightThumbStickY`, page.dis main.1 pc18..21).
	/// </summary>
	public static float RightStickY { get; set; }

	/// <summary>The right stick's X, −1..1, right positive, dead-zoned like Y — the item preview's spin.</summary>
	public static float RightStickX { get; set; }

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


	private static uint Bit(int pad, JoyButton button, uint mask) =>
		Input.IsJoyButtonPressed(pad, button) ? mask : 0u;

	/// <summary>Uses the existing Xbox button vocabulary for event capture; unmapped extras remain ignored.</summary>
	/// <param name="button">The actual mapped Godot button index.</param>
	/// <returns>One existing XInput bit or zero for unsupported buttons.</returns>
	internal static uint JoyButtonBit(JoyButton button) => button switch
	{
		JoyButton.DpadUp => DpadUp, JoyButton.DpadDown => DpadDown,
		JoyButton.DpadLeft => DpadLeft, JoyButton.DpadRight => DpadRight,
		JoyButton.Start => Start, JoyButton.Back => Back,
		JoyButton.LeftStick => LeftThumb, JoyButton.RightStick => RightThumb,
		JoyButton.LeftShoulder => LeftShoulder, JoyButton.RightShoulder => RightShoulder,
		JoyButton.A => ButtonA, JoyButton.B => ButtonB, JoyButton.X => ButtonX, JoyButton.Y => ButtonY,
		_ => 0,
	};

	/// <summary>
	/// Godot reports -1..1; the guest wants -32768..32767. Scaled by 32767 both ways so a full
	/// deflection is symmetric and cannot overflow the positive end.
	/// </summary>
	private static int Axis(int pad, JoyAxis axis) =>
		Mathf.Clamp(Mathf.RoundToInt(Input.GetJoyAxis(pad, axis) * 32767.0f), -32767, 32767);
}
