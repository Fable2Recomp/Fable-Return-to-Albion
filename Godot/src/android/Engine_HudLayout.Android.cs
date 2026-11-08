namespace Refii.Ui;

/// <summary>
/// Android's reading of a HUD layout: how far the d-pad is lifted, and which pieces the title placed
/// from their measured rect.
/// </summary>
public sealed partial class Engine_HudLayout
{
    /// <summary>
    /// How far Android lifts the d-pad, in reference units: clear of the touch controls' Options
    /// button below it, so a thumb pressing Down does not catch Options.
    /// </summary>
    public const float AndroidDpadLift = 56f;

    /// <summary>
    /// The pieces placed from their measured rect (the active title's
    /// <see cref="Engine_HudTitleLayout.Arrangement"/>; Fable 2's health readouts), which Android
    /// keeps where they stand (Engine_HudView.Android).
    /// </summary>
    public static bool PlacedFromRect(Piece piece) => Rules.Arrangement.ContainsKey(piece.Id);
}
