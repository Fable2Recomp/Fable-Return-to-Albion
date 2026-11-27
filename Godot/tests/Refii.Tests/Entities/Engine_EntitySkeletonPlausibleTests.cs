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
        AssertThat(rig.IsDisarticulatedCrumble(0u, 0x95247F72u, "Art/Creatures/Combatant/Skeleton/dotXSI/Skeleton_Base/Skeleton_Base.mdl", humanoidChain)).IsTrue();
        AssertThat(rig.IsDisarticulatedCrumble(0u, 0x95247F72u, null, flatRoots)).IsTrue();

        // Authoritative rigid-piece geometry triggers for both composite and single-model transitions,
        // while rejecting no-op same-hash swaps.
        AssertThat(rig.IsDisarticulatedCrumble(0u, 0x95247F72u, null, humanoidChain, isRigidPieceMesh: true)).IsTrue();
        AssertThat(rig.IsDisarticulatedCrumble(0x11223344u, 0x95247F72u, null, humanoidChain, isRigidPieceMesh: true)).IsTrue();
        AssertThat(rig.IsDisarticulatedCrumble(0x95247F72u, 0x95247F72u, null, humanoidChain, isRigidPieceMesh: true)).IsFalse();

        // Standard humanoid single model or non-composite heuristic transition must NOT trigger.
        AssertThat(rig.IsDisarticulatedCrumble(0u, 0x11223344u, "Characters/Villager_01.mdl", humanoidChain)).IsFalse();
        AssertThat(rig.IsDisarticulatedCrumble(0x11223344u, 0x95247F72u, "Creatures/Hollowman_Crumble_01.mdl", flatRoots)).IsFalse();
    }

    [TestCase]
    public void SmoothPoseCoherenceRejectsStringCheeseScatterWhileAllowingRootMotion()
    {
        int[] parents = { -1, 0, 1, 2, 1 };

        // Normal root-motion animation 12m away from model origin: root stays at (0,0,0), hip & limbs travel coherently.
        Vector3[] rootMotionPose =
        {
            Vector3.Zero,
            new(12.0f, 1.0f, -3.0f),  // hip (anchor)
            new(12.0f, 1.4f, -3.0f),  // chest
            new(12.0f, 1.75f, -3.0f), // head
            new(12.1f, 0.1f, -3.0f),  // foot
        };
        AssertThat(Engine_EntitySkeleton.IsSmoothPoseCoherent(
            rootMotionPose, parents, anchorIndex: 1, restReach: 1.8f, out int okBone, out _)).IsTrue();
        AssertThat(okBone).IsEqual(-1);

        // Exploded Havok ragdoll pose (head & hip separated by 28m): MUST be refused on smooth-skinned meshes.
        Vector3[] stringCheesePose =
        {
            Vector3.Zero,
            new(0.0f, -66.7f, 0.0f),  // hip fell through unmounted guest floor
            new(2.0f, -45.0f, 1.0f),  // chest
            new(5.0f, -38.6f, -2.0f), // head 28.5m away from hip
            new(-1.0f, -10.0f, 0.5f), // foot
        };
        AssertThat(Engine_EntitySkeleton.IsSmoothPoseCoherent(
            stringCheesePose, parents, anchorIndex: 1, restReach: 1.8f, out int badBone, out float badSpread)).IsFalse();
        AssertThat(badBone).IsGreater(0);
        AssertThat(badSpread).IsGreater(Engine_EntitySkeleton.SmoothBoneSpreadLimit(1.8f));
    }
}
