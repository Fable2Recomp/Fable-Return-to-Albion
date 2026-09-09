using System.Collections.Generic;
using Godot;

using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// The rebuilt HUD, drawn by Godot from a layout, the artist's pieces and the guest's numbers.
/// <see cref="Engine_HudLayout"/> says where a piece goes, <see cref="Engine_HudArt"/> what it
/// looks like, <see cref="Engine_HudValues"/> what it reads (by name). Nothing here mirrors an
/// element tree.
///
/// Pieces are placed every frame, not once: the anchor resolves against the visible canvas,
/// which changes with the window.
/// </summary>
public sealed partial class Engine_HudView : Node3D
{
    public Engine_HudLayout Layout { get; set; } = Engine_HudLayout.Shipped();

    public Engine_HudArt Art { get; set; } = new();

    public Engine_HudValues Values { get; } = new();

    /// <summary>The shipped look, for text pieces.</summary>
    public Engine_UiTheme? Theme { get; set; }

    /// <summary>Resolves a stated texture hash to a loaded texture, for the pieces' own art.</summary>
    public System.Func<uint, Texture2D?>? ResolveTexture { get; set; }

    /// <summary>The element pipeline for `hud.bgf`, supplied by the owner each sample.</summary>
    public Engine_UiElementResolve? Resolve { get; set; }

    /// <summary>The guest's chosen texture per element, as last sampled.</summary>
    public uint[]? LiveTextures { get; set; }

    /// <summary>
    /// The guest's stated per-element transforms — the third channel the clone binding takes,
    /// beside the draw gate and the texture. Required for a meter to fill: `ApplyTransform` falls
    /// back to the authored scale, and every bar in `hud.bgf` is authored `scale.x = 0`, so null
    /// re-zeroes the bar each frame over the meter scrub.
    /// </summary>
    public IReadOnlyList<float?[]?>? LiveTransform { get; set; }

    /// <summary>
    /// The guest's live element text, by ordinal — what a piece flagged
    /// <see cref="Engine_HudLayout.Piece.LiveText"/> paints on its own text elements. The
    /// expression wheel's eight titles are written by `sub_826AB1F8` onto the elements, not
    /// through the text registry the other pieces read.
    /// </summary>
    public string?[]? LiveTexts { get; set; }

    /// <summary>
    /// The window size the anchors resolve against. Fed by the owner: this node lives inside a
    /// SubViewport whose own size is the window's, which breaks a fixed-size editor preview.
    /// </summary>
    public Vector2 Window { get; set; } = new(1280f, 720f);

    /// <summary>
    /// The guest's per-element active bits, indexed by document ordinal — the state gate. A
    /// derived piece exists only while the guest lights its source element. Null (no sample yet)
    /// hides gated pieces: "not stated" must not render as "on".
    /// </summary>
    public bool[]? ElementActive { get; set; }

    /// <summary>
    /// The guest's stated slide per element — the same list the resolve folds. A group whose
    /// current slide is authored `Off` or `TurnOff…` has been switched OFF by the guest's slide
    /// toggle (`sub_823FE848` → GotoSlide), whatever the fold reads for its opacity: the fade-out
    /// slides COMMAND opacity 100 and animate it to 0, and a rebuilt group has no timeline to
    /// finish that on, so the quest box sat at 100/50 as a dark band across the top for good.
    /// </summary>
    public IReadOnlyList<int>? LiveSlides { get; set; }

    /// <summary>The name of the slide the guest states for an element, or empty.</summary>
    private string LiveSlideName(int ordinal)
    {
        if (Screen is not { } screen || LiveSlides is null || ordinal < 0 ||
            ordinal >= LiveSlides.Count || LiveSlides[ordinal] <= 0)
        {
            return string.Empty;
        }

        // Slide 0 is the master; the guest's numbering counts the rest from one.
        int wanted = LiveSlides[ordinal];
        int seen = 0;
        foreach (BgfSlide slide in screen.Document.Slides)
        {
            if (slide.OwnerElement == ordinal && !slide.IsMaster && ++seen == wanted)
            {
                return slide.Name;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// The parsed document, for the playhead tracks. Bars, fills and slide-ins are authored
    /// animations — keyframes over position/scale/opacity — scrubbed by a meter value:
    /// t = value x 10 on the element's 0..1000-tick timeline.
    /// </summary>
    public Engine_UiScreen? Screen { get; set; }

    /// <summary>What one animation track drives on one clone node.</summary>
    private enum TrackTarget
    {
        PositionX, PositionY, ScaleX, ScaleY, RotationZ, Opacity, UvScaleV, Other,
    }

    /// <summary>One playhead track bound to a node of a piece's clone.</summary>
    /// <param name="Value">A nested meter's own handle inside a group piece; null = the piece's.</param>
    /// <param name="Pulse">Whether that nested value is a pulser phase rather than a meter.</param>
    private sealed record BoundTrack(
        BgfAnimation Animation, Node3D Node, TrackTarget Target, float Base,
        Engine_HudValues.Handle? Value = null, bool Pulse = false);

    /// <summary>The attribute ids the scrub can apply — the engine's own hash space.</summary>
    private static readonly uint PositionXId =
        Engine_GameHash.Sdbm("position.x") & Engine_GameHash.AttributeIdMask;

    private static readonly uint PositionYId =
        Engine_GameHash.Sdbm("position.y") & Engine_GameHash.AttributeIdMask;

    private static readonly uint ScaleXId =
        Engine_GameHash.Sdbm("scale.x") & Engine_GameHash.AttributeIdMask;

    private static readonly uint ScaleYId =
        Engine_GameHash.Sdbm("scale.y") & Engine_GameHash.AttributeIdMask;

    private static readonly uint OpacityId =
        Engine_GameHash.Sdbm("opacity") & Engine_GameHash.AttributeIdMask;

    // The timing meter's sweet spot is a WEDGE: ExpressionFillPos swings `fill size` ±1.222 rad
    // and ExpressionFillSize swings the two bar groups from ±1.222 toward 0 (hud.bgf owners
    // 1498/1499) — rotation tracks, which the scrub dropped as 'Other' and drew the bars
    // wherever the master left them.
    private static readonly uint RotationZId =
        Engine_GameHash.Sdbm("rotation.z") & Engine_GameHash.AttributeIdMask;

    // The wedge's bars are the arc art itself: ExpressionFillSize scrubs each bar's height
    // 0.16→1.28 AND its image's `scalev` 0.063→0.5 in step, so the quad reveals a taller
    // band of the same atlas cell. Dropped, the image stayed a 16-row sliver of nothing.
    private static readonly uint ScaleVId =
        Engine_GameHash.Sdbm("scalev") & Engine_GameHash.AttributeIdMask;

    /// <summary>One built piece, with what it needs to be driven and placed.</summary>
    private sealed class Built
    {
        public required Engine_HudLayout.Piece Piece { get; init; }
        public required Node3D Node { get; init; }

        /// <summary>The authored animation tracks this piece's meter scrubs.</summary>
        public List<BoundTrack> Tracks { get; } = new();

        /// <summary>The clone's text elements, for a piece that paints live element text.</summary>
        public List<(MeshInstance3D Node, int Ordinal)> LiveTextNodes { get; } = new();

        /// <summary>What each of those last painted, keyed by ordinal.</summary>
        public Dictionary<int, string> ShownTexts { get; } = new();

        /// <summary>
        /// The string the FILE authors for each text node, keyed by ordinal — kept so a label
        /// the guest never writes can still say what it was authored to say. See the paint
        /// loop for why this is not a general fallback.
        /// </summary>
        public Dictionary<int, string> AuthoredTexts { get; } = new();

        /// <summary>
        /// The piece's own width in canvas units at scale 1, measured once. A bar scales from its
        /// left edge while a quad is centred on its origin, so the width is what lets the node be
        /// shifted back to keep the left edge put.
        /// </summary>
        public float Width { get; init; }

        public Engine_HudValues.Handle Value { get; init; }

        /// <summary>
        /// The authored basis, kept so the canvas factor multiplies it rather than replacing it:
        /// the per-frame write must be absolute or it compounds, while keeping the piece's size.
        /// </summary>
        public Basis Authored { get; init; } = Basis.Identity;

        /// <summary>
        /// This piece's clone bound to the elements that drive it — the same join and per-frame
        /// apply the owned books use. Without it a piece shows the file's placeholder: where the
        /// game chooses art at runtime the `.bgf` holds one stand-in for the whole set and the
        /// guest states a texture per element.
        /// </summary>
        public Engine_UiCloneBinding Binding { get; } = new();

        /// <summary>The last string drawn, so glyphs are only re-laid when the text changes.</summary>
        public string Shown { get; set; } = string.Empty;
    }

    private readonly List<Built> _built = new();

    /// <summary>The last published-channel report, so it prints on change rather than once.</summary>
    private string _publishedReported = string.Empty;

    /// <summary>
    /// Builds every piece the layout names. Safe to call again — it rebuilds from scratch.
    /// </summary>
    public void Build()
    {
        foreach (Built built in _built)
        {
            built.Node.QueueFree();
        }

        _built.Clear();
        BindCentreToggle();

        var missing = new List<string>();
        int unplaced = 0;
        foreach (Engine_HudLayout.Piece piece in Layout.Pieces)
        {
            // Inventoried, not drawn: `hud.bgf` binds 110 elements and states where only some go;
            // the rest are authored at their component's origin, so building them would stack
            // pieces at dead centre.
            if (!piece.Placed)
            {
                ++unplaced;
                continue;
            }

            float scale = Engine_HudLayout.Scale(Engine_HudLayout.CanvasExtent(Window));

            Node3D? node = piece.Driven == Engine_HudLayout.Drive.Text
                ? new MeshInstance3D { Name = piece.Id.Replace('.', '_') }
                : Art.Build(piece, new Vector2(160f * scale, 16f * scale));

            if (node is null)
            {
                // Named, not counted: art in neither the banks nor a mod folder means the layout
                // refers to something that does not exist.
                missing.Add($"{piece.Id}({piece.Art})");
                continue;
            }

            AddChild(node);
            var built = new Built
            {
                Piece = piece,
                Node = node,
                Width = Measure(node),
                Value = Engine_HudValues.Handle.For(piece.Value),
                // The ancestors' scale, which the art (the element's own basis) does not carry.
                Authored = node.Transform.Basis.Scaled(new Vector3(
                    piece.AncestorScale == default ? 1f : piece.AncestorScale.X,
                    piece.AncestorScale == default ? 1f : piece.AncestorScale.Y, 1f)),
            };

            if (Screen is { } document)
            {
                built.Binding.ResolveTexture = ResolveTexture;
                built.Binding.Bind(node, document);
                if (piece.LiveText)
                {
                    built.LiveTextNodes.AddRange(built.Binding.TextNodes(document));
                    foreach ((MeshInstance3D _, int ordinal) in built.LiveTextNodes)
                    {
                        if (ordinal >= 0 && ordinal < document.Nodes.Length &&
                            document.Nodes[ordinal].Text?.Display is { Length: > 0 } authored)
                        {
                            built.AuthoredTexts[ordinal] = authored;
                        }
                    }
                }
            }

            BindTracks(built);
            _built.Add(built);
        }

        // What the containment pass kept and dropped: a container is placed with the subtrees of
        // its separately-drawn children removed.
        var housings = new List<string>();
        foreach (Built b in _built)
        {
            if (b.Piece.Strip is { Count: > 0 } held)
            {
                housings.Add($"{b.Piece.Id}-[{string.Join(',', held)}]");
            }
        }

        if (housings.Count != 0)
        {
            GD.Print($"[hud] {housings.Count} container(s) placed with their live children " +
                $"stripped: {string.Join("; ", housings)}");
        }

        int tracks = 0;
        int scrubbed = 0;
        foreach (Built built in _built)
        {
            tracks += built.Tracks.Count;
            if (built.Tracks.Count > 0)
            {
                ++scrubbed;
            }
        }

        GD.Print($"[hud] built {_built.Count}/{Layout.Pieces.Count} piece(s); {Art.Describe()}; " +
            $"{scrubbed} meter piece(s) scrub {tracks} authored track(s)" +
            (unplaced == 0
                ? string.Empty
                : $"; {unplaced} bound piece(s) inventoried but UNPLACED — " +
                  $"see {Engine_HudLayout.SnapshotPath} to position them") +
            (missing.Count == 0 ? string.Empty : $"; NO ART: {string.Join(", ", missing)}"));
    }

    /// <summary>A built piece's width in canvas units, from whatever it actually became.</summary>
    private static float Measure(Node3D node) =>
        node is MeshInstance3D { Mesh: not null } mesh ? mesh.GetAabb().Size.X : 0f;

    /// <summary>
    /// Binds a meter piece's authored animation tracks to the nodes of its clone. A track lives on
    /// an element of the piece's subtree and targets position, scale or opacity. The track for the
    /// piece's root element carries the authored base of its animated axis: the layout owns where
    /// the piece sits, so the scrub applies the delta from the authored parked value.
    /// </summary>
    /// <summary>
    /// The element `screencentertoggle.lua` moves, and how far. `setCenterOn` (`main.1` pc4..21)
    /// reads `centerXPos` off the Behavior element and writes it to its parent's `position.x`,
    /// remembering the old x; `setCenterOff` (`main.2`) restores it. `SetDPadCenter(bool)` raises
    /// those two events. The layout places the parent absolutely every frame, so the latch is a
    /// conditional offset: the authored-to-centre delta in canvas units, resolved at build.
    /// </summary>
    private int _centreMover = -1;

    private float _centreDelta;

    private void BindCentreToggle()
    {
        _centreMover = -1;
        _centreDelta = 0f;
        if (Screen is not { } screen)
        {
            return;
        }

        foreach (UiNode node in screen.Nodes)
        {
            if (node.Type != BgfElementType.Behavior || node.Name != "CenterToggle" ||
                node.Parent is not { } mover ||
                !node.Element.TryGetAttribute("centerXPos", out BgfAttribute centre))
            {
                continue;
            }

            _centreMover = mover.Ordinal;
            _centreDelta = centre.AsFloat() - mover.PositionX;
            GD.Print($"[hud] CenterToggle on '{mover.Name}' (ord {mover.Ordinal}): " +
                $"centred x {centre.AsFloat()} = authored {mover.PositionX} {_centreDelta:+0.#;-0.#}");
            return;
        }
    }

    private Engine_UiBindings? _bindings;

    /// <summary>The HUD document's name bindings, read once — the fold of stated toggles needs them.</summary>
    public Engine_UiBindings? Bindings
    {
        get
        {
            if (_bindings is null && Screen is { } screen)
            {
                _bindings = Engine_UiBindings.Read(screen.Document);
            }

            return _bindings;
        }
    }

    /// <summary>
    /// The timing meter's pip: which anchor element carries it and its x offset in canvas units,
    /// or null while no pip travels. See <see cref="Engine_UiHudExpressionMeter"/>.
    /// </summary>
    public (string Anchor, float Offset)? ExpressionPip { get; set; }

    /// <summary>The meter's colour band while it is on (0 low, 1 medium, 2 high), else -1.</summary>
    public int ExpressionBand { get; set; } = -1;

    private const string PipRestMeta = "pip_rest_x";

    /// <summary>The arc's radius about the meter group's origin, in canvas units — measured off the authored arc art (halves 160 wide, ends at ±65°).</summary>
    private const float PipRadius = 165f;

    /// <summary>
    /// Shows the travelling pip on its anchor: the anchor's own art, authored at opacity 0, is
    /// lit and moved by the guest's offset; the other two anchors stay dark. Applied after the
    /// clone binding, which writes the guest's stated opacity 0 back every frame.
    /// </summary>
    private void DrivePip(Built built)
    {
        // The sweet-spot bars take the band's colour: white art, tinted as the guest tints
        // it (`SetTintingAmountAndDiffuseColour` at amount 1).
        if (ExpressionBand >= 0)
        {
            Color band = ExpressionBand switch { 0 => new Color(0f, 1f, 0f), 1 => new Color(1f, 1f, 0f), _ => new Color(1f, 0f, 0f) };
            foreach (string bar in new[] { "barleft", "barright" })
            {
                if (Engine_UiTheme.FindByName(built.Node, bar) is Node3D barNode)
                {
                    TintSubtree(barNode, band);
                }
            }
        }

        foreach (string name in new[]
            {
                Engine_UiHudExpressionMeter.HeroPip, Engine_UiHudExpressionMeter.HenchmanPip,
                Engine_UiHudExpressionMeter.BothPip,
            })
        {
            if (Engine_UiTheme.FindByName(built.Node, name) is not Node3D anchor)
            {
                continue;
            }

            bool lit = ExpressionPip is { } pip && pip.Anchor == name;
            if (!anchor.HasMeta(PipRestMeta))
            {
                anchor.SetMeta(PipRestMeta, anchor.Position.X);
            }

            // THE OFFSET IS AN ANGLE. `sub_823D0F10` computes (pos·0.01·130 − 65) and multiplies
            // it by flt_82100ED4 = 0.017453 (π/180) before `sub_82B4FC60` builds the rotation:
            // the pip swings ±65° about the anchor, which sits at the arc's centre — the arc
            // art is a circle of radius ~165 canvas units about the group origin, its ends at
            // ±65°. The anchor rides that circle and tilts with it.
            float rest = (float)anchor.GetMeta(PipRestMeta);
            if (lit)
            {
                float degrees = ExpressionPip!.Value.Offset;
                float theta = Mathf.DegToRad(degrees);
                anchor.Position = new Vector3(
                    rest + PipRadius * Mathf.Sin(theta), PipRadius * Mathf.Cos(theta), anchor.Position.Z);
                anchor.Rotation = new Vector3(0f, 0f, -theta);
            }
            else
            {
                anchor.Position = new Vector3(rest, 0f, anchor.Position.Z);
                anchor.Rotation = Vector3.Zero;
            }

            LightSubtree(anchor, lit ? 1f : 0f);
        }
    }

    // ================= THE MODERN PANELS =====================================================
    //
    // The shipped HUD backs its text with black-tinted slabs (`Material655`, diffuse 0/0/0,
    // TintingAmount 1 over the `hud` blur image) — the TopBox's 780-unit bar, the counters'
    // boxes. The remaster replaces them with rounded translucent panels fitted to the text the
    // guest actually states, refit every frame, so a longer objective gets a longer panel.
    // The authored slab nodes are hidden AFTER the clone binding, which re-shows what the
    // guest's state says is drawn.

    private readonly Dictionary<string, Engine_UiRoundedPanel> _panels = new();

    /// <summary>Each group piece's placed rect, so an off-screen widget names itself.</summary>
    private readonly Dictionary<string, string> _groupSpans = new();

    private string _groupReport = string.Empty;

    // The TopBox slab: the Bar and the blur/edge pieces authored beside it.
    private static readonly string[] SlabNames = { "Bar", "blur", "left", "right", "top", "bottom" };

    private void DrawModernPanels()
    {
        var used = new HashSet<string>();

        // THE TARGETED-NPC WIDGET IS FOUR SEPARATE COMPONENTS — the name, the opinion box and
        // its slider, the mood icons and the button — each authored at its own place in
        // `hud.bgf`, which is why it reads as scattered furniture rather than one widget. It is
        // gathered across the loop and given ONE rounded panel, the way the quest box and the
        // counters are, so it wears the same look as the rest of the HUD.
        Rect2? queryBounds = null;
        float queryBehind = 1.5f;
        _queryPieces = 0;
        _queryParts.Clear();
        _queryCandidates.Clear();
        _queryTitle = null;

        Rect2? counterBounds = null;
        float counterBehind = 1.5f;

        // WHERE EVERY GROUP ACTUALLY LANDS. The health readout has been reported off-screen
        // three times and guessed at three times — from its authored position, from its anchor
        // and offset, and from the saved arrangement — and each guess said it should be on
        // screen. What was never observable is the rect it OCCUPIES once the anchor, the offset,
        // the ancestor scale and its children's own transforms have all composed. That is the
        // number that decides it, so print it. Change-keyed, so it settles to silence.
        foreach (Built built in _built)
        {
            if (!built.Piece.Id.StartsWith("group.", System.StringComparison.Ordinal))
            {
                continue;
            }

            float groupBehind = 1.5f;
            Rect2? span = built.Node.Visible ? SubtreeBounds(built.Node, ref groupBehind) : null;
            _groupSpans[built.Piece.Id] = span is { } g
                ? $"{built.Piece.Id} @({built.Piece.Offset.X:0},{built.Piece.Offset.Y:0}) " +
                  $"anchor({built.Piece.Anchor.X:0.0},{built.Piece.Anchor.Y:0.0}) " +
                  $"rect {g.Position.X:0},{g.Position.Y:0} {g.Size.X:0}x{g.Size.Y:0}"
                : $"{built.Piece.Id} NOT DRAWN";
        }

        string groups = string.Join("  |  ", _groupSpans.Values);
        if (groups != _groupReport)
        {
            _groupReport = groups;
            GD.Print($"[hud-view] groups: {groups}");
        }

        foreach (Built built in _built)
        {
            if (!built.Node.Visible)
            {
                continue;
            }

            if (built.Piece.Id == "group.QuestBox.TopBox")
            {
                HideSlabs(built.Node);
                Rect2? bounds = null;
                foreach ((MeshInstance3D label, int _) in built.LiveTextNodes)
                {
                    Engine_UiHudTextStyle.Capture(label);
                    bounds = Union(bounds, TextBounds(label));
                }

                float behind = 1.5f;
                foreach ((MeshInstance3D label, int _) in built.LiveTextNodes)
                {
                    if (GodotObject.IsInstanceValid(label))
                    {
                        behind = System.Math.Min(behind, label.GlobalPosition.Z - 0.5f);
                    }
                }

                FitPanel("topbox", bounds, used, behind);
            }
            else if (built.Piece.Id.StartsWith("slidetoggle.Counter", System.StringComparison.Ordinal) ||
                built.Piece.Id.EndsWith(".CornerBox", System.StringComparison.Ordinal) ||
                built.Piece.Id.StartsWith("meter.CornerX.", System.StringComparison.Ordinal) ||
                built.Piece.Id.StartsWith("meter.CornerY.", System.StringComparison.Ordinal) ||
                built.Piece.Id.StartsWith("meter.SubtitlesX.", System.StringComparison.Ordinal) ||
                built.Piece.Id.StartsWith("meter.SubtitlesY.", System.StringComparison.Ordinal) ||
                built.Piece.Id == "slidefader2.Subtitles.Subtitles")
            {
                // The counters' strings sit in the CornerBox — a black nine-slice (`Group`#616:
                // BoxMiddle, corners, sides) sized by the CornerX/CornerY meters — and the
                // counter containers carry the flash overlay; all of it is the slab look, and
                // the strings are separate text pieces, so these pieces go whole.
                built.Node.Visible = false;
            }
            else if (built.Piece.Id.StartsWith("text.Counter", System.StringComparison.Ordinal) &&
                built.Node is MeshInstance3D counter)
            {
                // ONE BOX FOR ALL THE COUNTERS. Each line used to fit its OWN panel, keyed by
                // its piece id, so a job showing three of them — gold until promotion, the
                // multiplier, the current gold — drew three rounded panels stacked a line apart
                // and overlapping at the edges. They are one readout and they get one box, the
                // way the quest box holds its title and both body lines.
                if (TextBounds(counter) is { } line)
                {
                    counterBounds = Union(counterBounds, line);
                    counterBehind = System.Math.Min(counterBehind, counter.GlobalPosition.Z - 0.5f);
                }
            }
            else if (built.Piece.Id == "text.Subtitles.Text" && built.Node is MeshInstance3D label)
            {
                // NOT grouped with the counters: the subtitle sits elsewhere on the screen and
                // is a different readout, so it keeps its own box.
                FitPanel(built.Piece.Id, TextBounds(label), used, label.GlobalPosition.Z - 0.5f);
            }
            else if (built.Piece.Id.Contains(".TargetedQuery", System.StringComparison.Ordinal))
            {
                // AND THE SHIPPED SLAB GOES, exactly as the quest box's does. The citizen
                // readout authors its own black band — `BoxAndBar/TopBox` with the same
                // `Bar`/`blur`/`left`/`right`/`top` children, spanning x -641.7..+641.7 at
                // opacity 70 — which is a rule across the whole screen, not a panel. Replacing
                // it with the rounded panel is the entire point of `Engine_UiRoundedPanel`;
                // leaving it drew both.
                //
                // This also retires the reason the distance heuristic below exists: the union
                // used to come out 1269 units wide because the band was in it. With the band
                // hidden and the widget cut as one group, the subtree's own extent is the
                // answer and nothing has to be guessed from proximity.
                HideSlabs(built.Node);

                // THE PROMPTS SIT UNDER THE HEART, at the bar's right end. Our layout; only x
                // moves, since their y already places them below the bar.
                //
                // (The `.bgf` happens to put them at x 386. That is the old renderer's layout
                // and is not a constraint — we take ART and STRINGS from that file and place
                // things ourselves. This belongs in the layout template once the HUD editor
                // covers this widget; it is here only until then.)
                CentreUnderBar(built.Node);

                // ONE TEXT STYLE. The name is a title, the button's label is body — the same cut
                // the display box makes on a path ending "Title". The style is metadata the
                // re-mesh reads, so the shown text is dropped to force one repaint through it;
                // after that the piece repaints only when the guest changes the string.
                foreach ((MeshInstance3D text, int ordinal) in built.LiveTextNodes)
                {
                    if (!GodotObject.IsInstanceValid(text) || text.HasMeta(StyledMeta))
                    {
                        continue;
                    }

                    // THE ELEMENT DECIDES, not only the piece. While this widget was cut per
                    // binding the name had a piece of its own with "Title" in its id; cut whole
                    // there is one id for the lot, so the name would have taken body style with
                    // everything else. The element is called `TitleText` either way.
                    bool isTitle =
                        built.Piece.Id.Contains("Title", System.StringComparison.OrdinalIgnoreCase) ||
                        text.Name.ToString().Contains("Title", System.StringComparison.OrdinalIgnoreCase);
                    text.SetMeta(StyledMeta, true);
                    text.SetMeta("text_size",
                        isTitle ? Engine_UiHudTextStyle.TitleSize : Engine_UiHudTextStyle.BodySize);
                    text.SetMeta("text_colour",
                        isTitle ? Engine_UiHudTextStyle.TitleColour : Engine_UiHudTextStyle.BodyColour);
                    built.ShownTexts.Remove(ordinal);
                }

                // THE WIDGET IS UP ONLY WHILE THE GUEST NAMES SOMEONE.
                //
                // Nothing else here is a reliable "is it up" signal: the gift, divorce and
                // marry icons ride registry 9 and the slider rides registry 10, neither of
                // which this host states at all yet, so those elements stay drawn whether or
                // not a citizen is targeted — and a panel fitted to them sits on screen for
                // ever with nothing in it.
                //
                // The guest states it OUTRIGHT, so ask that rather than infer. The record's
                // `+0` byte is the readout's raise flag — `sub_823FD8E8` stores 1, `sub_823FD9B0`
                // stores 0 — and `Engine_UiHudTargetedQuery` now states it on this very channel.
                //
                // The previous gate read the shown TITLE and was wrong in a way that only a run
                // shows: `ShownTexts` is what we last PAINTED, and a re-text is skipped when the
                // guest states an empty string, so the last citizen's name stayed in that map
                // for ever and the panel never came down.
                if (Values.Visible(Engine_HudValues.Handle.For("TargetedQuery")) is not true)
                {
                    continue;
                }

                // The gauge and the icons are KEPT: they carry the opinion, which is the whole
                // point of the widget. Only the backing is replaced, and the panel is fitted to
                // everything so the parts read as one thing.
                ++_queryPieces;
                Rect2? one = SubtreeBounds(built.Node, ref queryBehind);
                _queryParts.Add(one is { } span
                    ? $"{built.Piece.Id} [{span.Position.X:0}..{span.Position.X + span.Size.X:0}]"
                    : $"{built.Piece.Id} [none]");

                // THE TITLE ANCHORS THE WIDGET, and everything that belongs to it sits near the
                // title. Unioning blind gave a rectangle 1269 units wide — the full design box —
                // which is the black band across the top of the screen rather than a panel.
                // Excluding oversized meshes was not enough: the span came from two NARROW
                // pieces at opposite edges, so the outlier has to be rejected by DISTANCE, not
                // by size. The name is the one part whose place is certain, so it is the anchor
                // and anything beyond a widget's width of it is not part of this widget.
                if (built.Piece.Id.Contains(".TargetedQueryTitle.", System.StringComparison.Ordinal))
                {
                    _queryTitle = one ?? _queryTitle;
                }

                _queryCandidates.Add(one);
            }
        }

        if (counterBounds is { } counters)
        {
            FitPanel("counters", counters.Grow(8f), used, counterBehind);
        }

        // Gathered against the title once every piece has been measured, because the title is
        // not necessarily the first of them.
        if (_queryTitle is { } anchor)
        {
            float centre = anchor.Position.X + (anchor.Size.X * 0.5f);
            foreach (Rect2? candidate in _queryCandidates)
            {
                if (candidate is not { } part)
                {
                    continue;
                }

                float partCentre = part.Position.X + (part.Size.X * 0.5f);
                if (System.Math.Abs(partCentre - centre) <= QueryGatherReach)
                {
                    queryBounds = Union(queryBounds, part);
                }
            }
        }
        else
        {
            foreach (Rect2? candidate in _queryCandidates)
            {
                queryBounds = Union(queryBounds, candidate);
            }
        }

        if (queryBounds is { } query)
        {
            // A little air, so the name and the gauge are not flush against the edge. The
            // quest box gets the same from the text bounds it fits to.
            FitPanel("targetedquery", query.Grow(8f), used, queryBehind);
        }

        // SAY WHAT THE PANEL DID. Three attempts at this widget have gone out without anyone
        // being able to see whether the panel was fitted, to what, or at what depth — the piece
        // ids and the bounds are the two things that decide it, and neither was observable.
        // Printed only when the answer changes, so it costs nothing once it settles.
        string report = queryBounds is { } r
            ? $"{_queryPieces} piece(s), rect {r.Position.X:0}, {r.Position.Y:0} {r.Size.X:0}x{r.Size.Y:0} " +
              $"at z {queryBehind:0.0} | {string.Join("  ", _queryParts)}"
            : $"{_queryPieces} piece(s), NO BOUNDS — every mesh was rejected as a container or none was drawn";
        if (_queryPieces != 0 && report != _queryReport)
        {
            _queryReport = report;
            GD.Print($"[hud-targeted] {report}");
        }

        foreach ((string key, Engine_UiRoundedPanel panel) in _panels)
        {
            if (!used.Contains(key))
            {
                panel.Visible = false;
            }
        }
    }

    /// <summary>A visible, non-empty label's bounds in the HUD's space, or null.</summary>
    private static Rect2? TextBounds(MeshInstance3D label)
    {
        if (!GodotObject.IsInstanceValid(label) || label.Mesh is null || !label.IsVisibleInTree())
        {
            return null;
        }

        // An unset uniform reads back as nil, which is the shader's default 1 — a freshly
        // painted label has never had its opacity written and is fully visible.
        if (label.MaterialOverride is ShaderMaterial m &&
            m.GetShaderParameter("opacity") is { VariantType: Variant.Type.Float } op &&
            op.AsSingle() < 0.05f)
        {
            return null;
        }

        Aabb box = label.GlobalTransform * label.Mesh.GetAabb();
        if (box.Size.X <= 0f || box.Size.Y <= 0f)
        {
            return null;
        }

        return new Rect2(box.Position.X, box.Position.Y, box.Size.X, box.Size.Y);
    }

    private static Rect2? Union(Rect2? a, Rect2? b) =>
        a is null ? b : b is null ? a : a.Value.Merge(b.Value);

    private void FitPanel(string key, Rect2? bounds, HashSet<string> used, float z)
    {
        if (bounds is not { } rect)
        {
            return;
        }

        if (!_panels.TryGetValue(key, out Engine_UiRoundedPanel? panel))
        {
            panel = new Engine_UiRoundedPanel { Name = "panel_" + key.Replace('.', '_') };
            AddChild(panel);
            _panels[key] = panel;
        }

        // Just behind the text it backs; the world sits far below every HUD depth.
        panel.Fit(rect, z);
        used.Add(key);
    }

    /// <summary>Marks a label already given the HUD text style, so it is styled once.</summary>
    private const string StyledMeta = "hud_text_styled";

    private int _queryPieces;
    private string _queryReport = string.Empty;

    /// <summary>Each targeted-widget piece and its horizontal span, so the outlier can be named.</summary>
    private readonly List<string> _queryParts = new();

    private readonly List<Rect2?> _queryCandidates = new();

    private Rect2? _queryTitle;

    /// <summary>
    /// How far from the name a part may sit and still belong to the targeted widget, in canvas
    /// units. The authored parts run from the mood icons out to the button, a little over four
    /// hundred either side; half the design box is the generous bound that still rejects an
    /// element parked at the screen edge.
    /// </summary>
    private const float QueryGatherReach = Engine_HudLayout.DesignWidth * 0.5f;

    /// <summary>
    /// The screen bounds of every drawn mesh under a node, and the nearest depth among them —
    /// <see cref="TextBounds"/> answers for one label, and this widget is a subtree of art.
    /// </summary>
    private static Rect2? SubtreeBounds(Node node, ref float behind)
    {
        Rect2? bounds = null;
        if (node is MeshInstance3D mesh && mesh.Mesh is not null && mesh.IsVisibleInTree())
        {
            Aabb box = mesh.GlobalTransform * mesh.Mesh.GetAabb();

            // CONTENT ONLY. A group's own root carries a quad the size of the canvas — it is the
            // container, not a drawn thing — and unioning it fitted the panel to the whole
            // screen, which is the full-width band across the top rather than a widget. Nothing
            // in this family is legitimately wider than half the design box: the opinion bar,
            // the widest part, is a little over a quarter of it.
            bool container = box.Size.X > Engine_HudLayout.DesignWidth * 0.5f ||
                box.Size.Y > Engine_HudLayout.DesignHeight * 0.5f;

            if (box.Size.X > 0f && box.Size.Y > 0f && !container)
            {
                bounds = new Rect2(box.Position.X, box.Position.Y, box.Size.X, box.Size.Y);
                behind = System.Math.Min(behind, mesh.GlobalPosition.Z - 0.5f);
            }
        }

        foreach (Node child in node.GetChildren())
        {
            bounds = Union(bounds, SubtreeBounds(child, ref behind));
        }

        return bounds;
    }

    /// <summary>The prompt groups the targeted readout hangs off to its right.</summary>
    private static readonly string[] PromptNames = { "LeftButton", "RightButton" };

    /// <summary>
    /// Where the prompts sit, in canvas units from the readout's centre — under the heart at the
    /// bar's right end. The mood icons are authored at x 285 inside `grp` (scale 0.5) inside
    /// `LoveSlider` (scale 1.5), i.e. 285 x 0.75 = 214 canvas units out, and the heart rides
    /// that same end. OUR number: the file hangs the prompts at 386 with everything else on 0,
    /// and this is where they belong instead. Template material once the editor reaches here.
    /// </summary>
    private const float PromptX = 214f;

    /// <summary>
    /// How far BELOW their authored height the prompts sit, in canvas units. Ours, like
    /// <see cref="PromptX"/>: the file stacks the readout tightly, and with the prompts pulled
    /// onto the centre line the "More Info" row ended up crowding the opinion bar and the heart
    /// it now sits under. Canvas +y is up, so this subtracts.
    /// </summary>
    private const float PromptDrop = 16f;

    /// <summary>Latches a prompt's authored height, so the drop is applied once and not per frame.</summary>
    private const string PromptBaseMeta = "hud_prompt_base_y";

    /// <summary>
    /// Brings the targeted readout's button prompts onto the widget's centre line, so they sit
    /// under the opinion bar rather than beside it. Only x moves; the authored y already places
    /// them below the bar, and the two prompts are authored at different heights so they stack
    /// rather than collide on the rare frame both are up.
    /// </summary>
    private static void CentreUnderBar(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is Node3D prompt &&
                System.Array.IndexOf(PromptNames, child.Name.ToString()) >= 0)
            {
                // FROM THE AUTHORED Y, NOT THE CURRENT ONE. This runs every frame, so
                // subtracting from `Position.Y` would drop the prompt another 16 units each
                // time and slide it off the bottom of the screen within a second. The authored
                // height is latched once, the first time this node is seen.
                if (!prompt.HasMeta(PromptBaseMeta))
                {
                    prompt.SetMeta(PromptBaseMeta, prompt.Position.Y);
                }

                float baseY = (float)prompt.GetMeta(PromptBaseMeta).AsDouble();
                prompt.Position = new Vector3(PromptX, baseY - PromptDrop, prompt.Position.Z);
                continue;
            }

            CentreUnderBar(child);
        }
    }

    private static void HideSlabs(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is Node3D spatial && System.Array.IndexOf(SlabNames, child.Name.ToString()) >= 0)
            {
                spatial.Visible = false;
                continue;
            }

            HideSlabs(child);
        }
    }

    private static void TintSubtree(Node node, Color colour)
    {
        if (node is MeshInstance3D mesh)
        {
            Engine_UiSceneBuilder.SetQuadTint(mesh, colour, 1f);
        }

        foreach (Node child in node.GetChildren())
        {
            TintSubtree(child, colour);
        }
    }

    private static void LightSubtree(Node node, float opacity)
    {
        // The anchors are authored at opacity 0, so the clone binding's draw gate hides every
        // node under them each frame; the lit anchor is re-shown here, after the binding.
        if (node is Node3D spatial)
        {
            spatial.Visible = opacity > 0f;
        }

        if (node is MeshInstance3D mesh)
        {
            Engine_UiSceneBuilder.SetQuadOpacity(mesh, opacity);
        }

        foreach (Node child in node.GetChildren())
        {
            LightSubtree(child, opacity);
        }
    }

    private void BindTracks(Built built)
    {
        if (Screen is not { } screen || built.Piece.ElementOrdinal < 0)
        {
            return;
        }

        // A group piece carries its nested meters and pulsers: each scrubs the tracks its own
        // element's slides activate, against its own published value.
        if (built.Piece.Id.StartsWith("group.", System.StringComparison.Ordinal))
        {
            _bindings ??= Engine_UiBindings.Read(screen.Document);
            foreach (Engine_UiBindings.Binding binding in _bindings.All)
            {
                bool pulse = binding.Kind == Engine_UiBindings.Kind.Pulser;

                // A LAYOUT METER MUST NOT DRAG A GROUP WE PLACED. `TopBoxY` is the quest box's
                // measured height, and inside `PlayerHealth` it owns `Health`'s position.y
                // track (0 -> -136) — so the health readout slid up and down by ~90 units as
                // the quest box grew and shrank, which the diagnostic caught as its rect y
                // wandering 202 / 220 / 294 between samples.
                //
                // That coupling is the ORIGINAL's layout decision, and layout is ours: we place
                // this widget, so nothing else may move it. `Engine_UiLiveValues` already keeps
                // this same list for the same reason.
                if (Engine_UiBindings.IsLayoutMeter(binding.Name))
                {
                    continue;
                }

                if ((binding.Kind != Engine_UiBindings.Kind.Meter && !pulse) ||
                    binding.ElementOrdinal == built.Piece.ElementOrdinal ||
                    !InSubtree(screen, binding.ElementOrdinal, built.Piece.ElementOrdinal))
                {
                    continue;
                }

                BindOwnedTracks(built, screen, binding.ElementOrdinal,
                    Engine_HudValues.Handle.For(binding.Name), pulse);
            }

            return;
        }

        if (built.Piece.Driven != Engine_HudLayout.Drive.Bar &&
            built.Piece.Driven != Engine_HudLayout.Drive.Pulse)
        {
            return;
        }

        BindOwnedTracks(built, screen, built.Piece.ElementOrdinal, null, false);
    }

    /// <summary>
    /// Binds the tracks activated by the slides an element owns, on the nodes of the piece's
    /// clone, scrubbed by <paramref name="value"/> (null: the piece's own value).
    /// </summary>
    private void BindOwnedTracks(
        Built built, Engine_UiScreen screen, int element, Engine_HudValues.Handle? value, bool pulse)
    {

        // Only the tracks this element's own slides activate: a meter scrubs its element's
        // timeline, while a nested fader's tracks (`ExpAmounts`, `TopBox`) belong to that fader
        // and are driven by the guest's stated opacity through the clone binding.
        var owned = new HashSet<int>();
        foreach (BgfSlide slide in screen.Document.Slides)
        {
            if (slide.OwnerElement != element)
            {
                continue;
            }

            foreach (BgfSlideEntry entry in slide.Entries)
            {
                foreach (int index in entry.ActivatedAnimations ?? System.Array.Empty<int>())
                {
                    if (index >= 0)
                    {
                        owned.Add(index);
                    }
                }
            }
        }

        BgfAnimation[] animations = screen.Document.Animations;
        for (int index = 0; index < animations.Length; ++index)
        {
            BgfAnimation animation = animations[index];
            if (!animation.ActiveOnLoad || animation.Keys.Length == 0 ||
                !InSubtree(screen, animation.ElementOrdinal, element) ||
                (owned.Count > 0 && !owned.Contains(index)))
            {
                continue;
            }

            TrackTarget target =
                animation.AttributeId == PositionXId ? TrackTarget.PositionX
                : animation.AttributeId == PositionYId ? TrackTarget.PositionY
                : animation.AttributeId == ScaleXId ? TrackTarget.ScaleX
                : animation.AttributeId == ScaleYId ? TrackTarget.ScaleY
                : animation.AttributeId == OpacityId ? TrackTarget.Opacity
                : animation.AttributeId == RotationZId ? TrackTarget.RotationZ
                : animation.AttributeId == ScaleVId ? TrackTarget.UvScaleV
                : TrackTarget.Other;
            if (target == TrackTarget.Other)
            {
                continue;
            }

            // BY ORDINAL through the clone binding, which joined every node of the clone to its
            // element: a name is ambiguous inside a widget — the timing meter's sweet-spot
            // rotation track targets a `Group` UNDER `fill size`, and the by-name search found
            // the meter's outer `Group` first and turned the whole arc.
            bool isRoot = animation.ElementOrdinal == built.Piece.ElementOrdinal;
            Node3D? node = isRoot
                ? built.Node
                : built.Binding.NodeFor(animation.ElementOrdinal) ??
                    Engine_UiTheme.FindByName(
                        built.Node, screen.Nodes[animation.ElementOrdinal].Name) as Node3D;

            // An image element folds into its owner's quad and has no node of its own — the
            // wedge's `scalev` track targets `expressionmeter` UNDER `barleft`, and the bar
            // is the mesh. A UV track climbs to the nearest bound ancestor.
            if (node is null && target == TrackTarget.UvScaleV)
            {
                for (UiNode? up = screen.Nodes[animation.ElementOrdinal].Parent;
                    up is not null && node is null; up = up.Parent)
                {
                    node = built.Binding.NodeFor(up.Ordinal);
                }
            }

            if (node is null)
            {
                continue;
            }

            UiNode doc = screen.Nodes[animation.ElementOrdinal];
            float baseline = target switch
            {
                TrackTarget.PositionX => isRoot ? doc.PositionX : 0f,
                TrackTarget.PositionY => isRoot ? doc.PositionY : 0f,
                _ => 0f,
            };

            built.Tracks.Add(new BoundTrack(animation, node, target, baseline, value, pulse));
        }
    }

    /// <summary>
    /// Scrubs a group piece's nested meters and pulsers, each track at its own value's
    /// playhead. An unpublished value leaves its tracks as authored.
    /// </summary>
    private void ScrubNested(Built built, Vector2 at)
    {
        foreach (BoundTrack track in built.Tracks)
        {
            if (track.Value is not { } handle)
            {
                continue;
            }

            float? t = track.Pulse
                ? Values.Pulser(handle) is { } phase ? Mathf.Clamp(phase, 0f, 1f) * 1000f : null
                : Values.Meter(handle) is { } value ? Mathf.Clamp(value, 0f, 100f) * 10f : null;
            if (t is { } time)
            {
                ApplyTrack(built, track, at, time);
            }
        }
    }

    /// <summary>Whether an element sits at or under a piece's root element.</summary>
    private static bool InSubtree(Engine_UiScreen screen, int element, int root)
    {
        for (UiNode? at = element >= 0 && element < screen.Nodes.Length
				? screen.Nodes[element] : null;
			at is not null; at = at.Parent)
        {
            if (at.Ordinal == root)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The live HUD, for the debug channel.</summary>
    public static Engine_HudView? Current { get; private set; }

    /// <summary>
    /// Every built piece whose id contains <paramref name="match"/>: where it is, how big, whether
    /// it is drawn, and what text it shows — the readout for "small and misaligned".
    /// </summary>
    public List<Dictionary<string, object?>> Describe(string match)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (Built built in _built)
        {
            if (match.Length != 0 &&
                !built.Piece.Id.Contains(match, System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!GodotObject.IsInstanceValid(built.Node))
            {
                continue;
            }

            Aabb? bounds = null;
            foreach ((MeshInstance3D label, int _) in built.LiveTextNodes)
            {
                if (GodotObject.IsInstanceValid(label) && label.Mesh is not null && label.IsVisibleInTree())
                {
                    Aabb box = label.GlobalTransform * label.Mesh.GetAabb();
                    bounds = bounds is null ? box : bounds.Value.Merge(box);
                }
            }

            var texts = new List<string>();
            foreach ((int _, string text) in built.ShownTexts)
            {
                texts.Add(text);
            }

            rows.Add(new Dictionary<string, object?>
            {
                ["id"] = built.Piece.Id,
                ["visible"] = built.Node.IsVisibleInTree(),
                ["position"] = new[] { built.Node.GlobalPosition.X, built.Node.GlobalPosition.Y, built.Node.GlobalPosition.Z },
                ["scale"] = new[] { built.Node.Scale.X, built.Node.Scale.Y },
                ["textBounds"] = bounds is { } b ? new[] { b.Position.X, b.Position.Y, b.Size.X, b.Size.Y } : null,
                ["texts"] = texts,
                ["children"] = built.Node.GetChildCount(),
            });
        }

        return rows;
    }

    public override void _Process(double delta)
    {
        Current = this;
    	using var _framePhase = Refii.Core.Engine_FramePhases.Measure("hudview");
        if (_built.Count == 0)
        {
            return;
        }

        Values.Sample((float)delta);

        // Nothing before the first publish: an empty registry means "the hero has not spawned",
        // not "everything is at zero".
        Visible = Values.Live;
        if (!Visible)
        {
            return;
        }

        Vector2 extent = Engine_HudLayout.CanvasExtent(Window);
        float scale = Engine_HudLayout.Scale(extent);

        // Counted over this pass; reset here rather than beside the report, which would zero them
        // before they are read.
        _hiddenByState = 0;
        _hiddenByValue = 0;
        _hiddenByDrive = 0;
        _why.Clear();
        _culprits.Clear();

        foreach (Built built in _built)
        {
            // The slide toggle first, for a group: its authored `Off`/`TurnOff…` slide is the
            // guest's statement that the box is down, and the fold cannot say so on its own.
            // A GROUP IS DOWN WHEN ITS ROOT RESOLVES DARK, not when its slide is NAMED Off: the
            // timing meter's root carries the FlashExpressionMeter toggle, whose slide 1 is
            // 'Off' and only stops the flash — the meter's own visibility is the frame's toggle
            // beneath it. The name test hid the meter for every job. The fold and the played-
            // slide release make the root's opacity say what the slide meant.
            if (built.Piece.ElementOrdinal >= 0 &&
                built.Piece.Id.StartsWith("group.", System.StringComparison.Ordinal) &&
                Resolve is { } rootResolve && built.Piece.ElementOrdinal < rootResolve.Opacity.Length &&
                rootResolve.Opacity[built.Piece.ElementOrdinal] <= 0.001f)
            {
                built.Node.Visible = false;
                ++_hiddenByState;
                Note(built, $"element {built.Piece.ElementOrdinal} resolves dark on slide " +
                    $"'{LiveSlideName(built.Piece.ElementOrdinal)}'");
                continue;
            }

            // The state gate first: the guest says whether this piece exists right now, and an
            // off piece takes no drive at all.
            if (built.Piece.ElementOrdinal >= 0 &&
                (ElementActive is not { } active ||
                 built.Piece.ElementOrdinal >= active.Length ||
                 !active[built.Piece.ElementOrdinal]))
            {
                built.Node.Visible = false;
                ++_hiddenByState;

                // The numbers that decided it, not just the count: the terms behind `active`
                // separate a parked HUD from a broken resolve.
                Note(built, Resolve is { } r &&
                        built.Piece.ElementOrdinal < r.Drawn.Length
                    ? $"element {built.Piece.ElementOrdinal} INACTIVE " +
                        $"(drawn={r.Drawn[built.Piece.ElementOrdinal]} " +
                        $"opacity={r.Opacity[built.Piece.ElementOrdinal]:0.00})" +
                        Culprit(built.Piece.ElementOrdinal, r)
                    : $"element {built.Piece.ElementOrdinal} INACTIVE (no resolve)");
                continue;
            }

            // No second gate on a channel that is not read. The GUI registries are MeterName 1,
            // SlideFaderName 2, SlideFader2Name 3, PulserName 4, TextName 6, SlideToggleName 9,
            // and `walk_gui_registry` runs for 1 and 6 only, so toggle/fader/pulser names are
            // unreadable here. The guest applies those as slides on the element (`sub_822A9C60`
            // resolves the name to its bound elements, `sub_828421F8`/`sub_823FE848` push the
            // slide), whose commands carry the active and opacity the state gate above reads.

            built.Node.Visible = true;

            // The piece's own parts follow the guest per element — which page of a widget is up,
            // what has been ghosted, and which picture each slot carries.
            if (Resolve is { } resolved)
            {
                built.Binding.Apply(resolved, LiveTextures, LiveTransform);
            }

            // By piece ID: the flash container for the same element is also Art 'ExpressionMeter',
            // and lighting the pip in that hollow clone put a white dot at the screen's centre.
            if (built.Piece.Id == "group.ExpressionMeter.ExpressionMeter")
            {
                DrivePip(built);
            }

            // The guest's own strings on the wheel's titles, applied only when they change: it
            // restates them every frame it holds the wheel.
            if (built.LiveTextNodes.Count != 0 && LiveTexts is { } texts && Theme is not null)
            {
                foreach ((MeshInstance3D label, int ordinal) in built.LiveTextNodes)
                {
                    string live = ordinal >= 0 && ordinal < texts.Length
                        ? texts[ordinal] ?? string.Empty
                        : string.Empty;

                    // A LABEL THE GUEST NEVER WRITES KEEPS THE ONE THE FILE GAVE IT. Not a
                    // general rule — a field the guest deliberately CLEARS must go blank, and
                    // falling back everywhere would put authoring placeholders on screen as if
                    // they were data, which is worse than empty.
                    //
                    // Narrowed to the names the binary proves carry no text. `TargetedQueryButton`
                    // is hashed in exactly two places — its initialiser and one publish, and that
                    // publish is `sub_823FE848` at 0x823fd71c, a registry-9 BOOL for visibility.
                    // No text writer exists for it anywhere in the image, while `hud.bgf` binds
                    // the name to TWO text elements which author `GUI_DIALOG_MORE_INFO` and
                    // `GUI_SCREEN_GIFT_ACCEPT`. The guest shows those; we were blanking them.
                    if (live.Length == 0 &&
                        built.Piece.Id.Contains(".TargetedQueryButton.", System.StringComparison.Ordinal) &&
                        built.AuthoredTexts.TryGetValue(ordinal, out string? authoredText))
                    {
                        live = authoredText;
                    }
                    if (built.ShownTexts.TryGetValue(ordinal, out string? shown) && shown == live)
                    {
                        continue;
                    }

                    built.ShownTexts[ordinal] = live;
                    if (GodotObject.IsInstanceValid(label))
                    {
                        Theme.SetMeshText(label, live);
                    }
                }
            }

            Vector2 at = Engine_HudLayout.Place(built.Piece, extent);

            // The guest's own move of a piece root. A layout offset derives from the authored
            // canvas position, but the guest restates a root's position (quest text, the health
            // bar's drop). The clone binding applies stated transforms only beneath a root, so
            // the difference is added here.
            at += StatedDelta(built) * scale;

            // `SetDPadCenter(true)` — the whole cluster under the mover shifts by the authored
            // delta; every piece cut from that subtree shifts with it. See BindCentreToggle.
            if (_centreMover >= 0 && Values.DpadCentered == true && Screen is { } doc &&
                built.Piece.ElementOrdinal >= 0 &&
                InSubtree(doc, built.Piece.ElementOrdinal, _centreMover))
            {
                at.X += _centreDelta * scale;
            }

            // The canvas factor multiplies the authored size, never replaces it. Composed from
            // the remembered basis rather than accumulated onto the live one, so the write stays
            // absolute across frames.
            built.Node.Transform = new Transform3D(
                built.Authored.Scaled(new Vector3(scale, scale, 1f)),
                built.Node.Transform.Origin);

            switch (built.Piece.Driven)
            {
                // The playhead, where the file authored one: a meter value scrubs the element
                // timeline (t = value x 10) and the piece's tracks say what moves. The left-edge
                // bar scaling below serves only meter pieces with no applicable tracks.
                case Engine_HudLayout.Drive.Bar when built.Tracks.Count > 0:
                    built.Node.Position = new Vector3(at.X, at.Y, built.Piece.Depth);
                    ScrubTracks(built, at);
                    break;

                // The pulser loops the timeline: the guest advances the phase and publishes it
                // (registry 4); the tick is `phase × 1000`. No phase means it is not pulsing —
                // the art stays as authored rather than hiding.
                case Engine_HudLayout.Drive.Pulse:
                    built.Node.Position = new Vector3(at.X, at.Y, built.Piece.Depth);
                    if (built.Tracks.Count > 0 && Values.Pulser(built.Value) is { } phase)
                    {
                        ScrubAt(built, at, Mathf.Clamp(phase, 0f, 1f) * 1000f);
                    }

                    break;

                case Engine_HudLayout.Drive.Bar:
                    DriveBar(built, at, scale);
                    break;

                case Engine_HudLayout.Drive.Text:
                    built.Node.Position = new Vector3(at.X, at.Y, built.Piece.Depth);
                    DriveText(built);
                    break;

                default:
                    built.Node.Position = new Vector3(at.X, at.Y, built.Piece.Depth);
                    if (built.Tracks.Count > 0)
                    {
                        ScrubNested(built, at);
                    }

                    break;
            }

            // The drive hides too: `ScrubTracks` and `DriveBar` return early on a meter the guest
            // has never published, after both gates above have passed.
            if (!built.Node.Visible)
            {
                ++_hiddenByDrive;
                Note(built, $"the drive found no value for '{built.Piece.Value}'");
            }
        }

        // Which channels the guest has published, re-reported when the set changes — keyed on the
        // change so a HUD that is merely waiting does not read as broken.
        var wanted = new List<Engine_HudValues.Handle>();
        foreach (Built built in _built)
        {
            if (built.Piece.Value.Length != 0)
            {
                wanted.Add(built.Value);
            }

            // AND THE CHANNELS NESTED INSIDE A GROUP. A group piece is built with an EMPTY
            // value — the names it drives belong to the meters inside it — so every nested
            // channel was invisible to this report. That covers the hold rings on the top box
            // (`RightTopButton`, `LeftTopButton`) and the box's own extent meters, which is
            // exactly the set you need when a hold prompt will not fill: without them, a guest
            // that never publishes and a guest publishing zero look identical, because the
            // meter read returns 0 rather than nothing for a name the presentation registered.
            foreach (BoundTrack track in built.Tracks)
            {
                if (track.Value is { } nested && !wanted.Contains(nested))
                {
                    wanted.Add(nested);
                }
            }
        }

        string published = Values.Describe(wanted);
        if (published != _publishedReported)
        {
            _publishedReported = published;
            GD.Print($"{Layout.Describe(extent)}; {published}");
        }

        DrawModernPanels();

        // Who is actually on screen, named by id on change.
        var visible = new List<string>();
        foreach (Built built in _built)
        {
            if (built.Node.Visible)
            {
                visible.Add(built.Piece.Id);
            }
        }

        string showing = string.Join("  ", visible);
        if (showing != _visibleReported)
        {
            _visibleReported = showing;

            // Why the rest are not: the state gate is the guest's answer, the value gate is
            // host-side, and the count alone cannot tell a parked element from a broken join.
            GD.Print($"[hud] visible ({visible.Count} of {_built.Count}): {showing}");
            GD.Print($"[hud] hidden — {_hiddenByState} by the guest's element state, " +
                $"{_hiddenByValue} by an unpublished channel before the drive (should be 0), " +
                $"{_hiddenByDrive} by the drive itself; " +
                $"{(_why.Count < WhyCap ? "all" : $"first {WhyCap}")} {_why.Count}: " +
                string.Join("  ", _why));

            // The same findings grouped by culprit element rather than per piece.
            if (_culprits.Count > 0)
            {
                var parts = new List<string>();
                foreach ((string where, int count) in _culprits)
                {
                    parts.Add($"{where} x{count}");
                }

                parts.Sort();
                GD.Print($"[hud] dark at: {string.Join("  ", parts)}");
            }
        }
    }

    /// <summary>Why the hidden pieces are hidden, counted over the last pass.</summary>
    private int _hiddenByState;

    private int _hiddenByValue;

    private int _hiddenByDrive;

    /// <summary>A sample of hidden pieces with the reason, capped.</summary>
    private readonly List<string> _why = new();

    /// <summary>How many dark pieces each zeroing element accounts for, over the last pass;
    /// uncapped, unlike <see cref="_why"/>.</summary>
    private readonly Dictionary<string, int> _culprits = new();

    private void Note(Built built, string reason)
    {
        if (_why.Count < WhyCap)
        {
            _why.Add($"{built.Piece.Id}: {reason}");
        }
    }

    /// <summary>How many hidden pieces the report names before it stops.</summary>
    private const int WhyCap = 24;

    /// <summary>
    /// The element that put this piece in the dark, and how. Opacity accumulates down the tree,
    /// so the piece's own number rarely decided anything: this names the first ancestor whose own
    /// term is zero, and which channel set it — a guest-stated zero is followed, an authored zero
    /// is the scene's parked state.
    /// </summary>
    private string Culprit(int ordinal, Engine_UiElementResolve resolve)
    {
        if (Screen is not { } screen || ordinal < 0 || ordinal >= screen.Nodes.Length ||
            resolve.Opacity[ordinal] > 0.01f)
        {
            return string.Empty;
        }

        for (UiNode? at = screen.Nodes[ordinal]; at is not null; at = at.Parent)
        {
            int up = at.Ordinal;
            if (up >= resolve.Own.Length || resolve.Own[up] > 0.01f)
            {
                continue;
            }

            string how = resolve.Stated[up] ? "the guest STATED it" : "AUTHORED/slid";
            string where = $"'{at.Name}'#{up} ({how})";
            _culprits[where] = _culprits.TryGetValue(where, out int seen) ? seen + 1 : 1;
            return $" — dark at {where}";
        }

        return " — dark with no element at zero (a parent chain we did not resolve)";
    }

    private string _visibleReported = string.Empty;

    /// <summary>
    /// Scrubs a piece's authored tracks to its meter's playhead. An unpublished channel hides the
    /// piece; a published value maps to t = value x 10 and each track evaluates the engine's
    /// bezier at that time. Root-element position tracks apply as deltas from the authored parked
    /// value (the layout owns the anchor); child tracks apply local values verbatim. Opacity is
    /// authored in percent and scaled by 0.01 at apply.
    /// </summary>
    private void ScrubTracks(Built built, Vector2 at)
    {
        if (Values.Meter(built.Value) is not { } value)
        {
            built.Node.Visible = false;
            return;
        }

        ScrubAt(built, at, Mathf.Clamp(value, 0f, 100f) * 10f);
    }

    /// <summary>Writes every bound track of a piece at one tick of its element timeline.</summary>
    private static void ScrubAt(Built built, Vector2 at, float t)
    {
        foreach (BoundTrack track in built.Tracks)
        {
            if (track.Value is null)
            {
                ApplyTrack(built, track, at, t);
            }
        }
    }

    /// <summary>Writes one bound track at one tick.</summary>
    private static void ApplyTrack(Built built, BoundTrack track, Vector2 at, float t)
    {
        {
            if (!GodotObject.IsInstanceValid(track.Node))
            {
                return;
            }

            float evaluated = track.Animation.Evaluate(t);
            bool isRoot = ReferenceEquals(track.Node, built.Node);
            switch (track.Target)
            {
                case TrackTarget.PositionX:
                    track.Node.Position = track.Node.Position with
                    {
                        X = isRoot ? at.X + (evaluated - track.Base) : evaluated,
                    };
                    break;

                case TrackTarget.PositionY:
                    track.Node.Position = track.Node.Position with
                    {
                        Y = isRoot ? at.Y + (evaluated - track.Base) : evaluated,
                    };
                    break;

                // Through the basis column, never `.Scale`: every meter fill in `hud.bgf` is
                // authored `scale.x = 0`, and Godot's scale getter answers (0,0,0) for all three
                // axes on a singular basis. See Engine_UiBasisScale.
                case TrackTarget.ScaleX:
                    Engine_UiBasisScale.SetX(track.Node, evaluated);
                    break;

                case TrackTarget.ScaleY:
                    Engine_UiBasisScale.SetY(track.Node, evaluated);
                    break;

                case TrackTarget.RotationZ:
                    // Radians, sign as authored — Engine_UiSceneBuilder.BuildTransform keeps
                    // rotation.z's sign in its Euler basis. The Rotation setter keeps the scale.
                    track.Node.Rotation = track.Node.Rotation with { Z = evaluated };
                    break;

                case TrackTarget.Opacity:
                    SetOpacity(track.Node, evaluated * 0.01f);
                    break;

                case TrackTarget.UvScaleV:
                    SetUvScaleV(track.Node, evaluated);
                    break;
            }
        }
    }

    /// <summary>Writes a scrubbed vertical UV extent onto a quad, keeping the builder's
    /// half-texel inset (Engine_UiSceneBuilder.InsetAxis) so the band's edges do not bleed.</summary>
    private static void SetUvScaleV(Node node, float extent)
    {
        if (node is not MeshInstance3D mesh || mesh.MaterialOverride is not ShaderMaterial material)
        {
            return;
        }

        // The strip is ANCHORED at its far edge: the bar's authored rect is v [0.939, 0.998]
        // for `scalev` 0.063 (the atlas's bottom rows, empty), and the guest's scrub to 0.5
        // grows it upward into the arc art. Hold the far edge, move the near one.
        Vector2 scale = material.GetShaderParameter("uv1_scale").AsVector2();
        Vector2 offset = material.GetShaderParameter("uv1_offset").AsVector2();
        float inset = material.GetShaderParameter("diffuse_tex").As<Texture2D>() is { } tex
            ? 1f / Mathf.Max(tex.GetHeight(), 1)
            : 0f;
        float far = offset.Y + scale.Y;
        float span = Mathf.Max(extent - inset, 0f);
        material.SetShaderParameter("uv1_offset", new Vector2(offset.X, far - span));
        material.SetShaderParameter("uv1_scale", new Vector2(scale.X, span));
    }

    /// <summary>Writes a scrubbed opacity onto a node's own quads — materials are per-piece
    /// owned copies, so this cannot fade a sibling or the pattern book.</summary>
    private static void SetOpacity(Node node, float alpha)
    {
        if (node is MeshInstance3D mesh && mesh.MaterialOverride is ShaderMaterial material)
        {
            material.SetShaderParameter("opacity", alpha);
        }

        foreach (Node child in node.GetChildren())
        {
            SetOpacity(child, alpha);
        }
    }

    /// <summary>
    /// Scales a bar by its meter, keeping the left edge where the layout put it: a quad is
    /// centred on its origin, so the node shifts by half the width removed.
    /// </summary>
    private void DriveBar(Built built, Vector2 at, float scale)
    {
        // Unpublished is not empty: a never-written meter hides the bar, a meter written as zero
        // draws an empty one.
        if (Values.Fraction(built.Value) is not { } fraction)
        {
            built.Node.Visible = false;
            return;
        }

        built.Node.Visible = true;
        built.Node.Scale = new Vector3(scale * fraction, scale, 1f);

        float full = built.Width * scale;
        built.Node.Position = new Vector3(
            at.X - (full * (1f - fraction) * 0.5f), at.Y, built.Piece.Depth);
    }

    /// <summary>Lays a text piece out in the shipped font, only when the string changes.</summary>
    /// <summary>
    /// How far the guest has moved a piece's element from its authored canvas position, in
    /// canvas units — zero when it has stated nothing. Composed through the parent chain
    /// exactly as the authored position is, so a stated local position lands where the file's
    /// hierarchy puts it.
    /// </summary>
    private Vector2 StatedDelta(Built built)
    {
        int ordinal = built.Piece.ElementOrdinal;
        if (Screen is not { } doc || ordinal < 0 || ordinal >= doc.Nodes.Length ||
            LiveTransform is null || ordinal >= LiveTransform.Count ||
            LiveTransform[ordinal] is not { } stated || stated.Length < 2 ||
            (stated[0] is null && stated[1] is null))
        {
            return Vector2.Zero;
        }

        UiNode node = doc.Nodes[ordinal];
        var at = new Vector2(stated[0] ?? node.PositionX, stated[1] ?? node.PositionY);
        for (UiNode? up = node.Parent; up is not null; up = up.Parent)
        {
            at = new Vector2(up.PositionX + (up.ScaleX * at.X), up.PositionY + (up.ScaleY * at.Y));
        }

        return at - Engine_UiElementResolve.CanvasPosition(node);
    }

    private void DriveText(Built built)
    {
        if (built.Node is not MeshInstance3D label || Theme is null)
        {
            return;
        }

        string live = Values.Text(built.Value) ?? string.Empty;
        if (live == built.Shown)
        {
            return;
        }

        built.Shown = live;
        if (live.Length == 0)
        {
            label.Mesh = null;
            label.MaterialOverride = null;
            return;
        }

        // The `.fac` record states the alignment and colour, and the guest positions the block's
        // left edge (`x = P.x - (px + 0.5) * w`).
        UiText? record = Screen is { } doc && built.Piece.ElementOrdinal >= 0 &&
            built.Piece.ElementOrdinal < doc.Nodes.Length
                ? doc.Nodes[built.Piece.ElementOrdinal].Text
                : null;
        var colour = record is not null
            ? new Color(record.R, record.G, record.B, 1f)
            : new Color(1f, 224f / 255f, 128f / 255f, 1f);
        Theme.Paint(
            label, live, record?.Size is > 0 and var size ? size : TextSize, colour,
            horizontalAlign: record?.HorzAlign ?? 0, verticalAlign: record?.VertAlign ?? 1,
            boxWidth: 0f, boxHeight: 0f, wrapMode: 0);
    }

    /// <summary>Point size for HUD text, at the layout's reference height.</summary>
    public const int TextSize = 18;
}
