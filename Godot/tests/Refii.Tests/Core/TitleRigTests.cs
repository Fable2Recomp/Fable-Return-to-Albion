namespace Refii.Tests.Core;

using GdUnit4;
using Refii.Animations;
using Refii.Titles.Fable2;
using Refii.Titles.Fable3;
using static GdUnit4.Assertions;

/// <summary>
/// Each title's rig answers the shared code in its own naming: Fable 2's skeletons keep the
/// <c>Shadow_*</c> names, Fable 3's are named "0x%08X" by the CRC-32 its binary looks names up with.
/// </summary>
[TestSuite]
public class TitleRigTests
{
	private static readonly string[] Fable3Bones =
	{
		"0x4A91790F", // the model root
		"0x6DAD352A", // Shadow_Hip
		"0x88CEDCD0", // Shadow_SpineControl
		"0xD6A89021", // Shadow_Spine
		"0xCBCA1BD6", // Shadow_Head
		"0x00D9B80F", // Shadow_jaw
		"0xF89D1408", // Shadow_L_Eye
		"0x274D3DEB", // Shadow_R_Eye
	};

	/// <summary>
	/// The hash is the guest's: `sub_823F04B8` is CRC-32 of the unmodified name, table 0x82064E40,
	/// initial 0, no final xor, so "Locator" is the 0x2897988D Fable 3's clips root on and
	/// "Shadow_SpineControl" the 0x88CEDCD0 its `.mdl.gmd` files carry.
	/// </summary>
	[TestCase]
	public void Fable3NamesBonesByTheHashItsBinaryUses()
	{
		var rig = new Fable3Rig();
		AssertThat(rig.BoneName("Locator")).IsEqual("0x2897988D");
		AssertThat(rig.BoneName("Shadow_SpineControl")).IsEqual("0x88CEDCD0");
		AssertThat(rig.Bone(Fable3Bones, Engine_TitleRig.RigRole.Hip)).IsEqual(1);
		AssertThat(rig.Bone(Fable3Bones, Engine_TitleRig.RigRole.SpineControl)).IsEqual(2);
		AssertThat(rig.Bone(Fable3Bones, Engine_TitleRig.RigRole.Head)).IsEqual(4);
		AssertThat(rig.Bone(Fable3Bones, Engine_TitleRig.RigRole.FaceRoot)).IsEqual(-1);
		AssertThat(rig.Is("Shadow_Hip", Engine_TitleRig.RigRole.Hip)).IsFalse();
	}

	[TestCase]
	public void Fable2NamesBonesByTheirRigNames()
	{
		var rig = new Fable2Rig();
		string[] bones = { "CH_Hero_Rig_Asset", "Shadow_Hip", "Shadow_SpineControl", "Shadow_Head" };
		AssertThat(rig.BoneName("Shadow_Neck")).IsEqual("Shadow_Neck");
		AssertThat(rig.Bone(bones, Engine_TitleRig.RigRole.Hip)).IsEqual(1);
		AssertThat(rig.Bone(bones, Engine_TitleRig.RigRole.SpineControl)).IsEqual(2);
		AssertThat(rig.Is("shadow_head", Engine_TitleRig.RigRole.Head)).IsTrue();
		AssertThat(rig.Bone(Fable3Bones, Engine_TitleRig.RigRole.Hip)).IsEqual(-1);
	}

	/// <summary>
	/// The feet and eyes by role: Fable 2 by name, heel before foot and tip before toe; Fable 3 by
	/// the hash its models carry, and no foot bone, which none of its models has.
	/// </summary>
	[TestCase]
	public void EachTitleNamesItsFeetAndEyes()
	{
		var fable2 = new Fable2Rig();
		var fable3 = new Fable3Rig();
		AssertThat(fable2.BoneName(Engine_TitleRig.RigRole.LeftHeel)).IsEqual("Shadow_L_Heel");
		AssertThat(fable2.BoneName(Engine_TitleRig.RigRole.RightFoot)).IsEqual("Shadow_R_Foot");
		AssertThat(fable2.BoneName(Engine_TitleRig.RigRole.LeftToeTip)).IsEqual("Shadow_L_Tip");
		AssertThat(fable2.BoneName(Engine_TitleRig.RigRole.RightToe)).IsEqual("Shadow_R_Toe");
		AssertThat(fable2.BoneName(Engine_TitleRig.RigRole.LeftEye)).IsEqual("Shadow_L_Eye");
		AssertThat(fable3.BoneName(Engine_TitleRig.RigRole.LeftHeel)).IsEqual("0xAD71BA9C");
		AssertThat(fable3.BoneName(Engine_TitleRig.RigRole.RightHeel)).IsEqual("0x94ADD977");
		AssertThat(fable3.BoneName(Engine_TitleRig.RigRole.RightToeTip)).IsEqual("0x1DB60216");
		AssertThat(fable3.BoneName(Engine_TitleRig.RigRole.LeftToe)).IsEqual("0xF9E16898");
		AssertThat(fable3.BoneName(Engine_TitleRig.RigRole.LeftFoot)).IsNull();
		AssertThat(fable3.BoneName(Engine_TitleRig.RigRole.Neck)).IsEqual("0x4C283B19");
		AssertThat(fable3.BoneName(Engine_TitleRig.RigRole.RightEye)).IsEqual("0x274D3DEB");
		AssertThat(Engine_TitleRig.None.BoneName(Engine_TitleRig.RigRole.Head)).IsNull();
	}

	[TestCase]
	public void FocalFallbackIsFable2s()
	{
		string[] bones = { "Shadow_Hip", "Shadow_Chest", "Shadow_Head", "Shadow_L_Eye" };
		var fable2 = new Fable2Rig();
		AssertThat(fable2.FocalBone(bones, "Character.Focal.Mouth")).IsEqual(2);
		AssertThat(fable2.FocalBone(bones, "Character.Focal.Torso")).IsEqual(1);
		AssertThat(fable2.FocalBone(bones, "Character.Focal.Eye.L")).IsEqual(3);
		AssertThat(fable2.FocalBone(bones, "Character.Focal.Eye.R")).IsEqual(2);
		AssertThat(fable2.FocalBone(bones, "Carry_Hand_DummyObject1")).IsEqual(-1);
		AssertThat(new Fable3Rig().FocalBone(Fable3Bones, "Character.Focal.Head")).IsEqual(-1);
	}

	[TestCase]
	public void EachTitleStatesItsPickRules()
	{
		var fable2 = new Fable2Rig();
		var fable3 = new Fable3Rig();
		AssertThat(fable2.StatesPartnerSet).IsFalse();
		AssertThat(fable3.StatesPartnerSet).IsTrue();
		AssertThat(fable2.CrossfadeTopAlone).IsEqual(0.98333f);
		AssertThat(fable2.CrossfadePartnerAlone).IsEqual(0.016667f);
		AssertThat(fable3.CrossfadeTopAlone).IsEqual(0.95f);
		AssertThat(fable3.CrossfadePartnerAlone).IsEqual(0.05f);
		AssertThat(fable2.ReachIncludesGuestRest).IsFalse();
		AssertThat(fable3.ReachIncludesGuestRest).IsTrue();
	}
}
