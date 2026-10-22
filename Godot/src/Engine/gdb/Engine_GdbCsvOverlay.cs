using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Refii.Core;

namespace Refii.Gdb;

/// <summary>What a <c>.gdb.csv</c> compiled to: a sparse patch database and the rows refused.</summary>
public sealed class GdbCsvResult
{
    public GdbPatchDatabase Database { get; } = new();

    /// <summary>Rows that were not applied, each with its line and the reason.</summary>
    public List<string> Errors { get; } = new();

    /// <summary>Rows applied (entries declared, properties set or added, aliases).</summary>
    public int Applied { get; set; }
}

/// <summary>
/// THE AUTHORED FORM of a GDB patch: rows a person writes, compiled against the database they
/// edit into the same sparse <see cref="GdbPatchDatabase"/> a <c>.gdb.tu</c> holds, so a mod's
/// <c>globals/globals.gdb.csv</c> merges with every other mod's patch through one compositor.
///
/// <para><b>Rows.</b> A line is <c>entry,property,value</c> or <c>entry,property,type,value</c>;
/// <c>#</c> starts a comment (a <c>#xxxxxxxx,</c> literal key is not one), a leading
/// <c>entry,property[,type],value</c> header is skipped, and fields may be quoted.</para>
/// <list type="bullet">
/// <item><c>Entry,Property,Value</c> — sets a property the entry already has, locally or by
/// inheritance. The type comes from the database, never from the row. An inherited property is
/// MATERIALISED on the entry: the patch declares it there, which is how the game's own data
/// overrides a parent (the guest's lookup stops at the first record declaring the name,
/// <c>GDBEntry_FindPropWalkParents</c> 0x821E5A08) — the siblings keep the parent's value.</item>
/// <item><c>Entry,Property,type,Value</c> — ADDS a property the entry's chain does not have. A
/// stated type on a property that exists must equal the existing type; a changed type is
/// refused.</item>
/// <item><c>NewEntry,parent,ParentEntry</c> — DECLARES an entry the database lacks; it must be the
/// entry's first row. The id is FNV-1 of the name (or the literal given); the name is filed. Its
/// other rows follow, inherited properties resolving through the parent.</item>
/// <item><c>NewEntry,@flags,N</c> — the schema flag byte of a new entry (default: the parent's).</item>
/// <item><c>Alias,@alias,Entry</c> — an extra name for an entry.</item>
/// </list>
/// <para><b>Keys.</b> An entry is a name the database knows (an alias or an id that is the name's
/// FNV-1), a <c>#xxxxxxxx</c> / <c>0xXXXXXXXX</c> literal, or a name declared in this file. A
/// property is its name (its hash is FNV-1 of the name) or a literal. Types: <c>bool int float
/// string enum ref resref</c>, or <c>t&lt;N&gt;</c> by number — the numbers of the shipped data
/// (Int = 1, Enum = 5).</para>
/// <para><b>Values.</b> bool: true/false/1/0; int: decimal or 0x hex; float: decimal or 0x bits;
/// string: text (filed in the name table) or a <c>#</c> literal; enum: a number or a name; ref: an
/// entry name or literal — a name that resolves to nothing is refused, because a dangling
/// reference is what the guest crashes on; resref: an entry, an asset path, or a literal.</para>
/// </summary>
public static class GdbCsvOverlay
{
    public const string CsvSuffix = ".gdb.csv";

    private const string FlagsRow = "@flags";
    private const string AliasRow = "@alias";

    /// <summary>
    /// Compiles <paramref name="text"/> against <paramref name="database"/>. Without a database only
    /// the rows' form is checked (columns, type tokens, value forms); nothing is built.
    /// </summary>
    public static GdbCsvResult Parse(string text, GdbMulti? database)
    {
        var result = new GdbCsvResult();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        var state = new ParseState(database, result);
        bool firstDataLine = true;
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (i == 0 && line.StartsWith('﻿'))
            {
                line = line[1..].Trim();
            }

            // A comment starts with '#'; so does a hash literal, which is a key when a comma follows it.
            if (line.Length == 0 ||
                (line[0] == '#' && !(line.Length > 9 && line[9] == ',' && TryParseHashLiteral(line[..9], out _))))
            {
                continue;
            }

            List<string> cells = Engine_Csv.SplitLine(line);
            for (int c = 0; c < cells.Count; c++)
            {
                cells[c] = cells[c].Trim();
            }

            bool wasFirst = firstDataLine;
            firstDataLine = false;
            if (wasFirst && cells.Count >= 2 &&
                cells[0].Equals("entry", StringComparison.OrdinalIgnoreCase) &&
                cells[1].Equals("property", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (cells.Count is not (3 or 4))
            {
                result.Errors.Add($"line {i + 1}: expected entry,property,value or entry,property,type,value — got {cells.Count} column(s)");
                continue;
            }

            string entryKey = cells[0];
            string propertyKey = cells[1];
            string? typeToken = cells.Count == 4 ? cells[2] : null;
            string value = cells[^1];
            int lineNumber = i + 1;

            if (entryKey.Length == 0 || propertyKey.Length == 0)
            {
                result.Errors.Add($"line {lineNumber}: the entry and the property cannot be empty");
                continue;
            }

            GdbFieldType? statedType = null;
            if (typeToken is not null)
            {
                if (!TryParseType(typeToken, out GdbFieldType parsedType))
                {
                    result.Errors.Add($"line {lineNumber}: unknown type '{typeToken}' (bool, int, float, string, enum, ref, resref, or t<N>)");
                    continue;
                }

                statedType = parsedType;
            }

            string? error = state.ApplyRow(lineNumber, entryKey, propertyKey, statedType, value);
            if (error is not null)
            {
                result.Errors.Add($"line {lineNumber}: {error}");
            }
            else
            {
                result.Applied++;
            }
        }

        return result;
    }

    /// <summary>
    /// The rows that state <paramref name="database"/>: the inverse of <see cref="Parse"/>. Every
    /// property row is typed (a stated type equal to the existing one is accepted on parse), an
    /// entry's <c>parent</c> row comes first so a new entry parses, and names come from the patch's
    /// own table, then <paramref name="names"/>, then as literals.
    /// </summary>
    public static string Format(GdbPatchDatabase database, GdbMulti? names)
    {
        var text = new StringBuilder();
        text.Append("entry,property,type,value\n");

        string EntryText(uint id)
        {
            if (database.Names.TryGetValue(id, out string? own) && own.Length != 0)
            {
                return Quote(own);
            }

            string known = names?.NameOf(id) ?? string.Empty;
            return known.Length != 0 ? Quote(known) : $"#{id:x8}";
        }

        string NameText(uint hash)
        {
            if (database.Names.TryGetValue(hash, out string? own) && own.Length != 0)
            {
                return Quote(own);
            }

            string known = names?.NameOf(hash) ?? string.Empty;
            return known.Length != 0 ? Quote(known) : $"#{hash:x8}";
        }

        foreach (GdbPatchEntry entry in database.Entries)
        {
            string entryText = EntryText(entry.Id);
            var ordered = new List<GdbPatchProperty>(entry.Properties);
            ordered.Sort((a, b) =>
                (a.NameHash == GdbView.HashParent ? 0 : 1).CompareTo(b.NameHash == GdbView.HashParent ? 0 : 1));

            // @flags only where parsing would not default to it: a shipped entry takes its own
            // flags, a new one its parent's.
            byte defaultFlags = 1;
            if (names is not null && names.TryLookup(entry.Id, out GdbCursor own))
            {
                defaultFlags = own.View.RecordSchemaFlags(own.Record);
            }
            else
            {
                GdbPatchProperty? link = entry.Properties.Find(p => p.NameHash == GdbView.HashParent);
                GdbPatchEntry? parentInPatch = link is null ? null : database.Entries.Find(e => e.Id == link.Value);
                if (parentInPatch is not null)
                {
                    defaultFlags = parentInPatch.SchemaFlags;
                }
                else if (link is not null && names is not null && names.TryLookup(link.Value, out GdbCursor parentCursor))
                {
                    defaultFlags = parentCursor.View.RecordSchemaFlags(parentCursor.Record);
                }
            }

            if (entry.SchemaFlags != defaultFlags)
            {
                text.Append(entryText).Append(',').Append(FlagsRow).Append(",int,").Append(entry.SchemaFlags).Append('\n');
            }

            foreach (GdbPatchProperty property in ordered)
            {
                string propertyText = property.NameHash == GdbView.HashParent ? "parent" : NameText(property.NameHash);
                string valueText = property.Type switch
                {
                    GdbFieldType.Bool => property.Value != 0 ? "true" : "false",
                    GdbFieldType.Int => unchecked((int)property.Value).ToString(CultureInfo.InvariantCulture),
                    GdbFieldType.Float => FormatFloat(property.Value),
                    GdbFieldType.String or GdbFieldType.Enum => NameText(property.Value),
                    GdbFieldType.Reference or GdbFieldType.ResourceReference => EntryText(property.Value),
                    _ => $"0x{property.Value:X8}",
                };

                text.Append(entryText).Append(',').Append(propertyText).Append(',')
                    .Append(TypeToken(property.Type)).Append(',').Append(valueText).Append('\n');
            }
        }

        foreach ((uint alias, uint target) in database.Aliases)
        {
            text.Append(NameText(alias)).Append(',').Append(AliasRow).Append(',').Append(EntryText(target)).Append('\n');
        }

        return text.ToString();
    }

    public static string TypeToken(GdbFieldType type) => type switch
    {
        GdbFieldType.Bool => "bool",
        GdbFieldType.Int => "int",
        GdbFieldType.Float => "float",
        GdbFieldType.String => "string",
        GdbFieldType.Enum => "enum",
        GdbFieldType.Reference => "ref",
        GdbFieldType.ResourceReference => "resref",
        _ => $"t{(byte)type}",
    };

    public static bool TryParseType(string token, out GdbFieldType type)
    {
        type = default;
        switch (token.Trim().ToLowerInvariant())
        {
            case "bool": type = GdbFieldType.Bool; return true;
            case "int": type = GdbFieldType.Int; return true;
            case "float": type = GdbFieldType.Float; return true;
            case "string": type = GdbFieldType.String; return true;
            case "enum": type = GdbFieldType.Enum; return true;
            case "ref": type = GdbFieldType.Reference; return true;
            case "resref": type = GdbFieldType.ResourceReference; return true;
        }

        string lowered = token.Trim().ToLowerInvariant();
        if (lowered.Length >= 2 && lowered[0] == 't' &&
            byte.TryParse(lowered.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out byte number))
        {
            type = (GdbFieldType)number;
            return true;
        }

        return false;
    }

    /// <summary><c>#xxxxxxxx</c> or <c>0xXXXXXXXX</c>, eight hex digits.</summary>
    public static bool TryParseHashLiteral(string text, out uint hash)
    {
        hash = 0;
        if (text.Length == 9 && text[0] == '#')
        {
            return uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash);
        }

        if (text.Length == 10 && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash);
        }

        return false;
    }

    /// <summary>The shortest decimal that reparses to the same bits (.NET's default); the bits in hex when not finite.</summary>
    private static string FormatFloat(uint bits)
    {
        float value = BitConverter.Int32BitsToSingle(unchecked((int)bits));
        return float.IsFinite(value) ? value.ToString(CultureInfo.InvariantCulture) : $"0x{bits:X8}";
    }

    private static string Quote(string field) =>
        field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? field : "\"" + field.Replace("\"", "\"\"") + "\"";

    private enum Where
    {
        None,
        Local,
        Inherited,
    }

    private sealed class ParseState
    {
        private readonly GdbMulti? _database;
        private readonly GdbCsvResult _result;
        private readonly Dictionary<uint, GdbPatchEntry> _entries = new();
        private readonly HashSet<uint> _declared = new();

        public ParseState(GdbMulti? database, GdbCsvResult result)
        {
            _database = database;
            _result = result;
        }

        private GdbPatchDatabase Db => _result.Database;

        public string? ApplyRow(int line, string entryKey, string propertyKey, GdbFieldType? statedType, string value)
        {
            if (propertyKey.Equals(AliasRow, StringComparison.OrdinalIgnoreCase))
            {
                return ApplyAlias(entryKey, value);
            }

            bool isParent = propertyKey.Equals("parent", StringComparison.OrdinalIgnoreCase) ||
                (TryParseHashLiteral(propertyKey, out uint literalProperty) && literalProperty == GdbView.HashParent);
            bool isFlags = propertyKey.Equals(FlagsRow, StringComparison.OrdinalIgnoreCase);

            if (_database is null)
            {
                // Form only: a typed number or flag must at least parse as one.
                if (isFlags && !byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    return $"@flags takes a number 0..255, not '{value}'";
                }

                if (statedType is GdbFieldType.Bool or GdbFieldType.Int or GdbFieldType.Float &&
                    !TryParseValue(statedType.Value, value, out _, out string? formError))
                {
                    return $"'{entryKey}.{propertyKey}' ({TypeToken(statedType.Value)}) {formError}";
                }

                return null;
            }

            if (!TryResolveEntry(entryKey, out uint entryId, out bool inDatabase, out bool inOverlay))
            {
                if (!isParent)
                {
                    return $"'{entryKey}' is not an entry of the database; a new entry starts with its parent row: {entryKey},parent,<ParentEntry>";
                }

                // A NEW ENTRY. Its id is the literal given or FNV-1 of its name; the name is filed.
                if (!TryParseHashLiteral(entryKey, out entryId))
                {
                    entryId = Engine_GameHash.Fnv1(entryKey);
                    Db.Names[entryId] = entryKey;
                }

                if (!ResolveReference(value, out uint parentId, out string? parentError))
                {
                    return $"the parent of new entry '{entryKey}' {parentError}";
                }

                var fresh = new GdbPatchEntry(entryId, FlagsOf(parentId));
                fresh.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, parentId));
                Db.Names[GdbView.HashParent] = "parent";
                Db.Entries.Add(fresh);
                _entries[entryId] = fresh;
                _declared.Add(entryId);
                return null;
            }

            if (isFlags)
            {
                if (!_declared.Contains(entryId))
                {
                    return $"'{entryKey}' is a shipped entry; @flags can only be set on an entry this file declares";
                }

                if (!byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte flags))
                {
                    return $"@flags takes a number 0..255, not '{value}'";
                }

                _entries[entryId].SchemaFlags = flags;
                return null;
            }

            uint propertyHash;
            if (isParent)
            {
                propertyHash = GdbView.HashParent;
                Db.Names[GdbView.HashParent] = "parent";
            }
            else if (!TryParseHashLiteral(propertyKey, out propertyHash))
            {
                propertyHash = Engine_GameHash.Fnv1(propertyKey);
            }

            GdbPatchEntry entry = GetOrOpenEntry(entryId);
            if (entry.Properties.Exists(p => p.NameHash == propertyHash))
            {
                return $"'{entryKey}.{propertyKey}' is set twice in this file; the first row stands";
            }

            Where where = ResolveType(entryId, propertyHash, out GdbFieldType existingType);
            GdbFieldType type;
            if (where == Where.None)
            {
                if (statedType is null)
                {
                    return $"'{entryKey}' has no property '{propertyKey}', on itself or its parents; a new property states its type: {entryKey},{propertyKey},<type>,{value}";
                }

                type = statedType.Value;
            }
            else
            {
                if (statedType is not null && statedType.Value != existingType)
                {
                    return $"'{entryKey}.{propertyKey}' is {TypeToken(existingType)}; its type cannot change to {TypeToken(statedType.Value)}";
                }

                type = existingType;
            }

            if (!TryParseValue(type, value, out uint raw, out string? valueError))
            {
                return $"'{entryKey}.{propertyKey}' ({TypeToken(type)}) {valueError}";
            }

            entry.Properties.Add(new GdbPatchProperty(propertyHash, type, raw));
            if (!isParent && !TryParseHashLiteral(propertyKey, out _))
            {
                Db.Names[propertyHash] = propertyKey;
            }

            return null;
        }

        private string? ApplyAlias(string aliasKey, string targetKey)
        {
            if (_database is null)
            {
                return null;
            }

            uint alias;
            if (!TryParseHashLiteral(aliasKey, out alias))
            {
                alias = Engine_GameHash.Fnv1(aliasKey);
                Db.Names[alias] = aliasKey;
            }

            if (!ResolveReference(targetKey, out uint target, out string? error))
            {
                return $"the alias '{aliasKey}' {error}";
            }

            Db.Aliases.Add((alias, target));
            return null;
        }

        private GdbPatchEntry GetOrOpenEntry(uint id)
        {
            if (!_entries.TryGetValue(id, out GdbPatchEntry? entry))
            {
                entry = new GdbPatchEntry(id, FlagsOf(id));
                Db.Entries.Add(entry);
                _entries[id] = entry;
            }

            return entry;
        }

        private byte FlagsOf(uint id)
        {
            if (_entries.TryGetValue(id, out GdbPatchEntry? own))
            {
                return own.SchemaFlags;
            }

            if (_database is not null && _database.TryLookup(id, out GdbCursor cursor))
            {
                return cursor.View.RecordSchemaFlags(cursor.Record);
            }

            return 1;
        }

        /// <summary>An entry key: literal, a name the database knows, or a name this file declared.</summary>
        private bool TryResolveEntry(string key, out uint id, out bool inDatabase, out bool inOverlay)
        {
            inDatabase = false;
            inOverlay = false;
            bool literal = TryParseHashLiteral(key, out id);
            if (!literal)
            {
                id = Engine_GameHash.Fnv1(key);
            }

            if (_database is not null && _database.TryLookup(id, out GdbCursor cursor))
            {
                // A name may be an alias: the record's own id is what the patch must carry.
                id = cursor.View.RecordHash(cursor.Record);
                inDatabase = true;
            }

            if (_entries.ContainsKey(id))
            {
                inOverlay = true;
            }

            return inDatabase || inOverlay || (literal && _database is null);
        }

        /// <summary>A ref value: literal, or a name that resolves to an entry of the database or of this file.</summary>
        private bool ResolveReference(string key, out uint id, out string? error)
        {
            error = null;
            if (TryParseHashLiteral(key, out id))
            {
                return true;
            }

            if (key.Length == 0)
            {
                error = "names no entry (empty)";
                return false;
            }

            if (TryResolveEntry(key, out id, out _, out _))
            {
                return true;
            }

            error = $"names '{key}', which is neither an entry of the database nor one this file declares (a #xxxxxxxx literal states a hash as-is)";
            return false;
        }

        /// <summary>Where <paramref name="propertyHash"/> is declared for <paramref name="entryId"/>, and its type.</summary>
        private Where ResolveType(uint entryId, uint propertyHash, out GdbFieldType type)
        {
            type = default;
            var visited = new HashSet<uint>();
            uint current = entryId;
            bool first = true;
            while (current != 0 && current != GdbHash.Null && visited.Add(current))
            {
                uint parent = 0;
                if (_entries.TryGetValue(current, out GdbPatchEntry? own))
                {
                    GdbPatchProperty? local = own.Properties.Find(p => p.NameHash == propertyHash);
                    if (local is not null)
                    {
                        type = local.Type;
                        return first ? Where.Local : Where.Inherited;
                    }

                    GdbPatchProperty? link = own.Properties.Find(p => p.NameHash == GdbView.HashParent);
                    if (link is not null)
                    {
                        parent = link.Value;
                    }
                }

                if (_database is not null && _database.TryLookup(current, out GdbCursor cursor))
                {
                    if (cursor.View.TryFindLocalRaw(cursor.Record, propertyHash, -1, out _, out type))
                    {
                        return first ? Where.Local : Where.Inherited;
                    }

                    if (_database.TryFindInherited(cursor, propertyHash, -1, out _, out _, out type))
                    {
                        return Where.Inherited;
                    }

                    if (parent == 0 &&
                        cursor.View.TryFindLocalRaw(cursor.Record, GdbView.HashParent, 6, out uint baseParent, out _))
                    {
                        parent = baseParent;
                    }
                }

                current = parent;
                first = false;
            }

            return Where.None;
        }

        private bool TryParseValue(GdbFieldType type, string value, out uint raw, out string? error)
        {
            raw = 0;
            error = null;
            switch (type)
            {
                case GdbFieldType.Bool:
                    switch (value.ToLowerInvariant())
                    {
                        case "true" or "1": raw = 1; return true;
                        case "false" or "0": raw = 0; return true;
                    }

                    error = $"takes true/false, not '{value}'";
                    return false;

                case GdbFieldType.Int:
                    if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                        uint.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
                    {
                        raw = hex;
                        return true;
                    }

                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int signed))
                    {
                        raw = unchecked((uint)signed);
                        return true;
                    }

                    if (uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw))
                    {
                        return true;
                    }

                    error = $"takes a whole number, not '{value}'";
                    return false;

                case GdbFieldType.Float:
                    if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                        uint.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bits))
                    {
                        raw = bits;
                        return true;
                    }

                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    {
                        raw = BitConverter.SingleToUInt32Bits(f);
                        return true;
                    }

                    error = $"takes a number, not '{value}'";
                    return false;

                case GdbFieldType.String:
                    if (value.Length == 9 && value[0] == '#' && TryParseHashLiteral(value, out raw))
                    {
                        return true;
                    }

                    raw = Engine_GameHash.Fnv1(value);
                    Db.Names[raw] = value;
                    return true;

                case GdbFieldType.Enum:
                    if (TryParseHashLiteral(value, out raw))
                    {
                        return true;
                    }

                    if (uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw))
                    {
                        return true;
                    }

                    if (value.Length == 0)
                    {
                        error = "takes a number or a name, not an empty value";
                        return false;
                    }

                    raw = Engine_GameHash.Fnv1(value);
                    Db.Names[raw] = value;
                    return true;

                case GdbFieldType.Reference:
                    if (!ResolveReference(value, out raw, out string? refError))
                    {
                        error = refError;
                        return false;
                    }

                    return true;

                case GdbFieldType.ResourceReference:
                    if (TryParseHashLiteral(value, out raw))
                    {
                        return true;
                    }

                    if (TryResolveEntry(value, out raw, out _, out _))
                    {
                        return true;
                    }

                    if (value.Length == 0)
                    {
                        error = "names no resource (empty)";
                        return false;
                    }

                    raw = GdbPatchBuilder.HashStringOrAssetPath(value);
                    if (!GdbPatchBuilder.IsVirtualAssetPath(value))
                    {
                        Db.Names[raw] = value;
                    }

                    return true;

                default:
                    if (TryParseHashLiteral(value, out raw) ||
                        uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw))
                    {
                        return true;
                    }

                    error = $"takes a number or a literal, not '{value}'";
                    return false;
            }
        }
    }
}
