using Refii.Titles.Fable2.PubGames.Framework;

namespace Refii.Titles.Fable2.PubGames.MageDice;

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
