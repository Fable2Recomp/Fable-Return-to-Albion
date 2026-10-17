using System;
using Godot;
using Refii.Launcher.Core;

namespace Refii.Launcher;

/// <summary>
/// The Install tab: everything the launcher derives from the player's own game, one row each,
/// with its state and a button to build it again.
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

	private Row _gameData = null!;
	private Row _gameCode = null!;
	private Row _shaders = null!;
	private Row _assets = null!;
	private Label _operationLabel = null!;
	private Label _statusLabel = null!;
	private Label _errorLabel = null!;
	private ScrollContainer _errorScroll = null!;
	private HSeparator _progressSeparator = null!;
	private HBoxContainer _buildControls = null!;
	private Button _pauseBuild = null!;
	private Button _stopBuild = null!;
	private bool _built;

	private const string CheckGlyph = "✓";
	private static readonly Color CheckFill = new(0.16f, 0.52f, 0.33f);

	/// <summary>Install the game data again from a disc image.</summary>
	public event Action? ReinstallRequested;

	/// <summary>
	/// Generate and compile the game code again: the one artefact that something outside the
	/// launcher can invalidate, since replacing the executable leaves a module whose every
	/// function address belongs to a different binary.
	/// </summary>
	public event Action? BuildGameCodeRequested;

	/// <summary>Translate the shader bank into the classic renderer's shaders.</summary>
	public event Action? CompileShadersRequested;

	/// <summary>
	/// Build every modernized texture and material now rather than during play. A one-off
	/// preparation of the player's data, not a preference: which look is drawn is the Look
	/// setting; this only decides when that look is paid for.
	/// </summary>
	public event Action? BuildModernizedAssetsRequested;

	/// <summary>Hold the running step where it is; resume it; abandon it.</summary>
	public event Action? PauseBuildRequested;
	public event Action? ResumeBuildRequested;
	public event Action? StopBuildRequested;

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

		// EVERYTHING SCROLLS. Four rows with their detail lines and a failure's text run past
		// the drawer's height; the drawer must not grow past the window to hold them.
		var scroll = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		AddChild(scroll);
		var content = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		content.AddThemeConstantOverride("separation", 8);
		scroll.AddChild(content);

		Label explain = MakeLabel(
			"What the launcher builds from your own game files. A tick means it is there and " +
			"current; each can be built again on its own.");
		explain.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		explain.AddThemeColorOverride("font_color", _style.TextDisabled);
		content.AddChild(explain);

		var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		rows.AddThemeConstantOverride("separation", 6);
		content.AddChild(rows);

		_gameData = AddRow(rows, "Game data", "Reinstall", () => ReinstallRequested?.Invoke());
		_gameCode = AddRow(rows, "Game code", "Build", () => BuildGameCodeRequested?.Invoke());
		_shaders = AddRow(rows, "Classic shaders", "Build", () => CompileShadersRequested?.Invoke());
		_assets = AddRow(rows, "Modernized assets", "Build", () => BuildModernizedAssetsRequested?.Invoke());

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
	/// One artefact: the tick, then the name over its detail line, then the button. A box
	/// rather than a grid so a row's detail wraps under its own name and a hidden tick does not
	/// shift the columns beside it.
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

	/// <summary>Designed action face matching the hero button: solid emerald fill,
	/// gold hairline, cream label.</summary>
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

	/// <summary>Repaints every row's state. <paramref name="busy"/> is a running step.</summary>
	public void Refresh(bool busy, bool gameIsRunning)
	{
		bool idle = !busy && !gameIsRunning;
		bool assets = Installer.AssetsExist();

		// Game data: the installed pressing and its title update, from the two file headers.
		bool pairOk = Installer.CheckInstalledTitleUpdate(out string pair);
		SetState(_gameData, assets, assets
			? (pairOk ? pair : "Title update: " + pair)
			: (pairOk ? "Not installed. Install from your disc image." : pair));
		_gameData.Action.Disabled = !idle;

		// Game code: tracked on the MODULE, which goes stale when the executable changes.
		string reason = string.Empty;
		bool gameCodeCurrent = assets && !Recompile.GameCodeBuild.NeedsBuildCached(out reason);
		SetState(_gameCode, gameCodeCurrent, assets
			? (gameCodeCurrent ? "Built from the installed executable." : reason)
			: "Needs the game data.");
		_gameCode.Action.Disabled = !idle || !assets;

		// Classic shaders: the pipeline's report marks the step done.
		Installer.ReinstallStepStatus status = Installer.GetReinstallStepStatus();
		SetState(_shaders, status.Shaders, assets
			? (status.Shaders
				? "Built. Drawn when the Look setting is Classic."
				: "Not built. Needed for the Classic look only.")
			: "Needs the game data.");
		_shaders.Action.Disabled = !idle || !assets;

		// Modernized assets: untracked, because the cache fills during play as well and a tick
		// driven by "has anything been cached" would show complete after one texture. Gated on
		// the game not running: the prebuild and a session would be two writers on one cache.
		SetState(_assets, false, assets
			? "Optional. Builds the Modern look's textures ahead of play instead of during it."
			: "Needs the game data.");
		_assets.Tick.Modulate = new Color(1, 1, 1, 0.35f);
		_assets.Action.Disabled = !idle || !assets;
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
