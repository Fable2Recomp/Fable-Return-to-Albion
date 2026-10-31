using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Refii.Game;

namespace Refii.AndroidRuntime;

/// <summary>
/// The Options panel: photo mode, the frame cap, the graphics preset, vibration, the F5/F6 diagnostics and Export as
/// touch buttons, folded behind the touch controls' Options button so the corner they stood in is the
/// HUD's again. Hidden until Options is tapped. Export unfolds into Logs and saves, and Saves; a
/// save is imported from the launcher only, never while the game runs (AndroidSaveTransfer).
/// </summary>
public partial class AndroidDiagnosticButtons : CanvasLayer
{
    private AndroidDiagnosticActions? _actions;
    private Button? _performance;
    private Button? _debug;
    private Button? _frameCap;
    private Button? _preset;
    private Button? _vibration;
    private Button? _photo;
    private Button? _export;
    private Button? _exportSaves;
    private Button? _exportBoth;
    private VBoxContainer? _panel;
    private Rect2 _options;

    /// <summary>The live refii.toml; empty until the host has prepared it.</summary>
    public Func<string>? ConfigPath { get; set; }

    /// <summary>Runs after a tap writes a setting, so the host applies it now rather than at its next poll.</summary>
    public Action? SettingsWritten { get; set; }

    /// <summary>Builds the panel, hidden; <see cref="Toggle"/> opens it under Options.</summary>
    public override void _Ready()
    {
        Layer = 150;
        ProcessMode = ProcessModeEnum.Always;
        Visible = false;
        _actions = new AndroidDiagnosticActions(
            GetParent().GetNode<GamePerfOverlay>("GamePerfOverlay").Toggle,
            GetParent().GetNode<GameDevMenu>("GameDevMenu").Toggle);
        _panel = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _panel.AddThemeConstantOverride("separation", 10);
        _frameCap = CreateButton(AndroidQuickSettings.FrameCapLabel(0));
        _preset = CreateButton(AndroidQuickSettings.PresetLabel(Refii.Launcher.Core.GraphicsPresets.Custom));
        _vibration = CreateButton(AndroidQuickSettings.VibrationLabel(true));
        _performance = CreateButton("Stats (F5)");
        _debug = CreateButton("Debug (F6)");
        _photo = CreateButton("Photo Mode");
        _export = CreateButton(ExportLabel);
        _exportBoth = CreateButton(ExportChoiceLabel(AndroidLogBundle.Contents.LogsAndSaves));
        _exportSaves = CreateButton(ExportChoiceLabel(AndroidLogBundle.Contents.Saves));
        // The result names the zip, wider than the panel: wrap it rather than widen the panel off screen.
        _exportBoth.AutowrapMode = _exportSaves.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _frameCap.Pressed += StepFrameCap;
        _preset.Pressed += StepPreset;
        _vibration.Pressed += ToggleVibration;
        _performance.Pressed += _actions.TogglePerformance;
        _debug.Pressed += _actions.ToggleDebug;
        _photo.Pressed += OpenPhotoMode;
        _export.Pressed += ToggleExportChoices;
        _exportSaves.Pressed += ExportSaves;
        _exportBoth.Pressed += ExportBoth;
        _panel.AddChild(_photo);
        _panel.AddChild(_frameCap);
        _panel.AddChild(_preset);
        _panel.AddChild(_vibration);
        _panel.AddChild(_performance);
        _panel.AddChild(_debug);
        _panel.AddChild(_export);
        _panel.AddChild(_exportBoth);
        _panel.AddChild(_exportSaves);
        ShowExportChoices(false);
        AddChild(_panel);
    }

    /// <summary>Opens the panel above <paramref name="options"/> (left edges aligned), or closes it.</summary>
    /// <param name="options">The Options button's rectangle, in the same canvas units.</param>
    public void Toggle(Rect2 options)
    {
        Visible = !Visible;
        _options = options;
        if (Visible)
        {
            ShowExportChoices(false);
            ShowSettings();
        }
        Place();
    }

    /// <summary>Stands the panel above Options with its bottom 10 units clear, as tall as its shown buttons.</summary>
    private void Place()
    {
        if (!Visible || _panel is not { } panel) return;
        panel.Size = Vector2.Zero;
        Vector2 size = panel.GetCombinedMinimumSize();
        panel.Position = new Vector2(_options.Position.X, _options.Position.Y - size.Y - 10f);
    }

    /// <summary>The buttons' screen rectangles: the touch controls leave a finger landing there to the buttons.</summary>
    public IEnumerable<Rect2> Rects()
    {
        if (!Visible) yield break;
        if (_frameCap is { } frameCap) yield return frameCap.GetGlobalRect();
        if (_preset is { } preset) yield return preset.GetGlobalRect();
        if (_vibration is { } vibration) yield return vibration.GetGlobalRect();
        if (_performance is { } performance) yield return performance.GetGlobalRect();
        if (_debug is { } debug) yield return debug.GetGlobalRect();
        if (_photo is { } photo) yield return photo.GetGlobalRect();
        foreach (Button? button in new[] { _export, _exportBoth, _exportSaves })
            if (button is { Visible: true }) yield return button.GetGlobalRect();
    }

    private const string ExportLabel = "Export...";

    /// <summary>The label of the choice that exports <paramref name="contents"/>.</summary>
    private static string ExportChoiceLabel(AndroidLogBundle.Contents contents) =>
        "  " + AndroidLogBundle.Noun(contents);

    /// <summary>Shows or folds away the export choices under Export: logs and saves for a bug report, or saves alone for a backup.</summary>
    private void ShowExportChoices(bool shown)
    {
        foreach (Button? button in new[] { _exportBoth, _exportSaves })
            if (button != null) button.Visible = shown;
        Place();
    }

    private void ToggleExportChoices() => ShowExportChoices(_exportBoth is { Visible: false });

    private void ExportSaves() => Export(_exportSaves, AndroidLogBundle.Contents.Saves);

    private void ExportBoth() => Export(_exportBoth, AndroidLogBundle.Contents.LogsAndSaves);

    /// <summary>Writes the export zip to the shared Download folder; the chosen button states where, or why not, until the panel next opens.</summary>
    private async void Export(Button? choice, AndroidLogBundle.Contents contents)
    {
        if (choice is not { Disabled: false } button) return;
        Button?[] choices = { _exportBoth, _exportSaves };
        foreach (Button? other in choices) if (other != null) other.Disabled = true;
        button.Text = "Exporting " + AndroidLogBundle.Noun(contents).ToLowerInvariant() + "...";
        string result = await AndroidLogBundle.ExportAsync(contents);
        if (!IsInstanceValid(button)) return;
        foreach (Button? other in choices) if (other != null && IsInstanceValid(other)) other.Disabled = false;
        button.Text = result;
        Place();
    }

    /// <summary>Opens photo mode (L3 + R3 on a pad, which a touchscreen cannot press), closing the panel.</summary>
    private void OpenPhotoMode()
    {
        Visible = false;
        GamePhotoMode.RequestOpen();
    }

    /// <summary>Steps the frame cap: 30, 60, uncapped.</summary>
    private void StepFrameCap() => Write(config => AndroidQuickSettings.SetFrameCap(
        config, AndroidQuickSettings.NextFrameCap(AndroidQuickSettings.FrameCap(config))));

    /// <summary>Steps the graphics preset between Performance and Quality, applying it to the running game.</summary>
    private void StepPreset() => Write(config => AndroidQuickSettings.ApplyPreset(
        config, AndroidQuickSettings.NextPreset(AndroidQuickSettings.Preset(config)),
        (name, value) => Refii.Core.Engine_RefiiConfig.TrySetGodotCvar(name, value)));

    /// <summary>Turns the game's rumble on the phone (or a controller) off or on, from the next frame.</summary>
    private void ToggleVibration() => Write(config =>
    {
        AndroidQuickSettings.SetVibration(config, !AndroidQuickSettings.Vibration(config));
        GameInput.RereadControlSettings();
    });

    /// <summary>Runs one setting change against the live file, then shows the result.</summary>
    private void Write(Action<string> change)
    {
        if (ConfigPath?.Invoke() is { Length: > 0 } config)
        {
            try
            {
                change(config);
                SettingsWritten?.Invoke();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                GD.PrintErr("[android] options: " + error.Message);
            }
        }
        ShowSettings();
    }

    /// <summary>Labels the cap, preset and vibration buttons from the file, as they now stand, and photo mode by whether it would open.</summary>
    private void ShowSettings()
    {
        if (_exportSaves is { Disabled: false } saves) saves.Text = ExportChoiceLabel(AndroidLogBundle.Contents.Saves);
        if (_exportBoth is { Disabled: false } both) both.Text = ExportChoiceLabel(AndroidLogBundle.Contents.LogsAndSaves);
        if (_photo is not null)
        {
            string? refusal = GamePhotoMode.OpenRefusal();
            _photo.Disabled = refusal is not null;
            _photo.Text = refusal is null ? "Photo Mode" : $"Photo Mode ({refusal})";
        }

        if (ConfigPath?.Invoke() is not { Length: > 0 } config || _frameCap is null || _preset is null || _vibration is null) return;
        try
        {
            _frameCap.Text = AndroidQuickSettings.FrameCapLabel(AndroidQuickSettings.FrameCap(config));
            _preset.Text = AndroidQuickSettings.PresetLabel(AndroidQuickSettings.Preset(config));
            _vibration.Text = AndroidQuickSettings.VibrationLabel(AndroidQuickSettings.Vibration(config));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            GD.PrintErr("[android] options: " + error.Message);
        }
    }

    /// <summary>Creates a finger-sized button that never takes keyboard or controller focus.</summary>
    /// <param name="text">The keyboard equivalent and menu name.</param>
    /// <returns>A button that consumes input only inside its own bounds.</returns>
    private static Button CreateButton(string text)
    {
        var button = new Button
        {
            Text = text, FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(250, 56),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        button.AddThemeFontSizeOverride("font_size", 20);
        return button;
    }

    /// <summary>Disconnects taps and releases bindings when leaving the game scene.</summary>
    public override void _ExitTree()
    {
        if (_actions is null) return;
        if (_frameCap is not null) _frameCap.Pressed -= StepFrameCap;
        if (_preset is not null) _preset.Pressed -= StepPreset;
        if (_vibration is not null) _vibration.Pressed -= ToggleVibration;
        if (_performance is not null) _performance.Pressed -= _actions.TogglePerformance;
        if (_debug is not null) _debug.Pressed -= _actions.ToggleDebug;
        if (_photo is not null) _photo.Pressed -= OpenPhotoMode;
        if (_export is not null) _export.Pressed -= ToggleExportChoices;
        if (_exportSaves is not null) _exportSaves.Pressed -= ExportSaves;
        if (_exportBoth is not null) _exportBoth.Pressed -= ExportBoth;
        _actions.Dispose();
        _actions = null;
    }
}
