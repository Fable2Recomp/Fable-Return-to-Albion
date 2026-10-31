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
	public static float Alpha => (float)Playhead.DrawAlpha(NowUsec);

	/// <summary>
	/// The point on the guest's game-tick timeline this frame is drawn at: the shared pair runs
	/// from its stated tick − 1 (alpha 0) to the stated tick (alpha 1), carrying past either end
	/// as the playhead does, less the measured arrival lateness
	/// (<see cref="Refii.Core.Engine_PairPlayhead.MarginTicks"/>) so a late tick is absorbed by
	/// latency rather than by extrapolating past the newest value. NaN before anything has been
	/// stated.
	/// </summary>
	/// <remarks>
	/// Everything stamped with a game tick is sampled HERE — clip times, clip weights, bone
	/// poses — so limbs and the camera share one instant and one rate. A per-series anchor set
	/// when the walk noticed a value restarts at each arrival: a value noticed early jumps the
	/// part of the segment not yet drawn, one noticed late holds. That was the limb stepping.
	/// </remarks>
	public static double DrawTick =>
		_statedTick == 0u || !Playhead.Ready ? double.NaN : _statedTick - 1.0 + Playhead.DrawAlpha(NowUsec);

	/// <summary>The tick the shared pair was last swapped on; 0 before anything has been stated.</summary>
	public static uint StatedTick => _statedTick;

	/// <summary>
	/// The instant this frame is drawn for, latched once per frame. Every draw point (camera,
	/// entity transforms, pose blends, clip times) reads it so one displayed image shows one moment
	/// of the guest's world; sampling the wall clock per subject would spread them across the
	/// frame's duration. Falls back to the live clock before the first stamp.
	/// </summary>
	public static ulong NowUsec => _now != 0UL
		? _now
		: (ulong)((System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000.0) / System.Diagnostics.Stopwatch.Frequency);

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
		Playhead.BeginFrame(nowUsec);
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

		// The latency margin moves once a frame, before any swap or draw this frame.
		Playhead.BeginFrame(nowUsec);
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

	/// <summary>A swap was withheld under a stated pause; the next swap re-anchors as one interval.</summary>
	private static bool _heldByPause;

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
		// The guest states a pause: the pair holds whatever its counter does (a paused cutscene
		// keeps counting). Released, the next tick re-anchors on the guest's current pair as one
		// swap — the ticks counted under the hold are not motion to catch up on.
		if (Refii.Core.Engine_StatedPause.Latched)
		{
			_heldByPause = _statedTick != 0u;
			return;
		}

		uint tick = Refii.Core.Engine_TickClock.GuestGameTick;
		if (tick == 0u || tick == _statedTick)
		{
			return;
		}

		int intervals = _statedTick == 0u || _heldByPause
			? 1
			: (int)System.Math.Clamp((long)tick - (long)_statedTick, 1L, 8L);
		_heldByPause = false;
		_statedTick = tick;

		// Histogram of ticks each swap claims to cover. The anchor advances by this many whole
		// periods; read beside `pairFloored` (draw point clamped at the floor) for diagnostics.
		SwapIntervals[intervals & 15] += 1;

		// At the frame's instant, as every entity pair swaps: the world and the bodies then
		// anchor on identical arrivals, and the swap time no longer depends on which feed
		// noticed first or how far into the frame it ran.
		Playhead.Swap(NowUsec, intervals);
	}

	/// <summary>The transform to draw for a stated pair, at this frame's alpha.</summary>
	public static Transform3D Blend(Transform3D previous, Transform3D stated, float alpha) =>
		new(previous.Basis.Slerp(stated.Basis, alpha), previous.Origin.Lerp(stated.Origin, alpha));

	/// <summary>The transform to draw for a stated pair, at this frame's alpha.</summary>
	public static Transform3D Blend(Transform3D previous, Transform3D stated) =>
		Blend(previous, stated, Alpha);
}
