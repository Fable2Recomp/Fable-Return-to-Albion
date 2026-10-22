namespace Refii.Tests.Modding;

using Godot;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Fx;

/// <summary>
/// An effect played on an entity rides the entity's drawn node in the entity's own frame and takes
/// its scale: a blade's burn authored guard to tip stays guard to tip at whatever size the guest
/// states the sword. Godot structs only — no runtime objects.
/// </summary>
[TestSuite]
public class FxEntityFrameTests
{
    private static readonly Vector3 Guard = new(0f, 0.08f, 0f);

    [TestCase]
    public void AnUnscaledBodyPlacesTheOffsetAlongItsOwnAxes()
    {
        var turned = new Basis(Vector3.Up, Mathf.Pi / 2f);
        Transform3D placed = Engine_FxPlayer.InEntityFrame(turned, new Vector3(1f, 2f, 3f), Guard);
        Vector3 expected = new Vector3(1f, 2f, 3f) + (turned * new Vector3(0f, 0f, -0.08f));
        AssertThat(placed.Origin.IsEqualApprox(expected)).IsTrue();
        AssertThat(placed.Basis.Column2.IsEqualApprox(-turned.Column2)).IsTrue();
    }

    [TestCase]
    public void AScaledBodyScalesTheEffectAndItsOffset()
    {
        var turned = new Basis(Vector3.Right, 0.6f);
        Transform3D unit = Engine_FxPlayer.InEntityFrame(turned, Vector3.Zero, Guard);
        Transform3D grown = Engine_FxPlayer.InEntityFrame(turned.Scaled(new Vector3(1.5f, 1.5f, 1.5f)), Vector3.Zero, Guard);
        AssertThat(grown.Origin.IsEqualApprox(unit.Origin * 1.5f)).IsTrue();
        AssertThat(Mathf.IsEqualApprox(grown.Basis.Column0.Length(), 1.5f)).IsTrue();
    }

    [TestCase]
    public void TheLocalTransformIsTheSameWhateverTheBodysSize()
    {
        // Constant under the node: nothing of the body's current scale is baked in.
        AssertThat(Engine_FxPlayer.InEntityLocal(Guard).Basis.IsEqualApprox(
            new Basis(Vector3.Right, Vector3.Up, Vector3.Forward))).IsTrue();
    }
}
