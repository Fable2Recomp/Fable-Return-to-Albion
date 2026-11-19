using GdUnit4;
using Refii.Fx;
using Refii.Game;
using static GdUnit4.Assertions;

namespace Refii.Tests;

[TestSuite]
public sealed class GameFxRetireTests
{
    [TestCase]
    public void RetireGraceTicksMatches15HzGuestStep()
    {
        AssertThat(GameFx.RetireGraceTicks).IsEqualApprox(22.5, 1e-6);
    }

    [TestCase]
    public void ShouldRetireOnAbsenceRespectsBufferSaturation()
    {
        AssertThat(GameFx.ShouldRetireOnAbsence(1, activeFxSaturated: false)).IsTrue();
        AssertThat(GameFx.ShouldRetireOnAbsence(1, activeFxSaturated: true)).IsFalse();
        AssertThat(GameFx.ShouldRetireOnAbsence(2, activeFxSaturated: true)).IsFalse();
        AssertThat(GameFx.ShouldRetireOnAbsence(3, activeFxSaturated: true)).IsTrue();
    }

    [TestCase]
    public void ShouldDrainOnRetireSkipsHardDestroyedAttachOnlyFx()
    {
        // Hard-destroyed AttachToRoot-only orb / glow recycles immediately so it never freezes in air.
        AssertThat(GameFx.ShouldDrainOnRetire(wasEmitting: true, hasDetachedParticles: false, debugAlive: 4)).IsFalse();

        // Detached world-space trails and soft-stopped FX keep draining existing particles.
        AssertThat(GameFx.ShouldDrainOnRetire(wasEmitting: true, hasDetachedParticles: true, debugAlive: 4)).IsTrue();
        AssertThat(GameFx.ShouldDrainOnRetire(wasEmitting: false, hasDetachedParticles: false, debugAlive: 4)).IsTrue();
        AssertThat(GameFx.ShouldDrainOnRetire(wasEmitting: false, hasDetachedParticles: true, debugAlive: 0)).IsFalse();
    }

    [TestCase]
    public void InfiniteLoopParticlesExpireOnceEmissionStops()
    {
        // Infinite loop (-1) without UseLife stays alive while emitting == true.
        AssertThat(Engine_FxSystem.ShouldParticleExpire(
            emitting: true,
            loopCount: -1,
            useLife: false,
            emitterLifetimeFrames: 0f,
            particleAge: 50f,
            particleLife: -1f,
            loopEndFrames: 15f,
            loopIter: 4)).IsFalse();

        // Once emission stops, it finishes its remaining loop span cleanly and expires.
        AssertThat(Engine_FxSystem.ShouldParticleExpire(
            emitting: false,
            loopCount: -1,
            useLife: false,
            emitterLifetimeFrames: 0f,
            particleAge: 10f,
            particleLife: -1f,
            loopEndFrames: 15f,
            loopIter: 0)).IsFalse();

        AssertThat(Engine_FxSystem.ShouldParticleExpire(
            emitting: false,
            loopCount: -1,
            useLife: false,
            emitterLifetimeFrames: 0f,
            particleAge: 15f,
            particleLife: -1f,
            loopEndFrames: 15f,
            loopIter: 0)).IsTrue();
    }
}
