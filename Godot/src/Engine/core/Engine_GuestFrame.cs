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

	public static void Publish(uint frame)
	{
		Changed = frame == 0u || frame != Frame;
		if (!Changed)
		{
			++Unchanged;
		}

		Frame = frame;
	}
}
