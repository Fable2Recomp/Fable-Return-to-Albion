using System;
using GdUnit4;
using Godot;
using Refii.Animations;
using Refii.Entities;
using Refii.Models;
using Refii.Titles.Fable2;
using Refii.Titles.Fable3;
using static GdUnit4.Assertions;

namespace Refii.Tests;

[TestSuite]
public sealed class Engine_TitleRigFocalTests
{
    private static readonly string[] Fable2Bones =
    {
        "Locator",
        "Shadow_Chest",
        "Shadow_Head",
        "Shadow_L_Eye",
        "Shadow_R_Eye",
    };

    private static readonly string[] Fable3Bones =
    {
        Engine_GmdSkeleton.HashedNodeName("Locator"),
        Engine_GmdSkeleton.HashedNodeName("Shadow_Chest"),
        Engine_GmdSkeleton.HashedNodeName("Shadow_Head"),
        Engine_GmdSkeleton.HashedNodeName("Shadow_L_Eye"),
        Engine_GmdSkeleton.HashedNodeName("Shadow_R_Eye"),
    };

    [TestCase]
    public void Fable2AndFable3ResolveAllFocalVariantsViaSharedImplementation()
    {
        Engine_TitleRig[] rigs = { new Fable2Rig(), new Fable3Rig() };
        string[][] boneSets = { Fable2Bones, Fable3Bones };

        for (int t = 0; t < rigs.Length; ++t)
        {
            Engine_TitleRig rig = rigs[t];
            string[] bones = boneSets[t];

            AssertThat(rig.FocalBone(bones, "Focal_Eye")).IsEqual(2);
            AssertThat(rig.FocalBone(bones, "Focal_Eye_DummyObject")).IsEqual(2);
            AssertThat(rig.FocalBone(bones, "Character.Focal.Mouth")).IsEqual(2);
            AssertThat(rig.FocalBone(bones, "Character.Focal.Head")).IsEqual(2);
            AssertThat(rig.FocalBone(bones, "Character.Focal.Chest")).IsEqual(1);
            AssertThat(rig.FocalBone(bones, "Character.Focal.Torso")).IsEqual(1);
            AssertThat(rig.FocalBone(bones, "Character.Focal.Eye.Left")).IsEqual(3);
            AssertThat(rig.FocalBone(bones, "Character.Focal.Eye.Right")).IsEqual(4);
            AssertThat(rig.FocalBone(bones, "Character.Focal.Eye")).IsEqual(2);
            AssertThat(rig.FocalBone(bones, "Unrelated_Dummy")).IsEqual(-1);
        }
    }

    [TestCase]
    public void EyeFallbacksToHeadWhenMissingOrUnposed()
    {
        Engine_TitleRig[] rigs = { new Fable2Rig(), new Fable3Rig() };
        string[][] noEyeBones =
        {
            new[] { "Locator", "Shadow_Chest", "Shadow_Head" },
            new[]
            {
                Engine_GmdSkeleton.HashedNodeName("Locator"),
                Engine_GmdSkeleton.HashedNodeName("Shadow_Chest"),
                Engine_GmdSkeleton.HashedNodeName("Shadow_Head"),
            },
        };
        string[][] fullBones = { Fable2Bones, Fable3Bones };

        for (int t = 0; t < rigs.Length; ++t)
        {
            Engine_TitleRig rig = rigs[t];
            AssertThat(rig.FocalBone(noEyeBones[t], "Character.Focal.Eye.Left")).IsEqual(2);
            AssertThat(rig.FocalBone(noEyeBones[t], "Character.Focal.Eye.Right")).IsEqual(2);

            // When L/R eye bone exists in the rig array but is not driven/posed by guest (e.g. hollowman socket),
            // FocalBone must fall back cleanly to the posed Head bone.
            Func<int, bool> headPosedOnly = bone => bone == 2 || bone == 1;
            AssertThat(rig.FocalBone(fullBones[t], "Character.Focal.Eye.Left", headPosedOnly)).IsEqual(2);
            AssertThat(rig.FocalBone(fullBones[t], "Character.Focal.Eye.Right", headPosedOnly)).IsEqual(2);
        }
    }

    [TestCase]
    public void ComputeFocalMountPreservesHeadLocalSocketAndRejectsOutOfRange()
    {
        var headRest = new Transform3D(Basis.Identity, new Vector3(0f, 1.70f, 0f));
        var eyeModelRest = new Transform3D(Basis.Identity, new Vector3(0.04f, 1.76f, -0.09f));

        Transform3D localMount = Engine_EntityAttachments.ComputeFocalMount(eyeModelRest, headRest);
        AssertThat(localMount.Origin.DistanceTo(new Vector3(0.04f, 0.06f, -0.09f))).IsLess(1e-5f);

        // Origin far from head (>= 0.75 m) resets to Identity so eye never floats at floor height.
        var farDummy = new Transform3D(Basis.Identity, new Vector3(0f, 0.2f, 0f));
        AssertThat(Engine_EntityAttachments.ComputeFocalMount(farDummy, headRest)).IsEqual(Transform3D.Identity);

        // Degenerate zero-determinant rest falls back to Identity safely.
        var degenerateRest = new Transform3D(Basis.Identity.Scaled(Vector3.Zero), Vector3.Zero);
        AssertThat(Engine_EntityAttachments.ComputeFocalMount(eyeModelRest, degenerateRest)).IsEqual(Transform3D.Identity);
    }
}
