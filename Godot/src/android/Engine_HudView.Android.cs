using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// Android's own handling of the health readouts: they stand still where the HUD places them,
/// rather than dropping to make room for the quest box as the original does.
/// </summary>
public sealed partial class Engine_HudView
{
    /// <summary>
    /// On Android, keeps a health readout (<see cref="Engine_HudLayout.PlacedFromRect"/>) where it
    /// was placed while the quest box stands. The guest makes that room itself: the root's
    /// `TopBox` slide puts `HealthAndMana` (the `TopBoxY` meter's element) at y -65, and the
    /// meter's track moves `Health` beneath it 0 -> -136 by the box's height, each stated as the
    /// element's position — the bar dropped ~92 units whenever that slide stood (rect y 293 -> 201)
    /// and rose when it went, which on a phone reads as the bar jumping for no reason. Desktop
    /// keeps the original's drop.
    /// </summary>
    private static void HoldAndroidHealthPlace(Built built, Engine_UiScreen screen, int meter)
    {
        if (!Engine_Platform.IsAndroid || !Engine_HudLayout.PlacedFromRect(built.Rules) ||
            !InSubtree(screen, meter, built.Piece.ElementOrdinal))
        {
            return;
        }

        UiNode element = screen.Nodes[meter];
        built.Binding.HoldPosition(meter, new Vector2(element.PositionX, element.PositionY));

        // What the meter's position tracks move keeps the track's START — where the guest puts it
        // with nothing to make room for. Not its authored position: `Health` is authored at the
        // track's END (y -136), and holding it there stood the bar lower still.
        var held = new Dictionary<int, Vector2>();
        foreach ((BgfAnimation animation, Engine_UiTimeline.Target target) in
            Engine_UiTimeline.Tracks(screen, meter))
        {
            if (target is not (Engine_UiTimeline.Target.PositionX or Engine_UiTimeline.Target.PositionY) ||
                animation.ElementOrdinal == meter)
            {
                continue;
            }

            UiNode node = screen.Nodes[animation.ElementOrdinal];
            Vector2 at = held.TryGetValue(animation.ElementOrdinal, out Vector2 soFar)
                ? soFar
                : new Vector2(node.PositionX, node.PositionY);
            float start = animation.Evaluate(0f);
            held[animation.ElementOrdinal] = target == Engine_UiTimeline.Target.PositionX
                ? new Vector2(start, at.Y)
                : new Vector2(at.X, start);
        }

        foreach ((int ordinal, Vector2 at) in held)
        {
            built.Binding.HoldPosition(ordinal, at);
        }

        GD.Print($"[hud] {built.Piece.Id}: '{element.Name}'#{meter} held at its authored place; " +
            string.Join(", ", System.Linq.Enumerable.Select(held,
                h => $"'{screen.Nodes[h.Key].Name}'#{h.Key} at ({h.Value.X:0.#}, {h.Value.Y:0.#})")));
    }

    /// <summary>A health readout's clearance from the screen's camera cutouts.</summary>
    private sealed class CutoutClearance
    {
        /// <summary>Frames until the cutouts are read again.</summary>
        public int Wait;

        /// <summary>How far the readout stands lowered, in seat canvas units.</summary>
        public float Drop;

        /// <summary>
        /// The readout's drawn rect relative to where it is placed, unlowered — measured while it
        /// shows and then kept, since it hides at full health. Null until first seen.
        /// </summary>
        public Rect2? Rest;
    }

    private readonly Dictionary<Built, CutoutClearance> _cutoutClearance = new();

    /// <summary>Canvas units kept between a readout and a cutout.</summary>
    private const float CutoutMargin = 12f;

    /// <summary>Frames between reads of the cutouts once the readout has been measured.</summary>
    private const int CutoutRecheckFrames = 30;

    /// <summary>
    /// How far to lower a health readout so it does not sit under a hole-punch camera, in seat
    /// canvas units: the corner punch of a phone like the S10e lands on the player's orb in one
    /// landscape side. Read from the display's cutouts rather than its safe area, which insets a
    /// whole edge for a punch in the middle of it that the bar never reaches. Read again every
    /// half second, so turning the phone round moves the bar back up. Zero off Android and on a
    /// split screen.
    /// </summary>
    private float AndroidCutoutDrop(Built built, Engine_UiSeats.Frame frame, Vector2 at)
    {
        if (!Engine_Platform.IsAndroid || !frame.IsWhole || !Engine_HudLayout.PlacedFromRect(built.Rules))
        {
            return 0f;
        }

        if (!_cutoutClearance.TryGetValue(built, out CutoutClearance? clearance))
        {
            _cutoutClearance[built] = clearance = new CutoutClearance();
        }

        if (--clearance.Wait > 0)
        {
            return clearance.Drop;
        }

        // Last frame's node stood lowered by the old drop from (about) this placement.
        float behind = 1.5f;
        if (built.Node.IsVisibleInTree() && SubtreeBounds(built.Node, ref behind) is { } drawn)
        {
            Vector2 a = frame.FromCanvas(drawn.Position);
            Vector2 b = frame.FromCanvas(drawn.End);
            Vector2 low = new(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y));
            clearance.Rest = new Rect2(low + new Vector2(0f, clearance.Drop) - at, (b - a).Abs());
        }

        clearance.Wait = clearance.Rest is null ? 1 : CutoutRecheckFrames;
        if (clearance.Rest is not { } rest)
        {
            return clearance.Drop;
        }

        var placed = new Rect2(rest.Position + at, rest.Size);
        float drop = 0f;
        Rect2 hit = default;

        // The cutouts are in the screen's own pixels, the seat in the stretched window's: on the
        // S10e 2280x1080 against about 1543x712, and unscaled a 149-pixel punch lowered the bar
        // half as far again as it needed, onto the Lock button.
        Vector2I screen = DisplayServer.WindowGetSize();
        if (screen.X <= 0 || screen.Y <= 0)
        {
            return clearance.Drop;
        }

        var toWindow = new Vector2(frame.Window.X / screen.X, frame.Window.Y / screen.Y);
        foreach (Rect2 cut in DisplayServer.GetDisplayCutouts())
        {
            // Screen pixels (+Y down) onto the seat's canvas (+Y up).
            Vector2 p = frame.FromPixel((cut.Position * toWindow) - frame.Origin);
            Vector2 q = frame.FromPixel((cut.End * toWindow) - frame.Origin);
            Rect2 hole = new Rect2(new Vector2(Mathf.Min(p.X, q.X), Mathf.Min(p.Y, q.Y)), (q - p).Abs())
                .Grow(CutoutMargin);
            if (hole.Intersects(placed) && placed.End.Y - hole.Position.Y > drop)
            {
                drop = placed.End.Y - hole.Position.Y;
                hit = cut;
            }
        }

        if (!Mathf.IsEqualApprox(drop, clearance.Drop))
        {
            GD.Print(drop > 0f
                ? $"[hud] {built.Piece.Id} lowered {drop:0} units clear of the camera cutout at {hit}"
                : $"[hud] {built.Piece.Id} back in place: no camera cutout under it");
        }

        clearance.Drop = drop;
        return drop;
    }
}
