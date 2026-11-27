using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Refii.AndroidRuntime;

/// <summary>
/// The touch control editor, opened from Options: it edits the layout of the kind of screen Options was
/// opened on (the world, a menu, a pub game's board...), each kept apart. Every movable control is ringed;
/// drag one to move it, tap one to pick it, and the toolbar in the middle of the screen (where no control
/// stands) makes the picked one smaller or bigger or puts it back. Reset screen goes back to the built
/// layout for this screen; a preset keeps every screen's layout under a name, to load again
/// (<see cref="AndroidTouchCustomLayout"/>, which saves every change).
/// While it is open the touch controls leave every touch to it, so nothing reaches the game.
/// </summary>
public partial class AndroidTouchEditor : CanvasLayer
{
    private AndroidTouchEditorSurface? _surface;
    private PanelContainer? _toolbar;
    private int _finger = -1;
    private Label? _picked;
    private Label? _preset;
    private Label? _opacity;
    private CheckButton? _hideControls;
    private int _presetAt;
    private LineEdit? _name;
    private Button? _smaller, _bigger, _resetOne, _delete;

    /// <summary>The live refii.toml, where the touch settings are written.</summary>
    public Func<string>? ConfigPath { get; set; }

    /// <summary>Runs after a touch setting is written, so the touch controls reread it now rather than at their next poll.</summary>
    public Action? SettingsWritten { get; set; }

    /// <summary>The kind of screen the touch controls are laid out for.</summary>
    public Func<TouchContext>? Screen { get; set; }

    /// <summary>The kind of screen being edited, fixed when the editor opens.</summary>
    private TouchContext _screen = TouchContext.World;

    /// <summary>The controls as the touch controls last laid them out.</summary>
    public Func<IReadOnlyList<TouchControl>>? Controls { get; set; }

    /// <summary>The picked control's id, or null.</summary>
    internal string? Picked { get; private set; }

    /// <summary>Builds the editor, hidden.</summary>
    public override void _Ready()
    {
        Layer = 155;
        ProcessMode = ProcessModeEnum.Always;
        Visible = false;
        _surface = new AndroidTouchEditorSurface { Editor = this, MouseFilter = Control.MouseFilterEnum.Stop };
        _surface.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_surface);

        var middle = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        middle.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        PanelContainer panel = _toolbar = AndroidSeeThrough.Panel();
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 8);
        _picked = AndroidSeeThrough.Label("", 18);
        _picked.HorizontalAlignment = HorizontalAlignment.Center;
        body.AddChild(_picked);

        var size = Row(body);
        _smaller = Add(size, "Smaller", 130, () => Resize(-1));
        _bigger = Add(size, "Bigger", 130, () => Resize(1));
        _resetOne = Add(size, "Reset button", 170, ResetOne);

        // How see-through every control is, for all of them at once (touch_opacity).
        var opacity = Row(body);
        Add(opacity, "-", 70, () => StepOpacity(-0.1f));
        _opacity = AndroidSeeThrough.Label("", 18);
        _opacity.CustomMinimumSize = new Vector2(200, 50);
        _opacity.HorizontalAlignment = HorizontalAlignment.Center;
        _opacity.VerticalAlignment = VerticalAlignment.Center;
        opacity.AddChild(_opacity);
        Add(opacity, "+", 70, () => StepOpacity(0.1f));

        _hideControls = new CheckButton { Text = "Hide on-screen controls" };
        _hideControls.Toggled += SetControlsHidden;
        Row(body).AddChild(_hideControls);

        var presets = Row(body);
        // Stepped with < and >, not a drop-down: a popup is a window of its own, which takes every touch
        // before the touch controls can pass it on as a click.
        Add(presets, "<", 56, () => StepPreset(-1));
        _preset = AndroidSeeThrough.Label("", 18);
        _preset.CustomMinimumSize = new Vector2(170, 50);
        _preset.HorizontalAlignment = HorizontalAlignment.Center;
        _preset.VerticalAlignment = VerticalAlignment.Center;
        _preset.ClipText = true;
        presets.AddChild(_preset);
        Add(presets, ">", 56, () => StepPreset(1));
        Add(presets, "Load", 100, LoadPreset);
        _delete = Add(presets, "Delete", 110, DeletePreset);

        var save = Row(body);
        _name = new LineEdit { PlaceholderText = "Preset name", CustomMinimumSize = new Vector2(220, 50) };
        _name.AddThemeFontSizeOverride("font_size", 18);
        save.AddChild(_name);
        Add(save, "Save preset", 190, SavePreset);

        var end = Row(body);
        Add(end, "Reset screen", 200, ResetScreen);
        Add(end, "Done", 200, Close);
        panel.AddChild(body);
        middle.AddChild(panel);
        AddChild(middle);
    }

    /// <summary>Opens the editor over the controls as they stand.</summary>
    public void Open()
    {
        Visible = true;
        Picked = null;
        _screen = Screen?.Invoke() is { } screen and not TouchContext.Hidden ? screen : TouchContext.World;
        Refresh();
    }

    /// <summary>Closes the editor; every change is already saved.</summary>
    public void Close()
    {
        Visible = false;
        Picked = null;
        _finger = -1;
        _name?.ReleaseFocus();
    }

    /// <summary>While open, the whole screen: the touch controls leave every touch to the editor.</summary>
    public void AddRects(List<Rect2> into)
    {
        if (Visible) into.Add(GetViewport().GetVisibleRect());
    }

    /// <summary>
    /// Picks and drags with the raw finger. Touches off the touch controls reach menus through a pointer
    /// that turns a quick slide into scrolling, so a drag is read here instead; the touch is left to go on,
    /// so the toolbar's buttons still take their taps.
    /// </summary>
    public override void _Input(InputEvent inputEvent)
    {
        if (!Visible) return;
        switch (inputEvent)
        {
            case InputEventScreenTouch { Pressed: true } touch when _finger < 0:
                if (_toolbar?.GetGlobalRect().HasPoint(touch.Position) == true) return;
                // The smallest control under the finger: a pill over a larger region wins.
                Picked = Editable().Where(c => c.Hit(touch.Position))
                    .OrderBy(c => c.Bounds.Size.LengthSquared()).FirstOrDefault()?.Id;
                _finger = Picked is null ? -1 : touch.Index;
                Refresh();
                break;
            case InputEventScreenTouch { Pressed: false } touch when touch.Index == _finger:
                _finger = -1;
                break;
            case InputEventScreenDrag drag when drag.Index == _finger && Picked is { } id:
                AndroidTouchCustomLayout.Move(_screen, id, drag.Relative, GetViewport().GetVisibleRect().Size);
                break;
        }
    }

    /// <summary>Redraws the outlines as the controls move.</summary>
    public override void _Process(double delta)
    {
        if (Visible) _surface?.QueueRedraw();
    }

    internal IEnumerable<TouchControl> Editable() =>
        (Controls?.Invoke() ?? Array.Empty<TouchControl>()).Where(AndroidTouchCustomLayout.Editable);

    /// <summary>The control's name as the player knows it: its caption, its label, or its id.</summary>
    private static string NameOf(TouchControl control) =>
        control.Caption.Length > 0 ? control.Caption : control.Label.Length > 0 ? control.Label : control.Id;

    private void Resize(int steps)
    {
        if (Picked is { } id) AndroidTouchCustomLayout.Resize(_screen, id, steps);
        Refresh();
    }

    private void ResetOne()
    {
        if (Picked is { } id) AndroidTouchCustomLayout.ResetControl(_screen, id);
        Refresh();
    }

    /// <summary>Turns the overlay off or restores it through the runtime's touch input setting.</summary>
    private void SetControlsHidden(bool hidden)
    {
        if (ConfigPath?.Invoke() is not { Length: > 0 } config) return;
        try
        {
            AndroidQuickSettings.SetHideTouchControls(config, hidden);
            SettingsWritten?.Invoke();
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            GD.PrintErr("[android] touch controls: " + error.Message);
        }
        Refresh();
    }

    private void StepOpacity(float step)
    {
        if (ConfigPath?.Invoke() is not { Length: > 0 } config) return;
        try
        {
            AndroidQuickSettings.SetTouchOpacity(config, AndroidQuickSettings.TouchOpacity(config) + step);
            SettingsWritten?.Invoke();
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            GD.PrintErr("[android] touch opacity: " + error.Message);
        }

        Refresh();
    }

    private void ResetScreen()
    {
        AndroidTouchCustomLayout.ResetScreen(_screen);
        Refresh();
    }

    /// <summary>Default first, then the saved presets.</summary>
    private static string[] PresetNames() => AndroidTouchCustomLayout.Presets.Prepend(AndroidTouchCustomLayout.DefaultName).ToArray();

    private void StepPreset(int direction)
    {
        int count = PresetNames().Length;
        _presetAt = ((_presetAt + direction) % count + count) % count;
        Refresh();
    }

    private void LoadPreset()
    {
        AndroidTouchCustomLayout.LoadPreset(PresetNames()[Math.Clamp(_presetAt, 0, PresetNames().Length - 1)]);
        Refresh();
    }

    private void DeletePreset()
    {
        if (_presetAt > 0) AndroidTouchCustomLayout.DeletePreset(PresetNames()[Math.Min(_presetAt, PresetNames().Length - 1)]);
        _presetAt = 0;
        Refresh();
    }

    private void SavePreset()
    {
        if (_name is not { } name) return;
        AndroidTouchCustomLayout.SavePreset(name.Text);
        string saved = name.Text.Trim();
        name.Text = "";
        name.ReleaseFocus();
        Refresh(saved);
    }

    /// <summary>Shows the picked control and its size, and the preset the arrows are on.</summary>
    internal void Refresh(string? select = null)
    {
        TouchControl? picked = Editable().FirstOrDefault(c => c.Id == Picked);
        if (_picked is not null)
        {
            string screen = AndroidTouchCustomLayout.ScreenName(_screen);
            _picked.Text = picked is null
                ? $"{screen}: drag a control to move it, or tap one to resize it"
                : $"{screen}: {NameOf(picked)}, size {AndroidTouchCustomLayout.Of(_screen, picked.Id).Size * 100:0}%";
        }

        foreach (Button? button in new[] { _smaller, _bigger, _resetOne })
            if (button is not null) button.Disabled = picked is null;
        if (_opacity is not null && ConfigPath?.Invoke() is { Length: > 0 } config)
        {
            try
            {
                _hideControls?.SetPressedNoSignal(AndroidQuickSettings.HideTouchControls(config));
                _opacity.Text = $"Opacity {AndroidQuickSettings.TouchOpacity(config) * 100:0}%";
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { _opacity.Text = "Opacity"; }
        }

        string[] names = PresetNames();
        if (select is not null) _presetAt = Math.Max(0, Array.IndexOf(names, select));
        _presetAt = Math.Clamp(_presetAt, 0, names.Length - 1);
        if (_preset is not null) _preset.Text = names[_presetAt];
        if (_delete is not null) _delete.Disabled = _presetAt == 0;
    }

    private static HBoxContainer Row(VBoxContainer body)
    {
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 8);
        body.AddChild(row);
        return row;
    }

    private static Button Add(HBoxContainer row, string text, float width, Action pressed)
    {
        Button button = AndroidSeeThrough.Button(text, new Vector2(width, 50), 18);
        button.Pressed += pressed;
        row.AddChild(button);
        return button;
    }
}
