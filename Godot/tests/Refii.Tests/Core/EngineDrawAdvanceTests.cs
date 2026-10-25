namespace Refii.Tests.Core;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;

/// <summary>
/// The stated-pause latch against the draw advance: nothing advances while the guest states a
/// pause, the pace is untouched by the hold, and at full pace the world delta is the frame's own.
/// </summary>
[TestSuite]
public class EngineDrawAdvanceTests
{
    private const double FrameSeconds = 1.0 / 120.0;
    private const ulong FrameUsec = 8_333UL;
    private const double TicksPerFrame = FrameSeconds * Engine_TickClock.GameTicksPerSecond;

    /// <summary>Runs frames of a healthy guest: the draw point moves one frame's worth of ticks per frame.</summary>
    private static (ulong now, double drawTick) RunSteady(int frames, ulong now, double drawTick)
    {
        for (int i = 0; i < frames; i++)
        {
            now += FrameUsec;
            drawTick += TicksPerFrame;
            Engine_DrawAdvance.Ticks(FrameSeconds, now, drawTick);
        }

        return (now, drawTick);
    }

    private static bool Near(double a, double b, double tolerance) => System.Math.Abs(a - b) <= tolerance;

    [TestCase]
    public void TicksAreZeroWhileTheGuestStatesAPause()
    {
        Engine_StatedPause.BeginFrame(false);
        (ulong now, double drawTick) = RunSteady(120, 1_000_000UL, 100.0);
        AssertThat(Engine_DrawAdvance.Ticks(FrameSeconds, now + FrameUsec, drawTick + TicksPerFrame) > 0.0).IsTrue();

        Engine_StatedPause.BeginFrame(true);
        AssertThat(Engine_StatedPause.Latched).IsTrue();
        AssertThat(Engine_StatedPause.HeldFrames).IsEqual(1L);
        AssertThat(Engine_DrawAdvance.Ticks(FrameSeconds, now + 2 * FrameUsec, drawTick + TicksPerFrame)).IsEqual(0.0);
        AssertThat(Engine_DrawAdvance.Seconds(FrameSeconds)).IsEqual(0.0);

        Engine_StatedPause.BeginFrame(true);
        AssertThat(Engine_StatedPause.HeldFrames).IsEqual(2L);

        Engine_StatedPause.BeginFrame(false);
        AssertThat(Engine_StatedPause.Latched).IsFalse();
        AssertThat(Engine_StatedPause.HeldFrames).IsEqual(0L);
    }

    [TestCase]
    public void SecondsIsTheFrameAtTheGuestsPaceWhenNotPaused()
    {
        Engine_StatedPause.BeginFrame(false);
        RunSteady(120, 1_000_000UL, 100.0);
        AssertThat(Engine_StatedPause.Latched).IsFalse();
        AssertThat(Engine_DrawAdvance.Seconds(FrameSeconds)).IsEqual(FrameSeconds * Engine_DrawAdvance.Pace);
        AssertThat(Near(Engine_DrawAdvance.Pace, 1.0, 1e-3)).IsTrue();
        AssertThat(Near(Engine_DrawAdvance.Seconds(FrameSeconds), FrameSeconds, 1e-5)).IsTrue();
    }

    [TestCase]
    public void TheHoldDoesNotMoveThePaceAndReleaseSeesNoJump()
    {
        Engine_StatedPause.BeginFrame(false);
        (ulong now, double drawTick) = RunSteady(240, 1_000_000UL, 100.0);
        double before = Engine_DrawAdvance.Pace;
        AssertThat(Near(before, 1.0, 1e-3)).IsTrue();

        // Two seconds held with the draw point standing still: an ordinary pause.
        for (int i = 0; i < 240; i++)
        {
            Engine_StatedPause.BeginFrame(true);
            now += FrameUsec;
            AssertThat(Engine_DrawAdvance.Ticks(FrameSeconds, now, drawTick)).IsEqual(0.0);
        }

        AssertThat(Engine_DrawAdvance.Pace).IsEqual(before);

        // Released: the first frames measure against a fresh seed, not the held draw point.
        Engine_StatedPause.BeginFrame(false);
        RunSteady(12, now, drawTick);
        AssertThat(Near(Engine_DrawAdvance.Pace, 1.0, 1e-3)).IsTrue();
    }
}
