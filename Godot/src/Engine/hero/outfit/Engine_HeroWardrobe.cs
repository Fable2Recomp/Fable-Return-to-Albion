using System.Collections.Generic;
using Refii.Equipment;

namespace Refii.Hero;

public sealed class WardrobeOption
{
    public string Label { get; init; } = string.Empty;

    public ClothingEntry? Entry { get; init; }
}

public static class Engine_HeroWardrobe
{
    public const string NoneLabel = "None";

    private static readonly object CacheLock = new();

    private static readonly Dictionary<(ClothingSlot, bool), List<WardrobeOption>> OptionCache =
        new();

    private static readonly Dictionary<bool, List<ClothingSlot>> SlotCache = new();

    private static int _generation = -1;

    private static void EnsureGeneration()
    {
        int generation = Engine_ClothingIndex.Generation;
        if (_generation == generation)
        {
            return;
        }

        _generation = generation;
        OptionCache.Clear();
        SlotCache.Clear();
    }

    public static List<WardrobeOption> OptionsFor(ClothingSlot slot, bool female)
    {
        lock (CacheLock)
        {
            EnsureGeneration();
            if (OptionCache.TryGetValue((slot, female), out List<WardrobeOption>? cached))
            {
                return cached;
            }

            List<WardrobeOption> built = BuildOptions(slot, female);
            OptionCache[(slot, female)] = built;
            return built;
        }
    }

    private static List<WardrobeOption> BuildOptions(ClothingSlot slot, bool female)
    {
        var options = new List<WardrobeOption> { new() { Label = NoneLabel } };
        var counts = new Dictionary<string, int>();

        List<ClothingEntry> entries = Engine_ClothingIndex.InSlot(slot, female);
        foreach (ClothingEntry entry in entries)
        {
            string label = Sanitise(entry.Name);
            counts[label] = counts.TryGetValue(label, out int seen) ? seen + 1 : 1;
        }

        foreach (ClothingEntry entry in entries)
        {
            string label = Sanitise(entry.Name);
            if (counts[label] > 1)
            {
                label = $"{label} [{entry.RecordHash:X8}]";
            }

            options.Add(new WardrobeOption { Label = label, Entry = entry });
        }

        return options;
    }

    public static List<ClothingSlot> SlotsFor(bool female)
    {
        lock (CacheLock)
        {
            EnsureGeneration();
            if (SlotCache.TryGetValue(female, out List<ClothingSlot>? cached))
            {
                return cached;
            }

            List<ClothingSlot> built = BuildSlots(female);
            SlotCache[female] = built;
            return built;
        }
    }

    private static List<ClothingSlot> BuildSlots(bool female)
    {
        var slots = new List<ClothingSlot>();
        foreach (ClothingSlot slot in Engine_ClothingSlots.Order)
        {
            if (female && slot is ClothingSlot.Beard or ClothingSlot.Moustache)
            {
                continue;
            }

            if (Engine_ClothingIndex.InSlot(slot, female).Count != 0)
            {
                slots.Add(slot);
            }
        }

        return slots;
    }

    public static string HintFor(List<WardrobeOption> options)
    {
        var hint = new System.Text.StringBuilder();
        for (int i = 0; i < options.Count; i++)
        {
            if (i != 0)
            {
                hint.Append(',');
            }

            hint.Append(options[i].Label);
        }

        return hint.ToString();
    }

    private static string Sanitise(string name)
    {
        string label = name.Replace(',', ' ').Trim();
        return label.Length == 0 ? "(unnamed)" : label;
    }
}
