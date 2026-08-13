using System.Collections.Generic;

namespace Refii.FlashGames.PubGames;

public sealed class Payout
{
    public Payout(int playerIndex, int space, int winnings, int bet, bool arch, int seat)
    {
        PlayerIndex = playerIndex;
        Space = space;
        Winnings = winnings;
        Bet = bet;
        Arch = arch;
        Seat = seat;
        Stack = null;
        PayStack = null;
    }

    public int PlayerIndex { get; }

    public int Space { get; }

    public int Winnings { get; }

    public int Bet { get; }

    public bool Arch { get; }

    public int Seat { get; }

    public ChipStack? Stack { get; set; }

    public ChipStack? PayStack { get; set; }
}

public sealed class BoardInfo
{
    private readonly List<KeystonePlayer?> _players = new();

    public BoardInfo(int boardIndex)
    {
        BoardIndex = boardIndex;
        AiBettorsOnSpace = new int[KeystoneSpace.SpaceCount];
        Payouts = new List<Payout>();
    }

    public int BoardIndex { get; }

    public int[] AiBettorsOnSpace { get; }

    public List<Payout> Payouts { get; }

    public void SetPlayer(int seat, KeystonePlayer? player)
    {
        while (_players.Count <= seat)
        {
            _players.Add(null);
        }

        KeystonePlayer? previous = _players[seat];
        if (previous is not null)
        {
            previous.IsSeated = false;
        }

        _players[seat] = player;
        if (player is not null)
        {
            player.IsSeated = true;
            player.ClearBets();
        }
    }

    public KeystonePlayer? GetPlayer(int seat) =>
        seat >= 0 && seat < _players.Count ? _players[seat] : null;
}
