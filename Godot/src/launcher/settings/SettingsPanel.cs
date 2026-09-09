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

	private const int NameColumnWidth = 145;
	private const int DefaultColumnWidth = 75;

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
		listBox.ContentMarginLeft = 6;
		listBox.ContentMarginRight = 6;
		listBox.ContentMarginTop = 4;
		listBox.ContentMarginBottom = 4;
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
		bool disabled = _model.IsDisabledVideoModeSetting(definition);

		Label name = MakeLabel(RuntimeSettingsModel.FormatDisplayName(definition));
		name.CustomMinimumSize = new Vector2(NameColumnWidth, 0);
		name.SizeFlagsHorizontal = SizeFlags.Fill;
		name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
		name.TooltipText = definition.Description;
		if (disabled)
		{
			name.AddThemeColorOverride("font_color", _style.TextDisabled);
		}

		grid.AddChild(name);

		Control editor = BuildEditor(definition, disabled);
		editor.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		editor.CustomMinimumSize = new Vector2(80, 0);
		grid.AddChild(editor);

		Label defaultLabel = MakeLabel(definition.DefaultValue);
		defaultLabel.CustomMinimumSize = new Vector2(DefaultColumnWidth, 0);
		defaultLabel.SizeFlagsHorizontal = SizeFlags.Fill;
		defaultLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
		defaultLabel.TooltipText = definition.DefaultValue;
		defaultLabel.AddThemeColorOverride("font_color", _style.TextDisabled);
		grid.AddChild(defaultLabel);
	}

	private Control BuildEditor(RuntimeCVarDefinition definition, bool disabled)
	{
		string current = _model.Values.TryGetValue(definition.Name, out string? value)
			? value
			: definition.DefaultValue;

		switch (definition.Type)
		{
			case RuntimeCVarType.Bool:
			{
				var check = new CheckBox
				{
					ButtonPressed = RuntimeConfig.NormalizeBoolValue(current) == "true",
					Disabled = disabled,
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
					Editable = !disabled,
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
					Editable = !disabled,
				};
				Apply(spin);
				spin.ValueChanged += v =>
					Commit(definition, v.ToString("0.###", CultureInfo.InvariantCulture));
				return spin;
			}

			default:
			{
				List<string> options = RuntimeSettingsModel.OptionsFor(definition, _model.Values);
				if (options.Count == 0)
				{
					var edit = new LineEdit { Text = current, Editable = !disabled };
					Apply(edit);
					edit.TextSubmitted += text => Commit(definition, text);
					edit.FocusExited += () => Commit(definition, edit.Text);
					return edit;
				}

				string display = current.Length == 0 ? definition.DefaultValue : current;
				if (definition.Name == "renderer_backend" && display == "native")
				{
					display = "xenia";
				}

				if (definition.Name == "resolution" && display.Length == 0)
				{
					display = "custom";
				}

				var option = new OptionButton { Disabled = disabled };
				Apply(option);
				for (int i = 0; i < options.Count; i++)
				{
					option.AddItem(options[i]);
					if (options[i] == display)
					{
						option.Selected = i;
					}
				}

				option.ItemSelected += index => Commit(definition, options[(int)index]);
				return option;
			}
		}
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
		if (definition.Name is "renderer_backend" or "renderer_plume_api"
			or "display_aspect" or "monitor")
		{
			Rebuild();
		}
	}
}
