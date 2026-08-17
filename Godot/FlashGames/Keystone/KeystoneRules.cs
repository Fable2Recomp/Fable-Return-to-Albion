using System;
using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.Keystone;

public readonly record struct AngleEntry(int Angle, int Id);

public static class KeystoneRules
{
    private static readonly int[] PayoutRates = BuildPayoutRates();
    private static readonly string[] BetIds = BuildBetIds();
    private static readonly AngleEntry[][] AngleMap = BuildAngleMap();
    private static readonly bool[][][][] WinnerTable = BuildWinnerTable();

    public static int DefaultPayoutRate(int id) =>
        id >= 0 && id < PayoutRates.Length ? PayoutRates[id] : 0;

    public static string BetIdToString(int id) =>
        id >= 0 && id < BetIds.Length ? BetIds[id] : string.Empty;

    public static IReadOnlyList<AngleEntry> GetAngleMap(int space) =>
        space >= 0 && space < AngleMap.Length ? AngleMap[space] : Array.Empty<AngleEntry>();

    public static IReadOnlyList<bool> GetWinners(int roll1, int roll2, int roll3) =>
        WinnerTable[roll1][roll2][roll3];

    public static double AtanDegrees(double y, double x) => 180 * Math.Atan2(y, x) / Math.PI;

    public static int NextBetLocation(int start, double angle)
    {
        IReadOnlyList<AngleEntry> map = GetAngleMap(start);
        if (map.Count == 0)
        {
            return start;
        }

        foreach (AngleEntry entry in map)
        {
            if (angle < entry.Angle)
            {
                return entry.Id;
            }
        }

        return map[0].Id;
    }

    public static void FindWinningInnerSpaces(int r1, int r2, int r3, int total, bool[] winners)
    {
        winners[total - 3 + KeystoneSpace.In3] = true;

        if (total is 10 or 11)
        {
            winners[KeystoneSpace.Keystone] = true;
        }
        else if (total != 3 && total != 18)
        {
            winners[total < 10 ? KeystoneSpace.Low : KeystoneSpace.High] = true;
            winners[(total & 1) != 0 ? KeystoneSpace.Odd : KeystoneSpace.Even] = true;
            winners[total is 5 or 6 or 9 or 13 or 14 or 17
                ? KeystoneSpace.Red
                : KeystoneSpace.Black] = true;
        }

        if (r1 == r2 || r1 == r3 || r2 == r3)
        {
            winners[KeystoneSpace.Pair] = true;
        }

        if (r1 == r2 && r1 == r3)
        {
            winners[KeystoneSpace.Trips] = true;
            switch (r1)
            {
                case 2:
                    winners[KeystoneSpace.Trip2S] = true;
                    break;
                case 3:
                    winners[KeystoneSpace.Trip3S] = true;
                    break;
                case 4:
                    winners[KeystoneSpace.Trip4S] = true;
                    break;
                case 5:
                    winners[KeystoneSpace.Trip5S] = true;
                    break;
            }
        }

        if (r1 != r2 && r1 != r3 && r2 != r3)
        {
            int low = Math.Min(r1, Math.Min(r2, r3));
            int high = Math.Max(r1, Math.Max(r2, r3));
            if (high - low == 2)
            {
                winners[KeystoneSpace.Straight] = true;
            }
        }
    }

    private static bool[][][][] BuildWinnerTable()
    {
        var table = new bool[7][][][];
        for (int a = 1; a <= 6; a++)
        {
            table[a] = new bool[7][][];
            for (int b = 1; b <= 6; b++)
            {
                table[a][b] = new bool[7][];
            }
        }

        for (int a = 1; a <= 6; a++)
        {
            for (int b = a; b <= 6; b++)
            {
                for (int c = b; c <= 6; c++)
                {
                    var winners = new bool[KeystoneSpace.SpaceCount];
                    FindWinningInnerSpaces(a, b, c, a + b + c, winners);
                    table[a][b][c] = winners;
                    table[a][c][b] = winners;
                    table[b][a][c] = winners;
                    table[b][c][a] = winners;
                    table[c][a][b] = winners;
                    table[c][b][a] = winners;
                }
            }
        }

        return table;
    }

    private static int[] BuildPayoutRates()
    {
        var rates = new int[KeystoneSpace.SpaceCount];
        rates[KeystoneSpace.Out3] = 35;
        rates[KeystoneSpace.Out4] = 20;
        rates[KeystoneSpace.Out5] = 12;
        rates[KeystoneSpace.Out6] = 8;
        rates[KeystoneSpace.Out7] = 6;
        rates[KeystoneSpace.Out8] = 5;
        rates[KeystoneSpace.Out9] = 4;
        rates[KeystoneSpace.Out10] = 3;
        rates[KeystoneSpace.Out11] = 3;
        rates[KeystoneSpace.Out12] = 4;
        rates[KeystoneSpace.Out13] = 5;
        rates[KeystoneSpace.Out14] = 6;
        rates[KeystoneSpace.Out15] = 8;
        rates[KeystoneSpace.Out16] = 12;
        rates[KeystoneSpace.Out17] = 20;
        rates[KeystoneSpace.Out18] = 35;
        rates[KeystoneSpace.In3] = 200;
        rates[KeystoneSpace.In4] = 70;
        rates[KeystoneSpace.In5] = 34;
        rates[KeystoneSpace.In6] = 20;
        rates[KeystoneSpace.In7] = 13;
        rates[KeystoneSpace.In8] = 9;
        rates[KeystoneSpace.In9] = 7;
        rates[KeystoneSpace.In10] = 6;
        rates[KeystoneSpace.In11] = 6;
        rates[KeystoneSpace.In12] = 7;
        rates[KeystoneSpace.In13] = 9;
        rates[KeystoneSpace.In14] = 13;
        rates[KeystoneSpace.In15] = 20;
        rates[KeystoneSpace.In16] = 34;
        rates[KeystoneSpace.In17] = 70;
        rates[KeystoneSpace.In18] = 200;
        rates[KeystoneSpace.Keystone] = 14;
        rates[KeystoneSpace.High] = 8;
        rates[KeystoneSpace.Low] = 8;
        rates[KeystoneSpace.Odd] = 8;
        rates[KeystoneSpace.Even] = 8;
        rates[KeystoneSpace.Red] = 8;
        rates[KeystoneSpace.Black] = 8;
        rates[KeystoneSpace.Pair] = 6;
        rates[KeystoneSpace.Straight] = 20;
        rates[KeystoneSpace.Trips] = 170;
        rates[KeystoneSpace.Trip2S] = 200;
        rates[KeystoneSpace.Trip3S] = 200;
        rates[KeystoneSpace.Trip4S] = 200;
        rates[KeystoneSpace.Trip5S] = 200;
        return rates;
    }

    private static string[] BuildBetIds()
    {
        var ids = new string[KeystoneSpace.SpaceCount];
        for (int i = 0; i < 16; i++)
        {
            ids[KeystoneSpace.Out3 + i] = "out" + (i + 3);
            ids[KeystoneSpace.In3 + i] = "in" + (i + 3);
        }

        ids[KeystoneSpace.Keystone] = "keystone";
        ids[KeystoneSpace.High] = "high";
        ids[KeystoneSpace.Low] = "low";
        ids[KeystoneSpace.Odd] = "odd";
        ids[KeystoneSpace.Even] = "even";
        ids[KeystoneSpace.Red] = "red";
        ids[KeystoneSpace.Black] = "black";
        ids[KeystoneSpace.Pair] = "pair";
        ids[KeystoneSpace.Straight] = "straight";
        ids[KeystoneSpace.Trips] = "trips";
        ids[KeystoneSpace.Trip2S] = "trip2s";
        ids[KeystoneSpace.Trip3S] = "trip3s";
        ids[KeystoneSpace.Trip4S] = "trip4s";
        ids[KeystoneSpace.Trip5S] = "trip5s";
        return ids;
    }

    private static AngleEntry[][] BuildAngleMap()
    {
        var map = new AngleEntry[KeystoneSpace.SpaceCount][];

        map[KeystoneSpace.Low] = new AngleEntry[]
        {
            new(22, KeystoneSpace.Even), new(67, KeystoneSpace.Keystone),
            new(112, KeystoneSpace.In7), new(157, KeystoneSpace.In7),
            new(202, KeystoneSpace.In6), new(247, KeystoneSpace.In4),
            new(292, KeystoneSpace.Trip2S), new(337, KeystoneSpace.Black),
        };
        map[KeystoneSpace.High] = new AngleEntry[]
        {
            new(22, KeystoneSpace.In15), new(67, KeystoneSpace.In15),
            new(112, KeystoneSpace.In14), new(157, KeystoneSpace.Keystone),
            new(202, KeystoneSpace.Odd), new(247, KeystoneSpace.Red),
            new(292, KeystoneSpace.Trip5S), new(337, KeystoneSpace.In17),
        };
        map[KeystoneSpace.Pair] = new AngleEntry[]
        {
            new(22, KeystoneSpace.Straight), new(67, KeystoneSpace.Straight),
            new(112, KeystoneSpace.Even), new(157, KeystoneSpace.Black),
            new(202, KeystoneSpace.Black), new(247, KeystoneSpace.Trip2S),
            new(292, KeystoneSpace.Trips), new(337, KeystoneSpace.Trips),
        };
        map[KeystoneSpace.Straight] = new AngleEntry[]
        {
            new(22, KeystoneSpace.Red), new(67, KeystoneSpace.Red),
            new(112, KeystoneSpace.Odd), new(157, KeystoneSpace.Pair),
            new(202, KeystoneSpace.Pair), new(247, KeystoneSpace.Trips),
            new(292, KeystoneSpace.Trips), new(337, KeystoneSpace.Trip4S),
        };
        map[KeystoneSpace.Even] = new AngleEntry[]
        {
            new(22, KeystoneSpace.Odd), new(67, KeystoneSpace.Keystone),
            new(112, KeystoneSpace.Keystone), new(157, KeystoneSpace.Low),
            new(202, KeystoneSpace.Low), new(247, KeystoneSpace.Black),
            new(292, KeystoneSpace.Pair), new(337, KeystoneSpace.Pair),
        };
        map[KeystoneSpace.Odd] = new AngleEntry[]
        {
            new(22, KeystoneSpace.High), new(67, KeystoneSpace.High),
            new(112, KeystoneSpace.Keystone), new(157, KeystoneSpace.Keystone),
            new(202, KeystoneSpace.Even), new(247, KeystoneSpace.Straight),
            new(292, KeystoneSpace.Straight), new(337, KeystoneSpace.Red),
        };
        map[KeystoneSpace.Black] = new AngleEntry[]
        {
            new(22, KeystoneSpace.Pair), new(67, KeystoneSpace.Even),
            new(112, KeystoneSpace.Low), new(157, KeystoneSpace.Low),
            new(202, KeystoneSpace.Low), new(247, KeystoneSpace.Trip2S),
            new(292, KeystoneSpace.Trip3S), new(337, KeystoneSpace.Trip3S),
        };
        map[KeystoneSpace.Red] = new AngleEntry[]
        {
            new(22, KeystoneSpace.High), new(67, KeystoneSpace.High),
            new(112, KeystoneSpace.High), new(157, KeystoneSpace.Odd),
            new(202, KeystoneSpace.Straight), new(247, KeystoneSpace.Trip4S),
            new(292, KeystoneSpace.Trip4S), new(337, KeystoneSpace.Trip5S),
        };
        map[KeystoneSpace.Keystone] = new AngleEntry[]
        {
            new(22, KeystoneSpace.High), new(67, KeystoneSpace.In11),
            new(112, KeystoneSpace.In10), new(157, KeystoneSpace.In10),
            new(202, KeystoneSpace.Low), new(247, KeystoneSpace.Even),
            new(292, KeystoneSpace.Even), new(337, KeystoneSpace.Odd),
        };
        map[KeystoneSpace.Trips] = new AngleEntry[]
        {
            new(22, KeystoneSpace.Trip4S), new(67, KeystoneSpace.Straight),
            new(112, KeystoneSpace.Pair), new(157, KeystoneSpace.Pair),
            new(202, KeystoneSpace.Trip3S), new(247, KeystoneSpace.Trip3S),
            new(292, KeystoneSpace.Trips), new(337, KeystoneSpace.Trip4S),
        };
        map[KeystoneSpace.Trip2S] = new AngleEntry[]
        {
            new(22, KeystoneSpace.Trip3S), new(67, KeystoneSpace.Black),
            new(112, KeystoneSpace.Low), new(157, KeystoneSpace.In4),
            new(202, KeystoneSpace.In3), new(247, KeystoneSpace.In3),
            new(292, KeystoneSpace.Trip2S), new(337, KeystoneSpace.Trip3S),
        };
        map[KeystoneSpace.Trip3S] = new AngleEntry[]
        {
            new(22, KeystoneSpace.Trips), new(67, KeystoneSpace.Pair),
            new(112, KeystoneSpace.Black), new(157, KeystoneSpace.Black),
            new(202, KeystoneSpace.Trip2S), new(247, KeystoneSpace.Trip2S),
            new(292, KeystoneSpace.Trip3S), new(337, KeystoneSpace.Trips),
        };
        map[KeystoneSpace.Trip4S] = new AngleEntry[]
        {
            new(22, KeystoneSpace.Trip5S), new(67, KeystoneSpace.Red),
            new(112, KeystoneSpace.Red), new(157, KeystoneSpace.Straight),
            new(202, KeystoneSpace.Trips), new(247, KeystoneSpace.Trips),
            new(292, KeystoneSpace.Trip4S), new(337, KeystoneSpace.Trip5S),
        };
        map[KeystoneSpace.Trip5S] = new AngleEntry[]
        {
            new(22, KeystoneSpace.In18), new(67, KeystoneSpace.In17),
            new(112, KeystoneSpace.High), new(157, KeystoneSpace.Red),
            new(202, KeystoneSpace.Trip4S), new(247, KeystoneSpace.Trip4S),
            new(292, KeystoneSpace.Trip5S), new(337, KeystoneSpace.In18),
        };

        map[KeystoneSpace.In3] = new AngleEntry[]
        {
            new(72, KeystoneSpace.Trip2S), new(174, KeystoneSpace.In4),
            new(282, KeystoneSpace.In3),
        };
        map[KeystoneSpace.In4] = new AngleEntry[]
        {
            new(63, KeystoneSpace.Low), new(163, KeystoneSpace.In5),
            new(174, KeystoneSpace.In4), new(273, KeystoneSpace.In3),
        };
        map[KeystoneSpace.In5] = new AngleEntry[]
        {
            new(52, KeystoneSpace.Low), new(151, KeystoneSpace.In6),
            new(163, KeystoneSpace.In5), new(262, KeystoneSpace.In4),
        };
        map[KeystoneSpace.In6] = new AngleEntry[]
        {
            new(39, KeystoneSpace.Low), new(140, KeystoneSpace.In7),
            new(151, KeystoneSpace.In6), new(249, KeystoneSpace.In5),
        };
        map[KeystoneSpace.In7] = new AngleEntry[]
        {
            new(29, KeystoneSpace.Low), new(129, KeystoneSpace.In8),
            new(140, KeystoneSpace.In7), new(239, KeystoneSpace.In6),
        };
        map[KeystoneSpace.In8] = new AngleEntry[]
        {
            new(18, KeystoneSpace.Low), new(118, KeystoneSpace.In9),
            new(129, KeystoneSpace.In8), new(228, KeystoneSpace.In7),
        };
        map[KeystoneSpace.In9] = new AngleEntry[]
        {
            new(8, KeystoneSpace.Low), new(108, KeystoneSpace.In10),
            new(118, KeystoneSpace.In9), new(218, KeystoneSpace.In8),
        };
        map[KeystoneSpace.In10] = new AngleEntry[]
        {
            new(90, KeystoneSpace.In11), new(108, KeystoneSpace.In10),
            new(204, KeystoneSpace.In9), new(354, KeystoneSpace.Keystone),
        };
        map[KeystoneSpace.In11] = new AngleEntry[]
        {
            new(72, KeystoneSpace.In12), new(90, KeystoneSpace.In11),
            new(186, KeystoneSpace.In10), new(336, KeystoneSpace.Keystone),
        };
        map[KeystoneSpace.In12] = new AngleEntry[]
        {
            new(62, KeystoneSpace.In13), new(72, KeystoneSpace.In12),
            new(172, KeystoneSpace.In11), new(322, KeystoneSpace.High),
        };
        map[KeystoneSpace.In13] = new AngleEntry[]
        {
            new(51, KeystoneSpace.In14), new(62, KeystoneSpace.In13),
            new(161, KeystoneSpace.In12), new(311, KeystoneSpace.High),
        };
        map[KeystoneSpace.In14] = new AngleEntry[]
        {
            new(40, KeystoneSpace.In15), new(51, KeystoneSpace.In14),
            new(150, KeystoneSpace.In13), new(300, KeystoneSpace.High),
        };
        map[KeystoneSpace.In15] = new AngleEntry[]
        {
            new(29, KeystoneSpace.In16), new(40, KeystoneSpace.In15),
            new(139, KeystoneSpace.In14), new(289, KeystoneSpace.High),
        };
        map[KeystoneSpace.In16] = new AngleEntry[]
        {
            new(17, KeystoneSpace.In17), new(29, KeystoneSpace.In16),
            new(128, KeystoneSpace.In15), new(278, KeystoneSpace.High),
        };
        map[KeystoneSpace.In17] = new AngleEntry[]
        {
            new(6, KeystoneSpace.In18), new(17, KeystoneSpace.In17),
            new(116, KeystoneSpace.In16), new(266, KeystoneSpace.High),
        };
        map[KeystoneSpace.In18] = new AngleEntry[]
        {
            new(6, KeystoneSpace.In18), new(108, KeystoneSpace.In17),
            new(258, KeystoneSpace.Trip5S),
        };

        map[KeystoneSpace.Out3] = new AngleEntry[]
        {
            new(174, KeystoneSpace.Out4), new(271, KeystoneSpace.Out3),
        };
        map[KeystoneSpace.Out4] = new AngleEntry[]
        {
            new(163, KeystoneSpace.Out5), new(174, KeystoneSpace.Out4),
            new(271, KeystoneSpace.Out3),
        };
        map[KeystoneSpace.Out5] = new AngleEntry[]
        {
            new(151, KeystoneSpace.Out6), new(163, KeystoneSpace.Out5),
            new(271, KeystoneSpace.Out4),
        };
        map[KeystoneSpace.Out6] = new AngleEntry[]
        {
            new(140, KeystoneSpace.Out7), new(151, KeystoneSpace.Out6),
            new(271, KeystoneSpace.Out5),
        };
        map[KeystoneSpace.Out7] = new AngleEntry[]
        {
            new(129, KeystoneSpace.Out8), new(140, KeystoneSpace.Out7),
            new(271, KeystoneSpace.Out6),
        };
        map[KeystoneSpace.Out8] = new AngleEntry[]
        {
            new(118, KeystoneSpace.Out9), new(129, KeystoneSpace.Out8),
            new(271, KeystoneSpace.Out7),
        };
        map[KeystoneSpace.Out9] = new AngleEntry[]
        {
            new(108, KeystoneSpace.Out10), new(118, KeystoneSpace.Out9),
            new(271, KeystoneSpace.Out8),
        };
        map[KeystoneSpace.Out10] = new AngleEntry[]
        {
            new(90, KeystoneSpace.Out11), new(108, KeystoneSpace.Out10),
            new(271, KeystoneSpace.Out9),
        };
        map[KeystoneSpace.Out11] = new AngleEntry[]
        {
            new(72, KeystoneSpace.Out12), new(90, KeystoneSpace.Out11),
            new(270, KeystoneSpace.Out10),
        };
        map[KeystoneSpace.Out12] = new AngleEntry[]
        {
            new(62, KeystoneSpace.Out13), new(72, KeystoneSpace.Out12),
            new(270, KeystoneSpace.Out11),
        };
        map[KeystoneSpace.Out13] = new AngleEntry[]
        {
            new(51, KeystoneSpace.Out14), new(62, KeystoneSpace.Out13),
            new(270, KeystoneSpace.Out12),
        };
        map[KeystoneSpace.Out14] = new AngleEntry[]
        {
            new(40, KeystoneSpace.Out15), new(51, KeystoneSpace.Out14),
            new(270, KeystoneSpace.Out13),
        };
        map[KeystoneSpace.Out15] = new AngleEntry[]
        {
            new(29, KeystoneSpace.Out16), new(40, KeystoneSpace.Out15),
            new(270, KeystoneSpace.Out14),
        };
        map[KeystoneSpace.Out16] = new AngleEntry[]
        {
            new(17, KeystoneSpace.Out17), new(29, KeystoneSpace.Out16),
            new(270, KeystoneSpace.Out15),
        };
        map[KeystoneSpace.Out17] = new AngleEntry[]
        {
            new(6, KeystoneSpace.Out18), new(17, KeystoneSpace.Out17),
            new(270, KeystoneSpace.Out16),
        };
        map[KeystoneSpace.Out18] = new AngleEntry[]
        {
            new(6, KeystoneSpace.Out18), new(270, KeystoneSpace.Out17),
        };

        for (int i = 0; i < map.Length; i++)
        {
            map[i] ??= Array.Empty<AngleEntry>();
        }

        return map;
    }
}
