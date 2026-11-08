using System;
using System.Collections.Generic;

namespace Refii.AndroidRuntime;

/// <summary>The host's own menu labels that speak of a desktop, as a phone says them.</summary>
/// <remarks>These rows are literal host labels with no string in the game's table: the front end's
/// way out and the pause menu's quit choices. On a phone there is no desktop to quit to; the rows
/// close the app, so they say so. Each title states its own (<see cref="Refii.Core.Engine_Title.PhoneHostLabels"/>).</remarks>
public static class AndroidMenuWords
{
    /// <summary>Fable 2's host rows (Fable2UiTemplates: the front end's and the pause menu's quit rows).</summary>
    public static readonly IReadOnlyDictionary<string, string> Fable2 = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Quit to Desktop"] = "Quit Game",
        ["Save and Quit to Desktop"] = "Save and Quit Game",
        ["Quit to Desktop and Don't Save"] = "Quit Game and Don't Save",
    };

    /// <summary>A title with no such rows.</summary>
    public static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    /// <summary>The label a phone shows for the unresolved host label <paramref name="tag"/>.</summary>
    public static string ForPhone(string tag) =>
        ForPhone(tag, Refii.Core.Engine_Platform.IsAndroid, Refii.Core.Engine_Title.Active);

    /// <summary>The same, with the platform and title given (the tests).</summary>
    internal static string ForPhone(string tag, bool phone, Refii.Core.Engine_Title title) =>
        phone && title.PhoneHostLabels.TryGetValue(tag, out string? said) ? said : tag;
}
