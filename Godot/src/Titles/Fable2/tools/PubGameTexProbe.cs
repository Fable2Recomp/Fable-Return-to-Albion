using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Godot;
using Refii.Core;
using Refii.Titles.Fable2.PubGames;
using Refii.Titles.Fable2.PubGames.Framework;
using Refii.Textures;

namespace Refii.Titles.Fable2;

/// <summary>
/// Checks that the pub games go through the engine's texture pipeline.
///
///     Godot_v4.7.1-stable_mono_linux.x86_64 --headless --path Godot \
///         --script res://src/Titles/Fable2/tools/PubGameTexProbe.cs
/// </summary>
/// <remarks>
/// THE PUB GAMES ARE A SEPARATE RUNTIME and were also a separate texture path: loose <c>.dds</c>
/// read with <c>File.ReadAllBytes</c> and decoded by Godot, reaching the screen without touching
/// the cache, the policy or the upscaler. This is the check that the second half of that is gone —
/// the decode is still Godot's, because the container is DDS, but everything after it is the same
/// chain the world and the front end use.
///
/// FOUR THINGS, and each of them has already been wrong in a way that reads as working:
///
/// 1. The data is FOUND. The old resolver accepted the first candidate that existed, and in this
///    tree that is a shaders-only directory, so the scene said "pub games not found" with the
///    files present two candidates away.
/// 2. The policy DECLINES the big ones. Every background ships at 1800x1300; doubling that is
///    3600, past the 2048 ceiling, so the right answer is to leave them alone. A probe that only
///    counted enhancements would call that a failure.
/// 3. The result is UNCOMPRESSED. The consumer is a CPU sampler, and a block-compressed image
///    would be decompressed straight back with the encode loss kept.
/// 4. The second run HITS THE CACHE. Cold and warm are timed separately, because "it produced an
///    image" is true whether or not any of this cached.
/// </remarks>
public partial class PubGameTexProbe : SceneTree
{
	private int _checks;

	private int _failures;

	public override void _Initialize()
	{
		// FORCED ON regardless of refii.toml. The probe's job is to exercise the path, and a run
		// that silently measured "upscaling is off in my config" would pass with nothing tested.
		Engine_TexEnhance.SetEnabled(true);
		GD.Print($"[pubtex] backend {Engine_TexEnhance.BackendId}, enabled {Engine_TexEnhance.Enabled}");

		string gameRoot = Engine_GameData.ResolveWhere(
			candidate => PubGamesCatalog.ResolveRoot(candidate).Length != 0);
		Check("game data with pub games in it was found", gameRoot.Length != 0, gameRoot);
		if (gameRoot.Length == 0)
		{
			Finish();
			return;
		}

		GD.Print($"[pubtex] pubgames at {PubGamesCatalog.ResolveRoot(gameRoot)}");

		// The GAME ROOT, not the pubgames directory — Discover resolves the rest itself.
		PubGamesSet? set = PubGamesCatalog.Discover(gameRoot);
		Check("the catalogue discovered a set", set is not null, gameRoot);
		if (set is null)
		{
			Finish();
			return;
		}

		GD.Print(
			$"[pubtex] {set.Games.Count} game(s), {set.SharedTextures.Count} shared texture(s)");

		PerFile(set);
		EndToEnd(set);
		RasterScale(set);
		Finish();
	}

	/// <summary>
	/// Rasterizing the same board at two scales.
	/// </summary>
	/// <remarks>
	/// UPSCALING A SOURCE IS HALF THE JOB. The shapes are rasterized on the CPU into textures at
	/// <see cref="SwfScene.TextureScale"/> times their AUTHORED size, and the host then magnifies
	/// the whole stage to fill the window — about 2.3x at 1080p from a 640x462 stage. At a scale of
	/// 1 that magnification is Godot stretching an authored-size bitmap, so the extra detail in an
	/// upscaled source has nowhere to land. The shipped art says the same thing on its own: the
	/// backgrounds are 1800x1300 against a 640-wide stage, already 2.8x what a scale of 1 can show.
	///
	/// THE FIT FACTOR ITSELF CANNOT BE CHECKED HERE. A headless viewport reports 64x64 whatever
	/// --resolution says, so what is checked is the mechanism: that changing the scale rebuilds the
	/// tree to the same shape with proportionally more pixels in it, and leaves nothing orphaned.
	/// </remarks>
	private void RasterScale(PubGamesSet set)
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

		var root = new Node2D();
		var scene = new SwfScene(stage, root);
		var at1 = Stopwatch.StartNew();
		scene.Sync();
		at1.Stop();
		Measure(root, out int spritesAt1, out long pixelsAt1, out int edgeAt1, out int capAt1);
		int nodesAt1 = CountNodes(root);

		Check("the board rasterizes at 1x", spritesAt1 > 0, $"{spritesAt1} sprite(s)");

		const float Wanted = 2.5f;
		scene.TextureScale = Wanted;
		var at2 = Stopwatch.StartNew();
		scene.Sync();
		at2.Stop();
		Measure(root, out int spritesAt2, out long pixelsAt2, out int edgeAt2, out int capAt2);
		int nodesAt2 = CountNodes(root);

		// MEGABYTES AS WELL AS TEXELS. The canvases are RGBA8 and they are the pub game's whole
		// texture budget, so the number that decides whether a raster scale is affordable is this
		// one — and the SECONDS beside it, because the rasterizer is software and runs at load.
		GD.Print(
			$"[pubtex] raster 1.0x: {spritesAt1} texture(s), {pixelsAt1 / 262144.0:0.0} MB, " +
			$"largest edge {edgeAt1}, {capAt1} at the {DefaultMaxEdge} cap, " +
			$"{at1.ElapsedMilliseconds} ms");
		GD.Print(
			$"[pubtex] raster {Wanted:0.0}x: {spritesAt2} texture(s), {pixelsAt2 / 262144.0:0.0} MB, " +
			$"largest edge {edgeAt2}, {capAt2} at the {DefaultMaxEdge} cap, " +
			$"{at2.ElapsedMilliseconds} ms");

		// THE SAME BOARD, not a different one. A rebuild that dropped or duplicated shapes would
		// still produce more pixels, so the sprite count is the check that matters most.
		Check(
			"the rebuild produces the same number of sprites",
			spritesAt1 == spritesAt2, $"{spritesAt1} then {spritesAt2}");

		// AREA, SO THE CEILING IS THE SQUARE OF THE SCALE — but the cap makes that a ceiling rather
		// than an expectation. Eight canvases are already at MaxTextureEdge at 2.5x and cannot grow
		// with the rest, so the honest assertion is that the scale reached most of the board, not
		// that it reached all of it. Asserting 6.25x here would be asserting that the cap does not
		// exist.
		double ratio = pixelsAt1 == 0 ? 0 : (double)pixelsAt2 / pixelsAt1;
		Check(
			$"the pixel count grows substantially, up to {Wanted * Wanted:0.0}x",
			ratio > 2.5 && ratio <= (Wanted * Wanted) + 0.1, $"{ratio:0.00}x");

		// NOTHING LEFT BEHIND. Rebuild detaches and frees the old containers before Sync builds the
		// new ones; a node it forgot would still be parented, drawing the old low-resolution copy
		// underneath the new one — and the tree would be twice the size.
		Check(
			"no orphaned nodes survive the rebuild",
			nodesAt1 == nodesAt2, $"{nodesAt1} then {nodesAt2}");

		root.QueueFree();
	}

	/// <summary>Sprites with a texture, and the total texels across them.</summary>
	private static void Measure(Node node, out int sprites, out long pixels)
	{
		Measure(node, out sprites, out pixels, out _, out _);
	}

	/// <summary>
	/// The same, plus how close the canvases are running to <see cref="SwfScene.MaxTextureEdge"/>.
	/// </summary>
	/// <remarks>
	/// The cap is reported because it BINDS. Doubling the raster scale does not quadruple the
	/// texels when a shape's canvas was already at the ceiling — it is the difference between the
	/// scale not working and the scale working on everything that had room, and the two look
	/// identical in a total.
	/// </remarks>
	private static void Measure(
		Node node, out int sprites, out long pixels, out int maxEdge, out int atCap)
	{
		// DEDUPED BY TEXTURE, because one rasterized shape is shared by every sprite that places
		// it — a per-sprite total counts the same canvas dozens of times and reads as gigabytes of
		// texture memory that was never allocated.
		var seen = new HashSet<ulong>();
		Walk(node, seen, out sprites, out pixels, out maxEdge, out atCap);
	}

	private static void Walk(
		Node node,
		HashSet<ulong> seen,
		out int sprites,
		out long pixels,
		out int maxEdge,
		out int atCap)
	{
		sprites = 0;
		pixels = 0;
		maxEdge = 0;
		atCap = 0;

		foreach (Node child in node.GetChildren())
		{
			if (child is Sprite2D { Texture: not null } sprite && seen.Add(sprite.Texture.GetInstanceId()))
			{
				int width = sprite.Texture.GetWidth();
				int height = sprite.Texture.GetHeight();
				++sprites;
				pixels += (long)width * height;
				maxEdge = Math.Max(maxEdge, Math.Max(width, height));
				if (Math.Max(width, height) >= DefaultMaxEdge)
				{
					++atCap;
				}
			}

			Walk(child, seen, out int s, out long p, out int e, out int c);
			sprites += s;
			pixels += p;
			maxEdge = Math.Max(maxEdge, e);
			atCap += c;
		}
	}

	/// <summary>Mirrors <see cref="SwfScene.MaxTextureEdge"/>'s default, for the cap report.</summary>
	private const int DefaultMaxEdge = 2048;

	private static int CountNodes(Node node)
	{
		int count = 1;
		foreach (Node child in node.GetChildren())
		{
			count += CountNodes(child);
		}

		return count;
	}

	/// <summary>
	/// Every <c>.dds</c> the set names, through <see cref="Engine_TexLibrary.EnhanceDecoded"/>.
	/// </summary>
	/// <remarks>
	/// Named individually rather than counted, because the interesting result is WHICH files the
	/// policy took and which it declined, and the two are both correct outcomes.
	/// </remarks>
	private void PerFile(PubGamesSet set)
	{
		var files = new List<string>(set.SharedTextures);
		foreach (PubGameEntry game in set.Games)
		{
			files.AddRange(game.Backgrounds);
		}

		Check("the set names at least one texture", files.Count != 0, $"{files.Count}");

		var enhanced = 0;
		var declined = 0;
		foreach (string path in files)
		{
			byte[] source = File.ReadAllBytes(path);
			var original = new Image();
			if (original.LoadDdsFromBuffer(source) != Error.Ok)
			{
				Check($"{Path.GetFileName(path)} decodes", false, "LoadDdsFromBuffer failed");
				continue;
			}

			int width = original.GetWidth();
			int height = original.GetHeight();
			TexPolicy policy = Engine_TexSemantic.For(TexSemantic.Ui);
			int wanted = Engine_TexSemantic.TargetScale(width, height, policy);

			Image? result = Engine_TexLibrary.EnhanceDecoded(
				source, original, TexSemantic.Ui, hasAlpha: true, Path.GetFileName(path),
				compress: false);

			if (wanted <= 1)
			{
				++declined;
				Check(
					$"{Path.GetFileName(path)} {width}x{height} is left alone",
					result is null, result is null ? "declined" : "enhanced anyway");
				continue;
			}

			++enhanced;
			if (result is null)
			{
				Check(
					$"{Path.GetFileName(path)} {width}x{height} enhances", false,
					$"policy wanted {wanted}x, the backend produced nothing");
				continue;
			}

			Check(
				$"{Path.GetFileName(path)} {width}x{height} -> {result.GetWidth()}x{result.GetHeight()}",
				result.GetWidth() == width * wanted && result.GetHeight() == height * wanted,
				$"expected {width * wanted}x{height * wanted}");

			// THE CPU SAMPLER'S REQUIREMENT. SwfBitmapSampler decompresses anything compressed, so
			// a compressed result here means the encode was paid for and thrown away.
			Check(
				$"{Path.GetFileName(path)} comes back uncompressed",
				!result.IsCompressed(), result.GetFormat().ToString());
		}

		GD.Print($"[pubtex] {enhanced} enhanced, {declined} left alone by policy");
	}

	/// <summary>
	/// The real call site: <see cref="PubGameTextures.Build"/>, twice.
	/// </summary>
	/// <remarks>
	/// Twice because the cache is the claim. The first build may run the model; the second must not,
	/// and the only honest evidence for that is the cache counters moving from stores to hits.
	/// </remarks>
	private void EndToEnd(PubGamesSet set)
	{
		PubGameEntry? game = null;
		foreach (PubGameEntry candidate in set.Games)
		{
			if (candidate.Name.Contains("keystone", StringComparison.OrdinalIgnoreCase))
			{
				game = candidate;
				break;
			}
		}

		Check("keystone is in the set", game is not null, string.Empty);
		if (game is null)
		{
			return;
		}

		SwfFile? file = SwfFile.TryLoad(File.ReadAllBytes(game.SwfPath), out string error);
		Check("keystone's game.swf loads", file is not null, error);
		if (file is null)
		{
			return;
		}

		SwfDocument document = SwfDocument.Build(file);

		long hitsBefore = Engine_TexCache.Hits;
		long storesBefore = Engine_TexCache.Stores;

		var cold = Stopwatch.StartNew();
		PubGameTextures first = PubGameTextures.Build(set, game, document);
		cold.Stop();

		long coldHits = Engine_TexCache.Hits - hitsBefore;
		long coldStores = Engine_TexCache.Stores - storesBefore;

		var warm = Stopwatch.StartNew();
		PubGameTextures second = PubGameTextures.Build(set, game, document);
		warm.Stop();

		long warmHits = Engine_TexCache.Hits - hitsBefore - coldHits;
		long warmStores = Engine_TexCache.Stores - storesBefore - coldStores;

		GD.Print(
			$"[pubtex] build 1: {cold.ElapsedMilliseconds} ms, {coldHits} hit, {coldStores} store");
		GD.Print(
			$"[pubtex] build 2: {warm.ElapsedMilliseconds} ms, {warmHits} hit, {warmStores} store");
		GD.Print(
			$"[pubtex] {first.Bindings.Count} binding(s), {first.Unbound.Count} unbound");

		Check("the build bound at least one texture", first.Bindings.Count != 0, string.Empty);
		Check(
			"both builds agree on the bindings",
			first.Bindings.Count == second.Bindings.Count,
			$"{first.Bindings.Count} then {second.Bindings.Count}");

		// STORES, NOT TIME. A warm build being fast is suggestive; a warm build storing nothing is
		// the actual claim, and it holds whether or not the machine was busy.
		Check("the second build stores nothing new", warmStores == 0, $"{warmStores} store(s)");

		// The declined files never reach the cache at all, so a set whose every texture is above
		// the ceiling would legitimately hit zero. Only assert hits when the first build stored.
		if (coldStores > 0)
		{
			Check("the second build reads from the cache", warmHits > 0, $"{warmHits} hit(s)");
		}
	}

	private void Check(string what, bool ok, string detail)
	{
		++_checks;
		if (!ok)
		{
			++_failures;
		}

		string suffix = detail.Length == 0 ? string.Empty : $"  [{detail}]";
		GD.Print($"[pubtex] {(ok ? "ok  " : "FAIL")} {what}{suffix}");
	}

	private void Finish()
	{
		Engine_TexLibrary.Report();
		GD.Print(
			_failures == 0
				? $"[pubtex] PROBE PASS — {_checks} check(s)"
				: $"[pubtex] PROBE FAIL — {_failures} of {_checks} check(s)");
		Quit(_failures == 0 ? 0 : 1);
	}
}
