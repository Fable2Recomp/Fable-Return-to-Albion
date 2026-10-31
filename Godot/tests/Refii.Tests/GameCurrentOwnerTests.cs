using System;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.Game;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Tests current-owner retirement and strong-cache/worker lifecycles without native Godot nodes.</summary>
[TestSuite]
public sealed class GameCurrentOwnerTests
{
    /// <summary>A departing current owner is released once; a repeated exit is harmless.</summary>
    [TestCase]
    public void CurrentOwnerRetiresExactlyOnce()
    {
        var owner = new Owner();
        Owner? current = owner;
        AssertThat(GameCurrentOwner.Retire(ref current, owner)).IsTrue();
        AssertThat(current).IsNull();
        AssertThat(GameCurrentOwner.Retire(ref current, owner)).IsFalse();
    }

    /// <summary>An old or equal-valued owner cannot erase the newly published feed.</summary>
    [TestCase]
    public void StaleExitPreservesReplacementByReferenceIdentity()
    {
        var old = new Owner();
        var replacement = new Owner();
        Owner? current = replacement;
        AssertThat(old.Equals(replacement)).IsTrue();
        AssertThat(GameCurrentOwner.Retire(ref current, old)).IsFalse();
        AssertThat(ReferenceEquals(current, replacement)).IsTrue();
        AssertThat(GameCurrentOwner.Retire(ref current, replacement)).IsTrue();
    }

    /// <summary>Detaching an unpublished owner does not manufacture a current reference or dispose a borrowed value.</summary>
    [TestCase]
    public void UnpublishedExitLeavesSharedResourcesUsable()
    {
        var borrowed = new Borrowed();
        var owner = new Owner(borrowed);
        Owner? current = null;
        AssertThat(GameCurrentOwner.Retire(ref current, owner)).IsFalse();
        AssertThat(borrowed.Disposed).IsFalse();
        current = owner;
        AssertThat(GameCurrentOwner.Retire(ref current, owner)).IsTrue();
        AssertThat(borrowed.Disposed).IsFalse();
    }

    /// <summary>Travel, a stale old exit, title retirement and same-region re-entry leave no static cache root.</summary>
    [TestCase]
    public void RegionTitleAndReloadLifecycleReleasesOwnedCacheReferences()
    {
        var roots = new Roots();
        WeakReference previous = Publish(roots);
        Collect();
        AssertThat(previous.IsAlive).IsTrue();
        WeakReference replacement = Replace(roots);
        Collect();
        AssertThat(previous.IsAlive).IsFalse();
        AssertThat(replacement.IsAlive).IsTrue();
        Retire(roots);
        Collect();
        AssertThat(replacement.IsAlive).IsFalse();
        WeakReference reloaded = Publish(roots);
        Retire(roots);
        Collect();
        AssertThat(reloaded.IsAlive).IsFalse();
        AssertThat(roots.Current).IsNull();
    }

    /// <summary>A worker's real closure may retain the old owner, but release does not republish it or touch borrowed data.</summary>
    [TestCase]
    public void LateWorkerLeaseCanFinishAfterRetirement()
    {
        var roots = new Roots();
        var borrowed = new Borrowed();
        (WeakReference owner, Action release) = WorkerLease(roots, borrowed);
        Collect();
        AssertThat(owner.IsAlive).IsTrue();
        AssertThat(roots.Current).IsNull();
        release();
        Collect();
        AssertThat(owner.IsAlive).IsFalse();
        AssertThat(roots.Current).IsNull();
        AssertThat(borrowed.Disposed).IsFalse();
    }

    /// <summary>Owns only the simulated main-thread publication field.</summary>
    private sealed class Roots { internal Owner? Current; }

    /// <summary>Represents a borrowed shared value that retirement must never dispose.</summary>
    private sealed class Borrowed : IDisposable
    {
        /// <summary>Whether another consumer's shared value was incorrectly closed.</summary>
        internal bool Disposed { get; private set; }
        /// <summary>Marks explicit disposal so the lifecycle test detects consuming borrowed ownership.</summary>
        public void Dispose() => Disposed = true;
    }

    /// <summary>A cache-owning feed; deliberately equal-valued to verify identity rather than Equals.</summary>
    private sealed class Owner
    {
        private readonly byte[] _cache = new byte[4096];
        private readonly Borrowed? _borrowed;
        /// <summary>Holds cache bytes and an optional borrowed value without defining disposal ownership.</summary>
        internal Owner(Borrowed? borrowed = null) => _borrowed = borrowed;
        /// <summary>Deliberately treats every owner as value-equal; retirement must ignore this override.</summary>
        public override bool Equals(object? other) => other is Owner;
        /// <summary>Returns the same hash for the deliberately equal-valued owners.</summary>
        public override int GetHashCode() => 1;
    }

    /// <summary>Separates allocation from collection so JIT local-variable liveness cannot mask the root boundary.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Publish(Roots roots)
    {
        roots.Current = new Owner();
        return new WeakReference(roots.Current);
    }

    /// <summary>Publishes a new feed, then delivers the departing older feed's delayed exit.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Replace(Roots roots)
    {
        Owner old = roots.Current!;
        roots.Current = new Owner();
        if (GameCurrentOwner.Retire(ref roots.Current, old)) throw new InvalidOperationException("Replacement was erased");
        return new WeakReference(roots.Current);
    }

    /// <summary>Uses the same retirement policy as the actual GameEntities exit hook.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Retire(Roots roots) => GameCurrentOwner.Retire(ref roots.Current, roots.Current!);

    /// <summary>Transfers only an external worker closure's temporary reference beyond title retirement.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Owner, Action Release) WorkerLease(Roots roots, Borrowed borrowed)
    {
        Owner? held = new Owner(borrowed);
        roots.Current = held;
        var weak = new WeakReference(held);
        GameCurrentOwner.Retire(ref roots.Current, held);
        return (weak, () => held = null);
    }

    /// <summary>Requests collection only in these small managed lifecycle fixtures, never in production retirement.</summary>
    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
