using System;
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
	private Label? _graphicsPresetSummary;
	private List<RuntimeCVarOption> _graphicsPresetOptions = new();

	/// <summary>
	/// The graphics preset as a dropdown on the preset card above the Display and Graphics tabs, in the
	/// desktop's Performance Preset style: the name, the dropdown, and a line saying what the preset in use
	/// gives. Picking one writes its settings, and Custom is shown, not offered, once a setting it decides
	/// no longer matches.
	/// </summary>
	/// <remarks>The card hands over a grid; it becomes one column of the row and its line. No default
	/// column: the tabbed panel has no header for one, and a lone "High" beside the dropdown read as the
	/// far end of a Low-High slider.</remarks>
	private void AddGraphicsPresetRow(GridContainer grid, RuntimeCVarDefinition definition)
	{
		grid.Columns = 1;
		var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		row.AddThemeConstantOverride("separation", 8);
		grid.AddChild(row);

		Label name = MakeLabel(DesktopSettingMetadata.Label(definition));
		name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		name.VerticalAlignment = VerticalAlignment.Center;
		name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
		name.TooltipText = RuntimeSettingsModel.FormatDescription(definition);
		row.AddChild(name);

		_graphicsPresetOptions = RuntimeSettingsModel.OptionsFor(definition, _model.Values);
		var option = new OptionButton
		{
			FitToLongestItem = false,
			TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
			CustomMinimumSize = new Vector2(Math.Max(LauncherScale.Px(140), ControlColumnWidth - 10), 0),
		};
		StyleDropdown(option);
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
		row.AddChild(option);
		_graphicsPresetOption = option;

		Label summary = MakeLabel(string.Empty);
		summary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		summary.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		summary.AddThemeFontSizeOverride("font_size", Math.Max(11, _fontSize - 3));
		summary.AddThemeColorOverride("font_color", LauncherTokens.SepiaInk);
		grid.AddChild(summary);
		_graphicsPresetSummary = summary;
		ShowGraphicsPreset();
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
		if (_graphicsPresetSummary is { } summary && IsInstanceValid(summary))
		{
			summary.Text = Refii.AndroidRuntime.AndroidSettingsLayout.Summary(current);
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
