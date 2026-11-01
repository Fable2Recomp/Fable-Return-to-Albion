using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// A touchscreen, as one more device on pad 0: the on-screen controls press the pad's own
/// buttons, triggers and left stick, and a camera drag turns the camera through the mouse's
/// rotate path. The controls themselves are the host's (the Android app draws them); this is
/// only the seam they press through.
/// </summary>
/// <remarks>
/// THE SAME RULES AS THE KEYBOARD. Buttons are OR'd with every other device's and the stick
/// takes whichever device is pushed hardest, so a controller and the screen can both be in hand;
/// and the focus gate mutes the screen with the other devices. The buttons are published as
/// PHYSICAL buttons: on Android each press and release is then a phase the single-player mailbox
/// keeps until the guest has read it (SinglePlayerControllerMailbox), so a tap is never lost to a
/// slow frame — the same guarantee a controller's buttons have there.
///
/// THE CAMERA TURNS ONLY AS FAR AS THE FINGER MOVES, and the camera is the console's: the turn is
/// posted like the mouse's (src/vfs/src/hooks/mouse_look.cpp hands it whole to the camera's own
/// receiver, its invert and sensitivity applied), but the look device stays the pad, so the
/// camera swings behind the hero as he walks the way it does on the stick.
/// </remarks>
public partial class GameInput
{
	/// <summary>The rest of the XInput bits a touch control can press.</summary>
	public const uint PadStart = Start;
	public const uint PadBack = Back;
	public const uint PadLeftThumb = LeftThumb;
	public const uint PadRightThumb = RightThumb;

	/// <summary>
	/// The turn a drag the full height of the screen makes, in degrees at the profile's middle
	/// camera sensitivity: a thumb's comfortable swipe is about a quarter turn.
	/// </summary>
	public const float TouchYawPerScreen = 200f;

	/// <summary>The same for the vertical: the camera's pitch range is narrower, so is the swipe's.</summary>
	public const float TouchPitchPerScreen = 120f;

	/// <summary>What the touch controls hold on pad 0, in the guest's units.</summary>
	public readonly record struct TouchHold(uint Buttons, uint LeftTrigger, uint RightTrigger, int LeftX, int LeftY);

	/// <summary>The touch controls' hold, stated by the host every frame; neutral without a touchscreen.</summary>
	public static TouchHold Touch { get; set; }

	/// <summary>How long a button pressed once stays down: a few of the guest's 30 Hz reads.</summary>
	public const double PressOnceSeconds = 0.12;

	private static uint _pressOnce;

	private static long _pressOnceUntil;

	/// <summary>
	/// Presses buttons on pad 0 for a moment, as a tap would: the host's own keys (the phone's back
	/// gesture, the app's return) pressing the pad's Start or B.
	/// </summary>
	public static void PressOnce(uint buttons)
	{
		_pressOnce = buttons;
		_pressOnceUntil = System.Environment.TickCount64 + (long)(PressOnceSeconds * 1000.0);
	}

	/// <summary>The buttons pressed once and still down.</summary>
	public static uint PressedOnce => System.Environment.TickCount64 < _pressOnceUntil ? _pressOnce : 0u;

	/// <summary>The touch controls as pad 0's buttons, triggers and left stick.</summary>
	private static void TouchState(ref PadState state)
	{
		TouchHold touch = Touch;
		uint buttons = touch.Buttons | PressedOnce;
		state.Buttons |= buttons;
		state.PhysicalButtons |= buttons;
		state.LeftTrigger = System.Math.Max(state.LeftTrigger, touch.LeftTrigger);
		state.RightTrigger = System.Math.Max(state.RightTrigger, touch.RightTrigger);
		state.LeftX = Dominant(state.LeftX, touch.LeftX);
		state.LeftY = Dominant(state.LeftY, touch.LeftY);
	}

	/// <summary>
	/// A camera drag, in screen axes (right and down positive) as a fraction of the screen's
	/// height, posted as the camera's turn: right turns right and up looks up, as with the mouse.
	/// </summary>
	/// <param name="screens">The drag over the screen's height.</param>
	/// <param name="sensitivity">The player's `touch_look_sensitivity`.</param>
	/// <returns>The degrees posted, zero when nothing was.</returns>
	public static Vector2 PostTouchLook(Vector2 screens, float sensitivity)
	{
		var degrees = new Vector2(
			screens.X * TouchYawPerScreen * sensitivity * (InvertCameraX ? -1f : 1f),
			-screens.Y * TouchPitchPerScreen * sensitivity);
		if (degrees == Vector2.Zero || HostBridge.TryGetAbiVersion() is null)
		{
			return Vector2.Zero;
		}

		HostBridge.PostMouseLook(degrees.X, degrees.Y);
		return degrees;
	}

	/// <summary>
	/// Whether an event is the touchscreen's pointer: Godot's own touch-to-mouse emulation and the
	/// touch controls' menu pointer both carry the emulation device id. Such a pointer selects the
	/// touch mode, never the keyboard's, and never turns the camera.
	/// </summary>
	private static bool IsTouchPointer(InputEvent @event) =>
		@event is InputEventMouse && @event.Device == InputEvent.DeviceIdEmulation;
}
