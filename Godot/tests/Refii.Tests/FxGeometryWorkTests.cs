using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using GdUnit4;
using Refii.Fx;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Checks exact bounds plus reused-batch and diagnostic lifecycles.</summary>
[TestSuite]
public sealed class FxGeometryWorkTests
{
    /// <summary>Creates distinguishable vertices with repeated and varying quad distances.</summary>
    private static List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> Batch(int count)
    {
        var values = new List<(Vector3, Vector2, Color, Vector2, Vector4)>();
        for (int i = 0; i < count; i++)
            values.Add((new Vector3((i / 6) % 7, -(i / 6) % 11, i % 6 * .25f),
                new Vector2(i, -i), new Color(i * .03f, .2f, .3f, 1), new Vector2(i % 13, -i % 17), new Vector4(i, -i, .25f, 1)));
        return values;
    }

    /// <summary>Retains the original list-based sorter as an independent golden reference.</summary>
    private static void OriginalSort(List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> values, Vector3 eye)
    {
        int quads = values.Count / 6;
        if (quads < 2) return;
        var order = new List<(float D, int Q)>();
        for (int q = 0; q < quads; q++)
        {
            Vector3 c = .5f * (values[q * 6].Pos + values[q * 6 + 1].Pos);
            order.Add(((c - eye).LengthSquared(), q));
        }
        order.Sort((l, r) => r.D.CompareTo(l.D));
        var sorted = new List<(Vector3, Vector2, Color, Vector2, Vector4)>();
        foreach (var item in order) for (int k = 0; k < 6; k++) sorted.Add(values[item.Q * 6 + k]);
        values.Clear(); values.AddRange(sorted);
    }

    /// <summary>Matches the original arithmetic across empty, partial, tied and large batches.</summary>
    [TestCase]
    public void ExactOriginalBounds()
    {
        bool before = Engine_FxGeometryWork.Enabled;
        try
        {
            Engine_FxGeometryWork.Enabled = true;
            foreach (int count in new[] { 0, 1, 6, 11, 12, 13, 96, 6000 })
            {
                var values = Batch(count);
                Aabb expected = default, actual = default; bool has = false, expectedHas = false;
                float offset = 0, expectedOffset = 0;
                foreach (var value in values)
                {
                    if (!expectedHas) { expected = new Aabb(value.Pos, Vector3.Zero); expectedHas = true; }
                    else expected = expected.Expand(value.Pos);
                    float reach = value.Off.Length(); if (reach > expectedOffset) expectedOffset = reach;
                }
                AssertThat(Engine_FxGeometryWork.TryBounds(values, ref actual, ref has, ref offset)).IsTrue();
                AssertThat(actual == expected && has == expectedHas && offset == expectedOffset).IsTrue();
            }
        }
        finally { Engine_FxGeometryWork.Enabled = before; }
    }

    /// <summary>Retains bounds across multiple surfaces and resets correctly as batches shrink or disappear.</summary>
    [TestCase]
    public void BoundsAndReuseLifecycle()
    {
        bool before = Engine_FxGeometryWork.Enabled;
        try
        {
            Engine_FxGeometryWork.Enabled = true;
            foreach (int count in new[] { 96, 600, 12, 0, 6 })
            {
                Aabb actual = default, expected = default; bool has = false, expectedHas = false;
                float offset = 0, expectedOffset = 0;
                foreach (int surface in new[] { count, count / 2 })
                {
                    var values = Batch(surface);
                    OriginalSort(values, Vector3.Zero);
                    foreach (var value in values)
                    {
                        if (!expectedHas) { expected = new Aabb(value.Pos, Vector3.Zero); expectedHas = true; }
                        else expected = expected.Expand(value.Pos);
                        float reach = value.Off.Length(); if (reach > expectedOffset) expectedOffset = reach;
                    }
                    AssertThat(Engine_FxGeometryWork.TryBounds(values, ref actual, ref has, ref offset)).IsTrue();
                }
                AssertThat(actual == expected && has == expectedHas && offset == expectedOffset).IsTrue();
            }
            var stable = Batch(600);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++)
            {
                Aabb bounds = default; bool has = false; float offset = 0;
                Engine_FxGeometryWork.TryBounds(stable, ref bounds, ref has, ref offset);
            }
            AssertThat(GC.GetAllocatedBytesForCurrentThread() - allocated).IsEqual(0L);
        }
        finally { Engine_FxGeometryWork.Enabled = before; }
    }

    /// <summary>Reads leave state alone, disabled paths leave inputs alone, and invalid values are rejected.</summary>
    [TestCase]
    public void DiagnosticLifecycle()
    {
        bool before = Engine_FxGeometryWork.Enabled;
        try
        {
            using var empty = JsonDocument.Parse("{}");
            AssertThat(Engine_FxGeometryWork.ReadRequest(empty.RootElement) is null).IsTrue();
            using var disable = JsonDocument.Parse("{\"fxBulkGeometry\":false}");
            Engine_FxGeometryWork.Enabled = Engine_FxGeometryWork.ReadRequest(disable.RootElement)!.Value;
            var values = Batch(12);
            Aabb bounds = new(Vector3.One, Vector3.One); bool has = true; float offset = 17;
            AssertThat(values[0] == Batch(12)[0]).IsTrue();
            AssertThat(Engine_FxGeometryWork.TryBounds(values, ref bounds, ref has, ref offset)).IsFalse();
            AssertThat(bounds == new Aabb(Vector3.One, Vector3.One) && has && offset == 17).IsTrue();
            using var invalid = JsonDocument.Parse("{\"fxBulkGeometry\":1}");
            bool rejected = false;
            try { Engine_FxGeometryWork.ReadRequest(invalid.RootElement); } catch (ArgumentException) { rejected = true; }
            AssertThat(rejected && !Engine_FxGeometryWork.Enabled).IsTrue();
        }
        finally { Engine_FxGeometryWork.Enabled = before; }
    }
}
