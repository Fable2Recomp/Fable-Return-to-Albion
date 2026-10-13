using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// The lighting zone each draw is in, as the guest states it: a light lights a draw only when
/// either zone is 0 or the two are equal (`sub_82AB6360`, 0x82AB6428-0x82AB644C).
/// </summary>
/// <remarks>
/// A zone is the CECBuilding a thing stands in, named by that building's definition hash; 1 is
/// outdoors, the zone every mesh object is constructed in (`sub_82AA2658`, 0x82AA26D8), and 0
/// is every zone. A landscape patch is outdoors for good (its ctor stores 1 at patch+0x64,
/// 0x82A90E1C, and nothing re-zones it). The guest keeps the rest per entity (`HostBridge.GetEntityLightZone`), so a draw's
/// zone is its entity's: a live node names its entity, a region copy names its .save record,
/// which the entity walk joins to the live entity.
/// </remarks>
public static class Engine_ClassicZones
{
    /// <summary>Outdoors: the mesh constructor's zone, for a draw with no entity behind it.</summary>
    public const uint Outdoors = 1u;

    private static readonly StringName EntityAddressMeta = "entity_address";
    private static readonly Dictionary<uint, uint> _entityOfRecord = new();

    /// <summary>The live entity built from <paramref name="record"/>; the entity walk, every pass.</summary>
    public static void NoteEntity(uint record, uint address)
    {
        if (record == 0u || address == 0u)
        {
            return;
        }
        lock (_entityOfRecord)
        {
            _entityOfRecord[record] = address;
        }
    }

    /// <summary>Forgets every record, for a region change.</summary>
    public static void Clear()
    {
        lock (_entityOfRecord)
        {
            _entityOfRecord.Clear();
        }
    }

    /// <summary>
    /// What a node names: its live entity, else its .save record (0 for either when absent).
    /// Read once per node; both are set when the node is made.
    /// </summary>
    public static (uint Entity, uint Record) Identify(GeometryInstance3D node)
    {
        if (node.HasMeta(EntityAddressMeta))
        {
            return (node.GetMeta(EntityAddressMeta).AsUInt32(), 0u);
        }
        if (node.HasMeta(Refii.Entities.Engine_EntityInstance.HashMeta))
        {
            string stated = node.GetMeta(Refii.Entities.Engine_EntityInstance.HashMeta).AsString();
            if (stated.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(stated[2..], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out uint record))
            {
                return (0u, record);
            }
        }
        return (0u, 0u);
    }

    /// <summary>The zone of a draw that names <paramref name="entity"/> or <paramref name="record"/>.</summary>
    public static uint Of(uint entity, uint record)
    {
        if (entity == 0u && record != 0u)
        {
            lock (_entityOfRecord)
            {
                _entityOfRecord.TryGetValue(record, out entity);
            }
        }
        return entity != 0u ? Refii.Host.HostBridge.GetEntityLightZone(entity) : Outdoors;
    }

    /// <summary>Whether a light in <paramref name="lightZone"/> lights a draw in <paramref name="drawZone"/>.</summary>
    public static bool Lights(uint lightZone, uint drawZone) =>
        lightZone == 0u || drawZone == 0u || lightZone == drawZone;
}
