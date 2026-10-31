using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using GdUnit4;
using Refii.Level;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Exercises retained-byte admission, overlapping reads and a complete synthetic level-read lifecycle.</summary>
[TestSuite]
public sealed class LevelReadsTests
{
    /// <summary>The Android policy halves retained capacity while desktop keeps its previous capacity.</summary>
    [TestCase]
    public void ProductionPoliciesKeepTheirDocumentedRetainedLimits()
    {
        AssertThat(new LevelReads(_ => null, android: true).Snapshot().LimitBytes).IsEqual(256L << 20);
        AssertThat(new LevelReads(_ => null, android: false).Snapshot().LimitBytes).IsEqual(512L << 20);
    }

    /// <summary>Small test budgets catch whole-file overshoot without allocating private or large game assets.</summary>
    [TestCase]
    public void AdmissionCountsCompleteFilesAndNeverExceedsTheBudget()
    {
        var reads = new LevelReads(_ => new byte[6], 10, 8, 2, 2);
        byte[] first = reads.Read("first")!;
        AssertThat(reads.Read("data/FIRST")).IsSame(first);
        AssertThat(reads.Read("second")!.Length).IsEqual(6);
        AssertThat(reads.Read("second")!.Length).IsEqual(6);
        var stats = reads.Snapshot();
        AssertThat(stats.HeldBytes).IsEqual(6L);
        AssertThat(stats.PeakHeldBytes).IsEqual(6L);
        AssertThat(stats.Entries).IsEqual(1);
        AssertThat(stats.CacheHits).IsEqual(1L);
        AssertThat(stats.BudgetSkips).IsEqual(2L);
        AssertThat(stats.SourceLoads).IsEqual(3L);
    }

    /// <summary>Oversize and zero-budget assets remain usable without becoming retained cache entries.</summary>
    [TestCase]
    public void OversizeAndZeroBudgetReadsAreUncachedButNotRejected()
    {
        var oversized = new LevelReads(_ => new byte[9], 100, 8, 1, 1);
        AssertThat(oversized.Read("large")!.Length).IsEqual(9);
        AssertThat(oversized.Read("large")!.Length).IsEqual(9);
        AssertThat(oversized.Snapshot().OversizeSkips).IsEqual(2L);
        AssertThat(oversized.Snapshot().HeldBytes).IsEqual(0L);
        var uncached = new LevelReads(_ => new byte[1], 0, 8, 1, 1);
        uncached.Read("one");
        AssertThat(uncached.Snapshot().BudgetSkips).IsEqual(1L);
        AssertThat(uncached.Snapshot().Entries).IsEqual(0);
    }

    /// <summary>Aliases share a cached absence and read errors are not permanently cached.</summary>
    [TestCase]
    public void MissingAliasesAreCachedAndExceptionsCanBeRetried()
    {
        int calls = 0;
        var missing = new LevelReads(_ => { ++calls; return null; }, 10, 10, 1, 1);
        AssertThat(missing.Read("missing")).IsNull();
        AssertThat(missing.Read("DATA/MISSING")).IsNull();
        AssertThat(calls).IsEqual(2);
        AssertThat(missing.Snapshot().Entries).IsEqual(1);
        bool fail = true;
        var retry = new LevelReads(_ => fail ? throw new IOException("transient") : new byte[3], 10, 10, 1, 1);
        bool threw = false;
        try { retry.Read("retry"); } catch (IOException) { threw = true; }
        AssertThat(threw).IsTrue();
        fail = false;
        AssertThat(retry.Read("retry")!.Length).IsEqual(3);
        AssertThat(retry.Snapshot().Failures).IsEqual(1L);
        AssertThat(retry.Snapshot().ActiveReaders).IsEqual(0);
    }

    /// <summary>Concurrent canonical aliases receive one byte-array result from one underlying read.</summary>
    [TestCase]
    public void OverlappingAliasesAreSingleFlight()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        var reads = new LevelReads(_ =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            if (!release.Wait(1000)) throw new TimeoutException("Test did not release its reader");
            return new byte[3];
        }, 10, 10, 4, 2);
        var results = new byte[]?[4];
        var errors = new Exception?[4];
        var threads = Enumerable.Range(0, 4).Select(i => new Thread(() =>
        {
            try { results[i] = reads.Read(i % 2 == 0 ? "data/SHARED" : "shared"); }
            catch (Exception error) { errors[i] = error; }
        })).ToArray();
        threads[0].Start();
        try
        {
            AssertThat(entered.Wait(1000)).IsTrue();
            foreach (var thread in threads.Skip(1)) thread.Start();
            AssertThat(SpinWait.SpinUntil(() => reads.Snapshot().CoalescedReads == 3, 1000)).IsTrue();
        }
        finally
        {
            release.Set();
            foreach (var thread in threads)
                if ((thread.ThreadState & ThreadState.Unstarted) == 0) thread.Join(1000);
        }
        AssertThat(calls).IsEqual(1);
        foreach (var error in errors) AssertThat(error).IsNull();
        foreach (var bytes in results) AssertThat(bytes).IsSame(results[0]);
        AssertThat(reads.Snapshot().ActiveReaders).IsEqual(0);
    }

    /// <summary>Distinct concurrent misses obey both the decode-worker limit and atomic retained-byte admission.</summary>
    [TestCase]
    public void ConcurrentAdmissionAndWorkingReadersStayBounded()
    {
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        int started = 0;
        var reads = new LevelReads(_ =>
        {
            if (Interlocked.Increment(ref started) <= 2) entered.Signal();
            if (!release.Wait(1000)) throw new TimeoutException("Test did not release read slots");
            return new byte[3];
        }, 8, 4, 4, 2);
        var errors = new Exception?[4];
        var threads = Enumerable.Range(0, 4).Select(i => new Thread(() =>
        {
            try { reads.Read("distinct-" + i); } catch (Exception error) { errors[i] = error; }
        })).ToArray();
        foreach (var thread in threads) thread.Start();
        try
        {
            AssertThat(entered.Wait(1000)).IsTrue();
            AssertThat(reads.Snapshot().ActiveReaders).IsEqual(2);
            AssertThat(Volatile.Read(ref started)).IsEqual(2);
        }
        finally { release.Set(); foreach (var thread in threads) thread.Join(1000); }
        foreach (var error in errors) AssertThat(error).IsNull();
        var stats = reads.Snapshot();
        AssertThat(stats.PeakReaders).IsEqual(2);
        AssertThat(stats.ActiveReaders).IsEqual(0);
        AssertThat(stats.HeldBytes).IsEqual(6L);
        AssertThat(stats.PeakHeldBytes).IsEqual(6L);
        AssertThat(stats.BudgetSkips).IsEqual(2L);
    }

    /// <summary>A nested synchronous reader reuses its slot, while same-path recursion fails and releases it.</summary>
    [TestCase]
    public void NestedReadsDoNotWaitForASecondSlotAndRecursionRemainsRetryable()
    {
        LevelReads reads = null!;
        reads = new LevelReads(path => path == "outer" ? reads.Read("inner") : new byte[2], 10, 10, 1, 1);
        AssertThat(reads.Read("outer")!.Length).IsEqual(2);
        AssertThat(reads.Snapshot().PeakReaders).IsEqual(1);
        AssertThat(reads.Snapshot().ActiveReaders).IsEqual(0);
        bool recurse = true;
        LevelReads recursive = null!;
        recursive = new LevelReads(path => recurse ? recursive.Read(path) : new byte[2], 10, 10, 1, 1);
        bool threw = false;
        try { recursive.Read("cycle"); } catch (InvalidOperationException) { threw = true; }
        AssertThat(threw).IsTrue();
        recurse = false;
        AssertThat(recursive.Read("cycle")!.Length).IsEqual(2);
        AssertThat(recursive.Snapshot().ActiveReaders).IsEqual(0);
        AssertThat(recursive.Snapshot().Failures).IsEqual(1L);
    }

    /// <summary>A nested reader must not await another request which is waiting for its only source slot.</summary>
    [TestCase]
    public void NestedReadBypassesAPendingReaderWaitingForItsSlot()
    {
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        LevelReads reads = null!;
        reads = new LevelReads(path =>
        {
            if (path != "outer") return new byte[2];
            entered.Set();
            if (!proceed.Wait(1000)) throw new TimeoutException("Test did not start the nested read");
            return reads.Read("inner");
        }, 10, 10, 2, 1);
        var errors = new Exception?[2];
        var outer = new Thread(() => { try { reads.Read("outer"); } catch (Exception error) { errors[0] = error; } });
        var inner = new Thread(() => { try { reads.Read("inner"); } catch (Exception error) { errors[1] = error; } });
        outer.Start();
        bool outerDone = false, innerDone = false;
        try
        {
            AssertThat(entered.Wait(1000)).IsTrue();
            inner.Start();
            AssertThat(SpinWait.SpinUntil(() => reads.Snapshot().PendingPaths == 2, 1000)).IsTrue();
        }
        finally
        {
            proceed.Set();
            outerDone = outer.Join(1000);
            if ((inner.ThreadState & ThreadState.Unstarted) == 0) innerDone = inner.Join(1000);
        }
        AssertThat(outerDone).IsTrue();
        AssertThat(innerDone).IsTrue();
        foreach (var error in errors) AssertThat(error).IsNull();
        AssertThat(reads.Snapshot().SourceLoads).IsEqual(2L);
        AssertThat(reads.Snapshot().PeakReaders).IsEqual(1);
        AssertThat(reads.Snapshot().PendingPaths).IsEqual(0);
    }

    /// <summary>Gzip bytes, missing assets, exceptions and a replacement level form one realistic pure pipeline.</summary>
    [TestCase]
    public void PrefetchDecodeCompleteAndReplacementHaveIndependentLifetimes()
    {
        byte[] payload = Enumerable.Repeat((byte)7, 32).ToArray();
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(payload);
        byte[] compressed = output.ToArray();
        var reads = new LevelReads(path => path switch
        {
            "world.gdb" => compressed,
            "broken" => throw new IOException("bad synthetic entry"),
            _ => null
        }, 64, 64, 2, 2);
        var result = reads.Prefetch(new[] { "world.gdb", "missing", "broken" });
        AssertThat(result.Fetched).IsEqual(1);
        AssertThat(result.Missing.OrderBy(p => p).ToArray()).IsEqual(new[] { "broken", "missing" });
        AssertThat(reads.Read("data/WORLD.GDB")!).IsEqual(payload);
        var completed = reads.Complete();
        AssertThat(completed.HeldBytes).IsEqual(32L);
        AssertThat(completed.SourceBytes).IsEqual((long)compressed.Length);
        AssertThat(completed.DecodedBytes).IsEqual(32L);
        AssertThat(completed.Inflations).IsEqual(1L);
        AssertThat(completed.Failures).IsEqual(1L);
        AssertThat(completed.ActiveReaders).IsEqual(0);
        AssertThat(LevelReads.LastCompleted!.Value.Id).IsEqual(completed.Id);
        var next = new LevelReads(_ => new byte[] { 9 }, 64, 64, 2, 2);
        AssertThat(next.Read("world.gdb")!).IsEqual(new byte[] { 9 });
        AssertThat(next.Snapshot().Id == completed.Id).IsFalse();
        AssertThat(reads.Read("world.gdb")!).IsEqual(payload);
        AssertThat(LevelReads.LastCompleted!.Value.CacheHits).IsEqual(completed.CacheHits);
    }

    /// <summary>An empty prefetch performs no underlying work and malformed bounds fail before use.</summary>
    [TestCase]
    public void EmptyPrefetchAndInvalidBoundsAreExplicit()
    {
        var reads = new LevelReads(_ => throw new Exception("must not read"), 0, 0, 1, 1);
        AssertThat(reads.Prefetch(Array.Empty<string>()).Fetched).IsEqual(0);
        AssertThat(reads.Snapshot().SourceCalls).IsEqual(0L);
        bool threw = false;
        try { _ = new LevelReads(_ => null, 0, 0, 0, 1); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        AssertThat(threw).IsTrue();
    }
}
