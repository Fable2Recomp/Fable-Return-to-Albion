using System;
using System.Collections.Generic;

namespace Refii.Gdb;

public static class Engine_EntityModels
{
    public const uint MorphComponent = 0x0D4ADA1A;
    public const uint CompositeModelRecord = 0x7CFF5EE2;
    public const uint Model = 0x90347E14;
    public const uint RetextureableModel = 0xC483A92A;
    public const uint CharacterFolder = 0x1022D6D2;
    public const uint Character = 0xBB214642;
    public const uint LegsAccessories = 0x3D7B3380;
    public const uint TorsoAccessories = 0xF5B074D4;

    /// <summary>
    /// The model fields a character's parts come from.
    ///
    /// NO EYE CASES. `RightEyeCaseModel` (0xFB479147) and `LeftEyeCaseModel` (0xCF68CC70) were
    /// in this list and are the reason NPC eyes read as a flat, featureless patch: the guest
    /// READS both fields and throws the results away — `sub_825B5160` stores `RightEyeModel`
    /// through `sub_8259D380` and `LeftEyeModel` through `sub_8259D3F8` (0x825B56AC, 0x825B56CC)
    /// and then calls `sub_82783418` for the two case fields at 0x825B56E0 and 0x825B56F4 with
    /// no store after either. Component 7's case slots (comp7+0x1C, +0x2C) therefore stay 0 and
    /// no case instance is ever created. Drawing them puts an opaque lens over the iris.
    /// </summary>
    private static readonly uint[] PartFields =
    {
        0x05294B89, 0x547DDA3E, 0xD622E5AD,
        0x139ADC04, 0x926B168B, 0x8D09F54F, 0x8D09F551, 0x8D09F56E,
        0x6503BD45, 0x6503BD5B,
        0xA0CFEC37,
        0x017CDC23, 0x017CDC24, 0x017CDC25, 0x017CDC26, 0x017CDC27,
    };

    private static readonly uint[] AppearanceFields =
    {
        GdbHash.GraphicAppearanceComponent,
        GdbHash.GraphicAppearanceAnimatedMeshComponent,
        GdbHash.StaticMeshComponent,
        GdbHash.StaticMultipleMeshComponent,
        GdbHash.StaticMultipleModelList,
        MorphComponent,
        CompositeModelRecord,
        0x3C06A4E4, 0x31FF8FCF, 0x515A75DA,
    };

    public static List<uint> ForEntity(GdbMulti multi, uint entityHash)
    {
        var models = new List<uint>();

        if (!multi.TryLookup(entityHash, out GdbCursor entity))
        {
            return models;
        }

        var appearanceRoots = new List<GdbCursor>();
        var resolvedEntityFields = new HashSet<uint>();
        var entityParents = new HashSet<uint>();

        GdbCursor current = entity;
        for (int depth = 0; depth < 64 && current.Valid; depth++)
        {
            uint parentHash = 0;

            foreach ((uint hash, GdbFieldType type, uint raw) in
                     current.View.RawFields(current.Record))
            {
                if (hash == GdbView.HashParent && type == GdbFieldType.Reference)
                {
                    parentHash = raw;
                    continue;
                }

                if (!resolvedEntityFields.Add(hash))
                {
                    continue;
                }

                if (IsValueType(type) && IsModelPathField(hash))
                {
                    Add(models, raw);
                }

                if (IsLinkType(type) && IsAppearanceField(hash) &&
                    multi.TryLookup(raw, out GdbCursor linked))
                {
                    appearanceRoots.Add(linked);
                }
            }

            if (parentHash == 0 || parentHash == GdbHash.Null ||
                !entityParents.Add(parentHash) ||
                !multi.TryLookup(parentHash, out current))
            {
                break;
            }
        }

        WalkAppearance(multi, appearanceRoots, models);
        return models;
    }

    private static void WalkAppearance(
        GdbMulti multi, List<GdbCursor> roots, List<uint> models)
    {
        var stack = new Stack<GdbCursor>(roots);
        var visited = new HashSet<uint>();

        while (stack.Count != 0 && visited.Count < 512)
        {
            GdbCursor node = stack.Pop();
            uint nodeHash = node.View.RecordHash(node.Record);
            if (nodeHash != 0 && !visited.Add(nodeHash))
            {
                continue;
            }

            var resolvedFields = new HashSet<uint>();
            var inheritedRecords = new HashSet<uint>();
            GdbCursor inherited = node;
            bool selectedCharacterVariant = false;

            for (int depth = 0; depth < 64 && inherited.Valid; depth++)
            {
                uint parentHash = 0;
                var fieldsInRecord = new List<uint>();

                foreach ((uint hash, GdbFieldType type, uint raw) in
                         inherited.View.RawFields(inherited.Record))
                {
                    if (hash == GdbView.HashParent && type == GdbFieldType.Reference)
                    {
                        parentHash = raw;
                        continue;
                    }

                    bool listEntry = IsInheritedListEntry(hash);
                    if (!listEntry && resolvedFields.Contains(hash))
                    {
                        continue;
                    }

                    if (hash == Character)
                    {
                        if (selectedCharacterVariant)
                        {
                            continue;
                        }

                        selectedCharacterVariant = true;
                    }

                    if (!listEntry)
                    {
                        fieldsInRecord.Add(hash);
                    }

                    if (IsValueType(type) && IsModelPathField(hash))
                    {
                        Add(models, raw);
                    }

                    if (IsLinkType(type) && IsAppearanceField(hash) &&
                        raw != 0 && raw != GdbHash.Null &&
                        multi.TryLookup(raw, out GdbCursor linked))
                    {
                        stack.Push(linked);
                    }
                }

                foreach (uint field in fieldsInRecord)
                {
                    resolvedFields.Add(field);
                }

                if (parentHash == 0 || parentHash == GdbHash.Null ||
                    !inheritedRecords.Add(parentHash) ||
                    !multi.TryLookup(parentHash, out inherited))
                {
                    break;
                }
            }
        }
    }

    private static bool IsValueType(GdbFieldType type) =>
        type is GdbFieldType.Float or GdbFieldType.String or GdbFieldType.ResourceReference;

    private static bool IsLinkType(GdbFieldType type) =>
        type is GdbFieldType.Reference or GdbFieldType.ResourceReference;

    private static bool IsModelPathField(uint hash)
    {
        if (hash == GdbHash.ModelFile || hash == GdbHash.ModelFile1 ||
            hash == GdbHash.ModelFile2 || hash == GdbHash.StaticMultipleModelFile ||
            hash == Model)
        {
            return true;
        }

        return Array.IndexOf(PartFields, hash) >= 0;
    }

    private static bool IsAppearanceField(uint hash)
    {
        if (IsModelPathField(hash))
        {
            return true;
        }

        if (hash == LegsAccessories || hash == TorsoAccessories ||
            hash == RetextureableModel || hash == CharacterFolder || hash == Character)
        {
            return true;
        }

        return Array.IndexOf(AppearanceFields, hash) >= 0;
    }

    private static bool IsInheritedListEntry(uint hash) =>
        hash == RetextureableModel || hash == Character ||
        hash == GdbHash.StaticMultipleModelFile;

    private static void Add(List<uint> models, uint hash)
    {
        if (hash != 0 && hash != GdbHash.Null && !models.Contains(hash))
        {
            models.Add(hash);
        }
    }
}
