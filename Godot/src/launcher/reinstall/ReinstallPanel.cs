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
	private Label _operationLabel = null!;
	private Label _statusLabel = null!;
	private Label _errorLabel = null!;
	private HSeparator _progressSeparator = null!;
	private bool _built;

	public event Action? ReinstallRequested;
	public event Action? UnpackBanksRequested;
	public event Action? DecompileLuaRequested;
	public event Action? CompileShadersRequested;

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
		_unpackBanks = AddAction("Unpack Game Banks",
			() => UnpackBanksRequested?.Invoke());
		_decompileLua = AddAction("Unpack and Decompile Lua",
			() => DecompileLuaRequested?.Invoke());
		_compileShaders = AddAction("Compile Shaders",
			() => CompileShadersRequested?.Invoke());

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

	private const float EdgeFadeFraction = 0.25f;

	private static StyleBox EdgeFaded(Color color)
	{
		var gradient = new Gradient
		{
			Offsets = new[] { 0.0f, EdgeFadeFraction, 1.0f - EdgeFadeFraction, 1.0f },
			Colors = new[]
			{
				new Color(color, 0.0f),
				color,
				color,
				new Color(color, 0.0f),
			},
		};

		var texture = new GradientTexture2D
		{
			Gradient = gradient,
			Width = 256,
			Height = 4,
			Fill = GradientTexture2D.FillEnum.Linear,
			FillFrom = new Vector2(0.0f, 0.0f),
			FillTo = new Vector2(1.0f, 0.0f),
		};

		return new StyleBoxTexture
		{
			Texture = texture,
			ContentMarginLeft = 0.0f,
			ContentMarginRight = 0.0f,
			ContentMarginTop = 0.0f,
			ContentMarginBottom = 0.0f,
		};
	}

	private Button AddAction(string text, Action onPressed)
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

		button.AddThemeStyleboxOverride("normal", EdgeFaded(_style.Button));
		button.AddThemeStyleboxOverride("hover", EdgeFaded(_style.ButtonHovered));
		button.AddThemeStyleboxOverride("pressed", EdgeFaded(_style.ButtonActive));
		button.AddThemeStyleboxOverride("focus", EdgeFaded(_style.ButtonHovered));
		button.AddThemeStyleboxOverride(
			"disabled", EdgeFaded(new Color(_style.Button, _style.Button.A * 0.45f)));
		button.AddThemeColorOverride("font_color", _style.Text);
		button.AddThemeColorOverride("font_hover_color", _style.Text);
		button.AddThemeColorOverride("font_pressed_color", _style.Text);
		button.AddThemeColorOverride("font_disabled_color", _style.TextDisabled);

		button.Pressed += onPressed;
		AddChild(button);
		return button;
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
	}

	private Label MakeLabel(string text)
	{
		var label = new Label { Text = text };
		Apply(label);
		label.AddThemeColorOverride("font_color", _style.Text);
		return label;
	}
}
