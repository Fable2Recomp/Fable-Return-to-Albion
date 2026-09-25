using System;
using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.MageDice;

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

    private double Band(int pair)
    {
        if (pair + 1 >= _spinTimes.Count)
        {
            return 0;
        }

        return _spinTimes[pair] + (Random.NextDouble() * (_spinTimes[pair + 1] - _spinTimes[pair]));
    }

    public void AnimateBonusSpin()
    {
        GetBonusSwitch()?.Show();
        PlaySound.Once(Audio, "M_bonusFourthSwitchReveal", null);

        MageDice.SpeedAnimation = false;

        string? endSound = null;
        if (GetBonusSwitch() is { } bonus && Variant.Types[bonus.Value] == Token.TypeBonusReel)
        {
            endSound = "W_bonusSetxLots";
        }

        PlaySound.Once(Audio, "M_flitSpinStart4thReel", "SwitchAnim");
        GetBonusSwitch()?.SpinReel(_spinTimeExtension1 + Band(0), endSound);
    }

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

    public void PlayNextWinAnimation()
    {
        if (_chainIndex < 0)
        {
            if (Chains.Count > 1)
            {
                while (_chainIndex < Chains.Count)
                {
                    _chainIndex++;
                    AnimateChain(false);
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

    private void AnimateChain(bool showFloaty)
    {
        // AS2 0x00173f-0x00177a calls this once more with chainIndex == chains.length; chains[i] is
        // undefined there, so no switch or line animates and only SB_highlightGlow (0x001a50) plays.
        if (_chainIndex >= Chains.Count)
        {
            PlaySound.Once(Audio, "SB_highlightGlow", null);
            return;
        }

        List<Switch> chain = Chains[_chainIndex];

        // AS2 0x001860-0x00189f: the middle tier is chain.length > 3 || bonusLevel > 0.
        var tier = 0;
        if (chain.Count > 5)
        {
            tier = 2;
        }
        else if (chain.Count > 3 || BonusLevel > 0)
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

    private void InitGlint()
    {
        IPubClip? glint = _boardMC.GetChild("ambient")?.GetChild("glint");
        glint?.GotoAndStop(glint.TotalFrames);
    }

    public void AnimateGlint()
    {
        IPubClip? glint = _boardMC.GetChild("ambient")?.GetChild("glint");
        if (glint is not null)
        {
            Animate.StartToEnd(glint, glint.TotalFrames / 60.0, "glint");
        }
    }
}
