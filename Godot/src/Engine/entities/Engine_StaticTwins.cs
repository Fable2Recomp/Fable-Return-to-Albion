using System;
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
///     a static copy is visible  IFF  we are not drawing a live node for that record
///                                    AND its LAYER is not deactivated
///
/// THE SECOND HALF USED TO READ "the guest HAS that record", AND THAT WAS THE BUG. It inferred
/// the gate from whether the entity turned up in a walk of guest memory, and absence from a walk
/// is not a statement. Two independent things made it wrong. The walk TRUNCATES — it stops at its
/// buffer's end and returns a plain count — so absence often meant "past the cap"; that alone
/// hid 1497 of Ravenscar's drawables while HobbeCave, whose population fit, lost none. And the
/// guest instantiates from a deferred store on demand, so a record it has not built YET is not a
/// record it has gated off.
///
/// The layer is the thing both sides actually state. The level states membership positionally in
/// its `.save`, and the guest keeps the runtime layer lists itself — so the gate is read rather
/// than guessed. See <c>GatedOff</c> for which list, which is not the obvious one.
///
/// The first half stands unchanged and for its original reason: the guest states 1437 live
/// entities but only 1030 name a model, so a record we cannot draw a live node for must keep its
/// static copy — that is the only copy, and removing it empties the street.
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

    /// <summary>
    /// The placement UID the guest gave each record, learned from its own live entities.
    /// </summary>
    /// <remarks>
    /// THE JOIN THE DESTROYED SET NEEDS. The guest keys DeadEntities by placement UID, and this
    /// class keys everything by record hash — different values written from different sources.
    /// The `.save` does carry the UID, but not as a named attribute: the guest reads it through
    /// a reader slot rather than by name, so parsing it out of the XML would mean guessing.
    ///
    /// The bridge already states both halves together for every live entity, so the mapping is
    /// learned rather than derived. Kept once seen: it is exactly the records whose entity has
    /// GONE that need it, and by then the guest is no longer reporting them.
    /// </remarks>
    private readonly Dictionary<uint, ulong> _uidOf = new();

    /// <summary>Records held hidden because the guest says the placement is destroyed.</summary>
    public int HiddenAsDestroyed { get; private set; }

    /// <summary>Records the record-to-UID join for a live entity the guest is reporting.</summary>
    public void NoteUid(uint record, ulong uid)
    {
        if (record == 0 || uid == 0)
        {
            return;
        }

        _uidOf[record] = uid;
    }

    /// <summary>
    /// Node names of the records left standing as the only copy, most-repeated first.
    /// </summary>
    /// <remarks>
    /// THE ONLY MEASURE OF WHAT THE STATIC LAYER IS FOR. Every other record here is built and
    /// then hidden, so these are the whole return on maintaining a second copy of the region
    /// and the whole reason a ghost can be left behind. Names rather than record hashes because
    /// the question is what CLASS of thing the guest never gives us geometry for.
    /// </remarks>
    public IReadOnlyList<string> StandingSample { get; private set; } = System.Array.Empty<string>();

    /// <summary>Distinct `.save` records the region drew something for.</summary>
    public int Records => _byRecord.Count;

    /// <summary>Drawables behind those records — more than one where a record drew several.</summary>
    public int Instances { get; private set; }

    /// <summary>Records hidden because the guest is drawing its own copy.</summary>
    public int HiddenForLive { get; private set; }

    /// <summary>
    /// Records hidden because their LAYER is off.
    ///
    /// This used to mean "the guest reported no entity for it", which was an inference and a
    /// wrong one. It now means the level put the record in a layer and the guest says that layer
    /// is not active — a statement from both sides.
    /// </summary>
    public int HiddenByLayer { get; private set; }

    /// <summary>
    /// Records gated off whose entity the guest is nonetheless reporting as LIVE.
    ///
    /// MUST BE ZERO. Anything else means the layer state is being read wrongly, and it is the
    /// only counter that can tell a correct gate from one that is quietly deleting the level.
    /// </summary>
    public int GatedButLive { get; private set; }

    /// <summary>The layer gate actually ran — the map was present and the guest could be asked.</summary>
    public bool LayerGateRan { get; private set; }

    /// <summary>Records shown again after the guest gave one up. Non-zero means a layer activated.</summary>
    public int Restored { get; private set; }

    /// <summary>
    /// The last reconcile ran against a TRUNCATED entity list.
    ///
    /// This no longer changes what is hidden — visibility is decided by the layer and by whether
    /// we drew a live node, neither of which reads an entity's absence. It still matters for the
    /// DEDUP half: a record whose live node exists past the cap will not be in `drawn`, so its
    /// static copy stays up and the prop is briefly doubled. A duplicate, not a hole, which is
    /// the direction this is all arranged to fail in.
    /// </summary>
    public bool LiveWasTruncated { get; private set; }

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
            twins.IndexLayers(props);
        }

        if (region.GetNodeOrNull<Node3D>(Engine_LevelEntities.NodeName) is { } entities)
        {
            twins.IndexEntities(entities);
        }

        return twins;
    }

    /// <summary>The layer each record sits in, from the `.save`. Zero when the file named none.</summary>
    private readonly Dictionary<uint, uint> _layerOf = new();

    /// <summary>Layers whose `load` is `AlwaysOn` — unconditional, never gateable.</summary>
    private readonly HashSet<uint> _alwaysOnLayers = new();

    /// <summary>Records whose layer the `.save` did state. Reported so a zero map is visible.</summary>
    public int LayeredRecords => _layerOf.Count;

    public int AlwaysOnLayers => _alwaysOnLayers.Count;

    /// <summary>
    /// Reads the record → layer map the props build left on the container.
    ///
    /// This is the gate's INPUT, and it is read from the level's own `.save` rather than
    /// inferred: layer membership is positional in that file — an entity belongs to the element
    /// that contains it — so it is a statement the level makes and not a heuristic.
    /// </summary>
    private void IndexLayers(Node3D props)
    {
        if (!props.HasMeta(Refii.Level.Engine_LevelProps.LayerRecordsMeta) ||
            !props.HasMeta(Refii.Level.Engine_LevelProps.LayerKeysMeta))
        {
            return;
        }

        long[] records =
            props.GetMeta(Refii.Level.Engine_LevelProps.LayerRecordsMeta).AsInt64Array();
        long[] keys = props.GetMeta(Refii.Level.Engine_LevelProps.LayerKeysMeta).AsInt64Array();

        for (int i = 0; i < records.Length && i < keys.Length; i++)
        {
            if (records[i] != 0L)
            {
                _layerOf[(uint)records[i]] = (uint)keys[i];
            }
        }

        if (props.HasMeta(Refii.Level.Engine_LevelProps.AlwaysOnLayersMeta))
        {
            foreach (long key in
                props.GetMeta(Refii.Level.Engine_LevelProps.AlwaysOnLayersMeta).AsInt64Array())
            {
                _alwaysOnLayers.Add((uint)key);
            }
        }
    }

    /// <summary>
    /// Should the region's copy of this record be gated off, per the guest's LAYER state?
    ///
    /// THE PREDICATE IS "IS THIS LAYER DEACTIVATED", AND IT IS NOT THE OBVIOUS ONE.
    ///
    /// The world manager keeps two `std::list&lt;uint32_t&gt;` — ACTIVATED at +60 and DEACTIVATED at
    /// +72 — and `sub_823662C8` reads a different one per `load` value:
    ///
    ///   823664d0  bne cr6, loc_82366548   AlwaysOn    -> spawn, consulting NEITHER list
    ///   823664e8  bl  sub_82320A68        InitiallyOn -> spawn iff ABSENT from +72
    ///   82366520  bl  sub_8244C428        else        -> spawn iff PRESENT in +60
    ///
    /// `sub_82320A68` walks `W+72` and returns PRESENT (`xori r3, r8, 1` at 82320AE4); the call
    /// site then spawns on zero (`beq cr6, loc_82366548` at 823664F4). And the two lists are
    /// pinned by their writers touching both off one base: `Layers.ActivateLayer` inserts into
    /// +60 and erases from +72, `DeactivateLayer` does the reverse.
    ///
    /// Only the first two rows can reach this code — the props build already dropped everything
    /// else — so the whole gate is: an `InitiallyOn` layer named in the DEACTIVATED list.
    ///
    /// Asking the ACTIVATED list instead would have been catastrophic and plausible. Both lists
    /// start EMPTY (`sub_8231BF20` default-constructs each, and only a save-game load assigns
    /// them), so on a new game "is it in the activated list" is false for every layer in the
    /// level and would have hidden every `InitiallyOn` prop in it.
    ///
    /// Null means we cannot ask, and then nothing is gated — the safe direction: an ungated prop
    /// is at worst a duplicate the entity pass retires, a wrongly gated one is a hole.
    /// </summary>
    private bool GatedOff(uint record, Func<uint, bool>? layerIsDeactivated)
    {
        if (layerIsDeactivated is null ||
            !_layerOf.TryGetValue(record, out uint layer) || layer == 0u)
        {
            return false;
        }

        return !_alwaysOnLayers.Contains(layer) && layerIsDeactivated(layer);
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
    /// <summary>Whether the region's copy of a record is currently hidden (claimed or off).</summary>
    public bool IsHidden(uint record) => _hidden.Contains(record);

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
    /// <param name="liveIsComplete">
    /// <paramref name="live"/> is EVERY entity the guest has, not a prefix of them. False
    /// disables the hide-on-unstated half — see the note on that branch.
    /// </param>
    /// <param name="layerIsDeactivated">
    /// The guest's own answer to "is this layer hash in the DEACTIVATED list", or null when it
    /// cannot be asked. THIS IS THE GATE — see <see cref="GatedOff"/> for why it is this list
    /// and not the activated one.
    /// </param>
    public void Reconcile(
        HashSet<uint> live,
        HashSet<uint> drawn,
        bool liveIsComplete = true,
        Func<uint, bool>? layerIsDeactivated = null)
    {
        HiddenForLive = 0;
        HiddenByLayer = 0;
        GatedButLive = 0;
        LiveWasTruncated = !liveIsComplete;
        LayerGateRan = layerIsDeactivated is not null && _layerOf.Count != 0;
        HiddenAsDestroyed = 0;

        // READ FRESH EVERY PASS, never cached. Deactivating a layer purges that layer's entries
        // from the guest's set, which is how broken props legitimately come back when a layer
        // cycles; a cached copy would keep them hidden for the rest of the session.
        var destroyed = new HashSet<ulong>(Refii.Host.HostBridge.GetDeadEntities());

        var standing = new Dictionary<string, int>();

        foreach (KeyValuePair<uint, List<Twin>> pair in _byRecord)
        {
            bool ours = drawn.Contains(pair.Key);

            // THE GATE IS THE LAYER, NOT THE ENTITY'S ABSENCE FROM A WALK.
            //
            // This used to read `live.Contains(record)` and hide anything the guest had not
            // reported. Two separate things made that wrong. The entity walk TRUNCATES — it stops
            // at its buffer's end and returns a plain count — so absence often just meant "past
            // the cap"; that alone cost Ravenscar 1497 drawables against HobbeCave's 0, the only
            // difference between them being whether the population fit. And even with a complete
            // list it inferred a gate the guest states outright: the guest instantiates from a
            // deferred store on demand, so a record it has not built yet is not a record it has
            // gated off.
            //
            // So ask the question the level and the guest both answer. Layer membership is
            // positional in the `.save` and the guest keeps the runtime layer state itself; a
            // record outside an inactive layer is not gated, whatever the entity walk did or did
            // not return. `live` is no longer consulted for visibility at all — only `drawn` is,
            // and that is a positive statement that we put geometry on screen for this record.
            bool gated = GatedOff(pair.Key, layerIsDeactivated);

            // THE THIRD TERM. "The guest has no live entity for this record" is not "show the
            // region's copy" — destroying a breakable produces exactly that state, and the copy
            // reappearing where the smashed one stood is the ghost. The guest states destruction
            // outright and persists it to its own save, so this asks rather than infers.
            bool gone = destroyed.Count != 0 &&
                _uidOf.TryGetValue(pair.Key, out ulong uid) && destroyed.Contains(uid);

            bool shouldShow = !ours && !gated && !gone;

            if (!shouldShow)
            {
                if (ours)
                {
                    ++HiddenForLive;
                }
                else if (gone)
                {
                    ++HiddenAsDestroyed;
                }
                else
                {
                    ++HiddenByLayer;

                    // THE CONTRADICTION, AND THE ONLY THING THAT CAN CATCH A WRONG GATE.
                    //
                    // We just hid a record because its layer reads as off, and the guest is
                    // reporting a live entity for that same record. Both cannot be true. A
                    // non-zero count here means the layer state is being read wrongly — the wrong
                    // world-manager list, or the right one with inverted polarity — and it is the
                    // failure that would otherwise look exactly like the bug this replaced:
                    // props quietly disappearing with every counter claiming to be correct.
                    if (live.Contains(pair.Key))
                    {
                        ++GatedButLive;
                    }
                }

                Hide(pair.Key);
                continue;
            }

            // Standing: shown, and thus the only copy of this record on screen.
            foreach (Twin twin in pair.Value)
            {
                if (!GodotObject.IsInstanceValid(twin.Node))
                {
                    continue;
                }

                string name = twin.Node.Name;
                standing.TryGetValue(name, out int seen);
                standing[name] = seen + 1;
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

        var ranked = new List<KeyValuePair<string, int>>(standing);
        ranked.Sort((a, b) => b.Value.CompareTo(a.Value));
        var sample = new List<string>();
        for (int i = 0; i < ranked.Count && i < 12; i++)
        {
            sample.Add($"{ranked[i].Key}x{ranked[i].Value}");
        }

        StandingSample = sample;
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
