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
		public Animation Animation = null!;
		public int[] RotationTrack = System.Array.Empty<int>();
		public int[] PositionTrack = System.Array.Empty<int>();
		public float Length;
		public bool Failed;
	}

	/// <summary>One clip playing on this rig, run on the host clock. The guest's leaf is the
	/// EVENT and the TARGET: it starts the channel, states the weight the channel eases toward
	/// at the record's blend rate, and its stated time only restarts the playhead when far off.
	/// A leaf that vanishes leaves its channel ramping out before it is dropped.</summary>
	private sealed class Channel
	{
		public HostBridge.GuestAnimLeaf Leaf;
		public Built Built = null!;
		public float StatedTime;
		public float Time;
		public float Weight;
		public float TargetWeight;
		public float BlendIn = 0.2f;
		public float BlendOut = 0.2f;
		public ulong LastUsec;
		public bool Seen;

		/// <summary>Position in this tick's export, front to back: the guest's list order.</summary>
		public int Order;
	}

	private readonly Skeleton3D _skeleton;
	private readonly string _skeletonName;
	private readonly Dictionary<uint, Built> _clips = new();
	private readonly Dictionary<ulong, Channel> _channels = new();
	private readonly List<ulong> _dropped = new();
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
		// Only a procedural layer the clips cannot express (dog paw/look IK, component 97)
		// keeps the pose feed; the byte at +576 reads 1 on ordinary rigs and means nothing here.
		if ((flags & 2) != 0)
		{
			++FallbackProcedural;
			LastFallback = "procedural";
			return false;
		}

		// A RAGDOLL IS THE GUEST'S POSE. Flag 8 is the spatial component's physics-driven bit
		// (comp3+144 & 4): Havok writes the finished pose into comp6+44 and the record list is
		// no longer what the body does. The pose feed carries that pose; folding clips over it
		// is what sent an electrocuted bandit's body flying apart (2026-09-05).
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

		ulong now = Godot.Time.GetTicksUsec();
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
		foreach (HostBridge.GuestAnimLeaf leaf in _leaves)
		{
			++order;
			Built? built = Ensure(leaf.Key);
			if (built is null)
			{
				++FallbackUnknownClip;
				LastFallback = $"unknown clip {leaf.Key:X8}";
				return false;
			}

			ulong key = PlayheadKey(leaf);
			if (!_channels.TryGetValue(key, out Channel? channel))
			{
				channel = new Channel
				{
					Leaf = leaf, Built = built, StatedTime = leaf.Time, Time = leaf.Time,
					Weight = 0f, LastUsec = now,
				};
				_channels[key] = channel;
			}

			// TWO LEAVES OF ONE CLIP UNDER ONE RECORD share this channel — a record crossfading
			// a clip into itself (WalkFootstep into WalkFootstep on a re-trigger) states two
			// playheads with weights that sum to the record's. Taking the last leaf's weight
			// alone put a 0 on the channel whenever the fading-out playhead was listed second,
			// and the whole body eased to rest for the length of the fade.
			if (channel.Seen)
			{
				channel.TargetWeight += leaf.Weight;
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
			if (leaf.BlendIn > 0f) channel.BlendIn = leaf.BlendIn;
			if (leaf.BlendOut > 0f) channel.BlendOut = leaf.BlendOut;
		}

		// THE TIMELINE, ON THE HOST CLOCK: weights ease at the blend rates, vanished channels
		// ramp out and drop, playheads advance and only a restart re-syncs them.
		_dropped.Clear();
		float totalWeight = 0f;
		foreach ((ulong key, Channel c) in _channels)
		{
			float dt = Mathf.Min((now - c.LastUsec) * 1e-6f, 0.25f);
			c.LastUsec = now;
			float target = c.Seen ? c.TargetWeight : 0f;
			float rate = target > c.Weight ? 1f / Mathf.Max(c.BlendIn, 0.02f) : 1f / Mathf.Max(c.BlendOut, 0.02f);
			c.Weight = Mathf.MoveToward(c.Weight, target, rate * dt);
			if (!c.Seen && c.Weight <= 1e-4f)
			{
				_dropped.Add(key);
				continue;
			}

			Advance(c, dt);
			if (!c.Leaf.Additive)
			{
				totalWeight += c.Weight;
			}
		}

		foreach (ulong key in _dropped)
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
		foreach (Channel c in _channels.Values)
		{
			if (c.Leaf.Additive || c.Leaf.Overlay || c.Weight <= 1e-5f)
			{
				continue;
			}

			_recordWeight[c.Leaf.Uid] = _recordWeight.GetValueOrDefault(c.Leaf.Uid) + c.Weight;
		}

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

		uint runnerUid = 0;
		float runnerWeight = 0f;
		foreach ((uint uid, float w) in _recordWeight)
		{
			if (uid != topUid && w > runnerWeight)
			{
				runnerUid = uid;
				runnerWeight = w;
			}
		}

		float topWeight = haveTop ? Mathf.Clamp(_recordWeight.GetValueOrDefault(topUid), 0f, 1f) : 0f;
		float topShare = runnerWeight > 1e-5f ? topWeight : 1f;
		if (topShare >= 0.98333f) topShare = 1f;
		if (topShare <= 0.016667f) topShare = 0f;
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
			_skeleton.SetBonePoseRotation(b, rotation);
			_skeleton.SetBonePosePosition(b, position);
			_finalPosition[b] = position;
		}

		ApplyMorph(bones);
		ApplyPostOps();

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
						$" seen {c.Seen} tracks {c.Built.Animation.GetTrackCount()}]");
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
				Quaternion q = built.Animation.RotationTrackInterpolate(rt, time);
				if (additive)
				{
					Quaternion first = built.Animation.RotationTrackInterpolate(rt, 0f);
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
				Vector3 p = built.Animation.PositionTrackInterpolate(pt, time);
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
				Quaternion q = _skeleton.GetBonePoseRotation(b);
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

				_skeleton.SetBonePoseRotation(b, r.Normalized());
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
			foreach (HostBridge.GuestBoneMorph m in HostBridge.GetBoneMorph(_entity))
			{
				int model = m.Bone >= 0 && m.Bone < _guestToModel.Length ? _guestToModel[m.Bone] : -1;
				if (model < 0 || model >= bones)
				{
					continue;
				}

				_morphOffset[model] = Engine_AnimSkeleton.ToYUp(m.Offset);
				_morphScale[model] = Engine_AnimSkeleton.ToYUp(m.Scale).Abs();
				if (m.Offset.LengthSquared() > 1e-10f || (m.Scale - Vector3.One).LengthSquared() > 1e-10f)
				{
					_hasMorph = true;
				}
			}
		}

		if (!_hasMorph && !_morphWasApplied)
		{
			return;
		}

		for (int b = 0; b < bones; ++b)
		{
			int parent = _parents![b];
			Vector3 parentScale = parent >= 0 && _hasMorph ? _morphScale[parent] : Vector3.One;
			Vector3 own = _hasMorph ? _morphScale[b] : Vector3.One;
			Vector3 offset = _hasMorph ? _morphOffset[b] : Vector3.Zero;
			if (parent >= 0 && offset != Vector3.Zero)
			{
				_skeleton.SetBonePosePosition(b, _finalPosition[b] + offset / parentScale);
			}

			_skeleton.SetBonePoseScale(b, own / parentScale);
		}

		_morphWasApplied = _hasMorph;
	}

	private bool _morphWasApplied;

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

	/// <summary>The clip time to sample: the guest's stated time, carried forward on the host
	/// clock since it was stated, wrapped for a looping clip and clamped otherwise.</summary>
	/// <summary>
	/// The playhead runs on the host clock from the moment the channel starts. The guest's
	/// stated time is checked only when it changes: within a third of a second of the playhead
	/// it is ignored (its 15 Hz stepping is not a signal), further away the clip has restarted
	/// or been re-timed and the playhead jumps to it.
	/// </summary>
	/// <summary>How far short of a one-shot clip's end its playhead may rest: one sample under
	/// the last key, so the sample never lands on the wrap.</summary>
	private const float EndMargin = 1e-3f;

	private static void Advance(Channel c, float dt)
	{
		float length = c.Built.Length;
		if (length <= 0f)
		{
			c.Time = 0f;
			return;
		}

		HostBridge.GuestAnimLeaf leaf = c.Leaf;

		// AT THE GUEST'S RATE. Clip time advances once per 15 Hz game tick by
		// worldScale × entityScale / 15 s, then through the clip's own warp table
		// (`sub_82200F60`: node+28 = warp(old), node+32 = warp(new)), so (current − previous) × 15
		// IS the rate the guest is playing this clip at — 1 at normal speed, a fraction in the
		// combat slow-motion the crescendo scale sets, 0 while paused. A playhead run on the host
		// clock alone raced ahead through slow-motion and snapped back at the resync.
		float rate = 1f;
		if (leaf.Loop)
		{
			float delta = LoopGap(leaf.Time - leaf.PreviousTime, length);
			if (delta >= 0f && delta * 15f < 4f) rate = delta * 15f;
		}
		else
		{
			float delta = leaf.Time - leaf.PreviousTime;
			if (delta >= 0f && delta * 15f < 4f) rate = delta * 15f;
		}

		c.Time += dt * rate;

		// A ONE-SHOT THE GUEST HAS STOPPED sits exactly where the guest left it: the rate is 0
		// at the end (or while paused), and a playhead a few frames behind would otherwise hold
		// short of the final pose until the resync threshold.
		if (rate <= 0f && !leaf.Loop)
		{
			c.Time = leaf.Time;
		}

		if (leaf.Time != c.StatedTime)
		{
			c.StatedTime = leaf.Time;
			float gap = leaf.Loop ? LoopGap(leaf.Time - c.Time, length) : leaf.Time - c.Time;
			if (Mathf.Abs(gap) > 0.33f)
			{
				c.Time = leaf.Time;
			}
		}

		if (leaf.Loop)
		{
			c.Time %= length;
			if (c.Time < 0f)
			{
				c.Time += length;
			}

			return;
		}

		c.Time = Mathf.Clamp(c.Time, 0f, length - EndMargin);
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
				["tracks"] = built.Failed ? 0 : built.Animation.GetTrackCount(),
				["bonesWithRotation"] = found,
				["bones"] = _skeleton.GetBoneCount(),
				["firstTrackPath"] = !built.Failed && built.Animation.GetTrackCount() > 0 ? built.Animation.TrackGetPath(0).ToString() : null,
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
				["playhead"] = _channels.TryGetValue(PlayheadKey(leaf), out Channel? ch) ? ch.Time : null,
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
			return built.Failed ? null : built;
		}

		built = new Built();
		_clips[key] = built;
		AnimClip? clip = Engine_AnimCatalog.Find(key);
		Animation? animation = clip is null
			? null
			: Engine_AnimBuilder.Build(clip, Engine_AnimCatalog.ActiveDecoder, _skeleton, _skeletonName);
		if (animation is null)
		{
			built.Failed = true;
			return null;
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

		return built;
	}
}
