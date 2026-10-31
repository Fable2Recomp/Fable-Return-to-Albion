using Godot;

namespace Refii.Mods;

/// <summary>
/// The mods' per-frame work in a game host: what reaches a running guest once per guest (the
/// settings, the declared abilities), the guest's Lua errors, and the mods' <c>godot/*.gd</c>
/// scripts under the host's node.
/// </summary>
/// <remarks>
/// ONE LIST FOR EVERY HOST. The desktop's <c>GameSession</c> and the Android host
/// (<c>AndroidGameHost</c>, which has no GameSession because that would start a second guest)
/// both call this, so a pump added here reaches both. The mount — the mods' files, rows, images
/// and Lua — is <c>Engine_GameAssets</c>'s and the input bindings are <c>GameInput</c>'s, which
/// both hosts already share.
/// </remarks>
public static class ModFrame
{
	/// <summary>
	/// Call every frame. <paramref name="guestUp"/>: a guest is up; <paramref name="driven"/>: the
	/// host is stepping it, which is when the mods' Godot scripts run.
	/// </summary>
	public static void Pump(Node session, bool guestUp, bool driven)
	{
		// The mods' settings.json values reach the game once per guest.
		ModSettings.Pump(guestUp);
		// The mods' declared abilities reach the engine's purchase once per guest (and after a remount).
		Refii.Ui.Engine_UiModAbilities.Pump(guestUp);
		ModErrors.Pump();

		// The mods' godot/*.gd scripts run under the host's node while the guest is up and driven.
		ModGodot.Pump(session, driven && guestUp);
	}

	/// <summary>Call as the host leaves the tree: the mod scripts and their singleton go before the guest does.</summary>
	public static void Stop() => ModGodot.Stop();
}
