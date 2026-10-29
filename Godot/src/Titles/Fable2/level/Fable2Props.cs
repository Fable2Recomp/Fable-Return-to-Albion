using System;
using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Gdb;
using Refii.Level;

/// <summary>
/// Fable 2's own buildings as the placement build needs them: Bowerstone Market's clocktower
/// (its platform stand-in and its cogs and hands), the market shells named once per level, the
/// tavern and general store placed by entity alone, the substitutions the model index gets wrong
/// in the market, and the slums' re-dressed townhouses.
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

    public const string TavernExterior =
        "art/environment/regions/bowerstone/buildings/dotxsi/" +
        "bs_market_tavern/bs_market_tavern/exterior.mdl";

    public const string GeneralShopExterior =
        "art/environment/regions/bowerstone/buildings/dotxsi/" +
        "bs_market_generalshop/bs_market_generalshop/exterior.mdl";

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

    public override string? NoHashShellModel(string entityKey) => entityKey switch
    {
        "bsmarkettavern" => TavernExterior,
        "generalstore" or "generalstore1" => GeneralShopExterior,
        _ => null,
    };

    public override bool IsShellParent(string normalisedPath) =>
        normalisedPath.Contains("bs_market_tarotstall/", StringComparison.Ordinal);

    public override bool IsMultiInstanceModel(string normalisedPath) =>
        normalisedPath.Contains("bs_townhouse_basic_snow_v2", StringComparison.Ordinal);

    public override bool AugmentsPlacedShell(LevelEntry level, string entityKey) =>
        level.LevelPath.Contains("bwsslums", StringComparison.OrdinalIgnoreCase) &&
        (entityKey.Contains("slumstreethouse", StringComparison.Ordinal) ||
         entityKey.Contains("townhouse", StringComparison.Ordinal));

    public override bool RejectSubstitute(string entityName, string modelPath)
    {
        string entityKey = Engine_PropModelIndex.EntityKey(entityName);
        string rawKey = Engine_LevelProps.CompactText(entityName);
        string modelKey = Engine_LevelProps.CompactText(Engine_LevelProps.NameOf(modelPath));
        string modelPathKey = Engine_LevelProps.CompactText(modelPath.Replace('\\', '/'));

        bool signEntity =
            entityKey.Contains("sign", StringComparison.Ordinal) ||
            rawKey.Contains("sign", StringComparison.Ordinal);
        bool doorEntity =
            entityKey.Contains("door", StringComparison.Ordinal) ||
            rawKey.Contains("door", StringComparison.Ordinal);

        bool generalStoreBuilding =
            Engine_LevelProps.KeyIsOrNumbered(entityKey, "generalstore") ||
            Engine_LevelProps.KeyIsOrNumbered(rawKey, "objectbuildinggeneralstore") ||
            Engine_LevelProps.KeyIsOrNumbered(rawKey, "newobjectbuildinggeneralstore");
        if (modelPathKey.Contains("bsmarketgeneralshop", StringComparison.Ordinal) &&
            (modelPathKey.Contains("exterior", StringComparison.Ordinal) ||
             modelPathKey.Contains("interior", StringComparison.Ordinal)) &&
            !generalStoreBuilding)
        {
            return true;
        }

        if (generalStoreBuilding && !signEntity &&
            modelKey.Contains("signgeneralstore", StringComparison.Ordinal))
        {
            return true;
        }

        bool tavernBuilding =
            entityKey.Contains("bsmarkettavern", StringComparison.Ordinal) ||
            entityKey.Contains("markettavern", StringComparison.Ordinal) ||
            rawKey.Contains("objectbuildingbsmarkettavern", StringComparison.Ordinal) ||
            rawKey.Contains("newobjectbuildingbsmarkettavern", StringComparison.Ordinal);
        if (modelPathKey.Contains("bsmarkettavern", StringComparison.Ordinal) &&
            !tavernBuilding)
        {
            return true;
        }

        if (tavernBuilding && !signEntity &&
            (modelKey.Contains("botavernsign", StringComparison.Ordinal) ||
             modelKey.Contains("tavernsign", StringComparison.Ordinal)))
        {
            return true;
        }

        bool tarotStallBuilding =
            entityKey.Contains("tarotstall", StringComparison.Ordinal) ||
            rawKey.Contains("tarotstall", StringComparison.Ordinal);
        if (tarotStallBuilding && !doorEntity &&
            modelKey.Contains("tarotstalldoors", StringComparison.Ordinal))
        {
            return true;
        }

        bool marketStallBuilding =
            Engine_LevelProps.KeyIsOrVariant(entityKey, "bsopenstall") ||
            Engine_LevelProps.KeyIsOrVariant(entityKey, "bsmarketopenstall") ||
            Engine_LevelProps.KeyIsOrVariant(entityKey, "bsmarketstall") ||
            rawKey.Contains("objectbuildingbsopenstall", StringComparison.Ordinal) ||
            rawKey.Contains("objectbuildingbsmarketstall", StringComparison.Ordinal) ||
            rawKey.Contains("newobjectbuildingbsmarketstall", StringComparison.Ordinal);
        bool marketStallShellModel =
            (modelKey.Contains("bsmarketmarketstall", StringComparison.Ordinal) ||
             modelKey.Contains("bsmarketopenstall", StringComparison.Ordinal) ||
             modelKey.Contains("bsopenstall", StringComparison.Ordinal)) &&
            !modelKey.Contains("esashopmarketstall", StringComparison.Ordinal);
        if (marketStallShellModel && !marketStallBuilding)
        {
            return true;
        }

        if ((generalStoreBuilding || tavernBuilding ||
             marketStallBuilding || tarotStallBuilding) &&
            (modelKey.Contains("bstownhouse", StringComparison.Ordinal) ||
             modelKey.Contains("townhouse", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }
}
