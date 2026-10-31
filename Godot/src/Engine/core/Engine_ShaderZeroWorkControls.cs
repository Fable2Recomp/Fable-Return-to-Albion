using System;
using System.Text.Json;

namespace Refii.Core;

/// <summary>Owns typed switches for shader work elimination and precomputed near-fog endpoints.</summary>
/// <remarks>Project shader globals start true before materials compile. These host values persist
/// across level reloads; a new process starts with a new owner and the same registered defaults.
/// The render channel publishes on the Godot thread without synchronous GPU-global readback.</remarks>
internal sealed class Engine_ShaderZeroWorkControls
{
    /// <summary>The project's registered boolean global for the exact-zero specular path.</summary>
    internal const string SpecularGlobal = "fable_zero_specular_work";

    /// <summary>The project's registered boolean global for the exact-zero cliff path.</summary>
    internal const string CliffGlobal = "fable_zero_cliff_work";

    /// <summary>The project's boolean global selecting precomputed near-fog endpoints when ready.</summary>
    internal const string NearFogGlobal = "fable_near_fog_precompute";

    /// <summary>The boolean global selecting per-instance terrain layer-mask work elimination.</summary>
    internal const string SparseTerrainGlobal = "fable_sparse_terrain_work";

    /// <summary>Whether nonspecular materials skip their zero-color Phong calculation.</summary>
    internal bool Specular { get; private set; } = true;

    /// <summary>Whether zero cliff weight skips cliff sampling; explicit gradients apply in both modes.</summary>
    internal bool Cliff { get; private set; } = true;

    /// <summary>Whether valid atmosphere snapshots replace the near-fog power/exponential work.</summary>
    internal bool NearFog { get; private set; } = true;

    /// <summary>Whether terrain chunks skip layers whose complete chunk mask proves no contribution.</summary>
    internal bool SparseTerrain { get; private set; } = true;

    /// <summary>Validates and publishes at most one optional, strictly boolean render control.</summary>
    /// <param name="request">The render request; unrelated render fields are left to their owners.</param>
    /// <param name="publish">Main-thread global publisher; state changes only after it succeeds.</param>
    /// <exception cref="JsonException">A supplied control is not a boolean, or multiple controls were supplied.</exception>
    internal void ApplyRequest(JsonElement request, Action<string, bool> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        bool? specular = ReadBoolean(request, "shaderZeroSpecular");
        bool? cliff = ReadBoolean(request, "shaderZeroCliff");
        bool? nearFog = ReadBoolean(request, "shaderNearFog");
        bool? sparseTerrain = ReadBoolean(request, "shaderSparseTerrain");
        if ((specular.HasValue ? 1 : 0) + (cliff.HasValue ? 1 : 0) + (nearFog.HasValue ? 1 : 0) + (sparseTerrain.HasValue ? 1 : 0) > 1)
            throw new JsonException("Change one shader control per request.");
        if (specular is bool specularValue)
        {
            publish(SpecularGlobal, specularValue);
            Specular = specularValue;
        }
        else if (cliff is bool cliffValue)
        {
            publish(CliffGlobal, cliffValue);
            Cliff = cliffValue;
        }
        else if (nearFog is bool nearFogValue)
        {
            publish(NearFogGlobal, nearFogValue);
            NearFog = nearFogValue;
        }
        else if (sparseTerrain is bool sparseTerrainValue)
        {
            publish(SparseTerrainGlobal, sparseTerrainValue);
            SparseTerrain = sparseTerrainValue;
        }
    }

    /// <summary>Reads one optional boolean without silently accepting null, numbers or strings.</summary>
    private static bool? ReadBoolean(JsonElement request, string name)
    {
        if (!request.TryGetProperty(name, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new JsonException(name + " must be a boolean."),
        };
    }
}
