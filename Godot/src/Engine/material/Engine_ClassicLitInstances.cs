using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// The classic renderer's per-draw lighting: every lit instance is assigned the lights the
/// title would assign it, grouped as the title groups them, with a constant row and a shader
/// permutation per group and one pass per group.
/// </summary>
/// <remarks>
/// THE TITLE'S RULE (`sub_82AB6360`, `sub_82B35D50`, `sub_82B35E88`, `sub_82B1EE68`): a light
/// is in a draw's set when its sphere meets the draw's bounds and its lighting zone admits the
/// draw's (<see cref="Engine_ClassicZones"/>); the set is split into groups of up to seven in
/// priority order — directional lights, shadow-casting spots, points, other spots — with at
/// most three groups, each a pass: the
/// first on the material's blend, the rest additive. Lights of a group fill the block from the
/// highest manager index down. The directional lights alone are the frame row every draw
/// shares; a draw with local lights takes rows of its own from <see cref="Engine_ClassicConstants"/>.
///
/// Instances enter through <see cref="Attach"/>, the per-instance lighting seam every prop and
/// entity already passes, and leave when their node goes.
/// </remarks>
public static class Engine_ClassicLitInstances
{
    private sealed class Entry
    {
        public GeometryInstance3D Node = null!;
        public readonly int[] Rows = new int[3];
        public int Groups;
        public readonly List<int> Lights = new();
        /// <summary>Per surface, the material the variants derive from: the ambient-keyed
        /// override when the placement has one, else the mesh's own; null = not classic.</summary>
        public ShaderMaterial?[] Bases = Array.Empty<ShaderMaterial?>();
        public bool[] BaseIsOverride = Array.Empty<bool>();
        /// <summary>Which frame of the round robin re-evaluates this entry.</summary>
        public int Slice;
        /// <summary>The row uniforms as last set on the node, so unchanged rows cost no call.</summary>
        public readonly int[] AppliedRows = new int[3];
        /// <summary>The group shape as last applied, hashed; the materials change only when it does.</summary>
        public int SignatureHash;
        /// <summary>The current group shape has no variant for some surface: the node draws on the frame row.</summary>
        public bool Unpaired;
        /// <summary>The live entity or .save record the node names (Engine_ClassicZones), read once.</summary>
        public uint Entity, Record;
        public bool Identified;
        /// <summary>World bounds, kept for a node no live entity moves (region geometry never does).</summary>
        public Aabb Bounds;
        public bool BoundsKnown;
    }

    /// <summary>
    /// Entries are re-evaluated one slice a frame: a static prop's lights change only when a
    /// light moves or flickers out of reach, and eight frames of latency on that is invisible,
    /// while evaluating five thousand bounds every frame was most of the main thread.
    /// </summary>
    private const int Slices = 8;
    private static int _slice;
    private static int _attached;
    private static readonly int[] _counts = new int[MaxGroups];
    private static readonly bool[] _singleLocal = new bool[MaxGroups];
    private static readonly List<Engine_ClassicLights.Light>[] _members =
    {
        new(7), new(7), new(7),
    };

    private const int MaxGroups = 3;
    private static readonly Dictionary<ulong, Entry> _entries = new();
    private static readonly List<ulong> _gone = new();
    private static readonly List<Engine_ClassicLights.Light>[] _buckets =
    {
        new(), new(), new(), new(),
    };
    private static readonly float[] _block = new float[Engine_ClassicRegisters.LightBlockRegisters * 4];
    private static readonly float[] _four = new float[4];
    private static long _frames;
    private static int _lit;

    /// <summary>Registers an instance the classic renderer lights; idempotent.</summary>
    public static void Attach(GeometryInstance3D node)
    {
        if (!Engine_ClassicMaterial.Active || node is null)
        {
            return;
        }
        ulong id = node.GetInstanceId();
        lock (_entries)
        {
            if (!_entries.ContainsKey(id))
            {
                _entries[id] = new Entry { Node = node, Slice = _attached++ % Slices };
            }
        }
    }

    /// <summary>Assigns this frame's lights to every instance. Main thread, after the light feed.</summary>
    public static void Pump()
    {
        ++_frames;
        IReadOnlyList<Engine_ClassicLights.Light> lights = Engine_ClassicLights.Lights;

        // The local lights, in Godot's axes for the bounds test; none means every draw is the
        // frame row and nothing below has to run.
        bool anyLocal = false;
        foreach (Engine_ClassicLights.Light light in lights)
        {
            if (light.Type != Engine_ClassicLights.Light.Directional)
            {
                anyLocal = true;
                break;
            }
        }

        bool anyMist = Engine_ClassicMist.Volumes.Count != 0;
        _byIndex.Clear();
        _arrived.Clear();
        _present.Clear();
        foreach (Engine_ClassicLights.Light light in lights)
        {
            _byIndex[light.Index] = light;
            // A light that joined the active set this frame (`sub_82240CE8` inserts it once the
            // view meets it): the draws it touches take it NOW, as the title's per-draw query
            // would, not when their slice comes round.
            var identity = (light.Index, light.Position);
            _present.Add(identity);
            if (light.Type != Engine_ClassicLights.Light.Directional && !_seen.Contains(identity))
            {
                _arrived.Add(light);
            }
        }
        (_seen, _present) = (_present, _seen);
        lock (_entries)
        {
            _gone.Clear();
            long assignFrom = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (KeyValuePair<ulong, Entry> pair in _entries)
            {
                Entry entry = pair.Value;
                if (entry.Slice != _slice && !Touches(entry, _arrived))
                {
                    continue;
                }
                if (!GodotObject.IsInstanceValid(entry.Node) || !entry.Node.IsInsideTree())
                {
                    Release(entry);
                    _gone.Add(pair.Key);
                    continue;
                }
                if (!anyLocal && !anyMist)
                {
                    if (entry.Groups != 0)
                    {
                        Release(entry);
                        ApplyGroups(entry, 0, null);
                    }
                    continue;
                }
                Assign(entry, lights);
            }
            Refii.Core.Engine_FramePhases.Lap("classic.lit.assign", ref assignFrom);
            foreach (ulong id in _gone)
            {
                _entries.Remove(id);
            }
            using (Refii.Core.Engine_FramePhases.Measure("classic.lit.rows"))
            {
                foreach (SharedRow shared in _sharedByRow.Values)
                {
                    Write(shared);
                }
            }
        }
        _slice = (_slice + 1) % Slices;
        Refii.Level.Engine_ClassicVistaMist.Pump();
        _lit = 0;
        foreach (Entry entry in _entries.Values)
        {
            if (entry.Groups != 0)
            {
                ++_lit;
            }
        }
    }

    /// <summary>
    /// ONE ROW PER DISTINCT LIGHT SET, NOT PER INSTANCE. A row is "this frame, these lights, this
    /// pass, this mist", so every instance whose group holds the same lights shares it — the
    /// title builds the block per draw and has no budget to run out of. Each row is rewritten
    /// every frame from the lights' current values (flicker, motion), so every instance it serves
    /// shows the same light at the same moment.
    /// </summary>
    private sealed class SharedRow
    {
        public ulong Key;
        public int Row;
        public int References;
        public int Group;
        public readonly List<int> Lights = new(7);
        public Engine_ClassicMist.Volume? Mist;
    }

    private static readonly Dictionary<ulong, SharedRow> _sharedByKey = new();
    private static readonly Dictionary<int, SharedRow> _sharedByRow = new();
    private static readonly Dictionary<int, Engine_ClassicLights.Light> _byIndex = new();

    private static int AcquireShared(int group, List<Engine_ClassicLights.Light> members, Engine_ClassicMist.Volume? mist)
    {
        ulong key = 14695981039346656037UL;
        void Mix(ulong v) => key = (key ^ v) * 1099511628211UL;
        Mix((ulong)group);
        Mix(mist?.Viewport?.GetInstanceId() ?? 0UL);
        foreach (Engine_ClassicLights.Light light in members)
        {
            Mix((ulong)(uint)light.Index);
            Mix((ulong)BitConverter.SingleToUInt32Bits(light.Position.X) << 32 | BitConverter.SingleToUInt32Bits(light.Position.Y));
        }
        if (_sharedByKey.TryGetValue(key, out SharedRow? shared))
        {
            ++shared.References;
            return shared.Row;
        }
        int row = Engine_ClassicConstants.AcquireRow();
        if (row == 0)
        {
            return 0;
        }
        shared = new SharedRow { Key = key, Row = row, References = 1, Group = group, Mist = mist };
        foreach (Engine_ClassicLights.Light light in members)
        {
            shared.Lights.Add(light.Index);
            _byIndex[light.Index] = light;
        }
        _sharedByKey[key] = shared;
        _sharedByRow[row] = shared;
        Write(shared);
        return row;
    }

    private static void ReleaseShared(int row)
    {
        if (row == 0 || !_sharedByRow.TryGetValue(row, out SharedRow? shared) || --shared.References > 0)
        {
            return;
        }
        _sharedByRow.Remove(row);
        _sharedByKey.Remove(shared.Key);
        Engine_ClassicConstants.ReleaseRow(row);
    }

    /// <summary>Packs a shared row from its lights' current values.</summary>
    private static void Write(SharedRow shared)
    {
        Array.Clear(_block);
        int slot = 0;
        foreach (int index in shared.Lights)
        {
            if (!_byIndex.TryGetValue(index, out Engine_ClassicLights.Light light))
            {
                continue;
            }
            Engine_ClassicLights.PackInto(_block, light, slot++);
            if (light.Primary && (light.Type == Engine_ClassicLights.Light.Directional
                || light.Type == Engine_ClassicLights.Light.Spot))
            {
                Engine_ClassicLights.PackCaster(_block, light);
            }
        }
        Engine_ClassicConstants.SetRowBlock(true, shared.Row, Engine_ClassicRegisters.Ps.LightPositions, _block);
        Engine_ClassicConstants.SetRowBlock(false, shared.Row, Engine_ClassicRegisters.Vs.LightPositions, Engine_ClassicLights.VertexBlock(_block));
        // g_AtmosphericFactors: every pass is attenuated, the first alone in-scatters.
        _four[0] = 1f; _four[1] = shared.Group == 0 ? 1f : 0f; _four[2] = 0f; _four[3] = 0f;
        Engine_ClassicConstants.SetRowBlock(true, shared.Row, Engine_ClassicRegisters.Ps.AtmosphericFactors, _four);
        if (shared.Mist is not null)
        {
            Engine_ClassicMist.WriteRegisters(shared.Mist, shared.Row);
        }
    }

    private static HashSet<(int, Vector3)> _seen = new();
    private static HashSet<(int, Vector3)> _present = new();
    private static readonly List<Engine_ClassicLights.Light> _arrived = new();

    /// <summary>Whether a light that arrived this frame reaches the entry's last known bounds.</summary>
    private static bool Touches(Entry entry, List<Engine_ClassicLights.Light> arrived)
    {
        if (arrived.Count == 0 || !entry.BoundsKnown)
        {
            return false;
        }
        Vector3 min = entry.Bounds.Position, max = entry.Bounds.End;
        foreach (Engine_ClassicLights.Light light in arrived)
        {
            var centre = new Vector3(-light.Position.X, light.Position.Z, light.Position.Y);
            if ((centre.Clamp(min, max) - centre).LengthSquared() <= light.Radius * light.Radius)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Distinct light-set rows live, for the debug channel.</summary>
    public static int SharedRows => _sharedByRow.Count;

    /// <summary>Instances registered, and those with local lights on the last frame.</summary>
    public static (int Attached, int Lit) Counts => (_entries.Count, _lit);

    private static void Assign(Entry entry, IReadOnlyList<Engine_ClassicLights.Light> lights)
    {
        if (!entry.Identified)
        {
            (entry.Entity, entry.Record) = Engine_ClassicZones.Identify(entry.Node);
            entry.Identified = true;
        }
        if (!entry.BoundsKnown || entry.Entity != 0u)
        {
            entry.Bounds = entry.Node.GlobalTransform * entry.Node.GetAabb();
            entry.BoundsKnown = true;
        }
        Aabb bounds = entry.Bounds;
        Vector3 min = bounds.Position, max = bounds.End;
        uint zone = Engine_ClassicZones.Of(entry.Entity, entry.Record);

        // The set: directional lights always, local lights whose sphere meets the bounds.
        foreach (List<Engine_ClassicLights.Light> bucket in _buckets)
        {
            bucket.Clear();
        }
        entry.Lights.Clear();
        for (int i = lights.Count - 1; i >= 0; i--)
        {
            Engine_ClassicLights.Light light = lights[i];
            if (!Engine_ClassicZones.Lights(light.Zone, zone))
            {
                continue;
            }
            if (light.Type == Engine_ClassicLights.Light.Directional)
            {
                _buckets[0].Add(light);
                continue;
            }
            Vector3 centre = new(-light.Position.X, light.Position.Z, light.Position.Y);
            Vector3 nearest = centre.Clamp(min, max);
            if ((nearest - centre).LengthSquared() > light.Radius * light.Radius)
            {
                continue;
            }
            if (light.Type == Engine_ClassicLights.Light.Spot)
            {
                _buckets[light.Casts && !light.NoShadow ? 1 : 3].Add(light);
            }
            else
            {
                _buckets[2].Add(light);
            }
        }

        // Groups of seven in priority order, three at most.
        int groups = 0;
        int[] counts = _counts;
        bool[] singleLocal = _singleLocal;
        List<Engine_ClassicLights.Light>[] members = _members;
        for (int g = 0; g < MaxGroups; g++)
        {
            members[g].Clear();
            counts[g] = 0;
            singleLocal[g] = false;
        }
        foreach (List<Engine_ClassicLights.Light> bucket in _buckets)
        {
            foreach (Engine_ClassicLights.Light light in bucket)
            {
                if (groups == 0 || members[groups - 1].Count == 7)
                {
                    if (groups == MaxGroups)
                    {
                        break;
                    }
                    ++groups;
                }
                members[groups - 1].Add(light);
            }
        }
        if (groups == 0)
        {
            if (entry.Groups != 0)
            {
                Release(entry);
                ApplyGroups(entry, 0, null);
            }
            return;
        }

        // Only directional lights and no mist: the frame row, the material as built.
        Engine_ClassicMist.Volume? mist = Engine_ClassicMist.VolumeFor(bounds);
        bool onlyDirectional = groups == 1 && _buckets[1].Count + _buckets[2].Count + _buckets[3].Count == 0;
        if (onlyDirectional && mist is null)
        {
            if (entry.Groups != 0)
            {
                Release(entry);
                ApplyGroups(entry, 0, null);
            }
            return;
        }

        for (int g = 0; g < groups; g++)
        {
            counts[g] = members[g].Count;
            int local = 0;
            foreach (Engine_ClassicLights.Light light in members[g])
            {
                if (light.Type != Engine_ClassicLights.Light.Directional)
                {
                    ++local;
                }
            }
            singleLocal[g] = members[g].Count == 1 && local == 1;

            // The row for this group's lights in this pass and mist, shared with every other
            // instance that has the same; taken before the old one is let go, so an unchanged
            // set keeps its row.
            int row = AcquireShared(g, members[g], mist);
            if (row == 0)
            {
                groups = g;
                break;
            }
            ReleaseShared(entry.Rows[g]);
            entry.Rows[g] = row;
        }
        for (int g = groups; g < MaxGroups; g++)
        {
            ReleaseShared(entry.Rows[g]);
            entry.Rows[g] = 0;
        }
        entry.Groups = groups;
        ApplyGroups(entry, groups, mist);
    }

    /// <summary>
    /// Sets the instance's row uniforms and, when the group shape changed, the surface materials:
    /// the group-0 variant with the later groups chained as its next passes.
    /// </summary>
    /// <summary>Applies the first <paramref name="groups"/> of the scratch counts to the entry.</summary>
    private static void ApplyGroups(Entry entry, int groups, Engine_ClassicMist.Volume? mist)
    {
        GeometryInstance3D node = entry.Node;
        int hash = groups;
        for (int g = 0; g < groups; g++)
        {
            hash = hash * 31 + _counts[g] * 2 + (_singleLocal[g] ? 1 : 0);
        }
        hash = hash * 31 + (mist?.Row ?? -1);
        if (hash != entry.SignatureHash)
        {
            entry.SignatureHash = hash;
            int[] counts = groups == 0 ? Array.Empty<int>() : _counts.AsSpan(0, groups).ToArray();
            bool[] singleLocal = groups == 0 ? Array.Empty<bool>() : _singleLocal.AsSpan(0, groups).ToArray();
            // ONE ROW PER NODE, SO ALL OR NOTHING. The row uniforms are the node's, read by every
            // surface; a surface left on its base (a family with no pair for the group, as trees
            // past one light) would read the group's light block as its own single light. Such a
            // shape keeps the whole node on the frame's row, and is remembered so it is not
            // retried at every assignment.
            entry.Unpaired = groups != 0 && !ApplyChains(entry, counts, singleLocal, mist);
            if (entry.Unpaired)
            {
                ApplyChains(entry, Array.Empty<int>(), Array.Empty<bool>(), null);
            }
        }
        if (entry.Unpaired && groups != 0)
        {
            Release(entry);
            groups = 0;
        }

        for (int g = 0; g < MaxGroups; g++)
        {
            int row = g < groups ? entry.Rows[g] : 0;
            if (entry.AppliedRows[g] != row)
            {
                entry.AppliedRows[g] = row;
                node.SetInstanceShaderParameter(RowUniform(g), row);
            }
        }
    }

    /// <summary>
    /// Sets each surface's material for the group shape: the group-0 variant with the later
    /// groups chained, or the base for none. False when a classic surface has no variant.
    /// </summary>
    private static bool ApplyChains(Entry entry, int[] counts, bool[] singleLocal, Engine_ClassicMist.Volume? mist)
    {
        GeometryInstance3D node = entry.Node;
        bool complete = true;
        if (node is MeshInstance3D mesh && mesh.Mesh is not null)
        {
            int surfaces = mesh.Mesh.GetSurfaceCount();
            Bases(entry, surfaces, i =>
                (mesh.GetSurfaceOverrideMaterial(i) as ShaderMaterial, mesh.Mesh.SurfaceGetMaterial(i) as ShaderMaterial));
            var chains = new ShaderMaterial?[surfaces];
            for (int i = 0; i < surfaces; i++)
            {
                chains[i] = Chain(entry.Bases[i], counts, singleLocal, mist);
                complete &= entry.Bases[i] is null || counts.Length == 0 || chains[i] is not null;
            }
            if (!complete)
            {
                return false;
            }
            for (int i = 0; i < surfaces; i++)
            {
                if (entry.Bases[i] is null)
                {
                    continue;
                }
                // Back to the single-group shape: the base itself (no override when the base is
                // the mesh's own material).
                mesh.SetSurfaceOverrideMaterial(i,
                    chains[i] ?? (entry.BaseIsOverride[i] ? entry.Bases[i] : null));
            }
        }
        else if (node is MultiMeshInstance3D batch && batch.Multimesh?.Mesh is { } shared)
        {
            // A batch has no per-surface override: its MaterialOverride replaces EVERY surface.
            // Only a one-surface batch can take the group variant that way; a house or a tree
            // drew all its surfaces with surface 0's variant (a whole townhouse in its window
            // texture, a trunk in its leaves). Those stay on their own materials and the frame row.
            if (shared.GetSurfaceCount() > 1)
            {
                return counts.Length == 0;
            }
            Bases(entry, 1, _ => (batch.MaterialOverride as ShaderMaterial, shared.SurfaceGetMaterial(0) as ShaderMaterial));
            if (entry.Bases[0] is not null)
            {
                ShaderMaterial? chain = Chain(entry.Bases[0], counts, singleLocal, mist);
                if (chain is null && counts.Length != 0)
                {
                    return false;
                }
                batch.MaterialOverride = chain ?? (entry.BaseIsOverride[0] ? entry.Bases[0] : null);
            }
        }
        return complete;
    }

    /// <summary>
    /// Remembers each surface's base material once: the current override unless it is one of
    /// our variants, else the mesh's material; a surface whose material is not classic gets null.
    /// </summary>
    private static void Bases(Entry entry, int surfaces, Func<int, (ShaderMaterial? Override, ShaderMaterial? Own)> read)
    {
        if (entry.Bases.Length != surfaces)
        {
            entry.Bases = new ShaderMaterial?[surfaces];
            entry.BaseIsOverride = new bool[surfaces];
        }
        for (int i = 0; i < surfaces; i++)
        {
            (ShaderMaterial? over, ShaderMaterial? own) = read(i);
            if (over is not null && !over.HasMeta(Engine_ClassicMaterial.GroupVariantMeta))
            {
                // An ambient-keyed override (or any override that is not ours) is the base.
                entry.Bases[i] = over.HasMeta(Engine_ClassicMaterial.ClassicMeta) ? over : null;
                entry.BaseIsOverride[i] = true;
            }
            else if (entry.Bases[i] is null || !GodotObject.IsInstanceValid(entry.Bases[i]))
            {
                entry.Bases[i] = own is not null && own.HasMeta(Engine_ClassicMaterial.ClassicMeta) ? own : null;
                entry.BaseIsOverride[i] = false;
            }
        }
    }

    /// <summary>The group-0 variant with the later groups as its next passes, or null for one group of the base's own shape.</summary>
    private static ShaderMaterial? Chain(ShaderMaterial? source, int[] counts, bool[] singleLocal, Engine_ClassicMist.Volume? mist)
    {
        if (source is null || counts.Length == 0)
        {
            return null;
        }
        if (source.HasMeta(Engine_ClassicMaterial.LayerChainMeta))
        {
            return LayerChain(source, counts, singleLocal, mist);
        }
        ShaderMaterial? first = Engine_ClassicMaterial.ForLightGroup(source, counts[0], singleLocal[0], 0, mist, Tail(counts, singleLocal, 1, 0));
        ShaderMaterial? previous = first;
        for (int g = 1; g < counts.Length && previous is not null; g++)
        {
            ShaderMaterial? next = Engine_ClassicMaterial.ForLightGroup(source, counts[g], singleLocal[g], g, mist, Tail(counts, singleLocal, g + 1, 0));
            previous.NextPass = next;
            previous = next;
        }
        // A glass surface's blended draw follows its light groups, lit by the first group.
        if (previous is not null && source.HasMeta(Engine_ClassicMaterial.TranslucentPassMeta) &&
            source.GetMeta(Engine_ClassicMaterial.TranslucentPassMeta).Obj is ShaderMaterial glass)
        {
            previous.NextPass = Engine_ClassicMaterial.ForLightGroup(glass, counts[0], singleLocal[0], 0, mist);
        }
        return first;
    }

    /// <summary>
    /// A landscape patch: its splat layers are a NextPass chain, and the title lights each layer
    /// on its own (`sub_822159D8` resets the pass counter per layer) — the layer's first pass on
    /// group 0, its later groups additive straight after it, before the next layer blends.
    /// </summary>
    private static ShaderMaterial? LayerChain(ShaderMaterial source, int[] counts, bool[] singleLocal, Engine_ClassicMist.Volume? mist)
    {
        ShaderMaterial? first = null, previous = null;
        int layerIndex = 0;
        for (ShaderMaterial? layer = source; layer is not null; layer = layer.NextPass as ShaderMaterial, ++layerIndex)
        {
            for (int g = 0; g < counts.Length; g++)
            {
                ShaderMaterial? pass = Engine_ClassicMaterial.ForLightGroup(
                    layer, counts[g], singleLocal[g], g, mist, Tail(counts, singleLocal, g + 1, layerIndex + 1));
                if (pass is null)
                {
                    if (g == 0)
                    {
                        return null;
                    }
                    break;
                }
                if (previous is null)
                {
                    first = pass;
                }
                else
                {
                    previous.NextPass = pass;
                }
                previous = pass;
            }
        }
        return first;
    }

    /// <summary>The shape chained after a pass: the groups from <paramref name="from"/> on, and the layer that follows.</summary>
    private static int Tail(int[] counts, bool[] singleLocal, int from, int nextLayer)
    {
        int hash = 17 + nextLayer;
        for (int g = from; g < counts.Length; g++)
        {
            hash = hash * 31 + counts[g] * 2 + (singleLocal[g] ? 1 : 0);
        }
        return hash * 31 + counts.Length - from;
    }

    private static StringName RowUniform(int group) => group switch
    {
        0 => "xr_light_row",
        1 => "xr_light_row2",
        _ => "xr_light_row3",
    };

    private static void Release(Entry entry)
    {
        for (int g = 0; g < MaxGroups; g++)
        {
            ReleaseShared(entry.Rows[g]);
            entry.Rows[g] = 0;
        }
        entry.Groups = 0;
    }
}
