using System;

namespace Refii.Models;

/// <summary>New geometry for one of a model's mesh buffers.</summary>
public sealed class MdlGeometryEdit
{
	/// <summary>Which buffer in <see cref="MdlInfo.MeshBuffers"/>.</summary>
	public int BufferIndex;

	/// <summary>Three floats per vertex.</summary>
	public float[] Positions = Array.Empty<float>();

	/// <summary>Two per vertex.</summary>
	public float[] Uvs = Array.Empty<float>();

	/// <summary>Three per vertex. Empty leaves the model's own normals alone.</summary>
	public float[] Normals = Array.Empty<float>();

	/// <summary>Four per vertex (xyz + handedness). Empty leaves the model's own alone.</summary>
	public float[] Tangents = Array.Empty<float>();

	/// <summary>Empty leaves the model's own indices alone.</summary>
	public ushort[] Indices = Array.Empty<ushort>();
}

/// <summary>
/// Replaces a model's geometry, keeping everything else byte for byte.
///
/// THE HEADER IS CARRIED, NOT REBUILT. A `.mdl` states far more than geometry — bounds, mesh and
/// material names, the movement block, tags, hide regions — and our reader walks parts of that
/// structure heuristically, which is fine for reading and no basis at all for writing. So this
/// takes the model being replaced as the template and overwrites only the two regions whose
/// extent the file states exactly: the vertex buffer and the index buffer. Everything a
/// hand-written file could get wrong is a byte copied from a file the game already loads.
///
/// It is the same bargain the game-database overlay makes, for the same reason: change what is
/// understood exactly, carry what is not.
///
/// SAME COUNTS. Vertex and index counts live in the descriptor this does not rewrite, so a
/// replacement keeps the topology of what it replaces — move vertices, re-UV, re-normal, rewind
/// triangles. Changing the counts means moving every offset after them and is a later step.
/// </summary>
public static class Engine_MdlWriter
{
	/// <summary>The rigid stream-0 layout, from the guest's own declaration builder.</summary>
	public const int RigidStride = 20;

	public const int SkinnedStride = 28;

	public const int TreeStride = 36;

	public static int StrideFor(MdlMeshBuffer buffer) =>
		buffer.IsEngineSkinned ? SkinnedStride : buffer.IsEngineTree ? TreeStride : RigidStride;

	public static int UvOffsetFor(MdlMeshBuffer buffer) => buffer.IsEngineSkinned ? 20 : 12;

	public static int TangentOffsetFor(MdlMeshBuffer buffer) => buffer.IsEngineSkinned ? 24 : 16;

	/// <summary>
	/// Writes <paramref name="edit"/> into a copy of <paramref name="original"/>.
	///
	/// <paramref name="info"/> must be the parse of that same file — its buffer offsets are
	/// absolute file positions and are what says where to write.
	/// </summary>
	public static bool TryReplaceGeometry(
		byte[] original, MdlInfo info, MdlGeometryEdit edit, out byte[] result, out string error)
	{
		result = original;
		error = string.Empty;

		if (edit.BufferIndex < 0 || edit.BufferIndex >= info.MeshBuffers.Count)
		{
			error =
				$"this model has {info.MeshBuffers.Count} mesh buffer(s); there is no buffer " +
				$"{edit.BufferIndex}";
			return false;
		}

		MdlMeshBuffer buffer = info.MeshBuffers[edit.BufferIndex];
		int stride = StrideFor(buffer);
		int vertexCount = (int)buffer.VertexCount;
		int indexCount = (int)buffer.FaceCount;

		if (edit.Positions.Length != 0 && edit.Positions.Length != vertexCount * 3)
		{
			error =
				$"this buffer holds {vertexCount} vertices; the replacement has " +
				$"{edit.Positions.Length / 3}. Vertex and index counts are stated in the part of " +
				"the file this does not rewrite, so a replacement keeps the same topology.";
			return false;
		}

		if (edit.Indices.Length != 0 && edit.Indices.Length != indexCount)
		{
			error =
				$"this buffer holds {indexCount} indices; the replacement has {edit.Indices.Length}.";
			return false;
		}

		if (!Fits(original, buffer.VertexOffset, (long)vertexCount * stride) ||
			!Fits(original, buffer.FaceOffset, (long)indexCount * 2))
		{
			error = "the model's own buffers run past the end of the file";
			return false;
		}

		var bytes = (byte[])original.Clone();

		for (int v = 0; v < vertexCount; v++)
		{
			int p = buffer.VertexOffset + (v * stride);

			if (edit.Positions.Length != 0)
			{
				WriteHalf(bytes, p, edit.Positions[v * 3]);
				WriteHalf(bytes, p + 2, edit.Positions[(v * 3) + 1]);
				WriteHalf(bytes, p + 4, edit.Positions[(v * 3) + 2]);
			}

			if (edit.Uvs.Length != 0)
			{
				int u = p + UvOffsetFor(buffer);
				WriteHalf(bytes, u, edit.Uvs[v * 2]);
				WriteHalf(bytes, u + 2, edit.Uvs[(v * 2) + 1]);
			}

			// A rigid buffer states its normal at +8; a skinned one keeps the same slot, with the
			// blend indices and weights after it.
			if (edit.Normals.Length != 0)
			{
				WriteU32(bytes, p + 8, EncodeNormal_11_11_10(
					edit.Normals[v * 3], edit.Normals[(v * 3) + 1], edit.Normals[(v * 3) + 2]));
			}

			if (edit.Tangents.Length != 0)
			{
				WriteU32(bytes, p + TangentOffsetFor(buffer), EncodeTangent_DEC3N(
					edit.Tangents[v * 4], edit.Tangents[(v * 4) + 1],
					edit.Tangents[(v * 4) + 2], edit.Tangents[(v * 4) + 3]));
			}
		}

		for (int i = 0; i < edit.Indices.Length; i++)
		{
			int at = buffer.FaceOffset + (i * 2);
			bytes[at] = (byte)(edit.Indices[i] >> 8);
			bytes[at + 1] = (byte)edit.Indices[i];
		}

		result = bytes;
		return true;
	}

	/// <summary>
	/// Replaces geometry AND its topology — a different vertex or triangle count.
	///
	/// WHY THIS IS POSSIBLE AT ALL: nothing in a `.mdl` is located by a stored offset. Every field
	/// is found by walking, and the geometry is inline — verified on 1,459 of 1,459 real buffers,
	/// where `vertexOffset + vertexCount x stride` lands exactly on the index data. So changing
	/// the counts moves bytes but breaks no pointers; only three stated LENGTHS have to follow.
	///
	/// The counts sit at fixed positions relative to the vertex data, derived backwards through
	/// the record's sequential fields and confirmed against the file on **1,688 of 1,688** engine
	/// rigid buffers. The 136 skinned buffers in that sample use a different record layout
	/// (`sub_82B26460`), so this refuses them rather than guessing.
	///
	/// The new geometry is written as a single TRIANGLE LIST section, which the shipped data
	/// proves the engine reads: of 339 buffers whose sections all state list, 339 have
	/// `indexCount == 3 x primCount` and none carries a restart index.
	/// </summary>
	public static bool TryReplaceMesh(
		byte[] original,
		MdlInfo info,
		int bufferIndex,
		float[] positions,
		float[] uvs,
		float[] normals,
		float[] tangents,
		ushort[] triangleIndices,
		out byte[] result,
		out string error)
	{
		result = original;
		error = string.Empty;

		if (bufferIndex < 0 || bufferIndex >= info.MeshBuffers.Count)
		{
			error = $"there is no mesh buffer {bufferIndex}";
			return false;
		}

		MdlMeshBuffer buffer = info.MeshBuffers[bufferIndex];
		if (buffer.IsEngineSkinned || buffer.IsEngineTree || buffer.IsAltPath || buffer.IsFoliagePath)
		{
			error =
				"only a static mesh can be RESIZED — a skinned, tree or foliage buffer states its " +
				"counts in a different record layout, and a skinned one would also need blend " +
				"indices matched to this model's own skeleton. Its mesh can still be edited at " +
				"the same vertex and triangle count, which leaves that data untouched.";
			return false;
		}

		if (buffer.SubMeshes.Count < 1)
		{
			error = "this buffer states no sections, so there is nothing to rewrite";
			return false;
		}

		if (info.LodSizes.Length == 0 || info.LodSizes[0] == 0)
		{
			error = "this model states no geometry for its first LOD";
			return false;
		}

		// THE OTHER LODs ARE COLLAPSED ONTO THIS ONE, using the engine's own mechanism.
		//
		// A size of 0 at slot i does not mean "empty": `sub_8221CE48` recurses to slot i-1 and
		// SHARES its object, which is why the shipped data contains headers like
		// `lodCount 3, sizes (15374, 0, 7888)`. So a replacement writes its mesh into LOD 0 and
		// zeroes the rest, and every distance draws the mod's mesh.
		//
		// Rewriting each LOD separately would need a decimated mesh per level, which is the
		// modder's decision and not something to invent from their one mesh.
		long lodZeroEnd = BodyStart + info.LodSizes[0];
		if (buffer.VertexOffset < BodyStart)
		{
			error = "this model's first mesh buffer sits before its geometry";
			return false;
		}

		int vertexCount = positions.Length / 3;
		if (vertexCount == 0 || positions.Length % 3 != 0)
		{
			error = "the replacement has no positions, or a count that is not a multiple of three";
			return false;
		}

		if (vertexCount > ushort.MaxValue)
		{
			error = $"{vertexCount} vertices; indices are 16-bit, so a mesh cannot exceed 65,535";
			return false;
		}

		if (triangleIndices.Length == 0 || triangleIndices.Length % 3 != 0)
		{
			error = "the replacement's indices are not whole triangles";
			return false;
		}

		foreach (ushort index in triangleIndices)
		{
			if (index >= vertexCount)
			{
				error = $"an index reaches vertex {index}, past the {vertexCount} supplied";
				return false;
			}
		}

		// The record's fields are sequential, so their positions come backwards from the vertex
		// data. Checked against the file before anything is written — a derivation that has
		// drifted must not silently overwrite a string.
		int sectionsAt = buffer.VertexOffset - (buffer.SubMeshes.Count * SectionSize);
		int sectionCountAt = sectionsAt - 4;
		int vertexCountAt = sectionCountAt - 16 - 12 - 12 - 4;
		int indexCountAt = vertexCountAt - 4;

		if (indexCountAt < 104 ||
			ReadU32(original, vertexCountAt) != buffer.VertexCount ||
			ReadU32(original, indexCountAt) != buffer.FaceCount ||
			ReadU32(original, sectionCountAt) != (uint)buffer.SubMeshes.Count)
		{
			error =
				"this model's record does not sit where a static mesh's record sits, so the " +
				"counts could not be located. It has not been touched.";
			return false;
		}

		int oldStride = StrideFor(buffer);
		long oldGeometry = ((long)buffer.VertexCount * oldStride) + ((long)buffer.FaceCount * 2);
		int geometryAt = buffer.VertexOffset;
		long tailAt = geometryAt + oldGeometry;
		if (tailAt > original.Length)
		{
			error = "the model's own geometry runs past the end of the file";
			return false;
		}

		// A SECOND VERTEX STREAM IS SIZED PER VERTEX, AND THIS CANNOT REBUILD IT.
		//
		// After the indices a buffer may carry `vertexCount x 16 x N` more bytes — the extra UV
		// sets, whose stride is stated once per LOD. Carrying that across verbatim while changing
		// the vertex count leaves the walker consuming a length that no longer matches what is
		// there, and the model stops parsing as an engine mesh at all.
		//
		// `TH_Hammers_Hammer` is the shape of it: 1,354 vertices and 31,316 bytes of geometry
		// inside a 54,104-byte block, where 1,354 x 16 = 21,664 accounts for almost all the rest.
		// Replacing its mesh with a smaller one succeeded on every field this writes and still
		// produced a file the engine parser rejected.
		//
		// Refused rather than guessed at: a same-count replacement (`TryReplaceGeometry`) is
		// unaffected and remains the way to edit these.
		long blockEnd = lodZeroEnd;
		if (tailAt > blockEnd)
		{
			error = "this model's first mesh buffer is not inside its first LOD";
			return false;
		}

		long afterIndices = blockEnd - tailAt;

		// THE SECOND STREAM IS REBUILT AT THE NEW VERTEX COUNT WHEN ITS SIZE CAN BE DERIVED.
		//
		// It holds N extra float4 UV sets per vertex, so its length is `vertexCount x 16 x N` and
		// N follows from what the block already spends: 699 of 703 single-buffer static models
		// account for exactly `vertexCount x 16 x N + 4`, with N either 1 (529 of them) or 0
		// (170). The four bytes are the trailing word.
		//
		// Only for a single-buffer model: with more than one, the bytes after the first buffer's
		// indices are the next record, and dividing them by a vertex count would be arithmetic on
		// something else entirely.
		//
		// The rebuilt stream is zeroed. It carries extra UV sets that nothing on our side reads —
		// the mount decodes stream 0 — and the guest never draws; what matters is that its LENGTH
		// matches the vertex count, because the walker consumes it by that arithmetic.
		bool vertexCountUnchanged = vertexCount == buffer.VertexCount;
		long perVertexStream = (long)buffer.VertexCount * SecondStreamSetBytes;
		int secondStreamSets = 0;
		bool rebuildSecondStream = false;

		if (!vertexCountUnchanged && afterIndices > TrailingAllowance && perVertexStream > 0)
		{
			// N IS STATED ONCE PER LOD, so it is derived from the LAST buffer — the only one
			// whose stream is followed by nothing but the trailing word. For every earlier
			// buffer the bytes after its indices are the next record, and dividing those by a
			// vertex count would be arithmetic on something else entirely.
			int sets = DeriveSecondStreamSets(info, blockEnd);
			if (sets > 0)
			{
				secondStreamSets = sets;
				rebuildSecondStream = true;
			}
		}

		if (afterIndices > TrailingAllowance && !vertexCountUnchanged && !rebuildSecondStream)
		{
			error =
				$"this buffer carries {afterIndices} bytes after its indices — a second vertex " +
				$"stream, sized per vertex. A replacement must keep its {buffer.VertexCount} " +
				$"vertices (this one has {vertexCount}); the triangles may change freely.";
			return false;
		}

		byte[] vertices = EncodeVertices(positions, uvs, normals, tangents, vertexCount);
		var indices = new byte[triangleIndices.Length * 2];
		for (int i = 0; i < triangleIndices.Length; i++)
		{
			indices[i * 2] = (byte)(triangleIndices[i] >> 8);
			indices[(i * 2) + 1] = (byte)triangleIndices[i];
		}

		// SEVERAL SECTIONS COLLAPSE TO ONE. A replacement is a single mesh, so it is written as a
		// single triangle list; the extra section records are spliced out and the count that
		// stated them rewritten. Their positions are all BEFORE the sections, so nothing that is
		// patched below moves.
		var built = new System.IO.MemoryStream(original.Length + vertices.Length + indices.Length);
		built.Write(original, 0, sectionsAt);
		built.Write(new byte[SectionSize]);
		built.Write(vertices, 0, vertices.Length);
		built.Write(indices, 0, indices.Length);

		int carryFrom = (int)tailAt;
		if (rebuildSecondStream)
		{
			built.Write(new byte[(long)vertexCount * SecondStreamSetBytes * secondStreamSets]);
			carryFrom += (int)(perVertexStream * secondStreamSets);
		}

		// Only to the end of LOD 0 — the later LODs' bytes are dropped, and their sizes zeroed
		// below so they alias this one.
		built.Write(original, carryFrom, (int)(blockEnd - carryFrom));
		byte[] bytes = built.ToArray();

		WriteU32(bytes, vertexCountAt, (uint)vertexCount);
		WriteU32(bytes, indexCountAt, (uint)indices.Length / 2);
		WriteU32(bytes, sectionCountAt, 1);

		// One section, stating a triangle list over the whole buffer.
		int section = sectionsAt;
		WriteU32(bytes, section, 0xFFFFFFFFu);
		WriteU32(bytes, section + 4, 0);
		bytes[section + 8] = 0;
		WriteU32(bytes, section + 9, (uint)(triangleIndices.Length / 3));
		WriteU32(bytes, section + 13, 0);

		WriteBounds(bytes, positions, vertexCount, section + 17, section + 29);

		// THE RECORD'S BOUNDS SIT AFTER THE COUNTS, NOT BEFORE THEM. The order on disk is
		// indexCount, vertexCount, aabbMin, aabbMax, sphere, sectionCount — which is exactly how
		// `vertexCountAt` was derived backwards from the sections. Writing them at negative
		// offsets put the sphere on top of the vertex count and the box over the index count, and
		// the rewritten file stopped parsing as an engine model at all.
		WriteBounds(bytes, positions, vertexCount, vertexCountAt + 4, vertexCountAt + 16);
		WriteSphere(bytes, positions, vertexCount, vertexCountAt + 28);
		WriteHeaderBounds(bytes, positions, vertexCount);

		// THE LOD SIZES ARE THE LENGTHS THAT MUST FOLLOW. Slot 0 is the exact byte length of the
		// block that is now the whole payload; the rest are zeroed so they alias it.
		WriteU32(bytes, 0x3C, (uint)(bytes.Length - BodyStart));
		for (int lod = 1; lod < 3; lod++)
		{
			WriteU32(bytes, 0x3C + (lod * 4), 0);
		}

		result = bytes;
		return true;
	}

	/// <summary>A section record: two words, the strip flag, primitive count, start, two vec3.</summary>
	public const int SectionSize = 41;

	/// <summary>Where the geometry payload begins, stated by the header as 16 + headerSize.</summary>
	public const int BodyStart = 104;

	/// <summary>
	/// How much may follow the index data before this refuses to resize.
	///
	/// A buffer with nothing after it carries a single trailing word; anything substantially
	/// larger is a per-vertex stream this does not rebuild.
	/// </summary>
	public const int TrailingAllowance = 64;

	/// <summary>One extra UV set is a float4 per vertex.</summary>
	public const int SecondStreamSetBytes = 16;

	/// <summary>The word that closes a mesh-buffer record.</summary>
	public const int TrailingWordBytes = 4;

	/// <summary>
	/// A sanity bound on the derived set count. The shipped data uses 0 or 1; anything large is
	/// arithmetic that happened to divide, not a real stream.
	/// </summary>
	public const int MaxSecondStreamSets = 4;

	private static byte[] EncodeVertices(
		float[] positions, float[] uvs, float[] normals, float[] tangents, int vertexCount)
	{
		var vertices = new byte[vertexCount * RigidStride];
		for (int v = 0; v < vertexCount; v++)
		{
			int p = v * RigidStride;
			WriteHalf(vertices, p, positions[v * 3]);
			WriteHalf(vertices, p + 2, positions[(v * 3) + 1]);
			WriteHalf(vertices, p + 4, positions[(v * 3) + 2]);

			WriteU32(vertices, p + 8, normals.Length >= (v + 1) * 3
				? EncodeNormal_11_11_10(normals[v * 3], normals[(v * 3) + 1], normals[(v * 3) + 2])
				: EncodeNormal_11_11_10(0f, 0f, 1f));

			WriteHalf(vertices, p + 12, uvs.Length >= (v + 1) * 2 ? uvs[v * 2] : 0f);
			WriteHalf(vertices, p + 14, uvs.Length >= (v + 1) * 2 ? uvs[(v * 2) + 1] : 0f);

			WriteU32(vertices, p + 16, tangents.Length >= (v + 1) * 4
				? EncodeTangent_DEC3N(
					tangents[v * 4], tangents[(v * 4) + 1], tangents[(v * 4) + 2], tangents[(v * 4) + 3])
				: EncodeTangent_DEC3N(1f, 0f, 0f, 1f));
		}

		return vertices;
	}

	private static void WriteBounds(byte[] bytes, float[] positions, int vertexCount, int minAt, int maxAt)
	{
		Extent(positions, vertexCount, out float[] min, out float[] max);
		for (int i = 0; i < 3; i++)
		{
			WriteU32(bytes, minAt + (i * 4), unchecked((uint)BitConverter.SingleToInt32Bits(min[i])));
			WriteU32(bytes, maxAt + (i * 4), unchecked((uint)BitConverter.SingleToInt32Bits(max[i])));
		}
	}

	private static void WriteSphere(byte[] bytes, float[] positions, int vertexCount, int at)
	{
		Sphere(positions, vertexCount, out float[] centre, out float radius);
		for (int i = 0; i < 3; i++)
		{
			WriteU32(bytes, at + (i * 4), unchecked((uint)BitConverter.SingleToInt32Bits(centre[i])));
		}

		WriteU32(bytes, at + 12, unchecked((uint)BitConverter.SingleToInt32Bits(radius)));
	}

	/// <summary>
	/// The file header's sphere, AABB and the diameter at 0x48.
	///
	/// `file[0x48] == max(2 x radius, 0.5)` in all 5,446 shipped headers, exactly. Nothing was
	/// found reading it, but the data says what correct is and it costs nothing to be right.
	/// </summary>
	private static void WriteHeaderBounds(byte[] bytes, float[] positions, int vertexCount)
	{
		WriteSphere(bytes, positions, vertexCount, 0x10);
		WriteBounds(bytes, positions, vertexCount, 0x20, 0x2C);
		Sphere(positions, vertexCount, out _, out float radius);
		WriteU32(bytes, 0x48,
			unchecked((uint)BitConverter.SingleToInt32Bits(MathF.Max(2f * radius, 0.5f))));
	}

	private static void Extent(float[] positions, int vertexCount, out float[] min, out float[] max)
	{
		min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
		max = new[] { float.MinValue, float.MinValue, float.MinValue };
		for (int v = 0; v < vertexCount; v++)
		{
			for (int i = 0; i < 3; i++)
			{
				float value = positions[(v * 3) + i];
				min[i] = MathF.Min(min[i], value);
				max[i] = MathF.Max(max[i], value);
			}
		}
	}

	/// <summary>
	/// Centre of the box, radius to the furthest vertex — never zero, because the guest divides
	/// by it (`1.0 / radius` into a vertex-shader constant at 0x822428e4).
	/// </summary>
	private static void Sphere(float[] positions, int vertexCount, out float[] centre, out float radius)
	{
		Extent(positions, vertexCount, out float[] min, out float[] max);
		centre = new float[3];
		for (int i = 0; i < 3; i++)
		{
			centre[i] = (min[i] + max[i]) * 0.5f;
		}

		radius = 0f;
		for (int v = 0; v < vertexCount; v++)
		{
			float dx = positions[v * 3] - centre[0];
			float dy = positions[(v * 3) + 1] - centre[1];
			float dz = positions[(v * 3) + 2] - centre[2];
			radius = MathF.Max(radius, MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz)));
		}

		if (radius <= 0f)
		{
			radius = 1e-3f;
		}
	}

	private static uint ReadU32(byte[] data, int at) =>
		((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3];

	/// <summary>
	/// How many extra float4 UV sets each vertex carries, from the last buffer in the block.
	///
	/// Zero when it cannot be derived — which is the signal to refuse rather than to assume none.
	/// </summary>
	private static int DeriveSecondStreamSets(MdlInfo info, long blockEnd)
	{
		MdlMeshBuffer? last = null;
		foreach (MdlMeshBuffer candidate in info.MeshBuffers)
		{
			if (last is null || candidate.VertexOffset > last.VertexOffset)
			{
				last = candidate;
			}
		}

		if (last is null || last.VertexCount == 0)
		{
			return 0;
		}

		long end = last.VertexOffset + ((long)last.VertexCount * StrideFor(last)) +
			((long)last.FaceCount * 2);
		long after = blockEnd - end;
		long perVertex = (long)last.VertexCount * SecondStreamSetBytes;

		if (after == TrailingWordBytes)
		{
			return 0;
		}

		if (after <= TrailingWordBytes || perVertex <= 0 ||
			(after - TrailingWordBytes) % perVertex != 0)
		{
			return 0;
		}

		long sets = (after - TrailingWordBytes) / perVertex;
		return sets is > 0 and <= MaxSecondStreamSets ? (int)sets : 0;
	}

	private static bool Fits(byte[] data, int offset, long length) =>
		offset >= 0 && offset + length <= data.Length;

	private static void WriteU32(byte[] data, int at, uint value)
	{
		data[at] = (byte)(value >> 24);
		data[at + 1] = (byte)(value >> 16);
		data[at + 2] = (byte)(value >> 8);
		data[at + 3] = (byte)value;
	}

	private static void WriteHalf(byte[] data, int at, float value)
	{
		ushort half = FloatToHalf(value);
		data[at] = (byte)(half >> 8);
		data[at + 1] = (byte)half;
	}

	/// <summary>
	/// The inverse of <see cref="MdlReader.HalfToFloat"/>, round-to-nearest-even.
	///
	/// Exact for anything that came out of a half in the first place, which is what makes a
	/// re-emitted position byte-identical to the one it replaced.
	/// </summary>
	public static ushort FloatToHalf(float value)
	{
		uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(value));
		uint sign = (bits >> 16) & 0x8000u;
		int exponent = (int)((bits >> 23) & 0xFF) - 127 + 15;
		uint mantissa = bits & 0x7FFFFFu;

		if (((bits >> 23) & 0xFF) == 0xFF)
		{
			// Infinity, or a NaN kept as one rather than turned into a number.
			return (ushort)(sign | 0x7C00u | (mantissa != 0 ? 0x200u : 0u));
		}

		if (exponent >= 0x1F)
		{
			return (ushort)(sign | 0x7C00u);
		}

		if (exponent <= 0)
		{
			if (exponent < -10)
			{
				return (ushort)sign;
			}

			mantissa |= 0x800000u;
			int shift = 14 - exponent;
			uint denormal = mantissa >> shift;
			if (((mantissa >> (shift - 1)) & 1) != 0)
			{
				denormal++;
			}

			return (ushort)(sign | denormal);
		}

		uint result = (uint)(exponent << 10) | (mantissa >> 13);
		if ((mantissa & 0x1000u) != 0 && ((mantissa & 0x0FFFu) != 0 || (result & 1) != 0))
		{
			result++;
		}

		return (ushort)(sign | result);
	}

	/// <summary>
	/// The inverse of <see cref="MdlReader.DecodeNormal_11_11_10"/>: x and y over 1023 in eleven
	/// bits, z over 511 in ten.
	/// </summary>
	public static uint EncodeNormal_11_11_10(float x, float y, float z)
	{
		uint ix = (uint)Quantise(x, 1023f, -1024, 1023) & 0x7FFu;
		uint iy = (uint)Quantise(y, 1023f, -1024, 1023) & 0x7FFu;
		uint iz = (uint)Quantise(z, 511f, -512, 511) & 0x3FFu;
		return ix | (iy << 11) | (iz << 22);
	}

	/// <summary>
	/// The inverse of <see cref="MdlReader.DecodeTangent_DEC3N"/>. The two top bits carry
	/// handedness: the decoder reads 0 and 1 as +1 and anything else as -1.
	/// </summary>
	public static uint EncodeTangent_DEC3N(float x, float y, float z, float w)
	{
		uint ix = (uint)Quantise(x, 511f, -512, 511) & 0x3FFu;
		uint iy = (uint)Quantise(y, 511f, -512, 511) & 0x3FFu;
		uint iz = (uint)Quantise(z, 511f, -512, 511) & 0x3FFu;
		uint iw = w < 0f ? 3u : 1u;
		return (iw << 30) | (ix << 20) | (iy << 10) | iz;
	}

	private static int Quantise(float value, float scale, int min, int max)
	{
		int quantised = (int)MathF.Round(value * scale);
		return Math.Clamp(quantised, min, max);
	}
}
