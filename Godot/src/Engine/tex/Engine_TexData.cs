using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using Refii.Core;

using Refii.Ui;

namespace Refii.Textures;

public enum TexSurfaceFormat
{
	Unsupported = 0,
	Bc1,
	Bc3,
	Bc4,

	Bc5,

	Rgba8,
}

public sealed class TexSurface
{
	public int Width { get; init; }

	public int Height { get; init; }

	public TexSurfaceFormat Format { get; init; }

	public byte[] Data { get; init; } = Array.Empty<byte>();

	/// <summary>
	/// How much of <see cref="Data"/> is the surface when it is a pooled buffer, which only
	/// <see cref="Engine_TexData.TryDecodePooled"/> hands out; -1 when it is all of it.
	/// </summary>
	internal int PooledLength { get; init; } = -1;

	/// <summary>The surface's bytes, whichever kind of buffer holds them.</summary>
	public ReadOnlySpan<byte> Bytes => PooledLength < 0 ? Data : new ReadOnlySpan<byte>(Data, 0, PooledLength);

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

	/// <summary>
	/// THE ALPHA SIBLING. A colour record's words 3..5 (u3, u4, u5) name a second body beside it:
	/// its compFlag (3 = the BC3 alpha plane), its offset from the record, its size. The planes
	/// path reads it through the header bank; a bare chain used to step over it and lose the
	/// alpha. 0 when the record has none.
	/// </summary>
	public uint AlphaCompFlag { get; init; }
	public int AlphaOffset { get; init; }
	public int AlphaSize { get; init; }
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
			bool hasSibling = u3 <= 24 && u4 == RecordSize + dataSize && u5 > 0 &&
				off + (long)u4 + u5 <= (long)d.Length + 32;
			uint alphaFlag = hasSibling ? u3 : 0u;
			int alphaOffset = hasSibling ? off + (int)u4 : 0;
			int alphaSize = hasSibling ? (int)Math.Min(u5, (uint)Math.Max(0, d.Length - (off + (int)u4))) : 0;
			if (compFlag == 7)
			{
				int available = Math.Max(0, d.Length - bodyStart);
				mips.Add(new TexMip
				{
					CompFlag = compFlag,
					BodyOffset = bodyStart,
					BodySize = (int)Math.Min(dataSize, (uint)available),
					HasSize = false,
					AlphaCompFlag = alphaFlag,
					AlphaOffset = alphaOffset,
					AlphaSize = alphaSize,
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
					AlphaCompFlag = alphaFlag,
					AlphaOffset = alphaOffset,
					AlphaSize = alphaSize,
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
		if (!TryDecodePooled(d, out surface, out error))
		{
			return false;
		}

		if (surface.PooledLength >= 0)
		{
			TexSurface pooled = surface;
			surface = new TexSurface
			{
				Width = pooled.Width,
				Height = pooled.Height,
				Format = pooled.Format,
				Data = pooled.Bytes.ToArray(),
				MipLevels = pooled.MipLevels,
			};
			Release(pooled);
		}

		return true;
	}

	/// <summary>
	/// <see cref="TryDecode"/> for a caller that is done with the surface before it returns: read
	/// it through <see cref="TexSurface.Bytes"/>, then <see cref="Release"/> it. A chain is a few
	/// megabytes at most and each load decodes hundreds.
	/// </summary>
	internal static bool TryDecodePooled(byte[] d, out TexSurface surface, out string error)
	{
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

	internal static void Release(TexSurface surface)
	{
		if (surface.PooledLength >= 0)
		{
			System.Buffers.ArrayPool<byte>.Shared.Return(surface.Data);
		}
	}

	/// <summary>A decode this large is a damaged header. Bodies top out at 8192 x 8192, 86 MB with its mips.</summary>
	private const long MaxPooledChainBytes = 1L << 30;

	private static long HeaderLevelBytes(int width, int height, int stride) =>
		(long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * stride;

	/// <summary>
	/// Decodes every level past the first into <paramref name="chain"/> behind it. A level that
	/// fails leaves the surface at level 0, returning false.
	/// </summary>
	private static bool TryDecodeMipChain(
		byte[] d, TexHeader header, int available, byte[] chain, ref int length, out int levels)
	{
		levels = 1;
		int cursor = length;
		int stride = header.BlockStride;
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

			if (!Engine_TexPlanes.TryDecodeLevelInto(d, offset, header, width, height, chain, cursor, out _))
			{
				return false;
			}

			cursor += (int)HeaderLevelBytes(width, height, stride);
		}

		length = cursor;
		levels = available;
		return true;
	}

	private static bool TryDecodeWithHeader(byte[] d, TexHeader header,
		out TexSurface surface, out string error)
	{
		surface = default!;
		error = string.Empty;

		// A CUBE IS NOT A SURFACE, and saying so plainly is the point. Every one of the 36 shipped
		// cube textures used to fail here with "plane 0: unsupported compFlag 24", which read as a
		// missing compression scheme and sent the search after a codec that does not exist — the
		// 24 is the size of the six-entry face table being misread as a plane triple. This path
		// genuinely cannot answer for a cube, so it names the one that can.
		if (header.IsCube)
		{
			error = "cube texture: decode it with Engine_TexCube.TryDecode";
			return false;
		}

		if (header.IsRaw)
		{
			return TryDecodeRawSurface(d, header, out surface, out error);
		}

		int recordOffset = header.LevelOffsets[0] > 0 ? header.LevelOffsets[0] : header.HeaderSize;
		if (recordOffset < 0 || recordOffset >= d.Length)
		{
			error = $"header points level 0 at {recordOffset}, outside {d.Length} bytes";
			return false;
		}

		TexSurfaceFormat format = header.BlockStride == 16
			? (header.CodecId == 3 ? TexSurfaceFormat.Bc5 : TexSurfaceFormat.Bc3)
			: header.SurfaceFormat;

		// EVERY LEVEL IN ONE BUFFER, sized from the header before the first is decoded.
		int stride = header.BlockStride;
		int available = Math.Max(1, Math.Min(header.LevelCount, header.LevelOffsets.Length));
		long chainBytes = 0;
		for (int level = 0, w = header.Width, h = header.Height; level < available; ++level)
		{
			chainBytes += HeaderLevelBytes(w, h, stride);
			w = Math.Max(1, w / 2);
			h = Math.Max(1, h / 2);
		}

		string planeError;
		if (chainBytes > MaxPooledChainBytes)
		{
			if (Engine_TexPlanes.TryDecodeLevel(d, recordOffset, header, out byte[] blocks, out planeError))
			{
				surface = new TexSurface
				{
					Width = header.Width,
					Height = header.Height,
					Format = format,
					Data = blocks,
				};
				return true;
			}
		}
		else
		{
			byte[] chain = System.Buffers.ArrayPool<byte>.Shared.Rent((int)chainBytes);
			if (Engine_TexPlanes.TryDecodeLevelInto(d, recordOffset, header, header.Width, header.Height, chain, 0, out planeError))
			{
				int length = (int)HeaderLevelBytes(header.Width, header.Height, stride);
				TryDecodeMipChain(d, header, available, chain, ref length, out int levels);
				surface = new TexSurface
				{
					Width = header.Width,
					Height = header.Height,
					Format = format,
					Data = chain,
					PooledLength = length,
					MipLevels = levels,
				};
				return true;
			}

			System.Buffers.ArrayPool<byte>.Shared.Return(chain);
		}

		var payload = new byte[d.Length - recordOffset];
		Buffer.BlockCopy(d, recordOffset, payload, 0, payload.Length);

		if (!TryDecodePyramid(payload, out surface, out error) &&
			!TryDecodeSingle(payload, out surface, out error))
		{
			error = planeError.Length > 0 ? planeError : error;
			return false;
		}

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

	private static bool Fits(TexSurface surface, TexSurfaceFormat format)
	{
		int stride = format is TexSurfaceFormat.Bc3 or TexSurfaceFormat.Bc5 ? 16 : 8;
		int blocksW = Math.Max(1, (surface.Width + 3) / 4);
		int blocksH = Math.Max(1, (surface.Height + 3) / 4);
		return surface.Data.Length >= blocksW * blocksH * stride;
	}

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

		int at = header.HeaderSize;
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

		bool blockCompressed = blockBytes >= 8;
		int unitsWide = blockCompressed ? header.BlocksWide : header.Width;
		int unitsHigh = blockCompressed ? header.BlocksHigh : header.Height;

		if (blockCompressed)
		{
			Engine_Xbox360Tiling.SwapWords16(payload);
		}
		else if (format != TexSurfaceFormat.Rgba8)
		{
			Engine_Xbox360Tiling.SwapWords32(payload);
		}

		// A8R8G8B8 / X8R8G8B8 ARE ALREADY A,R,G,B PER TEXEL in the bank, and the untile moves
		// whole texels — swapping the dwords first handed the swizzle B,G,R,A, so a 64x64
		// button icon came out as a dark olive disc at alpha 73 with a faint core: every
		// uncompressed 32-bit GUI texture was drawn with its channels rotated.

		byte[] data = Engine_Xbox360Tiling.Untile(payload, unitsWide, unitsHigh, blockBytes);

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

	/// <summary>
	/// A colour record decoded with its BC3 alpha sibling when it has one: BC3 blocks (alpha
	/// first, colour second, as the planes path lays them) in the game's own scheme; otherwise
	/// BC1. The alpha plane is decoded in the scheme the colour plane turned out to use.
	/// </summary>
	private static bool TryDecodeColourRecord(
		byte[] d, TexMip mip, out int width, out int height, out byte[] data, out TexSurfaceFormat format, out string error)
	{
		data = Array.Empty<byte>();
		format = TexSurfaceFormat.Unsupported;
		if (!Engine_LhTexCodec.TryDecodeCompressedMip(
				d, mip.BodyOffset, mip.BodySize, mip.CompFlag == 11, Engine_LhTexCodec.TexScheme.Auto,
				out width, out height, out byte[] colour, out _, out _, out Engine_LhTexCodec.TexScheme scheme, out error))
		{
			return false;
		}

		format = TexSurfaceFormat.Bc1;
		data = colour;
		if (mip.AlphaCompFlag != 3 || mip.AlphaSize <= 0)
		{
			return true;
		}

		if (!Engine_LhTexCodec.TryDecodeVariant(
				d, mip.AlphaOffset, mip.AlphaSize, 2, width, height, scheme, out byte[] alpha, out string alphaError))
		{
			// The colour stands on its own; the alpha is reported, not fatal.
			error = alphaError;
			return true;
		}

		int blocks = colour.Length / 8;
		var joined = new byte[blocks * 16];
		for (int i = 0; i < blocks; ++i)
		{
			Buffer.BlockCopy(alpha, i * 8, joined, i * 16, 8);
			Buffer.BlockCopy(colour, i * 8, joined, (i * 16) + 8, 8);
		}

		data = joined;
		format = TexSurfaceFormat.Bc3;
		return true;
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
				if (!TryDecodeColourRecord(d, mip, out levelWidth, out levelHeight, out byte[] blocks, out TexSurfaceFormat levelFormat, out _))
				{
					break;
				}
				data = blocks;
				if (format == TexSurfaceFormat.Unsupported) format = levelFormat;
				else if (format != levelFormat) break;
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

			int expected = LevelBytes(levelWidth, levelHeight) * (format == TexSurfaceFormat.Bc3 ? 2 : 1);
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
		// A BC3 chain keeps the levels it has; the image builder extends a short chain itself.
		if (levels.Count < expectedLevels && format != TexSurfaceFormat.Bc3)
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
				if (TryDecodeColourRecord(d, mip, out int w, out int h, out byte[] colour, out TexSurfaceFormat single, out string decodeError))
				{
					surface = new TexSurface
					{
						Width = w,
						Height = h,
						Format = single,
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
