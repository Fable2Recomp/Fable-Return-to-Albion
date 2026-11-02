using System;
using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Animations;

/// <summary>
/// Fable 2's rigs to the shared code: its skeletons keep the <c>.mdl</c> bone names, so a role is
/// its <c>Shadow_*</c> name; it has the host-driven ambient face; its partner is one record.
/// </summary>
public sealed class Fable2Rig : Engine_TitleRig
{
	public override string? RoleName(RigRole role) => role switch
	{
		RigRole.Hip => "Shadow_Hip",
		RigRole.SpineControl => "Shadow_SpineControl",
		RigRole.Head => "Shadow_Head",
		RigRole.Neck => "Shadow_Neck",
		RigRole.Chest => "Shadow_Chest",
		RigRole.Jaw => "Shadow_jaw",
		RigRole.LeftEye => "Shadow_L_Eye",
		RigRole.RightEye => "Shadow_R_Eye",
		RigRole.FaceRoot => "Shadow_FaceRoot",
		RigRole.LeftHeel => "Shadow_L_Heel",
		RigRole.RightHeel => "Shadow_R_Heel",
		RigRole.LeftFoot => "Shadow_L_Foot",
		RigRole.RightFoot => "Shadow_R_Foot",
		RigRole.LeftToeTip => "Shadow_L_Tip",
		RigRole.RightToeTip => "Shadow_R_Tip",
		RigRole.LeftToe => "Shadow_L_Toe",
		RigRole.RightToe => "Shadow_R_Toe",
		_ => null,
	};

	/// <summary>
	/// In the <c>.mdl</c> rigs (<c>CH_HeroMale_Morph_Body_Rig_Asset</c>) the face root is absent and
	/// the 62 facial bones (<c>Shadow_jaw</c>, <c>Shadow_m_mouth_*</c>, <c>Shadow_L_Eye</c>, ...) hang
	/// directly off <c>Shadow_Head</c>; attachment dummies (<c>Focal_*</c>, <c>Carry_*</c>,
	/// <c>FX_*</c>) and the hair, ponytail and ear chains are not facial. The loop is
	/// <c>IdleHappy</c> (7.67 s), else <c>IdleStand1</c>.
	/// </summary>
	private static readonly FaceRig AmbientFace = new(
		BonePrefix: "Shadow_",
		NonFacialBranches: new[] { "Ponytail", "Hair", "Ear" },
		PrimaryClip: 0xE903635Fu,
		FallbackClip: 0xB742E730u,
		JawRollDegrees: 4.5f,
		EyeRollDegrees: -90.0f);

	public override FaceRig Face => AmbientFace;

	/// <summary>
	/// <c>Character.Focal.Mouth|Head</c> on the head, <c>Chest|Torso</c> on the chest,
	/// <c>Eye.L|Eye.R</c> on that eye or else the head.
	/// </summary>
	public override int FocalBone(IReadOnlyList<string> boneNames, string dummy)
	{
		const string Focal = "Character.Focal.";
		if (string.IsNullOrEmpty(dummy) || !dummy.StartsWith(Focal, StringComparison.OrdinalIgnoreCase))
		{
			return -1;
		}

		string sub = dummy.Substring(Focal.Length);
		if (sub.StartsWith("Mouth", StringComparison.OrdinalIgnoreCase) ||
			sub.StartsWith("Head", StringComparison.OrdinalIgnoreCase))
		{
			return First(boneNames, "Shadow_Head", "Head");
		}

		if (sub.StartsWith("Chest", StringComparison.OrdinalIgnoreCase) ||
			sub.StartsWith("Torso", StringComparison.OrdinalIgnoreCase))
		{
			return First(boneNames, "Shadow_Chest", "Chest");
		}

		if (sub.StartsWith("Eye.L", StringComparison.OrdinalIgnoreCase))
		{
			return First(boneNames, "Shadow_L_Eye", "Shadow_Head");
		}

		if (sub.StartsWith("Eye.R", StringComparison.OrdinalIgnoreCase))
		{
			return First(boneNames, "Shadow_R_Eye", "Shadow_Head");
		}

		return -1;
	}

	private static int First(IReadOnlyList<string> boneNames, string name, string otherwise)
	{
		int bone = Find(boneNames, name);
		return bone >= 0 ? bone : Find(boneNames, otherwise);
	}

	/// <summary>`sub_82211660`: the top alone at 0.98333, the partner alone at 0.016667.</summary>
	public override float CrossfadeTopAlone => 0.98333f;

	public override float CrossfadePartnerAlone => 0.016667f;
}
