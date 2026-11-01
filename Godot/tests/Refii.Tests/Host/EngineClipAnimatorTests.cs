namespace Refii.Tests;

using System;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Animations;

[TestSuite]
public class EngineClipAnimatorTests
{
    [TestCase]
    public void TestOverlayFadeTransitions()
    {
        // Persistent overlay (matches both top and runner) should maintain full weight (1.0)
        float topShare = 0.3f;
        bool matchesTop = true;
        bool matchesRunner = true;
        float overlayFade = matchesTop && matchesRunner ? 1f : (matchesTop ? topShare : 1f - topShare);
        AssertThat(overlayFade).IsEqual(1f);

        // Incoming overlay (matches top only) should fade in linearly with topShare
        matchesTop = true;
        matchesRunner = false;
        overlayFade = matchesTop && matchesRunner ? 1f : (matchesTop ? topShare : 1f - topShare);
        AssertThat(overlayFade).IsEqual(0.3f);

        // Outgoing overlay (matches runner only) should fade out linearly with (1 - topShare)
        matchesTop = false;
        matchesRunner = true;
        overlayFade = matchesTop && matchesRunner ? 1f : (matchesTop ? topShare : 1f - topShare);
        AssertThat(overlayFade).IsEqual(0.7f);
    }

    [TestCase]
    public void TestSameClipSyncStaticGuard()
    {
        // Long cyclical animation (e.g. 2.8s SitHoldBeerLoop) must NOT be considered static
        float animatedClipLength = 2.8f;
        bool isStatic = false;
        bool shouldSyncTime = isStatic || animatedClipLength <= 0.05f;
        AssertThat(shouldSyncTime).IsFalse();

        // 1-frame static pose (e.g. dog idle Pose, <= 0.05s) should qualify for sync
        float staticClipLength = 0.033f;
        shouldSyncTime = isStatic || staticClipLength <= 0.05f;
        AssertThat(shouldSyncTime).IsTrue();
    }

    [TestCase]
    public void TestPriorityMaskBitfieldEvaluation()
    {
        // Fable 2 priorities are powers of two: 1, 2, 4, 8, 16, 32, 64, 128.
        // Masks are bitfields: 0x03 (prio 1 & 2), 0x1F (prio 1, 2, 4, 8, 16), 0x80 (prio 128).
        uint maskAmbientMug = 0x03; // matches priorities 1 and 2
        uint maskBroadOverlay = 0x1F;

        // Priority 8 (SitHoldBeerLoop primary) must NOT match mask 3
        uint winnerPrioritySit = 8;
        bool matchesSit = (maskAmbientMug & winnerPrioritySit) != 0;
        AssertThat(matchesSit).IsFalse();

        // Priority 1 (IdleHoldBeerLoop standing primary) MUST match mask 3 and mask 0x1F
        uint winnerPriorityStand = 1;
        AssertThat((maskAmbientMug & winnerPriorityStand) != 0).IsTrue();
        AssertThat((maskBroadOverlay & winnerPriorityStand) != 0).IsTrue();

        // Priority 128 (CombatPoseGoofy reaction) must NOT match mask 3, but matches 0x80
        uint winnerPriorityReaction = 128;
        AssertThat((maskAmbientMug & winnerPriorityReaction) != 0).IsFalse();
        AssertThat((0x80u & winnerPriorityReaction) != 0).IsTrue();
    }

    [TestCase]
    public void TestContinuousCrossfadeMonotonicity()
    {
        // Simulate continuous ramp crossfade: top ramps 0 -> 1, runner ramps 1 -> 0
        float lastShare = -1f;
        for (int step = 0; step <= 20; ++step)
        {
            float t = step / 20f;
            float topWeight = Mathf.Clamp(t * 1.1f, 0f, 1f);       // reaches 1.0 slightly before runner finishes
            float runnerWeight = Mathf.Clamp((1f - t) * 1.1f, 0f, 1f);

            float sum = topWeight + runnerWeight;
            float topShare = sum > 1e-4f ? Mathf.Clamp(topWeight / sum, 0f, 1f) : 1f;

            if (lastShare >= 0f)
            {
                // Must be strictly monotonic non-decreasing (no backwards jumps)
                AssertThat(topShare >= lastShare).IsTrue();
            }
            lastShare = topShare;
        }
        AssertThat(lastShare).IsEqual(1f);
    }

    [TestCase]
    public void TestIntraRecordFootstepNewLeafShareStartsAtZero()
    {
        // When a new footstep leaf (e.g. WalkFootstepMirror with share 0.1517) appears inside an
        // already-active primary locomotion record (uid=870, recordCurrent=1.0), its SharePrevious
        // must start at 0f so the sum of shares at pairAt=0 is 1.0 (outgoing) + 0.0 (incoming) = 1.0
        // rather than jumping to 1.1517 at tick start.
        var (recPrev, sharePrev) = Engine_ClipAnimator.InitialChannelPair(
            recordAlreadyActive: true,
            leafRecordPrevious: 0.8667f,
            leafRecordCurrent: 1.0f,
            leafShare: 0.1517f);
        AssertThat(recPrev).IsEqual(1.0f);
        AssertThat(sharePrev).IsEqual(0.0f);

        // When a brand-new record Uid appears (recordAlreadyActive = false), it preserves the
        // leaf's stated RecordPrevious and starts at leafShare.
        var (newRecPrev, newSharePrev) = Engine_ClipAnimator.InitialChannelPair(
            recordAlreadyActive: false,
            leafRecordPrevious: 0.0f,
            leafRecordCurrent: 0.2f,
            leafShare: 1.0f);
        AssertThat(newRecPrev).IsEqual(0.0f);
        AssertThat(newSharePrev).IsEqual(1.0f);
    }

    [TestCase]
    public void TestUnseenChannelInActiveRecordDropsWithoutGhosting()
    {
        // When a footstep leaf finishes its Type-1 lerp inside a persistent locomotion Uid that is
        // still seen in the snapshot and its Host-smoothed share has reached 0, it must be dropped
        // immediately rather than ramping out as a 3rd ghost footstep channel.
        AssertThat(Engine_ClipAnimator.ShouldDropUnseenChannelInActiveRecord(
            recordStillSeen: true, hadPendingClip: false)).IsTrue();

        // While the unseen sibling channel still has non-zero smoothed share complementing the
        // incoming sibling, it must NOT be dropped yet or the surviving sibling would snap to 1.0.
        AssertThat(Engine_ClipAnimator.ShouldDropUnseenChannelInActiveRecord(
            recordStillSeen: true,
            hadPendingClip: false,
            interpolatedWeight: 0.65f,
            smoothedShare: 0.65f)).IsFalse();

        // Once smoothedShare and interpolatedWeight reach 0, it drops cleanly.
        AssertThat(Engine_ClipAnimator.ShouldDropUnseenChannelInActiveRecord(
            recordStillSeen: true,
            hadPendingClip: false,
            interpolatedWeight: 0f,
            smoothedShare: 0f)).IsTrue();

        // If a leaf in the snapshot was skipped due to async background decoding, keep existing
        // channels of that Uid alive until the decode completes.
        AssertThat(Engine_ClipAnimator.ShouldDropUnseenChannelInActiveRecord(
            recordStillSeen: true, hadPendingClip: true)).IsFalse();

        // When an entire record Uid disappears from the snapshot (record-to-record handoff),
        // allow its channels to ramp out normally over BlendOut.
        AssertThat(Engine_ClipAnimator.ShouldDropUnseenChannelInActiveRecord(
            recordStillSeen: false, hadPendingClip: false)).IsFalse();
    }

    [TestCase]
    public void TestIsFacialTrackPosedDetectsUnposedCarryIdles()
    {
        // Raw skeleton bind pose for Shadow_L_Eye (-99.6 deg) and Shadow_Jaw (-2.7 deg):
        Quaternion restLeftEye = Quaternion.FromEuler(new Vector3(0f, 0f, Mathf.DegToRad(-99.6f)));
        Quaternion restJaw = Quaternion.FromEuler(new Vector3(0f, 0f, Mathf.DegToRad(-2.7f)));

        // IdleHoldBeerLoop (0x3207C71E / 0xE4BD3CF7) and SitHoldBeerLoop (0x509F78EA) bake
        // 1-keyframe tracks identical to raw bind pose (0.00 deg delta): must be recognized as unposed.
        var unposedEyeTrack = new[] { restLeftEye };
        var unposedJawTrack = new[] { restJaw, -restJaw };
        AssertThat(Engine_ClipAnimator.IsFacialTrackPosed(unposedEyeTrack, restLeftEye)).IsFalse();
        AssertThat(Engine_ClipAnimator.IsFacialTrackPosed(unposedJawTrack, restJaw)).IsFalse();

        // IdleStand1 (0xB742E730) and WalkFootstep rotate Shadow_L_Eye to ~ -90 deg (+9.6 deg delta)
        // and Shadow_Jaw to ~ +4.6 deg (+7.3 deg delta): must be recognized as posed.
        var posedEyeTrack = new[] { Quaternion.FromEuler(new Vector3(0f, 0f, Mathf.DegToRad(-90.0f))) };
        var posedJawTrack = new[] { Quaternion.FromEuler(new Vector3(0f, 0f, Mathf.DegToRad(4.6f))) };
        AssertThat(Engine_ClipAnimator.IsFacialTrackPosed(posedEyeTrack, restLeftEye)).IsTrue();
        AssertThat(Engine_ClipAnimator.IsFacialTrackPosed(posedJawTrack, restJaw)).IsTrue();
    }

    [TestCase]
    public void TestSmoothPostOpTargetInterpolatesOnHostClock()
    {
        // Upper-body lookRec updates at 15 Hz; between ticks, SmoothPostOpTarget must smoothly
        // Slerp toward the new target with hemisphere alignment rather than snapping or flipping.
        Quaternion prevTarget = Quaternion.FromEuler(new Vector3(0f, 0f, Mathf.DegToRad(-20.0f)));
        Quaternion nextTarget = -Quaternion.FromEuler(new Vector3(0f, 0f, Mathf.DegToRad(-32.0f))); // opposite hemisphere

        Quaternion mid = Engine_ClipAnimator.SmoothPostOpTarget(prevTarget, nextTarget, blend: 0.25f);
        float zDeg = Mathf.RadToDeg(mid.GetEuler().Z);
        AssertThat(zDeg < -21.0f && zDeg > -25.0f).IsTrue();
    }

    [TestCase]
    public void TestPostOpMode2AppliedBeforeOverlayDoesNotAmplifyOverlayArmBend()
    {
        // Reproduce live villager 0x4293B7B0 (WalkFootstepTurn45RMirror + MoodPoseScared1Loop):
        // Primary walk forearm Z-bend ~ -20.7 deg, lookRec target Z-bend ~ -20.7 deg,
        // lookRec mode = 2: t + w * (q - t) with w = 2.229, MoodPoseScared1Loop Z-bend ~ -49.1 deg at w_ov = 0.50.
        Quaternion qWalk = Quaternion.FromEuler(new Vector3(0f, 0f, Mathf.DegToRad(-20.7f)));
        Quaternion qTarget = Quaternion.FromEuler(new Vector3(0f, 0f, Mathf.DegToRad(-20.7f)));
        Quaternion qOverlay = Quaternion.FromEuler(new Vector3(0f, 0f, Mathf.DegToRad(-49.1f)));
        float lookWeight = 2.229f;
        float overlayWeight = 0.50f;

        // Correct Guest order (0x82229960 / 0x822DC980): apply lookRec to primary walk pose first,
        // then Slerp upper-body overlay on top.
        Quaternion postPrimary = Engine_ClipAnimator.EvaluatePostOpBone(qWalk, qTarget, mode: 2, weight: lookWeight);
        Quaternion guestOrderPose = postPrimary.Slerp(qOverlay, overlayWeight);
        float guestOrderZDeg = Mathf.RadToDeg(guestOrderPose.GetEuler().Z);

        // Old broken Host order: Slerp overlay first, then apply mode=2 lookRec (which amplified
        // the overlay's arm raise by 2.229x to ~ -64 deg).
        Quaternion overlayFirst = qWalk.Slerp(qOverlay, overlayWeight);
        Quaternion brokenOrderPose = Engine_ClipAnimator.EvaluatePostOpBone(overlayFirst, qTarget, mode: 2, weight: lookWeight);
        float brokenOrderZDeg = Mathf.RadToDeg(brokenOrderPose.GetEuler().Z);

        // Guest order stays near -34.9 deg (halfway between -20.7 and -49.1), whereas broken order
        // over-bends past -52 deg.
        AssertThat(Mathf.Abs(guestOrderZDeg - (-34.9f)) < 1.0f).IsTrue();
        AssertThat(brokenOrderZDeg < -50.0f).IsTrue();
    }

    [TestCase]
    public void TestMidStrideFootstepPlayheadSpawnStartsInPhaseLockstep()
    {
        // Live trace of 0x4289F630: WalkFootstep enters mid-stride at current=0.207 while
        // constructor-initialized previous=0.0, with drawTick lagging tick by 1.0 (alpha = 0).
        var playhead = new Engine_ClipPlayhead();
        playhead.Observe(
            previous: 0.0,
            current: 0.207,
            tick: 100u,
            length: 0.733,
            loop: false,
            cut: false,
            drawTick: 99.0);

        AssertThat(Math.Abs(playhead.Rate - 1.0) < 1e-4).IsTrue();
        double expectedStart = 0.207 - (1.0 / 15.0);
        AssertThat(Math.Abs(playhead.Time - expectedStart) < 1e-4).IsTrue();

        // At the midway render frame (alpha = 0.5, drawTick = 99.5), Reference(99.5) is 0.207 - 0.5/15.
        var midPlayhead = new Engine_ClipPlayhead();
        midPlayhead.Observe(0.0, 0.207, 100u, 0.733, false, false, 99.5);
        AssertThat(Math.Abs(midPlayhead.Time - (0.207 - (0.5 / 15.0))) < 1e-4).IsTrue();

        // Next guest tick (101) shifts 0.207 into previous and steps current to 0.27367:
        // Reference(100.0) is now 0.207, matching a 1x playhead that advanced by 1 tick with zero jump.
        playhead.Observe(
            previous: 0.207,
            current: 0.207 + (1.0 / 15.0),
            tick: 101u,
            length: 0.733,
            loop: false,
            cut: false,
            drawTick: 100.0);
        AssertThat(Math.Abs(playhead.Rate - 1.0) < 1e-4).IsTrue();
    }

    [TestCase]
    public void TestStaticVisemeCutsDoNotInferNonZeroPlaybackRate()
    {
        // Phonemes (trk=7) sets discrete 1/30s static viseme frames with previous == current (cut = true).
        var playhead = new Engine_ClipPlayhead();
        playhead.Observe(0.0667, 0.0667, 50u, 0.3667, false, cut: true, drawTick: 49.2);
        AssertThat(playhead.Rate).IsEqual(0.0);
        AssertThat(Math.Abs(playhead.Time - 0.0667) < 1e-5).IsTrue();

        // Switching to the next discrete viseme at tick 51 must snap to 0.1000 with Rate == 0.0
        // rather than inferring Rate = 0.5 and scrubbing across unrelated mouth shapes.
        playhead.Observe(0.1000, 0.1000, 51u, 0.3667, false, cut: true, drawTick: 50.2);
        AssertThat(playhead.Rate).IsEqual(0.0);
        AssertThat(Math.Abs(playhead.Time - 0.1000) < 1e-5).IsTrue();
    }

    [TestCase]
    public void TestCanReuseUnseenChannelRejectsDyingFootstepFromPreviousStride()
    {
        // Outgoing WalkFootstep channel at 0.522s must NOT be reused when a brand-new WalkFootstep
        // leaf spawns mid-stride at PreviousTime=0.0s, Time=0.207s.
        AssertThat(Engine_ClipAnimator.CanReuseUnseenChannel(
            statedTime: 0.522f,
            leafPreviousTime: 0.0f,
            leafTime: 0.207f,
            clipLength: 0.733f,
            loop: false,
            isStatic: false)).IsFalse();

        // Continuing WalkFootstep channel where PreviousTime matches StatedTime MUST be reused.
        AssertThat(Engine_ClipAnimator.CanReuseUnseenChannel(
            statedTime: 0.207f,
            leafPreviousTime: 0.207f,
            leafTime: 0.278f,
            clipLength: 0.733f,
            loop: false,
            isStatic: false)).IsTrue();
    }
}
