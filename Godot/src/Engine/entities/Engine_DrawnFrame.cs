using Godot;

namespace Refii.Entities;

/// <summary>
/// The one place a drawn transform is produced: one playhead, one blend, applied once a frame.
/// </summary>
/// <remarks>
/// Every drawn subject (camera, entity placements, the between-walk resample, bone-attached
/// weapons, static twins bound to a body, the FX timeline, the pose blend) blends "previous
/// toward current by an alpha" through this type, from one shared playhead read once per frame.
/// The walk's only job is to update pairs; drawing happens here every frame, unconditionally.
/// </remarks>
public static class Engine_DrawnFrame
{
	/// <summary>
	/// The playhead for everything the guest states on its game tick, shared by the camera and
	/// every entity so they are drawn at the same instant.
	/// </summary>
	/// <remarks>
	/// Owned by Engine_TickClock (which owns the guest's tick numbering); this is the same object.
	/// Do not create a parallel instance.
	/// </remarks>
	public static Refii.Core.Engine_PairPlayhead Playhead => Refii.Core.Engine_TickClock.Pair;

	/// <summary>
	/// Where this frame falls between the two values the guest last stated, computed from the
	/// frame's one instant (<see cref="NowUsec"/>). Not cached: <see cref="NoteRestated"/> can swap
	/// the playhead part-way through a frame, and everything drawn after the swap belongs to the
	/// new pair.
	/// </summary>
	public static float Alpha => (float)Playhead.Alpha(NowUsec);

	/// <summary>
	/// The instant this frame is drawn for, latched once per frame. Every draw point (camera,
	/// entity transforms, pose blends, clip times) reads it so one displayed image shows one moment
	/// of the guest's world; sampling the wall clock per subject would spread them across the
	/// frame's duration. Falls back to the live clock before the first stamp.
	/// </summary>
	public static ulong NowUsec => _now != 0UL ? _now : Godot.Time.GetTicksUsec();

	private static ulong _now;

	/// <summary>
	/// Stamps the instant this frame is drawn for. Called from `Engine_FrameStartNode`
	/// (`ProcessPriority` = `int.MinValue`), so it runs before any feed that draws. Explicitly
	/// told, not keyed lazily on `Engine.GetProcessFrames()`, which can fail to advance between
	/// two distinct frames.
	/// </summary>
	public static void BeginFrame(ulong nowUsec)
	{
		_now = nowUsec;
	}

	/// <summary>
	/// The guest's own simulation clock in seconds, as read this frame: `game+0x110`, written every
	/// iteration at 0x82276F18. NaN before a world is up.
	///
	/// A game tick is not a fixed duration (one or two high-frequency steps, 33.333 or 66.667 ms),
	/// and `GuestGameTick` increments by 1 either way, so a pair's width must come from this clock,
	/// not the tick count.
	/// </summary>
	public static double GuestSeconds { get; private set; } = double.NaN;

	public static void BeginFrame(ulong nowUsec, double guestSeconds)
	{
		_now = nowUsec;
		GuestSeconds = guestSeconds;
	}

	/// <summary>
	/// The interval a pair spans, in host microseconds, from the guest's own clock — or 0 when
	/// it cannot be trusted (no world, a re-base, a step outside a quarter to eight ticks).
	/// </summary>
	public static double StatedIntervalUsec(double fromGuestSeconds)
	{
		if (double.IsNaN(GuestSeconds) || double.IsNaN(fromGuestSeconds))
		{
			return 0.0;
		}

		double usec = (GuestSeconds - fromGuestSeconds) * 1_000_000.0;
		double nominal = Refii.Core.Engine_TickClock.TickUsec;
		return usec > nominal * 0.25 && usec < nominal * 8.0 ? usec : 0.0;
	}

	/// <summary>Frames the drawn pass has run; reported, to prove it runs every frame.</summary>
	public static long Frames;

	/// <summary>Swaps by how many guest ticks they claimed to span; index 1 is the healthy case.</summary>
	public static readonly long[] SwapIntervals = new long[16];

	/// <summary>The guest tick the playhead was last swapped on.</summary>
	private static uint _statedTick;

	/// <summary>
	/// The guest has restated something on its tick. Dated ONCE per guest tick, however many
	/// callers notice it.
	/// </summary>
	/// <remarks>
	/// Deduped on the guest's tick, not a per-pass flag: the guest restates every renderable and
	/// the camera in one pass, but they are noticed from different `_Process` calls, and the tick
	/// number is independent of who looks first.
	///
	/// The counter can move by more than one between looks; the playhead advances by the full
	/// interval count (clamped 1..8) so its anchor does not fall permanently behind.
	/// </remarks>
	public static void NoteRestated()
	{
		uint tick = Refii.Core.Engine_TickClock.GuestGameTick;
		if (tick == 0u || tick == _statedTick)
		{
			return;
		}

		int intervals = _statedTick == 0u
			? 1
			: (int)System.Math.Clamp((long)tick - (long)_statedTick, 1L, 8L);
		_statedTick = tick;

		// Histogram of ticks each swap claims to cover. The anchor advances by this many whole
		// periods; read beside `pairFloored` (draw point clamped at the floor) for diagnostics.
		SwapIntervals[intervals & 15] += 1;
		Playhead.Swap(Godot.Time.GetTicksUsec(), intervals);
	}

	/// <summary>The transform to draw for a stated pair, at this frame's alpha.</summary>
	public static Transform3D Blend(Transform3D previous, Transform3D stated, float alpha) =>
		new(previous.Basis.Slerp(stated.Basis, alpha), previous.Origin.Lerp(stated.Origin, alpha));

	/// <summary>The transform to draw for a stated pair, at this frame's alpha.</summary>
	public static Transform3D Blend(Transform3D previous, Transform3D stated) =>
		Blend(previous, stated, Alpha);
}
