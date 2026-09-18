using System;

namespace Refii.Lighting;

public static class Engine_PostFxState
{
    public const int Exposure = 0;
    public const int Applied = 1;
    public const int Prev = 2;
    public const int SmBlack = 3;
    public const int SmMid = 4;
    public const int SmWhite = 5;
    public const int SmP99 = 6;
    public const int RawBlack = 7;
    public const int RawMean = 8;
    public const int RawWhite = 9;
    public const int RawExp = 10;
    public const int FadeK = 11;
    public const int HasHistory = 12;
    public const int CurveValid = 13;
    public const int Threshold = 14;
    public const int ExposureFrame = 15;
    public const int AppliedFrame = 16;
    public const int HistMinPct = 17;
    public const int HistMean = 18;
    public const int HistMaxPct = 19;
    public const int HistExposureUsed = 20;
    public const int HistTotal = 21;
    public const int HistIdxMin = 22;
    public const int HistIdxAvg = 23;
    public const int HistIdxMax = 24;
    public const int HistIdxLast = 25;
    public const int HistAvgPct = 26;
    public const int HistLast = 27;
    public const int MaterialExposure = 28;
    public const int CurveScale = 29;
    public const int InvWhiteSq = 30;
    public const int RawGain = 31;
    public const int Header = 32;
    public const int Curve = 32;
    public const int Lut = Curve + Engine_PostFxModel.CurveSize;
    public const int Cdf = Lut + Engine_PostFxModel.CurveSize;
    // The flicker trap's four scalars, after the CDF. Mirrored in fable_postfx_reduce.glsl.
    public const int TrapPrevMean = Cdf + Engine_PostFxModel.CurveSize;
    public const int TrapHitFrame = TrapPrevMean + 1;
    public const int TrapHitMean = TrapPrevMean + 2;
    public const int TrapHitPrev = TrapPrevMean + 3;
    public const int Floats = TrapPrevMean + 4;
    public const int Bytes = Floats * sizeof(float);

    public static readonly string[] FieldNames =
    {
        "Exposure", "Applied", "Prev", "SmBlack", "SmMid", "SmWhite", "SmP99",
        "RawBlack", "RawMean", "RawWhite", "RawExp", "FadeK", "HasHistory", "CurveValid",
        "Threshold", "ExposureFrame", "AppliedFrame", "HistMinPct", "HistMean", "HistMaxPct",
        "HistExposureUsed", "HistTotal", "HistIdxMin", "HistIdxAvg", "HistIdxMax", "HistIdxLast",
        "HistAvgPct", "HistLast", "MaterialExposure", "CurveScale", "InvWhiteSq", "RawGain",
    };

    public static float[] Seed()
    {
        var s = new float[Floats];
        s[Exposure] = 4.0f;
        s[Applied] = 4.0f;
        s[Prev] = 4.0f;
        s[SmBlack] = 0.0f;
        s[SmMid] = 0.25f;
        s[SmWhite] = 1.0f;
        s[SmP99] = 1.0f;
        s[RawExp] = 1.0f;
        s[RawGain] = 1.0f;
        s[FadeK] = 1.0f;
        s[ExposureFrame] = 4.0f;
        s[AppliedFrame] = 4.0f;
        s[HistMean] = 0.5f;
        s[HistExposureUsed] = 1.0f;
        s[MaterialExposure] = 1.0f;
        return s;
    }

    public static byte[] SeedBytes()
    {
        float[] seed = Seed();
        var bytes = new byte[Bytes];
        Buffer.BlockCopy(seed, 0, bytes, 0, Bytes);
        return bytes;
    }

    public static void Fill(Engine_PostFxModel.State model, float[] lut, float[] into)
    {
        Array.Clear(into, 0, Math.Min(into.Length, Floats));
        into[Exposure] = model.Exposure;
        into[Applied] = model.Applied;
        into[Prev] = model.Prev;
        into[SmBlack] = model.SmBlack;
        into[SmMid] = model.SmMid;
        into[SmWhite] = model.SmWhite;
        into[SmP99] = model.SmP99;
        into[RawBlack] = model.RawBlack;
        into[RawMean] = model.RawMean;
        into[RawWhite] = model.RawWhite;
        into[RawExp] = model.RawExp;
        into[RawGain] = model.RawGain;
        into[FadeK] = model.FadeK;
        into[HasHistory] = model.HasHistory ? 1.0f : 0.0f;
        into[CurveValid] = model.CurveValid ? 1.0f : 0.0f;
        into[Threshold] = model.Threshold;
        into[ExposureFrame] = model.Exposure;
        into[AppliedFrame] = model.Applied;
        into[HistMinPct] = model.Hist.MinPct;
        into[HistMean] = model.Hist.Mean;
        into[HistMaxPct] = model.Hist.MaxPct;
        into[HistExposureUsed] = model.Hist.ExposureUsed;
        into[HistTotal] = model.Hist.Total;
        into[HistIdxMin] = model.Hist.IdxMin;
        into[HistIdxAvg] = model.Hist.IdxAvg;
        into[HistIdxMax] = model.Hist.IdxMax;
        into[HistIdxLast] = model.Hist.IdxLast;
        into[HistAvgPct] = model.Hist.AvgPct;
        into[HistLast] = model.Hist.Last;
        into[MaterialExposure] = 1.0f;
        Array.Copy(model.Curve, 0, into, Curve, Engine_PostFxModel.CurveSize);
        Array.Copy(lut, 0, into, Lut, Engine_PostFxModel.CurveSize);
    }
}
