namespace Refii.Launcher.Core;

public enum RuntimeCVarType
{
    Bool,
    Int,
    Double,
    String,
}

/// <summary>One runtime setting: its key in the title's config file, type, default and description.</summary>
/// <param name="OwnerTitle">
/// THE ONE TITLE THE SETTING BELONGS TO, by its folder (<see cref="Refii.Core.Engine_Title.Folder"/>),
/// or null when it applies to every title. Recorded from evidence, never judgement: a native cvar
/// defined under <c>&lt;Folder&gt;/src</c> is that title's, one defined in the shared SDK or host
/// (<c>rexglue-net/</c>, <c>src/</c>) is every title's; a Godot setting is every title's when shared
/// code reads it, and one title's when only that title's code (<c>Godot/src/Titles/&lt;Folder&gt;</c>)
/// or one of its own systems reads it. A title's launcher screen lists and its config file holds
/// only the settings that apply to it (<see cref="AppliesTo"/>).
/// </param>
public sealed record RuntimeCVarDefinition(
    string Name,
    string Category,
    RuntimeCVarType Type,
    string DefaultValue,
    string Description,
    string? AllowedValues = null,
    string? OwnerTitle = null)
{
    /// <summary>Whether <paramref name="title"/> reads this setting: it is shared, or it is that title's.</summary>
    public bool AppliesTo(Refii.Core.Engine_Title title) =>
        OwnerTitle is null || string.Equals(OwnerTitle, title.Folder, System.StringComparison.Ordinal);
}

/// <summary>
/// One entry of a settings dropdown: the value written to the title's config file and the text shown for it.
/// </summary>
public sealed record RuntimeCVarOption(string Value, string Label);
