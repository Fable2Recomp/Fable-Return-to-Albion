using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;
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

	/// <summary>The shadow skeleton holds the last pose this writer wrote to the body; false after a write it did not mirror.</summary>
	private bool _shadowSynced;

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
	/// exact. The two candidates are sixty times apart for a deep bone of a STANDING body. A
	/// body folded onto its own origin — a dog digging or lying down — brings them together, so
	/// this is a counter for the rig line and gates nothing; <see cref="LocalVotes"/> decides,
	/// on bone length.
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
		_hipBone = -2;  // the mapping changed: re-resolve the hip on next use
		_collapseProbeBone = -2;
		_collapseProbeRestY = 0f;
		_guestBones = guestBones;
		Mapped = mapped;
		GuestOnly = guestBones - mapped;

		// The rig's reach at rest: how far any bone sits from the root. A live pose is bounded
		// by it — no animation carries a bone past roughly twice the rig's own extent.
		float reach = 0f;
		for (int m = 0; m < skeleton.GetBoneCount(); ++m)
		{
			reach = Mathf.Max(reach, skeleton.GetBoneGlobalRest(m).Origin.Length());
		}

		_restReach = Mathf.Max(reach, 0.25f);

		// THE SKIN CARRIES THE BONE SCALE; THE SKELETON CARRIES NONE. The guest's skin matrix is
		// `[R * diag(s) | t]` per bone with s the bone's OWN scale (`sub_8222E2B8` ->
		// `sub_8222E1C0`), composed through the parents with rotation and translation only
		// (`sub_821F58D8`). A Godot bone pose inherits its parent's scale, and a pose written
		// as `inverse(parent) * child` to cancel that is decomposed by `SetBonePose` into
		// position, rotation and scale — the shear of `S_p⁻¹ R S_c` is dropped, and across a
		// rotated joint the parent's scale lands on the wrong axes of every descendant
		// (measured on the morphed hero: hands 16 cm and feet 12 cm from the guest's pose).
		// So the skeleton is posed with R and t alone, and the scale goes into the bind:
		// `pose * (diag(s) * bind⁻¹)` is exactly the guest's product.
		int count = skeleton.GetBoneCount();
		_inverseRest = new Transform3D[count];
		_skinScale = new Vector3[count];
		for (int m = 0; m < count; ++m)
		{
			_inverseRest[m] = skeleton.GetBoneGlobalRest(m).AffineInverse();
			_skinScale[m] = Vector3.One;
		}

		Skin = skeleton.CreateSkinFromRestTransforms();
	}

	/// <summary>
	/// The skin every mesh on this rig binds with — the model's inverse rests, each scaled by
	/// the bone's live scale (<see cref="SetBoneScales"/>). One resource, shared by the body,
	/// its shadow proxy and the cloth palette, so a scale lands on all of them at once.
	/// </summary>
	public Skin Skin { get; }

	private readonly Transform3D[] _inverseRest;

	private readonly Vector3[] _skinScale;

	/// <summary>Bind poses rewritten for a changed bone scale; run total.</summary>
	public static long SkinScaleWrites;

	/// <summary>
	/// The guest's own scale lane for model bone <paramref name="model"/> from the newest pose whose
	/// morph record is known (<see cref="FoldedRecord"/>), One before any. It is the clip's scale
	/// with the record already folded in, on both titles: Fable 2 `sub_821F2C78` runs
	/// `sub_822BF9B8` on the pose whenever comp6+152 is set (0x822bfb34 `vmulfp128`, stored to
	/// +0x20 at 0x822bfb3c) and Fable 3 `sub_8222AB68` does the same (0x8222ac80, stored at
	/// 0x8222ac88). A Fable 3 rig also scales bones of its own — 141 of 12,869 live bones, 3e-5
	/// (collapsed) to 4.5 — which only this lane carries.
	/// </summary>
	public Vector3 FoldedLane(int model) =>
		model >= 0 && model < _foldedLane.Length ? _foldedLane[model] : Vector3.One;

	/// <summary>
	/// The record <see cref="FoldedLane"/> was folded with: the guest's record read beside that pose
	/// and unchanged since the read before it, so the guest's fold in between used it. A lane taken
	/// beside a record that had just changed may carry the old one (the fold runs once a tick) and
	/// is not taken; one taken before the guest ever updated the pose carries none (One).
	/// </summary>
	public Vector3 FoldedRecord(int model) =>
		model >= 0 && model < _foldedRecord.Length ? _foldedRecord[model] : Vector3.One;

	/// <summary>Lanes <see cref="FoldedLane"/> took with their record; 0 before the first.</summary>
	public int FoldedCaptures { get; private set; }

	/// <summary>
	/// Gives each model bone its own scale, in the bone's axes, by folding it into the skin's
	/// bind pose — never into the skeleton, which must stay scale-free (see the constructor).
	/// Indexed by model bone; a shorter span leaves the rest at their last value. Only binds
	/// whose scale moved are rewritten, so a settled rig costs a comparison per bone.
	/// </summary>
	public void SetBoneScales(ReadOnlySpan<Vector3> scales)
	{
		int bones = Math.Min(scales.Length, _inverseRest.Length);
		for (int m = 0; m < bones; ++m)
		{
			Vector3 s = scales[m];
			if (!s.IsFinite() || Mathf.Abs(s.X) < 1e-6f || Mathf.Abs(s.Y) < 1e-6f || Mathf.Abs(s.Z) < 1e-6f)
			{
				s = Vector3.One;
			}

			if (s.IsEqualApprox(_skinScale[m]))
			{
				continue;
			}

			_skinScale[m] = s;
			Skin.SetBindPose(m, new Transform3D(Basis.FromScale(s), Vector3.Zero) * _inverseRest[m]);
			++SkinScaleWrites;
		}
	}

	private readonly float _restReach;

	/// <summary><see cref="Refii.Animations.Engine_TitleRig.ReachIncludesGuestRest"/>, read at construction.</summary>
	private readonly bool _reachIncludesGuestRest = Refii.Core.Engine_TitleGame.Active.Rig.ReachIncludesGuestRest;

	/// <summary>Poses refused because a bone carried non-finite data, non-unit rotation, or overflow coordinates.</summary>
	public static int ImplausiblePoses;

	/// <summary>Safety ceiling on raw bone coordinates matching <c>MaxFxReach</c> (no 4 m bone reach cap).</summary>
	public const float MaxBoneCoordinate = 10_000f;

	/// <summary>True while a host-driven disarticulated/crumble simulation owns this rig's bones.</summary>
	public bool IsDisarticulated { get; set; }

	/// <summary>
	/// True when the bound mesh carries separable single-bone rigid geometry (such as Fable 2's
	/// <c>Skeleton_Base.mdl</c>) rather than smooth multi-bone skinning.
	/// </summary>
	public bool IsRigidPieceMesh { get; set; }

	private int _spreadAnchorGuest = -2;

	private System.Func<int, bool>? _isPosedPredicate;

	/// <summary>Cached predicate matching <see cref="IsPosed"/> for zero-allocation attachment queries.</summary>
	public System.Func<int, bool> IsPosedPredicate => _isPosedPredicate ??= IsPosed;

	/// <summary>Model parent indices for every bone in this rig.</summary>
	public int[] Parents => _rig.Parents;

	/// <summary>
	/// Copies each bone's model-space global rest transform into <paramref name="destination"/>.
	/// </summary>
	public void CopyRestGlobals(System.Span<Transform3D> destination)
	{
		int count = System.Math.Min(_rig.BoneCount, destination.Length);
		for (int m = 0; m < count; ++m)
		{
			destination[m] = _skeleton.GetBoneGlobalRest(m);
		}
	}

	/// <summary>
	/// Validates a single bone transform against corruption/overflow faults without enforcing an
	/// arbitrary 4 m rest-reach cap so crumbled and ragdolled skeletons scatter freely.
	/// </summary>
	public static bool IsPlausibleBone(
		Vector3 translation, Quaternion rotation, Vector3 scale, out string? reason)
	{
		if (!translation.IsFinite() || !rotation.IsFinite() || !scale.IsFinite())
		{
			reason = "not finite";
			return false;
		}

		if (translation.Length() > MaxBoneCoordinate)
		{
			reason = "beyond world ceiling";
			return false;
		}

		if (rotation.LengthSquared() is < 0.5f or > 2f)
		{
			reason = "rotation not unit";
			return false;
		}

		reason = null;
		return true;
	}

	private static int _refusalsLogged;
	/// <summary>
	/// Rig names already reported as stuck. A flat cap on the LINE COUNT was spent entirely by
	/// the cooking pots and cupboards — which are genuinely static and genuinely uninteresting
	/// — while the creature that prompted the search never got a line. One line per rig NAME
	/// reports each distinct offender once and cannot be crowded out by a common prop.
	/// </summary>
	private static readonly HashSet<string> _stuckLogged = new();
	private static int _jumpsLogged;
	private int _localReads;

	/// <summary>Consecutive local reads after which a rig counts as stuck local (reported).</summary>
	private const int LocalStuckFrames = 300;

	/// <summary>Rigs that read local for LocalStuckFrames in a row — props, in practice.</summary>
	public static int LocalStuckRigs;

	/// <summary>Poses held because the array still read local (see LocalVotes).</summary>
	public static int LocalPosesHeld;

	/// <summary>Poses whose flag said local while the data was model space — applied.</summary>
	public static int FlagLocalDataModel;

	/// <summary>Poses held because the array was half converted (a split in guest order).</summary>
	public static int MixedPosesHeld;

	/// <summary>Bones that moved more than 1.5 m between two accepted poses.</summary>
	public static int PoseJumps;

	/// <summary>Accepted poses that differed from the rig's previous one, and all applies —
	/// their ratio is how often the guest's animation actually moves per sample.</summary>
	public static int PoseChanges;
	public static int PoseApplies;

	private int _myChanges;
	private int _myApplies;
	private Vector3[]? _lastTranslation;
	private Quaternion[]? _lastRotation;
	private bool _lastTranslationValid;

	/// <summary>This rig's own change ratio, for the per-rig report.</summary>
	public (int Changes, int Applies) ChangeCounts => (_myChanges, _myApplies);

	/// <summary>True once at least one valid guest pose has been accepted into the pose ring.</summary>
	public bool HasAppliedPose => _poses.Count > 0;

	/// <summary>
	/// Returns true when a bone's translation or rotation moved between two pose samples.
	/// Uses hemisphere-aware component difference on the quaternion rather than raw
	/// <c>curRot.Dot(prevRot)</c>, because slightly unnormalized guest quaternions
	/// (<c>|q|² != 1</c>) satisfy <c>|q · q| &lt; 1 - 1e-6</c> even when <c>curRot == prevRot</c>
	/// bit-for-bit, which previously forced stationary props to re-pose on 100% of frames.
	/// </summary>
	public static bool HasBonePoseChanged(Vector3 prevPos, Vector3 curPos, Quaternion prevRot, Quaternion curRot)
	{
		if (curPos.DistanceSquaredTo(prevPos) > 1e-8f)
		{
			return true;
		}

		float dot = prevRot.Dot(curRot);
		float lenSq = prevRot.LengthSquared() * curRot.LengthSquared();
		return lenSq > 1e-12f && (dot * dot) < (1f - 1e-6f) * lenSq;
	}

	/// <summary>Consecutive local reads held right now.</summary>
	public int LocalReads => _localReads;

	/// <summary>Called when a frame skips this rig (off camera): the next accepted pose is a
	/// fresh baseline, not a jump, and the blend restarts from it.</summary>
	public void ForgetLastPose()
	{
		_lastTranslationValid = false;
		_poses.Clear();

		// The skeleton is about to be written by something else — the clip animator — so what
		// this rig last wrote is no longer what the bones hold, nor what the shadow holds.
		_wroteSettled = false;
		_shadowSynced = false;
	}

	/// <summary>
	/// Maximum allowed distance from the posed hip/root anchor to any non-root bone on a
	/// smooth-skinned (non-disarticulated) character rig so detached Havok ragdoll bones can never
	/// stretch a smooth mesh into multi-meter "string cheese".
	/// </summary>
	public static float SmoothBoneSpreadLimit(float restReach) =>
		Mathf.Max(restReach * 2.5f, 3.5f);

	/// <summary>
	/// Checks whether one non-root bone translation on a smooth-skinned rig sits within the
	/// coherent spread bound around its posed hip/root anchor.
	/// </summary>
	public static bool IsSmoothBoneSpreadPlausible(
		Vector3 boneTranslation,
		Vector3 anchorTranslation,
		float restReach,
		out float spread)
	{
		float limit = SmoothBoneSpreadLimit(restReach);
		float distSq = boneTranslation.DistanceSquaredTo(anchorTranslation);
		if (distSq <= limit * limit)
		{
			spread = 0f;
			return true;
		}

		spread = Mathf.Sqrt(distSq);
		return false;
	}

	/// <summary>
	/// Pure-C# coherence check for a smooth-skinned pose: verifies every non-root bone lies within
	/// <see cref="SmoothBoneSpreadLimit"/> of the posed anchor bone while permitting arbitrary
	/// root-motion offsets away from the model origin.
	/// </summary>
	public static bool IsSmoothPoseCoherent(
		ReadOnlySpan<Vector3> boneTranslations,
		ReadOnlySpan<int> parents,
		int anchorIndex,
		float restReach,
		out int offendingBone,
		out float offendingSpread)
	{
		offendingBone = -1;
		offendingSpread = 0f;
		if ((uint)anchorIndex >= (uint)boneTranslations.Length)
		{
			return true;
		}

		Vector3 anchor = boneTranslations[anchorIndex];
		if (!anchor.IsFinite())
		{
			offendingBone = anchorIndex;
			offendingSpread = float.PositiveInfinity;
			return false;
		}

		for (int i = 0; i < boneTranslations.Length; ++i)
		{
			if (i == anchorIndex || (i < parents.Length && parents[i] < 0))
			{
				continue;
			}

			Vector3 t = boneTranslations[i];
			if (!t.IsFinite())
			{
				offendingBone = i;
				offendingSpread = float.PositiveInfinity;
				return false;
			}

			if (!IsSmoothBoneSpreadPlausible(t, anchor, restReach, out float spread))
			{
				offendingBone = i;
				offendingSpread = spread;
				return false;
			}
		}

		return true;
	}

	private int ResolveSpreadAnchorGuest()
	{
		if (_spreadAnchorGuest != -2)
		{
			return _spreadAnchorGuest;
		}

		int anchor = -1;
		for (int m = 0; m < _modelToGuest.Length; ++m)
		{
			int g = _modelToGuest[m];
			if (g >= 0 && _rig.Parents[m] >= 0 && _rig.Names[m].Contains("Hip", StringComparison.OrdinalIgnoreCase))
			{
				anchor = g;
				break;
			}
		}

		if (anchor < 0 && TryBone(Refii.Animations.Engine_TitleRig.RigRole.Hip, out int hip) &&
			hip < _modelToGuest.Length && _modelToGuest[hip] >= 0 && _rig.Parents[hip] >= 0)
		{
			anchor = _modelToGuest[hip];
		}

		if (anchor < 0)
		{
			for (int m = 0; m < _modelToGuest.Length; ++m)
			{
				if (_modelToGuest[m] >= 0 && _rig.Parents[m] >= 0)
				{
					anchor = _modelToGuest[m];
					break;
				}
			}
		}

		_spreadAnchorGuest = anchor;
		return anchor;
	}

	/// <summary>
	/// Whether a pose could be this rig's. The double-read tear detector in the bridge catches
	/// a write that lands BETWEEN its two copies; garbage that reads identically twice is what
	/// this catches: a non-finite lane, a non-unit rotation, overflow coordinates beyond
	/// <see cref="MaxBoneCoordinate"/>, or (on smooth-skinned rigs) bones scattered far past the
	/// rig's coherent rest reach.
	/// </summary>
	private bool Plausible(in HostBridge.BonePose pose)
	{
		bool enforceSmoothSpread = !IsDisarticulated && !IsRigidPieceMesh;
		int anchorGuest = enforceSmoothSpread ? ResolveSpreadAnchorGuest() : -1;
		bool hasAnchor = anchorGuest >= 0 && anchorGuest < pose.BoneCount;
		Vector3 anchorPos = hasAnchor ? pose.Translation(anchorGuest) : Vector3.Zero;
		if (hasAnchor && !anchorPos.IsFinite())
		{
			hasAnchor = false;
		}

		float effectiveReach = _reachIncludesGuestRest
			? Mathf.Max(_restReach, GuestRestReach)
			: _restReach;

		for (int m = 0; m < _modelToGuest.Length; ++m)
		{
			int g = _modelToGuest[m];
			if (g < 0)
			{
				continue;
			}

			Vector3 t = pose.Translation(g);
			Quaternion q = pose.Rotation(g);
			Vector3 sc = pose.Scale(g);
			if (!IsPlausibleBone(t, q, sc, out string? why))
			{
				if (_refusalsLogged < 12)
				{
					++_refusalsLogged;
					GD.Print($"[rig-refuse] '{_skeleton.Name}' bone {g} '{BoneName(m)}': {why} |t|={t.Length():0.000} |q|²={q.LengthSquared():0.000} scale={sc}");
				}

				return false;
			}

			if (hasAnchor && g != anchorGuest && _rig.Parents[m] >= 0 &&
				!IsSmoothBoneSpreadPlausible(t, anchorPos, effectiveReach, out float spread))
			{
				if (_refusalsLogged < 12)
				{
					++_refusalsLogged;
					GD.Print($"[rig-refuse] '{_skeleton.Name}' bone {g} '{BoneName(m)}': smooth spread {spread:0.00}m > {SmoothBoneSpreadLimit(effectiveReach):0.00}m (reach {effectiveReach:0.00}m)");
				}

				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Per-bone votes on whether the array is LOCAL (the array before the guest's in-place
	/// conversion) or MODEL, and whether the votes SPLIT in guest bone order — a converted head
	/// and a local tail is a converter preempted part-way. The flag at comp+0x14 is wrong both
	/// ways; the data is not. Never compose a local array with this reader: its element layout
	/// differs (translation first), which is the door's axis swap in the jump log.
	/// </summary>
	/// <remarks>
	/// THE VOTE IS ON BONE LENGTH, WHICH NO POSE CHANGES. A rigid bone is its rest local length
	/// from its PARENT in a model-space array, and that long from ZERO in a local one. The vote
	/// used to weigh each bone's distance from the model ORIGIN against its rest global distance,
	/// which is only true of a body standing the way it was bound: a dog with its head down
	/// digging, or lying down, carries every bone to within a local offset's length of its own
	/// origin, the vote tipped (the husky: local 46, model 40, on an array flagged and measured
	/// model), and the pose was held for as long as the dog stayed down.
	/// </remarks>
	private (int Local, int Model, bool Split) LocalVotes(in HostBridge.BonePose pose)
	{
		// A fraction of the rig, not a fixed length: what separates the two readings scales with
		// the creature, and five centimetres is wider than a beetle.
		int local = 0;
		int model = 0;
		int lastModelGuest = -1;
		int firstLocalGuest = int.MaxValue;
		float separation = Mathf.Max(_restReach * 0.02f, 1e-4f);
		_voteParents ??= VoteParents();
		for (int m = 0; m < _modelToGuest.Length; ++m)
		{
			int g = _modelToGuest[m];
			int parent = _voteParents[m];
			if (g < 0 || parent < 0)
			{
				continue;
			}

			Vector3 at = pose.Translation(g);
			float length = _rig.Translation(m).Length();
			float asLocal = Mathf.Abs(at.Length() - length);
			float asModel = Mathf.Abs((at - pose.Translation(parent)).Length() - length);
			if (Mathf.Abs(asLocal - asModel) < separation)
			{
				continue;  // says nothing either way — a child of a bone standing at the origin
			}

			if (asLocal < asModel)
			{
				++local;
				firstLocalGuest = Mathf.Min(firstLocalGuest, g);
			}
			else
			{
				++model;
				lastModelGuest = Mathf.Max(lastModelGuest, g);
			}
		}

		bool split = local >= 2 && model >= 2 && firstLocalGuest > lastModelGuest;
		return (local, model, split);
	}

	/// <summary>Each model bone's parent as a GUEST index, −1 for a root or an unmapped parent.</summary>
	private int[]? _voteParents;

	private int[] VoteParents()
	{
		var parents = new int[_modelToGuest.Length];
		for (int m = 0; m < parents.Length; ++m)
		{
			int parent = _skeleton.GetBoneParent(m);
			parents[m] = parent >= 0 && parent < _modelToGuest.Length ? _modelToGuest[parent] : -1;
		}

		return parents;
	}

	/// <summary>The node to parent a skinned mesh under.</summary>
	public Skeleton3D Node => _skeleton;

	/// <summary>
	/// Names an exploded rig once: any bone more than 4 m from the skeleton's origin after a
	/// write. A body drawn as a scarecrow is a bone-mapping fault, and which bones fly says
	/// which side mapped them — checked every 64th follow, reported once per skeleton.
	/// </summary>
	private void ProbeBonesOut(uint entity)
	{
		if (IsDisarticulated || _bonesOutReported || (++_bonesOutTicks & 63) != 0)
		{
			return;
		}

		var names = new System.Collections.Generic.List<string>();
		float worst = 0f;
		for (int i = 0; i < _skeleton.GetBoneCount(); ++i)
		{
			float d = _skeleton.GetBoneGlobalPose(i).Origin.Length();
			if (d > 4f)
			{
				worst = Mathf.Max(worst, d);
				if (names.Count < 8)
				{
					names.Add($"{_skeleton.GetBoneName(i)}@{d:0.0}m");
				}
			}
		}

		if (names.Count == 0)
		{
			return;
		}

		_bonesOutReported = true;
		GD.Print($"[rig] BONES OUT on entity 0x{entity:X8} ('{_skeleton.Name}', " +
			$"{_skeleton.GetBoneCount()} bone(s)): worst {worst:0.0}m — {string.Join(" ", names)}");
	}

	private bool _bonesOutReported;

	private int _bonesOutTicks;

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

	/// <summary>The guest's bone index for each model bone, -1 where the model bone has none.</summary>
	public int[] ModelToGuest => _modelToGuest;

	/// <summary>This rig's model bone with that name, matched the way the skeleton was built.</summary>
	public bool TryBone(string name, out int bone)
	{
		for (int m = 0; m < _rig.BoneCount; ++m)
		{
			if (string.Equals(_rig.Names[m], name, System.StringComparison.OrdinalIgnoreCase))
			{
				bone = m;
				return true;
			}
		}

		bone = -1;
		return false;
	}

	/// <summary>The rig's bone for a role, as the title names it (<see cref="Refii.Animations.Engine_TitleRig"/>).</summary>
	public bool TryBone(Refii.Animations.Engine_TitleRig.RigRole role, out int bone)
	{
		bone = Refii.Core.Engine_TitleGame.Active.Rig.Bone(_rig.Names, role);
		return bone >= 0;
	}

	/// <summary>The rig's bone names, in model order.</summary>
	public IReadOnlyList<string> BoneNames => _rig.Names;

	/// <summary>
	/// The GUEST's own pose for one bone, in this skeleton's space — a one-off read, for
	/// solving something the guest stated against its own pose. Never a per-frame path: the
	/// host plays the animation, and this is here to recover a constant, not to follow.
	///
	/// Refuses a torn or local-space sample rather than composing one: the caller can try again
	/// next frame, and a wrong answer here becomes a permanently wrong offset.
	/// </summary>
	public bool TryGuestBoneGlobal(uint entity, int modelBone, out Transform3D global)
	{
		global = Transform3D.Identity;
		if (modelBone < 0 || modelBone >= _modelToGuest.Length)
		{
			return false;
		}

		int g = _modelToGuest[modelBone];
		if (g < 0 || HostBridge.GetEntityPose(entity) is not { } pose ||
			pose.Torn || !pose.IsModelSpace || g >= pose.BoneCount)
		{
			return false;
		}

		global = ToGodot(pose.Rotation(g), pose.Translation(g), pose.Scale(g));
		return global.Origin.IsFinite();
	}

	/// <summary>The model's name for a bone, for diagnostics that have to be actionable.</summary>
	public string BoneName(int modelBone) =>
		modelBone >= 0 && modelBone < _rig.BoneCount ? _rig.Names[modelBone] : "<out of range>";

	/// <summary>The hip bone's model index: -2 unresolved, -1 this rig has none.</summary>
	/// <remarks>
	/// Resolved once, not per frame; invalidated wherever <see cref="_modelToGuest"/> is rebuilt
	/// (a re-rig on a mesh change).
	/// </remarks>
	private int _hipBone = -2;

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
		if (_hipBone == -2)
		{
			_hipBone = -1;
			for (int m = 0; m < _rig.BoneCount; ++m)
			{
				// The hip is the first real bone of every Fable rig and the one the body's
				// heading hangs off; the `*_Rig_Asset` wrapper above it is never posed.
				if (_rig.Names[m].Contains("Hip") && _modelToGuest[m] >= 0)
				{
					_hipBone = m;
					break;
				}
			}

			// A rig named by hash has no "Hip" in any name: the title's hip.
			if (_hipBone < 0 && TryBone(Refii.Animations.Engine_TitleRig.RigRole.Hip, out int hip) &&
				hip < _modelToGuest.Length && _modelToGuest[hip] >= 0)
			{
				_hipBone = hip;
			}
		}

		if (_hipBone < 0)
		{
			return float.NaN;
		}

		// MESH-space forward is M(0, 1, 0) = (0, 0, -1) — the model's own +Y, measured from
		// the sheath dummies in the `.mdl`, mapped through the mesh's basis.
		var forward = new Vector3(0f, 0f, -1f);
		Vector3 posed = _skeleton.GetBoneGlobalPose(_hipBone).Basis * forward;
		posed.Y = 0f;
		return posed.Length() < 1e-4f
			? float.NaN
			: Mathf.RadToDeg(forward.SignedAngleTo(posed.Normalized(), Vector3.Up));
	}

	private int _collapseProbeBone = -2;
	private float _collapseProbeRestY;

	/// <summary>
	/// Returns true when the rig's upper-body probe bone (Head, else Chest, else Hip) has dropped
	/// well below its standing rest height, confirming a dead body actually collapsed rather than
	/// freezing upright. Lazily resolved once on first call; never called on living entities.
	/// </summary>
	public bool IsPoseCollapsed()
	{
		if (_collapseProbeBone == -2)
		{
			_collapseProbeBone = -1;
			_collapseProbeRestY = 0f;
			ResolveCollapseProbe(Refii.Animations.Engine_TitleRig.RigRole.Head);
			if (_collapseProbeBone < 0) ResolveCollapseProbe(Refii.Animations.Engine_TitleRig.RigRole.Chest);
			if (_collapseProbeBone < 0) ResolveCollapseProbe(Refii.Animations.Engine_TitleRig.RigRole.Hip);
		}

		if (_collapseProbeBone < 0)
		{
			return false;
		}

		float posedY = _skeleton.GetBoneGlobalPose(_collapseProbeBone).Origin.Y;
		return Engine_CorpsePolicy.IsPoseCollapsed(_collapseProbeRestY, posedY);
	}

	private void ResolveCollapseProbe(Refii.Animations.Engine_TitleRig.RigRole role)
	{
		if (TryBone(role, out int bone) &&
			bone >= 0 &&
			bone < _modelToGuest.Length &&
			_modelToGuest[bone] >= 0)
		{
			float restY = _skeleton.GetBoneGlobalRest(bone).Origin.Y;
			if (float.IsFinite(restY) && restY >= Engine_CorpsePolicy.MinCollapseProbeRestHeight)
			{
				_collapseProbeBone = bone;
				_collapseProbeRestY = restY;
			}
		}
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
			? Build(model, guestNames, HostBridge.GetEntityRestPose(entity),
				HostBridge.GetEntityBoneParents(entity))
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
		Engine_MdlRig rig, string[] guestNames, HostBridge.BonePose? guestRest = null,
		int[]? guestParents = null)
	{
		// MANUAL MODIFIER PROCESSING: no Godot modifier ever drives these bones (poses are the
		// guest's, ragdolls included), but every Skeleton3D gets an inactive compatibility
		// PhysicalBoneSimulator3D child on entering the tree, and one modifier is enough for Godot
		// to run the whole modifier pass — back up every bone, process, force the dirty bones,
		// restore — on EVERY skeleton EVERY frame. Manual turns that pass off; a bone write still
		// updates the skeleton and its skin exactly as before.
		var skeleton = new Skeleton3D
		{
			Name = "Skeleton",
			ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Manual,
		};

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
		// THE MODEL'S NAMES, MADE LEGAL. The clips (Engine_AnimBuilder) map their tracks to
		// bones BY NAME, and a skeleton named bone_0.. matched only its root — every other bone
		// of every clip-driven rig fell to rest (2026-09-05). A name is sanitised of the
		// characters Godot rejects, given a suffix only when it would repeat, and replaced by
		// the index form only when empty; the count check below still guards the skin.
		var used = new HashSet<string>(rig.BoneCount);
		for (int i = 0; i < rig.BoneCount; ++i)
		{
			string name = i < rig.Names.Length ? rig.Names[i] : string.Empty;
			name = name.Replace(':', '_').Replace('/', '_').Trim();
			if (name.Length == 0)
			{
				name = $"bone_{i}";
			}

			if (!used.Add(name))
			{
				name = $"{name}_{i}";
				used.Add(name);
			}

			skeleton.AddBone(name);
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

		int[]? byTree = guestParents is { } parents && parents.Length == guestNames.Length
			? MapByTree(rig, guestNames, parents)
			: null;
		var modelToGuest = new int[rig.BoneCount];
		var unmapped = new List<string>();
		int mapped = 0;
		for (int m = 0; m < rig.BoneCount; ++m)
		{
			modelToGuest[m] = byTree is not null ? byTree[m]
				: byName.TryGetValue(rig.Names[m], out int g) ? g : -1;
			if (modelToGuest[m] >= 0)
			{
				++mapped;
			}
			else if (rig.Parents[m] >= 0)
			{
				// A model ROOT is not a miss. The guest pins it to its skeleton's root before
				// comparing any name (`sub_82B7F648` → `sub_82B7F4C8`: `stw r8, 8(r29)` stores
				// the skeleton root's index into the model root's node), the root of all 299
				// skeletons in the animation table is `Locator`, so no name ever matches it, and
				// that root's pose is stamped identity (`sub_821F57D0`) —
				// the rest every shipped model root carries (919 of 919 rigged models). Left
				// unmapped it draws at that rest, which is what the guest draws.
				//
				// NAME the miss, and say where it sits in the hierarchy. An unmapped bone keeps
				// its LOCAL rest under its parent and rides with it (`WriteBlended`, as
				// `sub_8222E2B8` composes the guest's own unmapped bones); what tears is geometry
				// weighted to it while mapped neighbours move. The child count is the tell.
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

		(float bindGap, string bindGapBone, float guestReach) = SetRest(skeleton, rig, modelToGuest, guestRest);

		// ONCE PER DISTINCT FINDING, not once per instance.
		//
		// This is a property of the MODEL, and a street holds a hundred copies of the same
		// lamp — so the run emitted a hundred identical lines and pushed everything after it
		// off the end of the log. A diagnostic that buries the rest of the report is a
		// diagnostic that costs more than it tells, and it hid the entity report three runs
		// running. Keyed on the message so a genuinely different miss still prints.
		if (unmapped.Count != 0)
		{
			string line = $"[rig] {unmapped.Count} model bone(s) with no guest bone of that " +
				$"name keep their local rest under their parent (the guest's unmapped palette " +
				$"entry, sub_8222E2B8): " + string.Join(", ", unmapped);
			if (_reportedUnmapped.Add(line))
			{
				GD.Print(line);
			}
		}

		// NO NAME MATCHED: the guest's own model→skeleton map (`sub_82B7F4C8`, CRC-32 of the
		// full name, `sub_823B0A38`) matches nothing below the root either, so the guest draws
		// this mesh at its rest whatever its skeleton plays. Both tables are printed because
		// that verdict belongs to the data — the mesh was authored on another rig's names.
		if (mapped == 0 && unmapped.Count != 0)
		{
			string line = $"[rig] model names {{{string.Join(", ", rig.Names)}}} share none " +
				$"with the guest skeleton {{{string.Join(", ", guestNames)}}} — the guest leaves " +
				$"this mesh at rest too";
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
			GuestRestReach = guestReach,
		};
	}

	/// <summary>
	/// The guest's own model→skeleton map (`sub_82B7F648`): both bone tables become trees
	/// (`sub_82B7F348`, the skeleton's from each entry's parent word), the model's root is paired
	/// with the skeleton's FIRST root whatever either is called, and below a paired node only its
	/// children are compared, by the CRC-32 of the full name (`sub_82B7F4C8`, `sub_823B0A38`). A
	/// bone whose parent did not pair is not paired, even when the skeleton has a bone of its
	/// name elsewhere — a held lantern's `FX_Particle_DummyObject` never takes the carrier's.
	/// A model root pairs only where its name is the skeleton root's (an identity either way:
	/// the guest stamps that root identity, `sub_821F57D0`).
	/// </summary>
	private static int[] MapByTree(Engine_MdlRig rig, string[] guestNames, int[] guestParents)
	{
		int guestRoot = System.Array.IndexOf(guestParents, -1);
		var children = new Dictionary<(int Parent, string Name), int>(guestNames.Length);
		for (int g = 0; g < guestNames.Length; ++g)
		{
			if (guestParents[g] >= 0 && guestNames[g].Length != 0)
			{
				children.TryAdd((guestParents[g], guestNames[g]), g);
			}
		}

		const int Unresolved = -2;
		var map = new int[rig.BoneCount];
		System.Array.Fill(map, Unresolved);

		int Resolve(int m, int depth)
		{
			if (map[m] != Unresolved)
			{
				return map[m];
			}

			int parent = rig.Parents[m];
			if (parent < 0 || parent >= rig.BoneCount || parent == m || depth > rig.BoneCount)
			{
				map[m] = guestRoot >= 0 && rig.Names[m] == guestNames[guestRoot] ? guestRoot : -1;
				return map[m];
			}

			int anchor = rig.Parents[parent] < 0 ? guestRoot : Resolve(parent, depth + 1);
			map[m] = anchor >= 0 && children.TryGetValue((anchor, rig.Names[m]), out int g) ? g : -1;
			return map[m];
		}

		for (int m = 0; m < rig.BoneCount; ++m)
		{
			Resolve(m, 0);
		}

		return map;
	}

	/// <summary>
	/// Pushes the guest's current pose into the skeleton. Cheap enough to call every frame —
	/// it is one memory read and one transform per bone.
	/// </summary>
	/// <summary>Guest-pose READ time this frame, in milliseconds; reset by the caller.</summary>
	/// <remarks>
	/// Split from the write because they are different costs with different fixes: the read is
	/// one P/Invoke over a bone array, the write is a binding call per bone into Godot. Which
	/// half dominates decides whether the answer is fewer reads or fewer writes.
	/// </remarks>
	public static double ReadMs;

	/// <summary>Bone WRITE time this frame, in milliseconds; reset by the caller.</summary>
	public static double WriteMs;

	/// <summary>
	/// Re-draws the bones at this instant, without reading the guest again.
	/// </summary>
	/// <remarks>
	/// `WriteBlended` takes its blend from WALL TIME against the newest pose, so it is built to
	/// be called every frame: alpha walks 0 toward 1 and the bones move between ticks. Reached
	/// only through <see cref="Follow"/>, it ran once per tick at the instant the pose landed —
	/// when `since` is ~0 and alpha is ~0 — so the rig was drawn from the PREVIOUS pose and
	/// never advanced toward the new one. Stale by a tick, and stepped rather than smooth.
	///
	/// Cheap when there is nothing to do: the settled check inside returns immediately once the
	/// blend has finished.
	/// </remarks>
	public void Reblend()
	{
		// The walk's write this frame already drew this instant.
		if (_blendedFrame != Refii.Core.Engine_ProcessFrame.Current)
		{
			WriteBlended();
		}
	}

	private ulong _blendedFrame = ulong.MaxValue;

	public void Follow(uint entity) => Follow(entity, write: true);

	/// <summary>Reads and stamps the guest's pose; writes the skeleton only when asked, so a
	/// far rig keeps its timeline current while writing bones at half the guest's rate.</summary>
	public void Follow(uint entity, bool write)
	{
		long from = System.Diagnostics.Stopwatch.GetTimestamp();
		FollowInner(entity);
		long mid = System.Diagnostics.Stopwatch.GetTimestamp();
		if (write)
		{
			WriteBlended();
		}
		long done = System.Diagnostics.Stopwatch.GetTimestamp();
		ProbeBonesOut(entity);

		double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
		ReadMs += (mid - from) * toMs;
		WriteMs += (done - mid) * toMs;
	}

	private void FollowInner(uint entity)
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

		// BY THE DATA, NOT THE FLAG (see LocalVotes). A split array is held; a wholly local
		// array is held until the guest's converter has run; a rig that never converts is a
		// prop holding its reference pose, logged once so it can be identified.
		//
		// NO EVIDENCE IS NOT EVIDENCE OF LOCAL, and it used to be read as such. A rig whose
		// bones cannot tell the two spaces apart returns no votes at all, and the old test
		// then fell back to `comp+0x14 == 1` and held the pose — for ever, on a flag this file
		// proves two screens down is write-only and left behind mid-pipeline. `sub_8257D8B8`
		// calls the local->model converter `sub_821F57D0` with no condition on it and only
		// then stamps 2, so an array a host reads outside the tick has been converted whatever
		// the flag says. With nothing to weigh, the proven default is the one to take.
		if (!IsDisarticulated)
		{
			(int localVotes, int modelVotes, bool split) = LocalVotes(pose);
			if (split)
			{
				++MixedPosesHeld;
				return;
			}

			if (localVotes > modelVotes)
			{
				++LocalPosesHeld;
				if (++_localReads == LocalStuckFrames)
				{
					++LocalStuckRigs;
					if (_stuckLogged.Count < 128 && _stuckLogged.Add(_rig.Names[0]))
					{
						GD.Print($"[rig-stuck] entity 0x{entity:X8} '{_skeleton.Name}' rig '{_rig.Names[0]}' bones {_guestBones} has read LOCAL for {LocalStuckFrames} frames (space={pose.Space}, votes local {localVotes} model {modelVotes}, reach {_restReach:0.000})");
					}
				}

				return;
			}
		}

		_localReads = 0;
		if (pose.Space == 1)
		{
			++FlagLocalDataModel;
		}

		if (!Plausible(pose))
		{
			++ImplausiblePoses;
			return;  // hold last frame's pose, as for a torn read
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

				// ROTATION AND TRANSLATION ONLY. The scale lane goes to the skin bind
				// (`SetBoneScales`), where `sub_8222E1C0`'s `[R * diag(s) | t]` is reproduced
				// without a scale ever entering the skeleton's parent chain.
				_composed[g] = new Transform3D(new Basis(_rotation[g]), _translation[g]);
			}
		}

		// IN THE MODEL'S ORDER, parents before children — see _modelToGuest. Bones the guest
		// does not have keep their rest, which is right for the wrapper and the attachment
		// dummies: they carry no skinning weight and nothing animates them.
		// POSE JUMPS AND CHANGES, IN DATA. A bone that moves more than 1.5 m between two
		// accepted poses is logged with the guest's values; whether the pose changed at all
		// feeds the per-rig rate the report prints.
		_lastTranslation ??= new Vector3[HostBridge.MaxBones];
		_lastRotation ??= new Quaternion[HostBridge.MaxBones];
		bool changed = false;
		++PoseApplies;
		++_myApplies;
		for (int m = 0; m < _modelToGuest.Length; ++m)
		{
			int g = _modelToGuest[m];
			if (g < 0)
			{
				continue;
			}

			// Rotation counts too: a head turn or a gesture on a leaf bone moves no translation
			// in model space, and a pose that "did not change" is never accepted or blended.
			Vector3 now = pose.Translation(g);
			Quaternion rotation = pose.Rotation(g);
			if (_lastTranslationValid && !changed &&
				HasBonePoseChanged(_lastTranslation[g], now, _lastRotation[g], rotation))
			{
				changed = true;
				++PoseChanges;
				++_myChanges;
			}

			if (_lastTranslationValid && now.DistanceTo(_lastTranslation[g]) > 1.5f)
			{
				++PoseJumps;
				if (_jumpsLogged < 12)
				{
					++_jumpsLogged;
					GD.Print($"[rig-jump] '{_skeleton.Name}' bone {g} '{BoneName(m)}' {_lastTranslation[g]} -> {now} torn={pose.Torn} space={pose.Space} tick={HostBridge.SimTick}");
				}
			}

			_lastTranslation[g] = now;
			_lastRotation[g] = rotation;
		}

		bool first = !_lastTranslationValid;
		_lastTranslationValid = true;

		// A RING OF STAMPED POSES, DRAWN AT THE WORLD'S DRAW POINT. The guest publishes a pose
		// every HF step (30 Hz) and we draw faster, so the drawn pose is interpolated between
		// the two stamped around `Engine_DrawnFrame.DrawTick` — the point the camera and every
		// clip time are drawn at. Looked up by stamp, so when the walk noticed a pose does not
		// enter. Every read is a sample: an unchanged pose is stamped as a hold of the newest
		// buffer (no copy), so a rig that stood still and then moves blends from where it stood
		// at the tick it started, not across the whole silence.
		if (_newestScale.Length != _rig.BoneCount)
		{
			_newestScale = new Vector3[_rig.BoneCount];
			System.Array.Fill(_newestScale, Vector3.One);
		}

		// Stamped with the guest's game tick (Engine_TickClock.GuestGameTick), not arrival time and
		// not `dword_83319650`. A pose is written inside the world update, so it belongs to one game
		// tick as a placement does; but `dword_83319650` skips: `sub_821D4628` branches past its own
		// increment when its object is not the head element (`821d46e8 beq cr6, loc_821D4AC8`,
		// target past the store at `821d4ac4`), which would put two distinct poses on one tick.
		uint sim = Engine_TickClock.GuestGameTick;
		if (sim == 0u)
		{
			return;  // no world yet: nothing to stamp a pose against
		}

		// The frame's stated stamp: which HF step of which tick left this pose in memory
		// (Engine_TickClock.FrameStamp). The same guest frame read again — the feed walking on
		// a frame with no new guest frame — is not a new sample. Only without a stated stamp is
		// the order of arrival used.
		double stamp = Engine_TickClock.FrameStamp;
		if (double.IsNaN(stamp))
		{
			stamp = _poses.NextHalfTick(sim);
		}
		else if (_poses.Count != 0 && stamp <= _poses.NewestStamp)
		{
			return;
		}

		if (!changed && !first && _poses.Count != 0)
		{
			int held = _slotBuffer[_poses.Slot(0)];
			_slotBuffer[_poses.Push(stamp)] = held;
			return;
		}

		int buffer = FreeBuffer();
		Transform3D[] fill = _buffers![buffer] ??= new Transform3D[_rig.BoneCount];
		++_bufferVersion[buffer];
		_slotBuffer[_poses.Push(stamp)] = buffer;
		Engine_TickClock.Observe(sim, Godot.Time.GetTicksUsec());
		for (int m = 0; m < _modelToGuest.Length; ++m)
		{
			int g = _modelToGuest[m];
			if (g < 0)
			{
				continue;
			}

			// The bone's own scale, kept beside the pose for the skin: permuted into the
			// mesh's axes exactly as ToGodot permutes it, never swizzled (see there).
			Vector3 guestScale = modelSpace ? pose.Scale(g) : _scale![g];
			var scale = modelSpace ? new Vector3(guestScale.X, guestScale.Z, guestScale.Y) : guestScale;
			_newestScale[m] = scale.IsFinite() ? scale : Vector3.One;
			fill[m] = modelSpace
				? ToGodot(pose.Rotation(g), pose.Translation(g), Vector3.One)
				: _composed![g];
		}

		TakeFoldedLanes(entity);
	}

	/// <summary>
	/// Takes the scale lanes of a pose the caller has already read, for a rig the clips drive: the
	/// pose feed (<see cref="Follow(uint, bool)"/>) is the only other reader, and it stops once the
	/// animator takes the rig, so the lanes it left would otherwise stand for good. Same lanes,
	/// same permutation and same pairing as the feed's; a torn pose, another rig's bone count or a
	/// vector the guest has not written yet states nothing.
	/// </summary>
	public void TakeStatedLanes(uint entity, HostBridge.BonePose pose)
	{
		if (pose.Torn || pose.BoneCount != _guestBones || (pose.Space != 1 && pose.Space != 2))
		{
			return;
		}

		if (_newestScale.Length != _rig.BoneCount)
		{
			_newestScale = new Vector3[_rig.BoneCount];
			System.Array.Fill(_newestScale, Vector3.One);
		}

		for (int m = 0; m < _modelToGuest.Length && m < _newestScale.Length; ++m)
		{
			int g = _modelToGuest[m];
			if (g < 0 || g >= pose.BoneCount)
			{
				continue;
			}

			// Permuted into the mesh's axes, never swizzled: see ToGodot.
			Vector3 guestScale = pose.Scale(g);
			var scale = new Vector3(guestScale.X, guestScale.Z, guestScale.Y);
			_newestScale[m] = scale.IsFinite() ? scale : Vector3.One;
		}

		TakeFoldedLanes(entity);
	}

	/// <summary>
	/// Pairs the newest lanes with the record the guest folded into them (<see cref="FoldedRecord"/>).
	/// The record is read only while lanes moved or a pairing waits, so a settled rig reads nothing.
	/// </summary>
	private void TakeFoldedLanes(uint entity)
	{
		int bones = _newestScale.Length;
		bool moved = _foldedLane.Length != bones;
		for (int m = 0; !moved && m < bones; ++m)
		{
			moved = _newestScale[m] != _foldedLane[m];
		}

		if (!moved && !_foldPending)
		{
			return;
		}

		if (_recordByGuest.Length != _guestBones)
		{
			_recordByGuest = new Vector3[_guestBones];
		}

		System.Array.Fill(_recordByGuest, Vector3.One);
		foreach (HostBridge.GuestBoneMorph morph in HostBridge.GetBoneMorph(entity))
		{
			if (morph.Bone >= 0 && morph.Bone < _guestBones)
			{
				// Permuted into the lane's axes, as the lane is.
				_recordByGuest[morph.Bone] = new Vector3(Mathf.Abs(morph.Scale.X), Mathf.Abs(morph.Scale.Z), Mathf.Abs(morph.Scale.Y));
			}
		}

		bool stable = _recordRead.Length == bones;
		if (!stable)
		{
			_recordRead = new Vector3[bones];
		}

		for (int m = 0; m < bones; ++m)
		{
			int g = m < _modelToGuest.Length ? _modelToGuest[m] : -1;
			Vector3 record = g >= 0 && g < _guestBones ? _recordByGuest[g] : Vector3.One;
			stable = stable && record == _recordRead[m];
			_recordRead[m] = record;
		}

		_foldPending = !stable;
		if (!stable)
		{
			return;
		}

		if (_foldedLane.Length != bones)
		{
			_foldedLane = new Vector3[bones];
			_foldedRecord = new Vector3[bones];
		}

		System.Array.Copy(_newestScale, _foldedLane, bones);
		System.Array.Copy(_recordRead, _foldedRecord, bones);
		++FoldedCaptures;
	}

	private Vector3[] _foldedLane = System.Array.Empty<Vector3>();
	private Vector3[] _foldedRecord = System.Array.Empty<Vector3>();

	/// <summary>The record as last read, per model bone in the lane's axes.</summary>
	private Vector3[] _recordRead = System.Array.Empty<Vector3>();

	private Vector3[] _recordByGuest = System.Array.Empty<Vector3>();

	/// <summary>Lanes moved beside a record that had just changed: read again on the next pose.</summary>
	private bool _foldPending;

	/// <summary>
	/// Stamped poses held: enough that a draw point a tick and a half behind the newest still
	/// finds its pair (two poses a tick, plus the playhead's floor of one tick).
	/// </summary>
	private const int PoseSlots = 6;

	/// <summary>One more buffer than slots, so a new pose never overwrites one still in use.</summary>
	private const int PoseBuffers = PoseSlots + 1;

	private Transform3D[]?[]? _buffers;
	private readonly int[] _bufferVersion = new int[PoseBuffers];
	private readonly Engine_StampRing _poses = new(PoseSlots);
	private readonly int[] _slotBuffer = new int[PoseSlots];

	/// <summary>A buffer no slot that survives the next push refers to.</summary>
	private int FreeBuffer()
	{
		_buffers ??= new Transform3D[]?[PoseBuffers];

		// The oldest slot is evicted by the push when the ring is full, so its buffer is free.
		int surviving = System.Math.Min(_poses.Count, PoseSlots - 1);
		for (int b = 0; b < PoseBuffers; ++b)
		{
			bool used = false;
			for (int back = 0; back < surviving; ++back)
			{
				if (_slotBuffer[_poses.Slot(back)] == b)
				{
					used = true;
					break;
				}
			}

			if (!used)
			{
				return b;
			}
		}

		return 0;
	}

	/// <summary>Each model bone's own scale from the newest pose, in the mesh's axes.</summary>
	private Vector3[] _newestScale = System.Array.Empty<Vector3>();

	private Transform3D[]? _globals;

	/// <summary>
	/// Writes the drawn pose: the stamped poses around the world's draw point, blended, every
	/// drawn frame — the display owns the clock, the guest's poses are its keyframes.
	/// LOCAL POSES, ONE PARENT INVERSE PER BONE: SetBoneGlobalPose recomputes the chain per
	/// call (quadratic per skeleton, ~25 ms of draw with every rig on screen). Unmapped bones
	/// keep their rest, composed under their parents so the inverses stay right.
	/// </summary>
	private void WriteBlended()
	{
		if (_poses.Count == 0 || _buffers is null)
		{
			return;
		}

		_blendedFrame = Refii.Core.Engine_ProcessFrame.Current;

		// AT THE WORLD'S DRAW POINT, BY STAMP. The guest does not blend a pair: `sub_82A75720`
		// assigns the same block to both `renderObj+0x200` and `+0x204` (`r4 = r29` at
		// `0x82a759f8` and `0x82a75a04`), so `sub_8222E2B8`'s lerp is the identity and it draws the
		// newest published block at the 30 Hz it publishes at. We draw faster, so blending
		// consecutive blocks is ours, and the draw point is the one the camera and the clip
		// times use (`Engine_DrawnFrame.DrawTick`). Never extrapolated: past the newest stamp the
		// newest is held, since a bone carried past its pose invents a limb position.
		//
		// A rig that was not read for many ticks and then moves: the span across that silence is
		// not a segment it animated over (Stale), so the later pose stands.
		double drawTick = Refii.Entities.Engine_DrawnFrame.DrawTick;
		Engine_StampRing.Where where = _poses.Locate(drawTick, StaleSpanTicks, out int earlier, out int later, out double t);
		int from = _slotBuffer[later];
		int to = from;
		float alpha = 1f;
		switch (where)
		{
			case Engine_StampRing.Where.Between:
				from = _slotBuffer[earlier];
				alpha = (float)t;
				++BlendedPoses;
				NoteAlpha(alpha, _poses.Stamp(0) - _poses.Stamp(1));
				break;
			case Engine_StampRing.Where.Stale:
				++HeldPoses;
				break;
			case Engine_StampRing.Where.After when _poses.Count >= 2 && drawTick > _poses.NewestStamp:
				Engine_TickClock.NoteStarved(_poses.NewestStamp);
				break;
		}

		if (_poses.Count >= 2 && !double.IsNaN(drawTick))
		{
			if (alpha <= 0.001f)
			{
				++AlphaAtZero;
			}
			else if (alpha >= 0.999f)
			{
				++AlphaAtOne;
			}
			else
			{
				++AlphaBetween;
			}
		}

		// Blending a buffer with itself (a hold) is the buffer.
		if (from == to || alpha >= 1f)
		{
			from = to;
			alpha = 1f;
		}
		else if (alpha <= 0f)
		{
			to = from;
			alpha = 1f;
		}

		Transform3D[] previous = _buffers[from]!;
		Transform3D[] target = _buffers[to]!;

		// NOTHING TO WRITE WHEN NOTHING WOULD CHANGE. A settled rig — one whose newest pose is
		// the one already on the skeleton, with the interpolation finished — would write the
		// same transform into every bone it wrote last frame. This is NOT the frustum gate that
		// was removed: that skipped rigs the camera could not see and left them in their bind
		// pose, whereas this skips only writes whose result is already there, so a rig off
		// camera still advances and still holds the pose the guest gave it.
		// Settled at the playhead's ceiling, not at 1: the blend carries past 1 while the guest is
		// late, so skipping at 1 would freeze the pose the carry exists to move. It settles once
		// alpha stops moving: at the ceiling or on a held pair.
		bool settled = alpha >= 1f && _wroteSettled &&
			_wroteBuffer == to && _wroteVersion == _bufferVersion[to];

		// THE SHADOW SKELETON TAKES THE SAME BONES, ON GUEST FRAMES (every frame for a principal).
		// A settled rig whose shadow already holds this pose has nothing to write either way; a
		// settled rig whose shadow missed the last write runs the loop for the shadow alone.
		bool shadowNow = Refii.Entities.Engine_ShadowSkeletons.WriteNow(_skeleton, out Skeleton3D? shadow) &&
			!(settled && _shadowSynced);
		if (settled && !shadowNow)
		{
			++WritesSkipped;
			return;
		}

		_globals ??= new Transform3D[_rig.BoneCount];
		_inverses ??= new Transform3D[_rig.BoneCount];
		if (_inverted is null)
		{
			_inverted = new bool[_rig.BoneCount];
		}
		else
		{
			System.Array.Clear(_inverted);
		}
		for (int m = 0; m < _modelToGuest.Length; ++m)
		{
			int parent = _rig.Parents[m];
			bool hasParent = parent >= 0 && parent < m;
			Transform3D parentGlobal = hasParent ? _globals[parent] : Transform3D.Identity;
			int g = _modelToGuest[m];
			Transform3D global;
			if (g >= 0)
			{
				global = alpha < 1f
					? previous[m].InterpolateWith(target[m], alpha)
					: target[m];
			}
			else
			{
				global = parentGlobal * ToGodot(_rig.Rotation(m), _rig.Translation(m), _rig.Scale(m));
			}

			_globals[m] = global;
			if (g < 0)
			{
				continue;
			}

			// ONE INVERSE PER PARENT, NOT PER CHILD, and none for a bone nothing asks about.
			// Bones are ordered parent-before-child, so a parent's global is final by the time
			// any child needs it: the first child that needs it pays, the rest read it.
			Transform3D local = global;
			if (hasParent)
			{
				if (!_inverted[parent])
				{
					_inverses[parent] = _globals[parent].AffineInverse();
					_inverted[parent] = true;
				}

				local = _inverses[parent] * global;
			}

			// ONE BINDING CALL, NOT THREE. `SetBonePosePosition`/`Rotation`/`Scale` are three
			// crossings into the engine per bone, and the rotation and scale forms additionally
			// cost a quaternion extraction and a per-axis scale decomposition to build arguments
			// Godot immediately recomposes. `SetBonePose` takes the transform whole.
			if (!settled)
			{
				_skeleton.SetBonePose(m, local);
			}

			if (shadowNow)
			{
				shadow!.SetBonePose(m, local);
			}
		}

		if (!settled)
		{
			// The scale lane is a property of the bone, not of the blend: the newest pose's
			// is the one the skin binds with (a morph record changes between rebuilds, not
			// between frames), and a settled rig has already written it.
			SetBoneScales(_newestScale);
			_wroteBuffer = to;
			_wroteVersion = _bufferVersion[to];
			_wroteSettled = alpha >= 1f;
			// The shadow trails this write unless it took it too.
			_shadowSynced = shadowNow;
		}
		else
		{
			_shadowSynced = true;
		}
	}

	/// <summary>
	/// Captures every live bone's current world-space position and orientation keyed by bone name so
	/// a replacement crumbled/disarticulated skeleton can seed cleanly from the live pose.
	/// </summary>
	public void CaptureWorldBones(
		Transform3D entityWorld,
		System.Collections.Generic.Dictionary<string, (Vector3 Pos, Quaternion Rot)> destination)
	{
		destination.Clear();
		for (int m = 0; m < _rig.BoneCount; ++m)
		{
			Transform3D world = entityWorld * _skeleton.GetBoneGlobalPose(m);
			if (world.Origin.IsFinite())
			{
				destination[_rig.Names[m]] = (world.Origin, world.Basis.Orthonormalized().GetRotationQuaternion());
			}
		}
	}

	/// <summary>
	/// Samples the guest's current live bone translations into Godot world space aligned by model
	/// bone index <c>m</c> (writing <c>NaN</c> for unmapped slots) for indoor floor tracking and
	/// reassembly detection, with zero heap allocations.
	/// </summary>
	public bool TrySampleGuestWorldBones(
		uint entity, Transform3D entityWorld, System.Span<Vector3> outWorldPositions, out int validCount)
	{
		validCount = 0;
		if (HostBridge.GetEntityPose(entity) is not { } pose ||
			pose.Torn ||
			pose.BoneCount != _guestBones ||
			(pose.Space != 1 && pose.Space != 2))
		{
			return false;
		}

		int limit = System.Math.Min(_modelToGuest.Length, outWorldPositions.Length);
		Vector3 invalid = new(float.NaN, float.NaN, float.NaN);
		for (int m = 0; m < limit; ++m)
		{
			outWorldPositions[m] = invalid;
			int g = _modelToGuest[m];
			if (g < 0)
			{
				continue;
			}

			Vector3 t = pose.Translation(g);
			if (!t.IsFinite() || t.Length() > MaxBoneCoordinate)
			{
				continue;
			}

			Vector3 worldPos = entityWorld * Swizzle(t);
			if (worldPos.IsFinite())
			{
				outWorldPositions[m] = worldPos;
				++validCount;
			}
		}

		return validCount > 0;
	}

	/// <summary>
	/// Writes a host-computed array of model-space bone transforms directly into the skeleton and
	/// its shadow proxy in O(N) parent order.
	/// </summary>
	public void ApplyModelSpacePoses(System.ReadOnlySpan<Transform3D> modelPoses)
	{
		int count = System.Math.Min(_rig.BoneCount, modelPoses.Length);
		if (count <= 0)
		{
			return;
		}

		_blendedFrame = Refii.Core.Engine_ProcessFrame.Current;
		_wroteSettled = false;

		bool shadowNow = Refii.Entities.Engine_ShadowSkeletons.WriteNow(_skeleton, out Skeleton3D? shadow);
		_globals ??= new Transform3D[_rig.BoneCount];
		_inverses ??= new Transform3D[_rig.BoneCount];
		if (_inverted is null)
		{
			_inverted = new bool[_rig.BoneCount];
		}
		else
		{
			System.Array.Clear(_inverted);
		}

		for (int m = 0; m < count; ++m)
		{
			Transform3D global = modelPoses[m];
			if (!global.Origin.IsFinite())
			{
				global = Transform3D.Identity;
			}

			_globals[m] = global;
		}

		for (int m = 0; m < count; ++m)
		{
			int parent = _rig.Parents[m];
			Transform3D local = _globals[m];
			if (parent >= 0 && parent < count && parent != m)
			{
				if (!_inverted[parent])
				{
					_inverses[parent] = _globals[parent].AffineInverse();
					_inverted[parent] = true;
				}

				local = _inverses[parent] * local;
			}

			_skeleton.SetBonePose(m, local);
			if (shadowNow)
			{
				shadow!.SetBonePose(m, local);
			}
		}

		_shadowSynced = shadowNow;
	}

	/// <summary>Where the pose blend's alpha lands: pinned to either end, or genuinely between.</summary>
	/// <remarks>
	/// Split three ways rather than averaged: an alpha alternating between the ends averages to a
	/// healthy-looking mid-range value.
	/// </remarks>
	public static long AlphaAtZero;

	public static long AlphaBetween;

	public static long AlphaAtOne;

	/// <summary>Rigs blended on the guest's sub-step alpha, and rigs holding a stale pair.</summary>
	public static long BlendedPoses;

	public static long HeldPoses;

	/// <summary>
	/// The last alphas any rig blended at, with the interval they were measured over — the
	/// per-frame sequence, not a histogram.
	/// </summary>
	/// <remarks>
	/// The sequence shows what the histogram cannot: smooth motion is a rising ramp that resets
	/// once per pose; a vibration is alpha moving backwards between consecutive frames.
	/// </remarks>
	private static readonly (ulong Usec, float Alpha, double Span)[] AlphaRing = new (ulong, float, double)[256];

	private static int _alphaAt;

	/// <summary>Records at most one sample per millisecond (in effect the first of each frame), so
	/// the 256-entry ring spans many frames rather than many subjects in one.</summary>
	private static void NoteAlpha(float alpha, double span)
	{
		ulong now = Godot.Time.GetTicksUsec();
		if (now - _lastNotedUsec < 1000UL)
		{
			return;
		}

		_lastNotedUsec = now;
		int at = System.Threading.Interlocked.Increment(ref _alphaAt);
		AlphaRing[(at & 255)] = (now, alpha, span);
	}

	private static ulong _lastNotedUsec;

	/// <summary>The alpha sequence as written, oldest first; for the debug channel.</summary>
	public static System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>> AlphaTrace()
	{
		var rows = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>();
		int at = _alphaAt;
		for (int back = 255; back >= 0; --back)
		{
			(ulong usec, float alpha, double span) = AlphaRing[((at - back) & 255)];
			if (usec == 0UL)
			{
				continue;
			}

			rows.Add(new System.Collections.Generic.Dictionary<string, object?>
			{
				["usec"] = usec,
				["alpha"] = alpha,
				["span"] = span,
			});
		}

		return rows;
	}

	/// <summary>Ticks of silence past which two accepted poses are not one segment.</summary>
	private const uint StaleSpanTicks = 4u;

	/// <summary>The pose buffer, and its fill, the skeleton currently holds.</summary>
	private int _wroteBuffer = -1;

	private int _wroteVersion;

	/// <summary>Whether that write was the finished pose rather than a step toward it.</summary>
	private bool _wroteSettled;

	/// <summary>
	/// True when this skeleton's newest raw pose has already been written and mirrored to its
	/// shadow skeleton (or when no valid pose samples exist to blend), so between-walk frames
	/// do not need to re-run <see cref="Reblend"/>.
	/// </summary>
	public bool IsSettled =>
		_poses.Count == 0 ||
		(_wroteSettled && _wroteBuffer == _slotBuffer[_poses.Slot(0)] &&
		 _wroteVersion == _bufferVersion[_wroteBuffer] && _shadowSynced);

	/// <summary>Writes skipped because the skeleton already held the result.</summary>
	public static long WritesSkipped;

	/// <summary>Each bone's global inverse, cached for its children — see WriteBlended.</summary>
	private Transform3D[]? _inverses;

	/// <summary>Which of those inverses have been computed this frame.</summary>
	private bool[]? _inverted;

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
	private static (float Gap, string Bone, float GuestReach) SetRest(
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

		float guestReach = 0f;
		if (guestGlobal is not null)
		{
			foreach (Transform3D bone in guestGlobal)
			{
				guestReach = Mathf.Max(guestReach, bone.Origin.Length());
			}
		}

		return (gap, gapBone, guestReach);
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

	/// <summary>How far any bone of the GUEST's rest sits from its root, composed as its live pose is.</summary>
	public float GuestRestReach { get; init; }

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
	internal static Vector3 Swizzle(Vector3 guest) => new(guest.X, guest.Z, -guest.Y);

	/// <summary>
	/// One bone's rotation, mapped into the mesh's space.
	///
	/// Same conjugation <see cref="ToGodot"/> performs — a quaternion under a change of basis
	/// is <c>M R M^-1</c>, not a component swizzle — but returned as a quaternion so the
	/// parent walk can compose rotations the way the guest does, with a quaternion PRODUCT,
	/// rather than by multiplying bases that carry scale.
	/// </summary>
	internal static Quaternion GuestRotation(Quaternion rotation)
	{
		float lengthSquared = rotation.LengthSquared();
		Basis basis = float.IsFinite(lengthSquared) && lengthSquared > 1e-8f
			? new Basis(rotation.Normalized())
			: Basis.Identity;

		return (MeshMap * basis * MeshMapInverse).GetRotationQuaternion();
	}

	/// <summary>
	/// For the debug channel: per model bone whose name contains <paramref name="filter"/>, the
	/// local rotation drawn, the `.mdl` rest, the guest's reference pose (skeleton+0x90, the pose
	/// `sub_822351B0` copies at 0x822355C4 for a bone its clip lacks) and the guest's live pose made
	/// local, each as degrees from the drawn rotation.
	/// </summary>
	public List<string> DescribeLocalSources(uint entity, string filter)
	{
		var rows = new List<string>();
		HostBridge.BonePose? reference = HostBridge.GetEntityRestPose(entity);
		Quaternion[]? referenceLocal = null;
		if (reference is { } rest)
		{
			referenceLocal = new Quaternion[rest.BoneCount];
			for (int g = 0; g < rest.BoneCount; ++g)
			{
				referenceLocal[g] = GuestRotation(rest.Rotation(g));
			}
		}

		HostBridge.BonePose? live = HostBridge.GetEntityPose(entity);
		Quaternion[]? liveLocal = null;
		if (live is { Torn: false } pose)
		{
			liveLocal = new Quaternion[pose.BoneCount];
			for (int g = 0; g < pose.BoneCount; ++g)
			{
				Quaternion own = GuestRotation(pose.Rotation(g));
				int parent = pose.Parents[g];
				liveLocal[g] = pose.IsModelSpace && parent >= 0 && parent < pose.BoneCount
					? GuestRotation(pose.Rotation(parent)).Inverse() * own
					: own;
			}
		}

		static string Degrees(Quaternion a, Quaternion b) =>
			$"{Mathf.RadToDeg(a.Normalized().AngleTo(b.Normalized())):0.0}";

		for (int m = 0; m < _skeleton.GetBoneCount(); ++m)
		{
			string name = _skeleton.GetBoneName(m);
			if (filter.Length != 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			Quaternion drawn = _skeleton.GetBonePoseRotation(m);
			Quaternion mdl = _skeleton.GetBoneRest(m).Basis.GetRotationQuaternion();
			int g = _modelToGuest[m];
			string fromReference = referenceLocal is not null && g >= 0 && g < referenceLocal.Length
				? Degrees(drawn, referenceLocal[g]) : "-";
			string fromLive = liveLocal is not null && g >= 0 && g < liveLocal.Length
				? Degrees(drawn, liveLocal[g]) : "-";
			string referenceToLive = referenceLocal is not null && liveLocal is not null && g >= 0 &&
				g < referenceLocal.Length && g < liveLocal.Length
				? Degrees(referenceLocal[g], liveLocal[g]) : "-";
			string mdlToLive = liveLocal is not null && g >= 0 && g < liveLocal.Length
				? Degrees(mdl, liveLocal[g]) : "-";
			rows.Add($"{m} '{name}' guest={g} drawn~mdl={Degrees(drawn, mdl)} drawn~ref={fromReference} " +
				$"drawn~live={fromLive} ref~live={referenceToLive} mdl~live={mdlToLive}");
		}

		return rows;
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
