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
/// Stands up a Fable 2 Gameface presentation and keeps it on screen — the one place a
/// <c>.bgf</c> becomes Godot nodes. Mounting the banks, the texture name table, the bitmap
/// fonts, the Babel database, parsing a screen and handing it to
/// <see cref="Engine_UiSceneBuilder"/> all live here; the viewer and the running game go
/// through it. Subclasses take the loaded resources off the protected members and the
/// <see cref="OnScreenShown"/> hook.
/// </summary>
public partial class Engine_UiPresentationHost : Node3D
{
	/// <summary>Fallback asset root, used when neither the env var nor the user data path resolves.</summary>
	[Export] public string AssetRoot { get; set; } = string.Empty;

	/// <summary>Screen to show first. Empty means whichever sorts first.</summary>
	[Export] public string StartScreen { get; set; } = string.Empty;

	/// <summary>
	/// Ignore the screen's own camera and frame the content instead — separates a layout fault
	/// from a framing one.
	/// </summary>
	[Export] public bool UseFramingCamera { get; set; }

	/// <summary>The shipped <c>data</c> directory to stream banks from.</summary>
	[Export(PropertyHint.GlobalDir)] public string GameDataRoot { get; set; } = string.Empty;

	/// <summary>Screen names, in display order. How one is fetched is <see cref="_loadScreen"/>'s job.</summary>
	private readonly List<string> _screens = new();

	/// <summary>Set by whichever start path won — banks or an extracted tree.</summary>
	private Func<string, Engine_UiScreen>? _loadScreen;

	private readonly List<IDisposable> _owned = new();
	private Engine_TextureNameTable? _names;

	/// <summary>
	/// The hash-to-path table the screens were built with, for a caller holding a texture HASH
	/// from the guest rather than a path — the load-game card portraits, for one.
	/// </summary>
	public Engine_TextureNameTable? TextureNames => _names;

	/// <summary>
	/// Loads a texture by its game-relative path through the same cache the screens use — for a
	/// caller resolving a texture the file does not name. Never open a second cache.
	/// </summary>
	public Texture2D? LoadTexture(string relativePath) => Textures?.Load(relativePath);

	/// <summary>Where the presentations came from, for diagnostics.</summary>
	protected string SourceRoot = string.Empty;

	/// <summary>The texture cache the built quads sample through. Null until loading succeeds.</summary>
	protected Engine_UiTextureCache? Textures { get; private set; }

	/// <summary>The bitmap fonts text elements are built with. Null until loading succeeds.</summary>
	public Engine_UiFontLibrary? Fonts { get; private set; }

	/// <summary>
	/// The localised string database, for anything holding a TAG but no presentation — a
	/// Godot-owned menu keeps its tags in the model, not in a <c>.bgf</c>. Never load
	/// <c>book.babel</c> a second time.
	/// </summary>
	public Engine_BabelDatabase? Strings { get; private set; }

	/// <summary>
	/// The shipped look, for anything drawn alongside the presentation rather than out of it.
	/// Re-catalogued on every <see cref="Show"/>, so its art comes from the presentation
	/// currently up. Null until loading succeeds.
	/// </summary>
	public Engine_UiTheme? Theme { get; private set; }

	/// <summary>
	/// Art as the FILE authors it, with nothing the guest has done applied — the pattern book a
	/// host-authored screen clones from. <see cref="Theme"/> is the other catalogue: the live
	/// screen, carrying the guest's slides, visibility, transforms and opacity. Cloning from
	/// that one picks up `ApplyLiveOpacity`'s baked result, so a row the guest never faded in
	/// comes out invisible. Built once per screen NAME and kept out of the scene tree.
	/// </summary>
	public Engine_UiTheme? Templates { get; private set; }

	private readonly Dictionary<string, Node3D> _templateTrees = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>The node tree currently mounted, or null before the first screen is shown.</summary>
	protected Node3D? Built { get; private set; }

	/// <summary>
	/// The mounted tree, for a caller that owns this screen and needs the mirror out of the way.
	/// The tree is still built — it is where the live art catalogue comes from and where owned
	/// views take their placement (`group.GlobalTransform`); an owner takes away only the
	/// DRAWING, restated every frame (see <c>GameUi.DriveOwnedBook</c>).
	/// </summary>
	public Node3D? MirrorTree => Built;

	/// <summary>The presentation currently built, for diagnostics.</summary>
	public Engine_UiScreen? CurrentScreen { get; private set; }

	/// <summary>Index into <see cref="ScreenNames"/> of the presentation on screen.</summary>
	public int CurrentIndex { get; private set; }

	/// <summary>The presentations available, once loading has finished.</summary>
	public IReadOnlyList<string> ScreenNames => _screens;

	/// <summary>True once the banks or the extracted tree opened and a screen can be shown.</summary>
	public bool IsLoaded => _loadScreen is not null;

	/// <summary>
	/// Which scene-level slide to build, 1-based, or 0 for the preview heuristic. Front-end
	/// screens are one page per slide (<see cref="Engine_BgfDocument.SetSceneSlide"/>). Takes
	/// effect on the next <see cref="ShowByName(string,int)"/> — overrides resolve at build.
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
	/// Per-element <c>globalactive</c> read from the running guest, by element ordinal — the
	/// guest's own draw gate. When present it REPLACES the slide-derived visibility, which can
	/// only reproduce what the file authored.
	/// </summary>
	public IReadOnlyList<bool>? LiveVisible { get; set; }

	/// <summary>
	/// Per-element live TRANSFORM read from the guest, by element ordinal:
	/// <c>{position.x, position.y, position.z, scale.x, scale.y, scale.z}</c>, any of which
	/// may be null when the guest holds no such attribute on that element.
	///
	/// Without this everything laid out at runtime collapses onto one spot — a menu's twelve
	/// row components are authored at the same place and moved apart by script.
	/// </summary>
	public IReadOnlyList<float?[]?>? LiveTransform { get; set; }

	/// <summary>
	/// Per-element live TEXTURE HASH read from the guest, by element ordinal; 0 where the
	/// guest states none.
	///
	/// The file's texture is a placeholder wherever the game picks the picture at runtime — the
	/// dpad quick-slots are all authored on a grey `empty.tex`, the HUD's twenty-four world-icon
	/// slots all on one icon.
	/// </summary>
	public IReadOnlyList<uint>? LiveTextures { get; set; }

	/// <summary>
	/// The guest's <c>TintingAmount</c> per element, or null where it states none — the WEIGHT of
	/// the vertex tint, not the tint. The guest subtracts <c>(1 - tint)</c> scaled by it rather
	/// than multiplying by the colour; it starts at zero on most quads.
	/// </summary>
	public IReadOnlyList<Engine_UiScreen.LiveTint?>? LiveTint { get; set; }

	/// <summary>
	/// The camera the current screen renders through, so a window resize can re-fit it. A canvas
	/// camera is sized against the window's ASPECT — see
	/// <see cref="Engine_UiSceneBuilder.FitCanvasCamera"/>.
	/// </summary>
	private Camera3D? _canvasCamera;

	private Vector2 _fittedTo;

	/// <summary>Re-fits the current camera when the viewport has changed size.</summary>
	protected void FitToViewport()
	{
		if (_canvasCamera is null || !IsInsideTree() ||
			!GodotObject.IsInstanceValid(_canvasCamera))
		{
			// The previous screen's camera is disposed with its tree and Show reassigns
			// _canvasCamera; touching the stale reference throws ObjectDisposedException.
			return;
		}

		Vector2 size = GetViewport().GetVisibleRect().Size;
		if (size == _fittedTo)
		{
			return;
		}

		_fittedTo = size;

		// The framing camera is fitted to the tree's bounds for a screen that ships no camera;
		// re-sizing it against the design box would throw that framing away.
		if (_canvasCamera.Name == "CanvasCamera")
		{
			Engine_UiSceneBuilder.FitCanvasCamera(_canvasCamera, size);
		}
	}

	public override void _Process(double delta) => FitToViewport();

	public override void _Ready()
	{
		// Streaming from the shipped banks is the real path; the extracted-tree branch below is
		// a fallback for a checkout without the game data.
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

		// One mount for everything: the bank filesystem indexes every bank under the game
		// directory and stitches the header/payload halves a .tex is split across.
		BnkFileSystem banks;
		try
		{
			banks = BnkFileSystem.Mount(data);
		}
		catch (Exception ex)
		{
			// A mount that is present but unreadable is surfaced rather than silently falling
			// through to the extracted tree.
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
				LiveText, LiveOpacity, LiveVisible, LiveTransform, LiveTextures, LiveTint);
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
	/// Loads the bitmap fonts and the localised string database. Both are LOOSE FILES under
	/// <c>language/</c>, not bank entries, so they come from the data root. Neither is fatal:
	/// without Babel the authored tag displays verbatim, as the engine falls back too.
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

		// Built on every path out of here, including the two failures above: a theme with no
		// string book still carries the fonts and the art.
		Theme = new Engine_UiTheme { Fonts = Fonts, Strings = Strings, Textures = LoadTexture };
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
	/// Finds the shipped game data. <see cref="GameDataRoot"/> is an explicit override;
	/// everything else defers to <see cref="Engine_GameData.Resolve"/> — never a second copy of
	/// the search order.
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
	/// once — the guest keeps several standing together, and it hides what it does not want with
	/// per-element <c>globalactive</c>, so a presentation being live says nothing about whether
	/// it is showing anything. <see cref="Show"/> owns exactly one screen instead. Banks, texture
	/// cache, fonts and string book are shared. Set <see cref="LiveSlides"/> and friends first;
	/// they are read while the screen is parsed. Null when the name is not shipped.
	/// </summary>
	public Node3D? BuildDetached(string name, out Engine_UiScreen? screen) =>
		BuildDetached(name, out screen, out _);

	/// <summary>
	/// As above, and reports what was built. A screen whose textures did not resolve still
	/// builds — the quads draw with no albedo — so the counts are what separate the two.
	/// </summary>
	public Node3D? BuildDetached(string name, out Engine_UiScreen? screen,
		out Engine_UiSceneBuilder? stats, bool keepInactiveForCatalogue = false)
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

			// The CATALOGUE build keeps slide-parked art, hidden and marked; a drawn build
			// never does — see Engine_UiSceneBuilder.KeepInactiveForCatalogue.
			KeepInactiveForCatalogue = keepInactiveForCatalogue,
		};

		Node3D built = builder.Build(screen);
		Engine_UiSceneBuilder.ApplyCameraAiming(built);
		stats = builder;
		return built;
	}

	/// <summary>
	/// Takes the mounted screen down and forgets it. <see cref="Show"/> frees the old screen
	/// only on its way to a new one, so a guest leaving the front end needs this. The art
	/// catalogue goes with it — every entry points into the tree being freed. Clones already
	/// taken out of it are unaffected.
	/// </summary>
	public void ClearScreen()
	{
		if (Built is null)
		{
			return;
		}

		RemoveChild(Built);
		Built.QueueFree();
		Built = null;
		CurrentScreen = null;
		Theme?.Clear();
	}

	/// <summary>Rebuilds the current screen — after the live slides have changed.</summary>
	public void Rebuild()
	{
		if (_screens.Count > 0)
			Show(CurrentIndex);
	}

	/// <summary>
	/// Element names hidden after every rebuild — the guest widgets a host-owned stand-in
	/// replaces (menu rows, its highlight). Registered by the stand-in's owner; cleared when
	/// the stand-in goes away.
	/// </summary>
	public readonly System.Collections.Generic.HashSet<string> SuppressedElementNames = new();

	private void ApplySuppressions(Node root)
	{
		if (SuppressedElementNames.Count == 0)
		{
			return;
		}

		int hidden = 0;
		void Walk(Node node)
		{
			if (node is Node3D spatial && SuppressedElementNames.Contains(node.Name))
			{
				if (spatial.Visible)
				{
					spatial.Visible = false;
					++hidden;
				}

				return;  // the subtree goes with its root
			}

			foreach (Node child in node.GetChildren())
			{
				Walk(child);
			}
		}

		Walk(root);
		if (hidden != 0)
		{
			GD.Print($"[game-ui] suppressed {hidden} guest element instance(s) after rebuild " +
				$"({string.Join(", ", SuppressedElementNames)})");
		}
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

		// On EVERY rebuild: the tree is torn down per guest state change, so a suppression done
		// once from outside comes back visible. The stand-in's owner registers the name once.
		ApplySuppressions(Built);

		// The catalogue describes whatever is standing up now, so it is rebuilt here rather than
		// once at load — cataloguing up front would build all 52 presentations.
		Theme?.Catalogue(Built, name);
		CatalogueTemplates(name);

		// A camera has to be made CURRENT or the viewport renders through Godot's default one.
		Camera3D camera = Engine_UiSceneBuilder.ActivateCamera(Built, UseFramingCamera);
		_canvasCamera = camera;
		FitToViewport();

		// What the frame engine did, per screen. Its two failure modes look identical on screen:
		// no frames found is a detection miss; frames with no border slices means the screen
		// expects the original's runtime cloning step.
		Engine_UiFrameDriver.Report layout = screen.FrameLayout;
		if (layout.Frames > 0)
		{
			GD.Print($"[ui-frame] {name}: {layout.Frames} frame(s), " +
				$"{layout.Contents} item(s) placed, {layout.Slots} slice(s), " +
				$"{layout.Blocks} block(s)" +
				(layout.WithoutBorder > 0
					? $", {layout.WithoutBorder} with no border elements to drive"
					: string.Empty));
		}

		OnScreenShown(screen, builder, camera);
	}

	/// <summary>
	/// Catalogues <see cref="Templates"/> from a FILE-ONLY build of a screen: the live inputs are
	/// withheld for the duration and restored after, so the same parser and builder produce the
	/// artist's version. Built once per name — the pattern book does not change per rebuild.
	/// </summary>
	private void CatalogueTemplates(string name)
	{
		Node3D? tree = TemplateTree(name);
		if (tree is null)
		{
			return;
		}

		Templates ??= new Engine_UiTheme { Fonts = Fonts, Strings = Strings, Textures = LoadTexture };
		Templates.Catalogue(tree, name);
	}

	/// <summary>
	/// A CLONE of a screen's whole file-only build, for a host that owns a small presentation
	/// outright — the pause banner, whose element names are synthesized so the by-name art
	/// catalogue cannot address its pieces. The clone shares the template's materials, which
	/// is safe while the template itself is never drawn.
	/// </summary>
	public Node3D? CloneTemplateTree(string name) =>
		TemplateTree(name) is { } tree ? (Node3D)tree.Duplicate() : null;

	/// <summary>
	/// A pattern book for ONE screen, as its own catalogue. <see cref="Templates"/> is
	/// re-catalogued per shown screen; a host-owned panel needing art from a screen that is never
	/// shown by name takes it from here instead of clobbering that book.
	/// </summary>
	public Engine_UiTheme? PatternBookFor(string name)
	{
		Node3D? tree = TemplateTree(name);
		if (tree is null)
		{
			return null;
		}

		var book = new Engine_UiTheme { Fonts = Fonts, Strings = Strings, Textures = LoadTexture };
		book.Catalogue(tree, name);
		return book;
	}

	/// <summary>
	/// The parsed DOCUMENT of a screen, from the same file-only build the pattern book uses —
	/// for a host-owned panel that joins live state read off the bridge by ordinal, and needs
	/// the authored structure (names, ordinals, element count) to do it. Built (and cached)
	/// with the live inputs withheld, so a stale live array from the last mirrored screen can
	/// never leak into the parse.
	/// </summary>
	public Engine_UiScreen? DocumentFor(string name)
	{
		TemplateTree(name);
		return _templateScreens.TryGetValue(name, out Engine_UiScreen? screen) ? screen : null;
	}

	private readonly Dictionary<string, Engine_UiScreen> _templateScreens =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>The cached FILE-ONLY build of a screen — the artist's version.</summary>
	private Node3D? TemplateTree(string name)
	{
		if (!_templateTrees.TryGetValue(name, out Node3D? tree))
		{
			// A pattern book is a whole detached build with inactive subtrees KEPT (the world map
			// is 4125 elements), built synchronously on the frame the screen is first shown.
			long began = System.Diagnostics.Stopwatch.GetTimestamp();
			IReadOnlyList<int>? slides = LiveSlides;
			IReadOnlyList<string?>? text = LiveText;
			IReadOnlyList<float?>? opacity = LiveOpacity;
			IReadOnlyList<bool>? visible = LiveVisible;
			IReadOnlyList<float?[]?>? transform = LiveTransform;
			IReadOnlyList<uint>? textures = LiveTextures;
			IReadOnlyList<Engine_UiScreen.LiveTint?>? tint = LiveTint;
			int sceneSlide = SceneSlide;

			try
			{
				LiveSlides = null;
				LiveText = null;
				LiveOpacity = null;
				LiveVisible = null;
				LiveTransform = null;
				LiveTextures = null;
				LiveTint = null;

				// The scene slide goes too: a screen is built on ONE page and the builder drops
				// an inactive element's whole subtree, so cataloguing on the guest's current page
				// throws away the components a host-owned screen borrows. Zero is the preview
				// heuristic — each owner's first real slide.
				SceneSlide = 0;
				tree = BuildDetached(name, out Engine_UiScreen? screen, out _,
					keepInactiveForCatalogue: true);
				if (screen is not null)
				{
					_templateScreens[name] = screen;
				}
			}
			finally
			{
				LiveSlides = slides;
				LiveText = text;
				LiveOpacity = opacity;
				LiveVisible = visible;
				LiveTransform = transform;
				LiveTextures = textures;
				LiveTint = tint;
				SceneSlide = sceneSlide;
			}

			if (tree is null)
			{
				return null;
			}

			_templateTrees[name] = tree;

			double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0
				/ System.Diagnostics.Stopwatch.Frequency;
			int elements = _templateScreens.TryGetValue(name, out Engine_UiScreen? built)
				? built.Nodes.Length
				: 0;
			Report($"[ui-host] pattern book '{name}' built in {ms:0.0}ms — {elements} element(s); " +
				$"{_templateTrees.Count} book(s) now resident, never released until the host " +
				"leaves the tree");
		}

		return tree;
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
		// The template trees were never added to the scene, so nothing else will free them.
		foreach (Node3D tree in _templateTrees.Values)
			tree.QueueFree();
		_templateTrees.Clear();
		_templateScreens.Clear();

		foreach (IDisposable owned in _owned)
			owned.Dispose();
		_owned.Clear();
	}
}
