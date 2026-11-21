namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Refii.Animations;

/// <summary>
/// Fable 3's rigs to the shared code. Its model files name a bone only by the hash of its rig name
/// (<see cref="Refii.Models.Engine_GmdSkeleton.HashedNodeName(string)"/>), so a role is that hash of
/// the <c>Shadow_*</c> name the binary looks up.
/// </summary>
/// <remarks>
/// <para>THE HIP'S CHILDREN STAY PUT here as in Fable 2. `sub_82272918` resolves "Shadow_Hip" and
/// "Shadow_SpineControl" through `sub_8226D318` (crc32 `sub_823F04B8` of the name, compared with
/// each skeleton entry's hash in `sub_8222AAE8`) and keeps up to six hip children but the spine
/// control at +128, as Fable 2's `sub_8229CD30` does at +116; the overlay blend `sub_82206348`
/// (0x8220679C..0x82206B3C) remembers the hip, blends, and rewrites those children as Fable 2's
/// `sub_8220CB18` (0x8220CC08..) does. Both roles are named, so the shared compensation runs.</para>
/// <para>NO FACE MASK. Fable 3's rigs carry another face (`Shadow_L_jaw`, `Shadow_R_jaw`, brows and
/// lids at 0x820C4100..); <see cref="Face"/> stays null, so no facial bone is marked.</para>
/// <para>FOCAL ATTACHMENTS are inherited from <see cref="Engine_TitleRig.FocalBone"/> through
/// <see cref="RoleName"/> and <see cref="BoneName"/>, mapping <c>Focal_Eye</c> and
/// <c>Character.Focal.*</c> onto Fable 3's hashed skeleton bones.</para>
/// </remarks>
public sealed class Fable3Rig : Engine_TitleRig
{
	/// <summary>
	/// The rig names this binary looks up, by role, and the foot bones its character models carry:
	/// of 4773 models in <c>globals_models.bnk</c>, 760 carry the hash of <c>Shadow_L_Heel</c> and
	/// <c>Shadow_R_Heel</c>, 264 of <c>Shadow_L_Tip</c> / <c>Shadow_R_Tip</c>, 4 of
	/// <c>Shadow_L_Toe</c> / <c>Shadow_R_Toe</c>, and none of <c>Shadow_L_Foot</c> /
	/// <c>Shadow_R_Foot</c> or a face root.
	/// </summary>
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
		RigRole.LeftHeel => "Shadow_L_Heel",
		RigRole.RightHeel => "Shadow_R_Heel",
		RigRole.LeftToeTip => "Shadow_L_Tip",
		RigRole.RightToeTip => "Shadow_R_Tip",
		RigRole.LeftToe => "Shadow_L_Toe",
		RigRole.RightToe => "Shadow_R_Toe",
		_ => null,
	};

	private static readonly Dictionary<string, string> Hashed = new();

	/// <summary>The Godot bone name, "0x%08X" of the rig name's hash.</summary>
	public override string BoneName(string rigName)
	{
		lock (Hashed)
		{
			if (!Hashed.TryGetValue(rigName, out string? name))
			{
				name = Refii.Models.Engine_GmdSkeleton.HashedNodeName(rigName);
				Hashed[rigName] = name;
			}

			return name;
		}
	}

	/// <summary>
	/// The bridge states the whole pick (Fable3/src/hostbridge/src/anim_feed.cpp): the top, every
	/// member of its partner set (`sub_826BC358`) and each one's share as its weight pair.
	/// </summary>
	public override bool StatesPartnerSet => true;

	/// <summary>
	/// No partner is built when `|w − 1| &lt; 0.05` (`sub_821EB068`) and the crossfade node keeps one
	/// side when `min(w, 1 − w) ≤ 0.05` (`sub_8222AEF8` case 1, 0x8222b4e0).
	/// </summary>
	public override float CrossfadeTopAlone => 0.95f;

	public override float CrossfadePartnerAlone => 0.05f;

	/// <summary>
	/// The skeleton's own rest places bones the model file does not: model 86BC6DE7's bone
	/// 0x4E8B85EC rests at 1.48 m with scale 2^-15 (skel+0x9C) against a model reach of 0.25.
	/// </summary>
	public override bool ReachIncludesGuestRest => true;
}
