using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using Godot;
using GdUnit4;
using Refii.Fx;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Checks byte-exact particle packing and reusable-surface/diagnostic lifecycles without native resources.</summary>
[TestSuite]
public sealed class FxSurfacePackerTests
{
    /// <summary>Creates varied positions, UVs, offsets and colours including clamped RGBA8 boundaries.</summary>
    private static List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> Batch(int count)
    {
        var list = new List<(Vector3, Vector2, Color, Vector2, Vector4)>();
        for (int i = 0; i < count; i++)
            list.Add((new Vector3(i * -.25f, i + .125f, -17), new Vector2(i / 7f, -i / 3f),
                new Color(i * .17f - .5f, i * .3f, .499f, i % 3 == 0 ? 1.5f : .333f), new Vector2(i * 2, -i), new Vector4(i, -i * .125f, .5f, i % 2)));
        return list;
    }

    /// <summary>Reproduces the original per-field writer, independently of typed attribute structs.</summary>
    private static (byte[] Positions, byte[] Attributes) Original(
        List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> list, int capacity, int colourBytes)
    {
        byte[] positions = new byte[capacity * 12], attributes = new byte[capacity * (colourBytes + 32)];
        for (int i = 0; i < capacity; i++)
        {
            var value = i < list.Count ? list[i] : (list[^1].Pos, list[^1].Uv, new Color(0, 0, 0, 0), Vector2.Zero, Vector4.Zero);
            var (pos, uv, col, off, motion) = value;
            MemoryMarshal.Write(positions.AsSpan(i * 12, 12), in pos);
            Span<byte> a = attributes.AsSpan(i * (colourBytes + 32), colourBytes + 32);
            if (colourBytes == 4)
            {
                a[0] = (byte)Math.Clamp(col.R * 255f, 0f, 255f);
                a[1] = (byte)Math.Clamp(col.G * 255f, 0f, 255f);
                a[2] = (byte)Math.Clamp(col.B * 255f, 0f, 255f);
                a[3] = (byte)Math.Clamp(col.A * 255f, 0f, 255f);
            }
            else MemoryMarshal.Write(a[..16], in col);
            MemoryMarshal.Write(a.Slice(colourBytes, 8), in uv);
            MemoryMarshal.Write(a.Slice(colourBytes + 8, 8), in off);
            MemoryMarshal.Write(a.Slice(colourBytes + 16, 16), in motion);
        }
        return (positions, attributes);
    }

    /// <summary>Both supported formats preserve every byte, including an empty tail or a large padded tail.</summary>
    [TestCase]
    public void OriginalBytesForBothFormats()
    {
        bool before = Engine_FxSurfacePacker.Enabled;
        try
        {
            Engine_FxSurfacePacker.Enabled = true;
            foreach (int colourBytes in new[] { 4, 16 })
            foreach (int count in new[] { 1, 6, 97 })
            foreach (int capacity in new[] { count, count + 95 })
            {
                var batch = Batch(count);
                var expected = Original(batch, capacity, colourBytes);
                byte[] vertices = new byte[capacity * 12], attributes = new byte[capacity * (colourBytes + 32)];
                Array.Fill(vertices, (byte)0xcc); Array.Fill(attributes, (byte)0xdd);
                AssertThat(Engine_FxSurfacePacker.TryPack(vertices, attributes, batch, capacity,
                    colourBytes + 32, 0, colourBytes, colourBytes, colourBytes + 8, colourBytes + 16)).IsTrue();
                AssertThat(vertices.AsSpan().SequenceEqual(expected.Positions)).IsTrue();
                AssertThat(attributes.AsSpan().SequenceEqual(expected.Attributes)).IsTrue();
            }
        }
        finally { Engine_FxSurfacePacker.Enabled = before; }
    }

    /// <summary>Growing and shrinking a reused surface clears retired vertices, keeps outputs detached, and allocates no frame scratch.</summary>
    [TestCase]
    public void ReuseGrowShrinkAndAllocationLifecycle()
    {
        bool before = Engine_FxSurfacePacker.Enabled;
        try
        {
            Engine_FxSurfacePacker.Enabled = true;
            byte[] vertices = new byte[192 * 12], attributes = new byte[192 * 36];
            foreach (int count in new[] { 6, 150, 12, 1, 96 })
            {
                var batch = Batch(count);
                var expected = Original(batch, 192, 4);
                Engine_FxSurfacePacker.TryPack(vertices, attributes, batch, 192, 36, 0, 4, 4, 12, 20);
                AssertThat(vertices.AsSpan().SequenceEqual(expected.Positions)).IsTrue();
                AssertThat(attributes.AsSpan().SequenceEqual(expected.Attributes)).IsTrue();
            }
            var stable = Batch(24);
            Engine_FxSurfacePacker.TryPack(vertices, attributes, stable, 192, 36, 0, 4, 4, 12, 20);
            byte[] snapshot = (byte[])attributes.Clone();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++)
                Engine_FxSurfacePacker.TryPack(vertices, attributes, stable, 192, 36, 0, 4, 4, 12, 20);
            long cost = GC.GetAllocatedBytesForCurrentThread() - allocated;
            AssertThat(cost).IsEqual(0L);
            AssertThat(attributes.AsSpan().SequenceEqual(snapshot)).IsTrue();
        }
        finally { Engine_FxSurfacePacker.Enabled = before; }
    }

    /// <summary>Rejected layouts and disabled diagnostics leave original buffers available for fallback, then re-enable cleanly.</summary>
    [TestCase]
    public void FallbackAndDiagnosticLifecycle()
    {
        bool before = Engine_FxSurfacePacker.Enabled;
        try
        {
            byte[] vertices = new byte[6 * 12], attributes = new byte[6 * 36];
            Array.Fill(vertices, (byte)7); Array.Fill(attributes, (byte)9);
            var batch = Batch(6);
            using var off = JsonDocument.Parse("{\"fxPackedWrites\":false}");
            Engine_FxSurfacePacker.Enabled = Engine_FxSurfacePacker.ReadRequest(off.RootElement)!.Value;
            AssertThat(Engine_FxSurfacePacker.TryPack(vertices, attributes, batch, 6, 36, 0, 4, 4, 12, 20)).IsFalse();
            Engine_FxSurfacePacker.Enabled = true;
            AssertThat(Engine_FxSurfacePacker.TryPack(vertices, attributes, batch, 6, 36, 1, 4, 4, 12, 20)).IsFalse();
            AssertThat(Engine_FxSurfacePacker.TryPack(vertices, attributes, batch, 5, 36, 0, 4, 4, 12, 20)).IsFalse();
            AssertThat(Engine_FxSurfacePacker.TryPack(vertices.AsSpan(1), attributes, batch, 6, 36, 0, 4, 4, 12, 20)).IsFalse();
            AssertThat(Engine_FxSurfacePacker.TryPack(vertices, attributes, Batch(0), 6, 36, 0, 4, 4, 12, 20)).IsFalse();
            foreach (byte value in vertices) AssertThat(value).IsEqual((byte)7);
            foreach (byte value in attributes) AssertThat(value).IsEqual((byte)9);
            using var invalid = JsonDocument.Parse("{\"fxPackedWrites\":1}");
            bool rejected = false;
            try { Engine_FxSurfacePacker.ReadRequest(invalid.RootElement); } catch (ArgumentException) { rejected = true; }
            AssertThat(rejected && Engine_FxSurfacePacker.Enabled).IsTrue();
            using var absent = JsonDocument.Parse("{}");
            AssertThat(Engine_FxSurfacePacker.ReadRequest(absent.RootElement).HasValue).IsFalse();
            AssertThat(Engine_FxSurfacePacker.TryPack(vertices, attributes, batch, 6, 36, 0, 4, 4, 12, 20)).IsTrue();
        }
        finally { Engine_FxSurfacePacker.Enabled = before; }
    }
}
