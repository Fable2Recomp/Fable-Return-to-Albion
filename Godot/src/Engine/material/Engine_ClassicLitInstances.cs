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
        /// <summary>Per group as last applied: its light count and whether it is one local light.</summary>
        public readonly int[] Counts = new int[3];
        public readonly bool[] Single = new bool[3];
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
        /// <summary>The node is in the tree (its TreeEntered / TreeExiting).</summary>
        public bool InTree;
        /// <summary>World bounds, kept for a node no live entity moves (region geometry never does).</summary>
        public Aabb Bounds;
        /// <summary>The frame layout (<see cref="_layout"/>) and zone the last complete assignment saw.</summary>
        public long Layout = long.MinValue;
        public uint Zone;
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

    private const int MaxGroups = 3;
    private static readonly Dictionary<ulong, Entry> _entries = new();
    private static readonly List<ulong> _gone = new();
    private static readonly float[] _block = new float[Engine_ClassicRegisters.LightBlockRegisters * 4];
    private static readonly float[] _four = new float[4];
    private static long _frames;
    private static int _lit;
    /// <summary>The assignment walk's split this frame: the zone lookups and the variant builds, in ticks.</summary>
    private static long _zoneTicks, _variantTicks;

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
                var entry = new Entry { Node = node, Slice = _attached++ % Slices, InTree = node.IsInsideTree() };
                _entries[id] = entry;
                // Kept by the node's own signals, not asked of the engine every assignment.
                node.TreeEntered += () => entry.InTree = true;
                node.TreeExiting += () => entry.InTree = false;
            }
        }
        Engine_ClassicFur.Attach(node);
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
        _layout = Layout(lights);
        (LastAssigned, LastSkipped) = (AssignedThisFrame, SkippedThisFrame);
        AssignedThisFrame = SkippedThisFrame = 0;
        lock (_entries)
        {
            _gone.Clear();
            _work.Clear();
            _zoneTicks = 0;
            _variantTicks = 0;
            long assignFrom = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (KeyValuePair<ulong, Entry> pair in _entries)
            {
                Entry entry = pair.Value;
                if (entry.Slice != _slice && !Touches(entry, _arrived))
                {
                    continue;
                }
                if (!GodotObject.IsInstanceValid(entry.Node) || !entry.InTree)
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
                Prepare(entry);
                _work.Add(entry);
            }
            Assign(lights);
            Refii.Core.Engine_FramePhases.Lap("classic.lit.assign", ref assignFrom);
            double tickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Refii.Core.Engine_FramePhases.Record("classic.lit.zone", _zoneTicks * tickMs, false);
            Refii.Core.Engine_FramePhases.Record("classic.lit.variant", _variantTicks * tickMs, false);
            // Inside the variant time: building new light-group materials (their shader composition
            // apart), and finding ones already built.
            Refii.Core.Engine_FramePhases.Record("classic.lit.compose", Engine_ClassicMaterial.VariantComposeTicks * tickMs, false);
            Refii.Core.Engine_FramePhases.Record("classic.lit.build",
                (Engine_ClassicMaterial.VariantBuildTicks - Engine_ClassicMaterial.VariantComposeTicks) * tickMs, false);
            Refii.Core.Engine_FramePhases.Record("classic.lit.lookup", Engine_ClassicMaterial.VariantLookupTicks * tickMs, false);
            Engine_ClassicMaterial.VariantComposeTicks = 0;
            Engine_ClassicMaterial.VariantBuildTicks = 0;
            Engine_ClassicMaterial.VariantLookupTicks = 0;
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
    private static readonly List<Engine_ClassicLights.Light> _members = new(7);

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
        _members.Clear();
        foreach (int index in shared.Lights)
        {
            if (!_byIndex.TryGetValue(index, out Engine_ClassicLights.Light light))
            {
                continue;
            }
            _members.Add(light);
            Engine_ClassicLights.PackInto(_block, light, slot++);
            if (light.Primary && (light.Type == Engine_ClassicLights.Light.Directional
                || light.Type == Engine_ClassicLights.Light.Spot))
            {
                Engine_ClassicLights.PackCaster(_block, light);
            }
        }
        // The group's one spot map (s9, c32..c35, c48): it may clear the slots' own-map flags,
        // so it goes before the block.
        Engine_ClassicSpotShadows.WriteRow(shared.Row, Engine_ClassicSpotShadows.ShadowingSpot(_members), _block);
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

    /// <summary>
    /// Everything an assignment reads besides the entry: the lights' identity, place, reach, zone
    /// and flags, the spot frustums and the mist volumes. A static entry (bounds cached) in the
    /// same zone under the same layout gets the same groups, so it is not assigned again.
    /// </summary>
    private static long _layout;

    /// <summary>The last frame's assignment walk: entries assigned, and static ones skipped under an unchanged layout.</summary>
    public static int AssignedThisFrame, SkippedThisFrame;
    public static int LastAssigned, LastSkipped;

    private static long Layout(IReadOnlyList<Engine_ClassicLights.Light> lights)
    {
        long h = Engine_ClassicLights.FrustumHash();
        void Mix(long v) => h = (h ^ v) * 1099511628211L;
        foreach (Engine_ClassicLights.Light light in lights)
        {
            Mix(light.Index);
            Mix(light.Type);
            Mix(light.Flags);
            Mix(light.Zone);
            Mix(BitConverter.SingleToInt32Bits(light.Radius));
            Mix(BitConverter.SingleToInt32Bits(light.Position.X));
            Mix(BitConverter.SingleToInt32Bits(light.Position.Y));
            Mix(BitConverter.SingleToInt32Bits(light.Position.Z));
        }
        foreach (Engine_ClassicMist.Volume volume in Engine_ClassicMist.Volumes)
        {
            Mix(volume.Row);
            Mix((long)(volume.Viewport?.GetInstanceId() ?? 0UL));
            Mix(volume.Min.GetHashCode());
            Mix(volume.Max.GetHashCode());
        }
        return h == long.MinValue ? 0 : h;
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

    /// <summary>
    /// The light groups an instance drew with on its last assignment: per group its constant row,
    /// light count and whether it is a single local light. 0 means the frame's row alone (its
    /// directional lights, BLT1). Main thread.
    /// </summary>
    public static int GroupsOf(GeometryInstance3D node, Span<int> rows, Span<int> counts, Span<bool> single)
    {
        lock (_entries)
        {
            if (!_entries.TryGetValue(node.GetInstanceId(), out Entry? entry))
            {
                return 0;
            }
            int n = Math.Min(entry.Groups, Math.Min(rows.Length, Math.Min(counts.Length, single.Length)));
            for (int g = 0; g < n; g++)
            {
                rows[g] = entry.Rows[g];
                counts[g] = entry.Counts[g];
                single[g] = entry.Single[g];
            }
            return n;
        }
    }

    /// <summary>Distinct light-set rows live, for the debug channel.</summary>
    public static int SharedRows => _sharedByRow.Count;

    /// <summary>Instances registered, and those with local lights on the last frame.</summary>
    public static (int Attached, int Lit) Counts => (_entries.Count, _lit);

    /// <summary>The entries this frame assigns, in walk order.</summary>
    private static readonly List<Entry> _work = new();

    /// <summary>What an entry's assignment found, computed off the main thread (<see cref="Compute"/>).</summary>
    private sealed class Found
    {
        public uint Zone;
        public bool Skip;
        public int Groups;
        public bool OnlyDirectional;
        public Engine_ClassicMist.Volume? Mist;
        public readonly List<Engine_ClassicLights.Light>[] Buckets = { new(), new(), new(), new() };
        public readonly List<Engine_ClassicLights.Light>[] Members = { new(7), new(7), new(7) };
    }

    private static readonly List<Found> _found = new();

    /// <summary>The engine reads an assignment needs, on the main thread: who the node is and, for a moving one, its box.</summary>
    private static void Prepare(Entry entry)
    {
        if (!entry.Identified)
        {
            (entry.Entity, entry.Record) = Engine_ClassicZones.Identify(entry.Node);
            entry.Identified = true;
        }
        if (!entry.BoundsKnown || entry.Entity != 0u)
        {
            entry.Bounds = entry.Node.GlobalTransform * Refii.Core.Engine_ServerSyncs.LocalAabb(entry.Node);
            entry.BoundsKnown = true;
        }
    }

    /// <summary>
    /// The frame's assignments: the sets and groups found in parallel (no engine object is
    /// touched there), then applied in walk order on the main thread, so rows are shared and
    /// handed out exactly as one sequential walk would.
    /// </summary>
    private static void Assign(IReadOnlyList<Engine_ClassicLights.Light> lights)
    {
        while (_found.Count < _work.Count)
        {
            _found.Add(new Found());
        }
        long zoneFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_work.Count >= ParallelFrom)
        {
            System.Threading.Tasks.Parallel.For(0, _work.Count, i => Compute(_work[i], _found[i], lights));
        }
        else
        {
            for (int i = 0; i < _work.Count; i++)
            {
                Compute(_work[i], _found[i], lights);
            }
        }
        _zoneTicks += System.Diagnostics.Stopwatch.GetTimestamp() - zoneFrom;
        for (int i = 0; i < _work.Count; i++)
        {
            Entry entry = _work[i];
            Found found = _found[i];
            if (found.Skip)
            {
                ++SkippedThisFrame;
                continue;
            }
            ++AssignedThisFrame;
            entry.Layout = long.MinValue;
            entry.Zone = found.Zone;
            if (Apply(entry, found) && entry.SignatureHash != int.MinValue)
            {
                entry.Layout = _layout;
            }
        }
    }

    /// <summary>Below this many entries a frame the walk stays on the main thread: the hand-off costs more.</summary>
    private const int ParallelFrom = 64;

    /// <summary>
    /// One entry's light set and groups. Any thread: reads the entry's cached box and identity,
    /// the frame's light list, the spot frustums and the mist volumes, all fixed while it runs.
    /// </summary>
    private static void Compute(Entry entry, Found found, IReadOnlyList<Engine_ClassicLights.Light> lights)
    {
        Aabb bounds = entry.Bounds;
        Vector3 min = bounds.Position, max = bounds.End;
        uint zone = Engine_ClassicZones.Of(entry.Entity, entry.Record);
        found.Zone = zone;
        found.Skip = entry.Entity == 0u && entry.Layout == _layout && entry.Zone == zone;
        if (found.Skip)
        {
            return;
        }

        // The set: directional lights always, local lights whose sphere meets the bounds.
        List<Engine_ClassicLights.Light>[] buckets = found.Buckets;
        foreach (List<Engine_ClassicLights.Light> bucket in buckets)
        {
            bucket.Clear();
        }
        for (int i = lights.Count - 1; i >= 0; i--)
        {
            Engine_ClassicLights.Light light = lights[i];
            if (!Engine_ClassicZones.Lights(light.Zone, zone))
            {
                continue;
            }
            if (light.Type == Engine_ClassicLights.Light.Directional)
            {
                buckets[0].Add(light);
                continue;
            }
            Vector3 centre = new(-light.Position.X, light.Position.Z, light.Position.Y);
            Vector3 nearest = centre.Clamp(min, max);
            if ((nearest - centre).LengthSquared() > light.Radius * light.Radius)
            {
                continue;
            }
            // A spot takes only what its frustum holds (its +96 receive filter, `sub_82A6CC98`).
            if (light.Type == Engine_ClassicLights.Light.Spot &&
                !Engine_ClassicLights.InFrustum(light.Index,
                    new Vector3(-max.X, min.Z, min.Y), new Vector3(-min.X, max.Z, max.Y)))
            {
                continue;
            }
            if (light.Type == Engine_ClassicLights.Light.Spot)
            {
                buckets[light.Casts && !light.NoShadow ? 1 : 3].Add(light);
            }
            else
            {
                buckets[2].Add(light);
            }
        }

        // Groups of seven in priority order, three at most.
        int groups = 0;
        List<Engine_ClassicLights.Light>[] members = found.Members;
        for (int g = 0; g < MaxGroups; g++)
        {
            members[g].Clear();
        }
        foreach (List<Engine_ClassicLights.Light> bucket in buckets)
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
        found.Groups = groups;
        found.OnlyDirectional = groups == 1 && buckets[1].Count + buckets[2].Count + buckets[3].Count == 0;
        found.Mist = groups == 0 ? null : Engine_ClassicMist.VolumeFor(bounds);
    }

    /// <summary>
    /// Applies what <see cref="Compute"/> found: the shared rows and the materials. Main thread.
    /// False when the entry's groups could not all be given rows (asked again next time).
    /// </summary>
    private static bool Apply(Entry entry, Found found)
    {
        int groups = found.Groups;
        if (groups == 0 || (found.OnlyDirectional && found.Mist is null))
        {
            // No light, or only directional lights and no mist: the frame row, the material as built.
            if (entry.Groups != 0)
            {
                Release(entry);
                ApplyGroups(entry, 0, null);
            }
            return true;
        }

        Engine_ClassicMist.Volume? mist = found.Mist;
        int[] counts = _counts;
        bool[] singleLocal = _singleLocal;
        for (int g = 0; g < MaxGroups; g++)
        {
            counts[g] = 0;
            singleLocal[g] = false;
        }
        bool complete = true;
        for (int g = 0; g < groups; g++)
        {
            List<Engine_ClassicLights.Light> members = found.Members[g];
            counts[g] = members.Count;
            int local = 0;
            foreach (Engine_ClassicLights.Light light in members)
            {
                if (light.Type != Engine_ClassicLights.Light.Directional)
                {
                    ++local;
                }
            }
            singleLocal[g] = members.Count == 1 && local == 1;

            // The row for this group's lights in this pass and mist, shared with every other
            // instance that has the same; taken before the old one is let go, so an unchanged
            // set keeps its row.
            int row = AcquireShared(g, members, mist);
            if (row == 0)
            {
                groups = g;
                complete = false;
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
        return complete;
    }

    /// <summary>
    /// Sets the instance's row uniforms and, when the group shape changed, the surface materials:
    /// the group-0 variant with the later groups chained as its next passes.
    /// </summary>
    /// <summary>Applies the first <paramref name="groups"/> of the scratch counts to the entry.</summary>
    private static void ApplyGroups(Entry entry, int groups, Engine_ClassicMist.Volume? mist)
    {
        long from = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            ApplyGroupsTimed(entry, groups, mist);
        }
        finally
        {
            _variantTicks += System.Diagnostics.Stopwatch.GetTimestamp() - from;
        }
    }

    private static void ApplyGroupsTimed(Entry entry, int groups, Engine_ClassicMist.Volume? mist)
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
            Engine_ClassicMaterial.Deferred = false;
            bool paired = groups == 0 || ApplyChains(entry, counts, singleLocal, mist);
            if (Engine_ClassicMaterial.Deferred)
            {
                // A variant waits for a later frame: the base until then, asked again at the
                // entry's next assignment (no signature matches).
                Engine_ClassicMaterial.Deferred = false;
                entry.SignatureHash = int.MinValue;
                entry.Unpaired = false;
                ApplyChains(entry, Array.Empty<int>(), Array.Empty<bool>(), null);
                Release(entry);
                groups = 0;
            }
            else
            {
                entry.Unpaired = !paired;
                if (entry.Unpaired)
                {
                    ApplyChains(entry, Array.Empty<int>(), Array.Empty<bool>(), null);
                }
            }
        }
        if (entry.Unpaired && groups != 0)
        {
            Release(entry);
            groups = 0;
        }
        for (int g = 0; g < MaxGroups; g++)
        {
            entry.Counts[g] = g < groups ? _counts[g] : 0;
            entry.Single[g] = g < groups && _singleLocal[g];
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
            // A batch has no per-surface override: its MaterialOverride replaces EVERY surface,
            // so only a one-surface batch takes its variant that way. A many-surface batch puts
            // its group on the SHARED mesh's surface materials instead — group 0 only, as the
            // title's instanced path draws (`sub_82223C68`: one light query on the batch's
            // union box, no additive passes). A single of the same mesh reads the variant's
            // source as its base (Bases); two batches of one mesh share the last-set variant,
            // each with its own row, and a wider variant reads an empty slot as no light.
            int surfaces = shared.GetSurfaceCount();
            if (surfaces > 1)
            {
                Bases(entry, surfaces, i => (null, shared.SurfaceGetMaterial(i) as ShaderMaterial));
                int[] firstCounts = counts.Length == 0 ? counts : new[] { counts[0] };
                bool[] firstSingle = singleLocal.Length == 0 ? singleLocal : new[] { singleLocal[0] };
                var batchChains = new ShaderMaterial?[surfaces];
                for (int i = 0; i < surfaces; i++)
                {
                    batchChains[i] = Chain(entry.Bases[i], firstCounts, firstSingle, mist);
                    complete &= entry.Bases[i] is null || firstCounts.Length == 0 || batchChains[i] is not null;
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
                    ShaderMaterial want = batchChains[i] ?? entry.Bases[i]!;
                    if (shared.SurfaceGetMaterial(i) != want)
                    {
                        shared.SurfaceSetMaterial(i, want);
                    }
                }
                return true;
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
    /// The load hold's warm: every light-group chain a lit instance could draw with, built before
    /// the guest runs so its first assignment finds them cached. A placed fixture whose reach (at
    /// its widest flicker) meets the instance's box may be in its set; with or without a
    /// directional light, any count up to all of them can occur, so every count's shape is built.
    /// Built through <see cref="Chain"/>, the same keys the assignment asks with.
    /// </summary>
    public static void PlanWarm(IReadOnlyList<(Vector3 Centre, float Reach)> fixtures)
    {
        _warm.Clear();
        if (fixtures.Count == 0)
        {
            return;
        }
        var seen = new HashSet<(int, int)>();
        lock (_entries)
        {
            foreach (Entry entry in _entries.Values)
            {
                if (!GodotObject.IsInstanceValid(entry.Node) || !entry.Node.IsInsideTree() || !ReadBases(entry))
                {
                    continue;
                }
                Aabb bounds = entry.Node.GlobalTransform * Refii.Core.Engine_ServerSyncs.LocalAabb(entry.Node);
                int reached = 0;
                foreach ((Vector3 centre, float reach) in fixtures)
                {
                    if ((centre.Clamp(bounds.Position, bounds.End) - centre).LengthSquared() <= reach * reach)
                    {
                        ++reached;
                    }
                }
                if (reached == 0)
                {
                    continue;
                }
                int bases = 17;
                foreach (ShaderMaterial? b in entry.Bases)
                {
                    bases = bases * 31 + (b?.GetInstanceId().GetHashCode() ?? 0);
                }
                for (int directional = 0; directional <= 1; directional++)
                {
                    for (int total = directional + 1; total <= Math.Min(reached + directional, MaxGroups * 7); total++)
                    {
                        (int[] counts, bool[] single) = Shape(total, directional);
                        if (seen.Add((bases, Tail(counts, single, 0, 0))))
                        {
                            _warm.Enqueue((entry, counts, single));
                        }
                    }
                }
            }
        }
    }

    /// <summary>Builds queued warm chains for up to <paramref name="budgetTicks"/>; a chain whose shader is still compiling goes round again.</summary>
    public static void PumpWarm(long budgetTicks)
    {
        long from = System.Diagnostics.Stopwatch.GetTimestamp();
        int waiting = 0;
        lock (_entries)
        {
            while (_warm.Count > waiting && System.Diagnostics.Stopwatch.GetTimestamp() - from < budgetTicks)
            {
                (Entry entry, int[] counts, bool[] single) = _warm.Dequeue();
                if (!GodotObject.IsInstanceValid(entry.Node))
                {
                    continue;
                }
                Engine_ClassicMaterial.Deferred = false;
                foreach (ShaderMaterial? b in entry.Bases)
                {
                    Chain(b, counts, single, null);
                }
                if (Engine_ClassicMaterial.Deferred)
                {
                    Engine_ClassicMaterial.Deferred = false;
                    _warm.Enqueue((entry, counts, single));
                    ++waiting;
                }
            }
        }
    }

    /// <summary>Warm chains not yet built.</summary>
    public static int WarmPending => _warm.Count;

    public static void ClearWarm()
    {
        _warm.Clear();
        _shaderWarm.Clear();
    }

    private static readonly Queue<ShaderMaterial> _shaderWarm = new();

    /// <summary>Base materials whose light-group shaders are not yet composed.</summary>
    public static int ShaderWarmPending => _shaderWarm.Count;

    /// <summary>
    /// Queues every distinct base material in the region — each surface's, each landscape
    /// layer's and each glass pass's — for <see cref="Engine_ClassicMaterial.WarmGroupShaders"/>.
    /// The fixture warm builds the shapes the placed lights can reach; lights the guest states
    /// after the hold (carried, cast, scripted) reach shapes no fixture does, and each would
    /// otherwise compose its shader in play (200-500 ms on the main thread).
    /// </summary>
    public static void PlanShaderWarm()
    {
        _shaderWarm.Clear();
        var seen = new HashSet<ulong>();
        void Add(ShaderMaterial? material)
        {
            // A landscape patch's layers are its NextPass chain (Chain reads the flag on the first).
            bool layered = material is not null && material.HasMeta(Engine_ClassicMaterial.LayerChainMeta);
            for (Material? m = material; m is ShaderMaterial layer; m = layered ? layer.NextPass : null)
            {
                if (seen.Add(layer.GetInstanceId()))
                {
                    _shaderWarm.Enqueue(layer);
                }
                if (layer.HasMeta(Engine_ClassicMaterial.TranslucentPassMeta) &&
                    layer.GetMeta(Engine_ClassicMaterial.TranslucentPassMeta).Obj is ShaderMaterial glass &&
                    seen.Add(glass.GetInstanceId()))
                {
                    _shaderWarm.Enqueue(glass);
                }
            }
        }
        lock (_entries)
        {
            foreach (Entry entry in _entries.Values)
            {
                if (!GodotObject.IsInstanceValid(entry.Node) || !entry.Node.IsInsideTree() || !ReadBases(entry))
                {
                    continue;
                }
                foreach (ShaderMaterial? b in entry.Bases)
                {
                    Add(b);
                }
            }
        }
    }

    /// <summary>Composes queued bases' light-group shaders for up to <paramref name="budgetTicks"/>. Main thread.</summary>
    public static void PumpShaderWarm(long budgetTicks)
    {
        long from = System.Diagnostics.Stopwatch.GetTimestamp();
        while (_shaderWarm.Count > 0 && System.Diagnostics.Stopwatch.GetTimestamp() - from < budgetTicks)
        {
            ShaderMaterial material = _shaderWarm.Dequeue();
            if (GodotObject.IsInstanceValid(material))
            {
                Engine_ClassicMaterial.WarmGroupShaders(material);
            }
        }
    }

    private static readonly Queue<(Entry Entry, int[] Counts, bool[] Single)> _warm = new();

    /// <summary>
    /// The groups for <paramref name="total"/> lights, the directional ones first (Assign's
    /// buckets): sevens, and a group of one is a single local light unless it holds the directional.
    /// </summary>
    private static (int[] Counts, bool[] Single) Shape(int total, int directional)
    {
        int groups = (total + 6) / 7;
        var counts = new int[groups];
        var single = new bool[groups];
        for (int g = 0; g < groups; g++)
        {
            counts[g] = Math.Min(7, total - g * 7);
            single[g] = counts[g] == 1 && (g > 0 || directional == 0);
        }
        return (counts, single);
    }

    /// <summary>The entry's bases as <see cref="ApplyChains"/> reads them; false when it would take no chain.</summary>
    private static bool ReadBases(Entry entry)
    {
        if (entry.Node is MeshInstance3D mesh && mesh.Mesh is not null)
        {
            Bases(entry, mesh.Mesh.GetSurfaceCount(), i =>
                (mesh.GetSurfaceOverrideMaterial(i) as ShaderMaterial, mesh.Mesh.SurfaceGetMaterial(i) as ShaderMaterial));
            return true;
        }
        if (entry.Node is MultiMeshInstance3D batch && batch.Multimesh?.Mesh is { } shared)
        {
            int surfaces = shared.GetSurfaceCount();
            if (surfaces == 1)
            {
                Bases(entry, 1, _ => (batch.MaterialOverride as ShaderMaterial, shared.SurfaceGetMaterial(0) as ShaderMaterial));
            }
            else
            {
                // The group lives on the shared mesh's surface materials (ApplyChains).
                Bases(entry, surfaces, i => (null, shared.SurfaceGetMaterial(i) as ShaderMaterial));
            }
            return true;
        }
        return false;
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
            if (own is not null && own.HasMeta(Engine_ClassicMaterial.GroupVariantMeta) &&
                own.GetMeta(Engine_ClassicMaterial.GroupSourceMeta).Obj is ShaderMaterial groupSource)
            {
                // A batch bound its group variant on the shared mesh: the base is the source.
                own = groupSource;
            }
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
