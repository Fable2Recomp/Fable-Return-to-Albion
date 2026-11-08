using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Godot;
using Refii.Launcher.Core;
using Refii.Launcher.Native;
using Refii.Launcher.Updates;

namespace Refii.Launcher;

/// <summary>Shared desktop and Android launcher presentation with platform-specific install and Play backends.</summary>
[Tool]
public partial class LauncherApp : Control
{
	private const float ArtCreditHeight = 26.0f;

	private static readonly string[] DebugTabs =
	{
		"Settings", "Modding", "Install", "Saves",
	};
	private static readonly string[] FileMenuItems =
	{
		"Set Install Folder", "Uninstall", "", "Show Announcements", "Show Debug Panel", "Close",
	};
	/// <summary>The About menu's entries every title shares, after the title's own.</summary>
	private static readonly LauncherLink[] SharedAboutLinks =
	{
		new("RexGlue", "https://github.com/rexglue/rexglue-sdk"),
	};

	/// <summary>The active title's screen (<see cref="LauncherTitleScreen.Active"/>), as last applied.</summary>
	private LauncherTitleScreen _screen = null!;

	/// <summary>The info drawer's tabs on the active title's screen.</summary>
	private IReadOnlyList<LauncherInfoTab> _infoTabList = Array.Empty<LauncherInfoTab>();

	/// <summary>The About menu's entries on the active title's screen, in menu order.</summary>
	private readonly List<LauncherLink> _aboutLinks = new();

	private bool _commitsLoading = true;
	private bool _commitsLoaded;
	private List<GitCommit> _commits = new();

	private bool _contributorsLoading = true;
	private bool _contributorsLoaded;
	private List<GitContributor> _contributors = new();

	/// <summary>
	/// Everything derived from the player's game — data, game code, classic shaders, modernized
	/// assets — runs through this one runner; the UI reads its label, status and fraction.
	/// </summary>
	private readonly Install.InstallPipeline _pipeline = new();
	private ManagedDownloadProgress? _downloadProgress;

	private readonly List<string> _consoleLines = new();

	private Patches _patches = null!;
	private LaunchController _launch = null!;

	/// <summary>
	/// The launcher's window mode before Play minimised it, restored when the game exits.
	/// Never remembers "minimised": that would leave the user with no window and no way back.
	/// </summary>
	private DisplayServer.WindowMode _launcherModeBeforePlay = DisplayServer.WindowMode.Windowed;

	/// <summary>Whether the launcher is currently stood down for a running game.</summary>
	private bool _standingDownForGame;
	private UpdateChecker _updates = null!;
	private Dictionary<string, string> _latestVersions = new();
	private Font? _font;

	private SmartStyle _menuStyle = null!;
	private SmartStyle _warningStyle = null!;
	private SmartStyle _buttonStyle = null!;
	private SmartStyle _infoStyle = null!;
	private SmartStyle _patchesStyle = null!;
	private SmartStyle _baseStyle = null!;

	private TextureRect _background = null!;
	private TextureRect _backgroundNext = null!;
	private BackgroundManager _backgrounds = null!;
	private Panel _menuBar = null!;
	private VBoxContainer _headerCol = null!;
	private RichTextLabel _wordmark = null!;
	private MenuButton _gameMenu = null!;
	private MenuButton _fileMenu = null!;
	private MenuButton _aboutMenu = null!;
	private Panel _warningPanel = null!;
	private Label _warningLabel = null!;
	private Panel _actionPanel = null!;
	private Button _primaryButton = null!;
	private HBoxContainer? _buildControls;
	private Button? _pauseBuildButton;
	private Button? _stopBuildButton;
	private TextureRect _progressTrack = null!;
	private Control _progressClip = null!;
	private TextureRect _progressFill = null!;
	private Label _artCredit = null!;
	private Panel _infoPanel = null!;
	private Button _drawerClose = null!;
	private Panel _settingsDrawer = null!;
	private Button _settingsClose = null!;
	private Label _settingsTitle = null!;
	private TabBar _settingsTabs = null!;
	private Button _infoTab = null!;
	private Button _settingsTab = null!;
	private Label _infoTitle = null!;
	private TabBar _infoTabs = null!;
	private RichTextLabel _infoBody = null!;
	private ScrollContainer _bodyScroll = null!;
	private SettingsPanel _settingsPanel = null!;
	private ModdingPanel _moddingPanel = null!;
	private ReinstallPanel _reinstallPanel = null!;
	private SavesPanel _savesPanel = null!;
	private float _menuBarHeight = 72.0f;

	/// <summary>
	/// The two ways the whole app can end without a crash record: the window manager closing the
	/// launcher's own window (auto-accepted quit) and a tree quit. Named in the log so a "closed
	/// to desktop" with a clean tail is not mistaken for a fault.
	/// </summary>
	public override void _Notification(int what)
	{
		if (what == NotificationWMCloseRequest)
		{
			GD.PrintErr("[launcher] WM close request on the launcher window — the app is quitting");
		}
		else if (what == NotificationPredelete)
		{
			GD.PrintErr("[launcher] the launcher root is being deleted — the app is quitting");

			// The game child ends here, after its own teardown and before the engine's display
			// unload; in the launcher this returns at once. See GameExit.EndChildProcess.
			Refii.Game.GameExit.EndChildProcess();
		}
	}

	/// <summary>Initializes the shared launcher with the backend appropriate to the exported platform.</summary>
	public override void _Ready()
	{
		if (!Engine.IsEditorHint() && Refii.Core.Engine_Platform.IsAndroid) { ReadyAndroid(); return; }
		CrashReporting.Install();
		LauncherPaths.OverrideLauncherDirectory = ResolveLauncherDirectory();
		Platform.RuntimeDirectoryOverride = ResolveRuntimeDirectory();

		// The recompiled game module has to land where the runtime looks for it, which is
		// beside the other native libraries — the per-architecture staging root, the one
		// location that survives an export. Same convention Engine_TexEnhance uses for the
		// upscaler weights.
		Refii.Core.Engine_AppRoot.EnsureLayout();
		Recompile.GameCodeBuild.BinDirectory = Refii.Core.Engine_AppRoot.BinDirectory;

		// THIS PROCESS IS THE ELEVATED RELAY, headless: it starts the game as Administrator and
		// exits with it (see GameElevatedLaunch). Nothing of the launcher is built here either.
		if (Refii.Game.GameElevatedLaunch.IsRelay)
		{
			foreach (Node child in GetChildren())
			{
				child.QueueFree();
			}

			Refii.Game.GameElevatedLaunch.RunRelay(this);
			return;
		}

		// THIS PROCESS IS THE GAME, not the launcher. Spawned by the launcher's Play with the
		// marker set — see GameChildProcess for why the game cannot simply run in-process.
		// Nothing below this point is built: no config, no theme, no panels, no update check.
		// The launcher's own window stays hidden; the game opens its own.
		if (Refii.Game.GameChildProcess.IsChild)
		{
			// THE SCENE IS ALREADY BUILT BY THE TIME `_Ready` RUNS, so returning early is not
			// enough — every launcher control in `main.tscn` still exists and still draws, and
			// the child came up showing a second, half-configured launcher beside the game.
			// Emptying this node leaves the main window blank.
			foreach (Node child in GetChildren())
			{
				child.QueueFree();
			}

			// DEFERRED, because minimising during `_Ready` is too early: the window manager has
			// not finished mapping the window and the request is dropped, which is why the
			// child's window stayed on screen.
			Callable.From(() => DisplayServer.WindowSetMode(
				DisplayServer.WindowMode.Minimized,
				(int)DisplayServer.MainWindowId)).CallDeferred();

			Refii.Game.GameWindow.Open(this);
			return;
		}

		LoadLauncherConfig();
		LoadActiveTitle();
		LauncherTheme.Load();

		// The mods that ship with the app, packed into each title's mods folder before the Mods
		// panel first lists it. Installed switched off; never over a copy someone has edited. A
		// title's shipped mods go to that title's folder whichever title is active; a title that
		// ships none has no folder to read.
		foreach (Refii.Core.Engine_Title title in Refii.Core.Engine_Title.All)
		{
			foreach (string line in Refii.Mods.ModBundled.Install(
				Refii.Core.Engine_AppRoot.BundledModsDirectoryFor(title),
				Refii.Mods.ModWorkspace.GetDefaultModsRoot(title.AssetsDirectory),
				title.ModExtension))
			{
				GD.Print("[mods] bundled " + line);
			}
		}

		_patches = new Patches();
		_launch = new LaunchController(_patches);
		_updates = new UpdateChecker();
		InitPatches();

		BindNodes();

		// NOT mounted here. The launcher shows a change log, settings and a Play button —
		// none of which reads a single game asset. Mounting cost 123,710 files and a
		// 200-texture decode on every startup, before the user had asked for anything.
		//
		// The two things that DO need the assets mount for themselves, when they start:
		// the editor (through the bnk_mount addon) and the game session.
		_font = LoadLauncherFont();
		BuildStyles();
		ApplyStyles();
		WireMenus();
		WireTabs();

		if (!Engine.IsEditorHint())
		{
			DisplayServer.WindowSetTitle(_screen.WindowTitle);
			ApplyWindowIcon();
			WindowEffects.Apply();
			CallDeferred(MethodName.ReapplyWindowTitle);
		}

		Resized += ApplyLayout;
		ApplyLayout();
		RefreshState();
		CallDeferred(MethodName.PlayEntrance);

		if (!Engine.IsEditorHint())
		{
			FetchGitHubData();
			StartUpdateCheck("Update check: ");
		}
	}
	private void BindNodes()
	{
		_background = GetNode<TextureRect>("Background");
		_backgroundNext = GetNode<TextureRect>("BackgroundNext");
		_menuBar = GetNode<Panel>("MenuBar");
		_headerCol = GetNode<VBoxContainer>("MenuBar/HeaderCol");
		_wordmark = GetNode<RichTextLabel>("MenuBar/HeaderCol/Wordmark");
		_fileMenu = GetNode<MenuButton>("MenuBar/HeaderCol/MenuRow/FileMenu");
		_aboutMenu = GetNode<MenuButton>("MenuBar/HeaderCol/MenuRow/AboutMenu");

		// THE GAME SWITCH, first in the row: which title everything else on the page is about.
		_gameMenu = GetNode<MenuButton>("MenuBar/HeaderCol/MenuRow/GameMenu");
		_warningPanel = GetNode<Panel>("WarningPanel");
		_warningLabel = GetNode<Label>("WarningPanel/WarningLabel");
		_actionPanel = GetNode<Panel>("ActionPanel");
		_primaryButton = GetNode<Button>("ActionPanel/PrimaryButton");

		_progressTrack = GetNode<TextureRect>("ActionPanel/ProgressTrack");
		_progressClip = GetNode<Control>("ActionPanel/ProgressClip");
		_progressFill = GetNode<TextureRect>("ActionPanel/ProgressClip/ProgressFill");
		_artCredit = GetNode<Label>("ArtCredit");
		_infoPanel = GetNode<Panel>("InfoPanel");
		_drawerClose = GetNode<Button>("InfoPanel/DrawerClose");
		_settingsDrawer = GetNode<Panel>("SettingsDrawer");
		_settingsClose = GetNode<Button>("SettingsDrawer/SettingsClose");
		_settingsTitle = GetNode<Label>("SettingsDrawer/SettingsColumn/SettingsTitle");
		_settingsTabs = GetNode<TabBar>("SettingsDrawer/SettingsColumn/SettingsTabs");
		_infoTab = GetNode<Button>("InfoTab");
		_settingsTab = GetNode<Button>("SettingsTab");
		_infoTitle = GetNode<Label>("InfoPanel/InfoColumn/InfoTitle");
		_infoTabs = GetNode<TabBar>("InfoPanel/InfoColumn/InfoTabs");
		_infoBody = GetNode<RichTextLabel>("InfoPanel/InfoColumn/BodyScroll/InfoBody");
		_bodyScroll = GetNode<ScrollContainer>("InfoPanel/InfoColumn/BodyScroll");
		_settingsPanel = GetNode<SettingsPanel>("SettingsDrawer/SettingsColumn/SettingsPanel");
		_moddingPanel = GetNode<ModdingPanel>("SettingsDrawer/SettingsColumn/ModdingPanel");
		_reinstallPanel = GetNode<ReinstallPanel>("SettingsDrawer/SettingsColumn/ReinstallPanel");
		_savesPanel = new SavesPanel
		{
			Name = "SavesPanel", Visible = false,
			SizeFlagsHorizontal = _reinstallPanel.SizeFlagsHorizontal, SizeFlagsVertical = _reinstallPanel.SizeFlagsVertical,
		};
		_reinstallPanel.AddSibling(_savesPanel);
	}

	private static bool IsExportedBuild() => OS.HasFeature("template");

	private static string GetHostExecutableDirectory() =>
		Path.GetDirectoryName(OS.GetExecutablePath())?.TrimEnd(Path.DirectorySeparatorChar)
		?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

	private static string ResolveLauncherDirectory()
	{
		string? explicitDir = System.Environment.GetEnvironmentVariable("REFII_LAUNCHER_DIR");
		if (!string.IsNullOrEmpty(explicitDir))
		{
			return explicitDir!.TrimEnd(Path.DirectorySeparatorChar);
		}

		// One anchor for the whole app. The heuristics that used to live here — probe
		// Godot/build for banks, else the repo root, else the managed assembly directory —
		// each answered "where is the app" differently from the guest and from the native
		// library search, which is how the install, the config and the runtime ended up in
		// three separate trees.
		return Refii.Core.Engine_AppRoot.Path;
	}

	private void MountGameAssets()
	{
		Refii.Assets.Engine_GameAssets? assets = Refii.Assets.Engine_GameAssets.Instance;
		if (assets is null)
		{
			return;
		}

		if (!_gameAssetsSignalsBound)
		{
			_gameAssetsSignalsBound = true;
			assets.Mounted += count =>
			{
				Log($"Game assets mounted: {count:N0} files.");
				VerifyTextureLoad(assets);
			};
			assets.MountFailed += reason => Log($"Game assets mount failed: {reason}");
		}

		// Do not mount when the game is not installed. Mounting nothing is not harmless:
		// it reports "0 files" as though the mount worked, which reads as a broken
		// reader rather than a missing install, and every consumer downstream then
		// behaves as if it has assets.
		string directory = Installer.ResolveGameDataDirectory();
		if (directory.Length == 0)
		{
			Log("Game assets not mounted: no install found. Install the game first.");
			return;
		}

		assets.Mount(directory);
	}

	private bool _gameAssetsSignalsBound;

	private void VerifyTextureLoad(Refii.Assets.Engine_GameAssets assets)
	{
		int attempted = 0;
		int loaded = 0;
		foreach (Refii.Bnk.BnkFile file in assets.EnumerateAllFiles())
		{
			if (!file.VirtualPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			++attempted;
			byte[]? raw = assets.ReadAllBytes(file.VirtualPath);
			if (raw is null)
			{
				continue;
			}

			Image? image = Refii.Textures.Engine_TexImage.TryCreateImage(raw, out _);
			if (image is not null)
			{
				++loaded;
				image.ClearMipmaps();
				image.Decompress();
				image.Resize(16, 16, Image.Interpolation.Bilinear);
				if (loaded <= 4)
				{
					Log($"  thumbnail ok {image.GetWidth()}x{image.GetHeight()} <- {file.VirtualPath}");
				}
			}

			if (attempted >= 200)
			{
				break;
			}
		}

		Log($"ResourceLoader.Load as Texture2D: {loaded}/{attempted} of first 200 .tex");
	}

	private static string? ResolveRuntimeDirectory()
	{
		string? explicitDir = System.Environment.GetEnvironmentVariable("REFII_RUNTIME_DIR");
		if (!string.IsNullOrEmpty(explicitDir))
		{
			return explicitDir!.TrimEnd(Path.DirectorySeparatorChar);
		}

		if (IsExportedBuild())
		{
			return null;
		}

		string projectDir = ProjectSettings.GlobalizePath("res://");
		string repoRoot = Path.GetFullPath(Path.Combine(projectDir, ".."));

		string staged = Path.Combine(projectDir, "build");
		if (File.Exists(Path.Combine(staged, Platform.ExecutableName)))
		{
			return staged.TrimEnd(Path.DirectorySeparatorChar);
		}

		// Every build output, not a hardcoded config list. The configurations differ by
		// platform — win-amd64-release/RelWithDebInfo on Windows, linux-amd64-debug/bin
		// here — and a fixed list simply never matches on the other one, which is how
		// the launcher came to report a missing runtime on Linux regardless of what was
		// actually built.
		string builds = Path.Combine(repoRoot, "out", "build");
		if (Directory.Exists(builds))
		{
			foreach (string config in Directory.EnumerateDirectories(builds))
			{
				foreach (string sub in new[]
				{
					"bin",
					"",
					Path.Combine("RelWithDebInfo", "bin"),
					Path.Combine("Release", "bin"),
					Path.Combine("Debug", "bin"),
					"RelWithDebInfo",
					"Release",
					"Debug",
				})
				{
					string candidate = sub.Length == 0 ? config : Path.Combine(config, sub);
					if (File.Exists(Path.Combine(candidate, Platform.ExecutableName)))
					{
						return candidate.TrimEnd(Path.DirectorySeparatorChar);
					}
				}
			}
		}

		return null;
	}

	private const string BundledConfig = "res://assets/launcher/config.cfg";

	private void LoadLauncherConfig()
	{
		string writePath = Path.Combine(LauncherPaths.GetLauncherDataDirectory(), "config.cfg");

		if (File.Exists(writePath))
		{
			Ini.Load(writePath);
			return;
		}

		if (Godot.FileAccess.FileExists(BundledConfig))
		{
			using Godot.FileAccess file =
				Godot.FileAccess.Open(BundledConfig, Godot.FileAccess.ModeFlags.Read);
			if (file is not null)
			{
				Ini.LoadFromText(file.GetAsText(), writePath);
				return;
			}
		}

		Ini.Load(writePath);
	}

	private const string TitleSection = "Launcher";
	private const string ActiveTitleKey = "ActiveTitle";

	/// <summary>
	/// The game the launcher installs, builds and plays, as the switch last left it. Read before
	/// anything resolves an install path, because every path is the active title's.
	/// </summary>
	private static void LoadActiveTitle()
	{
		Refii.Core.Engine_Title.Active = Refii.Core.Engine_Title.Find(
			Ini.GetString(ActiveTitleKey, Refii.Core.Engine_Title.Fable2.Key, TitleSection))
			?? Refii.Core.Engine_Title.Fable2;
	}

	private void ReapplyWindowTitle()
	{
		DisplayServer.WindowSetTitle(_screen.WindowTitle);
	}

	private static void ApplyWindowIcon()
	{
		string external = LauncherPaths.GetLauncherAssetPath("icon.png");
		if (File.Exists(external))
		{
			Image external_image = Image.LoadFromFile(external);
			if (external_image is not null)
			{
				DisplayServer.SetIcon(external_image);
				return;
			}
		}

		var bundled = ResourceLoader.Load<Texture2D>("res://assets/launcher/icon.png");
		Image? image = bundled?.GetImage();
		if (image is not null)
		{
			DisplayServer.SetIcon(image);
		}
	}

	private Font? LoadLauncherFont()
	{
		Font? baseFont = ResourceLoader.Load<Font>("res://assets/launcher/fable2.ttf");
		if (baseFont is null)
		{
			string external = LauncherPaths.GetLauncherAssetPath("fable2.ttf");
			if (File.Exists(external))
			{
				var fileFont = new FontFile();
				if (fileFont.LoadDynamicFont(external) == Error.Ok)
				{
					baseFont = fileFont;
				}
			}
		}

		if (baseFont is null)
		{
			return null;
		}

		// Subtle glyph tracking makes the display face read as crafted at title sizes.
		return new FontVariation { BaseFont = baseFont, SpacingGlyph = 1 };
	}

	private void BuildStyles()
	{
		_menuStyle = SmartStyle.FromTheme(LauncherTheme.MenuBarForeground, LauncherTheme.MenuBarBackground);
		_warningStyle = SmartStyle.FromTheme(LauncherTheme.WarningForeground, LauncherTheme.WarningBackground);
		_buttonStyle = SmartStyle.FromTheme(LauncherTheme.LaunchButtonForeground, LauncherTheme.LaunchButtonBackground);
		_infoStyle = SmartStyle.FromTheme(LauncherTheme.ChangeLogForeground, LauncherTheme.ChangeLogBackground);
		_patchesStyle = SmartStyle.FromTheme(LauncherTheme.PatchesForeground, LauncherTheme.PatchesBackground);
		_baseStyle = SmartStyle.FromTheme(LauncherTheme.BaseForeground, LauncherTheme.BaseBackground);
	}

	private const float ButtonFadeFraction = 0.25f;

	private static readonly float[] EdgeFadeOffsets =
		{ 0.0f, ButtonFadeFraction, 1.0f - ButtonFadeFraction, 1.0f };

	private static readonly float[] EdgeFadeAlphas = { 0.0f, 1.0f, 1.0f, 0.0f };

	private static GradientTexture2D FadeTexture(
		Color color, float[] offsets, float[] alphas)
	{
		var colors = new Color[offsets.Length];
		for (int index = 0; index < offsets.Length; index++)
		{
			colors[index] = new Color(color, color.A * alphas[index]);
		}

		var texture = new GradientTexture2D
		{
			Gradient = new Gradient { Offsets = offsets, Colors = colors },
			Width = 256,
			Height = 4,
			Fill = GradientTexture2D.FillEnum.Linear,
			FillFrom = new Vector2(0.0f, 0.0f),
			FillTo = new Vector2(1.0f, 0.0f),
		};

		return texture;
	}

	private static StyleBoxTexture HorizontalFade(
		Color color, float[] offsets, float[] alphas) =>
		new()
		{
			Texture = FadeTexture(color, offsets, alphas),
			ContentMarginLeft = 0.0f,
			ContentMarginRight = 0.0f,
			ContentMarginTop = 0.0f,
			ContentMarginBottom = 0.0f,
		};

	private static StyleBoxTexture EdgeFaded(Color color) =>
		HorizontalFade(color, EdgeFadeOffsets, EdgeFadeAlphas);

	/// <summary>The floating parchment-glass card every launcher surface is: a gold hairline, soft corners, a drop shadow.</summary>
	private static StyleBoxFlat GlassCard()
	{
		var box = new StyleBoxFlat { BgColor = LauncherTokens.ParchmentGlass };
		box.BorderColor = new Color(LauncherTokens.Gold, 0.45f);
		box.SetBorderWidthAll(1);
		box.SetCornerRadiusAll(LauncherTokens.RadiusMd);
		box.ShadowColor = new Color(0, 0, 0, 0.4f);
		box.ShadowSize = 18;
		box.ShadowOffset = new Vector2(0, 6);
		return box;
	}

	private static StyleBoxFlat Fill(Color color, Color? border = null)
	{
		var box = new StyleBoxFlat { BgColor = color };
		if (border is { } borderColor)
		{
			box.BorderColor = borderColor;
			box.SetBorderWidthAll(1);
		}

		return box;
	}

	private StyleBoxFlat TabFill(Color underline, int underlineWidth)
	{
		var box = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) };
		box.BorderColor = underline;
		box.BorderWidthBottom = underlineWidth;
		box.ContentMarginLeft = LauncherTokens.Sp2;
		box.ContentMarginRight = LauncherTokens.Sp2;
		box.ContentMarginTop = LauncherTokens.Sp2;
		box.ContentMarginBottom = 6;
		return box;
	}

	private static StyleBoxFlat ButtonFace(
		Color bg, Color border, Color? glow = null, int glowSize = 10)
	{
		var box = new StyleBoxFlat { BgColor = bg, BorderColor = border };
		box.SetBorderWidthAll(1);
		box.SetCornerRadiusAll(LauncherTokens.RadiusSm);
		box.ContentMarginLeft = LauncherTokens.Sp6;
		box.ContentMarginRight = LauncherTokens.Sp6;
		box.ContentMarginTop = LauncherTokens.Sp3;
		box.ContentMarginBottom = LauncherTokens.Sp3;
		if (glow is { } glowColor)
		{
			box.ShadowColor = glowColor;
			box.ShadowSize = glowSize;
			box.ShadowOffset = Vector2.Zero;
		}

		return box;
	}

	private void ApplyFont(Control control, int size, int tracking = 0)
	{
		if (_font is not null)
		{
			if (tracking > 0)
			{
				control.AddThemeFontOverride("font", new FontVariation
				{
					BaseFont = _font,
					SpacingGlyph = tracking,
				});
			}
			else
			{
				control.AddThemeFontOverride("font", _font);
			}
		}

		control.AddThemeFontSizeOverride("font_size", size);
	}

	private void ApplySystemFont(Control control, int size)
	{
		// Design language: the display face (fable2.ttf) is reserved for identity;
		// body and controls use the system UI face for legibility at small sizes.
		// Remove (not null) the override: null logs "rp_font is null" per call.
		control.RemoveThemeFontOverride("font");
		control.AddThemeFontSizeOverride("font_size", size);
	}

	/// <summary>
	/// Fluid type + spacing: re-applies every font size from LauncherScale so the
	/// whole UI scales like a responsive site. Runs on every resize.
	/// </summary>
	private void ApplyScale()
	{
		int body = LauncherScale.Font(LauncherTokens.BodySize);

		ApplyFont(_wordmark, LauncherScale.Font(LauncherTokens.WordmarkSize), 2);
		foreach (MenuButton menu in new[] { _gameMenu, _fileMenu, _aboutMenu })
		{
			ApplySystemFont(menu, body);
		}

		ApplySystemFont(_warningLabel, LauncherScale.Font(LauncherTokens.CaptionSize + 1));
		ApplyFont(_primaryButton, LauncherScale.Font(LauncherTokens.DisplaySize));
		ApplySystemFont(_artCredit, LauncherScale.Font(LauncherTokens.CaptionSize));
		ApplyFont(_infoTitle, LauncherScale.Font(LauncherTokens.InfoTitleSize));
		ApplySystemFont(_infoTabs, LauncherScale.Font(13));
		ApplySystemFont(_infoBody, body);

		ApplyFont(_settingsTitle, LauncherScale.Font(LauncherTokens.InfoTitleSize));
		ApplySystemFont(_settingsTabs, LauncherScale.Font(13));

		_drawerClose.AddThemeFontSizeOverride("font_size", LauncherScale.Font(19));
		_settingsClose.AddThemeFontSizeOverride("font_size", LauncherScale.Font(19));
		_infoTab.AddThemeFontSizeOverride("font_size", LauncherScale.Font(16));
		_settingsTab.AddThemeFontSizeOverride("font_size", LauncherScale.Font(16));

		// Panel content (settings, modding, reinstall) is uniform body text:
		// refresh the panels' stored size and re-apply to existing nodes.
		_settingsPanel.Configure(null, body, _infoStyle);
		_moddingPanel.Configure(null, body, _infoStyle);
		if (!ScaleAndroidInstallation(body)) _reinstallPanel.Configure(null, body, _infoStyle);
		_savesPanel.Configure(body);
		ScaleFonts(_settingsPanel, body);
		ScaleFonts(_moddingPanel, body);
		ScaleFonts(_reinstallPanel, body);
	}

	private static void ScaleFonts(Control root, int size)
	{
		root.AddThemeFontSizeOverride("font_size", size);
		foreach (Node child in root.GetChildren())
		{
			if (child is Control control)
			{
				ScaleFonts(control, size);
			}
		}
	}

	/// <summary>
	/// Micro-interactions for the primary action: a 2px hover lift, a dim press,
	/// and a gold focus ring for keyboard users. All short, all one-shot.
	/// </summary>
	private void WirePrimaryButtonInteractions()
	{
		_primaryButton.AddThemeStyleboxOverride("focus", FocusFace());
		_primaryButton.MouseEntered += () =>
		{
			if (_primaryButton.Disabled)
			{
				return;
			}

			_primaryButton.CreateTween()
				.TweenProperty(_primaryButton, "position", new Vector2(0, -2), 0.14)
				.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
		};
		_primaryButton.MouseExited += () => _primaryButton.CreateTween()
			.TweenProperty(_primaryButton, "position", Vector2.Zero, 0.14)
			.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
		_primaryButton.ButtonDown += () => _primaryButton.CreateTween()
			.TweenProperty(_primaryButton, "modulate", new Color(0.88f, 0.88f, 0.88f, 1f), 0.07);
		_primaryButton.ButtonUp += () => _primaryButton.CreateTween()
			.TweenProperty(_primaryButton, "modulate", Colors.White, 0.16);
	}

	private static StyleBoxFlat FocusFace()
	{
		var box = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) };
		box.BorderColor = LauncherTokens.Gold;
		box.SetBorderWidthAll(2);
		box.SetCornerRadiusAll(LauncherTokens.RadiusSm + 2);
		box.ExpandMarginLeft = 3;
		box.ExpandMarginRight = 3;
		box.ExpandMarginTop = 3;
		box.ExpandMarginBottom = 3;
		return box;
	}

	/// <summary>Edge tab that expands its drawer: a parchment glass tab protruding from
	/// the screen border into the art, rounded on the inward-facing side.</summary>
	private static void StyleTab(Button tab, string glyph, bool rightSide)
	{
		tab.Text = glyph;
		tab.MouseDefaultCursorShape = CursorShape.PointingHand;

		// Normal: Translucent parchment matching drawer surface with a gold hairline border
		var normal = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentGlass,
			BorderColor = new Color(LauncherTokens.Gold, 0.55f),
			ShadowColor = new Color(0, 0, 0, 0.35f),
			ShadowSize = 8,
		};
		normal.SetCornerRadiusAll(0);
		normal.SetBorderWidthAll(1);

		if (rightSide)
		{
			// Right edge button: Flat on the right screen border (border_width_right = 0),
			// curved on the left inward-facing side.
			normal.BorderWidthRight = 0;
			normal.CornerRadiusTopLeft = 14;
			normal.CornerRadiusBottomLeft = 14;
		}
		else
		{
			// Left edge button: Flat on the left screen border (border_width_left = 0),
			// curved on the right inward-facing side.
			normal.BorderWidthLeft = 0;
			normal.CornerRadiusTopRight = 14;
			normal.CornerRadiusBottomRight = 14;
		}

		// Hover: slightly brighter parchment with stronger gold glow and shadow
		var hover = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentLit,
			BorderColor = LauncherTokens.Gold,
			ShadowColor = new Color(LauncherTokens.Gold, 0.40f),
			ShadowSize = 12,
		};
		hover.SetCornerRadiusAll(0);
		hover.SetBorderWidthAll(1);

		if (rightSide)
		{
			hover.BorderWidthRight = 0;
			hover.CornerRadiusTopLeft = 14;
			hover.CornerRadiusBottomLeft = 14;
		}
		else
		{
			hover.BorderWidthLeft = 0;
			hover.CornerRadiusTopRight = 14;
			hover.CornerRadiusBottomRight = 14;
		}

		// Pressed: deeper parchment tone
		var pressed = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentRaised,
			BorderColor = LauncherTokens.GoldBright,
			ShadowColor = new Color(0, 0, 0, 0.5f),
			ShadowSize = 4,
		};
		pressed.SetCornerRadiusAll(0);
		pressed.SetBorderWidthAll(1);

		if (rightSide)
		{
			pressed.BorderWidthRight = 0;
			pressed.CornerRadiusTopLeft = 14;
			pressed.CornerRadiusBottomLeft = 14;
		}
		else
		{
			pressed.BorderWidthLeft = 0;
			pressed.CornerRadiusTopRight = 14;
			pressed.CornerRadiusBottomRight = 14;
		}

		tab.AddThemeStyleboxOverride("normal", normal);
		tab.AddThemeStyleboxOverride("hover", hover);
		tab.AddThemeStyleboxOverride("pressed", pressed);
		tab.AddThemeStyleboxOverride("focus", FocusFace());
		tab.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
		tab.AddThemeColorOverride("font_hover_color", LauncherTokens.GoldInk);
	}

	/// <summary>Soft fade-and-rise entrance for both floating drawers and their
	/// companions on first layout.</summary>
	private void PlayEntrance()
	{
		_settingsDrawer.Modulate = new Color(1, 1, 1, 0);
		_settingsDrawer.Position = _settingsDrawerBasePos + new Vector2(28, 0);
		_infoPanel.Modulate = new Color(1, 1, 1, 0);
		_infoPanel.Position = _drawerBasePos + new Vector2(-28, 0);
		_primaryButton.Modulate = new Color(1, 1, 1, 0);
		_primaryButton.Position = new Vector2(0, 10);
		_warningPanel.Modulate = new Color(1, 1, 1, 0);
		_warningPanel.Position = _warningBasePos + new Vector2(0, 8);

		var tween = CreateTween();
		tween.SetParallel(true);
		TweenEntrance(tween, _settingsDrawer, _settingsDrawerBasePos, 0.30f, 0.0f);
		TweenEntrance(tween, _infoPanel, _drawerBasePos, 0.30f, 0.03f);
		TweenEntrance(tween, _primaryButton, Vector2.Zero, 0.30f, 0.06f);
		TweenEntrance(tween, _warningPanel, _warningBasePos, 0.30f, 0.12f);
	}

	private static void TweenEntrance(
		Tween tween, Control control, Vector2 target, float duration, float delay)
	{
		tween.TweenProperty(control, "modulate", Colors.White, duration)
			.SetDelay(delay).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(control, "position", target, duration)
			.SetDelay(delay).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
	}

	/// <summary>Fade-in for modal dialogs when they mount.</summary>
	private static void AnimateIn(Control control)
	{
		control.Modulate = new Color(1, 1, 1, 0);
		control.Position += new Vector2(0, 14);
		var tween = control.CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(control, "modulate", Colors.White, 0.20)
			.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(control, "position", control.Position - new Vector2(0, 14), 0.20)
			.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
	}

	/// <summary>Quiet settle when switching tabs in either drawer.</summary>
	private void FadeInTabContent()
	{
		Control target = _settingsTabs.CurrentTab switch
		{
			0 => _settingsPanel,
			1 => _moddingPanel,
			2 => (Control?)_phone ?? _reinstallPanel,
			3 => _savesPanel,
			_ => _settingsPanel,
		};

		if (target.Visible)
		{
			target.Modulate = new Color(1, 1, 1, 0.4f);
			target.CreateTween()
				.TweenProperty(target, "modulate", Colors.White, 0.18)
				.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
		}
	}

	private void FadeInNewsContent()
	{
		if (_infoBody.Visible)
		{
			_infoBody.Modulate = new Color(1, 1, 1, 0.4f);
			_infoBody.CreateTween()
				.TweenProperty(_infoBody, "modulate", Colors.White, 0.18)
				.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
		}
	}

	private void ApplyStyles()
	{
		_screen = LauncherTitleScreen.Active;
		ApplyBackgrounds();

		// THE HEADER IS A CARD: the wordmark and the three menus sit on a light grey
		// translucent wash, sized to their content (see ApplyLayout) rather than a bar across
		// the window, so the cream text reads over any artwork. Grey, not the parchment of the
		// drawers: the header's text is cream and gold, and stays so.
		StyleBoxFlat headerCard = GlassCard();
		headerCard.BgColor = LauncherTokens.SlateGlass;
		headerCard.BorderColor = new Color(1, 1, 1, 0.22f);
		_menuBar.AddThemeStyleboxOverride("panel", headerCard);

		_wordmark.BbcodeEnabled = true;
		// The wordmark's width IS the card's: its minimum follows the text (the scene's fixed
		// 300 px was for a bar that never measured it), one line, never wrapped.
		_wordmark.FitContent = true;
		_wordmark.AutowrapMode = TextServer.AutowrapMode.Off;
		_wordmark.CustomMinimumSize = new Vector2(0, _wordmark.CustomMinimumSize.Y);
		ApplyWordmark();
		_wordmark.MouseFilter = MouseFilterEnum.Ignore;
		_wordmark.VerticalAlignment = VerticalAlignment.Center;
		_wordmark.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.65f));
		_wordmark.AddThemeConstantOverride("shadow_offset_x", 1);
		_wordmark.AddThemeConstantOverride("shadow_offset_y", 2);

		// Menu chips: a small dark glass background so the cream labels keep
		// contrast no matter what artwork is behind them.
		var menuNormal = new StyleBoxFlat
		{
			BgColor = LauncherTokens.InkGlass,
			BorderColor = new Color(LauncherTokens.Gold, 0.35f),
			ContentMarginLeft = 10,
			ContentMarginRight = 10,
			ContentMarginTop = 4,
			ContentMarginBottom = 4,
		};
		menuNormal.SetCornerRadiusAll(LauncherTokens.RadiusSm);
		menuNormal.SetBorderWidthAll(1);

		var menuHover = new StyleBoxFlat
		{
			BgColor = new Color(LauncherTokens.Oak, 0.72f),
			BorderColor = new Color(LauncherTokens.Gold, 0.7f),
			ContentMarginLeft = 10,
			ContentMarginRight = 10,
			ContentMarginTop = 4,
			ContentMarginBottom = 4,
		};
		menuHover.SetCornerRadiusAll(LauncherTokens.RadiusSm);
		menuHover.SetBorderWidthAll(1);

		foreach (MenuButton menu in new[] { _gameMenu, _fileMenu, _aboutMenu })
		{
			menu.MouseDefaultCursorShape = CursorShape.PointingHand;
			menu.TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming;
			menu.AddThemeColorOverride("font_color", LauncherTokens.Cream);
			menu.AddThemeColorOverride("font_hover_color", LauncherTokens.GoldBright);
			menu.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.6f));
			menu.AddThemeConstantOverride("shadow_offset_x", 1);
			menu.AddThemeConstantOverride("shadow_offset_y", 1);
			menu.AddThemeStyleboxOverride("normal", menuNormal);
			menu.AddThemeStyleboxOverride("hover", menuHover);
			menu.AddThemeStyleboxOverride("pressed", menuHover);
			menu.AddThemeStyleboxOverride("focus", FocusFace());
		}

		// Quiet floating notice tag: dark glass pill, subtle text. It sits above
		// the action, over the art, without shouting.
		var noticeBox = new StyleBoxFlat { BgColor = LauncherTokens.InkGlass };
		noticeBox.SetCornerRadiusAll(LauncherTokens.RadiusPill);
		noticeBox.BorderColor = new Color(LauncherTokens.Gold, 0.18f);
		noticeBox.SetBorderWidthAll(1);
		noticeBox.ContentMarginLeft = LauncherTokens.Sp4;
		noticeBox.ContentMarginRight = LauncherTokens.Sp4;
		_warningPanel.AddThemeStyleboxOverride("panel", noticeBox);
		// The title's screen states the notice; when it does not, the line the scene carries
		// stands. This assigned LauncherTheme.WarningText unconditionally, and its default is the
		// empty string — no launcher.ini ships one, so the pill drew empty over the artwork.
		if (_screen.WarningText.Length != 0)
		{
			_warningLabel.Text = _screen.WarningText;
		}

		// An ini that states an empty notice means no notice: hide the pill rather than float
		// an empty one.
		_warningPanel.Visible = _warningLabel.Text.Length != 0;
		_warningLabel.HorizontalAlignment = HorizontalAlignment.Center;
		_warningLabel.AddThemeColorOverride("font_color", new Color(LauncherTokens.Cream, 0.82f));

		// The action area is just a positioning pocket; the button floats.
		_actionPanel.AddThemeStyleboxOverride("panel", Fill(new Color(0, 0, 0, 0)));
		_primaryButton.MouseDefaultCursorShape = CursorShape.PointingHand;
		_primaryButton.AddThemeStyleboxOverride(
			"normal", ButtonFace(_buttonStyle.Button, new Color(LauncherTokens.Gold, 0.55f), new Color(LauncherTokens.Gold, 0.20f), 10));
		_primaryButton.AddThemeStyleboxOverride(
			"hover", ButtonFace(_buttonStyle.ButtonHovered, new Color(LauncherTokens.Gold, 0.8f), new Color(LauncherTokens.Gold, 0.38f), 14));
		_primaryButton.AddThemeStyleboxOverride(
			"pressed", ButtonFace(_buttonStyle.ButtonActive, new Color(LauncherTokens.Gold, 0.55f)));
		_primaryButton.AddThemeStyleboxOverride(
			"disabled", ButtonFace(new Color(LauncherTokens.OakDim, 0.85f), new Color(LauncherTokens.Gold, 0.35f)));
		_progressTrack.Texture =
			FadeTexture(new Color(LauncherTokens.Gold, 0.18f), EdgeFadeOffsets, EdgeFadeAlphas);
		_progressFill.Texture =
			FadeTexture(new Color(LauncherTokens.Gold, 0.85f), EdgeFadeOffsets, EdgeFadeAlphas);
		_primaryButton.AddThemeColorOverride("font_color", LauncherTokens.Cream);
		_primaryButton.AddThemeColorOverride("font_disabled_color", _infoStyle.TextDisabled);
		_primaryButton.Pressed += OnPrimaryPressed;
		WirePrimaryButtonInteractions();

		// CLIPPED, WITH AN ELLIPSIS. The button is a fixed 280 units wide and the install
		// status is written into it; a status longer than that drew straight past the edge
		// and under the pause and stop buttons beside it. The full line is still on the
		// Reinstall tab, which wraps.
		_primaryButton.ClipText = true;
		_primaryButton.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
		BuildBuildControls();

		ApplySystemFont(_artCredit, LauncherScale.Font(LauncherTokens.CaptionSize));
		_artCredit.AddThemeColorOverride("font_color", new Color(LauncherTokens.Cream, 0.72f));
		_artCredit.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.75f));
		_artCredit.AddThemeConstantOverride("shadow_offset_x", 1);

		// Announcements Drawer (Left Side):
		StyleBoxFlat infoBox = GlassCard();
		infoBox.ContentMarginLeft = LauncherTokens.Sp6;
		infoBox.ContentMarginRight = LauncherTokens.Sp6;
		infoBox.ContentMarginTop = LauncherTokens.Sp4;
		infoBox.ContentMarginBottom = LauncherTokens.Sp6;
		_infoPanel.AddThemeStyleboxOverride("panel", infoBox);
		_infoPanel.Visible = true; // Open by default (left side)

		_drawerClose.MouseDefaultCursorShape = CursorShape.PointingHand;
		_drawerClose.AddThemeColorOverride("font_color", LauncherTokens.SepiaInk);
		_drawerClose.AddThemeColorOverride("font_hover_color", LauncherTokens.GoldInk);
		_drawerClose.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) });
		_drawerClose.AddThemeStyleboxOverride("hover", new StyleBoxFlat { BgColor = new Color(LauncherTokens.Gold, 0.22f) });
		_drawerClose.AddThemeStyleboxOverride("pressed", new StyleBoxFlat { BgColor = new Color(LauncherTokens.Gold, 0.35f) });
		_drawerClose.AddThemeStyleboxOverride("focus", FocusFace());
		_drawerClose.Pressed += DismissDrawer;

		_infoTitle.AddThemeColorOverride("font_color", _infoStyle.Text);
		var titleRule = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) };
		titleRule.BorderColor = _infoStyle.Border;
		titleRule.BorderWidthBottom = 2;
		titleRule.ContentMarginBottom = 4;
		_infoTitle.AddThemeStyleboxOverride("normal", titleRule);

		_infoTabs.MouseDefaultCursorShape = CursorShape.PointingHand;
		_infoTabs.AddThemeStyleboxOverride("tab_selected", TabFill(LauncherTokens.Gold, 2));
		_infoTabs.AddThemeStyleboxOverride("tab_unselected", TabFill(new Color(0, 0, 0, 0), 0));
		_infoTabs.AddThemeStyleboxOverride("tab_hovered", TabFill(LauncherTokens.Sepia, 1));
		_infoTabs.AddThemeConstantOverride("h_separation", LauncherTokens.Sp3);
		_infoTabs.AddThemeColorOverride("font_selected_color", LauncherTokens.GoldInk);
		_infoTabs.AddThemeColorOverride("font_unselected_color", LauncherTokens.SepiaInk);
		// News Body:
		var bodyCard = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) };
		bodyCard.ContentMarginLeft = LauncherTokens.Sp1;
		bodyCard.ContentMarginRight = LauncherTokens.Sp1;
		bodyCard.ContentMarginTop = LauncherTokens.Sp2;
		bodyCard.ContentMarginBottom = LauncherTokens.Sp2;
		_bodyScroll.AddThemeStyleboxOverride("panel", bodyCard);
		_bodyScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
		_infoBody.FitContent = true;
		_infoBody.AddThemeColorOverride("default_color", _infoStyle.Text);
		_infoBody.AddThemeConstantOverride("line_separation", 5);

		// Complete Debug & System Drawer (Settings, Modding, Reinstall) - Default Open:
		var settingsBox = new StyleBoxFlat { BgColor = LauncherTokens.ParchmentGlassStrong };
		settingsBox.BorderColor = new Color(LauncherTokens.Gold, 0.55f);
		settingsBox.SetBorderWidthAll(1);
		settingsBox.SetCornerRadiusAll(LauncherTokens.RadiusMd);
		settingsBox.ShadowColor = new Color(0, 0, 0, 0.45f);
		settingsBox.ShadowSize = 20;
		settingsBox.ShadowOffset = new Vector2(0, 6);
		settingsBox.ContentMarginLeft = LauncherTokens.Sp6;
		settingsBox.ContentMarginRight = LauncherTokens.Sp6;
		settingsBox.ContentMarginTop = LauncherTokens.Sp4;
		settingsBox.ContentMarginBottom = LauncherTokens.Sp6;
		_settingsDrawer.AddThemeStyleboxOverride("panel", settingsBox);
		_settingsDrawer.Visible = true; // Open by default (right side)

		_settingsTitle.AddThemeColorOverride("font_color", _infoStyle.Text);
		var settingsRule = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) };
		settingsRule.BorderColor = _infoStyle.Border;
		settingsRule.BorderWidthBottom = 2;
		settingsRule.ContentMarginBottom = 4;
		_settingsTitle.AddThemeStyleboxOverride("normal", settingsRule);
		_settingsTabs.MouseDefaultCursorShape = CursorShape.PointingHand;
		_settingsTabs.AddThemeStyleboxOverride("tab_selected", TabFill(LauncherTokens.Gold, 2));
		_settingsTabs.AddThemeStyleboxOverride("tab_unselected", TabFill(new Color(0, 0, 0, 0), 0));
		_settingsTabs.AddThemeStyleboxOverride("tab_hovered", TabFill(LauncherTokens.Sepia, 1));
		_settingsTabs.AddThemeConstantOverride("h_separation", LauncherTokens.Sp3);
		_settingsTabs.AddThemeColorOverride("font_selected_color", LauncherTokens.GoldInk);
		_settingsTabs.AddThemeColorOverride("font_unselected_color", LauncherTokens.SepiaInk);

		_settingsClose.MouseDefaultCursorShape = CursorShape.PointingHand;
		_settingsClose.AddThemeColorOverride("font_color", LauncherTokens.SepiaInk);
		_settingsClose.AddThemeColorOverride("font_hover_color", LauncherTokens.GoldInk);
		_settingsClose.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) });
		_settingsClose.AddThemeStyleboxOverride("hover", new StyleBoxFlat { BgColor = new Color(LauncherTokens.Gold, 0.22f) });
		_settingsClose.AddThemeStyleboxOverride("pressed", new StyleBoxFlat { BgColor = new Color(LauncherTokens.Gold, 0.35f) });
		_settingsClose.AddThemeStyleboxOverride("focus", FocusFace());
		_settingsClose.Pressed += DismissSettingsDrawer;

		// Drawer tabs on the window edges: grab handles that expand the drawer
		// in. Hidden while their drawer is open.
		StyleTab(_infoTab, "i", rightSide: false);
		StyleTab(_settingsTab, "⚙", rightSide: true);
		_infoTab.Pressed += ToggleInfoDrawer;
		_settingsTab.Pressed += ToggleSettingsDrawer;
		_infoTab.Visible = false; // hidden while info drawer is open
		_settingsTab.Visible = false; // hidden while debug drawer is open

		if (_phone == null)
		{
		_reinstallPanel.ReinstallRequested += StartInstallFlow;
		_reinstallPanel.BuildGameCodeRequested += StartGameCodeBuild;
		_reinstallPanel.PauseBuildRequested += OnPauseBuildPressed;
		_reinstallPanel.ResumeBuildRequested += OnPauseBuildPressed;
		_reinstallPanel.StopBuildRequested += OnStopBuildPressed;
		_reinstallPanel.CompileShadersRequested += StartShaderBuild;
		_reinstallPanel.BuildModernizedAssetsRequested += StartAssetPrebuild;
		}

		ApplyScale();
	}

	/// <summary>Connects shared menus to actions supported by the current platform.</summary>
	private void WireMenus()
	{
		PopulateGameMenu();
		PopulateMenu(_fileMenu, _phone == null ? FileMenuItems : AndroidFileMenuItems, OnFileMenu);
		_aboutMenu.GetPopup().IdPressed += id =>
		{
			int index = (int)id;
			if (index >= 0 && index < _aboutLinks.Count)
			{
				Platform.TryOpenUrl(_aboutLinks[index].Url);
			}
		};
		PopulateAboutMenu();
		StylePopup(_gameMenu.GetPopup());
		StylePopup(_fileMenu.GetPopup());
		StylePopup(_aboutMenu.GetPopup());
	}

	/// <summary>One radio item per title; the button reads the active one.</summary>
	private void PopulateGameMenu()
	{
		PopupMenu popup = _gameMenu.GetPopup();
		popup.Clear();
		for (int i = 0; i < Refii.Core.Engine_Title.All.Count; i++)
		{
			popup.AddRadioCheckItem(PhoneTitleLabel(Refii.Core.Engine_Title.All[i]), i);
		}

		popup.IdPressed += id =>
		{
			int index = (int)id;
			if (index >= 0 && index < Refii.Core.Engine_Title.All.Count)
			{
				SwitchTitle(Refii.Core.Engine_Title.All[index]);
			}
		};

		RefreshGameMenu();
	}

	private void RefreshGameMenu()
	{
		PopupMenu popup = _gameMenu.GetPopup();
		for (int i = 0; i < Refii.Core.Engine_Title.All.Count; i++)
		{
			popup.SetItemChecked(i, Refii.Core.Engine_Title.All[i] == Refii.Core.Engine_Title.Active);
		}

		_gameMenu.Text = Refii.Core.Engine_Title.Active.Name;
	}

	private void ApplyWordmark()
	{
		_wordmark.Text = _screen.Wordmark;
	}

	/// <summary>The About menu: the active title's own links, then the shared ones.</summary>
	private void PopulateAboutMenu()
	{
		_aboutLinks.Clear();
		_aboutLinks.AddRange(_screen.AboutLinks);
		_aboutLinks.AddRange(SharedAboutLinks);

		PopupMenu popup = _aboutMenu.GetPopup();
		popup.Clear();
		for (int i = 0; i < _aboutLinks.Count; i++)
		{
			popup.AddItem(_aboutLinks[i].Label, i);
		}
	}

	/// <summary>
	/// The background art and its credit: the title's own, or a plain neutral background when it
	/// has none. A fresh manager per title, so the slideshow never shows another title's art.
	/// </summary>
	private void ApplyBackgrounds()
	{
		_backgrounds = new BackgroundManager(_background, _backgroundNext, _screen.Backgrounds);
		_backgrounds.Initialize();
		UpdateArtCredit();
	}

	/// <summary>The info drawer's tabs from the active title's screen; the drawer shows the first.</summary>
	private void ApplyInfoTabs()
	{
		_infoTabList = PhoneInfoTabs(_screen.InfoTabs);
		_infoTabs.ClearTabs();
		foreach (LauncherInfoTab tab in _infoTabList)
		{
			_infoTabs.AddTab(tab.Name);
		}

		_infoTabs.Visible = _infoTabList.Count != 0;
		RefreshInfoBody();
	}

	/// <summary>
	/// Rebuilds the page for the active title: its screen (wordmark, window title, notice, art,
	/// info drawer, About links), and the settings, modding and install panels, which follow the
	/// title's own config file, settings and folders.
	/// </summary>
	private void ApplyTitleScreen()
	{
		_screen = LauncherTitleScreen.Active;
		ApplyWordmark();
		// The header card is sized to the wordmark, which just changed; measured once the label
		// has laid the new text out.
		CallDeferred(MethodName.ApplyLayout);
		ApplyBackgrounds();
		ApplyInfoTabs();
		PopulateAboutMenu();
		if (!Engine.IsEditorHint())
		{
			DisplayServer.WindowSetTitle(_screen.WindowTitle);
		}

		_settingsPanel.ReloadForTitle();
		_moddingPanel.ReloadForTitle();
		ApplyScale();
	}

	/// <summary>
	/// Makes <paramref name="title"/> the game the launcher installs, builds and plays. Refused
	/// while an install or build runs or the game is up: each works on the title it started with.
	/// </summary>
	private void SwitchTitle(Refii.Core.Engine_Title title)
	{
		if (title == Refii.Core.Engine_Title.Active)
		{
			return;
		}

		if (_pipeline.Running || _launch.GameIsRunning || AndroidInstalling)
		{
			Log("Finish the running install or build, or close the game, before switching to " + title.Name + ".");
			RefreshGameMenu();
			return;
		}

		Refii.Core.Engine_Title.Active = title;
		Ini.SetString(ActiveTitleKey, title.Key, TitleSection);
		Ini.Save();

		Installer.InvalidateReinstallStepStatus();
		Recompile.GameCodeBuild.InvalidateStatus();
		// Read from the title's own config file: the next asset build takes this title's settings.
		Refii.Textures.Engine_TexSettings.Invalidate();

		RefreshGameMenu();
		ApplyTitleScreen();
		ReloadAndroidTitle();  // the phone's installation controls, LauncherApp.Android.cs
		Log("Switched to " + title.Name + ".");
		RefreshState();
		RefreshDebugDrawer();
	}

	private void StylePopup(PopupMenu popup)
	{
		var panel = new StyleBoxFlat { BgColor = _infoStyle.PopupBg };
		panel.BorderColor = _infoStyle.Border;
		panel.SetBorderWidthAll(1);
		panel.ShadowColor = new Color(0, 0, 0, 0.4f);
		panel.ShadowSize = 14;
		panel.ContentMarginLeft = 4;
		panel.ContentMarginRight = 4;
		panel.ContentMarginTop = 4;
		panel.ContentMarginBottom = 4;
		popup.AddThemeStyleboxOverride("panel", panel);

		var hover = new StyleBoxFlat { BgColor = _infoStyle.Header };
		hover.ContentMarginLeft = LauncherTokens.Sp2;
		hover.ContentMarginRight = LauncherTokens.Sp2;
		hover.ContentMarginTop = 2;
		hover.ContentMarginBottom = 2;
		popup.AddThemeStyleboxOverride("hover", hover);

		popup.AddThemeFontSizeOverride("font_size", LauncherScale.Font(LauncherTokens.BodySize));
		popup.AddThemeColorOverride("font_color", _infoStyle.Text);
		popup.AddThemeColorOverride("font_hover_color", _infoStyle.Text);
		popup.AddThemeColorOverride("font_separator_color", _infoStyle.Separator);
		popup.AddThemeColorOverride("font_disabled_color", _infoStyle.TextDisabled);
	}

	private static void PopulateMenu(MenuButton button, string[] items, Action<string> onSelected)
	{
		PopupMenu popup = button.GetPopup();
		popup.Clear();

		var labels = new List<string>();
		foreach (string item in items)
		{
			if (item.Length == 0)
			{
				popup.AddSeparator();
				labels.Add(string.Empty);
			}
			else
			{
				popup.AddItem(item, labels.Count);
				labels.Add(item);
			}
		}

		popup.IdPressed += id =>
		{
			int index = (int)id;
			if (index >= 0 && index < labels.Count && labels[index].Length != 0)
			{
				onSelected(labels[index]);
			}
		};
	}

	/// <summary>Uses the shared settings drawer with the current platform's installation controls.</summary>
	private void WireTabs()
	{
		ApplyInfoTabs();
		_infoTabs.TabChanged += _ =>
		{
			RefreshInfoBody();
			FadeInNewsContent();
		};

		_settingsTabs.ClearTabs();
		foreach (string tab in DebugTabs)
		{
			_settingsTabs.AddTab(tab);
		}
		_settingsTabs.TabChanged += _ =>
		{
			RefreshDebugDrawer();
			FadeInTabContent();
		};
	}

	private void OnFileMenu(string item)
	{
		if (OnAndroidFileMenu(item)) return;  // the phone's own entries, LauncherApp.Android.cs
		switch (item)
		{
			case "Set Install Folder":
				ShowInstallFolderDialog();
				break;

			case "Uninstall":
				Log(Installer.UninstallManagedFiles(out string uninstallLog)
					? uninstallLog
					: "No " + Installer.GetGameTitle() + " launcher-managed files to uninstall.");
				RefreshState();
				break;

			case "Show Info Panel":
			case "Show Announcements":
				ShowDrawer();
				break;

			case "Show Debug Panel":
			case "Show Launch Options":
				ShowSettingsDrawer();
				break;

			case "Close":
				if (!Engine.IsEditorHint())
				{
					GetTree().Quit();
				}

				break;
		}
	}

	private void ShowInstallFolderDialog()
	{
		var dialog = new FileDialog
		{
			FileMode = FileDialog.FileModeEnum.OpenDir,
			Access = FileDialog.AccessEnum.Filesystem,
			Title = "Select Install Folder",
			CurrentDir = Installer.GetInstallBasePath(),
			Size = new Vector2I(760, 520),
		};

		dialog.DirSelected += path =>
		{
			string withSeparator = path.EndsWith('/') || path.EndsWith('\\') ? path : path + "/";
			Ini.SetString("GamePath", withSeparator, "Game");
			Ini.Save();
			Log("Install path set to: " + withSeparator);
			RefreshState();
			dialog.QueueFree();
		};
		dialog.Canceled += () => dialog.QueueFree();

		AddChild(dialog);
	}

	private void Log(string message)
	{
		GD.Print(message);
		_consoleLines.Add(message);
		if (_consoleLines.Count > 200)
		{
			_consoleLines.RemoveAt(0);
		}

		RefreshInfoBody();
	}

	private Vector2 _drawerBasePos;
	private Vector2 _actionBasePos;
	private Vector2 _warningBasePos;
	private Vector2 _settingsDrawerBasePos;

	private void ApplyLayout()
	{
		Vector2 viewport = Size;
		LauncherScale.Update(viewport);
		ApplyScale();

		LauncherLayout layout = LauncherLayout.Build(Vector2.Zero, viewport, _menuBarHeight);

		// The header card hugs its content: the wordmark row and the menu row, plus the
		// HeaderCol's own inset on both sides, floating off the corner like the other cards.
		float headerInset = _headerCol.OffsetLeft;
		float cardGap = LauncherScale.Px(12.0f);
		_menuBar.Position = new Vector2(cardGap, cardGap);
		_menuBar.Size = new Vector2(
			_headerCol.GetCombinedMinimumSize().X + (headerInset * 2.0f), _menuBarHeight);

		// Edge tabs: glued to their window edge, vertically centered.
		_infoTab.OffsetTop = -LauncherScale.Px(44.0f);
		_infoTab.OffsetBottom = LauncherScale.Px(44.0f);
		_infoTab.OffsetRight = LauncherScale.Px(30.0f);
		_settingsTab.OffsetTop = -LauncherScale.Px(44.0f);
		_settingsTab.OffsetBottom = LauncherScale.Px(44.0f);
		_settingsTab.OffsetLeft = -LauncherScale.Px(30.0f);

		_artCredit.Position = new Vector2(
			layout.WarningPos.X + 12.0f, layout.WarningPos.Y - ArtCreditHeight);
		_artCredit.Size = new Vector2(layout.WarningSize.X - 24.0f, ArtCreditHeight);

		// WarningPanel, button and label are absolute-positioned and placed
		// here; nothing uses anchors so manual Position/Size tweening works.
		_warningBasePos = layout.WarningPos;
		_warningPanel.Position = _warningBasePos;
		_warningPanel.Size = layout.WarningSize;
		_warningLabel.Position = new Vector2(10.0f, 0.0f);
		_warningLabel.Size = new Vector2(layout.WarningSize.X - 20.0f, layout.WarningSize.Y);

		_actionBasePos = layout.ActionPos;
		_actionPanel.Position = _actionBasePos;
		_actionPanel.Size = layout.ActionSize;
		_primaryButton.Position = Vector2.Zero;
		_primaryButton.Size = layout.ActionSize;
		_progressTrack.Position = Vector2.Zero;
		_progressTrack.Size = layout.ActionSize;
		_progressClip.Position = Vector2.Zero;
		_progressClip.Size = new Vector2(0, layout.ActionSize.Y);
		_progressFill.Position = Vector2.Zero;
		_progressFill.Size = _primaryButton.Size;

		// Beside the primary button, over the artwork: the one free space next to it.
		if (_buildControls is not null)
		{
			_buildControls.Position = new Vector2(layout.ActionSize.X + LauncherScale.Px(10.0f), 0);
			_buildControls.Size = new Vector2(LauncherScale.Px(240.0f), layout.ActionSize.Y);
		}

		_drawerBasePos = layout.InfoPos;
		_infoPanel.Position = _drawerBasePos;
		_infoPanel.Size = layout.InfoSize;

		_settingsDrawerBasePos = layout.SettingsPos;
		_settingsDrawer.Position = _settingsDrawerBasePos;
		_settingsDrawer.Size = layout.SettingsSize;
	}

	/// <summary>Hide the info drawer (slides LEFT, off the left edge) and reveal
	/// the anchor pills when both drawers are closed.</summary>
	private void DismissDrawer()
	{
		var tween = CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(_infoPanel, "modulate", new Color(1, 1, 1, 0), 0.16)
			.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
		tween.TweenProperty(_infoPanel, "position", _drawerBasePos + new Vector2(-28, 0), 0.16)
			.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
		tween.TweenCallback(Callable.From(() =>
		{
			_infoPanel.Visible = false;
			_infoTab.Visible = true;
		}));
	}

	private void ShowDrawer()
	{
		_infoTab.Visible = false;
		_infoPanel.Visible = true;
		_infoPanel.Modulate = new Color(1, 1, 1, 0);
		_infoPanel.Position = _drawerBasePos + new Vector2(-28, 0);
		var tween = CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(_infoPanel, "modulate", Colors.White, 0.22)
			.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(_infoPanel, "position", _drawerBasePos, 0.22)
			.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
	}

	private void DismissSettingsDrawer()
	{
		_settingsPanel.SaveIfDirty();
		var tween = CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(_settingsDrawer, "modulate", new Color(1, 1, 1, 0), 0.16)
			.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
		tween.TweenProperty(_settingsDrawer, "position", _settingsDrawerBasePos + new Vector2(24, 0), 0.16)
			.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
		tween.TweenCallback(Callable.From(() =>
		{
			_settingsDrawer.Visible = false;
			_settingsTab.Visible = true;
		}));
	}

	private void ShowSettingsDrawer()
	{
		_settingsTab.Visible = false;
		_settingsDrawer.Visible = true;
		_settingsDrawer.Modulate = new Color(1, 1, 1, 0);
		_settingsDrawer.Position = _settingsDrawerBasePos + new Vector2(24, 0);
		var tween = CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(_settingsDrawer, "modulate", Colors.White, 0.22)
			.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(_settingsDrawer, "position", _settingsDrawerBasePos, 0.22)
			.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
	}

	private void ToggleInfoDrawer()
	{
		if (_infoPanel.Visible)
		{
			DismissDrawer();
		}
		else
		{
			// Since the drawers live on opposite sides, they can both stay open.
			ShowDrawer();
		}
	}

	private void ToggleSettingsDrawer()
	{
		if (_settingsDrawer.Visible)
		{
			DismissSettingsDrawer();
		}
		else
		{
			// Since the drawers live on opposite sides, they can both stay open.
			ShowSettingsDrawer();
		}
	}

	/// <summary>Displays progress and the primary action using the active platform backend.</summary>
	private void RefreshState()
	{
		if (_phone != null) { RefreshAndroidState(); return; }
		bool assetsExist = Installer.AssetsExist();
		bool installedBuildExists = Installer.InstalledBuildExists();
		bool canDownloadBuild = ManagedDownloads.HasDownloadSource();
		bool updateAvailable = _updates.UpdateAvailable;
		bool isoInProgress = _pipeline.Running;

		if (_downloadProgress is not null && _downloadProgress.IsComplete &&
			!_downloadProgress.HasError)
		{
			_downloadProgress = null;
		}

		bool downloadInProgress = _downloadProgress is not null && !_downloadProgress.IsComplete;
		bool gameIsRunning = _launch.GameIsRunning;

		string gameTitle = Installer.GetGameTitle();
		string label = "Play " + gameTitle;
		bool enabled = true;
		bool compactFont = false;

		if (isoInProgress)
		{
			label = ProgressLabel.BuildButtonLabel(_pipeline.Label, _pipeline.DisplayStatus());
			enabled = false;
			compactFont = true;
		}
		else if (downloadInProgress)
		{
			label = ManagedDownloader.BuildProgressButtonLabel(_downloadProgress!);
			enabled = false;
			compactFont = true;
		}
		else if (gameIsRunning)
		{
			label = gameTitle + " Running";
			enabled = false;
		}
		else if (!assetsExist)
		{
			// The whole install runs from here: extract the ISO, install the title update,
			// generate the game code and compile it.
			label = "Install " + gameTitle;
		}
		// The game is installed but this app has no runtime for it yet (a title whose ReXGlue
		// instance is not built): nothing the launcher can do will make it playable.
		else if (!Installer.TitleRuntimeExists())
		{
			label = Refii.Core.Engine_Title.Active.RemakeName + " Runtime Not Built";
			enabled = false;
			compactFont = true;
		}
		else if (Recompile.GameCodeBuild.NeedsBuildCached(out _))
		{
			label = "Build Game Code";
			compactFont = true;
		}
		// AFTER the two above, and last, because this one is no longer about the player's
		// install at all — it means refii's OWN runtime is missing, which is a broken
		// package. Ordered first, it hid "Install Fable 2" behind a dead end that no
		// action in the launcher could clear.
		else if (!installedBuildExists && canDownloadBuild)
		{
			label = "Download " + Platform.ExecutableName;
		}
		else if (!installedBuildExists)
		{
			label = "Missing Runtime Files";
		}
		else if (canDownloadBuild && updateAvailable)
		{
			label = "Update Available";
		}

		_primaryButton.Text = label;
		_primaryButton.Disabled = !enabled;
		_progressTrack.Visible = !enabled;
		_progressTrack.Position = _primaryButton.Position;
		_progressTrack.Size = _primaryButton.Size;

		float fraction = Mathf.Clamp(_pipeline.Fraction, 0.0f, 1.0f);
		_progressClip.Visible = !enabled && fraction > 0.0f;
		_progressClip.Position = _primaryButton.Position;
		_progressClip.Size = new Vector2(_primaryButton.Size.X * fraction, _primaryButton.Size.Y);
		_progressFill.Position = Vector2.Zero;
		_progressFill.Size = _primaryButton.Size;
		ApplyFont(_primaryButton, compactFont
			? LauncherScale.Font(LauncherTokens.BodySize)
			: LauncherScale.Font(LauncherTokens.DisplaySize));

		if (_warningLabel is not null && _warningPanel is not null)
		{
			if (_pipeline.Error.Length != 0 && !_pipeline.Running)
			{
				_warningLabel.Text = _pipeline.Error;
				_warningPanel.Visible = true;
			}
			else if (_screen.WarningText.Length != 0)
			{
				_warningLabel.Text = _screen.WarningText;
				_warningPanel.Visible = true;
			}
			else
			{
				_warningLabel.Text = string.Empty;
				_warningPanel.Visible = false;
			}
		}

		RefreshBuildControls();
		RefreshInfoBody();
		// The Install tab shows the running step and the last failure; it has to follow the
		// state, not only the tab change.
		RefreshDebugDrawer();
	}

	/// <summary>
	/// The pause and stop buttons beside the primary action, for the player who started
	/// the build from there and is not looking at the Reinstall tab.
	/// </summary>
	private void BuildBuildControls()
	{
		_buildControls = new HBoxContainer { Visible = false };
		_buildControls.AddThemeConstantOverride("separation", (int)LauncherScale.Px(8.0f));
		_actionPanel.AddChild(_buildControls);

		_pauseBuildButton = MakeBuildControlButton("Pause", OnPauseBuildPressed);
		_buildControls.AddChild(_pauseBuildButton);
		_stopBuildButton = MakeBuildControlButton("Stop", OnStopBuildPressed);
		_buildControls.AddChild(_stopBuildButton);
	}

	private Button MakeBuildControlButton(string text, Action onPressed)
	{
		var button = new Button
		{
			Text = text,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ExpandFill,
			MouseDefaultCursorShape = CursorShape.PointingHand,
		};
		button.AddThemeStyleboxOverride(
			"normal", ButtonFace(_buttonStyle.Button, new Color(LauncherTokens.Gold, 0.55f)));
		button.AddThemeStyleboxOverride(
			"hover", ButtonFace(_buttonStyle.ButtonHovered, new Color(LauncherTokens.Gold, 0.8f)));
		button.AddThemeStyleboxOverride(
			"pressed", ButtonFace(_buttonStyle.ButtonActive, new Color(LauncherTokens.Gold, 0.55f)));
		button.AddThemeStyleboxOverride(
			"disabled", ButtonFace(new Color(LauncherTokens.OakDim, 0.85f), new Color(LauncherTokens.Gold, 0.35f)));
		button.AddThemeStyleboxOverride("focus", FocusFace());
		button.AddThemeColorOverride("font_color", LauncherTokens.Cream);
		button.AddThemeColorOverride("font_disabled_color", _infoStyle.TextDisabled);
		ApplyFont(button, LauncherScale.Font(LauncherTokens.BodySize));
		button.Pressed += onPressed;
		return button;
	}

	private void RefreshBuildControls()
	{
		if (_buildControls is null || _pauseBuildButton is null || _stopBuildButton is null)
		{
			return;
		}

		Recompile.BuildControl? control = _pipeline.Control;
		_buildControls.Visible = control is not null;
		if (control is null)
		{
			return;
		}

		_pauseBuildButton.Text = control.IsPaused ? "Resume" : "Pause";
		_pauseBuildButton.Disabled = control.IsStopping;
		_stopBuildButton.Text = control.IsStopping ? "Stopping..." : "Stop";
		_stopBuildButton.Disabled = control.IsStopping;
	}

	private void OnPauseBuildPressed()
	{
		_pipeline.TogglePause(Log);
		RefreshState();
	}

	private void OnStopBuildPressed()
	{
		_pipeline.Stop(Log);
		RefreshState();
	}

	/// <summary>Shows shared launch settings or platform installation controls.</summary>
	private void RefreshDebugDrawer()
	{
		if (RefreshAndroidDrawer()) return;
		if (_settingsTabs is null)
		{
			return;
		}

		string tab = DebugTabs[Mathf.Clamp(_settingsTabs.CurrentTab, 0, DebugTabs.Length - 1)];
		bool isSettings = tab == "Settings";
		bool isModding = tab == "Modding";
		bool isReinstall = tab == "Install";
		bool isSaves = tab == "Saves";

		_settingsPanel.Visible = isSettings;
		_moddingPanel.Visible = isModding;
		_reinstallPanel.Visible = isReinstall;
		_savesPanel.Visible = isSaves;
		if (isSaves)
		{
			_savesPanel.Refresh(_launch.GameIsRunning);
		}

		if (isReinstall)
		{
			bool running = _pipeline.Running;
			_reinstallPanel.Refresh(running, _launch.GameIsRunning);
			_reinstallPanel.SetBuildControls(
				_pipeline.Control is not null,
				_pipeline.Control?.IsPaused ?? false,
				_pipeline.Control?.IsStopping ?? false);
			_reinstallPanel.SetProgress(
				running ? _pipeline.Label : string.Empty,
				running ? _pipeline.DisplayStatus()
						: (_pipeline.Error.Length != 0 ? _pipeline.Status : string.Empty),
				_pipeline.Error);
		}
	}

	private void RefreshInfoBody()
	{
		if (_infoTabs is null || _infoBody is null)
		{
			return;
		}
		if (ShowAndroidInstallationLog()) return;

		if (_infoTabList.Count == 0)
		{
			_infoBody.Text = string.Empty;
			return;
		}

		LauncherInfoTab tab = _infoTabList[Mathf.Clamp(_infoTabs.CurrentTab, 0, _infoTabList.Count - 1)];
		_infoBody.Text = tab.Source switch
		{
			LauncherInfoSource.ProjectChangeLog => BuildChangeLogText(),
			LauncherInfoSource.ProjectContributors => BuildCreditsText(),
			_ => tab.Text,
		};
	}

	private string BuildChangeLogText()
	{
		if (_commitsLoading)
		{
			return "[color=#8c7b6b]Fetching latest updates from GitHub...[/color]";
		}

		if (!_commitsLoaded)
		{
			return "[color=#900c3f]Failed to load news from GitHub.[/color]\n" +
				   "[color=#8c7b6b]Check network connection or relaunch the launcher.[/color]";
		}

		if (_commits.Count == 0)
		{
			return "[color=#8c7b6b]No commits found for this repository.[/color]";
		}

		var builder = new System.Text.StringBuilder();
		builder.Append("[color=#daa520][font_size=16]Latest Recomp Developments[/font_size][/color]\n\n");
		foreach (GitCommit commit in _commits)
		{
			builder.Append("[color=#2b221b][b]• ").Append(commit.Message).Append("[/b][/color]\n");
			builder.Append("[color=#8c7b6b][font_size=11]   commit [color=#daa520]").Append(commit.Sha).Append("[/color] by ").Append(commit.Author);
			if (!string.IsNullOrEmpty(commit.Date))
			{
				builder.Append(" on ").Append(commit.Date);
			}
			builder.Append("[/font_size][/color]\n\n");
		}

		return builder.ToString().TrimEnd();
	}

	private string BuildCreditsText()
	{
		if (_contributorsLoading)
		{
			return "[color=#8c7b6b]Loading contributors from GitHub...[/color]";
		}

		if (!_contributorsLoaded)
		{
			return "[color=#900c3f]Failed to load contributors from GitHub.[/color]\n" +
				   "[color=#8c7b6b]Check network connection or relaunch the launcher.[/color]";
		}

		if (_contributors.Count == 0)
		{
			return "[color=#8c7b6b]No contributors found for this repository.[/color]";
		}

		var builder = new System.Text.StringBuilder();
		builder.Append("[color=#daa520][font_size=16]Heroes of the Guild (Contributors)[/font_size][/color]\n");
		builder.Append("[color=#8c7b6b][font_size=12]").Append(_contributors.Count).Append(" developers and modders[/font_size][/color]\n\n");
		foreach (GitContributor contributor in _contributors)
		{
			builder.Append("[color=#2b221b][b]@").Append(contributor.Login).Append("[/b][/color]");
			if (contributor.Name.Length != 0)
			{
				builder.Append(" [color=#8c7b6b](").Append(contributor.Name).Append(")[/color]");
			}
			builder.Append(" [color=#daa520]•[/color] [color=#8c7b6b]").Append(contributor.Contributions).Append(" contributions[/color]\n");
		}

		return builder.ToString().TrimEnd();
	}

	private async void InitPatches()
	{
		string? response = null;
		if (PatchSource.OnlineMetadataEnabled())
		{
			response = await GitHubApi.HttpGetAsync(PatchSource.GetPatchesLocation());
		}

		_patches.Init(response);
		CallDeferred(nameof(ShowPatchesWindowIfAny));
	}

	private void ShowPatchesWindowIfAny()
	{
		if (Engine.IsEditorHint() || !_patches.HasPatches)
		{
			return;
		}

		var window = new PatchesWindow(_patches, null, LauncherScale.Font(LauncherTokens.BodySize), _patchesStyle);
		AddChild(window);
		AnimateIn(window);
	}

	private async void FetchGitHubData()
	{
		if (!Ini.GetBool("EnableOnlineMetadata", false, "Github"))
		{
			_commitsLoading = false;
			_contributorsLoading = false;
			RefreshInfoBody();
			return;
		}

		string repo = GitHubApi.GetRepositoryName();
		string branch = GitHubApi.GetRepositoryBranch();

		List<GitCommit>? commits = await GitHubApi.FetchCommitsAsync(repo, branch);
		_commits = commits ?? new List<GitCommit>();
		_commitsLoaded = commits is not null;
		_commitsLoading = false;
		CallDeferred(nameof(RefreshInfoBody));

		List<GitContributor>? contributors = await GitHubApi.FetchContributorsAsync(repo);
		_contributors = contributors ?? new List<GitContributor>();
		_contributorsLoaded = contributors is not null;
		_contributorsLoading = false;
		CallDeferred(nameof(RefreshInfoBody));
	}

	/// <summary>Routes the user's primary action to the platform's installation or game launch path.</summary>
	private void OnPrimaryPressed()
	{
		if (_phone != null) { PrimaryAndroid(); return; }
		if (Engine.IsEditorHint())
		{
			return;
		}

		if (!Installer.AssetsExist())
		{
			StartInstallFlow();
			return;
		}

		if (Recompile.GameCodeBuild.NeedsBuild(out string gameCodeReason))
		{
			Log(gameCodeReason + " Building it now.");
			StartGameCodeBuild();
			return;
		}

		if (ManagedDownloads.HasDownloadSource() &&
			(!Installer.InstalledBuildExists() || _updates.UpdateAvailable))
		{
			StartBuildDownload();
			return;
		}

		// THE GAME IS A CHILD PROCESS. Godot still hosts the guest and owns the renderer —
		// what changed is WHICH process does it.
		//
		// The window used to open in this process, which left the launcher standing but unable
		// to Play a second time: `RefiiHostStartup` refuses a restart because the recompiled
		// guest takes fixed memory reservations once per process. Spawning a copy of this
		// executable gives every Play a clean address space, and keeps a guest crash out of
		// the launcher.
		if (_launch.GameIsRunning)
		{
			Log("The game is already running.");
			return;
		}

		// TRACY'S ADVANCED DATA NEEDS AN ELEVATED GAME on Windows (kernel event tracing), and the
		// launcher never runs elevated or runs Tracy itself — so the question is put here, as the
		// player commits to the launch. Unattended, nobody can answer a permission prompt.
		if (Refii.Game.GameElevatedLaunch.Wanted(Refii.Core.Engine_RefiiConfig.Read(out _)))
		{
			if (Unattended)
			{
				Log("Unattended: starting the game without Tracy's advanced data (it needs Administrator).");
				StartGame(elevated: false);
				return;
			}

			AskChoice("Run the game as Administrator?",
				"Tracy's advanced data is on (tracy_advanced): CPU sampling with call stacks and " +
				"context switches for the game's threads. On Windows that comes from the kernel's " +
				"event tracing, which only an Administrator may start. Windows will ask for " +
				"permission next. Without it the game runs normally and those parts of a capture " +
				"stay empty.",
				new ChoiceDialog.Choice("Run as Administrator", () => StartGame(elevated: true)),
				new ChoiceDialog.Choice("Run without the advanced data", () => StartGame(elevated: false)));
			return;
		}

		StartGame(elevated: false);
	}

	private void StartGame(bool elevated)
	{
		if (_launch.GameIsRunning)
		{
			Log("The game is already running.");
			return;
		}

		if (!Refii.Game.GameChildProcess.Spawn(
				elevated, out Refii.Launcher.Core.ProcessHandle handle, out string spawnError))
		{
			Log("Failed to start the game. " + spawnError);
			return;
		}

		_launch.AdoptGameProcess(handle);
		_launcherModeBeforePlay = DisplayServer.WindowGetMode((int)DisplayServer.MainWindowId);
		if (_launcherModeBeforePlay == DisplayServer.WindowMode.Minimized)
		{
			_launcherModeBeforePlay = DisplayServer.WindowMode.Windowed;
		}

		DisplayServer.WindowSetMode(
			DisplayServer.WindowMode.Minimized, (int)DisplayServer.MainWindowId);
		_standingDownForGame = true;
		Log($"Started the game{(elevated ? " as Administrator" : string.Empty)} (pid {handle.ProcessId}); " +
			"the launcher will return when it closes.");
		RefreshState();
	}

	private async void StartUpdateCheck(string logPrefix)
	{
		UpdateCheckResult? result = await _updates.CheckAsync(logPrefix);
		if (result is null)
		{
			return;
		}

		_latestVersions = result.LatestVersions;
		if (result.Message.Length != 0)
		{
			Log(result.Message);
		}

		RefreshState();
	}

	private void StartBuildDownload()
	{
		_downloadProgress = _updates.StartDownload(_latestVersions, savedAnyVersion =>
		{
			CallDeferred(MethodName.OnBuildDownloadFinished, savedAnyVersion);
		});

		RefreshState();
	}

	private void OnBuildDownloadFinished(bool savedAnyVersion)
	{
		if (!savedAnyVersion)
		{
			StartUpdateCheck("Post-download check: ");
		}

		RefreshState();
	}

	private void UpdateArtCredit()
	{
		string credit = _backgrounds.CurrentCredit;
		float alpha = _backgrounds.CurrentCreditAlpha;

		_artCredit.Visible = credit.Length != 0 && alpha > 0.01f;
		if (!_artCredit.Visible)
		{
			return;
		}

		_artCredit.Text = credit;
		_artCredit.Modulate = new Color(1, 1, 1, alpha);
	}

	/// <summary>The whole install: the disc, the game code, and the classic shaders when asked.</summary>
	private void StartInstall(string isoPath, string xexpPath, bool classicShaders)
	{
		var steps = new List<Install.InstallPipeline.Step>
		{
			new(Install.InstallPipeline.StepKind.GameData, isoPath, xexpPath),
			new(Install.InstallPipeline.StepKind.GameCode),
		};
		if (classicShaders)
		{
			steps.Add(new(Install.InstallPipeline.StepKind.ClassicShaders));
		}
		StartSteps(steps);
	}

	/// <summary>Runs the game code build on its own, from the primary button or the Install tab.</summary>
	private void StartGameCodeBuild()
	{
		if (_pipeline.Running || _launch.GameIsRunning)
		{
			return;
		}

		AskBuildSpeed("Build Game Code", useAllCores =>
		{
			Recompile.GameCodeBuild.UseAllCores = useAllCores;
			StartSteps(new[] { new Install.InstallPipeline.Step(Install.InstallPipeline.StepKind.GameCode) });
		});
	}

	private void StartShaderBuild() =>
		StartSteps(new[] { new Install.InstallPipeline.Step(Install.InstallPipeline.StepKind.ClassicShaders) });

	private void StartAssetPrebuild() =>
		StartSteps(new[] { new Install.InstallPipeline.Step(Install.InstallPipeline.StepKind.ModernizedAssets) });

	private void StartSteps(IReadOnlyList<Install.InstallPipeline.Step> steps)
	{
		if (_pipeline.Running || _launch.GameIsRunning)
		{
			return;
		}

		_ = RunPipelineAsync(steps);
	}

	/// <summary>
	/// Runs the steps and settles the launcher afterwards: the step ticks, the mounted assets
	/// after a fresh install, and the unattended run's halt on a failed game code build.
	/// </summary>
	private async System.Threading.Tasks.Task RunPipelineAsync(IReadOnlyList<Install.InstallPipeline.Step> steps)
	{
		// THE SETTINGS AS THEY ARE ON DISK. The player has very likely just chosen a look in the
		// settings panel, and building for the settings they abandoned would be a long wait for
		// the wrong artefacts.
		_settingsPanel.SaveIfDirty();
		RefreshState();

		Install.InstallPipeline.Outcome outcome = await _pipeline.RunAsync(steps, Log);

		Installer.InvalidateReinstallStepStatus();
		Recompile.GameCodeBuild.InvalidateStatus();

		bool installed = false;
		foreach (Install.InstallPipeline.Step step in steps)
		{
			installed |= step.Kind == Install.InstallPipeline.StepKind.GameData;
		}

		if (outcome.Ok)
		{
			Log(installed ? "Installation complete." : "Done.");
			if (installed)
			{
				MountGameAssets();
			}
		}
		else
		{
			if (!outcome.Stopped)
			{
				string stepName = outcome.FailedStep.HasValue ? outcome.FailedStep.Value.ToString() : "Install";
				Log($"{stepName} failed: {outcome.Error}");
			}

			if (!outcome.Stopped && Unattended &&
				outcome.FailedStep == Install.InstallPipeline.StepKind.GameCode)
			{
				// Pressing again would only re-run the same generator on the same inputs.
				_unattendedHalted = true;
				Log("Unattended: the game code build failed; not pressing again.");
			}
		}

		RefreshState();
	}

	private void StartInstallFlow()
	{
		var dialog = new InstallDialog(null, LauncherScale.Font(LauncherTokens.BodySize), _baseStyle);

		// ISO and title update, then the one build decision, then the whole pipeline runs unattended:
		// extract, generate, compile. Asking again afterwards would strand the install
		// half-done behind a second button nobody knows to press.
		dialog.InstallRequested += (iso, xexp, classicShaders) => AskBuildSpeed(
			"Install " + Installer.GetGameTitle(),
			useAllCores =>
			{
				Recompile.GameCodeBuild.UseAllCores = useAllCores;
				StartInstall(iso, xexp, classicShaders);
			});

		AddChild(dialog);
	}

	/// <summary>Asks how many threads the compile may use, then proceeds.</summary>
	/// <summary>
	/// UNATTENDED MODE — `REFII_AUTO_LAUNCH=1`. Drives the launcher with nobody at the keyboard:
	/// the primary button is pressed until a game window opens, and every dialog that would wait
	/// for a person answers itself.
	///
	/// It is the SAME path a player takes, not a shortcut around it. `OnPrimaryPressed` already
	/// is the whole flow — install the assets, generate and compile the game code, download a
	/// missing runtime, then open the window — so unattended mode presses that one button and
	/// lets it decide. Copying a prebuilt `librefii_generated.so` into the tree instead would
	/// test an install nobody performed.
	/// </summary>
	private static readonly bool Unattended =
		System.Environment.GetEnvironmentVariable("REFII_AUTO_LAUNCH") == "1";

	/// <summary>Seconds since unattended mode last pressed the button; it must not spam.</summary>
	private double _autoPressAge;

	/// <summary>
	/// Presses the primary action when nothing is in flight. Gated on the same three latches the
	/// button's own enable uses, so it can never re-enter a build or an install that is running.
	/// </summary>
	private void DriveUnattended(double delta)
	{
		_autoPressAge += delta;
		if (_autoPressAge < AutoPressInterval)
		{
			return;
		}

		// THE WINDOW IS IN THIS PROCESS, so `GameIsRunning` — which watches for a spawned
		// executable — never becomes true and cannot end the loop. Ask the window itself.
		if (Refii.Game.GameWindow.IsOpen || _unattendedHalted)
		{
			return;
		}

		if (_pipeline.Running || _launch.GameIsRunning || _downloadProgress is not null)
		{
			// Something is running. Hold the clock at zero so the next press waits a full
			// interval after it finishes rather than firing the instant it does.
			_autoPressAge = 0.0;
			return;
		}

		_autoPressAge = 0.0;
		Log("Unattended: pressing the primary action.");
		OnPrimaryPressed();
	}

	/// <summary>Long enough that a state change settles before the next press is considered.</summary>
	private const double AutoPressInterval = 3.0;

	/// <summary>
	/// Set when a game-code build fails. Pressing again would only re-run the same generator
	/// on the same inputs and fail the same way (b170 spun through four zig compiles before
	/// anyone looked), so an unattended run stops here and leaves the failure on screen.
	/// </summary>
	private bool _unattendedHalted;

	private void AskBuildSpeed(string heading, Action<bool> proceed)
	{
		// Nobody is here to choose. All threads is what an unattended run wants anyway.
		if (Unattended)
		{
			proceed(true);
			return;
		}

		// The only thing about a long build the player can usefully choose, asked as they commit
		// to it rather than found in Settings after the wait.
		int threads = System.Environment.ProcessorCount;
		int half = Math.Max(1, threads / 2);
		AskChoice(heading,
			"Your game's code has to be compiled on this machine — around 150,000 " +
			"functions. It takes a while, and it is only done once.",
			new ChoiceDialog.Choice(
				"Use " + half + " of " + threads + " threads  —  slower, PC stays usable", () => proceed(false)),
			new ChoiceDialog.Choice(
				"Use all " + threads + " threads  —  fastest, PC will be sluggish", () => proceed(true)));
	}

	private void AskChoice(string heading, string text, params ChoiceDialog.Choice[] choices)
	{
		var dialog = new ChoiceDialog(null, LauncherScale.Font(LauncherTokens.BodySize), _baseStyle, heading, text, choices);
		AddChild(dialog);
		AnimateIn(dialog);
	}

	/// <summary>Updates shared presentation and observes platform progress without owning the background worker.</summary>
	/// <param name="delta">Frame interval in seconds.</param>
	public override void _Process(double delta)
	{
		if (_phone != null) { ProcessAndroid(delta); return; }
		// NOTHING TO DRIVE IN THE CHILD. `_Ready` returns before the launcher is built there,
		// so `_launch`, `_patches` and every panel are still null — this whole method would
		// dereference them. The game's own nodes do the work in that process. The same holds
		// for the elevated relay.
		if (Refii.Game.GameChildProcess.IsChild || Refii.Game.GameElevatedLaunch.IsRelay)
		{
			return;
		}

		// THE GAME IS A SEPARATE PROCESS NOW, so its exit is the signal to come back. Polled
		// rather than event-driven: `Process.Exited` fires on a thread pool thread and every
		// call below it touches the scene tree.
		if (_standingDownForGame && !_launch.GameIsRunning)
		{
			_standingDownForGame = false;

			// However the game ended (quit to desktop, a window close, a crash), only the child
			// process ends: the launcher always comes back.

			DisplayServer.WindowSetMode(_launcherModeBeforePlay, (int)DisplayServer.MainWindowId);
			DisplayServer.WindowMoveToForeground((int)DisplayServer.MainWindowId);
			Log("The game closed; the launcher is back.");
			RefreshState();
		}

		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("launcherapp");
		_backgrounds.Update((float)delta);
		UpdateArtCredit();

		if (Engine.IsEditorHint())
		{
			return;
		}

		// A running step moves its status from worker threads; the label is painted here.
		if (_pipeline.Running)
		{
			RefreshState();
		}

		if (_downloadProgress is not null || _launch.GameIsRunning != _primaryButton.Disabled)
		{
			RefreshState();
		}

		if (Unattended)
		{
			DriveUnattended(delta);
		}
	}
}
