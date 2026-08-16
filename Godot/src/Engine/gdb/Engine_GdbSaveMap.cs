using System;
using System.Collections.Generic;
using System.Text;

namespace Refii.Gdb;

/// <summary>How the level's `.save` divided into layers, and what that gate cost.</summary>
public sealed class SaveLayerStats
{
    public int Layers { get; set; }

    public int LayersOn { get; set; }

    /// <summary>Layers whose `load` is neither literal — `InitiallyOff`, or absent.</summary>
    public int LayersOff { get; set; }

    public int Entities { get; set; }

    /// <summary>Entities skipped because their layer is off at load.</summary>
    public int Gated { get; set; }

    /// <summary>
    /// Entities that were in no layer at all.
    ///
    /// Expected to be zero. The guest's walker only ever descends INTO a layer element, so an
    /// entity outside one can never spawn — but a non-zero count here would mean the file has
    /// a shape this reader does not understand, which is worth seeing rather than silently
    /// dropping.
    /// </summary>
    public int Unlayered { get; set; }

    public override string ToString() =>
        $"layers={Layers} (on={LayersOn} off={LayersOff}) entities={Entities} " +
        $"gated={Gated} unlayered={Unlayered}";
}

/// <summary>
/// One `&lt;Entity&gt;` from a level's `.save`, WITH the layer that contains it.
///
/// The layer is carried because the gate is not decided once at load. `AlwaysOn` is
/// unconditional, but `InitiallyOn` spawns only while its layer is not deactivated, and the
/// guest moves layers in and out of those lists while you play — so a host that resolves the
/// gate at build time and throws the layer away can never follow it afterwards. That is what
/// left `Engine_StaticTwins` inferring the gate from whether an entity happened to appear in a
/// walk of guest memory, which is not the same question and answered it wrongly.
/// </summary>
/// <param name="Record">The GDB record hash the entity names.</param>
/// <param name="Name">Its `name` attribute, for the log.</param>
/// <param name="LayerHash">FNV-1 over the layer `name` attribute's EXACT bytes.</param>
/// <param name="LayerAlwaysOn">
/// True for `AlwaysOn`, false for `InitiallyOn`. Only the second is gateable at runtime — the
/// guest's `AlwaysOn` branch consults no list at all, so nothing may ever hide one of these.
/// </param>
public readonly record struct SaveEntity(
    uint Record, string Name, uint LayerHash, bool LayerAlwaysOn);

public static class Engine_GdbSaveMap
{
    /// <summary>
    /// The two `load` values that spawn on a fresh load.
    ///
    /// VERIFIED IN THE BINARY, `sub_823662C8` @0x823664B4..0x82366548. The guest strcmps the
    /// layer's `load` attribute against exactly two literals and branches:
    ///
    ///   823664d0  bne cr6, loc_82366548     AlwaysOn                  -> spawn
    ///   823664e8  bl  sub_82320A68          InitiallyOn && !deactivated -> spawn
    ///   82366520  bl  sub_8244C428          else: only if the hash is in the ACTIVATED list
    ///
    /// `loc_82366548` is the call to `sub_823665F8`, the child-spawn walk. Both runtime lists
    /// live on the world manager (activated at +60, deactivated at +72) and are pure SAVE-GAME
    /// state — `World_LoadLevelFromSaveHeader` @0x8231C7C0 is the only thing that fills them,
    /// and the new-game path (`CmdLevelChange_Update` @0x823D60B8) does not call it. So for a
    /// fresh world both lists are empty and the rule collapses to exactly these two literals,
    /// which is what a host with no save state can and should implement.
    ///
    /// The third value that occurs is `InitiallyOff` — 1370 of the 1866 layers in the shipped
    /// levels — and it matches neither literal, so it does not spawn. It was the whole defect:
    /// this reader took every `&lt;Entity&gt;` in the file regardless of the layer holding it, so we
    /// placed quest and travel content the guest had deliberately not instantiated.
    /// </summary>
    private static readonly string[] LoadValuesThatSpawn = { "AlwaysOn", "InitiallyOn" };

    public static List<KeyValuePair<uint, string>> Parse(byte[]? saveBytes) =>
        Parse(saveBytes, out _);

    /// <summary>
    /// The same walk, projected to the (record, name) pairs most callers want.
    ///
    /// ONE PARSER. <see cref="ParseEntities"/> is the implementation and this is a view of it —
    /// a second reader of the same XML is how the layer gate and the placement pass would come
    /// to disagree about which entities the level states.
    /// </summary>
    public static List<KeyValuePair<uint, string>> Parse(
        byte[]? saveBytes, out SaveLayerStats stats)
    {
        List<SaveEntity> entities = ParseEntities(saveBytes, out stats);
        var pairs = new List<KeyValuePair<uint, string>>(entities.Count);
        foreach (SaveEntity entity in entities)
        {
            pairs.Add(new KeyValuePair<uint, string>(entity.Record, entity.Name));
        }

        return pairs;
    }

    /// <summary>
    /// The entities a level's `.save` states, GATED BY THEIR LAYER exactly as the guest gates
    /// them.
    ///
    /// The file is XML, one element per layer with the entities nested inside it:
    ///
    ///   &lt;id_5A4CBED4 name="DefaultLayer" load="AlwaysOn"&gt;
    ///       &lt;Entity name="Books_Group_V5"&gt;0x00151B81&lt;/Entity&gt;
    ///   &lt;id_9BF1160F name="QO170_JackLayer" load="InitiallyOff"&gt;
    ///       &lt;Entity name="QO170_SaltyJack"&gt;0x201F9CD3&lt;/Entity&gt;
    ///
    /// LAYER MEMBERSHIP IS POSITIONAL — an entity belongs to the layer that CONTAINS it, and
    /// there is no per-entity layer property. That is why hashing every plausible layer field
    /// name and scanning the shipped data for it found nothing: the relationship is structural.
    /// The guest reads it the same way, passing the enclosing element's hash into the entity
    /// constructor, which stores it at entity+132 and later uses it to destroy the layer.
    ///
    /// The `id_XXXXXXXX` tag is NOT the layer key — the guest hashes the `name` ATTRIBUTE with
    /// FNV-1 (basis 0x811C9DC5, visible as the `lis/ori` pair at 0x823664B4). Measured:
    /// `id_5A4CBED4` carries name="DefaultLayer", whose FNV-1 is 0x67E6B60F. They disagree, so
    /// the tag is some other id and nothing here should key on it. This reader needs neither —
    /// it reads the `load` attribute directly.
    /// </summary>
    public static List<SaveEntity> ParseEntities(
        byte[]? saveBytes, out SaveLayerStats stats)
    {
        stats = new SaveLayerStats();
        var entries = new List<SaveEntity>();
        if (saveBytes is null || saveBytes.Length == 0)
        {
            return entries;
        }

        string xml = Encoding.UTF8.GetString(saveBytes);

        // The layer currently open, if any. Tracked by TAG so the scope ends at its own close
        // element rather than at the first `</...>` that happens along — the entities are
        // children and close themselves constantly.
        string layerTag = string.Empty;
        bool layerSpawns = false;
        uint layerHash = 0;
        bool layerAlwaysOn = false;

        int cursor = 0;
        while (true)
        {
            int open = xml.IndexOf('<', cursor);
            if (open < 0 || open + 1 >= xml.Length)
            {
                break;
            }

            int close = xml.IndexOf('>', open + 1);
            if (close < 0)
            {
                break;
            }

            // A closing tag: only the current layer's own one matters.
            if (xml[open + 1] == '/')
            {
                if (layerTag.Length != 0 &&
                    string.CompareOrdinal(xml, open + 2, layerTag, 0, layerTag.Length) == 0)
                {
                    layerTag = string.Empty;
                    layerSpawns = false;
                    layerHash = 0;
                    layerAlwaysOn = false;
                }

                cursor = close + 1;
                continue;
            }

            if (xml[open + 1] is '?' or '!')
            {
                cursor = close + 1;
                continue;
            }

            int nameEnd = open + 1;
            while (nameEnd < close && !char.IsWhiteSpace(xml[nameEnd]) && xml[nameEnd] != '/')
            {
                ++nameEnd;
            }

            string tag = xml[(open + 1)..nameEnd];
            ReadOnlySpan<char> attributes = xml.AsSpan(nameEnd, close - nameEnd);

            if (tag.Equals("Entity", StringComparison.Ordinal))
            {
                cursor = ReadEntity(
                    xml, open, close, entries, stats,
                    layerTag, layerSpawns, layerHash, layerAlwaysOn);
                continue;
            }

            // ANY element carrying a `load` attribute is a layer. Keyed on the attribute rather
            // than on the `id_` tag shape, because the attribute is what the guest reads and
            // the tag spelling is not something this has to depend on.
            int load = attributes.IndexOf("load=\"".AsSpan(), StringComparison.Ordinal);
            if (load >= 0)
            {
                int valueStart = nameEnd + load + 6;
                int valueEnd = xml.IndexOf('"', valueStart);
                string value = valueEnd < 0 || valueEnd > close
                    ? string.Empty
                    : xml[valueStart..valueEnd];

                ++stats.Layers;
                bool spawns = Array.IndexOf(LoadValuesThatSpawn, value) >= 0;
                if (spawns)
                {
                    ++stats.LayersOn;
                }
                else
                {
                    ++stats.LayersOff;
                }

                // A self-closing layer holds nothing, so it opens no scope.
                if (xml[close - 1] != '/')
                {
                    layerTag = tag;
                    layerSpawns = spawns;

                    // THE LAYER'S OWN KEY, so the gate can be re-decided while the game runs.
                    //
                    // FNV-1 over the `name` attribute's EXACT bytes — the guest hashes it that
                    // way at 0x823664B4, with no lowercasing and no separator normalisation,
                    // unlike the world+level key. The `id_XXXXXXXX` tag is a DIFFERENT id and
                    // must not be used: `id_5A4CBED4` carries name="DefaultLayer", whose FNV-1
                    // is 0x67E6B60F.
                    layerHash = LayerKey(xml, nameEnd, close);
                    layerAlwaysOn = value.Equals("AlwaysOn", StringComparison.Ordinal);
                }
            }

            cursor = close + 1;
        }

        return entries;
    }

    /// <summary>
    /// FNV-1 of a layer element's <c>name="..."</c> attribute, or 0 when it has none.
    ///
    /// Hashed over the attribute's UTF-8 bytes exactly as written. A layer with no name cannot
    /// be matched against the guest's lists and is reported as 0 rather than as some fallback,
    /// because a wrong key would silently gate the wrong entities.
    /// </summary>
    private static uint LayerKey(string xml, int attributesStart, int close)
    {
        const string NameAttribute = "name=\"";

        int at = xml.IndexOf(NameAttribute, attributesStart, StringComparison.Ordinal);
        if (at < 0 || at > close)
        {
            return 0;
        }

        at += NameAttribute.Length;
        int end = xml.IndexOf('"', at);
        return end < 0 || end > close
            ? 0
            : Refii.Core.Engine_GameHash.Fnv1(xml[at..end]);
    }

    /// <summary>
    /// Takes one <c>&lt;Entity name="..."&gt;0xHASH&lt;/Entity&gt;</c> and returns where to resume.
    /// </summary>
    private static int ReadEntity(
        string xml,
        int open,
        int close,
        List<SaveEntity> entries,
        SaveLayerStats stats,
        string layerTag,
        bool layerSpawns,
        uint layerHash,
        bool layerAlwaysOn)
    {
        const string NameAttribute = "name=\"";

        int end = xml.IndexOf("</Entity>", close, StringComparison.Ordinal);
        int resume = end < 0 ? close + 1 : end + "</Entity>".Length;

        if (layerTag.Length == 0)
        {
            ++stats.Unlayered;
            return resume;
        }

        if (!layerSpawns)
        {
            ++stats.Gated;
            return resume;
        }

        int nameStart = xml.IndexOf(NameAttribute, open, StringComparison.Ordinal);
        if (nameStart < 0 || nameStart > close)
        {
            return resume;
        }

        nameStart += NameAttribute.Length;
        int nameEnd = xml.IndexOf('"', nameStart);
        if (nameEnd < 0 || end < 0)
        {
            return resume;
        }

        string name = xml[nameStart..nameEnd];

        int hashStart = xml.IndexOf("0x", close, StringComparison.Ordinal);
        if (hashStart < 0 || hashStart > end)
        {
            return resume;
        }

        uint hash = 0;
        for (int i = hashStart + 2; i < end; i++)
        {
            char c = xml[i];
            int digit;
            if (c is >= '0' and <= '9')
            {
                digit = c - '0';
            }
            else if (c is >= 'A' and <= 'F')
            {
                digit = c - 'A' + 10;
            }
            else if (c is >= 'a' and <= 'f')
            {
                digit = c - 'a' + 10;
            }
            else
            {
                continue;
            }

            hash = unchecked((hash << 4) | (uint)digit);
        }

        entries.Add(new SaveEntity(hash, name, layerHash, layerAlwaysOn));
        ++stats.Entities;
        return resume;
    }
}
