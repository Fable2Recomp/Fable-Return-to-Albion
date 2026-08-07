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
