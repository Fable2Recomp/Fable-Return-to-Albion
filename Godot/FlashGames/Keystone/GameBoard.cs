using System;
using System.Collections.Generic;

namespace Refii.FlashGames.PubGames;

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

    public void InitNotice() => SetAlpha("notice", 0);

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
                ["payout"] = KeystoneRules.GetPayoutRate(space).ToString(),
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

    public void ShowLegend(int index, string? frame, string? locId, bool show)
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
                Localize(button.GetChild("label"), locId, null);
            }
        }

        button.Alpha = show ? 100 : 0;
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

    public void AdjustLayers(bool allowDebugMenu)
    {
        string[] order =
        {
            "tableText", "fade", "playerShadows", "playerChips", "stonesGone", "stones",
            "overs", "hilights", "shadows", "diceShadows", "chips", "dice", "numbersAbove",
            "playerNames", "betStats", "spaceStats", "tourneyStats",
            "button5", "button4", "button3", "button2", "button1",
            "notice", "overlays", "debug", "safeArea", "debugHUD",
        };

        int depth = _root.GetNextHighestDepth();
        foreach (string name in order)
        {
            _root.GetChild(name)?.SwapDepths(depth++);
        }

        if (allowDebugMenu)
        {
            return;
        }

        foreach (string name in new[] { "debug", "safeArea", "debugHUD" })
        {
            _root.GetChild(name)?.RemoveMovieClip();
        }
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
            glow.Alpha = 0;
        }
    }

    public IPubClip? GetHilight(int id) =>
        _root.GetChild("hilights")?.GetChild(KeystoneRules.BetIdToString(id) + "glow");

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

        IPubClip? winClip = chips.AttachMovie("chipnum_win", name + "win", chips.GetNextHighestDepth());
        IPubClip? winBigClip =
            chips.AttachMovie("chipnum_winBig", name + "winBig", chips.GetNextHighestDepth());
        foreach (IPubClip? clip in new[] { winClip, winBigClip })
        {
            if (clip is null)
            {
                continue;
            }

            clip.X = marker.X;
            clip.Y = marker.Y + 0.2f;
            clip.XScale = 100;
            clip.YScale = 100;
            spaceClips.Add(clip);
        }

        IPubClip? lossClip = null;
        if (_keystone.Tutorial || _keystone.Bloodstone)
        {
            lossClip = chips.AttachMovie(
                "chipnum_loss", name + "loss", chips.GetNextHighestDepth());
            if (lossClip is not null)
            {
                Copy(marker, lossClip);
                spaceClips.Add(lossClip);
            }
        }

        IPubClip? poofClip = chips.AttachMovie("chipPoof", name + "poof", chips.GetNextHighestDepth());
        if (poofClip is not null)
        {
            Copy(marker, poofClip);
            spaceClips.Add(poofClip);
        }

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
