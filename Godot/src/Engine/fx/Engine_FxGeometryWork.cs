using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using Godot;

namespace Refii.Fx;

/// <summary>Reads retained particle tuples directly while accumulating surface bounds.</summary>
/// <remarks>Main-thread owned; callers must not mutate a list while its span is in use.
/// The original bounds arithmetic and corner-offset reach remain unchanged.</remarks>
internal static class Engine_FxGeometryWork
{
    /// <summary>Reversible profiler lever; false retains the original list-based loops.</summary>
    internal static bool Enabled = true;
    /// <summary>Validates the optional diagnostic before any rendering state changes.</summary>
    /// <param name="request">A render request; absence selects read-only inspection.</param>
    /// <returns>The requested flag or null without changing process state.</returns>
    internal static bool? ReadRequest(JsonElement request)
    {
        if (!request.TryGetProperty("fxBulkGeometry", out JsonElement value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("fxBulkGeometry must be a boolean.");
        return value.GetBoolean();
    }

    /// <summary>Expands the existing bounds with the same positions and corner-offset lengths.</summary>
    /// <param name="vertices">The retained, unmodified surface batch.</param>
    /// <param name="bounds">Bounds accumulated across surfaces this frame.</param>
    /// <param name="hasBounds">Whether a previous vertex seeded the accumulated bounds.</param>
    /// <param name="maxOffset">Largest corner-offset length across surfaces this frame.</param>
    /// <returns>False leaves bounds untouched for the original loop.</returns>
    internal static bool TryBounds(List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> vertices,
        ref Aabb bounds, ref bool hasBounds, ref float maxOffset)
    {
        if (!Enabled) return false;
        ReadOnlySpan<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> source = CollectionsMarshal.AsSpan(vertices);
        for (int i = 0; i < source.Length; i++)
        {
            ref readonly var value = ref source[i];
            if (!hasBounds) { bounds = new Aabb(value.Pos, Vector3.Zero); hasBounds = true; }
            else bounds = bounds.Expand(value.Pos);
            float reach = value.Off.Length();
            if (reach > maxOffset) maxOffset = reach;
        }
        return true;
    }
}
