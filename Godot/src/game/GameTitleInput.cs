using Refii.Host;

namespace Refii.Game;

/// <summary>
/// A title's part in the pads: what the guest sees of each, which player a pad is for the mods,
/// and when the world has the input. The shell's <see cref="GameInput"/> gathers the devices, the
/// debug channel and the replay into one state per pad and hands it here; this default publishes
/// it as it is, which is a title with no host-owned menus of its own.
/// </summary>
public class GameTitleInput
{
	/// <summary>The input for a title that adds nothing.</summary>
	public static GameTitleInput Raw { get; } = new();

	/// <summary>
	/// Publishes one pad to the guest. <paramref name="seen"/> is every source folded, before the
	/// mods withhold their claimed actions; <paramref name="guest"/> is what is left for the guest.
	/// </summary>
	public virtual void PublishPad(int pad, bool connected, GameInput.PadState seen, GameInput.PadState guest) =>
		HostBridge.SetControllerState(
			pad, connected, guest.Buttons, guest.LeftTrigger, guest.RightTrigger,
			guest.LeftX, guest.LeftY,
			// Invert X is ours: the guest has no horizontal invert of its own.
			GameInput.InvertCameraX ? -guest.RightX : guest.RightX, guest.RightY);

	/// <summary>Which player a pad carries for the mods' input (1 or 2), or 0 for none.</summary>
	public virtual int ModPlayer(int pad) => pad == 0 ? 1 : 0;

	/// <summary>Whether the player is in the world with nothing of the title's holding the input.</summary>
	public virtual bool WorldHasInput => HostBridge.IsInGame;

	/// <summary>
	/// The guest has just been handed an empty pad (photo mode took the input): every button
	/// still down stays hidden from it until released, so the hand-back is not a fresh press.
	/// </summary>
	public virtual void HoldUntilReleased()
	{
	}

	/// <summary>A title overlay is held under the pointer: the cursor is free and the camera still.</summary>
	public virtual bool PointerOverlay => false;
}
