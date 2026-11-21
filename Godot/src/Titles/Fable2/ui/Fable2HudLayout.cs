namespace Refii.Titles.Fable2;

using System.Collections.Generic;
using Godot;
using Refii.Ui;
using static Refii.Ui.Engine_HudLayout;

/// <summary>
/// Fable 2's `hud.bgf` to the shared HUD layout and view (<see cref="Engine_HudTitleLayout"/>): the
/// widgets it cuts whole, its shipped placements, its own-reader widgets as pieces, the view's
/// default layout, and the seat rules of its one presentation carrying both players.
/// </summary>
public sealed partial class Fable2HudLayout : Engine_HudTitleLayout
{
    /// <summary>The one instance <see cref="Fable2Title.HudLayout"/> answers.</summary>
    public static readonly Fable2HudLayout Instance = new();

    private Fable2HudLayout()
    {
    }

    public override IReadOnlyList<GroupSubtree> Groups => GroupSubtrees;

    public override IReadOnlyDictionary<string, (Anchor Anchor, Vector2 Offset)> Arrangement => Placements;

    public override bool AddsWidgets => true;

    /// <summary>The expression wheel's component, `Layer/wheelmenu` in `hud.bgf`.</summary>
    public const string ExpressionWheelName = "wheelmenu";

    /// <summary>The timing meter, `Layer/ExpressionMeter` in `hud.bgf`.</summary>
    public const string ExpressionMeterName = "ExpressionMeter";

    /// <summary>
    /// The subtrees cut whole: element name under `Layer`, id label, a fixed anchor (null snaps
    /// from the authored position) and the depth. The wheel sits in front of what it opens over.
    /// </summary>
    /// <remarks>
    /// `Layer/wheelmenu` — the expression wheel, driven from C++ on the HUD presentation
    /// (`sub_826AC730` slides "wheelmenu" 2 open / 4 reopen / 3 close, `sub_826AB1F8` writes
    /// the items' titles and textures, each item's `selected` pointer is a slide).
    /// </remarks>
    private static readonly GroupSubtree[] GroupSubtrees =
    {
        new(ExpressionWheelName, "ExpressionWheel", null, 0.5f),
        new(QuestBoxName, "QuestBox", Anchor.TopCentre, 0f),

        // The timing meter (expressions, the job minigames). Its three pips carry no binding and
        // no slide: the guest's meter object (`*(GUI+32)+44`, started by `StartExpressionMeterPip`
        // → `sub_82307050`) writes their positions onto the elements, so they only move inside a
        // unit that follows every element beneath it.
        new(ExpressionMeterName, "ExpressionMeter", null, 0.5f),

        // The job chain multiplier (`Layer/JobMultiplier`), a whole unit with no binding: its
        // root's slide (1 from off, 2 up, 3 down, 4/5 off) and `Group.Text` (`<gold_img>%d`) are
        // what `SetJobMultiplierLevel` / `TurnJobMulitplierOff` state (GameUi.StateJobMultiplier).
        new("JobMultiplier", "JobMultiplier", null, 0.5f),

        // THE HEALTH READOUT IS ONE WIDGET, and cutting it per binding is what detached it.
        // `Layer/UpperElements/PlayerHealth` holds five bound elements — the `TopBox` toggle on
        // the root, `TopBoxY` on `HealthAndMana`, `PlayerHealth` on `Bar`, `PlayerHealthMax` on
        // `BarMax` and `PlayerLowHealthPulser` on the orb — and a piece per binding strips the
        // bound descendants out of each clone. The orb (`Icon/Component/Base`), the heart
        // (`Icon/Heart`) and both bars then land in DIFFERENT clones, so a transform authored on
        // a shared ancestor reaches only whichever clone happens to contain that ancestor:
        // `TopBoxY` drove `Health`'s `position.y` 0 -> -136 track onto the clone holding the
        // heart alone, and nothing else moved with it.
        //
        // Cut whole, the nested meters bind to their own values and scrub at their own
        // playheads inside one clone, so the orb, heart, fill and cap compose the way the art
        // composes them and the group drops together when the quest box grows. The per-binding
        // pieces underneath are dropped by `AbsorbIntoGroups`, which owns exactly this case.
        //
        // The anchor is SNAPPED, not fixed: the authored position is the guest's statement of
        // where this belongs (x -369, or -512 through the widescreen adapter; y 295), and the
        // wheel and the meters already take theirs the same way.
        // PLACED, NOT SNAPPED. Snapping from the authored position put the widget's drawn
        // rect at canvas x -684 against a left edge of about -633 — 51 units past it, which is
        // the sliver that kept appearing. The composition between a group's origin and where
        // its children actually land is not something to re-derive: the `[hud-view] groups:`
        // line reports the rect, and these offsets are set from it.
        //
        // OUR NUMBERS, not the file's. They belong in the layout template with the rest, and
        // the saved arrangement overrides them anyway once one exists — these are the defaults
        // a fresh install gets.
        new("PlayerHealth", "PlayerHealth", Anchor.TopLeft, 0f),
        new("CoopHealth", "CoopHealth", Anchor.TopRight, 0f),

        // THE TARGETED-CITIZEN READOUT, for the same reason. `Layer/TargetedQuery` is one
        // component holding the name, the opinion slider, the mood-face pairs and the
        // gift/marry/divorce icons, and it carries NINE bindings — so per-binding pieces cut it
        // into nine independently anchored fragments and the opinion bar was stretched across
        // the whole screen instead of keeping the art's own geometry. The bar's authored reach
        // is `pip.x` -225..+224 inside `grp` at scale 0.5 under `LoveSlider` at 1.5 — a slider
        // about a seventh of the canvas wide, not a full-width rule.
        //
        // The wide dark band behind it IS authored and correct: `BoxAndBar/TopBox` spans
        // x -641.7..+641.7 at opacity 70. That one is the guest's, not ours.
        new("TargetedQuery", "TargetedQuery", Anchor.TopCentre, 0f),

        // The spell-charge meter, one per player, placed whole. `Layer/SpellCharging` is authored
        // at rot.z -1.5708 x 0.8, so its five pips (`Group/Spell5..Spell1`, 80 apart on the
        // group's x) stand as a vertical column at the left edge, and each `Icon`/`highlight`
        // counter-rotates +1.5708 to stay upright. Per-binding pieces would place each `Spell{n}`
        // twice (meter and slideset) and lose the column's rotation and its `Group` fade.
        //
        // The guest drives it only through element state: `sub_822C10A8` plays the root's
        // `PlayerSpellCharger` slides (2 on, 3 off-fast), `sub_825E9B98` sets each pip's slide
        // (2 assigned, 1 empty), `sub_825E9D58` marks the completed level (4 newest, 3 earlier),
        // `sub_825E9EA0` sets each `Icon` to the spell on that level, and the fill is the meter
        // `PlayerSpellcharge{n}`. `SpellCharging2` is the henchman's, mirrored at +390.
        new("SpellCharging", "SpellCharging", null, 0f),
        new("SpellCharging2", "SpellCharging2", null, 0f),

        // The LT look-at prompt, `Layer/LookAt`: icon, text and glow with NO binding, so it is cut
        // whole. The guest drives it through element state alone — `sub_82595F98` writes the
        // prompt onto `Text` (`sub_82425DD0`) and plays slide 2/4/5 (8 off) through
        // `sub_82596270`.
        new("LookAt", "LookAt", null, 0f),
    };

    /// <summary>
    /// Whose piece this is. `hud.bgf` is ONE presentation carrying both players: `PlayerHealth`
    /// and `SpellCharging` are player one's, `CoopHealth` and `SpellCharging2` (the henchman's
    /// column, mirrored at +390) player two's, and the guest drives each pair from its own
    /// player. Everything else is the screen's — the quest box, subtitles, prompts, the d-pad —
    /// and answers <see cref="Engine_UiSeats.Seat.Whole"/>.
    /// </summary>
    public override Engine_UiSeats.Seat OwnerOf(Piece piece) =>
        piece.Id.StartsWith("group.CoopHealth.", System.StringComparison.Ordinal) ||
        piece.Id.StartsWith("group.SpellCharging2.", System.StringComparison.Ordinal)
            ? Engine_UiSeats.Seat.PlayerTwo
            : piece.Id.StartsWith("group.PlayerHealth.", System.StringComparison.Ordinal) ||
              piece.Id.StartsWith("group.SpellCharging.", System.StringComparison.Ordinal)
                ? Engine_UiSeats.Seat.PlayerOne
                : Engine_UiSeats.Seat.Whole;

    /// <summary>
    /// The screen's pieces each half draws from its OWN values on split screen: the experience
    /// counter, the targeted-citizen readout and the d-pad, which the guest keeps for the local
    /// player and the host states for player two over the shared feed (Engine_HudValues.Under,
    /// GameUi's player-two resolve).
    /// </summary>
    public override bool EachPlayerOnSplit(Piece piece) =>
        piece.Id == "slidefader2.ExpAmounts.ExperienceAmounts" ||
        piece.Id == "meter.TopBoxY.ExperienceAmounts" ||
        piece.Id == "text.GenericExpText.GeneralExperience" ||
        piece.Id == "text.StrengthExpText.StrengthExperience" ||
        piece.Id == "text.SkillExpText.SkillExperience" ||
        piece.Id == "text.WillExpText.WillExperience" ||
        piece.Id.StartsWith("group.TargetedQuery.", System.StringComparison.Ordinal) ||
        IsDpad(piece);

    /// <summary>
    /// The d-pad: its four slots (`dpad.dpad.dpadmenu.group.{up,left,down,right}`, toggled by
    /// `DPadUp`..`DPadRight`) and the component and ring they sit in (`dpad`, `dpadmenu`), which
    /// ride the subtitles' `SubtitlesY` and `Subtitles`.
    /// </summary>
    public override bool IsDpad(Piece piece) =>
        piece.Id.StartsWith("slidetoggle.DPad", System.StringComparison.Ordinal) ||
        piece.Id == "meter.SubtitlesY.dpad" ||
        piece.Id == "meter.SubtitlesY.dpadmenu" ||
        piece.Id == "slidetoggle.Subtitles.dpad";

    /// <summary>
    /// The expression wheel — one widget, opened by either player (the guest keeps its owner at
    /// wheel+24, stated by `sub_826A9A60`), so on split screen it stands in its owner's half.
    /// </summary>
    public override bool IsExpressionWheel(Piece piece) =>
        piece.Art == ExpressionWheelName ||
        piece.Id.StartsWith("group.ExpressionWheel.", System.StringComparison.Ordinal);

    /// <summary>
    /// The screen's pieces both players read on split screen: the quest/info box (tutorials,
    /// quest offers, the co-op notices) and the subtitles. They speak of the one shared world, and
    /// each half shows them from the same feed. The targeted villager, the experience gains and the
    /// d-pad are each player's own (<see cref="EachPlayerOnSplit"/>); the rest of the screen's
    /// pieces are the guest's for the local player only.
    /// </summary>
    public override bool SharedOnSplit(Piece piece) =>
        piece.Id == "group.QuestBox.TopBox" ||
        piece.Id == "text.Subtitles.Text" ||
        piece.Id == "slidefader2.Subtitles.Subtitles";

    /// <summary>
    /// The timing meter, whose owner changes with each use: one meter, three pips — the hero's,
    /// the henchman's and both (`sub_823D0E10`) — so it belongs to whichever player armed it.
    /// </summary>
    public override bool IsExpressionMeter(Piece piece) =>
        piece.Art == ExpressionMeterName ||
        piece.Id.StartsWith("group.ExpressionMeter.", System.StringComparison.Ordinal);

    /// <summary>
    /// Player one's widgets and the guest's copies of them that state player two: `CoopHealth` is
    /// `PlayerHealth` (orb, heart, bar and cap) and `SpellCharging2` (the henchman's column) is
    /// `SpellCharging`, each laid out for the right of the shared screen and driven from hero two.
    /// </summary>
    private static readonly (string One, string Two, string OnePrefix, string TwoPrefix)[] PlayerTwoSources =
    {
        ("PlayerHealth", "CoopHealth", "group.PlayerHealth.", "group.CoopHealth."),
        ("SpellCharging", "SpellCharging2", "group.SpellCharging.", "group.SpellCharging2."),
    };

    // Answered per piece per frame by the player-two view. The prefixes are built once and the
    // answers memoised: the ids are the layout's finite set, and a string per call here was
    // measured at 1.7 MB per 30 s of play. Concurrent because the tests run in parallel.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> PlayerTwoIds =
        new(System.StringComparer.Ordinal);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> PlayerOneIds =
        new(System.StringComparer.Ordinal);

    /// <summary>
    /// The id of the piece that states player two's version of player one's piece
    /// <paramref name="id"/>, or null. A view drawn for player two draws player one's piece and
    /// takes its state and values from this one, which is not drawn there.
    /// </summary>
    public override string? PlayerTwoSourceId(string id)
    {
        if (PlayerTwoIds.TryGetValue(id, out string? known))
        {
            return known;
        }

        string? answer = null;
        foreach ((string one, string two, string onePrefix, _) in PlayerTwoSources)
        {
            if (id.StartsWith(onePrefix, System.StringComparison.Ordinal))
            {
                answer = $"group.{two}.{id[onePrefix.Length..].Replace(one, two, System.StringComparison.Ordinal)}";
                break;
            }
        }

        PlayerTwoIds[id] = answer;
        return answer;
    }

    /// <summary>The id of player one's piece that player two's source <paramref name="id"/> keys, or null.</summary>
    public override string? PlayerOneIdOf(string id)
    {
        if (PlayerOneIds.TryGetValue(id, out string? known))
        {
            return known;
        }

        string? answer = null;
        foreach ((string one, string two, _, string twoPrefix) in PlayerTwoSources)
        {
            if (id.StartsWith(twoPrefix, System.StringComparison.Ordinal))
            {
                answer = $"group.{one}.{id[twoPrefix.Length..].Replace(two, one, System.StringComparison.Ordinal)}";
                break;
            }
        }

        PlayerOneIds[id] = answer;
        return answer;
    }

    /// <summary>
    /// Anchor overrides for the pieces authored at their component's ORIGIN, whose position arrives
    /// later from a slide, an animation or the guest, and which would otherwise all snap to dead
    /// centre. An anchor here is a design decision, not a measurement. The table does not decide
    /// what is drawn — containment does — so naming a container here draws its children twice.
    /// </summary>
    private static readonly Dictionary<string, (Anchor Anchor, Vector2 Offset)> Placements =
        new()
        {
            // The health readouts, placed from their measured rect rather than the authored
            // position (see the note beside them in GroupSubtrees). The offsets include the
            // widescreen adapter's delta — player (-143, 0), co-op (+133, -6) — because
            // `Engine_HudView.StatedDelta` measures guest moves from the wide position, not the 4:3 one.
            ["group.PlayerHealth.PlayerHealth"] = (Anchor.TopLeft, new Vector2(69f, 65f)),
            ["group.CoopHealth.CoopHealth"] = (Anchor.TopRight, new Vector2(-29f, 77f)),

            // The quest banner's two lines, which the file parks together at the group origin.

            // The subtitle line is NOT here: the guest writes its position as an absolute
            // presentation position onto the Text element itself (`sub_823FC8F0`, per line), so
            // it is placed by the stated delta alone — an anchor here was added on top of it and
            // put the line off the bottom of the screen.
        };

    /// <summary>
    /// The shipped arrangement, as close to the original's as the anchors allow: the authored 16:9
    /// positions rebased onto anchors. A starting point, meant to be edited.
    /// </summary>
    public override Engine_HudLayout Shipped() => new Engine_HudLayout()
        .Add(new Piece("health.bar", "PlayerHealth", Anchor.TopLeft, new Vector2(96f, 64f),
            "PlayerHealth", Drive.Bar))
        .Add(new Piece("health.cap", "PlayerHealthMax", Anchor.TopLeft, new Vector2(96f, 64f),
            "PlayerHealthMax", Drive.Bar))
        .Add(new Piece("quest.title", "TitleText", Anchor.TopRight, new Vector2(-320f, 72f),
            "QuestTitle", Drive.Text))
        .Add(new Piece("quest.objective", "Text", Anchor.TopRight, new Vector2(-320f, 104f),
            "QuestObjective", Drive.Text));
}
