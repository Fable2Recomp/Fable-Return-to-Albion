using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Refii.Game;

namespace Refii.AndroidRuntime;

/// <summary>
/// The Options panel: photo mode, Graphics settings, vibration, the diagnostics (the desktop's F5 and F6) and Export as
/// touch buttons, and beside them, once Graphics settings is tapped, the Graphics column: the preset, the frame limit
/// and the settings that change the picture (<see cref="AndroidGraphicsOptions"/>), each stepped with - and +. Folded
/// behind the touch controls' Options button so the corner they stood in is the HUD's again; hidden until Options is
/// tapped, and the Graphics column with it until asked for, so Options opens on the buttons alone. Both
/// columns are see-through, so a change shows in the world behind them as it applies, and stepping the
/// preset moves every row it sets. Export unfolds into Logs and saves, and Saves; a save is imported
/// from the launcher only, never while the game runs (AndroidSaveTransfer).
/// </summary>
public partial class AndroidDiagnosticButtons : CanvasLayer
{
    private AndroidDiagnosticActions? _actions;
    private Button? _performance;
    private Button? _debug;
    private Label? _frameCap;
    private readonly List<FrameCapChoice> _frameCapButtons = new();
    private Button? _graphicsButton;
    private Button? _fpsCounterButton;
    private Button? _skipLauncherButton;
    private AndroidFpsCounter? _fpsCounter;
    private Button? _preset;
    private GridContainer? _presetChoices;
    private readonly List<PresetChoice> _presetButtons = new();
    private PanelContainer? _presetWarning;
    private Label? _presetWarningText;
    private Button? _presetWarningUse;
    private Button? _presetWarningCancel;
    private string? _warnedPreset;
    private Button? _vibration;
    private Button? _photo;
    private Button? _export;
    private Button? _diagnostics;
    private ScrollContainer? _buttonsScroll;
    private VBoxContainer? _buttons;
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
    private Button? _rebuildLater;
    private bool _rebuildWaiting;
    private bool _rebuildPutOff;
    private PanelContainer? _restartNote;
    private Label? _restartNoteText;
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
        _preset = CreateButton(AndroidQuickSettings.PresetLabel(Refii.Launcher.Core.GraphicsPresets.Custom));
        _fpsCounterButton = CreateButton(AndroidQuickSettings.FpsCounterLabel(false));
        _skipLauncherButton = CreateButton(AndroidQuickSettings.SkipLauncherLabel(false));
        _vibration = CreateButton(AndroidQuickSettings.VibrationLabel(AndroidQuickSettings.VibrationStrengthDefault));
        _diagnostics = CreateButton(DiagnosticsLabel);
        _performance = CreateButton("  Performance stats");
        _debug = CreateButton("  Developer tools");
        _photo = CreateButton("Photo Mode");
        _graphicsButton = CreateButton(GraphicsLabel);
        _remapButton = CreateButton("Controller buttons...");
        _editButton = CreateButton("Edit touch controls...");
        _export = CreateButton(ExportLabel);
        _exportBoth = CreateButton(ExportChoiceLabel(AndroidLogBundle.Contents.LogsAndSaves));
        _exportSaves = CreateButton(ExportChoiceLabel(AndroidLogBundle.Contents.Saves));
        // The result names the zip, wider than the panel: wrap it rather than widen the panel off screen.
        _exportBoth.AutowrapMode = _exportSaves.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _fpsCounterButton.Pressed += ToggleFpsCounter;
        _skipLauncherButton.Pressed += ToggleSkipLauncher;
        _preset.Pressed += TogglePresetChoices;
        _vibration.Pressed += StepVibration;
        _performance.Pressed += _actions.TogglePerformance;
        _debug.Pressed += _actions.ToggleDebug;
        _photo.Pressed += OpenPhotoMode;
        _graphicsButton.Pressed += ToggleGraphics;
        _remapButton.Pressed += OpenRemap;
        _editButton.Pressed += OpenEditor;
        _export.Pressed += ToggleExportChoices;
        _diagnostics.Pressed += ToggleDiagnostics;
        _exportSaves.Pressed += ExportSaves;
        _exportBoth.Pressed += ExportBoth;
        // THE BUTTONS SCROLL when they would rise off the top of the screen, as the Graphics column's
        // settings do: ten buttons and Options under them were already taller than a phone's canvas,
        // and Photo Mode stood half off the top; an unfolded Export or Diagnostics adds two more.
        _buttons = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _buttons.AddThemeConstantOverride("separation", 10);
        foreach (Button button in new[] { _photo, _graphicsButton, _fpsCounterButton, _skipLauncherButton, _vibration,
                     _remapButton, _editButton, _diagnostics, _performance, _debug, _export, _exportBoth, _exportSaves })
            _buttons.AddChild(button);
        _buttonsScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        _buttonsScroll.AddChild(_buttons);
        column.AddChild(_buttonsScroll);
        // Room for the Options button under the buttons; the Graphics column beside them, right of
        // the View pill, reaches down to Options' bottom edge.
        column.AddChild(_overOptions = new Control { MouseFilter = Control.MouseFilterEnum.Ignore });
        _panel.AddChild(column);
        _panel.AddChild(_graphics = CreateGraphics(_preset));
        _panel.AddChild(_about = CreateAbout());
        ShowGraphics(false);
        ShowExportChoices(false);
        ShowDiagnostics(false);
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
        GetParent().CallDeferred(Node.MethodName.AddChild, _fpsCounter = new AndroidFpsCounter { Name = "AndroidFpsCounter" });
    }

    /// <summary>Asks about a shadow rebuild as soon as one waits, and once the host has prepared refii.toml, reads the controller map and touch layouts saved beside it.</summary>
    public override void _Process(double delta)
    {
        // A preset or a row that turns shadows on starts the wait when the game next applies it, a frame
        // or more after the tap: the question follows as soon as it does.
        if (Visible && Refii.Models.Engine_ShaderSpecialisation.RebuildWaiting != _rebuildWaiting) ShowSettings();
        if (_savedLoaded || ConfigPath?.Invoke() is not { Length: > 0 } config) return;
        _savedLoaded = true;
        string folder = Path.GetDirectoryName(config) ?? "";
        AndroidPadRemap.Load(Path.Combine(folder, AndroidPadRemap.FileName));
        AndroidTouchCustomLayout.Load(Path.Combine(folder, AndroidTouchCustomLayout.FileName));
        try { _fpsCounter?.SetShown(AndroidQuickSettings.FpsCounter(config)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { GD.PrintErr("[android] options: " + error.Message); }
    }

    /// <summary>
    /// The back gesture's first stop: closes the touch editor or the remap menu, else the Graphics column,
    /// else the panel. A way out that never depends on a touch reaching a button.
    /// </summary>
    /// <returns>Whether something closed, so back goes no further.</returns>
    public bool CloseTop()
    {
        if (_editor is { Visible: true } editor) editor.Close();
        else if (_remap is { Visible: true } remap) remap.Close();
        else if (Visible && _graphics is { Visible: true }) ShowGraphics(false);
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

    /// <summary>One of the presets under the preset button, with its tap.</summary>
    private sealed record PresetChoice(string Preset, Button Button, Action Pick);

    /// <summary>One of the caps in the Frame limit row, with its tap.</summary>
    private sealed record FrameCapChoice(int Cap, Button Button, Action Pick);

    /// <summary>One graphics row: its setting, the value's text, and the - and + buttons with their taps.</summary>
    private sealed record GraphicsRow(AndroidGraphicsOption Option, Label Value, Button Less, Button More, Button Info,
        Action StepLess, Action StepMore, Action ShowInfo);

    /// <summary>The Graphics column: the preset button, the frame limit, then one row per setting, its name over its impact.</summary>
    private PanelContainer CreateGraphics(Button preset)
    {
        var box = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, SizeFlagsVertical = Control.SizeFlags.ShrinkEnd };
        box.AddThemeStyleboxOverride("panel", AndroidSeeThrough.Box(0.35f, 14f));
        var rows = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        rows.AddThemeConstantOverride("separation", 6);
        preset.CustomMinimumSize = new Vector2(510, 52);
        rows.AddChild(preset);
        // The preset button opens the presets to pick one from, rather than stepping through them:
        // each preset applied is a pass over the game's settings, and a pick is one.
        _presetChoices = new GridContainer { Columns = 2, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _presetChoices.AddThemeConstantOverride("h_separation", 6);
        _presetChoices.AddThemeConstantOverride("v_separation", 6);
        foreach (string choice in AndroidQuickSettings.Presets)
        {
            Button button = CreateButton(Refii.Launcher.Core.GraphicsPresets.Label(choice));
            button.CustomMinimumSize = new Vector2(252, 52);
            var picked = new PresetChoice(choice, button, () => PickPreset(choice));
            button.Pressed += picked.Pick;
            _presetButtons.Add(picked);
            _presetChoices.AddChild(button);
        }
        rows.AddChild(_presetChoices);
        rows.AddChild(_presetWarning = CreatePresetWarning());
        rows.AddChild(CreateFrameCaps());
        // The shadow rebuild's question stands under the preset, where the pick that needs it was made.
        rows.AddChild(_rebuild = CreateRebuild());
        // The line that says a change waits for the next start stands just above the settings, under the
        // preset and the frame limit, so the preset's choices stay where the finger left them.
        rows.AddChild(_restartNote = CreateRestartNote());
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

    /// <summary>
    /// The Frame limit row: its name, then a button per cap (uncapped, 20, 30, 40, 60), the one in use standing
    /// pressed as the preset in use does, so a cap is one tap rather than a cycle through the others.
    /// </summary>
    private HBoxContainer CreateFrameCaps()
    {
        var line = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        line.AddThemeConstantOverride("separation", 6);
        _frameCap = new Label
        {
            Text = AndroidQuickSettings.FrameCapLabel(0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _frameCap.AddThemeFontSizeOverride("font_size", 18);
        line.AddChild(_frameCap);
        foreach (int cap in AndroidQuickSettings.FrameCaps)
        {
            Button button = CreateButton(AndroidQuickSettings.FrameCapChoiceLabel(cap));
            button.CustomMinimumSize = new Vector2(70, 52);
            // The infinity sign comes from a fallback font that draws it small beside the numbers.
            button.AddThemeFontSizeOverride("font_size", cap <= 0 ? 36 : 22);
            var picked = new FrameCapChoice(cap, button, () => PickFrameCap(cap));
            button.Pressed += picked.Pick;
            _frameCapButtons.Add(picked);
            line.AddChild(button);
        }
        return line;
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
    /// The warning that stands in place of the preset choices when the one picked has one
    /// (GraphicsPresets.PickWarning): large, near opaque and red-edged so it can't be read past, and
    /// nothing applies until Use is tapped.
    /// </summary>
    private PanelContainer CreatePresetWarning()
    {
        StyleBoxFlat style = AndroidSeeThrough.Box(0.92f, 14f);
        style.BorderColor = ImpactColor(AndroidGraphicsImpact.High);
        style.SetBorderWidthAll(3);
        var box = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        box.AddThemeStyleboxOverride("panel", style);
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 8);
        Label heading = AndroidSeeThrough.Label("WARNING", 34);
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        heading.AddThemeColorOverride("font_color", ImpactColor(AndroidGraphicsImpact.High));
        _presetWarningText = new Label { CustomMinimumSize = new Vector2(480, 0), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _presetWarningText.AddThemeFontSizeOverride("font_size", 22);
        var choices = new HBoxContainer();
        choices.AddThemeConstantOverride("separation", 8);
        _presetWarningCancel = AndroidSeeThrough.Button("Cancel", new Vector2(236, 52), 20);
        _presetWarningUse = AndroidSeeThrough.Button("Use anyway", new Vector2(236, 52), 20);
        _presetWarningCancel.Pressed += CancelPresetWarning;
        _presetWarningUse.Pressed += UseWarnedPreset;
        choices.AddChild(_presetWarningCancel);
        choices.AddChild(_presetWarningUse);
        body.AddChild(heading);
        body.AddChild(_presetWarningText);
        body.AddChild(choices);
        box.AddChild(body);
        return box;
    }

    /// <summary>
    /// The box that stands while the phone's shadowed shaders wait for a loading screen
    /// (Engine_ShaderSpecialisation.RebuildWaiting): it says shadows come on there, and offers the rebuild
    /// now, a pause of seconds (tens of seconds on an older phone with cold caches).
    /// </summary>
    private PanelContainer CreateRebuild()
    {
        PanelContainer box = AndroidSeeThrough.Panel(0.6f);
        box.Visible = false;
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 8);
        _rebuildText = new Label { CustomMinimumSize = new Vector2(480, 0), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _rebuildText.AddThemeFontSizeOverride("font_size", 18);
        var choices = new HBoxContainer();
        choices.AddThemeConstantOverride("separation", 8);
        _rebuildNow = AndroidSeeThrough.Button("Rebuild now", new Vector2(236, 52), 20);
        _rebuildLater = AndroidSeeThrough.Button("Later", new Vector2(236, 52), 20);
        _rebuildNow.Pressed += RebuildNow;
        _rebuildLater.Pressed += PutOffRebuild;
        choices.AddChild(_rebuildNow);
        choices.AddChild(_rebuildLater);
        body.AddChild(_rebuildText);
        body.AddChild(choices);
        box.AddChild(body);
        return box;
    }

    /// <summary>
    /// The quiet line, hidden until a graphics change waits for the next start (AndroidRestartNotes): in
    /// the see-through box the about text and the rebuild question use, plain text and no buttons, so it
    /// never stands in the way. It stays while the panel is open, until a later graphics change replaces
    /// or clears it.
    /// </summary>
    private PanelContainer CreateRestartNote()
    {
        PanelContainer box = AndroidSeeThrough.Panel(0.6f);
        box.MouseFilter = Control.MouseFilterEnum.Ignore;
        box.Visible = false;
        _restartNoteText = new Label
        {
            CustomMinimumSize = new Vector2(480, 0), AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _restartNoteText.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(_restartNoteText);
        return box;
    }

    /// <summary>Shows <paramref name="note"/> under the preset, or puts the line away for null.</summary>
    private void ShowRestartNote(string? note)
    {
        if (_restartNote is null || _restartNoteText is null) return;
        bool was = _restartNote.Visible;
        string before = _restartNoteText.Text;
        _restartNoteText.Text = note ?? "";
        _restartNote.Visible = note is not null;
        if (was == _restartNote.Visible && before == _restartNoteText.Text) return;
        Place();
        // The wrapped text only knows its height once the column has laid it out.
        Callable.From(Place).CallDeferred();
    }

    /// <summary>Leaves the shadow rebuild for the next loading screen and puts the question away.</summary>
    private void PutOffRebuild()
    {
        _rebuildPutOff = true;
        ShowSettings();
    }

    /// <summary>Says the game will pause, lets that frame draw, then rebuilds the shadowed shaders.</summary>
    private async void RebuildNow()
    {
        if (_rebuildText is null || _rebuildNow is null) return;
        _rebuildText.Text = "Rebuilding the shaders with shadows. The game pauses for a moment (up to a minute on an older phone).";
        _rebuildNow.Disabled = true;
        if (_rebuildLater is not null) _rebuildLater.Disabled = true;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Refii.Models.Engine_ShaderSpecialisation.RebuildNow();
        if (!IsInstanceValid(this)) return;
        _rebuildNow.Disabled = false;
        if (_rebuildLater is not null) _rebuildLater.Disabled = false;
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
            ShowDiagnostics(false);
            ShowGraphics(false);
            ShowSettings();
        }
        Place();
    }

    private const string GraphicsLabel = "Graphics settings...";

    /// <summary>
    /// Shows the Graphics column beside the buttons, the presets open at its top, or puts it away with
    /// what it had opened (a setting's about box, a preset's warning).
    /// </summary>
    private void ShowGraphics(bool shown)
    {
        if (_graphics is not null) _graphics.Visible = shown;
        _aboutKey = null;
        if (_about is not null) _about.Visible = false;
        if (_presetChoices is not null) _presetChoices.Visible = shown;
        if (_presetWarning is not null) _presetWarning.Visible = false;
        // The next-start line belongs to this visit's changes: the column put away, or the panel
        // opened again (which puts the column away first), starts without it.
        if (!shown) ShowRestartNote(null);
        Place();
    }

    private void ToggleGraphics()
    {
        ShowGraphics(_graphics is { Visible: false });
        ShowSettings();
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
        if (_buttonsScroll is { } buttonsScroll && _buttons is { } buttons)
        {
            // As tall as the buttons, or as the room above Options allows with a margin; the column's
            // separation stands between the buttons and Options' room.
            float above = _options.Position.Y - TopMargin - 10f;
            float height = Math.Max(0f, Math.Min(buttons.GetCombinedMinimumSize().Y, above));
            buttonsScroll.CustomMinimumSize = new Vector2(buttons.GetCombinedMinimumSize().X, height);
        }
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

    /// <summary>Whether any of the buttons or the menus they open stands, so <see cref="AddRects"/> has a rectangle to add.</summary>
    public bool AnyRects => Visible || _remap is { Visible: true } || _editor is { Visible: true };

    /// <summary>
    /// Adds the buttons' screen rectangles to <paramref name="into"/>: the touch controls leave a finger
    /// landing there to the buttons. Asked every frame and every pointer event, so nothing is built while
    /// nothing stands (<see cref="AnyRects"/>).
    /// </summary>
    public void AddRects(List<Rect2> into)
    {
        if (!AnyRects) return;
        _remap?.AddRects(into);
        _editor?.AddRects(into);
        if (!Visible) return;
        // The whole Graphics column, so a finger between its buttons doesn't turn the camera.
        if (_graphics is { Visible: true } graphics) into.Add(graphics.GetGlobalRect());
        if (_about is { Visible: true } about) into.Add(about.GetGlobalRect());
        if (_rebuild is { } rebuild && rebuild.IsVisibleInTree()) into.Add(rebuild.GetGlobalRect());
        // The buttons' scroll area, not each button: one scrolled out of view must not keep its rectangle.
        if (_buttonsScroll is { } buttonsScroll) into.Add(buttonsScroll.GetGlobalRect());
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

    private void ToggleExportChoices()
    {
        bool open = _exportBoth is { Visible: false };
        if (open) ShowDiagnostics(false);
        ShowExportChoices(open);
        if (open) Reveal(_exportSaves);
    }

    private const string DiagnosticsLabel = "Diagnostics...";

    /// <summary>Shows or folds away the two diagnostic views under Diagnostics: the performance stats and
    /// the developer tools (the desktop's F5 and F6, named for a phone that has no F keys).</summary>
    private void ShowDiagnostics(bool shown)
    {
        foreach (Button? button in new[] { _performance, _debug })
            if (button != null) button.Visible = shown;
        Place();
    }

    private void ToggleDiagnostics()
    {
        bool open = _performance is { Visible: false };
        if (open) ShowExportChoices(false);
        ShowDiagnostics(open);
        if (open) Reveal(_debug);
    }

    /// <summary>Scrolls the buttons, once laid out, so <paramref name="button"/> (the last choice a fold
    /// unfolded) is in view.</summary>
    private void Reveal(Button? button)
    {
        if (button is null || _buttonsScroll is not { } scroll) return;
        Callable.From(() => { if (IsInstanceValid(button) && IsInstanceValid(scroll)) scroll.EnsureControlVisible(button); }).CallDeferred();
    }

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

    /// <summary>Sets the frame cap to <paramref name="cap"/>; 0 is uncapped.</summary>
    private void PickFrameCap(int cap) => WriteGraphics(config =>
    {
        string note = NoteFor(config, new[] { ("max_fps", Math.Max(0, cap).ToString(System.Globalization.CultureInfo.InvariantCulture)) });
        AndroidQuickSettings.SetFrameCap(config, cap);
        return note.Length > 0 ? note : null;
    });

    /// <summary>The line for writing <paramref name="writes"/> over the file as it stands: the one-setting note
    /// when a setting it changes waits for the next start, else empty.</summary>
    private static string NoteFor(string config, IEnumerable<(string Key, string Value)> writes) =>
        AndroidRestartNotes.ChangesNeedRestart(Refii.Launcher.Core.RuntimeConfig.ReadValues(config), writes)
            ? AndroidRestartNotes.SettingNote
            : "";

    /// <summary>Shows or hides the presets under the preset button.</summary>
    private void TogglePresetChoices()
    {
        if (_presetChoices is null) return;
        _presetChoices.Visible = !_presetChoices.Visible;
        if (_presetWarning is not null) _presetWarning.Visible = false;
        ShowSettings();
        Place();
    }

    /// <summary>
    /// Applies <paramref name="preset"/> to the running game, the choices staying open so the rows under them
    /// don't jump up under a finger; or first shows its warning in their place if it has one.
    /// </summary>
    private void PickPreset(string preset)
    {
        if (Refii.Launcher.Core.GraphicsPresets.PickWarning(preset) is { } warning && _presetWarning is not null && _presetWarningText is not null)
        {
            if (_presetChoices is not null) _presetChoices.Visible = false;
            _warnedPreset = preset;
            _presetWarningText.Text = warning;
            _presetWarning.Visible = true;
            Place();
            // The wrapped text only knows its height once the column has laid it out.
            Callable.From(Place).CallDeferred();
            return;
        }
        ApplyPreset(preset);
    }

    /// <summary>Puts the warning away and goes back to the choices, leaving the preset as it was.</summary>
    private void CancelPresetWarning()
    {
        if (_presetWarning is not null) _presetWarning.Visible = false;
        if (_presetChoices is not null) _presetChoices.Visible = true;
        Place();
    }

    /// <summary>Applies the preset the warning was about, back on the choices.</summary>
    private void UseWarnedPreset()
    {
        if (_presetWarning is not null) _presetWarning.Visible = false;
        if (_presetChoices is not null) _presetChoices.Visible = true;
        if (_warnedPreset is { } preset) ApplyPreset(preset);
    }

    /// <summary>Applies <paramref name="preset"/> to the running game.</summary>
    private void ApplyPreset(string preset)
    {
        WriteGraphics(config =>
        {
            // Asked before the write: what counts is what the preset changes from the file as it stood.
            bool restart = AndroidRestartNotes.PresetNeedsRestart(Refii.Launcher.Core.RuntimeConfig.ReadValues(config), preset);
            AndroidQuickSettings.ApplyPreset(config, preset,
                (name, value) => Refii.Core.Engine_RefiiConfig.TrySetGodotCvar(name, value));
            return restart ? AndroidRestartNotes.PresetNote : null;
        });
        Place();
    }

    /// <summary>Steps one graphics setting, applying it to the running game.</summary>
    private void StepGraphics(AndroidGraphicsOption option, int direction) => WriteGraphics(config =>
    {
        IReadOnlyList<(string Key, string Value)> writes =
            AndroidGraphicsOptions.Writes(option, AndroidGraphicsOptions.Step(option, AndroidQuickSettings.Value(config, option.Key), direction));
        string note = NoteFor(config, writes);
        AndroidQuickSettings.SetGraphics(config, writes,
            (name, value) => Refii.Core.Engine_RefiiConfig.TrySetGodotCvar(name, value));
        return note.Length > 0 ? note : null;
    });

    /// <summary>
    /// Runs one graphics change against the live file (<see cref="Write"/>), then shows the line it returns
    /// when part of it waits for the next start, or puts the line away when none does. A change that failed
    /// to write leaves the line as it was.
    /// </summary>
    private void WriteGraphics(Func<string, string?> change)
    {
        bool written = false;
        string? note = null;
        Write(config =>
        {
            note = change(config);
            written = true;
        });
        if (written) ShowRestartNote(note);
    }

    /// <summary>Shows or hides the frame rate in the top left corner.</summary>
    private void ToggleFpsCounter() => Write(config =>
    {
        bool on = !AndroidQuickSettings.FpsCounter(config);
        AndroidQuickSettings.SetFpsCounter(config, on);
        _fpsCounter?.SetShown(on);
    });

    /// <summary>Turns skipping the launcher at the next start on or off.</summary>
    private void ToggleSkipLauncher() => Write(config =>
        AndroidQuickSettings.SetSkipLauncher(config, !AndroidQuickSettings.SkipLauncher(config)));

    /// <summary>Steps the game's rumble on the phone (or a controller) through 100%, 75%, 50%, 25% and off, from the next frame.</summary>
    private void StepVibration() => Write(config =>
    {
        AndroidQuickSettings.SetVibrationLevel(config,
            AndroidQuickSettings.NextVibrationLevel(AndroidQuickSettings.VibrationLevel(config)));
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
            // Asked as soon as shadows start waiting, so the pause comes with a warning: once per wait.
            bool waiting = Refii.Models.Engine_ShaderSpecialisation.RebuildWaiting;
            if (waiting && !_rebuildWaiting)
            {
                _rebuildPutOff = false;
                _rebuildText.Text = "Shadows need a one-time shader rebuild. Rebuilding pauses the game for a few seconds " +
                    "(up to a minute on an older phone). Rebuild now, or later at the next loading screen?";
            }
            _rebuildWaiting = waiting;
            _rebuild.Visible = waiting && !_rebuildPutOff;
            Place();
        }

        if (ConfigPath?.Invoke() is not { Length: > 0 } config || _frameCap is null || _preset is null || _vibration is null) return;
        try
        {
            int cap = AndroidQuickSettings.FrameCap(config);
            _frameCap.Text = AndroidQuickSettings.FrameCapLabel(cap);
            foreach (FrameCapChoice choice in _frameCapButtons) choice.Button.Disabled = choice.Cap == cap;
            string current = AndroidQuickSettings.Preset(config);
            _preset.Text = AndroidQuickSettings.PresetLabel(current);
            // The preset in use is the one choice that does nothing.
            foreach (PresetChoice choice in _presetButtons)
                choice.Button.Disabled = choice.Preset.Equals(current, StringComparison.OrdinalIgnoreCase);
            if (_fpsCounterButton is not null)
                _fpsCounterButton.Text = AndroidQuickSettings.FpsCounterLabel(AndroidQuickSettings.FpsCounter(config));
            if (_skipLauncherButton is not null)
                _skipLauncherButton.Text = AndroidQuickSettings.SkipLauncherLabel(AndroidQuickSettings.SkipLauncher(config));
            _vibration.Text = AndroidQuickSettings.VibrationLabel(AndroidQuickSettings.VibrationLevel(config));
            foreach (GraphicsRow row in _rows)
            {
                string value = AndroidQuickSettings.Value(config, row.Option.Key);
                row.Value.Text = AndroidGraphicsOptions.Label(row.Option, value);
                // A row that changes nothing as things stand (shadow quality with no shadows) stands greyed.
                bool applies = AndroidGraphicsOptions.Applies(row.Option, key => AndroidQuickSettings.Value(config, key));
                row.Less.Disabled = !applies || !AndroidGraphicsOptions.CanStep(row.Option, value, -1);
                row.More.Disabled = !applies || !AndroidGraphicsOptions.CanStep(row.Option, value, 1);
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
        foreach (FrameCapChoice choice in _frameCapButtons) choice.Button.Pressed -= choice.Pick;
        if (_fpsCounterButton is not null) _fpsCounterButton.Pressed -= ToggleFpsCounter;
        if (_skipLauncherButton is not null) _skipLauncherButton.Pressed -= ToggleSkipLauncher;
        if (_preset is not null) _preset.Pressed -= TogglePresetChoices;
        foreach (PresetChoice choice in _presetButtons) choice.Button.Pressed -= choice.Pick;
        if (_presetWarningCancel is not null) _presetWarningCancel.Pressed -= CancelPresetWarning;
        if (_presetWarningUse is not null) _presetWarningUse.Pressed -= UseWarnedPreset;
        if (_vibration is not null) _vibration.Pressed -= StepVibration;
        if (_performance is not null) _performance.Pressed -= _actions.TogglePerformance;
        if (_debug is not null) _debug.Pressed -= _actions.ToggleDebug;
        if (_photo is not null) _photo.Pressed -= OpenPhotoMode;
        if (_graphicsButton is not null) _graphicsButton.Pressed -= ToggleGraphics;
        if (_remapButton is not null) _remapButton.Pressed -= OpenRemap;
        if (_editButton is not null) _editButton.Pressed -= OpenEditor;
        if (_rebuildNow is not null) _rebuildNow.Pressed -= RebuildNow;
        if (_rebuildLater is not null) _rebuildLater.Pressed -= PutOffRebuild;
        if (_export is not null) _export.Pressed -= ToggleExportChoices;
        if (_diagnostics is not null) _diagnostics.Pressed -= ToggleDiagnostics;
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
