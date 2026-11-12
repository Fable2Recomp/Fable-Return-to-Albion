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

    /// <summary>
    /// FNV-1 of "NewLamp_on_stick" entity template — Lionhead's outdoor lantern placements in Ravenscar (chapter3_good)
    /// that have an authored light dummy ('Prop.Layout.Light') but lack a ParticleEmitter component in GDB.
    /// </summary>
    public const uint LampOnStickTemplate = 0xB4BD3E22;

    /// <summary>
    /// Candle flame particle effect (0x6E4BB265 = FX_Candle_Flame_01) used as visible flame source for
    /// lanterns lacking authored GDB particle emitters.
    /// </summary>
    public const uint CandleFlame = 0x6E4BB265;

    /// <summary>
    /// Checks whether a placement is a "NewLamp_on_stick" lantern fixture based on parent template or entity name prefix.
    /// </summary>
    public static bool IsLampOnStickPlacement(GdbPlacement placement) =>
        placement.ParentHash == LampOnStickTemplate ||
        (placement.EntityName is not null && placement.EntityName.StartsWith("NewLamp_on_stick", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns all visual particle effect attachments for a placement, including authored GDB
    /// attachments and engine-level fallbacks for known base-game omissions (such as NewLamp_on_stick
    /// lanterns lacking authored particle emitters in GDB).
    /// </summary>
    public static List<FxAttachment> ResolvePlacementAttachments(
        GdbPlacement placement,
        List<GdbView> views)
    {
        List<FxAttachment> authored = HuntFxAttachments(placement.Hash, views);
        if (authored.Count == 0 && IsLampOnStickPlacement(placement))
        {
            authored.Add(new FxAttachment(CandleFlame, "Prop.Layout.Light", Vector3.Zero, false));
        }
        return authored;
    }

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

    /// <summary>The mesh convention (x, z, -y) from the simulation's (x, z, y); its own inverse.</summary>
    private static readonly Basis SimFromMesh = Basis.FromScale(new Vector3(1f, 1f, -1f));

    public static bool IsAmbientEffect(FxBank bank, uint effectHash)
    {
        // LOCKED FOR THE LEVEL CHANGE: the next region's FX spawn on a worker while the region
        // we are standing in still ticks its own effects on the main thread.
        lock (SustainCache)
        {
            if (SustainCache.TryGetValue(effectHash, out bool cached))
            {
                return cached;
            }
        }

        bool sustains = Engine_FxSystem.EffectSustains(bank, effectHash);
        lock (SustainCache)
        {
            SustainCache[effectHash] = sustains;
        }

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
    /// <param name="attachTo">
    /// Placements that own a LIVE node the effect must hang from rather than standing beside.
    ///
    /// A silver key bobs and spins, so its glow has to bob and spin with it. Before this the key
    /// was special-cased out of the walk entirely and handed a hardcoded effect name — which the
    /// data states anyway (<c>FX_Key_Silver_01</c>, resolved through the ordinary attachment walk
    /// and confirmed alive), and which cost the key both the offset its data states and the
    /// orientation rule every other attachment obeys. Passing the node instead keeps the one
    /// resolver and lets the animation come from the node.
    /// </param>
    public static int Spawn(
        Node3D container,
        GdbView levelView,
        List<GdbView> supplementalViews,
        List<GdbPlacement> placements,
        Dictionary<uint, string>? modelPaths = null,
        Dictionary<uint, Node3D>? attachTo = null,
        Refii.Lighting.LightProbeField? probes = null,
        bool syntheticOnly = false)
    {
        // UNDER CLASSIC THE GUEST DRAWS THEM. A placed effect is simulated by the guest like any
        // other, and Engine_ClassicParticles draws every live group with the title's own shaders;
        // a modern copy beside it drew each effect twice. In the editor no guest runs, so there
        // is nothing to draw them but this.
        if (Refii.Models.Engine_ClassicMaterial.Active && Refii.Host.HostBridge.IsGuestUp)
        {
            return 0;
        }

        FxBank? bank = _bankOverrideForTesting ?? FableFx.SharedBank();
        if (bank is null)
        {
            return 0;
        }

        var views = new List<GdbView>(supplementalViews.Count + 1) { levelView };
        views.AddRange(supplementalViews);

        // ONE EFFECT PER (PLACEMENT, EFFECT, BONE) — not one per placement, and not a proximity
        // test.
        //
        // What has to be stopped is the same attachment being emitted twice. The placement list
        // carries more entries than the save has entities and some SHARE a record hash, so the
        // walk below reaches one entity's attachment once per placement that names it — 49 of
        // BWSSlums' 134 hits. A distance rule would catch those only by also throwing away any
        // SECOND instance of an effect near the first, and street lamps are closer together than
        // 2.5m: measured on BWSSlums, of the 177 non-one-shot placements a 2.5m rule keeps 94 and
        // silently eats 83.
        //
        // Keying on the placement alone is nearly as lossy in the other direction, because one
        // entity can legitimately state several effects on several bones: Bowerlake's
        // Waterfall_Medium is one placement carrying fxenv_water_fall_main_thin, _wide and
        // _wider on three different dummies, and across the shipped levels 47 placements state
        // 58 effects beyond their first. The bone is in the key because that is what makes two
        // attachments on one prop distinct rather than a repeat.
        //
        // The guest has no dedup rule at all. It registers an attacher per component and plays
        // them all, so co-located duplicates in the data are meant to be duplicates on screen.
        var emitted = new Dictionary<(uint Placement, uint Effect, string Dummy), Vector3>();
        int count = 0;
        int oneShotSkips = 0;
        int byAuthored = 0;
        int attached = 0;
        int missedBone = 0;
        int duplicate = 0;
        int duplicateElsewhere = 0;
        int displaced = 0;
        int lights = 0;
        int attractors = 0;
        int splines = 0;
        int orbitals = 0;

        // WHY a bone was missed, not just how many. "17 stated a bone we could not find" has two
        // completely different causes behind it — the placement resolved to no model at all, so
        // there was never a rig to search, or the rig is there and does not carry the node the
        // data named. The first is a placement defect and belongs to Engine_LevelProps; the
        // second is ours. One counter cannot say which, and guessing picks the wrong file to fix.
        var noModel = new List<string>();
        var noNode = new List<string>();

        bool dumpPlacements = OS.GetEnvironment("REFII_FX_DUMP").Length != 0;

        bool Emit(
            GdbPlacement placement,
            uint hash,
            out Vector3 origin,
            bool bypassAmbient = false,
            string dummy = "",
            Vector3 offset = default,
            bool orient = false)
        {
            // A placement with a live node is positioned BY that node, so the effect's transform
            // is purely local to it — composing the placement transform again underneath a parent
            // that already carries it would place the effect twice as far from the origin.
            Node3D? parent = null;
            attachTo?.TryGetValue(placement.Hash, out parent);

            Transform3D xform = parent is null
                ? Engine_LevelProps.BuildTransform(placement)
                : Transform3D.Identity;

            // ONTO THE BONE, not the prop's origin. A lamp's flame belongs at its head, and 78
            // of BWSSlums' 86 effects state a DummyObject saying so.
            Vector3 placedAt = xform.Origin;
            Vector3 boneAt = Vector3.Zero;
            string boneSource = "none";
            string dumpModel = "";

            if (dummy.Length != 0)
            {
                string? model = null;
                modelPaths?.TryGetValue(placement.Hash, out model);
                dumpModel = model ?? "";

                if (model is null || model.Length == 0)
                {
                    missedBone++;
                    if (noModel.Count < 12)
                    {
                        noModel.Add($"{placement.EntityName ?? "?"}[{dummy}]");
                    }
                }
                else if (TryDummyTransform(model, dummy, out Transform3D bone))
                {
                    xform *= bone;
                    boneAt = bone.Origin;
                    boneSource = "gmd";
                    attached++;
                }
                else if (Engine_LevelProps.CompanionPath(model) is { Length: > 0 } companion &&
                         TryDummyTransform(companion, dummy, out Transform3D companionBone))
                {
                    // A BUILDING IS TWO MESHES, AND THE DUMMY IS ON WHICHEVER ONE CARRIES IT.
                    //
                    // `exterior.mdl` and `interior.mdl` are one entity: Engine_LevelProps draws
                    // both, but `fxModelPaths` can only remember the one it resolved first, and
                    // for the Bowerstone townhouses that is the interior. The chimney is on the
                    // outside of the building. Measured on the user's bwsmarket run, this alone
                    // is 12 of the region's 21 missed bones — every
                    // `Prop.FX.Particle.FX_Chimney_Smoke_01.par` in the region, dropped to the
                    // building's origin instead of its roof.
                    //
                    // Asking the sibling is not a guess about geometry. The guest looks the
                    // attachment up through the ENTITY's render component, which owns the whole
                    // building; splitting that into two files is our storage detail, so
                    // searching both is what restores the entity's single attachment set.
                    xform *= companionBone;
                    boneAt = companionBone.Origin;
                    boneSource = "companion";
                    attached++;
                }
                else
                {
                    missedBone++;
                    if (noNode.Count < 12)
                    {
                        noNode.Add($"{model}[{dummy}]");
                    }
                }
            }

            // Into the simulation's axes. The placement basis and the gmd bone are both in the
            // mesh convention — a guest local (x, y, z) stored as (x, z, -y) (`ConvertZUpToYUp`,
            // `Engine_LevelProps.ToGodotBasis` negating its third column) — but
            // Engine_FxSystem holds a guest local as (x, z, y); the mesh basis unconverted
            // mirrors the effect along its own local Y. diag(1, 1, -1) on the right is the
            // mesh-from-sim change of basis, its own inverse. Before the offset, which is read in
            // the simulation's axes too (`ReadOffsetVector`).
            xform = new Transform3D(xform.Basis * SimFromMesh, xform.Origin);

            // THE OFFSET IS SUBTRACTED, IT GOES INNERMOST, AND IT DOES NOT NEED A BONE. All
            // three are in the binary, and each is easy to get wrong in a way that reads as a
            // bad scale rather than a sign, an ordering or a missing case.
            //
            // `sub_825EBBC8` stores the offset matrix TWICE on the attach record: as given at
            // +16, and `sub_82260FE0` of it at +80. That function dots row 3 against each basis
            // row (`vmsum3fp128 v10, v12, v0` at 0x82261008 and its two neighbours), negates the
            // three results and transposes — the affine inverse of a rigid transform.
            // `sub_825ED2A0` then composes with `addi r4, r31, 0x50` — +80, the INVERSE, not the
            // +16 original — and puts it LEFT of the attach point, which in the guest's
            // row-vector convention means innermost:
            //
            //     child = inv(T(offset)) x attachPointLocal x parentWorld
            //
            // Godot composes the other way round, so the same frame is the placement, then the
            // bone, then a translation by MINUS the offset.
            //
            // Outside the bone branch because attachPointLocal is IDENTITY when nothing names a
            // dummy, and the offset still applies — that is not a rare corner, it is every
            // stated offset in the shipped levels. Silver keys, silver books and Murgo's statues
            // all state OffsetFromDummy (0, 0, -0.25) with no DummyObject at all, so an offset
            // applied only alongside a bone is an offset never applied.
            if (offset != Vector3.Zero)
            {
                xform *= new Transform3D(Basis.Identity, -offset);
                displaced++;
            }

            // POSITION FROM THE DUMMY, BASIS FROM THE FLAG.
            //
            // `sub_821EC8A8` pushes `GetWorldPosition` to the particle system unconditionally and
            // then chooses the orientation: `GetWorldRotation` when
            // OrientParticleToAttachmentPoint is set, and a literal identity quaternion —
            // `vand(splat(1.0), (0,0,0,~0))` = (0,0,0,1) — when it is not. The shipped data says
            // not, 280 times against 2, and an unstated flag is false as well.
            //
            // Only for an ATTACHED effect. A plain CECParticleEmitter standing at its entity's
            // origin never reaches that branch, so it keeps the placement's own rotation.
            if (dummy.Length != 0 && !orient)
            {
                xform = new Transform3D(Basis.Identity, xform.Origin);
            }

            // NO SCALE. The guest positions an effect root through `sub_832325A8` and orients it
            // through `sub_83231C40` → `sub_83228D78`, which rebuilds the matrix with a scale of
            // `splat(flt_82099490)` = 1.0 (0x83228dac..0x83228dcc) — the placement's scale moves
            // the dummy, never the effect.
            xform = new Transform3D(xform.Basis.Orthonormalized(), xform.Origin);

            origin = xform.Origin;

            if (dumpPlacements && dummy.Length != 0)
            {
                GD.Print(
                    $"[fx-dump] {placement.EntityName ?? "?"} rec={placement.Hash:X8} " +
                    $"maps={modelPaths?.Count ?? -1} {hash:X8} dummy='{dummy}' " +
                    $"bone={boneSource} placed={placedAt} boneLocal={boneAt} " +
                    $"final={xform.Origin} parented={(parent is not null)} " +
                    $"model={dumpModel}");
            }

            if (!bypassAmbient && !IsAmbientEffect(bank, hash))
            {
                oneShotSkips++;
                return false;
            }

            var key = (placement.Hash, hash, dummy);
            if (!emitted.TryAdd(key, xform.Origin))
            {
                duplicate++;

                // A REPEAT AT A DIFFERENT PLACE IS NOT A REPEAT. The placement list carries more
                // entries than the save has entities (1410 against 1345 in BWSSlums) and some
                // share a record hash, so keying on the hash alone quietly collapses two effects
                // into one wherever those twins stand apart. Counting the ones that disagree is
                // what says whether the key is the right key — a silent collapse and a correct
                // dedup produce exactly the same spawn count.
                if (emitted[key].DistanceSquaredTo(xform.Origin) > 0.01f)
                {
                    duplicateElsewhere++;
                }

                return false;
            }

            var fx = new FableFx
            {
                Name = $"Fx_{hash:X8}_{count}",
                EffectName = $"FX_{hash:X8}",
                Transform = xform,
            };
            if (probes is not null)
            {
                Transform3D levelXform = xform;
                for (Node? walk = parent; walk is Node3D n3 && walk != container; walk = walk.GetParent()) levelXform = n3.Transform * levelXform;
                Refii.Lighting.Engine_LevelBakedLighting.StoreProbeSample(fx, levelXform.Origin, probes);
            }
            (parent ?? container).AddChild(fx);
            count++;
            FxBank.FxComposition parts = bank.Composition(hash);
            lights += parts.Lights;
            attractors += parts.Attractors;
            splines += parts.Splines;
            orbitals += parts.Orbitals;
            return true;
        }

        // ONE ROUTE, because across the whole shipped game there has only ever been one.
        //
        // There were three. Measured over all 83 levels by this very function, the authored walk
        // below answers 5614 effects and the other two answer NOTHING — not few, zero:
        //
        //     authored=5614   byName=0   byBinding=0
        //
        // The name route asked whether a placement's own ENTITY name is a bank entry. The binding
        // route built a map by walking every record of every view twice per level, following
        // `ParticleEffect` chains — which is the field this file's own header documents as the
        // BREAKABLE'S DESTRUCTION EFFECT, the one that must never be resolved as ambience. So it
        // was a second, contradictory reading of the data that cost a full double walk of every
        // GDB record at every level load and placed nothing.
        //
        // Both are gone. The guest has one contract for standing ambience — a component asks for
        // a property named after its own class — and <see cref="HuntFxAttachments"/> is it.
        foreach (GdbPlacement placement in placements)
        {
            if (syntheticOnly && !IsLampOnStickPlacement(placement))
            {
                continue;
            }

            // NO PLACEMENT IS EXEMPT. The silver keys used to be skipped here and handed a
            // hardcoded `FX_Key_Silver_01` by Engine_LevelProps instead — a host invention that
            // the data made redundant: the ordinary walk resolves that same effect for them, and
            // going around it cost the keys the `OffsetFromDummy` their data states and the
            // orientation rule every other attachment obeys.
            if (syntheticOnly && HuntFxAttachments(placement.Hash, views).Count != 0)
            {
                continue;
            }

            foreach (FxAttachment attachment in ResolvePlacementAttachments(placement, views))
            {
                if (attachment.Effect == 0 || attachment.Effect == Null ||
                    bank.FindByHash(attachment.Effect) is null)
                {
                    continue;
                }

                if (Emit(
                        placement, attachment.Effect, out _, false,
                        attachment.Dummy, attachment.Offset, attachment.Orient))
                {
                    byAuthored++;
                }
            }
        }

        if (count != 0 || oneShotSkips != 0)
        {
            // Keep the breakdown. It is the measurement that says whether the data-derived
            // route actually reaches the effects, which is the question the deleted keyword
            // table was hiding.
            GD.Print(
                $"[fx] level spawn: {count} ({byAuthored} authored; " +
                $"{attached} on a bone ({displaced} offset from it), " +
                $"{missedBone} stated a bone we could not find; " +
                $"{oneShotSkips} one-shot placement(s) skipped, {duplicate} already emitted " +
                $"({duplicateElsewhere} of them somewhere else)); " +
                $"{lights} cast a light, {attractors} pull with an attractor, " +
                $"{splines} ride a spline, {orbitals} orbit");

            if (noModel.Count != 0)
            {
                GD.Print($"[fx]     no model for: {string.Join(", ", noModel)}");
            }

            if (noNode.Count != 0)
            {
                GD.Print($"[fx]     model carries no such node: {string.Join(", ", noNode)}");
            }
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
    /// FNV-1 of "OrientParticleToAttachmentPoint" — whether the effect takes the dummy's
    /// ROTATION, or only its position.
    ///
    /// FALSE IS THE ANSWER ALMOST EVERYWHERE, and false is also the unstated default:
    /// `sub_82767C70` reads it through `sub_82310290`, a get-bool that returns false when the
    /// property is absent. Across the shipped levels it is stated false 280 times and true twice.
    ///
    /// `sub_821EC8A8` is the branch. With the flag SET it asks the effect entity's transform
    /// component for slot 68 `GetWorldRotation` and hands that quaternion to
    /// `ParticleSystem::SetOrientation`. With it CLEAR it builds a literal identity quaternion —
    /// `vand(splat(1.0), unk_82099160)` where that mask is (0,0,0,~0), giving (0,0,0,1) — and
    /// sends THAT instead. Either way `GetWorldPosition` is fetched and pushed unconditionally,
    /// so the effect always sits ON the dummy; only its basis is in question.
    ///
    /// The designer-facing names are in the animation-event parser `sub_82765890`, which spells
    /// the two branches `NOT_ORIENT` and `ORIENT_TO_DUMMY`.
    ///
    /// This host composed the bone's full rotation onto every attachment, so a flame hanging from
    /// a rotated dummy leaned with it instead of standing up.
    /// </summary>
    public const uint OrientParticleToAttachmentPoint = 0xF825B96A;

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

    /// <summary>One effect an entity states, with where on the entity it hangs.</summary>
    /// <param name="Effect">
    /// The bank key, ALREADY RE-HASHED FROM THE RESOLVED TEXT rather than taken as stored.
    ///
    /// The bank is keyed on the LOWERCASED name and the GDB stores the hash of the string AS
    /// WRITTEN, so "fxcre_shadow_hobbe_tar" matches by luck because it is already lower and
    /// "FXCRE_Hollowman_Wisp_Trail" does not. The guest re-hashes too, resolving the stored hash
    /// to a name through GDBNames_ResolveHash before looking it up.
    /// </param>
    /// <param name="Dummy">
    /// The <c>DummyObject</c> bone the effect attaches to, empty when it is a plain
    /// CECParticleEmitter sitting at its entity's origin.
    ///
    /// `sub_82767C70` reads it as a TYPE 4 name — `sub_821D4D90` resolves the hash back to a
    /// string through `GDBNames_ResolveHash` — and stores it at the attacher record's +0. The
    /// models name the matching bone literally: BS_Lights_Town, ESA_HangingLantern and
    /// OB_MusicBox all carry an `FX_Particle_DummyObject`. Until it is honoured, a lamp's flame
    /// is placed at the lamp's ORIGIN rather than at its head.
    /// </param>
    /// <param name="Offset">
    /// <c>OffsetFromDummy</c>, a translation in the BONE's space that is SUBTRACTED, not added.
    ///
    /// `sub_827661B0` walks the attacher's list at stride 32 and loads the offset as a vector
    /// from the record's +16 (`lvx128 v1, r29, r18` with r18 = 16), which `sub_82B4FD98` turns
    /// into a matrix whose row 3 is that vector. `sub_825EBBC8` then stores the matrix twice on
    /// the attach record — as given at +16, and `sub_82260FE0` of it, the affine inverse, at
    /// +80. That inverse is what `sub_825ED2A0` composes with — `addi r4, r31, 0x50`, not
    /// `0x10` — and it goes LEFT of the attach point, so in row-vector terms the frame is
    ///
    ///     child = inv(translate(Offset)) x attachPointLocal x parentWorld
    ///
    /// Getting the sign backwards puts a flame as far the wrong side of the lamp as it should
    /// have been the right side, which reads as a plausible mis-scale rather than a sign error.
    ///
    /// It is a REFERENCE to a vector record, not three inline floats, and that record states
    /// only its non-zero components — see <see cref="ReadOffsetVector"/>, which also swaps it out
    /// of the file's Z-up axes into Godot's, so what arrives here is already a Godot vector.
    /// </param>
    /// <param name="Orient">
    /// <c>OrientParticleToAttachmentPoint</c> — see the constant of that name. False, the default,
    /// means the effect takes the attachment point's POSITION and an IDENTITY basis.
    /// </param>
    public readonly record struct FxAttachment(
        uint Effect, string Dummy, Vector3 Offset, bool Orient);

    /// <summary>
    /// EVERY effect an entity states, not just the nearest one.
    ///
    /// `sub_827661B0` is a LOOP: it walks the attacher's list from `*(a2+4)` to `*(a2+8)` at
    /// stride 32 and calls the spawner `sub_8238B860` once per entry, each with that entry's own
    /// offset vector from +16 and its own flags from +13/+14/+15. An entity with two attachments
    /// gets two effects — which the shipped data asks for plainly: the dummy census carries
    /// `Prop.FX.Particle.Effect_1.` AND `Prop.FX.Particle.Effect_2.`, and OB_MusicBox's rig
    /// carries `FX_Particle_DummyObject` beside `FX_Particle_DummyObject1` for them to hang from.
    ///
    /// Returning one hash could never express that, so a musicbox got its first effect and
    /// silently dropped the second.
    ///
    /// WHAT STILL RETURNS ONE is inheritance. `GDBEntry_FindPropWalkParents` (0x821E5A08) stops
    /// at the first entry in the parent chain that carries the property, so a child overriding
    /// its base's emitter REPLACES it — collecting along parent edges as well would give every
    /// overriding entity both its own effect and the one it was overriding. Multiplicity comes
    /// from LIST MEMBERSHIP and from nothing else, which is why <c>parent</c> is resolved through
    /// the field lookup below rather than being walked as an edge.
    /// </summary>
    /// <remarks>
    /// THE SHAPE IS THE GUEST'S, and it is visible in the data exactly as IDA described it.
    /// One real chain out of Bloodstone, printed by <c>FxCheck</c>:
    ///
    ///     ParticleEmitter (ResourceReference) 0xA4FCA3BC        &lt;- on the placement
    ///       ParticleEmitterComponent (Reference) 0x86FE8402     &lt;- sub_821DA3B0's component hop
    ///         ParticleEmitter (String) "fxcre_shadow_hobbe_tar" &lt;- the answer
    ///         parent (Reference) ParticleEmitterComponent       &lt;- base, states the empty hash
    ///
    /// THE TYPE DECIDES WHAT THE VALUE IS, and that one field name means both. As a String it is
    /// a name hash; as a Reference it is a GDB ENTRY hash and the effect is named on the entry it
    /// points at. Taking the value whichever it was meant every reference-typed field became an
    /// entry hash asked of a name table — 254 of Bloodstone's misses, none of them a data
    /// problem. So the String form ends the walk and the Reference form is an edge.
    ///
    /// THE BASE ENTRY STATES THE EMPTY HASH. Every ParticleEmitterComponent inherits from one
    /// that carries `ParticleEmitter = 0x811C9DC5`, the bare FNV-1 basis — the "nothing here"
    /// value. Treating that as a name gives every emitter in the game the same effect, so
    /// <see cref="Null"/> is rejected wherever a value is read.
    ///
    /// THE EDGES ARE A FIXED LIST, NOT A GRAPH SEARCH, and that is the whole difference between
    /// this and a walk that "finds more effects".
    ///
    /// A breadth-first walk over every reference does find effects — it found 481 in Bloodstone
    /// — but they are the wrong ones. It leaves the entity it started on, wanders through shared
    /// base entries, and comes back with whatever it bumps into: BWSSlums loaded with 67
    /// permanent `fxenv_windowbreak_small_green`, 27 `FXWPN_Breakable_China_01` and 12
    /// `FXWPN_Crate_Strike_01` going off in the street, while the torches and lanterns it walked
    /// straight past resolved to nothing.
    ///
    /// The guest never searches. Each component asks for a property named after its own class
    /// and reads what is on the other end, so the reachable set is exactly:
    ///
    ///     ParticleEmitterComponent  -> [parent*] -> ParticleEmitter (String)
    ///     ParticleAttacherComponent -> ParticlesToAttach -> &lt;entry&gt;* -> ParticleEmitter
    ///
    /// Every other Particle-named property in the data is an EVENT effect and belongs to
    /// something happening, not to the level standing still: ParticleEffect (breakable
    /// destruction), ImpactParticleEffect, LeftoverParticleEffect, OpenDoorParticleFX,
    /// WeaponParticle, ClothingParticle, OnFireParticleFXIndex, ThemeParticleFX,
    /// FirefliesParticleEffect. Adding any of them here puts a permanent explosion in a street.
    /// </remarks>
    public static List<FxAttachment> HuntFxAttachments(uint root, List<GdbView> views)
    {
        var found = new List<FxAttachment>();
        var distinct = new HashSet<FxAttachment>();
        var seen = new HashSet<uint> { root };
        var queue = new Queue<(uint Hash, int Depth, bool InList, string Dummy, Vector3 Offset,
            bool Orient)>();
        queue.Enqueue((root, 0, false, string.Empty, Vector3.Zero, false));
        int budget = 512;

        while (queue.Count != 0 && budget-- > 0)
        {
            (uint hash, int depth, bool inList, string carried, Vector3 carriedOffset,
                bool carriedOrient) = queue.Dequeue();

            foreach (GdbView view in views)
            {
                if (!view.TryLookup(hash, out int record))
                {
                    continue;
                }

                List<GdbField> fields = view.Fields(record);

                // THIS ENTRY'S OWN bone and displacement — its own statement, else its nearest
                // ancestor's, else the one the entry that led here stated. Own-first matters
                // once more than one attachment is in play: carrying a single dummy forward
                // across the whole walk, which is what a single pair of out-parameters forced,
                // gave the second attachment the first's bone.
                string dummy = Inherited(views, hash, DummyObject, GdbFieldType.String) is
                    { Text.Length: > 0 } statedDummy ? statedDummy.Text : carried;

                Vector3 offset = carriedOffset;
                if (Inherited(views, hash, OffsetFromDummy, GdbFieldType.Reference) is
                    { Raw: not 0 and not Null } statedOffset)
                {
                    offset = ReadOffsetVector(views, statedOffset.Raw);
                }

                // Own statement, else inherited, else FALSE — which is the guest's own default
                // for an absent bool, not a host choice.
                bool orient = carriedOrient;
                if (Inherited(views, hash, OrientParticleToAttachmentPoint, GdbFieldType.Bool)
                    is { } statedOrient)
                {
                    orient = statedOrient.AsBool;
                }

                // ONE EMITTER PER ENTRY, taken through the parent walk rather than by queueing
                // `parent` as an edge. Both reach a base entry's effect; only this one lets a
                // child REPLACE it. Walking parents as an edge and collecting everything found
                // would hand an overriding entity its own effect plus the one it overrode.
                if (Inherited(views, hash, ParticleEmitter, GdbFieldType.String) is
                    { Raw: not 0 and not Null } emitter)
                {
                    uint named = emitter.Text.Length != 0
                        ? FxBank.Fnv1Lower(emitter.Text)
                        : emitter.Raw;

                    // THE SAME ATTACHMENT REACHED TWICE IS ONE ATTACHMENT. An entity's
                    // ParticleEmitter entryRef and its ParticleEmitterComponent are different
                    // edges that routinely land on entries inheriting the same emitter from the
                    // same base, so the walk sees it once per route — 49 of BWSSlums' 134 hits.
                    // Two attachments are distinct when they differ in what plays or where it
                    // hangs, and identical otherwise, whichever way the walk got there.
                    var attachment = new FxAttachment(named, dummy, offset, orient);
                    if (named != 0 && named != Null && distinct.Add(attachment))
                    {
                        found.Add(attachment);
                    }
                }

                if (depth < 6)
                {
                    // Every hop is inherited too — `sub_821DA3B0` asks for a property named after
                    // the component's own class and a placement can carry none of its own — so
                    // these are resolved through the parent chain rather than read locally.
                    //
                    // ParticleEmitter is in the list because it has TWO live forms and this is
                    // the other one: as a type-6 entryRef `sub_82767C70` resolves it to another
                    // GDB entry, and that entry is where ParticleEmitterComponent lives. It is
                    // the FIRST hop of the chain FxCheck prints, so dropping it does not lose an
                    // effect here and there — it loses the ordinary route, and BWSSlums falls
                    // from 83 resolved to 17.
                    foreach (uint edge in new[]
                             { ParticleEmitter, ParticleEmitterComponent, ParticleAttacherComponent })
                    {
                        if (Inherited(views, hash, edge, GdbFieldType.Reference) is
                                { Raw: not 0 and not Null } hop &&
                            seen.Add(hop.Raw))
                        {
                            queue.Enqueue((hop.Raw, depth + 1, false, dummy, offset, orient));
                        }
                    }

                    // The list edges stay LOCAL, because a list entry states its attachments as
                    // several fields sharing one hash and the parent walk returns only the
                    // first. Inside the list any reference is followed — bounded, because the
                    // subtree is only ever entered through the attacher.
                    foreach (GdbField f in fields)
                    {
                        if (!f.IsReference || f.Raw == 0 || f.Raw == Null)
                        {
                            continue;
                        }

                        bool list = f.Hash is ParticlesToAttach or AttachedParticle;
                        if ((!list && !inList) || !seen.Add(f.Raw))
                        {
                            continue;
                        }

                        queue.Enqueue((f.Raw, depth + 1, true, dummy, offset, orient));
                    }
                }

                break;
            }
        }

        return found;
    }

    /// <summary>
    /// A property as <c>GDBEntry_FindPropWalkParents</c> (0x821E5A08) reads it: the entry's own
    /// statement, else the nearest ancestor that makes one, else nothing.
    /// </summary>
    /// <remarks>
    /// THE CHAIN CROSSES FILES, which is why this walks the views itself instead of calling
    /// <c>GdbView.TryFindFieldOwner</c>. That helper resolves `parent` with its own
    /// <c>TryLookup</c>, so it stops dead the moment an entry inherits from one in another
    /// .gdb — and a level's placements inherit from shared templates almost by definition.
    /// Measured on BWSSlums: single-view inheritance resolves 17 placements where the walk
    /// across all views resolves 83.
    /// </remarks>
    /// <remarks>
    /// PUBLIC because the light walk needs the same read. Placed lights arrive through the same
    /// chain as emitters and inherit from the same cross-file templates, so
    /// <see cref="Refii.Lighting.Engine_LevelLights"/> resolves its component through this rather
    /// than growing a second parent walk that would drift from this one.
    /// </remarks>
    public static GdbField? Inherited(
        List<GdbView> views, uint entry, uint fieldHash, GdbFieldType type)
    {
        uint current = entry;
        var path = new HashSet<uint>();

        for (int depth = 0; depth < 64 && current != 0 && current != Null && path.Add(current);
             depth++)
        {
            GdbView? owner = null;
            int record = -1;
            foreach (GdbView view in views)
            {
                if (view.TryLookup(current, out record))
                {
                    owner = view;
                    break;
                }
            }

            if (owner is null)
            {
                return null;
            }

            if (owner.TryFindLocal(record, fieldHash, (int)type, out GdbField found))
            {
                return found;
            }

            // Reference and ResourceReference are the same edge as far as the walk is concerned
            // — the data uses both for the component hop — so a Reference miss retries as one.
            if (type == GdbFieldType.Reference &&
                owner.TryFindLocal(
                    record, fieldHash, (int)GdbFieldType.ResourceReference, out GdbField resource))
            {
                return resource;
            }

            if (!owner.TryFindLocal(record, GdbView.HashParent, (int)GdbFieldType.Reference,
                    out GdbField parent) || parent.Raw == 0)
            {
                return null;
            }

            current = parent.Raw;
        }

        return null;
    }

    /// <summary>
    /// The vector an <c>OffsetFromDummy</c> reference points at — READ ONE COMPONENT AT A TIME,
    /// because the record states only the components that are non-zero.
    ///
    /// Measured across BWSSlums and its supplementals: of the thirteen stated offsets, one names
    /// all three of VecX/VecY/VecZ, four name two, and the rest name a single axis and inherit
    /// the others from `parent`. Asking for the three together — which is what
    /// <c>GdbView.TryReadVec3Record</c> does, and what every other vector in the level data
    /// wants — fails on twelve of the thirteen and reads as "no offset stated", which is
    /// indistinguishable from the field being absent and silently leaves the effect where it was.
    ///
    /// So each axis goes through the parent walk on its own, and an axis nobody states is zero:
    /// a base entry that carries no component is stating no displacement, not an error.
    ///
    /// RETURNED IN GODOT'S AXES, not the file's. A VecX/VecY/VecZ record is game space, which is
    /// Z-UP — the same triple <see cref="Engine_LevelProps.BuildTransform"/> turns into a Godot
    /// position as <c>(X, Z, Y)</c> and <c>Engine_GdbPlacements</c> reads every placement through.
    /// This was the one vector in the level data handed to Godot unswapped, and the shape of the
    /// data hid it: the offsets the shipped levels state are overwhelmingly a single axis, and
    /// the commonest of them is the silver keys', books' and Murgo's statues' <c>(0, 0, -0.25)</c>
    /// — a quarter metre DOWN from the dummy. Read as a Godot vector that is a quarter metre
    /// along -Z, which is HORIZONTAL: the effect stays at the right height, slides sideways, and
    /// reads as a bad attachment rather than a bad basis.
    /// </summary>
    private static Vector3 ReadOffsetVector(List<GdbView> views, uint recordHash)
    {
        foreach (GdbView view in views)
        {
            if (!view.TryLookup(recordHash, out int index))
            {
                continue;
            }

            return new Vector3(
                OffsetComponent(view, index, GdbView.HashVecX),
                OffsetComponent(view, index, GdbView.HashVecZ),
                OffsetComponent(view, index, GdbView.HashVecY));
        }

        return Vector3.Zero;
    }

    private static float OffsetComponent(GdbView view, int index, uint hash) =>
        view.TryFindFieldOwner(index, hash, (int)GdbFieldType.Float, out GdbField found, out _) &&
        float.IsFinite(found.AsFloat)
            ? found.AsFloat
            : 0f;

    /// <summary>
    /// The rest transform of a model's named dummy, in the model's own space.
    ///
    /// TWO TABLES HOLD DUMMIES and the stated name can be in either — the <c>.mdl.gmd</c>
    /// sidecar's nodes and attachments, and the <c>.mdl</c>'s own bones. The sidecar is asked
    /// first because it is the one the guest searches and the only one carrying the dotted names
    /// the data states.
    ///
    /// Walks the parent chain because a dummy is authored as a child — on the lamps
    /// `FX_Particle_DummyObject` hangs off bone 4, not off the root — so its own local rest is
    /// only part of the answer.
    ///
    /// These are STATIC props. The guest recomputes the attachment every frame from the live
    /// skeleton (`sub_825ED2A0`), which matters for a character carrying a torch; for a lamp
    /// bolted to a wall the rest pose is that same answer, evaluated once.
    /// </summary>
    /// <remarks>
    /// PUBLIC for the light walk, which hangs its lights on the same rigs through the same stated
    /// names — <c>Prop.Layout.Light</c> resolves to <c>Layout_Light_DummyObject</c>, which sits
    /// beside the <c>FX_Particle_DummyObject</c> this was written for. One attachment table, one
    /// lookup.
    /// </remarks>
    public static bool TryDummyTransform(string modelPath, string dummy, out Transform3D bone)
    {
        bone = Transform3D.Identity;
        if (dummy.Length == 0)
        {
            return false;
        }

        // THE .gmd FIRST, because that is the table the guest searches and it is the only one
        // that holds the stated name. The .mdl's bone table and the .gmd's are not the same set:
        // ESA_HangingLantern's bones are `FX_Particle_DummyObject` and `Layout_Light_DummyObject`,
        // while `Prop.FX.Particle.Effect_1.FXENV_Lamp_Flame_01.par` — the name its data actually
        // states — is an ATTACHMENT in the sidecar, listed after the nodes.
        //
        // Searching only the bones misses it, then misses the derived `FX_Particle_DummyObject1`
        // too because that node does not exist, and hangs the flame on the lantern's origin. Nine
        // of BWSSlums' eighteen misplaced effects were this one model.
        // Parsed outside the lock, published inside it. Two threads racing the same model
        // costs one redundant parse; a dictionary resized by both costs the next lookup.
        bool haveGmd;
        Engine_GmdSkeleton? gmd;
        lock (GmdCache)
        {
            haveGmd = GmdCache.TryGetValue(modelPath, out gmd);
        }

        if (!haveGmd)
        {
            gmd = Engine_GmdSkeleton.TryParse(
                Engine_BnkMeshLoader.ReadBytes(modelPath + ".gmd"));
            lock (GmdCache)
            {
                GmdCache[modelPath] = gmd;
            }
        }

        if (gmd is not null && gmd.TryResolve(dummy, out bone))
        {
            return true;
        }

        bool haveRig;
        Engine_MdlRig? rig;
        lock (RigCache)
        {
            haveRig = RigCache.TryGetValue(modelPath, out rig);
        }

        if (!haveRig)
        {
            var mesh = ResourceLoader.Load<Mesh>(
                Engine_BnkMeshLoader.PathPrefix + modelPath);
            rig = Engine_MdlRig.TryFrom(mesh);
            lock (RigCache)
            {
                RigCache[modelPath] = rig;
            }
        }

        if (rig is not { } r)
        {
            return false;
        }

        // THE MODEL'S OWN BONES, for the models whose sidecar is absent or unreadable. The
        // stated string is still tried first here for the same reason as above: it is the
        // guest's key, and `Layout_Light_DummyObject` and `Prop.Layout.Light` are both real
        // spellings of the same attachment on the same rig.
        int index = Array.FindIndex(
            r.Names, n => n.Equals(dummy, StringComparison.OrdinalIgnoreCase));

        // The authoring convention, as a last resort. It is a HOST INVENTION — the guest never
        // rewrites the name — so it only runs once both real tables have said no.
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

    private static readonly Dictionary<string, Engine_GmdSkeleton?> GmdCache = new();

    private static FxBank? _bankOverrideForTesting;

    /// <summary>Injects an FxBank override for testing FX spawning without loading files from disk.</summary>
    public static void InjectBankForTesting(FxBank? bank) => _bankOverrideForTesting = bank;

    /// <summary>Injects a GMD skeleton for testing dummy attachment resolution without loading files from disk.</summary>
    public static void InjectGmdForTesting(string modelPath, Engine_GmdSkeleton? skeleton)
    {
        lock (GmdCache)
        {
            GmdCache[modelPath] = skeleton;
        }
    }

    /// <summary>Drops the per-model rig and skeleton lookups; keyed by path, so region-scoped.</summary>
    public static void ClearCaches()
    {
        _bankOverrideForTesting = null;
        lock (RigCache)
        {
            RigCache.Clear();
        }

        lock (GmdCache)
        {
            GmdCache.Clear();
        }
    }
}
