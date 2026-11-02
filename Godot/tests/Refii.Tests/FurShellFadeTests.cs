using Godot;
using GdUnit4;
using Refii.Host;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The fur shells' gate: built whenever the colour pass is (`sub_82219AB8`, the product above 0),
/// so a drawn entity loses its coat only at a product of 0; above it the coat takes the body's fade.
/// </summary>
[TestSuite]
public sealed class FurShellFadeTests
{
	private static HostBridge.EntityAppearance Look(float alpha, float cameraAlpha) =>
		new(alpha, cameraAlpha, 1f, Vector4.One, HostBridge.EntityAppearance.DrawableFlag);

	/// <summary>The constructed look and every fade above 0 keep the shells.</summary>
	[TestCase]
	public void AnyOpacityAboveZeroKeepsTheShells()
	{
		AssertThat(HostBridge.EntityAppearance.Constructed.Submitted).IsTrue();
		AssertThat(Look(0.5f, 1f).Submitted).IsTrue();
		AssertThat(Look(1f, 0.25f).Submitted).IsTrue();
		// Under the 1e-4 draw floor and under one BLENDFACTOR step, still above the pass's 0.
		AssertThat(Look(1e-5f, 1f).Submitted).IsTrue();
		AssertThat(Look(1f / 512f, 1f).Submitted).IsTrue();
	}

	/// <summary>A principal under camera alpha 0 is still drawn, but its colour pass is not built.</summary>
	[TestCase]
	public void APrincipalAtCameraAlphaZeroIsDrawnWithoutShells()
	{
		HostBridge.EntityAppearance look = Look(1f, 0f);
		AssertThat(look.Drawn(principal: true)).IsTrue();
		AssertThat(look.Submitted).IsFalse();
	}

	/// <summary>A product of 0, or one that is not a number, builds nothing.</summary>
	[TestCase]
	public void NoProductNoShells()
	{
		AssertThat(Look(0f, 1f).Submitted).IsFalse();
		AssertThat(Look(0f, 0f).Submitted).IsFalse();
		AssertThat(Look(float.NaN, 1f).Submitted).IsFalse();
	}
}
