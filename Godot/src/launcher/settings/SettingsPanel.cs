using System.Collections.Generic;
using System.Globalization;
using Godot;
using Refii.Launcher.Core;

namespace Refii.Launcher;

[Tool]
public partial class SettingsPanel : VBoxContainer
{
	private readonly RuntimeSettingsModel _model = new();
	private Font? _font;
	private int _fontSize = 18;
	private SmartStyle _style = SmartStyle.FromTheme(
		new Color(0.02f, 0.1f, 0.2f, 1f), new Color(0.2f, 0.8f, 0.4f, 1f));

	// Design-base column widths, at LauncherScale.Unit 1.0. They go through
	// LauncherScale.Px like every other size in the launcher: the drawer's width scales
	// with the window, so fixed columns overflow it at the small end — 300px of columns in
	// a 324px drawer at 1024x640.
	private const int BaseNameColumnWidth = 145;
	private const int BaseDefaultColumnWidth = 75;
	private const int BaseEditorColumnWidth = 80;

	private Label _editingLabel = null!;
	private Label _statusLabel = null!;
	private LineEdit _filterEdit = null!;
	private VBoxContainer _list = null!;
	private bool _built;


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

	/// <summary>
	/// Writes any pending edits to <c>refii.toml</c>.
	/// </summary>
	/// <remarks>
	/// For the asset prebuild, which has to build for the settings the player MEANT rather than the
	/// ones that happen to be on disk — they will usually have just chosen a look in this panel and
	/// then gone to the reinstall tab to build it.
	/// </remarks>
	public void SaveIfDirty() => _model.Save();

	private void Build()
	{
		if (_built)
		{
			return;
		}

		_built = true;
		AddThemeConstantOverride("separation", 6);
		_model.Initialize();
		BuildHeader();
		Rebuild();
	}

	private void Apply(Control control)
	{
		if (_font is not null)
		{
			control.AddThemeFontOverride("font", _font);
		}

		control.AddThemeFontSizeOverride("font_size", _fontSize);
		if (control is Button or CheckBox or LineEdit or SpinBox)
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

	private void BuildHeader()
	{
		_editingLabel = MakeLabel("Editing: " + _model.ConfigPath);
		_editingLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		AddChild(_editingLabel);

		Label explain = MakeLabel(
			"Changes are written to refii.toml immediately. " +
			"Restart-only CVars still require relaunching the game.");
		explain.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		AddChild(explain);

		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 6);
		AddChild(row);

		_filterEdit = new LineEdit
		{
			PlaceholderText = "Filter",
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		Apply(_filterEdit);
		_filterEdit.TextChanged += text =>
		{
			_model.Filter = text;
			Rebuild();
		};
		row.AddChild(_filterEdit);

		var reload = new Button { Text = "Reload" };
		Apply(reload);
		reload.Pressed += () =>
		{
			_model.Reload();
			_filterEdit.Text = _model.Filter;
			Rebuild();
		};
		row.AddChild(reload);

		var reset = new Button { Text = "Reset all to defaults" };
		Apply(reset);
		reset.Pressed += () =>
		{
			_model.ResetAllToDefaults();
			Rebuild();
		};
		row.AddChild(reset);

		_statusLabel = MakeLabel(string.Empty);
		_statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		AddChild(_statusLabel);

		var listBackground = new PanelContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		var listBox = new StyleBoxFlat { BgColor = _style.ChildBg };
		listBox.BorderColor = _style.Border;
		listBox.SetBorderWidthAll(1);
		listBox.ContentMarginLeft = 12;
		listBox.ContentMarginRight = 12;
		listBox.ContentMarginTop = 8;
		listBox.ContentMarginBottom = 8;
		listBackground.AddThemeStyleboxOverride("panel", listBox);
		AddChild(listBackground);

		var scroll = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		listBackground.AddChild(scroll);

		_list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_list.AddThemeConstantOverride("separation", 2);
		scroll.AddChild(_list);
	}

	private void Rebuild()
	{
		_editingLabel.Text = "Editing: " + _model.ConfigPath;
		_statusLabel.Text = _model.StatusMessage;

		foreach (Node child in _list.GetChildren())
		{
			child.QueueFree();
		}

		foreach ((string category, List<RuntimeCVarDefinition> definitions) in _model.BuildGroups())
		{
			var header = MakeLabel(category);
			header.AddThemeColorOverride("font_color", _style.TabSelected);
			_list.AddChild(header);

			var grid = new GridContainer { Columns = 3 };
			grid.AddThemeConstantOverride("h_separation", 10);
			grid.AddThemeConstantOverride("v_separation", 3);
			grid.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			_list.AddChild(grid);

			foreach (RuntimeCVarDefinition definition in definitions)
			{
				AddRow(grid, definition);
			}

			_list.AddChild(new HSeparator());
		}
	}

	private void AddRow(GridContainer grid, RuntimeCVarDefinition definition)
	{
		Label name = MakeLabel(RuntimeSettingsModel.FormatDisplayName(definition));
		name.CustomMinimumSize = new Vector2(LauncherScale.Px(BaseNameColumnWidth), 0);
		name.SizeFlagsHorizontal = SizeFlags.Fill;
		name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
		name.TooltipText = RuntimeSettingsModel.FormatDescription(definition);
		grid.AddChild(name);

		List<RuntimeCVarOption> options = RuntimeSettingsModel.OptionsFor(definition, _model.Values);

		Control editor = BuildEditor(definition, options);
		editor.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		editor.CustomMinimumSize = new Vector2(LauncherScale.Px(BaseEditorColumnWidth), 0);
		grid.AddChild(editor);

		// The default column shows the default the way the dropdown does — "Off", not "0".
		string defaultText = options.Find(o => o.Value == definition.DefaultValue)?.Label
			?? definition.DefaultValue;
		Label defaultLabel = MakeLabel(defaultText);
		defaultLabel.CustomMinimumSize = new Vector2(LauncherScale.Px(BaseDefaultColumnWidth), 0);
		defaultLabel.SizeFlagsHorizontal = SizeFlags.Fill;
		defaultLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
		defaultLabel.TooltipText = defaultText;
		defaultLabel.AddThemeColorOverride("font_color", _style.TextDisabled);
		grid.AddChild(defaultLabel);
	}

	private Control BuildEditor(RuntimeCVarDefinition definition, List<RuntimeCVarOption> options)
	{
		string current = _model.Values.TryGetValue(definition.Name, out string? value)
			? value
			: definition.DefaultValue;

		if (RuntimeSettingsModel.IsLockedRenderingDriver(definition))
		{
			OptionButton locked = BuildDropdown(definition, "vulkan", options);
			locked.Disabled = true;
			locked.TooltipText = "Direct3D 12 is available on Windows only.";
			return locked;
		}

		// A setting with a stated list of values is a dropdown whatever its type: an Int that
		// names its modes shows the names and writes the number.
		if (options.Count != 0)
		{
			return BuildDropdown(definition, current, options);
		}

		switch (definition.Type)
		{
			case RuntimeCVarType.Bool:
			{
				if (definition.Name == "hdr_output")
				{
					return BuildHdrRowEditor(definition, current);
				}

				var check = new CheckBox
				{
					ButtonPressed = RuntimeConfig.NormalizeBoolValue(current) == "true",
				};
				Apply(check);
				check.Toggled += pressed => Commit(definition, pressed ? "true" : "false");
				return check;
			}

			case RuntimeCVarType.Int:
			{
				var spin = new SpinBox
				{
					MinValue = int.MinValue,
					MaxValue = int.MaxValue,
					Step = 1,
					Value = RuntimeSettingsModel.ParseInt(current, definition.DefaultValue),
				};
				Apply(spin);
				spin.ValueChanged += v =>
					Commit(definition, ((int)v).ToString(CultureInfo.InvariantCulture));
				return spin;
			}

			case RuntimeCVarType.Double:
			{
				var spin = new SpinBox
				{
					MinValue = double.MinValue,
					MaxValue = double.MaxValue,
					Step = 0.1,
					Value = RuntimeSettingsModel.ParseDouble(current, definition.DefaultValue),
				};
				Apply(spin);
				spin.ValueChanged += v =>
					Commit(definition, v.ToString("0.###", CultureInfo.InvariantCulture));
				return spin;
			}

			default:
			{
				var edit = new LineEdit { Text = current };
				Apply(edit);
				edit.TextSubmitted += text => Commit(definition, text);
				edit.FocusExited += () => Commit(definition, edit.Text);
				return edit;
			}
		}
	}

	private OptionButton BuildDropdown(
		RuntimeCVarDefinition definition, string current, List<RuntimeCVarOption> options)
	{
		string display = current.Length == 0 ? definition.DefaultValue : current;

		// FitToLongestItem OFF. It defaults ON, which makes the button's MINIMUM width its
		// longest item: one long option once asked for 186px, taking the panel's minimum to
		// 446 against the 416 the drawer gives it. SettingsColumn grows
		// in both directions, so the 30px overflow spilled 15px past each edge of the drawer and
		// clipped the first character off every wrapped line. The editor column's width is the
		// drawer's to decide, not the longest label's: a label too long for it ellipses here
		// and is still whole in the popup.
		var option = new OptionButton
		{
			FitToLongestItem = false,
			TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
		};
		Apply(option);
		int selected = -1;
		for (int i = 0; i < options.Count; i++)
		{
			option.AddItem(options[i].Label);
			if (options[i].Value == display)
			{
				selected = i;
			}
		}

		// A value the list does not offer — typed into the file by hand — is shown as itself
		// rather than silently read as the first entry.
		if (selected < 0)
		{
			options.Add(new RuntimeCVarOption(display, display));
			option.AddItem(display);
			selected = options.Count - 1;
		}

		option.Selected = selected;
		option.ItemSelected += index => Commit(definition, options[(int)index].Value);
		return option;
	}

	private Control BuildHdrRowEditor(RuntimeCVarDefinition definition, string current)
	{
		var container = new HBoxContainer();
		container.AddThemeConstantOverride("separation", 6);

		bool isEnabled = RuntimeConfig.NormalizeBoolValue(current) == "true";

		var check = new CheckBox
		{
			ButtonPressed = isEnabled,
		};
		Apply(check);

		var settingsBtn = new Button
		{
			Text = "Settings...",
			Disabled = !isEnabled,
		};
		Apply(settingsBtn);

		check.Toggled += pressed =>
		{
			settingsBtn.Disabled = !pressed;
			Commit(definition, pressed ? "true" : "false");
		};

		settingsBtn.Pressed += () =>
		{
			var dialog = new HdrSettingsDialog(_model, _style, _font, _fontSize, () =>
			{
				_statusLabel.Text = _model.StatusMessage;
			});
			Node top = (Node?)GetTree()?.Root.GetNodeOrNull("Launcher") ?? (Node?)GetWindow() ?? this;
			top.AddChild(dialog);
		};

		container.AddChild(check);
		container.AddChild(settingsBtn);
		return container;
	}

	private void Commit(RuntimeCVarDefinition definition, string value)
	{
		_model.Values[definition.Name] = value;
		_model.Save();
		_statusLabel.Text = _model.StatusMessage;

		// The resolution list is derived from the aspect AND the monitor — it is filtered to
		// what that screen can show — so either one moving has to redraw the row below it.
		// Otherwise it keeps offering the previous shape's sizes, or sizes the newly chosen
		// monitor cannot display.
		if (definition.Name is "display_aspect" or "monitor")
		{
			Rebuild();
		}
		else if (definition.Name == "hdr_output" && value == "true" && System.OperatingSystem.IsWindows())
		{
			if (_model.Values.TryGetValue("rendering_driver", out string? currentDriver) && currentDriver != "d3d12")
			{
				_model.Values["rendering_driver"] = "d3d12";
				_model.Save();
				Rebuild();
			}
		}
	}
}
