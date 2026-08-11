using System.Collections.Generic;
using Godot;
using Refii.Host;
using Refii.Models;

namespace Refii.Entities;

/// <summary>
/// Builds a Godot <see cref="Skeleton3D"/> for a live character and drives it from the guest.
///
/// THE SKELETON IS THE MODEL'S, AND THE POSE IS MAPPED ONTO IT BY NAME.
///
/// That sentence is the whole class, and the earlier version got it backwards. It built the
/// skeleton from the GUEST's rest pose, which is the natural thing to do — the guest is the
/// simulation and states what exists — but a skinned mesh's BLENDINDICES do not address the
/// guest's rig. They address the bone table shipped inside the `.mdl`, and the two do not
/// agree: the child hero's model declares 143 bones while the guest's runtime rig has 161,
/// and a chicken declares 39 against the guest's 39 with only 11 of the parent links in
/// common. Same rig, different numbering. Binding by index attaches every vertex to the
/// wrong bone, and that does not present as an index problem — it presents as a character
/// exploding into a fan of triangles.
///
/// The only key the two sides share is the bone NAME, so the map is built from names and the
/// skeleton is built in the model's numbering. The mesh then binds correctly by construction,
/// and no cached, shared mesh resource has to be rewritten per entity.
///
/// TWO POSES, IN DIFFERENT SPACES. The model's rest is LOCAL: it is fed in as-is and Godot
/// composes it, which is also what Godot inverts to derive the skin's bind matrices. The
/// guest's live pose is usually already MODEL space, so it goes straight in with no parent
/// walk — except when it is not. The guest writes the array local and converts it in place,
/// and the component STATES which one you are holding. Measured in BWSSlums, only 12 of 138
/// characters read as model space at any instant, so the local branch is the common path and
/// not an edge case.
/// </summary>
public sealed class Engine_EntitySkeleton
{
	private readonly Skeleton3D _skeleton;

	/// <summary>The model's bone table — the numbering the skeleton and the mesh both use.</summary>
	private readonly Engine_MdlRig _rig;

	/// <summary>
	/// For each MODEL bone, the guest bone that drives it, or -1 for one the guest does not
	/// have.
	///
	/// This direction and not its inverse, because the pose is applied by walking the model's
	/// bones IN ORDER. <see cref="Skeleton3D.SetBoneGlobalPose"/> derives a bone's local pose
	/// from its parent's current one, so a child written before its parent is computed against
	/// a stale parent and drifts when the parent later moves. The model's table is ordered
	/// parent-before-child; the guest's is too, but the map between them is a permutation, so
	/// walking the guest's order would write the model's bones out of order.
	/// </summary>
	private readonly int[] _modelToGuest;

	/// <summary>How many of the guest's bones found a bone of the same name in the model.</summary>
	public int Mapped { get; }

	/// <summary>Bones the guest has that the model does not. Expected to be non-zero.</summary>
	public int GuestOnly { get; }

	/// <summary>
	/// Unmapped-bone findings already reported, so a model shared by a hundred placements
	/// states its miss once. A run total on purpose — clearing it per frame would restore
	/// exactly the flood it exists to stop.
	/// </summary>
	private static readonly System.Collections.Generic.HashSet<string> _reportedUnmapped = new();

	/// <summary>Rigs left in bind this frame because the guest offered no pose at all.</summary>
	public static int NoPose;

	/// <summary>
	/// Poses refused because the read could not be made coherent — the guest was rewriting the
	/// array while we copied it.
	///
	/// A RUN TOTAL, deliberately, and unlike its neighbours it is NOT reset per frame. Tearing
	/// is a rare event by nature: a per-frame count of it would read zero almost always and
	/// say nothing about whether the run had a problem. It sits with the other run totals in
	/// the facing report rather than among the per-frame counts.
	/// </summary>
	public static int TornPoses;

	/// <summary>
	/// Rigs left in bind because the live pose's bone count differs from the guest skeleton's.
	///
	/// If it is non-zero the rig is fine and the FEED is being refused, which is what a T-pose
	/// looks like from the inside.
	/// </summary>
	public static int CountMismatch;

	/// <summary>The last (live, expected) pair that disagreed, so the shape of the gap shows.</summary>
	public static (int Live, int Expected) LastMismatch;

	/// <summary>Rigs whose pose vector the animation system has not written yet.</summary>
	public static int NeverPosed;

	/// <summary>
	/// The largest departure from a unit scale any bone reported this frame, and the scale that
	/// produced it.
	///
	/// This exists to close a specific question rather than to watch a number. The composition
	/// used to carry <c>_scale[parent] * s</c> down the chain, which is only destructive if the
	/// lane is not 1 — and a previous pass measured every bone in a region at exactly 1, which
	/// would have made the bug invisible and the fix a coincidence. One line settles which of
	/// those it was, and a deviation near zero would mean the explosion is still unexplained.
	/// </summary>
	public static float WorstScaleDeviation;

	/// <summary>The scale behind <see cref="WorstScaleDeviation"/>, so its shape shows.</summary>
	public static Vector3 WorstScale = Vector3.One;

	/// <summary>
	/// Bones whose raw translation matches the model's LOCAL rest length — genuinely local data.
	/// </summary>
	public static int BonesLookingLocal;

	/// <summary>
	/// Bones whose raw translation matches the model's GLOBAL rest distance instead — data that
	/// is ALREADY MODEL SPACE while the component's flag claims otherwise.
	///
	/// This is the measurement that decides the whole local branch, and it exists because the
	/// arithmetic already points one way and pointing is not proving. A tongue segment's local
	/// offset is ~0.019 (measured across 919 models); its distance from the root is ~1.17. Those
	/// differ by sixty times, so a single comparison per bone separates them with no ambiguity
	/// and no threshold worth arguing about.
	///
	/// If this dominates, the flag at comp+0x14 is not telling us what we assumed — and it is
	/// the field the guest itself never reads.
	/// </summary>
	public static int BonesLookingModel;

	/// <summary>Rigs that took the local-composition branch this frame.</summary>
	public static int RigsLocalBranch;

	/// <summary>Rigs whose pose the component already declared model space.</summary>
	public static int RigsModelBranch;

	/// <summary>
	/// Zeroes the per-frame refusal counters. Called once per tick by whoever drives the rigs.
	///
	/// Without this they are run totals read as frame counts: after five thousand frames
	/// "countMismatch=545000" means a hundred and nine rigs were refused, not half a million.
	/// A counter with no reset answers a different question than the one its name asks.
	/// </summary>
	public static void BeginFrame()
	{
		NoPose = 0;
		CountMismatch = 0;
		NeverPosed = 0;
		WorstScaleDeviation = 0f;
		WorstScale = Vector3.One;
		BonesLookingLocal = 0;
		BonesLookingModel = 0;
		RigsLocalBranch = 0;
		RigsModelBranch = 0;
	}

	/// <summary>
	/// Asks the DATA what space it is in, by comparing each bone's raw translation length
	/// against the two rest lengths it could plausibly be.
	///
	/// Length is invariant under the basis map, so no swizzle is needed and the comparison is
	/// exact. The two candidates are sixty times apart for a deep bone, so there is no
	/// threshold to tune and no case where both are close.
	/// </summary>
	private void Classify(in HostBridge.BonePose pose)
	{
		for (int m = 0; m < _modelToGuest.Length; ++m)
		{
			int g = _modelToGuest[m];
			if (g < 0)
			{
				continue;
			}

			float raw = pose.Translation(g).Length();
			float local = _rig.Translation(m).Length();
			float global = _skeleton.GetBoneGlobalRest(m).Origin.Length();

			// Only bones where the two answers actually differ can testify. Near the root they
			// coincide, and counting those would dilute the result toward whichever is nearer
			// by luck.
			if (Mathf.Abs(global - local) < 0.05f)
			{
				continue;
			}

			if (Mathf.Abs(raw - local) <= Mathf.Abs(raw - global))
			{
				++BonesLookingLocal;
			}
			else
			{
				++BonesLookingModel;
			}
		}
	}

	/// <summary>Scratch for composing a local pose, allocated once.</summary>
	private Transform3D[]? _composed;

	/// <summary>How many bones the GUEST's rig has, which is not the skeleton's count.</summary>
	private readonly int _guestBones;

	/// <summary>Read once — the environment does not change mid-run.</summary>
	private static bool? _bindOnly;

	/// <summary>Read once — see REFII_RIG_COMPOSE_LOCAL in <see cref="Follow"/>.</summary>
	private static bool? _composeLocal;

	private Engine_EntitySkeleton(
		Skeleton3D skeleton, Engine_MdlRig rig, int[] modelToGuest, int guestBones, int mapped)
	{
		_skeleton = skeleton;
		_rig = rig;
		_modelToGuest = modelToGuest;
		_guestBones = guestBones;
		Mapped = mapped;
		GuestOnly = guestBones - mapped;
	}

	/// <summary>The node to parent a skinned mesh under.</summary>
	public Skeleton3D Node => _skeleton;

	/// <summary>How many bones the rig has, in the model's numbering.</summary>
	public int BoneCount => _rig.BoneCount;

	/// <summary>How many bones the guest's own rig has.</summary>
	public int GuestBoneCount => _guestBones;

	/// <summary>
	/// True when the guest drives this MODEL bone. False means it holds its bind transform for
	/// ever while its parent moves, which tears any geometry weighted to it.
	/// </summary>
	public bool IsPosed(int modelBone) =>
		modelBone >= 0 && modelBone < _modelToGuest.Length && _modelToGuest[modelBone] >= 0;

	/// <summary>The model's name for a bone, for diagnostics that have to be actionable.</summary>
	public string BoneName(int modelBone) =>
		modelBone >= 0 && modelBone < _rig.BoneCount ? _rig.Names[modelBone] : "<out of range>";

	/// <summary>
	/// How far the POSE turns the body away from the entity's own forward, in degrees.
	///
	/// A character's rendered facing is the entity rotation and the bone pose COMPOSED, and the
	/// walking test cannot tell which of the two is wrong — a body turned 90° inside a correctly
	/// oriented entity looks identical to a correctly posed body in a 90° entity. This separates
	/// them: it ignores the node entirely and asks only what the hip does relative to the mesh's
	/// own forward.
	///
	/// Near zero means the pose keeps the body pointing where the model was authored, so any
	/// facing error belongs to the entity rotation. A large steady value means the guest's pose
	/// carries the heading itself, and composing the entity rotation on top double-counts it.
	///
	/// NaN when there is no hip to ask.
	/// </summary>
	public float PoseYawDegrees()
	{
		for (int m = 0; m < _rig.BoneCount; ++m)
		{
			// The hip is the first real bone of every Fable rig and the one the body's heading
			// hangs off; the `*_Rig_Asset` wrapper above it is never posed.
			if (!_rig.Names[m].Contains("Hip") || _modelToGuest[m] < 0)
			{
				continue;
			}

			// MESH-space forward is M(0, 1, 0) = (0, 0, -1) — the model's own +Y, measured from
			// the sheath dummies in the `.mdl`, mapped through the mesh's basis.
			var forward = new Vector3(0f, 0f, -1f);
			Vector3 posed = _skeleton.GetBoneGlobalPose(m).Basis * forward;
			posed.Y = 0f;
			return posed.Length() < 1e-4f
				? float.NaN
				: Mathf.RadToDeg(forward.SignedAngleTo(posed.Normalized(), Vector3.Up));
		}

		return float.NaN;
	}

	/// <summary>
	/// The axis-aligned bounds of the bones, in the space the mesh is drawn in.
	///
	/// This is the deterministic version of "does the rig look right", and it exists because
	/// the screenshot version was not usable: whether a character is in frame depends on where
	/// the guest happens to have pointed its camera, and four consecutive runs produced sky,
	/// cutscene and rooftops. Bounds need no camera.
	///
	/// Compared against the mesh's own AABB it answers the question directly. A rig that
	/// roughly fills the mesh is bound correctly whatever it looks like on screen; one whose
	/// bones collapse into a point, or sprawl orders of magnitude wider than the mesh, is not
	/// — and those two failures need opposite fixes.
	/// </summary>
	/// <param name="rest">
	/// True for the bind pose Godot composed from what we set, false for the live pose.
	/// </param>
	public Aabb Bounds(bool rest)
	{
		var bounds = new Aabb();
		for (int i = 0; i < _rig.BoneCount; ++i)
		{
			Vector3 origin = rest
				? _skeleton.GetBoneGlobalRest(i).Origin
				: _skeleton.GetBoneGlobalPose(i).Origin;

			bounds = i == 0 ? new Aabb(origin, Vector3.Zero) : bounds.Expand(origin);
		}

		return bounds;
	}

	/// <summary>
	/// Names the bone furthest from the mesh, and the chain that put it there.
	///
	/// "live bones size=(0.77, 1.75, 2.12)" against a mesh 0.43 across says a rig is exploding
	/// and says nothing about WHY, and the two candidate causes need opposite fixes. If the
	/// outlier's PARENT is also far out, the fault is upstream and the chain is being composed
	/// wrongly — a bad parent index, or a local pose composed as if it were model space. If the
	/// parent is where it should be and only the child has flown, the fault is that one bone's
	/// own transform, which is a read problem rather than a composition one.
	///
	/// Reported per rig rather than aggregated: one chicken of three explodes, deterministically
	/// and with the same numbers every run, so the interesting object is a specific one.
	/// </summary>
	public string DescribeOutlier()
	{
		if (_rig.BoneCount == 0)
		{
			return string.Empty;
		}

		Vector3 root = _skeleton.GetBoneGlobalPose(0).Origin;
		int worst = -1;
		float worstDistance = 0f;

		for (int i = 0; i < _rig.BoneCount; ++i)
		{
			float distance = _skeleton.GetBoneGlobalPose(i).Origin.DistanceTo(root);
			if (distance > worstDistance)
			{
				worstDistance = distance;
				worst = i;
			}
		}

		if (worst < 0)
		{
			return " — every bone sits on the root";
		}

		int parent = _rig.Parents[worst];
		string parentText = parent >= 0 && parent < _rig.BoneCount
			? $"'{_rig.Names[parent]}'[{parent}] at " +
			  $"{_skeleton.GetBoneGlobalPose(parent).Origin} " +
			  $"({_skeleton.GetBoneGlobalPose(parent).Origin.DistanceTo(root):0.###} out)"
			: "<root>";

		int guest = _modelToGuest[worst];
		int testified = BonesLookingLocal + BonesLookingModel;
		return $"\n    worst bone scale this frame {WorstScale} " +
			$"({WorstScaleDeviation:0.####} off unity)" +
			$"\n    raw pose translations look " +
			(testified == 0
				? "— no bone could tell the two apart"
				: $"{(BonesLookingModel > BonesLookingLocal ? "MODEL SPACE" : "LOCAL")}: " +
				  $"{BonesLookingModel} of {testified} bones match their distance from the root " +
				  $"rather than their local offset ({RigsModelBranch} rig(s) taken as model, " +
				  $"{RigsLocalBranch} composed)") +
			$"\n    furthest bone '{_rig.Names[worst]}'[{worst}] is {worstDistance:0.###} " +
			$"from the root at {_skeleton.GetBoneGlobalPose(worst).Origin}, " +
			$"rest {_skeleton.GetBoneGlobalRest(worst).Origin}; " +
			$"parent {parentText}; " +
			$"guest bone {(guest < 0 ? "NONE — holding rest" : guest.ToString())}";
	}

	/// <summary>
	/// Builds a rig for one entity, or null when it carries no animated mesh or the guest has
	/// not named its bones yet.
	///
	/// The MODEL supplies the bone TABLE — the numbering, the parents, the names — because that
	/// is what the mesh's BLENDINDICES address. The GUEST supplies the BIND POSE, because that
	/// is what its live pose is measured against.
	/// </summary>
	public static Engine_EntitySkeleton? TryBuild(uint entity, Engine_MdlRig? rig) =>
		rig is { } model && HostBridge.GetEntityBoneNames(entity) is { Length: > 0 } guestNames
			? Build(model, guestNames, HostBridge.GetEntityRestPose(entity))
			: null;

	/// <summary>
	/// Builds a rig from a model and a guest bone-name list, WHEREVER THEY CAME FROM.
	///
	/// Split from <see cref="TryBuild"/> so the editor can stand a character up without a
	/// running guest — pass the model's own names as the guest's and the map is the identity.
	/// The engine is shared between the editor and the game by rule, and a rig that only
	/// existed when the guest was live would be a second implementation waiting to happen.
	/// </summary>
	public static Engine_EntitySkeleton? Build(
		Engine_MdlRig rig, string[] guestNames, HostBridge.BonePose? guestRest = null)
	{
		var skeleton = new Skeleton3D { Name = "Skeleton" };

		// SYNTHETIC NAMES, NOT THE MODEL'S. Godot puts rules on a bone name that a Fable rig
		// has never heard of — it must be non-empty, must not contain ':' or '/', and since
		// 4.3 must be UNIQUE within the skeleton — and `AddBone` answers a name it dislikes by
		// returning -1 and adding nothing.
		//
		// That is silent, and it is not survivable. The skeleton ends up SHORTER than the
		// model's table while the mesh's BLENDINDICES still run to the model's highest bone, so
		// the renderer indexes a skin past its end. Naming them by index instead cannot fail,
		// and Godot never needs the real names anyway: the name is only the key that matches
		// the guest's rig to this one, and that map is built below, in our own dictionary.
		for (int i = 0; i < rig.BoneCount; ++i)
		{
			skeleton.AddBone($"bone_{i}");
		}

		// Believe nothing — the count is the whole safety property. If it ever disagrees the
		// right answer is no rig at all: an unskinned character draws in bind and looks wrong,
		// where a short skeleton reads out of bounds in the renderer.
		if (skeleton.GetBoneCount() != rig.BoneCount)
		{
			GD.PushError($"[rig] skeleton took {skeleton.GetBoneCount()} of the model's " +
				$"{rig.BoneCount} bones; refusing to bind a skin to it");
			skeleton.QueueFree();
			return null;
		}

		for (int i = 0; i < rig.BoneCount; ++i)
		{
			int parent = rig.Parents[i];
			if (parent >= 0 && parent < rig.BoneCount && parent != i)
			{
				skeleton.SetBoneParent(i, parent);
			}
		}

		// THE MAP, BY NAME, because the name is the only key the two rigs share.
		//
		// First match wins. Names are interned in the guest and a rig may legitimately repeat
		// one, so "the guest bone called X" is not guaranteed unique — taking the first keeps
		// the map a function and keeps it stable between frames, which matters more here than
		// resolving a duplicate cleverly.
		var byName = new Dictionary<string, int>(guestNames.Length);
		for (int g = 0; g < guestNames.Length; ++g)
		{
			if (guestNames[g].Length != 0)
			{
				byName.TryAdd(guestNames[g], g);
			}
		}

		var modelToGuest = new int[rig.BoneCount];
		var unmapped = new List<string>();
		int mapped = 0;
		for (int m = 0; m < rig.BoneCount; ++m)
		{
			modelToGuest[m] = byName.TryGetValue(rig.Names[m], out int g) ? g : -1;
			if (modelToGuest[m] >= 0)
			{
				++mapped;
			}
			else
			{
				// NAME the miss, and say where it sits in the hierarchy.
				//
				// "142 of 143 mapped" is a number that cannot be acted on. WHICH bone decides
				// everything: an unmapped fingertip is cosmetic, an unmapped spine holds every
				// descendant at its rest while its parent moves — and a bone frozen mid-chain
				// does not look like a missing bone, it looks like the mesh tearing, which is
				// what sent this investigation at scale and at the composition maths first.
				//
				// The child count is the tell. A leaf costs one bone; a bone with children
				// costs its whole subtree.
				int children = 0;
				for (int c = 0; c < rig.BoneCount; ++c)
				{
					if (rig.Parents[c] == m)
					{
						++children;
					}
				}

				unmapped.Add($"'{rig.Names[m]}'[{m}] parent={rig.Parents[m]} children={children}");
			}
		}

		(float bindGap, string bindGapBone) = SetRest(skeleton, rig, modelToGuest, guestRest);

		// ONCE PER DISTINCT FINDING, not once per instance.
		//
		// This is a property of the MODEL, and a street holds a hundred copies of the same
		// lamp — so the run emitted a hundred identical lines and pushed everything after it
		// off the end of the log. A diagnostic that buries the rest of the report is a
		// diagnostic that costs more than it tells, and it hid the entity report three runs
		// running. Keyed on the message so a genuinely different miss still prints.
		if (unmapped.Count != 0)
		{
			string line = $"[rig] {rig.BoneCount - mapped} model bone(s) the guest never " +
				$"names, so they hold their REST while their parents move: " +
				string.Join(", ", unmapped);
			if (_reportedUnmapped.Add(line))
			{
				GD.Print(line);
			}
		}

		return new Engine_EntitySkeleton(
			skeleton, rig, modelToGuest, guestNames.Length, mapped)
		{
			BindGap = bindGap,
			BindGapBone = bindGapBone,
		};
	}

	/// <summary>
	/// Pushes the guest's current pose into the skeleton. Cheap enough to call every frame —
	/// it is one memory read and one transform per bone.
	/// </summary>
	public void Follow(uint entity)
	{
		// REFII_RIG_BIND_ONLY=1 freezes every rig in its bind pose.
		//
		// A bisect, not a feature. When a character renders wrongly there are two halves that
		// could be at fault — the skeleton/skin/mesh binding, or the live pose being fed into
		// it — and the counts cannot tell them apart. With the live pose skipped, a character
		// that stands up correctly puts the fault entirely in the feed, and one that is still
		// wrong puts it entirely in the bind.
		if (_bindOnly ??= OS.GetEnvironment("REFII_RIG_BIND_ONLY") == "1")
		{
			return;
		}

		if (HostBridge.GetEntityPose(entity) is not { } pose)
		{
			++NoPose;
			return;
		}

		// A TORN READ IS WORSE THAN A STALE ONE, so hold what we already have.
		//
		// The guest rewrites the pose array in place and converts it from parent-relative to
		// model space BONE BY BONE over the same memory (`sub_821F57D0`). A read that lands
		// mid-conversion returns bones in two different spaces at once, and since we treat the
		// whole array as model space, the half that has not been converted yet is interpreted
		// completely wrongly — that is a character exploding for a single frame, not a bone
		// lagging slightly.
		//
		// Returning here leaves the Skeleton3D holding the previous frame's pose, which is at
		// most 33 ms old and always a real pose. There is nothing to fix up and nothing to
		// interpolate: the correct response to "this sample is not a pose" is to not use it.
		if (pose.Torn)
		{
			++TornPoses;
			return;
		}

		if (pose.BoneCount != _guestBones)
		{
			// A bone count that changed means the entity swapped rigs — a different skeleton,
			// not a pose for this one. Counted, because this is what a T-pose looks like from
			// the inside and silently returning made it indistinguishable from an animation
			// that had not started.
			++CountMismatch;
			LastMismatch = (pose.BoneCount, _guestBones);
			return;
		}

		// THE SPACE FLAG HAS THREE VALUES, NOT TWO. 1 is local, 2 is model, and 0 is neither —
		// the component allocated its pose vector but the animation system has not written it
		// yet. Treating "not 2" as "therefore local" fed an uninitialised array through the
		// parent-composition walk, and composing garbage does not look like garbage: it looks
		// like a character whose head and feet are right and whose middle explodes into spikes.
		if (pose.Space != 1 && pose.Space != 2)
		{
			++NeverPosed;
			return;  // never posed — leave it in bind rather than invent one
		}

		Classify(pose);

		// THE POSE ARRAY IS MODEL SPACE, WHATEVER comp+0x14 SAYS.
		//
		// We used to trust that flag and compose anything it called "local" through the parent
		// chain, and it is what made every character explode. Three pieces of evidence, in
		// increasing order of how hard they are to argue with:
		//
		// 1. THE FLAG IS NOT A CONTRACT. `comp+0x14` is WRITE-ONLY in the shipped binary — a
		//    whole-binary scan for a read of it found none. Six writers stamp it at different
		//    points of the tick; nothing consumes it. The guest's own live latch is comp+0x10.
		//    We picked a field the guest itself ignores and treated it as authority, which is
		//    exactly the mistake the architecture rule warns about.
		//
		// 2. THE GUEST ALWAYS CONVERTS. `sub_8257D8B8` calls the local->model converter
		//    `sub_821F57D0` unconditionally — no flag, no dirty bit, no visibility gate — and
		//    only then stamps 2. By the time anything outside the tick can read the array, it
		//    has been converted. A stamp of 1 is a value left behind mid-pipeline.
		//
		// 3. THE NUMBERS. Measured on the child hero: the live step from 'Shadow_tongue2' to
		//    'Shadow_tongue3' is 1.1521 long. That bone's LOCAL rest offset is ~0.019 (the
		//    median across 919 shipped models is 0.0186); its distance from the root is 1.1726.
		//    The step matches the absolute position to within 1.8% and misses the local one by
		//    sixty times. Composing that as if it were parent-relative adds every ancestor a
		//    second time, which is why the worst bone was always the tip of the longest chain.
		//
		// `Classify` re-measures this every frame rather than trusting the reasoning above, and
		// reports it in the rig line. If BonesLookingLocal ever dominates, this is wrong.
		//
		// REFII_RIG_COMPOSE_LOCAL=1 restores the parent walk, so the two can still be compared
		// without a rebuild.
		_composeLocal ??= OS.GetEnvironment("REFII_RIG_COMPOSE_LOCAL") == "1";

		bool modelSpace = _composeLocal != true || pose.IsModelSpace;
		if (modelSpace)
		{
			++RigsModelBranch;
		}
		else
		{
			++RigsLocalBranch;
		}

		if (!modelSpace)
		{
			_composed ??= new Transform3D[HostBridge.MaxBones];
			_rotation ??= new Quaternion[HostBridge.MaxBones];
			_translation ??= new Vector3[HostBridge.MaxBones];
			_scale ??= new Vector3[HostBridge.MaxBones];

			for (int g = 0; g < _guestBones; ++g)
			{
				Quaternion r = GuestRotation(pose.Rotation(g));
				Vector3 t = Swizzle(pose.Translation(g));

				// PERMUTED, NOT SWIZZLED — the same distinction ToGodot documents. A scale is
				// not a position: conjugating diag(s) through M permutes the axes and negates
				// nothing, and swizzling it would put a mirror on every bone.
				Vector3 guestScale = pose.Scale(g);
				var s = new Vector3(guestScale.X, guestScale.Z, guestScale.Y);

				if (!r.IsFinite() || !t.IsFinite() || !s.IsFinite())
				{
					r = Quaternion.Identity;
					t = Vector3.Zero;
					s = Vector3.One;
				}

				// SCALE IS NOT A LINK IN THE CHAIN — it is a property of the bone.
				//
				// Every bone's deviation from unity, so one line can say whether the lane holds
				// anything at all. It is asked because the scale used to be composed down the
				// chain, and if these were all 1 that would have been harmless.
				float deviation = Mathf.Max(Mathf.Abs(s.X - 1f),
					Mathf.Max(Mathf.Abs(s.Y - 1f), Mathf.Abs(s.Z - 1f)));

				if (deviation > WorstScaleDeviation)
				{
					WorstScaleDeviation = deviation;
					WorstScale = s;
				}

				// A bone whose parent comes later is a torn read, not a rig to fix up: the
				// guest's own conversion relies on parent-before-child ordering too.
				int parent = pose.Parents[g];
				if (g == 0)
				{
					// BONE 0 IS STAMPED IDENTITY, and its stored transform is thrown away.
					//
					// Not an optimisation and not a guess — `sub_821F57D0` writes the root's
					// element before it composes anything, and I read the three stores:
					//
					//     821f5858  stvx128 v127, r29, r26   ; pose[0]+0x10 = (0,0,0,0)
					//     821f585c  stvx128 v11,  r29, r5    ; pose[0]+0x20 = (1,1,1,1)
					//     821f5860  stvx128 v10,  r0,  r29   ; pose[0]+0x00 = (0,0,0,1)
					//
					// with r26=0x10, r5=0x20, v127 = vspltisw128 0, v11 = splat(flt_82099490)
					// = 1.0, and v10 = splat(1.0) AND unk_82099160 (0,0,0,0xFFFFFFFF), which is
					// the identity quaternion with w in lane 3. So the guest's "model space" is
					// the ROOT BONE's frame with the root's own animation zeroed out, and a host
					// that keeps it applies one extra transform to every bone in the rig.
					_translation[0] = Vector3.Zero;
					_rotation[0] = Quaternion.Identity;
					_scale[0] = Vector3.One;
				}
				else if (parent >= 0 && parent < g)
				{
					// COMPOSE TRS, NOT MATRICES, AND LEAVE SCALE OUT OF IT ENTIRELY.
					//
					// `sub_821F58D8` is 65 instructions and the only displacements it touches on
					// either element are 0, 8 and 0x10: a Hamilton product into the child's
					// quaternion at +0x00, then `t_parent + rotate(q_parent, t_child)` into the
					// child's translation at +0x10. It never loads or stores +0x20.
					//
					// So scale is neither accumulated down the chain nor applied to a child's
					// translation. It stays exactly as the animation left it, and reaches the
					// GPU one step later, per bone, in `sub_8222E1C0` — which builds
					// `[R * diag(s) | t]`, scaling COLUMN k by s[k] and leaving the translation
					// alone. That is a post-multiply on the bone's own axes.
					//
					// Carrying `_scale[parent] * s` here multiplied a field the guest never
					// reads, once per link, and a chain ten bones deep turns a scale the
					// animation never meant as a factor into a hundredfold one at the tip. That
					// is the fan of shards, and it is why the worst bone was always a tongue, a
					// ponytail or a wattle — the deepest chains in the rig.
					_translation[g] = _translation[parent] + (_rotation[parent] * t);
					_rotation[g] = _rotation[parent] * r;
					_scale[g] = s;
				}
				else
				{
					// Parent -1 on a non-root bone: the guest skips it and leaves the element
					// holding its local transform, so local IS model for this one.
					_translation[g] = t;
					_rotation[g] = r;
					_scale[g] = s;
				}

				// ScaledLocal for the same reason ToGodot uses it, and now with a citation:
				// `sub_8222E1C0` splats the scale's three lanes and multiplies them into the
				// three COLUMNS of the rotation matrix (`vmulfp128 v21, v24, v11` and its two
				// siblings), giving `[R * diag(s) | t]`. Scale on the bone's own axes, applied
				// after the rotation, and never touching the translation in column 3.
				_composed[g] = new Transform3D(
					new Basis(_rotation[g]).ScaledLocal(_scale[g]), _translation[g]);
			}
		}

		// IN THE MODEL'S ORDER, parents before children — see _modelToGuest. Bones the guest
		// does not have keep their rest, which is right for the wrapper and the attachment
		// dummies: they carry no skinning weight and nothing animates them.
		for (int m = 0; m < _modelToGuest.Length; ++m)
		{
			int g = _modelToGuest[m];
			if (g < 0)
			{
				continue;
			}

			_skeleton.SetBoneGlobalPose(m, modelSpace
				? ToGodot(pose.Rotation(g), pose.Translation(g), pose.Scale(g))
				: _composed![g]);
		}
	}

	/// <summary>
	/// Gives the skeleton its BIND POSE, from THE MODEL FILE, and measures how far the guest's
	/// own bind sits from it.
	///
	/// THE BIND MUST MATCH THE SPACE THE VERTICES ARE AUTHORED IN, WHICH IS THE MODEL'S.
	/// Skinning is <c>Σ wᵢ · pose_i · bind_i⁻¹ · v</c>, and <c>v</c> comes out of the `.mdl`, so
	/// <c>bind</c> has to be the `.mdl`'s. A previous pass took the bind from the guest instead,
	/// reasoning that the pose and the bind should come from the same rig — which sounds right
	/// and is wrong, because the vertex is the third term and it does not move.
	///
	/// The run that refuted it: the child hero's mesh is 1.3839 tall, its model bind 1.3797, and
	/// the guest's bind for the same entity **1.8542** — an ADULT skeleton, its X matching the
	/// adult composite's to seven digits. The hero's runtime rig is the grown-up one and the
	/// child's proportions live in the LIVE pose, not the reference pose. Binding a child mesh
	/// through an adult bind is what left that one character in shards after every other
	/// character had come good.
	///
	/// So the guest's rest is read only to be MEASURED against the model's. A large gap is worth
	/// knowing about — it is real, and it is why the idea was tempting — but it is not a licence
	/// to use it.
	/// </summary>
	private static (float Gap, string Bone) SetRest(
		Skeleton3D skeleton, Engine_MdlRig rig, int[] modelToGuest, HostBridge.BonePose? guestRest)
	{
		float gap = 0f;
		string gapBone = string.Empty;

		// The guest's rest is LOCAL and needs the same parent walk its live pose does NOT — see
		// `RefiiHostGetEntityRestPose`, which reads skeleton+0x90 and states as much, and
		// `sub_82B734B0`, the guest's own converter for it. Composed here in the GUEST's
		// numbering, because that is the numbering its parents are expressed in.
		Transform3D[]? guestGlobal = null;
		if (guestRest is { } bind && bind.BoneCount > 0)
		{
			guestGlobal = new Transform3D[bind.BoneCount];
			var rotation = new Quaternion[bind.BoneCount];
			var translation = new Vector3[bind.BoneCount];

			for (int g = 0; g < bind.BoneCount; ++g)
			{
				Quaternion r = GuestRotation(bind.Rotation(g));
				Vector3 t = Swizzle(bind.Translation(g));
				Vector3 guestScale = bind.Scale(g);
				var s = new Vector3(guestScale.X, guestScale.Z, guestScale.Y);

				int parent = bind.Parents[g];

				// THE SAME COMPOSITION THE LIVE POSE USES, because it is the same contract:
				// `sub_82B734B0` converts the rest through `sub_821F58D8` exactly as
				// `sub_821F57D0` converts the pose — a quaternion product and
				// `t_parent + rotate(q_parent, t_child)`, with the scale lane never read. A
				// Transform3D multiply would fold scale into the basis and shear every child of
				// a scaled bone. Element 0 is stamped identity by the converter, as it is there.
				if (g == 0)
				{
					rotation[0] = Quaternion.Identity;
					translation[0] = Vector3.Zero;
					s = Vector3.One;
				}
				else if (parent >= 0 && parent < g)
				{
					translation[g] = translation[parent] + (rotation[parent] * t);
					rotation[g] = rotation[parent] * r;
				}
				else
				{
					translation[g] = t;
					rotation[g] = r;
				}

				guestGlobal[g] = new Transform3D(
					new Basis(rotation[g]).ScaledLocal(s), translation[g]);
			}
		}

		// Walked parent-before-child — guaranteed, measured across all 919 rigged models — so a
		// parent's global rest is final before a child needs it to convert back to local.
		var global = new Transform3D[rig.BoneCount];
		for (int m = 0; m < rig.BoneCount; ++m)
		{
			Transform3D modelLocal =
				ToGodot(rig.Rotation(m), rig.Translation(m), rig.Scale(m));

			int parent = rig.Parents[m];
			Transform3D parentGlobal = parent >= 0 && parent < m
				? global[parent]
				: Transform3D.Identity;

			global[m] = parentGlobal * modelLocal;

			// MEASURED, NOT USED — see the note above on why the guest's bind is the wrong one.
			int g = modelToGuest[m];
			if (guestGlobal is not null && g >= 0 && g < guestGlobal.Length)
			{
				float apart = guestGlobal[g].Origin.DistanceTo(global[m].Origin);
				if (apart > gap)
				{
					gap = apart;
					gapBone = rig.Names[m];
				}
			}

			skeleton.SetBoneRest(m, modelLocal);

			// Seed the pose from the rest too, so a character drawn before its first live pose
			// arrives stands in bind rather than collapsing to the origin.
			skeleton.SetBonePosePosition(m, modelLocal.Origin);
			skeleton.SetBonePoseRotation(m, modelLocal.Basis.GetRotationQuaternion());
			skeleton.SetBonePoseScale(m, modelLocal.Basis.Scale);
		}

		return (gap, gapBone);
	}

	/// <summary>
	/// How far the guest's own bind sits from the model file's, for the worst bone of THIS rig.
	///
	/// Per instance, not static: a run total answers a different question than the one the name
	/// asks, and the previous version reported one shared figure against every character.
	/// </summary>
	public float BindGap { get; init; }

	/// <summary>Which bone disagreed most, because a number alone is not actionable.</summary>
	public string BindGapBone { get; init; } = string.Empty;

	/// <summary>
	/// One bone's transform, in THE MESH'S space.
	///
	/// The map here is <c>M: (x, y, z) -&gt; (x, z, -y)</c>, which is
	/// <see cref="Refii.Models.Engine_MdlGeometry"/>'s <c>ConvertZUpToYUp</c> — NOT the
	/// <c>(x, y, z) -&gt; (-x, z, y)</c> that <see cref="Refii.Game.GameEntities"/> and the
	/// camera use.
	///
	/// That distinction is the whole correctness of this class, and getting it wrong does not
	/// look like an axis error. Skinning multiplies each vertex by its bones' matrices, so
	/// vertices and bones have to be in ONE space; the vertices were already baked into M by
	/// the mesh reader. Posing the bones in the node's map instead left the two disagreeing by
	/// a rotation, and the mesh came out crumpled — which reads as a bad rig or bad weights
	/// rather than as a mismatched basis.
	///
	/// The node's map still applies, but one level up: <see cref="Refii.Game.GameEntities"/>
	/// puts it on the entity's own transform, and the skeleton rides that. Applying it here as
	/// well would be applying it twice.
	///
	/// Conjugation rather than component-swizzling, because a quaternion under a change of
	/// basis is <c>M R M^-1</c> — negating components happens to work only for maps that are
	/// their own inverse, and M is not one (applying it twice gives <c>(x, -y, -z)</c>).
	/// </summary>
	private static Transform3D ToGodot(Quaternion rotation, Vector3 guestTranslation,
		Vector3 guestScale)
	{
		float lengthSquared = rotation.LengthSquared();
		Basis basis = float.IsFinite(lengthSquared) && lengthSquared > 1e-8f
			? new Basis(rotation.Normalized())
			: Basis.Identity;

		basis = MeshMap * basis * MeshMapInverse;

		// THE SCALE IS THE ONE PART THAT MUST NOT PICK UP THE MAP'S SIGN, and it did.
		//
		// M sends y to -z, so a POSITION's third component flips — that is what Swizzle is
		// for and it is right for translations. A scale is not a position: conjugating it
		// gives `M diag(s) M^-1 = diag(sx, sz, sy)`, the axes permuted and nothing negated.
		// Swizzling it instead turned a unit scale into (1, 1, -1), which is a mirror, and
		// left EVERY bone with a determinant of -1. Measured after the fix: every one of the
		// 5,619 bones in a region reports a scale of 1, so the swizzle was doing nothing but
		// the damage.
		var scale = new Vector3(guestScale.X, guestScale.Z, guestScale.Y);

		// Finite or nothing. A single NaN or infinity in one bone propagates through the
		// parent chain and takes every descendant with it, which is indistinguishable from a
		// bad offset by eye and trivially distinguishable here.
		Vector3 translation = Swizzle(guestTranslation);
		if (!scale.IsFinite() || !translation.IsFinite())
		{
			return Transform3D.Identity;
		}

		// ScaledLocal, not Scaled. The element is quaternion THEN scale, so the scale belongs
		// inside the rotation — `R * diag(s)`, the bone's own axes. Godot's `Scaled`
		// premultiplies (`diag(s) * R`) and scales along the PARENT's axes instead, which is
		// silently identical for the uniform scales these rigs carry and wrong for the rest.
		basis = basis.ScaledLocal(scale);
		return new Transform3D(basis, translation);
	}

	/// <summary>The mesh's own map applied to a vector: <c>(x, y, z) -&gt; (x, z, -y)</c>.</summary>
	private static Vector3 Swizzle(Vector3 guest) => new(guest.X, guest.Z, -guest.Y);

	/// <summary>
	/// One bone's rotation, mapped into the mesh's space.
	///
	/// Same conjugation <see cref="ToGodot"/> performs — a quaternion under a change of basis
	/// is <c>M R M^-1</c>, not a component swizzle — but returned as a quaternion so the
	/// parent walk can compose rotations the way the guest does, with a quaternion PRODUCT,
	/// rather than by multiplying bases that carry scale.
	/// </summary>
	private static Quaternion GuestRotation(Quaternion rotation)
	{
		float lengthSquared = rotation.LengthSquared();
		Basis basis = float.IsFinite(lengthSquared) && lengthSquared > 1e-8f
			? new Basis(rotation.Normalized())
			: Basis.Identity;

		return (MeshMap * basis * MeshMapInverse).GetRotationQuaternion();
	}

	private Quaternion[]? _rotation;

	private Vector3[]? _translation;

	private Vector3[]? _scale;

	/// <summary>
	/// <c>ConvertZUpToYUp</c> as a basis — columns are the images of the guest's axes.
	///
	/// Determinant +1, so it is a rotation and does not flip the winding of a skinned mesh.
	/// </summary>
	private static readonly Basis MeshMap = new(
		new Vector3(1f, 0f, 0f),
		new Vector3(0f, 0f, -1f),
		new Vector3(0f, 1f, 0f));

	/// <summary>
	/// The inverse of <see cref="MeshMap"/>: <c>(x, y, z) -&gt; (x, -z, y)</c>.
	///
	/// Spelled out rather than computed, and spelled out rather than reusing MeshMap — an
	/// earlier version assumed the map was its own inverse, which the world's map is and this
	/// one is not.
	/// </summary>
	private static readonly Basis MeshMapInverse = new(
		new Vector3(1f, 0f, 0f),
		new Vector3(0f, 0f, 1f),
		new Vector3(0f, -1f, 0f));
}
