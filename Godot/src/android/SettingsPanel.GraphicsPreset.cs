using System.Collections.Generic;
using Godot;
using Refii.Launcher.Core;

namespace Refii.Launcher;

/// <summary>The phone's graphics preset row (<see cref="GraphicsPresets"/>).</summary>
public partial class SettingsPanel
{
	private OptionButton? _graphicsPresetOption;
	private List<RuntimeCVarOption> _graphicsPresetOptions = new();

	/// <summary>
	/// The graphics preset as a dropdown: picking one writes its settings, and Custom is shown,
	/// not offered, once a setting it decides no longer matches.
	/// </summary>
	private void AddGraphicsPresetRow(GridContainer grid, RuntimeCVarDefinition definition)
	{
		Label name = MakeLabel(RuntimeSettingsModel.FormatDisplayName(definition));
		name.CustomMinimumSize = new Vector2(LauncherScale.Px(BaseNameColumnWidth), 0);
		name.SizeFlagsHorizontal = SizeFlags.Fill;
		name.VerticalAlignment = VerticalAlignment.Center;
		name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
		name.TooltipText = RuntimeSettingsModel.FormatDescription(definition);
		grid.AddChild(name);

		_graphicsPresetOptions = RuntimeSettingsModel.OptionsFor(definition, _model.Values);
		var option = new OptionButton
		{
			FitToLongestItem = false,
			TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(LauncherScale.Px(BaseEditorColumnWidth), 0),
		};
		Apply(option);
		foreach (RuntimeCVarOption entry in _graphicsPresetOptions)
		{
			option.AddItem(entry.Label);
		}

		option.ItemSelected += index =>
		{
			string chosen = _graphicsPresetOptions[(int)index].Value;
			if (chosen == GraphicsPresets.Custom)
			{
				return;
			}
			_model.ApplyGraphicsPreset(chosen);
			_statusLabel.Text = _model.StatusMessage;
			Rebuild();
		};
		grid.AddChild(option);
		_graphicsPresetOption = option;
		ShowGraphicsPreset();

		string defaultText = _graphicsPresetOptions.Find(o => o.Value == definition.DefaultValue)?.Label
			?? definition.DefaultValue;
		Label defaultLabel = MakeLabel(defaultText);
		defaultLabel.CustomMinimumSize = new Vector2(LauncherScale.Px(BaseDefaultColumnWidth), 0);
		defaultLabel.SizeFlagsHorizontal = SizeFlags.Fill;
		defaultLabel.VerticalAlignment = VerticalAlignment.Center;
		defaultLabel.AddThemeColorOverride("font_color", _style.TextDisabled);
		grid.AddChild(defaultLabel);
	}

	/// <summary>Selects the preset the values match, without rebuilding the rows being edited.</summary>
	private void ShowGraphicsPreset()
	{
		if (_graphicsPresetOption is not { } option || !IsInstanceValid(option))
		{
			return;
		}

		string current = _model.CurrentGraphicsPreset();
		int index = _graphicsPresetOptions.FindIndex(o => o.Value == current);
		int custom = _graphicsPresetOptions.FindIndex(o => o.Value == GraphicsPresets.Custom);
		if (custom >= 0)
		{
			option.SetItemDisabled(custom, current != GraphicsPresets.Custom);
		}
		if (index >= 0)
		{
			option.Selected = index;
		}
	}

	/// <summary>Shows the preset the values match once a setting it decides was committed.</summary>
	private void RefreshGraphicsPreset(string changedSetting)
	{
		if (_model.RefreshGraphicsPreset(changedSetting))
		{
			ShowGraphicsPreset();
		}
	}
}
