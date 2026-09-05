using System;
using System.Collections.Generic;
using Refii.Gdb;
using Refii.Level;

namespace Refii.Lighting;

public static class LightingHash
{
    public const uint MainLightShadowed = 0x45E784B9;
    public const uint AmbientColour = 0x2DF6BC3B;
    public const uint LightmapColour = 0xB756359F;
    public const uint AmbientNormalMapDarkeningColour = 0x908D110B;

    // DistantShadowFactor (0xEB9E356D) and EnableLightZoneShadowCulling (0xB3E85CEA) ARE NOT HERE
    // ON PURPOSE. Both are real fields of the theme's Lighting record — descriptor indices 5 and 6,
    // landing at theme+0x60 and theme+0x70 with defaults 0.5 and 1.0 — and both are parsed by the
    // guest and then never read by it. Three independent sweeps for a consumer found none: every
    // load at those displacements resolves to a different object, and every function that reaches
    // the theme through the `+0x50 -> +0xC` chain touches only 0x10/0x20/0x30, the sun-rig block at
    // 0x360-0x410, and 0x4B0 upward.
    //
    // The name EnableLightZoneShadowCulling IS live, but on a different record: `sub_825720A8`
    // reads it off the RENDER SETTINGS entry and sets bit 0x8000 of that object's +0x8C, beside
    // MaxDrawDistance and LodDistance. So the day-set theme's copy is not the one that does
    // anything, and reading it here would have implemented the wrong record.
    //
    // We parsed both, interpolated both across the day-set, and consumed neither — dead weight
    // that read as "lighting features we have not got to yet" rather than as fields with no
    // behaviour behind them.

    public const uint AmbientColourRed = 0x727639A0;
    public const uint AmbientColourGreen = 0xF27F72A0;
    public const uint AmbientColourBlue = 0x27DDDA4B;
    public const uint AmbientColourFactor = 0xB5453B7E;

    public const uint LightmapColourRed = 0xB327A91C;
    public const uint LightmapColourGreen = 0xAA93EE6C;
    public const uint LightmapColourBlue = 0x3B2FF5C7;
    public const uint LightmapColourFactor = 0xB2E38B8A;

    public const uint AmbientNormalMapDarkeningColourRed = 0x845F9A30;
    public const uint AmbientNormalMapDarkeningColourGreen = 0x06BBC2B0;
    public const uint AmbientNormalMapDarkeningColourBlue = 0x649C467B;
}

public sealed class LightingTheme
{
    public bool HasAny { get; set; }
    public bool HasMainLightColour { get; set; }
    public bool HasMainLightShadowed { get; set; }
    public bool HasAmbientColour { get; set; }
    public bool HasLightmapColour { get; set; }
    public bool HasDarkeningColour { get; set; }

    public float[] MainLightColour { get; } = { 1.0f, 1.0f, 1.0f };
    public float[] AmbientColour { get; } = { 0.0f, 0.0f, 0.0f };
    public float[] LightmapColour { get; } = { 1.0f, 1.0f, 1.0f };
    public float[] DarkeningColour { get; } = { 1.0f, 1.0f, 1.0f };

    /// <summary>
    /// Whether the main light casts shadows, as a float the guest compares against 0.5.
    /// </summary>
    /// <remarks>
    /// THE DEFAULT IS OFF, NOT ON, and it is a default the guest arrives at by omission rather
    /// than by choice. <c>sub_82A45DD0</c> writes defaults for eleven theme slots and this is not
    /// one of them — there is no store to theme+0x20 nor to its stated-flag at theme+0xC34 — so
    /// the constructor's zero-fill (<c>li r11, 0xC2</c> over all 194 slots) is what stands. Zero
    /// is not greater than 0.5, so an unstated theme gets no sun shadow.
    ///
    /// It matters less than it looks: 446 of the 449 shipped Lighting records resolve this through
    /// the parent chain. But defaulting it to 1.0, as this did, made the three that do not the one
    /// case where we lit the region differently from the game.
    /// </remarks>
    public float MainLightShadowed { get; set; }

    public float SourceTimeOfDay { get; set; } = -1.0f;
}

public static class Engine_LightingTheme
{
    public static bool TryExtract(IReadOnlyList<byte[]> gdbs, float targetTime, out LightingTheme theme)
    {
        theme = new LightingTheme();
        var chain = Chain.Create(gdbs);
        if (chain is null)
        {
            return false;
        }

        float time = float.IsFinite(targetTime) && targetTime >= 0f ? targetTime : 0.5f;
        bool applied = false;

        if (chain.SelectThemeFromDaySet(time, out RecordRef dayTheme, out float dayTime))
        {
            theme.SourceTimeOfDay = dayTime;
            applied |= ApplyThemeRecord(chain, dayTheme, theme);
        }

        if (!applied)
        {
            foreach (RecordRef global in chain.LookupAll(EnvHash.EnvThemeGlobal))
            {
                applied |= ApplyThemeRecord(chain, global, theme);
            }
        }

        theme.HasAny = applied;
        return applied;
    }

    private static bool ApplyThemeRecord(Chain chain, RecordRef themeRecord, LightingTheme theme)
    {
        if (!themeRecord.Valid)
        {
            return false;
        }

        RecordRef lighting = chain.ResolveRecordField(themeRecord, EnvHash.Lighting);
        if (!lighting.Valid)
        {
            lighting = themeRecord;
        }

        bool any = false;
        var colour = new float[3];

        if (chain.ReadColourRecordField(lighting, EnvHash.MainLightColour, colour) ||
            chain.ReadFlatColour(lighting, EnvHash.MainLightColourRed, EnvHash.MainLightColourGreen,
                EnvHash.MainLightColourBlue, EnvHash.MainLightColourFactor, colour))
        {
            Array.Copy(colour, theme.MainLightColour, 3);
            theme.HasMainLightColour = true;
            any = true;
        }

        if (chain.ReadColourRecordField(lighting, LightingHash.AmbientColour, colour) ||
            chain.ReadFlatColour(lighting, LightingHash.AmbientColourRed, LightingHash.AmbientColourGreen,
                LightingHash.AmbientColourBlue, LightingHash.AmbientColourFactor, colour))
        {
            Array.Copy(colour, theme.AmbientColour, 3);
            theme.HasAmbientColour = true;
            any = true;
        }

        if (chain.ReadColourRecordField(lighting, LightingHash.LightmapColour, colour) ||
            chain.ReadFlatColour(lighting, LightingHash.LightmapColourRed, LightingHash.LightmapColourGreen,
                LightingHash.LightmapColourBlue, LightingHash.LightmapColourFactor, colour))
        {
            Array.Copy(colour, theme.LightmapColour, 3);
            theme.HasLightmapColour = true;
            any = true;
        }

        // NO FACTOR on this one. It is the only lighting colour the guest registers as type 1
        // rather than type 2 (`dword_8349EB8C = 1` at 0x83288CA0), and the type-1 arm of the
        // parser reads Red, Green and Blue only — 0x82A45768 / 0x82A457AC / 0x82A457F0 — then
        // scales by 1/255 alone at 0x82A45A74. Reading a Factor here reads a field the guest
        // never looks at.
        if (chain.ReadColourRecordField(
                lighting, LightingHash.AmbientNormalMapDarkeningColour, colour, useFactor: false) ||
            chain.ReadFlatColour(lighting, LightingHash.AmbientNormalMapDarkeningColourRed,
                LightingHash.AmbientNormalMapDarkeningColourGreen,
                LightingHash.AmbientNormalMapDarkeningColourBlue,
                0, colour))
        {
            Array.Copy(colour, theme.DarkeningColour, 3);
            theme.HasDarkeningColour = true;
            any = true;
        }

        // SunIntensity is NOT a Lighting field. The guest registers it under the Sky record
        // (`addi r29, r10, aSky@l` at 0x83288EA0, used as the record path at 0x83288EA8), so
        // SkyTheme is where it belongs and reading it here found it only when a parent chain
        // happened to leak it down.

        if (chain.ReadFloat(lighting, LightingHash.MainLightShadowed, out float shadowed))
        {
            theme.MainLightShadowed = shadowed;
            theme.HasMainLightShadowed = true;
            any = true;
        }

        return any;
    }
}

public readonly record struct RecordRef(int Db, int Index, bool Valid);

public sealed class Chain
{
    private readonly List<GdbView> _views = new();

    public static Chain? Create(IReadOnlyList<byte[]> gdbs)
    {
        var chain = new Chain();
        foreach (byte[] bytes in gdbs)
        {
            // Shared: the blended lighting extract rebuilds this chain three times per call.
            if (bytes is not null && GdbView.TryParseShared(bytes, out GdbView view, out _))
            {
                chain._views.Add(view);
            }
        }

        return chain._views.Count == 0 ? null : chain;
    }

    public bool SelectThemeFromDaySet(float targetTime, out RecordRef outTheme, out float outTime)
    {
        outTheme = default;
        outTime = targetTime;

        RecordRef daySet = FindDaySet();
        if (!daySet.Valid)
        {
            return false;
        }

        bool found = false;
        float bestDistance = float.MaxValue;

        foreach (GdbField field in _views[daySet.Db].Fields(daySet.Index))
        {
            byte type = (byte)field.Type;
            if (type != 4 && type != 6 && type != 7)
            {
                continue;
            }

            RecordRef entry = FieldToRecord(field);
            if (!entry.Valid)
            {
                continue;
            }

            RecordRef theme = ResolveRecordField(entry, EnvHash.Theme);
            if (!theme.Valid)
            {
                continue;
            }

            float time = targetTime;
            if (ReadFloat(entry, EnvHash.TimeOfDay, out float readTime))
            {
                time = NormalizeTimeOfDay(readTime);
            }

            float distance = Math.Abs(time - targetTime);
            distance = MathF.Min(distance, 1.0f - distance);
            if (!found || distance < bestDistance)
            {
                bestDistance = distance;
                outTheme = theme;
                outTime = time;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// A day-set entry's TimeOfDay to a day fraction in [0,1).
    ///
    /// THE STORED VALUE IS CLOCK FORM, HH.MM — not decimal hours and not a fraction. The guest
    /// takes it apart with integer arithmetic at <c>sub_82A46BD0</c>: it scales by 100 and
    /// truncates (<c>fmuls f0, f31, f29</c> with f29 = 100.0 at 0x82A46CFC, <c>fctiwz</c> at
    /// 0x82A46D0C), then divides and takes the remainder by 100 to get hours and minutes
    /// (<c>divw r7, r11, r16</c> at 0x82A46D20, <c>mulli</c>/<c>subf</c> at 0x82A46D40), and
    /// recombines as <c>hours/24 + (minutes/60)/24</c> — <c>fmadds f4, f5, f30, f9</c> at
    /// 0x82A46D68 with f30 = 1/24 and f28 = 1/60.
    ///
    /// So 6.30 is half past six, 0.2708 of a day. Reading it as decimal hours gives 0.2625 and
    /// reading it as an already-normalised fraction gives 6.30 wrapped to 0.30 — the first is
    /// wrong by twenty minutes of game time, the second picks a different theme entirely. Both
    /// land on a plausible-looking sun, which is why this needed the binary rather than a look
    /// at the numbers.
    /// </summary>
    public static float NormalizeTimeOfDay(float time)
    {
        if (!float.IsFinite(time) || time < 0.0f || time >= 24.0f)
        {
            return 0.5f;
        }

        int clock = (int)(time * 100.0f);
        int hours = clock / 100;
        int minutes = clock % 100;

        return (hours * (1.0f / 24.0f)) + (minutes * (1.0f / 60.0f) * (1.0f / 24.0f));
    }

    /// <summary>
    /// Every LevelData record with a day set, by name — the levels a region's gdb carries and
    /// what each one is lit by. For the sweep; the runtime selects by the loaded level's name.
    /// </summary>
    public List<(string Level, RecordRef DaySet, List<float> Times)> ListLevelDaySets()
    {
        var rows = new List<(string, RecordRef, List<float>)>();
        for (int db = 0; db < _views.Count; db++)
        {
            foreach (RecordRef level in LookupAllInDb(EnvHash.LevelData, db))
            {
                RecordRef daySet = ResolveRecordField(level, EnvHash.EnvironmentThemeDaySet);
                var times = new List<float>();
                if (daySet.Valid)
                {
                    foreach (GdbField field in _views[daySet.Db].Fields(daySet.Index))
                    {
                        byte type = (byte)field.Type;
                        if (type != 4 && type != 6 && type != 7)
                        {
                            continue;
                        }

                        RecordRef entry = FieldToRecord(field);
                        if (entry.Valid && ReadFloat(entry, EnvHash.TimeOfDay, out float t))
                        {
                            times.Add(NormalizeTimeOfDay(t));
                        }
                    }
                }

                rows.Add((_views[db].RecordName(level.Index), daySet, times));
            }
        }

        // The record type is not always LevelData: list every record that carries the field.
        for (int db = 0; db < _views.Count; db++)
        {
            for (int record = 0; record < _views[db].RecordCount; record++)
            {
                var owner = new RecordRef(db, record, true);
                if (!FindLocalField(owner, EnvHash.EnvironmentThemeDaySet, 0xFF, out _))
                {
                    continue;
                }

                RecordRef daySet = ResolveRecordField(owner, EnvHash.EnvironmentThemeDaySet);
                var times = new List<float>();
                if (daySet.Valid)
                {
                    foreach (GdbField field in _views[daySet.Db].Fields(daySet.Index))
                    {
                        byte type = (byte)field.Type;
                        if (type != 4 && type != 6 && type != 7)
                        {
                            continue;
                        }

                        RecordRef entry = FieldToRecord(field);
                        if (entry.Valid && ReadFloat(entry, EnvHash.TimeOfDay, out float t))
                        {
                            times.Add(NormalizeTimeOfDay(t));
                        }
                    }
                }

                rows.Add(($"db{db}:{_views[db].RecordName(record)}", daySet, times));
            }
        }

        return rows;
    }

    private RecordRef FindDaySet()
    {
        for (int db = 0; db < _views.Count; db++)
        {
            foreach (RecordRef level in LookupAllInDb(EnvHash.LevelData, db))
            {
                RecordRef daySet = ResolveRecordField(level, EnvHash.EnvironmentThemeDaySet);
                if (daySet.Valid)
                {
                    return daySet;
                }
            }
        }

        for (int db = 0; db < _views.Count; db++)
        {
            for (int record = 0; record < _views[db].RecordCount; record++)
            {
                var owner = new RecordRef(db, record, true);
                if (!FindLocalField(owner, EnvHash.EnvironmentThemeDaySet, 0xFF, out _))
                {
                    continue;
                }

                RecordRef daySet = ResolveRecordField(owner, EnvHash.EnvironmentThemeDaySet);
                if (daySet.Valid)
                {
                    return daySet;
                }
            }
        }

        return default;
    }

    public List<RecordRef> LookupAll(uint hash)
    {
        var result = new List<RecordRef>();
        if (hash == 0 || hash == EnvHash.Null)
        {
            return result;
        }

        for (int db = 0; db < _views.Count; db++)
        {
            if (_views[db].TryLookup(hash, out int index))
            {
                result.Add(new RecordRef(db, index, true));
            }
        }

        return result;
    }

    private List<RecordRef> LookupAllInDb(uint hash, int db)
    {
        var result = new List<RecordRef>();
        if (hash == 0 || hash == EnvHash.Null || db >= _views.Count)
        {
            return result;
        }

        if (_views[db].TryLookup(hash, out int index))
        {
            result.Add(new RecordRef(db, index, true));
        }

        return result;
    }

    private RecordRef LookupFirst(uint hash)
    {
        List<RecordRef> all = LookupAll(hash);
        return all.Count == 0 ? default : all[0];
    }

    private bool FindLocalField(RecordRef record, uint fieldHash, byte expectedType, out GdbField found)
    {
        found = default;
        if (!record.Valid || record.Db >= _views.Count)
        {
            return false;
        }

        foreach (GdbField field in _views[record.Db].Fields(record.Index))
        {
            if (field.Hash != fieldHash)
            {
                continue;
            }

            if (expectedType != 0xFF && (byte)field.Type != expectedType)
            {
                continue;
            }

            found = field;
            return true;
        }

        return false;
    }

    private bool FindField(RecordRef record, uint fieldHash, byte expectedType, out GdbField found)
    {
        found = default;
        if (!record.Valid || record.Db >= _views.Count)
        {
            return false;
        }

        RecordRef current = record;
        var seen = new HashSet<long>();

        for (int depth = 0; depth < 64; depth++)
        {
            long key = ((long)current.Db << 48) ^ current.Index;
            if (!seen.Add(key))
            {
                return false;
            }

            if (FindLocalField(current, fieldHash, expectedType, out found))
            {
                return true;
            }

            if (!FindLocalField(current, GdbView.HashParent, 0xFF, out GdbField parent))
            {
                return false;
            }

            current = FieldToRecord(parent);
            if (!current.Valid)
            {
                return false;
            }
        }

        return false;
    }

    private RecordRef FieldToRecord(GdbField field)
    {
        if (field.Raw == 0 || field.Raw == EnvHash.Null)
        {
            return default;
        }

        byte type = (byte)field.Type;
        if (type != 4 && type != 6 && type != 7)
        {
            return default;
        }

        return LookupFirst(field.Raw);
    }

    public RecordRef ResolveRecordField(RecordRef record, uint fieldHash)
    {
        return FindField(record, fieldHash, 0xFF, out GdbField field)
            ? FieldToRecord(field)
            : default;
    }

    public bool ReadFloat(RecordRef record, uint fieldHash, out float value)
    {
        value = 0f;
        if (!FindField(record, fieldHash, 3, out GdbField field))
        {
            return false;
        }

        if (!float.IsFinite(field.AsFloat))
        {
            return false;
        }

        value = field.AsFloat;
        return true;
    }

    private bool ReadColour(RecordRef record, uint red, uint green, uint blue, float[] outColour)
    {
        if (!ReadFloat(record, red, out float r) ||
            !ReadFloat(record, green, out float g) ||
            !ReadFloat(record, blue, out float b))
        {
            return false;
        }

        outColour[0] = ToLinearInput(r);
        outColour[1] = ToLinearInput(g);
        outColour[2] = ToLinearInput(b);
        return true;
    }

    public bool ReadFlatColour(
        RecordRef record, uint red, uint green, uint blue, uint factorHash, float[] outColour)
    {
        if (!ReadColour(record, red, green, blue, outColour))
        {
            return false;
        }

        if (factorHash != 0 &&
            ReadFloat(record, factorHash, out float factor) &&
            float.IsFinite(factor) && factor > 0f)
        {
            outColour[0] *= factor;
            outColour[1] *= factor;
            outColour[2] *= factor;
        }

        return true;
    }

    public bool ReadColourRecordField(RecordRef record, uint fieldHash, float[] outColour) =>
        ReadColourRecordField(record, fieldHash, outColour, useFactor: true);

    /// <summary>
    /// Reads a colour held as a child record with Red/Green/Blue/Factor fields.
    ///
    /// <paramref name="useFactor"/> exists because the guest's own parser distinguishes two
    /// colour kinds and one of them has no Factor at all — see the type-1 note at the
    /// AmbientNormalMapDarkeningColour read site.
    /// </summary>
    public bool ReadColourRecordField(
        RecordRef record, uint fieldHash, float[] outColour, bool useFactor)
    {
        RecordRef colour = ResolveRecordField(record, fieldHash);
        if (!colour.Valid)
        {
            return false;
        }

        if (!ReadColour(colour, EnvHash.Red, EnvHash.Green, EnvHash.Blue, outColour))
        {
            return false;
        }

        if (useFactor &&
            ReadFloat(colour, EnvHash.Factor, out float factor) &&
            float.IsFinite(factor) && factor > 0f)
        {
            outColour[0] *= factor;
            outColour[1] *= factor;
            outColour[2] *= factor;
        }

        return true;
    }

    /// <summary>
    /// GDB colour channels are authored 0..255 and the engine wants 0..1.
    ///
    /// The divisor is exactly 255 and there is no gamma step: <c>sub_82A44D08</c> holds
    /// <c>flt_833216C8 = 0.0039215689</c> in f31 for the whole parse and applies it with a
    /// single <c>fmuls</c> per channel — <c>fmuls f9, f12, f31</c> at 0x82A45644 — with no
    /// <c>pow</c>, no table and no square anywhere in the function.
    ///
    /// NOT CLAMPED, deliberately. The guest does not clamp, and the values genuinely exceed
    /// one: this region's LightmapColour comes out above five. Clamping the ratio before the
    /// Factor multiply silently caps any channel authored at or past 255 and quietly changes
    /// the hue of every tint that has one.
    /// </summary>
    private static float ToLinearInput(float value) =>
        float.IsFinite(value) ? MathF.Max(value * (1f / 255f), 0f) : 0f;
}
