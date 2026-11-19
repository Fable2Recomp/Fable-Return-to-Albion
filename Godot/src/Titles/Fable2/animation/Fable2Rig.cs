using System;
using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Animations;

/// <summary>
/// Fable 2's rigs to the shared code: its skeletons keep the <c>.mdl</c> bone names, so a role is
/// its <c>Shadow_*</c> name; its partner is one record.
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

	/// <summary>`sub_82211660`: the top alone at 0.98333, the partner alone at 0.016667.</summary>
	public override float CrossfadeTopAlone => 0.98333f;

	public override float CrossfadePartnerAlone => 0.016667f;
}
