using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Refii.Game;
using Refii.Launcher.Core;
using Refii.Lighting;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// `android_fx_spawns_per_frame` (AndroidFxSpawns, AndroidFxSpawnQueue): the live effect nodes a
/// walk builds, the rest waiting oldest first; and the post chain's unseen sources
/// (Engine_PostFxUnseen).
/// </summary>
[TestSuite]
public sealed class AndroidFxSpawnsTests
{
    /// <summary>One walk asking for <paramref name="keys"/> in order; the keys admitted.</summary>
    private static List<uint> Walk(AndroidFxSpawnQueue queue, params uint[] keys)
    {
        var admitted = new List<uint>();
        queue.BeginWalk();
        foreach (uint key in keys)
        {
            if (queue.TryAdmit(key))
            {
                admitted.Add(key);
            }
        }

        queue.EndWalk();
        return admitted;
    }

    private static uint[] Range(uint from, int count) =>
        Enumerable.Range(0, count).Select(i => from + (uint)i).ToArray();

    [TestCase]
    public void NoLimitAdmitsEverything()
    {
        var queue = new AndroidFxSpawnQueue { Limit = 0 };
        AssertThat(Walk(queue, Range(1, 200)).Count).IsEqual(200);
        AssertThat(queue.Waiting).IsEqual(0);
        AssertThat(queue.Deferred).IsEqual(0L);
    }

    /// <summary>A burst of 112 at a limit of 16: 16 a walk, in the order first seen, every one built in 7 walks.</summary>
    [TestCase]
    public void ABurstIsBuiltOverTheNextWalksInOrder()
    {
        var queue = new AndroidFxSpawnQueue { Limit = 16 };
        uint[] burst = Range(100, 112);
        var built = new List<uint>();
        for (int walk = 0; walk < 7; walk++)
        {
            // Every walk the guest lists every effect still playing; the built ones no longer ask.
            List<uint> admitted = Walk(queue, burst.Where(key => !built.Contains(key)).ToArray());
            AssertThat(admitted.Count).IsEqual(16);
            built.AddRange(admitted);
        }

        AssertThat(built.SequenceEqual(burst)).IsTrue();
        AssertThat(queue.Waiting).IsEqual(0);
        AssertThat(Walk(queue).Count).IsEqual(0);
    }

    /// <summary>Waiting effects go before ones seen later, wherever the walk lists them.</summary>
    [TestCase]
    public void TheOldestWaitingGoFirst()
    {
        var queue = new AndroidFxSpawnQueue { Limit = 2 };
        AssertThat(Walk(queue, 1, 2, 3, 4).SequenceEqual(new uint[] { 1, 2 })).IsTrue();

        // 3 and 4 wait; new 5 and 6 are listed first (the guest's swap-with-last), and still wait.
        AssertThat(Walk(queue, 5, 6, 4, 3).SequenceEqual(new uint[] { 4, 3 })).IsTrue();
        AssertThat(queue.IsWaiting(5) && queue.IsWaiting(6)).IsTrue();

        // A slot a waiting effect leaves goes to the next new one.
        AssertThat(Walk(queue, 7, 5).SequenceEqual(new uint[] { 5 })).IsTrue();
        AssertThat(queue.IsWaiting(7)).IsTrue();
    }

    /// <summary>Slots the waiting ones do not need go to new effects in the walk's order.</summary>
    [TestCase]
    public void SpareSlotsGoToNewEffects()
    {
        var queue = new AndroidFxSpawnQueue { Limit = 3 };
        Walk(queue, 1, 2, 3, 4);
        AssertThat(Walk(queue, 10, 4, 11, 12).SequenceEqual(new uint[] { 10, 4, 11 })).IsTrue();
        AssertThat(queue.Waiting).IsEqual(1);
        AssertThat(queue.IsWaiting(12)).IsTrue();
    }

    /// <summary>An effect the guest stops listing leaves the queue after a few walks; one listed again keeps its place.</summary>
    [TestCase]
    public void AnEffectTheGuestDropsLeavesTheQueue()
    {
        var queue = new AndroidFxSpawnQueue { Limit = 1 };
        Walk(queue, 1, 2, 3);
        AssertThat(queue.Waiting).IsEqual(2);

        // 2 is missing (unlisted or unplaced) for fewer walks than the drop: still first in line.
        Walk(queue, 3);
        AssertThat(queue.IsWaiting(2)).IsTrue();
        AssertThat(Walk(queue, 3, 2).SequenceEqual(new uint[] { 2 })).IsTrue();

        // 3 then goes unlisted for the whole grace and is gone, never built.
        for (int i = 0; i < AndroidFxSpawnQueue.MissedWalksBeforeDrop; i++)
        {
            AssertThat(Walk(queue).Count).IsEqual(0);
        }

        AssertThat(queue.Waiting).IsEqual(0);
        AssertThat(queue.IsWaiting(3)).IsFalse();
    }

    /// <summary>Asking twice in a walk (two nodes of one effect) gives the same answer, takes one slot and queues once.</summary>
    [TestCase]
    public void ASecondAskInAWalkGivesTheSameAnswer()
    {
        var queue = new AndroidFxSpawnQueue { Limit = 1 };
        AssertThat(Walk(queue, 1, 1, 2, 2, 2).SequenceEqual(new uint[] { 1, 1 })).IsTrue();
        AssertThat(queue.Waiting).IsEqual(1);
        AssertThat(queue.Deferred).IsEqual(3L);
        AssertThat(Walk(queue, 2, 2, 3).SequenceEqual(new uint[] { 2, 2 })).IsTrue();
        AssertThat(queue.Waiting).IsEqual(1);
        AssertThat(queue.IsWaiting(3)).IsTrue();
    }

    /// <summary>Lifting the limit builds everything waiting, and the queue empties.</summary>
    [TestCase]
    public void LiftingTheLimitEmptiesTheQueue()
    {
        var queue = new AndroidFxSpawnQueue { Limit = 1 };
        Walk(queue, 1, 2, 3);
        queue.Limit = 0;
        AssertThat(Walk(queue, 2, 3).Count).IsEqual(2);
        AssertThat(queue.Waiting).IsEqual(0);
    }

    [TestCase]
    public void TheLimitIsAPhonesOnly()
    {
        var empty = new Dictionary<string, string>();
        AssertThat(AndroidFxSpawns.Resolve(empty, phone: true)).IsEqual(AndroidFxSpawns.DefaultLimit);
        AssertThat(AndroidFxSpawns.Resolve(empty, phone: false)).IsEqual(0);
        var set = new Dictionary<string, string> { [AndroidFxSpawns.Key] = "4" };
        AssertThat(AndroidFxSpawns.Resolve(set, phone: true)).IsEqual(4);
        AssertThat(AndroidFxSpawns.Resolve(set, phone: false)).IsEqual(0);
        AssertThat(AndroidFxSpawns.Resolve(new Dictionary<string, string> { [AndroidFxSpawns.Key] = "0" }, true)).IsEqual(0);
        AssertThat(AndroidFxSpawns.Resolve(new Dictionary<string, string> { [AndroidFxSpawns.Key] = "-3" }, true)).IsEqual(0);

        RuntimeCVarDefinition row = RuntimeCVarSet.All.First(d => d.Name == AndroidFxSpawns.Key);
        AssertThat(row.DefaultValue).IsEqual(AndroidFxSpawns.DefaultLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The sun sources and halos go unbuilt unless the chain is on and its route usable.</summary>
    [TestCase]
    public void PostFxSourcesAreBuiltOnlyForAChainThatComposites()
    {
        AssertThat(Engine_PostFxUnseen.Skips(true, Engine_PostFxRoute.State.Usable)).IsFalse();
        AssertThat(Engine_PostFxUnseen.Skips(true, Engine_PostFxRoute.State.Unusable)).IsTrue();
        AssertThat(Engine_PostFxUnseen.Skips(true, Engine_PostFxRoute.State.Unknown)).IsTrue();
        AssertThat(Engine_PostFxUnseen.Skips(false, Engine_PostFxRoute.State.Usable)).IsTrue();

        // Off a phone it never skips (the test host is a desktop).
        AssertThat(Engine_PostFxUnseen.Now).IsFalse();
    }
}
