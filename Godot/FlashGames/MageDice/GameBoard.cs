using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.MageDice;

public class GameBoard
{
    protected readonly IPubRandom Random;
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

    public int RunningPayout => _runningPayout;

    public int BonusUnlocked => _bonusUnlocked;

    protected int BonusLevel => _bonusLevel;

    public bool Paid => _chains.Count > 0;

    public int BetMultiple => _betMultiple;

    public int NextBonusMultiplier => _nextBonusMultiplier;

    public int BonusMultiplier => _bonusMultiplier;

    public int Payout => _payout;

    public int FreeSpins => _freeSpins;

    public int TotalFreeSpins => _totalFreeSpins;

    public int NewFreeSpins => _newFreeSpins;

    public IReadOnlyList<List<Switch>> Chains => _chains;

    public IReadOnlyList<int> Payouts => _payouts;

    public IReadOnlyList<int> PayoutTypes => _payoutTypes;

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
