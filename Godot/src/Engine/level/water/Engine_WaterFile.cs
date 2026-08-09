using System;
using System.Collections.Generic;
using System.Text;

namespace Refii.Level;

public static class WaterParam
{
    public const int FresnelBias = 0;
    public const int ReflectionBias = 1;
    public const int NormalSpeed0X = 2;
    public const int NormalSpeed0Y = 3;
    public const int NormalSpeed1X = 4;
    public const int NormalSpeed1Y = 5;
    public const int NormalScale0X = 6;
    public const int NormalScale0Y = 7;
    public const int NormalScale1X = 8;
    public const int NormalScale1Y = 9;
    public const int DetailSpeed0X = 10;
    public const int DetailSpeed0Y = 11;
    public const int DetailSpeed1X = 12;
    public const int DetailSpeed1Y = 13;
    public const int DetailScale0X = 14;
    public const int DetailScale0Y = 15;
    public const int DetailScale1X = 16;
    public const int DetailScale1Y = 17;
    public const int SurfaceR = 18;
    public const int SurfaceG = 19;
    public const int SurfaceB = 20;
    public const int DeepR = 21;
    public const int DeepG = 22;
    public const int DeepB = 23;
    public const int NormalScale = 24;
    public const int ReflectionScaleX = 25;
    public const int ReflectionScaleY = 26;
    public const int RefractionScaleX = 27;
    public const int RefractionScaleY = 28;
    public const int ReflectionStrength = 29;
    public const int DiffuseAbsorption = 30;
    public const int SpecularReflectionFactor = 31;
    public const int SpecularHighlight = 32;
    public const int SpecularPower = 33;
    public const int GlitterBend = 34;
    public const int GlitterStrength = 35;
    public const int GlitterPower = 36;

    public const int Count = 37;
}

public sealed class WaterTile
{
    public float CentreX { get; set; }

    public float CentreZ { get; set; }

    public float ExtentX { get; set; }

    public float ExtentZ { get; set; }

    public int CellsX { get; set; }

    public int CellsZ { get; set; }

    public uint[] Aux { get; set; } = Array.Empty<uint>();

    public byte[] Mask { get; set; } = Array.Empty<byte>();
}

public sealed class WaterBody
{
    public float ParamA { get; set; }

    public float BaseHeight { get; set; }

    public float[] Params { get; } = new float[WaterParam.Count];

    public string NormalMapPath { get; set; } = string.Empty;

    public string SecondaryMapPath { get; set; } = string.Empty;

    public uint DeclaredTileCount { get; set; }

    public List<WaterTile> Tiles { get; } = new();

    public float Param(int index, float fallback)
    {
        if (index < 0 || index >= Params.Length)
        {
            return fallback;
        }

        float value = Params[index];
        return float.IsFinite(value) ? value : fallback;
    }
}

public sealed class WaterScene
{
    public uint Version { get; set; }

    public uint BodyCount { get; set; }

    public uint TileCount { get; set; }

    public List<WaterBody> Bodies { get; } = new();
}

public static class Engine_WaterFile
{
    private const uint Marker = 0x00000FECu;

    private const uint MaxBodyCount = 256;

    private const uint MaxTileCount = 4096;

    private const uint MaxAuxCount = 64;

    private const uint MaxMaskBytes = 1u << 20;

    private const uint MaxCells = 1024;

    public static bool TryParse(byte[] bytes, out WaterScene scene, out string error)
    {
        scene = new WaterScene();
        error = string.Empty;

        if (bytes.Length < 0x0C)
        {
            error = "too small for a .water header";
            return false;
        }

        var reader = new Reader(bytes);

        if (!reader.U32(out uint version) || version != 2)
        {
            error = $"unsupported .water version {version}";
            return false;
        }

        scene.Version = version;

        if (!reader.U32(out uint bodyCount) || bodyCount == 0 || bodyCount > MaxBodyCount)
        {
            error = $"implausible body count {bodyCount}";
            return false;
        }

        scene.BodyCount = bodyCount;

        long tableEnd = 8 + ((long)bodyCount * 4);
        if (tableEnd > bytes.Length)
        {
            error = "offset table past end of file";
            return false;
        }

        var offsets = new uint[bodyCount];
        for (uint k = 0; k < bodyCount; k++)
        {
            if (!reader.U32(out offsets[k]))
            {
                error = "truncated offset table";
                return false;
            }

            if (offsets[k] < tableEnd || offsets[k] >= bytes.Length)
            {
                error = $"body {k} offset {offsets[k]} out of range";
                return false;
            }
        }

        for (uint k = 0; k < bodyCount; k++)
        {
            var bodyReader = new Reader(bytes) { Position = (int)offsets[k] };
            if (TryParseBody(bodyReader, out WaterBody body))
            {
                scene.TileCount += (uint)body.Tiles.Count;
                scene.Bodies.Add(body);
            }
        }

        if (scene.Bodies.Count == 0)
        {
            error = "no body parsed";
            return false;
        }

        return true;
    }

    private static bool TryParseBody(Reader reader, out WaterBody body)
    {
        body = new WaterBody();

        if (!reader.U32(out uint marker) || marker != Marker)
        {
            return false;
        }

        if (!reader.F32(out float paramA) || !reader.F32(out float baseHeight))
        {
            return false;
        }

        body.ParamA = paramA;
        body.BaseHeight = baseHeight;

        for (int i = 0; i < body.Params.Length; i++)
        {
            if (!reader.F32(out float value))
            {
                return false;
            }

            body.Params[i] = value;
        }

        if (!reader.Strz(out string normalMap) || !reader.Strz(out string secondaryMap))
        {
            return false;
        }

        body.NormalMapPath = normalMap;
        body.SecondaryMapPath = secondaryMap;

        if (!reader.U32(out uint patchCount) || patchCount > MaxTileCount)
        {
            return false;
        }

        body.DeclaredTileCount = patchCount;

        for (uint pi = 0; pi < patchCount; pi++)
        {
            if (!reader.U32(out marker) || marker != Marker)
            {
                return false;
            }

            var tile = new WaterTile();

            if (!reader.F32(out float cx) || !reader.F32(out float cz) ||
                !reader.F32(out float ex) || !reader.F32(out float ez) ||
                !reader.F32(out float cellsX) || !reader.F32(out float cellsZ))
            {
                return false;
            }

            if (!(cellsX >= 1f) || !(cellsZ >= 1f) ||
                cellsX > MaxCells || cellsZ > MaxCells)
            {
                return false;
            }

            tile.CentreX = cx;
            tile.CentreZ = cz;
            tile.ExtentX = ex;
            tile.ExtentZ = ez;
            tile.CellsX = (int)cellsX;
            tile.CellsZ = (int)cellsZ;

            if (!reader.U32(out uint auxCount) || auxCount > MaxAuxCount)
            {
                return false;
            }

            tile.Aux = new uint[auxCount];
            for (uint a = 0; a < auxCount; a++)
            {
                if (!reader.U32(out tile.Aux[a]))
                {
                    return false;
                }
            }

            if (!reader.U32(out uint maskCount) || maskCount > MaxMaskBytes)
            {
                return false;
            }

            if (!reader.Need(maskCount))
            {
                return false;
            }

            if (maskCount != (uint)tile.CellsX * (uint)tile.CellsZ)
            {
                return false;
            }

            tile.Mask = reader.Take((int)maskCount);

            if (!reader.U32(out marker) || marker != Marker)
            {
                return false;
            }

            body.Tiles.Add(tile);
        }

        reader.U32(out _);
        return true;
    }

    private sealed class Reader
    {
        private readonly byte[] _data;

        public Reader(byte[] data) => _data = data;

        public int Position { get; set; }

        public bool Need(long count) => Position + count <= _data.Length;

        public bool U32(out uint value)
        {
            value = 0;
            if (!Need(4))
            {
                return false;
            }

            value = ((uint)_data[Position] << 24) | ((uint)_data[Position + 1] << 16) |
                    ((uint)_data[Position + 2] << 8) | _data[Position + 3];
            Position += 4;
            return true;
        }

        public bool F32(out float value)
        {
            value = 0f;
            if (!U32(out uint raw))
            {
                return false;
            }

            value = BitConverter.Int32BitsToSingle(unchecked((int)raw));
            return true;
        }

        public bool Strz(out string value)
        {
            value = string.Empty;
            int end = Position;
            while (end < _data.Length && _data[end] != 0)
            {
                end++;
            }

            if (end >= _data.Length)
            {
                return false;
            }

            value = Encoding.ASCII.GetString(_data, Position, end - Position);
            Position = end + 1;
            return true;
        }

        public byte[] Take(int count)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(_data, Position, slice, 0, count);
            Position += count;
            return slice;
        }
    }
}
