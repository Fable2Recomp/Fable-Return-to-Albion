using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Refii.AndroidRuntime;

/// <summary>
/// The controller remap menu, opened from Options: the game's sixteen inputs, each showing the physical
/// button or trigger that presses it. Tap one, then press the controller's button for it; the two swap
/// (<see cref="AndroidPadRemap.Assign"/>). Reset to default puts every input back. While it is open no
/// controller input reaches the game, and the touch controls leave every touch to it.
/// </summary>
public partial class AndroidPadRemapMenu : CanvasLayer
{
    private readonly List<(PadControl Control, Button Button)> _rows = new();
    private string? _capturing;
    private Label? _hint;

    /// <summary>The live refii.toml; the map is saved beside it.</summary>
    public Func<string>? ConfigPath { get; set; }

    /// <summary>Builds the menu, hidden.</summary>
    public override void _Ready()
    {
        Layer = 160;
        ProcessMode = ProcessModeEnum.Always;
        Visible = false;
        var screen = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        screen.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        PanelContainer panel = AndroidSeeThrough.Panel();
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 10);
        body.AddChild(AndroidSeeThrough.Label("Controller buttons", 22));
        _hint = AndroidSeeThrough.Label("", 16);
        body.AddChild(_hint);
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 8);
        foreach (PadControl control in AndroidPadRemap.Controls)
        {
            Button button = AndroidSeeThrough.Button("", new Vector2(340, 46), 18);
            button.Alignment = HorizontalAlignment.Left;
            string name = control.Name;
            button.Pressed += () => Capture(name);
            grid.AddChild(button);
            _rows.Add((control, button));
        }

        body.AddChild(grid);
        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        actions.AddThemeConstantOverride("separation", 10);
        Button reset = AndroidSeeThrough.Button("Reset to default", new Vector2(220, 52));
        Button done = AndroidSeeThrough.Button("Done", new Vector2(160, 52));
        reset.Pressed += ResetAll;
        done.Pressed += Close;
        actions.AddChild(reset);
        actions.AddChild(done);
        body.AddChild(actions);
        panel.AddChild(body);
        screen.AddChild(panel);
        AddChild(screen);
    }

    /// <summary>Opens the menu, holding the controller's input back from the game.</summary>
    public void Open()
    {
        Visible = true;
        _capturing = null;
        AndroidPadRemap.Suppress = true;
        Refresh();
    }

    /// <summary>Closes the menu and gives the controller back to the game.</summary>
    public void Close()
    {
        Visible = false;
        _capturing = null;
        AndroidPadRemap.Suppress = false;
    }

    /// <summary>While open, the whole screen: the touch controls leave every touch to the menu.</summary>
    public IEnumerable<Rect2> Rects()
    {
        if (Visible) yield return GetViewport().GetVisibleRect();
    }

    /// <summary>The next controller press goes to <paramref name="control"/>; tapped again, it stops waiting.</summary>
    private void Capture(string control)
    {
        _capturing = _capturing == control ? null : control;
        Refresh();
    }

    /// <summary>Takes the press the menu is waiting for: a button, or a trigger pulled past half way.</summary>
    public override void _Input(InputEvent inputEvent)
    {
        if (!Visible || _capturing is not { } control) return;
        string? physical = inputEvent switch
        {
            InputEventJoypadButton { Pressed: true } button => AndroidPadRemap.Physical(button.ButtonIndex),
            InputEventJoypadMotion { Axis: JoyAxis.TriggerLeft, AxisValue: > 0.5f } => "LT",
            InputEventJoypadMotion { Axis: JoyAxis.TriggerRight, AxisValue: > 0.5f } => "RT",
            _ => null,
        };
        if (physical is null) return;
        AndroidPadRemap.Assign(control, physical);
        _capturing = null;
        Save();
        Refresh();
        GetViewport().SetInputAsHandled();
    }

    private void ResetAll()
    {
        AndroidPadRemap.Reset();
        _capturing = null;
        Save();
        Refresh();
    }

    private void Save()
    {
        if (ConfigPath?.Invoke() is not { Length: > 0 } config) return;
        try
        {
            AndroidPadRemap.Save(Path.Combine(Path.GetDirectoryName(config) ?? "", AndroidPadRemap.FileName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            GD.PrintErr("[android] controller map: " + error.Message);
        }
    }

    /// <summary>Labels each row with its game input, its role and the physical input that presses it.</summary>
    private void Refresh()
    {
        if (_hint is not null)
        {
            _hint.Text = _capturing is { } waiting
                ? $"Press the controller button for {waiting} (tap it again to cancel)."
                : "Tap a button, then press the one on your controller that should do it.";
        }

        foreach ((PadControl control, Button button) in _rows)
        {
            string role = control.Role.Length > 0 ? $" ({control.Role})" : "";
            button.Text = _capturing == control.Name
                ? $"{control.Name}{role}:  press a button..."
                : $"{control.Name}{role}:  {AndroidPadRemap.SourceOf(control.Name)}";
        }
    }
}
