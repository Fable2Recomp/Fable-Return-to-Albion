using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace Refii.AndroidRuntime;

/// <summary>
/// One touch control moved and resized by the player: its centre's shift as fractions of the screen's
/// width and height, and its size as a multiple of the laid-out one. Fractions, so a layout survives a
/// new resolution or touch scale.
/// </summary>
public sealed record TouchAdjust(float Dx, float Dy, float Size);

/// <summary>
/// The player's own touch layouts, one per kind of screen (<see cref="TouchContext"/>: the world, menus,
/// a held wheel, a pub game's board, photo mode, the mirror): the controls they moved or resized there,
/// applied by id over the layout <see cref="AndroidTouchLayout.Build"/> makes for that screen, so drawing
/// and hit-testing both follow and moving A in the world leaves A in the menus alone. Named presets keep
/// every screen's layout together to come back to; Default is the layouts as built. The move stick and the
/// camera drag are regions rather than buttons, and the item ring sits over the HUD's own slots, so those
/// stay put. Saved beside refii.toml as <see cref="FileName"/>; every edit saves.
/// </summary>
public static class AndroidTouchCustomLayout
{
    /// <summary>The layouts' file, in the runtime folder.</summary>
    public const string FileName = "android-touch-layouts.json";

    /// <summary>The built layouts, with nothing moved.</summary>
    public const string DefaultName = "Default";

    /// <summary>The size range and step the editor's Smaller and Bigger move through.</summary>
    public const float MinSize = 0.5f, MaxSize = 2f, SizeStep = 0.1f;

    /// <summary>
    /// What is saved: each screen's layout in use, by <see cref="TouchContext"/> name, and the named presets,
    /// each every screen's. <see cref="Current"/> and <see cref="Presets"/> are the first files' one layout,
    /// for every screen alike; they are read as the world's and written empty.
    /// </summary>
    public sealed class LayoutsFile
    {
        public Dictionary<string, TouchAdjust> Current { get; set; } = new();
        public Dictionary<string, Dictionary<string, TouchAdjust>> Presets { get; set; } = new();
        public Dictionary<string, Dictionary<string, TouchAdjust>> Screens { get; set; } = new();
        public Dictionary<string, Dictionary<string, Dictionary<string, TouchAdjust>>> Saved { get; set; } = new();
    }

    private static LayoutsFile _file = new();
    private static string _path = "";

    /// <summary>Bumped on every change, so the touch controls lay out again.</summary>
    public static int Version { get; private set; }

    /// <summary>The adjustments in use on <paramref name="screen"/>, by control id.</summary>
    public static IReadOnlyDictionary<string, TouchAdjust> Current(TouchContext screen) =>
        _file.Screens.TryGetValue(screen.ToString(), out Dictionary<string, TouchAdjust>? layout)
            ? layout : new Dictionary<string, TouchAdjust>();

    /// <summary>The saved presets' names, sorted.</summary>
    public static IEnumerable<string> Presets => _file.Saved.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The name a player sees for a kind of screen.</summary>
    public static string ScreenName(TouchContext screen) => screen switch
    {
        TouchContext.World => "In the world",
        TouchContext.Menu => "Menus",
        TouchContext.Wheel => "Held wheel",
        TouchContext.PubGame => "Pub games",
        TouchContext.Photo => "Photo mode",
        TouchContext.Mirror => "The mirror",
        TouchContext.Decorate => "Decorating",
        _ => screen.ToString(),
    };

    /// <summary>Whether a control can be moved and resized: a button or pill, not a region or the HUD's item ring.</summary>
    public static bool Editable(TouchControl control) =>
        control.Kind is not (TouchControlKind.Stick or TouchControlKind.Look) && !control.OverHud &&
        !control.Id.StartsWith(AndroidTouchLayout.SpellPrefix, StringComparison.Ordinal) &&
        control.Id != AndroidTouchLayout.SpellsClose;

    /// <summary>
    /// <paramref name="controls"/>, laid out for <paramref name="screen"/>, with the player's adjustments
    /// for that screen: each moved and resized about its centre, kept on screen.
    /// </summary>
    public static IReadOnlyList<TouchControl> Apply(IReadOnlyList<TouchControl> controls, Vector2 size, TouchContext screen)
    {
        IReadOnlyDictionary<string, TouchAdjust> layout = Current(screen);
        if (layout.Count == 0) return controls;
        var adjusted = new List<TouchControl>(controls.Count);
        foreach (TouchControl control in controls)
        {
            adjusted.Add(Editable(control) && layout.TryGetValue(control.Id, out TouchAdjust? adjust)
                ? control with { Bounds = Adjusted(control.Bounds, adjust, size) }
                : control);
        }

        return adjusted;
    }

    /// <summary>A control's bounds after <paramref name="adjust"/>, its centre kept inside the screen.</summary>
    public static Rect2 Adjusted(Rect2 bounds, TouchAdjust adjust, Vector2 size)
    {
        Vector2 extent = bounds.Size * adjust.Size;
        Vector2 centre = bounds.GetCenter() + new Vector2(adjust.Dx * size.X, adjust.Dy * size.Y);
        centre = new Vector2(
            Mathf.Clamp(centre.X, extent.X * 0.5f, Mathf.Max(extent.X * 0.5f, size.X - extent.X * 0.5f)),
            Mathf.Clamp(centre.Y, extent.Y * 0.5f, Mathf.Max(extent.Y * 0.5f, size.Y - extent.Y * 0.5f)));
        return new Rect2(centre - extent * 0.5f, extent);
    }

    /// <summary>The adjustment in use for <paramref name="id"/> on <paramref name="screen"/>: none is no shift at full size.</summary>
    public static TouchAdjust Of(TouchContext screen, string id) =>
        Current(screen).TryGetValue(id, out TouchAdjust? adjust) ? adjust : new(0f, 0f, 1f);

    /// <summary>Shifts a control by <paramref name="delta"/> canvas units on a screen of <paramref name="size"/>.</summary>
    public static void Move(TouchContext screen, string id, Vector2 delta, Vector2 size)
    {
        if (size.X <= 0f || size.Y <= 0f) return;
        TouchAdjust was = Of(screen, id);
        Set(screen, id, was with { Dx = was.Dx + delta.X / size.X, Dy = was.Dy + delta.Y / size.Y });
    }

    /// <summary>Grows (<paramref name="steps"/> above 0) or shrinks a control by <see cref="SizeStep"/> a step.</summary>
    public static void Resize(TouchContext screen, string id, int steps)
    {
        TouchAdjust was = Of(screen, id);
        float size = MathF.Round(Math.Clamp(was.Size + steps * SizeStep, MinSize, MaxSize), 2);
        Set(screen, id, was with { Size = size });
    }

    /// <summary>One control on <paramref name="screen"/> back where and as large as it was built.</summary>
    public static void ResetControl(TouchContext screen, string id)
    {
        if (_file.Screens.TryGetValue(screen.ToString(), out Dictionary<string, TouchAdjust>? layout) && layout.Remove(id))
            Changed();
    }

    /// <summary>Every control on <paramref name="screen"/> back to its built layout; the other screens keep theirs.</summary>
    public static void ResetScreen(TouchContext screen)
    {
        if (_file.Screens.Remove(screen.ToString())) Changed();
    }

    /// <summary>Keeps every screen's layout in use as <paramref name="name"/>, replacing a preset of that name.</summary>
    public static void SavePreset(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase)) return;
        _file.Saved[name] = Copy(_file.Screens);
        Changed();
    }

    /// <summary>Puts preset <paramref name="name"/> in use on every screen; Default clears every adjustment.</summary>
    public static void LoadPreset(string name)
    {
        if (name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase))
        {
            _file.Screens.Clear();
            Changed();
        }
        else if (_file.Saved.TryGetValue(name, out Dictionary<string, Dictionary<string, TouchAdjust>>? preset))
        {
            _file.Screens = Copy(preset);
            Changed();
        }
    }

    /// <summary>Forgets preset <paramref name="name"/>; the layouts in use stay.</summary>
    public static void DeletePreset(string name)
    {
        if (_file.Saved.Remove(name)) Changed();
    }

    /// <summary>Reads the layouts from <paramref name="path"/>, where every later change is saved; a missing or unreadable file is the default.</summary>
    public static void Load(string path)
    {
        _path = path;
        _file = new LayoutsFile();
        try
        {
            if (File.Exists(path))
            {
                _file = JsonSerializer.Deserialize(File.ReadAllText(path), AndroidJsonContext.Default.LayoutsFile) ?? new LayoutsFile();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            GD.PrintErr("[android] touch layouts: " + error.Message);
        }

        // A first file's one layout was made in the world, and its presets with it.
        string world = TouchContext.World.ToString();
        if (_file.Current.Count > 0 && !_file.Screens.ContainsKey(world)) _file.Screens[world] = _file.Current;
        foreach ((string name, Dictionary<string, TouchAdjust> layout) in _file.Presets)
            _file.Saved.TryAdd(name, new Dictionary<string, Dictionary<string, TouchAdjust>> { [world] = layout });
        _file.Current = new();
        _file.Presets = new();
        Version++;
    }

    private static void Set(TouchContext screen, string id, TouchAdjust adjust)
    {
        string key = screen.ToString();
        if (!_file.Screens.TryGetValue(key, out Dictionary<string, TouchAdjust>? layout))
            _file.Screens[key] = layout = new Dictionary<string, TouchAdjust>();
        if (adjust is { Dx: 0f, Dy: 0f, Size: 1f }) layout.Remove(id);
        else layout[id] = adjust;
        if (layout.Count == 0) _file.Screens.Remove(key);
        Changed();
    }

    private static Dictionary<string, Dictionary<string, TouchAdjust>> Copy(Dictionary<string, Dictionary<string, TouchAdjust>> screens) =>
        screens.ToDictionary(pair => pair.Key, pair => new Dictionary<string, TouchAdjust>(pair.Value));

    private static void Changed()
    {
        Version++;
        if (_path.Length == 0) return;
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_file, AndroidJsonContext.Default.LayoutsFile));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            GD.PrintErr("[android] touch layouts: " + error.Message);
        }
    }
}
