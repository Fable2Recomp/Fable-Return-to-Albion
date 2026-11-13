namespace Refii.AndroidRuntime;

/// <summary>The line the launcher shows under a phone setting's row for its current choice
/// (SettingsPanel.SettingNotes.cs). Only on a phone, and only for a setting whose choices differ in a way the
/// player has to know before picking one.</summary>
public static class AndroidSettingNotes
{
    /// <summary>The note for <paramref name="key"/> at <paramref name="value"/>, or null for none.</summary>
    /// <param name="key">The setting's refii.toml key.</param>
    /// <param name="value">Its current value.</param>
    public static string? For(string key, string value)
    {
        if (!Refii.Launcher.Core.GraphicsPresets.Offered) return null;
        return key == AndroidBakedShadows.Key ? AndroidBakedShadows.Note(AndroidBakedShadows.Parse(value)) : null;
    }
}
