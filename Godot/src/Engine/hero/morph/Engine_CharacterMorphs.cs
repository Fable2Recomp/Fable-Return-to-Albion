using System;
using System.Collections.Generic;
using System.Globalization;

namespace Refii.Hero;

/// <summary>
/// Murgo's Mirror's look, in its three forms: a settings SLOT (this machine's players: slot 1
/// <c>character_morphs</c> / <c>character_morph_&lt;input&gt;</c> for the player pad 0 plays, slot 2
/// <c>character_morphs2</c> / <c>character_morph2_&lt;input&gt;</c> for local co-op's player two — the
/// <c>padmap_</c> / <c>padmap2_</c> shape), the player STORE's <c>character_morphs.*</c> set (each guest
/// player's look, owned by the machine that plays the hero and the same on both co-op machines), and
/// the weights and judged inputs published per guest player for the morph hook
/// (<c>refii.character_morphs.p&lt;N&gt;.*</c>, character_morph_hooks.cpp).
/// </summary>
public static class Engine_CharacterMorphs
{
    public const string AutomaticKey = "character_morphs";
    public const string SecondAutomaticKey = "character_morphs2";
    private const string InputPrefix = "character_morph_";
    private const string SecondInputPrefix = "character_morph2_";

    /// <summary>The store's look keys (refii_mod_appearance's): <c>character_morphs.automatic</c> and one per input.</summary>
    public const string StorePrefix = "character_morphs.";
    public const string StoreAutomaticKey = StorePrefix + "automatic";

    public static readonly HeroMorphSlider[] Sliders =
    {
        new(HeroMorphInput.Strength, "Strength", 0, 1, 0.05f),
        new(HeroMorphInput.Skill, "Skill", 0, 1, 0.05f),
        new(HeroMorphInput.Will, "Will", 0, 1, 0.05f),
        new(HeroMorphInput.Fat, "Weight", 0, 1, 0.05f),
        new(HeroMorphInput.Height, "Height", -1, 1, 0.05f),
        new(HeroMorphInput.Tan, "Tan", 0, 1, 0.05f),
        new(HeroMorphInput.Morality, "Morality appearance", 0, 1, 0.05f),
        new(HeroMorphInput.Purity, "Purity appearance", 0, 1, 0.05f),
        new(HeroMorphInput.Age, "Age appearance", 18, 60, 1),
    };

    /// <summary>
    /// The inputs villagers judge the hero's looks by, read as the CECStats fields and CECAge's years
    /// themselves rather than through the weight function: the appearance opinion
    /// <c>sub_82791658</c> (age, fat, tan, strength) and a reaction line's HeroAppearanceChecks
    /// <c>sub_8278F848</c> (fat, strength, height, tan, will, normalised purity, age).
    /// </summary>
    public static readonly HeroMorphInput[] JudgedInputs =
    {
        HeroMorphInput.Tan, HeroMorphInput.Fat, HeroMorphInput.Height, HeroMorphInput.Strength,
        HeroMorphInput.Will, HeroMorphInput.Purity, HeroMorphInput.Age,
    };

    private static string Name(HeroMorphInput input) => input.ToString().ToLowerInvariant();

    /// <summary>The per-frame keys, by input and made once: the look is compared every frame, on a standing world too.</summary>
    private static readonly string[] Keys = KeysWith(InputPrefix);
    private static readonly string[] SecondKeys = KeysWith(SecondInputPrefix);
    private static readonly string[] StoreKeys = KeysWith(StorePrefix);

    private static string[] KeysWith(string prefix)
    {
        var keys = new string[(int)HeroMorphInput.Age + 1];
        for (int i = 0; i < keys.Length; i++) keys[i] = prefix + Name((HeroMorphInput)i);
        return keys;
    }

    /// <summary>Slot 1's setting for an input; every slot-shaped reader and writer takes slot 1's keys.</summary>
    public static string Key(HeroMorphInput input) => Keys[(int)input];

    public static string Key(HeroMorphInput input, int slot) => (slot == 2 ? SecondKeys : Keys)[(int)input];

    public static string AutomaticKeyOf(int slot) => slot == 2 ? SecondAutomaticKey : AutomaticKey;

    /// <summary>Slot 1's key <paramref name="key"/> as slot <paramref name="slot"/> stores it.</summary>
    public static string SlotKey(string key, int slot)
    {
        if (slot != 2) return key;
        if (key == AutomaticKey) return SecondAutomaticKey;
        int input = Array.IndexOf(Keys, key);
        if (input >= 0) return SecondKeys[input];
        return key.StartsWith(InputPrefix, StringComparison.Ordinal) ? SecondInputPrefix + key[InputPrefix.Length..] : key;
    }

    /// <summary>A reader of slot <paramref name="slot"/>'s settings that answers slot 1's keys.</summary>
    public static Func<string, string> ForSlot(Func<string, string> read, int slot) =>
        slot == 2 ? key => read(SlotKey(key, 2)) : read;

    /// <summary>Slot-1-keyed values (<see cref="Values"/>) as slot <paramref name="slot"/> stores them.</summary>
    public static Dictionary<string, string> ForSlot(IReadOnlyDictionary<string, string> values, int slot)
    {
        var slotted = new Dictionary<string, string>(values.Count);
        foreach (var (key, value) in values) slotted[SlotKey(key, slot)] = value;
        return slotted;
    }

    public static string StoreKey(HeroMorphInput input) => StoreKeys[(int)input];

    public static string BridgePrefix(int player) =>
        "refii.character_morphs.p" + player.ToString(CultureInfo.InvariantCulture) + ".";

    public static string BridgeKey(HeroMorphInput input, int player) => BridgePrefix(player) + Name(input);

    public static bool Automatic(Func<string, string> read) =>
        read(AutomaticKey).Trim().Trim('"').ToLowerInvariant() is not ("false" or "0" or "off");

    public static float ManualTan(Func<string, string> read) => Automatic(read) ? 0.5f : Read(read).Tan;

    public static HeroMorphStats Read(Func<string, string> read)
    {
        var stats = new HeroMorphStats();
        ReadInto(stats, read);
        return stats;
    }

    /// <summary><see cref="Read"/> over stats that hold the defaults.</summary>
    private static void ReadInto(HeroMorphStats stats, Func<string, string> read)
    {
        foreach (var slider in Sliders)
        {
            if (float.TryParse(read(Key(slider.Input)).Trim().Trim('"'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out float value) && float.IsFinite(value))
                Engine_HeroMorphSliders.Set(stats, slider, value);
        }
    }

    public static int Steps(HeroMorphSlider slider) =>
        (int)MathF.Round((slider.Maximum - slider.Minimum) / slider.Step) + 1;

    public static int Index(HeroMorphSlider slider, float value) =>
        Math.Clamp((int)MathF.Round((value - slider.Minimum) / slider.Step), 0, Steps(slider) - 1);

    public static float Value(HeroMorphSlider slider, int index) =>
        Math.Clamp(slider.Minimum + Math.Clamp(index, 0, Steps(slider) - 1) * slider.Step,
            slider.Minimum, slider.Maximum);

    public static Dictionary<string, string> Values(Func<int, int> index)
    {
        var values = new Dictionary<string, string> { [AutomaticKey] = index(0) != 0 ? "true" : "false" };
        for (int i = 0; i < Sliders.Length; i++)
            values[Key(Sliders[i].Input)] = Value(Sliders[i], index(i + 1)).ToString("0.##", CultureInfo.InvariantCulture);
        return values;
    }

    /// <summary>The look slot-1-keyed settings state, as the store holds it: the mode (1 automatic) and every input.</summary>
    public static Dictionary<string, float> StoreValues(Func<string, string> read) =>
        StoreValues(read, new Dictionary<string, float>(Sliders.Length + 1));

    /// <summary><see cref="StoreValues(Func{string, string})"/> into a kept dictionary, replacing what it held; returns it.</summary>
    public static Dictionary<string, float> StoreValues(Func<string, string> read, Dictionary<string, float> look)
    {
        HeroMorphStats stats = _readStats ??= new HeroMorphStats();
        foreach (var slider in Sliders) stats[slider.Input] = DefaultStats[slider.Input];
        ReadInto(stats, read);
        look.Clear();
        look[StoreAutomaticKey] = Automatic(read) ? 1 : 0;
        foreach (var slider in Sliders) look[StoreKey(slider.Input)] = stats[slider.Input];
        return look;
    }

    private static readonly HeroMorphStats DefaultStats = new();

    /// <summary>The stats a kept <see cref="StoreValues(Func{string, string}, Dictionary{string, float})"/> reads through; never handed out.</summary>
    [ThreadStatic]
    private static HeroMorphStats? _readStats;

    /// <summary>The look the store holds for a player, or null while it lacks any key (a store just emptied).</summary>
    public static Dictionary<string, float>? StoredLook(Func<string, float?> get)
    {
        var look = new Dictionary<string, float>(Sliders.Length + 1);
        return StoredLook(get, look) ? look : null;
    }

    /// <summary><see cref="StoredLook(Func{string, float?})"/> into a kept dictionary, replacing what it held; false where that answers null.</summary>
    public static bool StoredLook(Func<string, float?> get, Dictionary<string, float> look)
    {
        look.Clear();
        if (get(StoreAutomaticKey) is not { } automatic) return false;
        look[StoreAutomaticKey] = automatic;
        foreach (var slider in Sliders)
        {
            if (get(StoreKey(slider.Input)) is not { } value || !float.IsFinite(value)) return false;
            look[StoreKey(slider.Input)] = value;
        }
        return true;
    }

    /// <summary>
    /// A stored look as slot <paramref name="slot"/>'s settings, each value exact (<c>R</c>) so the
    /// settings read back as the same look.
    /// </summary>
    public static Dictionary<string, string> SettingsOf(IReadOnlyDictionary<string, float> look, int slot)
    {
        var settings = new Dictionary<string, string>
        {
            [AutomaticKeyOf(slot)] = look.TryGetValue(StoreAutomaticKey, out float automatic) && automatic == 0 ? "false" : "true",
        };
        foreach (var slider in Sliders)
        {
            if (look.TryGetValue(StoreKey(slider.Input), out float value))
                settings[Key(slider.Input, slot)] = value.ToString("R", CultureInfo.InvariantCulture);
        }
        return settings;
    }

    /// <summary>The guest command that states a slot's look for <paramref name="player"/> (mod_commands.cpp <c>appearance_state</c>).</summary>
    public static string StateCommand(int player, IReadOnlyDictionary<string, float> look)
    {
        static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        var line = new System.Text.StringBuilder("appearance_state ");
        line.Append(player.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(look.TryGetValue(StoreAutomaticKey, out float automatic) && automatic == 0 ? '0' : '1');
        foreach (HeroMorphInput input in StateOrder)
            line.Append(' ').Append(Number(look.TryGetValue(StoreKey(input), out float value) ? value : new HeroMorphStats()[input]));
        return line.ToString();
    }

    /// <summary>The inputs of an <c>appearance_state</c> line, in refii_mod_appearance_t's order.</summary>
    public static readonly HeroMorphInput[] StateOrder =
    {
        HeroMorphInput.Tan, HeroMorphInput.Fat, HeroMorphInput.Height, HeroMorphInput.Strength, HeroMorphInput.Skill,
        HeroMorphInput.Will, HeroMorphInput.Morality, HeroMorphInput.Purity, HeroMorphInput.Age,
    };

    public static bool SameLook(IReadOnlyDictionary<string, float>? a, IReadOnlyDictionary<string, float>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        // The concrete enumerator where there is one: through the interface it is an object per compare.
        if (a is Dictionary<string, float> concrete)
        {
            foreach (var (key, value) in concrete)
            {
                if (!b.TryGetValue(key, out float other) || other != value) return false;
            }
            return true;
        }
        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out float other) || other != value) return false;
        }
        return true;
    }

    /// <summary>The skin tan a look states for the composites, 0..1; 0.5 is the guest's own.</summary>
    public static float LookTan(IReadOnlyDictionary<string, float>? look) =>
        look is not null && look.TryGetValue(StoreAutomaticKey, out float automatic) && automatic == 0 &&
        look.TryGetValue(StoreKey(HeroMorphInput.Tan), out float tan) ? Math.Clamp(tan, 0, 1) : 0.5f;

    /// <summary>
    /// Publishes guest player <paramref name="player"/>'s look for the morph hook: the weight of each
    /// morph type 2..18 (<see cref="Engine_HeroMorphState.Weight"/>, the weight function), the
    /// judged inputs, the mode, then the generation the hook re-reads on. No look is automatic.
    /// </summary>
    public static void Publish(IReadOnlyDictionary<string, float>? look, Action<string, float> set, int player, int generation)
    {
        var stats = new HeroMorphStats();
        bool automatic = true;
        if (look is not null)
        {
            automatic = !look.TryGetValue(StoreAutomaticKey, out float mode) || mode != 0;
            foreach (var slider in Sliders)
            {
                if (look.TryGetValue(StoreKey(slider.Input), out float value) && float.IsFinite(value))
                    Engine_HeroMorphSliders.Set(stats, slider, value);
            }
        }
        string prefix = BridgePrefix(player);
        for (uint type = 2; type <= 18; type++)
            set(prefix + type.ToString(CultureInfo.InvariantCulture), Engine_HeroMorphState.Weight(stats, type));
        foreach (HeroMorphInput input in JudgedInputs)
            set(BridgeKey(input, player), stats[input]);
        set(prefix + "automatic", automatic ? 1 : 0);
        set(prefix + "generation", generation);
    }
}
