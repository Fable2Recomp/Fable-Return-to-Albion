using System.Collections.Generic;
using Godot;

namespace Refii.Entities;

/// <summary>
/// The cast-only shadow proxy of anything the walk moves every displayed frame, kept at the
/// GUEST's rate: its own placement, and for a character its own skeleton, both written on guest
/// frames — or every frame for a principal.
/// </summary>
/// <remarks>
/// Godot marks every light an instance is paired with shadow-dirty on ANY update of that instance
/// (`renderer_scene_cull.cpp`, `_update_instance`): a bone write through its skeleton, or a move of
/// its transform. The visible body takes both every displayed frame (the clip animator and the feed
/// interpolate on Godot's clock, and the walk places the node each frame), so the proxy shares
/// neither: it stands BESIDE the body (same parent, the feed node) with its own skeleton under it,
/// and both are written here on guest frames.
///
/// The guest itself posed and placed at 30 Hz, so this is the original's cadence; what it costs is
/// the shadow trailing the visible body by at most one displayed frame. The principals never trail:
/// the hero and the co-op partner keep full fidelity, shadow included, so their proxies are written
/// and placed every frame. The walk states which rigs those are through <see cref="SetEveryFrame"/>.
///
/// The bone writers mirror their own writes (`Engine_EntitySkeleton`, `Engine_ClipAnimator`) — a
/// bulk copy afterwards would be two engine calls per bone per rig per guest frame, millions a
/// second. <see cref="WriteNow"/> is the one question they ask. Placement is mirrored from the
/// walk's one transform write (<see cref="Placed"/>), and a move that landed between guest frames
/// is caught up by <see cref="EndWalk"/> on the next one.
///
/// `REFII_SHADOW_GUEST_RATE=0` writes and places every proxy every frame, for an A/B.
/// </remarks>
public static class Engine_ShadowSkeletons
{
	public const string NodeName = "ShadowBones";

	private sealed class Entry
	{
		public required Skeleton3D? Shadow;
		public required MeshInstance3D Body;
		public required MeshInstance3D Proxy;
		public bool EveryFrame;
		public bool PlacePending;
		public bool? LastVisible;
	}

	private static readonly Dictionary<Skeleton3D, Entry> ByLive = new();
	private static readonly Dictionary<MeshInstance3D, Entry> ByBody = new();

	private static readonly bool GuestRate = System.Environment.GetEnvironmentVariable("REFII_SHADOW_GUEST_RATE") != "0";

	// HALF THE GUEST'S RATE, NOT THE GUEST'S RATE. A lantern map re-renders on any caster write
	// in its pool, and at a crowded square every guest frame writes some villager near every
	// lamp, so mirroring the shadows on every guest frame re-rendered all sixteen paraboloid
	// passes at 30 Hz, a cost per pass, not per draw. Every non-principal shadow skeleton and
	// proxy placement follows on
	// Engine_GuestFrame.HalfRate, one guest frame in two: the shadows of villagers step at 15 Hz
	// and lag their bodies by at most one guest frame; the bodies themselves keep their rate.
	// Principals stay at display rate.

	/// <summary>
	/// Debug lever (`refii_render shadowPrincipal`): false puts the principals on the guest's rate
	/// too, to measure what their every-frame shadow costs. The shipped value is true — the hero
	/// and the partner keep full fidelity.
	/// </summary>
	public static bool PrincipalEveryFrame = true;

	/// <summary>Shadow skeletons standing.</summary>
	public static int Active => ByLive.Count;

	/// <summary>Proxies placed here, skinned and rigid alike.</summary>
	public static int Following => ByBody.Count;

	/// <summary>Frames in which a writer mirrored a rig on a guest frame / every frame (principals).</summary>
	public static long GuestFrameWrites;
	public static long EveryFrameWrites;

	/// <summary>Proxy placements on guest frames / every frame, and moves caught up at a guest frame.</summary>
	public static long GuestFramePlacements;
	public static long EveryFramePlacements;
	public static long CaughtUpPlacements;

	/// <summary>The walk asked for full rate on a rig that has no shadow skeleton: the principal is not proxied.</summary>
	public static long EveryFrameMisses;

	/// <summary>
	/// The shadow skeleton for <paramref name="body"/>'s <paramref name="proxy"/>, built once as a
	/// bone-for-bone copy of <paramref name="live"/> (names, parents, rests, current poses; no
	/// children, so a bone attachment's mesh is not duplicated) and parented under the proxy, so it
	/// moves with the proxy's own placement rather than the body's.
	/// </summary>
	public static Skeleton3D Ensure(MeshInstance3D body, MeshInstance3D proxy, Skeleton3D live)
	{
		if (ByLive.TryGetValue(live, out Entry? have) && have.Shadow is not null &&
			GodotObject.IsInstanceValid(have.Shadow) && ReferenceEquals(have.Proxy, proxy))
		{
			have.PlacePending = true;
			Place(have);
			return have.Shadow;
		}

		bool wasEveryFrame = ByBody.TryGetValue(body, out Entry? prev) && prev.EveryFrame;
		Forget(body);

		// Manual modifier processing, for the reason Engine_EntitySkeleton.Build gives.
		var shadow = new Skeleton3D
		{
			Name = NodeName,
			ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Manual,
		};
		int bones = live.GetBoneCount();
		for (int b = 0; b < bones; b++)
		{
			shadow.AddBone(live.GetBoneName(b));
		}

		for (int b = 0; b < bones; b++)
		{
			int parent = live.GetBoneParent(b);
			if (parent >= 0)
			{
				shadow.SetBoneParent(b, parent);
			}

			shadow.SetBoneRest(b, live.GetBoneRest(b));
			shadow.SetBonePose(b, live.GetBonePose(b));
		}

		shadow.MotionScale = live.MotionScale;
		proxy.AddChild(shadow);
		var entry = new Entry { Shadow = shadow, Body = body, Proxy = proxy, EveryFrame = wasEveryFrame, PlacePending = true };
		ByLive[live] = entry;
		ByBody[body] = entry;
		return shadow;
	}

	/// <summary>
	/// Registers a skeleton-less follower — an attached child's proxy — for guest-rate placement
	/// and visibility mirroring. Every frame when its holder is a principal.
	/// </summary>
	public static void Follow(MeshInstance3D body, MeshInstance3D proxy, bool everyFrame)
	{
		if (ByBody.TryGetValue(body, out Entry? have) && have.Shadow is null &&
			ReferenceEquals(have.Proxy, proxy))
		{
			have.EveryFrame = everyFrame;
			return;
		}

		Forget(body);
		ByBody[body] = new Entry
		{
			Shadow = null, Body = body, Proxy = proxy, EveryFrame = everyFrame, PlacePending = true,
		};
	}

	/// <summary>A follower's holder became, or stopped being, a principal.</summary>
	public static void SetFollowerEveryFrame(MeshInstance3D body, bool everyFrame)
	{
		if (ByBody.TryGetValue(body, out Entry? entry))
		{
			entry.EveryFrame = everyFrame;
		}
	}

	/// <summary>The rig's rate: a principal's shadow is written and placed every frame, anyone else's on guest frames.</summary>
	public static void SetEveryFrame(Skeleton3D live, bool everyFrame)
	{
		if (ByLive.TryGetValue(live, out Entry? entry))
		{
			entry.EveryFrame = everyFrame;
		}
		else if (everyFrame)
		{
			++EveryFrameMisses;
		}
	}

	private static bool DueNow(Entry entry) =>
		(entry.EveryFrame && PrincipalEveryFrame) || !GuestRate || Refii.Core.Engine_GuestFrame.HalfRate;

	/// <summary>
	/// Whether a writer of <paramref name="live"/> should mirror this frame's bones into the
	/// shadow skeleton, and which one. False when the rig has no proxy.
	/// </summary>
	public static bool WriteNow(Skeleton3D live, out Skeleton3D? shadow)
	{
		if (!ByLive.TryGetValue(live, out Entry? entry) || entry.Shadow is null ||
			!GodotObject.IsInstanceValid(entry.Shadow))
		{
			shadow = null;
			return false;
		}

		shadow = entry.Shadow;
		if ((entry.EveryFrame && PrincipalEveryFrame) || !GuestRate)
		{
			++EveryFrameWrites;
			return true;
		}

		if (Refii.Core.Engine_GuestFrame.HalfRate)
		{
			++GuestFrameWrites;
			return true;
		}

		return false;
	}

	/// <summary>
	/// The body was just placed: lock the shadow proxy's transform to the body immediately.
	/// </summary>
	/// <remarks>
	/// <c>GameEntities.Place</c> only calls <see cref="Placed"/> when the body's transform actually
	/// changed (stationary NPCs and props return early and never call this). When an NPC is moving
	/// via display-rate interpolation (<c>ResampleMovers</c>), leaving the cast-only shadow proxy
	/// frozen at the previous 15 Hz tick position while the shadow-receiving visible body glides
	/// 10–15 cm ahead causes the proxy mesh to stand behind/inside the visible mesh and cast a 15 Hz
	/// self-shadowing strobe onto the character's back and limbs. Locking the proxy transform to the
	/// body whenever the body moves eliminates self-shadowing flicker without polling the guest.
	/// </remarks>
	public static void Placed(MeshInstance3D body)
	{
		if (!ByBody.TryGetValue(body, out Entry? entry))
		{
			return;
		}

		Place(entry);
		if ((entry.EveryFrame && PrincipalEveryFrame) || !GuestRate)
		{
			++EveryFramePlacements;
		}
		else
		{
			++GuestFramePlacements;
		}
	}

	/// <summary>
	/// Once per frame after the walk: a proxy that was created before entering the tree catches up,
	/// and every proxy shows exactly when its body does (a sibling inherits nothing).
	/// </summary>
	public static void EndWalk()
	{
		if (ByBody.Count == 0)
		{
			return;
		}

		foreach (Entry entry in ByBody.Values)
		{
			if (!GodotObject.IsInstanceValid(entry.Body) || !GodotObject.IsInstanceValid(entry.Proxy))
			{
				continue;
			}

			if (entry.PlacePending)
			{
				Place(entry);
				++CaughtUpPlacements;
			}

			bool visible = entry.Body.IsVisibleInTree();
			if (entry.LastVisible != visible)
			{
				entry.LastVisible = visible;
				entry.Proxy.Visible = visible;
			}
		}
	}

	private static void Place(Entry entry)
	{
		if (!entry.Body.IsInsideTree() || !entry.Proxy.IsInsideTree())
		{
			entry.PlacePending = true;
			return;
		}

		entry.Proxy.GlobalTransform = entry.Body.GlobalTransform;
		entry.PlacePending = false;
	}

	/// <summary>Drops the body's shadow skeleton: the proxy went, or the body is leaving the tree.</summary>
	public static void Forget(MeshInstance3D body)
	{
		if (!ByBody.Remove(body, out Entry? entry))
		{
			return;
		}

		Skeleton3D? liveKey = null;
		foreach ((Skeleton3D live, Entry candidate) in ByLive)
		{
			if (ReferenceEquals(candidate, entry))
			{
				liveKey = live;
				break;
			}
		}

		if (liveKey is not null)
		{
			ByLive.Remove(liveKey);
		}

		if (entry.Shadow is not null && GodotObject.IsInstanceValid(entry.Shadow))
		{
			entry.Shadow.QueueFree();
		}
	}

	public static Dictionary<string, object> Report()
	{
		int everyFrameRigs = 0;
		foreach (Entry entry in ByBody.Values)
		{
			if (entry.EveryFrame)
			{
				++everyFrameRigs;
			}
		}

		return new Dictionary<string, object>
		{
			["active"] = Active,
			["following"] = Following,
			["everyFrameRigs"] = everyFrameRigs,
			["everyFrameMisses"] = EveryFrameMisses,
			["guestFrameWrites"] = GuestFrameWrites,
			["everyFrameWrites"] = EveryFrameWrites,
			["guestFramePlacements"] = GuestFramePlacements,
			["everyFramePlacements"] = EveryFramePlacements,
			["caughtUpPlacements"] = CaughtUpPlacements,
			["guestRate"] = GuestRate,
			["principalEveryFrame"] = PrincipalEveryFrame,
		};
	}
}
