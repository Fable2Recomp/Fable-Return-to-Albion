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
	/// What a title hands back for one pad: the packet the guest is to see, the bits its masks
	/// took, and the physical and non-physical buttons after those masks — the Android transport
	/// publishes the physical phase beside the packet (<see cref="GameInput.Deliver"/>).
	/// </summary>
	public readonly record struct Published(GameControllerPacket Packet, uint MaskBits, uint PhysicalButtons, uint OtherButtons);

	/// <summary>
	/// Masks one pad for the guest. <paramref name="seen"/> is every source folded, before the
	/// mods withhold their claimed actions; <paramref name="guest"/> is what is left for the guest.
	/// The shell publishes what comes back, through whichever transport hosts the guest.
	/// </summary>
	public virtual Published PublishPad(int pad, bool connected, GameInput.PadState seen, GameInput.PadState guest) =>
		new(new GameControllerPacket(
			pad, connected, guest.Buttons, guest.LeftTrigger, guest.RightTrigger,
			guest.LeftX, guest.LeftY,
			// Invert X is ours: the guest has no horizontal invert of its own.
			GameInput.InvertCameraX ? -guest.RightX : guest.RightX, guest.RightY),
			0u, guest.PhysicalButtons, guest.OtherButtons);

	/// <summary>
	/// The keyboard and mouse actions, each the pad button it presses (<see cref="GameInput.KeyAction"/>):
	/// the launcher's Controls group, the in-game Controls page and the key-to-pad mapping all read it.
	/// </summary>
	public virtual GameInput.KeyAction[] KeyActions => GameInput.DefaultKeyActions;

	/// <summary>Whether the title's own "press START" screen is up (<see cref="GameInput.KeyContext.TitleScreen"/>).</summary>
	public virtual bool TitleScreenUp => false;

	/// <summary>Whether the title's on-screen choices have the face buttons (<see cref="GameInput.KeyContext.Choice"/>).</summary>
	public virtual bool ChoicesUp => false;

	/// <summary>
	/// Whether a map of the guest's has the input (<see cref="GameInput.KeyContext.Map"/>): a screen
	/// that reads its cursor from the left stick, so the mouse is captured and its motion is that
	/// stick, and the menu keys give way to the map's own.
	/// </summary>
	public virtual bool MapUp => false;

	/// <summary>
	/// The key a pad button's prompt shows now, or null for the shell's (Fable 2's) reading.
	/// <paramref name="shown"/> prints an action's first working input; <paramref name="playing"/> is
	/// whether the world has the input.
	/// </summary>
	public virtual string? KeyLabel(string button, System.Func<string, string> shown, bool playing) => null;

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

	/// <summary>The title's own account of its masks and menu edges, for the debug channel; null for none.</summary>
	public virtual object? Describe() => null;

	/// <summary>
	/// What the Android capture keys a pending tap to: the title's screens, modals and host
	/// ownership, as a changes-only identity. A title with none keys on nothing.
	/// </summary>
	internal virtual GameInputButtonRoute ButtonRoute() => new(false, 0, 0, uint.MaxValue, 0, 0, 0, 0);
}
