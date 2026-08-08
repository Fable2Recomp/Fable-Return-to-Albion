using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Refii.Bnk;
using Refii.Launcher.Formats;

namespace Refii.Launcher.Tools;

/// <summary>
/// Converts Fable 2's shipped UI presentations into native Godot scenes, on the user's
/// own machine, from the user's own copy of the game.
///
/// <b>Output must never go into the project.</b> Everything this produces is derived
/// from Fable 2's assets and cannot be redistributed, so it is written under
/// <c>user://</c> — the per-user data directory, outside the repository and outside a
/// Godot export — and is regenerated from the player's install. Writing to
/// <c>res://</c> would bake copyrighted art into the shipped project.
///
/// That is also why conversion is a first-run step rather than a build step: the build
/// machine has no right to the assets, and the player's machine already does. The
/// source is their banks (or an extracted loose tree), read in place.
///
/// Textures are written FIRST and re-loaded from disk before the scenes are built.
/// A <see cref="PackedScene"/> only records an external reference for a resource that
/// has a <c>ResourcePath</c>; a runtime-built <see cref="ImageTexture"/> has none, so
/// packing one embeds a private copy per scene instead of sharing it.
/// </summary>
public partial class UiSceneExporter : Node
{
	/// <summary>Shipped <c>data</c> directory to stream the banks from.</summary>
	[Export(PropertyHint.GlobalDir)] public string GameDataRoot { get; set; } = string.Empty;

	/// <summary>
	/// Where converted assets land. Must stay under <c>user://</c> — see the class
	/// remarks. <see cref="_Ready"/> refuses to run if this is pointed at <c>res://</c>.
	/// </summary>
	[Export] public string OutputRoot { get; set; } = "user://gui";

	public override void _Ready()
	{
		// Hard stop rather than a warning: converted output is derived Fable 2 art, and
		// anything under res:// ends up in the repository and in a Godot export.
		if (!OutputRoot.StartsWith("user://", StringComparison.Ordinal))
		{
			GD.PrintErr($"export: refusing to write to '{OutputRoot}'. Converted assets are " +
				"derived from the user's copy of Fable 2 and must stay under user://.");
			GetTree().Quit(2);
			return;
		}

		// The export is an explicit override; otherwise the one shared convention —
		// the plugin, the viewer and this must not disagree about which install they read.
		string data = Directory.Exists(GameDataRoot)
			? GameDataRoot
			: UiGameData.Resolve();

		if (!Directory.Exists(data))
		{
			GD.PrintErr($"export: game data root not found: '{data}'");
			GetTree().Quit(1);
			return;
		}

		int exit = Run(data);
		GetTree().Quit(exit);
	}

	private int Run(string data)
	{
		// One mount for everything — the engine's bank filesystem indexes every bank
		// under the game directory and stitches the two halves a .tex is split across.
		using var banks = BnkFileSystem.Mount(data);

		string texOut = $"{OutputRoot}/textures";
		string sceneOut = $"{OutputRoot}/screens";
		foreach (string dir in new[] { OutputRoot, texOut, sceneOut })
			DirAccess.MakeDirRecursiveAbsolute(dir);

		var allPaths = banks.EnumerateAllFiles().Select(f => f.VirtualPath).ToList();
		var names = TextureNameTable.FromPaths(allPaths, new[] { ".tex" });
		var cache = new UiTextureCache(banks);

		// Pass 1 — every texture any screen references, saved once and shared.
		var screens = allPaths
			.Where(p => p.EndsWith(".bgf", StringComparison.OrdinalIgnoreCase))
			.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (screens.Count == 0)
		{
			GD.PrintErr($"export: no .bgf presentations under {data}");
			return 1;
		}

		if (System.Environment.GetEnvironmentVariable("REFII_TEX_FORMATS") is not null)
		{
			var byFormat = new SortedDictionary<string, int>(StringComparer.Ordinal);
			foreach (string rel in allPaths.Where(p => p.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)))
			{
				if (!banks.TryGetFile(rel, out BnkFile f)) continue;
				byte[] bytes;
				try { bytes = f.ReadAllBytes(); } catch { continue; }
				string key = Refii.Textures.TexHeader.TryParse(bytes, out var h, out _)
					? $"pixelFormat={h.PixelFormat} codec={h.CodecId} flags=0x{h.Flags:X2}"
					: "no header";
				byFormat[key] = byFormat.TryGetValue(key, out int n) ? n + 1 : 1;
			}
			foreach ((string k, int n) in byFormat) GD.Print($"  {n,5}  {k}");
		}

		var wanted = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string bgfPath in screens)
		{
			UiScreen screen = LoadScreen(banks, bgfPath, names);
			foreach (UiNode node in screen.Nodes)
				if (node.Quad is not null && node.Quad.TexturePath.Length > 0)
					wanted.Add(node.Quad.TexturePath);
		}

		var saved = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
		int texOk = 0, texSkip = 0;
		foreach (string rel in wanted)
		{
			Texture2D? tex = cache.Load(rel);
			if (tex is null) { texSkip++; continue; }

			string path = $"{texOut}/{SanitizeAssetPath(rel)}.res";
			DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
			Error err = ResourceSaver.Save(tex, path);
			if (err != Error.Ok) { GD.PrintErr($"export: {rel}: save failed {err}"); texSkip++; continue; }

			// Re-point the in-memory texture at the file just written so scenes
			// reference it externally instead of embedding a copy.
			tex.TakeOverPath(path);
			saved[rel] = tex;
			texOk++;
		}
		GD.Print($"textures: {texOk} written, {texSkip} skipped, {wanted.Count} referenced");

		// Say WHY, grouped. A bare count of skips cannot distinguish a codec that is not
		// ported from a texture the game simply does not ship a payload for.
		if (cache.Failures.Count > 0)
		{
			var byReason = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
			foreach ((string rel, string why) in cache.Failures)
			{
				if (!byReason.TryGetValue(why, out List<string>? list))
					byReason[why] = list = new List<string>();
				list.Add(rel);
			}

			foreach ((string why, List<string> paths) in byReason)
			{
				GD.Print($"  skipped ({paths.Count}): {why}");
				foreach (string rel in paths.Take(3))
					GD.Print($"      {rel}");
			}
		}

		// Pass 2 — the scenes, resolving against the saved textures.
		int sceneOk = 0, sceneBad = 0;
		long quads = 0, texts = 0;
		foreach (string bgfPath in screens)
		{
			string name = Path.GetFileNameWithoutExtension(bgfPath.Replace('\\', '/'));
			UiScreen screen = LoadScreen(banks, bgfPath, names);

			var builder = new UiSceneBuilder
			{
				TextureResolver = rel => saved.TryGetValue(rel, out Texture2D? t) ? t : null,
			};
			Node3D root = builder.Build(screen);
			root.Name = name;

			// The scene has to carry a working camera, or opening it renders nothing.
			AddChild(root);
			UiSceneBuilder.ApplyCameraAiming(root);
			UiSceneBuilder.ActivateCamera(root);
			RemoveChild(root);

			Error err = UiSceneBuilder.SaveAsScene(root, $"{sceneOut}/{name}.tscn");
			if (err != Error.Ok) { GD.PrintErr($"export: {name}: {err}"); sceneBad++; }
			else { sceneOk++; quads += builder.QuadsBuilt; texts += builder.TextsBuilt; }

			root.QueueFree();
		}

		GD.Print($"screens: {sceneOk} written, {sceneBad} failed  ({quads} quads, {texts} text nodes)");
		GD.Print($"output: {sceneOut}");
		return sceneBad == 0 && texOk > 0 ? 0 : 1;
	}

	private static UiScreen LoadScreen(BnkFileSystem bank, string bgfPath, TextureNameTable names)
	{
		byte[] bgf = Read(bank, bgfPath) ?? throw new IOException($"{bgfPath} missing from bank");
		byte[]? fac = Read(bank, Path.ChangeExtension(bgfPath, ".fac"));
		byte[]? bsg = Read(bank, Path.ChangeExtension(bgfPath, ".bsg"));
		string name = Path.GetFileNameWithoutExtension(bgfPath.Replace('\\', '/'));
		return UiScreen.Parse(name, bgf, fac, bsg, names);
	}

	/// <summary>Reads one bank entry, or null when it is not present.</summary>
	private static byte[]? Read(BnkFileSystem banks, string virtualPath) =>
		banks.TryGetFile(virtualPath, out BnkFile file) ? file.ReadAllBytes() : null;

	/// <summary>
	/// Turns an engine asset path into a <c>res://</c>-safe relative path, preserving the
	/// directory structure so the converted tree mirrors the original.
	/// </summary>
	private static string SanitizeAssetPath(string relativePath)
	{
		string p = relativePath.Replace('\\', '/');
		if (p.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
			p = p[..^4];

		Span<char> buf = stackalloc char[p.Length];
		for (int i = 0; i < p.Length; i++)
		{
			char c = p[i];
			buf[i] = c is '/' ? '/' : (char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? char.ToLowerInvariant(c) : '_');
		}
		return new string(buf);
	}
}
