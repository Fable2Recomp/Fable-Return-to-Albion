using System;
using Godot;

namespace Refii.Models;

/// <summary>
/// The scene's wind for the modern surfaces, as the repeated family's WIND1 vertex shader takes
/// it: <c>fable_wind_direction</c> = the frame's scene+3384 direction (g_WindDirections row 0)
/// and <c>fable_wind_factors</c> = g_WindFactors.xy, built exactly as
/// <see cref="Engine_ClassicWind"/> builds them — s = scene wind speed (scene+3396) x the FIRST
/// swaying mesh's amplitude, taken once (`sub_82224578`'s function-local static), then
/// (100 s / |400 dir|, 100 s / |(-35, 153)|).
/// </summary>
/// <remarks>
/// Where no guest runs (the editor, the level viewer) the frame is absent and the wind is zero,
/// as the classic editor frame states it.
/// </remarks>
public static class Engine_SceneWind
{
    private static readonly float Row1 = new Vector2(-35f, 153f).Length();
    private static float? _amplitude;
    private static float? _scale;
    private static Vector3 _direction = new(float.NaN, 0f, 0f);
    private static Vector2 _factors;

    /// <summary>A swaying mesh's amplitude (.mdl +80); the first one fixes the scene's scale.</summary>
    public static void NoteAmplitude(float amplitude)
    {
        _amplitude ??= amplitude;
    }

    /// <summary>Reads the frame and publishes the globals. Main thread, once a frame.</summary>
    public static void Pump() => Pump(0f);

    /// <summary>Reads the frame and publishes the wind and display-interpolated tree sway globals. Main thread, once a frame.</summary>
    public static void Pump(float worldDelta)
    {
        Refii.Level.Engine_GuestAtmosphere.Read();
        Vector3 direction = Vector3.Zero;
        Vector2 factors = Vector2.Zero;
        Vector4 wind = Refii.Level.Engine_GuestAtmosphere.Wind;
        if (Refii.Level.Engine_GuestAtmosphere.SceneUp)
        {
            direction = new Vector3(wind.X, wind.Y, wind.Z);
            Refii.Level.Engine_Weather.WindWorld = Engine_GmdSkeleton.ZUpToYUp * (direction * wind.W);
            if (_scale is null && _amplitude is { } amplitude)
            {
                _scale = wind.W * amplitude;
            }
            float s = _scale ?? 0f;
            factors = new Vector2(
                100f * s / ((direction * 400f).Length() + 1e-10f),
                100f * s / (Row1 + 1e-10f));
        }

        // The trees' wind and interpolated clock (fable_tree_sway.gdshaderinc), every display frame.
        float treeTime = worldDelta > 0f
            ? Refii.Level.Engine_GuestAtmosphere.InterpolatedSeconds(worldDelta)
            : Refii.Level.Engine_GuestAtmosphere.Seconds();
        Refii.Level.Engine_GuestAtmosphere.StepTrees(treeTime);
        RenderingServer.GlobalShaderParameterSet(TreeWindName,
            new Vector4(direction.X, direction.Y, direction.Z, Refii.Level.Engine_GuestAtmosphere.TreeSpeed));
        RenderingServer.GlobalShaderParameterSet(TreeClockName,
            new Vector2(Refii.Level.Engine_GuestAtmosphere.TreeClock, Refii.Level.Engine_GuestAtmosphere.TreePhase));

        if (direction == _direction && factors == _factors)
        {
            return;
        }
        _direction = direction;
        _factors = factors;
        RenderingServer.GlobalShaderParameterSet(WindDirectionName, direction);
        RenderingServer.GlobalShaderParameterSet(WindFactorsName, factors);
    }

    private static readonly StringName WindDirectionName = "fable_wind_direction";
    private static readonly StringName WindFactorsName = "fable_wind_factors";
    private static readonly StringName TreeWindName = "fable_tree_wind";
    private static readonly StringName TreeClockName = "fable_tree_clock";
}
