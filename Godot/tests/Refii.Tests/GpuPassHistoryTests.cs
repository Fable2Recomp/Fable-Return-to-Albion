using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using GdUnit4;
using Refii.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Checks whole-frame publication, stale viewport lifecycles and concurrent scalar snapshots without Godot.</summary>
[TestSuite]
public sealed class GpuPassHistoryTests
{
    /// <summary>No native table or class initializer is required to report unavailable capture.</summary>
    [TestCase]
    public void EmptyHistoryIsUnavailableWithNoInventedFrame()
    {
        var history = new GpuPassHistory();
        Dictionary<string, object?> report = history.Report();
        AssertThat(history.Available).IsFalse();
        AssertThat(history.LastTotalMs).IsEqual(0.0);
        AssertThat((bool)report["available"]!).IsFalse();
        AssertThat((long)report["frames"]!).IsEqual(0L);
        AssertThat(report["capturedFrame"]).IsNull();
        AssertThat(report.ContainsKey("passes")).IsFalse();
        AssertThat(((string)report["note"]!).Contains("--gpu-profile", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>The first publication preserves pass order, existing fields and actual captured identity.</summary>
    [TestCase]
    public void CompletedFramePreservesExistingFieldsAndAddsIdentity()
    {
        var history = new GpuPassHistory();
        var sums = new Dictionary<string, double> { ["vp3/shadows"] = 3, ["vp3/opaque"] = 7 };
        AssertThat(history.PublishCompletedFrame(1042, new[] { "vp3/opaque", "vp3/shadows" }, sums, 10)).IsTrue();
        Dictionary<string, object?> report = history.Report();
        AssertThat(history.Available).IsTrue();
        AssertThat(history.LastTotalMs).IsEqual(10.0);
        AssertThat((long)report["frames"]!).IsEqual(1L);
        AssertThat((ulong)report["capturedFrame"]!).IsEqual(1042UL);
        AssertThat(Rows(report).Select(row => (string)row["pass"]!).ToArray())
            .IsEqual(new[] { "vp3/opaque", "vp3/shadows", "total" });
        AssertThat((int)report["currentPassCount"]!).IsEqual(2);
        AssertThat((int)report["stalePassCount"]!).IsEqual(0);
        foreach (Dictionary<string, object?> row in Rows(report))
        {
            AssertThat((long)row["lastFrame"]!).IsEqual(1L);
            AssertThat((long)row["frameAge"]!).IsEqual(0L);
            AssertThat((ulong)row["capturedFrame"]!).IsEqual(1042UL);
            AssertThat((ulong)row["capturedFrameAge"]!).IsEqual(0UL);
            AssertThat((bool)row["current"]!).IsTrue();
            AssertThat((double)row["avgMs"]!).IsEqual((double)row["lastMs"]!);
            AssertThat((double)row["peakMs"]!).IsEqual((double)row["lastMs"]!);
        }
        AssertCoherent(report);
    }

    /// <summary>Existing exponential average and decaying-peak arithmetic remain unchanged.</summary>
    [TestCase]
    public void RollingAverageAndPeakRetainTheirPreviousWeights()
    {
        var history = new GpuPassHistory();
        Publish(history, 10, ("opaque", 10), ("shadow", 10));
        Publish(history, 20, ("opaque", 2), ("shadow", 2));
        Dictionary<string, object?> row = Rows(history.Report()).Single(row => (string)row["pass"]! == "opaque");
        AssertThat((double)row["avgMs"]!).IsEqual(9.6);
        AssertThat((double)row["peakMs"]!).IsEqual(9.8);
        AssertThat((double)row["lastMs"]!).IsEqual(2.0);
        AssertThat((long)row["lastFrame"]!).IsEqual(2L);
        AssertThat((ulong)row["capturedFrame"]!).IsEqual(20UL);
        AssertCoherent(history.Report());
    }

    /// <summary>Loading viewports become stale, world rows are current, and reappearing views retain history.</summary>
    [TestCase]
    public void LoadWorldReturnLifecycleSeparatesHistoricalPassesFromCurrentTotal()
    {
        var history = new GpuPassHistory();
        Publish(history, 10, ("vp5/loading", 20));
        Dictionary<string, object?> loading = history.Report();
        Publish(history, 20, ("vp8/world", 3));
        Dictionary<string, object?> world = history.Report();
        Dictionary<string, object?> old = Rows(world).Single(row => (string)row["pass"]! == "vp5/loading");
        AssertThat((bool)old["current"]!).IsFalse();
        AssertThat((long)old["lastFrame"]!).IsEqual(1L);
        AssertThat((long)old["frameAge"]!).IsEqual(1L);
        AssertThat((ulong)old["capturedFrame"]!).IsEqual(10UL);
        AssertThat((ulong)old["capturedFrameAge"]!).IsEqual(10UL);
        AssertThat((int)world["currentPassCount"]!).IsEqual(1);
        AssertThat((int)world["stalePassCount"]!).IsEqual(1);
        AssertThat((double)loading["lastTotalMs"]!).IsEqual(20.0);
        AssertThat((long)loading["frames"]!).IsEqual(1L);
        AssertCoherent(world);
        Publish(history, 25, ("vp5/loading", 4));
        Dictionary<string, object?> returned = Rows(history.Report()).Single(row => (string)row["pass"]! == "vp5/loading");
        AssertThat((bool)returned["current"]!).IsTrue();
        AssertThat((double)returned["avgMs"]!).IsEqual(19.2);
        AssertThat((double)returned["peakMs"]!).IsEqual(19.6);
        AssertThat((long)returned["frameAge"]!).IsEqual(0L);
        AssertCoherent(history.Report());
    }

    /// <summary>Stale rows remain through age 299 and disappear at the original 300-publication boundary.</summary>
    [TestCase]
    public void RetentionUsesPublishedFramesRatherThanGapsInDeviceIds()
    {
        var history = new GpuPassHistory();
        Publish(history, 1, ("old", 100));
        for (ulong frame = 2; frame <= 300; ++frame) Publish(history, frame * 1000, ("world", 2));
        Dictionary<string, object?> row = Rows(history.Report()).Single(row => (string)row["pass"]! == "old");
        AssertThat((long)row["frameAge"]!).IsEqual(299L);
        AssertThat((ulong)row["capturedFrameAge"]!).IsEqual(299999UL);
        Publish(history, 301000, ("world", 2));
        AssertThat(Rows(history.Report()).Any(row => (string)row["pass"]! == "old")).IsFalse();
        AssertThat((int)history.Report()["stalePassCount"]!).IsEqual(0);
        AssertCoherent(history.Report());
    }

    /// <summary>Repeated captures do not skew averages, and device-ID reset is represented without unsigned underflow.</summary>
    [TestCase]
    public void DuplicateCaptureAndCounterResetKeepPublicationIdentityHonest()
    {
        var history = new GpuPassHistory();
        Publish(history, ulong.MaxValue, ("old", 4));
        string previous = JsonSerializer.Serialize(history.Report());
        AssertThat(history.PublishCompletedFrame(ulong.MaxValue, new[] { "changed" },
            new Dictionary<string, double> { ["changed"] = 500 }, 500)).IsFalse();
        AssertThat(JsonSerializer.Serialize(history.Report())).IsEqual(previous);
        Publish(history, 0, ("new", 2));
        Dictionary<string, object?> old = Rows(history.Report()).Single(row => (string)row["pass"]! == "old");
        AssertThat((long)old["frameAge"]!).IsEqual(1L);
        AssertThat(old["capturedFrameAge"]).IsNull();
        AssertThat((bool)old["current"]!).IsFalse();
        AssertThat((ulong)history.Report()["capturedFrame"]!).IsEqual(0UL);
        AssertCoherent(history.Report());
    }

    /// <summary>Caller changes and report mutation cannot alter retained history or another report.</summary>
    [TestCase]
    public void InputAndSerializedReportsHaveNoMutableHistoryAliases()
    {
        var history = new GpuPassHistory();
        var order = new List<string> { "opaque" };
        var sums = new Dictionary<string, double> { ["opaque"] = 7 };
        history.PublishCompletedFrame(1, order, sums, 7);
        Dictionary<string, object?> report = history.Report();
        order.Clear();
        sums["opaque"] = 900;
        Rows(report)[0]["lastMs"] = 999.0;
        Rows(report).Clear();
        report["lastTotalMs"] = 999.0;
        AssertThat((double)history.Report()["lastTotalMs"]!).IsEqual(7.0);
        AssertThat((double)Rows(history.Report())[0]["lastMs"]!).IsEqual(7.0);
        AssertThat(Rows(history.Report()).Count).IsEqual(2);
        AssertCoherent(history.Report());
    }

    /// <summary>Bad or throwing frame inputs cannot publish partial rows, identities or rolling averages.</summary>
    [TestCase]
    public void FailedFrameStagingPreservesPreviousPublicationAndCanRetry()
    {
        var history = new GpuPassHistory();
        Publish(history, 1, ("a", 1), ("b", 10));
        string previous = JsonSerializer.Serialize(history.Report());
        Action[] failures =
        {
            () => history.PublishCompletedFrame(2, null!, new Dictionary<string, double>(), 0),
            () => history.PublishCompletedFrame(2, Array.Empty<string>(), null!, 0),
            () => history.PublishCompletedFrame(2, new[] { "a" }, new Dictionary<string, double>(), 1),
            () => history.PublishCompletedFrame(2, new[] { "a", "a" }, new Dictionary<string, double> { ["a"] = 2, ["b"] = 20 }, 22),
            () => history.PublishCompletedFrame(2, new[] { "a", "missing" }, new Dictionary<string, double> { ["a"] = 2, ["b"] = 20 }, 22),
            () => history.PublishCompletedFrame(2, new[] { "a" }, new Dictionary<string, double> { ["a"] = double.NaN }, 1),
            () => history.PublishCompletedFrame(2, Array.Empty<string>(), new Dictionary<string, double>(), double.PositiveInfinity),
            () => history.PublishCompletedFrame(2, new[] { "a", "b" }, new ControlledValues(
                new Dictionary<string, double> { ["a"] = 2, ["b"] = 20 }, name => { if (name == "b") throw new InvalidOperationException("source failed"); }), 22),
        };
        foreach (Action fail in failures)
        {
            bool rejected = false;
            try { fail(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { rejected = true; }
            AssertThat(rejected).IsTrue();
            AssertThat(JsonSerializer.Serialize(history.Report())).IsEqual(previous);
        }
        Publish(history, 2, ("a", 2), ("b", 20));
        AssertThat((long)history.Report()["frames"]!).IsEqual(2L);
        AssertCoherent(history.Report());
    }

    /// <summary>A reader arriving during a blocked whole-frame publication receives only its completed result.</summary>
    [TestCase]
    public void ConcurrentReaderCannotObserveAnInProgressFrame()
    {
        var history = new GpuPassHistory();
        Publish(history, 1, ("a", 1), ("b", 10));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var readerStarted = new ManualResetEventSlim();
        Exception? writerError = null, readerError = null;
        Dictionary<string, object?>? observed = null;
        var values = new ControlledValues(new Dictionary<string, double> { ["a"] = 2, ["b"] = 20 }, name =>
        {
            if (name != "b") return;
            entered.Set();
            if (!release.Wait(1000)) throw new TimeoutException();
        });
        var writer = new Thread(() => { try { history.PublishCompletedFrame(2, new[] { "a", "b" }, values, 22); } catch (Exception e) { writerError = e; } }) { IsBackground = true };
        var reader = new Thread(() => { readerStarted.Set(); try { observed = history.Report(); } catch (Exception e) { readerError = e; } }) { IsBackground = true };
        writer.Start();
        bool staged = entered.Wait(1000);
        reader.Start();
        bool started = readerStarted.Wait(1000);
        bool prematurelyFinished;
        try { prematurelyFinished = reader.Join(20); }
        finally { release.Set(); }
        bool joined = writer.Join(1000) && reader.Join(1000);
        AssertThat(staged && started && joined).IsTrue();
        AssertThat(prematurelyFinished).IsFalse();
        AssertThat(writerError).IsNull();
        AssertThat(readerError).IsNull();
        AssertThat((ulong)observed!["capturedFrame"]!).IsEqual(2UL);
        AssertThat((double)observed["lastTotalMs"]!).IsEqual(22.0);
        AssertCoherent(observed);
    }

    /// <summary>Repeated concurrent frame publication and reporting preserve coherent totals and identities across viewport changes.</summary>
    [TestCase]
    public void ConcurrentLoadWorldReportsNeverMixScalarRowsFromDifferentFrames()
    {
        var history = new GpuPassHistory();
        using var start = new ManualResetEventSlim();
        using var firstPublished = new ManualResetEventSlim();
        Exception? writerError = null, readerError = null;
        int reads = 0;
        var writer = new Thread(() =>
        {
            try
            {
                if (!start.Wait(1000)) throw new TimeoutException();
                for (ulong frame = 1; frame <= 200; ++frame)
                {
                    Publish(history, frame * 7, (frame % 2 == 0 ? "vp3/world" : "vp5/load", frame), ("common", frame * 2));
                    if (frame == 1) firstPublished.Set();
                }
            }
            catch (Exception e) { writerError = e; }
        }) { IsBackground = true };
        var reader = new Thread(() =>
        {
            try
            {
                if (!start.Wait(1000)) throw new TimeoutException();
                if (!firstPublished.Wait(1000)) throw new TimeoutException();
                for (int i = 0; i < 400; ++i)
                {
                    Dictionary<string, object?> report = history.Report();
                    if (!(bool)report["available"]!) continue;
                    AssertCoherent(report);
                    ++reads;
                }
            }
            catch (Exception e) { readerError = e; }
        }) { IsBackground = true };
        writer.Start(); reader.Start(); start.Set();
        bool joined = writer.Join(1000) && reader.Join(1000);
        AssertThat(joined).IsTrue();
        AssertThat(writerError).IsNull();
        AssertThat(readerError).IsNull();
        AssertThat(reads > 0).IsTrue();
        AssertThat((long)history.Report()["frames"]!).IsEqual(200L);
        AssertCoherent(history.Report());
    }

    /// <summary>Publish a synthetic frame through the same aggregate/key interface used by the production sampler.</summary>
    private static void Publish(GpuPassHistory history, ulong frame, params (string Name, double Ms)[] intervals)
    {
        history.PublishCompletedFrame(frame, intervals.Select(row => row.Name).ToArray(),
            intervals.ToDictionary(row => row.Name, row => row.Ms), intervals.Sum(row => row.Ms));
    }

    /// <summary>Read the preserved JSON-compatible collection type used by existing debug/UI consumers.</summary>
    private static List<Dictionary<string, object?>> Rows(Dictionary<string, object?> report) =>
        (List<Dictionary<string, object?>>)report["passes"]!;

    /// <summary>Throw on cross-frame scalar mixtures; safe to invoke from concurrent test workers.</summary>
    private static void AssertCoherent(Dictionary<string, object?> report)
    {
        long publication = (long)report["frames"]!;
        ulong captured = (ulong)report["capturedFrame"]!;
        double sum = 0;
        int current = 0, stale = 0;
        foreach (Dictionary<string, object?> row in Rows(report))
        {
            bool isCurrent = (bool)row["current"]!;
            long age = (long)row["frameAge"]!;
            if (age != publication - (long)row["lastFrame"]! || isCurrent != (age == 0))
                throw new InvalidOperationException("Mixed publication/age metadata.");
            if (isCurrent && (ulong)row["capturedFrame"]! != captured)
                throw new InvalidOperationException("Mixed captured frame identifiers.");
            if ((string)row["pass"]! == "total")
            {
                if (!isCurrent || (double)row["lastMs"]! != (double)report["lastTotalMs"]!)
                    throw new InvalidOperationException("Total belongs to another frame.");
                continue;
            }
            if (isCurrent) { sum += (double)row["lastMs"]!; ++current; } else ++stale;
        }
        if (Math.Abs(sum - (double)report["lastTotalMs"]!) > 0.001 ||
            current != (int)report["currentPassCount"]! || stale != (int)report["stalePassCount"]!)
            throw new InvalidOperationException("Current pass sum/counts do not match the completed frame.");
    }

    /// <summary>An owned read-only frame source whose read boundary can deterministically block or fail.</summary>
    private sealed class ControlledValues : IReadOnlyDictionary<string, double>
    {
        private readonly IReadOnlyDictionary<string, double> _values;
        private readonly Action<string> _beforeRead;
        /// <summary>Create a controlled source without exposing publication internals as test hooks.</summary>
        public ControlledValues(IReadOnlyDictionary<string, double> values, Action<string> beforeRead)
        { _values = values; _beforeRead = beforeRead; }
        /// <summary>The ordinary source indexer.</summary>
        public double this[string key] => _values[key];
        /// <summary>The number of aggregated intervals.</summary>
        public int Count => _values.Count;
        /// <summary>The source keys.</summary>
        public IEnumerable<string> Keys => _values.Keys;
        /// <summary>The source milliseconds.</summary>
        public IEnumerable<double> Values => _values.Values;
        /// <summary>Check source membership.</summary>
        public bool ContainsKey(string key) => _values.ContainsKey(key);
        /// <summary>Run a controlled read boundary before returning its scalar interval.</summary>
        public bool TryGetValue(string key, out double value) { _beforeRead(key); return _values.TryGetValue(key, out value); }
        /// <summary>Enumerate the original aggregated values.</summary>
        public IEnumerator<KeyValuePair<string, double>> GetEnumerator() => _values.GetEnumerator();
        /// <summary>Enumerate the original values through the nongeneric interface.</summary>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
