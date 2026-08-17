namespace Refii.FlashGames.Framework;

public interface IPubGameSession
{
    bool IsFinished { get; }

    long FinalGold { get; }

    long FinalDebt { get; }
}
