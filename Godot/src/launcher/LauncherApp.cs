using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Godot;
using Refii.Launcher.Core;
using Refii.Launcher.Lua;
using Refii.Launcher.Native;
using Refii.Launcher.Updates;

namespace Refii.Launcher;

[Tool]
public partial class LauncherApp : Control
{
	private const float ArtCreditHeight = 26.0f;

	private static readonly string[] NewsTabs =
	{
		"Change Log", "Credits",
	};

	private static readonly string[] DebugTabs =
	{
		"Settings", "Modding", "Reinstall",
	};
	private static readonly string[] FileMenuItems =
	{
		"Set Install Folder", "Uninstall", "", "Verify Lua Workspace",
		"Reset All Lua Files", "Reset Lua File...", "", "Show Announcements", "Show Debug Panel", "Close",
	};
	private static readonly string[] AboutMenuItems =
	{
		"Fable 2 Recompiled", "RexGlue",
	};

	private bool _commitsLoading = true;
	private bool _commitsLoaded;
	private List<GitCommit> _commits = new();

	private bool _contributorsLoading = true;
	private bool _contributorsLoaded;
	private List<GitContributor> _contributors = new();

	private bool _installInProgress;
	private string _installLabel = string.Empty;
	private string _installStatus = string.Empty;
	private string _installError = string.Empty;
	private long _installCopiedBytes;
	private long _installTotalBytes;
	private int _installCompletedFiles;
	private int _installTotalFiles;
	private string _installCurrentFile = string.Empty;
	private bool _installExtractingIso;

	// Set while the game code is being generated and compiled. The byte-based fraction
	// cannot describe that phase — nothing is being copied — so it reports its own.
	private float _installStepFraction = -1.0f;
	private ManagedDownloadProgress? _downloadProgress;
	private LauncherOps? _ops;
	private string _opsStatus = string.Empty;

	private readonly List<string> _consoleLines = new();

	private Patches _patches = null!;
	private LaunchController _launch = null!;
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
	private RichTextLabel _wordmark = null!;
	private MenuButton _fileMenu = null!;
	private MenuButton _aboutMenu = null!;
	private Panel _warningPanel = null!;
	private Label _warningLabel = null!;
	private Panel _actionPanel = null!;
	private Button _primaryButton = null!;
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
	private float _menuBarHeight = 72.0f;

	public override void _Ready()
	{
		LauncherPaths.OverrideLauncherDirectory = ResolveLauncherDirectory();
		Platform.RuntimeDirectoryOverride = ResolveRuntimeDirectory();

		// The recompiled game module has to land where the runtime looks for it, which is
		// beside the other native libraries — the per-architecture staging root, the one
		// location that survives an export. Same convention Engine_TexEnhance uses for the
		// upscaler weights.
		Refii.Core.Engine_AppRoot.EnsureLayout();
		Recompile.GameCodeBuild.BinDirectory = Refii.Core.Engine_AppRoot.BinDirectory;
		if (!Engine.IsEditorHint())
		{
			LauncherOps.Initialize(LauncherPaths.OverrideLauncherDirectory);
		}

		LoadLauncherConfig();
		LauncherTheme.Load();

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
			DisplayServer.WindowSetTitle(LauncherTheme.WindowTitle);
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
		_wordmark = GetNode<RichTextLabel>("MenuBar/HeaderCol/Wordmark");
		_fileMenu = GetNode<MenuButton>("MenuBar/HeaderCol/MenuRow/FileMenu");
		_aboutMenu = GetNode<MenuButton>("MenuBar/HeaderCol/MenuRow/AboutMenu");
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
	private const string BundledBackground = "res://assets/launcher/backgrounds/01.png";

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

	private void ReapplyWindowTitle()
	{
		DisplayServer.WindowSetTitle(LauncherTheme.WindowTitle);
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

	private Texture2D? LoadBackgroundTexture()
	{
		var bundled = ResourceLoader.Load<Texture2D>(BundledBackground);
		if (bundled is not null)
		{
			return bundled;
		}

		foreach (string root in new[]
				 {
					 ProjectSettings.GlobalizePath("res://assets/launcher/backgrounds"),
					 LauncherPaths.GetLauncherAssetPath("backgrounds"),
				 })
		{
			if (!Directory.Exists(root))
			{
				continue;
			}

			string[] files = Directory.GetFiles(root);
			Array.Sort(files, StringComparer.OrdinalIgnoreCase);
			foreach (string file in files)
			{
				string extension = Path.GetExtension(file).ToLowerInvariant();
				if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
				{
					continue;
				}

				Image image = Image.LoadFromFile(file);
				if (image is not null)
				{
					return ImageTexture.CreateFromImage(image);
				}
			}
		}

		return null;
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
		foreach (MenuButton menu in new[] { _fileMenu, _aboutMenu })
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
		_reinstallPanel.Configure(null, body, _infoStyle);
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
			2 => _reinstallPanel,
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
		_backgrounds = new BackgroundManager(_background, _backgroundNext);
		_backgrounds.Initialize();

		// Toolbar: no bar at all. The artwork runs to the top of the window and
		// the wordmark + menus float over it with a soft shadow for legibility.
		_menuBar.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) });

		_wordmark.BbcodeEnabled = true;
		_wordmark.Text =
			"[color=#e8dcc2]FABLE [/color][color=#daa520]2[/color][color=#e8dcc2] RECOMPILED[/color]";
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

		foreach (MenuButton menu in new[] { _fileMenu, _aboutMenu })
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
		_warningLabel.Text = LauncherTheme.WarningText;
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

		ApplySystemFont(_artCredit, LauncherScale.Font(LauncherTokens.CaptionSize));
		_artCredit.AddThemeColorOverride("font_color", new Color(LauncherTokens.Cream, 0.72f));
		_artCredit.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.75f));
		_artCredit.AddThemeConstantOverride("shadow_offset_x", 1);

		// Announcements Drawer (Left Side):
		var infoBox = new StyleBoxFlat { BgColor = LauncherTokens.ParchmentGlass };
		infoBox.BorderColor = new Color(LauncherTokens.Gold, 0.45f);
		infoBox.SetBorderWidthAll(1);
		infoBox.SetCornerRadiusAll(LauncherTokens.RadiusMd);
		infoBox.ShadowColor = new Color(0, 0, 0, 0.4f);
		infoBox.ShadowSize = 18;
		infoBox.ShadowOffset = new Vector2(0, 6);
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

		_reinstallPanel.ReinstallRequested += StartInstallFlow;
		_reinstallPanel.UnpackBanksRequested += () => StartOps(ops => ops.ExtractGameBanks());
		_reinstallPanel.BuildGameCodeRequested += StartGameCodeBuild;
		_reinstallPanel.DecompileLuaRequested += () => StartOps(ops => ops.RebuildLuaWorkspace());
		_reinstallPanel.CompileShadersRequested += () => StartOps(ops => ops.CompileShaders());
		_reinstallPanel.BuildModernizedAssetsRequested += StartAssetPrebuild;

		ApplyScale();
	}

	private void WireMenus()
	{
		PopulateMenu(_fileMenu, FileMenuItems, OnFileMenu);
		PopulateMenu(_aboutMenu, AboutMenuItems, OnAboutMenu);
		StylePopup(_fileMenu.GetPopup());
		StylePopup(_aboutMenu.GetPopup());
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

	private void WireTabs()
	{
		_infoTabs.ClearTabs();
		foreach (string tab in NewsTabs)
		{
			_infoTabs.AddTab(tab);
		}
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
		switch (item)
		{
			case "Set Install Folder":
				ShowInstallFolderDialog();
				break;

			case "Uninstall":
				Log(Installer.UninstallManagedFiles(out string uninstallLog)
					? uninstallLog
					: "No Fable 2 launcher-managed files to uninstall.");
				RefreshState();
				break;

			case "Verify Lua Workspace":
				StartOps(ops => ops.VerifyLuaWorkspace());
				break;

			case "Reset All Lua Files":
				StartOps(ops => ops.ResetLuaWorkspace());
				break;

			case "Reset Lua File...":
				ShowResetLuaFileDialog();
				break;

			case "Show Info Panel":
			case "Show Announcements":
				ShowDrawer();
				break;

			case "Show Debug Panel":
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

	private void OnAboutMenu(string item)
	{
		switch (item)
		{
			case "Fable 2 Recompiled":
				Platform.TryOpenUrl("https://github.com/rexglue/refii");
				break;
			case "RexGlue":
				Platform.TryOpenUrl("https://github.com/rexglue/rexglue-sdk");
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

		_menuBar.Position = Vector2.Zero;
		_menuBar.Size = new Vector2(viewport.X, _menuBarHeight);

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

	private void RefreshState()
	{
		bool assetsExist = Installer.AssetsExist();
		bool installedBuildExists = Installer.InstalledBuildExists();
		bool canDownloadBuild = ManagedDownloads.HasDownloadSource();
		bool updateAvailable = _updates.UpdateAvailable;
		bool isoInProgress = _installInProgress || _ops is not null;

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
			label = ProgressLabel.BuildButtonLabel(_installLabel, _installStatus);
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
			// The whole install runs from here: extract the ISO, copy the XEXP, generate
			// the game code and compile it.
			label = "Install " + gameTitle;
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

		float fraction = InstallProgressFraction();
		_progressClip.Visible = !enabled && fraction > 0.0f;
		_progressClip.Position = _primaryButton.Position;
		_progressClip.Size = new Vector2(_primaryButton.Size.X * fraction, _primaryButton.Size.Y);
		_progressFill.Position = Vector2.Zero;
		_progressFill.Size = _primaryButton.Size;
		ApplyFont(_primaryButton, compactFont
			? LauncherScale.Font(LauncherTokens.BodySize)
			: LauncherScale.Font(LauncherTokens.DisplaySize));

		RefreshInfoBody();
	}

	private void RefreshDebugDrawer()
	{
		if (_settingsTabs is null)
		{
			return;
		}

		string tab = DebugTabs[Mathf.Clamp(_settingsTabs.CurrentTab, 0, DebugTabs.Length - 1)];
		bool isSettings = tab == "Settings";
		bool isModding = tab == "Modding";
		bool isReinstall = tab == "Reinstall";

		_settingsPanel.Visible = isSettings;
		_moddingPanel.Visible = isModding;
		_reinstallPanel.Visible = isReinstall;

		if (isReinstall)
		{
			_reinstallPanel.Refresh(
				_installInProgress || _ops is not null || _prebuilding, _launch.GameIsRunning);

			if (_prebuilding || _prebuildLabel.Length != 0)
			{
				_reinstallPanel.SetProgress(_prebuildLabel, _prebuildStatus);
			}
			else
			{
				_reinstallPanel.SetProgress(
					_ops is not null ? _ops.OperationLabel : _installLabel,
					_ops is not null ? _ops.StatusMessage : _installStatus,
					_ops is not null ? string.Empty : _installError);
			}
		}
	}

	private void RefreshInfoBody()
	{
		if (_infoTabs is null || _infoBody is null)
		{
			return;
		}

		string tab = NewsTabs[Mathf.Clamp(_infoTabs.CurrentTab, 0, NewsTabs.Length - 1)];
		_infoBody.Text = tab switch
		{
			"Change Log" => BuildChangeLogText(),
			"Credits" => BuildCreditsText(),
			_ => string.Empty,
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

	private void OnPrimaryPressed()
	{
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

		// Godot hosts the guest — open a game window rather than spawning refii.
		//
		// The old path launched the external executable, which brought its own window,
		// its own renderer and its own frame loop. Under the current design Godot owns
		// all three and the guest runs headless inside this process, so there is nothing
		// to spawn: the "runtime" is the host bridge, not an executable on disk.
		if (Refii.Game.GameWindow.Open(this) is null)
		{
			Log("A game window is already open.");
			return;
		}

		Log("Opened the game window; the guest runs inside this process.");
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

	private async void StartInstall(string isoPath, string xexpPath)
	{
		_installInProgress = true;
		_installLabel = "Installing";
		_installStatus = "Starting installation...";
		_installError = string.Empty;
		RefreshState();

		if (!Installer.InstallBasePathLeavesWindowsPathRoom(
				Installer.GetInstallBasePath(), out string pathError))
		{
			FailInstall("Install path is too long.", pathError);
			return;
		}

		string destination = Installer.GetGameDataDirectoryPath();
		_installStatus = "Extracting ISO...";
		RefreshState();
		Log("Extracting " + isoPath + " to " + destination);

		System.Threading.Interlocked.Exchange(ref _installCopiedBytes, 0);
		System.Threading.Interlocked.Exchange(ref _installTotalBytes, 0);
		Volatile.Write(ref _installCompletedFiles, 0);
		Volatile.Write(ref _installTotalFiles, 0);
		_installCurrentFile = string.Empty;
		_installExtractingIso = true;

		var result = await System.Threading.Tasks.Task.Run(() =>
		{
			bool ok = Xiso.XisoExtractor.ExtractImage(
				isoPath,
				destination,
				out string error,
				progress =>
				{
					System.Threading.Interlocked.Exchange(ref _installCopiedBytes, progress.CopiedBytes);
					System.Threading.Interlocked.Exchange(ref _installTotalBytes, progress.TotalBytes);
					Volatile.Write(ref _installCompletedFiles, progress.CompletedFiles);
					Volatile.Write(ref _installTotalFiles, progress.TotalFiles);
					_installCurrentFile = progress.CurrentFile;
				});
			return (ok, error);
		});

		_installExtractingIso = false;

		if (!result.ok)
		{
			FailInstall(_installStatus, result.error);
			return;
		}

		_installStatus = "Copying XEXP...";
		RefreshState();

		if (!Installer.CopyGameExecutable(
				xexpPath, Installer.GetGameMarkerPath(), out string copyError))
		{
			FailInstall(_installStatus, copyError);
			return;
		}

		// The game code, generated and compiled from the executable just installed. This is
		// the longest part of an install by a wide margin — it is a compiler building
		// roughly 150,000 functions — but it is not optional: the recompiled guest is
		// derived from the player's own image and cannot ship with the app.
		if (!await BuildGameCodeAsync())
		{
			return;
		}

		_installInProgress = false;
		_installLabel = string.Empty;
		_installStatus = string.Empty;
		_installError = string.Empty;
		Log("Installation complete.");
		RefreshState();
	}

	/// <summary>Runs the game code build on its own, from the Reinstall tab.</summary>
	private void StartGameCodeBuild()
	{
		if (_installInProgress || _ops is not null)
		{
			return;
		}

		AskBuildSpeed("Build Game Code", useAllCores =>
		{
			Recompile.GameCodeBuild.UseAllCores = useAllCores;
			RunGameCodeBuild();
		});
	}

	private async void RunGameCodeBuild()
	{

		_installInProgress = true;
		_installError = string.Empty;

		bool ok = await BuildGameCodeAsync();

		Recompile.GameCodeBuild.InvalidateStatus();

		if (ok)
		{
			_installInProgress = false;
			_installLabel = string.Empty;
			_installStatus = string.Empty;
			Log("Game code build complete.");
		}

		RefreshState();
	}

	/// <summary>
	/// Generates and compiles the game code, reporting into the install progress UI.
	/// </summary>
	/// <returns>False when it failed and the install has already been marked failed.</returns>
	private async System.Threading.Tasks.Task<bool> BuildGameCodeAsync()
	{
		_installLabel = "Building game code";
		_installStatus = "Preparing game code...";
		_installStepFraction = 0.0f;
		RefreshState();
		Log("Building the recompiled game module from the installed executable.");

		var result = await Recompile.GameCodeBuild.RunAsync(
			progress =>
			{
				_installStatus = progress.Status;
				_installStepFraction = progress.Fraction;
			},
			CancellationToken.None);

		_installStepFraction = -1.0f;

		if (!result.Ok)
		{
			FailInstall("Building game code failed.", result.Error);
			return false;
		}

		Log("Game code built: " + Recompile.GameCodeBuild.ModulePath);
		return true;
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

	private static float ParseCountedFraction(string status)
	{
		int slash = status.LastIndexOf('/');
		if (slash <= 0)
		{
			return 0.0f;
		}

		int start = slash - 1;
		while (start >= 0 && char.IsDigit(status[start]))
		{
			start--;
		}

		int end = slash + 1;
		while (end < status.Length && char.IsDigit(status[end]))
		{
			end++;
		}

		if (start + 1 >= slash || end <= slash + 1)
		{
			return 0.0f;
		}

		if (!int.TryParse(status[(start + 1)..slash], out int completed) ||
			!int.TryParse(status[(slash + 1)..end], out int total) ||
			total <= 0)
		{
			return 0.0f;
		}

		return Mathf.Clamp((float)completed / total, 0.0f, 1.0f);
	}

	private float InstallProgressFraction()
	{
		if (_installStepFraction >= 0.0f)
		{
			return Mathf.Clamp(_installStepFraction, 0.0f, 1.0f);
		}

		if (_ops is not null)
		{
			return ParseCountedFraction(_ops.StatusMessage);
		}

		long total = System.Threading.Interlocked.Read(ref _installTotalBytes);
		if (!_installInProgress || total <= 0)
		{
			return 0.0f;
		}

		long copied = System.Threading.Interlocked.Read(ref _installCopiedBytes);
		return Mathf.Clamp((float)copied / total, 0.0f, 1.0f);
	}

	private string BuildIsoStatus()
	{
		int total = Volatile.Read(ref _installTotalFiles);
		if (total <= 0)
		{
			return "Extracting ISO...";
		}

		string status = "Extracting ISO... " + Volatile.Read(ref _installCompletedFiles) + "/" + total;
		string file = _installCurrentFile;
		return file.Length == 0 ? status : status + " (" + file + ")";
	}

	private void FailInstall(string stage, string error)
	{
		_installInProgress = false;
		_installStatus = stage;
		_installError = error.Length == 0 ? "Game installation failed" : error;
		Log(_installError);
		RefreshState();
	}

	private void StartInstallFlow()
	{
		var dialog = new InstallDialog(null, LauncherScale.Font(LauncherTokens.BodySize), _baseStyle);

		// ISO/XEXP, then the one build decision, then the whole pipeline runs unattended:
		// extract, generate, compile. Asking again afterwards would strand the install
		// half-done behind a second button nobody knows to press.
		dialog.InstallRequested += (iso, xexp) => AskBuildSpeed(
			"Install " + Installer.GetGameTitle(),
			useAllCores =>
			{
				Recompile.GameCodeBuild.UseAllCores = useAllCores;
				StartInstall(iso, xexp);
			});

		AddChild(dialog);
	}

	/// <summary>Asks how many cores the compile may use, then proceeds.</summary>
	private void AskBuildSpeed(string heading, Action<bool> proceed)
	{
		var dialog = new BuildSpeedDialog(_font, TextFontSize, _baseStyle, heading);
		dialog.Chosen += useAllCores => proceed(useAllCores);
		AddChild(dialog);
		AnimateIn(dialog);
	}

	private void ShowResetLuaFileDialog()
	{
		if (_ops is not null)
		{
			return;
		}

		if (!LauncherOps.Available)
		{
			Log("Launcher operations library is unavailable.");
			return;
		}

		using LauncherOps? probe = LauncherOps.Create();
		if (probe is null)
		{
			Log("Failed to create a launcher operations handle.");
			return;
		}

		if (!probe.VerifyLuaWorkspaceSync(out string verifyError))
		{
			if (verifyError.Length != 0)
			{
				Log(verifyError);
			}

			return;
		}

		if (probe.BaselineMissing != 0 || probe.BaselineCorrupt != 0)
		{
			Log("Lua baseline is not ready for per-file reset.");
			return;
		}

		var dialog = new ResetLuaFileDialog(
			probe.GetVerifyEntries(), null, LauncherScale.Font(LauncherTokens.BodySize), _baseStyle);
		dialog.ResetRequested += path => StartOps(ops => ops.ResetLuaWorkspaceFile(path));
		AddChild(dialog);
		AnimateIn(dialog);
	}

	/// <summary>Set while the managed asset prebuild is running on its own thread.</summary>
	private volatile bool _prebuilding;

	/// <summary>The line the reinstall panel shows for it. Written by the worker, read here.</summary>
	private volatile string _prebuildLabel = string.Empty;

	private volatile string _prebuildStatus = string.Empty;

	/// <summary>
	/// Builds every modernized texture and material up front.
	/// </summary>
	/// <remarks>
	/// NOT THROUGH <see cref="StartOps"/>, and it cannot be. That path creates a native
	/// <see cref="LauncherOps"/> handle and polls it; this work is managed C# in
	/// <see cref="Refii.Textures.Engine_TexPrebuild"/> — the decoder, the upscaler and the cache
	/// all live on the Godot side. So it gets its own flag and its own two status strings, and
	/// <c>RefreshState</c> prefers them while it runs.
	///
	/// A BACKGROUND THREAD, because this is minutes of work: on the main thread the launcher would
	/// freeze solid, with no progress, no window, and an operating system offering to kill it.
	/// Background so that closing the launcher mid-run cannot leave a thread holding the cache
	/// open — the entries already written stay written, and the next run skips them.
	///
	/// THE WORKER NEVER TOUCHES A CONTROL. Godot's UI is main-thread only; a worker setting a
	/// Label's text is the fault that works on the developer's machine and crashes on somebody
	/// else's. It writes strings here and <c>RefreshState</c> paints them.
	/// </remarks>
	private void StartAssetPrebuild()
	{
		if (_prebuilding || _ops is not null || _installInProgress)
		{
			return;
		}

		string directory = Refii.Core.Engine_GameData.Resolve();
		if (directory.Length == 0)
		{
			Log("No game data found to build modernized assets from.");
			return;
		}

		// THE SETTINGS AS THEY ARE ON DISK. The player has very likely just chosen a look in the
		// settings panel, and building the cache for the settings they abandoned would be a long
		// wait for the wrong artefacts.
		_settingsPanel.SaveIfDirty();

		// REGISTERED HERE, because the launcher process has never touched a texture and so has
		// never configured the pipeline. Without it the backend is null and the whole run silently
		// does base decodes only — which looks exactly like a successful prebuild.
		Refii.Textures.Engine_TexEnhance.RegisterDefault();

		_prebuilding = true;
		_prebuildLabel = "Build Modernized Assets";
		_prebuildStatus = "Finding what to build...";
		Log($"Building modernized assets from {directory}");
		RefreshState();

		var thread = new System.Threading.Thread(() =>
		{
			try
			{
				Refii.Textures.Engine_TexPrebuild.Progress result =
					Refii.Textures.Engine_TexPrebuild.Run(
						directory,
						progress => _prebuildStatus = progress.Total == 0
							? "Finding what to build..."
							: $"{progress.Done:N0} of {progress.Total:N0} " +
							  $"({100.0 * progress.Done / progress.Total:F0}%) — " +
							  $"built {progress.Built:N0}, failed {progress.Failed:N0}");

				_prebuildStatus = result.Total == 0
					? "Nothing to build — no material records were found."
					: $"Done: built {result.Built:N0} of {result.Total:N0}" +
					  (result.Failed > 0 ? $", {result.Failed:N0} could not be read." : ".");
			}
			catch (Exception e)
			{
				_prebuildStatus = "Failed: " + e.Message;
			}
			finally
			{
				_prebuilding = false;
			}
		})
		{
			IsBackground = true,
			Name = "refii-prebuild",
			Priority = System.Threading.ThreadPriority.BelowNormal,
		};

		thread.Start();
	}

	private void StartOps(Action<LauncherOps> action)
	{
		if (_ops is not null)
		{
			return;
		}

		if (!LauncherOps.Available)
		{
			Log("Launcher operations library is unavailable.");
			return;
		}

		_ops = LauncherOps.Create();
		if (_ops is null)
		{
			Log("Failed to create a launcher operations handle.");
			return;
		}

		action(_ops);
		_opsStatus = string.Empty;
		RefreshState();
	}

	private void PumpOps()
	{
		if (_ops is null)
		{
			return;
		}

		string status = _ops.StatusMessage;
		if (status != _opsStatus)
		{
			_opsStatus = status;
			if (status.Length != 0)
			{
				Log(status);
			}
		}

		if (!_ops.IsComplete && !_ops.HasError)
		{
			_reinstallPanel.SetProgress(_ops.OperationLabel, status);
			return;
		}

		if (_ops.HasError)
		{
			_installLabel = _ops.OperationLabel;
			_installStatus = _ops.StatusMessage;
			_installError = _ops.ErrorMessage;
			Log(_ops.ErrorMessage);
		}
		else
		{
			_installLabel = string.Empty;
			_installStatus = string.Empty;
			_installError = string.Empty;
			Log(_ops.OperationLabel + " complete.");
		}

		_ops.Dispose();
		_ops = null;
		_opsStatus = string.Empty;
		Installer.InvalidateReinstallStepStatus();
		MountGameAssets();
		RefreshState();
	}

	public override void _Process(double delta)
	{
		_backgrounds.Update((float)delta);
		UpdateArtCredit();

		if (Engine.IsEditorHint())
		{
			return;
		}

		PumpOps();

		if (_installInProgress && _installExtractingIso)
		{
			_installStatus = BuildIsoStatus();
			RefreshState();
		}

		if (_downloadProgress is not null || _launch.GameIsRunning != _primaryButton.Disabled)
		{
			RefreshState();
		}
	}
}
