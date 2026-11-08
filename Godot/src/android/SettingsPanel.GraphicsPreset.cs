using System.Collections.Generic;
using Godot;
using Refii.Launcher.Core;

namespace Refii.Launcher;

/// <summary>The phone's graphics preset row (<see cref="GraphicsPresets"/>).</summary>
public partial class SettingsPanel
{
	/// <summary>Whether the header names the settings file: a desktop player can open it; on a phone it is a
	/// private path nobody can reach.</summary>
	private static bool ShowsConfigPath => !GraphicsPresets.Offered;

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
			if (GraphicsPresets.PickWarning(chosen) is { } warning && chosen != _model.CurrentGraphicsPreset())
			{
				ConfirmGraphicsPreset(chosen, warning);
				return;
			}
			ApplyGraphicsPreset(chosen);
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

	/// <summary>Writes <paramref name="preset"/>'s settings and shows the rows they changed.</summary>
	private void ApplyGraphicsPreset(string preset)
	{
		_model.ApplyGraphicsPreset(preset);
		_statusLabel.Text = _model.StatusMessage;
		Rebuild();
	}

	/// <summary>Shows <paramref name="warning"/> large and red: Use applies <paramref name="preset"/>, Cancel puts the row back on the preset in use.</summary>
	private void ConfirmGraphicsPreset(string preset, string warning)
	{
		var dialog = new ConfirmationDialog
		{
			Title = "WARNING",
			DialogText = warning,
			OkButtonText = "Use anyway",
			CancelButtonText = "Cancel",
			DialogAutowrap = true,
			MinSize = new Vector2I(LauncherScale.Px(560), 0),
		};
		dialog.GetLabel().AddThemeFontSizeOverride("font_size", LauncherScale.Px(22));
		dialog.GetLabel().AddThemeColorOverride("font_color", new Color(1f, 0.55f, 0.5f));
		dialog.Confirmed += () => ApplyGraphicsPreset(preset);
		dialog.Canceled += ShowGraphicsPreset;
		dialog.VisibilityChanged += () =>
		{
			if (!dialog.Visible)
			{
				dialog.QueueFree();
			}
		};
		Node top = (Node?)GetTree()?.Root.GetNodeOrNull("Launcher") ?? (Node?)GetWindow() ?? this;
		top.AddChild(dialog);
		dialog.PopupCentered();
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
