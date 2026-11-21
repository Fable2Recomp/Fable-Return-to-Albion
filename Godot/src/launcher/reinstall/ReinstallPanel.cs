using System;
using Godot;
using Refii.Core;
using Refii.Launcher.Core;

namespace Refii.Launcher;

/// <summary>
/// The Install tab: everything the launcher derives from the player's own game, one row each,
/// with its state and a button to build it again, scoped to the selected title via <see cref="GameScopeSelector"/>.
/// </summary>
/// <remarks>
/// A CHECKLIST, NOT A TOOLBOX. Each row is one artefact the install pipeline produces — the
/// game data, the game code, the classic shaders, the modernized assets — with a tick that says
/// whether it is there and current, and one button. The rows run through the same
/// <c>InstallPipeline</c> the install dialog uses, so this tab is also where a stopped or failed
/// step is resumed. One progress area and one pause/stop pair serve whichever step runs.
/// </remarks>
[Tool]
public partial class ReinstallPanel : VBoxContainer
{
	private Font? _font;
	private int _fontSize = 18;
	private SmartStyle _style = SmartStyle.FromTheme(
		new Color(0.02f, 0.1f, 0.2f, 1f), new Color(0.2f, 0.8f, 0.4f, 1f));

	private sealed class Row
	{
		public Label Tick = null!;
		public Label Name = null!;
		public Label Detail = null!;
		public Button Action = null!;
	}

	private Engine_Title _selectedTitle = Engine_Title.Active;
	private GameScopeSelector? _scopeSelector;
	private Row _gameData = null!;
	private Row _gameCode = null!;
	private Row _shaders = null!;
	private Row _assets = null!;
	private Button _openFolderButton = null!;
	private Button _uninstallButton = null!;
	private Label _operationLabel = null!;
	private Label _statusLabel = null!;
	private Label _errorLabel = null!;
	private ScrollContainer _errorScroll = null!;
	private HSeparator _progressSeparator = null!;
	private HBoxContainer _buildControls = null!;
	private Button _pauseBuild = null!;
	private Button _stopBuild = null!;
	private bool _built;
	private bool _lastBusy;
	private bool _lastGameRunning;

	private const string CheckGlyph = "✓";
	private static readonly Color CheckFill = new(0.16f, 0.52f, 0.33f);

	/// <summary>Currently selected game title in the Install tab.</summary>
	public Engine_Title SelectedTitle => _selectedTitle;

	/// <summary>Raised when the user switches the top segmented game selector.</summary>
	public event Action<Engine_Title>? TitleChanged;

	/// <summary>Install the game data again from a disc image for the given title.</summary>
	public event Action<Engine_Title>? ReinstallRequested;

	/// <summary>
	/// Generate and compile the game code again for the given title.
	/// </summary>
	public event Action<Engine_Title>? BuildGameCodeRequested;

	/// <summary>Translate the shader bank into the classic renderer's shaders for the given title.</summary>
	public event Action<Engine_Title>? CompileShadersRequested;

	/// <summary>
	/// Build every modernized texture and material now rather than during play for the given title.
	/// </summary>
	public event Action<Engine_Title>? BuildModernizedAssetsRequested;

	/// <summary>Remove launcher-managed extracted/compiled files for the given title (after confirmation).</summary>
	public event Action<Engine_Title>? UninstallRequested;

	/// <summary>Hold the running step where it is; resume it; abandon it.</summary>
	public event Action? PauseBuildRequested;
	public event Action? ResumeBuildRequested;
	public event Action? StopBuildRequested;

	/// <summary>The player chose which published builds the app updates to.</summary>
	public event Action<UpdateChannel>? UpdateChannelChanged;

	/// <summary>The player asked for another look at the project's releases.</summary>
	public event Action? UpdateCheckRequested;

	/// <summary>The player asked for the newest build to be downloaded and installed.</summary>
	public event Action? UpdateInstallRequested;

	private OptionButton? _updateChannel;
	private Label? _updateStatus;
	private Button? _updateButton;
	private bool _updateInstalls;

	/// <summary>
	/// States the app-update row: what the last look at the releases found, what the button does
	/// (<paramref name="installs"/>: install the newest build, else look again), and whether it can
	/// be pressed.
	/// </summary>
	public void SetUpdateState(string status, bool installs, bool enabled)
	{
		if (_updateStatus is null || _updateButton is null || _updateChannel is null)
		{
			return;
		}

		_updateStatus.Text = status;
		_updateInstalls = installs;
		_updateButton.Text = installs ? "Update" : "Check";
		_updateButton.Disabled = !enabled;
		_updateChannel.Disabled = !enabled;
		int wanted = ReleaseUpdates.Channel == UpdateChannel.Nightly ? 1 : 0;
		if (_updateChannel.Selected != wanted)
		{
			_updateChannel.Select(wanted);
		}
	}

	/// <summary>The app's own update row: the channel, what is published on it, and the button.</summary>
	private void AddUpdateRow(VBoxContainer content)
	{
		Label heading = MakeLabel("App updates");
		content.AddChild(heading);

		var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		row.AddThemeConstantOverride("separation", 8);

		_updateChannel = new OptionButton
		{
			CustomMinimumSize = new Vector2(190, 32),
			TooltipText = "Release follows the numbered betas. Nightly follows the newest dev build, which is untested.",
		};
		Apply(_updateChannel);
		_updateChannel.AddItem(ReleaseUpdates.ChannelName(UpdateChannel.Release));
		_updateChannel.AddItem(ReleaseUpdates.ChannelName(UpdateChannel.Nightly));
		_updateChannel.Select(ReleaseUpdates.Channel == UpdateChannel.Nightly ? 1 : 0);
		_updateChannel.ItemSelected += index =>
			UpdateChannelChanged?.Invoke(index == 1 ? UpdateChannel.Nightly : UpdateChannel.Release);
		row.AddChild(_updateChannel);

		_updateButton = MakeSecondaryButton("Check", () =>
		{
			if (_updateInstalls)
			{
				UpdateInstallRequested?.Invoke();
			}
			else
			{
				UpdateCheckRequested?.Invoke();
			}
		});
		row.AddChild(_updateButton);
		content.AddChild(row);

		_updateStatus = MakeLabel(string.Empty);
		_updateStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_updateStatus.AddThemeColorOverride("font_color", _style.TextDisabled);
		content.AddChild(_updateStatus);
		content.AddChild(new HSeparator());
	}

	/// <summary>Switches the Install tab's scoped game title without firing <see cref="TitleChanged"/>.</summary>
	public void SetTitle(Engine_Title title)
	{
		if (_selectedTitle == title)
		{
			_scopeSelector?.SetSelectedTitle(title, notify: false);
			_scopeSelector?.Refresh();
			return;
		}

		_selectedTitle = title;
		_scopeSelector?.SetSelectedTitle(title, notify: false);
		if (_built)
		{
			Refresh(_lastBusy, _lastGameRunning);
		}
	}

	private static string DescribeTitleInstallBadge(Engine_Title title)
	{
		try
		{
			return Installer.AssetsExist(title) ? "Installed" : "Not Installed";
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	public void Configure(Font? font, int fontSize, SmartStyle style)
	{
		_font = font;
		_fontSize = fontSize;
		_style = style;

		if (_built)
		{
			foreach (Node child in GetChildren())
			{
				child.QueueFree();
			}

			_scopeSelector = null;
			_built = false;
		}

		Build();
	}

	private void Build()
	{
		if (_built)
		{
			return;
		}

		_built = true;
		AddThemeConstantOverride("separation", 8);
		SizeFlagsVertical = SizeFlags.ExpandFill;

		_scopeSelector = new GameScopeSelector(_selectedTitle)
		{
			BadgeProvider = DescribeTitleInstallBadge,
		};
		_scopeSelector.Configure(_font, _fontSize);
		_scopeSelector.TitleSelected += title =>
		{
			SetTitle(title);
			TitleChanged?.Invoke(title);
		};
		AddChild(_scopeSelector);

		// EVERYTHING SCROLLS. Four rows with their detail lines and a failure's text run past
		// the drawer's height; the drawer must not grow past the window to hold them.
		var scroll = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		AddChild(scroll);

		var scrollMargin = new MarginContainer
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ExpandFill,
		};
		scrollMargin.AddThemeConstantOverride("margin_right", LauncherScale.Px(12));
		scrollMargin.AddThemeConstantOverride("margin_bottom", LauncherScale.Px(4));
		scroll.AddChild(scrollMargin);

		var content = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		content.AddThemeConstantOverride("separation", 8);
		scrollMargin.AddChild(content);

		AddUpdateRow(content);

		Label explain = MakeLabel(
			"What the launcher builds from your own game files. A tick means it is there and " +
			"current; each can be built again on its own.");
		explain.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		explain.AddThemeColorOverride("font_color", _style.TextDisabled);
		content.AddChild(explain);

		var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		rows.AddThemeConstantOverride("separation", 6);
		content.AddChild(rows);

		_gameData = AddRow(rows, "Game data", "Install", () => ReinstallRequested?.Invoke(_selectedTitle));
		_gameCode = AddRow(rows, "Game code", "Build", () => BuildGameCodeRequested?.Invoke(_selectedTitle));
		_shaders = AddRow(rows, "Classic shaders", "Build", () => CompileShadersRequested?.Invoke(_selectedTitle));
		_assets = AddRow(rows, "Modernized assets", "Build", () => BuildModernizedAssetsRequested?.Invoke(_selectedTitle));

		content.AddChild(new HSeparator());

		var manageRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		manageRow.AddThemeConstantOverride("separation", 8);
		_openFolderButton = MakeSecondaryButton("Open Data Folder", OnOpenDataFolderPressed);
		manageRow.AddChild(_openFolderButton);
		_uninstallButton = MakeDangerButton("Uninstall…", OnUninstallPressed);
		manageRow.AddChild(_uninstallButton);
		content.AddChild(manageRow);

		_progressSeparator = new HSeparator { Visible = false };
		content.AddChild(_progressSeparator);

		_operationLabel = MakeLabel(string.Empty);
		_operationLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_operationLabel.Visible = false;
		content.AddChild(_operationLabel);

		_statusLabel = MakeLabel(string.Empty);
		_statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_statusLabel.Visible = false;
		content.AddChild(_statusLabel);

		// A failure's text in its own box: a loader error runs to a dozen lines, and it is
		// read and copied, not skimmed, so it scrolls and selects rather than pushing the
		// buttons away.
		_errorScroll = new ScrollContainer
		{
			Visible = false,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
			CustomMinimumSize = new Vector2(0, 140),
		};
		var errorBox = new StyleBoxFlat
		{
			BgColor = new Color(0.35f, 0.08f, 0.08f, 0.25f),
			BorderColor = new Color(0.6f, 0.15f, 0.15f, 0.6f),
			ContentMarginLeft = 8,
			ContentMarginRight = 8,
			ContentMarginTop = 6,
			ContentMarginBottom = 6,
		};
		errorBox.SetBorderWidthAll(1);
		errorBox.SetCornerRadiusAll(4);
		var errorFrame = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		errorFrame.AddThemeStyleboxOverride("panel", errorBox);
		_errorScroll.AddChild(errorFrame);
		_errorLabel = MakeLabel(string.Empty);
		_errorLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_errorLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		_errorLabel.MouseFilter = MouseFilterEnum.Stop;
		errorFrame.AddChild(_errorLabel);
		content.AddChild(_errorScroll);

		// Shown while a step that can be held runs. Pause toggles to Resume while held.
		_buildControls = new HBoxContainer { Visible = false };
		_buildControls.AddThemeConstantOverride("separation", 8);
		_pauseBuild = MakeActionButton("Pause", () =>
		{
			if (_pauseBuild.Text == "Resume")
			{
				ResumeBuildRequested?.Invoke();
			}
			else
			{
				PauseBuildRequested?.Invoke();
			}
		});
		_buildControls.AddChild(_pauseBuild);
		_stopBuild = MakeActionButton("Stop", () => StopBuildRequested?.Invoke());
		_buildControls.AddChild(_stopBuild);
		content.AddChild(_buildControls);

		Refresh(false, false);
	}

	private void OnOpenDataFolderPressed()
	{
		string dir = Installer.ResolveGameDataDirectory(_selectedTitle);
		if (dir.Length == 0)
		{
			dir = _selectedTitle.AssetsDirectory;
		}

		System.IO.Directory.CreateDirectory(dir);
		OS.ShellOpen(dir);
	}

	private void OnUninstallPressed()
	{
		Engine_Title target = _selectedTitle;
		var dialog = new ConfirmationDialog
		{
			Title = $"Uninstall {target.Name} Managed Files",
			DialogText =
				$"Remove launcher-extracted game data and compiled code for {target.Name}?\n\n" +
				"Your save games and installed mod packages are preserved.",
			OkButtonText = $"Uninstall {target.Name}",
			CancelButtonText = "Cancel",
			DialogAutowrap = true,
			MinSize = new Vector2I(480, 0),
		};
		dialog.Confirmed += () => UninstallRequested?.Invoke(target);
		dialog.VisibilityChanged += () =>
		{
			if (!dialog.Visible)
			{
				dialog.QueueFree();
			}
		};
		Node host = (Node?)GetTree()?.Root.GetNodeOrNull("Launcher") ?? (Node?)GetWindow() ?? this;
		host.AddChild(dialog);
		dialog.PopupCentered();
	}

	/// <summary>Shows the pause and stop buttons while a step runs, and reflects its state.</summary>
	public void SetBuildControls(bool visible, bool paused, bool stopping)
	{
		_buildControls.Visible = visible;
		_pauseBuild.Text = paused ? "Resume" : "Pause";
		_pauseBuild.Disabled = stopping;
		_stopBuild.Text = stopping ? "Stopping..." : "Stop";
		_stopBuild.Disabled = stopping;
	}

	/// <summary>
	/// One artefact: the tick, then the name over its detail line, then the button.
	/// </summary>
	private Row AddRow(VBoxContainer rows, string name, string action, Action onPressed)
	{
		var row = new Row { Tick = MakeCheckBox(), Name = MakeLabel(name), Detail = MakeLabel(string.Empty) };
		var line = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		line.AddThemeConstantOverride("separation", 10);

		row.Tick.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
		row.Tick.SizeFlagsVertical = SizeFlags.ShrinkBegin;
		line.AddChild(row.Tick);

		var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		text.AddThemeConstantOverride("separation", 0);
		text.AddChild(row.Name);
		row.Detail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		row.Detail.AddThemeFontSizeOverride("font_size", Mathf.Max(12, _fontSize - 3));
		row.Detail.AddThemeColorOverride("font_color", _style.TextDisabled);
		text.AddChild(row.Detail);
		line.AddChild(text);

		row.Action = MakeActionButton(action, onPressed);
		row.Action.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
		row.Action.SizeFlagsVertical = SizeFlags.ShrinkBegin;
		row.Action.CustomMinimumSize = new Vector2(110, 32);
		line.AddChild(row.Action);

		rows.AddChild(line);
		return row;
	}

	private Label MakeCheckBox()
	{
		var box = new Label
		{
			Text = string.Empty,
			CustomMinimumSize = new Vector2(22, 22),
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
		};
		box.AddThemeFontSizeOverride("font_size", Mathf.Max(12, _fontSize - 4));
		box.AddThemeColorOverride("font_color", LauncherTokens.Cream);
		box.AddThemeStyleboxOverride("normal", CheckFace(done: false));
		return box;
	}

	private static StyleBox CheckFace(bool done)
	{
		var face = new StyleBoxFlat
		{
			BgColor = done ? CheckFill : new Color(0, 0, 0, 0),
			BorderColor = done
				? new Color(LauncherTokens.Gold, 0.4f)
				: new Color(LauncherTokens.Gold, 0.6f),
			ContentMarginLeft = 3,
			ContentMarginRight = 3,
			ContentMarginTop = 2,
			ContentMarginBottom = 2,
		};
		face.SetCornerRadiusAll(4);
		face.SetBorderWidthAll(1);
		return face;
	}

	/// <summary>Designed action face matching the hero button: solid emerald fill, gold hairline, cream label.</summary>
	private Button MakeActionButton(string text, Action onPressed)
	{
		var button = new Button
		{
			Text = text,
			CustomMinimumSize = new Vector2(150, 36),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			Alignment = HorizontalAlignment.Center,
			TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming,
		};
		Apply(button);

		button.AddThemeStyleboxOverride(
			"normal", ActionFace(_style.Button, new Color(LauncherTokens.Gold, 0.45f)));
		button.AddThemeStyleboxOverride(
			"hover", ActionFace(_style.ButtonHovered, new Color(LauncherTokens.Gold, 0.75f)));
		button.AddThemeStyleboxOverride(
			"pressed", ActionFace(_style.ButtonActive, new Color(LauncherTokens.Gold, 0.55f)));
		button.AddThemeStyleboxOverride(
			"disabled", ActionFace(new Color(LauncherTokens.OakDim, 0.9f), new Color(LauncherTokens.Gold, 0.2f)));
		button.AddThemeStyleboxOverride("focus", ActionFocus());
		button.AddThemeColorOverride("font_color", LauncherTokens.Cream);
		button.AddThemeColorOverride("font_hover_color", LauncherTokens.Cream);
		button.AddThemeColorOverride("font_pressed_color", LauncherTokens.Cream);
		button.AddThemeColorOverride("font_disabled_color", new Color(LauncherTokens.Cream, 0.45f));

		button.Pressed += onPressed;
		return button;
	}

	private Button MakeSecondaryButton(string text, Action onPressed)
	{
		var button = new Button
		{
			Text = text,
			CustomMinimumSize = new Vector2(130, 32),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			Alignment = HorizontalAlignment.Center,
			TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
		};
		Apply(button);
		button.AddThemeStyleboxOverride(
			"normal", ActionFace(new Color(LauncherTokens.ParchmentLit, 0.7f), new Color(LauncherTokens.Gold, 0.45f)));
		button.AddThemeStyleboxOverride(
			"hover", ActionFace(LauncherTokens.ParchmentLit, new Color(LauncherTokens.Gold, 0.8f)));
		button.AddThemeStyleboxOverride(
			"pressed", ActionFace(LauncherTokens.ParchmentRaised, new Color(LauncherTokens.Gold, 0.65f)));
		button.AddThemeStyleboxOverride(
			"disabled", ActionFace(new Color(LauncherTokens.Parchment, 0.35f), new Color(LauncherTokens.Gold, 0.18f)));
		button.AddThemeStyleboxOverride("focus", ActionFocus());
		button.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
		button.AddThemeColorOverride("font_hover_color", LauncherTokens.GoldInk);
		button.AddThemeColorOverride("font_pressed_color", LauncherTokens.GoldInk);
		button.AddThemeColorOverride("font_disabled_color", LauncherTokens.SepiaInk);
		button.Pressed += onPressed;
		return button;
	}

	private Button MakeDangerButton(string text, Action onPressed)
	{
		var button = new Button
		{
			Text = text,
			CustomMinimumSize = new Vector2(130, 32),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			Alignment = HorizontalAlignment.Center,
			TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
		};
		Apply(button);
		button.AddThemeStyleboxOverride(
			"normal", ActionFace(new Color(LauncherTokens.Crimson, 0.82f), new Color(LauncherTokens.Gold, 0.35f)));
		button.AddThemeStyleboxOverride(
			"hover", ActionFace(LauncherTokens.Crimson, new Color(LauncherTokens.GoldBright, 0.75f)));
		button.AddThemeStyleboxOverride(
			"pressed", ActionFace(new Color(LauncherTokens.Crimson, 0.95f), new Color(LauncherTokens.Gold, 0.55f)));
		button.AddThemeStyleboxOverride(
			"disabled", ActionFace(new Color(LauncherTokens.OakDim, 0.75f), new Color(LauncherTokens.Gold, 0.18f)));
		button.AddThemeStyleboxOverride("focus", ActionFocus());
		button.AddThemeColorOverride("font_color", LauncherTokens.Cream);
		button.AddThemeColorOverride("font_hover_color", LauncherTokens.Cream);
		button.AddThemeColorOverride("font_pressed_color", LauncherTokens.Cream);
		button.AddThemeColorOverride("font_disabled_color", new Color(LauncherTokens.Cream, 0.42f));
		button.Pressed += onPressed;
		return button;
	}

	private static StyleBox ActionFace(Color fill, Color border)
	{
		var face = new StyleBoxFlat
		{
			BgColor = fill,
			BorderColor = border,
			ContentMarginLeft = LauncherTokens.Sp4,
			ContentMarginRight = LauncherTokens.Sp4,
			ContentMarginTop = LauncherTokens.Sp1,
			ContentMarginBottom = LauncherTokens.Sp1,
		};
		face.SetCornerRadiusAll(LauncherTokens.RadiusMd);
		face.SetBorderWidthAll(1);
		return face;
	}

	private static StyleBox ActionFocus()
	{
		var face = new StyleBoxFlat
		{
			BgColor = new Color(0, 0, 0, 0),
			BorderColor = new Color(LauncherTokens.GoldBright, 0.9f),
		};
		face.SetCornerRadiusAll(LauncherTokens.RadiusMd);
		face.SetBorderWidthAll(2);
		return face;
	}

	/// <summary>Repaints every row's state for <see cref="SelectedTitle"/>. <paramref name="busy"/> is a running step.</summary>
	public void Refresh(bool busy, bool gameIsRunning)
	{
		_lastBusy = busy;
		_lastGameRunning = gameIsRunning;
		_scopeSelector?.Refresh();

		Engine_Title title = _selectedTitle;
		bool idle = !busy && !gameIsRunning;
		bool assets = Installer.AssetsExist(title);

		// Game data: the installed pressing and its title update, from the two file headers.
		bool pairOk = Installer.CheckInstalledTitleUpdate(title, out string pair);
		SetState(_gameData, assets, assets
			? (pairOk ? pair : "Title update: " + pair)
			: (pairOk ? "Not installed. Install from your disc image." : pair));
		_gameData.Action.Text = assets ? "Reinstall" : "Install";
		_gameData.Action.Disabled = !idle;

		// Game code: tracked on the MODULE, which goes stale when the executable changes.
		string reason = string.Empty;
		bool gameCodeCurrent = assets && !Recompile.GameCodeBuild.NeedsBuildCached(title, out reason);
		SetState(_gameCode, gameCodeCurrent, assets
			? (gameCodeCurrent ? "Built from the installed executable." : reason)
			: "Needs the game data.");
		_gameCode.Action.Disabled = !idle || !assets;

		// Classic shaders: the pipeline's report marks the step done.
		Installer.ReinstallStepStatus status = Installer.GetReinstallStepStatus(title);
		SetState(_shaders, status.Shaders, assets
			? (status.Shaders
				? "Built. Drawn when the Look setting is Classic."
				: "Not built. Needed for the Classic look only.")
			: "Needs the game data.");
		_shaders.Action.Disabled = !idle || !assets;

		// Modernized assets: ticked on a COMPLETED sweep for the current settings.
		SetState(_assets, status.Assets, !assets
			? "Needs the game data."
			: status.AssetsDetail.Length != 0
				? status.AssetsDetail
				: "Optional. Builds the Modern look's textures ahead of play instead of during it.");
		_assets.Tick.Modulate = status.Assets ? new Color(1, 1, 1, 1) : new Color(1, 1, 1, 0.35f);
		_assets.Action.Disabled = !idle || !assets;

		_openFolderButton.Text = $"Open {title.Name} Folder";
		_uninstallButton.Text = $"Uninstall {title.Name}…";
		_uninstallButton.Disabled = !idle || !assets;
	}

	private static void SetState(Row row, bool done, string detail)
	{
		row.Tick.Text = done ? CheckGlyph : string.Empty;
		row.Tick.AddThemeStyleboxOverride("normal", CheckFace(done));
		row.Detail.Text = detail;
		row.Detail.TooltipText = detail;
	}

	public void SetProgress(string operationLabel, string statusMessage, string errorMessage = "")
	{
		bool any = operationLabel.Length != 0 || statusMessage.Length != 0;
		_progressSeparator.Visible = any || errorMessage.Length != 0;
		_operationLabel.Visible = any;
		_operationLabel.Text = operationLabel;
		_statusLabel.Visible = statusMessage.Length != 0;
		_statusLabel.Text = statusMessage;
		_errorScroll.Visible = errorMessage.Length != 0;
		_errorLabel.Text = errorMessage;
	}

	private void Apply(Control control)
	{
		if (_font is not null)
		{
			control.AddThemeFontOverride("font", _font);
		}

		control.AddThemeFontSizeOverride("font_size", _fontSize);
		if (control is Button)
		{
			control.MouseDefaultCursorShape = CursorShape.PointingHand;
		}
	}

	private Label MakeLabel(string text)
	{
		var label = new Label { Text = text };
		Apply(label);
		label.AddThemeColorOverride("font_color", _style.Text);
		return label;
	}
}
