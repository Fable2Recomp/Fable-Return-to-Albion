using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Refii.Launcher.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// Whether a graphics change the Options panel writes waits for the next start, and the calm line the
/// panel then shows under it. No setting carries a flag for it: each one's description says so in
/// words ("Applies at the next launch.", "Takes effect at the next launch.", "Applies on next launch.",
/// "Applies at the next start.", "Takes effect the next time the game starts."), and those words are
/// the rule.
/// </summary>
public static class AndroidRestartNotes
{
    /// <summary>The line after one setting that waits for the next start.</summary>
    public const string SettingNote = "Applies the next time you start the game.";

    /// <summary>The line after a preset with a setting that waits for the next start.</summary>
    public const string PresetNote = "Some of this applies the next time you start the game.";

    // "Applies", "Takes effect" (also "Applies to models loaded after"), then within the same clause the
    // next launch or start. A passing "waits for the next launch" or "on its next start" without either verb
    // is not the setting's own rule.
    private static readonly Regex NextStart = new(
        @"\b(?:applies|apply|takes effect|take effect)\b[^.;]*?\bnext (?:launch|start|time the game starts|time you start the game)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Whether <paramref name="description"/> says its setting waits for the next start.</summary>
    public static bool DescriptionNeedsRestart(string? description) =>
        description is { Length: > 0 } && NextStart.IsMatch(description);

    /// <summary>Whether the setting <paramref name="key"/> waits for the next start, by its description in
    /// <see cref="RuntimeCVarSet.All"/>; an unknown key does not.</summary>
    public static bool NeedsRestart(string key) => NeedsRestart(key, RuntimeCVarSet.All);

    /// <summary>Whether the setting <paramref name="key"/> waits for the next start, by its description in
    /// <paramref name="definitions"/>; an unknown key does not.</summary>
    public static bool NeedsRestart(string key, IEnumerable<RuntimeCVarDefinition> definitions) =>
        definitions.Any(definition => definition.Name == key && DescriptionNeedsRestart(definition.Description));

    /// <summary>
    /// Whether writing <paramref name="writes"/> over <paramref name="current"/> changes a setting that waits
    /// for the next start. A setting written with the value it already has changes nothing and does not count;
    /// one absent from <paramref name="current"/> stands at its default.
    /// </summary>
    public static bool ChangesNeedRestart(IReadOnlyDictionary<string, string> current, IEnumerable<(string Key, string Value)> writes)
    {
        IReadOnlyList<RuntimeCVarDefinition> definitions = RuntimeCVarSet.All;
        foreach ((string key, string value) in writes)
        {
            if (!Same(ValueOf(current, key, definitions), value) && NeedsRestart(key, definitions))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether applying <paramref name="preset"/> over <paramref name="current"/> changes a setting that waits
    /// for the next start: what the preset would write (<see cref="GraphicsPresets.Writes"/>, its values and
    /// the defaults of those only the preset being left decides), against the values as they stand.
    /// </summary>
    /// <returns>False for "custom" or an unknown preset, which write nothing.</returns>
    public static bool PresetNeedsRestart(IReadOnlyDictionary<string, string> current, string preset)
    {
        var copy = new Dictionary<string, string>(current, StringComparer.Ordinal);
        return GraphicsPresets.Writes(preset, copy) is { } writes &&
            ChangesNeedRestart(current, writes.Select(write => (write.Key, write.Value)));
    }

    private static string ValueOf(IReadOnlyDictionary<string, string> current, string key, IEnumerable<RuntimeCVarDefinition> definitions) =>
        current.TryGetValue(key, out string? value)
            ? value
            : definitions.FirstOrDefault(definition => definition.Name == key)?.DefaultValue ?? "";

    /// <summary>Whether two written values are the same setting: as numbers, as switches, else as text.</summary>
    private static bool Same(string a, string b)
    {
        a = a.Trim().Trim('"').Trim();
        b = b.Trim().Trim('"').Trim();
        if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
            double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
        {
            return x == y;
        }

        if (bool.TryParse(a, out bool p) && bool.TryParse(b, out bool q))
        {
            return p == q;
        }

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
