using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.MageDice;

public class AnimatingSwitch : Switch
{
    private readonly IPubClip _boardMC;
    private readonly IPubClip? _reelMC;
    private readonly List<IPubClip> _winMCs = new();
    private readonly Framework.Audio _audio;
    private readonly IPubRandom _random;

    public AnimatingSwitch(
        GameVariant variant, int id, int reeltype, int value, IPubClip boardMC,
        Framework.Audio audio, IPubRandom random)
        : base(variant, id, reeltype, value, random)
    {
        _boardMC = boardMC;
        _audio = audio;
        _random = random;
        ReelImages = variant.ReelImages;
        _reelMC = boardMC.GetChild("reel" + ID);

        var suffix = string.Empty;
        while (true)
        {
            IPubClip? ring = boardMC.GetChild("circle" + ID + suffix);
            if (ring is null)
            {
                break;
            }

            ring.GotoAndStop(1);
            ring.Visible = false;
            _winMCs.Add(ring);
            suffix += "x";
        }

        Reveal();
    }

    public List<string> ReelImages { get; }

    protected IPubClip Board => _boardMC;

    protected IPubClip? ReelClip => _reelMC;

    public float X => _reelMC?.X ?? 0f;

    public float Y => _reelMC?.Y ?? 0f;

    public void SpinReel(double duration, string? endSound)
    {
        if (_reelMC is not null)
        {
            AnimateReel.SpinTo(
                _reelMC, this, LastValue, Value, endSound, duration, "SwitchAnim", _audio, _random);
        }
    }

    public void Reveal()
    {
        if (_reelMC is null)
        {
            return;
        }

        AnimateReel reveal = AnimateReel.Instant(
            _reelMC, this, Value, null, "SwitchAnim", _audio, _random);
        reveal.Run(1, null, false);
    }

    public void PlayWinAnimation(int level)
    {
        if (level >= 0 && level < _winMCs.Count)
        {
            AnimateWin.Timed(_winMCs[level], 1, "Payout");
        }
    }
}
