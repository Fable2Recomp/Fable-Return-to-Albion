using System;
using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Animations;

/// <summary>
/// Plays the guest's STATED clips on a rig, on Godot's clock. The guest keeps a blend tree
/// per rig (component 19) whose leaves are clip key, time and loop, folded by weights; the
/// bridge flattens it to <see cref="HostBridge.GuestAnimLeaf"/>s each frame. Each leaf's clip
/// is built once from the animation data (<see cref="Engine_AnimBuilder"/>, the same clips
/// the game ships) and sampled here per bone at the leaf's time, advanced on the host clock
/// between the guest's ticks; the leaves are blended by their weights and written as local
/// bone poses. No bone is copied from the guest. When a leaf's clip is unknown, the tree is
/// replicated, or the rig carries a procedural layer (dog paw IK), <see cref="Apply"/> answers
/// false and the caller keeps the pose feed for that frame — a correctness fallback, not a mode.
/// </summary>
public sealed class Engine_ClipAnimator
{
	private sealed class Built
	{
		/// <summary>Only on the resource path (<see cref="ManagedClipSampling"/> off).</summary>
		public Animation? Animation;
		public int[] RotationTrack = System.Array.Empty<int>();
		public int[] PositionTrack = System.Array.Empty<int>();
		public float Length;
		public bool Failed;

		/// <summary>The clip's keys, lifted out of the `Animation` resource ONCE.</summary>
		/// <remarks>
		/// Reading a key through `RotationTrackInterpolate` costs an interop crossing and a binary
		/// search (`Animation::_find`) per bone per rig per step; held here, a sample is a cursor
		/// step and a lerp in managed code.
		///
		/// Exactly Godot's arithmetic. Every track is built `InterpolationType.Linear`, and
		/// Godot's linear case is
		/// `_interpolate(keys[idx], keys[next], c)`, which for a Quaternion is
		/// `p_a.slerp(p_b, p_c)` (`scene/resources/animation.cpp:2456`). Slerp and lerp below
		/// reproduce that; anything that is NOT Linear must keep using the resource.
		/// </remarks>
		public Keys[] RotationKeys = System.Array.Empty<Keys>();

		public Keys[] PositionKeys = System.Array.Empty<Keys>();

		public bool KeysLifted;
	}

	/// <summary>
	/// A clip's keys, decoded once and shared by every rig. They depend only on the clip and the
	/// decoder (<see cref="Engine_AnimBuilder.DecodeKeys"/>); the skeleton only picks which bone
	/// each clip track lands on, and almost no two rigs share a bone list (clothing and hair).
	/// </summary>
	private sealed class SharedClip
	{
		public float Length;
		public Keys[] Rotations = System.Array.Empty<Keys>();
		public Keys[] Positions = System.Array.Empty<Keys>();
		public long LastTaken;
	}

	private readonly record struct SharedKey(Engine_AnimDecoder Decoder, uint Clip);

	private static readonly Dictionary<SharedKey, SharedClip?> SharedClips = new();

	/// <summary>Above this many shared clips the least recently taken half is dropped.</summary>
	private const int SharedClipCap = 2048;

	/// <summary>One track's keys, plus the cursor that makes a monotonic playhead O(1).</summary>
	private sealed class Keys
	{
		public float[] Times = System.Array.Empty<float>();
		public Quaternion[] Rotations = System.Array.Empty<Quaternion>();
		public Vector3[] Positions = System.Array.Empty<Vector3>();
		public int Cursor;

		/// <summary>The key index whose span contains <paramref name="t"/>, and the blend.</summary>
		/// <remarks>
		/// A playhead mostly moves forward by one span, so the cursor is stepped and only falls
		/// back to a search when overtaken (a wrap or a re-seek).
		/// </remarks>
		public int Span(float t, out float c)
		{
			c = 0f;
			int n = Times.Length;
			if (n <= 1)
			{
				return 0;
			}

			if (t <= Times[0])
			{
				return 0;
			}

			if (t >= Times[n - 1])
			{
				return n - 1;
			}

			// The cursor is shared by every rig and every leaf sampling this clip, so a miss
			// is common: step at most one span forward, otherwise binary-search.
			int i = Cursor;
			if (i < 0 || i >= n - 1 || Times[i] > t)
			{
				i = LastAtOrBefore(t);
			}
			else if (i < n - 2 && Times[i + 1] <= t)
			{
				++i;
				if (i < n - 2 && Times[i + 1] <= t)
				{
					i = LastAtOrBefore(t);
				}
			}

			Cursor = i;
			float span = Times[i + 1] - Times[i];
			c = span > 0f ? (t - Times[i]) / span : 0f;
			return i;
		}

		/// <summary>The last key index whose time is at or before <paramref name="t"/>, below n-1.</summary>
		private int LastAtOrBefore(float t)
		{
			int lo = 0, hi = Times.Length - 2;
			while (lo < hi)
			{
				int mid = (lo + hi + 1) >> 1;
				if (Times[mid] <= t)
				{
					lo = mid;
				}
				else
				{
					hi = mid - 1;
				}
			}

			return lo;
		}
	}

	/// <summary>Whether clips are sampled from the managed key cache instead of the resource.</summary>
	/// <remarks>A lever, not a setting: it exists so the two paths can be A/B'd on one run.</remarks>
	public static bool ManagedClipSampling = true;

	/// <summary>Copies a clip's keys out of the `Animation` resource, once.</summary>
	/// <remarks>
	/// Only linear tracks are lifted: the managed sample reproduces Godot's linear case and
	/// nothing else, so any other interpolation keeps using the resource.
	/// </remarks>
	private static void LiftKeys(Built built)
	{
		if (built.KeysLifted)
		{
			return;
		}

		built.KeysLifted = true;
		if (built.Animation is null)
		{
			return;
		}

		built.RotationKeys = new Keys[built.RotationTrack.Length];
		built.PositionKeys = new Keys[built.PositionTrack.Length];

		for (int b = 0; b < built.RotationTrack.Length; ++b)
		{
			int rt = built.RotationTrack[b];
			if (rt < 0 ||
				built.Animation.TrackGetInterpolationType(rt) != Animation.InterpolationType.Linear)
			{
				continue;
			}

			int n = built.Animation.TrackGetKeyCount(rt);
			var keys = new Keys { Times = new float[n], Rotations = new Quaternion[n] };
			for (int i = 0; i < n; ++i)
			{
				keys.Times[i] = (float)built.Animation.TrackGetKeyTime(rt, i);
				keys.Rotations[i] = built.Animation.TrackGetKeyValue(rt, i).AsQuaternion();
			}

			built.RotationKeys[b] = keys;
		}

		for (int b = 0; b < built.PositionTrack.Length; ++b)
		{
			int pt = built.PositionTrack[b];
			if (pt < 0 ||
				built.Animation.TrackGetInterpolationType(pt) != Animation.InterpolationType.Linear)
			{
				continue;
			}

			int n = built.Animation.TrackGetKeyCount(pt);
			var keys = new Keys { Times = new float[n], Positions = new Vector3[n] };
			for (int i = 0; i < n; ++i)
			{
				keys.Times[i] = (float)built.Animation.TrackGetKeyTime(pt, i);
				keys.Positions[i] = built.Animation.TrackGetKeyValue(pt, i).AsVector3();
			}

			built.PositionKeys[b] = keys;
		}
	}

	/// <summary>The clip's rotation for one bone, from the key cache when it has one.</summary>
	private static Quaternion SampleRotation(Built built, int bone, int track, float time)
	{
		Keys? keys = ManagedClipSampling && bone < built.RotationKeys.Length
			? built.RotationKeys[bone]
			: null;
		if (keys is null || keys.Times.Length == 0)
		{
			return built.Animation?.RotationTrackInterpolate(track, time) ?? Quaternion.Identity;
		}

		int i = keys.Span(time, out float c);
		return i >= keys.Times.Length - 1
			? keys.Rotations[i]
			: keys.Rotations[i].Slerp(keys.Rotations[i + 1], c);
	}

	/// <summary>The clip's translation for one bone, from the key cache when it has one.</summary>
	private static Vector3 SamplePosition(Built built, int bone, int track, float time)
	{
		Keys? keys = ManagedClipSampling && bone < built.PositionKeys.Length
			? built.PositionKeys[bone]
			: null;
		if (keys is null || keys.Times.Length == 0)
		{
			return built.Animation?.PositionTrackInterpolate(track, time) ?? Vector3.Zero;
		}

		int i = keys.Span(time, out float c);
		return i >= keys.Times.Length - 1
			? keys.Positions[i]
			: keys.Positions[i].Lerp(keys.Positions[i + 1], c);
	}

	/// <summary>One clip playing on this rig. The guest's leaf states the record's clip time
	/// and weight once per game tick; both are kept as tick-stamped scalars and drawn at the
	/// shared clock's draw point, which is the guest's own `lerp(prev, cur, alpha)` render rule.
	/// A leaf that vanishes leaves its channel ramping out before it is dropped.</summary>
	private sealed class Channel
	{
		public HostBridge.GuestAnimLeaf Leaf;
		public Built Built = null!;
		public float StatedTime;
		public float Time;
		public float Weight;
		public float TargetWeight;

		/// <summary>The clip time unwrapped across loops, one sample per game tick.</summary>
		public readonly Refii.Core.Engine_TickScalar Playhead = new();

		/// <summary>
		/// The record's OWN weight pair as the guest states it: latched at the tick start and
		/// live. Summed across the leaves of one record, as <see cref="TargetWeight"/> is.
		/// </summary>
		public float RecordPrevious;

		public float RecordCurrent;

		/// <summary>The tick the playhead was last stated on; MaxValue before the first.</summary>
		public uint PlayheadTick = uint.MaxValue;

		/// <summary>Running unwrapped clip time behind <see cref="Playhead"/>.</summary>
		public double Unwrapped;

		/// <summary>The stated time the unwrapped time was last advanced from.</summary>
		public float LastStated;
		public float BlendIn = 0.2f;
		public float BlendOut = 0.2f;
		public ulong LastUsec;
		public bool Seen;

		/// <summary>Position in this tick's export, front to back: the guest's list order.</summary>
		public int Order;
	}

	private readonly Skeleton3D _skeleton;

	/// <summary>The shadow skeleton missed a bone write since it was last mirrored, so the next mirror writes every bone.</summary>
	private bool _shadowStale = true;
	private readonly string _skeletonName;
	private readonly Dictionary<uint, Built> _clips = new();
	private Dictionary<string, List<int>>? _boneCandidates;
	/// <summary>
	/// One channel per stated leaf, not per clip. The guest's type-2 weighted node samples a
	/// single clip at several times and blends them by each child's weight — a viseme is four
	/// samples of the clip "Phonemes" (`sub_822B0618`), and a record crossfading a clip into
	/// itself states the same shape. The bridge lists leaves in a stable tree order, so the
	/// occurrence index names each sample.
	/// </summary>
	private readonly record struct ChannelKey(uint Uid, uint Clip, int At);

	private readonly Dictionary<ChannelKey, Channel> _channels = new();

	/// <summary>How many times each (record, clip) has been seen so far in the leaf walk.</summary>
	private readonly Dictionary<ulong, int> _keyOccurrences = new();
	private readonly List<ChannelKey> _dropped = new();
	public int Driven;
	public string LastFallback = string.Empty;

	private static ulong PlayheadKey(HostBridge.GuestAnimLeaf leaf) => ((ulong)leaf.Uid << 32) | leaf.Key;
	private readonly List<HostBridge.GuestAnimLeaf> _leaves = new();
	private Quaternion[] _rotation = System.Array.Empty<Quaternion>();
	private Vector3[] _position = System.Array.Empty<Vector3>();
	private float[] _rotationWeight = System.Array.Empty<float>();
	private float[] _positionWeight = System.Array.Empty<float>();
	private Quaternion[] _restRotation = System.Array.Empty<Quaternion>();
	private Vector3[] _restPosition = System.Array.Empty<Vector3>();

	public static int ClipDrivenFrames;
	public static int FallbackUnknownClip;
	public static int SkippedNonFiniteLeaves;
	public static int FallbackProcedural;
	public static int FallbackNoLeaves;

	/// <summary>Frames a rig yielded to the guest's physics-written pose (flag 8).</summary>
	public static int FallbackRagdoll;

	private readonly int[] _modelToGuest;
	private readonly Dictionary<ulong, byte[]> _masks = new();
	private Quaternion[] _layerRotation = System.Array.Empty<Quaternion>();
	private Vector3[] _layerPosition = System.Array.Empty<Vector3>();
	private float[] _layerRotationWeight = System.Array.Empty<float>();
	private float[] _layerPositionWeight = System.Array.Empty<float>();
	private readonly Dictionary<uint, float> _recordWeight = new();

	/// <summary>Each record's position in the guest's list — the runner-up's tie-break.</summary>
	private readonly Dictionary<uint, int> _recordOrder = new();
	private readonly List<Channel> _overlays = new();

	/// <summary>Shadow_Hip's model bone, or -1; its children bar Shadow_SpineControl.</summary>
	private int _hipBone = -1;

	private readonly List<int> _hipChildren = new();

	public Engine_ClipAnimator(Skeleton3D skeleton, string skeletonName, int[] modelToGuest)
	{
		_skeleton = skeleton;
		_skeletonName = skeletonName;
		_modelToGuest = modelToGuest;
		int bones = skeleton.GetBoneCount();
		_finalPosition = new Vector3[bones];
		_finalRotation = new Quaternion[bones];
		_finalScale = new Vector3[bones];
		int guestCount = 0;
		foreach (int g in modelToGuest)
		{
			guestCount = Math.Max(guestCount, g + 1);
		}

		_guestToModel = new int[guestCount];
		System.Array.Fill(_guestToModel, -1);
		for (int b = 0; b < modelToGuest.Length; ++b)
		{
			if (modelToGuest[b] >= 0)
			{
				_guestToModel[modelToGuest[b]] = b;
			}
		}

		_rotation = new Quaternion[bones];
		_position = new Vector3[bones];
		_rotationWeight = new float[bones];
		_positionWeight = new float[bones];
		_layerRotation = new Quaternion[bones];
		_layerPosition = new Vector3[bones];
		_layerRotationWeight = new float[bones];
		_layerPositionWeight = new float[bones];
		_restRotation = new Quaternion[bones];
		_restPosition = new Vector3[bones];
		for (int b = 0; b < bones; ++b)
		{
			Transform3D rest = skeleton.GetBoneRest(b);
			_restRotation[b] = rest.Basis.GetRotationQuaternion();
			_restPosition[b] = rest.Origin;
			if (skeleton.GetBoneName(b) == "Shadow_Hip")
			{
				_hipBone = b;
			}
		}

		if (_hipBone >= 0)
		{
			for (int b = 0; b < bones; ++b)
			{
				if (skeleton.GetBoneParent(b) == _hipBone && skeleton.GetBoneName(b) != "Shadow_SpineControl")
				{
					_hipChildren.Add(b);
				}
			}
		}
	}

	/// <summary>Whether this animator drives that skeleton — the identity a re-created rig fails.</summary>
	public bool Drives(Skeleton3D skeleton) => ReferenceEquals(_skeleton, skeleton) && Alive;

	/// <summary>False once the skeleton has been freed; nothing here may touch it then.</summary>
	public bool Alive => GodotObject.IsInstanceValid(_skeleton);

	/// <summary>Whether the guest states clips for this rig and every one of them is playable
	/// here; when true the bones were written and the pose feed must not run this frame.</summary>
	/// <summary>The skeleton's parent table, read once — see Apply.</summary>
	private int[]? _parents;

	/// <summary>
	/// Advances this rig's clips and, when <paramref name="writeBones"/>, poses the skeleton.
	/// </summary>
	/// <remarks>
	/// THE ADVANCE IS NOT OPTIONAL; THE WRITE IS. Channels advance by wall-clock delta and
	/// self-correct, so a rig whose bones are not written still holds the right time and poses
	/// correctly on the first frame it is written again. That is the distinction the frustum gate
	/// this file used to have got wrong: it skipped BOTH, so a villager culled from birth never
	/// advanced and slid down the street in its bind pose.
	///
	/// Everything after the advance is per bone — sampling every clip into accumulation buffers,
	/// folding them, and two engine crossings each to write the pose — and that is what a rig
	/// nobody can see does not need.
	/// </remarks>
	public bool Apply(uint entity, bool writeBones = true)
	{
		_entity = entity;
		if (!Alive)
		{
			LastFallback = "skeleton freed";
			return false;
		}

		int count = HostBridge.GetEntityAnimState(entity, _leaves, out uint flags);
		count = DelayNetworkLeaves(count);
		// Only a procedural layer the clips cannot express (dog paw/look IK, component 97)
		// keeps the pose feed; the byte at +576 reads 1 on ordinary rigs and means nothing here.
		if ((flags & 2) != 0)
		{
			++FallbackProcedural;
			LastFallback = "procedural";
			return false;
		}

		// A RAGDOLL IS THE GUEST'S POSE. Flag 8 is the ragdoll object in mode 3 or 4, the modes
		// `sub_8257DBC0` copies the Havok bodies back over the bones for; the record list is no
		// longer what the body does. The pose feed carries that pose; clips must not fold over it.
		if ((flags & 8) != 0)
		{
			++FallbackRagdoll;
			LastFallback = "ragdoll";
			return false;
		}

		if (count == 0 || !Engine_AnimCatalog.EnsureLoaded())
		{
			++FallbackNoLeaves;
			LastFallback = count == 0 ? "no leaves" : "catalog not loaded";
			return false;
		}

		// The frame's instant, not this rig's, so every rig samples at the same time.
		ulong now = Refii.Entities.Engine_DrawnFrame.NowUsec;
		// One counter for every stamp: the clip playhead rides the same game tick the poses and
		// placements do. `dword_83319650` skips (`821d46e8` branches past its own increment).
		uint tick = Refii.Core.Engine_TickClock.GuestGameTick;
		int bones = _skeleton.GetBoneCount();

		// THE PARENT TABLE IS TOPOLOGY, NOT STATE. `GetBoneParent` is a crossing into the engine
		// and it was called once per bone in four separate loops of this method, every apply, for
		// every rig — hundreds of calls per character per frame to read a table that cannot change
		// while the skeleton exists. Read once, held here.
		if (_parents is null || _parents.Length != bones)
		{
			_parents = new int[bones];
			for (int b = 0; b < bones; ++b)
			{
				_parents[b] = _skeleton.GetBoneParent(b);
			}
		}

		// EVENTS IN: every stated leaf finds or starts its channel and sets its target.
		foreach (Channel c in _channels.Values)
		{
			c.Seen = false;
		}

		int order = -1;
		_keyOccurrences.Clear();
		foreach (HostBridge.GuestAnimLeaf leaf in _leaves)
		{
			++order;

			// The pump does not check the guest's floats: a leaf read while its record is torn
			// down can carry NaN weight or time, and MoveToward throws on NaN. Such a leaf is
			// not seen this frame, so its channel ramps out.
			if (!float.IsFinite(leaf.Weight) || !float.IsFinite(leaf.Time))
			{
				++SkippedNonFiniteLeaves;
				continue;
			}
			Built? built = Ensure(leaf.Key);
			if (built is null)
			{
				++FallbackUnknownClip;
				LastFallback = $"unknown clip {leaf.Key:X8}";
				return false;
			}

			ulong baseKey = PlayheadKey(leaf);
			int at = _keyOccurrences.TryGetValue(baseKey, out int seen) ? seen : 0;
			_keyOccurrences[baseKey] = at + 1;
			var key = new ChannelKey(leaf.Uid, leaf.Key, at);
			if (!_channels.TryGetValue(key, out Channel? channel))
			{
				channel = new Channel
				{
					Leaf = leaf, Built = built, StatedTime = leaf.Time, Time = leaf.Time,
					Weight = 0f, LastUsec = now,
				};
				_channels[key] = channel;
			}

			// A second statement of the same occurrence cannot happen with the occurrence index
			// in the key; if it does, two playheads of one clip under one record carry weights
			// that sum to the record's, so sum them.
			if (channel.Seen)
			{
				channel.TargetWeight += leaf.Weight;

				// Assigned, not summed: both leaves belong to the same record and carry the same
				// pair. Summing it would double the denominator and halve every share.
				channel.RecordPrevious = leaf.RecordPrevious;
				channel.RecordCurrent = leaf.RecordCurrent;
				if (leaf.Weight > channel.Leaf.Weight)
				{
					channel.Leaf = leaf;
				}

				continue;
			}

			channel.Leaf = leaf;
			channel.Seen = true;
			channel.Order = order;
			channel.TargetWeight = leaf.Weight;
			channel.RecordPrevious = leaf.RecordPrevious;
			channel.RecordCurrent = leaf.RecordCurrent;
			if (leaf.BlendIn > 0f) channel.BlendIn = leaf.BlendIn;
			if (leaf.BlendOut > 0f) channel.BlendOut = leaf.BlendOut;
			ObservePlayhead(channel, leaf, tick);
		}

		// On the guest's ticks: a seen channel's weight and playhead are the stated per-tick
		// values drawn at the shared draw point; a vanished channel ramps out on the host clock
		// and drops. One alpha for the whole rig, from the owning body's playhead (else the
		// world's), read once so every clip on the rig shares the instant.
		float alpha = OwnerPlayhead is { Ready: true }
			? (float)OwnerPlayhead.Alpha(Refii.Entities.Engine_DrawnFrame.NowUsec)
			: Refii.Entities.Engine_DrawnFrame.Alpha;
		_dropped.Clear();
		float totalWeight = 0f;
		foreach ((ChannelKey key, Channel c) in _channels)
		{
			float dt = Mathf.Min((now - c.LastUsec) * 1e-6f, 0.25f);
			c.LastUsec = now;
			if (c.Seen)
			{
				// The guest states both ends of this blend. `sub_82211660` draws the weight as
				// `(1 - alpha) * record.weight_previous + alpha * record.weight_current`
				// (0x82211B08..0x82211B24); the pair is read, never rebuilt on the host.
				//
				// The leaf's own share of its record is preserved: `TargetWeight` is the record
				// weight already scaled by the blend tree above the leaf, so the share is
				// `TargetWeight / RecordCurrent` and the drawn weight is that share of the
				// blended record weight.
				float drawn = Mathf.Lerp(c.RecordPrevious, c.RecordCurrent, alpha);
				float share = c.RecordCurrent > 1e-5f ? c.TargetWeight / c.RecordCurrent : 1f;
				c.Weight = share * drawn;
			}
			else
			{
				float rate = 1f / Mathf.Max(c.BlendOut, 0.02f);
				c.Weight = Mathf.MoveToward(c.Weight, 0f, rate * dt);
				if (c.Weight <= 1e-4f)
				{
					_dropped.Add(key);
					continue;
				}
			}

			Advance(c, now);
			if (!c.Leaf.Additive)
			{
				totalWeight += c.Weight;
			}
		}

		foreach (ChannelKey key in _dropped)
		{
			_channels.Remove(key);
		}

		// The clips are current now; the pose is the expensive half and the caller may not want
		// it. Answering true keeps the caller on this branch — the rig IS clip-driven — so the
		// pose feed does not write over a skeleton the clips own.
		if (!writeBones)
		{
			return true;
		}

		if (totalWeight <= 1e-5f)
		{
			++FallbackNoLeaves;
			LastFallback = "zero weight";
			return false;
		}

		// THE GUEST'S FOLD, IN TWO PARTS.
		//
		// PRIMARIES (`sub_82211660`, `sub_822013A8`): one record WINS by priority — first in the
		// list on a tie — and is crossfaded against the highest-weighted other primary by the
		// winner's OWN weight, with the guest's short-circuits at 0.0167 and 0.9833. It is not a
		// normalised sum. Every bone the winner's clip lacks is the skeleton rest pose
		// (`sub_822351B0` copies skel+144), which is what a primary is for: the whole body.
		//
		// OVERLAYS (`sub_82232BF8`, `sub_8220CB18`): the second list, sorted by track then
		// priority, each gated by its priority bitmask against the winner's priority, and
		// composited PER BONE: `wb = mask[bone]/255 * weight`, rotation slerp, translation lerp.
		// A zero mask byte leaves the bone as the primaries left it. Folding these as primaries
		// was the T pose: a wave or a speech clip covers the arms or the face, and every other
		// bone went to rest while the placement kept walking.
		// A HARD CUT SNAPS. A primary that appears in the same tick its predecessor vanished
		// has nothing to crossfade from; its channel is born at weight 0 and eases up, and for
		// that first frame no primary carries weight — so no top, no overlays, and every bone
		// written as rest. That was the one-frame T-pose flash on each such cut (`[rig-rest]`
		// logged `w 0.00->1.00 PRI` on every one). The guest's record is at its full weight on
		// that tick, so the host's channel is too.
		bool anyPrimaryWeight = false;
		Channel? loudest = null;
		foreach (Channel c in _channels.Values)
		{
			if (c.Leaf.Additive || c.Leaf.Overlay)
			{
				continue;
			}

			if (c.Weight > 1e-5f)
			{
				anyPrimaryWeight = true;
				break;
			}

			if (c.Seen && c.TargetWeight > 1e-5f &&
				(loudest is null || c.Leaf.Priority > loudest.Leaf.Priority
				 || (c.Leaf.Priority == loudest.Leaf.Priority && c.Leaf.Uid < loudest.Leaf.Uid)))
			{
				loudest = c;
			}
		}

		if (!anyPrimaryWeight && loudest is not null)
		{
			loudest.Weight = loudest.TargetWeight;
			anyPrimaryWeight = true;
		}

		// NO PRIMARY, NO TREE. With nothing on the first list the guest builds no blend tree
		// (`sub_82211660` needs a top record) and its pose buffer keeps whatever it held; an
		// overlay alone — a mood pose ramping while the idle is between records — is not a
		// pose. Writing rest here was the last of the T-pose flashes. Hold the skeleton as it is.
		if (!anyPrimaryWeight)
		{
			++HeldNoPrimaryFrames;
			LastFallback = string.Empty;
			return true;
		}

		_recordWeight.Clear();
		_recordOrder.Clear();
		foreach (Channel c in _channels.Values)
		{
			if (c.Leaf.Additive || c.Leaf.Overlay || c.Weight <= 1e-5f)
			{
				continue;
			}

			_recordWeight[c.Leaf.Uid] = _recordWeight.GetValueOrDefault(c.Leaf.Uid) + c.Weight;
			if (_recordOrder.TryGetValue(c.Leaf.Uid, out int seen))
			{
				_recordOrder[c.Leaf.Uid] = Mathf.Min(seen, c.Order);
			}
			else
			{
				_recordOrder[c.Leaf.Uid] = c.Order;
			}
		}

		int Order(uint uid) => _recordOrder.GetValueOrDefault(uid, int.MaxValue);

		uint topUid = 0;
		int topOrder = -1;
		int topPriority = int.MinValue;
		bool haveTop = false;
		foreach (Channel c in _channels.Values)
		{
			if (c.Leaf.Additive || c.Leaf.Overlay || c.Weight <= 1e-5f)
			{
				continue;
			}

			// TIES GO TO THE NEWEST RECORD. `sub_822013A8` walks the list back to front with a
			// strict `>` (0x82201434..7c), and records are appended at the tail, so among equal
			// priorities the record nearest the tail — the one created last — wins. The export
			// walks front to back, so "newest" is the highest list position.
			if (!haveTop || c.Leaf.Priority > topPriority
				|| (c.Leaf.Priority == topPriority && c.Order > topOrder))
			{
				haveTop = true;
				topUid = c.Leaf.Uid;
				topOrder = c.Order;
				topPriority = c.Leaf.Priority;
			}
		}

		// The runner is picked deterministically: `_recordWeight` is a Dictionary with unstable
		// enumeration order, and the runner sets `topShare`, the crossfade ratio.
		//
		// Ties go to the oldest record — the guest's rule, the opposite of the winner's.
		// `sub_82237F90` scans the list head to tail with a strict `>` (0x8223803C), so among
		// equal weights the record nearest the head survives; the winner scan runs backward.
		uint runnerUid = 0;
		float runnerWeight = 0f;
		foreach ((uint uid, float w) in _recordWeight)
		{
			if (uid == topUid)
			{
				continue;
			}

			if (w > runnerWeight || (w == runnerWeight && runnerUid != 0u && Order(uid) < Order(runnerUid)))
			{
				runnerUid = uid;
				runnerWeight = w;
			}
		}

		if (runnerUid != _lastRunnerUid && _lastRunnerUid != 0u && runnerUid != 0u)
		{
			++RunnerFlips;
			++RigRunnerFlips;
		}

		_lastRunnerUid = runnerUid;
		LastTopUid = topUid;

		// The factor is the winner's own stated weight, not a normalised ratio. `sub_82211660`
		// computes `(1 - alpha) * prev + alpha * cur` off the winner record (0x82211B24) and
		// uses it raw — there is no division by the pair's total anywhere on that path, and no
		// clamp: the 59/60 and 1/60 branches below are the only guards the guest has.
		//
		// The presence test is 1e-4 on the runner's weight, not the winner's: `sub_82237F90`
		// emits no runner-up at all when its weight fails
		// `> 1e-4` (0x82238070..0x82238074), and `sub_82211660` then adopts the winner's pose
		// whole without computing a factor at all (0x82211A80..0x82211AD8).
		float topWeight = 0f;
		if (haveTop)
		{
			foreach (Channel c in _channels.Values)
			{
				if (!c.Leaf.Additive && !c.Leaf.Overlay && c.Leaf.Uid == topUid)
				{
					topWeight = Mathf.Lerp(c.RecordPrevious, c.RecordCurrent, alpha);
					break;
				}
			}
		}

		float topShare = runnerWeight > 1e-4f ? topWeight : 1f;
		if (topShare >= 0.98333f) topShare = 1f;
		if (topShare <= 0.016667f) topShare = 0f;

		// How far the crossfade ratio moved in one frame; a step moves every bone by that
		// fraction of the distance between two clips. Reported.
		if (_haveLastShare)
		{
			float step = Mathf.Abs(topShare - _lastShare);
			if (step > LargestShareStep)
			{
				LargestShareStep = step;
			}

			if (step > 0.1f)
			{
				++ShareSteps;
				++RigShareSteps;
			}
		}

		_lastShare = topShare;
		_haveLastShare = true;
		Channel? top = null;
		foreach (Channel c in _channels.Values)
		{
			if (!c.Leaf.Additive && !c.Leaf.Overlay && c.Leaf.Uid == topUid && c.Weight > 1e-5f)
			{
				top = c;
				break;
			}
		}

		System.Array.Clear(_rotationWeight, 0, bones);
		System.Array.Clear(_positionWeight, 0, bones);
		System.Array.Clear(_layerRotationWeight, 0, bones);
		System.Array.Clear(_layerPositionWeight, 0, bones);
		_overlays.Clear();
		bool partnerSampled = false;
		foreach (Channel c in _channels.Values)
		{
			HostBridge.GuestAnimLeaf leaf = c.Leaf;
			if (leaf.Overlay)
			{
				if (c.Weight > 1e-4f && haveTop && (leaf.PriorityMask & (uint)topPriority) != 0)
				{
					_overlays.Add(c);
				}
				continue;
			}
			if (leaf.Additive)
			{
				// A TYPE-3 CHILD THAT IS A CLIP IS A MASKED OVERLAY, NOT A DELTA. A clip leaf's
				// delta flag is always 0 (every loader passes 0 to `sub_82B77C00`; only a type-5
				// difference node sets it), so `sub_8220CB18` takes the masked path: per bone a
				// slerp toward the child by mask × weight, whole-body when no mask node sits
				// above it. That is the overlay path here; the first-frame delta this once
				// applied was an invention.
				if (c.Weight > 1e-4f && haveTop && (leaf.Uid == topUid || leaf.Uid == runnerUid))
				{
					_overlays.Add(c);
				}
				continue;
			}
			Built built = c.Built;
			float time = c.Time;
			if (leaf.Uid == topUid)
			{
				float weight = c.Weight / Mathf.Max(_recordWeight.GetValueOrDefault(topUid), 1e-5f);
				if (weight > 1e-5f)
				{
					Sample(built, time, weight, false, _rotation, _rotationWeight, _position, _positionWeight, bones);
				}
			}
			else if (leaf.Uid == runnerUid && topShare < 1f)
			{
				// THE PARTNER, kept apart: the guest's type-1 node SLERPs partner against top by the
				// top's own weight (`sub_82293278`, w > 0.5), which an nlerp of the two into one
				// accumulator only approximates.
				float weight = c.Weight / Mathf.Max(runnerWeight, 1e-5f);
				if (weight > 1e-5f)
				{
					Sample(built, time, weight, false, _layerRotation, _layerRotationWeight, _layerPosition, _layerPositionWeight, bones);
					partnerSampled = true;
				}
			}
			// a third primary never reaches the pose in the guest either
		}

		if (partnerSampled)
		{
			// crossfade(partner = A, top = B, t = topShare): result = slerp(A, B, t).
			for (int b = 0; b < bones; ++b)
			{
				bool hasTop = _rotationWeight[b] > 0f;
				bool hasPartner = _layerRotationWeight[b] > 0f;
				if (!hasTop && !hasPartner)
				{
					continue;
				}

				Quaternion topQ = hasTop ? _rotation[b].Normalized() : _restRotation[b];
				Quaternion partQ = hasPartner ? _layerRotation[b].Normalized() : _restRotation[b];
				_rotation[b] = partQ.Slerp(topQ, topShare);
				_rotationWeight[b] = 1f;
				if (_parents![b] >= 0)
				{
					Vector3 topP = _positionWeight[b] > 1e-5f ? _position[b] / _positionWeight[b] : _restPosition[b];
					Vector3 partP = _layerPositionWeight[b] > 1e-5f ? _layerPosition[b] / _layerPositionWeight[b] : _restPosition[b];
					_position[b] = partP.Lerp(topP, topShare);
					_positionWeight[b] = 1f;
				}
			}
		}


		if (_overlays.Count > 1)
		{
			_overlays.Sort((a, b) =>
				a.Leaf.Track != b.Leaf.Track ? a.Leaf.Track.CompareTo(b.Leaf.Track)
				: a.Leaf.Priority != b.Leaf.Priority ? a.Leaf.Priority.CompareTo(b.Leaf.Priority)
				: a.Leaf.Uid.CompareTo(b.Leaf.Uid));
		}

		// One overlay RECORD at a time: its leaves fold into a layer pose by their weights,
		// then the layer blends onto the body per bone at mask[bone]/255 times the record's
		// weight. Bones the layer's clips do not cover are its rest pose, as the guest samples
		// them; the mask is what keeps them off the body.
		for (int i = 0; i < _overlays.Count;)
		{
			uint uid = _overlays[i].Leaf.Uid;
			int j = i;
			float layerWeight = 0f;
			uint maskNode = 0;
			while (j < _overlays.Count && _overlays[j].Leaf.Uid == uid)
			{
				layerWeight += _overlays[j].Weight;
				if (maskNode == 0)
				{
					maskNode = _overlays[j].Leaf.Mask;
				}

				++j;
			}

			System.Array.Clear(_layerRotationWeight, 0, bones);
			System.Array.Clear(_layerPositionWeight, 0, bones);
			for (int k = i; k < j; ++k)
			{
				Channel c = _overlays[k];
				Sample(c.Built, c.Time, c.Weight / Mathf.Max(layerWeight, 1e-5f), false,
					_layerRotation, _layerRotationWeight, _layerPosition, _layerPositionWeight, bones);
			}

			byte[] mask = Mask(uid, maskNode);
			float w = Mathf.Clamp(layerWeight, 0f, 1f);

			// THE HIP'S CHILDREN STAY PUT. When an overlay's mask covers Shadow_Hip the guest
			// (`sub_8220CB18`, 0x8220CC20..) remembers the hip's local transform, applies the
			// blend, keeps the OLD translation on the new hip, and rewrites every hip child but
			// Shadow_SpineControl so its world placement is unchanged — the spine follows the
			// overlay's hip rotation, the legs do not swing with it.
			int hip = _hipBone;
			bool compensateHip = hip >= 0 && (mask.Length == 0 || (hip < _modelToGuest.Length &&
				_modelToGuest[hip] >= 0 && _modelToGuest[hip] < mask.Length && mask[_modelToGuest[hip]] != 0));
			Transform3D hipOld = compensateHip ? PoseOf(hip) : Transform3D.Identity;

			for (int b = 0; b < bones; ++b)
			{
				float wb = w;
				if (mask.Length != 0)
				{
					int g = b < _modelToGuest.Length ? _modelToGuest[b] : -1;
					wb = g >= 0 && g < mask.Length ? w * (mask[g] / 255f) : 0f;
				}

				if (wb <= 1e-4f)
				{
					continue;
				}

				Quaternion current = _rotationWeight[b] > 0f ? _rotation[b].Normalized() : _restRotation[b];
				Quaternion layer = _layerRotationWeight[b] > 0f ? _layerRotation[b].Normalized() : _restRotation[b];
				_rotation[b] = current.Slerp(layer, wb);
				_rotationWeight[b] = 1f;

				if (_parents![b] >= 0)
				{
					Vector3 currentPosition = _positionWeight[b] > 1e-5f ? _position[b] / _positionWeight[b] : _restPosition[b];
					Vector3 layerPosition = _layerPositionWeight[b] > 1e-5f ? _layerPosition[b] / _layerPositionWeight[b] : _restPosition[b];
					_position[b] = currentPosition.Lerp(layerPosition, wb);
					_positionWeight[b] = 1f;
				}
			}

			if (compensateHip)
			{
				Transform3D hipNew = PoseOf(hip);
				hipNew.Origin = hipOld.Origin;
				// child_local' · hipNew == child_local · hipOld  (row vectors, the guest's
				// convention); in Godot's column-major terms: local' = hipNew⁻¹ · hipOld · local.
				Transform3D fix = hipNew.AffineInverse() * hipOld;
				foreach (int child in _hipChildren)
				{
					Transform3D local = fix * PoseOf(child);
					_rotation[child] = local.Basis.GetRotationQuaternion().Normalized();
					_rotationWeight[child] = 1f;
					_position[child] = local.Origin;
					_positionWeight[child] = 1f;
				}

				_rotation[hip] = hipNew.Basis.GetRotationQuaternion().Normalized();
				_position[hip] = hipNew.Origin;
			}
			i = j;
		}

		LastOverlays = _overlays.Count;

		int restBones = 0;
		for (int b = 0; b < bones; ++b)
		{
			if (_rotationWeight[b] <= 0f)
			{
				++restBones;
			}

			Quaternion rotation = _rotationWeight[b] > 0f ? _rotation[b].Normalized() : _restRotation[b];
			Vector3 position = _positionWeight[b] > 1e-5f ? _position[b] / _positionWeight[b] : _restPosition[b];
			_finalRotation[b] = rotation;
			_finalPosition[b] = position;
		}

		ApplyMorph(bones);
		ApplyPostOps();

		// One crossing per bone, and only for bones that moved. The pose is handed over whole
		// (Godot decomposes it back; `ScaledLocal` is its rotation-then-scale composition). A
		// write is a marshalled call that also dirties the skeleton for the render thread, so
		// an unchanged bone is skipped.
		if (_written.Length != bones)
		{
			_written = new Transform3D[bones];
			_hasWritten = false;
		}

		int movedBones = 0;
		float moved = 0f;
		bool watched = Address != 0u && Address == WatchAddress;

		// THE SHADOW SKELETON TAKES THE SAME BONES, ON GUEST FRAMES (every frame for a principal).
		// Between guest frames the body takes writes the shadow does not, so the first mirror
		// after any of those writes every bone, not only the ones that moved this frame.
		bool shadowNow = Refii.Entities.Engine_ShadowSkeletons.WriteNow(_skeleton, out Skeleton3D? shadow);
		bool shadowAll = shadowNow && _shadowStale;
		for (int b = 0; b < bones; ++b)
		{
			var pose = new Transform3D(
				new Basis(_finalRotation[b]).ScaledLocal(_finalScale[b]), _finalPosition[b]);
			bool unchanged = _hasWritten && pose.IsEqualApprox(_written[b]);
			if (unchanged && !shadowAll)
			{
				++SkippedBoneWrites;
				continue;
			}

			if (!unchanged)
			{
				if (watched && _hasWritten)
				{
					moved += _written[b].Origin.DistanceTo(pose.Origin);
				}

				_written[b] = pose;
				++movedBones;
				_skeleton.SetBonePose(b, pose);
			}

			if (shadowNow)
			{
				shadow!.SetBonePose(b, pose);
			}
		}

		if (shadowNow)
		{
			_shadowStale = false;
		}
		else if (movedBones != 0)
		{
			_shadowStale = true;
		}

		if (watched && _hasWritten)
		{
			NoteWatch(moved, alpha, topShare, _channels.Count, movedBones);
		}

		_hasWritten = true;
		LastMovedBones = movedBones;
		BoneWrites += movedBones;
		if (movedBones != 0)
		{
			SkeletonsWritten++;
		}

		// The readout that says whether a rig is in bind pose BY CONSTRUCTION: bones no live
		// channel covered are written as rest, so a frame with most bones here is a T pose.
		LastRestBones = restBones;
		LastTopKey = top?.Leaf.Key ?? 0u;
		LastTopShare = topShare;
		LastChannels = _channels.Count;
		if (restBones * 2 > bones)
		{
			++MostlyRestFrames;
			ulong nowMs = Godot.Time.GetTicksMsec();
			if (RestLogs < 24 && nowMs - _lastRestLogMs > 3000)
			{
				++RestLogs;
				_lastRestLogMs = nowMs;
				var table = new System.Text.StringBuilder();
				foreach (Channel c in _channels.Values)
				{
					table.Append($" [{c.Leaf.Key:X8} uid {c.Leaf.Uid} pri {c.Leaf.Priority} trk {c.Leaf.Track}" +
						$" w {c.Weight:0.00}->{c.TargetWeight:0.00} {(c.Leaf.Overlay ? "OVL" : "PRI")}" +
						$"{(c.Leaf.Additive ? " add" : "")} mask {c.Leaf.Mask:X8} pm {c.Leaf.PriorityMask:X}" +
						$" seen {c.Seen} tracks {c.Built.Animation?.GetTrackCount() ?? c.Built.RotationTrack.Length}]");
				}

				GD.Print($"[rig-rest] '{_skeleton.Name}' {restBones}/{bones} rest, top {LastTopKey:X8} share {topShare:0.00} overlays {_overlays.Count}:{table}");
			}
		}

		++ClipDrivenFrames;
		++Driven;
		LastFallback = string.Empty;
		return true;
	}

	/// <summary>Bones written as rest on the last applied frame — no live channel covered them.</summary>
	public int LastRestBones { get; private set; }

	/// <summary>The clip key of the top (highest-priority) channel on the last frame, or 0.</summary>
	public uint LastTopKey { get; private set; }

	public float LastTopShare { get; private set; }

	public int LastChannels { get; private set; }

	/// <summary>Frames on this rig where more than half the bones were written as rest.</summary>
	public int MostlyRestFrames { get; private set; }

	public int BoneCount => _restRotation.Length;

	public int LastOverlays { get; private set; }

	/// <summary>
	/// Frames the crossfade partner changed identity. A flip moves `topShare`, and therefore the
	/// drawn pose; on a rig standing still this should not climb.
	/// </summary>
	public static long RunnerFlips;

	private uint _lastRunnerUid;

	/// <summary>
	/// This rig's own flip count and its current crossfade partner.
	/// </summary>
	public long RigRunnerFlips { get; private set; }

	public uint LastTopUid { get; private set; }

	public uint LastRunnerUid => _lastRunnerUid;

	/// <summary>The guest entity this animator drives; 0 until it is adopted.</summary>
	public uint Address;

	/// <summary>
	/// The owning body's anchor. The guest states the body's pair and its clip weights in the
	/// same pass on the same tick, so they share one anchor; two anchors put the limbs out of
	/// phase with the translation. Null while the body is not blending, where the world's
	/// alpha stands.
	/// </summary>
	public Refii.Core.Engine_PairPlayhead? OwnerPlayhead;

	/// <summary>
	/// The rig being watched, set from the debug channel. Pinned to a single rig so consecutive
	/// rows are that rig on consecutive frames; the other alpha rings mix subjects and cannot
	/// show a per-subject phase (clip time, blend weight, bone).
	/// </summary>
	/// <remarks>
	/// Setting this clears the ring, so no row from the previously watched rig survives.
	/// </remarks>
	public static uint WatchAddress
	{
		get => _watchAddress;
		set
		{
			_watchAddress = value;
			System.Array.Clear(Watch, 0, Watch.Length);
			_watchAt = 0;
		}
	}

	private static uint _watchAddress;

	private static readonly (ulong Usec, float Moved, float Alpha, float TopShare, int Channels, int Bones)[]
		Watch = new (ulong, float, float, float, int, int)[256];

	private static int _watchAt;

	private static void NoteWatch(float moved, float alpha, float topShare, int channels, int bones)
	{
		int at = System.Threading.Interlocked.Increment(ref _watchAt);
		Watch[at & 255] = (Godot.Time.GetTicksUsec(), moved, alpha, topShare, channels, bones);
	}

	/// <summary>One rig's drawn motion, frame by frame, oldest first; for the debug channel.</summary>
	public static List<Dictionary<string, object?>> WatchTrace()
	{
		var rows = new List<Dictionary<string, object?>>();
		int at = _watchAt;
		for (int back = 255; back >= 0; --back)
		{
			(ulong usec, float moved, float alpha, float share, int channels, int bones) =
				Watch[(at - back) & 255];
			if (usec == 0UL)
			{
				continue;
			}

			rows.Add(new Dictionary<string, object?>
			{
				["usec"] = usec,
				["moved"] = moved,
				["alpha"] = alpha,
				["topShare"] = share,
				["channels"] = channels,
				["bones"] = bones,
			});
		}

		return rows;
	}

	/// <summary>Frames the crossfade ratio moved more than a tenth in a single frame.</summary>
	public static long ShareSteps;

	/// <summary>The largest single-frame move of the crossfade ratio seen on any rig.</summary>
	public static float LargestShareStep;

	public long RigShareSteps { get; private set; }

	private float _lastShare;

	private bool _haveLastShare;

	/// <summary>Frames a rig kept its last pose because the guest listed no primary record.</summary>
	public static int HeldNoPrimaryFrames;

	private static int RestLogs;

	private ulong _lastRestLogMs;

	/// <summary>One clip's contribution at one weight into an accumulation buffer.</summary>
	private void Sample(Built built, float time, float weight, bool additive,
		Quaternion[] rotation, float[] rotationWeight, Vector3[] position, float[] positionWeight, int bones)
	{
		for (int b = 0; b < bones; ++b)
		{
			// BONE 0 IS IDENTITY IN THE GUEST'S POSE — rotation, translation AND scale. After
			// sampling, `sub_82208040` (0x82208140..78) overwrites bone 0 with (0,0,0,1) / zero /
			// one unconditionally, and `sub_821F57D0` does it again before composing model
			// space; the root's own sampled transform is parked at comp19+48 / comp6+64 and read
			// by nothing (whole-binary search, 2026-09-05). Every other bone keeps its sampled
			// LOCAL transform. Keeping the root's rotation here, as this once did, turned or
			// tilted a whole rig by a track the guest never applies.
			bool isRoot = _parents![b] < 0;
			int rt = built.RotationTrack[b];
			if (rt >= 0 && !isRoot)
			{
				Quaternion q = SampleRotation(built, b, rt, time);
				if (additive)
				{
					Quaternion first = SampleRotation(built, b, rt, 0f);
					Quaternion delta = q * first.Inverse();
					rotation[b] = rotationWeight[b] > 0f
						? (Quaternion.Identity.Slerp(delta, weight) * rotation[b]).Normalized()
						: rotation[b];
				}
				else
				{
					Accumulate(ref rotation[b], ref rotationWeight[b], q, weight);
				}
			}

			// NO ROOT TRANSLATION EITHER — the same rule; sampled here too it walked the body
			// away from its placement each loop and snapped it back at the wrap.
			int pt = built.PositionTrack[b];
			if (pt >= 0 && !additive && !isRoot)
			{
				Vector3 p = SamplePosition(built, b, pt, time);
				position[b] = positionWeight[b] > 0f ? position[b] + p * weight : p * weight;
				positionWeight[b] += weight;
			}
		}
	}

	/// <summary>
	/// The per-bone bytes of an overlay's mask node, by guest bone index; empty for a whole-body
	/// overlay. Cached by record uid AND node address: the node lives as long as the record, and
	/// a recycled address under a new uid is a different mask.
	/// </summary>
	private byte[] Mask(uint uid, uint maskNode)
	{
		if (maskNode == 0)
		{
			return System.Array.Empty<byte>();
		}

		ulong key = ((ulong)uid << 32) | maskNode;
		if (!_masks.TryGetValue(key, out byte[]? bytes))
		{
			bytes = HostBridge.GetAnimMask(maskNode, out _);
			_masks[key] = bytes;
			if (_masks.Count > 64)
			{
				_masks.Clear();
				_masks[key] = bytes;
			}
		}

		return bytes;
	}

	/// <summary>Seconds between re-reads of the morph record; it moves with the character, slowly.</summary>
	private const double MorphRefreshSeconds = 1.0;

	private double _morphReadAt = double.NegativeInfinity;

	private Vector3[] _morphOffset = System.Array.Empty<Vector3>();

	private Vector3[] _morphScale = System.Array.Empty<Vector3>();

	private Vector3[] _finalPosition = System.Array.Empty<Vector3>();

	/// <summary>The pose being assembled this frame; written to the skeleton once per bone at the end.</summary>
	private Quaternion[] _finalRotation = System.Array.Empty<Quaternion>();

	private Vector3[] _finalScale = System.Array.Empty<Vector3>();

	private bool _hasMorph;

	/// <summary>
	/// THE BODY'S PROPORTIONS, after the fold: the guest's `sub_822BF9B8` adds each morphed
	/// bone's offset (pre-divided by the parent's scale) and multiplies its scale, and its
	/// model-space compose carries NO scale — a bone's scale is its own, never inherited —
	/// while `sub_821E5AC0` scales each child's translation by the parent's. Godot inherits
	/// scale down the chain, which gives the translation scaling for free and would also scale
	/// every descendant's geometry; dividing each bone's scale by its parent's leaves every
	/// bone at exactly its own scale, the guest's result.
	/// </summary>
	/// <summary>Ops applied on the last frame, for the readout.</summary>
	public int PostOpsApplied { get; private set; }

	/// <summary>
	/// THE GUEST'S AIM AND LOOK, after the fold. `sub_822DC980` ends every node's evaluation
	/// by rewriting the skeleton's aim chain (5 bones) and look chain (6 bones) from the
	/// per-bone 4-vectors its IK stated on the node's records (+8 aim, +12 look), through
	/// `sub_822D9738`: with q the bone's folded local rotation and t the stated vector, q is
	/// negated when q·t &lt; 0, then by the record's mode — 1: q + w·t, 2: t + w·(q − t),
	/// 3: q + w·(t − q), 4: q + w·(q⊗t − q), otherwise t — and the result normalised (vmaddfp is A×C+B).
	/// Component-wise, so the Y-up conjugation commutes with it and the host applies the same
	/// arithmetic to its own local pose. Only the strongest record's ops count per chain: the
	/// guest applies each record's inside that record's evaluation, and stacking two here
	/// would double a head turn during a crossfade.
	/// </summary>
	private void ApplyPostOps()
	{
		PostOpsApplied = 0;
		HostBridge.GuestPostOps? ops = HostBridge.GetAnimPostOps(_entity);
		if (ops is not { } post || post.Ops.Count == 0)
		{
			return;
		}

		for (int kind = 0; kind < 2; kind++)
		{
			HostBridge.GuestPostOp? best = null;
			foreach (HostBridge.GuestPostOp op in post.Ops)
			{
				if (op.Kind == kind && op.RecordWeight > 1e-4f &&
					(best is null || op.RecordWeight > best.Value.RecordWeight))
				{
					best = op;
				}
			}

			if (best is not { } chosen || chosen.Mode == 0)
			{
				continue;
			}

			int[] chain = kind == 0 ? post.AimBones : post.LookBones;
			float w = chosen.Weight;
			for (int i = 0; i < chain.Length && i < chosen.Targets.Length; i++)
			{
				int guest = chain[i];
				int b = guest >= 0 && guest < _guestToModel.Length ? _guestToModel[guest] : -1;
				if (b < 0)
				{
					continue;
				}

				Quaternion t = chosen.Targets[i];
				if (!t.IsFinite() || t.LengthSquared() < 1e-8f)
				{
					continue;
				}

				t = Engine_AnimSkeleton.ToYUp(t);
				Quaternion q = _finalRotation[b];
				if (q.Dot(t) < 0f)
				{
					q = -q;
				}

				// Mode 4's helper (`sub_82233200`) is a quaternion product: the stated vector is a
				// DELTA applied in the bone's own frame, and the pose moves toward q⊗t by w.
				Quaternion r = chosen.Mode switch
				{
					1 => q + t * w,
					2 => t + (q - t) * w,
					3 => q + (t - q) * w,
					4 => q + (q * t - q) * w,
					_ => t,
				};
				if (!r.IsFinite() || r.LengthSquared() < 1e-8f)
				{
					continue;
				}

				_finalRotation[b] = r.Normalized();
				PostOpsApplied++;
			}
		}
	}

	private void ApplyMorph(int bones)
	{
		double now = Time.GetTicksMsec() / 1000.0;
		if (now - _morphReadAt >= MorphRefreshSeconds)
		{
			_morphReadAt = now;
			if (_morphOffset.Length != bones)
			{
				_morphOffset = new Vector3[bones];
				_morphScale = new Vector3[bones];
			}

			System.Array.Fill(_morphOffset, Vector3.Zero);
			System.Array.Fill(_morphScale, Vector3.One);
			_hasMorph = false;
			_morphHeight = HostBridge.GetBoneMorphHeight(_entity);
			_morphHeightBone = -1;
			foreach (HostBridge.GuestBoneMorph m in HostBridge.GetBoneMorph(_entity))
			{
				int model = m.Bone >= 0 && m.Bone < _guestToModel.Length ? _guestToModel[m.Bone] : -1;
				if (model < 0 || model >= bones)
				{
					continue;
				}

				if (m.Bone == 1 && _morphHeight > 0f)
				{
					_morphHeightBone = model;
					_hasMorph = true;
				}

				_morphOffset[model] = Engine_AnimSkeleton.ToYUp(m.Offset);
				_morphScale[model] = Engine_AnimSkeleton.ToYUp(m.Scale).Abs();
				if (m.Offset.LengthSquared() > 1e-10f || (m.Scale - Vector3.One).LengthSquared() > 1e-10f)
				{
					_hasMorph = true;
				}
			}
		}

		if (!_hasMorph)
		{
			System.Array.Fill(_finalScale, Vector3.One);
			_morphWasApplied = false;
			return;
		}

		for (int b = 0; b < bones; ++b)
		{
			int parent = _parents![b];
			Vector3 parentScale = parent >= 0 ? _morphScale[parent] : Vector3.One;
			Vector3 offset = _morphOffset[b];
			if (b == _morphHeightBone)
			{
				// Guest (t.x*h, t.y*h, t.z + off.z); its z is up, which is Y here.
				Vector3 t = _finalPosition[b];
				_finalPosition[b] = new Vector3(t.X * _morphHeight, t.Y + offset.Y, t.Z * _morphHeight);
			}
			else if (parent >= 0 && offset != Vector3.Zero)
			{
				_finalPosition[b] += offset / parentScale;
			}

			_finalScale[b] = _morphScale[b] / parentScale;
		}

		_morphWasApplied = true;
	}

	private bool _morphWasApplied;

	/// <summary>The record's height factor (rec+36) and the model bone it rewrites, -1 for none.</summary>
	private float _morphHeight;

	private int _morphHeightBone = -1;

	private uint _entity;

	/// <summary>Guest bone index → model bone, the inverse of the map the rig was built with.</summary>
	private int[] _guestToModel = System.Array.Empty<int>();

	/// <summary>A bone's current accumulated pose as a local transform (rest where unwritten).</summary>
	private Transform3D PoseOf(int b)
	{
		Quaternion q = _rotationWeight[b] > 0f ? _rotation[b].Normalized() : _restRotation[b];
		Vector3 p = _positionWeight[b] > 1e-5f ? _position[b] / _positionWeight[b] : _restPosition[b];
		return new Transform3D(new Basis(q), p);
	}

	private static void Accumulate(ref Quaternion sum, ref float sumWeight, Quaternion q, float weight)
	{
		if (sumWeight <= 0f)
		{
			sum = q;
			sumWeight = weight;
			return;
		}

		// Weighted nlerp with hemisphere alignment: enough for the small angles between the
		// clips a tree blends at once, and stable for any count of them.
		Quaternion aligned = sum.Dot(q) < 0f ? new Quaternion(-q.X, -q.Y, -q.Z, -q.W) : q;
		float total = sumWeight + weight;
		float t = weight / total;
		sum = new Quaternion(
			sum.X + (aligned.X - sum.X) * t,
			sum.Y + (aligned.Y - sum.Y) * t,
			sum.Z + (aligned.Z - sum.Z) * t,
			sum.W + (aligned.W - sum.W) * t);
		sumWeight = total;
	}

	/// <summary>
	/// Drops the written-pose cache, so the next pose writes every bone. Must be called
	/// whenever something else writes this skeleton — the pose feed takes over whenever
	/// <see cref="Apply"/> answers false — or the cache would claim bones the feed has since
	/// overwritten are still ours, and the rig would keep the feed's pose in those bones.
	/// </summary>
	public void ForgetWrittenPose()
	{
		// Another writer (the feed) is about to pose the body; the shadow's next mirror is a full one.
		_hasWritten = false;
		_shadowStale = true;
	}

	/// <summary>The last pose written per bone, so an unchanged bone is not written again.</summary>
	private Transform3D[] _written = System.Array.Empty<Transform3D>();

	private bool _hasWritten;

	/// <summary>Bones actually written on the last pose; reported.</summary>
	public int LastMovedBones { get; private set; }

	/// <summary>Bone writes skipped because the pose was unchanged; reported.</summary>
	public static long SkippedBoneWrites;

	/// <summary>Bone writes actually made, and how many skeletons they touched. Against
	/// <see cref="SkippedBoneWrites"/> these say whether the frustum gate is doing anything in
	/// a given scene, and whether dirty skeletons are what `frameSetupCpuMs` is spending on.</summary>
	public static long BoneWrites;

	public static long SkeletonsWritten;

	/// <summary>How far short of a one-shot clip's end its playhead may rest: one sample under
	/// the last key, so the sample never lands on the wrap.</summary>
	private const float EndMargin = 1e-3f;

	/// <summary>The most a clip may advance in one tick before it is read as re-timed: four ticks' worth.</summary>
	private const float RestartSeconds = 4f / 15f;

	/// <summary>How far the guest's stated previous time may sit from the value we last saw and
	/// still be the same playback: a float's worth of rounding, not a tolerance.</summary>
	private const float SeamSeconds = 1e-3f;

	/// <summary>
	/// Folds a tick's stated clip time into the channel's playhead timeline. The clip time
	/// advances by `worldScale × entityScale / 15` per game tick through the clip's warp table
	/// (`sub_82200F60`), so consecutive statements differ by a small non-negative step, unwrapped
	/// across a loop; a step outside that is a restart or a re-time and starts the timeline over
	/// at the new value — a hard cut, which is what the guest's fresh pair draws too. The first
	/// sight seeds the previous tick from the leaf's own previous time.
	/// </summary>
	private static void ObservePlayhead(Channel c, HostBridge.GuestAnimLeaf leaf, uint tick)
	{
		float length = c.Built.Length;
		if (c.PlayheadTick == uint.MaxValue)
		{
			c.PlayheadTick = tick;
			c.LastStated = leaf.Time;
			c.Unwrapped = leaf.Time;
			float back = length > 0f
				? (leaf.Loop ? LoopGap(leaf.Time - leaf.PreviousTime, length) : leaf.Time - leaf.PreviousTime)
				: 0f;
			if (back > 0f && back < RestartSeconds)
			{
				c.Playhead.Reset(tick - 1, leaf.Time - back);
				c.Playhead.Push(tick, leaf.Time);
			}
			else
			{
				c.Playhead.Reset(tick, leaf.Time);
			}

			return;
		}

		if (tick == c.PlayheadTick)
		{
			return;
		}

		uint elapsed = tick - c.PlayheadTick;
		c.PlayheadTick = tick;

		if (leaf.Network && length > 0f)
		{
			ObserveNetworkPlayhead(c, leaf, tick, elapsed, length);
			return;
		}

		// The guest states its own continuity; do not infer it from a threshold.
		// `sub_82200F60` shifts the current time into the leaf's +28 and writes the new one at
		// +32, so the stated previous time is by construction continuous with the current one.
		// Its twin `sub_822B4970` writes both slots the same value: a hard cut, no interpolation.
		float stated = length > 0f
			? (leaf.Loop ? LoopGap(leaf.Time - leaf.PreviousTime, length) : leaf.Time - leaf.PreviousTime)
			: 0f;

		// And whether the guest's own previous value is the one we last saw: if it is not, the
		// clip was re-timed between our observations and there is nothing to carry forward.
		float seam = length > 0f
			? (leaf.Loop ? LoopGap(leaf.PreviousTime - c.LastStated, length) : leaf.PreviousTime - c.LastStated)
			: 0f;

		bool cut = leaf.PreviousTime == leaf.Time;
		bool joins = Mathf.Abs(seam) <= SeamSeconds && stated >= -SeamSeconds && stated < RestartSeconds;
		c.LastStated = leaf.Time;
		if (length <= 0f || cut || !joins)
		{
			c.Unwrapped = leaf.Time;
			c.Playhead.Reset(tick, leaf.Time);
			return;
		}

		c.Unwrapped += seam + stated;
		c.Playhead.Push(tick, c.Unwrapped);
	}

	/// <summary>
	/// The guest's transform playback delay for a replica: `sub_8239E9E0` stamps each received
	/// sample `arrival + flt_8209B650` (0.1 s) and the replica tick plays it then.
	/// </summary>
	private const double NetworkPlaybackDelayUsec = 100_000.0;

	/// <summary>Leaf sets of a replica rig, newest last, with the frame instant each was read.</summary>
	private readonly List<(ulong Usec, List<HostBridge.GuestAnimLeaf> Leaves)> _networkHistory = new();

	private readonly Stack<List<HostBridge.GuestAnimLeaf>> _spareLeafLists = new();

	/// <summary>
	/// ONE TIMELINE FOR A REPLICA'S BODY AND ITS LIMBS. The guest times the two streams from their
	/// own arrivals and neither carries a sender time: the animation tree (messages 174/175) plays
	/// from its receipt (`sub_825657C8` stores the arrival at comp19+164, `sub_82563B78` samples
	/// from it), the transform (messages 5/6) from arrival + 0.1 s (`sub_8239E9E0`). So the limbs
	/// ran a tenth of a second ahead of the body: measured 2026-10-08, the idle clip began while
	/// player two's replica glided on for 0.45 s — feet sliding. A modern client plays both from
	/// one buffered timeline; here the replica's leaf sets are held back by the same 0.1 s, so the
	/// clip changes land when the body does. Only a rig whose leaves are network-sampled is
	/// delayed; everything the guest animates locally is drawn as read.
	/// </summary>
	private int DelayNetworkLeaves(int count)
	{
		bool network = false;
		foreach (HostBridge.GuestAnimLeaf leaf in _leaves)
		{
			if (leaf.Network)
			{
				network = true;
				break;
			}
		}

		if (!network)
		{
			foreach ((ulong _, List<HostBridge.GuestAnimLeaf> spare) in _networkHistory)
			{
				_spareLeafLists.Push(spare);
			}

			_networkHistory.Clear();
			return count;
		}

		ulong now = Refii.Entities.Engine_DrawnFrame.NowUsec;
		List<HostBridge.GuestAnimLeaf> copy = _spareLeafLists.Count > 0 ? _spareLeafLists.Pop() : new();
		copy.Clear();
		copy.AddRange(_leaves);
		_networkHistory.Add((now, copy));

		// The newest set read at least the delay ago; until one exists, the oldest held.
		double due = now - NetworkPlaybackDelayUsec;
		int pick = 0;
		for (int i = _networkHistory.Count - 1; i >= 0; --i)
		{
			if (_networkHistory[i].Usec <= due)
			{
				pick = i;
				break;
			}
		}

		// Everything older than the pick is spent.
		for (int i = 0; i < pick; ++i)
		{
			_spareLeafLists.Push(_networkHistory[i].Leaves);
		}

		_networkHistory.RemoveRange(0, pick);
		_leaves.Clear();
		_leaves.AddRange(_networkHistory[0].Leaves);
		return _leaves.Count;
	}

	/// <summary>The share of a network clip's drift closed per tick.</summary>
	private const float NetworkConvergence = 0.25f;

	/// <summary>
	/// A replica's clip (<see cref="HostBridge.GuestAnimLeaf.Network"/>): the time is the
	/// sender's packet extrapolated by the receipt alpha, so consecutive ticks differ by the
	/// clip's step plus the jitter of the packet's arrival and of the read. That is not a
	/// re-time, and cutting on it drew the replica once per tick (2026-10-08: a host-side watch
	/// of player two moved its bones on one frame in eleven). The timeline here advances by the
	/// stated step per tick and closes a quarter of the gap to the stated time each tick; only a
	/// gap past the restart bound — a new clip position, not drift — cuts.
	/// </summary>
	private static void ObserveNetworkPlayhead(Channel c, HostBridge.GuestAnimLeaf leaf, uint tick,
		uint elapsed, float length)
	{
		float step = leaf.Loop ? LoopGap(leaf.Time - leaf.PreviousTime, length) : leaf.Time - leaf.PreviousTime;
		c.LastStated = leaf.Time;
		if (!(step >= 0f) || step >= RestartSeconds || elapsed == 0u || elapsed > 8u)
		{
			c.Unwrapped = leaf.Time;
			c.Playhead.Reset(tick, leaf.Time);
			return;
		}

		double predicted = c.Unwrapped + (double)step * elapsed;
		float gap = (float)(leaf.Time - predicted);
		float error = leaf.Loop ? LoopGap(gap, length) : gap;
		if (Mathf.Abs(error) >= RestartSeconds)
		{
			c.Unwrapped = leaf.Time;
			c.Playhead.Reset(tick, leaf.Time);
			return;
		}

		c.Unwrapped = predicted + error * NetworkConvergence;
		c.Playhead.Push(tick, c.Unwrapped);
	}

	/// <summary>The clip time to sample now: the playhead timeline at the draw point, wrapped
	/// for a looping clip and clamped short of the end otherwise.</summary>
	private static void Advance(Channel c, double nowUsec)
	{
		float length = c.Built.Length;
		if (length <= 0f)
		{
			c.Time = 0f;
			return;
		}

		double t = c.Playhead.Sample(nowUsec);
		if (c.Leaf.Loop)
		{
			t %= length;
			if (t < 0.0)
			{
				t += length;
			}

			c.Time = (float)t;
			return;
		}

		c.Time = Mathf.Clamp((float)t, 0f, length - EndMargin);
	}

	/// <summary>The signed shortest distance between two times on a loop of the given length.</summary>
	private static float LoopGap(float gap, float length)
	{
		gap %= length;
		if (gap > length * 0.5f)
		{
			gap -= length;
		}
		else if (gap < -length * 0.5f)
		{
			gap += length;
		}

		return gap;
	}

	/// <summary>Built clips of this rig with their track coverage, for the debug channel.</summary>
	public List<Dictionary<string, object?>> DescribeClips()
	{
		var rows = new List<Dictionary<string, object?>>();
		foreach ((uint key, Built built) in _clips)
		{
			int found = 0;
			foreach (int t in built.RotationTrack)
			{
				if (t >= 0) ++found;
			}

			rows.Add(new Dictionary<string, object?>
			{
				["key"] = $"{key:X8}",
				["failed"] = built.Failed,
				["tracks"] = built.Failed ? 0 : built.Animation?.GetTrackCount() ?? -1,
				["shared"] = built.Animation is null,
				["bonesWithRotation"] = found,
				["bones"] = _skeleton.GetBoneCount(),
				["firstTrackPath"] = built.Animation is { } a && a.GetTrackCount() > 0 ? a.TrackGetPath(0).ToString() : null,
				["skeletonName"] = _skeletonName,
				["length"] = built.Length,
			});
		}

		return rows;
	}

	/// <summary>The rig's leaves as the guest states them, for the debug channel.</summary>
	public List<Dictionary<string, object?>> Describe(uint entity)
	{
		var rows = new List<Dictionary<string, object?>>();
		HostBridge.GetEntityAnimState(entity, _leaves, out uint flags);
		foreach (HostBridge.GuestAnimLeaf leaf in _leaves)
		{
			AnimClip? clip = Engine_AnimCatalog.Find(leaf.Key);
			rows.Add(new Dictionary<string, object?>
			{
				["clip"] = clip?.Name ?? $"{leaf.Key:X8}",
				["time"] = leaf.Time,
				["prev"] = leaf.PreviousTime,
				["weight"] = leaf.Weight,
				["loop"] = leaf.Loop,
				["additive"] = leaf.Additive,
				["priority"] = leaf.Priority,
				["uid"] = leaf.Uid,
				["playhead"] = _channels.TryGetValue(new ChannelKey(leaf.Uid, leaf.Key, 0), out Channel? ch) ? ch.Time : null,
				["hostWeight"] = ch?.Weight,
				["target"] = ch?.TargetWeight,
				["overlay"] = leaf.Overlay,
				["mask"] = $"{leaf.Mask:X8}",
				["maskBytes"] = leaf.Mask != 0 ? Mask(leaf.Uid, leaf.Mask).Length : 0,
				["prioMask"] = $"{leaf.PriorityMask:X}",
				["track"] = leaf.Track,
				["blendIn"] = leaf.BlendIn,
				["blendOut"] = leaf.BlendOut,
				["driven"] = Driven,
				["lastFallback"] = LastFallback,
				["length"] = clip?.DurationSeconds,
			});
		}

		return rows;
	}

	private Built? Ensure(uint key)
	{
		if (_clips.TryGetValue(key, out Built? built))
		{
			// The A/B lever turned off after a shared build: that build has no resource to read.
			if (ManagedClipSampling || built.Failed || built.Animation is not null)
			{
				return built.Failed ? null : built;
			}
		}

		built = ManagedClipSampling ? BuildShared(key) : BuildFromResource(key);
		_clips[key] = built;
		return built.Failed ? null : built;
	}

	/// <summary>This rig's view of a shared clip: its bones pointed at the clip's tracks.</summary>
	/// <remarks>
	/// The same assignment <see cref="Engine_AnimBuilder.Build"/> + <c>FindTrack</c> made: a bone
	/// takes the FIRST clip track mapped to it, and only a bone with a parent takes translation.
	/// </remarks>
	private Built BuildShared(uint key)
	{
		var built = new Built { KeysLifted = true };
		AnimClip? clip = Engine_AnimCatalog.Find(key);
		SharedClip? shared = clip is null ? null : SharedFor(clip, key);
		if (clip is null || shared is null)
		{
			built.Failed = true;
			return built;
		}

		_boneCandidates ??= Engine_AnimBuilder.BoneCandidates(_skeleton);
		int[] map = Engine_AnimBuilder.MapTracks(clip, _skeleton, _boneCandidates);
		if (map.Length == 0)
		{
			built.Failed = true;
			return built;
		}

		int bones = _skeleton.GetBoneCount();
		built.Length = shared.Length;
		built.RotationTrack = new int[bones];
		built.PositionTrack = new int[bones];
		built.RotationKeys = new Keys[bones];
		built.PositionKeys = new Keys[bones];
		System.Array.Fill(built.RotationTrack, -1);
		System.Array.Fill(built.PositionTrack, -1);
		for (int track = 0; track < map.Length; ++track)
		{
			int b = map[track];
			if (b < 0 || b >= bones || built.RotationTrack[b] >= 0)
			{
				continue;
			}

			built.RotationTrack[b] = track;
			built.RotationKeys[b] = shared.Rotations[track];
			if (_skeleton.GetBoneParent(b) >= 0)
			{
				built.PositionTrack[b] = track;
				built.PositionKeys[b] = shared.Positions[track];
			}
		}

		return built;
	}

	private static SharedClip? SharedFor(AnimClip clip, uint key)
	{
		var id = new SharedKey(Engine_AnimCatalog.ActiveDecoder, key);
		if (SharedClips.TryGetValue(id, out SharedClip? shared))
		{
			if (shared is not null)
			{
				shared.LastTaken = System.Environment.TickCount64;
			}

			return shared;
		}

		if (SharedClips.Count >= SharedClipCap)
		{
			TrimSharedClips();
		}

		Engine_AnimBuilder.DecodedKeys? decoded = Engine_AnimBuilder.DecodeKeys(clip, id.Decoder);
		if (decoded is not null)
		{
			int tracks = decoded.Times.Length;
			shared = new SharedClip
			{
				Length = decoded.Length,
				Rotations = new Keys[tracks],
				Positions = new Keys[tracks],
				LastTaken = System.Environment.TickCount64,
			};
			for (int i = 0; i < tracks; ++i)
			{
				shared.Rotations[i] = new Keys { Times = decoded.Times[i], Rotations = decoded.Rotations[i] };
				shared.Positions[i] = new Keys { Times = decoded.Times[i], Positions = decoded.Positions[i] };
			}
		}

		// A decode failure is the clip's, not the rig's: it is cached too.
		SharedClips[id] = shared;
		return shared;
	}

	/// <summary>The per-rig resource path, kept for <see cref="ManagedClipSampling"/> = false.</summary>
	private Built BuildFromResource(uint key)
	{
		var built = new Built();
		AnimClip? clip = Engine_AnimCatalog.Find(key);
		Animation? animation = clip is null
			? null
			: Engine_AnimBuilder.Build(clip, Engine_AnimCatalog.ActiveDecoder, _skeleton, _skeletonName);
		if (animation is null)
		{
			built.Failed = true;
			return built;
		}

		int bones = _skeleton.GetBoneCount();
		built.Animation = animation;
		built.Length = (float)animation.Length;
		built.RotationTrack = new int[bones];
		built.PositionTrack = new int[bones];
		for (int b = 0; b < bones; ++b)
		{
			string path = $"{_skeletonName}:{_skeleton.GetBoneName(b)}";
			built.RotationTrack[b] = animation.FindTrack(path, Animation.TrackType.Rotation3D);
			built.PositionTrack[b] = animation.FindTrack(path, Animation.TrackType.Position3D);
		}

		LiftKeys(built);
		return built;
	}

	/// <summary>Drops the least recently taken half; rigs holding a dropped clip keep it.</summary>
	private static void TrimSharedClips()
	{
		var stamps = new List<long>(SharedClips.Count);
		foreach (SharedClip? c in SharedClips.Values)
		{
			stamps.Add(c?.LastTaken ?? 0);
		}

		stamps.Sort();
		long cut = stamps[stamps.Count / 2];
		var drop = new List<SharedKey>();
		foreach ((SharedKey k, SharedClip? c) in SharedClips)
		{
			if ((c?.LastTaken ?? 0) <= cut)
			{
				drop.Add(k);
			}
		}

		foreach (SharedKey k in drop)
		{
			SharedClips.Remove(k);
		}
	}
}
