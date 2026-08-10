using System.Collections.Generic;
using Godot;

namespace Refii.Entities;

/// <summary>
/// Keeps the region's build-time copy of an entity and the guest's live one from both being
/// drawn, in either direction, for as long as the game runs.
///
/// THE GHOST. A region is built from the level's `.save` map and so is the guest: it
/// instantiates those same records as live entities and moves them. Both halves then draw, so
/// every door, cupboard, clock and music box in the street stands twice in the same spot, and
/// the copy you cannot interact with is ours.
///
/// ONE INVARIANT DECIDES EVERYTHING:
///
///     a static copy is visible  IFF  the guest HAS that record
///                                    AND we are not drawing a live node for it
///
/// Both halves of that matter, and they fail in opposite directions. The guest states 1437 live
/// entities but only 1030 name a model, so a record the guest HAS and we cannot draw must keep
/// its static copy — that is the only copy, and removing it empties the street. A record the
/// guest does NOT have is one it gated off (a layer whose `load` is `InitiallyOff`, a quest that
/// has not run) and drawing it is scenery the game never asked for.
///
/// NOTHING IS FREED, AND THAT IS THE POINT. `Layers.ActivateLayer` spawns a layer's entities
/// mid-game and `DeactivateLayer` destroys them again, so the correct answer for a given record
/// changes while the game runs. An earlier version called `QueueFree` and could only ever answer
/// once: a layer activated on entering a building found its props already destroyed, with no way
/// back. Hiding is reversible, so the invariant can simply be re-applied.
/// </summary>
public sealed class Engine_StaticTwins
{
    /// <summary>One drawable the guest may take over: a whole node, or one MultiMesh slot.</summary>
    private readonly struct Twin
    {
        public Twin(Node3D node, int instance, Transform3D standing)
        {
            Node = node;
            Instance = instance;
            Standing = standing;
        }

        public Node3D Node { get; }

        /// <summary>-1 for a node that is the instance; otherwise its MultiMesh slot.</summary>
        public int Instance { get; }

        /// <summary>
        /// Where a batched instance stands, captured at index time.
        ///
        /// A MultiMesh slot is hidden by collapsing its basis to zero, which destroys the
        /// transform — so restoring it needs the original, and the original has to be taken
        /// before anything is ever hidden.
        /// </summary>
        public Transform3D Standing { get; }
    }

    private readonly Dictionary<uint, List<Twin>> _byRecord = new();

    /// <summary>Records currently hidden. Membership IS the state; nothing is destroyed.</summary>
    private readonly HashSet<uint> _hidden = new();

    /// <summary>Distinct `.save` records the region drew something for.</summary>
    public int Records => _byRecord.Count;

    /// <summary>Drawables behind those records — more than one where a record drew several.</summary>
    public int Instances { get; private set; }

    /// <summary>Records hidden because the guest is drawing its own copy.</summary>
    public int HiddenForLive { get; private set; }

    /// <summary>Records hidden because the guest states no such entity — a gate we do not implement.</summary>
    public int HiddenUnstated { get; private set; }

    /// <summary>Records shown again after the guest gave one up. Non-zero means a layer activated.</summary>
    public int Restored { get; private set; }

    /// <summary>
    /// Records the guest named that the region drew nothing for.
    ///
    /// THE NUMBER THAT VALIDATES THE JOIN KEY. Both sides are built from this level's `.save`,
    /// so a live entity's record should almost always name something the region also drew. This
    /// being large while <see cref="HiddenForLive"/> is near zero would mean the guest's
    /// `entity+0x1C` is not the GDB record hash and the whole match is void — which shows up as
    /// a report rather than as props vanishing, because an unmatched record hides nothing.
    /// </summary>
    public int Unmatched { get; private set; }

    /// <summary>Live draws that carried no record hash at all.</summary>
    public int NoRecord { get; private set; }

    /// <summary>
    /// Indexes a mounted region's static entity copies.
    ///
    /// Both containers, because the region builds entities in two places for two reasons: the
    /// props path draws whatever a placement names a model for, and
    /// <see cref="Engine_LevelEntities"/> draws the creatures on top of it. They duplicate the
    /// guest identically and are reconciled identically.
    /// </summary>
    public static Engine_StaticTwins Build(Node3D region)
    {
        var twins = new Engine_StaticTwins();

        if (region.GetNodeOrNull<Node3D>(Refii.Level.Engine_LevelProps.NodeName) is { } props)
        {
            twins.IndexProps(props);
        }

        if (region.GetNodeOrNull<Node3D>(Engine_LevelEntities.NodeName) is { } entities)
        {
            twins.IndexEntities(entities);
        }

        return twins;
    }

    private void IndexProps(Node3D props)
    {
        foreach (Node child in props.GetChildren())
        {
            if (child is MultiMeshInstance3D batched)
            {
                if (!batched.HasMeta(Refii.Level.Engine_PropStreamer.RecordsMeta) ||
                    batched.Multimesh is not { } mesh)
                {
                    continue;
                }

                long[] records =
                    batched.GetMeta(Refii.Level.Engine_PropStreamer.RecordsMeta).AsInt64Array();
                for (int i = 0; i < records.Length && i < mesh.InstanceCount; i++)
                {
                    if (records[i] != 0L)
                    {
                        Add((uint)records[i],
                            new Twin(batched, i, mesh.GetInstanceTransform(i)));
                    }
                }

                continue;
            }

            if (child is MeshInstance3D single &&
                single.HasMeta(Refii.Level.Engine_PropStreamer.RecordMeta))
            {
                long record =
                    single.GetMeta(Refii.Level.Engine_PropStreamer.RecordMeta).AsInt64();
                if (record != 0L)
                {
                    Add((uint)record, new Twin(single, -1, single.Transform));
                }
            }
        }
    }

    /// <summary>
    /// Indexes the creature container, whose record rides as the text the inspector shows.
    ///
    /// Parsed rather than duplicated into a second numeric meta: the string is already the
    /// entity's identity everywhere else in the editor, and two spellings of one fact is how
    /// they drift apart.
    /// </summary>
    private void IndexEntities(Node3D entities)
    {
        foreach (Node child in entities.GetChildren())
        {
            if (child is not Node3D node || !node.HasMeta(Engine_EntityInstance.HashMeta))
            {
                continue;
            }

            string stated = node.GetMeta(Engine_EntityInstance.HashMeta).AsString();
            if (stated.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(stated[2..], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out uint record) &&
                record != 0u)
            {
                Add(record, new Twin(node, -1, node.Transform));
            }
        }
    }

    private void Add(uint record, Twin twin)
    {
        if (!_byRecord.TryGetValue(record, out List<Twin>? list))
        {
            list = new List<Twin>(1);
            _byRecord[record] = list;
        }

        list.Add(twin);
        ++Instances;
    }

    /// <summary>
    /// Hides the region's copy of one record the moment a live node for it is DRAWN.
    ///
    /// The fast half of the invariant, and the reason it exists separately from
    /// <see cref="Reconcile"/>: a region populates over many seconds, and waiting for the
    /// periodic pass would leave every door visibly doubled for the whole of that. Called on
    /// the frame the live node is created, never when the entity is merely reported — an entity
    /// whose model is a header-only stub resolves a path and then loads no mesh, and hiding on
    /// the report would take the only visible copy away.
    /// </summary>
    public void Claim(uint record)
    {
        if (record == 0u)
        {
            ++NoRecord;
            return;
        }

        if (!_byRecord.ContainsKey(record))
        {
            ++Unmatched;
            return;
        }

        Hide(record);
    }

    /// <summary>
    /// Re-applies the invariant to every indexed record.
    ///
    /// This is what makes the answer follow the guest for the whole run rather than being fixed
    /// at load. `Layers.ActivateLayer` spawns a gated layer's entities mid-game and
    /// `DeactivateLayer` destroys them, so records move between all three states while playing:
    /// hidden because we draw it, hidden because the guest does not have it, and visible because
    /// the guest has it and we cannot draw it.
    /// </summary>
    /// <param name="live">Every record the guest currently reports an entity for.</param>
    /// <param name="drawn">The subset of those we have a live node for.</param>
    public void Reconcile(HashSet<uint> live, HashSet<uint> drawn)
    {
        HiddenForLive = 0;
        HiddenUnstated = 0;

        foreach (KeyValuePair<uint, List<Twin>> pair in _byRecord)
        {
            bool stated = live.Contains(pair.Key);
            bool ours = drawn.Contains(pair.Key);
            bool shouldShow = stated && !ours;

            if (!shouldShow)
            {
                if (ours)
                {
                    ++HiddenForLive;
                }
                else
                {
                    ++HiddenUnstated;
                }

                Hide(pair.Key);
                continue;
            }

            if (_hidden.Remove(pair.Key))
            {
                // THE GUEST GAVE ONE BACK. Counted loudly: a non-zero Restored is a layer
                // having been activated or an entity having been destroyed under us, which is
                // exactly the behaviour the one-shot version could not produce.
                ++Restored;
                Show(pair.Value);
            }
        }
    }

    private void Hide(uint record)
    {
        if (!_hidden.Add(record) || !_byRecord.TryGetValue(record, out List<Twin>? twins))
        {
            return;  // already hidden
        }

        foreach (Twin twin in twins)
        {
            if (!GodotObject.IsInstanceValid(twin.Node))
            {
                continue;
            }

            if (twin.Instance < 0)
            {
                twin.Node.Visible = false;
                continue;
            }

            // A MULTIMESH INSTANCE CANNOT BE REMOVED, so it is collapsed instead. Godot's
            // instance buffer is a fixed-length array and `VisibleInstanceCount` only trims a
            // prefix, which is no use for an arbitrary slot. A basis of exactly zero sends every
            // vertex of the instance to one point, so it covers no pixels — and the slot is
            // still there, which is what lets `Show` put it back.
            if (twin.Node is MultiMeshInstance3D batched && batched.Multimesh is { } mesh &&
                twin.Instance < mesh.InstanceCount)
            {
                mesh.SetInstanceTransform(twin.Instance, new Transform3D(
                    new Basis(Vector3.Zero, Vector3.Zero, Vector3.Zero), twin.Standing.Origin));
            }
        }
    }

    private static void Show(List<Twin> twins)
    {
        foreach (Twin twin in twins)
        {
            if (!GodotObject.IsInstanceValid(twin.Node))
            {
                continue;
            }

            if (twin.Instance < 0)
            {
                twin.Node.Visible = true;

                // Its mesh may never have been streamed — the streamer skips hidden props, so a
                // prop that was hidden before its turn came up has no geometry yet.
                if (twin.Node is MeshInstance3D single &&
                    twin.Node.GetParent() is Refii.Level.Engine_PropStreamer streamer)
                {
                    streamer.EnsureStreamed(single);
                }

                continue;
            }

            if (twin.Node is MultiMeshInstance3D batched && batched.Multimesh is { } mesh &&
                twin.Instance < mesh.InstanceCount)
            {
                mesh.SetInstanceTransform(twin.Instance, twin.Standing);
            }
        }
    }
}
