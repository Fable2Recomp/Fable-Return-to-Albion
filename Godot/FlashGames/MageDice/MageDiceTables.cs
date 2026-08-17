using System.Collections.Generic;

namespace Refii.FlashGames.MageDice;

/// <summary>
/// One board type's worth of settings, as read from <c>magedice/settings.xml</c>.
/// </summary>
/// <remarks>
/// THE SHIPPED CODE HAS FORTY-FIVE SEPARATE FIELDS for this — <c>ReelImages</c>,
/// <c>ReelImagesB</c>, <c>ReelImagesC</c>, <c>Reel1Stops</c> … <c>Reel6StopsC</c>,
/// <c>ReelPayoutsx1</c> … <c>ReelPayoutsx6C</c> — three near-identical blocks distinguished only
/// by a suffix. They are gathered here instead, because the one thing that must not go wrong is a
/// setting binding to the wrong list: 41 variations each carry a full set, and a table that
/// silently took another board type's payouts would draw one paytable and settle by another.
/// Grouping them means the binding is stated once, in <c>MageDiceConfig</c>, and can be read at a
/// glance.
/// </remarks>
public sealed class MageDiceTables
{
    public MageDiceTables(int defaultBoardType) => BoardType = defaultBoardType;

    /// <summary>How many reels a table built from these tables has: 3, 4 or 6.</summary>
    public int BoardType { get; set; }

    /// <summary>Symbol artwork export names, indexed by token.</summary>
    public List<string> ReelImages { get; } = new();

    /// <summary>Each token's <see cref="Token"/> type, indexed by token.</summary>
    public List<int> ReelTypes { get; } = new();

    /// <summary>Six weighted stop tables, one per reel.</summary>
    public List<int>[] Stops { get; } =
    {
        new(), new(), new(), new(), new(), new(),
    };

    /// <summary>Six payout tables, indexed by match count minus one.</summary>
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
