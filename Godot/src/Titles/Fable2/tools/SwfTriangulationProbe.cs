using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Godot;
using Refii.Core;
using Refii.Titles.Fable2.PubGames;

namespace Refii.Titles.Fable2;

/// <summary>
/// Checks that the triangles agree with the fill rule the software rasterizer implements.
///
///     Godot_v4.7.1-stable_mono_linux.x86_64 --headless --path Godot \
///         --script res://src/Titles/Fable2/tools/SwfTriangulationProbe.cs
/// </summary>
/// <remarks>
/// THE RASTERIZER IS THE SPECIFICATION, not a reference implementation to be improved on. It fills
/// between alternate sorted crossings on every row, which is the even-odd rule, and the shipped
/// artwork was authored against exactly that: a ring inside another ring is a hole because that
/// rule says so, and nothing in the file marks it as one. So the question this asks is not "do the
/// triangles look right" but "is a point inside the triangles exactly when the even-odd rule says
/// it is inside".
///
/// SAMPLED, NOT RENDERED, and deliberately. Comparing two rendered images would fold in pixel
/// snapping, anti-aliasing and the sample grid, and a disagreement would be impossible to
/// attribute. A point-in-polygon test against a point-in-triangle test compares the two rules and
/// nothing else — and it runs headless, which a rendering comparison does not.
///
/// The boundary is where the two must differ slightly, because a sample exactly on an edge is
/// inside for one and outside for the other with no wrong answer available. Samples within a
/// tolerance of any edge are counted separately and excluded from the verdict rather than being
/// allowed to fail it.
/// </remarks>
public partial class SwfTriangulationProbe : SceneTree
{
	/// <summary>Samples across the shape's bounds, per axis.</summary>
	private const int Grid = 48;

	/// <summary>Shapes to check per game. The full set is thousands and the cost is quadratic.</summary>
	private const int PerGame = 120;

	/// <summary>
	/// How close to an edge a sample has to be before its answer is treated as undecidable.
	/// </summary>
	/// <remarks>
	/// In twips, so 1 twip is a twentieth of an authored pixel — far below anything visible, and
	/// far above the float error in a line-side test on coordinates of this magnitude.
	/// </remarks>
	private const float EdgeTolerance = 1.0f;

	private int _checks;

	private int _failures;

	public override void _Initialize()
	{
		string gameRoot = Engine_GameData.ResolveWhere(
			candidate => PubGamesCatalog.ResolveRoot(candidate).Length != 0);
		if (gameRoot.Length == 0)
		{
			GD.Print("[swftri] no pub games found");
			Quit(1);
			return;
		}

		PubGamesSet? set = PubGamesCatalog.Discover(gameRoot);
		if (set is null)
		{
			GD.Print("[swftri] the catalogue found nothing");
			Quit(1);
			return;
		}

		foreach (PubGameEntry game in set.Games)
		{
			Sweep(game);
		}

		Placed(set);

		GD.Print(
			_failures == 0
				? $"[swftri] PROBE PASS — {_checks} check(s)"
				: $"[swftri] PROBE FAIL — {_failures} of {_checks} check(s)");
		Quit(_failures == 0 ? 0 : 1);
	}

	/// <summary>
	/// The cost over the shapes a real board actually places.
	/// </summary>
	/// <remarks>
	/// THE COMPARABLE NUMBER, and the sweep above is not it. Keystone's file holds 2,035 shapes and
	/// a standing board places a small fraction of them — the rasterizer's measured 109 MB and
	/// 4.6 s were over 234 distinct canvases, not over the file. Quoting a whole-file triangulation
	/// time against that would be comparing a build of everything with a build of what is on
	/// screen, which is the mistake this project has made before with a raw count against a
	/// deduplicated one.
	/// </remarks>
	private void Placed(PubGamesSet set)
	{
		PubGameEntry? game = null;
		foreach (PubGameEntry candidate in set.Games)
		{
			if (candidate.Name.Contains("keystone", StringComparison.OrdinalIgnoreCase))
			{
				game = candidate;
			}
		}

		if (game is null)
		{
			return;
		}

		SwfFile? file = SwfFile.TryLoad(File.ReadAllBytes(game.SwfPath), out _);
		if (file is null)
		{
			return;
		}

		SwfDocument document = SwfDocument.Build(file);
		PubGameTextures textures = PubGameTextures.Build(set, game, document);
		var stage = new SwfStage(document, file.FrameSize, textures.Resolve);

		var placed = new HashSet<int>();
		Collect(stage.Root, placed);

		var timer = new Stopwatch();
		long triangles = 0;
		var shapes = 0;
		foreach (int id in placed)
		{
			if (!document.Shapes.TryGetValue(id, out SwfShape? shape) ||
				!document.Geometry.TryGetValue(id, out SwfGeometry? geometry))
			{
				continue;
			}

			++shapes;
			timer.Start();
			List<SwfFillMesh> meshes = SwfShapeTriangulator.Build(shape, geometry);
			timer.Stop();

			foreach (SwfFillMesh mesh in meshes)
			{
				triangles += mesh.TriangleCount;
			}
		}

		// VERTICES, NOT MEGABYTES OF CANVAS. Two floats a vertex is what the comparison is against:
		// the rasterizer's equivalent line was 109 MB of RGBA8, and it had to be rebuilt to change
		// resolution where this does not.
		double megabytes = triangles * 3.0 * 8.0 / (1024.0 * 1024.0);
		GD.Print(
			$"[swftri] keystone board: {shapes} placed shape(s) of {document.Shapes.Count}, " +
			$"{triangles:N0} triangle(s), {megabytes:0.0} MB of vertices, " +
			$"{timer.ElapsedMilliseconds} ms");

		Check("the placed board triangulates", shapes > 0 && triangles > 0, $"{shapes} shape(s)");
	}

	private static void Collect(SwfClip clip, HashSet<int> placed)
	{
		foreach (SwfClip child in clip.Children.Values)
		{
			if (child.CharacterId >= 0)
			{
				placed.Add(child.CharacterId);
			}

			Collect(child, placed);
		}
	}

	private void Sweep(PubGameEntry game)
	{
		SwfFile? file = SwfFile.TryLoad(File.ReadAllBytes(game.SwfPath), out string error);
		if (file is null)
		{
			GD.Print($"[swftri] {game.Name}: {error}");
			return;
		}

		SwfDocument document = SwfDocument.Build(file);

		// EVERY SHAPE IS TRIANGULATED, even though only a sample is compared. Building is where an
		// exception or a runaway triangle count would show up, and both are properties of the whole
		// set rather than of the sample.
		var built = new Stopwatch();
		long triangles = 0;
		var groups = 0;
		var empty = 0;
		var order = new List<int>(document.Shapes.Keys);
		order.Sort();

		var meshes = new Dictionary<int, List<SwfFillMesh>>();
		foreach (int id in order)
		{
			if (!document.Geometry.TryGetValue(id, out SwfGeometry? geometry))
			{
				continue;
			}

			built.Start();
			List<SwfFillMesh> shapeMeshes = SwfShapeTriangulator.Build(document.Shapes[id], geometry);
			built.Stop();

			meshes[id] = shapeMeshes;
			groups += shapeMeshes.Count;
			foreach (SwfFillMesh mesh in shapeMeshes)
			{
				triangles += mesh.TriangleCount;
			}

			if (shapeMeshes.Count == 0 && geometry.Contours.Count != 0)
			{
				++empty;
			}
		}

		GD.Print(
			$"[swftri] {game.Name}: {meshes.Count} shape(s) triangulated in " +
			$"{built.ElapsedMilliseconds} ms, {groups:N0} fill group(s), {triangles:N0} triangle(s)" +
			(empty == 0 ? string.Empty : $", {empty} produced nothing"));

		Check($"{game.Name}: every shape with contours produced triangles", empty == 0, $"{empty}");

		// THE LARGEST FIRST, then a spread. The big shapes are where a decomposition goes wrong and
		// where the triangle count would run away, so picking only the first N in id order would
		// systematically miss them.
		order.Sort((a, b) => Count(meshes, b).CompareTo(Count(meshes, a)));

		var compared = 0;
		double worst = 1.0;
		var worstId = -1;
		long undecidable = 0;
		long decided = 0;

		foreach (int id in order)
		{
			if (compared >= PerGame)
			{
				break;
			}

			if (!meshes.TryGetValue(id, out List<SwfFillMesh>? shapeMeshes) ||
				shapeMeshes.Count == 0 ||
				!document.Geometry.TryGetValue(id, out SwfGeometry? geometry))
			{
				continue;
			}

			++compared;
			double agreement = Compare(
				document.Shapes[id], geometry, shapeMeshes, out long near, out long total);
			undecidable += near;
			decided += total;

			if (agreement < worst)
			{
				worst = agreement;
				worstId = id;
			}
		}

		GD.Print(
			$"[swftri] {game.Name}: {compared} shape(s) sampled, {decided:N0} decidable point(s), " +
			$"{undecidable:N0} on an edge, worst agreement {worst * 100.0:0.000}% (shape {worstId})");

		// EXACT, not approximate. Both sides evaluate the same rule on the same coordinates, so any
		// disagreement away from an edge is a real difference in the geometry, not a tolerance.
		Check(
			$"{game.Name}: the triangles agree with even-odd everywhere off an edge",
			worst >= 1.0, $"{worst * 100.0:0.000}%");
	}

	private static int Count(Dictionary<int, List<SwfFillMesh>> meshes, int id)
	{
		if (!meshes.TryGetValue(id, out List<SwfFillMesh>? list))
		{
			return 0;
		}

		var total = 0;
		foreach (SwfFillMesh mesh in list)
		{
			total += mesh.TriangleCount;
		}

		return total;
	}

	/// <summary>
	/// The fraction of decidable sample points where the two rules agree, over every fill group.
	/// </summary>
	private static double Compare(
		SwfShape shape,
		SwfGeometry geometry,
		List<SwfFillMesh> meshes,
		out long near,
		out long decided)
	{
		near = 0;
		decided = 0;
		long agreed = 0;

		foreach (SwfFillMesh mesh in meshes)
		{
			var rings = new List<List<Vector2>>();
			foreach (SwfContour contour in geometry.Contours)
			{
				if (contour.FillStyle == mesh.FillStyle && contour.Points.Count >= 3)
				{
					rings.Add(contour.Points);
				}
			}

			if (rings.Count == 0)
			{
				continue;
			}

			Bounds(rings, out Vector2 min, out Vector2 max);
			Vector2 span = max - min;
			if (span.X <= 0f || span.Y <= 0f)
			{
				continue;
			}

			for (var iy = 0; iy < Grid; iy++)
			{
				for (var ix = 0; ix < Grid; ix++)
				{
					// OFFSET BY AN IRRATIONAL-ISH FRACTION rather than sampling on a round grid.
					// Authored artwork is full of axis-aligned edges on round coordinates, and a
					// grid that lands on them would put most of its samples in the undecidable
					// bucket and measure almost nothing.
					var point = new Vector2(
						min.X + (span.X * ((ix + 0.3183f) / Grid)),
						min.Y + (span.Y * ((iy + 0.2718f) / Grid)));

					if (NearAnyEdge(rings, point))
					{
						++near;
						continue;
					}

					++decided;
					if (InsideEvenOdd(rings, point) == InsideTriangles(mesh.Vertices, point))
					{
						++agreed;
					}
				}
			}
		}

		return decided == 0 ? 1.0 : (double)agreed / decided;
	}

	private static void Bounds(List<List<Vector2>> rings, out Vector2 min, out Vector2 max)
	{
		min = new Vector2(float.MaxValue, float.MaxValue);
		max = new Vector2(float.MinValue, float.MinValue);
		foreach (List<Vector2> ring in rings)
		{
			foreach (Vector2 point in ring)
			{
				min = new Vector2(Math.Min(min.X, point.X), Math.Min(min.Y, point.Y));
				max = new Vector2(Math.Max(max.X, point.X), Math.Max(max.Y, point.Y));
			}
		}
	}

	/// <summary>The rasterizer's rule: cross a ray, count the crossings, odd is inside.</summary>
	private static bool InsideEvenOdd(List<List<Vector2>> rings, Vector2 point)
	{
		var inside = false;
		foreach (List<Vector2> ring in rings)
		{
			int count = ring.Count;
			for (var i = 0; i < count; i++)
			{
				Vector2 a = ring[i];
				Vector2 b = ring[(i + 1) % count];
				if (a.Y == b.Y)
				{
					continue;
				}

				if (point.Y < Math.Min(a.Y, b.Y) || point.Y >= Math.Max(a.Y, b.Y))
				{
					continue;
				}

				float t = (point.Y - a.Y) / (b.Y - a.Y);
				if (a.X + (t * (b.X - a.X)) > point.X)
				{
					inside = !inside;
				}
			}
		}

		return inside;
	}

	private static bool InsideTriangles(Vector2[] vertices, Vector2 point)
	{
		for (var i = 0; i + 2 < vertices.Length; i += 3)
		{
			if (InsideTriangle(vertices[i], vertices[i + 1], vertices[i + 2], point))
			{
				return true;
			}
		}

		return false;
	}

	private static bool InsideTriangle(Vector2 a, Vector2 b, Vector2 c, Vector2 p)
	{
		float d1 = Cross(b - a, p - a);
		float d2 = Cross(c - b, p - b);
		float d3 = Cross(a - c, p - c);
		bool negative = d1 < 0 || d2 < 0 || d3 < 0;
		bool positive = d1 > 0 || d2 > 0 || d3 > 0;
		return !(negative && positive);
	}

	private static float Cross(Vector2 a, Vector2 b) => (a.X * b.Y) - (a.Y * b.X);

	/// <summary>
	/// Whether a point is close enough to an edge that neither rule owns the answer.
	/// </summary>
	/// <remarks>
	/// Both the polygon edges and the trapezoid boundaries matter, but the trapezoid boundaries are
	/// derived from the polygon's vertex rows and its edges, so testing the polygon covers both:
	/// a horizontal band boundary only exists where a vertex is.
	/// </remarks>
	private static bool NearAnyEdge(List<List<Vector2>> rings, Vector2 point)
	{
		foreach (List<Vector2> ring in rings)
		{
			int count = ring.Count;
			for (var i = 0; i < count; i++)
			{
				Vector2 a = ring[i];
				Vector2 b = ring[(i + 1) % count];
				if (DistanceToSegment(a, b, point) <= EdgeTolerance)
				{
					return true;
				}

				// The band boundary through this vertex, which the decomposition creates and the
				// polygon does not have as an edge.
				if (Math.Abs(point.Y - a.Y) <= EdgeTolerance)
				{
					return true;
				}
			}
		}

		return false;
	}

	private static float DistanceToSegment(Vector2 a, Vector2 b, Vector2 p)
	{
		Vector2 ab = b - a;
		float lengthSquared = ab.LengthSquared();
		if (lengthSquared <= 0f)
		{
			return (p - a).Length();
		}

		float t = Math.Clamp(((p - a).X * ab.X + (p - a).Y * ab.Y) / lengthSquared, 0f, 1f);
		return (p - (a + (ab * t))).Length();
	}

	private void Check(string what, bool ok, string detail)
	{
		++_checks;
		if (!ok)
		{
			++_failures;
		}

		string suffix = detail.Length == 0 ? string.Empty : $"  [{detail}]";
		GD.Print($"[swftri] {(ok ? "ok  " : "FAIL")} {what}{suffix}");
	}
}
