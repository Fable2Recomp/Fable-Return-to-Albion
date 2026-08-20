using System.Collections.Generic;
using Refii.Equipment;

namespace Refii.Hero;

public sealed class OutfitLayer
{
    public ClothingSlot Slot { get; init; } = ClothingSlot.None;

    public string Name { get; init; } = string.Empty;

    public uint ModelHash { get; init; }

    public string ModelPath { get; init; } = string.Empty;

    public int SortLayer { get; init; } = -100;

    public uint BodyAreasCovered { get; init; }

    public bool Open { get; init; }

    public List<string> ClustersCovered { get; } = new();
}

public sealed class OutfitResult
{
    public List<string> ModelPaths { get; } = new();

    public List<uint> ModelHashes { get; } = new();

    public List<OutfitLayer> Layers { get; } = new();

    public List<string> Occluded { get; } = new();

    public List<string> Missing { get; } = new();
}

public static class Engine_HeroOutfit
{
    public static OutfitResult Resolve(
        IReadOnlyDictionary<ClothingSlot, ClothingEntry> chosen, bool female) =>
        Resolve(chosen, female, null);

    public static OutfitResult Resolve(
        IReadOnlyDictionary<ClothingSlot, ClothingEntry> chosen,
        bool female,
        IReadOnlySet<ClothingSlot>? open)
    {
        var worn = new List<ClothingEntry>();

        foreach (ClothingSlot slot in Engine_ClothingSlots.Order)
        {
            if (female && slot is ClothingSlot.Beard or ClothingSlot.Moustache)
            {
                continue;
            }

            if (chosen.TryGetValue(slot, out ClothingEntry? entry) && entry is not null)
            {
                worn.Add(entry);
            }
        }

        var result = new OutfitResult();

        foreach (ClothingEntry entry in worn)
        {
            if (IsOccluded(entry, worn))
            {
                result.Occluded.Add(
                    $"{Engine_ClothingSlots.LabelOf(entry.Slot)}: {entry.Name}");
                continue;
            }

            bool wantsOpen =
                open is not null && open.Contains(entry.Slot) && entry.HasOpenVariant(female);

            string path = entry.ModelPathFor(female, wantsOpen);
            uint hash = entry.ModelHashFor(female, wantsOpen);

            if (path.Length == 0)
            {
                result.Missing.Add(
                    $"{Engine_ClothingSlots.LabelOf(entry.Slot)}: {entry.Name}");
                continue;
            }

            if (!result.ModelPaths.Contains(path))
            {
                result.ModelPaths.Add(path);
                result.ModelHashes.Add(hash);
            }

            var layer = new OutfitLayer
            {
                Slot = entry.Slot,
                Name = entry.Name,
                ModelHash = hash,
                ModelPath = path,
                SortLayer = entry.Detail.ClusterSortLayer,
                BodyAreasCovered = entry.Detail.BodyAreasCovered,
                Open = wantsOpen,
            };

            layer.ClustersCovered.AddRange(
                Engine_ClothingSlots.ClustersFor(entry.Detail, female));
            result.Layers.Add(layer);
        }

        return result;
    }

    private static bool IsOccluded(ClothingEntry entry, List<ClothingEntry> worn)
    {
        uint areas = entry.Detail.BodyAreasCovered;
        if (areas == 0)
        {
            return false;
        }

        foreach (ClothingEntry other in worn)
        {
            if (ReferenceEquals(other, entry) ||
                other.Detail.ClusterSortLayer <= entry.Detail.ClusterSortLayer)
            {
                continue;
            }

            if ((other.Detail.BodyAreasCovered & areas) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
