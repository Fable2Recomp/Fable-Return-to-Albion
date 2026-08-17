using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.MageDice;

/// <summary>
/// The rules of one Mage Dice machine: roll the reels, find the chains, work out what they pay.
/// </summary>
/// <remarks>
/// A 1:1 port of Mage Dice's <c>GameBoard.as</c>. Note the name is shared with
/// <see cref="Refii.FlashGames.Keystone.GameBoard"/> and the two have nothing in common — they are
/// different classes with the same name in two different SWFs, kept apart by namespace rather than
/// renamed, so each still matches its own source file.
///
/// TWO GLOBALS BECOME ARGUMENTS. The shipped code reaches for <c>Math.random()</c> and the global
/// <c>Audio</c>; both are passed in here so a seeded session replays and so nothing in the rules
/// layer reaches for engine state, exactly as the Keystone port does.
/// </remarks>
public class GameBoard
{
    protected readonly IPubRandom Random;
    // QUALIFIED, because Refii.Audio is a namespace and outranks the using here: C# walks outward
    // from Refii.FlashGames.MageDice and finds the NAMESPACE Refii.Audio before it consults an
    // imported type of the same name. Keystone writes a bare Audio only because it inherits
    // PubGame.Audio, and an inherited member outranks both.
    protected readonly Framework.Audio Audio;

    private readonly List<Switch> _switches = new();
    private readonly List<List<Switch>> _chains = new();
    private readonly List<int> _payouts = new();
    private readonly List<int> _payoutTypes = new();

    private int _betMultiple;
    private bool _betMultipleLimitHit;
    private int _bonusLevel;
    private int _bonusMultiplier;
    private int _bonusUnlocked;
    private int _freeSpins;
    private int _newFreeSpins;
    private int _nextBonusMultiplier;
    private int _payout;
    private int _runningPayout;
    private int _totalFreeSpins;

    public GameBoard(GameVariant v, IPubRandom random, Framework.Audio audio)
    {
        Variant = v;
        Random = random;
        Audio = audio;
        BonusSwitch = null;
        BoardType = Variant.GameType;
        _bonusUnlocked = 0;
        _bonusLevel = 0;
        _runningPayout = 0;
        _betMultiple = 1;
        _nextBonusMultiplier = 1;
        _bonusMultiplier = 1;
        _payout = 0;
        _freeSpins = 0;
        _totalFreeSpins = 0;
        _newFreeSpins = 0;
        _betMultipleLimitHit = false;
    }

    public GameVariant Variant { get; private set; }

    public int BoardType { get; }

    public Switch? BonusSwitch { get; protected set; }

    protected List<Switch> Switches => _switches;

    /// <summary>Winnings across a whole free-spin run, which is what the player is shown.</summary>
    public int RunningPayout => _runningPayout;

    public int BonusUnlocked => _bonusUnlocked;

    /// <summary>True when the last spin produced anything at all.</summary>
    public bool Paid => _chains.Count > 0;

    public int BetMultiple => _betMultiple;

    public int NextBonusMultiplier => _nextBonusMultiplier;

    public int BonusMultiplier => _bonusMultiplier;

    public int Payout => _payout;

    public int FreeSpins => _freeSpins;

    public int TotalFreeSpins => _totalFreeSpins;

    public int NewFreeSpins => _newFreeSpins;

    /// <summary>The chains that won, each a connected run of reels.</summary>
    public IReadOnlyList<List<Switch>> Chains => _chains;

    public IReadOnlyList<int> Payouts => _payouts;

    public IReadOnlyList<int> PayoutTypes => _payoutTypes;

    /// <summary>Mid-run free spins play themselves; the last one waits for the player.</summary>
    public bool AutoPlayFreeSpins() => _freeSpins > 0 && _freeSpins < _totalFreeSpins;

    public virtual Switch GetSwitch(int i) => _switches[i];

    public virtual void Dispose()
    {
        foreach (Switch s in _switches)
        {
            s.Dispose();
        }

        _switches.Clear();
    }

    protected virtual Switch BuildSwitch(int id, int reeltype, int value) =>
        new(Variant, id, reeltype, value, Random);

    protected virtual Switch BuildBonusSwitch(int id, int reeltype, int value) =>
        new(Variant, id, reeltype, value, Random);

    /// <summary>
    /// Builds the reels and wires up which of them count as adjacent.
    /// </summary>
    /// <remarks>
    /// ADJACENCY IS THE BOARD'S SHAPE and it is hand-written per board type, not derived. Three and
    /// four reels are fully connected — any match anywhere is a chain. SIX IS NOT: reel 0 touches
    /// only 1 and 2, reel 3 only 1 and 4, and so on, so the six-reel board is a shape where three
    /// matching symbols in the wrong places pay nothing. Deriving this from a rule would get it
    /// wrong; it is transcribed.
    ///
    /// THE OPENING SYMBOLS ARE SHUFFLED CHAIN TOKENS, so a machine does not always come to rest
    /// showing the same face. The shuffle is the shipped one — a forward pass swapping each entry
    /// with a uniformly random index, which is biased, and kept because the alternative is a
    /// different opening distribution than the game has.
    /// </remarks>
    public void Init()
    {
        BonusSwitch = null;

        var chainTokens = new List<int>();
        for (var i = 0; i < Variant.Types.Count; i++)
        {
            if (Variant.Types[i] == Token.TypeChain)
            {
                chainTokens.Add(i);
            }
        }

        for (var i = 0; i < chainTokens.Count; i++)
        {
            var j = (int)System.Math.Floor(Random.NextDouble() * chainTokens.Count);
            (chainTokens[i], chainTokens[j]) = (chainTokens[j], chainTokens[i]);
        }

        int Opening(int index) => index < chainTokens.Count ? chainTokens[index] : 0;

        switch (BoardType)
        {
            case 3:
                _switches.Add(BuildSwitch(0, 0, Opening(0)));
                _switches.Add(BuildSwitch(1, 1, Opening(1)));
                _switches.Add(BuildSwitch(2, 2, Opening(2)));
                if (Variant.HasBonusReel)
                {
                    BonusSwitch = BuildBonusSwitch(3, 3, Opening(4));
                    _switches.Add(BonusSwitch);
                }

                Wire(0, 1, 2);
                Wire(1, 0, 2);
                Wire(2, 0, 1);
                return;

            case 4:
                _switches.Add(BuildSwitch(0, 0, Opening(0)));
                _switches.Add(BuildSwitch(1, 1, Opening(1)));
                _switches.Add(BuildSwitch(2, 2, Opening(2)));
                _switches.Add(BuildSwitch(3, 3, Opening(3)));
                Wire(0, 1, 2, 3);
                Wire(1, 0, 2, 3);
                Wire(2, 0, 1, 3);
                Wire(3, 0, 1, 2);
                return;

            case 6:
                _switches.Add(BuildSwitch(0, 0, Opening(0)));
                _switches.Add(BuildSwitch(1, 1, Opening(1)));
                _switches.Add(BuildSwitch(2, 2, Opening(2)));
                _switches.Add(BuildSwitch(3, 3, Opening(3)));
                _switches.Add(BuildSwitch(4, 4, Opening(4)));
                _switches.Add(BuildSwitch(5, 5, Opening(5)));
                Wire(0, 1, 2);
                Wire(1, 0, 2, 3, 4);
                Wire(2, 0, 1, 4, 5);
                Wire(3, 1, 4);
                Wire(4, 1, 2, 3, 5);
                Wire(5, 2, 4);
                return;

            default:
                return;
        }
    }

    private void Wire(int from, params int[] neighbours)
    {
        foreach (int to in neighbours)
        {
            GetSwitch(from).AddNeighbor(GetSwitch(to));
        }
    }

    /// <summary>
    /// Clears the last result and takes a free spin if one is owed.
    /// </summary>
    /// <remarks>
    /// THE RUNNING TOTAL SURVIVES A FREE SPIN AND NOTHING ELSE. Free spins are one accumulating
    /// session — the player is shown what the whole run has won — so <see cref="RunningPayout"/>
    /// and <see cref="TotalFreeSpins"/> reset only on a spin that was paid for.
    /// </remarks>
    public virtual void NewSpin()
    {
        _chains.Clear();
        _payouts.Clear();
        _payoutTypes.Clear();
        _bonusUnlocked = 0;
        _bonusLevel = 0;

        if (_freeSpins > 0)
        {
            _freeSpins--;
        }
        else
        {
            _runningPayout = 0;
            _totalFreeSpins = 0;
        }

        _bonusMultiplier = _nextBonusMultiplier;
        _payout = 0;
        _newFreeSpins = 0;

        foreach (Switch s in _switches)
        {
            s.NewSpin();
        }
    }

    public void DebugForceSwitches(IReadOnlyList<int> ary)
    {
        for (var i = 0; i < Variant.GameType; i++)
        {
            GetSwitch(i).DebugForceValue(ary[i]);
        }

        Update();
    }

    public void BeginSpin()
    {
        for (var i = 0; i < Variant.GameType; i++)
        {
            GetSwitch(i).Roll();
        }

        Update();
    }

    public void DebugForceBonusSwitch(int val)
    {
        BonusSwitch?.DebugForceValue(val);
        Update();
    }

    public void BeginBonusSpin()
    {
        BonusSwitch?.Roll();
        Update();
    }

    public void DebugForceBonus2Switch(int val)
    {
        BonusSwitch?.DebugForceValue(val);
        for (var i = 0; i < Variant.GameType; i++)
        {
            GetSwitch(i).Roll();
        }

        Update();
    }

    public void BeginBonus2Spin()
    {
        BonusSwitch?.Roll();
        for (var i = 0; i < Variant.GameType; i++)
        {
            GetSwitch(i).Roll();
        }

        Update();
    }

    /// <summary>
    /// Records a winning group.
    /// </summary>
    /// <remarks>
    /// ONLY A CHAIN IS SCALED BY THE STAKE. A free-spin or multiplier win is a COUNT — two free
    /// spins is two free spins whatever the bet — so multiplying those by the stake would hand a
    /// high roller a hundred free spins off one symbol.
    /// </remarks>
    public void AddWinner(List<Switch> s, int val, int tokenType)
    {
        if (tokenType == Token.TypeChain)
        {
            val *= CostPerSpin() * _bonusMultiplier;
        }

        _chains.Add(s);
        _payouts.Add(val);
        _payoutTypes.Add(tokenType);
    }

    /// <summary>
    /// Reads the stopped reels and works out every win on the board.
    /// </summary>
    /// <remarks>
    /// THREE MODES, and the bonus ones are not variations of the ordinary one. At bonus level 1 the
    /// bonus reel alone is read — either it shows another bonus symbol and escalates to level 2, or
    /// it pays as though three of its symbol had landed. At level 2 EVERY reel pays at the
    /// three-match rate. Only the ordinary path walks the token table.
    ///
    /// A CHAIN IS A FLOOD FILL, not a run. Reels are a graph (see <see cref="Init"/>), so a win is
    /// a connected component of reels showing the same token, found breadth-first from each reel in
    /// turn with a visited flag stopping the same component being paid twice.
    /// </remarks>
    public void Update()
    {
        if (_bonusLevel == 1)
        {
            if (BonusSwitch is not null &&
                Variant.Types[BonusSwitch.Value] == Token.TypeBonusReel)
            {
                _bonusUnlocked = 2;
                _bonusLevel = 2;
            }
            else if (BonusSwitch is not null)
            {
                AddWinner(
                    new List<Switch> { BonusSwitch },
                    Variant.TokenPays(BonusSwitch.Value, 3),
                    Token.TypeChain);
                _bonusUnlocked = 0;
            }

            return;
        }

        if (_bonusLevel == 2)
        {
            foreach (Switch s in _switches)
            {
                AddWinner(
                    new List<Switch> { s }, Variant.TokenPays(s.Value, 3), Token.TypeChain);
            }

            _bonusUnlocked = 0;
            return;
        }

        for (var token = 0; token < Variant.Types.Count; token++)
        {
            switch (Variant.Types[token])
            {
                case Token.TypeBonusReel:
                {
                    var matched = 0;
                    for (var i = 0; i < Variant.GameType; i++)
                    {
                        if (GetSwitch(i).Value == token)
                        {
                            matched++;
                        }
                    }

                    // EXACTLY THREE, not three or more. A fourth bonus symbol on a four-reel board
                    // does not unlock anything, which is the shipped behaviour.
                    if (matched == 3)
                    {
                        _bonusUnlocked = 1;
                        _bonusLevel = 1;
                    }

                    break;
                }

                case Token.TypeBonusMult:
                {
                    for (var i = 0; i < Variant.GameType; i++)
                    {
                        Switch s = GetSwitch(i);
                        if (s.Value == token)
                        {
                            AddWinner(new List<Switch> { s }, 1, Token.TypeBonusMult);
                        }
                    }

                    break;
                }

                case Token.TypeFreeSpin:
                case Token.TypeChain:
                {
                    for (var i = 0; i < Variant.GameType; i++)
                    {
                        GetSwitch(i).Visited = false;
                    }

                    for (var i = 0; i < Variant.GameType; i++)
                    {
                        var group = new List<Switch>();
                        var queue = new Queue<Switch>();
                        queue.Enqueue(GetSwitch(i));

                        while (queue.Count > 0)
                        {
                            Switch s = queue.Dequeue();
                            if (s.Visited || s.Value != token)
                            {
                                continue;
                            }

                            group.Add(s);
                            s.Visited = true;
                            foreach (Switch neighbour in s.Neighbors)
                            {
                                if (!neighbour.Visited)
                                {
                                    queue.Enqueue(neighbour);
                                }
                            }
                        }

                        int pays = Variant.TokenPays(token, group.Count);
                        if (pays > 0)
                        {
                            group.Sort((a, b) => a.ID.CompareTo(b.ID));
                            AddWinner(group, pays, Variant.Types[token]);
                        }
                    }

                    break;
                }
            }
        }
    }

    /// <summary>
    /// Turns the recorded wins into chips, free spins and multipliers.
    /// </summary>
    /// <remarks>
    /// THE MULTIPLIER RESETS ONLY IF IT WAS NOT RAISED THIS SPIN. <c>nextBonusMultiplier</c>
    /// accumulates as multiplier symbols land and is moved into <c>bonusMultiplier</c> by the next
    /// <see cref="NewSpin"/>; the equality test at the end is how a multiplier that was just spent
    /// and not re-earned falls back to 1 while one earned this spin survives.
    /// </remarks>
    public void ResolveAndPay()
    {
        for (var i = 0; i < _chains.Count; i++)
        {
            int amount = _payouts[i];
            int type = _payoutTypes[i];
            if (type == Token.TypeFreeSpin)
            {
                _freeSpins += amount;
                _newFreeSpins += amount;
                _totalFreeSpins += amount;
            }
            else if (type == Token.TypeBonusMult)
            {
                _nextBonusMultiplier += amount;
            }
            else
            {
                _payout += amount;
                _runningPayout += amount;
            }
        }

        if (_bonusMultiplier == _nextBonusMultiplier)
        {
            _nextBonusMultiplier = 1;
        }
    }

    public int CostPerSpin() => Variant.Cost * _betMultiple;

    /// <summary>
    /// Moves the stake up or down, and says whether anything worth a sound happened.
    /// </summary>
    /// <remarks>
    /// THE BET IS LOCKED DURING A FREE-SPIN RUN OR A PENDING MULTIPLIER, because both were earned
    /// against the stake that was showing when they landed — letting the player raise the bet
    /// afterwards would pay out a run they never staked.
    /// </remarks>
    public bool ChangeBetMultiple(int x, int max, bool newPress)
    {
        var changed = false;

        if (FreeSpins < 1 && NextBonusMultiplier < 2)
        {
            int before = _betMultiple;
            _betMultiple = Util.Clamp(_betMultiple + x, 1, max);
            if (_betMultiple != before)
            {
                _betMultipleLimitHit = false;
                Audio.PlaySound("G_playerBetDing");
                changed = true;
            }
            else if (newPress || !_betMultipleLimitHit)
            {
                _betMultipleLimitHit = true;
                Audio.PlaySound("G_limitReached");
                changed = true;
            }
        }
        else if (newPress || !_betMultipleLimitHit)
        {
            _betMultipleLimitHit = true;
            Audio.PlaySound("G_limitReached");
            changed = true;
        }

        return changed;
    }

    public void UseFreeSpin() => _freeSpins -= 1;
}
