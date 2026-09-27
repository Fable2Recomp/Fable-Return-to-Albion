using System;
using System.Collections.Generic;

namespace Refii.Gdb;

/// <summary>
/// What an entity definition states about drawing its static mesh, as the guest reads it.
/// </summary>
/// <remarks>
/// The distances are properties of the definition's GraphicAppearanceComponent record, read by
/// `sub_82572438` into the drawable's ten-float block: MaxDrawDistance (times a class multiplier
/// that is 1.0 for every class), DrawDistanceFadeRangeFactor, BillboardDistance, LodDistance,
/// FadeInDistance, FadeInRangeFactor. The guest clamps a non-positive MaxDrawDistance to 0 and
/// BillboardDistance to at most MaxDrawDistance. A property the record chain does not carry
/// reads 0 (`sub_82196B80` answers 0.0 for an absent property). The LOD and shadow models are
/// the StaticMeshComponent's LodModel and ShadowModel (`sub_828E7798`), as model path hashes.
/// </remarks>
public readonly record struct GdbDrawSettings(
    float MaxDrawDistance,
    float FadeRangeFactor,
    float LodDistance,
    float BillboardDistance,
    float FadeInDistance,
    float FadeInRangeFactor,
    uint LodModelHash,
    uint ShadowModelHash);

public static class Engine_GdbDrawSettings
{
    public const uint MaxDrawDistance = 0x171E1806;
    public const uint DrawDistanceFadeRangeFactor = 0x5DC9317E;
    public const uint LodDistance = 0x5B077413;
    public const uint BillboardDistance = 0xBFFC8DDB;
    public const uint FadeInDistance = 0x74D853D3;
    public const uint FadeInRangeFactor = 0x5EAEA400;
    public const uint LodModel = 0x43E4F945;
    public const uint ShadowModel = 0xAA3B436C;

    /// <summary>
    /// The settings of the record <paramref name="recordHash"/> names (a placement or a
    /// definition), through its parents. False when no record in the chain carries a
    /// GraphicAppearanceComponent, which is not a statement of 0 but of no appearance here.
    /// </summary>
    public static bool TryRead(GdbView view, uint recordHash, out GdbDrawSettings settings)
    {
        settings = default;
        if (recordHash == 0 || recordHash == GdbHash.Null || !view.TryLookup(recordHash, out int record))
        {
            return false;
        }

        int appearance = -1;
        Walk(view, record, r =>
        {
            if (appearance < 0 &&
                view.TryFindLocalRaw(r, GdbHash.GraphicAppearanceComponent, 6, out uint reference, out _) &&
                view.TryLookup(reference, out int appearanceRecord))
            {
                appearance = appearanceRecord;
            }
        });
        if (appearance < 0)
        {
            return false;
        }

        float maxDraw = Float(view, appearance, MaxDrawDistance);
        float billboard = Float(view, appearance, BillboardDistance);
        if (maxDraw <= 0f)
        {
            maxDraw = 0f;
        }

        if (maxDraw < billboard)
        {
            billboard = maxDraw;
        }

        uint lodModel = 0;
        uint shadowModel = 0;
        Walk(view, appearance, r =>
        {
            if (!view.TryFindLocalRaw(r, GdbHash.StaticMeshComponent, 6, out uint reference, out _) ||
                !view.TryLookup(reference, out int mesh))
            {
                return;
            }

            if (lodModel == 0)
            {
                lodModel = PathHash(view, mesh, LodModel);
            }

            if (shadowModel == 0)
            {
                shadowModel = PathHash(view, mesh, ShadowModel);
            }
        });

        settings = new GdbDrawSettings(
            maxDraw,
            Float(view, appearance, DrawDistanceFadeRangeFactor),
            Float(view, appearance, LodDistance),
            billboard,
            Float(view, appearance, FadeInDistance),
            Float(view, appearance, FadeInRangeFactor),
            lodModel,
            shadowModel);
        return true;
    }

    /// <summary>The first float the record or its parents state for the field; 0 when none does.</summary>
    private static float Float(GdbView view, int record, uint field)
    {
        float value = 0f;
        bool found = false;
        Walk(view, record, r =>
        {
            if (!found && view.TryFindLocalRaw(r, field, (int)GdbFieldType.Float, out uint raw, out _))
            {
                value = BitConverter.Int32BitsToSingle(unchecked((int)raw));
                found = true;
            }
        });
        return float.IsFinite(value) ? value : 0f;
    }

    /// <summary>The first model path hash the record or its parents state for a string field; 0 when none does.</summary>
    private static uint PathHash(GdbView view, int record, uint field)
    {
        uint hash = 0;
        Walk(view, record, r =>
        {
            if (hash == 0 &&
                view.TryFindLocalRaw(r, field, -1, out uint raw, out GdbFieldType type) &&
                type is GdbFieldType.String or GdbFieldType.Float &&
                raw != 0 && raw != GdbHash.Null)
            {
                hash = raw;
            }
        });
        return hash;
    }

    private static void Walk(GdbView view, int record, Action<int> visit)
    {
        var seen = new HashSet<int>();
        int current = record;
        for (int depth = 0; depth < 64 && seen.Add(current); depth++)
        {
            visit(current);
            if (!view.TryFindLocalRaw(current, GdbView.HashParent, 6, out uint parent, out _) ||
                parent == 0 || parent == GdbHash.Null || !view.TryLookup(parent, out current))
            {
                return;
            }
        }
    }
}
