using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GdUnit4;
using Refii.Core;
using Refii.Game;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Exercises actual retirement ordering and producer publication without native Godot objects.</summary>
[TestSuite]
public sealed class RegionRetirementTests
{
    /// <summary>Long background intervals consume no active deadline, including repeated pause notifications and startup while paused.</summary>
    [TestCase]
    public void ApplicationPauseExcludesOnlyBackgroundTimeAndPreservesActiveDeadline()
    {
        var gate = new GameRegionRetirement(0, 10);
        gate.Advance(0, 15, 1, false);
        gate.SetApplicationPaused(true, 16);
        gate.SetApplicationPaused(true, 100);
        AssertThat(gate.Advance(1, 200, 0, false)).IsEqual(RegionRetirementWork.None);
        AssertThat(gate.Status.ElapsedSeconds).IsEqual(6.0);
        AssertThat(gate.Status.ExcludedPauseSeconds).IsEqual(184.0);
        gate.SetApplicationPaused(false, 216);
        gate.SetApplicationPaused(false, 217);
        AssertThat(gate.Advance(1, 217, 0, false)).IsEqual(RegionRetirementWork.ClearAndCollect);
        gate.SetApplicationPaused(true, 218);
        gate.SetApplicationPaused(false, 318);
        AssertThat(gate.Advance(2, 318, 0, true)).IsEqual(RegionRetirementWork.None);
        AssertThat(gate.Status.ElapsedSeconds).IsEqual(8.0);
        AssertThat(gate.Advance(3, 318 + GameRegionRetirement.TimeoutSeconds - 8, 0, false)).IsEqual(RegionRetirementWork.Failed);
        var startingPaused = new GameRegionRetirement(5, 1, true);
        AssertThat(startingPaused.Advance(6, 101, 0, false)).IsEqual(RegionRetirementWork.None);
        startingPaused.SetApplicationPaused(false, 101);
        AssertThat(startingPaused.Advance(6, 102, 0, false)).IsEqual(RegionRetirementWork.ClearAndCollect);
        AssertThat(startingPaused.Status.ElapsedSeconds).IsEqual(1.0);
    }

    /// <summary>Finalizer success/failure during pause is retained; invalid notification clocks fail closed.</summary>
    [TestCase]
    public void PausedFinalizerCompletionAndNotificationFailuresRemainObservable()
    {
        var success = new GameRegionRetirement(0, 0);
        success.Advance(1, 1, 0, false);
        success.SetApplicationPaused(true, 2);
        success.Advance(1, 100, 0, true);
        AssertThat(success.Status.FinalizerPasses).IsEqual(1);
        AssertThat(success.Advance(2, 101, 0, false)).IsEqual(RegionRetirementWork.None);
        success.SetApplicationPaused(false, 102);
        AssertThat(success.Advance(2, 103, 0, false)).IsEqual(RegionRetirementWork.Collect);
        var failure = new GameRegionRetirement(0, 0);
        failure.Advance(1, 1, 0, false);
        failure.SetApplicationPaused(true, 2);
        AssertThat(failure.Advance(1, 100, 0, true, "paused finalizer fault")).IsEqual(RegionRetirementWork.Failed);
        AssertThat(failure.Status.Error).IsEqual("paused finalizer fault");
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, -1.0 })
        {
            var invalidGate = new GameRegionRetirement(0, 0);
            AssertThat(invalidGate.SetApplicationPaused(true, invalid)).IsEqual(RegionRetirementWork.Failed);
            AssertThat(invalidGate.Status.NewBuildBlocked).IsTrue();
        }
    }

    /// <summary>Early guest phases can advance, while a real new destination retains a continuous preload fence.</summary>
    [TestCase]
    public void PreloadFenceHandoffPreservesOnlyAnExistingNewDestination()
    {
        AssertThat(GameRegionRetirement.PreservePreloadHold(false, 2, 1, 3)).IsFalse();
        AssertThat(GameRegionRetirement.PreservePreloadHold(true, null, 1, 3)).IsFalse();
        AssertThat(GameRegionRetirement.PreservePreloadHold(true, 1, 1, 8)).IsFalse();
        AssertThat(GameRegionRetirement.PreservePreloadHold(true, 2, 1, 2)).IsFalse();
        AssertThat(GameRegionRetirement.PreservePreloadHold(true, 2, 1, 3)).IsTrue();
        AssertThat(GameRegionRetirement.PreservePreloadHold(true, 2, 1, 7)).IsTrue();
        AssertThat(GameRegionRetirement.PreservePreloadHold(true, 2, 1, null)).IsTrue();
        AssertThat(GameRegionRetirement.PreservePreloadHold(true, 1, 1, null)).IsFalse();
        AssertThat(GameRegionRetirement.PreservePreloadHold(true, 2, 0, null)).IsTrue();
        AssertThat(GameRegionRetirement.PreservePreloadHold(false, null, 0, null)).IsFalse();
    }

    /// <summary>Three passes are separated by rendered epochs, including after the last pass.</summary>
    [TestCase]
    public void QuiescentGateRequiresDeletionAndThreeSeparatedFinalizerPasses()
    {
        var gate = new GameRegionRetirement(10, 100);
        AssertThat(gate.Advance(10, 100.1, 0, false)).IsEqual(RegionRetirementWork.None);
        AssertThat(gate.Advance(11, 100.2, 1, false)).IsEqual(RegionRetirementWork.None);
        AssertThat(gate.Advance(11, 100.3, 0, false)).IsEqual(RegionRetirementWork.ClearAndCollect);
        for (int pass = 0; pass < 3; ++pass)
        {
            ulong frame = (ulong)(11 + pass);
            AssertThat(gate.Advance(frame, 100.4 + pass, 0, false)).IsEqual(RegionRetirementWork.None);
            AssertThat(gate.Advance(frame, 100.5 + pass, 0, true)).IsEqual(RegionRetirementWork.None);
            AssertThat(gate.Status.FinalizerPasses).IsEqual(pass + 1);
            AssertThat(gate.Advance(frame, 100.6 + pass, 0, false)).IsEqual(RegionRetirementWork.None);
            AssertThat(gate.Advance(frame + 1, 100.7 + pass, 0, false)).IsEqual(
                pass == 2 ? RegionRetirementWork.Complete : RegionRetirementWork.Collect);
        }
        AssertThat(gate.Status.NewBuildBlocked).IsFalse();
        AssertThat(gate.Advance(100, 200, 40, true, "late fault")).IsEqual(RegionRetirementWork.None);
        AssertThat(gate.Status.Error).IsEqual(string.Empty);
    }

    /// <summary>An abandoned builder's late root needs its own deletion epoch before cache retirement.</summary>
    [TestCase]
    public void LateDetachedWorkerResultCannotSkipItsDeletionFrame()
    {
        var gate = new GameRegionRetirement(20, 10);
        AssertThat(gate.Advance(21, 11, 1, false)).IsEqual(RegionRetirementWork.None);
        gate.RequireDeletionFrame(30);
        AssertThat(gate.Advance(30, 12, 0, false)).IsEqual(RegionRetirementWork.None);
        AssertThat(gate.Advance(31, 13, 0, false)).IsEqual(RegionRetirementWork.ClearAndCollect);
    }

    /// <summary>Neither a stuck producer nor finalizer can silently authorize an overlapping destination build.</summary>
    [TestCase]
    public void DeadlinesFailOnceAndPreserveBlockedDiagnostics()
    {
        foreach (bool collecting in new[] { false, true })
        {
            var gate = new GameRegionRetirement(0, 0);
            if (collecting) gate.Advance(1, 1, 0, false);
            AssertThat(gate.Advance(2, GameRegionRetirement.TimeoutSeconds, collecting ? 0 : 4, false)).IsEqual(RegionRetirementWork.Failed);
            var failed = gate.Status;
            AssertThat(failed.NewBuildBlocked).IsTrue();
            AssertThat(failed.Error.Contains("timed out")).IsTrue();
            AssertThat(gate.Advance(3, GameRegionRetirement.TimeoutSeconds + 1, 0, true)).IsEqual(RegionRetirementWork.None);
            AssertThat(gate.Status).IsEqual(failed);
        }
    }

    /// <summary>A failing cleanup pass is explicit and does not repeat collection or unlock the build.</summary>
    [TestCase]
    public void CollectionFailureAndResumedProducerAreTerminal()
    {
        var failed = new GameRegionRetirement(0, 0);
        failed.Advance(1, 1, 0, false);
        AssertThat(failed.Advance(1, 2, 0, true, "fixture finalizer failure")).IsEqual(RegionRetirementWork.Failed);
        AssertThat(failed.Status.Error).IsEqual("fixture finalizer failure");
        AssertThat(failed.Status.FinalizerPasses).IsEqual(0);
        var resumed = new GameRegionRetirement(0, 0);
        resumed.Advance(1, 1, 0, false);
        AssertThat(resumed.Advance(2, 2, 1, false)).IsEqual(RegionRetirementWork.Failed);
        AssertThat(resumed.Status.Error.Contains("resumed")).IsTrue();
    }

    /// <summary>Invalid clocks/counts cannot escape into JSON diagnostics or accidentally complete a gate.</summary>
    [TestCase]
    public void InvalidObservationFailsClosedAndRepeatedRegionsStartFresh()
    {
        foreach (double clock in new[] { double.NaN, double.PositiveInfinity, 9.0 })
        {
            var gate = new GameRegionRetirement(0, 10);
            AssertThat(gate.Advance(1, clock, 0, false)).IsEqual(RegionRetirementWork.Failed);
            AssertThat(double.IsFinite(gate.Status.ElapsedSeconds)).IsTrue();
        }
        var old = new GameRegionRetirement(0, 0);
        var snapshot = old.Status;
        old.Advance(1, 1, 0, false);
        var next = new GameRegionRetirement(200, 200);
        AssertThat(next.Status.Stage).IsEqual("waiting-workers");
        AssertThat(next.Status.FinalizerPasses).IsEqual(0);
        AssertThat(snapshot.Stage).IsEqual("waiting-workers");
        AssertThat(new GameRegionRetirement(0, 0).Advance(1, 1, -1, false)).IsEqual(RegionRetirementWork.Failed);
    }

    /// <summary>Counting starts before scheduling; an outgoing worker cannot republish or queue a new job.</summary>
    [TestCase]
    public void ScheduledProducerRetirementAndLatePublicationHaveAnAtomicLifecycle()
    {
        int baseline = Engine_RegionWorkers.GlobalActive;
        var owner = new Engine_RegionWorkers();
        Action? scheduled = null;
        var completions = new Queue<string>();
        owner.TryPublish(() => completions.Enqueue("old"));
        AssertThat(owner.TryQueue(() => owner.TryPublish(() => completions.Enqueue("late")),
            action => { scheduled = action; return Task.CompletedTask; })).IsTrue();
        AssertThat(owner.Active).IsEqual(1);
        AssertThat(Engine_RegionWorkers.GlobalActive).IsEqual(baseline + 1);
        owner.Retire(completions.Clear);
        owner.Retire(completions.Clear);
        AssertThat(owner.TryQueue(() => throw new InvalidOperationException("must not execute"))).IsFalse();
        AssertThat(owner.TryPublish(() => completions.Enqueue("bad"))).IsFalse();
        scheduled!();
        AssertThat(owner.Active).IsEqual(0);
        AssertThat(Engine_RegionWorkers.GlobalActive).IsEqual(baseline);
        AssertThat(completions.Count).IsEqual(0);
    }

    /// <summary>Throwing workers and failed schedulers release their lease exactly once.</summary>
    [TestCase]
    public void WorkerAndSchedulerFailuresCannotLeakOrDoubleReleaseCounts()
    {
        foreach (int kind in new[] { 0, 1, 2 })
        {
            int baseline = Engine_RegionWorkers.GlobalActive;
            var owner = new Engine_RegionWorkers();
            bool threw = false;
            try
            {
                owner.TryQueue(() => { if (kind == 2) throw new InvalidOperationException("worker"); }, action =>
                {
                    if (kind == 0) throw new InvalidOperationException("scheduler");
                    action();
                    throw new InvalidOperationException("scheduler after execution");
                });
            }
            catch (InvalidOperationException) { threw = true; }
            AssertThat(threw).IsTrue();
            AssertThat(owner.Active).IsEqual(0);
            AssertThat(Engine_RegionWorkers.GlobalActive).IsEqual(baseline);
        }
    }

    /// <summary>Retirement cannot interleave between accepting a completion and draining its queue.</summary>
    [TestCase]
    public void ConcurrentPublishAndRetireLeaveNoCompletionBehind()
    {
        var owner = new Engine_RegionWorkers();
        var queue = new Queue<int>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task publish = Task.Run(() => owner.TryPublish(() =>
        {
            entered.Set();
            if (!release.Wait(1000)) throw new TimeoutException();
            queue.Enqueue(1);
        }));
        AssertThat(entered.Wait(1000)).IsTrue();
        Task retire = Task.Run(() => owner.Retire(queue.Clear));
        release.Set();
        AssertThat(Task.WaitAll(new[] { publish, retire }, 1000)).IsTrue();
        AssertThat(queue.Count).IsEqual(0);
        AssertThat(owner.TryPublish(() => queue.Enqueue(2))).IsFalse();
    }

    /// <summary>The actual worker gate blocks clearing until a scheduled old producer has finished its refused publication.</summary>
    [TestCase]
    public void ProducerAndRetirementPolicyComposeAcrossRepeatedLevelChanges()
    {
        for (int level = 0; level < 3; ++level)
        {
            var owner = new Engine_RegionWorkers();
            var gate = new GameRegionRetirement(0, 0);
            Action? oldWorker = null;
            bool published = false;
            owner.TryQueue(() => published = owner.TryPublish(() => { }),
                work => { oldWorker = work; return Task.CompletedTask; });
            owner.Retire();
            AssertThat(gate.Advance(1, 1, owner.Active, false)).IsEqual(RegionRetirementWork.None);
            oldWorker!();
            AssertThat(published).IsFalse();
            AssertThat(gate.Advance(2, 2, owner.Active, false)).IsEqual(RegionRetirementWork.ClearAndCollect);
            for (int pass = 0; pass < 3; ++pass)
            {
                gate.Advance((ulong)(2 + pass), 3 + pass, 0, true);
                AssertThat(gate.Advance((ulong)(3 + pass), 3.1 + pass, 0, false)).IsEqual(
                    pass == 2 ? RegionRetirementWork.Complete : RegionRetirementWork.Collect);
            }
        }
    }

    /// <summary>Baked retirement disposes only its iterator and drops probe/frame roots, even when atlas publication fails.</summary>
    [TestCase]
    public void BorrowedReferenceRetirementIsIdempotentAndPublicationFailureLeavesNoOwner()
    {
        foreach (bool fail in new[] { false, true })
        {
            var owner = new BorrowedWalk();
            IEnumerator<string>? walk = owner;
            object? probe = new object();
            object? frame = new object();
            int publications = 0;
            try { Engine_RegionReferences.Release(ref probe, ref walk, ref frame, () => { ++publications; if (fail) throw new InvalidOperationException(); }); }
            catch (InvalidOperationException) { AssertThat(fail).IsTrue(); }
            AssertThat(owner.Disposals).IsEqual(1);
            AssertThat(walk).IsNull();
            AssertThat(frame).IsNull();
            AssertThat(probe).IsNull();
            AssertThat(publications).IsEqual(1);
            Engine_RegionReferences.Release(ref probe, ref walk, ref frame, () => ++publications);
            AssertThat(owner.Disposals).IsEqual(1);
            AssertThat(publications).IsEqual(2);
        }
    }

    /// <summary>A native-free owned iterator with borrowed values.</summary>
    private sealed class BorrowedWalk : IEnumerator<string>
    {
        /// <summary>Recorded owner releases.</summary>
        public int Disposals { get; private set; }
        /// <summary>No native object is created by this fixture.</summary>
        public string Current => string.Empty;
        object IEnumerator.Current => Current;
        /// <summary>Ends this fixture walk.</summary>
        public bool MoveNext() => false;
        /// <summary>Does not support restarting.</summary>
        public void Reset() => throw new NotSupportedException();
        /// <summary>Releases only the iterator owner.</summary>
        public void Dispose() => ++Disposals;
    }
}
