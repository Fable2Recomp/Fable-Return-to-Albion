using System;
using System.Collections.Generic;

namespace Refii.Fx.Xex;

public static class XexConst
{
    public const uint MagicBaad = 0xBAADF00Du;
    public const uint MagicTimeline = 0x4AD78C59u;
    public const uint MagicChild = 0x37E185B9u;
    public const uint MagicSpline = 0x50B5F001u;

    public const float Fps = 30.0f;
    public const float SecPerFrame = 1.0f / 30.0f;
    public const float Flt8335F018 = 1.0f / 30.0f;

    public static readonly float[] ScalarParamDefaults =
    {
        0.95f, 1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f,
        0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 1.0f, 1.0f, 15.0f,
        1.0f, 64.0f, 50.0f, 0.05f, 0.05f, 0.1f, 0.2f, 0.1f,
        0.7853982f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 20.0f,
    };

    public static readonly float[][] VecParamDefaults =
    {
        new[] { 0f, 0f, 0f, 0f }, new[] { 1f, 1f, 1f, 0f }, new[] { 1f, 1f, 1f, 0f }, new[] { 0f, 0f, 0f, 0f },
    };

    public static int Fctiwz(float f)
    {
        if (float.IsNaN(f)) return int.MinValue;
        if (f >= 2147483648.0f) return int.MaxValue;
        if (f <= -2147483648.0f) return int.MinValue;
        return (int)f;
    }
}

public sealed class XexStream
{
    public byte[] Base;
    public int Pos;
    public bool Overrun;

    public XexStream(byte[] data) { Base = data; }

    public bool Can(int n) => Pos + n <= Base.Length;
    public void Skip(int n) { if (!Can(n)) { Overrun = true; Pos = Base.Length; return; } Pos += n; }
    public void Seek(int p) { Pos = p <= Base.Length ? p : Base.Length; }
    public int Tell() => Pos;

    public byte U8() { if (!Can(1)) { Overrun = true; return 0; } return Base[Pos++]; }

    public uint U32()
    {
        if (!Can(4)) { Overrun = true; Pos = Base.Length; return 0; }
        uint v = ((uint)Base[Pos] << 24) | ((uint)Base[Pos + 1] << 16) | ((uint)Base[Pos + 2] << 8) | Base[Pos + 3];
        Pos += 4;
        return v;
    }

    public int I32() => (int)U32();
    public float F32() => BitConverter.Int32BitsToSingle((int)U32());

    public uint PeekU32()
    {
        if (!Can(4)) return 0;
        return ((uint)Base[Pos] << 24) | ((uint)Base[Pos + 1] << 16) | ((uint)Base[Pos + 2] << 8) | Base[Pos + 3];
    }

    public void Vec3(float[] o) { o[0] = F32(); o[1] = F32(); o[2] = F32(); }
    public void Vec4(float[] o) { o[0] = F32(); o[1] = F32(); o[2] = F32(); o[3] = F32(); }

    public string LenStrNul()
    {
        uint n = U32();
        if (n > 4096 || !Can((int)n + 1)) { Overrun = true; return string.Empty; }
        string s = System.Text.Encoding.ASCII.GetString(Base, Pos, (int)n);
        Pos += (int)n + 1;
        return s;
    }
}

public struct XexScalarKey { public float TimeSecs; public int Frame; public float Value; }
public struct XexVecKey { public float TimeSecs; public int Frame; public float X, Y, Z; }

public sealed class XexScalarTimeline
{
    public bool Present;
    public uint MarkerVersion;
    public bool HadMarker;
    public List<XexScalarKey> Keys = new();
    public bool HasSamples;
    public float[] Samples = new float[10];
    public float InvStep10;
    public float StartSecs = -1.0f;
    public float DefVal;

    public float Eval(int frame)
    {
        if (!HasSamples) return DefVal;
        float f = (frame - StartSecs) * InvStep10;
        int i0 = XexConst.Fctiwz(f); i0 = Math.Max(i0, 0); i0 = Math.Min(i0, 9);
        int i1 = XexConst.Fctiwz(f) + 1; i1 = Math.Max(i1, 0); i1 = Math.Min(i1, 9);
        float frac = f - i0;
        return ((Samples[i1] - Samples[i0]) * frac) + Samples[i0];
    }

    public float EvalSecs(float secs) => Eval((int)(secs * XexConst.Fps));
}

public sealed class XexVecTimeline
{
    public bool Present;
    public uint MarkerVersion;
    public bool HadMarker;
    public List<XexVecKey> Keys = new();
    public bool HasSamples;
    public float[][] Samples = MakeSamples();
    public float InvStep10;
    public float StartSecs = -1.0f;
    public float[] DefVal = new float[4];

    private static float[][] MakeSamples()
    {
        var s = new float[10][];
        for (int i = 0; i < 10; i++) s[i] = new float[4];
        return s;
    }

    public void Eval(int frame, float[] o)
    {
        if (!HasSamples) { for (int k = 0; k < 4; k++) o[k] = DefVal[k]; return; }
        float f = (frame - StartSecs) * InvStep10;
        int i0 = XexConst.Fctiwz(f); i0 = Math.Max(i0, 0); i0 = Math.Min(i0, 9);
        int i1 = XexConst.Fctiwz(f) + 1; i1 = Math.Max(i1, 0); i1 = Math.Min(i1, 9);
        float frac = f - i0;
        for (int k = 0; k < 4; k++) o[k] = ((Samples[i1][k] - Samples[i0][k]) * frac) + Samples[i0][k];
    }
}

public class XexEntityType
{
    public uint TypeId;
    public uint EntityId;
    public uint SubType;
    public uint Version;
    public float[] Pos = new float[3];
    public float[] Quat = { 0f, 0f, 0f, 1f };
    public bool UseLife;
    public int LifetimeFrames;
    public float ProcessRadius = 32.0f;
    public uint TrailingKey;
    public XexScalarTimeline[] Scalar = MakeScalar();
    public XexVecTimeline[] Vec = { new(), new(), new(), new() };
    public int FileBegin, FileEnd;

    private static XexScalarTimeline[] MakeScalar()
    {
        var a = new XexScalarTimeline[28];
        for (int i = 0; i < 28; i++) a[i] = new XexScalarTimeline();
        return a;
    }

    public float EvalScalar(int idx, int frame)
    {
        if (idx < 0 || idx >= 28) return 0f;
        return Scalar[idx].Present ? Scalar[idx].Eval(frame) : XexConst.ScalarParamDefaults[idx];
    }
}

public class XexEmitterType : XexEntityType
{
    public bool PositionalInteractable;
    public int MaxActiveParticles = -1;
    public float InitialNumParticles = 1.0f;
    public float StartFadeIn = 0.8f, EndFadeIn = 1.0f, StartFadeOut = 25.0f, EndFadeOut = 32.0f;
    public bool SortParticles;
    public bool ForceFullSizeRenderBuffer;
    public bool AttachToRoot;
}

public enum XexEmitterShape : byte { Point = 0, Sphere = 1, Disc = 2, Mesh = 3 }

public sealed class XexStandardEmitterType : XexEmitterType
{
    public bool HadMarker;
    public uint WrapperVersion;
    public XexEmitterShape Shape = XexEmitterShape.Point;
    public float ShapeRadius = 0.1f;
    public bool ShapeFlagA;
    public bool ShapeFlagB;
    public bool ReleaseTrail;
    public bool GlobalCollider;
    public float CollisionRestitution = 1.0f;
}

public sealed class XexSplineObject
{
    public bool HadMarker;
    public uint Version;
    public List<float[]> Points = new();
    public bool RandomGenerator;
    public bool ReverseFlow;
    public float Rate = 0.01f;
    public bool LoopedSpline;
    public bool UseRandomStartPoint;
    public bool TreatAsStraightSegments;
    public bool ScaleEmissionRatesBySplineLength;
}

public sealed class XexSplineEmitterType : XexEmitterType
{
    public XexSplineObject Spline = new();
    public int AnimationMode;
    public int NumSplineSegments = 1;
    public float RandomiseAnimMagnitude = 0.5f;
    public float SmoothAnimationSpeed;
    public float JerkyAnimationRate;
    public bool RandomiseAnimMagnitudeFlag;
    public bool RandomiseAnimationRates;
}

public sealed class XexSplineEntityType : XexEntityType
{
    public bool PositionalInteractable;
    public XexSplineObject Spline = new();
}

public sealed class XexOrbitalEntityType : XexEntityType
{
    public bool PositionalInteractable;
    public float AngularRotationRate;
}

public sealed class XexAttractorEntityType : XexEntityType
{
    public bool PositionalInteractable;
    public bool GlobalParticleAttractor;
}

public sealed class XexPointLightEntityType : XexEntityType
{
    public bool PositionalInteractable;
    public uint HaloTextureHash;
}

public sealed class XexSpotLightEntityType : XexEntityType
{
    public bool PositionalInteractable;
    public uint SpotLightTextureHash;
    public uint HaloTextureHash;
    public bool SpotLightCastsShadows;
}

public sealed class XexRootEntityType : XexEntityType { }

public static class XexTag
{
    public const uint Stub = 0x1, DiffuseAlpha = 0x2, DiffuseColor = 0x4, DiffuseTexture = 0x8, Size = 0x10,
        SpriteNumPoints = 0x20, SrcBlendMode = 0x40, DestBlendMode = 0x80, BlendOp = 0x100, RotationAngle = 0x200,
        FloatSize = 0x400, NumSegments = 0x800, MeshID = 0x1000, RandomSize = 0x2000, RotationOrient = 0x4000,
        SelfIllumFactor = 0x8000, GlobalFrameScale = 0x10000, AlignmentQuat = 0x20000;
}

public sealed class XexMaterial
{
    public uint Id;
    public uint Hdr;
    public uint Mask;
    public uint TrailingId;
    public List<uint> TagOrder = new();

    public float Alpha = 1.0f;
    public float[] ColorFile = { 1f, 1f, 1f };
    public float[] ColorSq = { 1f, 1f, 1f };
    public uint TextureHash;
    public string TexturePath = string.Empty;
    public uint NumXDiv = 1, NumYDiv = 1;
    public bool RandomInitialFrame;
    public float FrameChangeSpeed;
    public bool IsDisplacementTexture;
    public bool ApplySaturationMask;
    public float SoftParticleDepthContrast;
    public float[] Dimensions = { 1f, 1f };
    public uint NumSpritePoints = 4;
    public uint SrcBlend = 1, DstBlend = 5, BlendOp;
    public float RotSpeed, RotSpread;
    public bool RotInitialRandom;
    public float FloatSize = 1.0f;
    public uint NumSegments = 1;
    public uint MeshHash;
    public string MeshPath = string.Empty;
    public bool IsDisplacementMesh;
    public float MrsChangeFreq, MrsMin = 1f, MrsRange;
    public float RsChangeFreq; public float[] RsMin = { 1f, 1f }; public float[] RsRange = { 0f, 0f }; public bool RsLinked = true;
    public float OrSpeed, OrSpread; public bool OrInitialRandomAngle; public float[] OrAxis = { 1f, 0f, 0f }; public bool OrInitialRandomAxis = true;
    public float SelfIllum = 1.0f;
    public float[] GlobalFrameScale = { 1f, 1f };
    public float[] AlignQuat = { 0f, 0f, 1f, 0f };

    public bool Has(uint tag) => (Mask & tag) != 0;
}

public sealed class XexSystemChild
{
    public bool HadMarker;
    public uint MarkerVersion;
    public float StartSecs;
    public int StartFrames;
    public uint MaterialId;
    public XexMaterial Material = new();
}

public sealed class XexTexEntry
{
    public uint TexHash;
    public float TStartNorm;
    public float Zero;
    public float FrameChangeSpeed;
    public sbyte NumXDiv = 1, NumYDiv = 1;
    public byte RandomInitialFrame;
    public byte IsDisplacement;
    public string Path = string.Empty;
}

public sealed class XexSegmentRecord
{
    public float InvSpan;
    public float[] Rgb = { 1f, 1f, 1f };
    public float Alpha = 1.0f;
    public float[] Dims = { 0f, 0f };
    public float[] GlobalFrameScale = { 0f, 0f };
    public float SelfIllum = 1.0f;
    public uint NumSpritePoints;
    public uint SrcBlend = 1, DstBlend = 5, BlendOp;
    public float SoftDepth;
    public byte IsDispTex, SatMask;
    public byte HasAlignQuat;
    public float[] AlignQuat = { 0f, 0f, 0f, 1f };
    public float RotSpeed, RotSpread;
    public byte RotInitRandom;
    public byte RandomInitialFrame;
    public float FrameChangeSpeed;
    public int RsFreqFrames;
    public float[] RsMin = { 0f, 0f };
    public float[] RsRange = { 1f, 1f };
    public byte RsLinked = 1;
    public float Size = 1.0f;
    public int NumSegments;
    public uint MeshId;
    public string MeshPath = string.Empty;
    public byte IsDispMesh;
    public float[] OrAxis = { 0f, 0f, 0f };
    public byte OrInitRandomAxis;
    public float MeshScale = 1.0f;
    public float MrsMin = 1.0f, MrsRange;
    public int StartFrames;
    public string TexPath = string.Empty;
    public uint TexHash;
}

public sealed class XexSystemDef
{
    public uint Id;
    public uint MaterialType = 1;
    public List<XexSystemChild> Children = new();
    public float LoopStartFrames;
    public float LoopEndFrames;
    public uint LoopCount;
    public uint TrailingId;

    public List<XexSegmentRecord> Segments = new();
    public List<XexTexEntry> TexList = new();
    public List<int> ChildOrder = new();
    public List<float> StartFramesVec = new();
}

public struct XexEffectItem { public uint EntityTypeId; public uint MaterialTimelineId; public uint TrailMaterialTimelineId; }
public sealed class XexEffectTypeVector { public uint Priority; public List<XexEffectItem> Items = new(); }
public sealed class XexEffectCollection
{
    public uint Id;
    public bool ZSortEmitters;
    public uint UpdateFrequency;
    public List<XexEffectTypeVector> TypeVectors = new();
    public uint TrailingId;
}

public struct XexEntityRef { public uint TypeId; public uint EntityId; public uint SubType; public int Index; }

public sealed class XexBank
{
    public bool Ok;
    public string Error = string.Empty;
    public uint BankVersion;
    public bool HadHeaderMarker;
    public uint HeaderFlag;
    public float GlobalFloat;

    public uint Section2Param, Section3Param, Section4Param, Section5Version;
    public int Section2Offset, Section3Offset, Section4Offset, Section5Offset, EndOffset;

    public List<XexMaterial> Materials = new();
    public List<XexSystemDef> Systems = new();
    public List<XexStandardEmitterType> StdEmitters = new();
    public List<XexSplineEmitterType> SplineEmitters = new();
    public List<XexSplineEntityType> SplineEntities = new();
    public List<XexOrbitalEntityType> Orbitals = new();
    public List<XexAttractorEntityType> Attractors = new();
    public List<XexPointLightEntityType> PointLights = new();
    public List<XexSpotLightEntityType> SpotLights = new();
    public List<XexRootEntityType> Roots = new();
    public List<XexEntityRef> EntityOrder = new();
    public List<XexEffectCollection> Effects = new();

    public Dictionary<uint, int> MaterialById = new();
    public Dictionary<uint, int> SystemById = new();
    public Dictionary<uint, XexEntityRef> EntityById = new();
    public Dictionary<uint, int> EffectById = new();

    public XexEntityType? Entity(uint id)
    {
        if (!EntityById.TryGetValue(id, out XexEntityRef r)) return null;
        return r.TypeId switch
        {
            0 => StdEmitters[r.Index],
            3 => Orbitals[r.Index],
            4 => Attractors[r.Index],
            5 => SplineEntities[r.Index],
            6 => Roots[r.Index],
            7 => PointLights[r.Index],
            8 => SpotLights[r.Index],
            9 => SplineEmitters[r.Index],
            _ => null,
        };
    }

    public XexSystemDef? System(uint id) => SystemById.TryGetValue(id, out int i) ? Systems[i] : null;
    public XexEffectCollection? Effect(uint id) => EffectById.TryGetValue(id, out int i) ? Effects[i] : null;
}

public static class XexBankParser
{
    public static uint Fnv1LowerResourceName(string s)
    {
        uint h = 0x811C9DC5u;
        foreach (char ch in s)
        {
            byte c = (byte)ch;
            if (c >= (byte)'A' && c <= (byte)'Z') c = (byte)(c + 32);
            if (c == (byte)'/') c = (byte)'\\';
            h *= 0x01000193u;
            h ^= c;
        }

        return h;
    }

    private struct ScalarNode { public int Frame; public float Value; }
    private struct VecNode { public int Frame; public float X, Y, Z; }

    private static float EvalListAtFrame(List<ScalarNode> l, float defVal, int frame)
    {
        int nxt = -1;
        for (int i = l.Count - 1; i >= 0; i--)
        {
            ScalarNode n = l[i];
            if (frame >= n.Frame)
            {
                if (nxt < 0) return n.Value;
                ScalarNode nn = l[nxt];
                return ((float)(frame - n.Frame) / (nn.Frame - n.Frame) * (nn.Value - n.Value)) + n.Value;
            }

            nxt = i;
        }

        return defVal;
    }

    private static void EvalVecListAtFrame(List<VecNode> l, float[] defVal, int frame, float[] o)
    {
        int nxt = -1;
        for (int i = l.Count - 1; i >= 0; i--)
        {
            VecNode n = l[i];
            if (frame >= n.Frame)
            {
                if (nxt < 0) { o[0] = n.X; o[1] = n.Y; o[2] = n.Z; o[3] = 0; return; }
                VecNode nn = l[nxt];
                float w = (float)(frame - n.Frame) / (nn.Frame - n.Frame);
                o[0] = (w * (nn.X - n.X)) + n.X; o[1] = (w * (nn.Y - n.Y)) + n.Y; o[2] = (w * (nn.Z - n.Z)) + n.Z; o[3] = 0;
                return;
            }

            nxt = i;
        }

        o[0] = defVal[0]; o[1] = defVal[1]; o[2] = defVal[2]; o[3] = defVal[3];
    }

    private static void ReadScalarTimeline(XexStream s, XexScalarTimeline t, float defVal)
    {
        t.Present = false; t.HadMarker = false; t.MarkerVersion = 0; t.Keys.Clear(); t.HasSamples = false;
        t.InvStep10 = 0f; t.StartSecs = -1.0f; t.DefVal = defVal;
        int mark = s.Tell();
        uint magic = s.U32();
        if (magic == XexConst.MagicTimeline) { t.HadMarker = true; t.MarkerVersion = s.U32(); }
        else s.Seek(mark);
        int count = s.I32();
        if (count < 0 || count > 100000) { s.Overrun = true; return; }
        var list = new List<ScalarNode>(count);
        for (int i = 0; i < count && !s.Overrun; i++)
        {
            float tsec = s.F32();
            int frame = XexConst.Fctiwz(XexConst.Fps * tsec);
            float val = s.F32();
            list.Add(new ScalarNode { Frame = frame, Value = val });
            t.Keys.Add(new XexScalarKey { TimeSecs = tsec, Frame = frame, Value = val });
        }

        if (s.Overrun) return;
        if (count == 0) t.StartSecs = -1.0f;
        else
        {
            t.StartSecs = XexConst.SecPerFrame * list[0].Frame;
            if (count == 1) t.DefVal = list[0].Value;
        }

        list = StableSortByFrame(list);
        if (list.Count > 1)
        {
            int span = list[^1].Frame - list[0].Frame;
            float spanF = span;
            float step = spanF * 0.11111111f;
            for (int i = 0; i < 10; i++) t.Samples[i] = EvalListAtFrame(list, t.DefVal, XexConst.Fctiwz(i * step));
            t.InvStep10 = (spanF > 0.0f ? 1.0f / spanF : 0.0f) * 10.0f;
            t.HasSamples = true;
        }

        t.Present = t.StartSecs >= 0.0f;
    }

    private static List<ScalarNode> StableSortByFrame(List<ScalarNode> l)
    {
        var idx = new List<int>(l.Count);
        for (int i = 0; i < l.Count; i++) idx.Add(i);
        idx.Sort((a, b) => l[a].Frame != l[b].Frame ? l[a].Frame.CompareTo(l[b].Frame) : a.CompareTo(b));
        var o = new List<ScalarNode>(l.Count);
        foreach (int i in idx) o.Add(l[i]);
        return o;
    }

    private static List<VecNode> StableSortByFrame(List<VecNode> l)
    {
        var idx = new List<int>(l.Count);
        for (int i = 0; i < l.Count; i++) idx.Add(i);
        idx.Sort((a, b) => l[a].Frame != l[b].Frame ? l[a].Frame.CompareTo(l[b].Frame) : a.CompareTo(b));
        var o = new List<VecNode>(l.Count);
        foreach (int i in idx) o.Add(l[i]);
        return o;
    }

    private static void ReadVecTimeline(XexStream s, XexVecTimeline t, float[] defVal)
    {
        t.Present = false; t.HadMarker = false; t.MarkerVersion = 0; t.Keys.Clear(); t.HasSamples = false;
        t.InvStep10 = 0f; t.StartSecs = -1.0f;
        for (int k = 0; k < 4; k++) t.DefVal[k] = defVal[k];
        int mark = s.Tell();
        uint magic = s.U32();
        if (magic == XexConst.MagicTimeline) { t.HadMarker = true; t.MarkerVersion = s.U32(); }
        else s.Seek(mark);
        int count = s.I32();
        if (count < 0 || count > 100000) { s.Overrun = true; return; }
        var list = new List<VecNode>(count);
        var v = new float[3];
        for (int i = 0; i < count && !s.Overrun; i++)
        {
            float tsec = s.F32();
            int frame = XexConst.Fctiwz(XexConst.Fps * tsec);
            s.Vec3(v);
            list.Add(new VecNode { Frame = frame, X = v[0], Y = v[1], Z = v[2] });
            t.Keys.Add(new XexVecKey { TimeSecs = tsec, Frame = frame, X = v[0], Y = v[1], Z = v[2] });
        }

        if (s.Overrun) return;
        if (count == 0) t.StartSecs = -1.0f;
        else
        {
            t.StartSecs = XexConst.SecPerFrame * list[0].Frame;
            if (count == 1) { t.DefVal[0] = list[0].X; t.DefVal[1] = list[0].Y; t.DefVal[2] = list[0].Z; t.DefVal[3] = 0; }
        }

        list = StableSortByFrame(list);
        if (list.Count > 1)
        {
            int span = list[^1].Frame - list[0].Frame;
            float spanF = span;
            float step = spanF * 0.11111111f;
            for (int i = 0; i < 10; i++) EvalVecListAtFrame(list, t.DefVal, XexConst.Fctiwz(i * step), t.Samples[i]);
            t.InvStep10 = (spanF > 0.0f ? 1.0f / spanF : 0.0f) * 10.0f;
            t.HasSamples = true;
        }

        t.Present = t.StartSecs >= 0.0f;
    }

    private static void LoadScalarParam(XexStream s, XexEntityType e, int idx)
    {
        if (idx < 0 || idx >= 28) { s.Overrun = true; return; }
        ReadScalarTimeline(s, e.Scalar[idx], XexConst.ScalarParamDefaults[idx]);
    }

    private static void LoadVecParam(XexStream s, XexEntityType e, int idx)
    {
        if (idx < 0 || idx >= 4) { s.Overrun = true; return; }
        ReadVecTimeline(s, e.Vec[idx], XexConst.VecParamDefaults[idx]);
    }

    private static void EntityTypeLoadBin(XexStream s, XexEntityType e)
    {
        e.Version = s.U32();
        s.Vec3(e.Pos);
        s.Vec4(e.Quat);
        e.UseLife = s.U8() != 0;
        int lifeFrames = s.I32();
        e.LifetimeFrames = XexConst.Fctiwz((XexConst.Fps * XexConst.Flt8335F018) * lifeFrames);
        float r = s.F32();
        if (e.Version == 0 && r > 32.0f) r = 32.0f;
        e.ProcessRadius = r;
        e.TrailingKey = s.U32();
    }

    private static void EmitterTypeLoadBin(XexStream s, XexEmitterType e, uint bankVersion)
    {
        e.PositionalInteractable = s.U8() != 0;
        LoadScalarParam(s, e, 2);
        LoadScalarParam(s, e, 3);
        e.MaxActiveParticles = s.I32();
        e.InitialNumParticles = s.F32();
        e.StartFadeIn = s.F32();
        e.EndFadeIn = s.F32();
        e.StartFadeOut = s.F32();
        e.EndFadeOut = s.F32();
        e.SortParticles = s.U8() != 0;
        e.ForceFullSizeRenderBuffer = false;
        if (bankVersion >= 2) e.ForceFullSizeRenderBuffer = s.U8() != 0;
        e.AttachToRoot = s.U8() != 0;
        EntityTypeLoadBin(s, e);
    }

    private static void StandardEmitterLoadBin(XexStream s, XexStandardEmitterType e, uint bankVersion, uint ver)
    {
        LoadScalarParam(s, e, 4);
        LoadScalarParam(s, e, 5);
        LoadVecParam(s, e, 0);
        LoadScalarParam(s, e, 6);
        LoadScalarParam(s, e, 7);
        LoadScalarParam(s, e, 8);
        LoadScalarParam(s, e, 9);
        LoadScalarParam(s, e, 10);
        LoadScalarParam(s, e, 11);
        LoadVecParam(s, e, 1);
        LoadScalarParam(s, e, 12);
        LoadScalarParam(s, e, 13);
        LoadVecParam(s, e, 2);
        LoadScalarParam(s, e, 14);
        e.ReleaseTrail = s.U8() != 0;
        if ((int)ver >= 3)
        {
            e.GlobalCollider = s.U8() != 0;
            e.CollisionRestitution = s.F32();
        }

        EmitterTypeLoadBin(s, e, bankVersion);
    }

    private static void StdEmitterDeserialize(XexStream s, XexStandardEmitterType e, uint bankVersion)
    {
        int mark = s.Tell();
        uint magic = s.U32();
        uint ver = 0;
        if (magic == XexConst.MagicBaad)
        {
            e.HadMarker = true;
            ver = s.U32();
            e.WrapperVersion = ver;
            switch (e.Shape)
            {
                case XexEmitterShape.Point: break;
                case XexEmitterShape.Sphere:
                case XexEmitterShape.Disc:
                    e.ShapeRadius = s.F32();
                    e.ShapeFlagA = s.U8() != 0;
                    e.ShapeFlagB = s.U8() != 0;
                    break;
                case XexEmitterShape.Mesh:
                    e.ShapeFlagA = s.U8() != 0;
                    break;
            }
        }
        else
        {
            s.Seek(mark);
        }

        StandardEmitterLoadBin(s, e, bankVersion, ver);
    }

    private static void MaterialLoadBinary(XexStream s, XexMaterial m, uint bankVersion)
    {
        m.Hdr = s.U32();
        int count = s.I32();
        if (count < 0 || count > 64) { s.Overrun = true; return; }
        bool hasMesh = false;
        for (int i = 0; i < count && !s.Overrun; i++)
        {
            uint tag = s.U32();
            m.TagOrder.Add(tag);
            switch (tag)
            {
                case XexTag.Stub: break;
                case XexTag.DiffuseAlpha: m.Alpha = s.F32(); break;
                case XexTag.DiffuseColor:
                    s.Vec3(m.ColorFile);
                    // THE GUEST SQUARES HERE TOO, exactly once, at bank load:
                    // CMaterialParamDiffuseColor::ReadStream (sub_8321AC90) does
                    // `vmulfp128 v12, v13, v0` on the authored colour and the squared store wins
                    // at param+16; the upload to g_KeyFrameColors (sub_821CA108 -> binding 0x151)
                    // is verbatim from there. Only the colour class squares - scalars store raw.
                    for (int k = 0; k < 3; k++) m.ColorSq[k] = m.ColorFile[k] * m.ColorFile[k];
                    break;
                case XexTag.DiffuseTexture:
                    m.TextureHash = s.U32();
                    m.TexturePath = s.LenStrNul();
                    m.NumXDiv = s.U32();
                    m.NumYDiv = s.U32();
                    m.RandomInitialFrame = s.U8() != 0;
                    m.FrameChangeSpeed = s.F32();
                    m.IsDisplacementTexture = s.U8() != 0;
                    m.ApplySaturationMask = s.U8() != 0;
                    if (bankVersion < 2) m.SoftParticleDepthContrast = s.U8() != 0 ? 0.5f : 0.0f;
                    else m.SoftParticleDepthContrast = s.F32();
                    break;
                case XexTag.Size: m.Dimensions[0] = s.F32(); m.Dimensions[1] = s.F32(); break;
                case XexTag.SpriteNumPoints: m.NumSpritePoints = s.U32(); break;
                case XexTag.SrcBlendMode: m.SrcBlend = s.U32(); break;
                case XexTag.DestBlendMode: m.DstBlend = s.U32(); break;
                case XexTag.BlendOp: m.BlendOp = s.U32(); break;
                case XexTag.RotationAngle:
                    m.RotSpeed = s.F32(); m.RotSpread = s.F32(); m.RotInitialRandom = s.U8() != 0; break;
                case XexTag.FloatSize: m.FloatSize = s.F32(); break;
                case XexTag.NumSegments: m.NumSegments = s.U32(); break;
                case XexTag.MeshID:
                    m.MeshHash = s.U32();
                    m.MeshPath = s.LenStrNul();
                    m.IsDisplacementMesh = s.U8() != 0;
                    hasMesh = true;
                    break;
                case XexTag.RandomSize:
                    if (hasMesh)
                    {
                        m.MrsChangeFreq = s.F32(); m.MrsMin = s.F32(); m.MrsRange = s.F32();
                    }
                    else
                    {
                        m.RsChangeFreq = s.F32();
                        m.RsMin[0] = s.F32(); m.RsMin[1] = s.F32();
                        m.RsRange[0] = s.F32(); m.RsRange[1] = s.F32();
                        m.RsLinked = s.U8() != 0;
                    }

                    break;
                case XexTag.RotationOrient:
                    m.OrSpeed = s.F32(); m.OrSpread = s.F32(); m.OrInitialRandomAngle = s.U8() != 0;
                    s.Vec3(m.OrAxis);
                    m.OrInitialRandomAxis = s.U8() != 0;
                    break;
                case XexTag.SelfIllumFactor: m.SelfIllum = s.F32(); break;
                case XexTag.GlobalFrameScale: m.GlobalFrameScale[0] = s.F32(); m.GlobalFrameScale[1] = s.F32(); break;
                case XexTag.AlignmentQuat: s.Vec4(m.AlignQuat); break;
                default: s.Overrun = true; return;
            }

            m.Mask |= tag;
        }

        m.TrailingId = s.U32();
    }

    private static void SplineObjectLoadBin(XexStream s, XexSplineObject o, bool skipPoints)
    {
        int mark = s.Tell();
        uint ver = 0;
        uint magic = s.U32();
        if (magic == XexConst.MagicSpline) { o.HadMarker = true; ver = s.U32(); }
        else s.Seek(mark);
        o.Version = ver;
        if (!skipPoints)
        {
            uint n = s.U32();
            if (n > 65536) { s.Overrun = true; return; }
            o.Points.Clear();
            for (uint i = 0; i < n && !s.Overrun; i++) { var p = new float[3]; s.Vec3(p); o.Points.Add(p); }
        }

        o.RandomGenerator = s.U8() != 0;
        o.ReverseFlow = s.U8() != 0;
        o.Rate = s.F32();
        o.LoopedSpline = s.U8() != 0;
        o.UseRandomStartPoint = s.U8() != 0;
        if (ver >= 3) o.TreatAsStraightSegments = s.U8() != 0;
        if (ver >= 4) o.ScaleEmissionRatesBySplineLength = s.U8() != 0;
    }

    private static void OrbitalLoadBin(XexStream s, XexOrbitalEntityType e)
    {
        e.PositionalInteractable = s.U8() != 0;
        LoadScalarParam(s, e, 25);
        e.AngularRotationRate = s.F32();
        LoadScalarParam(s, e, 0);
        LoadScalarParam(s, e, 1);
        EntityTypeLoadBin(s, e);
    }

    private static void AttractorLoadBin(XexStream s, XexAttractorEntityType e)
    {
        e.PositionalInteractable = s.U8() != 0;
        LoadScalarParam(s, e, 26);
        LoadScalarParam(s, e, 27);
        e.GlobalParticleAttractor = s.U8() != 0;
        LoadScalarParam(s, e, 0);
        LoadScalarParam(s, e, 1);
        EntityTypeLoadBin(s, e);
    }

    private static void SplineEntityLoadBin(XexStream s, XexSplineEntityType e)
    {
        e.PositionalInteractable = s.U8() != 0;
        SplineObjectLoadBin(s, e.Spline, false);
        LoadScalarParam(s, e, 0);
        LoadScalarParam(s, e, 1);
        EntityTypeLoadBin(s, e);
    }

    private static void PointLightLoadBin(XexStream s, XexPointLightEntityType e)
    {
        e.PositionalInteractable = s.U8() != 0;
        e.HaloTextureHash = s.U32();
        LoadVecParam(s, e, 3);
        for (int idx = 15; idx <= 23; idx++) LoadScalarParam(s, e, idx);
        LoadScalarParam(s, e, 0);
        LoadScalarParam(s, e, 1);
        EntityTypeLoadBin(s, e);
    }

    private static void SpotLightLoadBin(XexStream s, XexSpotLightEntityType e)
    {
        e.PositionalInteractable = s.U8() != 0;
        e.SpotLightTextureHash = s.U32();
        e.HaloTextureHash = s.U32();
        e.SpotLightCastsShadows = s.U8() != 0;
        LoadVecParam(s, e, 3);
        LoadScalarParam(s, e, 15);
        LoadScalarParam(s, e, 24);
        for (int idx = 16; idx <= 23; idx++) LoadScalarParam(s, e, idx);
        LoadScalarParam(s, e, 0);
        LoadScalarParam(s, e, 1);
        EntityTypeLoadBin(s, e);
    }

    private static void SplineEmitterLoadBin(XexStream s, XexSplineEmitterType e, uint bankVersion)
    {
        e.Spline.RandomGenerator = true;
        e.Spline.UseRandomStartPoint = true;
        e.Spline.TreatAsStraightSegments = true;
        EmitterTypeLoadBin(s, e, bankVersion);
        SplineObjectLoadBin(s, e.Spline, true);
        e.AnimationMode = s.I32();
        e.NumSplineSegments = s.I32();
        e.RandomiseAnimMagnitude = s.F32();
        e.SmoothAnimationSpeed = s.F32();
        e.JerkyAnimationRate = s.F32();
        e.RandomiseAnimMagnitudeFlag = s.U8() != 0;
        e.RandomiseAnimationRates = s.U8() != 0;
        if (e.NumSplineSegments <= 0) e.NumSplineSegments = 1;
    }

    private static void TimedMaterialLoadBin(XexStream s, XexSystemChild c, uint bankVersion)
    {
        int mark = s.Tell();
        uint peek = s.U32();
        if (peek == XexConst.MagicChild) { c.HadMarker = true; c.MarkerVersion = s.U32(); }
        else s.Seek(mark);
        c.StartSecs = s.F32();
        int frames = XexConst.Fctiwz(XexConst.Fps * c.StartSecs);
        c.StartFrames = frames;
        if (c.StartSecs > 0.0f && frames == 0) c.StartFrames = 1;
        c.MaterialId = s.U32();
        MaterialLoadBinary(s, c.Material, bankVersion);
        c.Material.Id = c.MaterialId;
    }

    private static void LoopInfoLoadBin(XexStream s, XexSystemDef sys)
    {
        float startSec = s.F32();
        float oldEnd = sys.LoopEndFrames;
        sys.LoopStartFrames = XexConst.Fps * startSec;
        if (oldEnd < sys.LoopStartFrames) sys.LoopEndFrames = sys.LoopStartFrames + 1.0f;
        float endSec = s.F32();
        sys.LoopEndFrames = XexConst.Fps * endSec;
        if (sys.LoopEndFrames < sys.LoopStartFrames) sys.LoopEndFrames = sys.LoopStartFrames + 1.0f;
        sys.LoopCount = s.U32();
    }

    private static void MaterialTimelineLoadBin(XexStream s, XexSystemDef sys, uint bankVersion)
    {
        sys.MaterialType = s.U32();
        int n = s.I32();
        if (n < 0 || n > 4096) { s.Overrun = true; return; }
        sys.Children.Clear();
        for (int i = 0; i < n && !s.Overrun; i++)
        {
            var c = new XexSystemChild();
            TimedMaterialLoadBin(s, c, bankVersion);
            sys.Children.Add(c);
        }

        if (s.Overrun) return;
        BuildSegments(sys);
        LoopInfoLoadBin(s, sys);
        sys.TrailingId = s.U32();
    }

    private static void EffectCollectionLoadBin(XexStream s, XexEffectCollection e, uint sectionVersion)
    {
        if (sectionVersion >= 2) e.ZSortEmitters = s.U8() != 0;
        if (sectionVersion >= 3) e.UpdateFrequency = s.U32();
        int n = s.I32();
        if (n < 0 || n > 4096) { s.Overrun = true; return; }
        for (int i = 0; i < n && !s.Overrun; i++)
        {
            var tv = new XexEffectTypeVector { Priority = s.U32() };
            int m = s.I32();
            if (m < 0 || m > 4096) { s.Overrun = true; return; }
            for (int j = 0; j < m && !s.Overrun; j++)
            {
                tv.Items.Add(new XexEffectItem { EntityTypeId = s.U32(), MaterialTimelineId = s.U32(), TrailMaterialTimelineId = s.U32() });
            }

            e.TypeVectors.Add(tv);
        }

        e.TrailingId = s.U32();
    }

    public static void BuildSegments(XexSystemDef sys)
    {
        sys.Segments.Clear();
        sys.TexList.Clear();
        sys.ChildOrder.Clear();
        sys.StartFramesVec.Clear();
        int n = sys.Children.Count;
        if (n == 0) return;
        for (int i = 0; i < n; i++) sys.ChildOrder.Add(i);
        sys.ChildOrder.Sort((a, b) =>
            sys.Children[a].StartFrames != sys.Children[b].StartFrames
                ? sys.Children[a].StartFrames.CompareTo(sys.Children[b].StartFrames)
                : a.CompareTo(b));

        int lastF = sys.Children[sys.ChildOrder[^1]].StartFrames;
        float totalF = lastF;
        for (int i = 0; i < n; i++) sys.Segments.Add(new XexSegmentRecord());
        uint mode = sys.MaterialType;

        for (int i = 0; i < n; i++)
        {
            XexSystemChild child = sys.Children[sys.ChildOrder[i]];
            XexMaterial m = child.Material;
            XexSegmentRecord rec = sys.Segments[i];
            XexSegmentRecord? prev = i > 0 ? sys.Segments[i - 1] : null;
            rec.StartFrames = child.StartFrames;
            if (mode == 1)
            {
                rec.Dims[0] = rec.Dims[1] = 0f;
                rec.GlobalFrameScale[0] = rec.GlobalFrameScale[1] = 0f;
                rec.SelfIllum = 1f;
                rec.NumSpritePoints = 0;
                rec.RsMin[0] = rec.RsMin[1] = 0f;
                rec.RsRange[0] = rec.RsRange[1] = 1f;
                rec.RsLinked = 1;
            }
            else if (mode == 2 || mode == 3)
            {
                rec.Size = 1f; rec.SelfIllum = 1f; rec.NumSegments = 0;
            }
            else if (mode == 4)
            {
                rec.MeshId = Fnv1LowerResourceName("ILLEGAL_MESH_PARTICLE_ID");
                rec.SelfIllum = 1f;
                rec.MeshScale = 1f; rec.MrsMin = 1f; rec.MrsRange = 0f;
            }

            void TexSideEffect(bool mode1)
            {
                var e = new XexTexEntry
                {
                    TexHash = m.TextureHash,
                    TStartNorm = totalF != 0f ? child.StartFrames / totalF : 0f,
                    FrameChangeSpeed = m.FrameChangeSpeed,
                    NumXDiv = (sbyte)m.NumXDiv, NumYDiv = (sbyte)m.NumYDiv,
                    RandomInitialFrame = (byte)(m.RandomInitialFrame ? 1 : 0),
                    IsDisplacement = mode1 ? (byte)0 : (byte)(m.IsDisplacementTexture ? 1 : 0),
                    Path = m.TexturePath,
                };
                rec.TexHash = m.TextureHash;
                rec.TexPath = m.TexturePath;
                if (mode1)
                {
                    if (sys.TexList.Count == 0)
                    {
                        sys.TexList.Add(e);
                        rec.RandomInitialFrame = (byte)(m.RandomInitialFrame ? 1 : 0);
                    }
                    else
                    {
                        XexTexEntry last = sys.TexList[^1];
                        if (last.TexHash == e.TexHash && last.NumXDiv == e.NumXDiv && last.NumYDiv == e.NumYDiv)
                            rec.RandomInitialFrame = prev is not null ? prev.RandomInitialFrame : (byte)0;
                        else { sys.TexList.Add(e); rec.RandomInitialFrame = (byte)(m.RandomInitialFrame ? 1 : 0); }
                        rec.FrameChangeSpeed = m.FrameChangeSpeed;
                    }

                    rec.IsDispTex = (byte)(m.IsDisplacementTexture ? 1 : 0);
                    rec.SatMask = (byte)(m.ApplySaturationMask ? 1 : 0);
                    rec.SoftDepth = m.SoftParticleDepthContrast;
                }
                else
                {
                    bool differs = sys.TexList.Count == 0;
                    if (!differs)
                    {
                        XexTexEntry last = sys.TexList[^1];
                        differs = !(last.TexHash == e.TexHash && last.TStartNorm == e.TStartNorm && last.Zero == e.Zero &&
                                    last.FrameChangeSpeed == e.FrameChangeSpeed && last.NumXDiv == e.NumXDiv &&
                                    last.NumYDiv == e.NumYDiv && last.RandomInitialFrame == e.RandomInitialFrame &&
                                    last.IsDisplacement == e.IsDisplacement);
                    }

                    if (differs) sys.TexList.Add(e);
                    rec.RandomInitialFrame = (byte)(m.RandomInitialFrame ? 1 : 0);
                    rec.FrameChangeSpeed = m.FrameChangeSpeed;
                    rec.IsDispTex = (byte)(m.IsDisplacementTexture ? 1 : 0);
                    rec.SatMask = (byte)(m.ApplySaturationMask ? 1 : 0);
                    rec.SoftDepth = m.SoftParticleDepthContrast;
                }
            }

            void InheritTex()
            {
                if (prev is not null)
                {
                    rec.RandomInitialFrame = prev.RandomInitialFrame;
                    rec.FrameChangeSpeed = prev.FrameChangeSpeed;
                    rec.IsDispTex = prev.IsDispTex;
                    rec.TexHash = prev.TexHash; rec.TexPath = prev.TexPath;
                }
                else { rec.FrameChangeSpeed = 0f; rec.RandomInitialFrame = 0; rec.IsDispTex = 0; }
            }

            uint tagset = mode == 1 ? 0x3A3FEu : (mode == 2 || mode == 3) ? 0x28DCEu : mode == 4 ? 0x7406u : 0u;
            for (uint tag = 1; tag <= 0x20000; tag <<= 1)
            {
                if ((tagset & tag) == 0) continue;
                bool found = m.Has(tag);
                switch (tag)
                {
                    case XexTag.DiffuseAlpha:
                        rec.Alpha = found ? m.Alpha : (prev is not null ? prev.Alpha : 1f); break;
                    case XexTag.DiffuseColor:
                        for (int k = 0; k < 3; k++) rec.Rgb[k] = found ? m.ColorSq[k] : (prev is not null ? prev.Rgb[k] : 1f);
                        break;
                    case XexTag.DiffuseTexture:
                        if (found) TexSideEffect(mode == 1); else InheritTex();
                        break;
                    case XexTag.Size:
                        for (int k = 0; k < 2; k++) rec.Dims[k] = found ? m.Dimensions[k] : (prev is not null ? prev.Dims[k] : 1f);
                        break;
                    case XexTag.SpriteNumPoints:
                        rec.NumSpritePoints = found ? m.NumSpritePoints : (prev is not null ? prev.NumSpritePoints : 4u); break;
                    case XexTag.SrcBlendMode:
                        rec.SrcBlend = found ? m.SrcBlend : (prev is not null ? prev.SrcBlend : 1u); break;
                    case XexTag.DestBlendMode:
                        rec.DstBlend = found ? m.DstBlend : (prev is not null ? prev.DstBlend : 5u); break;
                    case XexTag.BlendOp:
                        rec.BlendOp = found ? m.BlendOp : (prev is not null ? prev.BlendOp : 0u); break;
                    case XexTag.RotationAngle:
                        rec.RotSpeed = found ? m.RotSpeed : (prev is not null ? prev.RotSpeed : 0f);
                        rec.RotSpread = found ? m.RotSpread : (prev is not null ? prev.RotSpread : 0f);
                        rec.RotInitRandom = found ? (byte)(m.RotInitialRandom ? 1 : 0) : (prev is not null ? prev.RotInitRandom : (byte)0);
                        break;
                    case XexTag.FloatSize:
                        if (mode == 4) rec.MeshScale = found ? m.FloatSize : (prev is not null ? prev.MeshScale : 1f);
                        else rec.Size = found ? m.FloatSize : (prev is not null ? prev.Size : 1f);
                        break;
                    case XexTag.NumSegments:
                        rec.NumSegments = found ? (int)m.NumSegments : (prev is not null ? prev.NumSegments : 1); break;
                    case XexTag.MeshID:
                        if (found) { rec.MeshId = m.MeshHash; rec.MeshPath = m.MeshPath; rec.IsDispMesh = (byte)(m.IsDisplacementMesh ? 1 : 0); }
                        else if (prev is not null) { rec.MeshId = prev.MeshId; rec.MeshPath = prev.MeshPath; rec.IsDispMesh = prev.IsDispMesh; }
                        else { rec.MeshId = Fnv1LowerResourceName("ILLEGAL_MESH_PARTICLE_ID"); rec.IsDispMesh = 0; }
                        break;
                    case XexTag.RandomSize:
                        if (mode == 4)
                        {
                            rec.RsFreqFrames = found ? XexConst.Fctiwz(XexConst.Fps * m.MrsChangeFreq) : (prev is not null ? prev.RsFreqFrames : 0);
                            rec.MrsMin = found ? m.MrsMin : (prev is not null ? prev.MrsMin : 1f);
                            rec.MrsRange = found ? m.MrsRange : (prev is not null ? prev.MrsRange : 0f);
                        }
                        else
                        {
                            rec.RsFreqFrames = found ? XexConst.Fctiwz(XexConst.Fps * m.RsChangeFreq) : (prev is not null ? prev.RsFreqFrames : 0);
                            for (int k = 0; k < 2; k++)
                            {
                                rec.RsMin[k] = found ? m.RsMin[k] : (prev is not null ? prev.RsMin[k] : 1f);
                                rec.RsRange[k] = found ? m.RsRange[k] : (prev is not null ? prev.RsRange[k] : 0f);
                            }

                            rec.RsLinked = found ? (byte)(m.RsLinked ? 1 : 0) : (prev is not null ? prev.RsLinked : (byte)1);
                        }

                        break;
                    case XexTag.RotationOrient:
                        rec.RotSpeed = found ? m.OrSpeed : (prev is not null ? prev.RotSpeed : 0f);
                        rec.RotSpread = found ? m.OrSpread : (prev is not null ? prev.RotSpread : 0f);
                        rec.RotInitRandom = found ? (byte)(m.OrInitialRandomAngle ? 1 : 0) : (prev is not null ? prev.RotInitRandom : (byte)0);
                        for (int k = 0; k < 3; k++) rec.OrAxis[k] = found ? m.OrAxis[k] : (prev is not null ? prev.OrAxis[k] : (k == 0 ? 1f : 0f));
                        rec.OrInitRandomAxis = found ? (byte)(m.OrInitialRandomAxis ? 1 : 0) : (prev is not null ? prev.OrInitRandomAxis : (byte)1);
                        break;
                    case XexTag.SelfIllumFactor:
                        rec.SelfIllum = found ? m.SelfIllum : (prev is not null ? prev.SelfIllum : 1f); break;
                    case XexTag.GlobalFrameScale:
                        for (int k = 0; k < 2; k++) rec.GlobalFrameScale[k] = found ? m.GlobalFrameScale[k] : (prev is not null ? prev.GlobalFrameScale[k] : 1f);
                        break;
                    case XexTag.AlignmentQuat:
                        if (mode == 1)
                        {
                            if (found) { rec.HasAlignQuat = 1; for (int k = 0; k < 4; k++) rec.AlignQuat[k] = m.AlignQuat[k]; }
                            else if (prev is not null) { rec.HasAlignQuat = prev.HasAlignQuat; for (int k = 0; k < 4; k++) rec.AlignQuat[k] = prev.AlignQuat[k]; }
                            else { rec.HasAlignQuat = 0; rec.AlignQuat[0] = 0; rec.AlignQuat[1] = 0; rec.AlignQuat[2] = 1; rec.AlignQuat[3] = 0; }
                        }

                        break;
                    default: break;
                }
            }
        }

        for (int i = n - 1; i >= 1; i--)
        {
            int span = sys.Children[sys.ChildOrder[i]].StartFrames - sys.Children[sys.ChildOrder[i - 1]].StartFrames;
            if (span <= 1) span = 2;
            sys.Segments[i].InvSpan = 1.0f / (span - 1);
        }

        for (int i = 0; i < n; i++)
        {
            int f = sys.Children[sys.ChildOrder[i]].StartFrames;
            sys.StartFramesVec.Add(MathF.Floor(XexConst.Fps * (f / XexConst.Fps)));
        }
    }

    public static bool ParseBank(byte[] data, XexBank o)
    {
        var s = new XexStream(data);
        if (data.Length < 24 || System.Text.Encoding.ASCII.GetString(data, 0, 16) != "ParticleBankFile") { o.Error = "bad magic"; return false; }
        s.Pos = 16;
        o.BankVersion = s.U32();
        {
            int mark = s.Tell();
            uint magic = s.U32();
            if (magic == XexConst.MagicBaad)
            {
                o.HadHeaderMarker = true;
                o.HeaderFlag = s.U32();
                if (o.HeaderFlag != 0) o.GlobalFloat = s.F32();
            }
            else
            {
                s.Seek(mark);
            }
        }

        uint bv = o.BankVersion;

        o.Section2Offset = s.Tell();
        o.Section2Param = s.U32();
        {
            int count = s.I32();
            if (count < 0 || count > 200000) { o.Error = "bad material count"; return false; }
            for (int i = 0; i < count; i++)
            {
                uint key = s.U32();
                var m = new XexMaterial();
                MaterialLoadBinary(s, m, bv);
                m.Id = key;
                if (s.Overrun) { o.Error = $"material #{i} overrun"; return false; }
                o.MaterialById[key] = o.Materials.Count;
                o.Materials.Add(m);
            }
        }

        o.Section3Offset = s.Tell();
        o.Section3Param = s.U32();
        {
            int count = s.I32();
            if (count < 0 || count > 200000) { o.Error = "bad system count"; return false; }
            for (int i = 0; i < count; i++)
            {
                uint key = s.U32();
                var sys = new XexSystemDef { Id = key };
                MaterialTimelineLoadBin(s, sys, bv);
                if (s.Overrun) { o.Error = $"system #{i} overrun"; return false; }
                o.SystemById[key] = o.Systems.Count;
                o.Systems.Add(sys);
            }
        }

        o.Section4Offset = s.Tell();
        o.Section4Param = s.U32();
        {
            int count = s.I32();
            if (count < 0 || count > 200000) { o.Error = "bad entity count"; return false; }
            for (int i = 0; i < count; i++)
            {
                int begin = s.Tell();
                uint typeId = s.U32();
                uint entityId = s.U32();
                uint subType = s.U32();
                var reference = new XexEntityRef { TypeId = typeId, EntityId = entityId, SubType = subType, Index = -1 };
                void Finish(XexEntityType e, int index)
                {
                    e.TypeId = typeId; e.EntityId = entityId; e.SubType = subType;
                    e.FileBegin = begin; e.FileEnd = s.Tell();
                    reference.Index = index;
                }

                switch (typeId)
                {
                    case 0:
                    {
                        var e = new XexStandardEmitterType();
                        if (subType > 3) { o.Error = "bad std emitter subtype"; return false; }
                        e.Shape = (XexEmitterShape)subType;
                        if (e.Shape == XexEmitterShape.Mesh) e.ShapeRadius = 0f;
                        StdEmitterDeserialize(s, e, bv);
                        Finish(e, o.StdEmitters.Count);
                        o.StdEmitters.Add(e);
                        break;
                    }

                    case 3: { var e = new XexOrbitalEntityType(); OrbitalLoadBin(s, e); Finish(e, o.Orbitals.Count); o.Orbitals.Add(e); break; }
                    case 4: { var e = new XexAttractorEntityType(); AttractorLoadBin(s, e); Finish(e, o.Attractors.Count); o.Attractors.Add(e); break; }
                    case 5: { var e = new XexSplineEntityType(); SplineEntityLoadBin(s, e); Finish(e, o.SplineEntities.Count); o.SplineEntities.Add(e); break; }
                    case 6: { var e = new XexRootEntityType(); EntityTypeLoadBin(s, e); Finish(e, o.Roots.Count); o.Roots.Add(e); break; }
                    case 7: { var e = new XexPointLightEntityType(); PointLightLoadBin(s, e); Finish(e, o.PointLights.Count); o.PointLights.Add(e); break; }
                    case 8: { var e = new XexSpotLightEntityType(); SpotLightLoadBin(s, e); Finish(e, o.SpotLights.Count); o.SpotLights.Add(e); break; }
                    case 9: { var e = new XexSplineEmitterType(); SplineEmitterLoadBin(s, e, bv); Finish(e, o.SplineEmitters.Count); o.SplineEmitters.Add(e); break; }
                    default:
                        o.Error = $"entity #{i} unsupported type {typeId}";
                        return false;
                }

                if (s.Overrun) { o.Error = $"entity #{i} overrun"; return false; }
                o.EntityOrder.Add(reference);
                o.EntityById[entityId] = reference;
            }
        }

        o.Section5Offset = s.Tell();
        o.Section5Version = s.U32();
        {
            int count = s.I32();
            if (count < 0 || count > 200000) { o.Error = "bad effect count"; return false; }
            for (int i = 0; i < count; i++)
            {
                var e = new XexEffectCollection { Id = s.U32() };
                EffectCollectionLoadBin(s, e, o.Section5Version);
                if (s.Overrun) { o.Error = $"effect #{i} overrun"; return false; }
                o.EffectById[e.Id] = o.Effects.Count;
                o.Effects.Add(e);
            }
        }

        o.EndOffset = s.Tell();
        o.Ok = true;
        return true;
    }
}
