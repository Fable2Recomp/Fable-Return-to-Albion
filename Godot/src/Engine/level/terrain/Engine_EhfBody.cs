using System;
using System.Collections.Generic;
using System.Text;

namespace Refii.Level;

public sealed class EhfLodEntry
{
    public string[] Strings { get; } = new string[6];

    public uint MaterialFlags { get; set; }

    public float BaseTileScale { get; set; }

    public float BaseIntensity { get; set; } = 1f;

    public float DetailTileScale { get; set; }

    public float DetailIntensity { get; set; } = 1f;

    public float TrailingUnused { get; set; }

    public string BaseDiffuse => Strings[0];

    public string BaseNormal => Strings[1];

    public string DetailDiffuse => Strings[3];

    public string DetailNormal => Strings[4];
}

public sealed class EhfPaintResource
{
    public uint Width { get; set; }

    public uint Height { get; set; }

    public uint PixelFormat { get; set; }

    public byte[] Data { get; set; } = Array.Empty<byte>();
}

public sealed class EhfChunkLayer
{
    public uint MaterialIndex { get; set; }

    public uint NameIndex { get; set; }

    public float TileU { get; set; }

    public float TileV { get; set; }

    public float MaskScaleU { get; set; }

    public float MaskScaleV { get; set; }

    public byte[] TextureIndex { get; } = new byte[4];

    public byte[] Blend { get; } = new byte[4];
}

public sealed class EhfChunk
{
    public float[] Origin { get; } = new float[3];

    public float[] Extent { get; } = new float[3];

    public List<EhfChunkLayer> Layers { get; } = new();
}

public sealed class EhfBackgroundPatch
{
    public float[] AabbMin { get; } = new float[3];

    public float[] AabbMax { get; } = new float[3];

    public List<(uint Offset, uint Length)> Pages { get; } = new();
}

public sealed class EhfParsedBody
{
    public uint ChunkWidth { get; set; }

    public uint ChunkHeight { get; set; }

    public uint SplatWidth { get; set; }

    public uint SplatHeight { get; set; }

    public List<EhfLodEntry> Lods { get; } = new();

    public List<EhfPaintResource> PaintResources { get; } = new();

    public List<EhfPaintResource> WeightMasks { get; } = new();

    public List<EhfChunk> Chunks { get; } = new();

    public List<EhfBackgroundPatch> BackgroundPatches { get; } = new();

    public byte[] SplatIndices { get; set; } = Array.Empty<byte>();

    public int BytesConsumed { get; set; }

    public int BytesRemaining { get; set; }
}

public static class Engine_EhfBody
{
    private const string Magic = "HeightFieldGraphicsFile";

    private const int HeaderLength = 63;

    private const uint TexMagic = 0xFFFFFFFEu;

    public static bool TryParse(byte[] ehf, out EhfParsedBody body, out string error)
    {
        body = new EhfParsedBody();
        error = string.Empty;

        if (ehf.Length < HeaderLength || !HasMagic(ehf))
        {
            error = "bad .ehf header";
            return false;
        }

        uint bodyOffset = ReadU32(ehf, 55);
        uint bodySize = ReadU32(ehf, 59);
        if ((long)bodyOffset + bodySize > ehf.Length)
        {
            error = "body extent past end of file";
            return false;
        }

        var walker = new Walker(ehf, (int)bodyOffset, (int)bodySize);

        if (!SkipTexBlob(walker))
        {
            error = $"tex[0]: {walker.Error}";
            return false;
        }

        if (!SkipTexBlob(walker))
        {
            error = $"tex[1]: {walker.Error}";
            return false;
        }

        if (!walker.F32(out _))
        {
            error = $"float: {walker.Error}";
            return false;
        }

        if (!ReadHeightGrids(walker, body, out error))
        {
            return false;
        }

        if (!walker.F32(out _))
        {
            error = $"860E8 float: {walker.Error}";
            return false;
        }

        if (!walker.U32(out uint backgroundCount))
        {
            error = $"860E8 count: {walker.Error}";
            return false;
        }

        if (backgroundCount > 10000)
        {
            error = "860E8 count implausible";
            return false;
        }

        if (!walker.Skip((long)backgroundCount * 18))
        {
            error = $"860E8 entries: {walker.Error}";
            return false;
        }

        if (!ReadWeightMasks(walker, body, out error))
        {
            return false;
        }

        if (!ReadLodPalette(walker, body, out error))
        {
            return false;
        }

        if (!ReadPaintResources(walker, body, out error))
        {
            return false;
        }

        if (!ReadChunkGrid(walker, body, out error))
        {
            return false;
        }

        if (!ReadPageTables(walker, body, backgroundCount, out error))
        {
            return false;
        }

        body.BytesConsumed = walker.Position;
        body.BytesRemaining = walker.Length - walker.Position;
        return true;
    }

    private static bool ReadHeightGrids(Walker walker, EhfParsedBody body, out string error)
    {
        error = string.Empty;

        if (!walker.U32(out uint count))
        {
            error = $"850A0 count: {walker.Error}";
            return false;
        }

        if (count > 10000)
        {
            error = "850A0 count implausible";
            return false;
        }

        for (uint k = 0; k < count; k++)
        {
            if (!walker.F32(out _) || !walker.F32(out _) ||
                !walker.U32(out uint subWidth) || !walker.U32(out uint subHeight))
            {
                error = $"850A0 entry header: {walker.Error}";
                return false;
            }

            if (subWidth > 1024 || subHeight > 1024)
            {
                error = "850A0 sub dims implausible";
                return false;
            }

            if (!walker.Skip((long)subWidth * subHeight * 160))
            {
                error = $"850A0 grid: {walker.Error}";
                return false;
            }

            var patch = new EhfBackgroundPatch();
            for (int c = 0; c < 3; c++)
            {
                if (!walker.F32(out float value))
                {
                    error = "850A0 aabb min";
                    return false;
                }

                patch.AabbMin[c] = value;
            }

            for (int c = 0; c < 3; c++)
            {
                if (!walker.F32(out float value))
                {
                    error = "850A0 aabb max";
                    return false;
                }

                patch.AabbMax[c] = value;
            }

            body.BackgroundPatches.Add(patch);
        }

        return true;
    }

    private static bool ReadWeightMasks(Walker walker, EhfParsedBody body, out string error)
    {
        error = string.Empty;

        while (walker.Position + 4 <= walker.Length &&
               walker.PeekU32(walker.Position) == TexMagic)
        {
            int start = walker.Position;
            var mask = new EhfPaintResource
            {
                Width = walker.PeekU32(start + 0x10),
                Height = walker.PeekU32(start + 0x14),
                PixelFormat = walker.PeekU32(start + 0x18),
            };

            uint metaSize = walker.PeekU32(start + 0x20);
            if (mask.PixelFormat == 98u)
            {
                long dataOffset = start + metaSize;
                long dataLength = (long)mask.Width * mask.Height * 2;
                if (dataOffset + dataLength <= walker.Length)
                {
                    mask.Data = walker.Slice((int)dataOffset, (int)dataLength);
                }
            }

            body.WeightMasks.Add(mask);

            if (!SkipTexBlob(walker))
            {
                error = $"weight mask: {walker.Error}";
                return false;
            }
        }

        return true;
    }

    private static bool ReadLodPalette(Walker walker, EhfParsedBody body, out string error)
    {
        error = string.Empty;

        if (!walker.U32(out uint count))
        {
            error = $"LOD count read: {walker.Error}";
            return false;
        }

        if (count > 200)
        {
            error = "LOD count implausible";
            return false;
        }

        for (uint k = 0; k < count; k++)
        {
            var entry = new EhfLodEntry();

            for (int s = 0; s < 3; s++)
            {
                if (!walker.NullString(out string text))
                {
                    error = $"LOD[{k}].str[{s}]: {walker.Error}";
                    return false;
                }

                entry.Strings[s] = text;
            }

            if (!walker.F32(out float baseScale))
            {
                error = "LOD base scale";
                return false;
            }

            entry.BaseTileScale = baseScale;

            if (!walker.U32(out uint flags))
            {
                error = "LOD material flags";
                return false;
            }

            entry.MaterialFlags = flags;

            if (!walker.F32(out float baseWeight))
            {
                error = "LOD base weight";
                return false;
            }

            entry.BaseIntensity = baseWeight;

            for (int s = 3; s < 6; s++)
            {
                if (!walker.NullString(out string text))
                {
                    error = $"LOD[{k}].str[{s}]: {walker.Error}";
                    return false;
                }

                entry.Strings[s] = text;
            }

            if (!walker.F32(out float detailScale) ||
                !walker.F32(out float detailIntensity) ||
                !walker.F32(out float trailing))
            {
                error = "LOD trailing floats";
                return false;
            }

            entry.DetailTileScale = detailScale;
            entry.DetailIntensity = detailIntensity;
            entry.TrailingUnused = trailing;

            body.Lods.Add(entry);
        }

        return true;
    }

    private static bool ReadPaintResources(Walker walker, EhfParsedBody body, out string error)
    {
        error = string.Empty;

        if (!walker.U32(out uint count))
        {
            error = $"85DB0 count: {walker.Error}";
            return false;
        }

        if (count > 32)
        {
            error = "85DB0 count implausible";
            return false;
        }

        for (uint k = 0; k < count; k++)
        {
            if (!walker.Need(0x60))
            {
                error = $"85DB0 tex[{k}]: {walker.Error}";
                return false;
            }

            int start = walker.Position;
            var resource = new EhfPaintResource
            {
                Width = walker.PeekU32(start + 0x10),
                Height = walker.PeekU32(start + 0x14),
                PixelFormat = walker.PeekU32(start + 0x18),
            };

            body.PaintResources.Add(resource);

            if (resource.PixelFormat == 99u &&
                Engine_EhfSplat.TryDecodePf99SplatMap(
                    walker.Data, walker.Base + start, walker.Length - start,
                    out byte[] decoded, out int decodedWidth, out int decodedHeight, out _))
            {
                resource.Data = decoded;
                resource.Width = (uint)decodedWidth;
                resource.Height = (uint)decodedHeight;

                body.SplatIndices = decoded;
                body.SplatWidth = (uint)decodedWidth;
                body.SplatHeight = (uint)decodedHeight;
            }

            if (!SkipTexBlob(walker))
            {
                error = $"85DB0 tex[{k}]: {walker.Error}";
                return false;
            }
        }

        return true;
    }

    private static bool ReadChunkGrid(Walker walker, EhfParsedBody body, out string error)
    {
        error = string.Empty;

        if (!walker.U32(out uint chunkWidth) || !walker.U32(out uint chunkHeight))
        {
            error = $"chunk grid W/H: {walker.Error}";
            return false;
        }

        if (chunkWidth == 0 || chunkHeight == 0 || chunkWidth > 256 || chunkHeight > 256)
        {
            error = $"chunk grid implausible: {chunkWidth}x{chunkHeight}";
            return false;
        }

        body.ChunkWidth = chunkWidth;
        body.ChunkHeight = chunkHeight;

        long total = (long)chunkWidth * chunkHeight;
        for (long ci = 0; ci < total; ci++)
        {
            var chunk = new EhfChunk();

            for (int c = 0; c < 3; c++)
            {
                if (!walker.F32(out float value))
                {
                    error = $"chunk origin: {walker.Error}";
                    return false;
                }

                chunk.Origin[c] = value;
            }

            for (int c = 0; c < 3; c++)
            {
                if (!walker.F32(out float value))
                {
                    error = $"chunk extent: {walker.Error}";
                    return false;
                }

                chunk.Extent[c] = value;
            }

            if (!walker.U32(out uint layerCount))
            {
                error = $"chunk layer count: {walker.Error}";
                return false;
            }

            if (layerCount > 32)
            {
                error = $"chunk[{ci}] layer count implausible: {layerCount}";
                return false;
            }

            for (uint li = 0; li < layerCount; li++)
            {
                var layer = new EhfChunkLayer();

                if (!walker.U32(out uint materialIndex))
                {
                    error = "layer material_idx";
                    return false;
                }

                if (!walker.U32(out uint nameIndex))
                {
                    error = "layer name_idx";
                    return false;
                }

                if (!walker.F32(out float tileU))
                {
                    error = "layer uv0";
                    return false;
                }

                if (!walker.F32(out float tileV))
                {
                    error = "layer uv1";
                    return false;
                }

                layer.MaterialIndex = materialIndex;
                layer.NameIndex = nameIndex;
                layer.TileU = tileU;
                layer.TileV = tileV;

                if (nameIndex < body.PaintResources.Count)
                {
                    EhfPaintResource resource = body.PaintResources[(int)nameIndex];
                    if (resource.Width > 0)
                    {
                        layer.MaskScaleU = 16f / resource.Width;
                    }

                    if (resource.Height > 0)
                    {
                        layer.MaskScaleV = 16f / resource.Height;
                    }
                }

                for (int i = 0; i < 4; i++)
                {
                    if (!walker.U8(out byte textureIndex))
                    {
                        error = "layer idx";
                        return false;
                    }

                    if (!walker.U8(out byte blend))
                    {
                        error = "layer blend";
                        return false;
                    }

                    layer.TextureIndex[i] = textureIndex;
                    layer.Blend[i] = blend;
                }

                chunk.Layers.Add(layer);
            }

            body.Chunks.Add(chunk);
        }

        return true;
    }

    private static bool ReadPageTables(
        Walker walker, EhfParsedBody body, uint backgroundCount, out string error)
    {
        error = string.Empty;

        if (!walker.U8(out _))
        {
            error = "final flag";
            return false;
        }

        for (uint k = 0; k < backgroundCount; k++)
        {
            if (!walker.U32(out uint subCount))
            {
                error = "final sub_count";
                return false;
            }

            if (subCount > 65535)
            {
                error = "final sub_count implausible";
                return false;
            }

            if (!walker.Need((long)subCount * 8))
            {
                error = "final sub data";
                return false;
            }

            if (k < body.BackgroundPatches.Count)
            {
                List<(uint, uint)> pages = body.BackgroundPatches[(int)k].Pages;
                for (uint s = 0; s < subCount; s++)
                {
                    int at = walker.Position + ((int)s * 8);
                    pages.Add((walker.PeekU32(at), walker.PeekU32(at + 4)));
                }
            }

            walker.Skip((long)subCount * 8);
        }

        return true;
    }

    private static bool SkipTexBlob(Walker walker)
    {
        int start = walker.Position;
        if (!walker.Need(0x60))
        {
            return false;
        }

        uint magic = walker.PeekU32(start);
        if (magic != TexMagic)
        {
            walker.Error = $"bad .tex magic 0x{magic:x} at body 0x{start:x}";
            return false;
        }

        uint pixelFormat = walker.PeekU32(start + 0x18);
        uint metaSize = walker.PeekU32(start + 0x20);
        if (metaSize > 0x100)
        {
            walker.Error = $"implausible mt 0x{metaSize:x} in .tex at body 0x{start:x}";
            return false;
        }

        if (!walker.Need(metaSize + 8))
        {
            return false;
        }

        long next;
        if (pixelFormat == 98u)
        {
            uint width = walker.PeekU32(start + 0x10);
            uint height = walker.PeekU32(start + 0x14);
            next = start + metaSize + ((long)width * height * 2);
        }
        else
        {
            uint compressed = walker.PeekU32((int)(start + metaSize + 4));
            next = start + metaSize + 8 + compressed;
        }

        if (next > walker.Length)
        {
            walker.Error =
                $"tex blob overruns body (pf={pixelFormat} end={next} body={walker.Length})";
            return false;
        }

        walker.Position = (int)next;
        return true;
    }

    private static bool HasMagic(byte[] ehf)
    {
        for (int i = 0; i < Magic.Length; i++)
        {
            if (ehf[i] != Magic[i])
            {
                return false;
            }
        }

        return true;
    }

    private static uint ReadU32(byte[] data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) |
        ((uint)data[at + 2] << 8) | data[at + 3];

    private sealed class Walker
    {
        public Walker(byte[] data, int baseOffset, int length)
        {
            Data = data;
            Base = baseOffset;
            Length = length;
        }

        public byte[] Data { get; }

        public int Base { get; }

        public int Length { get; }

        public int Position { get; set; }

        public string Error { get; set; } = string.Empty;

        public bool Need(long count)
        {
            if (Position + count > Length)
            {
                Error = $"out of body at 0x{Position:x} " +
                        $"(needed {count}, have {Length - Position})";
                return false;
            }

            return true;
        }

        public bool U32(out uint value)
        {
            value = 0;
            if (!Need(4))
            {
                return false;
            }

            value = PeekU32(Position);
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

        public bool U8(out byte value)
        {
            value = 0;
            if (!Need(1))
            {
                return false;
            }

            value = Data[Base + Position];
            Position++;
            return true;
        }

        public bool Skip(long count)
        {
            if (!Need(count))
            {
                return false;
            }

            Position += (int)count;
            return true;
        }

        public bool NullString(out string value, int maxLength = 256)
        {
            value = string.Empty;
            int start = Position;
            int end = start;

            while (end < Length && end < start + maxLength && Data[Base + end] != 0)
            {
                end++;
            }

            if (end >= Length || end == start + maxLength)
            {
                Error = $"null string overrun at 0x{Position:x}";
                return false;
            }

            value = Encoding.ASCII.GetString(Data, Base + start, end - start);
            Position = end + 1;
            return true;
        }

        public uint PeekU32(int at) => ReadU32(Data, Base + at);

        public byte[] Slice(int at, int count)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(Data, Base + at, slice, 0, count);
            return slice;
        }
    }
}
