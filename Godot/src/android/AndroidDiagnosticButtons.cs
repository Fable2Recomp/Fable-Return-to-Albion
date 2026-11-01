using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Refii.Game;

namespace Refii.AndroidRuntime;

/// <summary>
/// The Options panel: photo mode, the frame cap, vibration, the F5/F6 diagnostics and Export as touch
/// buttons, and beside them the Graphics column: the preset and the settings that change the picture
/// (<see cref="AndroidGraphicsOptions"/>), each stepped with - and +. Folded behind the touch controls'
/// Options button so the corner they stood in is the HUD's again; hidden until Options is tapped. Both
/// columns are see-through, so a change shows in the world behind them as it applies, and stepping the
/// preset moves every row it sets. Export unfolds into Logs and saves, and Saves; a save is imported
/// from the launcher only, never while the game runs (AndroidSaveTransfer).
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
    private Button? _remapButton;
    private Button? _editButton;
    private AndroidPadRemapMenu? _remap;
    private AndroidTouchEditor? _editor;
    private bool _savedLoaded;
    private Button? _exportSaves;
    private Button? _exportBoth;
    private HBoxContainer? _panel;
    private PanelContainer? _graphics;
    private ScrollContainer? _graphicsScroll;
    private VBoxContainer? _graphicsList;
    private PanelContainer? _about;
    private PanelContainer? _rebuild;
    private Label? _rebuildText;
    private Button? _rebuildNow;
    private Label? _aboutText;
    private string? _aboutKey;
    private Control? _overOptions;
    private readonly List<GraphicsRow> _rows = new();
    private Rect2 _options;

    /// <summary>The live refii.toml; empty until the host has prepared it.</summary>
    public Func<string>? ConfigPath { get; set; }

    /// <summary>Runs after a tap writes a setting, so the host applies it now rather than at its next poll.</summary>
    public Action? SettingsWritten { get; set; }

    /// <summary>The title's touch controls, for the touch editor; null with none.</summary>
    public IAndroidTouchControls? Touch { get; set; }

    /// <summary>Builds the panel, hidden; <see cref="Toggle"/> opens it under Options.</summary>
    public override void _Ready()
    {
        Layer = 150;
        ProcessMode = ProcessModeEnum.Always;
        Visible = false;
        _actions = new AndroidDiagnosticActions(
            GetParent().GetNode<GamePerfOverlay>("GamePerfOverlay").Toggle,
            GetParent().GetNode<GameDevMenu>("GameDevMenu").Toggle);
        _panel = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _panel.AddThemeConstantOverride("separation", 10);
        var column = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ShrinkEnd,
        };
        column.AddThemeConstantOverride("separation", 10);
        _frameCap = CreateButton(AndroidQuickSettings.FrameCapLabel(0));
        _preset = CreateButton(AndroidQuickSettings.PresetLabel(Refii.Launcher.Core.GraphicsPresets.Custom));
        _vibration = CreateButton(AndroidQuickSettings.VibrationLabel(true));
        _performance = CreateButton("Stats (F5)");
        _debug = CreateButton("Debug (F6)");
        _photo = CreateButton("Photo Mode");
        _remapButton = CreateButton("Controller buttons...");
        _editButton = CreateButton("Edit touch controls...");
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
        _remapButton.Pressed += OpenRemap;
        _editButton.Pressed += OpenEditor;
        _export.Pressed += ToggleExportChoices;
        _exportSaves.Pressed += ExportSaves;
        _exportBoth.Pressed += ExportBoth;
        column.AddChild(_photo);
        column.AddChild(_frameCap);
        column.AddChild(_vibration);
        column.AddChild(_performance);
        column.AddChild(_debug);
        column.AddChild(_remapButton);
        column.AddChild(_editButton);
        column.AddChild(_export);
        column.AddChild(_exportBoth);
        column.AddChild(_exportSaves);
        // Room for the Options button under the buttons; the Graphics column beside them, right of
        // the View pill, reaches down to Options' bottom edge.
        column.AddChild(_overOptions = new Control { MouseFilter = Control.MouseFilterEnum.Ignore });
        _panel.AddChild(column);
        _panel.AddChild(_graphics = CreateGraphics(_preset));
        _panel.AddChild(_about = CreateAbout());
        _panel.AddChild(_rebuild = CreateRebuild());
        ShowExportChoices(false);
        AddChild(_panel);
        // The two menus Options opens stand on their own layers, beside this one, so they show with
        // the panel closed.
        _remap = new AndroidPadRemapMenu { Name = "AndroidPadRemapMenu", ConfigPath = () => ConfigPath?.Invoke() ?? "" };
        _editor = new AndroidTouchEditor
        {
            Name = "AndroidTouchEditor", Controls = () => Touch?.LaidOut ?? Array.Empty<TouchControl>(),
            Screen = () => Touch?.CurrentContext ?? TouchContext.World,
            ConfigPath = () => ConfigPath?.Invoke() ?? "", OpacityWritten = () => Touch?.RereadSettingsSoon(),
        };
        GetParent().CallDeferred(Node.MethodName.AddChild, _remap);
        GetParent().CallDeferred(Node.MethodName.AddChild, _editor);
    }

    /// <summary>Once the host has prepared refii.toml, reads the controller map and touch layouts saved beside it.</summary>
    public override void _Process(double delta)
    {
        if (_savedLoaded || ConfigPath?.Invoke() is not { Length: > 0 } config) return;
        _savedLoaded = true;
        string folder = Path.GetDirectoryName(config) ?? "";
        AndroidPadRemap.Load(Path.Combine(folder, AndroidPadRemap.FileName));
        AndroidTouchCustomLayout.Load(Path.Combine(folder, AndroidTouchCustomLayout.FileName));
    }

    /// <summary>
    /// The back gesture's first stop: closes the touch editor or the remap menu, else the panel. A way out
    /// that never depends on a touch reaching a button.
    /// </summary>
    /// <returns>Whether something closed, so back goes no further.</returns>
    public bool CloseTop()
    {
        if (_editor is { Visible: true } editor) editor.Close();
        else if (_remap is { Visible: true } remap) remap.Close();
        else if (Visible) Visible = false;
        else return false;
        return true;
    }

    /// <summary>Opens the controller remap menu, closing the panel.</summary>
    private void OpenRemap()
    {
        Visible = false;
        _remap?.Open();
    }

    /// <summary>Opens the touch control editor, closing the panel.</summary>
    private void OpenEditor()
    {
        Visible = false;
        _editor?.Open();
    }

    /// <summary>One graphics row: its setting, the value's text, and the - and + buttons with their taps.</summary>
    private sealed record GraphicsRow(AndroidGraphicsOption Option, Label Value, Button Less, Button More, Button Info,
        Action StepLess, Action StepMore, Action ShowInfo);

    /// <summary>The Graphics column: the preset button, then one row per setting, its name over its impact.</summary>
    private PanelContainer CreateGraphics(Button preset)
    {
        var box = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, SizeFlagsVertical = Control.SizeFlags.ShrinkEnd };
        box.AddThemeStyleboxOverride("panel", AndroidSeeThrough.Box(0.35f, 14f));
        var rows = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        rows.AddThemeConstantOverride("separation", 6);
        preset.CustomMinimumSize = new Vector2(510, 52);
        rows.AddChild(preset);
        // The settings scroll under the preset once they outgrow the room above Options (Place sizes it):
        // a drag that starts on a row's name scrolls, since the names let the press through to it.
        _graphicsScroll = new ScrollContainer
        {
            MouseFilter = Control.MouseFilterEnum.Pass, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _graphicsList = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _graphicsList.AddThemeConstantOverride("separation", 6);
        _graphicsScroll.AddChild(_graphicsList);
        rows.AddChild(_graphicsScroll);
        foreach (AndroidGraphicsOption option in AndroidGraphicsOptions.All)
        {
            var line = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            line.AddThemeConstantOverride("separation", 8);
            Button less = CreateStepButton("-"), more = CreateStepButton("+");
            Button info = AndroidSeeThrough.Button("?", new Vector2(48, 52), 22);
            var label = new VBoxContainer
            {
                MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                Alignment = BoxContainer.AlignmentMode.Center,
            };
            label.AddThemeConstantOverride("separation", 0);
            var name = new Label { Text = option.Name };
            var impact = new Label { Text = AndroidGraphicsOptions.ImpactLabel(option.Impact) };
            var value = new Label { CustomMinimumSize = new Vector2(96, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            name.AddThemeFontSizeOverride("font_size", 18);
            impact.AddThemeFontSizeOverride("font_size", 13);
            impact.AddThemeColorOverride("font_color", ImpactColor(option.Impact));
            value.AddThemeFontSizeOverride("font_size", 18);
            label.AddChild(name);
            label.AddChild(impact);
            line.AddChild(less);
            line.AddChild(label);
            line.AddChild(value);
            line.AddChild(more);
            line.AddChild(info);
            _graphicsList.AddChild(line);
            var row = new GraphicsRow(option, value, less, more, info,
                () => StepGraphics(option, -1), () => StepGraphics(option, 1), () => ToggleAbout(option));
            less.Pressed += row.StepLess;
            more.Pressed += row.StepMore;
            info.Pressed += row.ShowInfo;
            _rows.Add(row);
        }

        box.AddChild(rows);
        return box;
    }

    /// <summary>The box beside the Graphics column that says what a setting does, hidden until a row's ? is tapped.</summary>
    private PanelContainer CreateAbout()
    {
        PanelContainer about = AndroidSeeThrough.Panel(0.6f);
        about.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        about.Visible = false;
        _aboutText = new Label { CustomMinimumSize = new Vector2(300, 0), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _aboutText.AddThemeFontSizeOverride("font_size", 18);
        about.AddChild(_aboutText);
        return about;
    }

    /// <summary>
    /// The box that stands while the phone's shadowed shaders wait for a loading screen
    /// (Engine_ShaderSpecialisation.RebuildWaiting): it says shadows come on there, and offers the rebuild
    /// now, a pause of seconds (tens of seconds on an older phone with cold caches).
    /// </summary>
    private PanelContainer CreateRebuild()
    {
        PanelContainer box = AndroidSeeThrough.Panel(0.6f);
        box.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        box.Visible = false;
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 8);
        _rebuildText = new Label { CustomMinimumSize = new Vector2(300, 0), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _rebuildText.AddThemeFontSizeOverride("font_size", 18);
        _rebuildNow = AndroidSeeThrough.Button("Rebuild now", new Vector2(300, 52), 20);
        _rebuildNow.Pressed += RebuildNow;
        body.AddChild(_rebuildText);
        body.AddChild(_rebuildNow);
        box.AddChild(body);
        return box;
    }

    /// <summary>Says the game will pause, lets that frame draw, then rebuilds the shadowed shaders.</summary>
    private async void RebuildNow()
    {
        if (_rebuildText is null || _rebuildNow is null) return;
        _rebuildText.Text = "Rebuilding the shaders with shadows. The game pauses for a moment (up to a minute on an older phone).";
        _rebuildNow.Disabled = true;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Refii.Models.Engine_ShaderSpecialisation.RebuildNow();
        if (!IsInstanceValid(this)) return;
        _rebuildNow.Disabled = false;
        ShowSettings();
    }

    /// <summary>Shows what <paramref name="option"/> does beside the column, or hides it when it is already shown.</summary>
    private void ToggleAbout(AndroidGraphicsOption option)
    {
        if (_about is null || _aboutText is null) return;
        _aboutKey = _aboutKey == option.Key ? null : option.Key;
        _about.Visible = _aboutKey is not null;
        _aboutText.Text = $"{option.Name}\n{option.About}\n{AndroidGraphicsOptions.ImpactLabel(option.Impact)}";
        Place();
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
            _aboutKey = null;
            if (_about is not null) _about.Visible = false;
        }
        Place();
    }

    /// <summary>The space the Graphics column leaves above it, in canvas units.</summary>
    private const float TopMargin = 8f;

    /// <summary>
    /// Stands the buttons above Options with their bottom 10 units clear, and the Graphics column beside
    /// them down to Options' bottom edge, each as tall as what it shows; the column's settings scroll when
    /// they would rise past <see cref="TopMargin"/>.
    /// </summary>
    private void Place()
    {
        if (!Visible || _panel is not { } panel) return;
        if (_overOptions is { } room) room.CustomMinimumSize = new Vector2(0, _options.Size.Y);
        if (_graphics is { } graphics && _graphicsScroll is { } scroll && _graphicsList is { } list)
        {
            // As tall as its rows, or as the room above Options' bottom edge allows with a margin.
            float around = graphics.GetCombinedMinimumSize().Y - scroll.CustomMinimumSize.Y;
            float rows = list.GetCombinedMinimumSize().Y;
            scroll.CustomMinimumSize = new Vector2(0, Math.Max(0f, Math.Min(rows, _options.End.Y - TopMargin - around)));
        }
        panel.Size = Vector2.Zero;
        Vector2 size = panel.GetCombinedMinimumSize();
        panel.Position = new Vector2(_options.Position.X, _options.End.Y - size.Y);
    }

    /// <summary>The buttons' screen rectangles: the touch controls leave a finger landing there to the buttons.</summary>
    public IEnumerable<Rect2> Rects()
    {
        foreach (Rect2 rect in _remap?.Rects() ?? Array.Empty<Rect2>()) yield return rect;
        foreach (Rect2 rect in _editor?.Rects() ?? Array.Empty<Rect2>()) yield return rect;
        if (!Visible) yield break;
        if (_frameCap is { } frameCap) yield return frameCap.GetGlobalRect();
        // The whole Graphics column, so a finger between its buttons doesn't turn the camera.
        if (_graphics is { } graphics) yield return graphics.GetGlobalRect();
        if (_about is { Visible: true } about) yield return about.GetGlobalRect();
        if (_rebuild is { Visible: true } rebuild) yield return rebuild.GetGlobalRect();
        if (_vibration is { } vibration) yield return vibration.GetGlobalRect();
        if (_performance is { } performance) yield return performance.GetGlobalRect();
        if (_debug is { } debug) yield return debug.GetGlobalRect();
        if (_photo is { } photo) yield return photo.GetGlobalRect();
        foreach (Button? button in new[] { _remapButton, _editButton, _export, _exportBoth, _exportSaves })
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

    /// <summary>Steps the graphics preset through Ultra Performance, Performance, Balanced and Quality, applying it to the running game.</summary>
    private void StepPreset() => Write(config => AndroidQuickSettings.ApplyPreset(
        config, AndroidQuickSettings.NextPreset(AndroidQuickSettings.Preset(config)),
        (name, value) => Refii.Core.Engine_RefiiConfig.TrySetGodotCvar(name, value)));

    /// <summary>Steps one graphics setting, applying it to the running game.</summary>
    private void StepGraphics(AndroidGraphicsOption option, int direction) => Write(config =>
        AndroidQuickSettings.SetGraphics(config, option.Key,
            AndroidGraphicsOptions.Step(option, AndroidQuickSettings.Value(config, option.Key), direction),
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

        if (_rebuild is not null && _rebuildText is not null)
        {
            bool waiting = Refii.Models.Engine_ShaderSpecialisation.RebuildWaiting;
            if (waiting && !_rebuild.Visible)
                _rebuildText.Text = "Shadows need a one-time shader rebuild. They come on at the next loading screen.";
            _rebuild.Visible = waiting;
            Place();
        }

        if (ConfigPath?.Invoke() is not { Length: > 0 } config || _frameCap is null || _preset is null || _vibration is null) return;
        try
        {
            _frameCap.Text = AndroidQuickSettings.FrameCapLabel(AndroidQuickSettings.FrameCap(config));
            _preset.Text = AndroidQuickSettings.PresetLabel(AndroidQuickSettings.Preset(config));
            _vibration.Text = AndroidQuickSettings.VibrationLabel(AndroidQuickSettings.Vibration(config));
            foreach (GraphicsRow row in _rows)
            {
                string value = AndroidQuickSettings.Value(config, row.Option.Key);
                row.Value.Text = AndroidGraphicsOptions.Label(row.Option, value);
                row.Less.Disabled = !AndroidGraphicsOptions.CanStep(row.Option, value, -1);
                row.More.Disabled = !AndroidGraphicsOptions.CanStep(row.Option, value, 1);
            }
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
        AndroidSeeThrough.Style(button);
        return button;
    }

    /// <summary>The impact line's colour: red for high, amber for medium, green for low.</summary>
    private static Color ImpactColor(AndroidGraphicsImpact impact) => impact switch
    {
        AndroidGraphicsImpact.High => new Color(1f, 0.55f, 0.5f),
        AndroidGraphicsImpact.Medium => new Color(1f, 0.82f, 0.45f),
        _ => new Color(0.6f, 0.9f, 0.6f),
    };

    /// <summary>A graphics row's - or + button: wide and finger-tall, see-through.</summary>
    private static Button CreateStepButton(string text)
    {
        var button = new Button
        {
            Text = text, FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(76, 52),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        button.AddThemeFontSizeOverride("font_size", 28);
        AndroidSeeThrough.Style(button);
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
        if (_remapButton is not null) _remapButton.Pressed -= OpenRemap;
        if (_editButton is not null) _editButton.Pressed -= OpenEditor;
        if (_rebuildNow is not null) _rebuildNow.Pressed -= RebuildNow;
        if (_export is not null) _export.Pressed -= ToggleExportChoices;
        if (_exportSaves is not null) _exportSaves.Pressed -= ExportSaves;
        if (_exportBoth is not null) _exportBoth.Pressed -= ExportBoth;
        foreach (GraphicsRow row in _rows)
        {
            row.Less.Pressed -= row.StepLess;
            row.More.Pressed -= row.StepMore;
            row.Info.Pressed -= row.ShowInfo;
        }
        _rows.Clear();
        _actions.Dispose();
        _actions = null;
    }
}
