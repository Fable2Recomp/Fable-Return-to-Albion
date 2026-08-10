using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Refii.Bnk;
using Refii.Core;
using Refii.Text;
using Refii.Textures;

namespace Refii.Ui;

/// <summary>
/// Stands up a Fable 2 Gameface presentation and keeps it on screen.
///
/// This is the ONE place a <c>.bgf</c> becomes Godot nodes. Mounting the banks, building
/// the texture name table, loading the bitmap fonts and the Babel database, parsing a
/// screen and handing it to <see cref="Engine_UiSceneBuilder"/> are all the same work
/// whether the screen is being eyeballed in the viewer or drawn by the running game, so
/// they live here and both go through it.
///
/// It lives here rather than in the viewer so the game does not reach its front end by
/// instantiating a development harness. That is not a tidiness point: the harness cycles
/// presentations on the arrow keys, so a direction pressed in the running game would flip the
/// front end to whichever of the 52 screens sorted next.
///
/// Subclasses get the loaded resources through the protected members and a
/// <see cref="OnScreenShown"/> hook; see <c>UiScreenViewer</c>, which is now only the
/// diagnostics and the keyboard.
/// </summary>
public partial class Engine_UiPresentationHost : Node3D
{
	/// <summary>Fallback asset root, used when neither the env var nor the user data path resolves.</summary>
	[Export] public string AssetRoot { get; set; } = string.Empty;

	/// <summary>Screen to show first. Empty means whichever sorts first.</summary>
	[Export] public string StartScreen { get; set; } = string.Empty;

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

	/// <summary>Set by whichever start path won — banks or an extracted tree.</summary>
	private Func<string, Engine_UiScreen>? _loadScreen;

	private readonly List<IDisposable> _owned = new();
	private Engine_TextureNameTable? _names;

	/// <summary>Where the presentations came from, for diagnostics.</summary>
	protected string SourceRoot = string.Empty;

	/// <summary>The texture cache the built quads sample through. Null until loading succeeds.</summary>
	protected Engine_UiTextureCache? Textures { get; private set; }

	/// <summary>The bitmap fonts text elements are built with. Null until loading succeeds.</summary>
	protected Engine_UiFontLibrary? Fonts { get; private set; }

	/// <summary>
	/// The localised string database, for anything that has a TAG but no presentation.
	///
	/// Exposed rather than private because a host-authored screen resolves its labels through
	/// the same book — a Godot-owned menu holds text tags in its MODEL, not in a <c>.bgf</c>,
	/// so it has no element to read a string off. It was private, and the menu harness reached
	/// around it by loading <c>book.babel</c> a second time; that is the second implementation
	/// the engine rules forbid, and it would have drifted the moment either side learned about
	/// a language override.
	/// </summary>
	protected Engine_BabelDatabase? Strings { get; private set; }

	/// <summary>
	/// The shipped look, for anything drawn ALONGSIDE the presentation rather than out of it.
	///
	/// Built here because this is where the fonts, the strings and the standing screen already
	/// are: a Godot-owned menu laid over the front end, or a screen the original never had,
	/// needs exactly those three and has no business opening banks of its own to get them.
	/// Re-catalogued on every <see cref="Show"/>, so the art it hands out always comes from
	/// the presentation currently up.
	///
	/// Null until loading succeeds — a host that never found the game data has no look to
	/// offer, and a caller should fall back to text or to nothing rather than to invented art.
	/// </summary>
	public Engine_UiTheme? Theme { get; private set; }

	/// <summary>The node tree currently mounted, or null before the first screen is shown.</summary>
	protected Node3D? Built { get; private set; }

	/// <summary>The presentation currently built, for diagnostics.</summary>
	public Engine_UiScreen? CurrentScreen { get; private set; }

	/// <summary>Index into <see cref="ScreenNames"/> of the presentation on screen.</summary>
	public int CurrentIndex { get; private set; }

	/// <summary>The presentations available, once loading has finished.</summary>
	public IReadOnlyList<string> ScreenNames => _screens;

	/// <summary>True once the banks or the extracted tree opened and a screen can be shown.</summary>
	public bool IsLoaded => _loadScreen is not null;

	/// <summary>
	/// Which scene-level slide to build, 1-based, or 0 for the preview heuristic.
	///
	/// The front-end screens are one page per slide — see
	/// <see cref="Engine_BgfDocument.SetSceneSlide"/>. Changing this takes effect on the next
	/// <see cref="ShowByName(string,int)"/>, because visibility and attribute overrides
	/// are resolved while the screen is being built.
	/// </summary>
	public int SceneSlide { get; set; }

	/// <summary>
	/// Per-element slides read from the running guest, by element ordinal. When set, these
	/// win over <see cref="SceneSlide"/> — they are what the game is actually showing
	/// rather than a guess at it.
	/// </summary>
	public IReadOnlyList<int>? LiveSlides { get; set; }

	/// <summary>Per-element text read from the running guest, by element ordinal.</summary>
	public IReadOnlyList<string?>? LiveText { get; set; }

	/// <summary>Per-element opacity read from the running guest, by element ordinal.</summary>
	public IReadOnlyList<float?>? LiveOpacity { get; set; }

	/// <summary>
	/// Per-element <c>globalactive</c> read from the running guest, by element ordinal.
	///
	/// The guest's own draw gate. When present it REPLACES the slide-derived visibility, which
	/// can only reproduce what the file authored — the screens change it at runtime through
	/// Lua and the file knows nothing about that.
	/// </summary>
	public IReadOnlyList<bool>? LiveVisible { get; set; }

	/// <summary>
	/// Per-element live TRANSFORM read from the guest, by element ordinal:
	/// <c>{position.x, position.y, position.z, scale.x, scale.y, scale.z}</c>, any of which
	/// may be null when the guest holds no such attribute on that element.
	///
	/// Without this every element sits where the FILE put it, and anything the game lays out
	/// at runtime collapses onto one spot — a menu's rows are twelve components authored at
	/// the same place and moved apart by script, so ignoring this drew them all on top of each
	/// other and read as garbled text.
	/// </summary>
	public IReadOnlyList<float?[]?>? LiveTransform { get; set; }

	public override void _Ready()
	{
		// Streaming from the shipped banks is the real path — no extraction, and the
		// same parser the guest's VFS uses. The extracted-tree branch below stays only
		// as a fallback for a checkout without the game data.
		if (TryStartFromBanks())
			return;

		if (!TryStartFromExtractedTree())
			return;

		OnLoaded();
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

		_names = Engine_TextureNameTable.FromPaths(texturePaths, new[] { ".tex" });
		Textures = new Engine_UiTextureCache(banks);
		LoadTextResources(data);

		_screens.AddRange(byName.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
		SourceRoot = $"banks: {data}";

		_loadScreen = name =>
		{
			string bgfPath = byName[name];
			byte[] bgf = Read(banks, bgfPath) ?? throw new IOException($"{bgfPath} vanished from the bank");
			byte[]? fac = Read(banks, Path.ChangeExtension(bgfPath, ".fac"));
			// The .bsg carries the base transform the .bgf overrides — all three
			// sidecars are separate bank entries under the same stem.
			byte[]? bsg = Read(banks, Path.ChangeExtension(bgfPath, ".bsg"));
			// LiveOpacity and LiveVisible are passed HERE, and were not before: the properties
			// existed, GameUi set them every frame, and nothing ever read them. A live value
			// that never reaches the parser is worse than no live value, because the call site
			// looks like it is following the guest and is not.
			return Engine_UiScreen.Parse(name, bgf, fac, bsg, _names, Strings, SceneSlide, LiveSlides,
				LiveText, LiveOpacity, LiveVisible, LiveTransform);
		};

		OnLoaded();
		return true;
	}

	/// <summary>
	/// Serves screens from a bank extraction laid out as <c>gameface/</c>, <c>headers/</c>
	/// and <c>payloads/</c>. Returns false — having reported why — when there is no such
	/// tree, since by this point the banks have already been ruled out.
	/// </summary>
	private bool TryStartFromExtractedTree()
	{
		SourceRoot = ResolveAssetRoot();
		if (SourceRoot.Length == 0)
		{
			Report("No GUI asset root found.\nSet REFII_GUI_ASSETS to a directory containing headers/, payloads/ and gameface/.");
			return false;
		}

		string gameface = Path.Combine(SourceRoot, "gameface");

		foreach (string required in new[]
			{ gameface, Path.Combine(SourceRoot, "headers"), Path.Combine(SourceRoot, "payloads") })
		{
			if (!Directory.Exists(required))
			{
				Report($"Asset root {SourceRoot}\nis missing {Path.GetFileName(required)}/.");
				return false;
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
			return false;
		}

		_loadScreen = name => Engine_UiScreen.Load(files[name], _names, null, SceneSlide, LiveSlides);
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
		Fonts = Engine_UiFontLibrary.Load(dataRoot);
		foreach (string failure in Fonts.Failures)
			GD.PushWarning($"[ui] font: {failure}");

		string? textDir = Engine_UiFontLibrary.LanguageSubdirectory(
			dataRoot, Engine_UiFontLibrary.DefaultCulture, "text");
		string babel = textDir is null ? string.Empty : Path.Combine(textDir, "book.babel");
		if (babel.Length == 0 || !File.Exists(babel))
		{
			GD.PushWarning($"[ui] no string database under {dataRoot}; tags will display raw");
		}
		else
		{
			try
			{
				Strings = Engine_BabelDatabase.Load(babel);
			}
			catch (Exception exception)
			{
				GD.PushWarning($"[ui] string database: {exception.Message}");
			}
		}

		// Built on EVERY path out of here, including the two failures above. A theme with no
		// string book still carries the fonts and the art, and a label showing its raw tag is
		// a better outcome than a host-authored screen with no theme at all — which cannot
		// tell "the game data is missing" from "nobody built one".
		Theme = new Engine_UiTheme { Fonts = Fonts, Strings = Strings };
	}

	/// <summary>
	/// Base name of a bank entry. Bank paths are authored with backslashes, which are
	/// ordinary characters on Linux — <see cref="Path.GetFileNameWithoutExtension"/>
	/// would hand back the whole path, so the separators are normalised first.
	/// </summary>
	private static string BankEntryName(string bankPath) =>
		Path.GetFileNameWithoutExtension(bankPath.Replace('\\', '/'));

	/// <summary>
	/// Called once the presentations are available. The default puts
	/// <see cref="StartScreen"/> up; the viewer overrides it to honour its environment
	/// variables first.
	/// </summary>
	protected virtual void OnLoaded()
	{
		int start = StartScreen.Length == 0
			? 0
			: _screens.FindIndex(n => string.Equals(n, StartScreen, StringComparison.OrdinalIgnoreCase));
		Show(start >= 0 ? start : 0);
	}

	/// <summary>
	/// Finds the shipped game data.
	///
	/// The <see cref="GameDataRoot"/> export is an explicit override and nothing more;
	/// everything else defers to the shared convention. This used to carry its own copy of
	/// the search order, which is precisely how a viewer ends up reading a different install
	/// from the plugin and the exporter.
	/// </summary>
	private string ResolveGameDataRoot()
	{
		if (Directory.Exists(GameDataRoot))
			return GameDataRoot;

		string resolved = Engine_GameData.Resolve();
		return Directory.Exists(resolved) ? resolved : string.Empty;
	}

	/// <summary>
	/// Finds the extraction. Kept separate so the precedence is readable and so a missing
	/// root reports which candidates were tried.
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

	/// <summary>
	/// Shows a screen by name, on a given scene slide. Returns false when the
	/// presentation is not among the shipped set, or when loading has not finished.
	/// </summary>
	public bool ShowByName(string name, int sceneSlide)
	{
		SceneSlide = sceneSlide;
		return ShowByName(name);
	}

	/// <summary>Shows a screen with the guest's live per-element slides applied.</summary>
	public bool ShowByName(string name, IReadOnlyList<int>? liveSlides)
	{
		LiveSlides = liveSlides;
		return ShowByName(name);
	}

	/// <summary>
	/// Shows a screen by name. Returns false when the presentation is not among the
	/// shipped set, or when loading has not finished.
	/// </summary>
	public bool ShowByName(string name)
	{
		int index = _screens.FindIndex(
			n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
		if (index < 0 || _loadScreen is null)
			return false;

		Show(index);
		return true;
	}

	/// <summary>
	/// Builds a screen and hands it back WITHOUT mounting it, so a caller can hold several at
	/// once.
	///
	/// <see cref="Show"/> owns exactly one screen: it frees the last one before building the
	/// next. That matches a viewer flipping through presentations and does NOT match the game,
	/// where the guest keeps several standing up together — a HUD under a pause menu under a
	/// menu cycler, or a main menu over a scrolling background. Drawing one of those at a time
	/// is why the others were missing, and picking "the important one" is not available either:
	/// the guest hides what it does not want with per-element <c>globalactive</c>, so a
	/// presentation being live says nothing about whether it is showing anything.
	///
	/// Everything else is shared — the same banks, texture cache, fonts and string book — so
	/// holding five screens costs five node trees and not five mounts.
	///
	/// Set <see cref="LiveSlides"/> and friends before calling; they are read while the screen
	/// is parsed. Returns null when the name is not among the shipped presentations.
	/// </summary>
	public Node3D? BuildDetached(string name, out Engine_UiScreen? screen) =>
		BuildDetached(name, out screen, out _);

	/// <summary>
	/// As above, and reports what was actually built.
	///
	/// The counts matter because a screen whose textures did not resolve still BUILDS: the
	/// quads are there, they just draw with no albedo, which is a white rectangle. "It built"
	/// and "it looks right" are different claims and this is what separates them.
	/// </summary>
	public Node3D? BuildDetached(string name, out Engine_UiScreen? screen,
		out Engine_UiSceneBuilder? stats)
	{
		screen = null;
		stats = null;
		if (_loadScreen is null)
		{
			return null;
		}

		int index = _screens.FindIndex(
			n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
		if (index < 0)
		{
			return null;
		}

		try
		{
			screen = _loadScreen(_screens[index]);
		}
		catch (Exception ex)
		{
			Report($"{name}\nload failed: {ex.Message}");
			return null;
		}

		var builder = new Engine_UiSceneBuilder
		{
			TextureResolver = rel => Textures?.Load(rel),
			FontResolver = size => Fonts?.For(size),
		};

		Node3D built = builder.Build(screen);
		Engine_UiSceneBuilder.ApplyCameraAiming(built);
		stats = builder;
		return built;
	}

	/// <summary>Rebuilds the current screen — after the live slides have changed.</summary>
	public void Rebuild()
	{
		if (_screens.Count > 0)
			Show(CurrentIndex);
	}

	/// <summary>Builds the presentation at <paramref name="index"/> and mounts it.</summary>
	public void Show(int index)
	{
		if (_loadScreen is null || index < 0 || index >= _screens.Count)
			return;

		CurrentIndex = index;
		string name = _screens[index];

		if (Built is not null)
		{
			RemoveChild(Built);
			Built.QueueFree();
			Built = null;
		}

		Engine_UiScreen screen;
		try
		{
			screen = _loadScreen(name);
		}
		catch (Exception ex)
		{
			Report($"{name}\nload failed: {ex.Message}");
			return;
		}

		CurrentScreen = screen;

		var builder = new Engine_UiSceneBuilder
		{
			TextureResolver = rel => Textures?.Load(rel),
			FontResolver = size => Fonts?.For(size),
		};
		Built = builder.Build(screen);
		AddChild(Built);
		Engine_UiSceneBuilder.ApplyCameraAiming(Built);

		// The art a host-authored screen can borrow is whatever is standing up RIGHT NOW, so
		// the catalogue is rebuilt here rather than once at load. Cataloguing every screen up
		// front would mean building all 52 presentations to find one tab.
		Theme?.Catalogue(Built, name);

		// Building a camera is not enough — one has to be made current or the viewport
		// renders through Godot's default camera and the screen looks empty.
		Camera3D camera = Engine_UiSceneBuilder.ActivateCamera(Built, UseFramingCamera);

		OnScreenShown(screen, builder, camera);
	}

	/// <summary>
	/// Called after a screen has been built, mounted and aimed. The base reports the
	/// counts; the viewer overrides it to add its dumps.
	/// </summary>
	protected virtual void OnScreenShown(
		Engine_UiScreen screen, Engine_UiSceneBuilder builder, Camera3D camera)
	{
		Report($"[{CurrentIndex + 1}/{_screens.Count}] {screen.Name}   " +
			$"{screen.CanvasWidth}x{screen.CanvasHeight}\n" +
			$"quads {builder.QuadsBuilt} (degenerate {builder.DegenerateQuads})   " +
			$"textures {builder.TexturesResolved}   " +
			$"text {builder.TextsBuilt} ({builder.TextGlyphsBuilt} glyphs)   " +
			$"camera {camera.Name}");
	}

	/// <summary>Where progress and failures go. The viewer also writes them to its label.</summary>
	protected virtual void Report(string message) => GD.Print(message);

	public override void _ExitTree()
	{
		foreach (IDisposable owned in _owned)
			owned.Dispose();
		_owned.Clear();
	}
}
