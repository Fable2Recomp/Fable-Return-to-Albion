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

	private readonly int[] _modelToGuest;
	private readonly Dictionary<ulong, byte[]> _masks = new();
	private Quaternion[] _layerRotation = System.Array.Empty<Quaternion>();
	private Vector3[] _layerPosition = System.Array.Empty<Vector3>();
	private float[] _layerRotationWeight = System.Array.Empty<float>();
	private float[] _layerPositionWeight = System.Array.Empty<float>();
	private readonly Dictionary<uint, float> _recordWeight = new();
	private readonly List<Channel> _overlays = new();

	public Engine_ClipAnimator(Skeleton3D skeleton, string skeletonName, int[] modelToGuest)
	{
		_skeleton = skeleton;
		_skeletonName = skeletonName;
		_modelToGuest = modelToGuest;
		int bones = skeleton.GetBoneCount();
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
		}
	}

	/// <summary>Whether this animator drives that skeleton — the identity a re-created rig fails.</summary>
	public bool Drives(Skeleton3D skeleton) => ReferenceEquals(_skeleton, skeleton) && Alive;

	/// <summary>False once the skeleton has been freed; nothing here may touch it then.</summary>
	public bool Alive => GodotObject.IsInstanceValid(_skeleton);

	/// <summary>Whether the guest states clips for this rig and every one of them is playable
	/// here; when true the bones were written and the pose feed must not run this frame.</summary>
	public bool Apply(uint entity)
	{
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

		if (count == 0 || !Engine_AnimCatalog.EnsureLoaded())
		{
			++FallbackNoLeaves;
			LastFallback = count == 0 ? "no leaves" : "catalog not loaded";
			return false;
		}

		ulong now = Godot.Time.GetTicksUsec();
		int bones = _skeleton.GetBoneCount();

		// EVENTS IN: every stated leaf finds or starts its channel and sets its target.
		foreach (Channel c in _channels.Values)
		{
			c.Seen = false;
		}

		foreach (HostBridge.GuestAnimLeaf leaf in _leaves)
		{
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
		int topPriority = int.MinValue;
		bool haveTop = false;
		foreach (Channel c in _channels.Values)
		{
			if (c.Leaf.Additive || c.Leaf.Overlay || c.Weight <= 1e-5f)
			{
				continue;
			}

			if (!haveTop || c.Leaf.Priority > topPriority
				|| (c.Leaf.Priority == topPriority && c.Leaf.Uid < topUid))
			{
				haveTop = true;
				topUid = c.Leaf.Uid;
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
		_overlays.Clear();
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

			Built built = c.Built;
			float time = c.Time;
			float weight;
			if (leaf.Additive)
			{
				weight = c.Weight;
			}
			else if (leaf.Uid == topUid)
			{
				weight = topShare * (c.Weight / Mathf.Max(_recordWeight.GetValueOrDefault(topUid), 1e-5f));
			}
			else if (leaf.Uid == runnerUid)
			{
				weight = (1f - topShare) * (c.Weight / Mathf.Max(runnerWeight, 1e-5f));
			}
			else
			{
				continue;  // a third primary never reaches the pose in the guest either
			}

			if (weight <= 1e-5f)
			{
				continue;
			}

			Sample(built, time, weight, leaf.Additive, _rotation, _rotationWeight, _position, _positionWeight, bones);
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

				if (_skeleton.GetBoneParent(b) >= 0)
				{
					Vector3 currentPosition = _positionWeight[b] > 1e-5f ? _position[b] / _positionWeight[b] : _restPosition[b];
					Vector3 layerPosition = _layerPositionWeight[b] > 1e-5f ? _layerPosition[b] / _layerPositionWeight[b] : _restPosition[b];
					_position[b] = currentPosition.Lerp(layerPosition, wb);
					_positionWeight[b] = 1f;
				}
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
			int rt = built.RotationTrack[b];
			if (rt >= 0)
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

			// NO ROOT MOTION FROM THE CLIP: the guest extracts the clip's travel into the
			// entity's placement and rebases the root; sampled here too it walked the body
			// away from its placement each loop and snapped it back at the wrap.
			int pt = built.PositionTrack[b];
			if (pt >= 0 && !additive && _skeleton.GetBoneParent(b) >= 0)
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
	private static void Advance(Channel c, float dt)
	{
		float length = c.Built.Length;
		if (length <= 0f)
		{
			c.Time = 0f;
			return;
		}

		c.Time += dt;
		HostBridge.GuestAnimLeaf leaf = c.Leaf;
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

		c.Time = Mathf.Clamp(c.Time, 0f, length);
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
