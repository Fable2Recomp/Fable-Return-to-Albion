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
	/// The host-driven ambient face: an idle face loop sampled into a character's facial bones
	/// while its own clips pose none. Null when the title has none, and no facial bone is marked.
	/// </summary>
	public virtual FaceRig? Face => null;

	/// <summary>
	/// A title's facial bones and the ambient face they take: the facial bones are the ones under
	/// the face root (or the head) named with <paramref name="BonePrefix"/>, outside any branch a
	/// name in <paramref name="NonFacialBranches"/> marks; the loop is
	/// <paramref name="PrimaryClip"/>, else <paramref name="FallbackClip"/>; an unposed jaw and eye
	/// rest at their rest pitch and yaw with the stated roll.
	/// </summary>
	public sealed record FaceRig(
		string BonePrefix,
		IReadOnlyList<string> NonFacialBranches,
		uint PrimaryClip,
		uint FallbackClip,
		float JawRollDegrees,
		float EyeRollDegrees);

	/// <summary>
	/// Attachment of a dummy no part's <c>.mdl.gmd</c> names, by the dummy's name: the rig bone in
	/// <paramref name="boneNames"/> the title mounts it on, or -1 when it mounts none.
	/// </summary>
	public virtual int FocalBone(IReadOnlyList<string> boneNames, string dummy) => -1;

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
