using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using Refii.Textures;

namespace Refii.Lighting;

/// <summary>
/// Fable 3's level lightmaps, <c>&lt;scenario&gt;.cflmp</c>, read into the same
/// <see cref="LightmapContainer"/> a Fable 2 <c>.lmp</c> fills: one texture blob per placement
/// key in <see cref="LightmapContainer.ObjectLightmapBlobs"/>, its optional second texture in
/// <see cref="LightmapContainer.ObjectSecondLightmapBlobs"/>.
/// </summary>
/// <remarks>
/// THE LAYOUT, big-endian, as the title reads it (Fable 3 IDB):
///
///     "F3LM\0", u32 version (12 only), u32 built changelist, u8, char[128] build machine
///                                                          sub_82CC2620 0x82cc26dc..0x82cc27b0
///     u32 entryCount, u32 pageCount, u8                    sub_82CC2830 0x82cc288c..0x82cc28cc
///     entryCount x { u64 key, u32 mips, u32 off0, u32 size0, u32 off1, u32 size1,
///                    u8 x0, y0, page0, x1, y1, page1,
///                    u32 n + n x u32 starts0, u32 n + n x u32 starts1, u32, u32 }
///                                                          loop 0x82cc28e0..0x82cc2b50
///     u32 blobSize, pageCount x { u32 size, u32 width, u32 height }
///     blobSize bytes of mip chains, skipped at load (seek 0x82cc2ca4) and streamed later
///     pageCount Fable 3 .tex thumbnail pages
///     two maps of u32 count x { u64 key, u32 size, .tex }   sub_82CC2EF0, into loader+8 / +24
///
/// The two trailing words of an entry are read and dropped (0x82cc2af4, 0x82cc2b10).
///
/// A CHAIN is square DXT1 (the streamer creates format 0x1A200152, sub_82CC9D18) of
/// <c>32 &lt;&lt; (mips - 1)</c> texels, every level a u32 compressed length and a zlib body that
/// inflates into exactly <c>max(0x2000, dim * dim / 2)</c> tiled bytes (sub_82CFC6B0). Levels run
/// largest first; <c>starts[k]</c> is where the chain of <c>32 &lt;&lt; k</c> begins, so the read
/// at sub_82CFC110 takes <c>off + starts[k]</c> to the chain's end and gets level k and below.
///
/// A ZERO-SIZE CHAIN streams nothing: the placement is drawn from its THUMBNAIL, the 32x32 cell
/// at (x * 32, y * 32) of the page it names (sub_82CE53E8 takes the page from the region's list
/// by that byte), which is also what every streamed placement shows until its chain arrives.
///
/// KEYS are the full 64 bits, compared with <c>cmpld</c> on insert (0x82cc3718) and lookup
/// (0x82ce5484). The title composes the placement's as <c>(*(this + 0xC) &lt;&lt; 32) +
/// *(owner + 0x20)</c> (sub_82253AC0, 0x82253c94..0x82253ca4), and <c>this + 0xC</c> is the
/// static-mesh component's <c>ModelFile</c> (CECGraphicAppearanceStaticMesh, read at
/// sub_829ACD48 0x829acef8): Fable 2's <c>(ModelFile &lt;&lt; 32) | placement hash</c> unchanged.
/// ONE TABLE serves every keyed thing: placements, the engine level's type-2 instances by their
/// own 64-bit id, and heightfields by the type-4 record's id (high half zero), which the
/// heightfield resolves through the same sub_82CC8DE0 (sub_82CB0658). Hence
/// <see cref="LightmapContainer.SharedKeyTable"/>.
///
/// THE TWO SLOTS: slot 0 is bound on sampler 3 (`g_TextureAOSampler`), slot 1 on sampler 7
/// (`g_TextureLightmapSampler`), each with its slot's UV rect (+8 in the slot) in c53/c54
/// (sub_8221DCD0, sub_82251848). The rect is the identity once the chain is resident and the
/// thumbnail cell before, which <see cref="Thumbnail"/> crops out instead.
/// </remarks>
public static class Engine_StreamedLightmapFile
{
    public const string Magic = "F3LM";

    public const uint Version = 12;

    private const int BuildNameBytes = 128;

    private const int ThumbnailTexels = 32;

    /// <summary>The floor sub_82CFC6B0 puts under a level's size: a 32x32-block tiled surface.</summary>
    private const int MinimumLevelBytes = 0x2000;

    /// <summary>The largest chain the streamer has a pool for: "Lightmaps 1024" (sub_82CDE058).</summary>
    private const uint MaximumLevels = 6;

    /// <summary>Bytes an entry takes with both start lists empty.</summary>
    private const int MinimumEntryBytes = 8 + (5 * 4) + 6 + 4 + 4 + 8;

    /// <summary>The codec word the file's own resident lightmaps carry (pixel format 35, flags 2).</summary>
    private const int ResidentCodecId = 13;

    private const int RawSurfaceFlag = 0x02;

    private sealed class Entry
    {
        public ulong Key;
        public uint Mips;
        public readonly uint[] Offset = new uint[2];
        public readonly uint[] Size = new uint[2];
        public readonly byte[] Thumbnail = new byte[6];
        public readonly uint[][] Starts = new uint[2][];
    }

    public static bool IsStreamed(byte[] bytes) =>
        bytes.Length >= Magic.Length + 1 &&
        bytes[0] == (byte)'F' && bytes[1] == (byte)'3' && bytes[2] == (byte)'L' && bytes[3] == (byte)'M' &&
        bytes[4] == 0;

    public static bool TryParse(byte[] bytes, out LightmapContainer container, out string error)
    {
        container = new LightmapContainer();
        error = string.Empty;

        var reader = new BeReader(bytes);
        if (!reader.MatchAscii(Magic) || !reader.U8(out byte terminator) || terminator != 0)
        {
            error = "magic mismatch (expected \"F3LM\")";
            return false;
        }

        if (!reader.U32(out uint version) || version != Version)
        {
            error = $"unsupported version (accepts {Version})";
            return false;
        }

        if (!reader.U32(out uint changelist) || !reader.U8(out _) || !reader.Skip(BuildNameBytes))
        {
            error = "truncated header";
            return false;
        }

        container.Version = version;
        container.BuiltChangelist = changelist;
        container.SharedKeyTable = true;

        if (!reader.U32(out uint entryCount) || !reader.U32(out uint pageCount) || !reader.U8(out _) ||
            (long)entryCount * MinimumEntryBytes > bytes.Length - reader.Position)
        {
            error = "truncated reading entry and page counts";
            return false;
        }

        var entries = new Entry[entryCount];
        for (uint i = 0; i < entryCount; i++)
        {
            var entry = new Entry();
            if (!reader.U64(out entry.Key) || !reader.U32(out entry.Mips) ||
                !reader.U32(out entry.Offset[0]) || !reader.U32(out entry.Size[0]) ||
                !reader.U32(out entry.Offset[1]) || !reader.U32(out entry.Size[1]))
            {
                error = $"truncated entry {i}";
                return false;
            }

            for (int t = 0; t < entry.Thumbnail.Length; t++)
            {
                reader.U8(out entry.Thumbnail[t]);
            }

            for (int slot = 0; slot < 2; slot++)
            {
                if (!reader.U32(out uint count) || !reader.U32Array(count, out entry.Starts[slot]))
                {
                    error = $"truncated entry {i} start list {slot}";
                    return false;
                }
            }

            if (!reader.U32(out _) || !reader.U32(out _))
            {
                error = $"truncated entry {i} tail";
                return false;
            }

            entries[i] = entry;
        }

        if (!reader.U32(out uint blobSize) || (long)pageCount * 12 > bytes.Length - reader.Position)
        {
            error = "truncated reading blob size";
            return false;
        }

        var pageSizes = new uint[pageCount];
        for (uint i = 0; i < pageCount; i++)
        {
            if (!reader.U32(out pageSizes[i]) || !reader.U32(out _) || !reader.U32(out _))
            {
                error = $"truncated page {i} size";
                return false;
            }
        }

        int blobBase = reader.Position;
        if (!reader.Skip(blobSize))
        {
            error = "truncated mip chains";
            return false;
        }

        var pages = new byte[pageCount][];
        for (uint i = 0; i < pageCount; i++)
        {
            if (!reader.Bytes(pageSizes[i], out pages[i]))
            {
                error = $"truncated thumbnail page {i}";
                return false;
            }
        }

        for (int map = 0; map < 2; map++)
        {
            var target = map == 0 ? container.ObjectLightmapBlobs : container.ObjectSecondLightmapBlobs;
            if (!reader.U32(out uint count))
            {
                error = $"truncated resident map {map} count";
                return false;
            }

            for (uint i = 0; i < count; i++)
            {
                if (!reader.U64(out ulong key) || !reader.U32(out uint size) ||
                    !reader.Bytes(size, out byte[] texture))
                {
                    error = $"truncated resident map {map} texture {i}";
                    return false;
                }

                target[key] = texture;
            }
        }

        var untiledPages = new byte[]?[pageCount];
        foreach (Entry entry in entries)
        {
            for (int slot = 0; slot < 2; slot++)
            {
                // Slot 1 exists only when the entry states it: `*(entry + 44) > 0` at sub_82CE53E8,
                // a signed test that the 0xFFFFFFFF of a one-texture entry fails.
                if (slot == 1 && unchecked((int)entry.Offset[1]) <= 0)
                {
                    continue;
                }

                byte[]? blob = entry.Size[slot] > 0
                    ? TopLevel(bytes, blobBase, blobSize, entry, slot, out error)
                    : Thumbnail(pages, untiledPages, entry, slot, out error);
                if (blob is null)
                {
                    error = $"entry {entry.Key:X16} slot {slot}: {error}";
                    return false;
                }

                (slot == 0 ? container.ObjectLightmapBlobs : container.ObjectSecondLightmapBlobs)[entry.Key] = blob;
            }
        }

        return true;
    }

    private static byte[]? TopLevel(
        byte[] bytes, int blobBase, uint blobSize, Entry entry, int slot, out string error)
    {
        error = string.Empty;
        uint[] starts = entry.Starts[slot];
        if (entry.Mips < 1 || entry.Mips > MaximumLevels || starts.Length != entry.Mips)
        {
            error = $"{entry.Mips} level(s) with {starts.Length} start(s)";
            return null;
        }

        uint offset = entry.Offset[slot];
        uint size = entry.Size[slot];
        uint start = starts[entry.Mips - 1];
        if ((ulong)offset + size > blobSize || start + 4UL > size)
        {
            error = $"chain {offset}+{size} (top at {start}) outside {blobSize} bytes";
            return null;
        }

        int at = blobBase + (int)offset + (int)start;
        uint packed = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at, 4));
        if (start + 4UL + packed > size)
        {
            error = $"top level of {packed} bytes overruns its chain";
            return null;
        }

        int dim = ThumbnailTexels << (int)(entry.Mips - 1);
        var tiled = new byte[Math.Max(MinimumLevelBytes, dim * dim / 2)];
        using var inflater = new ZLibStream(
            new MemoryStream(bytes, at + 4, (int)packed, writable: false), CompressionMode.Decompress);
        int filled = 0;
        while (filled < tiled.Length)
        {
            int read = inflater.Read(tiled, filled, tiled.Length - filled);
            if (read == 0)
            {
                error = $"top level inflates to {filled} of {tiled.Length} bytes";
                return null;
            }

            filled += read;
        }

        return RawDxt1(dim, tiled);
    }

    private static byte[]? Thumbnail(
        byte[][] pages, byte[]?[] untiledPages, Entry entry, int slot, out string error)
    {
        error = string.Empty;
        int x = entry.Thumbnail[slot * 3];
        int y = entry.Thumbnail[(slot * 3) + 1];
        int page = entry.Thumbnail[(slot * 3) + 2];
        if (page >= pages.Length ||
            !TexHeader.TryParse(pages[page], out TexHeader header, out _) ||
            !header.IsRaw || header.PixelFormat != (int)TexPixelFormat.Dxt1 ||
            (x + 1) * ThumbnailTexels > header.Width || (y + 1) * ThumbnailTexels > header.Height)
        {
            error = $"thumbnail ({x},{y}) on page {page} of {pages.Length}";
            return null;
        }

        byte[]? linear = untiledPages[page];
        if (linear is null)
        {
            int count = BinaryPrimitives.ReadInt32BigEndian(pages[page].AsSpan(header.HeaderSize, 4));
            int available = pages[page].Length - header.HeaderSize - 4;
            linear = Engine_Xbox360Tiling.Untile(
                pages[page].AsSpan(header.HeaderSize + 4, Math.Clamp(count, 0, available)),
                header.BlocksWide, header.BlocksHigh, 8);
            untiledPages[page] = linear;
        }

        const int cellBlocks = ThumbnailTexels / 4;
        var cell = new byte[cellBlocks * cellBlocks * 8];
        for (int row = 0; row < cellBlocks; row++)
        {
            int source = (((y * cellBlocks) + row) * header.BlocksWide + (x * cellBlocks)) * 8;
            Buffer.BlockCopy(linear, source, cell, row * cellBlocks * 8, cellBlocks * 8);
        }

        return RawDxt1(ThumbnailTexels,
            Engine_Xbox360Tiling.Tile(cell, cellBlocks, cellBlocks, 8));
    }

    /// <summary>
    /// One tiled DXT1 level as the raw-surface Fable 3 .tex the same file ships its resident
    /// lightmaps in (the two maps behind the thumbnail pages), so one decoder reads both.
    /// </summary>
    private static byte[] RawDxt1(int dim, byte[] tiled)
    {
        var blob = new byte[TexHeader.Fable3Size + 4 + tiled.Length];
        Span<byte> h = blob;
        BinaryPrimitives.WriteUInt32BigEndian(h[0x00..], TexHeader.Fable3Magic);
        BinaryPrimitives.WriteInt32BigEndian(h[0x04..], 4);
        BinaryPrimitives.WriteInt32BigEndian(h[0x08..], 4 + tiled.Length);
        BinaryPrimitives.WriteInt32BigEndian(h[0x10..], RawSurfaceFlag);
        BinaryPrimitives.WriteInt32BigEndian(h[0x14..], dim);
        BinaryPrimitives.WriteInt32BigEndian(h[0x18..], dim);
        BinaryPrimitives.WriteInt32BigEndian(h[0x1C..], (int)TexPixelFormat.Dxt1);
        BinaryPrimitives.WriteInt32BigEndian(h[0x20..], ResidentCodecId);
        BinaryPrimitives.WriteInt32BigEndian(h[0x28..], 1);
        BinaryPrimitives.WriteInt32BigEndian(h[0x2C..], TexHeader.Fable3Size);
        BinaryPrimitives.WriteInt32BigEndian(h[TexHeader.Fable3Size..], tiled.Length);
        Buffer.BlockCopy(tiled, 0, blob, TexHeader.Fable3Size + 4, tiled.Length);
        return blob;
    }
}
