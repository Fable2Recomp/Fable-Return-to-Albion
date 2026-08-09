using System;
using System.Collections.Generic;
using Refii.Gdb;

namespace Refii.Entities;

public sealed class PropertyInfo
{
    public string SaleSignEntityName { get; set; } = string.Empty;

    public string BuildingEntityName { get; set; } = string.Empty;

    public uint BuildingEntityHash { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public string Anecdotes { get; set; } = string.Empty;

    public string Benefits { get; set; } = string.Empty;

    public string BuildingTypeName { get; set; } = string.Empty;

    public uint BuildingNameTag { get; set; }

    public uint BuildingAddressTag { get; set; }

    public uint BuildingAnecdotesTag { get; set; }

    public uint BuildingBenefitsTag { get; set; }

    public int BasicSalePrice { get; set; } = -1;

    public int CanRent { get; set; } = -1;

    public List<KeyValuePair<string, string>> Fields { get; } = new();
}

public static class Engine_PropertyDetails
{
    public const uint BuildingSaleSignComponent = 0xBD0B8004;
    public const uint BuildingToSell = 0x48FE32FA;
    public const uint BuildingComponent = 0x2BB2DD22;
    public const uint BuildingIncomeComponent = 0xFF54B615;
    public const uint BuildingName = 0x6D0DD750;
    public const uint BuildingAddress = 0x764CA4F3;
    public const uint BuildingAnecdotes = 0x0F0F4579;
    public const uint BuildingBenefits = 0xE9A32859;
    public const uint BasicSalePrice = 0xF8926AC3;
    public const uint CanRent = 0xCE8EC970;
    public const uint BuildingType = 0x73909541;
    public const uint TypeField = 0x161478BD;

    private static readonly (string Label, uint Hash)[] BuildingFields =
    {
        ("Building wealth offset", 0x078705A7),
        ("Economic change modifier", 0x617E0339),
        ("Economy value modifier", 0xB8E6AEAD),
        ("Furniture value modifier", 0x50A15F93),
        ("Income multiplier", 0x91BDB719),
        ("Selling multiplier", 0x9F5BE9E2),
        ("Not-for-sale adjustment", 0x66705185),
        ("Income-to-wealth multiplier", 0xAA1B2BC5),
        ("Decoration-to-wealth multiplier", 0xAF5A30A6),
        ("Rent morality modifier", 0xA90D8D54),
        ("Beds", 0xA47B4195),
        ("Initial furniture level", 0x96EB25B7),
        ("Cost to sleep here", 0xBA25B9F0),
        ("Can be decorated", 0xE4B79F49),
        ("Can sell once purchased", 0x9572CC55),
        ("Check buyer is standing inside", 0x70F96C57),
        ("Is dungeon", 0x42533825),
        ("Dungeon rent", 0x557421A8),
        ("Populate", 0x0D37617B),
    };

    private static readonly (string Label, uint Hash)[] IncomeFields =
    {
        ("Days between income drops", 0xA0330F81),
        ("Maximum income drops", 0xE78B07AA),
    };

    private static readonly (uint Bit, string Name)[] TypeBits =
    {
        (0x0001, "Home"),
        (0x0002, "Dock"),
        (0x0004, "Farm"),
        (0x0008, "Town hall"),
        (0x0010, "Warehouse"),
        (0x0020, "Shop"),
        (0x0040, "Tavern"),
        (0x0080, "Bordello"),
        (0x0100, "Unique property"),
        (0x0200, "Market stall"),
        (0x0400, "Coach house"),
    };

    public static Dictionary<uint, PropertyInfo> Extract(
        GdbMulti multi, IReadOnlyList<KeyValuePair<uint, string>> saveMap)
    {
        var names = new Dictionary<uint, string>();
        foreach (KeyValuePair<uint, string> saved in saveMap)
        {
            names.TryAdd(saved.Key, saved.Value);
        }

        var result = new Dictionary<uint, PropertyInfo>();

        foreach (KeyValuePair<uint, string> saved in saveMap)
        {
            if (!multi.TryLookup(saved.Key, out GdbCursor sign) ||
                !multi.TryFindInherited(
                    sign, BuildingSaleSignComponent, 6, out _, out uint signComponentHash,
                    out _) ||
                !IsRef(signComponentHash) ||
                !multi.TryLookup(signComponentHash, out GdbCursor signComponent) ||
                !multi.TryFindInherited(
                    signComponent, BuildingToSell, -1, out _, out uint buildingHash,
                    out GdbFieldType buildingRefType) ||
                buildingRefType is not (GdbFieldType.String or GdbFieldType.Reference
                    or GdbFieldType.ResourceReference) ||
                !IsRef(buildingHash))
            {
                continue;
            }

            var info = new PropertyInfo
            {
                SaleSignEntityName = saved.Value,
                BuildingEntityHash = buildingHash,
                BuildingEntityName = names.TryGetValue(buildingHash, out string? knownName)
                    ? knownName
                    : multi.NameOf(buildingHash),
            };

            if (multi.TryLookup(buildingHash, out GdbCursor building))
            {
                if (multi.TryFindInherited(
                        building, BuildingComponent, 6, out _, out uint componentHash,
                        out _) &&
                    IsRef(componentHash) &&
                    multi.TryLookup(componentHash, out GdbCursor component))
                {
                    var sources = new[] { component, building };

                    info.BuildingNameTag = ReadTag(multi, sources, BuildingName);
                    info.BuildingAddressTag = ReadTag(multi, sources, BuildingAddress);
                    info.BuildingAnecdotesTag = ReadTag(multi, sources, BuildingAnecdotes);
                    info.BuildingBenefitsTag = ReadTag(multi, sources, BuildingBenefits);
                    info.DisplayName = info.BuildingNameTag != 0
                        ? multi.NameOf(info.BuildingNameTag)
                        : string.Empty;
                    info.Address = info.BuildingAddressTag != 0
                        ? multi.NameOf(info.BuildingAddressTag)
                        : string.Empty;

                    if (ReadRaw(multi, sources, BasicSalePrice, out uint price,
                            out GdbFieldType priceType) &&
                        priceType is GdbFieldType.Int or GdbFieldType.Enum)
                    {
                        info.BasicSalePrice = (int)price;
                    }

                    if (ReadRaw(multi, sources, CanRent, out uint rent,
                            out GdbFieldType rentType) &&
                        rentType == GdbFieldType.Bool)
                    {
                        info.CanRent = rent != 0 ? 1 : 0;
                    }

                    uint typeValue = 0;
                    if (ReadRaw(multi, sources, BuildingType, out uint typeRaw,
                            out GdbFieldType typeType))
                    {
                        if (typeType is GdbFieldType.Int or GdbFieldType.Enum)
                        {
                            typeValue = typeRaw;
                        }
                        else if (typeType is GdbFieldType.Reference
                                     or GdbFieldType.ResourceReference &&
                                 IsRef(typeRaw) &&
                                 multi.TryLookup(typeRaw, out GdbCursor typeRecord) &&
                                 multi.TryFindInherited(
                                     typeRecord, TypeField, -1, out _, out uint inner,
                                     out GdbFieldType innerType) &&
                                 innerType is GdbFieldType.Int or GdbFieldType.Enum)
                        {
                            typeValue = inner;
                        }
                    }

                    if (typeValue != 0)
                    {
                        info.BuildingTypeName = TypeName(typeValue);
                    }

                    foreach ((string label, uint hash) in BuildingFields)
                    {
                        AppendField(multi, info, sources, label, hash);
                    }
                }

                if (multi.TryFindInherited(
                        building, BuildingIncomeComponent, 6, out _, out uint incomeHash,
                        out _) &&
                    IsRef(incomeHash) &&
                    multi.TryLookup(incomeHash, out GdbCursor income))
                {
                    var incomeSources = new[] { income, building };
                    foreach ((string label, uint hash) in IncomeFields)
                    {
                        AppendField(multi, info, incomeSources, label, hash);
                    }
                }
            }

            if (info.DisplayName.Length == 0)
            {
                info.DisplayName = info.BuildingEntityName;
            }

            result[saved.Key] = info;
        }

        return result;
    }

    private static bool ReadRaw(
        GdbMulti multi, GdbCursor[] sources, uint fieldHash,
        out uint raw, out GdbFieldType type)
    {
        foreach (GdbCursor source in sources)
        {
            if (multi.TryFindInherited(source, fieldHash, -1, out _, out raw, out type))
            {
                return true;
            }
        }

        raw = 0;
        type = default;
        return false;
    }

    private static uint ReadTag(GdbMulti multi, GdbCursor[] sources, uint fieldHash)
    {
        if (ReadRaw(multi, sources, fieldHash, out uint tag, out GdbFieldType type) &&
            type is GdbFieldType.String or GdbFieldType.ResourceReference &&
            IsRef(tag))
        {
            return tag;
        }

        return 0;
    }

    private static void AppendField(
        GdbMulti multi, PropertyInfo info, GdbCursor[] sources, string label, uint hash)
    {
        if (!ReadRaw(multi, sources, hash, out uint raw, out GdbFieldType type))
        {
            return;
        }

        string value = type switch
        {
            GdbFieldType.Bool => raw != 0 ? "Yes" : "No",
            GdbFieldType.Int or GdbFieldType.Enum => ((int)raw).ToString(),
            GdbFieldType.Float => FormatFloat(raw),
            GdbFieldType.String or GdbFieldType.Reference or GdbFieldType.ResourceReference =>
                multi.NameOf(raw),
            _ => string.Empty,
        };

        if (value.Length != 0)
        {
            info.Fields.Add(new KeyValuePair<string, string>(label, value));
        }
    }

    private static string FormatFloat(uint raw)
    {
        float value = BitConverter.Int32BitsToSingle(unchecked((int)raw));
        if (!float.IsFinite(value))
        {
            return string.Empty;
        }

        string text = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return text;
    }

    private static string TypeName(uint value)
    {
        string label = string.Empty;
        uint remaining = value;
        foreach ((uint bit, string name) in TypeBits)
        {
            if ((value & bit) == 0)
            {
                continue;
            }

            label = label.Length == 0 ? name : label + " / " + name;
            remaining &= ~bit;
        }

        if (remaining != 0 || label.Length == 0)
        {
            string extra = $"Type 0x{value:X8}";
            label = label.Length == 0 ? extra : label + " / " + extra;
        }

        return label;
    }

    private static bool IsRef(uint hash) => hash != 0 && hash != GdbHash.Null;
}
