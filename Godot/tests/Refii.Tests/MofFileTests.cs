using System.Numerics;
using System.Text;
using GdUnit4;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Engine_MofFile against a synthetic Fable 3 morph offset file laid out as skeletalmorphs.bnk ships them.</summary>
[TestSuite]
public sealed class MofFileTests
{
    private static string Synthetic(string newline = "\n", int declared = 2) =>
        string.Join(newline, new[]
        {
            "#Morph Offset File",
            $"ShadowBonesCount:{declared}",
            "ShadowBone:Shadow_Hip",
            "PosX:0.0",
            "PosY:-0.0",
            "PosZ:0.014",
            "SclX:1.303",
            "SclY:1.0",
            "SclZ:1.303",
            "ShadowBone:Shadow_SpineControl",
            "PosX:0",
            "PosY:1.16415",
            "PosZ:0",
            "SclX:1.1",
            "SclY:1.1",
            "SclZ:1.1",
        }) + newline;

    [TestCase]
    public void ParsesEveryBoneInOrder()
    {
        Engine_MofFile mof = Engine_MofFile.Parse(Encoding.ASCII.GetBytes(Synthetic()));

        AssertThat(mof.HasHeader).IsTrue();
        AssertThat(mof.DeclaredCount).IsEqual(2);
        AssertThat(mof.Bones.Count).IsEqual(2);

        Engine_MofFile.Bone hip = mof.Bones[0];
        AssertThat(hip.Name).IsEqual("Shadow_Hip");
        AssertThat(hip.Offset).IsEqual(new Vector3(0.0f, -0.0f, 0.014f));
        AssertThat(hip.Scale).IsEqual(new Vector3(1.303f, 1.0f, 1.303f));

        Engine_MofFile.Bone spine = mof.Bones[1];
        AssertThat(spine.Name).IsEqual("Shadow_SpineControl");
        AssertThat(spine.Offset).IsEqual(new Vector3(0.0f, 1.16415f, 0.0f));
        AssertThat(spine.Scale).IsEqual(new Vector3(1.1f, 1.1f, 1.1f));
    }

    [TestCase]
    public void CrlfAndLookupAndIdentity()
    {
        Engine_MofFile mof = Engine_MofFile.Parse(Synthetic("\r\n"));

        AssertThat(mof.Bones.Count).IsEqual(2);
        AssertThat(mof.Find("shadow_spinecontrol")).IsNotNull();
        AssertThat(mof.Find("Shadow_Nose")).IsNull();
        AssertThat(Engine_MofFile.IsIdentity(mof.Bones[0])).IsFalse();

        Engine_MofFile.Bone rest = new("Rest", Vector3.Zero, Vector3.One);
        AssertThat(Engine_MofFile.IsIdentity(rest)).IsTrue();
    }

    [TestCase]
    public void MissingLanesKeepDefaultsAndDeclaredCountIsReportedNotTrusted()
    {
        string text = "ShadowBonesCount:5\nShadowBone:Only\nSclY:2.5\nBogus:1\nPosZ:x\n";
        Engine_MofFile mof = Engine_MofFile.Parse(text);

        AssertThat(mof.HasHeader).IsFalse();
        AssertThat(mof.DeclaredCount).IsEqual(5);
        AssertThat(mof.Bones.Count).IsEqual(1);
        AssertThat(mof.Bones[0].Offset).IsEqual(Vector3.Zero);
        AssertThat(mof.Bones[0].Scale).IsEqual(new Vector3(1.0f, 2.5f, 1.0f));
    }
}
