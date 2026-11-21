using System;
using Godot;

namespace Refii.AndroidRuntime;

/// <summary>
/// The arithmetic of the phone's tree cards (<see cref="AndroidTreeCards"/>): the fade band, the cull,
/// where cards take over, the atlas cells, one placement's record and when the atlas is captured again.
/// Pure; tree_cards.gdshader repeats <see cref="CardFade"/> and <see cref="Cell"/>.
/// </summary>
public static class AndroidTreeCardMaths
{
    /// <summary>Cells across the atlas.</summary>
    public const int Columns = 8;

    /// <summary>A cell's side in pixels.</summary>
    public const int CellPixels = 128;

    /// <summary>Mips the atlas carries (Engine_ClassicImpostorMips).</summary>
    public const int Mips = 4;

    /// <summary>Bytes a texel of the atlas takes: RGBA16F.</summary>
    public const int BytesPerTexel = 8;

    /// <summary>
    /// A card's opacity at <paramref name="distance"/> metres: in over [0.75, 0.875] F, out over the last
    /// eighth before <paramref name="cull"/>, linear in both; 0 outside [0.75 F, cull].
    /// </summary>
    public static float CardFade(float f, float cull, float distance)
    {
        if (!(f > 0f) || !(cull > 0f))
        {
            return 0f;
        }

        float fadeIn = Math.Clamp((distance - (0.75f * f)) / (0.125f * f), 0f, 1f);
        float fadeOut = Math.Clamp((cull - distance) / (0.125f * cull), 0f, 1f);
        return fadeIn * fadeOut;
    }

    /// <summary>The cards' far end: the stated cull, or `tree_far_reach` when it is set and nearer.</summary>
    public static float CardCull(float statedCull, float treeFarReach) =>
        treeFarReach > 0f && float.IsFinite(treeFarReach) && treeFarReach < statedCull ? treeFarReach : statedCull;

    /// <summary>Rows of cells <paramref name="kinds"/> models need; at least one.</summary>
    public static int Rows(int kinds, int columns = Columns) =>
        Math.Max(1, (Math.Max(kinds, 0) + columns - 1) / columns);

    /// <summary>The UV rectangle of cell <paramref name="index"/>: left to right, then top to bottom.</summary>
    public static Rect2 Cell(int index, int columns, int rows)
    {
        int col = index % columns, row = index / columns;
        return new Rect2(col / (float)columns, row / (float)rows, 1f / columns, 1f / rows);
    }

    /// <summary>
    /// Cell <paramref name="index"/>'s centre from the atlas's centre, in cells: x to the right, y up.
    /// The classic atlas's layout (Engine_ClassicBillboards.Place) at half its scale.
    /// </summary>
    public static Vector2 CellCentre(int index, int columns, int rows)
    {
        int col = index % columns, row = index / columns;
        return new Vector2(col - ((columns - 1) * 0.5f), ((rows - 1) * 0.5f) - row);
    }

    /// <summary>
    /// One placement's card: the world centre of the model's bound sphere and its world radius.
    /// <paramref name="sphere"/> is in the file's axes {x, y, z, r}; Godot model axes are the file's
    /// (x, z, -y). False for a placement collapsed to a point (a slot a live entity took over).
    /// </summary>
    public static bool Record(Transform3D placement, Vector4 sphere, out Vector3 centre, out float radius)
    {
        float scale = placement.Basis.Column0.Length();
        if (!(scale >= 1e-4f) || !float.IsFinite(scale))
        {
            centre = Vector3.Zero;
            radius = 0f;
            return false;
        }

        centre = placement * new Vector3(sphere.X, sphere.Z, -sphere.Y);
        radius = scale * sphere.W;
        return true;
    }

    /// <summary>The compass heading of a direction about the vertical, in degrees [0, 360).</summary>
    public static float YawDegrees(Vector3 direction)
    {
        float degrees = MathF.Atan2(direction.X, -direction.Z) * (180f / MathF.PI);
        return degrees < 0f ? degrees + 360f : degrees;
    }

    /// <summary>The smaller angle between two headings, in degrees [0, 180].</summary>
    public static float YawBetween(float a, float b)
    {
        float d = MathF.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }

    /// <summary>How far a colour moved: its largest channel change as a share of the last colour's largest channel (at least 0.1).</summary>
    public static float ColourShare(Vector3 last, Vector3 now)
    {
        float scale = MathF.Max(0.1f, MathF.Max(MathF.Abs(last.X), MathF.Max(MathF.Abs(last.Y), MathF.Abs(last.Z))));
        Vector3 d = (now - last).Abs();
        return MathF.Max(d.X, MathF.Max(d.Y, d.Z)) / scale;
    }

    /// <summary>The angle between two sun directions in degrees; 0 when either is missing.</summary>
    public static float SunDegrees(Vector3 last, Vector3 now)
    {
        if (last.LengthSquared() <= 1e-8f || now.LengthSquared() <= 1e-8f)
        {
            return 0f;
        }

        float cos = Math.Clamp(last.Normalized().Dot(now.Normalized()), -1f, 1f);
        return MathF.Acos(cos) * (180f / MathF.PI);
    }

    /// <summary>Degrees the sun moves before a recapture once <see cref="LightSettleSeconds"/> have passed.</summary>
    public const float SunSoftDegrees = 2f;

    /// <summary>Degrees the sun moves before a recapture at once (a clock jump, a sleep).</summary>
    public const float SunHardDegrees = 15f;

    /// <summary>Share the sun's colour or the ambient moves before a recapture once <see cref="LightSettleSeconds"/> have passed.</summary>
    public const float ColourSoftShare = 0.03f;

    /// <summary>Share the sun's colour or the ambient moves before a recapture at once (dawn, dusk, weather).</summary>
    public const float ColourHardShare = 0.06f;

    /// <summary>
    /// Seconds since the last full capture before a small light change recaptures. The game's clock turns
    /// the sun about 2 degrees every 9-13 s (OnePlus 7 Pro logcat, Brightwood), so the soft limits alone
    /// recaptured that often with nothing moving.
    /// </summary>
    public const double LightSettleSeconds = 60.0;

    /// <summary>
    /// Why the light wants the atlas captured again, or null: a sun that appeared or went at once; a
    /// move past the hard limits at once; a move past the soft limits only once
    /// <paramref name="seconds"/> since the last capture reach <see cref="LightSettleSeconds"/>.
    /// The reason names the input and its change, for the log.
    /// </summary>
    public static string? LightReason(
        Vector3 lastSun, Vector3 sun, Vector3 lastColour, Vector3 colour, Vector3 lastAmbient, Vector3 ambient,
        double seconds)
    {
        bool hadSun = lastSun.LengthSquared() > 1e-8f, hasSun = sun.LengthSquared() > 1e-8f;
        if (hadSun != hasSun)
        {
            return hasSun ? "sun on" : "sun off";
        }

        float angle = SunDegrees(lastSun, sun);
        float sunShare = ColourShare(lastColour, colour);
        float ambientShare = ColourShare(lastAmbient, ambient);
        bool settled = seconds >= LightSettleSeconds;
        if (angle > SunHardDegrees || (settled && angle > SunSoftDegrees))
        {
            return $"sun angle {angle:0.0} deg";
        }

        if (sunShare > ColourHardShare || (settled && sunShare > ColourSoftShare))
        {
            return $"sun colour {sunShare * 100f:0.0}%";
        }

        if (ambientShare > ColourHardShare || (settled && ambientShare > ColourSoftShare))
        {
            return $"ambient {ambientShare * 100f:0.0}%";
        }

        return null;
    }

    /// <summary>Whether the view turned more than <paramref name="limit"/> degrees from the last capture's heading; 0 never.</summary>
    public static bool Turned(float lastYaw, float yaw, float limit) => limit > 0f && YawBetween(lastYaw, yaw) > limit;

    /// <summary>`tree_billboard_turn_degrees` held to 0 (off) or [5, 90].</summary>
    public static float TurnLimit(float degrees) =>
        !(degrees > 0f) || !float.IsFinite(degrees) ? 0f : Math.Clamp(degrees, 5f, 90f);

    /// <summary>`tree_billboard_kinds_per_frame` held to 0 (all in one frame) or [1, 64].</summary>
    public static int KindsPerFrame(int value) => value <= 0 ? 0 : Math.Min(value, 64);

    /// <summary>
    /// The capture viewport's grid of cells for one frame's batch: up to <see cref="Columns"/> across.
    /// <paramref name="perFrame"/> 0 takes every kind at once.
    /// </summary>
    public static (int Columns, int Rows) BatchGrid(int perFrame, int kinds)
    {
        int n = Math.Max(1, perFrame > 0 ? Math.Min(perFrame, Math.Max(kinds, 1)) : kinds);
        return (Math.Min(n, Columns), Rows(n));
    }

    /// <summary>Frames one full capture of <paramref name="kinds"/> models takes at <paramref name="perFrame"/> a frame (0: one).</summary>
    public static int CaptureFrames(int kinds, int perFrame) =>
        kinds <= 0 ? 0 : perFrame <= 0 ? 1 : (kinds + perFrame - 1) / perFrame;

    /// <summary>Cell <paramref name="index"/>'s top-left pixel in a grid <paramref name="columns"/> cells across.</summary>
    public static Vector2I CellPixel(int index, int columns = Columns, int cell = CellPixels) =>
        new(index % columns * cell, index / columns * cell);

    /// <summary>`tree_billboard_start_scale` held to [1, 8]; anything else is 1.</summary>
    public static float StartScale(float scale) =>
        !(scale > 1f) || !float.IsFinite(scale) ? 1f : MathF.Min(scale, 8f);

    /// <summary>
    /// Where a tree's model ends and its card has taken over: <paramref name="scale"/> × the stated F,
    /// never past the stated cull max(D, F), where the model would have ended without cards.
    /// </summary>
    public static float Start(float f, float statedCull, float scale) =>
        MathF.Min(f * StartScale(scale), MathF.Max(statedCull, f));

    /// <summary>
    /// The model's far end on a stamped tree node: <see cref="Start"/>, or with no cards (split screen)
    /// the stated cull, as the tree drew before `tree_billboards`.
    /// </summary>
    public static float ModelEnd(float f, float statedCull, float scale, bool cards) =>
        cards ? Start(f, statedCull, scale) : MathF.Max(statedCull, f);

    /// <summary>
    /// The card's far end for a tree whose model ends at <paramref name="start"/>: <see cref="CardCull"/>,
    /// or 0 (no card) when that is not past the start.
    /// </summary>
    public static float CardEnd(float start, float statedCull, float treeFarReach)
    {
        float cull = CardCull(statedCull, treeFarReach);
        return cull > start ? cull : 0f;
    }

    /// <summary>
    /// A tree node's band {fade-in start, 1 / its length, power, end, 1 / (0.125 end), power} and range
    /// {begin, margin, end, margin, fade} with the model's end moved to <paramref name="end"/>: the
    /// fade-out keeps its last eighth and the range keeps the batch's spread past the band. Never ends
    /// before the fade-in has finished. The same arrays back when nothing moves.
    /// </summary>
    public static (float[] Band, float[] Stated) WithEnd(float[] band, float[] stated, float end)
    {
        if (band.Length < 6 || stated.Length < 3 || !(end > 0f) || !float.IsFinite(end))
        {
            return (band, stated);
        }

        float inEnd = band[1] > 0f ? band[0] + (1f / band[1]) : 0f;
        end = MathF.Max(end, inEnd / 0.875f);
        if (MathF.Abs(end - band[3]) < 1e-3f)
        {
            return (band, stated);
        }

        float pad = MathF.Max(stated[2] - band[3], 0f);
        var moved = (float[])band.Clone();
        moved[3] = end;
        moved[4] = 1f / (0.125f * end);
        var movedStated = (float[])stated.Clone();
        movedStated[2] = end + pad;
        return (moved, movedStated);
    }

    /// <summary>
    /// A stamped tree node's band and range once its model ends where its own card takes over
    /// (<see cref="ModelEnd"/> from the node's own {F, cull} stamp), as AndroidTreeCards.Register writes
    /// them before the hand-over and the far reach apply (Engine_TreeReach.Cap). The stamped node is the
    /// tree's impostor: its `_LOD1` model, or its main model when it has none.
    /// </summary>
    public static (float[] Band, float[] Stated) Restamped(
        float[] band, float[] stated, float f, float statedCull, float scale, bool cards) =>
        WithEnd(band, stated, ModelEnd(f, statedCull, scale, cards));

    /// <summary>
    /// An area's cards for the log: how many placements draw a card (<see cref="CardEnd"/> past 0), the range
    /// of their starts and of their far ends, and how many have none because their cull is within their start.
    /// </summary>
    public readonly record struct Spread(int Cards, int Without, float MinStart, float MaxStart, float MinCull, float MaxCull)
    {
        /// <summary>"region: N cards of K tree models, cards from a-b m (start scale s), cull c-d m[, W placements without a card ...]".</summary>
        public string Describe(int models, float scale)
        {
            string head = $"region: {Cards} cards of {models} tree models, ";
            string cards = Cards > 0
                ? $"cards from {MinStart:0}-{MaxStart:0} m (start scale {scale:0.##}), cull {MinCull:0}-{MaxCull:0} m"
                : $"no cards (start scale {scale:0.##})";
            return head + cards + (Without > 0 ? $", {Without} placements without a card (cull within the start)" : string.Empty);
        }
    }

    /// <summary>The <see cref="Spread"/> of <paramref name="placements"/> (each its node's start and stated cull). Pure.</summary>
    public static Spread RegionSpread(System.Collections.Generic.IEnumerable<(float Start, float StatedCull)> placements, float treeFarReach)
    {
        int cards = 0, without = 0;
        float minStart = 0f, maxStart = 0f, minCull = 0f, maxCull = 0f;
        foreach ((float start, float statedCull) in placements)
        {
            float cull = CardEnd(start, statedCull, treeFarReach);
            if (!(cull > 0f))
            {
                without++;
                continue;
            }

            minStart = cards == 0 ? start : MathF.Min(minStart, start);
            maxStart = cards == 0 ? start : MathF.Max(maxStart, start);
            minCull = cards == 0 ? cull : MathF.Min(minCull, cull);
            maxCull = cards == 0 ? cull : MathF.Max(maxCull, cull);
            cards++;
        }

        return new Spread(cards, without, minStart, maxStart, minCull, maxCull);
    }

    /// <summary>
    /// Whether the capture should draw the far build of a tree's materials (`prop_far_shading_metres`):
    /// the real trees switch to it past <paramref name="metres"/>, so they wear it at the hand-over when
    /// that is nearer than where the card has fully faded in (0.875 of <paramref name="start"/>).
    /// </summary>
    public static bool CaptureFar(float metres, float start) => metres > 0f && metres <= 0.875f * start;

    /// <summary>The atlas's memory with its mips, for <paramref name="kinds"/> models at <paramref name="cell"/> pixels a cell.</summary>
    public static long AtlasBytes(int kinds, int cell = CellPixels)
    {
        if (kinds <= 0)
        {
            return 0;
        }

        long w = (long)Columns * cell, h = (long)Rows(kinds) * cell, bytes = 0;
        for (int mip = 0; mip < Mips; mip++)
        {
            bytes += Math.Max(1, w >> mip) * Math.Max(1, h >> mip) * BytesPerTexel;
        }

        return bytes;
    }

    /// <summary>
    /// The heading a capture pass faces, and the area heading to keep. With turn recaptures on
    /// (<paramref name="turn"/> above 0) a pass faces the view. With them off, every pass in an area faces the way
    /// its first did, so a light pass or a late model never turns the pictures while the player pans.
    /// </summary>
    public static (Godot.Vector3 Heading, Godot.Vector3? Area) CaptureHeading(float turn, Godot.Vector3? area, Godot.Vector3 view)
    {
        if (turn > 0f)
        {
            return (view, area);
        }

        Godot.Vector3 fixedHeading = area ?? view;
        return (fixedHeading, fixedHeading);
    }
}
