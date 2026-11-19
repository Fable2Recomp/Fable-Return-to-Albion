namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using GdUnit4;
using Godot;
using Refii.Fx;
using Refii.Game;
using static GdUnit4.Assertions;

/// <summary>
/// Regression tests for character/weapon FX lifecycle, placement, emission gating, and spline guards:
/// 1. Live guest effects default to <see cref="GameFx.LiveFxDefaultLoop"/> (<c>false</c>) so
///    one-shot splashes and muzzle flashes do not replay at 1.5s while waiting on retirement.
/// 2. Continuous guest emitters (<c>UseLife = false</c>) and beams halt spawning immediately when
///    <see cref="Engine_FxSystem.SetEmitting"/> is set to <c>false</c>.
/// 3. Finite FX lights (<c>UseLife = true</c>) extinguish cleanly once <c>Age &gt; LifetimeFrames</c>.
/// 4. <see cref="GameFx.ShouldRecycleTracked"/> detects effect changes, disjoint handle reuse,
///    and <c>!WasEmitting &amp;&amp; AnyEmitting</c> re-triggers while keeping live continuations.
/// 5. <see cref="GameFx.TryLatchLocalSpline"/> and <see cref="GameEntities.MapAttachedTargetTransform"/>
///    preserve valid local offsets and reject singular target transforms cleanly.
/// </summary>
[TestSuite]
public class CharacterFxPlacementAndLoopTests
{
    private static Engine_FxSystem BuildSystem(bool useLife, int lifetimeFrames, float initialNum, float spawnRate)
    {
        var bank = new FxBank { Ok = true };
        var em = new FxEmitter
        {
            TypeId = 0,
            HasMotion = true,
            MaxActive = 32f,
            InitialNum = initialNum,
            AttachToRoot = true,
            LifetimeFrames = lifetimeFrames,
            UseLife = useLife,
        };
        em.Vec1.DefaultValue = new[] { 1f, 1f, 1f };
        if (spawnRate > 0f)
        {
            em.Generic[2].DefaultValue = spawnRate;
        }

        var sysDef = new FxSystemDef { Mode = 1, ChildCount = 2, LastStartFrames = 10 };
        sysDef.Visuals.Add(new FxVisual
        {
            HasTexture = true,
            Texture = "splash.tex",
            HasSize = true,
            SizeX = 0.25f,
            SizeY = 0.25f,
        });

        var eff = new FxEffect
        {
            Name = "test_character_fx",
            NameHash = FxBank.Fnv1Lower("test_character_fx"),
            UpdateFrequency = 1,
        };
        eff.Parts.Add(new FxEffectPart(0, 0, 1, 0, 0));

        bank.Emitters.Add(em);
        bank.Systems.Add(sysDef);
        bank.Effects.Add(eff);
        bank.Reindex();

        var fx = new Engine_FxSystem();
        var placement = new FxPlacement { EffectName = "test_character_fx" };
        fx.Build(bank, new[] { placement });
        AssertThat(placement.Resolved).IsTrue();
        return fx;
    }

    [TestCase]
    public void OneShotFxDoesNotReplayPast1500msWhenLoopIsFalse()
    {
        Engine_FxSystem nonLooping = BuildSystem(useLife: true, lifetimeFrames: 5, initialNum: 4f, spawnRate: 0f);
        nonLooping.SetLoop(GameFx.LiveFxDefaultLoop);

        // Step 15 frames (0.5s at 30 Hz): initial burst spawns and dies out cleanly.
        for (int i = 0; i < 15; i++)
        {
            nonLooping.Update(1f / 30f, Transform3D.Identity);
        }

        int spawnedInitially = nonLooping.TotalSpawned;
        AssertThat(spawnedInitially).IsEqual(4);
        AssertThat(nonLooping.AliveCount()).IsEqual(0);

        // Step another 75 frames (total 3.0s, well past the 1.5s preview loop threshold).
        for (int i = 0; i < 75; i++)
        {
            nonLooping.Update(1f / 30f, Transform3D.Identity);
        }

        AssertThat(nonLooping.TotalSpawned).IsEqual(spawnedInitially);
        AssertThat(nonLooping.AliveCount()).IsEqual(0);

        // Control case: with Loop = true (preview mode), Engine_FxSystem replays after 1.5s.
        Engine_FxSystem previewLooping = BuildSystem(useLife: true, lifetimeFrames: 5, initialNum: 4f, spawnRate: 0f);
        previewLooping.SetLoop(true);
        for (int i = 0; i < 90; i++)
        {
            previewLooping.Update(1f / 30f, Transform3D.Identity);
        }

        AssertThat(previewLooping.TotalSpawned).IsGreater(spawnedInitially);
    }

    [TestCase]
    public void ContinuousFxStillEmitsPast1500msWhenLoopIsFalse()
    {
        Engine_FxSystem continuous = BuildSystem(useLife: false, lifetimeFrames: 10, initialNum: 0f, spawnRate: 2f);
        continuous.SetLoop(GameFx.LiveFxDefaultLoop);

        for (int i = 0; i < 45; i++)
        {
            continuous.Update(1f / 30f, Transform3D.Identity);
        }

        int spawnedAt1500ms = continuous.TotalSpawned;
        AssertThat(spawnedAt1500ms).IsGreater(0);
        AssertThat(continuous.AliveCount()).IsGreater(0);

        for (int i = 0; i < 45; i++)
        {
            continuous.Update(1f / 30f, Transform3D.Identity);
        }

        AssertThat(continuous.TotalSpawned).IsGreater(spawnedAt1500ms);
        AssertThat(continuous.AliveCount()).IsGreater(0);
    }

    [TestCase]
    public void MapAttachedTargetTransformPreservesFootAndWaterOffset()
    {
        var statedBasis = new Basis(Vector3.Up, 0.3f);
        var drawnBasis = new Basis(Vector3.Up, 0.8f);
        var targetStated = new Transform3D(statedBasis, new Vector3(10f, 20f, 5f));
        var targetDrawn = new Transform3D(drawnBasis, new Vector3(12f, 20.5f, 7f));

        // Splash / footstep placed 0.9m below the character's stated origin (water level / feet).
        var effectStated = new Transform3D(statedBasis, new Vector3(10f, 19.1f, 5f));

        Transform3D placed = GameEntities.MapAttachedTargetTransform(targetStated, targetDrawn, effectStated);

        AssertThat(placed.Origin.X).IsEqualApprox(12f, 1e-4f);
        AssertThat(placed.Origin.Y).IsEqualApprox(19.6f, 1e-4f);
        AssertThat(placed.Origin.Z).IsEqualApprox(7f, 1e-4f);
        AssertThat(placed.Basis.IsEqualApprox(drawnBasis)).IsTrue();
    }

    [TestCase]
    public void TrackAttachedTargetClearsWhenLiveOwnerDetachesButPersistsWhenOwnerDestroyed()
    {
        var attachedByKey = new Dictionary<uint, uint>();
        const uint rootKey = 0x42C7E5F0u;
        const uint owner = 0x42774E30u;
        const uint hero = 0x42BC7D10u;

        // 1. Effect latches attached target when guest states it.
        GameFx.TrackAttachedTarget(attachedByKey, rootKey, owner, hero);
        AssertThat(attachedByKey.TryGetValue(rootKey, out uint latched)).IsTrue();
        AssertThat(latched).IsEqual(hero);

        // 2. Temporary emitter entity destroyed (owner == 0, attached == 0): keep latched target
        // so particles can finish fading out against the host skeleton/weapon.
        GameFx.TrackAttachedTarget(attachedByKey, rootKey, 0u, 0u);
        AssertThat(attachedByKey.TryGetValue(rootKey, out uint persisted)).IsTrue();
        AssertThat(persisted).IsEqual(hero);

        // 3. Live emitter component explicitly states detached (owner != 0, attached == 0):
        // clear the stale attachment so world-space effects stop following the character.
        GameFx.TrackAttachedTarget(attachedByKey, rootKey, owner, 0u);
        AssertThat(attachedByKey.ContainsKey(rootKey)).IsFalse();
    }

    [TestCase]
    public void LiveFxDefaultsAndRetireGraceAreConfiguredForOneShotAndTicks()
    {
        AssertThat(GameFx.LiveFxDefaultLoop).IsFalse();
        AssertThat(GameFx.RetireGraceTicks).IsGreaterEqual(15.0);
    }

    [TestCase]
    public void SetEmittingFalseHaltsContinuousEmittersAndBeamsWhileDrainingExistingParticles()
    {
        // 1. Continuous billboard emitter stops spawning immediately when SetEmitting(false) while existing particles finish.
        Engine_FxSystem continuous = BuildSystem(useLife: false, lifetimeFrames: 0, initialNum: 0f, spawnRate: 1f);
        continuous.SetLoop(false);
        continuous.SetEmitting(true);

        for (int i = 0; i < 15; i++)
        {
            continuous.Update(1f / 30f, Transform3D.Identity);
        }

        int spawnedBeforeStop = continuous.TotalSpawned;
        AssertThat(spawnedBeforeStop).IsGreater(0);
        AssertThat(continuous.AliveCount()).IsGreater(0);

        continuous.SetEmitting(false);
        for (int i = 0; i < 5; i++)
        {
            continuous.Update(1f / 30f, Transform3D.Identity);
        }

        AssertThat(continuous.TotalSpawned).IsEqual(spawnedBeforeStop);
        AssertThat(continuous.AliveCount()).IsGreater(0);

        // Drain past LastStartFrames (10) -> all existing particles retire cleanly with zero new spawns.
        for (int i = 0; i < 15; i++)
        {
            continuous.Update(1f / 30f, Transform3D.Identity);
        }

        AssertThat(continuous.TotalSpawned).IsEqual(spawnedBeforeStop);
        AssertThat(continuous.AliveCount()).IsEqual(0);

        // 2. Continuous beam emitter stops refilling once SetEmitting(false) is set.
        var beamBank = new FxBank { Ok = true };
        var beamEm = new FxEmitter
        {
            TypeId = 9,
            HasMotion = true,
            NumSplineSegments = 3,
            MaxActive = 32f,
            AttachToRoot = true,
            UseLife = false,
            LifetimeFrames = 0,
        };
        beamEm.Vec1.DefaultValue = new[] { 1f, 1f, 1f };

        var beamSys = new FxSystemDef { Mode = 2, ChildCount = 2, LastStartFrames = 30 };
        beamSys.Visuals.Add(new FxVisual
        {
            HasTexture = true,
            Texture = "lightning.tex",
            HasSize = true,
            SizeX = 0.25f,
            SizeY = 0.25f,
        });

        var beamEff = new FxEffect
        {
            Name = "test_continuous_beam",
            NameHash = FxBank.Fnv1Lower("test_continuous_beam"),
            UpdateFrequency = 1,
        };
        beamEff.Parts.Add(new FxEffectPart(0, 0, 1, 0, 0));
        beamBank.Emitters.Add(beamEm);
        beamBank.Systems.Add(beamSys);
        beamBank.Effects.Add(beamEff);
        beamBank.Reindex();

        var beamFx = new Engine_FxSystem();
        var beamPlace = new FxPlacement { EffectName = "test_continuous_beam" };
        beamFx.Build(beamBank, new[] { beamPlace });
        AssertThat(beamPlace.Resolved).IsTrue();
        beamFx.SetLoop(false);
        beamFx.SetEmitting(true);

        beamFx.Update(1f / 30f, Transform3D.Identity);
        beamFx.Update(1f / 30f, Transform3D.Identity);
        int beamSpawned = beamFx.TotalSpawned;
        AssertThat(beamSpawned).IsGreater(0);
        AssertThat(beamFx.AliveCount()).IsGreater(0);

        beamFx.SetEmitting(false);
        for (int i = 0; i < 5; i++)
        {
            beamFx.Update(1f / 30f, Transform3D.Identity);
        }

        AssertThat(beamFx.TotalSpawned).IsEqual(beamSpawned);
        AssertThat(beamFx.AliveCount()).IsEqual(0);
    }

    [TestCase]
    public void FiniteLightEmitterExtinguishesAfterLifetimeFrames()
    {
        var bank = new FxBank { Ok = true };
        var lightEmitter = new FxEmitter
        {
            TypeId = 7,
            MaxActive = 1f,
            AttachToRoot = true,
            UseLife = true,
            LifetimeFrames = 15,
        };
        for (int i = 0; i < 28; i++)
        {
            lightEmitter.Generic[i] = Engine_FxBank.MakeTimeline(i, i switch
            {
                15 => new[] { (0f, 10.0f) },
                16 => new[] { (0f, 5.0f) },
                _ => Array.Empty<(float, float)>(),
            });
        }
        lightEmitter.Vec0 = Engine_FxBank.MakeVecTimeline(0, Array.Empty<(float, float, float, float)>());
        lightEmitter.Vec1 = Engine_FxBank.MakeVecTimeline(1, Array.Empty<(float, float, float, float)>());
        lightEmitter.Vec2 = Engine_FxBank.MakeVecTimeline(2, Array.Empty<(float, float, float, float)>());
        lightEmitter.LightColour = Engine_FxBank.MakeVecTimeline(3, new[] { (0f, 1f, 0.5f, 0.1f) });
        Engine_FxBank.LinkParameters(lightEmitter);

        var eff = new FxEffect
        {
            Name = "test_finite_light",
            NameHash = FxBank.Fnv1Lower("test_finite_light"),
            UpdateFrequency = 1,
        };
        eff.Parts.Add(new FxEffectPart(0, -1, 1, 0, 0));
        bank.Emitters.Add(lightEmitter);
        bank.Effects.Add(eff);
        bank.Reindex();

        var fx = new Engine_FxSystem();
        var placement = new FxPlacement { EffectName = "test_finite_light" };
        fx.Build(bank, new[] { placement });
        AssertThat(placement.Resolved).IsTrue();
        fx.SetLoop(false);

        for (int i = 0; i < 10; i++)
        {
            fx.Update(1f / 30f, Transform3D.Identity);
        }

        Engine_FxSystem.FxLightState midLife = fx.LightAt(0);
        AssertThat(midLife.Range).IsGreater(0f);
        AssertThat(midLife.Energy).IsGreater(0f);

        for (int i = 0; i < 10; i++)
        {
            fx.Update(1f / 30f, Transform3D.Identity);
        }

        Engine_FxSystem.FxLightState expired = fx.LightAt(0);
        AssertThat(expired.Range).IsEqual(0f);
        AssertThat(expired.Energy).IsEqual(0f);
    }

    [TestCase]
    public void ShouldRecycleTrackedDetectsAllHandleRecycleAndContinuationCases()
    {
        // 1. Effect hash changed on same pooled handle address -> must recycle.
        AssertThat(GameFx.ShouldRecycleTracked(
            trackedEffect: 0x64D9750Bu,
            incomingEffect: 0x2992CB02u,
            hasKnownInstances: true,
            hasInstanceOverlap: true,
            wasEmitting: true,
            incomingAnyEmitting: true)).IsTrue();

        // 2. Same effect hash on recycled handle address with disjoint child CParticleNode pointers -> must recycle.
        AssertThat(GameFx.ShouldRecycleTracked(
            trackedEffect: 0x2992CB02u,
            incomingEffect: 0x2992CB02u,
            hasKnownInstances: true,
            hasInstanceOverlap: false,
            wasEmitting: true,
            incomingAnyEmitting: true)).IsTrue();

        // 3. Same effect & overlapping instance pointer after emission had already stopped -> re-arm must recycle.
        AssertThat(GameFx.ShouldRecycleTracked(
            trackedEffect: 0x2992CB02u,
            incomingEffect: 0x2992CB02u,
            hasKnownInstances: true,
            hasInstanceOverlap: true,
            wasEmitting: false,
            incomingAnyEmitting: true)).IsTrue();

        // 4. Same continuing emitting effect -> keep node.
        AssertThat(GameFx.ShouldRecycleTracked(
            trackedEffect: 0x64D9750Bu,
            incomingEffect: 0x64D9750Bu,
            hasKnownInstances: true,
            hasInstanceOverlap: true,
            wasEmitting: true,
            incomingAnyEmitting: true)).IsFalse();

        // 5. Same effect transitioning Emitting -> !Emitting (or continuing !Emitting drain) -> keep node to drain.
        AssertThat(GameFx.ShouldRecycleTracked(
            trackedEffect: 0x2992CB02u,
            incomingEffect: 0x2992CB02u,
            hasKnownInstances: true,
            hasInstanceOverlap: true,
            wasEmitting: true,
            incomingAnyEmitting: false)).IsFalse();
        AssertThat(GameFx.ShouldRecycleTracked(
            trackedEffect: 0x2992CB02u,
            incomingEffect: 0x2992CB02u,
            hasKnownInstances: true,
            hasInstanceOverlap: true,
            wasEmitting: false,
            incomingAnyEmitting: false)).IsFalse();
    }

    [TestCase]
    public void TryLatchLocalSplineConvertsValidTransformsAndRejectsSingularMatrices()
    {
        var basisRest = new Basis(Vector3.Up, Mathf.Pi * 0.5f);
        var targetStated = new Transform3D(basisRest, new Vector3(10f, 2f, 5f));
        // Local offsets (0,0,0) and (0,0,0.8) under +90 deg Y rotation -> world (10,2,5) and (10.8,2,5).
        Vector3[] worldRest =
        {
            new Vector3(10f, 2f, 5f),
            new Vector3(10.8f, 2f, 5f),
        };

        bool ok = GameFx.TryLatchLocalSpline(targetStated, worldRest, out Vector3[] localPts);
        AssertThat(ok).IsTrue();
        AssertThat(localPts.Length).IsEqual(2);
        AssertThat(localPts[0].IsEqualApprox(Vector3.Zero)).IsTrue();
        AssertThat(localPts[1].IsEqualApprox(new Vector3(0f, 0f, 0.8f))).IsTrue();

        // Transform latched local points onto unrotated moved weapon at (15, 3, 8) -> (15, 3, 8.8).
        var targetDrawn = new Transform3D(Basis.Identity, new Vector3(15f, 3f, 8f));
        AssertThat((targetDrawn * localPts[1]).IsEqualApprox(new Vector3(15f, 3f, 8.8f))).IsTrue();

        // Singular target transform must be rejected cleanly.
        var singularBasis = new Basis(Vector3.Zero, Vector3.Zero, Vector3.Zero);
        var singular = new Transform3D(singularBasis, Vector3.Zero);
        AssertThat(GameFx.TryLatchLocalSpline(singular, worldRest, out _)).IsFalse();

        // Insufficient point count must be rejected cleanly.
        AssertThat(GameFx.TryLatchLocalSpline(targetStated, worldRest.AsSpan(0, 1), out _)).IsFalse();
    }

    [TestCase]
    public void MapAttachedTargetTransformFallsBackSafelyOnSingularTargetTransform()
    {
        var singularStated = new Transform3D(new Basis(Vector3.Zero, Vector3.Zero, Vector3.Zero), new Vector3(1f, 2f, 3f));
        var drawn = Transform3D.Identity;
        var effectStated = new Transform3D(Basis.Identity, new Vector3(12f, 20f, 7f));

        Transform3D mapped = GameEntities.MapAttachedTargetTransform(singularStated, drawn, effectStated);
        AssertThat(mapped.Origin.IsFinite()).IsTrue();
        AssertThat(mapped.IsEqualApprox(effectStated)).IsTrue();
    }

    [TestCase]
    public void ResolveNodeEmittingAllowsInitialBurstWhenGuestAlreadyClosedWindow()
    {
        AssertThat(GameFx.ResolveNodeEmitting(incomingAnyEmitting: false, sawGuestEmitting: false, debugSpawned: 0)).IsTrue();
        AssertThat(GameFx.ResolveNodeEmitting(incomingAnyEmitting: false, sawGuestEmitting: false, debugSpawned: 1)).IsFalse();
        AssertThat(GameFx.ResolveNodeEmitting(incomingAnyEmitting: true, sawGuestEmitting: false, debugSpawned: 0)).IsTrue();
        AssertThat(GameFx.ResolveNodeEmitting(incomingAnyEmitting: true, sawGuestEmitting: true, debugSpawned: 5)).IsTrue();
        AssertThat(GameFx.ResolveNodeEmitting(incomingAnyEmitting: false, sawGuestEmitting: true, debugSpawned: 0)).IsFalse();
        AssertThat(GameFx.ResolveNodeEmitting(incomingAnyEmitting: false, sawGuestEmitting: true, debugSpawned: 5)).IsFalse();

        // Simulate a 0-frame instant burst whose guest window closed before host step 1.
        Engine_FxSystem instantBurst = BuildSystem(useLife: true, lifetimeFrames: 0, initialNum: 4f, spawnRate: 0f);
        instantBurst.SetLoop(GameFx.LiveFxDefaultLoop);
        instantBurst.SetEmitting(GameFx.ResolveNodeEmitting(false, false, instantBurst.TotalSpawned));
        instantBurst.Update(1f / 30f, Transform3D.Identity);
        AssertThat(instantBurst.TotalSpawned).IsEqual(4);
        AssertThat(instantBurst.AliveCount()).IsEqual(4);

        // Once the initial burst has spawned, emission closes cleanly without respawning.
        instantBurst.SetEmitting(GameFx.ResolveNodeEmitting(false, false, instantBurst.TotalSpawned));
        for (int i = 0; i < 15; i++)
        {
            instantBurst.Update(1f / 30f, Transform3D.Identity);
        }

        AssertThat(instantBurst.TotalSpawned).IsEqual(4);
        AssertThat(instantBurst.AliveCount()).IsEqual(0);
    }

    [TestCase]
    public void ShouldClearWeaponSplineDetectsDetachAndTargetSwitch()
    {
        AssertThat(GameFx.ShouldClearWeaponSpline(0u, 0x42BC7D10u)).IsTrue();
        AssertThat(GameFx.ShouldClearWeaponSpline(0x42BC7D20u, 0x42BC7D10u)).IsTrue();
        AssertThat(GameFx.ShouldClearWeaponSpline(0x42BC7D10u, 0x42BC7D10u)).IsFalse();
        AssertThat(GameFx.ShouldClearWeaponSpline(0u, 0u)).IsFalse();
        AssertThat(GameFx.ShouldClearWeaponSpline(0x42BC7D10u, 0u)).IsFalse();
    }

    [TestCase]
    public void ShouldRecycleTrackedSuppressesHeuristicsUnderBufferSaturation()
    {
        // Hash change must still recycle even when the guest FX buffer is saturated.
        AssertThat(GameFx.ShouldRecycleTracked(
            trackedEffect: 0x64D9750Bu,
            incomingEffect: 0x2992CB02u,
            hasKnownInstances: true,
            hasInstanceOverlap: false,
            wasEmitting: true,
            incomingAnyEmitting: true,
            bufferSaturated: true)).IsTrue();

        // Disjoint child pointers and emission re-arm must not false-recycle under buffer saturation.
        AssertThat(GameFx.ShouldRecycleTracked(
            trackedEffect: 0x2992CB02u,
            incomingEffect: 0x2992CB02u,
            hasKnownInstances: true,
            hasInstanceOverlap: false,
            wasEmitting: true,
            incomingAnyEmitting: true,
            bufferSaturated: true)).IsFalse();
        AssertThat(GameFx.ShouldRecycleTracked(
            trackedEffect: 0x2992CB02u,
            incomingEffect: 0x2992CB02u,
            hasKnownInstances: true,
            hasInstanceOverlap: true,
            wasEmitting: false,
            incomingAnyEmitting: true,
            bufferSaturated: true)).IsFalse();
    }
}
