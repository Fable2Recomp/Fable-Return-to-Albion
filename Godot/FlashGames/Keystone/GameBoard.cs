using System;
using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.Keystone;

public sealed class GameBoard
{
    private static readonly int[] ArchStoneAngles =
        { 180, 166, 155, 144, 133, 123, 112, 99, 81, 68, 57, 47, 36, 25, 14, 0 };

    private readonly Keystone _keystone;
    private readonly IPubClip _root;
    private readonly IPubRandom _random;

    private readonly List<ChipStack>[] _chipStacks =
        new List<ChipStack>[KeystoneSpace.SpaceCount];

    private readonly List<ChipStack>[] _payoutStacks =
        new List<ChipStack>[KeystoneSpace.SpaceCount];

    private readonly List<ChipStackMulti> _playerStacks = new();
    private readonly List<Die> _dice = new();
    private readonly Stone?[] _stones = new Stone?[KeystoneSpace.SpaceCount];

    public GameBoard(Keystone keystone, IPubClip root, IPubRandom random)
    {
        _keystone = keystone;
        _root = root;
        _random = random;
    }

    public int MaxPlayersPerBoard { get; private set; }

    public IReadOnlyList<ChipStackMulti> PlayerStacks => _playerStacks;

    public IReadOnlyList<Die> Dice => _dice;

    public void Init()
    {
        InitChipStacks();
        InitDice();
        InitStones();

        IPubClip? safeArea = _root.GetChild("safeArea");
        if (safeArea is not null)
        {
            safeArea.Visible = false;
        }

        ClearAllHilights();
        ClearAllFoci();
        ClearAllNumbers();
        ResetAllArrows();
        LocalizeBoard();
    }

    public void LocalizeBoard()
    {
        IPubClip? text = _root.GetChild("tableText");
        if (text is null)
        {
            return;
        }

        Localize(text.GetChild("keystoneLabel")?.GetChild("label"), "txtKeystoneSpace", null);
        Localize(text.GetChild("pairLabel")?.GetChild("label"), "txtPairSpace", null);
        Localize(text.GetChild("tripsLabel")?.GetChild("label"), "txtTripsSpace", null);
        Localize(text.GetChild("straightLabel")?.GetChild("label"), "txtStraightSpace", null);
    }

    public void InitTourneyStats()
    {
        if (!_keystone.Tournament)
        {
            _root.GetChild("tourneyStats")?.RemoveMovieClip();
            return;
        }

        UpdateTourneyStats();
    }

    public void UpdateTourneyStats()
    {
        Localize(
            _root.GetChild("tourneyStats")?.GetChild("label"),
            "txtTournamentGamesRolls",
            new Dictionary<string, string>
            {
                ["game"] = _keystone.CurrentGame.ToString(),
                ["total"] = _keystone.TournamentRounds.ToString(),
                ["roll"] = _keystone.CurrentRoll.ToString(),
            });
    }

    public static double GetStackAngle(float x, float y) => Math.Atan2(y - 1100, x - 900);

    public void SeatPlayerOnMainBoard(int seat, KeystonePlayer player)
    {
        ChipStackMulti? stack = GetPlayerStack(seat);
        if (stack is not null)
        {
            stack.Value = player.Chips;
        }

        IPubClip? label = PlayerNameLabel(seat);
        if (label is not null)
        {
            label.Text = WrapPlayerName(_keystone.Localizer.LocalizeString(
                "txtPlayerName", new Dictionary<string, string> { ["value"] = player.Name }));
        }

        _keystone.SetLastChips(seat, player.Chips);
        UpdatePlayerChipDisplay(seat, player.Chips);
    }

    public void UnseatPlayerOnMainBoard(int seat)
    {
        ChipStackMulti? stack = GetPlayerStack(seat);
        if (stack is not null)
        {
            stack.Value = 0;
        }

        IPubClip? label = PlayerNameLabel(seat);
        if (label is not null)
        {
            label.Text = _keystone.Localizer.LocalizeString("txtEmptySeat", null);
        }

        _keystone.SetLastChips(seat, 0);
        UpdatePlayerChipDisplay(seat, 0);
    }

    public void UpdatePlayerChipDisplay(int seat, int value)
    {
        Localize(
            _root.GetChild("playerNames")?.GetChild("chipTotal" + seat)?.GetChild("label"),
            "txtChipTotal",
            new Dictionary<string, string> { ["value"] = value.ToString() });
    }

    public void HideUnusedPlayerUI(int seatsPerTable)
    {
        IPubClip? names = _root.GetChild("playerNames");
        if (names is null)
        {
            return;
        }

        for (int seat = seatsPerTable; seat < MaxPlayersPerBoard; seat++)
        {
            IPubClip? name = names.GetChild("player" + seat + "name");
            if (name is not null)
            {
                name.Visible = false;
            }

            IPubClip? total = names.GetChild("chipTotal" + seat);
            if (total is not null)
            {
                total.Visible = false;
            }
        }
    }

    public static string WrapPlayerName(string name)
    {
        int space = name.LastIndexOf(' ');
        return space > 0 ? name[..space] + "\n" + name[(space + 1)..] : name;
    }

    private IPubClip? PlayerNameLabel(int seat) =>
        _root.GetChild("playerNames")?.GetChild("player" + seat + "name")?.GetChild("label");

    public void InitCenter() => SetAlpha("fade", 0);

    private float? _noticeY1;
    private float _noticeY2;

    public void InitNotice() => SetAlpha("notice", 0);

    public void ShowNotice(bool show, double time, bool blocking)
    {
        IPubClip? notice = _root.GetChild("notice");
        if (notice is not null)
        {
            FadeClip(notice, show, time, blocking);
        }
    }

    public void SetNoticeText(
        string id, IReadOnlyDictionary<string, string>? vars,
        string id2, IReadOnlyDictionary<string, string>? vars2,
        string id3, IReadOnlyDictionary<string, string>? vars3,
        string frame)
    {
        IPubClip? notice = _root.GetChild("notice");
        if (notice is null)
        {
            return;
        }

        IPubClip? text1 = notice.GetChild("noticeText");
        IPubClip? text2 = notice.GetChild("noticeText2");

        if (_noticeY1 is null)
        {
            _noticeY1 = text1?.Y ?? 0f;
            _noticeY2 = text2?.Y ?? 0f;
        }

        Localize(text1, id, vars);
        if (text1 is not null)
        {
            text1.Y = _noticeY1.Value +
                ((text1.Height - (text1.TextHeight * text1.YScale / 100f)) / 2f);
        }

        Localize(text2, id2, vars2);
        if (text2 is not null)
        {
            text2.Y = _noticeY2 +
                ((text2.Height - (text2.TextHeight * text2.YScale / 100f)) / 2f);
        }

        IPubClip? button = notice.GetChild("button1");
        button?.GetChild("button")?.GotoAndStop(frame);
        Localize(button?.GetChild("label"), id3, vars3);

        if (text1 is not null)
        {
            text1.Alpha = 100;
        }

        if (text2 is not null)
        {
            text2.Alpha = 0;
        }

        if (button is not null)
        {
            button.Alpha = 0;
        }
    }

    public void SwapNoticeText(bool forward, double time, bool blocking)
    {
        IPubClip? notice = _root.GetChild("notice");
        if (notice is null)
        {
            return;
        }

        IPubClip? text1 = notice.GetChild("noticeText");
        if (text1 is not null)
        {
            FadeClip(text1, !forward, time, blocking);
        }

        IPubClip? text2 = notice.GetChild("noticeText2");
        if (text2 is not null)
        {
            FadeClip(text2, forward, time, blocking);
        }

        IPubClip? button = notice.GetChild("button1");
        if (button is not null)
        {
            FadeClip(button, forward, time, blocking);
        }
    }

    public void InitSpaceStats()
    {
        SetAlpha("spaceStats", 0);
        IPubClip? stats = _root.GetChild("spaceStats");
        if (stats is null)
        {
            return;
        }

        Localize(stats.GetChild("label1"), "txtSpaceBetLabel", null);
        Localize(stats.GetChild("label2"), "txtSpaceOddsLabel", null);
    }

    /// <summary><c>ShowSpaceStats</c> (0x001785-0x0017b7): a 250 ms fade of the space panel.</summary>
    public void ShowSpaceStats(bool show, object? group, bool blocking)
    {
        IPubClip? stats = _root.GetChild("spaceStats");
        if (stats is not null)
        {
            FadeClip(stats, show, 250, blocking).Group = group;
        }
    }

    public void UpdateSpaceStats(int space)
    {
        IPubClip? stats = _root.GetChild("spaceStats");
        if (stats is null)
        {
            return;
        }

        Localize(stats.GetChild("label0"), "txtSpace_" + KeystoneRules.BetIdToString(space), null);
        Localize(
            stats.GetChild("value2"),
            _keystone.Bloodstone ? "txtSpaceOddsBloodstone" : "txtSpaceOddsValue",
            new Dictionary<string, string>
            {
                ["payout"] = _keystone.GetPayoutRate(space).ToString(),
                ["bet"] = _keystone.GetPayoutBet(space).ToString(),
            });

        UpdateSpaceStatsBet(space);
    }

    public void UpdateSpaceStatsBet(int space)
    {
        KeystonePlayer? human = _keystone.GetHumanPlayer();
        if (human is null)
        {
            return;
        }

        Localize(
            _root.GetChild("spaceStats")?.GetChild("value1"),
            "txtSpaceBetValue",
            new Dictionary<string, string> { ["value"] = human.Bets[space].ToString() });
    }

    public void InitBetStats()
    {
        IPubClip? stats = _root.GetChild("betStats");
        if (stats is null)
        {
            return;
        }

        if (_keystone.Bloodstone)
        {
            Localize(stats.GetChild("label1"), "txtArchRisk", null);
            Localize(stats.GetChild("label2"), "txtInsideRisk", null);
            Localize(stats.GetChild("label3"), "txtChipsAtRisk", null);
            Localize(stats.GetChild("label4"), "txtWin", null);
            return;
        }

        Localize(stats.GetChild("label1"), "txtArchBet", null);
        Localize(stats.GetChild("label3"), "txtInsideBet", null);
        Localize(stats.GetChild("label2"), "txtArchWin", null);
        Localize(stats.GetChild("label4"), "txtInsideWin", null);
    }

    public void UpdateBetStats(int archBets, int archWins, int insideBets, int insideWins)
    {
        IPubClip? stats = _root.GetChild("betStats");
        if (stats is null)
        {
            return;
        }

        Value(stats.GetChild("value1"), "txtArchBetValue", archBets);
        Value(stats.GetChild("value2"), "txtArchWinValue", archWins);
        Value(stats.GetChild("value3"), "txtInsideBetValue", insideBets);
        Value(stats.GetChild("value4"), "txtInsideWinValue", insideWins);
    }

    public void ShowLegend(
        int index, string? frame, string? locId,
        IReadOnlyDictionary<string, string>? locVars, bool show, double time, bool blocking)
    {
        IPubClip? button = _root.GetChild("button" + index);
        if (button is null)
        {
            return;
        }

        if (show)
        {
            if (frame is not null)
            {
                button.GetChild("button")?.GotoAndStop(frame);
            }

            if (locId is not null)
            {
                Localize(button.GetChild("label"), locId, locVars);
            }
        }

        FadeClip(button, show, time, blocking);
    }

    private void Value(IPubClip? clip, string key, int value) =>
        Localize(clip, key, new Dictionary<string, string> { ["value"] = value.ToString() });

    private void Localize(
        IPubClip? clip, string key, IReadOnlyDictionary<string, string>? variables)
    {
        if (clip is not null)
        {
            clip.Text = _keystone.Localizer.LocalizeString(key, variables);
        }
    }

    public void InitLegend(int index)
    {
        IPubClip? button = _root.GetChild("button" + index);
        if (button is null)
        {
            return;
        }

        button.Alpha = 0;
        button.GetChild("button")?.GotoAndStop(1);
    }

    private void SetAlpha(string name, float alpha)
    {
        IPubClip? clip = _root.GetChild(name);
        if (clip is not null)
        {
            clip.Alpha = alpha;
        }
    }

    public int MaxBettorsOnSpace(int id) => _chipStacks[id]?.Count ?? 0;

    public ChipStack? GetChipStack(int playerIndex, int id)
    {
        List<ChipStack>? stacks = _chipStacks[id];
        if (stacks is null)
        {
            return null;
        }

        int free = -1;
        for (int i = 0; i < stacks.Count; i++)
        {
            if (stacks[i].Owner == playerIndex)
            {
                return stacks[i];
            }

            if (free < 0 && stacks[i].Owner < 0)
            {
                free = i;
            }
        }

        if (free < 0)
        {
            return null;
        }

        stacks[free].Owner = playerIndex;
        List<ChipStack>? payouts = _payoutStacks[id];
        if (payouts is not null && free < payouts.Count)
        {
            payouts[free].Owner = playerIndex;
        }

        return stacks[free];
    }

    public ChipStack? GetPayoutStack(int playerIndex, int id)
    {
        List<ChipStack>? stacks = _payoutStacks[id];
        if (stacks is null)
        {
            return null;
        }

        foreach (ChipStack stack in stacks)
        {
            if (stack.Owner == playerIndex)
            {
                return stack;
            }
        }

        return null;
    }

    public ChipStackMulti? GetPlayerStack(int playerIndex) =>
        playerIndex >= 0 && playerIndex < _playerStacks.Count ? _playerStacks[playerIndex] : null;

    public Stone? GetStone(int id) => _stones[id];

    public double DropStoneIntoArch(Stone stone, double delay, object? group)
    {
        const double frameTime = 667;
        stone.Removed = false;

        var move = new AnimatePos(
            stone.OffX, stone.OffY, stone.BaseX, stone.BaseY, stone,
            1, delay, AnimateBase.StyleLinear)
        {
            Group = group,
        };
        _keystone.CallbackManager.AddCallbackObject(move);

        var frames = new AnimateFrame(
            1, stone.Clip.TotalFrames, stone, frameTime, delay, AnimateBase.StyleLinear)
        {
            Group = group,
        };
        _keystone.CallbackManager.AddCallbackObject(frames);

        return frameTime + delay;
    }

    public void RevealStones(object? group)
    {
        _keystone.PlayDelayedSound(0, "K_stonesRevealShort", group);

        double last = 0;
        for (int i = 0; i < 8; i++)
        {
            foreach (int id in new[] { i + KeystoneSpace.Out3, KeystoneSpace.Out18 - i })
            {
                Stone? stone = _stones[id];
                if (stone is null || !stone.Removed)
                {
                    continue;
                }

                double end = DropStoneIntoArch(
                    stone, (i * 200) + (_random.NextDouble() * 200), group);
                last = Math.Max(last, end);
            }
        }

        _keystone.PlayDelayedSound(last, "K_stonesRevealShortEnd", group);
    }

    public void CollapseArch(object? group)
    {
        _keystone.PlayDelayedSound(0, "K_collapseArch", group);

        const double drop = 1300;
        int leftIndex = 0;
        int rightIndex = 0;

        for (int i = 0; i < 8; i++)
        {
            CollapseStone(_stones[i + KeystoneSpace.Out3], ref leftIndex, drop, group);
            CollapseStone(_stones[KeystoneSpace.Out18 - i], ref rightIndex, drop, group);
        }
    }

    private void CollapseStone(Stone? stone, ref int index, double drop, object? group)
    {
        if (stone is null || stone.Removed)
        {
            return;
        }

        stone.Removed = true;
        double delay = (index * 150) + (_random.NextDouble() * 100);
        _keystone.PlayDelayedSound(delay, "K_collapseStone", group);

        var fall = new AnimatePos(
            stone.BaseX, stone.BaseY, stone.BaseX, stone.BaseY + drop, stone,
            800, delay, AnimateBase.StyleEaseOut)
        {
            Group = group,
        };
        _keystone.CallbackManager.AddCallbackObject(fall);
        index++;
    }

    public void RemoveStone(int id, object? group)
    {
        Stone? stone = _stones[id];
        if (stone is null)
        {
            return;
        }

        stone.Removed = true;
        double delay = _random.NextDouble() * 200;

        bool keystone = id is KeystoneSpace.Out3 or KeystoneSpace.Out10
            or KeystoneSpace.Out11 or KeystoneSpace.Out18;
        _keystone.PlayDelayedSound(delay, keystone ? "K_removeKeystone" : "K_removeStone", group);

        var slide = new AnimatePos(
            stone.BaseX, stone.BaseY, stone.OffX, stone.OffY, stone,
            800, delay, AnimateBase.StyleEaseOut)
        {
            Group = group,
        };
        _keystone.CallbackManager.AddCallbackObject(slide);
    }

    public void RollDice(int roll1, int roll2, int roll3, bool cheat, object? group)
    {
        const double spread = 1000;
        const double lift = 275;
        const double sideways = 80;
        const double spinFrames = 120;
        double spinTime = spinFrames * AnimateBase.FrameTime60Fps;

        int[] rolls = { roll1, roll2, roll3 };
        if (cheat)
        {
            for (int i = 0; i < rolls.Length; i++)
            {
                rolls[i] = Die.GetPreCheatRoll(rolls[i]);
            }
        }

        double latest = 0;
        double earliest = spread;

        for (int i = 0; i < 3 && i < _dice.Count; i++)
        {
            Die die = _dice[i];
            die.SetRoll(rolls[i], false, 1);

            int totalFrames = die.TotalFrames;
            int startFrame = Math.Max(1, totalFrames - (int)spinFrames);

            double delay = _random.NextDouble() * spread;
            latest = Math.Max(latest, delay);
            earliest = Math.Min(earliest, delay);

            float x = i switch
            {
                0 => (float)(die.BaseX - sideways - (sideways * _random.NextDouble())),
                2 => (float)(die.BaseX + sideways + (sideways * _random.NextDouble())),
                _ => die.BaseX,
            };

            var toss = new AnimatePos(
                x, die.BaseY, x, die.BaseY - lift - (lift * _random.NextDouble()), die,
                1, delay, AnimateBase.StyleLinear)
            {
                Group = group,
            };
            _keystone.CallbackManager.AddCallbackObject(toss);

            var spin = new AnimateFrame(
                startFrame, totalFrames, die, spinTime, delay, AnimateBase.StyleLinear)
            {
                Group = group,
            };
            _keystone.CallbackManager.AddCallbackObject(spin);
        }

        _keystone.PlayDelayedSound(earliest, "K_rollDiceShort", group);
        _keystone.PlayDelayedSound(latest + spinTime, "K_rollDiceShortEnd", group);
    }

    public double PresentDice(double time, object? group)
    {
        _keystone.PlayDelayedSound(0, "K_presentDice", group);

        double longest = 0;
        foreach (Die die in _dice)
        {
            double delay = Math.Floor(_random.NextDouble() * time / 2);
            longest = Math.Max(longest, delay);

            var settle = new AnimatePos(
                die.X, die.Y, die.BaseX, die.BaseY, die,
                time, delay, AnimateBase.StyleEaseOut)
            {
                Group = group,
            };
            _keystone.CallbackManager.AddCallbackObject(settle);
        }

        return time + longest;
    }

    public void RemoveDice(double time, object? group, bool blocking)
    {
        _keystone.PlayDelayedSound(0, "K_removeDice", group);

        const double drop = 500;
        for (int i = 0; i < _dice.Count; i++)
        {
            Die die = _dice[i];
            double delay = _random.NextDouble() * time / 3;
            _keystone.PlayDelayedSound(delay, "K_removeDie", group);

            var fall = new AnimatePos(
                die.X, die.Y, die.X, die.Y + drop, die,
                time - delay, delay, AnimateBase.StyleEaseOut)
            {
                Group = group,
                Blocking = blocking && i == _dice.Count - 1,
            };
            _keystone.CallbackManager.AddCallbackObject(fall);
        }
    }

    public void SetDiceValues(int roll1, int roll2, int roll3)
    {
        int[] rolls = { roll1, roll2, roll3 };
        for (int i = 0; i < 3 && i < _dice.Count; i++)
        {
            _dice[i].SetRoll(rolls[i]);
        }
    }

    public void ShowNumberAbove(int id, bool show, object? group, bool blocking, double delay)
    {
        IPubClip? number = GetNumberAbove(id);
        if (number is null)
        {
            return;
        }

        if (show)
        {
            _keystone.PlayDelayedSound(delay, "K_revealSeeker", group);
        }

        var fade = new AnimateAlpha(
            number.Alpha, show ? 100 : 0, number, 300, delay, AnimateBase.StyleLinear)
        {
            Blocking = blocking,
            Group = group,
        };
        _keystone.CallbackManager.AddCallbackObject(fade);
    }

    public void ShowHilight(
        int id, bool show, object? group, bool blocking, double delay, int cueCount)
    {
        IPubClip? glow = GetHilight(id);
        if (glow is null)
        {
            return;
        }

        if (show)
        {
            int cue = Math.Min(cueCount, 8);
            _keystone.PlayDelayedSound(
                delay,
                _keystone.Bloodstone ? "BS_Loser" + cue : "K_showWinHilight0" + cue,
                group);
        }

        _keystone.DelayCallback(delay, _ =>
        {
            if (show)
            {
                glow.Play();
            }
            else
            {
                glow.Stop();
            }
        });

        var fade = new AnimateAlpha(
            glow.Alpha, show ? 100 : 0, glow, 300, delay, AnimateBase.StyleLinear)
        {
            Blocking = blocking,
            Group = group,
        };
        _keystone.CallbackManager.AddCallbackObject(fade);
    }

    public void ShowFocus(int id, bool show, bool fade, object? group, bool blocking)
    {
        IPubClip? over = GetFocus(id);
        if (over is null)
        {
            return;
        }

        if (show)
        {
            UpdateSpaceStats(id);
        }

        float from = fade ? over.Alpha : (show ? 100 : 0);
        var animation = new AnimateAlpha(
            from, show ? 100 : 0, over, 250, 0, AnimateBase.StyleLinear)
        {
            Blocking = blocking,
            Group = group,
        };
        _keystone.CallbackManager.AddCallbackObject(animation);
    }

    public void ShowJackpot(bool blocking, object? group)
    {
        _keystone.PlayDelayedSound(1, "W_jackpot", group);
        _keystone.PlayDelayedSound(1, "applause05_jackpot", group);

        var jackpot = new AnimateJackpot(
            "txtJackpot", _keystone.Localizer, _root, 2000, 1, AnimateBase.StyleLinear)
        {
            Blocking = blocking,
            Group = group,
        };
        _keystone.CallbackManager.AddCallbackObject(jackpot);
    }

    public const double PulseTime = 200;
    public const string PulseGroup = "dg";

    public AnimatePulse PulseClip(IPubClip clip, double delay)
    {
        var pulse = new AnimatePulse(clip, PulseTime, delay) { Group = PulseGroup };
        _keystone.CallbackManager.AddCallbackObject(pulse);
        return pulse;
    }

    public void PulseLegend(int index)
    {
        IPubClip? inner = _root.GetChild("button" + index)?.GetChild("button");
        if (inner is not null)
        {
            PulseClip(inner, 0);
        }
    }

    public void PulseNoticeButton()
    {
        IPubClip? inner = _root.GetChild("notice")?.GetChild("button1")?.GetChild("button");
        if (inner is not null)
        {
            PulseClip(inner, 0);
        }
    }

    public AnimateAlpha FadeClip(IPubClip clip, bool show, double time, bool blocking)
    {
        var fade = new AnimateAlpha(
            clip.Alpha, show ? 100 : 0, clip, time, 0, AnimateBase.StyleLinear)
        {
            Blocking = blocking,
        };
        _keystone.CallbackManager.AddCallbackObject(fade);
        return fade;
    }

    public void ShowCenter(bool show, double time, bool blocking)
    {
        IPubClip? fade = _root.GetChild("fade");
        if (fade is null)
        {
            return;
        }

        var animation = new AnimateAlpha(
            fade.Alpha, show ? 0 : 100, fade, time, 0, AnimateBase.StyleLinear)
        {
            Blocking = blocking,
        };
        _keystone.CallbackManager.AddCallbackObject(animation);
    }

    public void SetStoneOffScreen(int id) => _stones[id]?.SetOffScreen();

    public void SetStoneOnArch(int id) => _stones[id]?.SetOnArch();

    public void ClearAllNumbers()
    {
        IPubClip? numbers = _root.GetChild("numbersAbove");
        if (numbers is null)
        {
            return;
        }

        for (int i = 3; i <= 18; i++)
        {
            IPubClip? number = numbers.GetChild("number" + i);
            if (number is not null)
            {
                number.Alpha = 0;
            }
        }
    }

    public IPubClip? GetNumberAbove(int id) =>
        _root.GetChild("numbersAbove")?.GetChild("number" + (id - KeystoneSpace.Out3 + 3));

    public void ClearAllHilights()
    {
        IPubClip? hilights = _root.GetChild("hilights");
        if (hilights is null)
        {
            return;
        }

        for (int i = KeystoneSpace.FirstBetSpace; i <= KeystoneSpace.LastBetSpace; i++)
        {
            IPubClip? glow = hilights.GetChild(KeystoneRules.BetIdToString(i) + "glow");
            if (glow is null)
            {
                continue;
            }

            glow.Stop();
            if (_keystone.Bloodstone)
            {
                glow.SetTint(0.02f, 0.02f, 0.02f, 0, 0, 0);
            }

            glow.Alpha = 0;
        }
    }

    public IPubClip? GetHilight(int id) =>
        _root.GetChild("hilights")?.GetChild(KeystoneRules.BetIdToString(id) + "glow");

    public int TutorialHilightDepth { get; set; }

    private IPubClip? PlayerName(int seat) =>
        _root.GetChild("playerNames")?.GetChild("player" + seat + "name");

    private IPubClip? ChipTotal(int seat) =>
        _root.GetChild("playerNames")?.GetChild("chipTotal" + seat);

    public void HidePlayerName(int seat)
    {
        IPubClip? name = PlayerName(seat);
        if (name is not null)
        {
            name.Visible = false;
        }
    }

    public void SetBetStatsAlpha(float alpha)
    {
        IPubClip? stats = _root.GetChild("betStats");
        if (stats is not null)
        {
            stats.Alpha = alpha;
        }
    }

    public void SetChipTotalAlpha(int seat, float alpha)
    {
        IPubClip? total = ChipTotal(seat);
        if (total is not null)
        {
            total.Alpha = alpha;
        }
    }

    public void FadeInPlayerStack(int seat, double time, object? group)
    {
        ChipStackMulti? stack = GetPlayerStack(seat);
        if (stack is null)
        {
            return;
        }

        var fade = new AnimateAlpha(0, 100, stack, time, 0, AnimateBase.StyleLinear)
        {
            Group = group,
        };
        _keystone.CallbackManager.AddCallbackObject(fade);
    }

    public void FadeInChipTotal(int seat, double time, object? group, bool blocking)
    {
        IPubClip? total = ChipTotal(seat);
        if (total is null)
        {
            return;
        }

        var fade = new AnimateAlpha(0, 100, total, time, 0, AnimateBase.StyleLinear)
        {
            Group = group,
            Blocking = blocking,
        };
        _keystone.CallbackManager.AddCallbackObject(fade);
    }

    public void ShowTutorialHilight(
        bool show, bool inner, object? group, bool blocking)
    {
        string export = inner ? "tutorial_hilites02" : "tutorial_hilites";
        IPubClip? hilights = _root.GetChild("tutorialHilights");

        if (show && hilights is null)
        {
            hilights = _root.AttachMovie(export, "tutorialHilights", TutorialHilightDepth);
            if (hilights is not null)
            {
                hilights.Alpha = 0;
            }
        }

        if (hilights is null)
        {
            return;
        }

        // Shown in 1 ms, hidden over 250 (0x005802-0x005872).
        var fade = new AnimateAlpha(
            show ? 0 : 100, show ? 100 : 0, hilights, show ? 1 : 250, 0, AnimateBase.StyleLinear)
        {
            Blocking = blocking,
            Group = group,
        };

        if (!show)
        {
            fade.SetEndCallback(_ => KillTutorialHilight(), null);
        }

        _keystone.CallbackManager.AddCallbackObject(fade);
    }

    public void KillTutorialHilight() => _root.GetChild("tutorialHilights")?.RemoveMovieClip();

    public void SwitchToLammer(ChipStack? stack)
    {
        if (stack is null)
        {
            return;
        }

        stack.Shadow?.RemoveMovieClip();
        stack.Shadow = null;

        IPubClip chips = stack.Stack;
        int depth = chips.GetDepth();
        string name = chips.Name;
        float x = chips.X;
        float y = chips.Y;
        float scale = chips.XScale;
        chips.RemoveMovieClip();

        IPubClip? lammer = _root.GetChild("chips")?.AttachMovie("lammer", name, depth);
        if (lammer is null)
        {
            return;
        }

        lammer.X = x;
        lammer.Y = y;
        lammer.XScale = scale;
        lammer.YScale = scale;

        stack.Stack = lammer;
        stack.Lammer = true;
        stack.Value = 0;
    }

    /// <summary>
    /// <c>ShowArrow</c> (0x001057-0x001102): the seeker ghost over a fallen stone,
    /// <c>stonesGone.ghostN</c>, on frame 2 when shown and 1 when not.
    /// </summary>
    public void ShowArrow(int id, bool show, double delay, object? group, bool blocking)
    {
        IPubClip? ghost = GetGhost(id - KeystoneSpace.Out3 + 3);
        if (show)
        {
            _keystone.PlayDelayedSound(delay, "K_moveSeeker", group);
        }

        int frame = show ? 2 : 1;
        var animation = new AnimateFrame(frame, frame, ghost, 1, delay, AnimateBase.StyleLinear)
        {
            Blocking = blocking,
            Group = group,
        };
        _keystone.CallbackManager.AddCallbackObject(animation);
    }

    /// <summary><c>ResetAllArrows</c> (0x000f9d-0x000fe8).</summary>
    public void ResetAllArrows()
    {
        for (int i = 3; i <= 18; i++)
        {
            GetGhost(i)?.GotoAndStop(1);
        }
    }

    private IPubClip? GetGhost(int number) =>
        _root.GetChild("stonesGone")?.GetChild("ghost" + number);

    public void ClearAllFoci()
    {
        IPubClip? overs = _root.GetChild("overs");
        if (overs is null)
        {
            return;
        }

        for (int i = KeystoneSpace.FirstBetSpace; i <= KeystoneSpace.LastBetSpace; i++)
        {
            IPubClip? over = overs.GetChild(KeystoneRules.BetIdToString(i) + "over");
            if (over is not null)
            {
                over.Alpha = 0;
            }
        }
    }

    public IPubClip? GetFocus(int id) =>
        _root.GetChild("overs")?.GetChild(KeystoneRules.BetIdToString(id) + "over");

    private void InitStones()
    {
        IPubClip? stones = _root.GetChild("stones");
        if (stones is null)
        {
            return;
        }

        for (int i = KeystoneSpace.Out3; i <= KeystoneSpace.Out18; i++)
        {
            IPubClip? clip = stones.GetChild(KeystoneRules.BetIdToString(i) + "stonein");
            if (clip is null)
            {
                continue;
            }

            var stone = new Stone(clip, ArchStoneAngles[i - KeystoneSpace.Out3], _keystone.SkinIndex);
            _stones[i] = stone;
            stone.SetOffScreen();
        }
    }

    private void InitDice()
    {
        IPubClip shadows = _root.CreateEmptyMovieClip("diceShadows", _root.GetNextHighestDepth());
        IPubClip dice = _root.CreateEmptyMovieClip("dice", _root.GetNextHighestDepth());

        int index = 0;
        while (true)
        {
            IPubClip? marker = _root.GetChild("die" + index);
            if (marker is null)
            {
                break;
            }

            _dice.Add(new Die(
                index, dice, shadows, marker.X, marker.Y, marker.XScale, marker.YScale, _random));
            index++;
            marker.RemoveMovieClip();
        }
    }

    private void InitChipStacks()
    {
        int seats = 1 + _keystone.NumberOfAIPlayers;
        IPubClip shadows = ShadowsClip();
        IPubClip chips = _root.CreateEmptyMovieClip("chips", _root.GetNextHighestDepth());
        IPubClip playerShadows =
            _root.CreateEmptyMovieClip("playerShadows", _root.GetNextHighestDepth());
        IPubClip playerChips =
            _root.CreateEmptyMovieClip("playerChips", _root.GetNextHighestDepth());

        var spaceClips = new List<IPubClip>();
        var playerClips = new List<IPubClip>();

        for (int space = KeystoneSpace.FirstBetSpace; space <= KeystoneSpace.LastBetSpace; space++)
        {
            string id = KeystoneRules.BetIdToString(space);
            var stacks = new List<ChipStack>();
            var payouts = new List<ChipStack>();
            _chipStacks[space] = stacks;
            _payoutStacks[space] = payouts;

            int seat = 0;
            while (true)
            {
                IPubClip? marker = _root.GetChild(id + "bet" + seat);
                if (marker is null)
                {
                    break;
                }

                if (seat < seats)
                {
                    BuildSpaceStack(
                        marker, chips, shadows, spaceClips, stacks, payouts);
                }

                marker.RemoveMovieClip();
                seat++;
            }
        }

        int player = 0;
        while (true)
        {
            IPubClip? marker = _root.GetChild("player" + player + "chips0");
            if (marker is null)
            {
                break;
            }

            if (player < seats)
            {
                BuildPlayerStack(player, playerChips, playerShadows, playerClips);
            }
            else
            {
                for (int i = 0; i < 3; i++)
                {
                    _root.GetChild("player" + player + "chips" + i)?.RemoveMovieClip();
                }
            }

            player++;
        }

        MaxPlayersPerBoard = player;

        SortByY(spaceClips);
        SortByY(playerClips);
    }

    private void BuildSpaceStack(
        IPubClip marker, IPubClip chips, IPubClip shadows,
        List<IPubClip> spaceClips, List<ChipStack> stacks, List<ChipStack> payouts)
    {
        string name = marker.Name;
        IPubClip? stackClip;
        IPubClip? shadowClip = null;

        if (_keystone.Bloodstone)
        {
            stackClip = chips.AttachMovie("lammer", name, chips.GetNextHighestDepth());
        }
        else
        {
            stackClip = chips.AttachMovie("chipStack", name, chips.GetNextHighestDepth());
            shadowClip = shadows.AttachMovie(
                "chipStackShadow", name + "shadow", shadows.GetNextHighestDepth());
        }

        if (stackClip is null)
        {
            return;
        }

        Copy(marker, stackClip);
        Copy(marker, shadowClip);
        spaceClips.Add(stackClip);

        IPubClip? payClip = chips.AttachMovie("chipStack", name + "pay", chips.GetNextHighestDepth());
        if (payClip is null)
        {
            return;
        }

        Copy(marker, payClip);
        payClip.Y = marker.Y + 0.1f;
        spaceClips.Add(payClip);

        // One init object carries on from the pay stack (0x003c48-0x003db8): y + 0.2 and
        // `_xscale = 100` written twice, so every label and the poof keep the marker's _yscale.
        IPubClip? winClip = AttachLabel(chips, marker, "chipnum_win", name + "win", spaceClips);
        IPubClip? winBigClip =
            AttachLabel(chips, marker, "chipnum_winBig", name + "winBig", spaceClips);
        IPubClip? lossClip = _keystone.Tutorial || _keystone.Bloodstone
            ? AttachLabel(chips, marker, "chipnum_loss", name + "loss", spaceClips)
            : null;
        IPubClip? poofClip = AttachLabel(chips, marker, "chipPoof", name + "poof", spaceClips);

        var stack = new ChipStack(
            stackClip, shadowClip, poofClip, null, null, lossClip, _keystone.Bloodstone, _random)
        {
            Angle = GetStackAngle(stackClip.X, stackClip.Y),
        };
        stacks.Add(stack);

        var payout = new ChipStack(payClip, null, null, winClip, winBigClip, null, false, _random)
        {
            Angle = stack.Angle,
        };
        payouts.Add(payout);
    }

    private static IPubClip? AttachLabel(
        IPubClip chips, IPubClip marker, string export, string name, List<IPubClip> spaceClips)
    {
        IPubClip? clip = chips.AttachMovie(export, name, chips.GetNextHighestDepth());
        if (clip is null)
        {
            return null;
        }

        clip.X = marker.X;
        clip.Y = marker.Y + 0.2f;
        clip.XScale = 100;
        clip.YScale = marker.YScale;
        spaceClips.Add(clip);
        return clip;
    }

    private void BuildPlayerStack(
        int player, IPubClip playerChips, IPubClip playerShadows, List<IPubClip> playerClips)
    {
        var stacks = new IPubClip[3];
        var shadows = new IPubClip[3];

        for (int i = 0; i < 3; i++)
        {
            IPubClip? marker = _root.GetChild("player" + player + "chips" + i);
            if (marker is null)
            {
                return;
            }

            IPubClip? stack = playerChips.AttachMovie(
                "chipStack", marker.Name, playerChips.GetNextHighestDepth());
            IPubClip? shadow = playerShadows.AttachMovie(
                "chipStackShadow", marker.Name + "shadow", playerShadows.GetNextHighestDepth());
            if (stack is null || shadow is null)
            {
                return;
            }

            Copy(marker, stack);
            Copy(marker, shadow);
            stacks[i] = stack;
            shadows[i] = shadow;
            playerClips.Add(stack);
            marker.RemoveMovieClip();
        }

        var multi = new ChipStackMulti(
            stacks[0], shadows[0], stacks[1], shadows[1], stacks[2], shadows[2], _random)
        {
            Owner = player,
            Angle1 = GetStackAngle(stacks[0].X, stacks[0].Y),
            Angle2 = GetStackAngle(stacks[1].X, stacks[1].Y),
            Angle3 = GetStackAngle(stacks[2].X, stacks[2].Y),
        };

        _playerStacks.Add(multi);
    }

    private IPubClip ShadowsClip() =>
        _root.GetChild("shadows") ??
        _root.CreateEmptyMovieClip("shadows", _root.GetNextHighestDepth());

    private static void Copy(IPubClip from, IPubClip? to)
    {
        if (to is null)
        {
            return;
        }

        to.X = from.X;
        to.Y = from.Y;
        to.XScale = from.XScale;
        to.YScale = from.YScale;
    }

    private static void SortByY(List<IPubClip> clips)
    {
        clips.Sort((a, b) => a.Y.CompareTo(b.Y));
        for (int i = 0; i < clips.Count; i++)
        {
            clips[i].SwapDepths(i);
        }
    }
}
