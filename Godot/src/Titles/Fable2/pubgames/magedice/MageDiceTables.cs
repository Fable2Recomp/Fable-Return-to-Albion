using System.Collections.Generic;

namespace Refii.Titles.Fable2.PubGames.MageDice;

public sealed class MageDiceTables
{
    public MageDiceTables(int defaultBoardType) => BoardType = defaultBoardType;

    public int BoardType { get; set; }

    public List<string> ReelImages { get; } = new();

    public List<int> ReelTypes { get; } = new();

    public List<int>[] Stops { get; } =
    {
        new(), new(), new(), new(), new(), new(),
    };

    public List<int>[] Payouts { get; } =
    {
        new(), new(), new(), new(), new(), new(),
    };

    public GameVariant ToVariant(int minBet) =>
        new(
            "?", BoardType, minBet, ReelImages, ReelTypes,
            new List<List<int>>
            {
                Payouts[0], Payouts[1], Payouts[2], Payouts[3], Payouts[4], Payouts[5],
            },
            Stops[0], Stops[1], Stops[2], Stops[3], Stops[4], Stops[5]);
}
