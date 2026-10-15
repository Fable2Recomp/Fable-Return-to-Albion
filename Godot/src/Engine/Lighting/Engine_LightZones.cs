using System.Collections.Generic;
using Godot;

namespace Refii.Lighting;

/// <summary>
/// The guest's light zones for the modern renderer: a placed light that no shadow map stops at
/// walls lights only its own side, outdoors or in.
/// </summary>
/// <remarks>
/// THE GUEST'S RULE (`sub_82AB6360`, Engine_ClassicZones): a light lights a draw only when either
/// zone is 0 or the two are equal; a zone is the CECBuilding a thing stands in, 1 outdoors. In the
/// modern renderer a light with a world shadow map already stops at walls and still passes
/// windows and doors, which is the look wanted (user, 2026-10-16: through windows and doors is
/// fine, through walls is not). The zone rule is kept for the rest: a light without a map, or
/// whose map holds only the entities (Engine_LevelLights.CasterMaskOf), would otherwise light
/// a room through its wall.
///
/// HOW, IN GODOT'S TERMS. A light reaches an instance when any bit of its cull mask meets one of
/// the instance's layers. An interior draw trades the world bit (1) for <see cref="IndoorMask"/>
/// and keeps its other bits; an outdoor light with no wall-stopping map leaves out the indoor bit
/// and the two bits interiors keep (reflection 16, entity caster 17), an indoor one takes the
/// indoor bit and the lamp posts. World shadow masks are "everything but lamp posts and
/// reflections", so interiors still cast into every map they did. A prop batch can span inside
/// and outside and stays as it is.
/// </remarks>
public static class Engine_LightZones
{
    /// <summary>Render layer 14: an interior draw's stand-in for the world bit.</summary>
    public const uint IndoorMask = 1u << 13;

    public static readonly StringName HostRecordMeta = "light_host_record";

    /// <summary>What an outdoor light with no wall-stopping map lights: everything but interiors.</summary>
    private const uint OutdoorOnly = ~(IndoorMask | Refii.Level.Engine_ReflectionLayer.Mask | Engine_LevelLights.EntityCasterLayerMask);

    /// <summary>
    /// What an indoor light with no wall-stopping map lights: interiors, lamp posts, and the guest's
    /// particles, which carry no zone (a fire's smoke inside takes the room's light).
    /// </summary>
    private const uint IndoorOnly = IndoorMask | Engine_LevelLights.LampHostLayerMask | Refii.Models.Engine_ClassicParticles.Layer;

    private const uint All = uint.MaxValue;

    /// <summary>Frames between re-reads of a light's zone; zones do not move.</summary>
    private const int LightZoneFrames = 120;

    /// <summary>Props zoned per frame while their records wait for the entity walk.</summary>
    private const int PropsPerFrame = 256;

    private sealed class Lit
    {
        public required Light3D Node;
        public required uint Record;
        /// <summary>0 (every zone) until the walk has joined its host to an entity.</summary>
        public uint Zone;
        public int Stagger;
    }

    private static readonly List<Lit> Lights = new();
    private static readonly List<(GeometryInstance3D Node, uint Record, long Until)> Pending = new();

    /// <summary>Frames a prop waits for the walk to join its record; one that never joins stays as it is.</summary>
    private const long PropWaitFrames = 1800;
    private static int _cursor;
    private static long _frame;

    /// <summary>Whether the modern renderer is up (the classic one keeps its own zones).</summary>
    public static bool Active => !Refii.Models.Engine_ClassicMaterial.Active;

    /// <summary>
    /// <paramref name="layers"/> for a draw in <paramref name="zone"/>: an interior's world bit
    /// becomes the indoor bit. Draws without the world bit are left as they are.
    /// </summary>
    public static uint Zoned(uint layers, uint zone) =>
        Active && IsIndoors(zone) && (layers & 1u) != 0u ? (layers & ~1u) | IndoorMask : layers;

    private static bool IsIndoors(uint zone) => zone != 0u && zone != Refii.Models.Engine_ClassicZones.Outdoors;

    /// <summary>A placed light, hosted by placement <paramref name="record"/>. Main thread.</summary>
    public static void AddLight(Light3D node, uint record)
    {
        if (!Active)
        {
            return;
        }
        node.SetMeta(HostRecordMeta, record);
        Lights.Add(new Lit { Node = node, Record = record, Stagger = Lights.Count % LightZoneFrames });
    }

    /// <summary>A streamed prop of placement <paramref name="record"/>, zoned once its entity is known. Main thread.</summary>
    public static void AddProp(GeometryInstance3D node, uint record)
    {
        if (Active && record != 0u && node is not MultiMeshInstance3D)
        {
            Pending.Add((node, record, _frame + PropWaitFrames));
        }
    }

    /// <summary>Forgets every light and prop, for a region change.</summary>
    public static void Clear()
    {
        Lights.Clear();
        Pending.Clear();
        _cursor = 0;
    }

    /// <summary>Zones waiting props and sets each light's mask. Main thread, once a frame.</summary>
    public static void Pump()
    {
        ++_frame;
        ZoneProps();
        for (int i = Lights.Count - 1; i >= 0; i--)
        {
            Lit lit = Lights[i];
            if (!GodotObject.IsInstanceValid(lit.Node))
            {
                Lights.RemoveAt(i);
                continue;
            }
            if ((_frame + lit.Stagger) % LightZoneFrames == 0)
            {
                // An unjoined host lights every zone, as before: never guess "outdoors" for it.
                lit.Zone = Refii.Models.Engine_ClassicZones.TryEntityOf(lit.Record, out uint host)
                    ? Refii.Host.HostBridge.GetEntityLightZone(host)
                    : 0u;
            }
            uint mask = MaskFor(lit);
            if (lit.Node.LightCullMask != mask)
            {
                lit.Node.LightCullMask = mask;
            }
        }
    }

    /// <summary>All for a light whose map stops at walls or which lights every zone; else its own side.</summary>
    private static uint MaskFor(Lit lit)
    {
        Light3D light = lit.Node;
        bool walled = light.ShadowEnabled && (light.ShadowCasterMask & 1u) != 0u;
        if (walled || lit.Zone == 0u)
        {
            return All;
        }
        return IsIndoors(lit.Zone) ? IndoorOnly : OutdoorOnly;
    }

    /// <summary>Props whose record the walk has joined to an entity take their zone's layers, a slice a frame.</summary>
    private static void ZoneProps()
    {
        int checks = System.Math.Min(PropsPerFrame, Pending.Count);
        for (int n = 0; n < checks && Pending.Count != 0; n++)
        {
            _cursor %= Pending.Count;
            (GeometryInstance3D node, uint record, long until) = Pending[_cursor];
            if (!GodotObject.IsInstanceValid(node) || _frame > until)
            {
                Pending.RemoveAt(_cursor);
                continue;
            }
            if (!Refii.Models.Engine_ClassicZones.TryEntityOf(record, out uint entity))
            {
                ++_cursor;
                continue;
            }
            uint layers = Zoned(node.Layers, Refii.Host.HostBridge.GetEntityLightZone(entity));
            if (node.Layers != layers)
            {
                node.Layers = layers;
            }
            Pending.RemoveAt(_cursor);
        }
    }
}
