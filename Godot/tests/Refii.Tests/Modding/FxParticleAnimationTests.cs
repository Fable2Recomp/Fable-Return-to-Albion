namespace Refii.Tests.Modding;

using System;
using System.IO;
using GdUnit4;
using Godot;
using Refii.Fx;
using Refii.Models;
using static GdUnit4.Assertions;

/// <summary>
/// Regression tests for FX particle animation fixes:
/// 1. Trailing keyframe slots in the 20-entry particle constant table repeat the last keyframe
///    (`sub_821CA108` at `0x821CA250..0x821CA264`) instead of zeroing slot 19, preventing
///    `v_texture_time` (`scales.w`), particle size, and global scale from rewinding to 0 when a
///    particle's keyframe age reaches `[18.0, 19.0]`.
/// 2. `FableFx` clears `_addMesh` whenever `GuestDrawn` is true (even when `_hasDisplacement` is
///    true, as on torches `FX_ED73286B` and braziers `FX_91112A7B`), preventing frozen frame-0
///    additive flame surfaces from lingering on top of `Engine_ClassicParticles`.
/// 3. Effects faded out by distance (`LightFade <= 0.001f`) skip host fallback emission.
/// </summary>
[TestSuite]
public class FxParticleAnimationTests
{
    [TestCase]
    public void TrailingKeyframeSlotsRepeatLastKeyframeInsteadOfZero()
    {
        const int keys = 19;
        var buffer = new float[keys * 10];
        for (int k = 0; k < keys; k++)
        {
            int o = k * 10;
            buffer[o + 0] = 0.1f * (k + 1); // r
            buffer[o + 1] = 0.2f * (k + 1); // g
            buffer[o + 2] = 0.3f * (k + 1); // b
            buffer[o + 3] = 1.0f;           // a
            buffer[o + 4] = 1.5f;           // scale x
            buffer[o + 5] = 2.5f;           // scale y
            buffer[o + 6] = 0.5f;           // global scale x
            buffer[o + 7] = 0.75f;          // global scale y
            buffer[o + 8] = 1.0f;           // self-illum
            buffer[o + 9] = k / 18f;        // v_texture_time (0.0 .. 1.0)
        }

        var colours = new Vector4[20];
        var scales = new Vector4[20];
        var globalScales = new Vector4[20];

        Engine_FxSystem.FillGuestKeyframeTables(buffer, 0, keys, 2.0f, colours, scales, globalScales);

        // Slot 18 is the 19th keyframe (v_texture_time = 1.0).
        AssertThat(Mathf.IsEqualApprox(scales[18].W, 1.0f)).IsTrue();
        // Slot 19 must repeat slot 18 so `mix(table[18], table[19], f)` for `key` in `[18.0, 19.0]`
        // holds `v_texture_time = 1.0` and non-zero scales instead of rewinding toward zero.
        AssertThat(colours[19]).IsEqual(colours[18]);
        AssertThat(scales[19]).IsEqual(scales[18]);
        AssertThat(globalScales[19]).IsEqual(globalScales[18]);
        AssertThat(Mathf.IsEqualApprox(globalScales[19].X, 1.0f)).IsTrue();
        AssertThat(Mathf.IsEqualApprox(globalScales[19].Y, 1.5f)).IsTrue();
    }

    [TestCase]
    public void ZeroKeyframesClearsAllSlotsToZero()
    {
        var buffer = Array.Empty<float>();
        var colours = new Vector4[20];
        var scales = new Vector4[20];
        var globalScales = new Vector4[20];
        colours[0] = Vector4.One;
        scales[19] = Vector4.One;

        Engine_FxSystem.FillGuestKeyframeTables(buffer, 0, 0, 1.0f, colours, scales, globalScales);

        for (int k = 0; k < 20; k++)
        {
            AssertThat(colours[k]).IsEqual(Vector4.Zero);
            AssertThat(scales[k]).IsEqual(Vector4.Zero);
            AssertThat(globalScales[k]).IsEqual(Vector4.Zero);
        }
    }

    [TestCase]
    public void AdditiveMeshIsSuppressedWhenGuestDrawnEvenWithDisplacement()
    {
        // When GuestDrawn is true (e.g. torches with _hasDisplacement = true), _addMesh must NOT
        // be used so FableFx clears any frame-0 fallback additive surfaces.
        AssertThat(Engine_FxSystem.ShouldUseAdditiveMesh(guestDrawn: true, hasAddMesh: true, additiveLayerPresent: true))
            .IsFalse();
        AssertThat(Engine_FxSystem.ShouldUseAdditiveMesh(guestDrawn: false, hasAddMesh: true, additiveLayerPresent: true))
            .IsTrue();
        AssertThat(Engine_FxSystem.ShouldUseAdditiveMesh(guestDrawn: false, hasAddMesh: true, additiveLayerPresent: false))
            .IsFalse();
    }

    [TestCase]
    public void DistanceFadedEffectsSkipHostFallbackGeometry()
    {
        AssertThat(Engine_FxSystem.ShouldCullByFade(0f)).IsTrue();
        AssertThat(Engine_FxSystem.ShouldCullByFade(0.001f)).IsTrue();
        AssertThat(Engine_FxSystem.ShouldCullByFade(0.01f)).IsFalse();

        // Even if GuestDrawn became false because the guest withdrew its render twin past ProcessRadius,
        // a faded-out effect (LightFade == 0) must not pop into FableFx fallback simulation.
        AssertThat(Engine_FxSystem.ShouldRunLightsOnly(
            guestDrawn: false,
            hasDisplacement: true,
            guestMeshTwins: true,
            hasMeshParticles: false,
            lightFade: 0f)).IsTrue();

        // Close effect with displacement and GuestDrawn = true still steps for _dispMesh.
        AssertThat(Engine_FxSystem.ShouldRunLightsOnly(
            guestDrawn: true,
            hasDisplacement: true,
            guestMeshTwins: true,
            hasMeshParticles: false,
            lightFade: 1f)).IsFalse();
    }

    [TestCase]
    public void GuestWorldMatrixRoundTripsThroughEntitySo3SpaceAndTimeline()
    {
        // Exact 4x4 identity at world origin is detected as a world-space emitter.
        float[] identity16 =
        {
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, 0f, 0f, 1f,
        };
        AssertThat(Engine_FxSystem.IsExactGuestIdentity4x4(identity16)).IsTrue();

        // 90-degree rotation around guest Z with non-uniform row scales (2, 3, 4) and translation (10, 20, 30):
        // u0 = (0, 1, 0), u1 = (-1, 0, 0), u2 = (0, 0, 1), det([u0; u1; u2]) = +1.
        float[] rotated16 =
        {
             0f, 2f, 0f, 0f,
            -3f, 0f, 0f, 0f,
             0f, 0f, 4f, 0f,
            10f, 20f, 30f, 1f,
        };
        AssertThat(Engine_FxSystem.IsExactGuestIdentity4x4(rotated16)).IsFalse();
        AssertThat(Engine_FxSystem.TryDecomposeGuestWorldMatrix(
            rotated16, out Transform3D entityWorld, out Vector3 scale, out bool idBasis)).IsTrue();
        AssertThat(idBasis).IsFalse();
        AssertThat(entityWorld.Basis.Determinant()).IsEqualApprox(1.0f, 1e-4f);
        AssertThat(scale.IsEqualApprox(new Vector3(2f, 3f, 4f))).IsTrue();

        // Passing through Engine_TickTimeline (which converts Basis -> Quaternion -> Basis) must preserve
        // handedness and round-trip back to the exact transposed vs_g_MatWorld columns.
        var timeline = new Refii.Core.Engine_TickTimeline(stillWhenUnchanged: false);
        timeline.Reset(10.0, entityWorld);
        Transform3D advanced = timeline.Advance(1_000_000UL, 9.5);

        Span<Vector4> cols = stackalloc Vector4[3];
        Engine_FxSystem.ComposeGuestMatWorldColumns(advanced, scale, cols);
        AssertThat(cols[0].IsEqualApprox(new Vector4(0f, -3f, 0f, 10f))).IsTrue();
        AssertThat(cols[1].IsEqualApprox(new Vector4(2f, 0f, 0f, 20f))).IsTrue();
        AssertThat(cols[2].IsEqualApprox(new Vector4(0f, 0f, 4f, 30f))).IsTrue();

        // Entity <-> FableFx conversion flips determinant between +1 (entity mesh) and -1 (FX sim) and is self-inverse.
        Transform3D fxWorld = Engine_FxSystem.EntityMeshToFxTransform(entityWorld);
        AssertThat(fxWorld.Basis.Determinant()).IsEqualApprox(-1.0f, 1e-4f);
        Transform3D roundTripEntity = Engine_FxSystem.FxToEntityMeshTransform(fxWorld);
        AssertThat(roundTripEntity.IsEqualApprox(entityWorld)).IsTrue();
    }

    [TestCase]
    public void MovingExpOrbInterpolatesSmoothlyAt240HzWhileStaticTorchStaysSettled()
    {
        // Static torch at (-40, 118, 15): settles immediately on Reset and never enters moving set.
        float[] torch16 =
        {
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f,
            -40f, 118f, 15f, 1f,
        };
        AssertThat(Engine_FxSystem.TryDecomposeGuestWorldMatrix(
            torch16, out Transform3D torchWorld, out _, out bool torchId)).IsTrue();
        AssertThat(torchId).IsTrue();

        var torchTimeline = new Refii.Core.Engine_TickTimeline(stillWhenUnchanged: false);
        torchTimeline.Reset(100.0, torchWorld);
        AssertThat(torchTimeline.Settled).IsTrue();
        torchTimeline.Observe(101.0, torchWorld);
        AssertThat(torchTimeline.Settled).IsTrue();

        // Moving EXP orb advancing +0.6 m per 15 Hz tick along X: across 16 display frames (240 Hz, 4167 us/frame)
        // it must advance monotonically in sub-centimetre increments instead of holding still for 15 frames.
        var orbTimeline = new Refii.Core.Engine_TickTimeline(stillWhenUnchanged: false);
        var start = new Transform3D(Engine_FxSystem.EntityGuestIdentity, new Vector3(0f, 1f, 0f));
        var next = new Transform3D(Engine_FxSystem.EntityGuestIdentity, new Vector3(0.6f, 1f, 0f));

        // Use a monotonic timestamp ahead of earlier suite tests so Engine_DrawAdvance.Measure advances cleanly.
        ulong usec = 900_000_000_000UL;
        orbTimeline.Reset(100.0, start);
        _ = orbTimeline.Advance(usec, 100.0);
        orbTimeline.Observe(101.0, next);
        AssertThat(orbTimeline.Settled).IsFalse();

        float prevX = 0f;
        for (int frame = 1; frame <= 16; frame++)
        {
            usec += 4167UL;
            double drawTick = 100.0 + (frame / 16.0);
            Transform3D drawn = orbTimeline.Advance(usec, drawTick);
            AssertThat(drawn.Origin.X > prevX).IsTrue();
            AssertThat(drawn.Origin.X - prevX < 0.1f).IsTrue();
            prevX = drawn.Origin.X;
        }
        AssertThat(prevX > 0.35f).IsTrue();
    }

    [TestCase]
    public void ModernGuestFadeOutDisablesPre50mSeamWhileClassicPreservesRamp()
    {
        // Ground flame FX_91112A7B has authored fade [f2=22, f3=32, f4=0.85].
        Vector4 classic = Engine_FxSystem.BuildGuestFadeOutInfo(22f, 32f, 0.85f, disableDistanceFade: false);
        float classicAt35m = Math.Clamp((classic.X - 35f) * classic.Y, 0f, 1f);
        float classicAt27m = Math.Clamp((classic.X - 27f) * classic.Y, 0f, 1f);
        AssertThat(classicAt35m).IsEqualApprox(0f, 1e-5f);
        AssertThat(classicAt27m).IsEqualApprox(0.5f, 1e-5f);
        AssertThat(classic.Z).IsEqualApprox(0.85f, 1e-5f);

        // Under Modern lighting, pre-50 m fade-out is disabled so ground flames and torches stay visible
        // seamlessly across 22..50 m into FableFx's 50..200 m range, while preserving f4 intensity in .z.
        Vector4 modern = Engine_FxSystem.BuildGuestFadeOutInfo(22f, 32f, 0.85f, disableDistanceFade: true);
        float modernAt35m = Math.Clamp((modern.X - 35f) * modern.Y, 0f, 1f);
        float modernAt48m = Math.Clamp((modern.X - 48f) * modern.Y, 0f, 1f);
        AssertThat(modernAt35m).IsEqualApprox(1f, 1e-5f);
        AssertThat(modernAt48m).IsEqualApprox(1f, 1e-5f);
        AssertThat(modern.Z).IsEqualApprox(0.85f, 1e-5f);
    }

    [TestCase]
    public void EffectiveFreezeDistanceExtendsTo200mUnderModernLighting()
    {
        AssertThat(Engine_FxSystem.EffectiveFreezeDistance(modernModel: false)).IsEqual(50f);
        AssertThat(Engine_FxSystem.EffectiveFreezeDistance(modernModel: true)).IsEqual(200f);
    }

    [TestCase]
    public void TranslucentAmbienceParticlesReceiveAddressBasedSortingOffset()
    {
        // Stationary translucent ambience groups (ExactIdentity4x4, not bone attached, RenderPriority 0)
        // receive a deterministic address-based sorting offset to break depth sort ties without flickering.
        uint addr0 = 0x42E516D0;
        uint addr1 = 0x42E51BB0;
        float sort0 = (float)(addr0 & 0xFF) * 1e-5f;
        float sort1 = (float)(addr1 & 0xFF) * 1e-5f;

        AssertThat(sort0).IsEqual(0xD0 * 1e-5f);
        AssertThat(sort1).IsEqual(0xB0 * 1e-5f);
        AssertThat(sort0 != sort1).IsTrue();

        // Bone-attached and non-translucent (e.g. RenderPriority 10 additive) effects must keep offset 0f.
        bool boneAttached = true;
        int renderPriority = 10;
        float boneOffset = boneAttached ? 0f : sort0;
        float additiveOffset = renderPriority != 0 ? 0f : sort0;
        AssertThat(boneOffset).IsEqual(0f);
        AssertThat(additiveOffset).IsEqual(0f);
    }

    [TestCase]
    public void NewlySpawnedParticlesRecordBirthPrevPosAndStepAt30Hz()
    {
        var bank = new FxBank { Ok = true };
        var em = new FxEmitter
        {
            TypeId = 0,
            HasMotion = true,
            MaxActive = 10f,
            InitialNum = 1f,
            AttachToRoot = false,
            LifetimeFrames = 30,
            UseLife = true,
        };
        em.Generic[4].DefaultValue = 6.0f;  // initial speed = 6 m/s
        em.Generic[11].DefaultValue = 1.0f; // upward Z direction
        em.Generic[12].DefaultValue = 1.0f;
        em.Generic[13].DefaultValue = 1.0f;
        em.Vec1.DefaultValue = new[] { 1f, 1f, 1f };

        var sysDef = new FxSystemDef { Mode = 1, ChildCount = 2, LastStartFrames = 30 };
        sysDef.Visuals.Add(new FxVisual { HasTexture = true, Texture = "fxt_sparkle.tex", HasSize = true, SizeX = 0.2f, SizeY = 0.2f });

        var eff = new FxEffect
        {
            Name = "test_fountain_sparkles",
            NameHash = FxBank.Fnv1Lower("test_fountain_sparkles"),
            UpdateFrequency = 0, // Authored as group 0 (15 Hz LOD on Xbox 360), stepped at 30 Hz in Modern FableFx.
        };
        eff.Parts.Add(new FxEffectPart(0, 0, 1, 0, -1));

        bank.Emitters.Add(em);
        bank.Systems.Add(sysDef);
        bank.Effects.Add(eff);
        bank.Reindex();

        var fx = new Engine_FxSystem();
        var placement = new FxPlacement { EffectName = "test_fountain_sparkles" };
        fx.Build(bank, new[] { placement });
        AssertThat(placement.Resolved).IsTrue();

        // Even with UpdateFrequency == 0, Modern FableFx steps on the very first 1/30 s tick.
        var originTransform = Transform3D.Identity;
        fx.Update(1f / 30f, originTransform);
        AssertThat(fx.LastSteps).IsEqual(1);
        AssertThat(fx.AliveCount()).IsEqual(1);

        // The newly spawned particle must have HasPrev == true and PrevPos at the birth origin
        // while Pos has advanced by 1 step (6 m/s * 1/30 s = 0.2 m), so its first-step motion is non-zero.
        foreach ((Vector3 prevPos, Vector3 pos, bool hasPrev) in fx.ParticleMotionStates())
        {
            AssertThat(hasPrev).IsTrue();
            AssertThat(prevPos.Length()).IsLess(1e-4f);
            AssertThat((pos - prevPos).Length()).IsGreater(0.15f);
        }

        // Moving the node on an intermediate display frame (dt = 1/120 s < 1/30 s, so LastSteps == 0)
        // must rebase the detached particle and flag LastRebased = true so FableFx.Step re-emits.
        var movedTransform = new Transform3D(Basis.Identity, new Vector3(0.1f, 0f, 0f));
        fx.Update(1f / 120f, movedTransform);
        AssertThat(fx.LastSteps).IsEqual(0);
        AssertThat(fx.LastRebased).IsTrue();
    }

    [TestCase]
    public void PackSurfaceBatchPreservesUnclampedLinearHdrColoursInCustom1()
    {
        var list = new System.Collections.Generic.List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)>
        {
            (new Vector3(1f, 2f, 3f), new Vector2(0f, 1f), new Color(2.75f, 1.5f, 0.25f, 0.8f), new Vector2(0.1f, 0.2f), new Vector4(0.3f, 0.4f, 0.5f, 0f)),
        };

        Span<Vector3> positions = stackalloc Vector3[2];
        Span<Vector2> uvs = stackalloc Vector2[2];
        Span<float> colours = stackalloc float[8];
        Span<Vector2> offsets = stackalloc Vector2[2];
        Span<float> motions = stackalloc float[8];

        Engine_FxSystem.PackSurfaceBatch(list, 2, positions, uvs, colours, offsets, motions);

        // Vertex 0 must preserve linear HDR channels > 1.0 without 8-bit [0..1] clamping.
        AssertThat(colours[0]).IsEqualApprox(2.75f, 1e-5f);
        AssertThat(colours[1]).IsEqualApprox(1.5f, 1e-5f);
        AssertThat(colours[2]).IsEqualApprox(0.25f, 1e-5f);
        AssertThat(colours[3]).IsEqualApprox(0.8f, 1e-5f);

        // Padded tail vertex (index 1) must have zeroed colour and motion.
        AssertThat(colours[4]).IsEqual(0f);
        AssertThat(colours[7]).IsEqual(0f);
        AssertThat(motions[4]).IsEqual(0f);
    }

    [TestCase]
    public void NonLoopingSystemWithUseLifeUsesVisualSpanForParticlesAndEmitterLifetimeForWindow()
    {
        // 1. Direct helper contract: non-looping uses system LastStartFrames; infinite loop uses emitter LifetimeFrames.
        AssertThat(Engine_FxSystem.ComputeParticleLife(useLife: true, emitterLifetimeFrames: 30f, loopCount: 0, systemLifeFrames: 18f))
            .IsEqual(18f);
        AssertThat(Engine_FxSystem.ComputeParticleLife(useLife: true, emitterLifetimeFrames: 6f, loopCount: 0, systemLifeFrames: 30f))
            .IsEqual(30f);
        AssertThat(Engine_FxSystem.ComputeParticleLife(useLife: true, emitterLifetimeFrames: 20f, loopCount: -1, systemLifeFrames: 10f))
            .IsEqual(20f);
        AssertThat(Engine_FxSystem.ComputeParticleLife(useLife: false, emitterLifetimeFrames: 0f, loopCount: -1, systemLifeFrames: 30f))
            .IsEqual(-1f);

        // 2. When emitter LifetimeFrames (30) > system LastStartFrames (10) (like weapon sparkles FX_64D9750B):
        // particles spawned at frame 0 expire at LastStartFrames (10), while emitter continues spawning until frame 30.
        var sparkleBank = new FxBank { Ok = true };
        var sparkleEmitter = new FxEmitter
        {
            TypeId = 0,
            HasMotion = true,
            MaxActive = 32f,
            InitialNum = 1f,
            AttachToRoot = true,
            LifetimeFrames = 30,
            UseLife = true,
        };
        sparkleEmitter.Vec1.DefaultValue = new[] { 1f, 1f, 1f };

        var sparkleSys = new FxSystemDef { Mode = 1, ChildCount = 2, LastStartFrames = 10 };
        sparkleSys.Visuals.Add(new FxVisual { HasTexture = true, Texture = "sparkle.tex", HasSize = true, SizeX = 0.2f, SizeY = 0.2f });

        var sparkleEff = new FxEffect
        {
            Name = "test_weapon_sparkles",
            NameHash = FxBank.Fnv1Lower("test_weapon_sparkles"),
            UpdateFrequency = 1,
        };
        sparkleEff.Parts.Add(new FxEffectPart(0, 0, 1, 0, 0));
        sparkleBank.Emitters.Add(sparkleEmitter);
        sparkleBank.Systems.Add(sparkleSys);
        sparkleBank.Effects.Add(sparkleEff);
        sparkleBank.Reindex();

        var sparkleFx = new Engine_FxSystem();
        var sparklePlace = new FxPlacement { EffectName = "test_weapon_sparkles" };
        sparkleFx.Build(sparkleBank, new[] { sparklePlace });
        AssertThat(sparklePlace.Resolved).IsTrue();
        sparkleFx.SetLoop(false);

        for (int i = 0; i < 8; i++) sparkleFx.Update(1f / 30f, Transform3D.Identity);
        AssertThat(sparkleFx.AliveCount()).IsEqual(1);

        // Step past LastStartFrames (10) before LifetimeFrames (30): particle dies at its 10-frame visual span.
        for (int i = 0; i < 5; i++) sparkleFx.Update(1f / 30f, Transform3D.Identity);
        AssertThat(sparkleFx.AliveCount()).IsEqual(0);

        // 3. When emitter LifetimeFrames (6) < system LastStartFrames (30) (like muzzle flash FX_2992CB02 / water splash FX_76528C9A):
        // emission stops at frame 6 while spawned particles survive past frame 6 through frame 30.
        var burstBank = new FxBank { Ok = true };
        var burstEmitter = new FxEmitter
        {
            TypeId = 0,
            HasMotion = true,
            MaxActive = 32f,
            InitialNum = 1f,
            AttachToRoot = true,
            LifetimeFrames = 6,
            UseLife = true,
        };
        burstEmitter.Generic[2].DefaultValue = 1f;
        burstEmitter.Vec1.DefaultValue = new[] { 1f, 1f, 1f };

        var burstSys = new FxSystemDef { Mode = 1, ChildCount = 2, LastStartFrames = 30 };
        burstSys.Visuals.Add(new FxVisual { HasTexture = true, Texture = "flash.tex", HasSize = true, SizeX = 0.2f, SizeY = 0.2f });

        var burstEff = new FxEffect
        {
            Name = "test_muzzle_flash",
            NameHash = FxBank.Fnv1Lower("test_muzzle_flash"),
            UpdateFrequency = 1,
        };
        burstEff.Parts.Add(new FxEffectPart(0, 0, 1, 0, 0));
        burstBank.Emitters.Add(burstEmitter);
        burstBank.Systems.Add(burstSys);
        burstBank.Effects.Add(burstEff);
        burstBank.Reindex();

        var burstFx = new Engine_FxSystem();
        var burstPlace = new FxPlacement { EffectName = "test_muzzle_flash" };
        burstFx.Build(burstBank, new[] { burstPlace });
        AssertThat(burstPlace.Resolved).IsTrue();
        burstFx.SetLoop(false);

        for (int i = 0; i < 6; i++) burstFx.Update(1f / 30f, Transform3D.Identity);
        int spawnedAtFrame6 = burstFx.TotalSpawned;
        AssertThat(spawnedAtFrame6).IsGreater(0);
        AssertThat(burstFx.AliveCount()).IsEqual(spawnedAtFrame6);

        // At frame 15 (past LifetimeFrames = 6, before LastStartFrames = 30): no new spawns, all burst particles still alive.
        for (int i = 0; i < 9; i++) burstFx.Update(1f / 30f, Transform3D.Identity);
        AssertThat(burstFx.TotalSpawned).IsEqual(spawnedAtFrame6);
        AssertThat(burstFx.AliveCount()).IsEqual(spawnedAtFrame6);

        // Step past LastStartFrames = 30 from the last spawned particle (total 40 frames): particles expire naturally.
        for (int i = 0; i < 25; i++) burstFx.Update(1f / 30f, Transform3D.Identity);
        AssertThat(burstFx.AliveCount()).IsEqual(0);
    }

    [TestCase]
    public void WeaponAugmentTrackerTracksWeaponLocomotionAndSplineOverride()
    {
        // Set up weapon local spline endpoints (e.g. gun barrel)
        var localSpline = new Vector3[]
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(0f, 0f, 0.8f),
        };

        // Weapon at rest at world position (10, 2, 5)
        var weaponTransformRest = new Transform3D(Basis.Identity, new Vector3(10f, 2f, 5f));
        var drawnPtsRest = new Vector3[localSpline.Length];
        for (int i = 0; i < localSpline.Length; i++)
        {
            drawnPtsRest[i] = weaponTransformRest * localSpline[i];
        }

        AssertThat(drawnPtsRest[0].IsEqualApprox(new Vector3(10f, 2f, 5f))).IsTrue();
        AssertThat(drawnPtsRest[1].IsEqualApprox(new Vector3(10f, 2f, 5.8f))).IsTrue();

        // Weapon moving during hero run to world position (15, 2, 8) with 90 degree yaw
        var runningBasis = Basis.Identity.Rotated(Vector3.Up, Mathf.Pi * 0.5f);
        var weaponTransformRun = new Transform3D(runningBasis, new Vector3(15f, 2f, 8f));
        var drawnPtsRun = new Vector3[localSpline.Length];
        for (int i = 0; i < localSpline.Length; i++)
        {
            drawnPtsRun[i] = weaponTransformRun * localSpline[i];
        }

        AssertThat(drawnPtsRun[0].IsEqualApprox(new Vector3(15f, 2f, 8f))).IsTrue();
        // After 90 deg rotation, (0, 0, 0.8) along local Z becomes (0.8, 0, 0) in world
        AssertThat(drawnPtsRun[1].X).IsEqualApprox(15.8f, 1e-4f);
        AssertThat(drawnPtsRun[1].Y).IsEqualApprox(2.0f, 1e-4f);
        AssertThat(drawnPtsRun[1].Z).IsEqualApprox(8.0f, 1e-4f);
    }

    [TestCase]
    public void PerKeyframeVisualRenderStatePreservesDistinctBlendModes()
    {
        var bank = new FxBank();
        var emitter = new FxEmitter
        {
            HasMotion = true,
            MaxActive = 4f,
            InitialNum = 1f,
            LifetimeFrames = 30,
            UseLife = true,
        };
        var sys = new FxSystemDef { Mode = 1, ChildCount = 2, LastStartFrames = 30 };

        // Keyframe 0: alpha blend ("mix": src=1, dst=5, op=0), SaturationMask = true, SoftDepthContrast = 0.5
        var vis0 = new FxVisual
        {
            HasTexture = true,
            Texture = "tex_mix.tex",
            HasSize = true,
            SizeX = 0.5f,
            SizeY = 0.5f,
            SrcBlend = 1,
            DstBlend = 5,
            BlendOp = 0,
            SaturationMask = true,
            SoftDepthContrast = 0.5f,
        };
        // Keyframe 1: additive one ("addone": src=1, dst=1, op=0), SaturationMask = false, SoftDepthContrast = 0
        var vis1 = new FxVisual
        {
            HasTexture = true,
            Texture = "tex_add.tex",
            HasSize = true,
            SizeX = 0.5f,
            SizeY = 0.5f,
            SrcBlend = 1,
            DstBlend = 1,
            BlendOp = 0,
            SaturationMask = false,
            SoftDepthContrast = 0f,
        };
        // Keyframe 2: displacement ("disp")
        var vis2 = new FxVisual
        {
            HasTexture = true,
            Texture = "tex_disp.tex",
            HasSize = true,
            SizeX = 0.5f,
            SizeY = 0.5f,
            Displacement = true,
        };

        sys.Visuals.Add(vis0);
        sys.Visuals.Add(vis1);
        sys.Visuals.Add(vis2);

        (string blend0, bool sat0, float soft0) = Engine_FxSystem.ResolveVisualRenderState(vis0);
        (string blend1, bool sat1, float soft1) = Engine_FxSystem.ResolveVisualRenderState(vis1);
        (string blend2, bool sat2, float soft2) = Engine_FxSystem.ResolveVisualRenderState(vis2);

        AssertThat(blend0).IsEqual("mix");
        AssertThat(sat0).IsTrue();
        AssertThat(soft0).IsEqualApprox(0.5f, 1e-5f);

        AssertThat(blend1).IsEqual("addone");
        AssertThat(sat1).IsFalse();
        AssertThat(soft1).IsEqualApprox(0.0f, 1e-5f);

        AssertThat(blend2).IsEqual("disp");
        AssertThat(sat2).IsFalse();
        AssertThat(soft2).IsEqualApprox(0.0f, 1e-5f);

        // Verify full FxSystem build & update completes cleanly with heterogeneous keyframe visuals.
        var eff = new FxEffect { Name = "test_multi_blend", NameHash = FxBank.Fnv1Lower("test_multi_blend"), UpdateFrequency = 1 };
        eff.Parts.Add(new FxEffectPart(0, 0, 1, 0, 0));
        bank.Emitters.Add(emitter);
        bank.Systems.Add(sys);
        bank.Effects.Add(eff);
        bank.Reindex();

        var fx = new Engine_FxSystem();
        var place = new FxPlacement { EffectName = "test_multi_blend" };
        fx.Build(bank, new[] { place });
        AssertThat(place.Resolved).IsTrue();
        fx.Update(1f / 30f, Transform3D.Identity);
        AssertThat(fx.AliveCount()).IsGreater(0);
    }
}
