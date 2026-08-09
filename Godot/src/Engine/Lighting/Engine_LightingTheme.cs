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
    public const uint DistantShadowFactor = 0xEB9E356D;
    public const uint EnableLightZoneShadowCulling = 0xB3E85CEA;

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
    public const uint AmbientNormalMapDarkeningColourFactor = 0x2059168E;
}

public sealed class LightingTheme
{
    public bool HasAny { get; set; }
    public bool HasMainLightColour { get; set; }
    public bool HasMainLightShadowed { get; set; }
    public bool HasAmbientColour { get; set; }
    public bool HasLightmapColour { get; set; }
    public bool HasDarkeningColour { get; set; }
    public bool HasSunIntensity { get; set; }
    public bool HasDistantShadowFactor { get; set; }
    public bool HasLightZoneShadowCulling { get; set; }

    public float[] MainLightColour { get; } = { 1.0f, 1.0f, 1.0f };
    public float[] AmbientColour { get; } = { 0.0f, 0.0f, 0.0f };
    public float[] LightmapColour { get; } = { 1.0f, 1.0f, 1.0f };
    public float[] DarkeningColour { get; } = { 1.0f, 1.0f, 1.0f };

    public float SunIntensity { get; set; } = 1.0f;
    public float MainLightShadowed { get; set; } = 1.0f;
    public float DistantShadowFactor { get; set; } = 1.0f;
    public float EnableLightZoneShadowCulling { get; set; }

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

        if (chain.ReadColourRecordField(lighting, LightingHash.AmbientNormalMapDarkeningColour, colour) ||
            chain.ReadFlatColour(lighting, LightingHash.AmbientNormalMapDarkeningColourRed,
                LightingHash.AmbientNormalMapDarkeningColourGreen,
                LightingHash.AmbientNormalMapDarkeningColourBlue,
                LightingHash.AmbientNormalMapDarkeningColourFactor, colour))
        {
            Array.Copy(colour, theme.DarkeningColour, 3);
            theme.HasDarkeningColour = true;
            any = true;
        }

        if (chain.ReadFloat(lighting, EnvHash.SunIntensity, out float sunIntensity))
        {
            theme.SunIntensity = sunIntensity;
            theme.HasSunIntensity = true;
            any = true;
        }

        if (chain.ReadFloat(lighting, LightingHash.MainLightShadowed, out float shadowed))
        {
            theme.MainLightShadowed = shadowed;
            theme.HasMainLightShadowed = true;
            any = true;
        }

        if (chain.ReadFloat(lighting, LightingHash.DistantShadowFactor, out float distant))
        {
            theme.DistantShadowFactor = distant;
            theme.HasDistantShadowFactor = true;
            any = true;
        }

        if (chain.ReadFloat(lighting, LightingHash.EnableLightZoneShadowCulling, out float zoneCulling))
        {
            theme.EnableLightZoneShadowCulling = zoneCulling;
            theme.HasLightZoneShadowCulling = true;
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
            if (bytes is not null && GdbView.TryParse(bytes, out GdbView view, out _))
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

    private static float NormalizeTimeOfDay(float time)
    {
        if (!float.IsFinite(time))
        {
            return 0.5f;
        }

        if (time > 1.0f && time <= 24.0f)
        {
            time *= 1.0f / 24.0f;
        }

        time -= MathF.Floor(time);
        if (time < 0.0f)
        {
            time += 1.0f;
        }

        return time;
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

    public bool ReadColourRecordField(RecordRef record, uint fieldHash, float[] outColour)
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

        if (ReadFloat(colour, EnvHash.Factor, out float factor) &&
            float.IsFinite(factor) && factor > 0f)
        {
            outColour[0] *= factor;
            outColour[1] *= factor;
            outColour[2] *= factor;
        }

        return true;
    }

    private static float ToLinearInput(float value) =>
        Math.Clamp(value * (1f / 255f), 0f, 1f);
}
