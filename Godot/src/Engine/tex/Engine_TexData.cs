using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Refii.Textures;

public enum TexSurfaceFormat
{
	Unsupported = 0,
	Bc1,
	Bc3,
	Bc4,

    /// <summary>
    /// Two-channel DXN/BC5, which Fable 2 uses for every tangent-space normal map. Its
    /// block is a BC4 red half followed by a BC4 green half, so it has DXT5's 16-byte
    /// stride and NONE of its meaning: read as DXT5 the green half is interpreted as a
    /// BC1 colour block and the surface comes out as noise.
    /// </summary>
    Bc5,

    /// <summary>Uncompressed 32-bit, already swizzled to Godot's channel order.</summary>
	Rgba8,
}

public sealed class TexSurface
{
	public int Width { get; init; }

	public int Height { get; init; }

	public TexSurfaceFormat Format { get; init; }

	public byte[] Data { get; init; } = Array.Empty<byte>();

	public int MipLevels { get; init; } = 1;

	public bool HasMipmaps => MipLevels > 1;
}

public sealed class TexMip
{
	public uint CompFlag { get; init; }

	public int BodyOffset { get; init; }

	public int BodySize { get; init; }

	public bool HasSize { get; init; }

	public int Width { get; init; }

	public int Height { get; init; }
}

public static class Engine_TexData
{
	private const int RecordSize = 48;
	private const int CompressedHeaderBytes = 444;

	public static bool TryParseChain(byte[] d, out List<TexMip> mips, out string error)
	{
		mips = new List<TexMip>();
		error = string.Empty;

		if (d.Length < RecordSize)
		{
			error = "too small for a mip record";
			return false;
		}

		if (!(Be(d, 0) <= 24 && Be(d, 4) == RecordSize))
		{
			error = "not a bare-chain .tex";
			return false;
		}

		int off = 0;
		while (off + RecordSize <= d.Length)
		{
			uint compFlag = Be(d, off);
			uint dataOffset = Be(d, off + 4);
			uint dataSize = Be(d, off + 8);
			uint u3 = Be(d, off + 12);
			uint u4 = Be(d, off + 16);
			uint u5 = Be(d, off + 20);

			if (dataOffset != RecordSize || compFlag > 24)
			{
				break;
			}

			if ((long)off + RecordSize + dataSize > (long)d.Length + 32)
			{
				break;
			}

			int bodyStart = off + RecordSize;
			if (compFlag == 7)
			{
				int available = Math.Max(0, d.Length - bodyStart);
				mips.Add(new TexMip
				{
					CompFlag = compFlag,
					BodyOffset = bodyStart,
					BodySize = (int)Math.Min(dataSize, (uint)available),
					HasSize = false,
				});
			}
			else
			{
				if (bodyStart + 4 > d.Length || dataSize < CompressedHeaderBytes)
				{
					break;
				}

				mips.Add(new TexMip
				{
					CompFlag = compFlag,
					BodyOffset = bodyStart,
					BodySize = (int)Math.Min(dataSize, (uint)(d.Length - bodyStart)),
					HasSize = true,
					Width = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(bodyStart)),
					Height = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(bodyStart + 2)),
				});
			}

			long advance = RecordSize + dataSize;
			if (u3 <= 24 && u4 == RecordSize + dataSize && u5 > 0 &&
				off + (long)u4 + u5 <= (long)d.Length + 32)
			{
				advance = (long)u4 + u5;
			}

			if (advance <= 0)
			{
				break;
			}

			off += (int)advance;
		}

		if (mips.Count == 0)
		{
			error = "no mip records";
			return false;
		}

		return true;
	}

	private const int BlockBytes = 8;

	private static int ExpectedLevelCount(int width, int height)
	{
		int levels = 1;
		int w = width;
		int h = height;
		while (w > 1 || h > 1)
		{
			w = Math.Max(1, w / 2);
			h = Math.Max(1, h / 2);
			++levels;
		}

		return levels;
	}

	private static int LevelBytes(int width, int height) =>
		Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * BlockBytes;

	public static bool TryDecode(byte[] d, out TexSurface surface, out string error)
	{
        // Most .tex files open with an 84-byte header, not a mip chain. Read it first:
        // it is the authority on format, dimensions and container, none of which the
        // payload can be trusted for. Sniffing the payload instead reads a DXT5 surface
        // as BC1 — plausible-looking and stripped of its alpha — and rejects outright
        // every file whose bytes do not begin with a record.
		if (TexHeader.TryParse(d, out TexHeader header, out _))
		{
			return TryDecodeWithHeader(d, header, out surface, out error);
		}

		if (TryDecodePyramid(d, out surface, out error))
		{
			return true;
		}

		return TryDecodeSingle(d, out surface, out error);
	}

    /// <summary>
    /// Decodes levels 1..n and appends them to the already-decoded level 0.
    ///
    /// Without a chain a texture uploads with no mipmaps, and the GPU then samples a
    /// full-resolution surface for a handful of pixels whenever the model is far away. The
    /// result is aliasing that looks convincingly like a broken decoder — the Crucible
    /// arena sparkled all over at a distance and was pin-sharp up close, which is the
    /// signature of missing mips rather than bad blocks.
    ///
    /// Returns however many levels the FILE holds, which for Fable 2 is usually short of
    /// 1x1. Levels must still be contiguous and correctly sized: Godot derives each one
    /// arithmetically from the base dimensions, so a chain that skips a level or disagrees
    /// about a size desynchronises every level after it. A gap therefore fails the whole
    /// chain rather than returning a prefix.
    /// </summary>
	private static bool TryDecodeMipChain(
		byte[] d, TexHeader header, byte[] level0, out byte[] chain, out int levels)
	{
		chain = Array.Empty<byte>();
		levels = 1;

		int available = Math.Min(header.LevelCount, header.LevelOffsets.Length);
		if (available < 2)
		{
			return false;
		}

        // A SHORT chain is returned as-is, with MipLevels saying how short.
        //
        // Godot sizes a mipmapped surface by deriving every level from the base dimensions
        // down to 1x1; it does not read a level count from us, so it rejects a short chain
        // outright (128x128 DXT1 stopping at 16x16 is 10,880 bytes against the 10,936 it
        // computes). 95% of Fable 2's textures are short, so refusing them here is refusing
        // mipmaps for the whole game.
        //
        // Completing the chain is Engine_TexImage's job, not this one: the tail has to be
        // downsampled and re-encoded, which needs Godot's Image, and this decoder's
        // contract is bytes and formats.
		var parts = new List<byte[]>(available) { level0 };
		int width = header.Width;
		int height = header.Height;

		for (int level = 1; level < available; ++level)
		{
			width = Math.Max(1, width / 2);
			height = Math.Max(1, height / 2);

			int offset = header.LevelOffsets[level];
			if (offset <= 0 || offset >= d.Length)
			{
				return false;
			}

			if (!Engine_TexPlanes.TryDecodeLevel(
					d, offset, header, width, height, out byte[] blocks, out _))
			{
				return false;
			}

			parts.Add(blocks);
		}

		int total = 0;
		foreach (byte[] part in parts)
		{
			total += part.Length;
		}

		chain = new byte[total];
		int cursor = 0;
		foreach (byte[] part in parts)
		{
			Buffer.BlockCopy(part, 0, chain, cursor, part.Length);
			cursor += part.Length;
		}

		levels = parts.Count;
		return true;
	}

    /// <summary>
    /// Decodes a file that carries a <see cref="TexHeader"/>, dispatching on the
    /// container the header names and falling back to the chain decoders for the ones
    /// that do hold a mip chain after the header.
    /// </summary>
	private static bool TryDecodeWithHeader(byte[] d, TexHeader header,
		out TexSurface surface, out string error)
	{
		surface = default!;
		error = string.Empty;

		if (header.IsRaw)
		{
			return TryDecodeRawSurface(d, header, out surface, out error);
		}

        // Level 0's PLANE RECORD, located by the header. This is the correct read: the
        // payload is one 48-byte record per level, not a chain, and only a record read
        // recovers the second plane of a two-plane family.
        int recordOffset = header.LevelOffsets[0] > 0 ? header.LevelOffsets[0] : TexHeader.Size;
        if (recordOffset < 0 || recordOffset >= d.Length)
        {
            error = $"header points level 0 at {recordOffset}, outside {d.Length} bytes";
            return false;
        }

        if (Engine_TexPlanes.TryDecodeLevel(d, recordOffset, header, out byte[] blocks, out string planeError))
        {
            // Stride alone cannot name the format: codecs 1, 2 and 3 all pack two planes
            // into 16 bytes, but codec 3 is DXN and the other two are DXT2/3/5. Labelling
            // all three Bc3 hands Godot a BC5 surface to decode as DXT5.
            TexSurfaceFormat format = header.BlockStride == 16
                ? (header.CodecId == 3 ? TexSurfaceFormat.Bc5 : TexSurfaceFormat.Bc3)
                : header.SurfaceFormat;

            if (TryDecodeMipChain(d, header, blocks, out byte[] chain, out int levels))
            {
                surface = new TexSurface
                {
                    Width = header.Width,
                    Height = header.Height,
                    Format = format,
                    Data = chain,
                    MipLevels = levels,
                };
                return true;
            }

            surface = new TexSurface
            {
                Width = header.Width,
                Height = header.Height,
                Format = format,
                Data = blocks,
            };
            return true;
        }

        // Fall back to the chain sniffer for anything the record path cannot decode —
        // an unported plane codec, or a file whose payload really is chain-shaped. It
        // returns a colour-only surface, which is still better than nothing.
        var payload = new byte[d.Length - recordOffset];
        Buffer.BlockCopy(d, recordOffset, payload, 0, payload.Length);

        if (!TryDecodePyramid(payload, out surface, out error) &&
            !TryDecodeSingle(payload, out surface, out error))
        {
            error = planeError.Length > 0 ? planeError : error;
            return false;
        }

        // The header names the ORIGINAL surface format; the chain decoders report what
        // they actually produced. Those differ whenever a family is only partly ported —
        // a DXT5 file whose colour plane decodes to BC1 blocks is BC1-shaped data with a
        // Bc3 label, and relabelling it makes Image.CreateFromData reject the buffer for
        // being half the size it expects.
        //
        // So the header only wins when the byte count agrees with it. That keeps the
        // authoritative format where it is safe — which is what recovers alpha — and
        // leaves a partly-decoded surface honestly labelled.
        TexSurfaceFormat declared = header.SurfaceFormat;
        if (declared != TexSurfaceFormat.Unsupported &&
            declared != surface.Format &&
            Fits(surface, declared))
        {
            surface = new TexSurface
            {
                Width = surface.Width,
                Height = surface.Height,
                Format = declared,
                Data = surface.Data,
                MipLevels = surface.MipLevels,
            };
        }

        return true;
    }

    /// <summary>
    /// True when a surface's byte count is consistent with a format, counting the top
    /// level only — a mipped surface carries more, never less.
    /// </summary>
	private static bool Fits(TexSurface surface, TexSurfaceFormat format)
	{
		int stride = format is TexSurfaceFormat.Bc3 or TexSurfaceFormat.Bc5 ? 16 : 8;
		int blocksW = Math.Max(1, (surface.Width + 3) / 4);
		int blocksH = Math.Max(1, (surface.Height + 3) / 4);
		return surface.Data.Length >= blocksW * blocksH * stride;
	}

    /// <summary>
    /// The <c>flags &amp; 0x02</c> container: the payload is exactly what the Xbox 360's
    /// GPU was handed — word-swapped for the big-endian bus and TILED — after a u32 byte
    /// count. Both have to be undone; untiled it is noise.
    /// </summary>
    private static bool TryDecodeRawSurface(byte[] d, TexHeader header,
        out TexSurface surface, out string error)
    {
        surface = default!;
        error = string.Empty;

        int blockBytes = header.TilingBlockBytes;
        TexSurfaceFormat format = header.SurfaceFormat;
        if (blockBytes == 0 || format == TexSurfaceFormat.Unsupported)
        {
            error = $"raw surface: unsupported pixel format {header.PixelFormat}";
            return false;
        }

        // A u32 count precedes the surface, immediately after the header.
        int at = TexHeader.Size;
        if (at + 4 > d.Length)
        {
            error = "raw surface: truncated before its byte count";
            return false;
        }

        int count = (int)Be(d, at);
        at += 4;
        if (count <= 0 || at + count > d.Length)
        {
            count = d.Length - at;
        }

        var payload = new byte[count];
        Buffer.BlockCopy(d, at, payload, 0, count);

        // Blocks for a compressed format, pixels for an uncompressed one.
        bool blockCompressed = blockBytes >= 8;
        int unitsWide = blockCompressed ? header.BlocksWide : header.Width;
        int unitsHigh = blockCompressed ? header.BlocksHigh : header.Height;

        // Endianness is per unit width: a compressed block is a stream of 16-bit fields,
        // an A8R8G8B8 pixel is one 32-bit word.
        if (blockCompressed)
        {
            Engine_Xbox360Tiling.SwapWords16(payload);
        }
        else
        {
            Engine_Xbox360Tiling.SwapWords32(payload);
        }

        byte[] data = Engine_Xbox360Tiling.Untile(payload, unitsWide, unitsHigh, blockBytes);

        // A8R8G8B8 is ARGB in memory; Godot's Rgba8 wants RGBA.
		if (format == TexSurfaceFormat.Rgba8)
		{
			SwizzleArgbToRgba(data, header.PixelFormat == (int)TexPixelFormat.X8R8G8B8);
		}

		surface = new TexSurface
		{
			Width = header.Width,
			Height = header.Height,
			Format = format,
			Data = data,
		};

		return true;
	}

    /// <summary>
    /// Rotates ARGB bytes into RGBA order in place. <paramref name="opaque"/> forces
    /// alpha to 255 for the X8 variant, whose alpha byte is undefined rather than 0 —
    /// trusting it there makes the whole surface invisible.
    /// </summary>
	private static void SwizzleArgbToRgba(byte[] data, bool opaque)
	{
		for (int i = 0; i + 3 < data.Length; i += 4)
		{
			byte a = data[i];
			data[i] = data[i + 1];
			data[i + 1] = data[i + 2];
			data[i + 2] = data[i + 3];
			data[i + 3] = opaque ? (byte)0xFF : a;
		}
	}

	private static bool TryDecodePyramid(byte[] d, out TexSurface surface, out string error)
	{
		surface = default!;
		error = string.Empty;

		if (!TryParseChain(d, out List<TexMip> mips, out error) || mips.Count < 1)
		{
			return false;
		}

		var levels = new List<byte[]>();
		TexSurfaceFormat format = TexSurfaceFormat.Unsupported;
		int width = 0;
		int height = 0;
		int nextWidth = 0;
		int nextHeight = 0;

		foreach (TexMip mip in mips)
		{
			byte[]? data = null;
			int levelWidth;
			int levelHeight;

			if (mip.CompFlag is 1 or 11)
			{
				if (!Engine_LhTexCodec.TryDecodeCompressedMip(
						d, mip.BodyOffset, mip.BodySize, mip.CompFlag == 11,
						out levelWidth, out levelHeight, out byte[] blocks, out _))
				{
					break;
				}

				data = blocks;
				if (format == TexSurfaceFormat.Unsupported) format = TexSurfaceFormat.Bc1;
				else if (format != TexSurfaceFormat.Bc1) break;
			}
			else if (mip.CompFlag is 2 or 3 or 4 && mip.HasSize)
			{
				levelWidth = mip.Width;
				levelHeight = mip.Height;
				if (!Engine_LhTexCodec.TryDecodeVariant(
						d, mip.BodyOffset, mip.BodySize, 2, levelWidth, levelHeight,
						out byte[] blocks, out _))
				{
					break;
				}

				data = blocks;
				if (format == TexSurfaceFormat.Unsupported) format = TexSurfaceFormat.Bc4;
				else if (format != TexSurfaceFormat.Bc4) break;
			}
			else if (mip.CompFlag == 7 && nextWidth > 0)
			{
				levelWidth = nextWidth;
				levelHeight = nextHeight;
				int want = LevelBytes(levelWidth, levelHeight);
				if (mip.BodySize < want)
				{
					break;
				}

				data = Slice(d, mip.BodyOffset, want);
			}
			else
			{
				break;
			}

			if (data is null)
			{
				break;
			}

			if (levels.Count == 0)
			{
				width = levelWidth;
				height = levelHeight;
			}
			else if (levelWidth != nextWidth || levelHeight != nextHeight)
			{
				break;
			}

			int expected = LevelBytes(levelWidth, levelHeight);
			if (data.Length != expected)
			{
				if (data.Length < expected)
				{
					break;
				}

				Array.Resize(ref data, expected);
			}

			levels.Add(data);
			nextWidth = Math.Max(1, levelWidth / 2);
			nextHeight = Math.Max(1, levelHeight / 2);

			if (levelWidth == 1 && levelHeight == 1)
			{
				break;
			}
		}

		if (levels.Count < 1 || format == TexSurfaceFormat.Unsupported)
		{
			return false;
		}

		int levelWidthNow = width;
		int levelHeightNow = height;
		for (int i = 1; i < levels.Count; ++i)
		{
			levelWidthNow = Math.Max(1, levelWidthNow / 2);
			levelHeightNow = Math.Max(1, levelHeightNow / 2);
		}

		int expectedLevels = ExpectedLevelCount(width, height);
		if (levels.Count < expectedLevels)
		{
			bool bc4 = format == TexSurfaceFormat.Bc4;
			int channels = bc4 ? 1 : 4;
			byte[] pixels = bc4
				? Engine_TexMipGen.DecodeBc4(levels[^1], levelWidthNow, levelHeightNow)
				: Engine_TexMipGen.DecodeBc1(levels[^1], levelWidthNow, levelHeightNow);

			while (levels.Count < expectedLevels)
			{
				pixels = Engine_TexMipGen.Downscale(pixels, levelWidthNow, levelHeightNow, channels);
				levelWidthNow = Math.Max(1, levelWidthNow / 2);
				levelHeightNow = Math.Max(1, levelHeightNow / 2);
				levels.Add(bc4
					? Engine_TexMipGen.EncodeBc4(pixels, levelWidthNow, levelHeightNow)
					: Engine_TexMipGen.EncodeBc1(pixels, levelWidthNow, levelHeightNow));
			}
		}

		int total = 0;
		foreach (byte[] level in levels) total += level.Length;
		var combined = new byte[total];
		int written = 0;
		foreach (byte[] level in levels)
		{
			Buffer.BlockCopy(level, 0, combined, written, level.Length);
			written += level.Length;
		}

		surface = new TexSurface
		{
			Width = width,
			Height = height,
			Format = format,
			Data = combined,
			MipLevels = levels.Count,
		};
		return true;
	}

	private static bool TryDecodeSingle(byte[] d, out TexSurface surface, out string error)
	{
		surface = default!;

		if (!TryParseChain(d, out List<TexMip> mips, out error))
		{
			return false;
		}

		int fallbackWidth = 0;
		int fallbackHeight = 0;
		foreach (TexMip mip in mips)
		{
			if (mip.HasSize)
			{
				fallbackWidth = mip.Width;
				fallbackHeight = mip.Height;
				break;
			}
		}

		foreach (TexMip mip in mips)
		{
			if (mip.CompFlag is 1 or 11)
			{
				if (Engine_LhTexCodec.TryDecodeCompressedMip(
						d, mip.BodyOffset, mip.BodySize, mip.CompFlag == 11,
						out int w, out int h, out byte[] colour, out string decodeError))
				{
					surface = new TexSurface
					{
						Width = w,
						Height = h,
						Format = TexSurfaceFormat.Bc1,
						Data = colour,
					};
					return true;
				}

				error = decodeError;
				continue;
			}

			if (mip.CompFlag is 2 or 3 or 4 && mip.HasSize)
			{
				if (Engine_LhTexCodec.TryDecodeVariant(
						d, mip.BodyOffset, mip.BodySize, 2, mip.Width, mip.Height,
						out byte[] single, out string variantError))
				{
					surface = new TexSurface
					{
						Width = mip.Width,
						Height = mip.Height,
						Format = TexSurfaceFormat.Bc4,
						Data = single,
					};
					return true;
				}

				error = variantError;
				continue;
			}

			if (mip.CompFlag == 7 && fallbackWidth > 0 && fallbackHeight > 0)
			{
				int blocksW = (fallbackWidth + 3) / 4;
				int blocksH = (fallbackHeight + 3) / 4;
				long bc1 = (long)blocksW * blocksH * 8;
				long bc3 = (long)blocksW * blocksH * 16;

				if (mip.BodySize >= bc1 && mip.BodySize < bc3)
				{
					surface = new TexSurface
					{
						Width = fallbackWidth,
						Height = fallbackHeight,
						Format = TexSurfaceFormat.Bc1,
						Data = Slice(d, mip.BodyOffset, (int)bc1),
					};
					return true;
				}

				if (mip.BodySize >= bc3)
				{
					surface = new TexSurface
					{
						Width = fallbackWidth,
						Height = fallbackHeight,
						Format = TexSurfaceFormat.Bc3,
						Data = Slice(d, mip.BodyOffset, (int)bc3),
					};
					return true;
				}
			}
		}

		if (error.Length == 0)
		{
			error = "no decodable mip (all records use an unported variant)";
		}

		return false;
	}

	private static byte[] Slice(byte[] source, int offset, int length)
	{
		var result = new byte[length];
		Buffer.BlockCopy(source, offset, result, 0, length);
		return result;
	}

	private static uint Be(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
}
