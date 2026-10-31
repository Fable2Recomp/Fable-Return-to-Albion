using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace Refii.Core;

/// <summary>Separates diagnostic goals, Android pacing and reversible post-processing selection from guest timing.</summary>
internal static class Engine_PresentationPolicy
{
    /// <summary>Returns the performance goal; it never limits presentation or simulation.</summary>
    internal static int TargetFps(bool android) => android ? 30 : 120;

    /// <summary>Returns the diagnostic frame-time budget for the selected platform.</summary>
    internal static double BudgetMs(bool android) => 1000d / TargetFps(android);

    /// <summary>Formats the goal without implying a resolution or an FPS cap.</summary>
    internal static string BudgetLabel(bool android) =>
        FormattableString.Invariant($"target {TargetFps(android)} fps / {BudgetMs(android):0.00} ms");

    /// <summary>Selects the full chain by default or the original prototype's off mode for a startup comparison.</summary>
    /// <param name="values">Private runtime values; no settings are written.</param>
    /// <returns>The validated on/off mode; absent or malformed values preserve full post-processing.</returns>
    internal static string PostFxMode(IReadOnlyDictionary<string, string> values) =>
        Engine_RefiiConfig.Bool(values, "android_postfx_enabled", true) ? "on" : "off";

    /// <summary>Validates a reversible live compositor bypass before any debug state changes.</summary>
    /// <param name="request">The render request; an absent key is read-only.</param>
    /// <returns>The selected enable flag or null when no change was requested.</returns>
    internal static bool? ReadPostFxRequest(JsonElement request)
    {
        if (!request.TryGetProperty("postFxEnabled", out JsonElement value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("postFxEnabled must be a boolean.");
        return value.GetBoolean();
    }

    /// <summary>Applies Android's presentation on each startup, independently of the guest clock.</summary>
    /// <remarks>The `max_fps` setting is the only limit: 0 leaves drawing uncapped. A phone that
    /// cannot hold its rate unevenly alternates fast and slow frames and heats faster, which
    /// lowers its clocks further; a phone starts with a 30 FPS cap
    /// (<c>GraphicsPresets.AndroidFrameCap</c>), set beside the preset rather than in it.</remarks>
    /// <param name="maxFps">The configured cap; zero or negative is uncapped.</param>
    /// <param name="setMaxFps">Main-thread engine FPS setter.</param>
    /// <param name="setVsync">Main-thread window VSync setter.</param>
    internal static void ApplyAndroid(int maxFps, Action<int> setMaxFps, Action<DisplayServer.VSyncMode> setVsync)
    {
        ArgumentNullException.ThrowIfNull(setMaxFps);
        ArgumentNullException.ThrowIfNull(setVsync);
        setMaxFps(Math.Max(0, maxFps));
        setVsync(DisplayServer.VSyncMode.Disabled);
    }
}
