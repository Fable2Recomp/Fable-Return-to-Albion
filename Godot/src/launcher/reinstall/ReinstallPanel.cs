using System;
using Godot;
using Refii.Launcher.Core;

namespace Refii.Launcher;

[Tool]
public partial class ReinstallPanel : VBoxContainer
{
	private Font? _font;
	private int _fontSize = 18;
	private SmartStyle _style = SmartStyle.FromTheme(
		new Color(0.02f, 0.1f, 0.2f, 1f), new Color(0.2f, 0.8f, 0.4f, 1f));

	private Label _targetLabel = null!;
	private Button _reinstall = null!;
	private Button _unpackBanks = null!;
	private Button _decompileLua = null!;
	private Button _compileShaders = null!;
	private Button _buildGameCode = null!;
	private Button _buildAssets = null!;
	private Label _unpackBanksStatus = null!;
	private Label _decompileLuaStatus = null!;
	private Label _compileShadersStatus = null!;
	private Label _buildGameCodeStatus = null!;
	private Label _operationLabel = null!;
	private Label _statusLabel = null!;
	private Label _errorLabel = null!;
	private HSeparator _progressSeparator = null!;
	private bool _built;

	private const string CheckGlyph = "✓";
	private static readonly Color CheckFill = new(0.16f, 0.52f, 0.33f);

	public event Action? ReinstallRequested;
	public event Action? UnpackBanksRequested;
	public event Action? DecompileLuaRequested;
	public event Action? CompileShadersRequested;

	/// <summary>
	/// Generate and compile the game code again.
	/// </summary>
	/// <remarks>
	/// Separately runnable because it is the one install step that can be invalidated by
	/// something outside the launcher: the module is built from a specific game executable,
	/// and replacing that executable leaves a module whose every function address belongs
	/// to a different binary.
	/// </remarks>
	public event Action? BuildGameCodeRequested;

	/// <summary>
	/// Build every modernized texture and material now, rather than during play.
	/// </summary>
	/// <remarks>
	/// AN INSTALL STEP, which is why it sits here beside unpacking the banks and compiling the
	/// shaders rather than in the settings panel where it was first put. It is the same kind of
	/// thing they are: a one-off preparation of the player's own game data that makes every later
	/// session cheaper, not a preference that changes how the game behaves. The preference — which
	/// look you get — is <c>materials_modernized</c>, and it lives in settings; this only decides
	/// WHEN that look is paid for.
	/// </remarks>
	public event Action? BuildModernizedAssetsRequested;

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

		_targetLabel = MakeLabel(string.Empty);
		_targetLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		AddChild(_targetLabel);

		_reinstall = AddAction("Reinstall Fable 2", () => ReinstallRequested?.Invoke());
		_unpackBanks = AddTrackedAction("Unpack Game Banks",
			() => UnpackBanksRequested?.Invoke(), out _unpackBanksStatus);
		_decompileLua = AddTrackedAction("Unpack and Decompile Lua",
			() => DecompileLuaRequested?.Invoke(), out _decompileLuaStatus);
		_compileShaders = AddTrackedAction("Compile Shaders",
			() => CompileShadersRequested?.Invoke(), out _compileShadersStatus);

		// Tracked on the MODULE, not on a marker file, because it is the module the runtime
		// loads — and its tick has to go out when the installed executable changes, which is
		// something no "did this step run" flag can notice.
		_buildGameCode = AddTrackedAction("Build Game Code",
			() => BuildGameCodeRequested?.Invoke(), out _buildGameCodeStatus);

		// UNTRACKED, unlike the three above. Their tick comes from
		// Installer.GetReinstallStepStatus, which reports on files the native installer writes;
		// this one fills the app's own texture cache, which the installer knows nothing about, and a tick
		// driven by "has anything been cached" would show complete after a single texture.
		_buildAssets = AddAction(
			"Build Modernized Assets", () => BuildModernizedAssetsRequested?.Invoke());

		_progressSeparator = new HSeparator { Visible = false };
		AddChild(_progressSeparator);

		_operationLabel = MakeLabel(string.Empty);
		_operationLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_operationLabel.Visible = false;
		AddChild(_operationLabel);

		_statusLabel = MakeLabel(string.Empty);
		_statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_statusLabel.Visible = false;
		AddChild(_statusLabel);

		_errorLabel = MakeLabel(string.Empty);
		_errorLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_errorLabel.Visible = false;
		AddChild(_errorLabel);

		Refresh(false, false);
	}

	private Button AddAction(string text, Action onPressed)
	{
		Button button = MakeActionButton(text, onPressed);
		AddChild(button);
		return button;
	}

	/// <summary>Action row with a native-style status check: an empty box while
	/// the step is pending, a filled box with a check once it is done.</summary>
	private Button AddTrackedAction(string text, Action onPressed, out Label status)
	{
		var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		row.AddThemeConstantOverride("separation", 8);

		status = MakeCheckBox();
		row.AddChild(status);

		Button button = MakeActionButton(text, onPressed);
		row.AddChild(button);
		AddChild(row);
		return button;
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
	/// gold hairline, cream label. The fade-to-transparent gradient is gone.</summary>
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

	public void Refresh(bool busy, bool gameIsRunning)
	{
		_targetLabel.Text = "Target: " + Installer.GetLuaWorkspacePath().Replace('\\', '/');

		bool reinstallEnabled = !busy && !gameIsRunning;
		bool moddingEnabled = reinstallEnabled && Installer.AssetsExist();

		_reinstall.Disabled = !reinstallEnabled;
		_unpackBanks.Disabled = !moddingEnabled;
		_decompileLua.Disabled = !moddingEnabled;
		_compileShaders.Disabled = !moddingEnabled;
		_buildGameCode.Disabled = !reinstallEnabled;

		// GATED ON THE ASSETS EXISTING, like the other three, because there is nothing to build
		// from without them — and on the game not running, because the prebuild and a live session
		// would be two writers on one cache directory.
		_buildAssets.Disabled = !moddingEnabled;

		Installer.ReinstallStepStatus status = Installer.GetReinstallStepStatus();
		SetStatus(_unpackBanksStatus, status.Banks);
		SetStatus(_decompileLuaStatus, status.Lua);
		SetStatus(_compileShadersStatus, status.Shaders);
		SetStatus(_buildGameCodeStatus, !Recompile.GameCodeBuild.NeedsBuildCached(out _));
	}

	private static void SetStatus(Label label, bool done)
	{
		label.Text = done ? CheckGlyph : string.Empty;
		label.AddThemeStyleboxOverride("normal", CheckFace(done));
	}

	public void SetProgress(string operationLabel, string statusMessage, string errorMessage = "")
	{
		bool any = operationLabel.Length != 0 || statusMessage.Length != 0;
		_progressSeparator.Visible = any || errorMessage.Length != 0;
		_operationLabel.Visible = any;
		_operationLabel.Text = operationLabel;
		_statusLabel.Visible = statusMessage.Length != 0;
		_statusLabel.Text = statusMessage;
		_errorLabel.Visible = errorMessage.Length != 0;
		_errorLabel.Text = "Error: " + errorMessage;
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
