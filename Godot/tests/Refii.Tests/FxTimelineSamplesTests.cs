using GdUnit4;
using Refii.Fx.Xex;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>A timeline holds its samples only once it has more than one key.</summary>
[TestSuite]
public sealed class FxTimelineSamplesTests
{
    private static readonly float[] Defaults = { 0.25f, 0.5f, 0.75f, 1f };

    private static XexScalarTimeline Scalar(params (float Time, float Value)[] keys)
    {
        var t = new XexScalarTimeline();
        foreach ((float time, float value) in keys) t.Keys.Add(new XexScalarKey { TimeSecs = time, Value = value });
        XexBankParser.BakeScalar(t, 0.25f);
        return t;
    }

    private static XexVecTimeline Vec(params (float Time, float X)[] keys)
    {
        var t = new XexVecTimeline();
        foreach ((float time, float x) in keys) t.Keys.Add(new XexVecKey { TimeSecs = time, X = x, Y = x + 1f, Z = x + 2f });
        XexBankParser.BakeVec(t, Defaults);
        return t;
    }

    [TestCase]
    public void NoKeysNoSamplesTheDefault()
    {
        XexScalarTimeline scalar = Scalar();
        AssertThat(scalar.HasSamples).IsFalse();
        AssertThat(scalar.Samples.Length).IsEqual(0);
        AssertThat(scalar.Eval(12)).IsEqual(0.25f);

        XexVecTimeline vec = Vec();
        AssertThat(vec.HasSamples).IsFalse();
        AssertThat(vec.Samples.Length).IsEqual(0);
        var o = new float[4];
        vec.Eval(12, o);
        AssertThat(o[2]).IsEqual(0.75f);
    }

    [TestCase]
    public void TwoKeysSampleTenTimes()
    {
        XexScalarTimeline scalar = Scalar((0f, 0f), (1f, 9f));
        AssertThat(scalar.HasSamples).IsTrue();
        AssertThat(scalar.Samples.Length).IsEqual(10);
        AssertThat(scalar.Eval(0)).IsEqual(scalar.Samples[0]);
        AssertThat(scalar.Samples[9]).IsEqual(9f);

        XexVecTimeline vec = Vec((0f, 0f), (1f, 9f));
        AssertThat(vec.HasSamples).IsTrue();
        AssertThat(vec.Samples.Length).IsEqual(10);
        AssertThat(vec.Samples[9].Length).IsEqual(4);
        AssertThat(vec.Samples[9][0]).IsEqual(9f);
        var o = new float[4];
        vec.Eval(0, o);
        AssertThat(o[1]).IsEqual(vec.Samples[0][1]);
    }

    [TestCase]
    public void BakingAgainWithFewerKeysFallsBackToTheDefault()
    {
        XexScalarTimeline scalar = Scalar((0f, 0f), (1f, 9f));
        scalar.Keys.RemoveAt(1);
        XexBankParser.BakeScalar(scalar, 0.25f);
        AssertThat(scalar.HasSamples).IsFalse();
        AssertThat(scalar.Eval(5)).IsEqual(0f); // One key: its value is the default.
    }
}
