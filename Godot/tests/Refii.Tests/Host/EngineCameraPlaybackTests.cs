namespace Refii.Tests;

using System;
using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Core;

/// <summary>
/// The camera's playback through the shared pair playhead and its tick timeline, driven the way
/// the frame drives them: the stamp read at frame start, the camera read milliseconds later, the
/// guest's step finishing at its own instant. Ticks arrive 66 ± 8 ms apart (the measured
/// pattern), frames are drawn every 7.8 ms.
/// </summary>
[TestSuite]
public class EngineCameraPlaybackTests
{
    private const double TickUsec = Engine_TickClock.TickUsec;
    private const double FrameUsec = 7_800.0;

    /// <summary>Camera travel per tick, metres: 7.5 m/s, the measured pan.</summary>
    private const float MetresPerTick = 0.5f;

    /// <summary>Arrival offsets from the nominal tick instant, in microseconds; the measured 57–75 ms gaps.</summary>
    private static readonly double[] JitterUsec =
    {
        8_000, -8_000, 4_000, 0, 8_000, -4_000, 2_000, -6_000, 8_000, 6_000, -8_000, 1_000, 8_000, -2_000, 5_000, -8_000,
    };

    private sealed class Run
    {
        public readonly List<double> Speeds = new();
        public readonly List<double> DrawTicks = new();
        public readonly List<double> NewestTicks = new();
        public readonly List<double> MarginSteps = new();
        public readonly List<double> ElapsedTicks = new();
        public readonly List<double> RawAlphas = new();
        public double MeanSpeed;
        public int WarmFrames;
        public int SwapsExtrapolating;
        public int Swaps;
    }

    private static Transform3D CameraAt(uint tick) =>
        new(new Basis(Vector3.Up, 0.02f * tick), new Vector3(MetresPerTick * tick, 1f, 0f));

    private static double ArrivalUsec(uint tick) => tick * TickUsec + JitterUsec[tick % JitterUsec.Length];

    /// <param name="gated">
    /// True: the camera is a statement only on a frame whose step completed before frame start,
    /// as GameWorld now does. False: every frame observes, with the stamp read at frame start —
    /// the defect.
    /// </param>
    /// <param name="readOffsetUsec">How far into the frame the camera is read after the stamp.</param>
    private static Run Simulate(bool gated, double readOffsetUsec, double seconds, ulong baseUsec)
    {
        var pair = new Engine_PairPlayhead(TickUsec, carry: true);
        var line = new Engine_TickTimeline();
        var run = new Run();

        uint statedTick = 0;
        uint newestSeenAtStart = 0;
        double lastStart = double.NaN;
        Vector3? lastDrawn = null;
        double lastDrawTick = double.NaN;
        double lastMargin = 0.0;
        double warmUsec = 2.5e6;

        for (double t = 20.0 * TickUsec; t < 20.0 * TickUsec + seconds * 1e6; t += FrameUsec)
        {
            ulong now = baseUsec + (ulong)t;
            pair.BeginFrame(now);

            // Frame start: the stamp and the HF-pass counter, read in one instant. The newest tick
            // whose step has finished by now is the stamp; the step is "complete" when it landed
            // since the last frame start.
            uint tick = newestSeenAtStart;
            while (ArrivalUsec(tick + 1) <= t)
            {
                ++tick;
            }

            bool stepComplete = tick != newestSeenAtStart;
            newestSeenAtStart = tick;
            if (tick != statedTick)
            {
                int steps = statedTick == 0 ? 1 : (int)(tick - statedTick);
                pair.Swap(now, steps);
                statedTick = tick;
                ++run.Swaps;
                if (pair.LastSwapAlpha - (steps - 1) - 1.0 > pair.MarginTicks)
                {
                    ++run.SwapsExtrapolating;
                }
            }

            double drawTick = statedTick - 1.0 + pair.DrawAlpha(now);

            // Later in the frame: the camera, which may already be the next step's.
            uint cameraTick = tick;
            while (ArrivalUsec(cameraTick + 1) <= t + readOffsetUsec)
            {
                ++cameraTick;
            }

            Transform3D stated = CameraAt(cameraTick);
            bool observe = line.Empty || !gated || stepComplete;
            if (line.Empty)
            {
                line.Reset(statedTick, stated);
            }
            else if (observe)
            {
                line.Observe(statedTick, stated);
            }

            Transform3D drawn = line.Advance(now, drawTick);

            if (t >= 20.0 * TickUsec + warmUsec && lastDrawn is { } previous && !double.IsNaN(lastStart))
            {
                double dt = (t - lastStart) * 1e-6;
                run.Speeds.Add(drawn.Origin.DistanceTo(previous) / dt);
                run.DrawTicks.Add(drawTick);
                run.NewestTicks.Add(line.NewestTick);
                run.RawAlphas.Add(pair.Alpha(now));
                run.MarginSteps.Add(Math.Abs(pair.MarginTicks - lastMargin));
                run.ElapsedTicks.Add((t - lastStart) / TickUsec);
                if (!double.IsNaN(lastDrawTick))
                {
                    AssertThat(drawTick >= lastDrawTick).OverrideFailureMessage(
                        $"draw point went backwards: {lastDrawTick} -> {drawTick}").IsTrue();
                }
            }

            lastDrawn = drawn.Origin;
            lastStart = t;
            lastDrawTick = drawTick;
            lastMargin = pair.MarginTicks;
        }

        double sum = 0.0;
        foreach (double s in run.Speeds)
        {
            sum += s;
        }

        run.MeanSpeed = sum / run.Speeds.Count;
        run.WarmFrames = run.Speeds.Count;
        return run;
    }

    [TestCase]
    public void TestGatedCameraStaysWithinHalfToOneAndAHalfOfMeanSpeed()
    {
        Run run = Simulate(gated: true, readOffsetUsec: 4_000.0, seconds: 12.0, baseUsec: 100_000_000UL);
        AssertThat(run.WarmFrames).IsGreater(1000);

        double slowest = double.MaxValue;
        double fastest = 0.0;
        foreach (double s in run.Speeds)
        {
            slowest = Math.Min(slowest, s / run.MeanSpeed);
            fastest = Math.Max(fastest, s / run.MeanSpeed);
        }

        AssertThat(slowest).OverrideFailureMessage($"slowest frame {slowest:F3} of mean").IsGreaterEqual(0.5);
        AssertThat(fastest).OverrideFailureMessage($"fastest frame {fastest:F3} of mean").IsLessEqual(1.5);

        // The stated pan is 7.5 m/s and playback is 1.0: the drawn mean is within a percent of it.
        AssertThat(Math.Abs(run.MeanSpeed / (MetresPerTick * Engine_TickClock.GameTicksPerSecond) - 1.0)).IsLess(0.01);
    }

    [TestCase]
    public void TestUngatedReadReproducesTheMeasuredBurstThenCrawl()
    {
        // The defect, modelled: a frame reads the next step's camera under this frame's stamp
        // whenever the step finishes between the stamp read and the camera read. Measured live:
        // one frame at 2.6–3.3x, then a tick at 0.16–0.27x (the correction floor over the pan speed).
        Run run = Simulate(gated: false, readOffsetUsec: 4_000.0, seconds: 12.0, baseUsec: 200_000_000UL);

        double slowest = double.MaxValue;
        double fastest = 0.0;
        foreach (double s in run.Speeds)
        {
            slowest = Math.Min(slowest, s / run.MeanSpeed);
            fastest = Math.Max(fastest, s / run.MeanSpeed);
        }

        AssertThat(fastest).OverrideFailureMessage($"fastest frame {fastest:F3} of mean").IsGreater(2.0);
        AssertThat(slowest).OverrideFailureMessage($"slowest frame {slowest:F3} of mean").IsLess(0.35);
    }

    [TestCase]
    public void TestDrawPointStaysBehindTheNewestSample()
    {
        Run run = Simulate(gated: true, readOffsetUsec: 4_000.0, seconds: 12.0, baseUsec: 300_000_000UL);

        // The margin is steered to the 90th percentile of measured lateness, not the peak: the
        // arrivals above it extrapolate, bounded and velocity-continuous, and they are few. The
        // draw point never crosses the newest stated tick by more than the lateness the margin
        // leaves — a small fraction of a tick — and sits behind it on nearly every frame.
        int ahead = 0;
        double furthest = 0.0;
        for (int i = 0; i < run.DrawTicks.Count; ++i)
        {
            double past = run.DrawTicks[i] - run.NewestTicks[i];
            if (past > 0.0)
            {
                ++ahead;
                furthest = Math.Max(furthest, past);
            }
        }

        AssertThat(furthest).OverrideFailureMessage($"draw point ran {furthest:F3} ticks past the newest sample").IsLess(0.1);
        AssertThat((double)ahead / run.DrawTicks.Count).OverrideFailureMessage(
            $"{ahead} of {run.DrawTicks.Count} frames drawn past the newest sample").IsLess(0.1);
        AssertThat((double)run.SwapsExtrapolating / run.Swaps).IsLess(0.2);
    }

    [TestCase]
    public void TestMarginMovesUnderThePlaybackRateBound()
    {
        Run run = Simulate(gated: true, readOffsetUsec: 4_000.0, seconds: 12.0, baseUsec: 400_000_000UL);

        // Rising at most 2% of elapsed time, falling at most 0.2%: a speed error bounded at any
        // frame rate, never a per-frame or per-swap step.
        for (int i = 0; i < run.MarginSteps.Count; ++i)
        {
            AssertThat(run.MarginSteps[i]).IsLessEqual(run.ElapsedTicks[i] * 0.02 + 1e-9);
        }
    }

    [TestCase]
    public void TestSwapDoesNotMoveTheDrawPoint()
    {
        // A late and an early swap: the draw point advances by the frame's time either side,
        // never by the swap. Anchoring re-centres by 5% of the drift, which at ±8 ms is under
        // 0.4 ms: well inside one frame's advance.
        var pair = new Engine_PairPlayhead(TickUsec, carry: true);
        double now = 50.0 * TickUsec;
        pair.Swap(now);
        uint stated = 1;
        double previous = stated - 1.0 + pair.DrawAlpha(now);
        double[] arrivals = { 74_000, 58_000, 66_667, 75_000, 60_000, 66_667, 66_667 };
        double nextArrival = now + arrivals[0];
        int at = 0;
        for (double t = now + FrameUsec; t < now + 600_000; t += FrameUsec)
        {
            pair.BeginFrame(t);
            if (t >= nextArrival)
            {
                pair.Swap(t);
                ++stated;
                ++at;
                nextArrival += arrivals[at % arrivals.Length];
            }

            double drawTick = stated - 1.0 + pair.DrawAlpha(t);
            double moved = drawTick - previous;
            double frame = FrameUsec / TickUsec;
            AssertThat(moved).OverrideFailureMessage($"draw point moved {moved:F4} ticks over a {frame:F4} tick frame").IsBetween(frame * 0.5, frame * 1.5);
            previous = drawTick;
        }
    }
}
