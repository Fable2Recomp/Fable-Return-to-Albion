using Refii.FlashGames.Framework;

namespace Refii.FlashGames.MageDice;

/// <summary>
/// The fourth reel on a three-reel board: hidden behind a shutter until it is won.
/// </summary>
/// <remarks>
/// THE SHUTTER IS THE REVEAL. <see cref="Show"/> and <see cref="Hide"/> are not visibility toggles
/// — they run the shutter clip forwards or backwards with the reel fading behind it, both blocking
/// their group so the game waits for the reveal before spinning. The eased animation and the
/// half-second pause are the whole reason the bonus feels like an event.
///
/// A 1:1 port of <c>BonusSwitch.as</c>.
/// </remarks>
public sealed class BonusSwitch : AnimatingSwitch
{
    private readonly IPubClip? _shutterMC;

    private bool _open;

    public BonusSwitch(
        GameVariant variant, int id, int reeltype, int value, IPubClip boardMC,
        Framework.Audio audio, IPubRandom random)
        : base(variant, id, reeltype, value, boardMC, audio, random)
    {
        _shutterMC = boardMC.GetChild("bonusShutter");
        _open = false;
    }

    /// <summary>A bonus reel left open from the last spin closes before the next one.</summary>
    public override void NewSpin()
    {
        if (_open)
        {
            Hide();
        }
    }

    public void Hide()
    {
        if (ReelClip is null || _shutterMC is null)
        {
            return;
        }

        Action.BlockOn(Fade.Out(ReelClip, 0.5, "SwitchAnim"));
        Action.EaseOut(Action.BlockOn(
            Animate.Once(_shutterMC, 1, _shutterMC.TotalFrames / 60.0, "SwitchAnim")));
        Fade.InstantOut(_shutterMC, "SwitchAnim");
        _open = false;

        // -1 IS "NOTHING TO SPIN FROM", which AnimateReel reads as the engraved blank face.
        Value = -1;
    }

    public void Show()
    {
        if (ReelClip is null || _shutterMC is null)
        {
            return;
        }

        ReelClip.GotoAndStop(1);
        IPubClip? image = ReelClip.GetChild("image");
        if (image is not null && image.GetChild("img") is null)
        {
            image.AttachMovie("engraving", "img", 0);
        }

        LastValue = -1;
        _shutterMC.GotoAndStop(1);
        Wait.Seconds(0.5, "SwitchAnim");
        Fade.InstantIn(_shutterMC, "SwitchAnim");
        Action.EaseIn(Action.BlockOn(Animate.Once(
            _shutterMC, _shutterMC.TotalFrames, _shutterMC.TotalFrames / 60.0, "SwitchAnim")));
        Action.BlockOn(Fade.In(ReelClip, 0.5, "SwitchAnim"));
        _open = true;
    }
}
