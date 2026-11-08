using System.Collections.Generic;
using Godot;
using Refii.Mods;

namespace Refii.AndroidRuntime;

/// <summary>
/// THE MODS' TOUCH CONTROLS: what the enabled mods ask of the screen (<see cref="ModInputTouch"/>),
/// as controls among the pad's own. An action's PILL stands in the top row, filling leftward from
/// Emotes in declaration order and on into a second row beneath when the first is full, a mode's
/// pills hung under their own mod's pill; each is a
/// plain pill of the same look as Menu's, named by the mod, and the touch editor moves it like any
/// other (its id is <see cref="PillPrefix"/> and the action). An AREA is an invisible control over
/// the mod's own drawing — the hotbar's slots, the minimap. A CAPTION renames the on-screen pad
/// button the action is bound to, and stands it even where the hero could not use it otherwise.
/// </summary>
/// <remarks>
/// Each frame the controls read what stands (<see cref="Now"/>), lay out again when it changed
/// (<see cref="Same"/>), and tell the mods what a finger holds (<see cref="Hold"/>).
/// </remarks>
public static class AndroidTouchModControls
{
    /// <summary>The id of an action's pill, before its scoped name.</summary>
    public const string PillPrefix = "mod:";

    /// <summary>The id of an action's area, before its scoped name: never drawn, never edited.</summary>
    public const string AreaPrefix = "modarea:";

    /// <summary>What the mods ask of the screen now.</summary>
    public sealed record Offer(
        IReadOnlyList<(string Action, string Text, bool Modal)> Pills,
        IReadOnlyList<(string Action, Rect2 Rect)> Areas,
        IReadOnlyDictionary<string, string> Captions)
    {
        public static readonly Offer None = new(
            System.Array.Empty<(string, string, bool)>(), System.Array.Empty<(string, Rect2)>(), new Dictionary<string, string>());

        public bool Empty => Pills.Count == 0 && Areas.Count == 0 && Captions.Count == 0;
    }

    private static readonly List<(string Action, string Text, bool Modal)> _pills = new();
    private static readonly List<(string Action, Rect2 Rect)> _areas = new();
    private static readonly Dictionary<string, string> _captions = new();

    /// <summary>The lists above as an offer, compared against the last without building one.</summary>
    private static readonly Offer _scratch = new(_pills, _areas, _captions);

    /// <summary>
    /// The pills, areas and captions standing now: <paramref name="previous"/> itself while nothing
    /// changed (read every frame, so the same is not built again). <paramref name="fromHud"/> maps a
    /// HUD piece's canvas rectangle onto the controls' canvas (null while the HUD's canvas is unknown).
    /// </summary>
    public static Offer Now(System.Func<Rect2, Rect2?> fromHud, Offer previous)
    {
        IReadOnlyList<ModInput.Action> actions = ModInput.All;
        _pills.Clear();
        _captions.Clear();
        // The pills that always stand first, then a mode's (a "when"), so a mode coming and going
        // never moves the others.
        for (int pass = 0; pass < 2; ++pass)
        {
            bool modal = pass == 1;
            foreach (ModInput.Action action in actions)
            {
                if (action.Touch is { Pill.Length: > 0 } pill && (pill.When.Length != 0) == modal && ModInputTouch.Shown(action))
                {
                    _pills.Add((action.Name, ModText.Game(pill.Pill), modal));
                }
            }
        }

        foreach (ModInput.Action action in actions)
        {
            if (!ModInputTouch.Shown(action))
            {
                continue;
            }

            if (action.Touch!.Pill.Length == 0 && action.Touch.Caption.Length != 0)
            {
                foreach (JoyButton button in ModInput.EffectivePad(action).Buttons)
                {
                    _captions[ModInput.PadName(button)] = ModText.Game(action.Touch.Caption);
                }
            }
        }

        _areas.Clear();
        if (ModInputTouch.AnyAreas)
        {
            ModInputTouch.Areas(_areas, fromHud);
        }

        if (_scratch.Empty)
        {
            return Offer.None;
        }

        return Same(_scratch, previous)
            ? previous
            : new Offer(_pills.ToArray(), _areas.ToArray(), new Dictionary<string, string>(_captions));
    }

    /// <summary>Whether two offers lay out the same: the same pills and captions, and every area within two units of where it was.</summary>
    public static bool Same(Offer a, Offer b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.Pills.Count != b.Pills.Count || a.Areas.Count != b.Areas.Count || a.Captions.Count != b.Captions.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Pills.Count; ++i)
        {
            if (a.Pills[i] != b.Pills[i])
            {
                return false;
            }
        }

        for (int i = 0; i < a.Areas.Count; ++i)
        {
            if (a.Areas[i].Action != b.Areas[i].Action ||
                a.Areas[i].Rect.Position.DistanceTo(b.Areas[i].Rect.Position) > 2f ||
                a.Areas[i].Rect.Size.DistanceTo(b.Areas[i].Rect.Size) > 2f)
            {
                return false;
            }
        }

        foreach ((string button, string caption) in a.Captions)
        {
            if (!b.Captions.TryGetValue(button, out string? other) || other != caption)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The size, in layout units, a pill's text is measured at.</summary>
    private const int PillFontSize = 20;

    /// <summary>A pill's width for its resolved text, in layout units: the text's run in the touch controls' font plus the pill's ends.</summary>
    public static float PillWidth(string text) =>
        Mathf.Max(88f, ThemeDB.FallbackFont.GetStringSize(text, HorizontalAlignment.Left, -1, PillFontSize).X + 36f);

    /// <summary>
    /// The pills, laid leftward from <paramref name="right"/> along the top row and, past
    /// <paramref name="leftLimit"/>, on along a second row beneath from the same right edge. A mode's
    /// pill (a "when") hangs under its own mod's pill instead, the mode's pills stacked down from it,
    /// so the row never moves as a mode comes and goes; a mode with no pill of its mod above joins the row.
    /// </summary>
    public static void AddPills(List<TouchControl> controls, Offer offer, float right, float leftLimit, float top,
        float pillH, float gap, float u)
    {
        float x = right, y = top;
        var placed = new Dictionary<string, (Rect2 Under, int Hung)>();
        foreach ((string action, string text, bool modal) in offer.Pills)
        {
            float width = PillWidth(text) * u;
            string mod = ModOf(action);
            Rect2 bounds;
            if (modal && placed.TryGetValue(mod, out (Rect2 Under, int Hung) parent))
            {
                bounds = new Rect2(parent.Under.Position.X, parent.Under.End.Y + gap + parent.Hung * (pillH + gap), width, pillH);
                placed[mod] = (parent.Under, parent.Hung + 1);
            }
            else
            {
                if (x - width < leftLimit && x < right)
                {
                    // The first row is full: a second beneath it, from the same right edge.
                    x = right;
                    y += pillH + gap;
                }

                bounds = new Rect2(x - width, y, width, pillH);
                x -= width + gap;
                if (!modal && !placed.ContainsKey(mod))
                {
                    placed[mod] = (bounds, 0);
                }
            }

            // A pill steers as a face button does: a thumb sliding off it turns the camera while it is held.
            controls.Add(new TouchControl(PillPrefix + action, TouchControlKind.Button, bounds,
                false, 0u, TouchTrigger.None, text, string.Empty, AndroidTouchLayout.Slate, Steers: true));
        }
    }

    /// <summary>The mod a scoped action is declared by: <c>skate</c> of <c>skate.manual</c>.</summary>
    private static string ModOf(string action) => action.IndexOf('.') is var dot and > 0 ? action[..dot] : action;

    /// <summary>The areas, as controls that press nothing on the pad: the finger is the mod's.</summary>
    public static void AddAreas(List<TouchControl> controls, Offer offer)
    {
        foreach ((string action, Rect2 rect) in offer.Areas)
        {
            controls.Add(new TouchControl(AreaPrefix + action, TouchControlKind.Button, rect, false, 0u,
                TouchTrigger.None, string.Empty, string.Empty, AndroidTouchLayout.Slate));
        }
    }

    /// <summary>Whether a control is a mod's area, which is neither drawn nor edited.</summary>
    public static bool IsArea(TouchControl control) => control.Id.StartsWith(AreaPrefix, System.StringComparison.Ordinal);

    private static readonly List<string> _held = new();

    /// <summary>Tells the mods which of their pills and areas a finger holds now (every frame; none while the controls are hidden).</summary>
    public static void Hold(AndroidTouchPad pad, IReadOnlyList<TouchControl> controls, double now)
    {
        _held.Clear();
        foreach (TouchControl control in controls)
        {
            string id = control.Id;
            if (id.StartsWith(PillPrefix, System.StringComparison.Ordinal) && pad.Pressing(id, now))
            {
                _held.Add(id[PillPrefix.Length..]);
            }
            else if (id.StartsWith(AreaPrefix, System.StringComparison.Ordinal) && pad.Pressing(id, now))
            {
                _held.Add(id[AreaPrefix.Length..]);
            }
        }

        ModInputTouch.SetHeld(_held);
    }
}
