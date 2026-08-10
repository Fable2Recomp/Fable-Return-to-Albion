using System;
using System.Collections.Generic;
using Godot;
using Refii.Fx;
using Refii.Gdb;
using Refii.Models;

namespace Refii.Level;

public static class Engine_LevelFx
{
    /// <summary>
    /// FNV-1 of "ParticleEffect" — the BREAKABLE'S DESTRUCTION EFFECT, not an ambient one.
    ///
    /// `sub_82587A08` is the breakable component. It reads this property off the entity's
    /// component definition and hands it to the spawner at 0x8238B860 when the object breaks.
    /// A window's ParticleEffect is the shower of glass it makes when smashed; a crate's is the
    /// splinters.
    ///
    /// So it MUST NOT be resolved as level ambience, and treating it as interchangeable with
    /// ParticleEmitter is what made BWSSlums load with 67 permanent window-smashes,
    /// 27 breaking china and 12 crate strikes going off in the street — while the torches and
    /// lanterns, whose effect is stated on a ParticleEmitter, resolved to nothing.
    ///
    /// It is kept as a constant because the classifier in FxCheck reports on it, and because
    /// naming the thing we deliberately do not follow is worth more than deleting it.
    /// </summary>
    public const uint ParticleEffect = 0x5B009F68;

    /// <summary>
    /// FNV-1 of "ParticleEmitter" — NOT "ParticleSystemName", which this was called and which
    /// hashes to 0xE2FB6C91 instead.
    ///
    /// The guest reads this property in three places and never spells the hash out: there is no
    /// {hash, name} registration table anywhere in the image, so every call site hashes the
    /// literal string at runtime through `Fnv1HashWithBasis` (0x821F3D58) with the basis
    /// materialised inline. <c>sub_82767C70</c> is the clearest — it loads "ParticleEmitter" and
    /// 0x811C9DC5, hashes them, and asks for the property as TYPE 6, an entry reference.
    /// <c>sub_82569B08</c> asks for the same name as TYPE 4, a string hash it then resolves to a
    /// name and looks up in particle_bank.bnk. Both forms are live, which is why the walk below
    /// takes the value whatever its type says.
    /// </summary>
    public const uint ParticleEmitter = 0x4EDC9083;

    public const uint Null = 0x811C9DC5;

    public const uint SilverKeyTemplate = 0x4AB4C31A;

    public const uint SilverKeyTemplateDynamic = 0xEA7C60E5;

    public const string SilverKeyEffect = "FX_Key_Silver_01";

    /// <summary>
    /// The two template entries a silver key is placed from. Matched on the placement's PARENT,
    /// which is the identity the data states.
    ///
    /// Not a substring match against the entity name: two placements can share a template and
    /// disagree about their names, and a name can contain "silverkey" without being a key.
    /// </summary>
    public static bool IsSilverKeyPlacement(GdbPlacement placement) =>
        placement.ParentHash is SilverKeyTemplate or SilverKeyTemplateDynamic;

    private static readonly Dictionary<uint, bool> SustainCache = new();

    public static bool IsAmbientEffect(FxBank bank, uint effectHash)
    {
        if (SustainCache.TryGetValue(effectHash, out bool cached))
        {
            return cached;
        }

        bool sustains = Engine_FxSystem.EffectSustains(bank, effectHash);
        SustainCache[effectHash] = sustains;
        return sustains;
    }

    /// <summary>
    /// EVERY EFFECT HERE COMES OUT OF THE DATA. There is no keyword table.
    ///
    /// A keyword table would be easy to write — "torch" means fxenv_torch_fire_03, "brazier"
    /// means FXENV_Medium_Brazier_Fire_01 — and it would stand in for data that is there to be
    /// read:
    ///
    ///   - `sub_82587A08` reads "ParticleEffect" off the entity's component definition and hands
    ///     it to the spawner at 0x8238B860. `sub_82767C70` and `sub_82569B08` read
    ///     "ParticleEmitter" the same way. The effect is NEVER derived from the model name, and
    ///     there is no per-object-type effects table anywhere in the image — `ParticleFXTableIdx`
    ///     looks like one and is a per-instance saved int belonging to the burning system.
    ///
    /// What the data DOES require is a walk rather than a field read, which is what the two
    /// resolvers below do. `sub_821DA3B0` hops from the entity to its component definition
    /// through a type-6 entryRef named after the component's own class, and
    /// `GDBEntry_FindPropWalkParents` (0x821E5A08) follows the parent chain when an entry has no
    /// property of its own. A placement can carry no particle field and still have an effect.
    /// </summary>
    public static int Spawn(
        Node3D container,
        GdbView levelView,
        List<GdbView> supplementalViews,
        List<GdbPlacement> placements,
        Dictionary<uint, string>? modelPaths = null)
    {
        FxBank? bank = FableFx.SharedBank();
        if (bank is null)
        {
            return 0;
        }

        var views = new List<GdbView>(supplementalViews.Count + 1) { levelView };
        views.AddRange(supplementalViews);

        Dictionary<uint, uint> bindings = ExtractParticleFxBindings(views, bank);

        // ONE EFFECT PER PLACEMENT, keyed on the placement's own record hash.
        //
        // The record hash, not a proximity test. What has to be stopped is the two passes below
        // emitting the same placement twice — the second pass walks the whole list again, so
        // anything the first resolved comes back round — and a distance rule does that only by
        // also throwing away any SECOND instance of an effect near the first. Street lamps are
        // closer together than 2.5m: measured on BWSSlums, 289 placements resolve, 112 are
        // one-shots, and of the 177 left a 2.5m rule keeps 94 and silently eats 83.
        //
        // The guest has no such rule. It registers an attacher per component and plays them all,
        // so co-located duplicates in the data are meant to be co-located duplicates on screen.
        var emitted = new HashSet<uint>();
        int count = 0;
        int oneShotSkips = 0;
        int byAuthored = 0;
        int byName = 0;
        int byBinding = 0;
        int attached = 0;
        int missedBone = 0;

        bool Emit(
            GdbPlacement placement,
            uint hash,
            out Vector3 origin,
            bool bypassAmbient = false,
            string dummy = "")
        {
            Transform3D xform = Engine_LevelProps.BuildTransform(placement);

            // ONTO THE BONE, not the prop's origin. A lamp's flame belongs at its head, and 78
            // of BWSSlums' 86 effects state a DummyObject saying so.
            if (dummy.Length != 0 && modelPaths is not null &&
                modelPaths.TryGetValue(placement.Hash, out string? model) &&
                TryDummyTransform(model, dummy, out Transform3D bone))
            {
                xform *= bone;
                attached++;
            }
            else if (dummy.Length != 0)
            {
                missedBone++;
            }

            origin = xform.Origin;

            if (!bypassAmbient && !IsAmbientEffect(bank, hash))
            {
                oneShotSkips++;
                return false;
            }

            if (!emitted.Add(placement.Hash))
            {
                return false;
            }

            var fx = new FableFx
            {
                Name = $"Fx_{hash:X8}_{count}",
                EffectName = $"FX_{hash:X8}",
                Transform = xform,
            };
            container.AddChild(fx);
            count++;
            return true;
        }

        foreach (GdbPlacement placement in placements)
        {
            uint fx = HuntFxHash(placement.Hash, views, out string dummy);
            if (fx == 0 || fx == Null || bank.FindByHash(fx) is null)
            {
                continue;
            }

            if (Emit(placement, fx, out _, false, dummy))
            {
                byAuthored++;
            }
        }

        foreach (GdbPlacement placement in placements)
        {
            if (IsSilverKeyPlacement(placement))
            {
                continue;
            }

            string name = placement.EntityName ?? string.Empty;
            uint fx = 0;
            int source = 0;

            // An EXACT name identity, not a mapping. The guest resolves a type-4 ParticleEmitter
            // by taking the property's string hash, resolving it to a name through
            // GDBNames_ResolveHash and looking THAT up in particle_bank.bnk — so a placement
            // whose own name is a bank entry is an effect entity standing in the level, not a
            // prop we have guessed an effect for. The lookup has to hit the bank exactly; there
            // is no substring or keyword step.
            if (name.Length != 0 && bank.FindByName(name) is not null)
            {
                fx = FxBank.Fnv1Lower(name);
                source = 1;
            }
            else if (bindings.TryGetValue(placement.Hash, out uint viaRecord) &&
                     bank.FindByHash(viaRecord) is not null)
            {
                fx = viaRecord;
                source = 2;
            }
            else if (placement.ParentHash != 0 &&
                     bindings.TryGetValue(placement.ParentHash, out uint viaParent) &&
                     bank.FindByHash(viaParent) is not null)
            {
                fx = viaParent;
                source = 2;
            }

            if (fx != 0 && fx != Null && bank.FindByHash(fx) is not null &&
                Emit(placement, fx, out _))
            {
                switch (source)
                {
                    case 1: byName++; break;
                    case 2: byBinding++; break;
                }
            }
        }

        if (count != 0 || oneShotSkips != 0)
        {
            // Keep the breakdown. It is the measurement that says whether the data-derived
            // routes actually reach the effects, which is the question the deleted keyword
            // table was hiding.
            GD.Print(
                $"[fx] level spawn: {count} ({byAuthored} authored, {byName} by name, " +
                $"{byBinding} by binding; {attached} on a bone, {missedBone} stated a bone we " +
                $"could not find; {oneShotSkips} one-shot placement(s) skipped)");
        }

        return count;
    }

    /// <summary>FNV-1 of "ParticleEmitterComponent" — CECParticleEmitter's definition hop.</summary>
    public const uint ParticleEmitterComponent = 0x19EED53C;

    /// <summary>FNV-1 of "ParticleAttacherComponent" — CECParticleAttacher's definition hop.</summary>
    public const uint ParticleAttacherComponent = 0x19516626;

    /// <summary>FNV-1 of "ParticlesToAttach" — the attacher's list of attachment entries.</summary>
    public const uint ParticlesToAttach = 0x4B6E0B22;

    /// <summary>FNV-1 of "AttachedParticle" — the base entry each attachment inherits from.</summary>
    public const uint AttachedParticle = 0x8FD61CD7;

    /// <summary>FNV-1 of "parent" — the inheritance link.</summary>
    public const uint Parent = 0x5F6317D5;

    /// <summary>FNV-1 of "DummyObject" — the bone an attached effect hangs from.</summary>
    public const uint DummyObject = 0xF4DF0382;

    /// <summary>FNV-1 of "OffsetFromDummy" — a translation in the bone's space, SUBTRACTED.</summary>
    public const uint OffsetFromDummy = 0xE867B8A0;

    /// <summary>
    /// Turns a stated <c>DummyObject</c> path into the bone name a model actually carries.
    ///
    /// The data states a dotted namespace path; the models name their bones with underscores
    /// and a <c>_DummyObject</c> suffix. Every distinct value in the shipped data, and what it
    /// has to become:
    ///
    ///     Prop.Layout.Light                                 -> Layout_Light_DummyObject
    ///     Prop.FX.Particle.FX_Torch_Fire_01.par             -> FX_Particle_DummyObject
    ///     Prop.FX.Particle.Effect_1.FXENV_Lamp_Flame_01.par -> FX_Particle_DummyObject1
    ///     Prop.FX.Particle.Effect_1.                        -> FX_Particle_DummyObject1
    ///     Character.Focal.Chest                             -> Focal_Chest_DummyObject
    ///
    /// `Prop.Layout.Light` is what settles it. It carries no effect name and no `.par`, so
    /// nothing has to be stripped — and `Layout_Light_DummyObject` is a bone the lamp models
    /// demonstrably have, sitting beside `FX_Particle_DummyObject` on the same rig. That fixes
    /// the shape as: drop the leading category, join what is left with underscores, append
    /// `_DummyObject`.
    ///
    /// The two things that ARE stripped are both trailing decoration rather than path:
    /// a `&lt;EffectName&gt;.par` pair naming what hangs there, and an `Effect_N` segment which is
    /// the INDEX — OB_MusicBox carries `FX_Particle_DummyObject` and `FX_Particle_DummyObject1`
    /// side by side, and `Effect_1` is how the data picks the second.
    /// </summary>
    public static string DummyBoneName(string dummyPath)
    {
        if (dummyPath.Length == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>(
            dummyPath.Split('.', StringSplitOptions.RemoveEmptyEntries));
        if (parts.Count < 2)
        {
            return string.Empty;
        }

        // The category — Prop, Character — names the owner, not the dummy.
        parts.RemoveAt(0);

        // A trailing "<EffectName>.par" is a note about what hangs here, not part of the path.
        if (parts.Count >= 2 && parts[^1].Equals("par", StringComparison.OrdinalIgnoreCase))
        {
            parts.RemoveRange(parts.Count - 2, 2);
        }

        string suffix = string.Empty;
        if (parts.Count != 0 &&
            parts[^1].StartsWith("Effect_", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(parts[^1].AsSpan("Effect_".Length), out int index))
        {
            parts.RemoveAt(parts.Count - 1);
            suffix = index == 0 ? string.Empty : index.ToString();
        }

        return parts.Count == 0
            ? string.Empty
            : string.Join('_', parts) + "_DummyObject" + suffix;
    }

    /// <summary>
    /// The ONLY edges an ambient effect is reached through, and the reason there is a list at
    /// all rather than a graph search.
    ///
    /// A breadth-first walk over every reference does find effects — it found 481 in Bloodstone
    /// — but they are the wrong ones. It leaves the entity it started on, wanders through shared
    /// base entries, and comes back with whatever it bumps into: BWSSlums loaded with 67
    /// permanent `fxenv_windowbreak_small_green`, 27 `FXWPN_Breakable_China_01` and 12
    /// `FXWPN_Crate_Strike_01` going off in the street, while the torches and lanterns it walked
    /// straight past resolved to nothing.
    ///
    /// The guest never searches. Each component asks for a property named after its own class
    /// and reads what is on the other end, so the reachable set is exactly these edges:
    ///
    ///     ParticleEmitterComponent  -> [parent*] -> ParticleEmitter (String)
    ///     ParticleAttacherComponent -> ParticlesToAttach -> <entry>* -> ParticleEmitter
    ///
    /// Every other Particle-named property in the data is an EVENT effect and belongs to
    /// something happening, not to the level standing still: ParticleEffect (breakable
    /// destruction), ImpactParticleEffect, LeftoverParticleEffect, OpenDoorParticleFX,
    /// WeaponParticle, ClothingParticle, OnFireParticleFXIndex, ThemeParticleFX,
    /// FirefliesParticleEffect. Adding any of them here puts a permanent explosion in a street.
    /// </summary>
    private static readonly uint[] AmbientEdges =
    {
        ParticleEmitterComponent,
        ParticleAttacherComponent,
        ParticlesToAttach,
        AttachedParticle,
        ParticleEmitter,
        Parent,
    };

    /// <summary>
    /// Resolves the effect a placement states, by walking the chain the guest walks.
    ///
    /// THE SHAPE IS THE GUEST'S, and it is visible in the data exactly as IDA described it.
    /// One real chain out of Bloodstone, printed by <c>FxCheck</c>:
    ///
    ///     ParticleEmitter (ResourceReference) 0xA4FCA3BC        <- on the placement
    ///       ParticleEmitterComponent (Reference) 0x86FE8402     <- sub_821DA3B0's component hop
    ///         ParticleEmitter (String) "fxcre_shadow_hobbe_tar" <- the answer
    ///         parent (Reference) ParticleEmitterComponent       <- base, states the empty hash
    ///
    /// Three things that each cost a resolution if you skip them:
    ///
    /// THE TYPE DECIDES WHAT THE VALUE IS. As a String it is a name hash; as a Reference it is
    /// a GDB ENTRY hash and the effect is named on the entry it points at. Returning the value
    /// whichever it was meant every reference-typed field became an entry hash asked of a name
    /// table — 254 of Bloodstone's misses, none of them a data problem.
    ///
    /// THE BANK IS KEYED ON THE LOWERCASED NAME, and the GDB stores the hash of the string AS
    /// WRITTEN. "fxcre_shadow_hobbe_tar" matches by luck because it is already lower;
    /// "FXCRE_Hollowman_Wisp_Trail" does not. So the answer comes from re-hashing the resolved
    /// TEXT, not from the stored hash — which is what the guest does too, resolving the hash to
    /// a name through GDBNames_ResolveHash before looking it up.
    ///
    /// THE BASE ENTRY STATES THE EMPTY HASH. Every ParticleEmitterComponent inherits from one
    /// that carries `ParticleEmitter = 0x811C9DC5`, the bare FNV-1 basis — the "nothing here"
    /// value. Treating that as a name gives every emitter in the game the same effect, so
    /// <see cref="Null"/> is rejected wherever a value is read.
    /// </summary>
    /// <remarks>
    /// It follows <see cref="AmbientEdges"/> and nothing else, breadth-first so the nearest
    /// stated effect wins. Inside a ParticlesToAttach list the entries are unnamed, so that one
    /// subtree opens up to any reference — bounded, because it is only reached through the
    /// attacher.
    /// </remarks>
    public static uint HuntFxHash(uint root, List<GdbView> views) =>
        HuntFxHash(root, views, out _, out _);

    public static uint HuntFxHash(uint root, List<GdbView> views, out string dummy) =>
        HuntFxHash(root, views, out dummy, out _);

    /// <param name="dummy">
    /// The <c>DummyObject</c> bone the effect attaches to, empty when it is a plain
    /// CECParticleEmitter sitting at its entity's origin.
    ///
    /// `sub_82767C70` reads it as a TYPE 4 name — `sub_821D4D90` resolves the hash back to a
    /// string through `GDBNames_ResolveHash` — and stores it at the attacher record's +0. The
    /// models name the matching bone literally: BS_Lights_Town, ESA_HangingLantern and
    /// OB_MusicBox all carry an `FX_Particle_DummyObject`. Until it is honoured, a lamp's flame
    /// is placed at the lamp's ORIGIN rather than at its head.
    /// </param>
    /// <param name="offset">
    /// <c>OffsetFromDummy</c>, a translation in the BONE's space that is SUBTRACTED, not added.
    ///
    /// `sub_827661B0` builds a matrix whose rotation rows are identity and whose row 3 is the
    /// offset, and `sub_825EBBC8` stores its INVERSE at the attach record's +80. That inverse is
    /// what `sub_825ED2A0` composes with — `addi r4, r31, 0x50`, not `0x10` — so the frame is
    ///
    ///     child = inv(translate(Offset)) x attachPointLocal x parentWorld
    ///
    /// Getting the sign backwards puts a flame as far the wrong side of the lamp as it should
    /// have been the right side, which reads as a plausible mis-scale rather than a sign error.
    /// </param>
    public static uint HuntFxHash(
        uint root, List<GdbView> views, out string dummy, out Vector3 offset)
    {
        dummy = string.Empty;
        offset = Vector3.Zero;
        var seen = new HashSet<uint> { root };
        var queue = new Queue<(uint Hash, int Depth, bool InList)>();
        queue.Enqueue((root, 0, false));
        int budget = 512;

        while (queue.Count != 0 && budget-- > 0)
        {
            (uint hash, int depth, bool inList) = queue.Dequeue();

            foreach (GdbView view in views)
            {
                if (!view.TryLookup(hash, out int record))
                {
                    continue;
                }

                List<GdbField> fields = view.Fields(record);

                // The attacher states its bone on the SAME entry that states the emitter, so
                // pick it up on the way past rather than walking the chain a second time.
                foreach (GdbField f in fields)
                {
                    if (f.Hash == DummyObject && f.Text.Length != 0)
                    {
                        dummy = f.Text;
                    }
                    // OffsetFromDummy is read here once its encoding is measured — see FxCheck.
                    // sub_824BC3D0 writes it as three floats into the attacher record at
                    // +16/+20/+24, but how the GDB stores that struct is not yet established
                    // and guessing at it would silently displace every attached effect.
                }

                foreach (GdbField f in fields)
                {
                    if (f.Hash != ParticleEmitter ||
                        f.Type != GdbFieldType.String || f.Raw == 0 || f.Raw == Null)
                    {
                        continue;
                    }

                    uint named = f.Text.Length != 0 ? FxBank.Fnv1Lower(f.Text) : f.Raw;
                    if (named != 0 && named != Null)
                    {
                        return named;
                    }
                }

                if (depth < 6)
                {
                    foreach (GdbField f in fields)
                    {
                        if (!f.IsReference || f.Raw == 0 || f.Raw == Null)
                        {
                            continue;
                        }

                        bool named = Array.IndexOf(AmbientEdges, f.Hash) >= 0;
                        if ((!named && !inList) || !seen.Add(f.Raw))
                        {
                            continue;
                        }

                        queue.Enqueue((f.Raw, depth + 1, f.Hash == ParticlesToAttach || inList));
                    }
                }

                break;
            }
        }

        return 0;
    }

    /// <summary>
    /// The rest transform of a model's named dummy bone, in the model's own space.
    ///
    /// Walks the parent chain because a dummy is authored as a child — on the lamps
    /// `FX_Particle_DummyObject` hangs off bone 4, not off the root — so its own local rest is
    /// only part of the answer.
    ///
    /// These are STATIC props. The guest recomputes the attachment every frame from the live
    /// skeleton (`sub_825ED2A0`), which matters for a character carrying a torch; for a lamp
    /// bolted to a wall the rest pose is that same answer, evaluated once.
    /// </summary>
    private static bool TryDummyTransform(string modelPath, string dummy, out Transform3D bone)
    {
        bone = Transform3D.Identity;
        if (dummy.Length == 0)
        {
            return false;
        }

        if (!RigCache.TryGetValue(modelPath, out Engine_MdlRig? rig))
        {
            var mesh = ResourceLoader.Load<Mesh>(
                Engine_BnkMeshLoader.PathPrefix + modelPath);
            rig = Engine_MdlRig.TryFrom(mesh);
            RigCache[modelPath] = rig;
        }

        if (rig is not { } r)
        {
            return false;
        }

        // THE STATED STRING FIRST, because that is the guest's actual key.
        //
        // `sub_822650A0` CRC-32s the DummyObject value UNMODIFIED (poly 0xEDB88320, init 0, no
        // final XOR — `sub_823B0A38`) and binary-searches the .gmd node table with it. There is
        // no dotted-path parsing and no "_DummyObject" concatenation anywhere in the guest.
        //
        // Dumping BS_Lights_Town_Slum.mdl.gmd settles that the key is present verbatim — the
        // node table holds `Prop.Layout.Light`, `Prop.FX.Particle.FXENV_Lamp_Flame_01.par` and
        // `Prop.Action.Generic` alongside `Layout_Light_DummyObject` and
        // `FX_Particle_DummyObject`. Both spellings are real nodes, which is why the derived
        // form below also finds something; the dotted one is the one the data asked for.
        int index = Array.FindIndex(
            r.Names, n => n.Equals(dummy, StringComparison.OrdinalIgnoreCase));

        // The authoring convention, as a fallback only. `Prop.Layout.Light` and
        // `Layout_Light_DummyObject` name the same attachment on the same rig, so a model whose
        // table our reader exposes under only the underscore spelling still resolves.
        if (index < 0)
        {
            string derived = DummyBoneName(dummy);
            if (derived.Length != 0)
            {
                index = Array.FindIndex(
                    r.Names, n => n.Equals(derived, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (index < 0)
        {
            return false;
        }

        var local = Transform3D.Identity;
        for (int at = index; at >= 0; at = r.Parents[at])
        {
            var step = new Transform3D(
                new Basis(r.Rotation(at)).Scaled(r.Scale(at)), r.Translation(at));
            local = step * local;
        }

        bone = local;
        return true;
    }

    private static readonly Dictionary<string, Engine_MdlRig?> RigCache = new();

    private static Dictionary<uint, uint> ExtractParticleFxBindings(
        List<GdbView> views, FxBank bank)
    {
        var outMap = new Dictionary<uint, uint>();
        var parentOf = new Dictionary<uint, uint>();

        foreach (GdbView view in views)
        {
            for (int i = 0; i < view.RecordCount; i++)
            {
                uint recordHash = view.RecordHash(i);
                foreach ((uint fieldHash, GdbFieldType type, uint val) in view.RawFields(i))
                {
                    if (fieldHash == GdbView.HashParent &&
                        type is GdbFieldType.Reference or GdbFieldType.ResourceReference &&
                        val != 0)
                    {
                        parentOf[recordHash] = val;
                    }
                }
            }

            for (int i = 0; i < view.RecordCount; i++)
            {
                uint recordHash = view.RecordHash(i);
                uint chainRoot = 0;
                foreach ((uint fieldHash, GdbFieldType type, uint val) in view.RawFields(i))
                {
                    if (fieldHash == ParticleEffect &&
                        type is GdbFieldType.Reference or GdbFieldType.ResourceReference &&
                        val != 0)
                    {
                        chainRoot = val;
                    }
                }

                if (chainRoot == 0 || outMap.ContainsKey(recordHash))
                {
                    continue;
                }

                uint best = ChainFxHash(view, chainRoot, bank);
                if (best != 0)
                {
                    outMap[recordHash] = best;
                }
            }
        }

        if (outMap.Count != 0 && parentOf.Count != 0)
        {
            var resolved = new Dictionary<uint, uint>(outMap);
            foreach (KeyValuePair<uint, uint> entry in parentOf)
            {
                uint start = entry.Key;
                if (resolved.ContainsKey(start))
                {
                    continue;
                }

                uint current = start;
                int guard = 64;
                var path = new HashSet<uint>();
                while (guard-- > 0 && current != 0 && path.Add(current))
                {
                    if (!parentOf.TryGetValue(current, out uint parent))
                    {
                        break;
                    }

                    current = parent;
                    if (outMap.TryGetValue(current, out uint hit))
                    {
                        resolved[start] = hit;
                        break;
                    }
                }
            }

            outMap = resolved;
        }

        return outMap;
    }

    private static uint ChainFxHash(GdbView view, uint chainRoot, FxBank bank)
    {
        uint best = 0;
        var seen = new HashSet<uint>();
        var stack = new Stack<uint>();
        stack.Push(chainRoot);
        int budget = 4096;

        while (stack.Count != 0 && budget-- > 0)
        {
            uint reference = stack.Pop();
            if (reference == 0 || !seen.Add(reference) ||
                !view.TryLookup(reference, out int record))
            {
                continue;
            }

            foreach ((uint _, GdbFieldType type, uint val) in view.RawFields(record))
            {
                if (type == GdbFieldType.String && val != Null)
                {
                    if (best == 0)
                    {
                        best = val;
                    }

                    if (bank.FindByHash(val) is not null)
                    {
                        return val;
                    }
                }
                else if (type is GdbFieldType.Reference or GdbFieldType.ResourceReference &&
                         val != 0)
                {
                    stack.Push(val);
                }
            }
        }

        return best;
    }

}
