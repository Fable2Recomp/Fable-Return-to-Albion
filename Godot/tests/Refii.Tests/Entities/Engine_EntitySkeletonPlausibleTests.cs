using GdUnit4;
using Godot;
using Refii.Animations;
using Refii.Entities;
using static GdUnit4.Assertions;

namespace Refii.Tests;

[TestSuite]
public sealed class Engine_EntitySkeletonPlausibleTests
{
    [TestCase]
    public void IsPlausibleBoneAllowsScatteredPosesBeyondFourMetersAndRejectsFaults()
    {
        foreach (float distance in new[] { 5f, 25f, 500f })
        {
            bool ok = Engine_EntitySkeleton.IsPlausibleBone(
                new Vector3(distance, 1.2f, -distance * 0.5f),
                Quaternion.Identity,
                Vector3.One,
                out string? reason);
            AssertThat(ok).IsTrue();
            AssertThat(reason).IsNull();
        }

        AssertThat(Engine_EntitySkeleton.IsPlausibleBone(
            new Vector3(float.NaN, 0f, 0f), Quaternion.Identity, Vector3.One, out string? nanReason)).IsFalse();
        AssertThat(nanReason).IsEqual("not finite");

        AssertThat(Engine_EntitySkeleton.IsPlausibleBone(
            new Vector3(float.PositiveInfinity, 0f, 0f), Quaternion.Identity, Vector3.One, out _)).IsFalse();

        AssertThat(Engine_EntitySkeleton.IsPlausibleBone(
            new Vector3(12_000f, 0f, 0f), Quaternion.Identity, Vector3.One, out string? rangeReason)).IsFalse();
        AssertThat(rangeReason).IsEqual("beyond world ceiling");

        AssertThat(Engine_EntitySkeleton.IsPlausibleBone(
            Vector3.One, new Quaternion(0.1f, 0f, 0f, 0f), Vector3.One, out string? rotReason)).IsFalse();
        AssertThat(rotReason).IsEqual("rotation not unit");
    }

    [TestCase]
    public void IsDisarticulatedCrumbleDetectsBonePilesAndSkipsNormalRigs()
    {
        Engine_TitleRig rig = Engine_TitleRig.None;
        int[] flatRoots = { -1, 0, 0, 0, 0, 0, 0, 0 };
        int[] humanoidChain = { -1, 0, 1, 2, 3, 4, 3, 6 };

        // Composite -> crumbled bone pile path or 6+ independent pieces under root.
        AssertThat(rig.IsDisarticulatedCrumble(0u, 0x95247F72u, "Creatures/Hollowman_Crumble_01.mdl", humanoidChain)).IsTrue();
        AssertThat(rig.IsDisarticulatedCrumble(0u, 0x95247F72u, null, flatRoots)).IsTrue();

        // Standard humanoid single model or non-composite transition must NOT trigger.
        AssertThat(rig.IsDisarticulatedCrumble(0u, 0x11223344u, "Characters/Villager_01.mdl", humanoidChain)).IsFalse();
        AssertThat(rig.IsDisarticulatedCrumble(0x11223344u, 0x95247F72u, "Creatures/Hollowman_Crumble_01.mdl", flatRoots)).IsFalse();
    }
}
