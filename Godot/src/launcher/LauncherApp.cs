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
	private const int TextFontSize = 18;
	private const int ArtCreditFontSize = 17;
	private const float ArtCreditHeight = 26.0f;
	private const float WarningFadeFraction = 0.35f;
	private const int TitleFontSize = 30;

	private static readonly string[] InfoTabs =
	{
		"Change Log", "Credits", "Reinstall", "Modding", "Settings",
	};

	private static readonly string[] FileMenuItems =
	{
		"Set Install Folder", "Uninstall", "", "Verify Lua Workspace",
		"Reset All Lua Files", "Reset Lua File...", "", "Close",
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
	private Label _infoTitle = null!;
	private TabBar _infoTabs = null!;
	private RichTextLabel _infoBody = null!;
	private SettingsPanel _settingsPanel = null!;
	private ModdingPanel _moddingPanel = null!;
	private ReinstallPanel _reinstallPanel = null!;

	private float _menuBarHeight = 26.0f;

	public override void _Ready()
	{
		LauncherPaths.OverrideLauncherDirectory = ResolveLauncherDirectory();
		Platform.RuntimeDirectoryOverride = ResolveRuntimeDirectory();
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
		_fileMenu = GetNode<MenuButton>("MenuBar/MenuRow/FileMenu");
		_aboutMenu = GetNode<MenuButton>("MenuBar/MenuRow/AboutMenu");
		_warningPanel = GetNode<Panel>("WarningPanel");
		_warningLabel = GetNode<Label>("WarningPanel/WarningLabel");
		_actionPanel = GetNode<Panel>("ActionPanel");
		_primaryButton = GetNode<Button>("ActionPanel/PrimaryButton");

		_progressTrack = GetNode<TextureRect>("ActionPanel/ProgressTrack");
		_progressClip = GetNode<Control>("ActionPanel/ProgressClip");
		_progressFill = GetNode<TextureRect>("ActionPanel/ProgressClip/ProgressFill");
		_artCredit = GetNode<Label>("ArtCredit");
		_infoPanel = GetNode<Panel>("InfoPanel");
		_infoTitle = GetNode<Label>("InfoPanel/InfoColumn/InfoTitle");
		_infoTabs = GetNode<TabBar>("InfoPanel/InfoColumn/InfoTabs");
		_infoBody = GetNode<RichTextLabel>("InfoPanel/InfoColumn/InfoBody");
		_settingsPanel = GetNode<SettingsPanel>("InfoPanel/InfoColumn/SettingsPanel");
		_moddingPanel = GetNode<ModdingPanel>("InfoPanel/InfoColumn/ModdingPanel");
		_reinstallPanel = GetNode<ReinstallPanel>("InfoPanel/InfoColumn/ReinstallPanel");
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

		if (IsExportedBuild())
		{
			return GetHostExecutableDirectory();
		}

		string projectDir = ProjectSettings.GlobalizePath("res://");
		string staged = Path.Combine(projectDir, "build");
		if (Directory.Exists(staged))
		{
			return staged.TrimEnd(Path.DirectorySeparatorChar);
		}

		string repoRoot = Path.GetFullPath(Path.Combine(projectDir, ".."));
		if (Directory.Exists(Path.Combine(repoRoot, "assets")))
		{
			return repoRoot.TrimEnd(Path.DirectorySeparatorChar);
		}

		return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
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
				foreach (string sub in new[] { "bin", "", "RelWithDebInfo", "Release", "Debug" })
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
		var imported = ResourceLoader.Load<Font>("res://assets/launcher/fable2.ttf");
		if (imported is not null)
		{
			return imported;
		}

		string external = LauncherPaths.GetLauncherAssetPath("fable2.ttf");
		if (File.Exists(external))
		{
			var font = new FontFile();
			if (font.LoadDynamicFont(external) == Error.Ok)
			{
				return font;
			}
		}

		return null;
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

	private const int TabRounding = 4;

	private StyleBoxFlat TabFill(Color color)
	{
		var box = new StyleBoxFlat { BgColor = color };
		box.BorderColor = _infoStyle.Border;
		box.SetBorderWidthAll(1);
		box.CornerRadiusTopLeft = TabRounding;
		box.CornerRadiusTopRight = TabRounding;
		box.CornerRadiusBottomLeft = 0;
		box.CornerRadiusBottomRight = 0;
		box.ContentMarginLeft = 8;
		box.ContentMarginRight = 8;
		box.ContentMarginTop = 3;
		box.ContentMarginBottom = 3;
		return box;
	}

	private void ApplyFont(Control control, int size)
	{
		if (_font is not null)
		{
			control.AddThemeFontOverride("font", _font);
		}

		control.AddThemeFontSizeOverride("font_size", size);
	}

	private void ApplyStyles()
	{
		_backgrounds = new BackgroundManager(_background, _backgroundNext);
		_backgrounds.Initialize();

		_menuBar.AddThemeStyleboxOverride("panel", Fill(_menuStyle.MenuBarBg));
		foreach (MenuButton menu in new[] { _fileMenu, _aboutMenu })
		{
			ApplyFont(menu, TextFontSize);
			menu.AddThemeColorOverride("font_color", _menuStyle.Text);
		}

		_warningPanel.AddThemeStyleboxOverride(
			"panel",
			HorizontalFade(
				_warningStyle.WindowBg,
				new[] { 0.0f, 1.0f - WarningFadeFraction, 1.0f },
				new[] { 1.0f, 1.0f, 0.0f }));
		_warningLabel.Text = LauncherTheme.WarningText;
		ApplyFont(_warningLabel, TextFontSize);
		_warningLabel.AddThemeColorOverride("font_color", _warningStyle.Text);

		_actionPanel.AddThemeStyleboxOverride("panel", Fill(_buttonStyle.WindowBg, _buttonStyle.Border));
		ApplyFont(_primaryButton, TitleFontSize);
		_primaryButton.AddThemeStyleboxOverride("normal", EdgeFaded(_buttonStyle.Button));
		_primaryButton.AddThemeStyleboxOverride("hover", EdgeFaded(_buttonStyle.ButtonHovered));
		_primaryButton.AddThemeStyleboxOverride("pressed", EdgeFaded(_buttonStyle.ButtonActive));
		_primaryButton.AddThemeStyleboxOverride("disabled", Fill(new Color(0, 0, 0, 0)));
		_progressTrack.Texture =
			FadeTexture(new Color(0.3f, 0.3f, 0.3f, 0.5f), EdgeFadeOffsets, EdgeFadeAlphas);
		_progressFill.Texture =
			FadeTexture(_buttonStyle.Button, EdgeFadeOffsets, EdgeFadeAlphas);
		_primaryButton.AddThemeColorOverride("font_color", _buttonStyle.Text);
		_primaryButton.AddThemeColorOverride("font_disabled_color", _buttonStyle.Text);

		ApplyFont(_artCredit, ArtCreditFontSize);
		_artCredit.AddThemeColorOverride("font_color", _warningStyle.Text);
		_artCredit.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.75f));
		_artCredit.AddThemeConstantOverride("shadow_offset_x", 1);
		_artCredit.AddThemeConstantOverride("shadow_offset_y", 1);
		_primaryButton.Pressed += OnPrimaryPressed;

		_infoPanel.AddThemeStyleboxOverride("panel", Fill(_infoStyle.WindowBg, _infoStyle.Border));
		ApplyFont(_infoTitle, TextFontSize);
		_infoTitle.AddThemeColorOverride("font_color", _infoStyle.Text);
		var titleBar = new StyleBoxFlat { BgColor = _infoStyle.TitleBg };
		titleBar.ContentMarginLeft = 6;
		titleBar.ContentMarginRight = 6;
		titleBar.ContentMarginTop = 2;
		titleBar.ContentMarginBottom = 2;
		_infoTitle.AddThemeStyleboxOverride("normal", titleBar);

		ApplyFont(_infoTabs, TextFontSize);
		_infoTabs.AddThemeStyleboxOverride("tab_selected", TabFill(_infoStyle.TabSelected));
		_infoTabs.AddThemeStyleboxOverride("tab_unselected", TabFill(_infoStyle.Tab));
		_infoTabs.AddThemeStyleboxOverride("tab_hovered", TabFill(_infoStyle.TabHovered));
		_infoTabs.AddThemeConstantOverride("h_separation", 4);
		_infoTabs.AddThemeColorOverride("font_selected_color", _infoStyle.Text);
		_infoTabs.AddThemeColorOverride("font_unselected_color", _infoStyle.Text);

		ApplyFont(_infoBody, TextFontSize);
		_infoBody.AddThemeColorOverride("default_color", _infoStyle.Text);

		_settingsPanel.Configure(_font, TextFontSize, _infoStyle);
		_moddingPanel.Configure(_font, TextFontSize, _infoStyle);
		_reinstallPanel.Configure(_font, TextFontSize, _infoStyle);
		_reinstallPanel.ReinstallRequested += StartInstallFlow;
		_reinstallPanel.UnpackBanksRequested += () => StartOps(ops => ops.ExtractGameBanks());
		_reinstallPanel.DecompileLuaRequested += () => StartOps(ops => ops.RebuildLuaWorkspace());
		_reinstallPanel.CompileShadersRequested += () => StartOps(ops => ops.CompileShaders());
	}

	private void WireMenus()
	{
		PopulateMenu(_fileMenu, FileMenuItems, OnFileMenu);
		PopulateMenu(_aboutMenu, AboutMenuItems, OnAboutMenu);
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
		foreach (string tab in InfoTabs)
		{
			_infoTabs.AddTab(tab);
		}

		_infoTabs.TabChanged += _ => RefreshInfoBody();
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

	private void ApplyLayout()
	{
		Vector2 viewport = Size;
		LauncherLayout layout = LauncherLayout.Build(Vector2.Zero, viewport, _menuBarHeight);

		_menuBar.Position = Vector2.Zero;
		_menuBar.Size = new Vector2(viewport.X, _menuBarHeight);

		_artCredit.Position = new Vector2(
			layout.WarningPos.X + 12.0f, layout.WarningPos.Y - ArtCreditHeight);
		_artCredit.Size = new Vector2(layout.WarningSize.X - 24.0f, ArtCreditHeight);

		_warningPanel.Position = layout.WarningPos;
		_warningPanel.Size = layout.WarningSize;
		_warningLabel.Position = new Vector2(10.0f, 0.0f);
		_warningLabel.Size = new Vector2(layout.WarningSize.X - 20.0f, layout.WarningSize.Y);

		_actionPanel.Position = layout.ActionPos;
		_actionPanel.Size = layout.ActionSize;
		_primaryButton.Position = new Vector2(
			0, layout.ActionSize.Y - LauncherLayout.BottomPadding - LauncherLayout.PrimaryButtonHeight);
		_primaryButton.Size = new Vector2(layout.ActionSize.X, LauncherLayout.PrimaryButtonHeight);
		_progressTrack.Position = _primaryButton.Position;
		_progressTrack.Size = _primaryButton.Size;
		_progressClip.Position = _primaryButton.Position;
		_progressClip.Size = new Vector2(0, _primaryButton.Size.Y);
		_progressFill.Position = Vector2.Zero;
		_progressFill.Size = _primaryButton.Size;

		_infoPanel.Position = layout.InfoPos;
		_infoPanel.Size = layout.InfoSize;
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
			label = "Install " + gameTitle;
		}
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
		ApplyFont(_primaryButton, compactFont ? TextFontSize : TitleFontSize);

		RefreshInfoBody();
	}

	private void RefreshInfoBody()
	{
		// Log() calls through to here, and logging can happen before BindNodes() has
		// run — anything in _Ready that reports a problem does exactly that. Without
		// this guard the FIRST such message becomes a NullReferenceException that takes
		// the whole launcher down, so the startup path is least able to report the
		// startup problems it exists to report.
		if (_infoTabs is null || _infoBody is null)
		{
			return;
		}

		string tab = InfoTabs[Mathf.Clamp(_infoTabs.CurrentTab, 0, InfoTabs.Length - 1)];
		bool isSettings = tab == "Settings";
		bool isModding = tab == "Modding";
		bool isReinstall = tab == "Reinstall";

		_settingsPanel.Visible = isSettings;
		_moddingPanel.Visible = isModding;
		_reinstallPanel.Visible = isReinstall;
		_infoBody.Visible = !isSettings && !isModding && !isReinstall;

		if (isReinstall)
		{
			_reinstallPanel.Refresh(_installInProgress || _ops is not null, _launch.GameIsRunning);
			_reinstallPanel.SetProgress(
				_ops is not null ? _ops.OperationLabel : _installLabel,
				_ops is not null ? _ops.StatusMessage : _installStatus,
				_ops is not null ? string.Empty : _installError);
		}

		if (isSettings || isModding || isReinstall)
		{
			return;
		}

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
			return "Loading commits from GitHub...";
		}

		if (!_commitsLoaded)
		{
			return "Failed to load commits from GitHub\n" +
				   "Please check your internet connection and try restarting the application.";
		}

		if (_commits.Count == 0)
		{
			return "No commits found for this repository";
		}

		var builder = new System.Text.StringBuilder();
		foreach (GitCommit commit in _commits)
		{
			builder.Append("- [").Append(commit.Sha).Append("] ").Append(commit.Message).Append('\n');
			builder.Append("    by ").Append(commit.Author).Append(" on ").Append(commit.Date).Append("\n\n");
		}

		return builder.ToString().TrimEnd();
	}

	private string BuildCreditsText()
	{
		if (_contributorsLoading)
		{
			return "Loading contributors from GitHub...";
		}

		if (!_contributorsLoaded)
		{
			return "Failed to load contributors from GitHub\n" +
				   "Please check your internet connection and try restarting the application.";
		}

		if (_contributors.Count == 0)
		{
			return "No contributors found for this repository";
		}

		var builder = new System.Text.StringBuilder();
		builder.Append("Total Contributors: ").Append(_contributors.Count).Append('\n');
		builder.Append(new string('-', 32)).Append('\n');
		foreach (GitContributor contributor in _contributors)
		{
			builder.Append('@').Append(contributor.Login);
			if (contributor.Name.Length != 0)
			{
				builder.Append(" (").Append(contributor.Name).Append(')');
			}

			builder.Append("  ").Append(contributor.Contributions).Append(" contributions\n");
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

		var window = new PatchesWindow(_patches, _font, TextFontSize, _patchesStyle);
		AddChild(window);
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

		_installInProgress = false;
		_installLabel = string.Empty;
		_installStatus = string.Empty;
		_installError = string.Empty;
		Log("Installation complete.");
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
		var dialog = new InstallDialog(_font, TextFontSize, _baseStyle);
		dialog.InstallRequested += StartInstall;

		AddChild(dialog);
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
			probe.GetVerifyEntries(), _font, TextFontSize, _baseStyle);
		dialog.ResetRequested += path => StartOps(ops => ops.ResetLuaWorkspaceFile(path));
		AddChild(dialog);
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
