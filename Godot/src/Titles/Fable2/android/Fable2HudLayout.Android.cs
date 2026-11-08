namespace Refii.Titles.Fable2;

using Godot;
using Refii.Ui;
using static Refii.Ui.Engine_HudLayout;

/// <summary>
/// Android's own arrangement of Fable 2's HUD, applied beneath the player's file: the d-pad lifted
/// clear of the touch controls, the spell-charge meters moved in from the edges where Spells and
/// Lock stand.
/// </summary>
public sealed partial class Fable2HudLayout
{
    /// <summary>
    /// Raises every d-pad piece (<see cref="IsDpad"/>) of <paramref name="layout"/> by
    /// <paramref name="units"/>, together so the slots stay in their ring. A platform's default,
    /// applied beneath the player's own file.
    /// </summary>
    /// <returns>How many pieces moved.</returns>
    public static int LiftDpad(Engine_HudLayout layout, float units) =>
        Nudge(layout, Instance.IsDpad, new Vector2(0f, -units));

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
    public static int InsetSpellCharge(Engine_HudLayout layout, float units) =>
        Nudge(layout, piece => piece.Id == "group.SpellCharging.SpellCharging", new Vector2(units, 0f)) +
        Nudge(layout, piece => piece.Id == "group.SpellCharging2.SpellCharging2", new Vector2(-units, 0f));

    /// <summary>
    /// The platform's own arrangement, applied beneath the player's file: on Android the d-pad
    /// lifted clear of the touch controls' Options (<see cref="Engine_HudLayout.AndroidDpadLift"/>) and the spell
    /// charge moved in from the edges, where Spells and Lock stand
    /// (<see cref="AndroidSpellChargeInset"/>). Nothing elsewhere.
    /// </summary>
    public static void ApplyPlatformDefaults(Engine_HudLayout layout)
    {
        if (!Refii.Core.Engine_Platform.IsAndroid)
        {
            return;
        }

        LiftDpad(layout, AndroidDpadLift);
        InsetSpellCharge(layout, AndroidSpellChargeInset);
        // A phone's screen is small and tapped: mod HUD groups take their touchScale.
        Engine_HudModGroups.Handheld = true;
    }

    /// <summary>Moves every piece <paramref name="which"/> picks by <paramref name="delta"/> (reference units, +Y down).</summary>
    private static int Nudge(Engine_HudLayout layout, System.Func<Piece, bool> which, Vector2 delta)
    {
        int moved = 0;
        foreach (Piece piece in System.Linq.Enumerable.ToArray(layout.Pieces))
        {
            if (which(piece) && layout.Reposition(piece.Id, piece.Anchor, piece.Offset + delta))
            {
                ++moved;
            }
        }

        return moved;
    }
}
