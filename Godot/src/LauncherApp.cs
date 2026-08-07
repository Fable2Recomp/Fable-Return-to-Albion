using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Refii.Launcher.Core;

namespace Refii.Launcher;

[Tool]
public partial class LauncherApp : Control
{
	private const int TextFontSize = 18;
	private const int TitleFontSize = 30;

	private static readonly string[] InfoTabs =
	{
		"Change Log", "Credits", "Reinstall", "Modding", "Settings",
	};

	private string _changeLogText =
		"Failed to load commits from GitHub\n" +
		"Please check your internet connection and try restarting the app";

	private readonly List<string> _consoleLines = new();
	private SettingsPanel _settingsPanel = null!;

	private Patches _patches = null!;
	private LaunchController _launch = null!;
	private Font? _font;

	private SmartStyle _menuStyle = null!;
	private SmartStyle _warningStyle = null!;
	private SmartStyle _buttonStyle = null!;
	private SmartStyle _infoStyle = null!;

	private TextureRect _background = null!;
	private Panel _menuBar = null!;
	private Panel _warningPanel = null!;
	private Label _warningLabel = null!;
	private Panel _actionPanel = null!;
	private Button _primaryButton = null!;
	private Panel _infoPanel = null!;
	private TabBar _infoTabs = null!;
	private RichTextLabel _infoBody = null!;

	private float _menuBarHeight = 26.0f;

	public override void _Ready()
	{
		LauncherPaths.OverrideLauncherDirectory = ResolveLauncherDirectory();
		Ini.Load(Path.Combine(LauncherPaths.GetLauncherDataDirectory(), "config.cfg"));
		LauncherTheme.Load();

		_patches = new Patches();
		_launch = new LaunchController(_patches);

		_font = LoadLauncherFont();
		BuildStyles();
		BuildUi();

		if (!Engine.IsEditorHint())
		{
			DisplayServer.WindowSetTitle(LauncherTheme.WindowTitle);
		}

		Resized += ApplyLayout;
		ApplyLayout();
		RefreshState();
	}

	private static string ResolveLauncherDirectory()
	{
		string? explicitDir = System.Environment.GetEnvironmentVariable("REFII_LAUNCHER_DIR");
		if (!string.IsNullOrEmpty(explicitDir))
		{
			return explicitDir!.TrimEnd(Path.DirectorySeparatorChar);
		}

		string projectDir = ProjectSettings.GlobalizePath("res://");
		string repoRoot = Path.GetFullPath(Path.Combine(projectDir, ".."));

		foreach (string configuration in new[] { "RelWithDebInfo", "Release", "Debug" })
		{
			string candidate = Path.Combine(
				repoRoot, "out", "build", "win-amd64-release", configuration, "bin");
			if (File.Exists(Path.Combine(candidate, Platform.ExecutableName)))
			{
				return candidate.TrimEnd(Path.DirectorySeparatorChar);
			}
		}

		return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
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

		GD.PushWarning("fable2.ttf not found; falling back to the default font.");
		return null;
	}

	private Texture2D? LoadBackgroundTexture()
	{
		foreach (string root in new[]
				 {
					 LauncherPaths.GetLauncherAssetPath("backgrounds"),
					 ProjectSettings.GlobalizePath("res://assets/launcher/backgrounds"),
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

	private static StyleBoxFlat PaddedFill(Color color)
	{
		var box = new StyleBoxFlat { BgColor = color };
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

	private void BuildUi()
	{
		_background = new TextureRect
		{
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
			Texture = LoadBackgroundTexture(),
		};
		_background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		AddChild(_background);

		_menuBar = new Panel();
		_menuBar.AddThemeStyleboxOverride("panel", Fill(_menuStyle.MenuBarBg));
		AddChild(_menuBar);

		var menuRow = new HBoxContainer { Position = new Vector2(6, 0) };
		menuRow.AddThemeConstantOverride("separation", 4);
		_menuBar.AddChild(menuRow);

		menuRow.AddChild(BuildMenuButton("File", new[]
		{
			"Set Install Folder", "Uninstall", "", "Verify Lua Workspace",
			"Reset All Lua Files", "Reset Lua File...", "", "Close",
		}, OnFileMenu));
		menuRow.AddChild(BuildMenuButton(
			"About", new[] { "Fable 2 Recompiled", "RexGlue" }, OnAboutMenu));

		_warningPanel = new Panel();
		_warningPanel.AddThemeStyleboxOverride("panel", Fill(_warningStyle.WindowBg, _warningStyle.Border));
		AddChild(_warningPanel);

		_warningLabel = new Label
		{
			Text = LauncherTheme.WarningText,
			VerticalAlignment = VerticalAlignment.Center,
			Position = new Vector2(10, 0),
		};
		ApplyFont(_warningLabel, TextFontSize);
		_warningLabel.AddThemeColorOverride("font_color", _warningStyle.Text);
		_warningPanel.AddChild(_warningLabel);

		_actionPanel = new Panel();
		_actionPanel.AddThemeStyleboxOverride("panel", Fill(_buttonStyle.WindowBg, _buttonStyle.Border));
		AddChild(_actionPanel);

		_primaryButton = new Button();
		ApplyFont(_primaryButton, TitleFontSize);
		_primaryButton.AddThemeStyleboxOverride("normal", Fill(_buttonStyle.Button));
		_primaryButton.AddThemeStyleboxOverride("hover", Fill(_buttonStyle.ButtonHovered));
		_primaryButton.AddThemeStyleboxOverride("pressed", Fill(_buttonStyle.ButtonActive));
		_primaryButton.AddThemeStyleboxOverride("disabled", Fill(new Color(0.3f, 0.3f, 0.3f, 0.5f)));
		_primaryButton.AddThemeColorOverride("font_color", _buttonStyle.Text);
		_primaryButton.AddThemeColorOverride("font_disabled_color", _buttonStyle.Text);
		_primaryButton.Pressed += OnPrimaryPressed;
		_actionPanel.AddChild(_primaryButton);

		_infoPanel = new Panel();
		_infoPanel.AddThemeStyleboxOverride("panel", Fill(_infoStyle.WindowBg, _infoStyle.Border));
		AddChild(_infoPanel);

		var infoColumn = new VBoxContainer();
		infoColumn.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		infoColumn.AddThemeConstantOverride("separation", 6);
		_infoPanel.AddChild(infoColumn);

		var infoTitle = new Label { Text = "Launcher Info", Position = new Vector2(8, 0) };
		ApplyFont(infoTitle, TextFontSize);
		infoTitle.AddThemeColorOverride("font_color", _infoStyle.Text);
		infoColumn.AddChild(infoTitle);

		_infoTabs = new TabBar();
		foreach (string tab in InfoTabs)
		{
			_infoTabs.AddTab(tab);
		}

		ApplyFont(_infoTabs, TextFontSize);
		_infoTabs.AddThemeStyleboxOverride("tab_selected", PaddedFill(_infoStyle.TabSelected));
		_infoTabs.AddThemeStyleboxOverride("tab_unselected", PaddedFill(_infoStyle.Tab));
		_infoTabs.AddThemeStyleboxOverride("tab_hovered", PaddedFill(_infoStyle.TabHovered));
		_infoTabs.AddThemeConstantOverride("h_separation", 4);
		_infoTabs.AddThemeColorOverride("font_selected_color", _infoStyle.Text);
		_infoTabs.AddThemeColorOverride("font_unselected_color", _infoStyle.Text);
		_infoTabs.TabChanged += _ => RefreshInfoBody();
		infoColumn.AddChild(_infoTabs);

		_infoBody = new RichTextLabel
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			BbcodeEnabled = false,
		};
		ApplyFont(_infoBody, TextFontSize);
		_infoBody.AddThemeColorOverride("default_color", _infoStyle.Text);
		infoColumn.AddChild(_infoBody);

		_settingsPanel = new SettingsPanel(_font, TextFontSize, _infoStyle)
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			Visible = false,
		};
		infoColumn.AddChild(_settingsPanel);
	}

	private MenuButton BuildMenuButton(string title, string[] items, Action<string> onSelected)
	{
		var button = new MenuButton { Text = title, Flat = true };
		ApplyFont(button, TextFontSize);
		button.AddThemeColorOverride("font_color", _menuStyle.Text);

		PopupMenu popup = button.GetPopup();
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

		return button;
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
			case "Reset All Lua Files":
			case "Reset Lua File...":
				Log(item + " requires the Lua workspace library, which is not wired up yet.");
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
		dialog.PopupCentered();
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

		_warningPanel.Position = layout.WarningPos;
		_warningPanel.Size = layout.WarningSize;
		_warningLabel.Size = new Vector2(layout.WarningSize.X - 20, layout.WarningSize.Y);

		_actionPanel.Position = layout.ActionPos;
		_actionPanel.Size = layout.ActionSize;
		_primaryButton.Position = new Vector2(
			0, layout.ActionSize.Y - LauncherLayout.BottomPadding - LauncherLayout.PrimaryButtonHeight);
		_primaryButton.Size = new Vector2(layout.ActionSize.X, LauncherLayout.PrimaryButtonHeight);

		_infoPanel.Position = layout.InfoPos;
		_infoPanel.Size = layout.InfoSize;
	}

	private void RefreshState()
	{
		bool assetsExist = Installer.AssetsExist();
		bool installedBuildExists = Installer.InstalledBuildExists();
		const bool canDownloadBuild = false;
		const bool updateAvailable = false;
		const bool isoInProgress = false;
		const bool downloadInProgress = false;
		bool gameIsRunning = _launch.GameIsRunning;

		string gameTitle = "Fable 2";
		string label = "Play " + gameTitle;
		bool enabled = true;
		bool compactFont = false;

		if (isoInProgress)
		{
			enabled = false;
			compactFont = true;
		}
		else if (downloadInProgress)
		{
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
		ApplyFont(_primaryButton, compactFont ? TextFontSize : TitleFontSize);

		RefreshInfoBody();
	}

	private void RefreshInfoBody()
	{
		if (_infoTabs is null || _infoBody is null)
		{
			return;
		}

		string tab = InfoTabs[Mathf.Clamp(_infoTabs.CurrentTab, 0, InfoTabs.Length - 1)];
		bool isSettings = tab == "Settings";

		_settingsPanel.Visible = isSettings;
		_infoBody.Visible = !isSettings;
		if (isSettings)
		{
			return;
		}

		_infoBody.Text = tab switch
		{
			"Change Log" => _changeLogText,
			"Reinstall" => "Target: " + Installer.GetLuaWorkspacePath(),
			_ => string.Empty,
		};
	}

	private void OnPrimaryPressed()
	{
		if (!Installer.AssetsExist() || !Installer.InstalledBuildExists())
		{
			return;
		}

		LaunchResult result = _launch.Launch();
		GD.Print(result.Message);
		RefreshState();
	}

	public override void _Process(double delta)
	{
		if (_launch.GameIsRunning != _primaryButton.Disabled)
		{
			RefreshState();
		}
	}
}
