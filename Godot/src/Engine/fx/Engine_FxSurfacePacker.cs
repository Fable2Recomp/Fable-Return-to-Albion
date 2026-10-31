using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using Godot;

namespace Refii.Fx;

/// <summary>Packs the existing effect surface bytes through typed spans instead of per-field byte slices.</summary>
/// <remarks>The caller retains its original fallback for unrecognized layouts. Simulation,
/// vertex order, padding, colour truncation and upload lengths remain identical.</remarks>
internal static class Engine_FxSurfacePacker
{
    /// <summary>Reversible profiler lever; the original writer remains available for same-process comparisons.</summary>
    internal static bool Enabled = true;

    /// <summary>Validates the optional packing lever before the debug endpoint mutates rendering state.</summary>
    internal static bool? ReadRequest(JsonElement request)
    {
        if (!request.TryGetProperty("fxPackedWrites", out JsonElement value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("fxPackedWrites must be a boolean.");
        return value.GetBoolean();
    }

    /// <summary>Matches Godot's packed RGBA8 followed by two uncompressed Vector2 attributes and one Vector4 motion attribute.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByteAttributes
    {
        internal uint Colour;
        internal Vector2 Uv;
        internal Vector2 Offset;
        internal Vector4 Motion;
    }

    /// <summary>Matches Godot's floating colour followed by two uncompressed Vector2 attributes and one Vector4 motion attribute.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct FloatAttributes
    {
        internal Color Colour;
        internal Vector2 Uv;
        internal Vector2 Offset;
        internal Vector4 Motion;
    }

    /// <summary>Packs recognized little-endian layouts; false leaves both buffers unchanged for the original writer.</summary>
    /// <param name="vertices">Exact position buffer for the retained surface capacity.</param>
    /// <param name="attributes">Exact attribute buffer including upstream motion vectors.</param>
    /// <param name="list">Unmodified live particle vertices.</param>
    /// <param name="capacity">Surface capacity including degenerate tail vertices.</param>
    /// <param name="stride">Byte distance between consecutive attribute records.</param>
    /// <param name="colourOffset">First colour byte in each record.</param>
    /// <param name="colourBytes">Packed or floating colour size.</param>
    /// <param name="uvOffset">First texture coordinate byte.</param>
    /// <param name="offsetOffset">First corner-offset byte.</param>
    /// <param name="motionOffset">First upstream motion-vector byte.</param>
    /// <returns>Whether the original writer can be replaced with byte-identical span packing.</returns>
    internal static bool TryPack(Span<byte> vertices, Span<byte> attributes,
        List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> list, int capacity,
        int stride, int colourOffset, int colourBytes, int uvOffset, int offsetOffset, int motionOffset)
    {
        int n = list.Count;
        if (!Enabled || !BitConverter.IsLittleEndian || n == 0 || capacity < n ||
            colourOffset != 0 || uvOffset != colourBytes || offsetOffset != colourBytes + 8 ||
            motionOffset != colourBytes + 16 || stride != colourBytes + 32 || colourBytes is not (4 or 16) ||
            vertices.Length != (long)capacity * 12 || attributes.Length != (long)capacity * stride)
            return false;

        // List<T>.get_Item copies the whole particle tuple. Read the retained batch directly;
        // no list mutation occurs while a surface is packed on the main thread.
        ReadOnlySpan<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> source = CollectionsMarshal.AsSpan(list);
        Span<Vector3> positions = MemoryMarshal.Cast<byte, Vector3>(vertices);
        if (colourBytes == 4)
        {
            Span<ByteAttributes> packed = MemoryMarshal.Cast<byte, ByteAttributes>(attributes);
            for (int i = 0; i < n; i++)
            {
                ref readonly var value = ref source[i];
                Color c = value.Col;
                uint rgba = (uint)(byte)Math.Clamp(c.R * 255f, 0f, 255f) |
                    ((uint)(byte)Math.Clamp(c.G * 255f, 0f, 255f) << 8) |
                    ((uint)(byte)Math.Clamp(c.B * 255f, 0f, 255f) << 16) |
                    ((uint)(byte)Math.Clamp(c.A * 255f, 0f, 255f) << 24);
                positions[i] = value.Pos;
                packed[i] = new ByteAttributes { Colour = rgba, Uv = value.Uv, Offset = value.Off, Motion = value.Motion };
            }
            // Retain the exact old tail: last position and UV, zero colour and corner offset.
            positions[n..].Fill(source[n - 1].Pos);
            packed[n..].Fill(new ByteAttributes { Uv = source[n - 1].Uv });
        }
        else
        {
            Span<FloatAttributes> packed = MemoryMarshal.Cast<byte, FloatAttributes>(attributes);
            for (int i = 0; i < n; i++)
            {
                ref readonly var value = ref source[i];
                positions[i] = value.Pos;
                packed[i] = new FloatAttributes { Colour = value.Col, Uv = value.Uv, Offset = value.Off, Motion = value.Motion };
            }
            positions[n..].Fill(source[n - 1].Pos);
            packed[n..].Fill(new FloatAttributes { Uv = source[n - 1].Uv });
        }
        return true;
    }
}
