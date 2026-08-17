using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.MageDice;

/// <summary>
/// A <see cref="Switch"/> bound to the clips that draw it.
/// </summary>
/// <remarks>
/// THE CLIPS ARE FOUND BY NAME OFF THE BOARD, not passed in: reel <c>N</c> lives at
/// <c>board.reel&lt;N&gt;</c> and its win rings at <c>board.circle&lt;N&gt;</c>,
/// <c>circle&lt;N&gt;x</c>, <c>circle&lt;N&gt;xx</c> … The ring list is walked until one is
/// missing, so a board that ships two win tiers gets two and one that ships three gets three,
/// without anything stating the count.
///
/// A 1:1 port of <c>AnimatingSwitch.as</c>.
/// </remarks>
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

    /// <summary>Spins to whatever <see cref="Switch.Roll"/> already decided.</summary>
    public void SpinReel(double duration, string? endSound)
    {
        if (_reelMC is not null)
        {
            AnimateReel.SpinTo(
                _reelMC, this, LastValue, Value, endSound, duration, "SwitchAnim", _audio, _random);
        }
    }

    /// <summary>Snaps to the current symbol with no spin, for setting the board up.</summary>
    public void Reveal()
    {
        if (_reelMC is null)
        {
            return;
        }

        // RUN IMMEDIATELY RATHER THAN SCHEDULED. The board has to be showing something before the
        // first frame is drawn, and a scheduled action would not run until the callback manager
        // next ticks.
        AnimateReel reveal = AnimateReel.Instant(
            _reelMC, this, Value, null, "SwitchAnim", _audio, _random);
        reveal.Run(1, null, false);
    }

    /// <summary>Lights the win ring for a chain of this size band.</summary>
    public void PlayWinAnimation(int level)
    {
        if (level >= 0 && level < _winMCs.Count)
        {
            AnimateWin.Timed(_winMCs[level], 1, "Payout");
        }
    }
}
