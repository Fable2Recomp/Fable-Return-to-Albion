using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Refii.Launcher.Core;

namespace Refii.AndroidRuntime;

/// <summary>Maps real Android worker stages into the original launcher's button and current-stage progress.</summary>
public static class AndroidLauncherProgress
{
    /// <summary>Builds a playable, working or install action without consulting desktop executable paths.</summary>
    /// <param name="running">Whether an Android worker owns installation.</param>
    /// <param name="playable">Whether Android has a completed module or active runtime.</param>
    /// <param name="status">Actual worker stage or failure.</param>
    /// <returns>The original launcher's primary button label.</returns>
    public static string Label(bool running, bool playable, string status) => running
        ? ProgressLabel.BuildButtonLabel("Installing", status) : playable ? "Play Fable 2" : "Install Fable 2";

    /// <summary>Returns measured current-stage progress; unknown-duration stages remain indeterminate.</summary>
    /// <param name="status">Real copy/extraction percentage or source-unit count.</param>
    /// <returns>A bounded stage fraction, or zero when the stage has no measurable fraction.</returns>
    public static float Fraction(string status)
    {
        var units = Regex.Match(status, @"^Compiling game code (\d+)/(\d+)$", RegexOptions.CultureInvariant);
        if (units.Success && int.TryParse(units.Groups[1].Value, out int current) && int.TryParse(units.Groups[2].Value, out int total) && total > 0)
            return Math.Clamp((current - 1f) / total, 0, 1);
        var percent = Regex.Match(status, @"^(?:Copying selected file|Extracting game files): (\d+)%", RegexOptions.CultureInvariant);
        return percent.Success && float.TryParse(percent.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out float value)
            ? Math.Clamp(value / 100, 0, 1) : 0;
    }
}
