using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Refii.Launcher.Core;

namespace Refii.Launcher;

/// <summary>
/// Modal dialog for tuning placed lighting and shadow budgets, reach, and ranges,
/// as well as advanced directional and atmospheric lighting controls,
/// styled consistently with the Lionhead launcher design language.
/// </summary>
[Tool]
public partial class LightingSettingsDialog : Control
{
	private const int PopupWidth = 680;

	private readonly RuntimeSettingsModel _model;
	private readonly SmartStyle _style;
	private readonly Font? _font;
	private readonly int _fontSize;
	private readonly Action _onSave;

	// Core range controls
	private HSlider _lightRangeSlider = null!;
	private SpinBox _lightRangeSpin = null!;
	private HSlider _shadowRangeSlider = null!;
	private SpinBox _shadowRangeSpin = null!;

	// Advanced control sync callbacks
	private readonly List<Action> _rebindControls = new();

	private Label _metricsLabel = null!;
	private Button _perfPresetBtn = null!;
	private Button _balancedPresetBtn = null!;
	private Button _qualityPresetBtn = null!;

	private bool _updating;

	public LightingSettingsDialog(
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

		// Calculate available height to clamp dialog within launcher window (with padding)
		float viewportHeight = GetViewportRect().Size.Y;
		float availableHeight = viewportHeight > 100f ? viewportHeight - 60f : 660f;
		float dialogHeight = Math.Clamp(availableHeight, 400f, 660f);

		// Main dialog card
		var frame = new PanelContainer
		{
			CustomMinimumSize = new Vector2(PopupWidth, dialogHeight),
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

		var rootCol = new VBoxContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
		};
		rootCol.AddThemeConstantOverride("separation", LauncherTokens.Sp3);
		frame.AddChild(rootCol);

		// 1. FIXED HEADER
		var headerCol = new VBoxContainer();
		headerCol.AddThemeConstantOverride("separation", 2);

		var title = MakeLabel("Advanced Lighting & Shadows");
		title.AddThemeColorOverride("font_color", _style.TabSelected);
		title.AddThemeFontSizeOverride("font_size", _fontSize + 3);
		headerCol.AddChild(title);

		var subtitle = MakeLabel(
			"Configure fixture light and dynamic shadow draw distances in metres, " +
			"and atmospheric illumination. Presets adjust core distance ranges together or you can tune each dial individually.");
		subtitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		subtitle.AddThemeColorOverride("font_color", _style.TextDisabled);
		headerCol.AddChild(subtitle);

		rootCol.AddChild(headerCol);

		// Presets Quick-Select Row
		var presetRow = new HBoxContainer();
		presetRow.AddThemeConstantOverride("separation", LauncherTokens.Sp2);
		var presetLabel = MakeLabel("Presets:");
		presetLabel.AddThemeColorOverride("font_color", _style.TextDisabled);
		presetRow.AddChild(presetLabel);

		_perfPresetBtn = new Button { Text = "Performance" };
		StyleButton(_perfPresetBtn);
		_perfPresetBtn.TooltipText = "Focused distance ranges: 45m light range, 30m shadow range.";
		_perfPresetBtn.Pressed += () => ApplyNamedPreset("performance");
		presetRow.AddChild(_perfPresetBtn);

		_balancedPresetBtn = new Button { Text = "Balanced" };
		StyleButton(_balancedPresetBtn);
		_balancedPresetBtn.TooltipText = "Default balance: 65m light range, 50m shadow range.";
		_balancedPresetBtn.Pressed += () => ApplyNamedPreset("balanced");
		presetRow.AddChild(_balancedPresetBtn);

		_qualityPresetBtn = new Button { Text = "Quality" };
		StyleButton(_qualityPresetBtn);
		_qualityPresetBtn.TooltipText = "Extended fidelity: 85m light range, 70m shadow range.";
		_qualityPresetBtn.Pressed += () => ApplyNamedPreset("quality");
		presetRow.AddChild(_qualityPresetBtn);

		rootCol.AddChild(presetRow);
		rootCol.AddChild(new HSeparator());

		// 2. SCROLLABLE SETTINGS BODY
		var scroll = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		rootCol.AddChild(scroll);

		// Internal margin to provide comfortable padding next to the vertical scroll bar
		var scrollMargin = new MarginContainer
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		scrollMargin.AddThemeConstantOverride("margin_left", LauncherTokens.Sp2);
		scrollMargin.AddThemeConstantOverride("margin_right", LauncherTokens.Sp4 + LauncherTokens.Sp2); // 24px right padding
		scrollMargin.AddThemeConstantOverride("margin_top", LauncherTokens.Sp1);
		scrollMargin.AddThemeConstantOverride("margin_bottom", LauncherTokens.Sp3);
		scroll.AddChild(scrollMargin);

		var contentCol = new VBoxContainer
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		contentCol.AddThemeConstantOverride("separation", LauncherTokens.Sp4);
		scrollMargin.AddChild(contentCol);

		// ==================== SECTION A: CORE DISTANCES & SHADOW COVERAGE ====================
		contentCol.AddChild(MakeSectionHeader("Core Distances & Shadow Coverage"));

		// A1. Light Range (Metres)
		string defLightRange = GetCVarDefault("placed_light_range", "65.0");
		double currentLightRange = RuntimeSettingsModel.ParseDouble(_model.GetValueOrDefault("placed_light_range", defLightRange), defLightRange);
		var lightRangeCtrl = BuildSliderSpinRow(
			"Light Range (metres)",
			"Maximum distance from the camera in metres beyond which placed fixture lights fade and cull (Performance: 45m, Balanced: 65m, Quality: 85m).",
			20.0, 150.0, 5.0, currentLightRange,
			out _lightRangeSlider, out _lightRangeSpin,
			val =>
			{
				_model.Values["placed_light_range"] = val.ToString("0.0", CultureInfo.InvariantCulture);
				CommitCoreValues();
			});
		contentCol.AddChild(lightRangeCtrl);

		// A2. Shadow Range (Metres)
		string defShadowRange = GetCVarDefault("placed_shadow_range", "30.0");
		double currentShadowRange = RuntimeSettingsModel.ParseDouble(_model.GetValueOrDefault("placed_shadow_range", defShadowRange), defShadowRange);
		var shadowRangeCtrl = BuildSliderSpinRow(
			"Shadow Range (metres)",
			"Maximum distance from the camera in metres beyond which placed light shadows fade and cull (Performance: 10m, Balanced: 30m, Quality: 70m).",
			2.0, 100.0, 5.0, currentShadowRange,
			out _shadowRangeSlider, out _shadowRangeSpin,
			val =>
			{
				_model.Values["placed_shadow_range"] = val.ToString("0.0", CultureInfo.InvariantCulture);
				CommitCoreValues();
			});
		contentCol.AddChild(shadowRangeCtrl);

		// ==================== SECTION B: DIRECTIONAL SUN & MOON SHADOWS ====================
		contentCol.AddChild(MakeSectionHeader("Directional Sun & Moon Shadows"));

		contentCol.AddChild(BuildCheckBoxRow(
			"sun_shadows",
			"Directional Sun & Moon Shadows",
			"Dynamic real-time shadow cascades cast across the terrain by the sun and moon (default: on)."));

		contentCol.AddChild(BuildSliderSpinRow(
			"sun_shadow_distance",
			"Sun Shadow Distance (metres)",
			"Maximum distance in metres from the camera for directional sun and moon shadows (default: 200m).",
			50.0, 600.0, 25.0));

		contentCol.AddChild(BuildSliderSpinRow(
			"sun_shadow_reach_scale",
			"Sun Shadow Reach Multiplier",
			"Multiplier on size-tiered sun shadow reach distances for props and architecture (default: 1.0x).",
			0.2, 3.0, 0.05));

		contentCol.AddChild(BuildOptionRow(
			"sun_shadow_splits",
			"Sun Shadow Cascades",
			"Number of shadow cascade splits used by directional sunlight (default: 2 cascades).",
			new[] { "2", "4" },
			new[] { "2 Cascades (Lower GPU load)", "4 Cascades (Crisper near shadows)" }));

		contentCol.AddChild(BuildOptionRow(
			"sun_shadow_map",
			"Sun Shadow Map Resolution",
			"Resolution of the directional shadow map atlas (default: 4096).",
			new[] { "2048", "4096", "8192" },
			new[] { "2048 x 2048 (Performance)", "4096 x 4096 (Standard)", "8192 x 8192 (Ultra)" }));

		contentCol.AddChild(BuildSliderSpinRow(
			"sun_light_scale",
			"Sun Energy Multiplier",
			"Multiplier on directional sun and moon light intensity (default: 1.35x).",
			0.2, 3.0, 0.05));

		contentCol.AddChild(BuildOptionRow(
			"tree_shadow_reach",
			"Tree Shadow Reach",
			"Distance at which tree canopy shadows are cast into the world (default: Full Cascade Depth).",
			new[] { "full", "stated" },
			new[] { "Full Cascade Depth (recommended)", "Stated Range (authentic 360)" }));

		// ==================== SECTION C: PLACED FIXTURES & WORLD SHADOWS ====================
		contentCol.AddChild(MakeSectionHeader("Placed Fixtures & World Shadows"));

		contentCol.AddChild(BuildCheckBoxRow(
			"placed_light_shadows",
			"Placed Fixture Shadows",
			"Enable dynamic shadow casting for torches, lanterns, and braziers based on the active shadow budget (default: on)."));

		contentCol.AddChild(BuildCheckBoxRow(
			"placed_light_fade",
			"Distance Cutoff Fading",
			"Gradually fade and cull distant fixtures beyond the light range to reduce draw calls (default: on)."));

		contentCol.AddChild(BuildSliderSpinRow(
			"placed_light_limit",
			"Lit Fixture Limit",
			"The most placed fixtures lit at once, the nearest first; the rest fade out until near. 0 lights every fixture in range (default: 0; Quality preset on a phone: 24).",
			0.0, 64.0, 1.0));

		contentCol.AddChild(BuildSliderSpinRow(
			"placed_light_scale",
			"Fixture Intensity Scale",
			"Multiplier on outdoor torch, lamp, and fixture brightness (default: 1.5x).",
			0.2, 3.0, 0.05));

		contentCol.AddChild(BuildSliderSpinRow(
			"placed_light_reach_scale",
			"Fixture Reach Scale",
			"Multiplier on outdoor torch, lamp, and fixture radius (default: 1.5x).",
			0.25, 3.0, 0.05));

		contentCol.AddChild(BuildSliderSpinRow(
			"building_interior_ambient_scale",
			"Building Interior Ambient Scale",
			"Scales down ambient light inside town buildings in outdoor levels so rooms are not washed out (default: 0.5x; caves unaffected).",
			0.0, 2.0, 0.05));

		contentCol.AddChild(BuildSliderSpinRow(
			"building_interior_light_scale",
			"Building Interior Light Scale",
			"Multiplier on placed light intensity inside town buildings in outdoor levels (default: 0.65x; caves unaffected).",
			0.0, 3.0, 0.05));

		contentCol.AddChild(BuildSliderSpinRow(
			"building_interior_reach_scale",
			"Building Interior Reach Scale",
			"Multiplier on placed light radius inside town buildings in outdoor levels (default: 1.0x; caves unaffected).",
			0.25, 3.0, 0.05));

		contentCol.AddChild(BuildSliderSpinRow(
			"unattached_light_scale",
			"Unattached Floating Light Scale",
			"Multiplier on placed OmniLight3D/SpotLight3D nodes not connected to any 3D light fixture in outdoor levels (0.0 disables them; default: 0.35x; caves unaffected).",
			0.0, 2.0, 0.05));

		contentCol.AddChild(BuildOptionRow(
			"static_shadow_resolution",
			"Static Shadow Resolution",
			"Atlas size for pre-baked static fixture shadows (2048=balanced, 4096=sharp, 8192=ultra). Modifying will trigger a re-bake during the startup warmup screen.",
			new[] { "2048", "4096", "8192" },
			new[] { "2048 (Default)", "4096 (High)", "8192 (Ultra)" }));

		// ==================== SECTION D: ILLUMINATION & TONE MAPPING ====================
		contentCol.AddChild(MakeSectionHeader("Illumination & Tone Mapping"));

		contentCol.AddChild(BuildCheckBoxRow(
			"gi_half_resolution",
			"Half-Resolution GI Resolve",
			"Evaluate global illumination at half resolution for improved frame rates (default: on)."));

		contentCol.AddChild(BuildSliderSpinRow(
			"ambient_light_scale",
			"Ambient Fill Multiplier",
			"General ambient fill light intensity across exterior environments (default: 1.0x).",
			0.2, 3.0, 0.1));

		contentCol.AddChild(BuildSliderSpinRow(
			"bloom_scale",
			"Bloom Intensity Scale",
			"Multiplier on the scene's bloom in the modern compositor (default: 0, off; 1.0x is the original's). The sun, moon and lamp halos glow regardless.",
			0.0, 2.0, 0.05));

		contentCol.AddChild(BuildSliderSpinRow(
			"modern_dark_lift",
			"Exterior Dark Frame Lift",
			"Exposure compensation cap for very dark outdoor night scenes (default: 1.0).",
			1.0, 16.0, 0.5));

		contentCol.AddChild(BuildOptionRow(
			"modern_tone_curve",
			"Tone Mapping Curve",
			"Color tone curve mapping scene radiance to display output (default: fable).",
			new[] { "fable", "filmic", "all" },
			new[] { "Fable (Stylised HDR + CDF contrast)", "Filmic (Interiors only)", "All (Filmic everywhere)" }));

		contentCol.AddChild(BuildCheckBoxRow(
			"modern_depth_of_field",
			"Depth of Field Blur",
			"Cinematic focal distance blurring during cutscenes and dialogue (default: off)."));

		contentCol.AddChild(BuildSliderSpinRow(
			"interior_ambient_scale",
			"Interior Ambient Scale",
			"Flat ambient fill scalar in caves, cellars, and building interiors (default: 1.0x).",
			0.1, 4.0, 0.1));

		contentCol.AddChild(BuildSliderSpinRow(
			"interior_ambient_neutral",
			"Interior Neutral Lift",
			"Pulls interior ambient color tint toward neutral white to preserve color vibrancy (default: 0.0).",
			0.0, 1.0, 0.05));

		contentCol.AddChild(BuildSliderSpinRow(
			"interior_dark_lift",
			"Interior Dark Frame Lift",
			"Exposure compensation cap for pitch-dark caves and interior chambers (default: 1.5).",
			1.0, 16.0, 0.25));

		contentCol.AddChild(BuildSliderSpinRow(
			"interior_fog_scale",
			"Interior Fog Density",
			"Atmospheric fog density multiplier inside caves and interiors (default: 0.2).",
			0.0, 1.0, 0.05));

		contentCol.AddChild(BuildSliderSpinRow(
			"fx_additive_gain",
			"Additive Particle FX Gain",
			"Radiance multiplier on particle effects, Will spells, and magic glows (default: 1.0x).",
			0.2, 3.0, 0.1));

		contentCol.AddChild(BuildSliderSpinRow(
			"baked_light_weight",
			"Fable Lightmap Bake Weight",
			"Blend weight of original 2008 offline vertex bakes in the modern lighting model (default: 0.45).",
			0.0, 1.5, 0.05));

		contentCol.AddChild(BuildCheckBoxRow(
			"prop_probe_bake_weight",
			"Fable Static Prop Bake Weight",
			"Applies offline vertex bake weighting to unbaked static props and clutter (default: off)."));

		// 3. FIXED FOOTER
		var footerCol = new VBoxContainer();
		footerCol.AddThemeConstantOverride("separation", LauncherTokens.Sp2);

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
		footerCol.AddChild(metricsBox);

		footerCol.AddChild(new HSeparator());

		var footerButtons = new HBoxContainer();
		footerButtons.AddThemeConstantOverride("separation", LauncherTokens.Sp2);

		var resetBtn = new Button { Text = "Reset Defaults" };
		StyleButton(resetBtn);
		resetBtn.TooltipText = "Reset all lighting and shadow settings to their canonical default values.";
		resetBtn.Pressed += ResetAllDefaults;
		footerButtons.AddChild(resetBtn);

		var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		footerButtons.AddChild(spacer);

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
		footerButtons.AddChild(doneBtn);

		footerCol.AddChild(footerButtons);
		rootCol.AddChild(footerCol);

		UpdateMetrics();
	}

	private Control MakeSectionHeader(string title)
	{
		var col = new VBoxContainer();
		col.AddThemeConstantOverride("separation", 2);

		var header = MakeLabel(title);
		header.AddThemeColorOverride("font_color", _style.TabSelected);
		header.AddThemeFontSizeOverride("font_size", _fontSize + 1);
		col.AddChild(header);

		var sep = new HSeparator();
		col.AddChild(sep);

		return col;
	}

	private static string GetCVarDefault(string cvarName, string fallback = "")
	{
		RuntimeCVarDefinition? def = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == cvarName);
		return def?.DefaultValue ?? fallback;
	}

	private Control BuildSliderSpinRow(
		string title,
		string description,
		double min,
		double max,
		double step,
		double currentValue,
		out HSlider slider,
		out SpinBox spin,
		Action<double> onCommit)
	{
		var row = new VBoxContainer();
		row.AddThemeConstantOverride("separation", 2);

		var header = new HBoxContainer();
		var titleLabel = MakeLabel(title);
		titleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		header.AddChild(titleLabel);

		spin = new SpinBox
		{
			MinValue = min,
			MaxValue = max * 2, // Allow typing higher values
			Step = step,
			Value = currentValue,
			CustomMinimumSize = new Vector2(80, 0),
		};
		Apply(spin);
		header.AddChild(spin);
		row.AddChild(header);

		var descLabel = MakeLabel(description);
		descLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		descLabel.AddThemeColorOverride("font_color", _style.TextDisabled);
		row.AddChild(descLabel);

		slider = new HSlider
		{
			MinValue = min,
			MaxValue = max,
			Step = step,
			Value = Math.Clamp(currentValue, min, max),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			Scrollable = false,
		};
		Apply(slider);
		row.AddChild(slider);

		var sRef = slider;
		var spRef = spin;

		sRef.ValueChanged += val =>
		{
			if (_updating) return;
			_updating = true;
			spRef.Value = val;
			onCommit(val);
			_updating = false;
		};

		spRef.ValueChanged += val =>
		{
			if (_updating) return;
			_updating = true;
			sRef.Value = Math.Clamp(val, min, max);
			onCommit(val);
			_updating = false;
		};

		return row;
	}

	private Control BuildSliderSpinRow(
		string cvarName,
		string title,
		string description,
		double min,
		double max,
		double step)
	{
		string defStr = GetCVarDefault(cvarName, "1.0");
		double defVal = RuntimeSettingsModel.ParseDouble(defStr, defStr);
		double currentValue = RuntimeSettingsModel.ParseDouble(
			_model.GetValueOrDefault(cvarName, defStr),
			defStr);

		Control ctrl = BuildSliderSpinRow(
			title, description, min, max, step, currentValue,
			out HSlider slider, out SpinBox spin,
			val =>
			{
				_model.Values[cvarName] = step < 1.0
					? val.ToString("0.00", CultureInfo.InvariantCulture)
					: ((int)Math.Round(val)).ToString(CultureInfo.InvariantCulture);
				_model.Save();
				_onSave();
			});

		_rebindControls.Add(() =>
		{
			string currentDef = GetCVarDefault(cvarName, "1.0");
			double v = RuntimeSettingsModel.ParseDouble(
				_model.GetValueOrDefault(cvarName, currentDef),
				currentDef);
			spin.Value = v;
			slider.Value = Math.Clamp(v, min, max);
		});

		return ctrl;
	}

	private Control BuildCheckBoxRow(
		string cvarName,
		string title,
		string description)
	{
		var row = new VBoxContainer();
		row.AddThemeConstantOverride("separation", 2);

		string defStr = GetCVarDefault(cvarName, "false");
		bool defVal = ParseBool(defStr, false);
		bool current = ParseBool(
			_model.GetValueOrDefault(cvarName, defStr),
			defVal);

		var cb = new CheckBox
		{
			Text = title,
			ButtonPressed = current,
		};
		StyleCheckBox(cb);

		cb.Toggled += state =>
		{
			if (_updating) return;
			_model.Values[cvarName] = state ? "true" : "false";
			_model.Save();
			_onSave();
		};

		_rebindControls.Add(() =>
		{
			string currentDef = GetCVarDefault(cvarName, "false");
			cb.ButtonPressed = ParseBool(
				_model.GetValueOrDefault(cvarName, currentDef),
				ParseBool(currentDef, false));
		});

		row.AddChild(cb);

		var descLabel = MakeLabel(description);
		descLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		descLabel.AddThemeColorOverride("font_color", _style.TextDisabled);
		row.AddChild(descLabel);

		return row;
	}

	private Control BuildOptionRow(
		string cvarName,
		string title,
		string description,
		string[] options,
		string[]? optionLabels = null)
	{
		var row = new VBoxContainer();
		row.AddThemeConstantOverride("separation", 2);

		var header = new HBoxContainer();
		var titleLabel = MakeLabel(title);
		titleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		header.AddChild(titleLabel);

		var opt = new OptionButton
		{
			CustomMinimumSize = new Vector2(170, 0),
		};
		StyleDropdown(opt);

		for (int i = 0; i < options.Length; i++)
		{
			string label = optionLabels != null && i < optionLabels.Length ? optionLabels[i] : options[i];
			opt.AddItem(label, i);
		}

		string defStr = GetCVarDefault(cvarName, options[0]);
		string current = _model.GetValueOrDefault(cvarName, defStr).Trim().Trim('"');
		int selectedIdx = Array.FindIndex(options, o => o.Equals(current, StringComparison.OrdinalIgnoreCase));
		opt.Selected = selectedIdx >= 0 ? selectedIdx : 0;

		opt.ItemSelected += idx =>
		{
			if (_updating) return;
			if (idx >= 0 && idx < options.Length)
			{
				_model.Values[cvarName] = options[idx];
				_model.Save();
				_onSave();
			}
		};

		_rebindControls.Add(() =>
		{
			string currentDef = GetCVarDefault(cvarName, options[0]);
			string cur = _model.GetValueOrDefault(cvarName, currentDef).Trim().Trim('"');
			int idx = Array.FindIndex(options, o => o.Equals(cur, StringComparison.OrdinalIgnoreCase));
			opt.Selected = idx >= 0 ? idx : 0;
		});

		header.AddChild(opt);
		row.AddChild(header);

		var descLabel = MakeLabel(description);
		descLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		descLabel.AddThemeColorOverride("font_color", _style.TextDisabled);
		row.AddChild(descLabel);

		return row;
	}

	private void CommitCoreValues()
	{
		_model.Values["placed_light_range"] = _lightRangeSpin.Value.ToString("0.0", CultureInfo.InvariantCulture);
		_model.Values["placed_shadow_range"] = _shadowRangeSpin.Value.ToString("0.0", CultureInfo.InvariantCulture);
		_model.Values["lighting_preset"] = _model.CurrentLightingPreset();

		_model.Save();
		UpdateMetrics();
		_onSave();
	}

	private void ApplyNamedPreset(string presetName)
	{
		_model.ApplyLightingPreset(presetName);

		_updating = true;
		string defLightRange = GetCVarDefault("placed_light_range", "65.0");
		string defShadowRange = GetCVarDefault("placed_shadow_range", "50.0");

		double lr = RuntimeSettingsModel.ParseDouble(_model.GetValueOrDefault("placed_light_range", defLightRange), defLightRange);
		double sr = RuntimeSettingsModel.ParseDouble(_model.GetValueOrDefault("placed_shadow_range", defShadowRange), defShadowRange);

		_lightRangeSpin.Value = lr;
		_lightRangeSlider.Value = Math.Clamp(lr, 20.0, 150.0);
		_shadowRangeSpin.Value = sr;
		_shadowRangeSlider.Value = Math.Clamp(sr, 10.0, 100.0);

		_updating = false;
		UpdateMetrics();
		_onSave();
	}

	private void ResetAllDefaults()
	{
		// 1. Reset core settings to Balanced preset
		_model.ApplyLightingPreset("balanced");

		// 2. Reset all advanced lighting cvars to their canonical defaults from RuntimeCVarSet
		string[] advancedLightingCVars = new[]
		{
			"sun_shadows",
			"sun_shadow_distance",
			"sun_shadow_reach_scale",
			"sun_shadow_splits",
			"sun_shadow_map",
			"sun_light_scale",
			"tree_shadow_reach",
			"placed_light_shadows",
			"placed_light_fade",
			"placed_light_limit",
			"placed_light_scale",
			"placed_light_reach_scale",
			"building_interior_ambient_scale",
			"building_interior_light_scale",
			"building_interior_reach_scale",
			"unattached_light_scale",
			"static_shadow_resolution",
			"gi_half_resolution",
			"ambient_light_scale",
			"bloom_scale",
			"modern_dark_lift",
			"modern_tone_curve",
			"modern_depth_of_field",
			"interior_ambient_scale",
			"interior_ambient_neutral",
			"interior_dark_lift",
			"interior_fog_scale",
			"fx_additive_gain",
			"baked_light_weight",
			"prop_probe_bake_weight",
		};

		foreach (string cvar in advancedLightingCVars)
		{
			string defVal = GetCVarDefault(cvar);
			if (!string.IsNullOrEmpty(defVal))
			{
				_model.Values[cvar] = defVal;
			}
		}

		_model.Save();

		// 3. Re-sync all UI widgets
		_updating = true;
		string defLightRange = GetCVarDefault("placed_light_range", "65.0");
		string defShadowRange = GetCVarDefault("placed_shadow_range", "50.0");

		double lr = RuntimeSettingsModel.ParseDouble(_model.GetValueOrDefault("placed_light_range", defLightRange), defLightRange);
		double sr = RuntimeSettingsModel.ParseDouble(_model.GetValueOrDefault("placed_shadow_range", defShadowRange), defShadowRange);

		_lightRangeSpin.Value = lr;
		_lightRangeSlider.Value = Math.Clamp(lr, 20.0, 150.0);
		_shadowRangeSpin.Value = sr;
		_shadowRangeSlider.Value = Math.Clamp(sr, 10.0, 100.0);

		foreach (Action rebind in _rebindControls)
		{
			rebind();
		}

		_updating = false;
		UpdateMetrics();
		_onSave();
	}

	private void UpdateMetrics()
	{
		string preset = _model.CurrentLightingPreset();
		string presetDisplay = preset.Length > 0 ? char.ToUpperInvariant(preset[0]) + preset[1..] : "Custom";
		double shadowRange = _shadowRangeSpin.Value;
		double lightRange = _lightRangeSpin.Value;

		_metricsLabel.Text =
			$"Preset: {presetDisplay}  |  Light Range: {lightRange:0}m  |  Shadow Range: {shadowRange:0}m\n" +
			$"Range-Governed: All visible lights in view cast dynamic shadows within {shadowRange:0}m with smooth distance fade.";
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
		{
			Dismiss();
			GetViewport().SetInputAsHandled();
		}
	}

	private void StyleDropdown(OptionButton option)
	{
		Apply(option);

		var faceNormal = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentLit,
			BorderColor = new Color(LauncherTokens.Gold, 0.5f),
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = 10,
			ContentMarginRight = 10,
			ContentMarginTop = 4,
			ContentMarginBottom = 4,
		};
		faceNormal.SetBorderWidthAll(1);

		var faceHover = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentLit,
			BorderColor = LauncherTokens.Gold,
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = 10,
			ContentMarginRight = 10,
			ContentMarginTop = 4,
			ContentMarginBottom = 4,
		};
		faceHover.SetBorderWidthAll(1);

		option.AddThemeStyleboxOverride("normal", faceNormal);
		option.AddThemeStyleboxOverride("hover", faceHover);
		option.AddThemeStyleboxOverride("pressed", faceHover);
		option.AddThemeStyleboxOverride("focus", faceHover);

		option.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
		option.AddThemeColorOverride("font_hover_color", LauncherTokens.Walnut);
		option.AddThemeColorOverride("font_pressed_color", LauncherTokens.Walnut);
		option.AddThemeColorOverride("font_focus_color", LauncherTokens.Walnut);
		option.AddThemeColorOverride("font_disabled_color", LauncherTokens.Sepia);

		PopupMenu popup = option.GetPopup();
		if (_font is not null)
		{
			popup.AddThemeFontOverride("font", _font);
		}
		popup.AddThemeFontSizeOverride("font_size", _fontSize);

		var popupPanel = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentRaised,
			BorderColor = LauncherTokens.Gold,
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ShadowColor = new Color(0, 0, 0, 0.45f),
			ShadowSize = 12,
			ShadowOffset = new Vector2(0, 4),
			ContentMarginLeft = 6,
			ContentMarginRight = 6,
			ContentMarginTop = 6,
			ContentMarginBottom = 6,
		};
		popupPanel.SetBorderWidthAll(1);
		popup.AddThemeStyleboxOverride("panel", popupPanel);

		var popupHover = new StyleBoxFlat
		{
			BgColor = new Color(LauncherTokens.Gold, 0.22f),
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm - 2,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm - 2,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm - 2,
			CornerRadiusTopRight = LauncherTokens.RadiusSm - 2,
			ContentMarginLeft = 8,
			ContentMarginRight = 8,
			ContentMarginTop = 3,
			ContentMarginBottom = 3,
		};
		popup.AddThemeStyleboxOverride("hover", popupHover);

		popup.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
		popup.AddThemeColorOverride("font_hover_color", LauncherTokens.Oak);
		popup.AddThemeColorOverride("font_accelerator_color", LauncherTokens.Sepia);
		popup.AddThemeColorOverride("font_disabled_color", LauncherTokens.Sepia);
		popup.AddThemeColorOverride("font_separator_color", LauncherTokens.Sepia);
	}

	private void StyleButton(Button button)
	{
		Apply(button);

		var normal = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentLit,
			BorderColor = new Color(LauncherTokens.Gold, 0.5f),
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = LauncherTokens.Sp3,
			ContentMarginRight = LauncherTokens.Sp3,
			ContentMarginTop = 4,
			ContentMarginBottom = 4,
		};
		normal.SetBorderWidthAll(1);

		var hover = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentLit,
			BorderColor = LauncherTokens.Gold,
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = LauncherTokens.Sp3,
			ContentMarginRight = LauncherTokens.Sp3,
			ContentMarginTop = 4,
			ContentMarginBottom = 4,
		};
		hover.SetBorderWidthAll(1);

		button.AddThemeStyleboxOverride("normal", normal);
		button.AddThemeStyleboxOverride("hover", hover);
		button.AddThemeStyleboxOverride("pressed", hover);
		button.AddThemeStyleboxOverride("focus", hover);
		button.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
		button.AddThemeColorOverride("font_hover_color", LauncherTokens.Walnut);
		button.AddThemeColorOverride("font_pressed_color", LauncherTokens.Walnut);
		button.AddThemeColorOverride("font_focus_color", LauncherTokens.Walnut);
	}

	private void StyleCheckBox(CheckBox cb)
	{
		Apply(cb);
		cb.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
		cb.AddThemeColorOverride("font_pressed_color", LauncherTokens.Walnut);
		cb.AddThemeColorOverride("font_hover_color", LauncherTokens.Oak);
		cb.AddThemeColorOverride("font_hover_pressed_color", LauncherTokens.Oak);
		cb.AddThemeColorOverride("font_focus_color", LauncherTokens.Walnut);
		cb.AddThemeColorOverride("font_disabled_color", LauncherTokens.Sepia);
	}

	private void Dismiss() => QueueFree();

	private void Apply(Control control)
	{
		if (_font is not null)
		{
			control.AddThemeFontOverride("font", _font);
		}
		control.AddThemeFontSizeOverride("font_size", _fontSize);
		if (control is Button or CheckBox or HSlider or SpinBox or OptionButton)
		{
			control.MouseDefaultCursorShape = CursorShape.PointingHand;
		}
		if (control is Slider s)
		{
			s.Scrollable = false;
		}
		if (control is CheckBox cb)
		{
			cb.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
			cb.AddThemeColorOverride("font_pressed_color", LauncherTokens.Walnut);
			cb.AddThemeColorOverride("font_hover_color", LauncherTokens.Oak);
			cb.AddThemeColorOverride("font_hover_pressed_color", LauncherTokens.Oak);
			cb.AddThemeColorOverride("font_focus_color", LauncherTokens.Walnut);
			cb.AddThemeColorOverride("font_disabled_color", LauncherTokens.Sepia);
		}
	}

	private static bool ParseBool(string? value, bool fallback)
	{
		if (string.IsNullOrWhiteSpace(value)) return fallback;
		string norm = RuntimeConfig.NormalizeBoolValue(value);
		return norm == "true";
	}

	private Label MakeLabel(string text)
	{
		var label = new Label { Text = text };
		Apply(label);
		label.AddThemeColorOverride("font_color", _style.Text);
		return label;
	}
}
