namespace Refii.Core;

/// <summary>
/// The guest's render frame, sampled once per host frame before anything reads guest state.
/// The guest evaluates poses, HUD values and its menus once per HF tick (30 Hz); a host frame
/// on which the counter has not moved reads exactly what the last one read, so the readers gate
/// on <see cref="Changed"/> and spend those frames interpolating what they already hold.
/// No counter (the guest not up, or the export missing) reads as changed every frame.
/// </summary>
public static class Engine_GuestFrame
{
	/// <summary>The counter as last sampled; 0 while the guest is not up.</summary>
	public static uint Frame { get; private set; }

	/// <summary>Whether this host frame saw a new guest frame.</summary>
	public static bool Changed { get; private set; } = true;

	/// <summary>Host frames that found no new guest frame; reported, not used.</summary>
	public static long Unchanged { get; private set; }

	/// <summary>
	/// A new guest frame, and one of every two: the cadence for what follows the guest at half
	/// its rate — the shadow skeletons of every rig but the principals. Two guest frames make a
	/// game tick, so this is one sub-step of each tick; a host frame that spans several guest
	/// frames (under 30 fps) sees whichever parity landed, which is still one in two on average.
	/// </summary>
	public static bool HalfRate { get; private set; } = true;

	private static uint _lastGameTick;

	public static void Publish(uint frame)
	{
		Changed = frame == 0u || frame != Frame;
		if (!Changed)
		{
			++Unchanged;
		}

		// Drive the 15 Hz half-rate cadence from the guest's actual 15 Hz game tick
		// (Engine_TickClock.GuestGameTick) rather than raw render-frame even/odd parity:
		// 1. `dword_83319650` can skip a single increment (`0x821D46E8`), shifting game ticks onto
		//    odd render frames; raw `(frame & 1u) == 0u` then fires 180 degrees out of phase with
		//    `_tickCompletedThisWalk` (`FarRigPosesThisStep`), causing distant rigs to pose on odd
		//    frames while `Engine_ShadowSkeletons.WriteNow` only accepts even frames.
		// 2. If two guest render frames elapse during a single host frame (e.g. odd -> odd),
		//    checking `tickAdvanced` guarantees the 15 Hz pulse is never dropped.
		uint gameTick = Engine_TickClock.GuestGameTick;
		bool tickAdvanced = gameTick != 0u && gameTick != _lastGameTick;
		_lastGameTick = gameTick;
		HalfRate = Changed && (tickAdvanced || (gameTick == 0u && (frame & 1u) == 0u));
		Frame = frame;
	}
}
