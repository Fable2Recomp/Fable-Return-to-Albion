using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Refii.Textures;

/// <summary>
/// The six faces of a cube <c>.tex</c>.
/// </summary>
/// <remarks>
/// THERE IS NO NEW CODEC HERE, and that is the whole finding. Every one of the 36 shipped cube
/// textures failed to decode with <c>plane 0: unsupported compFlag 24</c>, which read as a missing
/// compression scheme. It is not one: 24 is not a compFlag at all, it is
/// <c>faceOffset[0]</c> — and 24 is simply the byte size of the six-entry face table in front of
/// it. The old parser was reading the first three words of that table as a
/// <c>(compFlag, dataOffset, size)</c> triple, which is why the "flag" was always 24 and the
/// "size" was always the second face's offset.
///
/// A cube level record is:
///
///     u32 faceOffset[6]     relative to the level record start
///     per face:  a standard 48-byte plane record, then that face's data
///
/// So each face decodes through <see cref="Engine_TexPlanes.TryDecodeLevel"/> exactly as a 2D
/// level does, with the existing compFlags. Confirmed on three shipped files:
///
///   Katana_Iron_EnvMap 32x32   offsets 24 584 1144 1704 2264 2824, uniform step 560
///                              = 48-byte plane record + 512-byte raw BC1 face, and the level
///                              spans 84..3468 = 24 + 6*560 exactly.
///   ob_augment_black_env 128   offsets non-uniform because the faces are Huffman (compFlag 1);
///                              face 0 runs 108..2384 = 48 + 2228 exactly.
///   its last mip               offsets 24 80 136 192 248 304, step 56 = 48 + 8 (the minimum BC1
///                              block), and 24308 + 24 + 6*56 = 24668 = the end of the file.
/// </remarks>
public static class Engine_TexCube
{
	/// <summary>A cube has six faces, in the D3D order the guest binds them.</summary>
	public const int FaceCount = 6;

	/// <summary>Bytes of face-offset table in front of the first face.</summary>
	private const int FaceTableSize = FaceCount * 4;

	/// <summary>
	/// Decodes all six faces, each with its full mip chain.
	/// </summary>
	/// <remarks>
	/// Faces are returned in file order. The guest binds them through <c>tfetchCube</c>, which
	/// takes D3D's face order (+X, -X, +Y, -Y, +Z, -Z), and nothing in the file reorders them —
	/// so file order IS face order until something proves otherwise. Reordering on a hunch is how
	/// a reflection ends up mirrored in a way nobody can point at.
	/// </remarks>
	public static bool TryDecode(
		byte[] d, TexHeader header, out TexSurface[] faces, out string error)
	{
		faces = Array.Empty<TexSurface>();
		error = string.Empty;

		if (!header.IsCube)
		{
			error = "not a cube texture";
			return false;
		}

		int levels = Math.Min(Math.Max(1, header.LevelCount), header.LevelOffsets.Length);
		var chains = new List<byte[]>[FaceCount];
		for (int face = 0; face < FaceCount; ++face)
		{
			chains[face] = new List<byte[]>(levels);
		}

		int usableLevels = 0;
		int width = header.Width;
		int height = header.Height;

		for (int level = 0; level < levels; ++level)
		{
			int record = header.LevelOffsets[level];
			if (record <= 0 || record + FaceTableSize > d.Length)
			{
				break;
			}

			bool levelOk = true;
			var decoded = new byte[FaceCount][];

			for (int face = 0; face < FaceCount; ++face)
			{
				int faceOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(
					d.AsSpan(record + (face * 4), 4));

				int faceRecord = record + faceOffset;
				if (faceOffset < FaceTableSize ||
					faceRecord + Engine_TexPlanes.RecordSize > d.Length)
				{
					error = $"level {level} face {face}: table entry {faceOffset} is outside the file";
					levelOk = false;
					break;
				}

				if (!Engine_TexPlanes.TryDecodeLevel(
						d, faceRecord, header, width, height,
						out byte[] blocks, out string faceError))
				{
					error = $"level {level} face {face}: {faceError}";
					levelOk = false;
					break;
				}

				decoded[face] = blocks;
			}

			if (!levelOk)
			{
				// A LEVEL THAT FAILS TRUNCATES THE CHAIN, it does not fail the texture. The
				// smallest mips are where an exporter is most likely to have done something odd,
				// and a cube that renders with four of its six mips is worth more than one that
				// does not render at all.
				break;
			}

			for (int face = 0; face < FaceCount; ++face)
			{
				chains[face].Add(decoded[face]);
			}

			++usableLevels;
			width = Math.Max(1, width / 2);
			height = Math.Max(1, height / 2);
		}

		if (usableLevels == 0)
		{
			if (error.Length == 0)
			{
				error = "no cube level decoded";
			}

			return false;
		}

		faces = new TexSurface[FaceCount];
		for (int face = 0; face < FaceCount; ++face)
		{
			int total = 0;
			foreach (byte[] part in chains[face])
			{
				total += part.Length;
			}

			var payload = new byte[total];
			int cursor = 0;
			foreach (byte[] part in chains[face])
			{
				Buffer.BlockCopy(part, 0, payload, cursor, part.Length);
				cursor += part.Length;
			}

			faces[face] = new TexSurface
			{
				Width = header.Width,
				Height = header.Height,
				Format = header.SurfaceFormat,
				Data = payload,
				MipLevels = usableLevels,
			};
		}

		error = string.Empty;
		return true;
	}
}
