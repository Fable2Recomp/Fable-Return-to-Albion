using System;

namespace Refii.Entities;

/// <summary>
/// `far_rig_pose_hz`: how often a rig beyond the full-rate radius writes its bones, in real time,
/// when that is to be fewer than the guest's own half rate gives it.
/// </summary>
/// <remarks>
/// <para>
/// THE GUEST'S DIVIDER DOES NOT DIVIDE BELOW 30 FPS. A far rig poses on the walk whose guest frame
/// completes a game tick (GameEntities' <c>_tickCompletedThisWalk</c>); at 30 fps and above that is every
/// other walk, 15 Hz. Under it every walk spans a tick, so every far rig poses on every walk and
/// the slower the phone, the less the LOD saves: nothing at all on a Galaxy S10e at 14 fps.
/// </para>
/// <para>
/// A rate in real time holds whatever the frame rate. Each far rig has its own grid of slots,
/// <c>1/hz</c> apart, and writes on the first tick step at or after each slot. Still only on
/// tick steps: the shadow skeleton mirrors a write only on those
/// (Engine_ShadowSkeletons.WriteNow), and a far rig written between them would cast its shadow
/// from an older pose. Spread, each rig's grid is offset by a phase from its address, so the
/// writes are shared evenly across the walks instead of falling on one in every few; together,
/// every far rig shares one grid, which keeps a lamp's shadow pool of far rigs re-rendering at
/// the rate rather than on every walk that writes any of them.
/// </para>
/// <para>
/// A rig that has missed two of its slots (it was out of view) writes on the next tick step
/// rather than waiting for its slot, so it never comes back into view holding an old pose for a
/// whole period.
/// </para>
/// </remarks>
public static class Engine_FarRigPoseRate
{
	/// <summary>The slowest rate taken: the clip animator's ramps clamp a step to a quarter second.</summary>
	public const float MinHz = 4f;

	/// <summary>The fastest: the guest's own frames.</summary>
	public const float MaxHz = 30f;

	/// <summary>A configured rate, clamped to what is honoured; 0 (off) for zero, negative or not a number.</summary>
	public static float Clamp(float hz) =>
		!float.IsFinite(hz) || hz <= 0f ? 0f : Math.Clamp(hz, MinHz, MaxHz);

	/// <summary>
	/// The rig's offset in its grid, in slots: an even spread over [0, 1) from the address. Not
	/// the reconcile cadence's hash, so a rig's slots and its other cadences do not lock together.
	/// </summary>
	public static double Phase(uint address)
	{
		uint h = address;
		h ^= h >> 16;
		h *= 0x85EBCA6Bu;
		h ^= h >> 13;
		h *= 0xC2B2AE35u;
		h ^= h >> 16;
		return h / 4294967296.0;
	}

	/// <summary>Whether a slot of a grid offset by <paramref name="phase"/> lies in (<paramref name="from"/>, <paramref name="to"/>].</summary>
	public static bool Crossed(double from, double to, float hz, double phase) =>
		Math.Floor((to * hz) + phase) != Math.Floor((from * hz) + phase);

	/// <summary>
	/// Whether a far rig writes its bones on this walk.
	/// </summary>
	/// <param name="tickStep">This walk's guest frame completed a game tick.</param>
	/// <param name="from">Seconds at the previous tick step's walk.</param>
	/// <param name="to">Seconds at this walk.</param>
	/// <param name="hz">The clamped rate.</param>
	/// <param name="spread">Each rig on its own phase, rather than all on one.</param>
	/// <param name="address">The rig's entity.</param>
	/// <param name="sinceWriteMs">Milliseconds since the rig last wrote its bones.</param>
	public static bool Due(bool tickStep, double from, double to, float hz, bool spread, uint address, ulong sinceWriteMs)
	{
		if (!tickStep)
		{
			return false;
		}

		if (sinceWriteMs * (double)hz >= 2000.0)
		{
			return true;
		}

		return Crossed(from, to, hz, spread ? Phase(address) : 0.0);
	}
}
