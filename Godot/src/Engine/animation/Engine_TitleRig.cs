using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Animations;

/// <summary>
/// What a title's rigs are to the shared animation and entity code: which bone plays a role, the
/// name its Godot skeleton gives a bone its own rig tables name, and the title's own rules for the
/// face, the crossfade and the pose check. The shell asks through
/// <see cref="Refii.Core.Engine_TitleGame.Rig"/>; the default names nothing and adds nothing.
/// </summary>
/// <remarks>
/// A FEATURE RUNS ON A TITLE BECAUSE ITS RIG SAYS SO. A shared feature that needs a bone asks for
/// it by role and does nothing when the title names none; a title-only behaviour is a member here
/// that the other title leaves at its default.
/// </remarks>
public class Engine_TitleRig
{
	/// <summary>A title whose rigs the shared code knows nothing about.</summary>
	public static readonly Engine_TitleRig None = new();

	/// <summary>A bone a shared feature asks for by what it does.</summary>
	public enum RigRole
	{
		Hip,
		SpineControl,
		Head,
		Neck,
		Chest,
		Jaw,
		LeftEye,
		RightEye,
		FaceRoot,
		LeftHeel,
		RightHeel,
		LeftFoot,
		RightFoot,
		LeftToeTip,
		RightToeTip,
		LeftToe,
		RightToe,
	}

	/// <summary>The name the title's rig tables give a role's bone, or null when they name none.</summary>
	public virtual string? RoleName(RigRole role) => null;

	/// <summary>The Godot skeleton's name for the title's bone for <paramref name="role"/>, or null when it names none.</summary>
	public string? BoneName(RigRole role) => RoleName(role) is { } name ? BoneName(name) : null;

	/// <summary>
	/// The Godot skeleton's name for a bone the title's rig tables name (<c>Shadow_Neck</c>): the
	/// name itself for a title whose skeletons keep names.
	/// </summary>
	public virtual string BoneName(string rigName) => rigName;

	/// <summary>Whether a Godot bone name is the title's bone for <paramref name="role"/>, ignoring case.</summary>
	public bool Is(string boneName, RigRole role) =>
		RoleName(role) is { } name && string.Equals(boneName, BoneName(name), StringComparison.OrdinalIgnoreCase);

	/// <summary>The first of <paramref name="boneNames"/> that is the title's bone for <paramref name="role"/>, or -1.</summary>
	public int Bone(IReadOnlyList<string> boneNames, RigRole role)
	{
		if (RoleName(role) is not { } name)
		{
			return -1;
		}

		return Find(boneNames, BoneName(name));
	}

	/// <summary>The skeleton's bone for <paramref name="role"/>, or -1.</summary>
	public int Bone(Skeleton3D skeleton, RigRole role)
	{
		if (RoleName(role) is not { } name)
		{
			return -1;
		}

		string bone = BoneName(name);
		for (int b = 0; b < skeleton.GetBoneCount(); b++)
		{
			if (string.Equals(skeleton.GetBoneName(b), bone, StringComparison.OrdinalIgnoreCase))
			{
				return b;
			}
		}

		return -1;
	}

	/// <summary>
	/// The skeleton's first bone, in bone order, that is the title's bone for
	/// <paramref name="role"/> or for <paramref name="otherwise"/>, or -1.
	/// </summary>
	public int Bone(Skeleton3D skeleton, RigRole role, RigRole otherwise)
	{
		string? first = BoneName(role);
		string? second = BoneName(otherwise);
		if (first is null && second is null)
		{
			return -1;
		}

		for (int b = 0; b < skeleton.GetBoneCount(); b++)
		{
			string name = skeleton.GetBoneName(b);
			if ((first is not null && string.Equals(name, first, StringComparison.OrdinalIgnoreCase)) ||
				(second is not null && string.Equals(name, second, StringComparison.OrdinalIgnoreCase)))
			{
				return b;
			}
		}

		return -1;
	}

	/// <summary>The first of <paramref name="boneNames"/> equal to <paramref name="name"/> ignoring case, or -1.</summary>
	public static int Find(IReadOnlyList<string> boneNames, string name)
	{
		for (int b = 0; b < boneNames.Count; b++)
		{
			if (string.Equals(boneNames[b], name, StringComparison.OrdinalIgnoreCase))
			{
				return b;
			}
		}

		return -1;
	}

	// ---------------------------------------------------------------- the face

	/// <summary>
	/// The title's facial bones, whose rotations the animator takes from the guest's published
	/// pose. Null when the title names none, and no facial bone is marked.
	/// </summary>
	public virtual FaceRig? Face => null;

	/// <summary>
	/// A title's facial bones: the ones under the face root (or the head) named with
	/// <paramref name="BonePrefix"/>, outside any branch a name in
	/// <paramref name="NonFacialBranches"/> marks.
	/// </summary>
	public sealed record FaceRig(string BonePrefix, IReadOnlyList<string> NonFacialBranches);

	// ---------------------------------------------------------------- attachments

	/// <summary>
	/// Attachment of a focal dummy (<c>Focal_Eye</c> or <c>Character.Focal.*</c>) when no part
	/// <c>.mdl.gmd</c> binds it to a live rig bone: resolves through <see cref="RoleName"/> and
	/// <see cref="BoneName(string)"/> so both named rigs (Fable 2) and hashed rigs (Fable 3) share
	/// one implementation.
	/// </summary>
	public virtual int FocalBone(IReadOnlyList<string> boneNames, string dummy) =>
		FocalBone(boneNames, dummy, isPosed: null);

	/// <summary>
	/// Resolves a focal dummy to a bone index in <paramref name="boneNames"/>, preferring posed
	/// bones when <paramref name="isPosed"/> is supplied.
	/// </summary>
	public virtual int FocalBone(IReadOnlyList<string> boneNames, string dummy, Func<int, bool>? isPosed)
	{
		if (boneNames is null || boneNames.Count == 0 || string.IsNullOrEmpty(dummy))
		{
			return -1;
		}

		if (string.Equals(dummy, "Focal_Eye", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(dummy, "Focal_Eye_DummyObject", StringComparison.OrdinalIgnoreCase))
		{
			return ResolveFocalRole(boneNames, RigRole.Head, "Head", isPosed);
		}

		const string Focal = "Character.Focal.";
		if (!dummy.StartsWith(Focal, StringComparison.OrdinalIgnoreCase))
		{
			return -1;
		}

		string sub = dummy.Substring(Focal.Length);
		if (sub.StartsWith("Mouth", StringComparison.OrdinalIgnoreCase) ||
			sub.StartsWith("Head", StringComparison.OrdinalIgnoreCase))
		{
			return ResolveFocalRole(boneNames, RigRole.Head, "Head", isPosed);
		}

		if (sub.StartsWith("Chest", StringComparison.OrdinalIgnoreCase) ||
			sub.StartsWith("Torso", StringComparison.OrdinalIgnoreCase))
		{
			return ResolveFocalRole(boneNames, RigRole.Chest, "Chest", isPosed);
		}

		if (sub.StartsWith("Eye.L", StringComparison.OrdinalIgnoreCase) ||
			sub.StartsWith("Eye_L", StringComparison.OrdinalIgnoreCase))
		{
			int eye = ResolveRoleOnly(boneNames, RigRole.LeftEye, isPosed);
			return eye >= 0 ? eye : ResolveFocalRole(boneNames, RigRole.Head, "Head", isPosed);
		}

		if (sub.StartsWith("Eye.R", StringComparison.OrdinalIgnoreCase) ||
			sub.StartsWith("Eye_R", StringComparison.OrdinalIgnoreCase))
		{
			int eye = ResolveRoleOnly(boneNames, RigRole.RightEye, isPosed);
			return eye >= 0 ? eye : ResolveFocalRole(boneNames, RigRole.Head, "Head", isPosed);
		}

		if (sub.StartsWith("Eye", StringComparison.OrdinalIgnoreCase))
		{
			return ResolveFocalRole(boneNames, RigRole.Head, "Head", isPosed);
		}

		return -1;
	}

	private int ResolveRoleOnly(IReadOnlyList<string> boneNames, RigRole role, Func<int, bool>? isPosed)
	{
		int bone = Bone(boneNames, role);
		if (bone >= 0 && (isPosed is null || isPosed(bone)))
		{
			return bone;
		}

		return -1;
	}

	private int ResolveFocalRole(IReadOnlyList<string> boneNames, RigRole role, string fallbackName, Func<int, bool>? isPosed)
	{
		int roleBone = Bone(boneNames, role);
		if (roleBone >= 0 && (isPosed is null || isPosed(roleBone)))
		{
			return roleBone;
		}

		int named = Find(boneNames, fallbackName);
		if (named >= 0 && (isPosed is null || isPosed(named)))
		{
			return named;
		}

		return roleBone >= 0 ? roleBone : named;
	}

	/// <summary>
	/// Returns true when an entity transitions from a composite character (<paramref name="prevModelHash"/>
	/// == 0) to a single crumbled-bones model (<paramref name="newModelHash"/> != 0) whose skeleton
	/// consists of multiple independent disarticulated roots or matches a crumble path convention.
	/// </summary>
	public virtual bool IsDisarticulatedCrumble(
		uint prevModelHash, uint newModelHash, string? modelPath, ReadOnlySpan<int> parents)
	{
		if (prevModelHash != 0u || newModelHash == 0u || parents.Length < 4)
		{
			return false;
		}

		if (!string.IsNullOrEmpty(modelPath) &&
			(modelPath.Contains("crumble", StringComparison.OrdinalIgnoreCase) ||
			 modelPath.Contains("skeleton_pieces", StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}

		// A crumbled bone-pile rig attaches its bones directly under root (-1 or 0), whereas
		// articulated humanoid skeletons chain limbs through a single Hip -> Spine hierarchy.
		int independentPieces = 0;
		bool rootIsZero = parents[0] < 0;
		for (int i = 0; i < parents.Length; ++i)
		{
			int p = parents[i];
			if (p < 0 || (rootIsZero && p == 0))
			{
				if (++independentPieces >= 6)
				{
					return true;
				}
			}
		}

		return false;
	}

	// ---------------------------------------------------------------- the guest's pick

	/// <summary>
	/// Whether the title's bridge states the whole primary pick — the top, every member of its
	/// partner set and each one's share as its weight pair — so the animator elects nothing itself.
	/// </summary>
	public virtual bool StatesPartnerSet => false;

	/// <summary>The top record's share at or above which the guest keeps it alone.</summary>
	public virtual float CrossfadeTopAlone => 1f;

	/// <summary>The top record's share at or below which the guest keeps the partner alone.</summary>
	public virtual float CrossfadePartnerAlone => 0f;

	// ---------------------------------------------------------------- the pose check

	/// <summary>
	/// Whether a live pose is bounded by the guest skeleton's own rest reach as well as the
	/// model's, for a title whose skeletons rest bones where its model files do not.
	/// </summary>
	public virtual bool ReachIncludesGuestRest => false;
}
