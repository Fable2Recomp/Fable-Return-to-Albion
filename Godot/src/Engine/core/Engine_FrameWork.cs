using System;
using System.Diagnostics;

namespace Refii.Core;

/// <summary>Accumulates repeated subsystem work and publishes one total per phase per frame.</summary>
/// <remarks>Main-thread owned. Recording each actor through FramePhases directly would average
/// actors instead of frames. Zero-work frames are published too, so inactive history decays.</remarks>
internal sealed class Engine_FrameWork
{
    private readonly string[] _names;
    private readonly double[] _ticks;

    /// <summary>Copies phase names and allocates reusable counters once.</summary>
    internal Engine_FrameWork(params string[] names)
    {
        if (names.Length == 0) throw new ArgumentException("At least one phase is required.", nameof(names));
        foreach (string name in names)
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Phase names must be nonempty.", nameof(names));
        _names = (string[])names.Clone();
        _ticks = new double[names.Length];
    }

    /// <summary>Starts a fresh frame, including frames whose subsystem returns early.</summary>
    internal Frame BeginFrame()
    {
        Array.Clear(_ticks);
        return new Frame(this);
    }

    /// <summary>Accumulates one elapsed segment and starts the next at the same timestamp.</summary>
    internal void Lap(int phase, ref long since)
    {
        long now = Stopwatch.GetTimestamp();
        AddTicks(phase, Math.Max(0, now - since));
        since = now;
    }

    /// <summary>Adds nonnegative stopwatch ticks; deterministic input permits headless tests.</summary>
    internal void AddTicks(int phase, long ticks)
    {
        if (ticks < 0) throw new ArgumentOutOfRangeException(nameof(ticks));
        _ticks[phase] += ticks;
    }

    /// <summary>Publishes scalar frame totals; the sink seam also tests the full frame lifecycle.</summary>
    internal void Publish(Action<string, double>? sink = null)
    {
        for (int i = 0; i < _names.Length; i++)
        {
            double ms = _ticks[i] * 1000.0 / Stopwatch.Frequency;
            if (sink is null) Engine_FramePhases.Record(_names[i], ms, false);
            else sink(_names[i], ms);
        }
    }

    /// <summary>Publishes even when a subsystem returns early or throws during its frame.</summary>
    internal readonly struct Frame : IDisposable
    {
        private readonly Engine_FrameWork _owner;

        /// <summary>Retains the main-thread frame's accumulator without boxing or allocations.</summary>
        internal Frame(Engine_FrameWork owner) => _owner = owner;

        /// <summary>Completes the frame's one publication per phase.</summary>
        public void Dispose() => _owner.Publish();
    }
}
