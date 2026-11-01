using Godot;

namespace Refii.Ui;

/// <summary>
/// Android's own HUD arrangement, applied beneath the player's file: the d-pad lifted clear of the
/// touch controls, the spell-charge meters moved in from the edges where Spells and Lock stand.
/// </summary>
public sealed partial class Engine_HudLayout
{
    /// <summary>
    /// How far Android lifts the d-pad, in reference units: clear of the touch controls' Options
    /// button below it, so a thumb pressing Down does not catch Options.
    /// </summary>
    public const float AndroidDpadLift = 56f;

    /// <summary>
    /// Raises every d-pad piece (<see cref="IsDpad"/>) by <paramref name="units"/>, together so the
    /// slots stay in their ring. A platform's default, applied beneath the player's own file.
    /// </summary>
    /// <returns>How many pieces moved.</returns>
    public int LiftDpad(float units) => Nudge(IsDpad, new Vector2(0f, -units));

    /// <summary>
    /// How far Android moves the spell-charge meters in from the side edges, in reference units:
    /// the touch controls' Spells and Lock buttons stand at the edges there.
    /// </summary>
    public const float AndroidSpellChargeInset = 120f;

    /// <summary>
    /// Moves player one's spell-charge meter (down the left edge) in by <paramref name="units"/>,
    /// and player two's (down the right) by the same, mirrored. A platform's default, applied
    /// beneath the player's own file.
    /// </summary>
    /// <returns>How many pieces moved.</returns>
    public int InsetSpellCharge(float units) =>
        Nudge(piece => piece.Id == "group.SpellCharging.SpellCharging", new Vector2(units, 0f)) +
        Nudge(piece => piece.Id == "group.SpellCharging2.SpellCharging2", new Vector2(-units, 0f));

    /// <summary>
    /// The platform's own arrangement, applied beneath the player's file: on Android the d-pad
    /// lifted clear of the touch controls' Options (<see cref="AndroidDpadLift"/>) and the spell
    /// charge moved in from the edges, where Spells and Lock stand
    /// (<see cref="AndroidSpellChargeInset"/>). Nothing elsewhere.
    /// </summary>
    public void ApplyPlatformDefaults()
    {
        if (!Refii.Core.Engine_Platform.IsAndroid)
        {
            return;
        }

        LiftDpad(AndroidDpadLift);
        InsetSpellCharge(AndroidSpellChargeInset);
    }

    /// <summary>Moves every piece <paramref name="which"/> picks by <paramref name="delta"/> (reference units, +Y down).</summary>
    private int Nudge(System.Func<Piece, bool> which, Vector2 delta)
    {
        int moved = 0;
        foreach (Piece piece in _pieces.ToArray())
        {
            if (which(piece) && Reposition(piece.Id, piece.Anchor, piece.Offset + delta))
            {
                ++moved;
            }
        }

        return moved;
    }
}
