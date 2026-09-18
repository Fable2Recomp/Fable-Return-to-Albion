namespace Refii.Launcher.Core;

public enum RuntimeCVarType
{
    Bool,
    Int,
    Double,
    String,
}

public sealed record RuntimeCVarDefinition(
    string Name,
    string Category,
    RuntimeCVarType Type,
    string DefaultValue,
    string Description,
    string? AllowedValues = null);

/// <summary>
/// One entry of a settings dropdown: the value written to refii.toml and the text shown for it.
/// </summary>
public sealed record RuntimeCVarOption(string Value, string Label);
