using GdUnit4;
using Godot;
using Refii.Lighting;
using static GdUnit4.Assertions;

namespace Refii.Tests.Lighting;

/// <summary>
/// Verifies volumetric light shaft budget limits, perceptual scoring, and hysteresis stability.
/// </summary>
[TestSuite]
public sealed class VolumetricShaftBudgetTests
{
	[TestCase]
	public void DefaultMaxActiveShaftLights_Is16WithinBufferCapacity()
	{
		AssertThat(Engine_VolumetricLightShafts.MaxActiveShaftLights).IsEqual(16);
		AssertThat(Engine_VolumetricLightShafts.MaxActiveShaftLights <= Engine_VolumetricLightShafts.MaxShaftLights).IsTrue();
	}

	[TestCase]
	public void ScoreCandidateLight_RanksCloserHigherEnergyFixturesAboveFarDimOnes()
	{
		Vector3 eye = Vector3.Zero;
		Vector3 warm = new(1.0f, 0.8f, 0.5f);

		float nearBright = Engine_VolumetricLightShafts.ScoreCandidateLight(
			eye, new Vector3(0, 0, -8f), range: 14f, warm, energy: 2.0f, fogEnergyScale: 1.0f, wasActive: false);
		float farDim = Engine_VolumetricLightShafts.ScoreCandidateLight(
			eye, new Vector3(0, 0, -45f), range: 10f, warm, energy: 0.6f, fogEnergyScale: 1.0f, wasActive: false);

		AssertThat(nearBright > farDim).IsTrue();
		AssertThat(farDim > 0f).IsTrue();
	}

	[TestCase]
	public void ScoreCandidateLight_AppliesHysteresisStickinessAndRejectsZeroFogScale()
	{
		Vector3 eye = Vector3.Zero;
		Vector3 pos = new(0, 0, -15f);
		Vector3 tint = Vector3.One;

		float cold = Engine_VolumetricLightShafts.ScoreCandidateLight(
			eye, pos, range: 12f, tint, energy: 1.5f, fogEnergyScale: 1.0f, wasActive: false);
		float sticky = Engine_VolumetricLightShafts.ScoreCandidateLight(
			eye, pos, range: 12f, tint, energy: 1.5f, fogEnergyScale: 1.0f, wasActive: true);
		float disabled = Engine_VolumetricLightShafts.ScoreCandidateLight(
			eye, pos, range: 12f, tint, energy: 1.5f, fogEnergyScale: 0.0f, wasActive: true);

		AssertThat(Mathf.Abs((sticky / cold) - 1.15f) < 1e-4f).IsTrue();
		AssertThat(disabled).IsEqual(0.0f);
	}

	[TestCase]
	public void EvaluateSampleKernel_SoftCoreEliminatesOriginHotspotAndForwardMieBullseye_WhilePreservingOuterShafts()
	{
		const float range = 14.8f;
		float invStatedR2 = 1.0f / (range * range);
		float softCore = range * Engine_LevelLights.FloatingVolumetricCoreFraction; // ~7.104m
		const float anisotropy = 0.65f;

		// 1. At d = 0 looking straight through the origin (cosTheta = +1), the legacy point-source kernel
		//    produces a bright forward-Mie + 1/sqrt(0.35) pinpoint orb, whereas the soft-core kernel is
		//    completely smooth and isotropic.
		Engine_VolumetricLightShafts.EvaluateSampleKernel(
			d2: 0.0f, invStatedR2, softCoreRadius: 0.0f, cosTheta: 1.0f, anisotropy,
			out float pointOriginAtten, out float pointOriginPhase);
		Engine_VolumetricLightShafts.EvaluateSampleKernel(
			d2: 0.0f, invStatedR2, softCoreRadius: softCore, cosTheta: 1.0f, anisotropy,
			out float softOriginAtten, out float softOriginPhaseForward);
		Engine_VolumetricLightShafts.EvaluateSampleKernel(
			d2: 0.0f, invStatedR2, softCoreRadius: softCore, cosTheta: -1.0f, anisotropy,
			out _, out float softOriginPhaseBack);

		float pointOriginPeak = pointOriginAtten * pointOriginPhase;
		float softOriginPeak = softOriginAtten * softOriginPhaseForward;
		AssertThat(softOriginPeak < pointOriginPeak * 0.05f).IsTrue();
		AssertThat(Mathf.IsEqualApprox(softOriginPhaseForward, softOriginPhaseBack, 1e-6f)).IsTrue();

		// 2. Across the inner room volume (d = 0m vs d = 2.5m), soft-core attenuation stays uniform
		//    instead of collapsing 4x away from a tiny point.
		Engine_VolumetricLightShafts.EvaluateSampleKernel(
			d2: 2.5f * 2.5f, invStatedR2, softCoreRadius: softCore, cosTheta: 0.0f, anisotropy,
			out float softInnerAtten, out _);
		AssertThat(softInnerAtten > softOriginAtten * 0.90f).IsTrue();

		// 3. At window/doorway shaft distances (d = 8.0m, forward grazing cosTheta = 0.85), soft-core
		//    maintains full volumetric shaft brightness and directional phase so exterior blue light shafts stay strong.
		Engine_VolumetricLightShafts.EvaluateSampleKernel(
			d2: 8.0f * 8.0f, invStatedR2, softCoreRadius: 0.0f, cosTheta: 0.85f, anisotropy,
			out float pointMidAtten, out float pointMidPhase);
		Engine_VolumetricLightShafts.EvaluateSampleKernel(
			d2: 8.0f * 8.0f, invStatedR2, softCoreRadius: softCore, cosTheta: 0.85f, anisotropy,
			out float softMidAtten, out float softMidPhase);

		AssertThat(softMidAtten >= pointMidAtten).IsTrue();
		AssertThat(softMidPhase > softOriginPhaseForward * 1.5f).IsTrue();
		AssertThat((softMidAtten * softMidPhase) > (pointMidAtten * pointMidPhase) * 0.75f).IsTrue();

		// 4. Clean window cutoff at and beyond stated range R (accounting for float32 reciprocal rounding at exact boundary).
		Engine_VolumetricLightShafts.EvaluateSampleKernel(
			d2: range * range, invStatedR2, softCoreRadius: softCore, cosTheta: 1.0f, anisotropy,
			out float boundaryAtten, out _);
		AssertThat(Mathf.IsEqualApprox(boundaryAtten, 0.0f, 1e-6f)).IsTrue();

		Engine_VolumetricLightShafts.EvaluateSampleKernel(
			d2: (range + 0.01f) * (range + 0.01f), invStatedR2, softCoreRadius: softCore, cosTheta: 1.0f, anisotropy,
			out float edgeAtten, out float edgePhase);
		AssertThat(edgeAtten).IsEqual(0.0f);
		AssertThat(edgePhase).IsEqual(0.0f);
	}
}
