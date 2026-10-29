using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Refii.Core;
using Refii.Titles.Fable2.PubGames;

namespace Refii.Titles.Fable2;

/// <summary>
/// What the pub games' vector artwork is actually made of.
///
///     Godot_v4.7.1-stable_mono_linux.x86_64 --headless --path Godot \
///         --script res://src/Titles/Fable2/tools/SwfShapeCensus.cs
/// </summary>
/// <remarks>
/// ASKED BEFORE REPLACING THE RASTERIZER. The boards are drawn by a software scanline filler into
/// bitmaps at authored size, which is why they are soft when the stage is magnified to fill a
/// window and why sharpening them costs seconds of CPU. Letting Godot draw the vectors instead is
/// resolution independent and free by comparison — but only for the fill kinds it can express
/// directly.
///
/// So the question is which kinds are present and in what proportion. A solid fill is a polygon; a
/// clipped bitmap fill is a polygon with UVs; a focal gradient is neither, and if the boards are
/// full of them the answer is different. Counting first is cheaper than discovering it halfway
/// through.
///
/// The other two numbers that decide the shape of the work: how many contours a single fill group
/// has — one ring needs no boolean at all, several need even-odd resolved — and how many shapes
/// carry strokes, which are polylines rather than polygons.
/// </remarks>
public partial class SwfShapeCensus : SceneTree
{
	public override void _Initialize()
	{
		string gameRoot = Engine_GameData.ResolveWhere(
			candidate => PubGamesCatalog.ResolveRoot(candidate).Length != 0);
		if (gameRoot.Length == 0)
		{
			GD.Print("[swfcensus] no pub games found");
			Quit(1);
			return;
		}

		PubGamesSet? set = PubGamesCatalog.Discover(gameRoot);
		if (set is null)
		{
			GD.Print("[swfcensus] the catalogue found nothing");
			Quit(1);
			return;
		}

		var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
		var ringCounts = new SortedDictionary<int, int>();
		var totals = new int[6];

		foreach (PubGameEntry game in set.Games)
		{
			SwfFile? file = SwfFile.TryLoad(File.ReadAllBytes(game.SwfPath), out string error);
			if (file is null)
			{
				GD.Print($"[swfcensus] {game.Name}: {error}");
				continue;
			}

			SwfDocument document = SwfDocument.Build(file);
			int shapes = 0;
			int withStrokes = 0;
			int openContours = 0;
			int fills = 0;
			int maxRings = 0;

			foreach ((int id, SwfShape shape) in document.Shapes)
			{
				++shapes;
				if (!document.Geometry.TryGetValue(id, out SwfGeometry? geometry))
				{
					continue;
				}

				if (geometry.Strokes.Count != 0)
				{
					++withStrokes;
				}

				openContours += geometry.OpenContours;

				// GROUPED THE WAY THE RASTERIZER GROUPS THEM, so the ring counts describe the work
				// a replacement would actually have to do rather than the raw contour list.
				var byFill = new Dictionary<int, int>();
				foreach (SwfContour contour in geometry.Contours)
				{
					if (contour.FillStyle <= 0 || contour.FillStyle > shape.FillStyles.Count)
					{
						continue;
					}

					byFill[contour.FillStyle] = byFill.GetValueOrDefault(contour.FillStyle) + 1;
				}

				foreach ((int index, int rings) in byFill)
				{
					++fills;
					maxRings = Math.Max(maxRings, rings);
					ringCounts[rings] = ringCounts.GetValueOrDefault(rings) + 1;

					string kind = shape.FillStyles[index - 1].Kind.ToString();
					kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
				}
			}

			GD.Print(
				$"[swfcensus] {game.Name}: {shapes} shape(s), {fills} fill group(s), " +
				$"{withStrokes} with strokes, {openContours} open contour(s), " +
				$"largest group {maxRings} ring(s)");

			totals[0] += shapes;
			totals[1] += fills;
			totals[2] += withStrokes;
			totals[3] += openContours;
			totals[4] = Math.Max(totals[4], maxRings);
		}

		GD.Print("[swfcensus] fill kinds across every game:");
		var kindTotal = 0;
		foreach ((string kind, int count) in kinds)
		{
			kindTotal += count;
		}

		foreach ((string kind, int count) in kinds)
		{
			GD.Print($"[swfcensus]   {kind,-28} {count,6}  {100.0 * count / kindTotal:0.0}%");
		}

		GD.Print("[swfcensus] rings per fill group:");
		var singles = 0;
		foreach ((int rings, int count) in ringCounts)
		{
			if (rings == 1)
			{
				singles = count;
			}

			if (rings <= 8 || count > 50)
			{
				GD.Print($"[swfcensus]   {rings,4} ring(s) {count,6}");
			}
		}

		GD.Print(
			$"[swfcensus] {totals[0]} shape(s), {totals[1]} fill group(s), " +
			$"{singles} of them a single ring ({100.0 * singles / Math.Max(totals[1], 1):0.0}%), " +
			$"largest {totals[4]} ring(s), {totals[2]} shape(s) with strokes");

		Quit(0);
	}
}
