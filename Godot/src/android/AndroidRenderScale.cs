using Godot;
using Refii.Game;
using System;
using System.IO;

namespace Refii.AndroidRuntime;

/// <summary>Applies changed world-buffer scales without altering canvas layout or separate UI viewports.</summary>
internal sealed class AndroidRenderScale
{
    private float? _applied;

    /// <summary>The last successfully applied scale; the unchanged engine default is one.</summary>
    internal float Applied => _applied ?? 1f;

    /// <summary>Reads the known private configuration without silently accepting a missing live file.</summary>
    /// <remarks>Absent percentages use Android's half-resolution performance baseline. The shared
    /// desktop defaults and explicit percentages retain their existing validation and bounds;
    /// this supplies a parsed default in memory without rewriting the player's configuration.</remarks>
    /// <param name="path">The already initialized runtime configuration path.</param>
    /// <returns>The existing shared configuration policy's parsed settings.</returns>
    /// <exception cref="FileNotFoundException">The live configuration is unavailable.</exception>
    internal static GameWindowSettings ReadSettings(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Live render-scale configuration is unavailable", path);
        return SettingsOf(Refii.Launcher.Core.RuntimeConfig.ReadValues(path), path);
    }

    /// <summary><see cref="ReadSettings"/> for values already read: <paramref name="values"/> is left as it is, for its other readers.</summary>
    internal static GameWindowSettings SettingsOf(System.Collections.Generic.SortedDictionary<string, string> values, string path)
    {
        if (!values.ContainsKey("render_scale_percent"))
        {
            values = new System.Collections.Generic.SortedDictionary<string, string>(values, values.Comparer);
            values.Add("render_scale_percent", "50");
        }
        return GameWindowSettings.FromValues(values, path);
    }

    /// <summary>Invokes the root-viewport setter once per effective percentage change.</summary>
    /// <param name="settings">Settings parsed by the shared desktop runtime configuration policy.</param>
    /// <param name="apply">The host's root-world setter; no UI viewport is passed to this policy.</param>
    /// <param name="adjust">The scale against the root's own target from the scale against the screen
    /// (<see cref="AndroidCanvasResolution.WorldScale"/>); null keeps the configured one.</param>
    /// <returns>True after a changed value was successfully applied.</returns>
    internal bool Apply(GameWindowSettings settings, Action<float> apply, Func<float, float>? adjust = null)
    {
        // Mobile has no FSR1/2 implementation. Read the explicit percentage instead
        // of letting an unrelated desktop FSR quality preset silently replace it.
        float scale = settings.RenderScalePercent / 100f;
        if (adjust is not null) scale = adjust(scale);
        if (_applied == scale) return false;
        apply(scale);
        _applied = scale;
        return true;
    }

    /// <summary>Estimates the world-buffer dimensions without querying or reading back the GPU.</summary>
    /// <param name="logicalSize">The root viewport's visible canvas size.</param>
    /// <param name="stretchScale">Its canvas stretch transform scale.</param>
    /// <param name="worldScale">The root viewport's applied 3D buffer scale.</param>
    /// <returns>Expected floating-point dimensions before renderer rounding or alignment.</returns>
    internal static Vector2 ExpectedSize(Vector2 logicalSize, Vector2 stretchScale, float worldScale) =>
        logicalSize * stretchScale * worldScale;
}
