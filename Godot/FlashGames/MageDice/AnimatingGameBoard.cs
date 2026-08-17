using System;
using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.MageDice;

/// <summary>
/// The Mage Dice board on screen: the reels, the win rings, the lines between them.
/// </summary>
/// <remarks>
/// THE BOARD CLIP IS SWAPPED IN AT RUNTIME. The stage ships a placeholder called <c>board</c>,
/// which the constructor deletes and replaces with the export named
/// <c>&lt;reels&gt;switch&lt;skin&gt;</c> — <c>3switch0</c>, <c>4switch2</c>, <c>6switch1</c>. The
/// reel count and the pub's decor are both in that one name, which is why a three-reel table and a
/// six-reel one can share every line of code below.
///
/// A 1:1 port of <c>AnimatingGameBoard.as</c>.
/// </remarks>
public sealed class AnimatingGameBoard : GameBoard
{
    private readonly IPubClip _boardMC;
    private readonly MageDiceWinVars _winVars;
    private readonly List<double> _spinTimes;
    private readonly double _spinTimeExtension1;
    private readonly double _spinTimeExtension2;
    private readonly Localizer _localizer;

    private int _chainIndex;
    private bool _firstPayoutSequence;

    public AnimatingGameBoard(
        GameVariant v, int skinIndex, List<double> spinTimes, double spinTimeExtension1,
        double spinTimeExtension2, MageDiceWinVars winVars, IPubClip root,
        IPubRandom random, Framework.Audio audio, Localizer localizer)
        : base(v, random, audio)
    {
        _localizer = localizer;

        IPubClip? placeholder = root.GetChild("board");
        int depth = placeholder?.GetDepth() ?? root.GetNextHighestDepth();
        placeholder?.RemoveMovieClip();

        _boardMC = root.AttachMovie($"{BoardType}switch{skinIndex}", "board", depth)
            ?? root.CreateEmptyMovieClip("board", depth);

        _winVars = winVars;
        _chainIndex = -1;
        _firstPayoutSequence = false;
        _spinTimes = spinTimes;
        _spinTimeExtension1 = spinTimeExtension1;
        _spinTimeExtension2 = spinTimeExtension2;

        if (BoardType == 3)
        {
            HideBonusReel();
        }

        HideWinAnims();
        InitGlint();
    }

    public IPubClip GetBoardClip() => _boardMC;

    /// <summary>
    /// True while the first pass over a win is still playing.
    /// </summary>
    /// <remarks>
    /// AUTOPLAY WAITS ON THIS. The first pass lights every chain at once so the player sees the
    /// shape of the win; only afterwards are they walked one at a time with their amounts. Letting
    /// autoplay start the next spin during that first pass would hide the result it is celebrating.
    /// </remarks>
    public bool FirstPayoutSequence => Chains.Count > 0 && _firstPayoutSequence;

    public BonusSwitch? GetBonusSwitch() => BonusSwitch as BonusSwitch;

    public override Switch GetSwitch(int i) => Switches[i];

    private AnimatingSwitch Animating(int i) => (AnimatingSwitch)Switches[i];

    protected override Switch BuildSwitch(int id, int reeltype, int value) =>
        new AnimatingSwitch(Variant, id, reeltype, value, _boardMC, Audio, Random);

    protected override Switch BuildBonusSwitch(int id, int reeltype, int value) =>
        new BonusSwitch(Variant, id, reeltype, value, _boardMC, Audio, Random);

    private void HideBonusReel()
    {
        Hide("bonusShutter", alphaOnly: true);
        Hide("reel3", alphaOnly: true);
        Hide("circle3", alphaOnly: false);
        Hide("circle3x", alphaOnly: false);

        void Hide(string name, bool alphaOnly)
        {
            IPubClip? clip = _boardMC.GetChild(name);
            if (clip is null)
            {
                return;
            }

            clip.GotoAndStop(1);
            if (alphaOnly)
            {
                clip.Alpha = 0;
            }
            else
            {
                clip.Visible = false;
            }
        }
    }

    /// <summary>Clears the lines drawn between reels of a winning chain.</summary>
    private void HideWinAnims()
    {
        for (var a = 0; a < BoardType; a++)
        {
            for (int b = a + 1; b < BoardType; b++)
            {
                IPubClip? line = _boardMC.GetChild("line" + a + b);
                if (line is not null)
                {
                    line.GotoAndStop(1);
                    line.Visible = false;
                }
            }
        }
    }

    public override void NewSpin()
    {
        base.NewSpin();
        _chainIndex = -1;
        _firstPayoutSequence = true;
    }

    /// <summary>
    /// Starts every reel spinning, staggered, with the sound each one should land on.
    /// </summary>
    /// <remarks>
    /// THE STAGGER IS CUMULATIVE AND RANDOM WITHIN A BAND. <c>spinTimes</c> is twelve numbers read
    /// as six min/max pairs, one per reel, and each reel's stop time is added to the last — so the
    /// reels always land left to right, but never on the same beat twice.
    ///
    /// THE LANDING SOUND IS A TEASE, chosen from what the board will look like once this reel
    /// stops. <c>M_genericMatching2</c> as the second of a pair lands, <c>M_bonusMatching3</c> as
    /// the third bonus symbol lands. The count is rebuilt here rather than read off the payout
    /// tables because it has to be the count SO FAR, reel by reel, not the final one.
    ///
    /// THE SIX-REEL BOARD NEEDS ITS OWN ARITHMETIC because its reels are not all adjacent — a
    /// match on reel 3 only counts if reel 1 shows the same face. That is why this repeats the
    /// adjacency from <see cref="GameBoard.Init"/> rather than counting matches outright: the
    /// sound has to promise a win that the payout will actually honour.
    ///
    /// AND THE TWO BONUS REELS BUY TIME. A second bonus symbol extends the next reel's spin by
    /// <c>spinTimeExtension1</c> and a third by <c>spinTimeExtension2</c>, so the reel that could
    /// complete the set hangs before it lands.
    /// </remarks>
    public void AnimateSpin()
    {
        var counts = new int[Variant.ReelImages.Count];
        var multipliers = 0;
        var freeSpins = 0;

        PlaySound.Once(Audio, "M_flitSpinGyro", "SwitchAnim");

        double when = 0;
        for (var reel = 0; reel < Variant.GameType; reel++)
        {
            int pair = reel * 2;
            when += Band(pair);

            string? sound = null;
            int value = GetSwitch(reel).Value;
            int type = Variant.Types[value];

            if (type == Token.TypeChain)
            {
                if (Variant.GameType == 6)
                {
                    switch (reel)
                    {
                        case 3:
                            if (GetSwitch(1).Value == value)
                            {
                                counts[value]++;
                                if (Variant.TokenPays(value, counts[value]) != 0)
                                {
                                    sound = "M_genericMatching" + counts[value];
                                }
                            }
                            else if (counts[value] == 0)
                            {
                                counts[value] = 1;
                            }

                            break;

                        case 4:
                            if (GetSwitch(1).Value == value || GetSwitch(2).Value == value ||
                                GetSwitch(3).Value == value)
                            {
                                counts[value]++;
                                if (GetSwitch(1).Value != value && GetSwitch(2).Value == value &&
                                    GetSwitch(3).Value == value)
                                {
                                    counts[value]++;
                                }

                                if (Variant.TokenPays(value, counts[value]) != 0)
                                {
                                    sound = "M_genericMatching" + counts[value];
                                }
                            }
                            else if (counts[value] == 0)
                            {
                                counts[value] = 1;
                            }

                            break;

                        case 5:
                            if (GetSwitch(2).Value == value || GetSwitch(4).Value == value)
                            {
                                counts[value]++;
                                if (Variant.TokenPays(value, counts[value]) != 0)
                                {
                                    sound = "M_genericMatching" + counts[value];
                                }
                            }

                            break;

                        default:
                            counts[value]++;
                            if (Variant.TokenPays(value, counts[value]) != 0)
                            {
                                sound = "M_genericMatching" + counts[value];
                            }

                            break;
                    }
                }
                else
                {
                    counts[value]++;
                    if (Variant.TokenPays(value, counts[value]) != 0)
                    {
                        sound = "M_genericMatching" + counts[value];
                    }
                }
            }
            else if (type == Token.TypeBonusMult)
            {
                multipliers++;
                sound = "M_bonusCrownMultiplier" + multipliers;
            }
            else if (type == Token.TypeFreeSpin)
            {
                if (Variant.GameType == 6)
                {
                    switch (reel)
                    {
                        case 3:
                            freeSpins = IsFreeSpin(1) || IsFreeSpin(2) ? freeSpins + 1 : 1;
                            sound = "M_bonusCoinFreeSpin" + freeSpins;
                            break;

                        case 4:
                            freeSpins = IsFreeSpin(1) || IsFreeSpin(2) || IsFreeSpin(3)
                                ? freeSpins + 1
                                : 1;
                            sound = "M_bonusCoinFreeSpin" + freeSpins;
                            break;

                        case 5:
                            if (IsFreeSpin(2) || IsFreeSpin(4))
                            {
                                freeSpins++;
                                sound = "M_bonusCoinFreeSpin" + freeSpins;
                            }

                            break;

                        default:
                            freeSpins++;
                            sound = "M_bonusCoinFreeSpin" + freeSpins;
                            break;
                    }
                }
                else
                {
                    freeSpins++;

                    // A LONE FREE SPIN ON THE LAST REEL GETS NO SOUND, because it cannot be the
                    // start of anything — there are no reels left to match it.
                    if (reel != Variant.GameType - 1 || freeSpins != 1)
                    {
                        sound = "M_bonusCoinFreeSpin" + freeSpins;
                    }
                }
            }
            else if (Variant.HasBonusReel)
            {
                if (reel == 0 && type == Token.TypeBonusReel)
                {
                    sound = "M_bonusMatching1";
                }

                if (reel == 1 && IsBonusReel(0))
                {
                    when += _spinTimeExtension1;
                    if (type == Token.TypeBonusReel)
                    {
                        sound = "M_bonusMatching2";
                    }
                }
                else if (reel == 2 && IsBonusReel(1) && IsBonusReel(0))
                {
                    when += _spinTimeExtension2;
                    if (type == Token.TypeBonusReel)
                    {
                        sound = "M_bonusMatching3";
                    }
                }
            }

            Animating(reel).SpinReel(when, sound);
        }

        bool IsFreeSpin(int reel) => Variant.Types[GetSwitch(reel).Value] == Token.TypeFreeSpin;

        bool IsBonusReel(int reel) => Variant.Types[GetSwitch(reel).Value] == Token.TypeBonusReel;
    }

    /// <summary>A stop time drawn from reel <c>pair / 2</c>'s min/max band.</summary>
    private double Band(int pair)
    {
        if (pair + 1 >= _spinTimes.Count)
        {
            return 0;
        }

        return _spinTimes[pair] + (Random.NextDouble() * (_spinTimes[pair + 1] - _spinTimes[pair]));
    }

    /// <summary>Opens the shutter and spins the bonus reel on its own.</summary>
    public void AnimateBonusSpin()
    {
        GetBonusSwitch()?.Show();
        PlaySound.Once(Audio, "M_bonusFourthSwitchReveal", null);

        // THE HURRY-UP IS CANCELLED HERE. The bonus reel is the payoff; letting a held button rush
        // it would skip the one spin the player most wants to watch.
        MageDice.SpeedAnimation = false;

        string? endSound = null;
        if (GetBonusSwitch() is { } bonus && Variant.Types[bonus.Value] == Token.TypeBonusReel)
        {
            endSound = "W_bonusSetxLots";
        }

        PlaySound.Once(Audio, "M_flitSpinStart4thReel", "SwitchAnim");
        GetBonusSwitch()?.SpinReel(_spinTimeExtension1 + Band(0), endSound);
    }

    /// <summary>The second bonus tier: the bonus reel and every ordinary reel together.</summary>
    public void AnimateBonus2Spin()
    {
        MageDice.SpeedAnimation = false;
        PlaySound.Once(Audio, "M_flitSpinGyro", "SwitchAnim");
        GetBonusSwitch()?.SpinReel(_spinTimeExtension1 + Band(0), null);

        for (var reel = 0; reel < Variant.GameType; reel++)
        {
            Animating(reel).SpinReel(_spinTimeExtension1 + Band(0), null);
        }
    }

    /// <summary>
    /// Steps the payout celebration on by one.
    /// </summary>
    /// <remarks>
    /// TWO PASSES OVER THE CHAINS. The first lights all of them at once with no amounts — that is
    /// the <c>chainIndex &lt; 0</c> branch, which runs the whole list and then rewinds. The second
    /// walks them one at a time, each with its floating amount. A single chain skips the first pass
    /// entirely, because showing one win twice is just a stutter.
    /// </remarks>
    public void PlayNextWinAnimation()
    {
        if (_chainIndex < 0)
        {
            if (Chains.Count > 1)
            {
                while (_chainIndex < Chains.Count)
                {
                    _chainIndex++;
                    if (_chainIndex < Chains.Count)
                    {
                        AnimateChain(false);
                    }
                }
            }

            _chainIndex = 0;
            return;
        }

        if (_chainIndex >= Chains.Count)
        {
            _chainIndex = 0;
            _firstPayoutSequence = false;
            return;
        }

        AnimateChain(true);
        _chainIndex++;
    }

    /// <summary>
    /// Lights one chain: its reels, the lines between them, and optionally what it paid.
    /// </summary>
    /// <remarks>
    /// THE RING TIER IS THE CHAIN'S SIZE, not its value: more than five reels gets the top ring,
    /// more than three — or anything won inside the bonus — gets the middle one. A big win and a
    /// valuable win are not the same thing, and the board says which this was.
    /// </remarks>
    private void AnimateChain(bool showFloaty)
    {
        List<Switch> chain = Chains[_chainIndex];

        var tier = 0;
        if (chain.Count > 5)
        {
            tier = 2;
        }
        else if (chain.Count > 3 || BonusUnlocked > 0)
        {
            tier = 1;
        }

        for (var i = 0; i < chain.Count; i++)
        {
            if (chain[i] is AnimatingSwitch lit)
            {
                lit.PlayWinAnimation(tier);
            }

            for (int j = i + 1; j < chain.Count; j++)
            {
                IPubClip? line = _boardMC.GetChild("line" + chain[i].ID + chain[j].ID);
                if (line is not null)
                {
                    AnimateWin.Timed(line, 1, "Payout");
                }
            }
        }

        if (showFloaty)
        {
            int amount = Payouts[_chainIndex];
            int type = PayoutTypes[_chainIndex];
            string key = type switch
            {
                Token.TypeFreeSpin => "txtFloatyTextFreeSpin",
                Token.TypeBonusMult => "txtFloatyTextMultiplier",
                _ => "txtFloatyTextValue",
            };

            FloatyText.Timed(
                _boardMC, key,
                new Dictionary<string, string> { ["value"] = amount.ToString() },
                _winVars, 1, "Payout", _localizer);
        }

        PlaySound.Once(Audio, "SB_highlightGlow", null);
    }

    /// <summary>The idle shine, parked on its last frame so it is not showing.</summary>
    private void InitGlint()
    {
        IPubClip? glint = _boardMC.GetChild("ambient")?.GetChild("glint");
        glint?.GotoAndStop(glint.TotalFrames);
    }

    /// <summary>Runs the idle shine, which is what a board does when nobody is playing it.</summary>
    public void AnimateGlint()
    {
        IPubClip? glint = _boardMC.GetChild("ambient")?.GetChild("glint");
        if (glint is not null)
        {
            Animate.StartToEnd(glint, glint.TotalFrames / 60.0, "glint");
        }
    }
}
