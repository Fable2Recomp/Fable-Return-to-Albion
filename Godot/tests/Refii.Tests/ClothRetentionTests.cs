using GdUnit4;
using Refii.Physics;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The skin-retention weight per engine (<see cref="Engine_ClothSolver.UpdateTimeLuts"/>): Fable 2's
/// pow(attr * scale / 64, dt), Fable 3's attr / 64 per 30 Hz guest step.
/// </summary>
[TestSuite]
public sealed class ClothRetentionTests
{
    [TestCase]
    public void Fable2KeepsPowOfDt()
    {
        ClothInstance inst = ClothInstance.Create();
        Engine_ClothSolver.UpdateTimeLuts(inst, 1f / 60f, 1f);
        AssertFloat(inst.BlendWeight[9].X).IsEqualApprox(System.MathF.Pow(9f / 64f, 1f / 60f), 1e-6f);
    }

    [TestCase]
    public void Fable3KeepsAttrOver64PerGuestStep()
    {
        ClothInstance inst = ClothInstance.Create();
        Engine_ClothSolver.UpdateTimeLuts(inst, 1f / 30f, 2f, perStep: true);
        AssertFloat(inst.BlendWeight[9].X).IsEqualApprox(9f / 64f, 1e-6f);
        AssertFloat(inst.BlendWeight[0].X).IsEqualApprox(0f, 1e-6f);
        AssertFloat(inst.BlendWeight[64].X).IsEqualApprox(1f, 1e-6f);
    }

    [TestCase]
    public void Fable3RetentionPerSecondIsRateIndependent()
    {
        ClothInstance inst = ClothInstance.Create();
        Engine_ClothSolver.UpdateTimeLuts(inst, 1f / 120f, 1f, perStep: true);
        float fourSteps = System.MathF.Pow(inst.BlendWeight[9].X, 4f);
        AssertFloat(fourSteps).IsEqualApprox(9f / 64f, 1e-5f);
    }

    [TestCase]
    public void SwitchingModeRebuildsTheTable()
    {
        ClothInstance inst = ClothInstance.Create();
        Engine_ClothSolver.UpdateTimeLuts(inst, 1f / 30f, 1f);
        Engine_ClothSolver.UpdateTimeLuts(inst, 1f / 30f, 1f, perStep: true);
        AssertFloat(inst.BlendWeight[9].X).IsEqualApprox(9f / 64f, 1e-6f);
    }
}
