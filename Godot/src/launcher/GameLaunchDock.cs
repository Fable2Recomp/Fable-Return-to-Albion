using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;
using Refii.Launcher.Core;

namespace Refii.Launcher;

/// <summary>Primary action routed when a user clicks a title card's main CTA button in <see cref="GameLaunchDock"/>.</summary>
public enum TitleCardActionKind
{
	/// <summary>Card button is disabled (game running, pipeline busy, or runtime unavailable).</summary>
	None,

	/// <summary>Launch the installed and up-to-date game.</summary>
	Play,

	/// <summary>Open the disc/TU installation wizard for this title.</summary>
	Install,

	/// <summary>Generate and compile recompiled game code for this title.</summary>
	BuildGameCode,

	/// <summary>Download or update the shared host runtime.</summary>
	DownloadRuntime,
}

/// <summary>Secondary per-title management actions exposed in each card's <c>⋯</c> overflow menu.</summary>
public enum TitleCardOverflowAction
{
	/// <summary>Launch the game elevated as Administrator (Windows).</summary>
	RunAsAdmin,

	/// <summary>Open the title's extracted game data / assets directory in the OS file manager.</summary>
	OpenDataFolder,

	/// <summary>Open the right drawer on the Install tab scoped to this title.</summary>
	ManageInstall,

	/// <summary>Open the right drawer on the Modding tab scoped to this title.</summary>
	ManageMods,

	/// <summary>Open the right drawer on the Saves tab scoped to this title.</summary>
	ViewSaves,
}

/// <summary>
/// Pure, headless-testable presentation state for one game card in <see cref="GameLaunchDock"/>.
/// </summary>
public readonly record struct TitleCardState(
	Engine_Title Title,
	TitleCardActionKind Action,
	string ButtonLabel,
	string StatusText,
	bool PrimaryEnabled,
	bool CompactButtonFont,
	bool ShowRunAsAdminOption,
	string? BadgeText,
	string BadgeTooltip,
	float CardProgressFraction);

/// <summary>
/// Side-by-side dual-game launch dock showing <c>Fable 2</c> and <c>Fable 3</c> on one launcher
/// home screen, with per-title primary actions, overflow menus, and a shared build/install progress bar.
/// </summary>
public sealed partial class GameLaunchDock : VBoxContainer
{
	private sealed class CardWidgets
	{
		public Engine_Title Title = null!;
		public PanelContainer Card = null!;
		public Label TitleLabel = null!;
		public PanelContainer BadgePill = null!;
		public Label BadgeLabel = null!;
		public MenuButton OverflowButton = null!;
		public Label StatusDot = null!;
		public Label StatusLabel = null!;
		public Button ActionButton = null!;
		public ProgressBar CardProgress = null!;
		public readonly List<TitleCardOverflowAction> OverflowActions = new();
		public TitleCardActionKind CurrentAction = TitleCardActionKind.None;
	}

	private readonly Dictionary<Engine_Title, CardWidgets> _cards = new();
	private readonly HBoxContainer _cardsRow;
	private readonly PanelContainer _pipelineCard;
	private readonly Label _pipelineLabel;
	private readonly ProgressBar _pipelineBar;
	private readonly HBoxContainer _pipelineButtons;
	private readonly Button _pauseButton;
	private readonly Button _stopButton;

	private Font? _displayFont;
	private SmartStyle _buttonStyle = SmartStyle.FromTheme(LauncherTokens.Cream, LauncherTokens.Emerald);
	private SmartStyle _infoStyle = SmartStyle.FromTheme(LauncherTokens.Walnut, LauncherTokens.Parchment);

	/// <summary>Raised when a user clicks a title card's primary button.</summary>
	public event Action<Engine_Title, TitleCardActionKind>? PrimaryActionRequested;

	/// <summary>Raised when a user chooses an entry from a title card's <c>⋯</c> overflow menu.</summary>
	public event Action<Engine_Title, TitleCardOverflowAction>? OverflowActionRequested;

	/// <summary>Raised when the user clicks Pause / Resume on an active pipeline build.</summary>
	public event Action? PauseBuildRequested;

	/// <summary>Raised when the user clicks Stop on an active pipeline build.</summary>
	public event Action? StopBuildRequested;

	/// <summary>
	/// Pure state evaluator computing a title card's button label, status line, enablement, and badge
	/// without touching the Godot scene tree or display server.
	/// </summary>
	public static TitleCardState EvaluateCardState(
		Engine_Title title,
		bool assetsExist,
		bool titleRuntimeExists,
		bool needsBuild,
		string needsBuildReason = "",
		bool installedBuildExists = true,
		bool canDownloadBuild = false,
		bool updateAvailable = false,
		bool downloadInProgress = false,
		string downloadLabel = "",
		bool gameRunning = false,
		Engine_Title? runningGameTitle = null,
		bool pipelineRunning = false,
		Engine_Title? pipelineTitle = null,
		string pipelineLabel = "",
		string pipelineStatus = "",
		float pipelineFraction = 0.0f)
	{
		LauncherTitleScreen screen = LauncherTitleScreen.For(title);
		string? badge = screen.StatusBadge;
		string badgeTip = screen.StatusTooltip;

		if (pipelineRunning)
		{
			bool ownsPipeline = pipelineTitle is null || pipelineTitle == title;
			if (ownsPipeline)
			{
				string detail = pipelineStatus.Length != 0
					? pipelineStatus
					: (pipelineLabel.Length != 0 ? pipelineLabel : "Working…");
				float clamped = pipelineFraction > 0.0f ? Math.Clamp(pipelineFraction, 0.0f, 1.0f) : 0.0f;
				return new TitleCardState(
					title,
					TitleCardActionKind.None,
					"Working…",
					detail,
					PrimaryEnabled: false,
					CompactButtonFont: true,
					ShowRunAsAdminOption: false,
					badge,
					badgeTip,
					clamped);
			}

			return new TitleCardState(
				title,
				TitleCardActionKind.None,
				assetsExist ? "Play" : "Install",
				$"Busy ({pipelineTitle!.Name})",
				PrimaryEnabled: false,
				CompactButtonFont: false,
				ShowRunAsAdminOption: false,
				badge,
				badgeTip,
				0.0f);
		}

		if (downloadInProgress)
		{
			return new TitleCardState(
				title,
				TitleCardActionKind.None,
				"Downloading…",
				downloadLabel.Length != 0 ? downloadLabel : "Downloading runtime…",
				PrimaryEnabled: false,
				CompactButtonFont: true,
				ShowRunAsAdminOption: false,
				badge,
				badgeTip,
				0.0f);
		}

		if (gameRunning)
		{
			bool isThisRunning = runningGameTitle is null || runningGameTitle == title;
			return new TitleCardState(
				title,
				TitleCardActionKind.None,
				isThisRunning ? "Running" : (assetsExist ? "Play" : "Install"),
				isThisRunning ? "Game session active" : $"{runningGameTitle!.Name} is running",
				PrimaryEnabled: false,
				CompactButtonFont: false,
				ShowRunAsAdminOption: false,
				badge,
				badgeTip,
				0.0f);
		}

		if (!assetsExist)
		{
			return new TitleCardState(
				title,
				TitleCardActionKind.Install,
				"Install",
				"Not installed",
				PrimaryEnabled: true,
				CompactButtonFont: false,
				ShowRunAsAdminOption: false,
				badge,
				badgeTip,
				0.0f);
		}

		if (!titleRuntimeExists)
		{
			return new TitleCardState(
				title,
				TitleCardActionKind.None,
				"Unavailable",
				"Runtime not built",
				PrimaryEnabled: false,
				CompactButtonFont: true,
				ShowRunAsAdminOption: false,
				badge,
				badgeTip,
				0.0f);
		}

		if (needsBuild)
		{
			return new TitleCardState(
				title,
				TitleCardActionKind.BuildGameCode,
				"Build Code",
				needsBuildReason.Length != 0 ? needsBuildReason : "Code build required",
				PrimaryEnabled: true,
				CompactButtonFont: true,
				ShowRunAsAdminOption: false,
				badge,
				badgeTip,
				0.0f);
		}

		if (!installedBuildExists && canDownloadBuild)
		{
			return new TitleCardState(
				title,
				TitleCardActionKind.DownloadRuntime,
				"Download",
				"Runtime download needed",
				PrimaryEnabled: true,
				CompactButtonFont: true,
				ShowRunAsAdminOption: false,
				badge,
				badgeTip,
				0.0f);
		}

		if (!installedBuildExists)
		{
			return new TitleCardState(
				title,
				TitleCardActionKind.None,
				"Unavailable",
				"Missing runtime files",
				PrimaryEnabled: false,
				CompactButtonFont: true,
				ShowRunAsAdminOption: false,
				badge,
				badgeTip,
				0.0f);
		}

		if (canDownloadBuild && updateAvailable)
		{
			return new TitleCardState(
				title,
				TitleCardActionKind.DownloadRuntime,
				"Update",
				"Runtime update available",
				PrimaryEnabled: true,
				CompactButtonFont: true,
				ShowRunAsAdminOption: false,
				badge,
				badgeTip,
				0.0f);
		}

		return new TitleCardState(
			title,
			TitleCardActionKind.Play,
			"Play",
			"Ready to play",
			PrimaryEnabled: true,
			CompactButtonFont: false,
			ShowRunAsAdminOption: OperatingSystem.IsWindows(),
			badge,
			badgeTip,
			0.0f);
	}

	/// <summary>Constructs the side-by-side dual-game launch dock over <see cref="Engine_Title.All"/>.</summary>
	public GameLaunchDock()
	{
		SizeFlagsHorizontal = SizeFlags.ExpandFill;
		AddThemeConstantOverride("separation", 8);

		_pipelineCard = BuildPipelineStrip(
			out _pipelineLabel,
			out _pipelineBar,
			out _pipelineButtons,
			out _pauseButton,
			out _stopButton);
		_pipelineCard.Visible = false;
		AddChild(_pipelineCard);

		_cardsRow = new HBoxContainer
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ExpandFill,
		};
		_cardsRow.AddThemeConstantOverride("separation", 12);
		AddChild(_cardsRow);

		foreach (Engine_Title title in Engine_Title.All)
		{
			CardWidgets card = BuildCard(title);
			_cards[title] = card;
			_cardsRow.AddChild(card.Card);
		}
	}

	/// <summary>Configures fonts and smart styles for the dock's cards, buttons, and popups.</summary>
	public void Configure(Font? displayFont, SmartStyle buttonStyle, SmartStyle infoStyle)
	{
		_displayFont = displayFont;
		_buttonStyle = buttonStyle;
		_infoStyle = infoStyle;
		ApplyStylesAndScale();
	}

	/// <summary>Re-applies responsive font sizes and styleboxes across all cards.</summary>
	public void ApplyStylesAndScale()
	{
		int titleSize = LauncherScale.Font(LauncherTokens.WordmarkSize);
		int bodySize = LauncherScale.Font(LauncherTokens.BodySize);
		int captionSize = LauncherScale.Font(LauncherTokens.CaptionSize);
		int ctaSize = LauncherScale.Font(LauncherTokens.DisplaySize - 3);

		ApplySecondaryControlButtonStyle(_pauseButton, bodySize);
		ApplySecondaryControlButtonStyle(_stopButton, bodySize);
		_pipelineLabel.RemoveThemeFontOverride("font");
		_pipelineLabel.AddThemeFontSizeOverride("font_size", captionSize);

		foreach (CardWidgets w in _cards.Values)
		{
			w.Card.AddThemeStyleboxOverride("panel", MakeCardFace());

			if (_displayFont is not null)
			{
				w.TitleLabel.AddThemeFontOverride("font", _displayFont);
			}
			w.TitleLabel.AddThemeFontSizeOverride("font_size", titleSize);
			w.TitleLabel.AddThemeColorOverride("font_color", LauncherTokens.Cream);

			w.BadgeLabel.RemoveThemeFontOverride("font");
			w.BadgeLabel.AddThemeFontSizeOverride("font_size", Math.Max(10, captionSize - 1));

			w.StatusDot.RemoveThemeFontOverride("font");
			w.StatusDot.AddThemeFontSizeOverride("font_size", captionSize);
			w.StatusLabel.RemoveThemeFontOverride("font");
			w.StatusLabel.AddThemeFontSizeOverride("font_size", captionSize);

			w.OverflowButton.RemoveThemeFontOverride("font");
			w.OverflowButton.AddThemeFontSizeOverride("font_size", bodySize + 2);
			ApplyOverflowButtonStyle(w.OverflowButton);
			StylePopup(w.OverflowButton.GetPopup(), bodySize);

			ApplyPrimaryButtonStyle(w.ActionButton, ctaSize);
		}
	}

	/// <summary>Updates all title cards and the shared pipeline progress strip.</summary>
	public void UpdateStates(
		IReadOnlyDictionary<Engine_Title, TitleCardState> states,
		bool showPipelineStrip,
		string pipelineSummary,
		float pipelineFraction,
		bool showBuildControls,
		bool isPaused,
		bool isStopping)
	{
		int ctaRegular = LauncherScale.Font(LauncherTokens.DisplaySize - 3);
		int ctaCompact = LauncherScale.Font(LauncherTokens.BodySize);

		foreach ((Engine_Title title, CardWidgets w) in _cards)
		{
			if (!states.TryGetValue(title, out TitleCardState state))
			{
				continue;
			}

			w.CurrentAction = state.Action;
			w.ActionButton.Text = state.ButtonLabel;
			w.ActionButton.Disabled = !state.PrimaryEnabled;
			w.ActionButton.AddThemeFontSizeOverride("font_size", state.CompactButtonFont ? ctaCompact : ctaRegular);
			if (state.CompactButtonFont)
			{
				w.ActionButton.RemoveThemeFontOverride("font");
			}
			else if (_displayFont is not null)
			{
				w.ActionButton.AddThemeFontOverride("font", _displayFont);
			}

			bool isInstallStyle = state.Action == TitleCardActionKind.Install;
			ApplyCardPrimaryFace(w.ActionButton, isInstallStyle);

			w.StatusLabel.Text = state.StatusText;
			w.StatusLabel.TooltipText = state.StatusText;
			Color dotColor = state.Action switch
			{
				TitleCardActionKind.Play => new Color("#4cd97b"),
				TitleCardActionKind.BuildGameCode or TitleCardActionKind.DownloadRuntime => LauncherTokens.Sunset,
				TitleCardActionKind.Install => new Color(LauncherTokens.Cream, 0.45f),
				_ when state.ButtonLabel == "Running" => LauncherTokens.WillCyan,
				_ when state.ButtonLabel == "Working…" => LauncherTokens.GoldBright,
				_ => new Color(LauncherTokens.Cream, 0.35f),
			};
			w.StatusDot.AddThemeColorOverride("font_color", dotColor);

			bool hasBadge = !string.IsNullOrEmpty(state.BadgeText);
			w.BadgePill.Visible = hasBadge;
			if (hasBadge)
			{
				w.BadgeLabel.Text = state.BadgeText!;
				w.BadgePill.TooltipText = state.BadgeTooltip;
			}

			w.CardProgress.Visible = state.CardProgressFraction > 0.0f;
			w.CardProgress.Value = state.CardProgressFraction;

			RebuildOverflowPopup(w, state.ShowRunAsAdminOption);
		}

		_pipelineCard.Visible = showPipelineStrip;
		if (showPipelineStrip)
		{
			_pipelineLabel.Text = pipelineSummary;
			_pipelineBar.Indeterminate = pipelineFraction < 0.0f;
			_pipelineBar.Value = pipelineFraction >= 0.0f ? Math.Clamp(pipelineFraction, 0.0f, 1.0f) : 0.0f;
			_pipelineButtons.Visible = showBuildControls;
			if (showBuildControls)
			{
				_pauseButton.Text = isPaused ? "Resume" : "Pause";
				_pauseButton.Disabled = isStopping;
				_stopButton.Text = isStopping ? "Stopping…" : "Stop";
				_stopButton.Disabled = isStopping;
			}
		}
	}

	private CardWidgets BuildCard(Engine_Title title)
	{
		var widgets = new CardWidgets { Title = title };

		var card = new PanelContainer
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ExpandFill,
		};
		card.AddThemeStyleboxOverride("panel", MakeCardFace());
		widgets.Card = card;

		var col = new VBoxContainer
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ExpandFill,
		};
		col.AddThemeConstantOverride("separation", 6);
		card.AddChild(col);

		// Header row: Title + optional status pill (Experimental) + ⋯ overflow menu
		var headerRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		headerRow.AddThemeConstantOverride("separation", 6);
		col.AddChild(headerRow);

		var titleLabel = new Label
		{
			Text = title.Name,
			VerticalAlignment = VerticalAlignment.Center,
		};
		titleLabel.AddThemeColorOverride("font_color", LauncherTokens.Cream);
		titleLabel.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.65f));
		titleLabel.AddThemeConstantOverride("shadow_offset_x", 1);
		titleLabel.AddThemeConstantOverride("shadow_offset_y", 1);
		headerRow.AddChild(titleLabel);
		widgets.TitleLabel = titleLabel;

		var badgePill = new PanelContainer
		{
			Visible = false,
			SizeFlagsVertical = SizeFlags.ShrinkCenter,
			MouseFilter = MouseFilterEnum.Stop,
		};
		var pillStyle = new StyleBoxFlat
		{
			BgColor = new Color(LauncherTokens.Sunset, 0.22f),
			BorderColor = new Color(LauncherTokens.GoldBright, 0.65f),
			ContentMarginLeft = 6,
			ContentMarginRight = 6,
			ContentMarginTop = 1,
			ContentMarginBottom = 1,
		};
		pillStyle.SetCornerRadiusAll(LauncherTokens.RadiusPill);
		pillStyle.SetBorderWidthAll(1);
		badgePill.AddThemeStyleboxOverride("panel", pillStyle);

		var badgeLabel = new Label
		{
			Text = string.Empty,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		badgeLabel.AddThemeColorOverride("font_color", LauncherTokens.GoldBright);
		badgePill.AddChild(badgeLabel);
		headerRow.AddChild(badgePill);
		widgets.BadgePill = badgePill;
		widgets.BadgeLabel = badgeLabel;

		headerRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

		var overflow = new MenuButton
		{
			Text = "⋯",
			Flat = false,
			TooltipText = $"{title.Name} options",
			MouseDefaultCursorShape = CursorShape.PointingHand,
			CustomMinimumSize = new Vector2(28, 24),
		};
		Engine_Title capturedTitle = title;
		overflow.GetPopup().IdPressed += id =>
		{
			int idx = (int)id;
			if (idx >= 0 && idx < widgets.OverflowActions.Count)
			{
				OverflowActionRequested?.Invoke(capturedTitle, widgets.OverflowActions[idx]);
			}
		};
		headerRow.AddChild(overflow);
		widgets.OverflowButton = overflow;

		// Status line: colored dot + single-line status caption
		var statusRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		statusRow.AddThemeConstantOverride("separation", 5);
		col.AddChild(statusRow);

		var dot = new Label
		{
			Text = "●",
			VerticalAlignment = VerticalAlignment.Center,
		};
		statusRow.AddChild(dot);
		widgets.StatusDot = dot;

		var statusLabel = new Label
		{
			Text = string.Empty,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			ClipText = true,
			TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
			MouseFilter = MouseFilterEnum.Pass,
		};
		statusLabel.AddThemeColorOverride("font_color", new Color(LauncherTokens.Cream, 0.78f));
		statusRow.AddChild(statusLabel);
		widgets.StatusLabel = statusLabel;

		// Action button
		var actionBtn = new Button
		{
			Text = "Play",
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(0, 38),
			ClipText = true,
			TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
			MouseDefaultCursorShape = CursorShape.PointingHand,
		};
		actionBtn.Pressed += () =>
		{
			if (widgets.CurrentAction != TitleCardActionKind.None)
			{
				PrimaryActionRequested?.Invoke(capturedTitle, widgets.CurrentAction);
			}
		};
		col.AddChild(actionBtn);
		widgets.ActionButton = actionBtn;

		// Thin per-card progress bar when this card owns an active pipeline step
		var cardProgress = new ProgressBar
		{
			Visible = false,
			ShowPercentage = false,
			MinValue = 0,
			MaxValue = 1,
			Step = 0.001,
			CustomMinimumSize = new Vector2(0, 4),
		};
		StyleProgressBar(cardProgress);
		col.AddChild(cardProgress);
		widgets.CardProgress = cardProgress;

		RebuildOverflowPopup(widgets, showRunAsAdmin: false);
		return widgets;
	}

	private PanelContainer BuildPipelineStrip(
		out Label summaryLabel,
		out ProgressBar bar,
		out HBoxContainer controlsRow,
		out Button pauseBtn,
		out Button stopBtn)
	{
		var strip = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		var face = new StyleBoxFlat
		{
			BgColor = LauncherTokens.InkGlass,
			BorderColor = new Color(LauncherTokens.Gold, 0.48f),
			ContentMarginLeft = 10,
			ContentMarginRight = 10,
			ContentMarginTop = 6,
			ContentMarginBottom = 6,
		};
		face.SetCornerRadiusAll(LauncherTokens.RadiusSm + 2);
		face.SetBorderWidthAll(1);
		strip.AddThemeStyleboxOverride("panel", face);

		var outer = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		outer.AddThemeConstantOverride("separation", 5);
		strip.AddChild(outer);

		var topRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		topRow.AddThemeConstantOverride("separation", 8);
		outer.AddChild(topRow);

		summaryLabel = new Label
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			ClipText = true,
			TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
			VerticalAlignment = VerticalAlignment.Center,
		};
		summaryLabel.AddThemeColorOverride("font_color", LauncherTokens.Cream);
		topRow.AddChild(summaryLabel);

		controlsRow = new HBoxContainer { Visible = false };
		controlsRow.AddThemeConstantOverride("separation", 6);
		pauseBtn = new Button
		{
			Text = "Pause",
			CustomMinimumSize = new Vector2(74, 24),
			MouseDefaultCursorShape = CursorShape.PointingHand,
		};
		pauseBtn.Pressed += () => PauseBuildRequested?.Invoke();
		controlsRow.AddChild(pauseBtn);

		stopBtn = new Button
		{
			Text = "Stop",
			CustomMinimumSize = new Vector2(68, 24),
			MouseDefaultCursorShape = CursorShape.PointingHand,
		};
		stopBtn.Pressed += () => StopBuildRequested?.Invoke();
		controlsRow.AddChild(stopBtn);
		topRow.AddChild(controlsRow);

		bar = new ProgressBar
		{
			ShowPercentage = false,
			MinValue = 0,
			MaxValue = 1,
			Step = 0.001,
			CustomMinimumSize = new Vector2(0, 6),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		StyleProgressBar(bar);
		outer.AddChild(bar);

		return strip;
	}

	private static void RebuildOverflowPopup(CardWidgets widgets, bool showRunAsAdmin)
	{
		PopupMenu popup = widgets.OverflowButton.GetPopup();
		popup.Clear();
		widgets.OverflowActions.Clear();

		void AddItem(string label, TitleCardOverflowAction action)
		{
			popup.AddItem(label, widgets.OverflowActions.Count);
			widgets.OverflowActions.Add(action);
		}

		if (showRunAsAdmin)
		{
			AddItem("Run as Administrator", TitleCardOverflowAction.RunAsAdmin);
			popup.AddSeparator();
		}

		AddItem($"Open {widgets.Title.Name} Data Folder", TitleCardOverflowAction.OpenDataFolder);
		AddItem("Manage Install…", TitleCardOverflowAction.ManageInstall);
		AddItem("Manage Mods…", TitleCardOverflowAction.ManageMods);
		AddItem("View Saves…", TitleCardOverflowAction.ViewSaves);
	}

	private static StyleBoxFlat MakeCardFace()
	{
		var box = new StyleBoxFlat
		{
			BgColor = new Color(0.11f, 0.09f, 0.07f, 0.80f),
			BorderColor = new Color(LauncherTokens.Gold, 0.45f),
			ShadowColor = new Color(0, 0, 0, 0.42f),
			ShadowSize = 14,
			ShadowOffset = new Vector2(0, 4),
			ContentMarginLeft = 12,
			ContentMarginRight = 12,
			ContentMarginTop = 9,
			ContentMarginBottom = 10,
		};
		box.SetCornerRadiusAll(LauncherTokens.RadiusMd);
		box.SetBorderWidthAll(1);
		return box;
	}

	private void ApplyPrimaryButtonStyle(Button button, int fontSize)
	{
		if (_displayFont is not null)
		{
			button.AddThemeFontOverride("font", _displayFont);
		}
		button.AddThemeFontSizeOverride("font_size", fontSize);
		button.AddThemeColorOverride("font_color", LauncherTokens.Cream);
		button.AddThemeColorOverride("font_hover_color", LauncherTokens.Cream);
		button.AddThemeColorOverride("font_pressed_color", LauncherTokens.Cream);
		button.AddThemeColorOverride("font_disabled_color", new Color(LauncherTokens.Cream, 0.45f));
		button.AddThemeStyleboxOverride("focus", MakeFocusRing());
		ApplyCardPrimaryFace(button, installVariant: false);
	}

	private void ApplyCardPrimaryFace(Button button, bool installVariant)
	{
		Color normalBg = installVariant
			? new Color(LauncherTokens.OakRaised, 0.92f)
			: _buttonStyle.Button;
		Color hoverBg = installVariant
			? new Color(LauncherTokens.GoldInk, 0.88f)
			: _buttonStyle.ButtonHovered;
		Color pressedBg = installVariant
			? LauncherTokens.Oak
			: _buttonStyle.ButtonActive;
		Color border = installVariant
			? new Color(LauncherTokens.GoldBright, 0.65f)
			: new Color(LauncherTokens.Gold, 0.58f);

		button.AddThemeStyleboxOverride("normal", MakeButtonBox(normalBg, border, glow: !installVariant));
		button.AddThemeStyleboxOverride("hover", MakeButtonBox(hoverBg, new Color(LauncherTokens.GoldBright, 0.9f), glow: true));
		button.AddThemeStyleboxOverride("pressed", MakeButtonBox(pressedBg, border, glow: false));
		button.AddThemeStyleboxOverride("disabled", MakeButtonBox(new Color(LauncherTokens.OakDim, 0.78f), new Color(LauncherTokens.Gold, 0.22f), glow: false));
	}

	private static StyleBoxFlat MakeButtonBox(Color bg, Color border, bool glow)
	{
		var box = new StyleBoxFlat
		{
			BgColor = bg,
			BorderColor = border,
			ContentMarginLeft = 12,
			ContentMarginRight = 12,
			ContentMarginTop = 5,
			ContentMarginBottom = 5,
		};
		box.SetCornerRadiusAll(LauncherTokens.RadiusSm);
		box.SetBorderWidthAll(1);
		if (glow)
		{
			box.ShadowColor = new Color(LauncherTokens.Gold, 0.22f);
			box.ShadowSize = 8;
		}
		return box;
	}

	private static void ApplyOverflowButtonStyle(MenuButton button)
	{
		var normal = new StyleBoxFlat
		{
			BgColor = new Color(1, 1, 1, 0.06f),
			BorderColor = new Color(LauncherTokens.Gold, 0.28f),
			ContentMarginLeft = 6,
			ContentMarginRight = 6,
			ContentMarginTop = 1,
			ContentMarginBottom = 1,
		};
		normal.SetCornerRadiusAll(LauncherTokens.RadiusSm);
		normal.SetBorderWidthAll(1);

		var hover = new StyleBoxFlat
		{
			BgColor = new Color(LauncherTokens.Gold, 0.24f),
			BorderColor = new Color(LauncherTokens.GoldBright, 0.75f),
			ContentMarginLeft = 6,
			ContentMarginRight = 6,
			ContentMarginTop = 1,
			ContentMarginBottom = 1,
		};
		hover.SetCornerRadiusAll(LauncherTokens.RadiusSm);
		hover.SetBorderWidthAll(1);

		button.AddThemeStyleboxOverride("normal", normal);
		button.AddThemeStyleboxOverride("hover", hover);
		button.AddThemeStyleboxOverride("pressed", hover);
		button.AddThemeStyleboxOverride("focus", MakeFocusRing());
		button.AddThemeColorOverride("font_color", LauncherTokens.Cream);
		button.AddThemeColorOverride("font_hover_color", LauncherTokens.GoldBright);
	}

	private void ApplySecondaryControlButtonStyle(Button button, int fontSize)
	{
		button.RemoveThemeFontOverride("font");
		button.AddThemeFontSizeOverride("font_size", Math.Max(11, fontSize - 1));
		button.AddThemeStyleboxOverride("normal", MakeButtonBox(_buttonStyle.Button, new Color(LauncherTokens.Gold, 0.5f), glow: false));
		button.AddThemeStyleboxOverride("hover", MakeButtonBox(_buttonStyle.ButtonHovered, new Color(LauncherTokens.GoldBright, 0.8f), glow: false));
		button.AddThemeStyleboxOverride("pressed", MakeButtonBox(_buttonStyle.ButtonActive, new Color(LauncherTokens.Gold, 0.55f), glow: false));
		button.AddThemeStyleboxOverride("disabled", MakeButtonBox(new Color(LauncherTokens.OakDim, 0.8f), new Color(LauncherTokens.Gold, 0.25f), glow: false));
		button.AddThemeStyleboxOverride("focus", MakeFocusRing());
		button.AddThemeColorOverride("font_color", LauncherTokens.Cream);
		button.AddThemeColorOverride("font_disabled_color", new Color(LauncherTokens.Cream, 0.45f));
	}

	private void StylePopup(PopupMenu popup, int fontSize)
	{
		var panel = new StyleBoxFlat
		{
			BgColor = _infoStyle.PopupBg,
			BorderColor = _infoStyle.Border,
			ShadowColor = new Color(0, 0, 0, 0.4f),
			ShadowSize = 12,
			ContentMarginLeft = 4,
			ContentMarginRight = 4,
			ContentMarginTop = 4,
			ContentMarginBottom = 4,
		};
		panel.SetBorderWidthAll(1);
		popup.AddThemeStyleboxOverride("panel", panel);

		var hover = new StyleBoxFlat
		{
			BgColor = _infoStyle.Header,
			ContentMarginLeft = LauncherTokens.Sp2,
			ContentMarginRight = LauncherTokens.Sp2,
			ContentMarginTop = 2,
			ContentMarginBottom = 2,
		};
		popup.AddThemeStyleboxOverride("hover", hover);

		popup.AddThemeFontSizeOverride("font_size", fontSize);
		popup.AddThemeColorOverride("font_color", _infoStyle.Text);
		popup.AddThemeColorOverride("font_hover_color", _infoStyle.Text);
		popup.AddThemeColorOverride("font_separator_color", _infoStyle.Separator);
		popup.AddThemeColorOverride("font_disabled_color", _infoStyle.TextDisabled);
	}

	private static void StyleProgressBar(ProgressBar bar)
	{
		var bg = new StyleBoxFlat { BgColor = new Color(LauncherTokens.Gold, 0.16f) };
		bg.SetCornerRadiusAll(3);
		var fill = new StyleBoxFlat { BgColor = LauncherTokens.GoldBright };
		fill.SetCornerRadiusAll(3);
		bar.AddThemeStyleboxOverride("background", bg);
		bar.AddThemeStyleboxOverride("fill", fill);
	}

	private static StyleBoxFlat MakeFocusRing()
	{
		var box = new StyleBoxFlat
		{
			BgColor = new Color(0, 0, 0, 0),
			BorderColor = LauncherTokens.GoldBright,
			ExpandMarginLeft = 2,
			ExpandMarginRight = 2,
			ExpandMarginTop = 2,
			ExpandMarginBottom = 2,
		};
		box.SetCornerRadiusAll(LauncherTokens.RadiusSm + 1);
		box.SetBorderWidthAll(2);
		return box;
	}
}
