using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Godot;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`android_optimize_index_order`: the same triangles, winding kept, in a better order; blended surfaces untouched.</summary>
[TestSuite]
public sealed class AndroidIndexOrderTests
{
	/// <summary>A width x height grid of quads, two triangles each, listed in a deliberately cache-hostile order.</summary>
	private static (Vector3[] Vertices, int[] Indices) Grid(int width, int height)
	{
		var vertices = new Vector3[(width + 1) * (height + 1)];
		for (int y = 0; y <= height; y++)
		{
			for (int x = 0; x <= width; x++)
			{
				vertices[y * (width + 1) + x] = new Vector3(x, y, (x * 7 + y * 3) % 5 * 0.01f);
			}
		}

		var triangles = new List<int[]>();
		for (int y = 0; y < height; y++)
		{
			for (int x = 0; x < width; x++)
			{
				int a = y * (width + 1) + x, b = a + 1, c = a + width + 1, d = c + 1;
				triangles.Add(new[] { a, c, b });
				triangles.Add(new[] { b, c, d });
			}
		}

		var shuffled = triangles.OrderBy(t => (t[0] * 2654435761u) ^ (uint)t[2]).SelectMany(t => t).ToArray();
		return (vertices, shuffled);
	}

	/// <summary>Each triangle as its vertex triple rotated to start at the smallest index (rotation keeps the winding).</summary>
	private static List<(int, int, int)> Triangles(int[] indices)
	{
		var set = new List<(int, int, int)>();
		for (int i = 0; i < indices.Length; i += 3)
		{
			int a = indices[i], b = indices[i + 1], c = indices[i + 2];
			if (b < a && b <= c) (a, b, c) = (b, c, a);
			else if (c < a && c < b) (a, b, c) = (c, a, b);
			set.Add((a, b, c));
		}

		set.Sort();
		return set;
	}

	private static Godot.Collections.Array ArraysOf(Vector3[] vertices, int[] indices)
	{
		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices;
		arrays[(int)Mesh.ArrayType.Index] = indices;
		return arrays;
	}

	[TestCase, RequireGodotRuntime]
	public void AReorderedListHasTheSameTrianglesWithTheirWinding()
	{
		(Vector3[] vertices, int[] indices) = Grid(40, 40);
		int[]? ordered = AndroidIndexOrder.Reorder(Variant.CreateFrom(vertices), indices);
		AssertThat(ordered).IsNotNull();
		AssertThat(ordered!.Length).IsEqual(indices.Length);
		AssertThat(Triangles(ordered).SequenceEqual(Triangles(indices))).IsTrue();
		AssertThat(ordered.SequenceEqual(indices)).IsFalse();
	}

	[TestCase, RequireGodotRuntime]
	public void ALevelOfTheSurfaceIsReorderedToo()
	{
		(Vector3[] vertices, int[] indices) = Grid(40, 40);
		int[] coarse = indices.Take(indices.Length / 2 / 3 * 3).ToArray();
		var levels = new Engine_MdlSurfaceArrays.Levels();
		levels.Add(0.5f, coarse);
		Godot.Collections.Array arrays = ArraysOf(vertices, indices);
		float[] table = levels.Packed();

		AndroidIndexOrder.Apply(false, arrays, levels);

		int[] own = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
		int[] level = levels.Lods[0.5f].AsInt32Array();
		AssertThat(Triangles(own).SequenceEqual(Triangles(indices))).IsTrue();
		AssertThat(Triangles(level).SequenceEqual(Triangles(coarse))).IsTrue();
		AssertThat(level.Length).IsEqual(coarse.Length);
		AssertThat(levels.Packed().SequenceEqual(table)).IsTrue();
		AssertThat(arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().SequenceEqual(vertices)).IsTrue();
	}

	[TestCase, RequireGodotRuntime]
	public void ABlendedSurfaceIsUntouched()
	{
		(Vector3[] vertices, int[] indices) = Grid(20, 20);
		var levels = new Engine_MdlSurfaceArrays.Levels();
		int[] coarse = indices.Take(60).ToArray();
		levels.Add(0.5f, coarse);
		Godot.Collections.Array arrays = ArraysOf(vertices, indices);

		AndroidIndexOrder.Apply(true, arrays, levels);

		AssertThat(arrays[(int)Mesh.ArrayType.Index].AsInt32Array().SequenceEqual(indices)).IsTrue();
		AssertThat(levels.Lods[0.5f].AsInt32Array().SequenceEqual(coarse)).IsTrue();
	}

	[TestCase, RequireGodotRuntime]
	public void AMalformedListIsLeftAlone()
	{
		(Vector3[] vertices, _) = Grid(2, 2);
		AssertThat(AndroidIndexOrder.Reorder(Variant.CreateFrom(vertices), new[] { 0, 1, 2, 3 })).IsNull();
		AssertThat(AndroidIndexOrder.Reorder(Variant.CreateFrom(vertices), new[] { 0, 1, 99 })).IsNull();
	}

	/// <summary>Prints the cost per list on a grid the size of a big prop (measurement, not an assertion).</summary>
	[TestCase, RequireGodotRuntime]
	public void TimeAPropSizedList()
	{
		(Vector3[] vertices, int[] indices) = Grid(100, 100);
		Variant positions = Variant.CreateFrom(vertices);
		AndroidIndexOrder.Reorder(positions, indices);
		long start = System.Diagnostics.Stopwatch.GetTimestamp();
		const int runs = 20;
		for (int i = 0; i < runs; i++) AndroidIndexOrder.Reorder(positions, indices);
		double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / runs;
		GD.Print($"[index-order] {indices.Length / 3} triangles, {vertices.Length} vertices: {ms:F2} ms per list");
		AssertThat(ms).IsLess(1000.0);
	}

	/// <summary>Off a phone the key is never read and nothing is reordered, whatever a desktop refii.toml says.</summary>
	[TestCase]
	public void TheDesktopNeverReadsTheKey()
	{
		AssertThat(Refii.Core.Engine_Platform.IsAndroid).IsFalse();
		AssertThat(AndroidIndexOrder.Enabled).IsFalse();
		AssertThat(typeof(AndroidIndexOrder).GetField("_enabled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null) is null).IsTrue();
	}
}
