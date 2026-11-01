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

    /// <summary>Render layer 15: an indoor lamp-host fixture's stand-in for <see cref="Engine_LevelLights.LampHostLayerMask"/>.</summary>
    public const uint IndoorLampHostMask = 1u << 14;

    public const string HostRecordMeta = "light_host_record";

    /// <summary>What an outdoor light lights: everything but interiors and indoor lamp hosts.</summary>
    private const uint OutdoorOnly = ~(IndoorMask | IndoorLampHostMask | Refii.Level.Engine_ReflectionLayer.Mask | Engine_LevelLights.EntityCasterLayerMask);

    /// <summary>
    /// What an indoor light lights: interiors (including characters zoned to the interior via Zoned()),
    /// indoor lamp-host fixtures, and the guest's particles, which carry no zone.
    /// Does not include EntityCasterLayerMask or LampHostLayerMask so indoor lights do not leak
    /// through walls onto outdoor NPCs or outdoor street-lamp fixtures.
    /// </summary>
    private const uint IndoorOnly = IndoorMask | IndoorLampHostMask | Refii.Models.Engine_ClassicParticles.Layer;

    private const uint All = uint.MaxValue;

    /// <summary>Frames between re-reads of a light's zone; zones do not move.</summary>
    private const int LightZoneFrames = 120;

    /// <summary>Props zoned per frame while their records wait for the entity walk.</summary>
    private const int PropsPerFrame = 256;

    private sealed class Lit
    {
        public required Light3D Node;
        public required uint Record;
        public bool IgnoreZoning;
        /// <summary>0 (every zone) until the walk has joined its host to an entity.</summary>
        public uint Zone;
        public uint StatedMask;
        public int Stagger;
    }

    private static readonly List<Lit> Lights = new();
    private static readonly Dictionary<Light3D, uint> LightZoneByNode = new();

    /// <summary>Bumped whenever a light's zone or cull mask is stated or the set is cleared, so a
    /// reader caching <see cref="ZoneOf"/> or <see cref="IsIndoorLight"/> per light knows to re-read.</summary>
    public static int Generation { get; private set; }
    private static readonly Dictionary<uint, int> InteriorCountByEntity = new();
    private static readonly List<(GeometryInstance3D Node, uint Record, long Until)> Pending = new();

    /// <summary>Frames a prop waits for the walk to join its record; one that never joins stays as it is.</summary>
    private const long PropWaitFrames = 1800;
    private static int _cursor;
    private static long _frame;
    private static int _unresolvedLights;
    private static bool _lastInterior;

    /// <summary>Whether the modern renderer is up (the classic one keeps its own zones).</summary>
    public static bool Active => !Refii.Models.Engine_ClassicMaterial.Active;

    /// <summary>The hero's current CECBuilding light zone (0 if unknown, 1 if outdoors, >1 if inside a building).</summary>
    public static uint HeroZone { get; set; } = Refii.Models.Engine_ClassicZones.Outdoors;

    /// <summary>Whether the hero is currently inside a building zone or the level is an interior.</summary>
    public static bool IsViewerIndoors => Engine_LightingFrame.CurrentInterior || IsIndoors(HeroZone);

    /// <summary>The resolved CECBuilding zone of <paramref name="light"/> (0 if unzoned, 1 if outdoors, >1 if inside a building).</summary>
    public static uint ZoneOf(Light3D light) => LightZoneByNode.TryGetValue(light, out uint z) ? z : 0u;

    /// <summary>
    /// <paramref name="layers"/> for a draw in <paramref name="zone"/>: an interior's world bit
    /// becomes <see cref="IndoorMask"/>, and an indoor lamp-host fixture's <see cref="Engine_LevelLights.LampHostLayerMask"/>
    /// becomes <see cref="IndoorLampHostMask"/> (leaving out <see cref="IndoorMask"/> so the fixture never
    /// casts shadows into placed lights). Draws without the world/lamp-host bits or in an interior level
    /// are left as they are.
    /// </summary>
    public static uint Zoned(uint layers, uint zone)
    {
        if (!Active || Engine_LightingFrame.CurrentInterior || !IsIndoors(zone))
        {
            return layers;
        }

        if ((layers & Engine_LevelLights.LampHostLayerMask) != 0u)
        {
            return (layers & ~(1u | Engine_LevelLights.LampHostLayerMask | Engine_LevelLights.EntityCasterLayerMask)) | IndoorLampHostMask;
        }

        if ((layers & 1u) != 0u)
        {
            return (layers & ~1u) | IndoorMask;
        }

        return layers;
    }

    /// <summary>
    /// Resolves a light's effective zone from its placement <paramref name="record"/> and the
    /// bridge's <paramref name="rawZone"/> (<c>RefiiHostGetEntityLightZone</c>).
    /// </summary>
    /// <remarks>
    /// In the guest (<c>sub_825C2060</c>, <c>sub_825C2108</c>), <c>CECGraphicAppearanceLight</c>
    /// forces zone 0 when <c>IgnoreZoning</c> is set, and otherwise reads <c>renderable + 0xD0</c>
    /// without taking <c>CECBuilding</c>'s definition-hash override (<c>record &amp; 0x7FFFFFFF</c>).
    /// A light hosted directly on a <c>CECBuilding</c> entity (such as a buyable market stall) is
    /// therefore in <see cref="Refii.Models.Engine_ClassicZones.Outdoors"/> (1).
    /// </remarks>
    public static uint ResolveLightZone(uint record, uint rawZone, bool ignoreZoning = false)
    {
        if (ignoreZoning)
        {
            return 0u;
        }
        if (rawZone != 0u && rawZone == (record & 0x7FFFFFFFu))
        {
            return Refii.Models.Engine_ClassicZones.Outdoors;
        }
        return rawZone;
    }

    /// <summary>
    /// Resolves a static prop's effective zone from its placement <paramref name="record"/>, the
    /// bridge's <paramref name="rawZone"/>, its <paramref name="modelPath"/>, the number of
    /// interior mesh records on the entity (<c>GetEntityInteriorMeshes</c>), and whether the
    /// placement hosts its own light (<paramref name="isLampHost"/>).
    /// </summary>
    /// <remarks>
    /// In <c>sub_828F9F90</c> (<c>0x828FA4D8</c>), a <c>CECBuilding</c>'s mesh record takes the
    /// building's interior zone (<c>record &amp; 0x7FFFFFFF</c>) only when its GDB <c>Interior</c>
    /// flag is set; building <c>exterior.mdl</c> meshes and open market stalls (<paramref name="interiorMeshCount"/> == 0
    /// and <paramref name="isLampHost"/>, or <c>MarketStall</c> models) stay in
    /// <see cref="Refii.Models.Engine_ClassicZones.Outdoors"/> (1), while single-mesh enclosed
    /// <c>CECBuilding</c> chambers (<c>sub_822A7EB8</c>) retain their building zone.
    /// </remarks>
    public static uint ResolvePropZone(
        uint record,
        uint rawZone,
        string? modelPath,
        int interiorMeshCount,
        bool isLampHost = false)
    {
        if (rawZone != 0u && rawZone == (record & 0x7FFFFFFFu))
        {
            if ((modelPath is not null &&
                 modelPath.EndsWith("exterior.mdl", System.StringComparison.OrdinalIgnoreCase)) ||
                (!Engine_LightingFrame.CurrentInterior && interiorMeshCount == 0))
            {
                return Refii.Models.Engine_ClassicZones.Outdoors;
            }
        }
        return rawZone;
    }

    /// <summary>Whether a light zone is a building's (not 0, not outdoors).</summary>
    public static bool IsIndoors(uint zone) => zone != 0u && zone != Refii.Models.Engine_ClassicZones.Outdoors;

    /// <summary>Whether <paramref name="light"/> has been zoned strictly to an interior building.</summary>
    public static bool IsIndoorLight(Light3D light) => Active && (light.LightCullMask & 1u) == 0u;

    /// <summary>Whether <paramref name="light"/> has been zoned strictly to the outdoors.</summary>
    public static bool IsOutdoorLight(Light3D light) => Active && (light.LightCullMask & IndoorMask) == 0u;

    /// <summary>A placed light, hosted by placement <paramref name="record"/>. Main thread.</summary>
    public static void AddLight(Light3D node, uint record, bool ignoreZoning = false)
    {
        if (!Active)
        {
            return;
        }
        node.SetMeta(HostRecordMeta, record);
        Lights.Add(new Lit
        {
            Node = node,
            Record = record,
            IgnoreZoning = ignoreZoning,
            Stagger = Lights.Count % LightZoneFrames,
        });
        _unresolvedLights++;
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
        LightZoneByNode.Clear();
        InteriorCountByEntity.Clear();
        Generation++;
        Pending.Clear();
        _cursor = 0;
        _unresolvedLights = 0;
        HeroZone = Refii.Models.Engine_ClassicZones.Outdoors;
    }

    /// <summary>Zones waiting props and sets each light's mask. Main thread, once a frame.</summary>
    public static void Pump()
    {
        bool interiorChanged = Engine_LightingFrame.CurrentInterior != _lastInterior;
        if (!Refii.Core.Engine_GuestFrame.Changed && !interiorChanged && Pending.Count == 0 && _frame > 0)
        {
            return;
        }

        _lastInterior = Engine_LightingFrame.CurrentInterior;
        ++_frame;
        if (Refii.Core.Engine_GuestFrame.Changed)
        {
            uint hero = Refii.Host.HostBridge.HeroEntityAddress();
            if (hero != 0u)
            {
                uint z = Refii.Host.HostBridge.GetEntityLightZone(hero);
                if (z != 0u)
                {
                    HeroZone = z;
                }
            }
        }
        ZoneProps();
        if (!interiorChanged && _unresolvedLights == 0 && (_frame % LightZoneFrames) != 0)
        {
            return;
        }

        int unresolved = 0;
        bool anyMaskChanged = false;
        for (int i = Lights.Count - 1; i >= 0; i--)
        {
            Lit lit = Lights[i];
            bool settled = lit.IgnoreZoning || IsIndoors(lit.Zone) || (lit.Zone != 0u && _frame > PropWaitFrames);
            if (settled && lit.StatedMask == MaskFor(lit))
            {
                if ((_frame + lit.Stagger) % LightZoneFrames == 0 && !GodotObject.IsInstanceValid(lit.Node))
                {
                    Lights.RemoveAt(i);
                }
                continue;
            }
            if (!GodotObject.IsInstanceValid(lit.Node))
            {
                Lights.RemoveAt(i);
                continue;
            }
            if (!settled && (_frame == 1 || (_frame + lit.Stagger) % LightZoneFrames == 0))
            {
                // Static placed lights never move between buildings; re-poll outdoor (Zone == 1)
                // lights during initial streaming in case their CECBuilding registers late.
                //
                // THE LIGHT TAKES ITS HOST'S OWN ZONE. Fable 3's CECGraphicAppearanceLight builds
                // each light descriptor from the renderable's zone word, not the static-mesh
                // building override (`sub_827983C0` 0x82798414: `+154 ? 0 : *(block + 60)`, the
                // renderable's +0xF4), so a light on a CECBuilding entity is zoned by rend+0xF4
                // (RefiiHostGetEntityOwnLightZone). Fable 2 keeps the override path it was
                // verified against; ResolveLightZone's record-hash rule covers its lamp hosts.
                if (Refii.Models.Engine_ClassicZones.TryEntityOf(lit.Record, out uint host))
                {
                    uint hostZone = Refii.Core.Engine_Title.Active == Refii.Core.Engine_Title.Fable3
                        ? Refii.Host.HostBridge.GetEntityOwnLightZone(host)
                        : Refii.Host.HostBridge.GetEntityLightZone(host);
                    uint resolved = ResolveLightZone(lit.Record, hostZone, lit.IgnoreZoning);
                    if (resolved != 0u && resolved != lit.Zone)
                    {
                        lit.Zone = resolved;
                        LightZoneByNode[lit.Node] = resolved;
                        Generation++;
                    }
                }
            }
            uint mask = MaskFor(lit);
            if (lit.StatedMask != mask)
            {
                lit.StatedMask = mask;
                if (lit.Node.LightCullMask != mask)
                {
                    lit.Node.LightCullMask = mask;
                    Engine_LevelLights.RefreshLightScales(lit.Node);
                    Generation++;
                    anyMaskChanged = true;
                }
            }
            if (!lit.IgnoreZoning && !IsIndoors(lit.Zone) && _frame <= PropWaitFrames)
            {
                unresolved++;
            }
        }
        if (anyMaskChanged)
        {
            Engine_LightFlicker.RefreshAllBaseValues();
        }
        _unresolvedLights = unresolved;
    }

    /// <summary>Lights its own zone; unzoned lights and interior-level lights light all zones.</summary>
    private static uint MaskFor(Lit lit)
    {
        if (lit.IgnoreZoning || lit.Zone == 0u || Engine_LightingFrame.CurrentInterior)
        {
            return All;
        }
        return IsIndoors(lit.Zone) ? IndoorOnly : OutdoorOnly;
    }

    /// <summary>Props whose record the walk has joined to an entity take their zone's layers, a slice a frame.</summary>
    private static void ZoneProps()
    {
        if (Engine_LightingFrame.CurrentInterior)
        {
            Pending.Clear();
            _cursor = 0;
            return;
        }

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
            uint rawZone = Refii.Host.HostBridge.GetEntityLightZone(entity);
            if (rawZone == (record & 0x7FFFFFFFu))
            {
                string path = node.HasMeta(Refii.Level.Engine_PropStreamer.ModelPathMeta)
                    ? node.GetMeta(Refii.Level.Engine_PropStreamer.ModelPathMeta).AsString()
                    : string.Empty;
                if (!InteriorCountByEntity.TryGetValue(entity, out int intCount))
                {
                    intCount = Refii.Host.HostBridge.GetEntityInteriorMeshes(entity).Length;
                    InteriorCountByEntity[entity] = intCount;
                }
                rawZone = ResolvePropZone(record, rawZone, path, intCount, Engine_LevelLights.IsLampHost(record));
            }
            uint layers = Zoned(node.Layers, rawZone);
            if (node.Layers != layers)
            {
                node.Layers = layers;
                if (node is GeometryInstance3D geom)
                {
                    bool isIndoorInstance = (layers & (IndoorMask | IndoorLampHostMask)) != 0u;
                    float instanceAmbient = isIndoorInstance ? Engine_LevelBakedLighting.BuildingInteriorAmbientScale : 1.0f;
                    Refii.Core.Engine_InstanceParams.Set(geom, Engine_LevelBakedLighting.Param_ambient_scale, instanceAmbient);
                }
                if (node is MeshInstance3D meshNode)
                {
                    Refii.Entities.Engine_ShadowProxy.Sync(meshNode);
                }
            }
            Pending.RemoveAt(_cursor);
        }
    }
}
