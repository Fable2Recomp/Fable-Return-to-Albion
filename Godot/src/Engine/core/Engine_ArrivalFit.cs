using Godot;

namespace Refii.Core;

/// <summary>
/// One moving body's (or the camera's) drawn transform under `motion_arrival_fit`
/// (<see cref="Engine_TickTimeline.ArrivalFit"/>): the guest's statements timed by when the host
/// first drew them, not by the game tick they were stamped with, a straight line fitted through the
/// last <see cref="RingSize"/> of them, and the drawn transform following that line
/// <see cref="DelayUsec"/> behind the frame.
/// </summary>
/// <remarks>
/// WHY NOT THE STAMP. A statement is stamped with `GuestGameTick` read at frame start, but the guest
/// writes placements on its 30 Hz half-steps and moves `live` only partway into the next tick, the
/// counter is bumped at the top of a tick, and the host reads all of it at an arbitrary moment of
/// a guest that runs behind its grants. Measured on a Galaxy S10e over twelve walks, a walk
/// states a steady ~34 cm a tick, yet 30-45% of statements put 0.5x or 2x that over their stamped tick delta: the
/// stamps carry ±0.5-tick errors and races, and a timeline that divides by them draws ±50% speed —
/// the hero's frame steps ran 25,25,25,25,6,6,12 cm, a per-frame step sd of 35-70% of its local
/// median in every mode. The arrival time has errors too (a 50 ms frame of quantisation, the guest's own
/// jitter), but they are bounded, unbiased and averaged by the fit; a stamp's are a whole tick.
///
/// WHY A LINE AND A FOLLOWER. Drawn straight off the fitted line, each new statement moves the
/// whole line and the drawn point jumps with it (replayed on the twelve traces: step sd 28%,
/// 9 stalls and lurches a walk). The drawn transform therefore moves each frame by the line's own
/// velocity and is pulled onto the line by <see cref="FollowGain"/>: a statement that bends the
/// line bends the drawn path over ~125 ms instead of in one frame. Replayed (MotionReplayTests):
/// step sd 14% of its local median against the 49% the phone drew, mean frame-to-frame change 15%
/// against 33%, 1.2 stalls and lurches a walk against 23, and 7.5 cm off the stated path at p95
/// against 17. A critically damped spring in place of the follower scored as evenly (15%) but
/// overshot turns and never settled exactly. The cost is latency: the drawn point is 98 ms behind
/// the stated path (p95 134) where the timeline's was 65 (p95 178), and on a synthetic jog a
/// reversal is drawn 200 ms after the guest's (the timeline, on an ideal draw point: 96) and a
/// 90° turn rounded by 16 cm (4).
///
/// CONFIRMED ON THE PHONE (S10e, build 61f, out-and-back walks with the key toggled live in
/// pairs): step sd 17/12/13/28% and 0-3 stalls and lurches per ~115 frames with it, against
/// 34/59/44/45% and 10-32 without; the hero's step against the camera's, ~20% against ~49%.
///
/// NOT ON THE TICK CLOCK. The draw point is the frame instant less <see cref="DelayUsec"/>, not
/// the shared playhead's DrawTick: on the phone DrawTick advanced 7-60% unevenly frame to frame,
/// with holds, which is what the timeline's own correction carried onto the screen. The legs
/// (Engine_ClipPlayhead) stay on DrawTick, smoothed by their own bounded steering. Replayed, the
/// instant the body is drawn at leads the legs' by -22 ms on average (the timeline: +10), a fixed
/// phase of ~7 cm between root and stride that slides nothing; it wanders by 60 ms sd (54 under
/// the timeline: the legs' own wander) and changes less from frame to frame (14 ms against 20),
/// so the feet slide no more than they did.
///
/// No allocation per statement or frame: the ring is inline. One is rented per body only while
/// it moves (<see cref="Rent"/>) and handed back when it rests, so the ~3000 bodies that stand
/// still carry none.
/// </remarks>
public sealed class Engine_ArrivalFit
{
	/// <summary>
	/// Statements the line is fitted through. At the phone's 2-3 frames between statements five
	/// span ~500 ms; at the desktop's 15 Hz, ~270 ms. Replayed over the twelve phone walks, four
	/// and six both scored a step sd of 16% against five's 14%.
	/// </summary>
	public const int RingSize = 5;

	/// <summary>
	/// How far behind the frame the line is drawn, in µs. Statements reached the phone 1-4 frames
	/// apart, mostly 2 (100 ms). At 100 ms the draw point was past the newest arrival on 11% of the
	/// walking frames and past the coast on 3%; 120 ms (5% and 0.6%) scored a step sd of 13.5%
	/// against 14.0% for 20 ms more latency.
	/// </summary>
	public const ulong DelayUsec = 100_000UL;

	/// <summary>
	/// How far past the newest statement's arrival the line is run on at full speed, in µs: through
	/// a 3-frame gap at the delay above. A body that really stopped overruns by this and the fade
	/// (<see cref="FadeUsec"/>) of its last speed and is walked back when the stop is stated.
	/// </summary>
	public const ulong CoastUsec = 50_000UL;

	/// <summary>
	/// Past the coast the drawn motion slows to a stop over this long (µs) rather than at once.
	/// Held hard at the coast, a late statement (a 4-frame gap) was a stop and a lurch: replayed,
	/// stalls and lurches 3.8 a walk against 1.2 with the fade. Longer fades scored the same and
	/// overran a real stop further: on the synthetic jog (3 m/s) 21 cm at 50 ms, 29 at 100, 15 with
	/// none; the timeline overran the same stop by 27.
	/// </summary>
	public const ulong FadeUsec = 50_000UL;

	/// <summary>
	/// How fast the drawn transform is pulled onto the line: the fraction left after one second is
	/// e^-FollowGain: a third of the gap closed per frame at 20 fps. Replayed: 6 was 0.7 points smoother but rounded a 90°
	/// turn 2.4 cm wider and settled a stop 120 ms later; 10 let each statement through as a step
	/// (sd 15%).
	/// </summary>
	public const double FollowGain = 8.0;

	/// <summary>
	/// A move stated after a stand: the stand is taken to have lasted until this long before the
	/// move arrived (µs) — a phone's interval between statements — so the first line through the
	/// two is the walk's speed, not the move spread over however long the thing stood.
	/// </summary>
	public const ulong StartUsec = 100_000UL;

	/// <summary>
	/// An unchanged statement is a stop only this long (µs) after the newest move arrived. The
	/// bodies state an unchanged placement only after four ticks (267 ms at full pace); the camera
	/// every complete step, including the unchanged camera a read racing the guest's tick finds
	/// mid-walk — taken as a stop, that cleared the line on every race.
	/// </summary>
	public const ulong StillUsec = 200_000UL;

	/// <summary>A stand drawn this close to its statement (m, rad, scalar units) is put exactly on it.</summary>
	public const float RestMetres = 1e-3f;

	public const float RestRadians = 1e-3f;

	public const float RestScalar = 1e-3f;

	/// <summary>Bodies the pool keeps for reuse; past it a returned fit is left to the collector.</summary>
	public const int PoolLimit = 512;

	/// <summary>
	/// Fits rented and not returned; for the debug channel. A body dropped while it moves (a
	/// despawn) leaves its fit to the collector and is never counted back, so this drifts upward
	/// over a session by the fits lost to despawns.
	/// </summary>
	public static int Live { get; private set; }

	/// <summary>Stops taken (an unchanged statement after <see cref="StillUsec"/>); run total.</summary>
	public static long Stops;

	private static readonly System.Collections.Generic.Stack<Engine_ArrivalFit> Pool = new();

	/// <summary>One statement: when it was first drawn (0 until then), and what it stated.</summary>
	private struct Arrival
	{
		public ulong Usec;
		public Vector3 Origin;
		public Quaternion Rotation;
		public float Scalar;
	}

	[System.Runtime.CompilerServices.InlineArray(RingSize)]
	private struct ArrivalRing
	{
		private Arrival _element;
	}

	private ArrivalRing _ring;
	private int _head;
	private int _count;

	/// <summary>The oldest statement is a stand the next move started from (<see cref="StartUsec"/>).</summary>
	private bool _fromRest;

	/// <summary>An unchanged statement waits for its frame instant to say whether it is a stop.</summary>
	private bool _stillPending;

	private Vector3 _position;
	private Quaternion _rotation = Quaternion.Identity;
	private float _scalar;
	private ulong _lastUsec;

	/// <summary>
	/// Whether the drawn transform has come to rest exactly on a stand: one statement left in the
	/// ring and the follower closed onto it. The timeline hands the fit back on it.
	/// </summary>
	public bool Resting { get; private set; }

	/// <summary>
	/// A fit for a thing that starts to move: seeded with the stand it moves from (the timeline's
	/// newest statement) and drawn on from where the timeline last drew it.
	/// </summary>
	public static Engine_ArrivalFit Rent(Vector3 origin, Quaternion rotation, float scalar,
		Vector3 drawnOrigin, Quaternion drawnRotation, float drawnScalar)
	{
		Engine_ArrivalFit fit = Pool.Count > 0 ? Pool.Pop() : new Engine_ArrivalFit();
		++Live;
		fit._count = 0;
		fit._head = 0;
		fit.Push(0UL, origin, rotation, scalar);
		fit._fromRest = true;
		fit._stillPending = false;
		fit.Resting = false;
		fit._position = drawnOrigin;
		fit._rotation = drawnRotation.Normalized();
		fit._scalar = drawnScalar;
		fit._lastUsec = 0UL;
		return fit;
	}

	/// <summary>Hands a fit back for the next body that moves.</summary>
	public static void Return(Engine_ArrivalFit fit)
	{
		--Live;
		if (Pool.Count < PoolLimit)
		{
			Pool.Push(fit);
		}
	}

	private ref Arrival Back(int back) => ref _ring[(_head - back + RingSize) % RingSize];

	private void Push(ulong usec, Vector3 origin, Quaternion rotation, float scalar)
	{
		_head = (_head + 1) % RingSize;
		_ring[_head] = new Arrival { Usec = usec, Origin = origin, Rotation = rotation.Normalized(), Scalar = scalar };
		_count = System.Math.Min(_count + 1, RingSize);
	}

	/// <summary>
	/// A statement, timed by the next frame that draws it. <paramref name="still"/>: unchanged from
	/// the newest, which is a stop only once the move before it is <see cref="StillUsec"/> old.
	/// Two statements in one frame: the later replaces the earlier.
	/// </summary>
	public void Observe(Vector3 origin, Quaternion rotation, float scalar, bool still)
	{
		Resting = false;
		if (still)
		{
			_stillPending = true;
			return;
		}

		_stillPending = false;
		if (_count > 0 && Back(0).Usec == 0UL && !(_fromRest && _count == 1))
		{
			Back(0) = new Arrival { Origin = origin, Rotation = rotation.Normalized(), Scalar = scalar };
			return;
		}

		Push(0UL, origin, rotation, scalar);
	}

	/// <summary>
	/// Restates the ring and the drawn transform as turned by <paramref name="turn"/> about
	/// <paramref name="pivot"/> (<see cref="Engine_TickTimeline.TurnAbout(Vector3, Basis)"/>).
	/// </summary>
	public void TurnAbout(Vector3 pivot, Basis turn, Quaternion rotation)
	{
		for (int back = 0; back < _count; back++)
		{
			ref Arrival arrival = ref Back(back);
			arrival.Origin = pivot + (turn * (arrival.Origin - pivot));
			arrival.Rotation = (rotation * arrival.Rotation).Normalized();
		}

		_position = pivot + (turn * (_position - pivot));
		_rotation = (rotation * _rotation).Normalized();
	}

	/// <summary>The drawn transform for the frame at <paramref name="nowUsec"/>.</summary>
	public void Advance(ulong nowUsec, out Vector3 position, out Quaternion rotation, out float scalar)
	{
		// Statements are timed by the first frame that draws them.
		for (int back = 0; back < _count; back++)
		{
			if (Back(back).Usec == 0UL)
			{
				Back(back).Usec = nowUsec;
			}
		}

		if (_fromRest && _count >= 2)
		{
			// The stand lasted until just before the move: the line through the two is the walk.
			ref Arrival rest = ref Back(_count - 1);
			ulong moved = Back(_count - 2).Usec;
			ulong from = moved > StartUsec ? moved - StartUsec : 0UL;
			if (rest.Usec < from || rest.Usec >= moved)
			{
				rest.Usec = from;
			}

			_fromRest = false;
		}

		if (_stillPending)
		{
			_stillPending = false;
			if (_count > 1 && nowUsec >= Back(0).Usec + StillUsec)
			{
				// A stop: the line is the stand alone, and the drawn transform settles onto it.
				Arrival stand = Back(0);
				stand.Usec = nowUsec;
				_count = 0;
				Push(stand.Usec, stand.Origin, stand.Rotation, stand.Scalar);
				_fromRest = true;
				++Stops;
			}
		}

		double seconds = _lastUsec == 0UL || nowUsec <= _lastUsec
			? 0.0
			: System.Math.Min((nowUsec - _lastUsec) * 1e-6, 0.25);
		_lastUsec = nowUsec;

		Line(nowUsec, out Vector3 target, out Quaternion turn, out float scalarTarget,
			out Vector3 velocity, out Vector3 spin, out float scalarRate);

		// Moved by the line's own motion, then pulled onto it: a new statement bends the drawn
		// path over a few frames instead of moving it in one.
		double pull = 1.0 - System.Math.Exp(-FollowGain * seconds);
		_position += velocity * (float)seconds;
		_position += (target - _position) * (float)pull;
		if (_position.DistanceTo(target) > Engine_TickTimeline.SnapMetres)
		{
			++Engine_TickTimeline.Snaps;
			_position = target;
		}

		float spinAngle = spin.Length() * (float)seconds;
		if (spinAngle > 1e-7f)
		{
			_rotation = (_rotation * new Quaternion(spin.Normalized(), spinAngle)).Normalized();
		}

		if (_rotation.AngleTo(turn) > Engine_TickTimeline.SnapRadians)
		{
			++Engine_TickTimeline.Snaps;
			_rotation = turn;
		}
		else
		{
			_rotation = _rotation.Slerp(turn, (float)pull).Normalized();
		}

		_scalar += scalarRate * (float)seconds;
		_scalar += (scalarTarget - _scalar) * (float)pull;

		// A stand: put exactly on it once within a hair, which the follower alone never reaches.
		Resting = false;
		if (_count == 1 && _position.DistanceTo(target) < RestMetres && _rotation.AngleTo(turn) < RestRadians &&
			Mathf.Abs(_scalar - scalarTarget) < RestScalar)
		{
			_position = target;
			_rotation = turn;
			_scalar = scalarTarget;
			Resting = true;
		}

		position = _position;
		rotation = _rotation;
		scalar = _scalar;
	}

	/// <summary>
	/// The least-squares line through the ring against arrival time, evaluated at the draw point
	/// (the frame less <see cref="DelayUsec"/>, held <see cref="CoastUsec"/> past the newest
	/// arrival), and its slope — none once held. Offsets are taken from the newest statement, so a
	/// ring of one is that statement exactly. The rotation is fitted as rotation vectors in the
	/// newest's frame.
	/// </summary>
	private void Line(ulong nowUsec, out Vector3 target, out Quaternion turn, out float scalarTarget,
		out Vector3 velocity, out Vector3 spin, out float scalarRate)
	{
		ref Arrival newest = ref Back(0);
		Quaternion inverse = newest.Rotation.Inverse();
		double meanT = 0.0;
		Vector3 meanP = Vector3.Zero, meanR = Vector3.Zero;
		double meanS = 0.0;
		for (int back = 0; back < _count; back++)
		{
			ref Arrival a = ref Back(back);
			meanT += ((double)a.Usec - newest.Usec) * 1e-6;
			meanP += a.Origin - newest.Origin;
			meanR += Log(inverse * a.Rotation);
			meanS += a.Scalar - newest.Scalar;
		}

		meanT /= _count;
		meanP /= _count;
		meanR /= _count;
		meanS /= _count;

		double variance = 0.0;
		Vector3 slopeP = Vector3.Zero, slopeR = Vector3.Zero;
		double slopeS = 0.0;
		for (int back = 0; back < _count; back++)
		{
			ref Arrival a = ref Back(back);
			double dt = (((double)a.Usec - newest.Usec) * 1e-6) - meanT;
			variance += dt * dt;
			slopeP += (a.Origin - newest.Origin - meanP) * (float)dt;
			slopeR += (Log(inverse * a.Rotation) - meanR) * (float)dt;
			slopeS += (a.Scalar - newest.Scalar - meanS) * dt;
		}

		if (variance > 1e-9)
		{
			slopeP /= (float)variance;
			slopeR /= (float)variance;
			slopeS /= variance;
		}
		else
		{
			slopeP = Vector3.Zero;
			slopeR = Vector3.Zero;
			slopeS = 0.0;
		}

		(double run, double motion) = Coast(((double)nowUsec - DelayUsec - newest.Usec) * 1e-6);
		double at = run - meanT;
		target = newest.Origin + meanP + (slopeP * (float)at);
		turn = (newest.Rotation * Exp(meanR + (slopeR * (float)at))).Normalized();
		scalarTarget = newest.Scalar + (float)(meanS + (slopeS * at));
		velocity = slopeP * (float)motion;
		spin = slopeR * (float)motion;
		scalarRate = (float)(slopeS * motion);
	}

	/// <summary>
	/// Where the line is drawn <paramref name="past"/> seconds after the newest arrival, and the
	/// fraction of its motion drawn there: all of it to <see cref="CoastUsec"/>, slowing linearly
	/// to nothing over <see cref="FadeUsec"/>, then held — position and speed both continuous.
	/// </summary>
	internal static (double Run, double Motion) Coast(double past)
	{
		double coast = CoastUsec * 1e-6, fade = FadeUsec * 1e-6;
		if (past <= coast)
		{
			return (past, 1.0);
		}

		double over = past - coast;
		if (over >= fade)
		{
			return (coast + (0.5 * fade), 0.0);
		}

		return (coast + over - (over * over / (2.0 * fade)), 1.0 - (over / fade));
	}

	/// <summary>A unit quaternion as a rotation vector (axis × angle), the short way round.</summary>
	internal static Vector3 Log(Quaternion q)
	{
		if (q.W < 0f)
		{
			q = -q;
		}

		var v = new Vector3(q.X, q.Y, q.Z);
		float s = v.Length();
		if (s < 1e-8f)
		{
			return v * 2f;
		}

		return v * (2f * Mathf.Atan2(s, q.W) / s);
	}

	/// <summary>A rotation vector as a unit quaternion.</summary>
	internal static Quaternion Exp(Vector3 r)
	{
		float angle = r.Length();
		if (angle < 1e-8f)
		{
			return new Quaternion(r.X * 0.5f, r.Y * 0.5f, r.Z * 0.5f, 1f).Normalized();
		}

		return new Quaternion(r / angle, angle);
	}
}
