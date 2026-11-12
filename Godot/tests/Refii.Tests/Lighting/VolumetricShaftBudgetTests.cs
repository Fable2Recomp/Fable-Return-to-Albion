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
}
