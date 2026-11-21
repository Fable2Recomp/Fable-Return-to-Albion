using Godot;

namespace Refii.Level;

/// <summary>
/// The scene-wide atmosphere the guest states in its frame block (RefiiHostGetClassicFrame):
/// the global mist strength and the wind, read once a frame for whichever renderer is up.
/// </summary>
/// <remarks>
/// The classic feed reads the whole block for its registers and hands it over
/// (<see cref="Take"/>); the modern renderer, which has no feed, asks <see cref="Read"/>.
/// </remarks>
public static class Engine_GuestAtmosphere
{
    private const int Floats = 148;
    /// <summary>The theme block's lightmap colour lane, zero until the scene is up.</summary>
    private const int SceneLane = 36 + 3;
    private const int MistStrengthLane = 143;
    private const int WindLane = 144;

    private static readonly float[] _frame = new float[Floats];
    private static ulong _readFrame = ulong.MaxValue;

    /// <summary>Whether the guest's scene is up (its theme block carries a lightmap colour).</summary>
    public static bool SceneUp { get; private set; }

    /// <summary>The scene's global mist strength (O+3040.x); 1 when the scene is not up.</summary>
    public static float MistStrength { get; private set; } = 1f;

    /// <summary>The scene's wind: unit direction xyz and speed w (O+0xD38..0xD44), guest axes.</summary>
    public static Vector4 Wind { get; private set; }

    /// <summary>The scene's wind speed as the trees take it: clamp(W+0xD44, 0, 10), 0 when the scene is not up.</summary>
    public static float TreeSpeed { get; private set; }

    /// <summary>The clock the trees run on (the guest's seconds) and R+0x254, every tree's integrated phase.</summary>
    public static float TreeClock { get; private set; }

    public static float TreePhase { get; private set; }

    private static float _phaseClock = float.NaN;
    private static double _smoothedGuestSeconds = double.NaN;

    /// <summary>
    /// Advances a smoothed display-rate clock locked to <paramref name="rawGuestSeconds"/> so swaying
    /// trees and foliage shadows glide at the display refresh rate instead of stepping on 15 Hz guest ticks.
    /// Pure helper safe for headless unit tests.
    /// </summary>
    public static double StepInterpolatedSeconds(ref double smoothed, double rawGuestSeconds, float worldDelta)
    {
        if (!double.IsFinite(rawGuestSeconds))
        {
            return smoothed;
        }

        if (!double.IsFinite(smoothed) || System.Math.Abs(rawGuestSeconds - smoothed) > 0.5)
        {
            smoothed = rawGuestSeconds;
            return smoothed;
        }

        if (worldDelta <= 0f)
        {
            return smoothed;
        }

        double predicted = smoothed + worldDelta;
        double err = rawGuestSeconds - predicted;
        double steer = System.Math.Clamp(
            err * System.Math.Min(1.0, worldDelta * 6.0),
            -worldDelta * 0.35,
            worldDelta * 0.50);
        smoothed = predicted + steer;
        return smoothed;
    }

    /// <summary>
    /// Steps the trees' phase to <paramref name="time"/>: R+0x254 += clamp(speed, 0, 10) * dt from a
    /// zeroed field (`sub_822DAB20`), so the first step spans the whole clock. Main thread, once a frame.
    /// </summary>
    public static void StepTrees(float time)
    {
        if (float.IsNaN(_phaseClock) || time < _phaseClock)
        {
            TreePhase = 0f;
            _phaseClock = 0f;
        }
        TreePhase += TreeSpeed * (time - _phaseClock);
        _phaseClock = time;
        TreeClock = time;
    }

    /// <summary>The guest's seconds, or the host's where no guest frame has stamped one.</summary>
    public static float Seconds()
    {
        double seconds = Refii.Entities.Engine_DrawnFrame.GuestSeconds;
        return double.IsFinite(seconds) ? (float)seconds : (float)(Time.GetTicksMsec() / 1000.0);
    }

    /// <summary>
    /// Display-rate interpolated guest seconds advanced by <paramref name="worldDelta"/> between guest ticks.
    /// </summary>
    public static float InterpolatedSeconds(float worldDelta)
    {
        double raw = Refii.Entities.Engine_DrawnFrame.GuestSeconds;
        if (!double.IsFinite(raw))
        {
            _smoothedGuestSeconds = double.NaN;
            return (float)(Time.GetTicksMsec() / 1000.0);
        }

        return (float)StepInterpolatedSeconds(ref _smoothedGuestSeconds, raw, worldDelta);
    }

    /// <summary>Takes a frame block already read (the classic feed's). Main thread.</summary>
    public static void Take(float[] frame)
    {
        _readFrame = Engine.GetProcessFrames();
        bool scene = frame[SceneLane] != 0f;
        SceneUp = scene;
        MistStrength = scene ? frame[MistStrengthLane] : 1f;
        TreeSpeed = scene ? Mathf.Clamp(frame[WindLane + 3], 0f, 10f) : 0f;
        Wind = new Vector4(frame[WindLane], frame[WindLane + 1], frame[WindLane + 2], frame[WindLane + 3]);
    }

    /// <summary>Reads the block once this frame unless a feed already handed it over. Main thread.</summary>
    public static void Read()
    {
        if (_readFrame == Engine.GetProcessFrames())
        {
            return;
        }
        if (Refii.Host.HostBridge.GetClassicFrame(_frame) < Floats)
        {
            _readFrame = Engine.GetProcessFrames();
            SceneUp = false;
            MistStrength = 1f;
            Wind = Vector4.Zero;
            TreeSpeed = 0f;
            return;
        }
        Take(_frame);
    }
}
