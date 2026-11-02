using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Refii.Core;

namespace Refii.Hero;

/// <summary>One morph a mod declares in its <c>morphs.json</c>.</summary>
public sealed record ModMorph(string Mod, string Name, string Label, float Default, float Minimum, float Maximum, float Step)
{
    /// <summary><c>&lt;mod&gt;.&lt;name&gt;</c>, the name every surface takes.</summary>
    public string Scoped => Mod + "." + Name;

    /// <summary>The MorphType the guest's records carry for it (<see cref="Engine_ModMorphs.Id(string)"/>).</summary>
    public uint Id => Engine_ModMorphs.Id(Scoped);

    /// <summary>The player store key the weight lives under (refii_mod_morph_weight's).</summary>
    public string StoreKey => Engine_ModMorphs.StoreKey(Id);

    public float Clamp(float weight) => float.IsFinite(weight) ? Math.Clamp(weight, Minimum, Maximum) : Default;

    public int Steps => Step > 0 ? (int)MathF.Round((Maximum - Minimum) / Step) + 1 : 2;

    public int Index(float weight) => Math.Clamp((int)MathF.Round((Clamp(weight) - Minimum) / Math.Max(Step, 1e-6f)), 0, Steps - 1);

    public float Value(int index) => Clamp(Minimum + Math.Clamp(index, 0, Steps - 1) * Step);
}

/// <summary>One problem in a <c>morphs.json</c>, or between two mods' morphs.</summary>
public readonly record struct ModMorphIssue(string Pointer, bool Error, string Message);

/// <summary>
/// MODS' OWN MORPH TYPES. The guest's one weight function <c>sub_822DA028(CECMorph, type)</c> answers
/// types 1..18 and 0 for 11, 13 and anything at or above 19 (its switch bounds, TU1 0x822DA274..7C),
/// and every morph reader passes the record's <c>MorphType</c> to it unchecked — vertex
/// (0x8220E29C / 0x8220E304), texture (0x8220EE64), skeletal (0x825B7C44) and particle (the
/// rebuild <c>sub_825F33C8</c>). So a record carrying a type of its own is drawn at whatever weight
/// the morph hook answers for it (character_morph_hooks.cpp): the player store's
/// <c>morph.&lt;id&gt;</c> for that player, else the declared default, clamped.
/// <para>A mod declares them in <c>morphs.json</c>: <c>[{ "name", "label", "default", "min", "max",
/// "step" }]</c>. The type is <see cref="Id(string)"/> of <c>&lt;mod&gt;.&lt;name&gt;</c>, which a
/// <c>.gdb.csv</c> writes as <c>@morph:&lt;name&gt;</c> (Engine_GdbCsvOverlay).</para>
/// <para>The registry of every enabled mod is published for the hook as settings:
/// <c>refii.morphs.count</c>, per index <c>refii.morphs.&lt;i&gt;.hi</c> / <c>.lo</c> (the id's two
/// 16-bit halves: a setting is a float), <c>.default</c>, <c>.min</c>, <c>.max</c>, then
/// <c>refii.morphs.generation</c>, which the hook re-reads on (mod_api.cpp).</para>
/// </summary>
public static class Engine_ModMorphs
{
    public const string ManifestPath = "morphs.json";

    /// <summary>The player store's key prefix: <c>morph.&lt;id&gt;</c>, the id in decimal.</summary>
    public const string StorePrefix = "morph.";

    public const string SettingPrefix = "refii.morphs.";

    /// <summary>The lowest id a mod morph takes: below it are the shipped and special types.</summary>
    public const uint FirstId = 0x100;

    /// <summary>The most morphs the hook holds (mod_api.cpp kMaxModMorphs).</summary>
    public const int Capacity = 256;

    private static List<ModMorph>? _registry;
    private static List<string> _problems = new();

    /// <summary>Bumped each time the registry is read again.</summary>
    public static int Generation { get; private set; }

    /// <summary>
    /// The MorphType of <c>&lt;mod&gt;.&lt;name&gt;</c>: FNV-1 (the game's hash) of the name in lower
    /// case, its top bit cleared so it is positive in the guest's signed compare, and moved up by
    /// <see cref="FirstId"/> when it lands under it — never 0, the shipped 1..18, the scar type 19, the
    /// morph animations' 11 and 13, nor -1.
    /// </summary>
    public static uint Id(string scoped)
    {
        uint id = Engine_GameHash.Fnv1((scoped ?? string.Empty).Trim().ToLowerInvariant()) & 0x7FFFFFFFu;
        return id < FirstId ? id + FirstId : id;
    }

    public static string StoreKey(uint id) => StorePrefix + id.ToString(CultureInfo.InvariantCulture);

    /// <summary>The id as every script surface writes it: <c>0x</c> and eight hex digits.</summary>
    public static string Hex(uint id) => "0x" + id.ToString("x8", CultureInfo.InvariantCulture);

    /// <summary>An id written <c>0x…</c>, or null (anything else is a name, as the native side reads it).</summary>
    public static uint? ParseId(string text)
    {
        text = (text ?? string.Empty).Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex)
            ? hex
            : null;
    }

    /// <summary>
    /// A morph reference as a <c>.gdb.csv</c> or a script writes it — <c>&lt;mod&gt;.&lt;name&gt;</c>,
    /// a bare <c>&lt;name&gt;</c> of <paramref name="mod"/>, or an id — as its id; null when a bare name
    /// has no mod to belong to.
    /// </summary>
    public static uint? Resolve(string reference, string? mod)
    {
        reference = (reference ?? string.Empty).Trim();
        if (reference.Length == 0) return null;
        if (ParseId(reference) is { } id) return id;
        if (reference.Contains('.')) return Id(reference);
        return string.IsNullOrEmpty(mod) ? null : Id(mod + "." + reference);
    }

    /// <summary>Every enabled mod's morphs, in load order (read once per mount).</summary>
    public static IReadOnlyList<ModMorph> All => _registry ??= Load();

    /// <summary>What the last read refused, one line each, for the mod report.</summary>
    public static IReadOnlyList<string> Problems
    {
        get
        {
            _ = All;
            return _problems;
        }
    }

    /// <summary>Drops the registry; the next read takes the enabled mods again.</summary>
    public static void Reload()
    {
        _registry = null;
        ++Generation;
    }

    /// <summary>Sets the registry outright (a test, or a host with no mount).</summary>
    public static void Set(IEnumerable<ModMorph>? morphs)
    {
        _registry = morphs?.ToList();
        _problems = new List<string>();
        ++Generation;
    }

    public static ModMorph? Find(uint id) => All.FirstOrDefault(m => m.Id == id);

    /// <summary>A registered morph by id from the registry already read, without reading the mods (null before the first read).</summary>
    public static ModMorph? Peek(uint id) => id >= FirstId ? _registry?.FirstOrDefault(m => m.Id == id) : null;

    /// <summary>A registered morph by <c>&lt;mod&gt;.&lt;name&gt;</c> or id.</summary>
    public static ModMorph? Find(string reference) => Resolve(reference, null) is { } id ? Find(id) : null;

    private static List<ModMorph> Load()
    {
        var files = new List<(string Mod, string Text)>();
        try
        {
            files = Refii.Mods.ModEntries.ReadEnabledText(ManifestPath);
        }
        catch (Exception exception)
        {
            _problems = new List<string> { $"{ManifestPath}: {exception.Message}" };
            return new List<ModMorph>();
        }

        var problems = new List<string>();
        List<ModMorph> morphs = Compose(files, problems);
        _problems = problems;
        return morphs;
    }

    /// <summary>
    /// The registry the mods' files make, lowest priority first: each file's morphs, then a morph whose
    /// id another already holds is refused (two names hashing alike, or the same name twice), and the
    /// registry stops at <see cref="Capacity"/>.
    /// </summary>
    public static List<ModMorph> Compose(IEnumerable<(string Mod, string Text)> files, List<string> problems)
    {
        var morphs = new List<ModMorph>();
        var byId = new Dictionary<uint, ModMorph>();
        foreach ((string mod, string text) in files)
        {
            var issues = new List<ModMorphIssue>();
            foreach (ModMorph morph in Parse(mod, text, issues))
            {
                if (byId.TryGetValue(morph.Id, out ModMorph? holder))
                {
                    problems.Add(holder.Scoped == morph.Scoped
                        ? $"{mod}:{ManifestPath}: '{morph.Name}' is declared twice; the first stands"
                        : $"{mod}:{ManifestPath}: '{morph.Scoped}' has the id {Hex(morph.Id)} '{holder.Scoped}' already holds; rename one");
                    continue;
                }

                if (morphs.Count >= Capacity)
                {
                    problems.Add($"{mod}:{ManifestPath}: '{morph.Scoped}' is past the {Capacity} mod morphs the hook holds");
                    continue;
                }

                byId[morph.Id] = morph;
                morphs.Add(morph);
            }

            foreach (ModMorphIssue issue in issues.Where(i => i.Error))
                problems.Add($"{mod}:{ManifestPath}: {(issue.Pointer.Length != 0 ? issue.Pointer + ": " : string.Empty)}{issue.Message}");
        }

        return morphs;
    }

    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal) { "name", "label", "default", "min", "max", "step" };

    /// <summary>
    /// One <c>morphs.json</c>: an array of <c>{ "name", "label", "default", "min", "max", "step" }</c>.
    /// <c>name</c> is required (letters, digits and <c>_</c>); <c>label</c> defaults to the name,
    /// <c>min</c> 0, <c>max</c> 1, <c>default</c> <c>min</c>, <c>step</c> 0.05. A morph whose range is
    /// empty or not finite, or whose default lies outside it, is refused.
    /// </summary>
    public static List<ModMorph> Parse(string mod, string text, List<ModMorphIssue> issues)
    {
        var morphs = new List<ModMorph>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text ?? string.Empty);
        }
        catch (JsonException exception)
        {
            issues.Add(new ModMorphIssue(string.Empty, true, $"not valid JSON: {exception.Message}"));
            return morphs;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                issues.Add(new ModMorphIssue(string.Empty, true,
                    "must be a JSON array of morphs: [{ \"name\": \"glow\", \"label\": \"Glow\", \"default\": 0, \"min\": 0, \"max\": 1, \"step\": 0.05 }]"));
                return morphs;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int index = 0;
            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                string at = $"[{index++}]";
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    issues.Add(new ModMorphIssue(at, true, "a morph is an object { \"name\": .., \"min\": .., \"max\": .. }"));
                    continue;
                }

                foreach (JsonProperty property in entry.EnumerateObject())
                {
                    if (!Fields.Contains(property.Name))
                        issues.Add(new ModMorphIssue($"{at}.{property.Name}", false, "not a morph field (name, label, default, min, max, step); ignored"));
                }

                string name = entry.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : string.Empty;
                if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                {
                    issues.Add(new ModMorphIssue($"{at}.name", true, "a morph needs a name of letters, digits and _ (it is the morph's id)"));
                    continue;
                }

                if (!names.Add(name))
                {
                    issues.Add(new ModMorphIssue($"{at}.name", true, $"'{name}' is declared twice"));
                    continue;
                }

                bool ok = true;
                float Number(string field, float fallback)
                {
                    if (!entry.TryGetProperty(field, out JsonElement value)) return fallback;
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d) && double.IsFinite(d)) return (float)d;
                    issues.Add(new ModMorphIssue($"{at}.{field}", true, "must be a number"));
                    ok = false;
                    return fallback;
                }

                float min = Number("min", 0), max = Number("max", 1);
                float initial = Number("default", min), step = Number("step", 0.05f);
                string label = entry.TryGetProperty("label", out JsonElement l) && l.ValueKind == JsonValueKind.String && l.GetString()!.Trim().Length != 0
                    ? l.GetString()!.Trim()
                    : name;
                if (!ok) continue;
                if (!(max > min))
                {
                    issues.Add(new ModMorphIssue(at, true, $"max ({max}) must be above min ({min})"));
                    continue;
                }

                if (initial < min || initial > max)
                {
                    issues.Add(new ModMorphIssue($"{at}.default", true, $"{initial} is outside {min}..{max}"));
                    continue;
                }

                if (!(step > 0) || step > max - min)
                {
                    issues.Add(new ModMorphIssue($"{at}.step", true, $"must be above 0 and at most the range ({max - min})"));
                    continue;
                }

                if (min < -16 || max > 16)
                    issues.Add(new ModMorphIssue(at, false, "a weight past ±16 pushes a target far beyond its model; the guest blends it unclamped"));

                morphs.Add(new ModMorph(mod, name, label, initial, min, max, step));
            }
        }

        return morphs;
    }

    /// <summary>
    /// Publishes the registry for the morph hook (mod_api.cpp reads it back on the generation): the
    /// count, then each morph's id halves, default and range, the generation last.
    /// </summary>
    public static void Publish(IReadOnlyList<ModMorph> morphs, Action<string, float> set, int generation)
    {
        int count = Math.Min(morphs.Count, Capacity);
        set(SettingPrefix + "count", count);
        for (int i = 0; i < count; i++)
        {
            ModMorph morph = morphs[i];
            string prefix = SettingPrefix + i.ToString(CultureInfo.InvariantCulture) + ".";
            set(prefix + "hi", morph.Id >> 16);
            set(prefix + "lo", morph.Id & 0xFFFF);
            set(prefix + "default", morph.Default);
            set(prefix + "min", morph.Minimum);
            set(prefix + "max", morph.Maximum);
        }

        set(SettingPrefix + "generation", generation);
    }

    /// <summary>A player's weight for <paramref name="morph"/> as the hook answers it: the stored value clamped, else the default.</summary>
    public static float Weight(ModMorph morph, float? stored) => stored is { } value && float.IsFinite(value) ? morph.Clamp(value) : morph.Default;
}
