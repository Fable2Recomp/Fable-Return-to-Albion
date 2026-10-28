using System;
using System.Text.Json;

namespace Refii.Core;

/// <summary>Owns an opt-in material fragment-cost diagnostic; zero preserves normal rendering.</summary>
/// <remarks>Bit 1 bypasses terrain fragment work; bit 2 bypasses standard-model work after alpha coverage.
/// Bit 4 bypasses canopy fragment work after coverage; bit 8 bypasses model vertex PRT evaluation.
/// Geometry, vertex deformation, render modes and lights remain. This is a visual diagnostic,
/// never a quality setting. Reads and world reloads retain the process owner's value.</remarks>
internal sealed class Engine_ShaderCostDiagnostic
{
    /// <summary>The integer global registered before shaders compile, with a zero boot default.</summary>
    internal const string Global = "fable_shader_cost_diagnostic";

    /// <summary>The last successfully published diagnostic mask, from zero through fifteen.</summary>
    internal int Mask { get; private set; }

    /// <summary>Validates an optional exact integer mask and publishes it before changing reported state.</summary>
    /// <param name="request">A render-channel request; unrelated keys remain with their owners.</param>
    /// <param name="publish">Godot-thread global publisher, injected for headless lifecycle testing.</param>
    /// <exception cref="JsonException">The supplied mask is not an integer from zero through fifteen.</exception>
    internal void ApplyRequest(JsonElement request, Action<string, int> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        if (!request.TryGetProperty("shaderCostDiagnostic", out JsonElement value)) return;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int mask) || mask < 0 || mask > 15)
            throw new JsonException("shaderCostDiagnostic must be an integer from 0 through 15.");
        publish(Global, mask);
        Mask = mask;
    }
}
