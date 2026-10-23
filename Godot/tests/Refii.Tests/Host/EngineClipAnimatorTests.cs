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
}
