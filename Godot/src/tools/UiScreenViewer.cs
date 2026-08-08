using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Refii.Bnk;
using Refii.Launcher.Formats;

namespace Refii.Launcher.Tools;

/// <summary>
/// Renders a Fable 2 Gameface screen so it can be eyeballed against the original game.
///
/// This is a development harness, not part of the launcher UI. It expects a bank
/// extraction laid out as three roots — see <see cref="AssetRoot"/> — and cycles through
/// every <c>.bgf</c> it finds with the left/right arrows.
///
/// The asset root is resolved in this order, so a checkout with assets in a different
/// place needs no code change:
/// <list type="number">
/// <item>the <c>REFII_GUI_ASSETS</c> environment variable,</item>
/// <item><c>&lt;user data&gt;/assets/gui-extracted</c>,</item>
/// <item>the <c>AssetRoot</c> export on the node.</item>
/// </list>
/// </summary>
public partial class UiScreenViewer : Node3D
{
	/// <summary>Fallback asset root, used when neither the env var nor the user data path resolves.</summary>
	[Export] public string AssetRoot { get; set; } = string.Empty;

	/// <summary>Screen to show first. Empty means whichever sorts first.</summary>
	[Export] public string StartScreen { get; set; } = "menubox";

	/// <summary>
	/// Ignore the screen's own camera and frame the content instead. Separates a
	/// layout fault from a framing one — if the pieces sit correctly under this and
	/// wrongly under the authored camera, the camera is what needs work.
	/// </summary>
	[Export] public bool UseFramingCamera { get; set; }

	/// <summary>
	/// The shipped <c>data</c> directory to stream banks from. Setting this in the
	/// Inspector is the whole configuration — no environment variables needed.
	/// </summary>
	[Export(PropertyHint.GlobalDir)] public string GameDataRoot { get; set; } = string.Empty;

	/// <summary>Screen names, in display order. How one is fetched is <see cref="_loadScreen"/>'s job.</summary>
	private readonly List<string> _screens = new();
	private int _index;

	/// <summary>Set by whichever start path won — banks or an extracted tree.</summary>
	private Func<string, UiScreen>? _loadScreen;

	private readonly List<IDisposable> _owned = new();
	private TextureNameTable? _names;
	private UiTextureCache? _textures;
	private BnkFileSystem? _banks;
	private UiFontLibrary? _fonts;
	private BabelDatabase? _strings;
	private Node3D? _current;
	private Label? _status;
	private string _root = string.Empty;

	public override void _Ready()
	{
		_status = GetNodeOrNull<Label>("%Status");

		// Streaming from the shipped banks is the real path — no extraction, and the
		// same parser the guest's VFS uses. The extracted-tree branch below stays only
		// as a fallback for a checkout without the game data.
		if (TryStartFromBanks())
			return;

		_root = ResolveAssetRoot();
		if (_root.Length == 0)
		{
			Report("No GUI asset root found.\nSet REFII_GUI_ASSETS to a directory containing headers/, payloads/ and gameface/.");
			return;
		}

		string gameface = Path.Combine(_root, "gameface");
		string headers = Path.Combine(_root, "headers");
		string payloads = Path.Combine(_root, "payloads");

		foreach (string required in new[] { gameface, headers, payloads })
		{
			if (!Directory.Exists(required))
			{
				Report($"Asset root {_root}\nis missing {Path.GetFileName(required)}/.");
				return;
			}
		}

		// The name table hashes the real extracted paths rather than guessing from the
		// authoring strings, which is what takes UV resolution to 100%.

		var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string file in Directory.EnumerateFiles(gameface, "*.bgf"))
			files[Path.GetFileNameWithoutExtension(file)] = file;

		_screens.AddRange(files.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
		if (_screens.Count == 0)
		{
			Report($"No .bgf files under {gameface}.");
			return;
		}

		_loadScreen = name => UiScreen.Load(files[name], _names);
		Start();
	}

	/// <summary>
	/// Opens the shipped banks and serves every screen and texture straight out of
	/// them. Returns false when the game data is not present, so the caller can fall
	/// back to an extracted tree.
	/// </summary>
	private bool TryStartFromBanks()
	{
		string data = ResolveGameDataRoot();
		if (data.Length == 0)
			return false;

		// One mount for everything. The engine's bank filesystem indexes every bank
		// under the game directory and stitches the header/payload halves a .tex is
		// split across, so screens and textures come from the same handle rather than
		// from three separately-opened banks.
		BnkFileSystem banks;
		try
		{
			banks = BnkFileSystem.Mount(data);
		}
		catch (Exception ex)
		{
			// A mount that is present but unreadable is worth surfacing rather than
			// silently falling through to the extracted tree.
			Report($"Bank streaming unavailable: {ex.Message}");
			return false;
		}

		_owned.Add(banks);
		_banks = banks;

		var texturePaths = new List<string>();
		var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (BnkFile file in banks.EnumerateAllFiles())
		{
			string path = file.VirtualPath;
			if (path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
				texturePaths.Add(path);
			// One entry per presentation; the .fac sidecar is a separate bank entry.
			else if (path.EndsWith(".bgf", StringComparison.OrdinalIgnoreCase))
				byName[BankEntryName(path)] = path;
		}

		if (byName.Count == 0)
			return false;

		_names = TextureNameTable.FromPaths(texturePaths, new[] { ".tex" });
		_textures = new UiTextureCache(banks);
		LoadTextResources(data);

		_screens.AddRange(byName.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
		_root = $"banks: {data}";

		_loadScreen = name =>
		{
			string bgfPath = byName[name];
			byte[] bgf = Read(banks, bgfPath) ?? throw new IOException($"{bgfPath} vanished from the bank");
			byte[]? fac = Read(banks, Path.ChangeExtension(bgfPath, ".fac"));
			// The .bsg carries the base transform the .bgf overrides — all three
			// sidecars are separate bank entries under the same stem.
			byte[]? bsg = Read(banks, Path.ChangeExtension(bgfPath, ".bsg"));
			return UiScreen.Parse(name, bgf, fac, bsg, _names, _strings);
		};

		Start();
		return true;
	}

	/// <summary>Reads one bank entry, or null when it is not present.</summary>
	private static byte[]? Read(BnkFileSystem banks, string virtualPath) =>
		banks.TryGetFile(virtualPath, out BnkFile file) ? file.ReadAllBytes() : null;

	/// <summary>
	/// Loads the bitmap fonts and the localised string database.
	///
	/// Both are LOOSE FILES under <c>language/</c>, not bank entries, so they are found
	/// from the data root rather than through the mounted banks. Neither is fatal: a
	/// screen without fonts still renders its art, and without Babel the authored tag is
	/// displayed verbatim — which is what the engine falls back to as well.
	/// </summary>
	private void LoadTextResources(string dataRoot)
	{
		_fonts = UiFontLibrary.Load(dataRoot);
		foreach (string failure in _fonts.Failures)
			GD.PushWarning($"[ui] font: {failure}");

		DumpFontAtlasIfRequested();

		string? textDir = UiFontLibrary.LanguageSubdirectory(
			dataRoot, UiFontLibrary.DefaultCulture, "text");
		string babel = textDir is null ? string.Empty : Path.Combine(textDir, "book.babel");
		if (babel.Length == 0 || !File.Exists(babel))
		{
			GD.PushWarning($"[ui] no string database under {dataRoot}; tags will display raw");
			return;
		}

		try
		{
			_strings = BabelDatabase.Load(babel);
		}
		catch (Exception exception)
		{
			GD.PushWarning($"[ui] string database: {exception.Message}");
		}
	}

	/// <summary>
	/// With <c>REFII_FONT_DUMP=&lt;dir&gt;</c>, writes each loaded glyph atlas to a PNG and
	/// prints its channel statistics.
	///
	/// Whether the atlas carries colour or a coverage mask, and whether the 360's
	/// 16-bit byte swap is needed, cannot be settled from a composited screen — black
	/// glyph boxes look the same whether the alpha is wrong, the swap is wrong or the
	/// UVs are. Look at the atlas.
	/// </summary>
	private void DumpFontAtlasIfRequested()
	{
		string? directory = System.Environment.GetEnvironmentVariable("REFII_FONT_DUMP");
		if (string.IsNullOrEmpty(directory) || _fonts is null)
			return;

		DirAccess.MakeDirRecursiveAbsolute(directory!);

		foreach (int size in new[] { 12, 18, 24 })
		{
			if (_fonts.For(size) is not { } selection)
				continue;

			(UiFont font, float scale) = selection;
			Image image = font.Atlas.GetImage();
			if (image.IsCompressed())
				image.Decompress();

			// Channel ranges answer the question directly: a coverage mask is
			// white-on-black in RGB with alpha pinned at 1.
			float minA = 1f, maxA = 0f, minL = 1f, maxL = 0f;
			for (int y = 0; y < image.GetHeight(); y += 4)
			{
				for (int x = 0; x < image.GetWidth(); x += 4)
				{
					Color c = image.GetPixel(x, y);
					float l = Mathf.Max(c.R, Mathf.Max(c.G, c.B));
					minA = Mathf.Min(minA, c.A); maxA = Mathf.Max(maxA, c.A);
					minL = Mathf.Min(minL, l);   maxL = Mathf.Max(maxL, l);
				}
			}

			string name = $"{UiFontLibrary.BucketFor(size)}".ToLowerInvariant();
			string path = Path.Combine(directory!, $"font_{name}.png");
			GD.Print($"font {name} (size {size} -> scale {scale:F3}): " +
				$"{image.GetWidth()}x{image.GetHeight()} {image.GetFormat()}  " +
				$"alpha [{minA:F2}..{maxA:F2}]  luma [{minL:F2}..{maxL:F2}]  " +
				$"lineHeight={font.Font.LineHeight} cellHeight={font.Font.CellHeight} " +
				$"glyphs={font.Font.GlyphCount}");
			GD.Print(image.SavePng(path) == Error.Ok ? $"  wrote {path}" : "  save failed");
		}
	}

	/// <summary>
	/// Base name of a bank entry. Bank paths are authored with backslashes, which are
	/// ordinary characters on Linux — <see cref="Path.GetFileNameWithoutExtension"/>
	/// would hand back the whole path, so the separators are normalised first.
	/// </summary>
	private static string BankEntryName(string bankPath) =>
		Path.GetFileNameWithoutExtension(bankPath.Replace('\\', '/'));

	private void Start()
	{
		// REFII_UI_SCREEN picks the screen without editing the scene, so a capture run
		// can target any of the 52.
		string want = System.Environment.GetEnvironmentVariable("REFII_UI_SCREEN") ?? StartScreen;
		int start = _screens.FindIndex(n => string.Equals(n, want, StringComparison.OrdinalIgnoreCase));
		_index = start >= 0 ? start : 0;
		Show(_index);
		CaptureIfRequested();
	}

	/// <summary>
	/// Finds the shipped game data.
	///
	/// The <see cref="GameDataRoot"/> export is an explicit override and nothing more;
	/// everything else defers to <see cref="BankFileSystem.ResolveGameDataRoot"/>. This
	/// used to carry its own copy of the search order, which is precisely how a viewer
	/// ends up reading a different install from the plugin and the exporter.
	/// </summary>
	private string ResolveGameDataRoot()
	{
		if (Directory.Exists(GameDataRoot))
			return GameDataRoot;

		string resolved = UiGameData.Resolve();
		return Directory.Exists(resolved) ? resolved : string.Empty;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (_screens.Count == 0 || @event is not InputEventKey { Pressed: true } key)
			return;

		int delta = key.Keycode switch
		{
			Key.Right or Key.Down => 1,
			Key.Left or Key.Up => -1,
			_ => 0,
		};
		if (delta == 0)
			return;

		_index = (_index + delta + _screens.Count) % _screens.Count;
		Show(_index);
		GetViewport().SetInputAsHandled();
	}

	private void Show(int index)
	{
		string name = _screens[index];

		if (_current is not null)
		{
			RemoveChild(_current);
			_current.QueueFree();
			_current = null;
		}

		UiScreen screen;
		try
		{
			screen = _loadScreen!(name);
		}
		catch (Exception ex)
		{
			Report($"{name}\nload failed: {ex.Message}");
			return;
		}

		var builder = new UiSceneBuilder
		{
			TextureResolver = rel => _textures?.Load(rel),
			FontResolver = size => _fonts?.For(size),
		};
		_current = builder.Build(screen);
		AddChild(_current);
		UiSceneBuilder.ApplyCameraAiming(_current);

		// Building a camera is not enough — one has to be made current or the viewport
		// renders through Godot's default camera and the screen looks empty.
		// REFII_UI_FRAME forces the framing camera without editing the scene, so one run
		// can be compared against the authored camera's.
		bool framing = UseFramingCamera ||
			System.Environment.GetEnvironmentVariable("REFII_UI_FRAME") is not null;
		Camera3D camera = UiSceneBuilder.ActivateCamera(_current, framing);

		DumpSlides(screen);
		DumpQuads(screen);
		DumpSliceGeometry();
		DumpTextureIfRequested();

		Report($"[{index + 1}/{_screens.Count}] {screen.Name}   {screen.CanvasWidth}x{screen.CanvasHeight}\n" +
			$"quads {builder.QuadsBuilt} (degenerate {builder.DegenerateQuads})   " +
			$"textures {builder.TexturesResolved}   " +
			$"text {builder.TextsBuilt} ({builder.TextGlyphsBuilt} glyphs)   " +
			$"camera {camera.Name}\n" +
			"left/right: change screen");
	}

	/// <summary>
	/// Writes the rendered viewport to <c>REFII_UI_SHOT</c> and quits. Rendering can
	/// only be confirmed by looking at pixels — a scene that loads and builds nodes
	/// still renders nothing if no camera is current.
	/// </summary>
	private async void CaptureIfRequested()
	{
		string? all = System.Environment.GetEnvironmentVariable("REFII_UI_SHOT_ALL");
		if (!string.IsNullOrEmpty(all))
		{
			await CaptureEveryScreen(all!);
			return;
		}

		string? target = System.Environment.GetEnvironmentVariable("REFII_UI_SHOT");
		if (string.IsNullOrEmpty(target))
			return;

		// Two frames: one to flush the scene change, one to draw it.
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

		Image image = GetViewport().GetTexture().GetImage();
		Error err = image.SavePng(target!);
		GD.Print(err == Error.Ok ? $"wrote {target}" : $"screenshot failed: {err}");
		GetTree().Quit();
	}

	/// <summary>
	/// With <c>REFII_UI_SHOT_ALL=&lt;dir&gt;</c>, renders every screen to a PNG in one
	/// process and quits.
	///
	/// One process, not 52: the bank mount and font load dominate a single-screen run,
	/// so capturing the set one process at a time costs minutes rather than seconds and
	/// makes a whole-set sweep something you avoid doing.
	/// </summary>
	private async System.Threading.Tasks.Task CaptureEveryScreen(string directory)
	{
		DirAccess.MakeDirRecursiveAbsolute(directory);

		for (int i = 0; i < _screens.Count; i++)
		{
			Show(i);

			// Two frames: one to flush the scene change, one to draw it.
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

			Image image = GetViewport().GetTexture().GetImage();
			string path = Path.Combine(directory, $"{i:D2}_{_screens[i]}.png");
			if (image.SavePng(path) != Error.Ok)
				GD.PrintErr($"screenshot failed: {path}");
		}

		GD.Print($"captured {_screens.Count} screens to {directory}");
		GetTree().Quit();
	}

	public override void _ExitTree()
	{
		foreach (IDisposable owned in _owned)
			owned.Dispose();
		_owned.Clear();
	}

	/// <summary>
	/// With <c>REFII_UI_SLIDES</c> set, prints each owning element's slides and what each
	/// switches on.
	///
	/// Which named slide the game shows is not in the file — see
	/// <see cref="BgfDocument.ComputeVisibility"/> — so this is how you see what the
	/// alternatives even are before deciding what a static preview should pick.
	/// </summary>
	private void DumpSlides(UiScreen screen)
	{
		if (System.Environment.GetEnvironmentVariable("REFII_UI_SLIDES") is null)
			return;

		BgfDocument doc = screen.Document;
		var byOwner = new Dictionary<int, List<BgfSlide>>();
		foreach (BgfSlide s in doc.Slides)
		{
			if (!byOwner.TryGetValue(s.OwnerElement, out var list))
				byOwner[s.OwnerElement] = list = new List<BgfSlide>();
			list.Add(s);
		}

		GD.Print($"slides for {screen.Name}: {doc.Slides.Length} across {byOwner.Count} owners");
		foreach ((int owner, List<BgfSlide> owned) in byOwner.OrderBy(p => p.Key))
		{
			string ownerName = owner >= 0 && owner < screen.Nodes.Length
				? screen.Nodes[owner].Name
				: $"<ordinal {owner}>";
			GD.Print($"  owner {owner} ({ownerName}) — {owned.Count} slide(s)");

			foreach (BgfSlide s in owned)
			{
				int on = s.Entries.Count(e => e.Active);
				int commands = s.Entries.Sum(e => e.CommandCount);
				int events = s.Entries.Sum(e => e.EventCount);
				GD.Print($"      {(s.IsMaster ? "*" : " ")} {s.Name,-28} {s.Entries.Length,4} entries, " +
					$"{on,4} on, {commands,5} commands, {events,4} events");

				foreach (BgfSlideEntry e in s.Entries)
				{
					if (e.CommandCount == 0)
						continue;

					string target = e.ElementOrdinal >= 0 && e.ElementOrdinal < screen.Nodes.Length
						? screen.Nodes[e.ElementOrdinal].Name
						: $"<{e.ElementOrdinal}>";

					foreach (BgfAttribute a in e.Commands)
					{
						string value = a.Type switch
						{
							BgfValueType.Float => a.AsFloat().ToString("0.####"),
							BgfValueType.Bool => a.AsBool().ToString(),
							BgfValueType.String => $"str[{a.AsStringIndex()}]",
							_ => a.AsInt().ToString(),
						};
						GD.Print($"          {(e.Active ? "on " : "off")} {target,-18} " +
							$"{BgfAttributeNames.Describe(a.Id),-16} {a.Type,-10} {value}");
					}
				}
			}
		}
	}

	/// <summary>
	/// With <c>REFII_UI_QUADS</c> set, prints every quad's source record — path in the
	/// tree, atlas rectangle, tint, opacity and resolved texture.
	///
	/// The geometry dump answers "where is it"; this answers "what is it sampling",
	/// which is the question a flat-coloured quad actually poses. Reading it off the
	/// screen instead has produced wrong guesses.
	/// </summary>
	private void DumpQuads(UiScreen screen)
	{
		if (System.Environment.GetEnvironmentVariable("REFII_UI_QUADS") is null)
			return;

		GD.Print($"quads for {screen.Name}  (canvas {screen.CanvasWidth}x{screen.CanvasHeight})");
		GD.Print("  path                                              u0     u1     v0     v1    " +
			"du     dv    tint            op   texture");

		foreach (UiNode n in screen.Walk())
		{
			if (n.Quad is not { } q)
				continue;

			var path = new List<string>();
			for (UiNode? walk = n; walk is not null; walk = walk.Parent)
				path.Add(walk.Name);
			path.Reverse();

			// A quad can be absent from the render for two very different reasons: its own
			// slide entry switched it off, or an ANCESTOR's did and took the subtree with
			// it. Naming the ancestor is the difference between "this element is off" and
			// "this whole component is off", which reads identically on screen.
			string dropped = string.Empty;
			for (UiNode? walk = n; walk is not null; walk = walk.Parent)
			{
				if (!walk.Visible)
					dropped = ReferenceEquals(walk, n) ? "  OFF" : $"  OFF via {walk.Name}";
			}

			Texture2D? tex = q.TexturePath.Length == 0 ? null : _textures?.Load(q.TexturePath);
			string texInfo = q.TexturePath.Length == 0
				? $"<unresolved hash {q.TextureHash:X8}>"
				: $"{Path.GetFileName(q.TexturePath.Replace('\\', '/'))} " +
				  (tex is null ? "(LOAD FAILED)" : $"{tex.GetWidth()}x{tex.GetHeight()}");

			GD.Print($"  {Truncate(string.Join('/', path), 48),-48} " +
				$"{q.UMin,6:F3} {q.UMax,6:F3} {q.VMin,6:F3} {q.VMax,6:F3} " +
				$"{q.UMax - q.UMin,6:F3} {q.VMax - q.VMin,6:F3} " +
				$"{q.R,4:F2},{q.G,4:F2},{q.B,4:F2} {n.Opacity,5:F0}  {texInfo}" +
				(q.IsDegenerate ? "  STRETCH" : "") + dropped);
		}
	}

	private static IEnumerable<Camera3D> FindCameras(Node? node)
	{
		if (node is null)
			yield break;
		if (node is Camera3D cam)
			yield return cam;
		foreach (Node child in node.GetChildren())
			foreach (Camera3D found in FindCameras(child))
				yield return found;
	}

	private static string Truncate(string value, int width) =>
		value.Length <= width ? value : "…" + value[^(width - 1)..];

	/// <summary>
	/// With <c>REFII_UI_DEBUG</c> set, prints the world extent of every BUILT quad, by
	/// full path. Engine-measured geometry, so it settles pitch-versus-width questions
	/// that a screenshot can only suggest.
	///
	/// By full path, not grouped by parent name: slice names repeat across the frame
	/// groups in a screen, so all six of a menu's <c>UpperLeftCorner</c>s collapse into
	/// one bucket and a subtree that never got built looks indistinguishable from one
	/// that did. That is exactly how the missing paperbox edges hid.
	///
	/// Built quads only — pair it with <see cref="DumpQuads"/>, which walks the screen
	/// data, to see what the slide baseline dropped.
	/// </summary>
	private void DumpSliceGeometry()
	{
		if (System.Environment.GetEnvironmentVariable("REFII_UI_DEBUG") is null || _current is null)
			return;

		var rows = new List<(string path, Aabb box, string tex)>();

		void Walk(Node node, string prefix)
		{
			string path = prefix.Length == 0 ? node.Name.ToString() : prefix + "/" + node.Name;
			if (node is MeshInstance3D mesh)
			{
				rows.Add((path,
					mesh.GlobalTransform * mesh.GetAabb(),
					mesh.HasMeta("texture_path") ? (string)mesh.GetMeta("texture_path") : ""));
			}
			foreach (Node child in node.GetChildren())
				Walk(child, path);
		}

		Walk(_current, string.Empty);

		// Camera versus content, together: "the screen is empty" has two causes that look
		// identical — content built somewhere the camera is not pointing, and a camera
		// whose projection does not cover content that is exactly where it should be.
		Aabb content = default;
		bool seeded = false;
		foreach ((_, Aabb box, _) in rows)
		{
			content = seeded ? content.Merge(box) : box;
			seeded = true;
		}

		if (seeded)
		{
			GD.Print($"content bounds: centre {content.GetCenter()}  size {content.Size}  " +
				$"x=[{content.Position.X:F1}..{content.End.X:F1}] " +
				$"y=[{content.Position.Y:F1}..{content.End.Y:F1}] " +
				$"z=[{content.Position.Z:F1}..{content.End.Z:F1}]");
		}

		foreach (Camera3D cam in FindCameras(_current))
		{
			GD.Print($"camera {cam.Name}: current={cam.Current} {cam.Projection} " +
				$"fov={cam.Fov:F2} size={cam.Size:F2} near={cam.Near:F3} far={cam.Far:F1} " +
				$"keep={cam.KeepAspect}");
			GD.Print($"    at {cam.GlobalPosition}  forward {-cam.GlobalTransform.Basis.Z}  " +
				$"up {cam.GlobalTransform.Basis.Y}");
			if (cam.HasMeta("lookatpoint"))
				GD.Print($"    authored lookatpoint (parent space) {(Vector3)cam.GetMeta("lookatpoint")}");
		}

		GD.Print($"built quad world extents ({rows.Count} meshes):");
		foreach ((string path, Aabb box, string tex) in rows)
		{
			string leaf = tex.Length == 0 ? "" : Path.GetFileName(tex.Replace('\\', '/'));
			GD.Print($"  {Truncate(path, 54),-54} x=[{box.Position.X,8:F2} ..{box.End.X,8:F2}] w={box.Size.X,8:F2}  " +
				$"y=[{box.Position.Y,8:F2} ..{box.End.Y,8:F2}] h={box.Size.Y,8:F2}  {leaf}");
		}
	}

	/// <summary>
	/// With <c>REFII_TEX_DUMP=&lt;engine\path.tex&gt;</c> and <c>REFII_TEX_OUT</c>, writes that
	/// texture to a PNG. Lets an atlas cell be inspected directly instead of inferred
	/// from how it looks composited into a screen.
	/// </summary>
	private void DumpTextureIfRequested()
	{
		string? want = System.Environment.GetEnvironmentVariable("REFII_TEX_DUMP");
		string? outPath = System.Environment.GetEnvironmentVariable("REFII_TEX_OUT");
		if (string.IsNullOrEmpty(want) || string.IsNullOrEmpty(outPath) || _textures is null)
			return;

		Texture2D? tex = _textures.Load(want!);
		if (tex is null)
		{
			GD.Print($"texture dump failed: {want} -> " +
				(_textures.Failures.TryGetValue(want!, out string? why) ? why : "not found"));
			return;
		}

		Image image = tex.GetImage();
		// Decompress so the PNG carries actual pixels rather than block data.
		if (image.IsCompressed())
			image.Decompress();
		GD.Print(image.SavePng(outPath!) == Error.Ok
			? $"wrote {outPath} ({image.GetWidth()}x{image.GetHeight()}, {image.GetFormat()})"
			: "texture dump: SavePng failed");
	}

	private void Report(string message)
	{
		if (_status is not null)
			_status.Text = message;
		GD.Print(message);
	}

	/// <summary>
	/// Finds the extraction. Kept out of <see cref="_Ready"/> so the precedence is
	/// readable and so a missing root reports which candidates were tried.
	/// </summary>
	private string ResolveAssetRoot()
	{
		string? fromEnv = System.Environment.GetEnvironmentVariable("REFII_GUI_ASSETS");
		if (!string.IsNullOrEmpty(fromEnv) && Directory.Exists(fromEnv))
			return fromEnv!;

		string? home = System.Environment.GetEnvironmentVariable("HOME");
		if (!string.IsNullOrEmpty(home))
		{
			string candidate = Path.Combine(home!, ".local", "share", "refii", "assets", "gui-extracted");
			if (Directory.Exists(candidate))
				return candidate;
		}

		return Directory.Exists(AssetRoot) ? AssetRoot : string.Empty;
	}
}
