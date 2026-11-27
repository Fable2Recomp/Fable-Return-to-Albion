using System;
using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Gdb;
using Refii.Level;

/// <summary>
/// Fable 2's own buildings as the placement build needs them: Bowerstone Market's clocktower
/// (its platform stand-in and its cogs and hands) and the market shells named once per level.
/// </summary>
public sealed class Fable2Props : Engine_TitleProps
{
    public const uint ClocktowerParentHash = 0xD55304DB;

    public const string ClocktowerBasePath =
        "Art\\Environment\\Regions\\Bowerstone\\Structures\\dotXSI\\" +
        "BS_Market_ClockTower\\BS_Market_ClockTower.mdl";

    public static readonly string[] ClocktowerPartPaths =
    {
        "Art\\Environment\\Regions\\Bowerstone\\Structures\\dotXSI\\" +
        "BS_Market_ClockTower_Cogs\\BS_Market_ClockTower_Cogs.mdl",
        "Art\\Environment\\Regions\\Bowerstone\\Structures\\dotXSI\\" +
        "BS_Market_ClockTower_HourHand\\BS_Market_ClockTower_HourHand.mdl",
        "Art\\Environment\\Regions\\Bowerstone\\Structures\\dotXSI\\" +
        "BS_Market_ClockTower_MinuteHand\\BS_Market_ClockTower_MinuteHand.mdl",
    };

    public override bool PlacesSubstitutedBase(IReadOnlyList<GdbPlacement> placements)
    {
        uint clocktowerBaseHash = Engine_PropModelIndex.PathHash(ClocktowerBasePath);
        foreach (GdbPlacement placement in placements)
        {
            if (placement.ModelPathHash == clocktowerBaseHash ||
                placement.ModelPathHashes.Contains(clocktowerBaseHash))
            {
                return true;
            }
        }

        return false;
    }

    public override string? SubstituteModel(
        GdbPlacement placement, string entityName, string modelPath, bool levelPlacesBase, out string? companion)
    {
        companion = null;
        bool clocktowerAudit = placement.ParentHash == ClocktowerParentHash ||
            Engine_PropModelIndex.EntityKey(entityName)
                .Contains("clocktower", StringComparison.Ordinal);
        if (clocktowerAudit && !levelPlacesBase &&
            Engine_LevelProps.NormalisePath(modelPath).Contains(
                "bs_market_platform/bs_market_platform.mdl", StringComparison.Ordinal) &&
            Engine_PropModelIndex.ResolveByPath(ClocktowerBasePath) is string towerPath)
        {
            companion = modelPath;
            return towerPath;
        }

        return null;
    }

    public override IReadOnlyList<string> CompanionParts(string modelPath, out string tag)
    {
        tag = "clocktower";
        return Engine_LevelProps.NormalisePath(modelPath).Contains(
            "bs_market_clocktower/bs_market_clocktower.mdl", StringComparison.Ordinal)
            ? ClocktowerPartPaths
            : Array.Empty<string>();
    }

    public override bool IsUniqueShellModel(string p) =>
        p.Contains("bs_market_gatehouse", StringComparison.Ordinal) ||
        p.Contains("bs_market_clocktower", StringComparison.Ordinal) ||
        p.Contains("bs_market_platform", StringComparison.Ordinal) ||
        p.Contains("bs_market_largeshop", StringComparison.Ordinal) ||
        p.Contains("bs_market_smallshop", StringComparison.Ordinal) ||
        p.Contains("bs_market_generalshop", StringComparison.Ordinal) ||
        p.Contains("bs_market_tavern", StringComparison.Ordinal) ||
        p.Contains("bs_market_tarotstall", StringComparison.Ordinal) ||
        p.Contains("bs_townhouse", StringComparison.Ordinal);

    public override bool IsMultiInstanceModel(string normalisedPath) =>
        normalisedPath.Contains("bs_townhouse_basic_snow_v2", StringComparison.Ordinal);
}
