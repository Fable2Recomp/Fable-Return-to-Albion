using System;
using System.Collections.Generic;

namespace Refii.Core;

/// <summary>
/// Publishes complete GPU timestamp frames and copies detached scalar reports without Godot interop.
/// Historical averages remain useful, but an absent viewport is explicitly stale rather than part
/// of the latest frame. Input collections belong to the publisher and remain stable during a call.
/// </summary>
public sealed class GpuPassHistory
{
    /// <summary>Retain the previous report's 300-published-frame visibility window.</summary>
    private const long RetentionFrames = 300;

    /// <summary>Mutable rolling state accessed only while the history gate is held.</summary>
    private sealed class Pass
    {
        public double AverageMs, PeakMs, LastMs;
        public int Order;
        public long LastFrame;
        public ulong CapturedFrame;
    }

    /// <summary>One scalar row copied under the gate; no mutable Pass escapes publication.</summary>
    private readonly record struct Row(string Name, double AverageMs, double PeakMs, double LastMs,
        int Order, long LastFrame, ulong CapturedFrame, long FrameAge, ulong? CapturedFrameAge);

    /// <summary>One completed publication with privately owned, read-only scalar rows.</summary>
    private sealed record Snapshot(long Frames, ulong? CapturedFrame, double LastTotalMs,
        IReadOnlyList<Row> Rows);

    private readonly object _gate = new();
    private readonly Dictionary<string, Pass> _passes = new();
    private readonly HashSet<string> _pendingNames = new();
    private readonly List<(string Name, double Ms)> _pendingRows = new();
    private long _frames;
    private ulong? _capturedFrame;
    private double _lastTotalMs;

    /// <summary>Whether at least one whole timestamp frame has been published.</summary>
    public bool Available { get { lock (_gate) return _frames != 0; } }

    /// <summary>The most recently completed frame's first-to-last timestamp span.</summary>
    public double LastTotalMs { get { lock (_gate) return _lastTotalMs; } }

    /// <summary>
    /// Validate and publish all aggregated intervals plus their total as one completed frame.
    /// The gate covers the entire publication so a reader never mixes two frames. Duplicate
    /// captured IDs do not advance rolling averages. Scratch storage is reused between calls.
    /// </summary>
    /// <param name="capturedFrame">The actual RenderingDevice captured-frame identifier.</param>
    /// <param name="order">Unique interval keys in the engine's timestamp order.</param>
    /// <param name="sums">Already aggregated milliseconds for each ordered key.</param>
    /// <param name="totalMs">The first-to-last timestamp span, independently supplied by the sampler.</param>
    /// <returns>True for a newly published frame; false for the same captured ID as the last publication.</returns>
    public bool PublishCompletedFrame(ulong capturedFrame, IReadOnlyList<string> order,
        IReadOnlyDictionary<string, double> sums, double totalMs)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(sums);
        lock (_gate)
        {
            if (_capturedFrame == capturedFrame) return false;
            if (order.Count != sums.Count || !double.IsFinite(totalMs))
                throw new ArgumentException("A completed GPU frame requires matching keys and a finite total.");

            // Stage before changing rolling state: even a failed source enumeration must not
            // leave partially updated rows behind a previously completed frame identity.
            _pendingNames.Clear();
            _pendingRows.Clear();
            foreach (string name in order)
            {
                if (name is null || !_pendingNames.Add(name) ||
                    !sums.TryGetValue(name, out double ms) || !double.IsFinite(ms))
                    throw new ArgumentException("A completed GPU frame requires unique keys with finite intervals.");
                _pendingRows.Add((name, ms));
            }

            long publication = _frames + 1;
            for (int i = 0; i < _pendingRows.Count; ++i)
                Record(_pendingRows[i].Name, _pendingRows[i].Ms, i, publication, capturedFrame);
            Record("total", totalMs, int.MaxValue, publication, capturedFrame);
            _lastTotalMs = totalMs;
            _capturedFrame = capturedFrame;
            _frames = publication;
            return true;
        }
    }

    /// <summary>Update one rolling row while its whole-frame publisher owns the gate.</summary>
    private void Record(string name, double ms, int order, long publication, ulong capturedFrame)
    {
        if (!_passes.TryGetValue(name, out Pass? pass))
        {
            pass = new Pass { AverageMs = ms };
            _passes.Add(name, pass);
        }
        pass.LastMs = ms;
        pass.LastFrame = publication;
        pass.CapturedFrame = capturedFrame;
        pass.Order = order;
        pass.AverageMs += (ms - pass.AverageMs) * 0.05;
        pass.PeakMs = Math.Max(pass.PeakMs * 0.98, ms);
    }

    /// <summary>Copy metadata and every scalar row under the same gate as whole-frame publication.</summary>
    private Snapshot Capture()
    {
        lock (_gate)
        {
            var rows = new List<Row>(_passes.Count);
            foreach ((string name, Pass pass) in _passes)
            {
                long age = _frames - pass.LastFrame;
                if (age >= RetentionFrames) continue;
                ulong? capturedAge = _capturedFrame is ulong current && current >= pass.CapturedFrame
                    ? current - pass.CapturedFrame : null;
                rows.Add(new Row(name, pass.AverageMs, pass.PeakMs, pass.LastMs, pass.Order,
                    pass.LastFrame, pass.CapturedFrame, age, capturedAge));
            }
            rows.Sort((a, b) => a.Order.CompareTo(b.Order));
            return new Snapshot(_frames, _capturedFrame, _lastTotalMs, rows.AsReadOnly());
        }
    }

    /// <summary>
    /// Return a fresh JSON-compatible report from one immutable scalar snapshot. Existing timing
    /// fields and collection types are preserved. Only current interval rows, excluding total,
    /// belong in a latest-frame sum; averages and peaks intentionally include earlier frames.
    /// </summary>
    /// <returns>Detached dictionaries/lists whose mutation cannot alter subsequent reports.</returns>
    public Dictionary<string, object?> Report()
    {
        Snapshot snapshot = Capture();
        var report = new Dictionary<string, object?>
        {
            ["available"] = snapshot.Frames != 0,
            ["frames"] = snapshot.Frames,
            ["capturedFrame"] = snapshot.CapturedFrame,
        };
        if (snapshot.Frames == 0)
        {
            report["note"] = "no completed GPU timestamp frame has been published — launch with --gpu-profile " +
                "(the desktop launcher uses gpu_profile = true in refii.toml) and relaunch";
            return report;
        }

        int currentCount = 0, staleCount = 0;
        var passes = new List<Dictionary<string, object?>>(snapshot.Rows.Count);
        foreach (Row row in snapshot.Rows)
        {
            bool current = row.FrameAge == 0;
            if (row.Name != "total") { if (current) ++currentCount; else ++staleCount; }
            passes.Add(new Dictionary<string, object?>
            {
                ["pass"] = row.Name,
                ["avgMs"] = Math.Round(row.AverageMs, 3),
                ["peakMs"] = Math.Round(row.PeakMs, 3),
                ["lastMs"] = Math.Round(row.LastMs, 3),
                ["lastFrame"] = row.LastFrame,
                ["capturedFrame"] = row.CapturedFrame,
                ["frameAge"] = row.FrameAge,
                ["capturedFrameAge"] = row.CapturedFrameAge,
                ["current"] = current,
            });
        }
        report["passes"] = passes;
        report["lastTotalMs"] = Math.Round(snapshot.LastTotalMs, 3);
        report["currentPassCount"] = currentCount;
        report["stalePassCount"] = staleCount;
        report["pass_scope"] = "current marks the latest completed publication; sum only current lastMs intervals " +
            "excluding total. frameAge counts published samples; capturedFrameAge is the device-ID difference " +
            "when monotonic. avgMs/peakMs retain history, including inactive passes.";
        return report;
    }
}
