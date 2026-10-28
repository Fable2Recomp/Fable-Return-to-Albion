using System;
using System.Text.Json;

namespace Refii.Models;

/// <summary>Preserves authored model LOD distances across physical viewport sizes.</summary>
/// <remarks>Godot 4.7.1 normalizes the pixel threshold by the RenderingServer viewport width,
/// not the canvas override or scaled 3D buffer. Its projection multiplier also divides by the
/// project-wide rendering/scaling_3d/scale. The matching threshold includes that project scale;
/// changing only a viewport's world-buffer percentage does not change this authored-distance rule.
/// No visibility range, mesh, terrain index or draw distance is changed by this policy.</remarks>
internal sealed class Engine_MeshLodPolicy
{
    private (float Pixels, float Width, float Height, float ProjectScale)? _inputs;

    /// <summary>The last successfully published configured threshold, before optional live overrides.</summary>
    internal float? ParityThreshold { get; private set; }

    /// <summary>The shared configuration's last successfully applied mesh_lod_pixels value.</summary>
    internal float? ConfiguredPixels => _inputs?.Pixels;

    /// <summary>Computes the desktop's 720-line rule with Godot's global scale compensation.</summary>
    /// <param name="pixels">The configured detail multiplier; one matches the authored default, zero draws full detail.</param>
    /// <param name="physicalHeight">Actual RenderingServer viewport height, before 3D resolution scaling.</param>
    /// <param name="projectScale">The project-wide rendering/scaling_3d/scale used by Godot's LOD multiplier.</param>
    /// <returns>The root-world viewport threshold, without changing any visibility range.</returns>
    internal static float Calculate(float pixels, float physicalHeight, float projectScale)
    {
        if (!float.IsFinite(pixels) || pixels < 0f || pixels > 64f)
            throw new ArgumentOutOfRangeException(nameof(pixels));
        RequirePositive(physicalHeight, nameof(physicalHeight));
        RequirePositive(projectScale, nameof(projectScale));
        float threshold = pixels * (physicalHeight / Engine_MdlLods.ReferenceHeight) * projectScale;
        if (!float.IsFinite(threshold)) throw new ArgumentOutOfRangeException(nameof(physicalHeight));
        return threshold;
    }

    /// <summary>Publishes only when configuration, actual viewport extent or global scale changes.</summary>
    /// <param name="pixels">The parsed shared mesh_lod_pixels setting.</param>
    /// <param name="physicalWidth">Actual RenderingServer viewport width; a resize retires any live override.</param>
    /// <param name="physicalHeight">Actual RenderingServer viewport height, not the logical canvas height.</param>
    /// <param name="projectScale">The project-wide scale, independent of the live viewport buffer scale.</param>
    /// <param name="publish">Main-thread root-world threshold setter.</param>
    /// <returns>True only after a changed input set was successfully published.</returns>
    /// <remarks>Unchanged status ticks deliberately leave a profiler override alone. A real resize
    /// or setting change reapplies configured quality. Failed setters retain the previous snapshot
    /// so the same change can be retried without claiming success.</remarks>
    internal bool Apply(float pixels, float physicalWidth, float physicalHeight, float projectScale,
        Action<float> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        RequirePositive(physicalWidth, nameof(physicalWidth));
        float threshold = Calculate(pixels, physicalHeight, projectScale);
        var inputs = (pixels, physicalWidth, physicalHeight, projectScale);
        if (_inputs == inputs) return false;
        publish(threshold);
        _inputs = inputs;
        ParityThreshold = threshold;
        return true;
    }

    /// <summary>Normalizes a viewport threshold using the renderer's actual physical width.</summary>
    /// <param name="threshold">The currently applied finite, nonnegative pixel threshold.</param>
    /// <param name="physicalWidth">Actual RenderingServer viewport width before world-buffer scaling.</param>
    /// <returns>The ratio passed to Godot's mesh LOD selection.</returns>
    internal static float NormalizedThreshold(float threshold, float physicalWidth)
    {
        if (!float.IsFinite(threshold) || threshold < 0f)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        RequirePositive(physicalWidth, nameof(physicalWidth));
        return threshold / physicalWidth;
    }

    /// <summary>Reproduces the renderer's projection multiplier for perspective LOD selection.</summary>
    /// <param name="projectionX">The camera projection matrix's positive X scale.</param>
    /// <param name="projectScale">The global project scale read by the pinned renderer.</param>
    /// <returns>The multiplier applied to camera-to-box distance.</returns>
    internal static float DistanceMultiplier(float projectionX, float projectScale)
    {
        RequirePositive(projectionX, nameof(projectionX));
        RequirePositive(projectScale, nameof(projectScale));
        return (2f / projectionX) / projectScale;
    }

    /// <summary>Reads one optional bounded numeric profiler threshold without coercing other JSON types.</summary>
    /// <param name="request">The render request, whose unrelated fields remain owned by their handlers.</param>
    /// <returns>Null when absent; a finite value in zero through 256 otherwise.</returns>
    /// <exception cref="JsonException">A supplied threshold is malformed, nonfinite or outside the diagnostic bound.</exception>
    internal static float? ReadProfilerThreshold(JsonElement request)
    {
        if (!request.TryGetProperty("meshLodThreshold", out JsonElement value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float threshold) ||
            !float.IsFinite(threshold) || threshold < 0f || threshold > 256f)
            throw new JsonException("meshLodThreshold must be a finite number from 0 through 256; zero draws full detail.");
        return threshold;
    }

    /// <summary>Rejects invalid extents and scales before any engine setter can be invoked.</summary>
    private static void RequirePositive(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0f) throw new ArgumentOutOfRangeException(name);
    }
}
