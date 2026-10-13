using Godot;

namespace Refii.Models;

/// <summary>
/// The guest's live theme block (all 194 parameters, Engine_ClassicThemeParams names them) and
/// the sky object's sun and moon, read once a frame — the values its sky draws state, after the
/// guest's own time-of-day blending. Empty until the scene is up (and in a replayed lab frame).
/// </summary>
public static class Engine_ClassicTheme
{
    public const int Parameters = 194;
    private const int Floats = Parameters * 4 + 13;
    private static readonly float[] _values = new float[Floats];

    /// <summary>Whether this frame carried the block.</summary>
    public static bool Live { get; private set; }

    /// <summary>Main thread, once a frame.</summary>
    public static void Pump() =>
        Live = Refii.Host.HostBridge.GetClassicTheme(_values) >= Floats;

    /// <summary>Lane <paramref name="lane"/> of parameter <paramref name="index"/>.</summary>
    public static float Param(int index, int lane = 0) => _values[index * 4 + lane];

    /// <summary>The sky object's sun colour (S+0) and direction (S+16).</summary>
    public static Vector3 SunColour => new(_values[776], _values[777], _values[778]);
    public static Vector3 SunDirection => new(_values[780], _values[781], _values[782]);

    /// <summary>The moon's direction (M+0) and phase (M+20, 0..7).</summary>
    public static Vector3 MoonDirection => new(_values[784], _values[785], _values[786]);
    public static int MoonPhase => (int)_values[788];
}
