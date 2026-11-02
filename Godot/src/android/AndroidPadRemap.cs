using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Refii.Game;

namespace Refii.AndroidRuntime;

/// <summary>One of a controller's sixteen inputs: its name, its XInput bit (0 for a trigger) and what it does in play.</summary>
public sealed record PadControl(string Name, uint Bit, string Role);

/// <summary>
/// The player's controller remap: which physical button or trigger presses each of the game's. Applied
/// where a physical pad enters the game (GameInput.PadState.Merge, through GameInput.RemapPad), so polled
/// and captured buttons, menus and play all follow it. The map is a permutation: giving a game button a
/// new physical one hands its old one to the button that had it, so nothing is left unreachable. Saved
/// beside refii.toml as <see cref="FileName"/>; the touch controls never pass through it.
/// </summary>
public static class AndroidPadRemap
{
    /// <summary>The map's file, in the runtime folder.</summary>
    public const string FileName = "android-controller-map.json";

    /// <summary>A trigger counts as pressed past this, of 255 (the XInput threshold).</summary>
    public const uint TriggerPressed = 30;

    /// <summary>The sixteen inputs, in the order the remap menu lists them.</summary>
    public static readonly PadControl[] Controls =
    {
        new("A", GameInput.ButtonA, "Use"),
        new("B", GameInput.ButtonB, "Magic"),
        new("X", GameInput.ButtonX, "Melee"),
        new("Y", GameInput.ButtonY, "Ranged"),
        new("LB", GameInput.LeftShoulder, "View"),
        new("RB", GameInput.RightShoulder, ""),
        new("LT", 0u, "Lock"),
        new("RT", 0u, "Spells"),
        new("Back", GameInput.Back, "Emotes"),
        new("Start", GameInput.Start, "Menu"),
        new("LS", GameInput.LeftThumb, "Left stick click"),
        new("RS", GameInput.RightThumb, "Camera mode"),
        new("Up", GameInput.DpadUp, "Item"),
        new("Down", GameInput.DpadDown, "Item"),
        new("Left", GameInput.DpadLeft, "Item"),
        new("Right", GameInput.DpadRight, "Item"),
    };

    /// <summary>For each game input (by its index in <see cref="Controls"/>), the physical input that presses it.</summary>
    private static int[] _source = Identity();

    /// <summary>While true (the remap menu is open) no physical pad reaches the game.</summary>
    public static bool Suppress { get; set; }

    /// <summary>Whether every input presses itself.</summary>
    public static bool IsDefault => _source.Select((s, i) => s == i).All(same => same);

    /// <summary>The physical input that presses game input <paramref name="control"/>.</summary>
    public static string SourceOf(string control) => Controls[_source[Index(control)]].Name;

    /// <summary>
    /// Makes physical <paramref name="physical"/> press game <paramref name="control"/>; the game input it
    /// pressed before takes <paramref name="control"/>'s old physical one.
    /// </summary>
    public static void Assign(string control, string physical)
    {
        int target = Index(control), source = Index(physical);
        int previous = Array.IndexOf(_source, source);
        if (previous == target) return;
        _source[previous] = _source[target];
        _source[target] = source;
    }

    /// <summary>Every input back to pressing itself.</summary>
    public static void Reset() => _source = Identity();

    /// <summary>
    /// Rewrites one device's buttons and triggers through the map. A trigger pressing a trigger keeps its
    /// travel; any other pairing presses fully.
    /// </summary>
    public static void Apply(ref uint buttons, ref uint leftTrigger, ref uint rightTrigger)
    {
        if (Suppress)
        {
            buttons = 0;
            leftTrigger = rightTrigger = 0;
            return;
        }

        if (IsDefault) return;
        uint inButtons = buttons, inLeft = leftTrigger, inRight = rightTrigger;
        buttons = 0;
        leftTrigger = rightTrigger = 0;
        for (int target = 0; target < Controls.Length; target++)
        {
            PadControl from = Controls[_source[target]], to = Controls[target];
            uint travel = from.Name switch
            {
                "LT" => inLeft,
                "RT" => inRight,
                _ => (inButtons & from.Bit) != 0 ? 255u : 0u,
            };
            if (to.Name == "LT") leftTrigger = Math.Max(leftTrigger, travel);
            else if (to.Name == "RT") rightTrigger = Math.Max(rightTrigger, travel);
            else if (travel > TriggerPressed) buttons |= to.Bit;
        }
    }

    /// <summary>The physical input a Godot joypad button is, or null for one the game has no use for.</summary>
    public static string? Physical(JoyButton button) => button switch
    {
        JoyButton.A => "A", JoyButton.B => "B", JoyButton.X => "X", JoyButton.Y => "Y",
        JoyButton.LeftShoulder => "LB", JoyButton.RightShoulder => "RB",
        JoyButton.Back => "Back", JoyButton.Start => "Start",
        JoyButton.LeftStick => "LS", JoyButton.RightStick => "RS",
        JoyButton.DpadUp => "Up", JoyButton.DpadDown => "Down",
        JoyButton.DpadLeft => "Left", JoyButton.DpadRight => "Right",
        _ => null,
    };

    /// <summary>Reads the map from <paramref name="path"/>; a missing or unreadable file is the default.</summary>
    public static void Load(string path)
    {
        Reset();
        try
        {
            if (!File.Exists(path)) return;
            var saved = JsonSerializer.Deserialize(File.ReadAllText(path), AndroidJsonContext.Default.DictionaryStringString);
            if (saved is null) return;
            foreach ((string control, string physical) in saved)
            {
                if (Known(control) && Known(physical)) Assign(control, physical);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            GD.PrintErr("[android] controller map: " + error.Message);
            Reset();
        }
    }

    /// <summary>Writes the map to <paramref name="path"/>, only the inputs that differ from themselves; the default deletes it.</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static void Save(string path)
    {
        if (IsDefault)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        var changed = new Dictionary<string, string>();
        for (int i = 0; i < Controls.Length; i++)
        {
            if (_source[i] != i) changed[Controls[i].Name] = Controls[_source[i]].Name;
        }

        File.WriteAllText(path, JsonSerializer.Serialize(changed, AndroidJsonContext.Default.DictionaryStringString));
    }

    private static bool Known(string name) => Array.FindIndex(Controls, c => c.Name == name) >= 0;

    private static int Index(string name)
    {
        int at = Array.FindIndex(Controls, c => c.Name == name);
        return at >= 0 ? at : throw new ArgumentException("Unknown controller input: " + name, nameof(name));
    }

    private static int[] Identity() => Enumerable.Range(0, Controls.Length).ToArray();
}
