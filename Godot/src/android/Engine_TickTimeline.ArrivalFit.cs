using Godot;

namespace Refii.Core;

/// <summary>
/// The timeline's `motion_arrival_fit` rule (<see cref="Engine_ArrivalFit"/>), which the Android
/// Low and Medium presets turn on.
/// </summary>
public sealed partial class Engine_TickTimeline
{
	/// <summary>The refii.toml key of <see cref="ArrivalFit"/>.</summary>
	public const string ArrivalFitKey = "motion_arrival_fit";

	/// <summary>
	/// `motion_arrival_fit`: a body or the camera (a timeline built with stillWhenUnchanged) that
	/// moves is drawn by <see cref="Engine_ArrivalFit"/> — its statements timed by the frame that
	/// first draws them and fitted with a line drawn 100 ms behind the frame — instead of by their
	/// tick stamps at the shared draw point. Off by default (the Android Low and Medium presets turn
	/// it on); the debug channel's `render` op flips it live (`motionArrivalFit`). Main-thread
	/// owned, read once per observation and advance.
	/// </summary>
	/// <remarks>
	/// The timeline's own bookkeeping (<see cref="Newest"/>, <see cref="NewestTick"/>, the stated
	/// motion <see cref="Settled"/> reads) runs either way, and the fit draws from and into the
	/// same drawn transform, so the switch is seamless in both directions. A live effect
	/// (stillWhenUnchanged false) keeps this rule: its statements are written on some ticks only.
	/// Measured and chosen in <see cref="Engine_ArrivalFit"/>.
	/// </remarks>
	public static bool ArrivalFit;

	/// <summary>Reads <see cref="ArrivalFitKey"/> at launch; the debug channel or a live `cvar-set` may change it afterwards.</summary>
	public static void Configure(System.Collections.Generic.IReadOnlyDictionary<string, string> config) =>
		ArrivalFit = Engine_RefiiConfig.Bool(config, ArrivalFitKey, false);

	/// <summary>The arrival fit drawing this timeline while it moves under <see cref="ArrivalFit"/>; null otherwise.</summary>
	private Engine_ArrivalFit? _arrivals;

	/// <summary>Whether <see cref="ArrivalFit"/> is drawing this timeline now; for tests and the debug channel.</summary>
	public bool FittingArrivals => _arrivals is not null;

	/// <summary>
	/// A statement for the arrival fit, before the timeline's own bookkeeping takes it as the newest:
	/// a thing that starts to move rents a fit seeded with the stand it moves from and the transform
	/// last drawn; one unchanged from the newest is a stand, which only a moving fit hears.
	/// </summary>
	private void ObserveArrival(Transform3D sample, float scalar)
	{
		bool still = sample.IsEqualApprox(Newest) && Mathf.IsEqualApprox(scalar, NewestScalar);
		if (_arrivals is null)
		{
			if (still)
			{
				return;
			}

			_arrivals = Engine_ArrivalFit.Rent(Newest.Origin, Newest.Basis.GetRotationQuaternion(), NewestScalar,
				_position, _rotation, _drawnScalar);
		}

		_arrivals.Observe(sample.Origin, sample.Basis.GetRotationQuaternion(), scalar, still);
	}

	/// <summary>
	/// The frame the arrival fit draws, while one is drawing this timeline. Switched off live, the
	/// fit is handed back and the timeline's own rule carries on from the drawn transform.
	/// </summary>
	/// <returns>True when <paramref name="drawn"/> is this frame's transform.</returns>
	private bool TryAdvanceArrival(ulong nowUsec, out Transform3D drawn)
	{
		drawn = default;
		if (_arrivals is null)
		{
			return false;
		}

		if (ArrivalFit)
		{
			drawn = AdvanceArrival(nowUsec);
			return true;
		}

		ReleaseArrivals();  // switched off live: this rule carries on from the drawn transform
		return false;
	}

	/// <summary>
	/// The frame drawn by the arrival fit. Once it rests on a stand it is handed back and the drawn
	/// transform put exactly on the newest statement, so <see cref="Settled"/> holds and the caller
	/// stops advancing the thing.
	/// </summary>
	private Transform3D AdvanceArrival(ulong nowUsec)
	{
		_lastUsec = nowUsec;
		_arrivals!.Advance(nowUsec, out _position, out _rotation, out _drawnScalar);
		if (_arrivals.Resting)
		{
			_position = Newest.Origin;
			_rotation = Newest.Basis.GetRotationQuaternion();
			_drawnScalar = NewestScalar;
			ReleaseArrivals();
		}

		return new Transform3D(new Basis(_rotation) * Basis.FromScale(Newest.Basis.Scale), _position);
	}

	private void ReleaseArrivals()
	{
		if (_arrivals is not null)
		{
			Engine_ArrivalFit.Return(_arrivals);
			_arrivals = null;
		}
	}
}
