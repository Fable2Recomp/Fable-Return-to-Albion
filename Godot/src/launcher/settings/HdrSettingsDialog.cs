using System;
using System.Globalization;
using Godot;
using Refii.Launcher.Core;
using Refii.Launcher.Native;

namespace Refii.Launcher;

/// <summary>
/// Modal dialog for calibrating HDR display peak brightness and paper white reference luminance,
/// styled consistently with the Lionhead launcher design language.
/// </summary>
[Tool]
public partial class HdrSettingsDialog : Control
{
	private const int PopupWidth = 560;

	private readonly RuntimeSettingsModel _model;
	private readonly SmartStyle _style;
	private readonly Font? _font;
	private readonly int _fontSize;
	private readonly Action _onSave;

	private HSlider _peakSlider = null!;
	private SpinBox _peakSpin = null!;
	private HSlider _whiteSlider = null!;
	private SpinBox _whiteSpin = null!;
	private Label _metricsLabel = null!;
	private Button _detectBtn = null!;
	private Label _detectStatusLabel = null!;
	private int _detectFlashGeneration;
	private bool _updating;

	public HdrSettingsDialog(
		RuntimeSettingsModel model,
		SmartStyle style,
		Font? font,
		int fontSize,
		Action onSave)
	{
		_model = model;
		_style = style;
		_font = font;
		_fontSize = fontSize;
		_onSave = onSave;
	}

	public override void _Ready()
	{
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Stop;

		// Semitransparent warm ink backdrop
		var dim = new ColorRect { Color = LauncherTokens.InkGlass };
		dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		dim.GuiInput += @event =>
		{
			if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
			{
				Dismiss();
			}
		};
		AddChild(dim);

		var centre = new CenterContainer();
		centre.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		centre.MouseFilter = MouseFilterEnum.Ignore;
		AddChild(centre);

		// Main dialog card
		var frame = new PanelContainer
		{
			CustomMinimumSize = new Vector2(PopupWidth, 0),
			MouseFilter = MouseFilterEnum.Stop,
		};

		var frameBox = new StyleBoxFlat
		{
			BgColor = _style.PopupBg,
			BorderColor = _style.Border,
			CornerRadiusBottomLeft = LauncherTokens.RadiusMd,
			CornerRadiusBottomRight = LauncherTokens.RadiusMd,
			CornerRadiusTopLeft = LauncherTokens.RadiusMd,
			CornerRadiusTopRight = LauncherTokens.RadiusMd,
			ShadowColor = new Color(0, 0, 0, 0.4f),
			ShadowSize = 12,
			ShadowOffset = new Vector2(0, 4),
			ContentMarginLeft = LauncherTokens.Sp4,
			ContentMarginRight = LauncherTokens.Sp4,
			ContentMarginTop = LauncherTokens.Sp3,
			ContentMarginBottom = LauncherTokens.Sp4,
		};
		frameBox.SetBorderWidthAll(1);
		frame.AddThemeStyleboxOverride("panel", frameBox);
		centre.AddChild(frame);

		var rootCol = new VBoxContainer();
		rootCol.AddThemeConstantOverride("separation", LauncherTokens.Sp3);
		frame.AddChild(rootCol);

		// Header
		var headerCol = new VBoxContainer();
		headerCol.AddThemeConstantOverride("separation", 2);

		var title = MakeLabel("HDR Display Calibration");
		title.AddThemeColorOverride("font_color", _style.TabSelected);
		title.AddThemeFontSizeOverride("font_size", _fontSize + 3);
		headerCol.AddChild(title);

		var subtitle = MakeLabel(
			"Calibrate the peak highlight brightness and diffuse paper white for your monitor. " +
			"Legacy torch and flame radiance scales dynamically to prevent clipping.");
		subtitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		subtitle.AddThemeColorOverride("font_color", _style.TextDisabled);
		headerCol.AddChild(subtitle);

		rootCol.AddChild(headerCol);
		rootCol.AddChild(new HSeparator());

		// Settings controls
		var controlsCol = new VBoxContainer();
		controlsCol.AddThemeConstantOverride("separation", LauncherTokens.Sp3);

		// 1. Peak Brightness Row
		double currentPeak = ReadDouble("hdr_peak_nits", 1000.0);
		if (currentPeak <= 0.0) currentPeak = 1000.0;

		var peakHeader = new HBoxContainer();
		var peakTitle = MakeLabel("Peak Brightness (nits)");
		peakTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		peakHeader.AddChild(peakTitle);

		_peakSpin = new SpinBox
		{
			MinValue = 100,
			MaxValue = 10000,
			Step = 10,
			Value = currentPeak,
			CustomMinimumSize = new Vector2(100, 0),
		};
		Apply(_peakSpin);
		peakHeader.AddChild(_peakSpin);
		controlsCol.AddChild(peakHeader);

		_peakSlider = new HSlider
		{
			MinValue = 400,
			MaxValue = 2000,
			Step = 25,
			Value = Math.Clamp(currentPeak, 400.0, 2000.0),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		Apply(_peakSlider);
		controlsCol.AddChild(_peakSlider);

		var peakDesc = MakeLabel("The maximum nit capacity for specular highlights, sun glints, and spells.");
		peakDesc.AddThemeColorOverride("font_color", _style.TextDisabled);
		controlsCol.AddChild(peakDesc);

		// 2. Paper White Row
		double currentWhite = ReadDouble("hdr_reference_nits", 200.0);
		if (currentWhite <= 0.0) currentWhite = 200.0;

		var whiteHeader = new HBoxContainer();
		var whiteTitle = MakeLabel("Paper White (nits)");
		whiteTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		whiteHeader.AddChild(whiteTitle);

		_whiteSpin = new SpinBox
		{
			MinValue = 50,
			MaxValue = 1000,
			Step = 5,
			Value = currentWhite,
			CustomMinimumSize = new Vector2(100, 0),
		};
		Apply(_whiteSpin);
		whiteHeader.AddChild(_whiteSpin);
		controlsCol.AddChild(whiteHeader);

		_whiteSlider = new HSlider
		{
			MinValue = 80,
			MaxValue = 500,
			Step = 10,
			Value = Math.Clamp(currentWhite, 80.0, 500.0),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		Apply(_whiteSlider);
		controlsCol.AddChild(_whiteSlider);

		var whiteDesc = MakeLabel("Baseline luminance for diffuse white surfaces and UI text (standard: 200 nits).");
		whiteDesc.AddThemeColorOverride("font_color", _style.TextDisabled);
		controlsCol.AddChild(whiteDesc);

		rootCol.AddChild(controlsCol);

		// 3. Dynamic Calculation Status Box
		var metricsBox = new PanelContainer();
		var metricsStyle = new StyleBoxFlat
		{
			BgColor = _style.ChildBg,
			BorderColor = _style.Border,
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = LauncherTokens.Sp3,
			ContentMarginRight = LauncherTokens.Sp3,
			ContentMarginTop = 6,
			ContentMarginBottom = 6,
		};
		metricsStyle.SetBorderWidthAll(1);
		metricsBox.AddThemeStyleboxOverride("panel", metricsStyle);

		_metricsLabel = MakeLabel(string.Empty);
		_metricsLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		metricsBox.AddChild(_metricsLabel);
		rootCol.AddChild(metricsBox);

		// Sync logic
		_peakSlider.ValueChanged += val =>
		{
			if (_updating) return;
			_updating = true;
			_peakSpin.Value = val;
			CommitValues();
			_updating = false;
		};

		_peakSpin.ValueChanged += val =>
		{
			if (_updating) return;
			_updating = true;
			_peakSlider.Value = Math.Clamp(val, _peakSlider.MinValue, _peakSlider.MaxValue);
			CommitValues();
			_updating = false;
		};

		_whiteSlider.ValueChanged += val =>
		{
			if (_updating) return;
			_updating = true;
			_whiteSpin.Value = val;
			CommitValues();
			_updating = false;
		};

		_whiteSpin.ValueChanged += val =>
		{
			if (_updating) return;
			_updating = true;
			_whiteSlider.Value = Math.Clamp(val, _whiteSlider.MinValue, _whiteSlider.MaxValue);
			CommitValues();
			_updating = false;
		};

		UpdateMetrics();

		// Footer buttons and status feedback
		rootCol.AddChild(new HSeparator());

		_detectStatusLabel = MakeLabel(string.Empty);
		_detectStatusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_detectStatusLabel.Visible = false;
		rootCol.AddChild(_detectStatusLabel);

		var footer = new HBoxContainer();
		footer.AddThemeConstantOverride("separation", LauncherTokens.Sp2);

		_detectBtn = new Button { Text = "Detect Display" };
		Apply(_detectBtn);
		_detectBtn.TooltipText = "Query Windows DXGI or Linux DRM hardware reports for monitor peak brightness.";
		_detectBtn.Pressed += OnDetectDisplay;
		footer.AddChild(_detectBtn);

		var resetBtn = new Button { Text = "Reset Defaults" };
		Apply(resetBtn);
		resetBtn.TooltipText = "Reset to standard 1000 nits peak and 200 nits paper white.";
		resetBtn.Pressed += () =>
		{
			_updating = true;
			_peakSpin.Value = 1000;
			_peakSlider.Value = 1000;
			_whiteSpin.Value = 200;
			_whiteSlider.Value = 200;
			CommitValues();
			_updating = false;
			_detectStatusLabel.Visible = false;
		};
		footer.AddChild(resetBtn);

		var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		footer.AddChild(spacer);

		var doneBtn = new Button { Text = "Done" };
		Apply(doneBtn);
		var btnNormal = new StyleBoxFlat
		{
			BgColor = LauncherTokens.Emerald,
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = LauncherTokens.Sp4,
			ContentMarginRight = LauncherTokens.Sp4,
			ContentMarginTop = 4,
			ContentMarginBottom = 4,
		};
		var btnHover = new StyleBoxFlat
		{
			BgColor = LauncherTokens.EmeraldLit,
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = LauncherTokens.Sp4,
			ContentMarginRight = LauncherTokens.Sp4,
			ContentMarginTop = 4,
			ContentMarginBottom = 4,
		};
		doneBtn.AddThemeStyleboxOverride("normal", btnNormal);
		doneBtn.AddThemeStyleboxOverride("hover", btnHover);
		doneBtn.AddThemeStyleboxOverride("pressed", btnHover);
		doneBtn.AddThemeColorOverride("font_color", LauncherTokens.Cream);
		doneBtn.AddThemeColorOverride("font_hover_color", Colors.White);
		doneBtn.Pressed += Dismiss;
		footer.AddChild(doneBtn);

		rootCol.AddChild(footer);
	}

	private void OnDetectDisplay()
	{
		var info = HdrDisplayDetector.QueryPrimaryDisplay();
		int currentGen = ++_detectFlashGeneration;

		if (info.Success && info.PeakNits > 0f)
		{
			_updating = true;
			_peakSpin.Value = Math.Round(info.PeakNits);
			_peakSlider.Value = Math.Clamp(info.PeakNits, (float)_peakSlider.MinValue, (float)_peakSlider.MaxValue);
			CommitValues();
			_updating = false;

			_detectStatusLabel.Text = $"✓ {info.StatusMessage}";
			_detectStatusLabel.AddThemeColorOverride("font_color", LauncherTokens.EmeraldLit);
			_detectStatusLabel.Visible = true;

			FlashDetectButton(true, "✓ Detected!");
		}
		else
		{
			_detectStatusLabel.Text = $"✗ {info.StatusMessage}";
			_detectStatusLabel.AddThemeColorOverride("font_color", new Color("#ff6b6b"));
			_detectStatusLabel.Visible = true;

			FlashDetectButton(false, "✗ Not Found");
		}

		var timer = GetTree().CreateTimer(2.5);
		timer.Timeout += () =>
		{
			if (currentGen == _detectFlashGeneration && IsInstanceValid(this) && IsInstanceValid(_detectBtn))
			{
				RevertDetectButton();
			}
		};
	}

	private void FlashDetectButton(bool success, string label)
	{
		_detectBtn.Text = label;
		var flashBox = new StyleBoxFlat
		{
			BgColor = success ? LauncherTokens.Emerald : LauncherTokens.Crimson,
			BorderColor = success ? LauncherTokens.GoldBright : new Color("#ff6b6b"),
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = LauncherTokens.Sp3,
			ContentMarginRight = LauncherTokens.Sp3,
			ContentMarginTop = 4,
			ContentMarginBottom = 4,
		};
		flashBox.SetBorderWidthAll(1);
		_detectBtn.AddThemeStyleboxOverride("normal", flashBox);
		_detectBtn.AddThemeStyleboxOverride("hover", flashBox);
		_detectBtn.AddThemeStyleboxOverride("pressed", flashBox);
		_detectBtn.AddThemeColorOverride("font_color", Colors.White);
		_detectBtn.AddThemeColorOverride("font_hover_color", Colors.White);
	}

	private void RevertDetectButton()
	{
		_detectBtn.Text = "Detect Display";
		_detectBtn.RemoveThemeStyleboxOverride("normal");
		_detectBtn.RemoveThemeStyleboxOverride("hover");
		_detectBtn.RemoveThemeStyleboxOverride("pressed");
		_detectBtn.RemoveThemeColorOverride("font_color");
		_detectBtn.RemoveThemeColorOverride("font_hover_color");
	}

	private void CommitValues()
	{
		_model.Values["hdr_peak_nits"] = ((int)Math.Round(_peakSpin.Value)).ToString(CultureInfo.InvariantCulture);
		_model.Values["hdr_reference_nits"] = ((int)Math.Round(_whiteSpin.Value)).ToString(CultureInfo.InvariantCulture);
		_model.Save();
		UpdateMetrics();
		_onSave();
	}

	private void UpdateMetrics()
	{
		double peak = _peakSpin.Value;
		double white = _whiteSpin.Value;
		double headroom = Math.Max(peak / Math.Max(white, 1.0), 1.0);
		double boost = 1.0 + 0.375 * (headroom - 1.0);
		double flameNits = boost * white;

		_metricsLabel.Text =
			$"Dynamic Headroom: {headroom:0.00}x  |  Legacy Flame Radiance: {boost:0.00}x (~{flameNits:0} nits)\n" +
			$"Unbounded Specular Headroom: ~{peak - flameNits:0} nits remaining for glints, sun, and bloom.";
	}

	private double ReadDouble(string key, double fallback)
	{
		if (_model.Values.TryGetValue(key, out string? str) &&
		    double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
		{
			return val;
		}
		return fallback;
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
		{
			Dismiss();
			GetViewport().SetInputAsHandled();
		}
	}

	private void Dismiss() => QueueFree();

	private void Apply(Control control)
	{
		if (_font is not null)
		{
			control.AddThemeFontOverride("font", _font);
		}
		control.AddThemeFontSizeOverride("font_size", _fontSize);
		if (control is Button or HSlider or SpinBox)
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
