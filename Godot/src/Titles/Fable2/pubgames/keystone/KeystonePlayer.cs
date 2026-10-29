using Refii.Titles.Fable2.PubGames.Framework;

namespace Refii.Titles.Fable2.PubGames.Keystone;

public sealed class KeystonePlayer : Player
{
    public KeystonePlayer(
        int id, bool isAI, string name, int gold, int debt,
        int starRating, int points, int pointsForNextLevel)
        : base(id, isAI, name, gold, debt, starRating, points, pointsForNextLevel)
    {
        Bets = new int[KeystoneSpace.SpaceCount];
        BetsToMake = 0;
        BetWaitTime = 0;
        JustUnseated = false;
        Wins = 0;
        InsideWins = 0;
    }

    public int[] Bets { get; }

    public int Wins { get; set; }

    public int InsideWins { get; set; }

    public int BetsToMake { get; set; }

    public double BetWaitTime { get; set; }

    public bool JustUnseated { get; set; }

    public override void ClearBets()
    {
        for (int i = KeystoneSpace.FirstBetSpace; i <= KeystoneSpace.LastBetSpace; i++)
        {
            Bets[i] = 0;
        }
    }

    public int GetBetCount(bool arch)
    {
        int first = arch ? KeystoneSpace.Out3 : KeystoneSpace.FirstInnerSpace;
        int last = arch ? KeystoneSpace.Out18 : KeystoneSpace.LastInnerSpace;
        int count = 0;
        for (int i = first; i <= last; i++)
        {
            if (Bets[i] > 0)
            {
                count++;
            }
        }

        return count;
    }

    public int GetBetAmount(bool arch)
    {
        int first = arch ? KeystoneSpace.Out3 : KeystoneSpace.FirstInnerSpace;
        int last = arch ? KeystoneSpace.Out18 : KeystoneSpace.LastInnerSpace;
        int total = 0;
        for (int i = first; i <= last; i++)
        {
            total += Bets[i];
        }

        return total;
    }

    public int GetTotalBetAmount()
    {
        int total = 0;
        for (int i = KeystoneSpace.FirstBetSpace; i <= KeystoneSpace.LastBetSpace; i++)
        {
            total += Bets[i];
        }

        return total;
    }
}
