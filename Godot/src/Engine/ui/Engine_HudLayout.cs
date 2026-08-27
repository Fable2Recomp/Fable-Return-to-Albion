using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// Where the rebuilt HUD's pieces go, and which art each one wears. A piece is a NAMED record —
/// movable by editing one entry — whose art resolves by id through an override table before it
/// reaches the banks.
///
/// A piece hangs from a fraction of the screen and is then offset in reference units, and only the
/// OFFSET is scaled: that is what keeps a corner piece in its corner at an aspect the game never
/// shipped for. The guest has exactly two layouts, 4:3 and 16:9, carried as attributes beside each
/// element (<see cref="Engine_UiWidescreen"/>), and takes the closer of the two for anything else.
/// </summary>
public sealed class Engine_HudLayout
{
    /// <summary>Where the edited arrangement lives — the editor writes it, every boot reads it.</summary>
    public const string LayoutPath = "user://hud/layout.json";

    /// <summary>The derived layout as it ran, written every boot for the editor and modders.</summary>
    public const string SnapshotPath = "user://hud/layout.snapshot.json";

    /// <summary>
    /// The design height every offset and size is authored against — the CANVAS height, which must
    /// equal the design height. The camera draws 720 canvas units across the window height whatever
    /// the pixel count, so this only rescales pieces when the visible extent grows past the design
    /// (a narrower-than-16:9 window revealing more height).
    /// </summary>
    public const float ReferenceHeight = 720f;

    /// <summary>A fraction of the screen a piece is attached to: (0,0) top-left, (1,1) bottom-right.</summary>
    public readonly record struct Anchor(float X, float Y)
    {
        public static readonly Anchor TopLeft = new(0f, 0f);
        public static readonly Anchor TopCentre = new(0.5f, 0f);
        public static readonly Anchor TopRight = new(1f, 0f);
        public static readonly Anchor Centre = new(0.5f, 0.5f);
        public static readonly Anchor BottomLeft = new(0f, 1f);
        public static readonly Anchor BottomCentre = new(0.5f, 1f);
        public static readonly Anchor BottomRight = new(1f, 1f);
    }

    /// <summary>How a piece's value drives what is drawn.</summary>
    public enum Drive
    {
        /// <summary>Static art. No value; it is either shown or it is not.</summary>
        None,

        /// <summary>A bar. The meter scales it along X, from its left edge.</summary>
        Bar,

        /// <summary>A text field, taking the guest's published string.</summary>
        Text,

        /// <summary>
        /// A pulser: the guest loops the element's authored timeline (`sub_822C21E0`,
        /// `time = phase × 1000`) and publishes the phase on registry 4; the piece scrubs its
        /// tracks at that tick.
        /// </summary>
        Pulse,
    }

    /// <summary>One placed piece of the HUD.</summary>
    /// <param name="Id">
    /// The piece's stable name, what a layout edit and a mod override address it by. Not the `.bgf`
    /// element name: the art behind a piece can be replaced.
    /// </param>
    /// <param name="Art">
    /// The FALLBACK art, as an element name in `hud.bgf`; <see cref="Engine_HudArt"/> consults the
    /// override table first.
    /// </param>
    /// <param name="Anchor">Which fraction of the screen the piece hangs from.</param>
    /// <param name="Offset">
    /// Where it sits relative to that anchor, in reference units, +X right and +Y DOWN; the flip
    /// into the canvas's +Y-up space happens once, in <see cref="Place"/>.
    /// </param>
    /// <param name="Value">The meter or text name that drives it, or empty for static art.</param>
    /// <param name="Driven">What that value does to it.</param>
    /// <param name="ElementOrdinal">
    /// The `hud.bgf` element this piece was derived from, or -1 for a hand-authored piece. It is
    /// the piece's STATE gate: the guest's own active bit on that element says whether the thing
    /// exists right now (the wheel is closed, the ABXY prompt is not offered, the pulser is dark).
    /// </param>
    /// <param name="Placed">
    /// Whether this piece has a place on screen yet. The file states what a piece IS exactly, but
    /// not where most of them sit — two dozen are authored at their component's origin because a
    /// slide, an animation or the guest supplies the position later, and snapping those lands them
    /// all at dead centre. An unplaced piece is real, carries its value and appears in the snapshot
    /// and the editor; it is simply not drawn until somebody says where it goes.
    /// </param>
    public readonly record struct Piece(
        string Id,
        string Art,
        Anchor Anchor,
        Vector2 Offset,
        string Value = "",
        Drive Driven = Drive.None,
        int ElementOrdinal = -1,
        bool Placed = true,
        IReadOnlySet<string>? Strip = null,
        float Depth = 0f,
        bool LiveText = false,
        Vector2 AncestorScale = default);

    /// <summary>The expression wheel's component, `Layer/wheelmenu` in `hud.bgf`.</summary>
    public const string ExpressionWheelName = "wheelmenu";

    /// <summary>The quest/info box, `Layer/TopBox` in `hud.bgf`.</summary>
    public const string QuestBoxName = "TopBox";

    /// <summary>The timing meter, `Layer/ExpressionMeter` in `hud.bgf`.</summary>
    public const string ExpressionMeterName = "ExpressionMeter";

    /// <summary>
    /// The subtrees cut whole: element name under `Layer`, id label, a fixed anchor (null snaps
    /// from the authored position) and the depth. The wheel sits in front of what it opens over.
    /// </summary>
    private static readonly (string Name, string Label, Anchor? Anchor, float Depth)[] GroupSubtrees =
    {
        (ExpressionWheelName, "ExpressionWheel", null, 0.5f),
        (QuestBoxName, "QuestBox", Anchor.TopCentre, 0f),

        // The timing meter (expressions, the job minigames). Its three pips carry no binding and
        // no slide: the guest's meter object (`*(GUI+32)+44`, started by `StartExpressionMeterPip`
        // → `sub_82307050`) writes their positions onto the elements, so they only move inside a
        // unit that follows every element beneath it.
        (ExpressionMeterName, "ExpressionMeter", null, 0.5f),
    };

    private readonly List<Piece> _pieces = new();

    public IReadOnlyList<Piece> Pieces => _pieces;

    public Engine_HudLayout Add(Piece piece)
    {
        _pieces.Add(piece);
        return this;
    }

    /// <summary>
    /// Where a piece lands in the canvas's own space, for a given viewport. The canvas is
    /// centre-origin with +Y up; a layout is authored top-left with +Y down, and the flip is here.
    /// </summary>
    public static Vector2 Place(Piece piece, Vector2 viewport)
    {
        float scale = Scale(viewport);

        // The anchor first, in screen pixels from the top-left...
        float x = (piece.Anchor.X * viewport.X) + (piece.Offset.X * scale);
        float y = (piece.Anchor.Y * viewport.Y) + (piece.Offset.Y * scale);

        // ...then into the canvas: centre-origin, +Y up.
        return new Vector2(x - (viewport.X * 0.5f), (viewport.Y * 0.5f) - y);
    }

    /// <summary>
    /// The scale a piece is drawn at — viewport height over the design height. Unclamped, unlike
    /// the achievement toast's: the HUD is the game's own furniture and tracks the display.
    /// </summary>
    public static float Scale(Vector2 viewport) =>
        viewport.Y <= 0f ? 1f : viewport.Y / ReferenceHeight;

    /// <summary>The canvas box the design is authored in — the one the canvas camera fits.</summary>
    public const float DesignWidth = 1280f;
    public const float DesignHeight = 720f;

    /// <summary>
    /// How much of the canvas is VISIBLE at a given window aspect, in canvas units — what anchors
    /// anchor to, and not the design box. `Engine_UiSceneBuilder.FitCanvasCamera` fits the whole
    /// 1280x720 box rather than cropping: wider than 16:9 pins the height and reveals extra WIDTH,
    /// narrower pins the width and reveals extra HEIGHT, so on an ultrawide the right edge is well
    /// past x=640.
    /// </summary>
    public static Vector2 CanvasExtent(Vector2 window)
    {
        if (window.X <= 0f || window.Y <= 0f)
        {
            return new Vector2(DesignWidth, DesignHeight);
        }

        // The same switch the camera makes — a disagreement insets every edge piece by the
        // difference. Height is pinned down to the authored 4:3 box (960x720), width below that.
        float aspect = window.X / window.Y;
        return aspect >= Engine_UiSceneBuilder.MinimumWidth / DesignHeight
            ? new Vector2(DesignHeight * aspect, DesignHeight)
            : new Vector2(Engine_UiSceneBuilder.MinimumWidth,
                Engine_UiSceneBuilder.MinimumWidth / aspect);
    }

    /// <summary>
    /// Derives a whole layout from `hud.bgf`'s OWN bindings — the file binds 52 meters and 18 text
    /// fields and states, per element, which value it answers to and where the artist put it.
    /// The ANCHOR is what this adds: each element's authored canvas position becomes a screen
    /// fraction, that fraction snaps to the nearest edge or centre, and the remainder is the
    /// offset. The result is the starting arrangement and is meant to be edited.
    /// </summary>
    public static Engine_HudLayout FromBindings(
        Engine_UiScreen screen, Engine_UiBindings bindings)
    {
        var layout = new Engine_HudLayout();

        // Which bound elements are ANCESTORS of another bound element.
        var bound = new HashSet<int>();
        foreach (Engine_UiBindings.Binding b in bindings.All)
        {
            bound.Add(b.ElementOrdinal);
        }

        // Containment, and what each container holds. A piece's art is its element and everything
        // under it, so a bound group holding another bound piece would draw that piece twice —
        // once live, once as the authored placeholder inside the group's clone. The group is kept
        // and the DUPLICATE removed: each container carries the names of the bound elements
        // beneath it, and the clone drops exactly those subtrees.
        var contains = new Dictionary<int, HashSet<string>>();
        foreach (Engine_UiBindings.Binding b in bindings.All)
        {
            if (b.ElementOrdinal < 0 || b.ElementOrdinal >= screen.Nodes.Length)
            {
                continue;
            }

            string name = screen.Nodes[b.ElementOrdinal].Name;
            for (UiNode? up = screen.Nodes[b.ElementOrdinal].Parent; up is not null; up = up.Parent)
            {
                if (!bound.Contains(up.Ordinal))
                {
                    continue;
                }

                if (!contains.TryGetValue(up.Ordinal, out HashSet<string>? held))
                {
                    held = new HashSet<string>(StringComparer.Ordinal);
                    contains[up.Ordinal] = held;
                }

                held.Add(name);
            }
        }

        // One anchor per SIBLING GROUP, decided before any piece is made. Anchoring per piece
        // splits any widget that straddles `Nearest`'s 0.25/0.75 thresholds — no threshold
        // survives that — and the file states the grouping: siblings under one parent are one
        // widget. Each member then keeps its own offset, which carries the authored arrangement.
        //
        // From the members' MEAN, not the parent's own position: a parent at the origin with all
        // the placement on its children is ordinary here, and would anchor a corner widget to the
        // middle of the screen.
        var groupSum = new Dictionary<int, Vector2>();
        var groupCount = new Dictionary<int, int>();
        foreach (Engine_UiBindings.Binding binding in bindings.All)
        {
            if (binding.ElementOrdinal < 0 || binding.ElementOrdinal >= screen.Nodes.Length ||
                Owned(screen, binding.ElementOrdinal))
            {
                continue;
            }

            UiNode at = screen.Nodes[binding.ElementOrdinal];
            int group = at.Parent?.Ordinal ?? -1;
            Vector2 where = Engine_UiElementResolve.CanvasPosition(at);

            groupSum[group] = groupSum.TryGetValue(group, out Vector2 sum) ? sum + where : where;
            groupCount[group] = groupCount.TryGetValue(group, out int seen) ? seen + 1 : 1;
        }

        var groupAnchor = new Dictionary<int, Anchor>();
        foreach ((int group, Vector2 sum) in groupSum)
        {
            groupAnchor[group] = SnapAnchor(sum / groupCount[group]);
        }

        foreach (Engine_UiBindings.Binding binding in bindings.All)
        {
            Drive driven = binding.Kind switch
            {
                Engine_UiBindings.Kind.Meter => Drive.Bar,
                Engine_UiBindings.Kind.Text => Drive.Text,
                Engine_UiBindings.Kind.Pulser => Drive.Pulse,

                // Slide toggles drive an element's STATE rather than a value and a rebuilt piece
                // has no slides, so they are carried as static art rather than dropped.
                _ => Drive.None,
            };

            if (binding.ElementOrdinal < 0 || binding.ElementOrdinal >= screen.Nodes.Length)
            {
                continue;
            }

            UiNode node = screen.Nodes[binding.ElementOrdinal];

            // A binding inside a widget Godot already draws is not a HUD piece:
            // Engine_UiSpellAssign draws the assignment carousel whole and only while the wheel
            // is open.
            if (Owned(screen, binding.ElementOrdinal))
            {
                continue;
            }

            Vector2 canvas = Engine_UiElementResolve.CanvasPosition(node);
            Anchor anchor = groupAnchor.TryGetValue(node.Parent?.Ordinal ?? -1, out Anchor shared)
                ? shared
                : SnapAnchor(canvas);
            Vector2 offset = OffsetFrom(anchor, canvas);

            string id =
                $"{binding.Kind.ToString().ToLowerInvariant()}.{binding.Name}.{binding.ElementName}";

            // Placed by default; the one exclusion is containment. Where a group and a child are
            // both bound the CHILD wins — it is the part the guest drives — and the group's clone
            // drops that subtree.
            contains.TryGetValue(binding.ElementOrdinal, out HashSet<string>? strip);
            const bool placed = true;
            if (Arrangement.TryGetValue(id, out (Anchor Anchor, Vector2 Offset) put))
            {
                anchor = put.Anchor;
                offset = put.Offset;
            }

            layout.Add(new Piece(
                Id: id,
                Art: binding.ElementName,
                Anchor: anchor,
                Offset: offset,
                Value: binding.Name,
                Driven: driven,
                ElementOrdinal: binding.ElementOrdinal,
                Strip: strip,
                AncestorScale: AncestorScaleOf(screen, binding.ElementOrdinal),

                // A container sits BEHIND what it holds. Every piece is placed at z 0, and canvas
                // +Z faces the camera, so a container steps back by how much it holds — more
                // bound elements beneath it means higher in the tree and further back.
                Depth: strip is null ? 0f : -0.05f * strip.Count,
                Placed: placed));
        }

        // Group pieces — widgets cut WHOLE, with no binding of their own, following the guest's
        // live state for every element beneath them through the clone binding; their nested meters
        // and pulsers scrub their own element's tracks (Engine_HudView).
        //
        //   `Layer/wheelmenu` — the expression wheel, driven from C++ on the HUD presentation
        //   (`sub_826AC730` slides "wheelmenu" 2 open / 4 reopen / 3 close, `sub_826AB1F8` writes
        //   the items' titles and textures, each item's `selected` pointer is a slide).
        //
        //   `Layer/TopBox` — the quest/info box. The guest positions its text (`sub_823FC8F0`
        //   writes TopTitle/TopMain box-relative) and sizes the slab through the TopBoxX/TopBoxY
        //   meters (`sub_823FE638`, from the measured text); its buttons ride the TopBoxX tracks
        //   and its faders are the guest's stated opacity.
        foreach ((string name, string label, Anchor? fixedAnchor, float depth) in GroupSubtrees)
        {
            for (int i = 0; i < screen.Nodes.Length; ++i)
            {
                UiNode node = screen.Nodes[i];
                if (node.Type != BgfElementType.Component || node.Name != name ||
                    node.Parent?.Name != "Layer")
                {
                    continue;
                }

                Vector2 canvas = Engine_UiElementResolve.CanvasPosition(node);
                Anchor anchor = fixedAnchor ?? SnapAnchor(canvas);
                string id = $"group.{label}.{name}";
                Vector2 offset = OffsetFrom(anchor, canvas);
                if (Arrangement.TryGetValue(id, out (Anchor Anchor, Vector2 Offset) put))
                {
                    anchor = put.Anchor;
                    offset = put.Offset;
                }

                layout.Add(new Piece(
                    Id: id,
                    Art: name,
                    Anchor: anchor,
                    Offset: offset,
                    Value: string.Empty,
                    Driven: Drive.None,
                    ElementOrdinal: node.Ordinal,
                    Strip: null,
                    AncestorScale: AncestorScaleOf(screen, node.Ordinal),
                    Depth: depth,
                    Placed: true,
                    LiveText: true));
                break;
            }
        }

        // An arrangement entry that matches nothing is a renamed binding, and would otherwise be
        // silent — the piece just would not appear, which looks like the guest not lighting it.
        var stranded = new List<string>();
        foreach (string id in Arrangement.Keys)
        {
            if (!layout._pieces.Exists(p => p.Id == id))
            {
                stranded.Add(id);
            }
        }

        if (stranded.Count != 0)
        {
            GD.Print($"[hud-layout] the shipped arrangement names {stranded.Count} piece(s) " +
                $"hud.bgf does not bind: {string.Join(", ", stranded)}");
        }

        AbsorbIntoGroups(screen, layout._pieces);
        ReportNesting(screen, layout._pieces);
        return layout;
    }

    /// <summary>
    /// Widgets inside `hud.bgf` that a Godot-owned view draws whole, by element name — both
    /// players' copies of the will assigner, which share one controller as the guest's do.
    /// </summary>
    private static readonly HashSet<string> OwnedSubtrees = new(System.StringComparer.Ordinal)
    {
        Engine_UiSpellAssign.ElementName,
        Engine_UiSpellAssign.ElementName + "2",
    };

    /// <summary>Whether an element sits inside — or is — a subtree another view owns.</summary>
    private static bool Owned(Engine_UiScreen screen, int ordinal)
    {
        if (ordinal < 0 || ordinal >= screen.Nodes.Length)
        {
            return false;
        }

        for (UiNode? at = screen.Nodes[ordinal]; at is not null; at = at.Parent)
        {
            if (OwnedSubtrees.Contains(at.Name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Anchor overrides for the pieces authored at their component's ORIGIN, whose position arrives
    /// later from a slide, an animation or the guest, and which would otherwise all snap to dead
    /// centre. An anchor here is a design decision, not a measurement. The table does not decide
    /// what is drawn — containment does — so naming a container here draws its children twice.
    /// </summary>
    private static readonly Dictionary<string, (Anchor Anchor, Vector2 Offset)> Arrangement =
        new()
        {
            // The quest banner's two lines, which the file parks together at the group origin.

            // The subtitle line, likewise — its box states a place, the text inside it does not.
            ["text.Subtitles.Text"] = (Anchor.BottomCentre, new Vector2(0f, -34f)),
        };

    /// <summary>
    /// Reports any two PLACED pieces where one element sits inside the other: nesting is invisible
    /// in the id and its symptom, a widget drawn twice slightly apart, reads as a layout problem.
    /// </summary>
    private static void ReportNesting(Engine_UiScreen screen, List<Piece> pieces)
    {
        var clashes = new List<string>();
        foreach (Piece piece in pieces)
        {
            if (!piece.Placed || piece.ElementOrdinal < 0)
            {
                continue;
            }

            foreach (Piece other in pieces)
            {
                if (!other.Placed || other.ElementOrdinal < 0 ||
                    other.ElementOrdinal == piece.ElementOrdinal)
                {
                    continue;
                }

                // Only one direction is reported, so a clash names each pair once.
                if (!IsAncestor(screen, other.ElementOrdinal, piece.ElementOrdinal))
                {
                    continue;
                }

                // Nesting is not a clash when the container stripped it: a group keeps its own art
                // and drops the subtrees of the children drawn live on top of it.
                if (other.Strip?.Contains(screen.Nodes[piece.ElementOrdinal].Name) == true)
                {
                    continue;
                }

                clashes.Add($"{other.Id} contains {piece.Id}");
            }
        }

        if (clashes.Count != 0)
        {
            GD.PushWarning($"[hud-layout] {clashes.Count} placed piece(s) NEST and will draw " +
                $"twice: {string.Join("; ", clashes)}");
        }
    }

    /// <summary>
    /// A group owns its subtree: it is placed whole and follows the guest's slides and opacity for
    /// every element beneath it through the clone binding, so a piece derived for one of its
    /// children is a second COPY — and one that never sees its parent's state. Dropped here.
    /// </summary>
    private static void AbsorbIntoGroups(Engine_UiScreen screen, List<Piece> pieces)
    {
        var absorbed = new List<string>();
        foreach (Piece group in pieces.ToArray())
        {
            if (!group.Id.StartsWith("group.", StringComparison.Ordinal) || group.ElementOrdinal < 0)
            {
                continue;
            }

            pieces.RemoveAll(piece =>
            {
                bool inside = !ReferenceEquals(piece, group) && piece.ElementOrdinal >= 0 &&
                    IsAncestor(screen, group.ElementOrdinal, piece.ElementOrdinal);
                if (inside)
                {
                    absorbed.Add($"{piece.Id} into {group.Id}");
                }

                return inside;
            });
        }

        if (absorbed.Count != 0)
        {
            GD.Print($"[hud-layout] {absorbed.Count} piece(s) absorbed by the group that owns " +
                $"their subtree: {string.Join("; ", absorbed)}");
        }
    }

    /// <summary>
    /// The scale the ancestors ABOVE a piece's element compose onto it (`HealthAndMana` is authored
    /// at 0.6); the clone keeps only the element's own basis, so the layout carries the rest.
    /// </summary>
    public static Vector2 AncestorScaleOf(Engine_UiScreen screen, int element)
    {
        var scale = Vector2.One;
        if (element < 0 || element >= screen.Nodes.Length)
        {
            return scale;
        }

        for (UiNode? up = screen.Nodes[element].Parent; up is not null; up = up.Parent)
        {
            scale *= new Vector2(up.ScaleX == 0f ? 1f : up.ScaleX, up.ScaleY == 0f ? 1f : up.ScaleY);
        }

        return scale;
    }

    /// <summary>Whether <paramref name="ancestor"/> is at or above <paramref name="element"/>.</summary>
    private static bool IsAncestor(Engine_UiScreen screen, int ancestor, int element)
    {
        if (element < 0 || element >= screen.Nodes.Length)
        {
            return false;
        }

        for (UiNode? at = screen.Nodes[element].Parent; at is not null; at = at.Parent)
        {
            if (at.Ordinal == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Where a canvas position sits as a screen fraction, top-left origin.</summary>
    private static Vector2 Fraction(Vector2 canvas) => new(
        (canvas.X + (DesignWidth * 0.5f)) / DesignWidth,
        ((DesignHeight * 0.5f) - canvas.Y) / DesignHeight);

    /// <summary>The nearest of the three anchors on one axis — 0, 0.5 or 1: an edge, or the middle.</summary>
    private static float Nearest(float fraction) =>
        fraction < 0.25f ? 0f : fraction > 0.75f ? 1f : 0.5f;

    /// <summary>The anchor a position on its own would choose.</summary>
    private static Anchor SnapAnchor(Vector2 canvas)
    {
        Vector2 f = Fraction(canvas);
        return new Anchor(Nearest(f.X), Nearest(f.Y));
    }

    /// <summary>
    /// Where a canvas position sits relative to a GIVEN anchor, in reference units — separate from
    /// choosing the anchor, because a widget's pieces share one anchor and keep their own places.
    /// </summary>
    private static Vector2 OffsetFrom(Anchor anchor, Vector2 canvas)
    {
        Vector2 f = Fraction(canvas);
        float scale = ReferenceHeight / DesignHeight;
        return new Vector2(
            (f.X - anchor.X) * DesignWidth * scale,
            (f.Y - anchor.Y) * DesignHeight * scale);
    }

    /// <summary>
    /// The shipped arrangement, as close to the original's as the anchors allow: the authored 16:9
    /// positions rebased onto anchors. A starting point, meant to be edited.
    /// </summary>
    public static Engine_HudLayout Shipped() => new Engine_HudLayout()
        .Add(new Piece("health.bar", "PlayerHealth", Anchor.TopLeft, new Vector2(96f, 64f),
            "PlayerHealth", Drive.Bar))
        .Add(new Piece("health.cap", "PlayerHealthMax", Anchor.TopLeft, new Vector2(96f, 64f),
            "PlayerHealthMax", Drive.Bar))
        .Add(new Piece("quest.title", "TitleText", Anchor.TopRight, new Vector2(-320f, 72f),
            "QuestTitle", Drive.Text))
        .Add(new Piece("quest.objective", "Text", Anchor.TopRight, new Vector2(-320f, 104f),
            "QuestObjective", Drive.Text));

    /// <summary>One line naming what a layout holds, for the run.</summary>
    public string Describe(Vector2 viewport) =>
        $"[hud-layout] {_pieces.Count} piece(s) at {viewport.X:F0}x{viewport.Y:F0} " +
        $"(scale {Scale(viewport):F2}, aspect {(viewport.Y <= 0f ? 0f : viewport.X / viewport.Y):F2})";

    /// <summary>
    /// Moves one piece and PLACES it — positioning a piece is the statement that it belongs on
    /// screen, so there is no second switch to throw. False when no piece has the id.
    /// </summary>
    public bool Reposition(string id, Anchor anchor, Vector2 offset)
    {
        for (int i = 0; i < _pieces.Count; ++i)
        {
            if (_pieces[i].Id == id)
            {
                _pieces[i] = _pieces[i] with
                {
                    Anchor = anchor, Offset = offset, Placed = true,
                };
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Applies a saved arrangement over this layout, by piece id. The file MOVES pieces, it does
    /// not define them: what a piece is — art, value, drive — is derived from `hud.bgf`'s bindings
    /// every boot. An id matching no piece is reported, since that is what a renamed binding looks
    /// like. Returns how many pieces moved; 0 with no file is ordinary, not an error.
    /// </summary>
    public int ApplyOverrides(string path)
    {
        if (!FileAccess.FileExists(path))
        {
            return 0;
        }

        using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (file is null)
        {
            return 0;
        }

        var parsed = Json.ParseString(file.GetAsText());
        if (parsed.VariantType != Variant.Type.Dictionary)
        {
            GD.PushWarning($"[hud-layout] '{path}' is not a JSON object; ignored");
            return 0;
        }

        var root = parsed.AsGodotDictionary();
        if (!root.TryGetValue("pieces", out Variant piecesVariant) ||
            piecesVariant.VariantType != Variant.Type.Dictionary)
        {
            GD.PushWarning($"[hud-layout] '{path}' has no 'pieces' object; ignored");
            return 0;
        }

        int moved = 0;
        var unknown = new List<string>();
        foreach (var entry in piecesVariant.AsGodotDictionary())
        {
            string id = entry.Key.AsString();
            if (entry.Value.VariantType != Variant.Type.Dictionary)
            {
                continue;
            }

            var record = entry.Value.AsGodotDictionary();
            if (!record.TryGetValue("anchor", out Variant anchorVariant) ||
                !record.TryGetValue("offset", out Variant offsetVariant))
            {
                continue;
            }

            var anchorArray = anchorVariant.AsGodotArray();
            var offsetArray = offsetVariant.AsGodotArray();
            if (anchorArray.Count < 2 || offsetArray.Count < 2)
            {
                continue;
            }

            var anchor = new Anchor(
                (float)anchorArray[0].AsDouble(), (float)anchorArray[1].AsDouble());
            var offset = new Vector2(
                (float)offsetArray[0].AsDouble(), (float)offsetArray[1].AsDouble());

            if (Reposition(id, anchor, offset))
            {
                ++moved;
            }
            else
            {
                unknown.Add(id);
            }
        }

        if (unknown.Count != 0)
        {
            GD.Print($"[hud-layout] '{path}' names {unknown.Count} piece(s) this build " +
                $"does not have: {string.Join(", ", unknown)}");
        }

        return moved;
    }

    /// <summary>
    /// Writes the whole arrangement, every piece, so the file survives a rederive that changes
    /// which pieces exist. The art/value/driven fields are informational —
    /// <see cref="ApplyOverrides"/> never reads them — and are there for the editor and modders.
    /// </summary>
    public bool Save(string path)
    {
        var pieces = new Godot.Collections.Dictionary();
        foreach (Piece piece in _pieces)
        {
            pieces[piece.Id] = new Godot.Collections.Dictionary
            {
                ["anchor"] = new Godot.Collections.Array { piece.Anchor.X, piece.Anchor.Y },
                ["offset"] = new Godot.Collections.Array { piece.Offset.X, piece.Offset.Y },
                ["art"] = piece.Art,
                ["value"] = piece.Value,
                ["driven"] = piece.Driven.ToString(),

                // Informational, like the three above: an anchor and an offset in this file are
                // what place a piece, so there is nothing to read back.
                ["placed"] = piece.Placed,
            };
        }

        var root = new Godot.Collections.Dictionary { ["pieces"] = pieces };

        using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (file is null)
        {
            GD.PushWarning($"[hud-layout] cannot write '{path}': {FileAccess.GetOpenError()}");
            return false;
        }

        file.StoreString(Json.Stringify(root, "\t"));
        return true;
    }
}
