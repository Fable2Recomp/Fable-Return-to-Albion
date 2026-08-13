using System;
using System.Collections.Generic;

namespace Refii.Fx;

public sealed class FxTimeline
{
    public List<(float Time, float Value)> Keys { get; } = new();

    public float DefaultValue { get; set; }

    public bool IsEmpty => Keys.Count == 0;

    public float Eval(float t)
    {
        if (Keys.Count == 0)
        {
            return DefaultValue;
        }

        if (t <= Keys[0].Time)
        {
            return Keys[0].Value;
        }

        if (t >= Keys[^1].Time)
        {
            return Keys[^1].Value;
        }

        for (int i = 1; i < Keys.Count; i++)
        {
            if (t <= Keys[i].Time)
            {
                (float ta, float va) = Keys[i - 1];
                (float tb, float vb) = Keys[i];
                float span = tb - ta;
                float u = span > 1e-6f ? (t - ta) / span : 0f;
                return va + ((vb - va) * u);
            }
        }

        return Keys[^1].Value;
    }

    public float MaxValue()
    {
        float m = DefaultValue;
        foreach ((_, float v) in Keys)
        {
            if (v > m)
            {
                m = v;
            }
        }

        return m;
    }
}

public sealed class FxEmitter
{
    public uint TypeId;
    public uint EntityId;
    public uint SubType;

    public float[] Pos = new float[3];
    public float[] Quat = { 0f, 0f, 0f, 1f };

    public float MaxActive;
    public float InitialNum;
    public float StartTimeSecs;
    public float SpawnRadius;

    public FxTimeline Rate = new();
    public FxTimeline RateSpread = new();
    public FxTimeline Speed = new();
    public FxTimeline SpeedSpread = new();
    public FxTimeline AccX = new();
    public FxTimeline AccY = new();
    public FxTimeline AccZ = new();
    public FxTimeline VelX = new();
    public FxTimeline VelY = new();
    public FxTimeline VelZ = new();
    public FxTimeline Wind = new();
    public bool HasMotion;
}

public sealed class FxVisual
{
    public float Delay;
    public string Texture = string.Empty;
    public int TexCols = 1;
    public int TexRows = 1;
    public bool RandomFrame;
    public float FrameSpeed;
    public bool Displacement;
    public bool HasColor;
    public float[] Color = { 1f, 1f, 1f };

    public bool HasAlpha;
    public float Alpha = 1f;

    public bool HasRotation;
    public float RotSpeed;
    public float RotSpread;
    public bool RotInitialRandom;

    public bool HasAlignQuat;
    public float[] AlignQuat = { 0f, 0f, 0f, 1f };

    public int SrcBlend = -1;
    public int DstBlend = -1;
    public bool HasSize;
    public float SizeX;
    public float SizeY;

    public bool HasFlicker;
    public float FlickerPeriod;
    public float FlickerBaseX = 1f;
    public float FlickerBaseY = 1f;
    public float FlickerSpreadX;
    public float FlickerSpreadY;
    public bool FlickerShared = true;

    public bool HasScalarSize;
    public float ScalarSize = 1f;
    public bool HasNumSegments;
    public int NumSegments = 1;
    public bool HasSelfIllum;
    public float SelfIllum = 1f;
    public bool HasGlobalScale;
    public float[] GlobalScale = { 1f, 1f };
    public bool HasSpritePoints;
    public int SpritePoints = 4;
    public bool HasBlendOp;
    public int BlendOp;

    public bool HasTexture;

    public bool HasMesh;
    public string MeshPath = string.Empty;

    public bool HasRandScale;
    public float RsFreq;
    public float RsMin = 1f;
    public float RsRange;

    public bool HasOrient;
    public float OrSpeed;
    public float OrSpread;
    public bool OrRandAngle;
    public float[] OrAxis = { 0f, 0f, 1f };
    public bool OrRandAxis = true;
}

public sealed class FxSystemDef
{
    public uint Id;
    public uint Mode = 1;
    public List<FxVisual> Visuals { get; } = new();
}

public readonly record struct FxEffectPart(int Emitter, int System, uint Group);

public sealed class FxEffect
{
    public uint NameHash;
    public string Name = string.Empty;
    public List<FxEffectPart> Parts { get; } = new();
}

public sealed class FxBank
{
    public bool Ok;
    public string Error = string.Empty;
    public uint Version;

    public List<FxEmitter> Emitters { get; } = new();
    public List<FxSystemDef> Systems { get; } = new();
    public List<FxEffect> Effects { get; } = new();

    private Dictionary<uint, FxEffect>? _byHash;

    public FxEffect? FindByHash(uint hash)
    {
        _byHash ??= Build();
        return _byHash.TryGetValue(hash, out FxEffect? effect) ? effect : null;
    }

    public FxEffect? FindByName(string name) => FindByHash(Fnv1Lower(name));

    private Dictionary<uint, FxEffect> Build()
    {
        var map = new Dictionary<uint, FxEffect>();
        foreach (FxEffect effect in Effects)
        {
            map[effect.NameHash] = effect;
        }

        return map;
    }

    public static uint Fnv1Lower(string s)
    {
        uint h = 0x811C9DC5u;
        foreach (char c in s)
        {
            h *= 0x01000193u;
            h ^= (byte)char.ToLowerInvariant(c);
        }

        return h;
    }
}

public static class Engine_FxBank
{
    private const uint MarkVer = 0x4AD78C59u;
    private const uint Baad = 0xBAADF00Du;
    private const uint ChildMark = 0x37E185B9u;
    private const int Sec4Fixed = 0x1C917E;

    private static readonly float[] GenericDefaults =
    {
        0.95f, 1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f,
        0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 1.0f, 1.0f, 15.0f,
        1.0f, 64.0f, 50.0f, 0.05f, 0.05f, 0.1f, 0.2f, 0.1f,
        0.785398185f, 0.0f, 0.0f, 0.0f,
    };

    private sealed class Reader
    {
        public readonly byte[] Data;
        public int O;
        public bool Bad;

        public Reader(byte[] data) => Data = data;

        public bool Have(int k) => O + k <= Data.Length;

        public uint U32()
        {
            if (!Have(4))
            {
                Bad = true;
                return 0;
            }

            uint v = ((uint)Data[O] << 24) | ((uint)Data[O + 1] << 16) |
                     ((uint)Data[O + 2] << 8) | Data[O + 3];
            O += 4;
            return v;
        }

        public float F32() => BitConverter.Int32BitsToSingle((int)U32());

        public byte U8()
        {
            if (!Have(1))
            {
                Bad = true;
                return 0;
            }

            return Data[O++];
        }

        public void Skip(int k)
        {
            O += k;
            if (O > Data.Length)
            {
                O = Data.Length;
                Bad = true;
            }
        }

        public uint Peek(int at)
        {
            if (at + 4 > Data.Length)
            {
                return 0;
            }

            return ((uint)Data[at] << 24) | ((uint)Data[at + 1] << 16) |
                   ((uint)Data[at + 2] << 8) | Data[at + 3];
        }
    }

    private static void OptBaad(Reader r)
    {
        if (r.Peek(r.O) == Baad)
        {
            r.Skip(4);
            r.U32();
        }
    }

    private static float GenericDefault(int idx) =>
        idx >= 0 && idx < GenericDefaults.Length ? GenericDefaults[idx] : 0f;

    private static FxTimeline ReadStl(Reader r, int idx)
    {
        var t = new FxTimeline { DefaultValue = GenericDefault(idx) };
        if (r.Peek(r.O) == MarkVer)
        {
            r.Skip(4);
            r.U32();
        }

        uint cnt = r.U32();
        if (cnt > 100000)
        {
            r.Bad = true;
            return t;
        }

        for (uint i = 0; i < cnt && !r.Bad; i++)
        {
            float time = r.F32();
            float value = r.F32();
            t.Keys.Add((time, value));
        }

        return t;
    }

    private static void SkipStl(Reader r, int idx) => ReadStl(r, idx);

    private static void SkipVtl(Reader r)
    {
        if (r.Peek(r.O) == MarkVer)
        {
            r.Skip(4);
            r.U32();
        }

        uint cnt = r.U32();
        if (cnt > 100000)
        {
            r.Bad = true;
            return;
        }

        r.Skip((int)cnt * 16);
    }

    private static void ReadBase(Reader r, FxEmitter e)
    {
        r.U32();
        e.Pos = new[] { r.F32(), r.F32(), r.F32() };
        e.Quat = new[] { r.F32(), r.F32(), r.F32(), r.F32() };
        r.U8();
        float start = r.F32();
        r.F32();
        r.U32();

        e.StartTimeSecs = start >= 0f && start < 3600f ? start : 0f;
    }

    private static void ReadEmitterTail(Reader r, FxEmitter e)
    {
        r.U8();
        e.Rate = ReadStl(r, 2);
        e.RateSpread = ReadStl(r, 3);
        e.MaxActive = (int)r.U32();
        e.InitialNum = r.F32();
        r.F32();
        r.F32();
        r.F32();
        r.F32();
        r.U8();
        r.U8();
        r.U8();
        ReadBase(r, e);
    }

    private static void ReadStandard(Reader r, FxEmitter e)
    {
        OptBaad(r);
        if (e.SubType is 1 or 2)
        {
            e.SpawnRadius = r.F32();
            r.U8();
            r.U8();
        }
        else if (e.SubType == 3)
        {
            r.U8();
        }

        e.Speed = ReadStl(r, 4);
        e.SpeedSpread = ReadStl(r, 5);
        SkipVtl(r);
        e.AccX = ReadStl(r, 6);
        e.AccY = ReadStl(r, 7);
        e.AccZ = ReadStl(r, 8);
        e.VelX = ReadStl(r, 9);
        e.VelY = ReadStl(r, 10);
        e.VelZ = ReadStl(r, 11);
        SkipVtl(r);
        e.Wind = ReadStl(r, 12);
        SkipStl(r, 13);
        SkipVtl(r);
        SkipStl(r, 14);
        r.U8();
        r.U8();
        r.F32();
        ReadEmitterTail(r, e);
        e.HasMotion = true;
    }

    private static void ReadLight(Reader r, FxEmitter e)
    {
        r.U8();
        r.U32();
        SkipVtl(r);
        foreach (int idx in new[] { 15, 16, 17, 18, 19, 20, 21, 22, 23, 0, 1 })
        {
            SkipStl(r, idx);
        }

        ReadBase(r, e);
    }

    private static void ReadLight8(Reader r, FxEmitter e)
    {
        r.U8();
        r.U32();
        r.U32();
        r.U8();
        SkipVtl(r);
        foreach (int idx in new[] { 15, 24, 16, 17, 18, 19, 20, 21, 22, 23, 0, 1 })
        {
            SkipStl(r, idx);
        }

        ReadBase(r, e);
    }

    private static void ReadSplineObject(Reader r)
    {
        r.U32();
        uint np = r.U32();
        if (np > 4096)
        {
            r.Bad = true;
            return;
        }

        r.Skip((int)np * 12);
        r.U8();
        r.U8();
        r.F32();
        r.Skip(4);
    }

    private static void ReadSplineEntity(Reader r, FxEmitter e)
    {
        r.U32();
        r.U8();
        ReadSplineObject(r);
        SkipStl(r, 0);
        SkipStl(r, 1);
        ReadBase(r, e);
    }

    private static void ReadSplineEmitter(Reader r, FxEmitter e)
    {
        ReadEmitterTail(r, e);
        r.U32();
        r.U32();
        r.U8();
        r.U8();
        r.F32();
        r.Skip(4);
        r.F32();
        r.F32();
        r.F32();
        r.F32();
        r.F32();
        r.U8();
        r.U8();
        e.HasMotion = true;
    }

    private static void ReadType3(Reader r, FxEmitter e)
    {
        r.U8();
        SkipStl(r, 25);
        r.F32();
        SkipStl(r, 0);
        SkipStl(r, 1);
        ReadBase(r, e);
    }

    private static void ReadAttractor(Reader r, FxEmitter e)
    {
        r.U8();
        SkipStl(r, 26);
        SkipStl(r, 27);
        r.U8();
        SkipStl(r, 0);
        SkipStl(r, 1);
        ReadBase(r, e);
    }

    private static bool ReadEntity(Reader r, FxEmitter e)
    {
        e.TypeId = r.U32();
        e.EntityId = r.U32();
        e.SubType = r.U32();
        switch (e.TypeId)
        {
            case 0: ReadStandard(r, e); break;
            case 3: ReadType3(r, e); break;
            case 4: ReadAttractor(r, e); break;
            case 5: ReadSplineEntity(r, e); break;
            case 6: ReadBase(r, e); break;
            case 7: ReadLight(r, e); break;
            case 8: ReadLight8(r, e); break;
            case 9: ReadSplineEmitter(r, e); break;
            default: return false;
        }

        return !r.Bad;
    }

    private static bool WalkNode(Reader r, FxVisual? outVis, ref bool outTextured)
    {
        uint nid = r.U32();
        r.U32();
        uint cnt = r.U32();
        if (r.Bad || cnt > 64)
        {
            r.Bad = true;
            return false;
        }

        bool hasMesh = false;
        for (uint i = 0; i < cnt && !r.Bad; i++)
        {
            uint tag = r.U32();
            switch (tag)
            {
                case 0x1:
                    break;
                case 0x2:
                {
                    float av = r.F32();
                    if (outVis is not null)
                    {
                        outVis.Alpha = av;
                        outVis.HasAlpha = true;
                    }

                    break;
                }

                case 0x400:
                {
                    float tw = r.F32();
                    if (outVis is not null)
                    {
                        outVis.HasScalarSize = true;
                        outVis.ScalarSize = tw;
                    }

                    break;
                }

                case 0x4:
                {
                    float cr = r.F32();
                    float cg = r.F32();
                    float cb = r.F32();
                    if (outVis is not null)
                    {
                        outVis.Color = new[] { cr, cg, cb };
                        outVis.HasColor = true;
                    }

                    break;
                }

                case 0x8:
                {
                    r.U32();
                    uint ln = r.U32();
                    if (r.Bad || ln > 512 || !r.Have((int)ln + 1))
                    {
                        r.Bad = true;
                        break;
                    }

                    string path = System.Text.Encoding.ASCII.GetString(
                        r.Data, r.O, (int)ln);
                    r.Skip((int)ln + 1);
                    uint nx = r.U32();
                    uint ny = r.U32();
                    byte rif = r.U8();
                    float spd = r.F32();
                    byte disp = r.U8();
                    r.U8();
                    r.F32();
                    if (outVis is not null)
                    {
                        outVis.Texture = path;
                        outVis.TexCols = nx is >= 1 and <= 16 ? (int)nx : 1;
                        outVis.TexRows = ny is >= 1 and <= 16 ? (int)ny : 1;
                        outVis.RandomFrame = rif != 0;
                        outVis.FrameSpeed = spd;
                        outVis.Displacement = disp != 0;
                        outVis.HasTexture = true;
                    }

                    outTextured = true;
                    break;
                }

                case 0x10:
                {
                    float sx = r.F32();
                    float sy = r.F32();
                    if (outVis is not null)
                    {
                        outVis.SizeX = sx;
                        outVis.SizeY = sy;
                        outVis.HasSize = true;
                    }

                    break;
                }

                case 0x10000:
                {
                    float gsx = r.F32();
                    float gsy = r.F32();
                    if (outVis is not null)
                    {
                        outVis.HasGlobalScale = true;
                        outVis.GlobalScale = new[] { gsx, gsy };
                    }

                    break;
                }

                case 0x20:
                {
                    uint sp = r.U32();
                    if (outVis is not null)
                    {
                        outVis.HasSpritePoints = true;
                        outVis.SpritePoints = (int)sp;
                    }

                    break;
                }
                case 0x800:
                {
                    uint tl = r.U32();
                    if (outVis is not null)
                    {
                        outVis.HasNumSegments = true;
                        outVis.NumSegments = tl is >= 1 and <= 512 ? (int)tl : 1;
                    }

                    break;
                }

                case 0x200:
                {
                    float speed = r.F32();
                    float spread = r.F32();
                    byte initRand = r.U8();
                    if (outVis is not null)
                    {
                        outVis.HasRotation = true;
                        outVis.RotSpeed = speed;
                        outVis.RotSpread = spread;
                        outVis.RotInitialRandom = initRand != 0;
                    }

                    break;
                }

                case 0x20000:
                {
                    float qx = r.F32();
                    float qy = r.F32();
                    float qz = r.F32();
                    float qw = r.F32();
                    if (outVis is not null)
                    {
                        outVis.HasAlignQuat = true;
                        outVis.AlignQuat = new[] { qx, qy, qz, qw };
                    }

                    break;
                }

                case 0x40:
                {
                    uint b = r.U32();
                    if (outVis is not null)
                    {
                        outVis.SrcBlend = (int)b;
                    }

                    break;
                }

                case 0x80:
                {
                    uint b = r.U32();
                    if (outVis is not null)
                    {
                        outVis.DstBlend = (int)b;
                    }

                    break;
                }

                case 0x100:
                {
                    uint op = r.U32();
                    if (outVis is not null)
                    {
                        outVis.HasBlendOp = true;
                        outVis.BlendOp = (int)op;
                    }

                    break;
                }

                case 0x1000:
                {
                    r.U32();
                    uint ln = r.U32();
                    if (r.Bad || ln > 512 || !r.Have((int)ln + 2))
                    {
                        r.Bad = true;
                        break;
                    }

                    string mpath = System.Text.Encoding.ASCII.GetString(
                        r.Data, r.O, (int)ln);
                    r.Skip((int)ln + 1 + 1);
                    if (outVis is not null)
                    {
                        outVis.HasMesh = true;
                        outVis.MeshPath = mpath;
                    }

                    hasMesh = true;
                    break;
                }

                case 0x2000:
                {
                    if (hasMesh)
                    {
                        float fq = r.F32();
                        float mn = r.F32();
                        float rg = r.F32();
                        if (outVis is not null)
                        {
                            outVis.HasRandScale = true;
                            outVis.RsFreq = fq;
                            outVis.RsMin = mn;
                            outVis.RsRange = rg;
                        }

                        break;
                    }

                    float per = r.F32();
                    float bx = r.F32();
                    float by = r.F32();
                    float sx2 = r.F32();
                    float sy2 = r.F32();
                    byte shared = r.U8();
                    if (outVis is not null)
                    {
                        outVis.HasFlicker = true;
                        outVis.FlickerPeriod = per;
                        outVis.FlickerBaseX = bx;
                        outVis.FlickerBaseY = by;
                        outVis.FlickerSpreadX = sx2;
                        outVis.FlickerSpreadY = sy2;
                        outVis.FlickerShared = shared != 0;
                    }

                    break;
                }

                case 0x4000:
                {
                    float osp = r.F32();
                    float ospr = r.F32();
                    byte ra = r.U8();
                    float ax = r.F32();
                    float ay = r.F32();
                    float az = r.F32();
                    byte rx = r.U8();
                    if (outVis is not null)
                    {
                        outVis.HasOrient = true;
                        outVis.OrSpeed = osp;
                        outVis.OrSpread = ospr;
                        outVis.OrRandAngle = ra != 0;
                        outVis.OrAxis = new[] { ax, ay, az };
                        outVis.OrRandAxis = rx != 0;
                    }

                    break;
                }

                case 0x8000:
                {
                    float si = r.F32();
                    if (outVis is not null)
                    {
                        outVis.HasSelfIllum = true;
                        outVis.SelfIllum = si;
                    }

                    break;
                }

                default:
                    r.Bad = true;
                    break;
            }
        }

        uint nid2 = r.U32();
        if (r.Bad || nid2 != nid)
        {
            r.Bad = true;
            return false;
        }

        return true;
    }

    public static bool Parse(byte[] bytes, FxBank outBank)
    {
        var r = new Reader(bytes);
        byte[] magic = System.Text.Encoding.ASCII.GetBytes("ParticleBankFile");
        if (bytes.Length < 0x2C)
        {
            outBank.Error = "too small";
            return false;
        }

        for (int i = 0; i < 16; i++)
        {
            if (bytes[i] != magic[i])
            {
                outBank.Error = "bad magic";
                return false;
            }
        }

        r.O = 16;
        if (r.Peek(r.O) == Baad)
        {
            r.Skip(4);
        }

        outBank.Version = r.U32();

        r.O = 0x20;
        r.U32();
        uint c2 = r.U32();
        bool sectionsOk = c2 > 0 && c2 < 200000;
        bool ignored = false;
        for (uint i = 0; sectionsOk && i < c2; i++)
        {
            if (!WalkNode(r, null, ref ignored))
            {
                sectionsOk = false;
                break;
            }
        }

        if (sectionsOk)
        {
            r.U32();
            uint c3 = r.U32();
            if (c3 == 0 || c3 > 200000)
            {
                sectionsOk = false;
            }

            for (uint i = 0; sectionsOk && i < c3; i++)
            {
                var sys = new FxSystemDef { Id = r.U32(), Mode = r.U32() };
                uint cc = r.U32();
                if (r.Bad || cc > 32)
                {
                    sectionsOk = false;
                    break;
                }

                var raw = new List<FxVisual>();
                for (uint j = 0; j < cc; j++)
                {
                    if (r.Peek(r.O) == ChildMark)
                    {
                        r.Skip(8);
                    }

                    float delay = r.F32();
                    var vis = new FxVisual { Delay = delay };
                    bool textured = false;
                    if (!WalkNode(r, vis, ref textured))
                    {
                        sectionsOk = false;
                        break;
                    }

                    raw.Add(vis);
                }

                if (!sectionsOk)
                {
                    break;
                }

                for (int k = 1; k < raw.Count; k++)
                {
                    FxVisual v = raw[k];
                    FxVisual p = raw[k - 1];
                    if (!v.HasAlpha && p.HasAlpha)
                    {
                        v.HasAlpha = true;
                        v.Alpha = p.Alpha;
                    }

                    if (!v.HasColor && p.HasColor)
                    {
                        v.HasColor = true;
                        v.Color = (float[])p.Color.Clone();
                    }

                    if (!v.HasTexture && p.HasTexture)
                    {
                        v.HasTexture = true;
                        v.Texture = p.Texture;
                        v.TexCols = p.TexCols;
                        v.TexRows = p.TexRows;
                        v.RandomFrame = p.RandomFrame;
                        v.FrameSpeed = p.FrameSpeed;
                        v.Displacement = p.Displacement;
                    }

                    if (!v.HasSize && p.HasSize)
                    {
                        v.HasSize = true;
                        v.SizeX = p.SizeX;
                        v.SizeY = p.SizeY;
                    }

                    if (v.SrcBlend < 0)
                    {
                        v.SrcBlend = p.SrcBlend;
                    }

                    if (v.DstBlend < 0)
                    {
                        v.DstBlend = p.DstBlend;
                    }

                    if (!v.HasRotation && p.HasRotation)
                    {
                        v.HasRotation = true;
                        v.RotSpeed = p.RotSpeed;
                        v.RotSpread = p.RotSpread;
                        v.RotInitialRandom = p.RotInitialRandom;
                    }

                    if (!v.HasFlicker && p.HasFlicker)
                    {
                        v.HasFlicker = true;
                        v.FlickerPeriod = p.FlickerPeriod;
                        v.FlickerBaseX = p.FlickerBaseX;
                        v.FlickerBaseY = p.FlickerBaseY;
                        v.FlickerSpreadX = p.FlickerSpreadX;
                        v.FlickerSpreadY = p.FlickerSpreadY;
                        v.FlickerShared = p.FlickerShared;
                    }

                    if (!v.HasScalarSize && p.HasScalarSize)
                    {
                        v.HasScalarSize = true;
                        v.ScalarSize = p.ScalarSize;
                    }

                    if (!v.HasNumSegments && p.HasNumSegments)
                    {
                        v.HasNumSegments = true;
                        v.NumSegments = p.NumSegments;
                    }

                    if (!v.HasAlignQuat && p.HasAlignQuat)
                    {
                        v.HasAlignQuat = true;
                        v.AlignQuat = (float[])p.AlignQuat.Clone();
                    }

                    if (!v.HasMesh && p.HasMesh)
                    {
                        v.HasMesh = true;
                        v.MeshPath = p.MeshPath;
                    }

                    if (!v.HasRandScale && p.HasRandScale)
                    {
                        v.HasRandScale = true;
                        v.RsFreq = p.RsFreq;
                        v.RsMin = p.RsMin;
                        v.RsRange = p.RsRange;
                    }

                    if (!v.HasOrient && p.HasOrient)
                    {
                        v.HasOrient = true;
                        v.OrSpeed = p.OrSpeed;
                        v.OrSpread = p.OrSpread;
                        v.OrRandAngle = p.OrRandAngle;
                        v.OrAxis = (float[])p.OrAxis.Clone();
                        v.OrRandAxis = p.OrRandAxis;
                    }
                }

                foreach (FxVisual v in raw)
                {
                    if (v.HasTexture || v.HasMesh)
                    {
                        sys.Visuals.Add(v);
                    }
                }

                r.Skip(12);
                uint id2 = r.U32();
                if (r.Bad || id2 != sys.Id)
                {
                    sectionsOk = false;
                    break;
                }

                outBank.Systems.Add(sys);
            }
        }

        int sec4At = sectionsOk ? r.O : Sec4Fixed;
        if (!sectionsOk)
        {
            outBank.Systems.Clear();
            outBank.Error = "sec2/3 walk failed; effects unavailable";
        }

        if (sec4At + 8 > bytes.Length)
        {
            outBank.Error = "no section 4";
            return false;
        }

        r.O = sec4At;
        r.Bad = false;
        r.U32();
        uint count = r.U32();
        if (count == 0 || count > 200000)
        {
            outBank.Error = "bad entity count";
            return false;
        }

        for (uint k = 0; k < count; k++)
        {
            var e = new FxEmitter();
            int start = r.O;
            if (!ReadEntity(r, e) || r.O <= start)
            {
                outBank.Error = $"entity parse stopped at #{k}";
                break;
            }

            outBank.Emitters.Add(e);
        }

        if (sectionsOk && outBank.Emitters.Count == count && r.O + 8 <= bytes.Length)
        {
            var eidToIdx = new Dictionary<uint, int>();
            for (int i = 0; i < outBank.Emitters.Count; i++)
            {
                eidToIdx[outBank.Emitters[i].EntityId] = i;
            }

            var sysToIdx = new Dictionary<uint, int>();
            for (int i = 0; i < outBank.Systems.Count; i++)
            {
                sysToIdx[outBank.Systems[i].Id] = i;
            }

            r.U32();
            r.U32();
            while (r.O + 17 <= bytes.Length && !r.Bad)
            {
                int recStart = r.O;
                uint key = r.U32();
                r.U32();
                r.U32();
                byte cnt = r.U8();
                if (cnt > 64)
                {
                    r.O = recStart;
                    break;
                }

                var eff = new FxEffect { NameHash = key };
                bool recOk = true;
                for (byte i = 0; i < cnt && recOk; i++)
                {
                    uint group = r.U32();
                    uint m = r.U32();
                    if (r.Bad || m > 16)
                    {
                        recOk = false;
                        break;
                    }

                    for (uint j = 0; j < m; j++)
                    {
                        uint eid = r.U32();
                        uint sid = r.U32();
                        r.U32();
                        int emitterIdx = eidToIdx.TryGetValue(eid, out int ei) ? ei : -1;
                        int systemIdx = sysToIdx.TryGetValue(sid, out int si) ? si : -1;
                        if (emitterIdx >= 0)
                        {
                            eff.Parts.Add(new FxEffectPart(emitterIdx, systemIdx, group));
                        }
                    }
                }

                uint key2 = r.U32();
                if (!recOk || r.Bad || key2 != key)
                {
                    r.O = recStart;
                    break;
                }

                outBank.Effects.Add(eff);
            }
        }

        outBank.Ok = outBank.Emitters.Count > 0;
        if (!outBank.Ok && outBank.Error.Length == 0)
        {
            outBank.Error = "no emitters parsed";
        }

        return outBank.Ok;
    }
}
