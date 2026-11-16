using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Refii.Models;

/// <summary>Replacement texture path strings for a single <see cref="MdlMaterial"/> record.</summary>
public readonly struct MdlMaterialTexturePatch
{
	public string? Diffuse { get; init; }
	public string? Specular { get; init; }
	public string? Normal { get; init; }
	public string? EnvironmentMap { get; init; }
	public string? SelfIllumination { get; init; }

	public string? GetSlot(int slotIndex) => slotIndex switch
	{
		0 => Diffuse,
		1 => Specular,
		2 => Normal,
		3 => EnvironmentMap,
		4 => SelfIllumination,
		_ => null,
	};
}

/// <summary>Describes a material record parsed from LOD 0 of an engine <c>.mdl</c> file.</summary>
public readonly struct MdlMaterialSlotEntry
{
	public int FlatIndex { get; init; }
	public int MeshIndex { get; init; }
	public string MeshName { get; init; }
	public int MaterialIndex { get; init; }
	public MdlMaterial Material { get; init; }
}

/// <summary>Specification for a submesh section in a rewritten mesh buffer.</summary>
public readonly struct MdlSectionSpec
{
	public int MaterialIndex { get; init; }
	public int StartIndex { get; init; }
	public int TriangleCount { get; init; }
	public float[]? BoundingBox { get; init; }
}

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
/// THE POSITION IS A FLOAT16_4 AND ITS w IS THE PRT CLUSTER. Both declaration builders state
/// POSITION as type 16 of the D3DDECLTYPE table — FLOAT16_4, 8 bytes (TU1 `sub_82A8B0E0` and
/// `sub_82A750E8` against the table at 0x83316CC0; disc `sub_82A97A70` and `sub_82A760D0`
/// against 0x83316B80) — so bytes +6..7 are the position's w, and the `amb2` vertex shaders
/// take it as `a0 = trunc(POSITION.w)` to index `g_PRTConstants(a0 + 0..3)`: the vertex's PRT
/// cluster as a register offset, `cluster x (pca x channels + 4) / 4` vec4s, the block stride
/// `sub_8225DC80` uploads by. That is the 0 / 4.0 / 8.0 the shipped files hold (pca 4,
/// channels 3, at most 3 clusters in 12 registers). Every other vertex shader reads
/// POSITION.xyz. A rebuilt vertex states <see cref="RebuiltClusterOffset"/>.
///
/// <see cref="TryReplaceGeometry"/> keeps the counts and rewrites the two regions in place —
/// move vertices, re-UV, re-normal, rewind triangles, on any buffer kind including skinned
/// (whose bone bytes it leaves alone). <see cref="TryReplaceMesh"/> changes the topology of a
/// rigid or skinned buffer and restates every length that follows from it.
/// </summary>
public static class Engine_MdlWriter
{
	/// <summary>The rigid stream-0 layout, from the guest's own declaration builder.</summary>
	public const int RigidStride = 20;

	public const int SkinnedStride = 28;

	public const int TreeStride = 36;

	public static int StrideFor(MdlMeshBuffer buffer) =>
		buffer.IsEngineSkinned ? SkinnedStride : buffer.IsEngineTree ? TreeStride : RigidStride;

	/// <summary>
	/// The PRT cluster offset a rebuilt vertex states in POSITION.w: cluster 0. The writer zeroes
	/// the per-vertex PCA weights with the geometry, so the `amb2` shader lights the vertex by
	/// its cluster's MEAN transfer; cluster 0 is the one every model with a transfer has, and the
	/// only value a model without one holds. The cluster is the compressor's choice per authored
	/// vertex, not a function of section or material, and a rebuild has no vertex order to carry
	/// it by — <see cref="TryReplaceGeometry"/> keeps the file's own, weights and all.
	/// </summary>
	public const float RebuiltClusterOffset = 0f;

	public static int UvOffsetFor(MdlMeshBuffer buffer) => buffer.IsEngineSkinned ? 20 : 12;

	public static int TangentOffsetFor(MdlMeshBuffer buffer) => buffer.IsEngineSkinned ? 24 : 16;

	/// <summary>
	/// These writers edit a version-36 file at its buffers' file offsets. A model whose buffers
	/// were repacked (<see cref="MdlInfo.Body"/>, Fable 3's versions 46 and 47) has no such offsets
	/// in its file; its vertices are rewritten by <see cref="TryRewritePlanarVertices"/>.
	/// </summary>
	private const string RepackedRefusal =
		"this model's buffers were repacked from Fable 3's planar layout; only its vertices can be " +
		"rewritten, at the same counts, in place in its planar blocks";

	/// <summary>The largest magnitude a FLOAT16 position holds.</summary>
	private const float HalfMax = 65504f;

	/// <summary>
	/// Fable 3 (MeshFile 46/47): moves LOD-0 parts' vertices in place, at the file offsets the
	/// reader kept (<see cref="Engine_MdlEngine47.PlanarStreams"/>), into a copy of
	/// <paramref name="original"/>.
	/// </summary>
	/// <remarks>
	/// <paramref name="info"/> must be <see cref="Engine_MdlEngine47.TryParse"/>'s parse of these
	/// same bytes. Per vertex: position x, y, z as FLOAT16 at stream 0 +0/+2/+4 (w, the PRT
	/// cluster, is kept); normal at the 8-byte block +0 (<see cref="EncodeNormal_11_11_10"/>);
	/// tangent at +4 (<see cref="EncodeTangent_DEC3N"/>). Nothing is resized: every count, index,
	/// UV, bone index, weight, bound, cloth table and coarser LOD's own stream stays the
	/// template's. A coarser part under the shared-stream flag draws LOD 0's stream and so moves
	/// with it. An edit's empty array leaves that element alone; UVs and indices are refused.
	/// </remarks>
	public static bool TryRewritePlanarVertices(
		byte[] original, MdlInfo info, IReadOnlyList<MdlGeometryEdit> edits, out byte[] result, out string error)
	{
		result = original;
		error = string.Empty;

		if (info.Body is null || !Engine_MdlEngine47.IsMeshFile47(original))
		{
			error = "not a Fable 3 MeshFile (version 46 or 47) parsed from these bytes";
			return false;
		}

		var seen = new HashSet<int>();
		var streams = new Engine_MdlEngine47.PlanarStreams[edits.Count];
		for (int e = 0; e < edits.Count; e++)
		{
			MdlGeometryEdit edit = edits[e];
			if (edit.BufferIndex < 0 || edit.BufferIndex >= info.MeshBuffers.Count)
			{
				error = $"this model has {info.MeshBuffers.Count} part(s) in LOD 0; there is no part {edit.BufferIndex}";
				return false;
			}

			if (!seen.Add(edit.BufferIndex))
			{
				error = $"part {edit.BufferIndex} is edited twice";
				return false;
			}

			if (edit.Uvs.Length != 0 || edit.Indices.Length != 0)
			{
				error = $"part {edit.BufferIndex}: UVs and indices stay the template's; only positions, normals and tangents are rewritten";
				return false;
			}

			MdlMeshBuffer buffer = info.MeshBuffers[edit.BufferIndex];
			if (!Engine_MdlEngine47.TryGetPlanarStreams(buffer, out Engine_MdlEngine47.PlanarStreams planar) ||
				planar.SharesLod0Stream || planar.PositionOffset < 0 || planar.NormalOffset < 0 ||
				planar.VertexCount != buffer.VertexCount)
			{
				error = $"part {edit.BufferIndex} has no vertex stream of its own recorded in this file";
				return false;
			}

			int vc = planar.VertexCount;
			if ((edit.Positions.Length != 0 && edit.Positions.Length != vc * 3) ||
				(edit.Normals.Length != 0 && edit.Normals.Length != vc * 3) ||
				(edit.Tangents.Length != 0 && edit.Tangents.Length != vc * 4))
			{
				error =
					$"part {edit.BufferIndex} holds {vc} vertices; the replacement states " +
					$"{edit.Positions.Length / 3} positions, {edit.Normals.Length / 3} normals and " +
					$"{edit.Tangents.Length / 4} tangents. A Fable 3 model is rewritten at its own counts.";
				return false;
			}

			foreach (float value in edit.Positions)
			{
				if (!float.IsFinite(value) || MathF.Abs(value) > HalfMax)
				{
					error = $"part {edit.BufferIndex}: a position of {value} does not fit a FLOAT16";
					return false;
				}
			}

			if (!Fits(original, planar.PositionOffset, ((long)(vc - 1) * planar.PositionStride) + 6) ||
				!Fits(original, planar.NormalOffset, (long)vc * Engine_MdlEngine47.PlanarStreams.NormalStride))
			{
				error = $"part {edit.BufferIndex}'s vertex blocks run past the end of the file";
				return false;
			}

			streams[e] = planar;
		}

		var bytes = (byte[])original.Clone();
		for (int e = 0; e < edits.Count; e++)
		{
			MdlGeometryEdit edit = edits[e];
			Engine_MdlEngine47.PlanarStreams planar = streams[e];
			for (int v = 0; v < planar.VertexCount; v++)
			{
				if (edit.Positions.Length != 0)
				{
					int p = planar.PositionOffset + (v * planar.PositionStride);
					WriteHalf(bytes, p, edit.Positions[v * 3]);
					WriteHalf(bytes, p + 2, edit.Positions[(v * 3) + 1]);
					WriteHalf(bytes, p + 4, edit.Positions[(v * 3) + 2]);
				}

				int n = planar.NormalOffset + (v * Engine_MdlEngine47.PlanarStreams.NormalStride);
				if (edit.Normals.Length != 0)
				{
					WriteU32(bytes, n, EncodeNormal_11_11_10(
						edit.Normals[v * 3], edit.Normals[(v * 3) + 1], edit.Normals[(v * 3) + 2]));
				}

				if (edit.Tangents.Length != 0)
				{
					WriteU32(bytes, n + Engine_MdlEngine47.PlanarStreams.TangentInBlock, EncodeTangent_DEC3N(
						edit.Tangents[v * 4], edit.Tangents[(v * 4) + 1],
						edit.Tangents[(v * 4) + 2], edit.Tangents[(v * 4) + 3]));
				}
			}
		}

		result = bytes;
		return true;
	}

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

		if (info.Body is not null)
		{
			error = RepackedRefusal;
			return false;
		}

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
	/// the counts moves bytes but breaks no pointers; only the stated LENGTHS have to follow:
	/// the record's vertex and index counts, its section count, the LOD size, and the two
	/// per-vertex tails the LOD states — the extra UV sets (`ShDescriptor[4]` float4s per
	/// vertex, which the reader walks by) and the lightmap channel (4 bytes per rigid and tree
	/// vertex of the whole LOD, present when the section flag is set).
	///
	/// The counts sit at fixed positions relative to the vertex data, derived backwards through
	/// the record's sequential fields and checked against the file before anything is written.
	/// A rigid record carries 40 bytes of bounds between its counts and its section count; a
	/// skinned one carries none, and its 28-byte vertices name four of the model's own bones.
	///
	/// The new geometry is written as TRIANGLE LIST sections, which the shipped data proves the
	/// engine reads: of 339 buffers whose sections all state list, 339 have
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
		=> TryReplaceMesh(
			original, info, bufferIndex,
			positions, uvs, normals, tangents, triangleIndices,
			sections: null, boneIndices: null, boneWeights: null, out result, out error);

	public static bool TryReplaceMesh(
		byte[] original,
		MdlInfo info,
		int bufferIndex,
		float[] positions,
		float[] uvs,
		float[] normals,
		float[] tangents,
		ushort[] triangleIndices,
		IReadOnlyList<MdlSectionSpec>? sections,
		out byte[] result,
		out string error)
		=> TryReplaceMesh(
			original, info, bufferIndex,
			positions, uvs, normals, tangents, triangleIndices,
			sections, boneIndices: null, boneWeights: null, out result, out error);

	/// <param name="boneIndices">
	/// Four per vertex for a skinned buffer: indices into the model's own bone table
	/// (<see cref="MdlInfo.Bones"/>), slot k paired with <paramref name="boneWeights"/> slot k.
	/// Ignored for a rigid buffer.
	/// </param>
	/// <param name="boneWeights">Four per vertex, 0..255, summing to 255 for a vertex that is skinned at all.</param>
	public static bool TryReplaceMesh(
		byte[] original,
		MdlInfo info,
		int bufferIndex,
		float[] positions,
		float[] uvs,
		float[] normals,
		float[] tangents,
		ushort[] triangleIndices,
		IReadOnlyList<MdlSectionSpec>? sections,
		byte[]? boneIndices,
		byte[]? boneWeights,
		out byte[] result,
		out string error)
	{
		result = original;
		error = string.Empty;

		if (info.Body is not null)
		{
			error = RepackedRefusal;
			return false;
		}

		if (bufferIndex < 0 || bufferIndex >= info.MeshBuffers.Count)
		{
			error = $"there is no mesh buffer {bufferIndex}";
			return false;
		}

		MdlMeshBuffer buffer = info.MeshBuffers[bufferIndex];
		if (buffer.IsEngineTree || buffer.IsAltPath || buffer.IsFoliagePath)
		{
			error =
				"only a rigid or a skinned engine buffer can be RESIZED — a tree or foliage part " +
				"states its counts in a different record layout. Its mesh can still be edited at " +
				"the same vertex and triangle count.";
			return false;
		}

		bool skinned = buffer.IsEngineSkinned;
		if (skinned && (boneIndices is null || boneWeights is null))
		{
			error =
				"this buffer is SKINNED: every vertex names four of the model's own bones, so a " +
				"replacement needs bone indices and weights matched to this model's skeleton. " +
				"Ship a rigged glTF whose joints are named as this model's bones are.";
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

		foreach (MdlClothBlock cloth in info.ClothBlocks)
		{
			if (cloth.RecordIndex == (uint)bufferIndex)
			{
				error =
					"this buffer carries a cloth simulation whose tables are sized to its own " +
					"vertices; a replacement would simulate the old mesh. Its vertices can still " +
					"be edited at the same count.";
				return false;
			}
		}

		foreach (MdlFurBlock fur in info.FurBlocks)
		{
			if (fur.RecordIndex == (uint)bufferIndex)
			{
				error = "this buffer carries a fur block; its vertices can still be edited at the same count.";
				return false;
			}
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

		if (skinned && (boneIndices!.Length != vertexCount * 4 || boneWeights!.Length != vertexCount * 4))
		{
			error = "the bone indices and weights must be four per vertex";
			return false;
		}

		if (skinned)
		{
			foreach (byte bone in boneIndices!)
			{
				if (bone >= info.Bones.Count)
				{
					error = $"a vertex names bone {bone}; this model has {info.Bones.Count}";
					return false;
				}
			}
		}

		if (sections is not null)
		{
			foreach (MdlSectionSpec sec in sections)
			{
				if (sec.StartIndex < 0 || sec.TriangleCount < 0 ||
					sec.StartIndex + ((long)sec.TriangleCount * 3) > triangleIndices.Length)
				{
					error = "a section reaches past the replacement's triangles";
					return false;
				}
			}
		}

		// THE RECORD'S FIELDS ARE SEQUENTIAL, so their positions come backwards from the vertex
		// data. Checked against the file before anything is written — a derivation that has
		// drifted must not silently overwrite a string.
		int sectionsAt = buffer.VertexOffset - (buffer.SubMeshes.Count * SectionSize);
		int sectionCountAt = sectionsAt - 4;
		int vertexCountAt = sectionCountAt - 4 - (skinned ? 0 : RigidRecordBoundsBytes);
		int indexCountAt = vertexCountAt - 4;

		if (indexCountAt < BodyStart ||
			ReadU32(original, vertexCountAt) != buffer.VertexCount ||
			ReadU32(original, indexCountAt) != buffer.FaceCount ||
			ReadU32(original, sectionCountAt) != (uint)buffer.SubMeshes.Count)
		{
			error =
				"this model's record does not sit where a " + (skinned ? "skinned" : "static") +
				" mesh's record sits, so the counts could not be located. It has not been touched.";
			return false;
		}

		// THE PER-VERTEX TAILS ARE STATED, NOT DERIVED. `ShDescriptor[4]` is the LOD's count of
		// extra float4 UV sets per vertex, read out of the LOD header and the number the reader
		// itself walks each record's second stream by. A set is zeroed in the rebuilt stream:
		// nothing on our side reads it and the guest never draws; what matters is that its
		// LENGTH follows the new vertex count.
		uint sh4 = info.ShDescriptor.Length > 4 ? info.ShDescriptor[4] : 0;
		int oldStride = StrideFor(buffer);
		long blockEnd = BodyStart + info.LodSizes[0];
		long dataEnd = buffer.VertexOffset + ((long)buffer.VertexCount * oldStride) +
			((long)buffer.FaceCount * 2) + ((long)buffer.VertexCount * SecondStreamSetBytes * sh4);
		if (buffer.VertexOffset < BodyStart || dataEnd > blockEnd || blockEnd > original.Length)
		{
			error = "this model's first mesh buffer is not inside its first LOD";
			return false;
		}

		// THE LIGHTMAP CHANNEL closes the LOD when the section flag is set: 4 bytes per rigid
		// and tree vertex, in record order, skinned records excluded. A rigid buffer's slice is
		// rebuilt (zeroed — a replacement mesh has no authored lightmap UVs) at the new count;
		// the other records' slices are carried.
		long lightmapBytes = 0;
		long lightmapSliceAt = 0;
		bool inLightmap = false;
		if (info.SectionFlag != 0)
		{
			for (int i = 0; i < info.MeshBuffers.Count; i++)
			{
				MdlMeshBuffer other = info.MeshBuffers[i];
				if (other.IsEngineSkinned)
				{
					continue;
				}

				if (i == bufferIndex)
				{
					lightmapSliceAt = lightmapBytes;
					inLightmap = true;
				}

				lightmapBytes += (long)other.VertexCount * 4;
			}
		}

		long lightmapAt = blockEnd - lightmapBytes;
		if (lightmapAt < dataEnd)
		{
			error = "this model's lightmap channel overlaps its geometry; the LOD was not understood";
			return false;
		}

		byte[] vertices = EncodeVertices(
			positions, uvs, normals, tangents, vertexCount, skinned, boneIndices, boneWeights);
		var indices = new byte[triangleIndices.Length * 2];
		for (int i = 0; i < triangleIndices.Length; i++)
		{
			indices[i * 2] = (byte)(triangleIndices[i] >> 8);
			indices[(i * 2) + 1] = (byte)triangleIndices[i];
		}

		int sectionCount = sections is not null && sections.Count > 0 ? sections.Count : 1;
		int newSectionsBytes = sectionCount * SectionSize;

		var built = new MemoryStream(original.Length + vertices.Length + indices.Length + newSectionsBytes + 256);
		built.Write(original, 0, sectionsAt);
		built.Write(new byte[newSectionsBytes]);
		built.Write(vertices, 0, vertices.Length);
		built.Write(indices, 0, indices.Length);
		built.Write(new byte[(long)vertexCount * SecondStreamSetBytes * sh4]);

		// The record's trailing words and every later record, up to the lightmap channel.
		built.Write(original, (int)dataEnd, (int)(lightmapAt - dataEnd));

		if (lightmapBytes != 0)
		{
			if (inLightmap)
			{
				long oldSlice = (long)buffer.VertexCount * 4;
				built.Write(original, (int)lightmapAt, (int)lightmapSliceAt);
				built.Write(new byte[vertexCount * 4]);
				built.Write(original, (int)(lightmapAt + lightmapSliceAt + oldSlice),
					(int)(lightmapBytes - lightmapSliceAt - oldSlice));
			}
			else
			{
				built.Write(original, (int)lightmapAt, (int)lightmapBytes);
			}
		}

		// Only to the end of LOD 0 — the later LODs' bytes are dropped, and their sizes zeroed
		// below so they alias this one.
		byte[] bytes = built.ToArray();

		WriteU32(bytes, vertexCountAt, (uint)vertexCount);
		WriteU32(bytes, indexCountAt, (uint)indices.Length / 2);
		WriteU32(bytes, sectionCountAt, (uint)sectionCount);

		if (sections is not null && sections.Count > 0)
		{
			for (int s = 0; s < sections.Count; s++)
			{
				MdlSectionSpec sec = sections[s];
				int section = sectionsAt + (s * SectionSize);
				WriteU32(bytes, section, NoHideRegion);
				WriteU32(bytes, section + 4, (uint)sec.MaterialIndex);
				bytes[section + 8] = TriangleListSection;
				WriteU32(bytes, section + 9, (uint)sec.TriangleCount);
				WriteU32(bytes, section + 13, (uint)sec.StartIndex);

				if (sec.BoundingBox is { Length: 6 } sbb)
				{
					for (int k = 0; k < 6; k++)
					{
						WriteU32(bytes, section + 17 + (k * 4), unchecked((uint)BitConverter.SingleToInt32Bits(sbb[k])));
					}
				}
				else
				{
					ComputeSectionBounds(positions, triangleIndices, sec.StartIndex, sec.TriangleCount, out float[] sMin, out float[] sMax);
					for (int k = 0; k < 3; k++)
					{
						WriteU32(bytes, section + 17 + (k * 4), unchecked((uint)BitConverter.SingleToInt32Bits(sMin[k])));
						WriteU32(bytes, section + 29 + (k * 4), unchecked((uint)BitConverter.SingleToInt32Bits(sMax[k])));
					}
				}
			}
		}
		else
		{
			// One section, stating a triangle list over the whole buffer.
			int section = sectionsAt;
			WriteU32(bytes, section, NoHideRegion);
			WriteU32(bytes, section + 4, 0);
			bytes[section + 8] = TriangleListSection;
			WriteU32(bytes, section + 9, (uint)(triangleIndices.Length / 3));
			WriteU32(bytes, section + 13, 0);

			WriteBounds(bytes, positions, vertexCount, section + 17, section + 29);
		}

		// THE RIGID RECORD'S BOUNDS SIT AFTER THE COUNTS, NOT BEFORE THEM. The order on disk is
		// indexCount, vertexCount, aabbMin, aabbMax, sphere, sectionCount — which is exactly how
		// `vertexCountAt` was derived backwards from the sections. Writing them at negative
		// offsets put the sphere on top of the vertex count and the box over the index count, and
		// the rewritten file stopped parsing as an engine model at all.
		if (!skinned)
		{
			WriteBounds(bytes, positions, vertexCount, vertexCountAt + 4, vertexCountAt + 16);
			WriteSphere(bytes, positions, vertexCount, vertexCountAt + 28);
		}

		// THE HEADER'S BOUNDS COVER THE WHOLE MODEL, so they are the union of the replacement
		// and every other buffer this LOD still carries — the sphere centre is what the guest
		// culls and picks the LOD tier by.
		WriteHeaderBounds(bytes, UnionPositions(original, info, bufferIndex, positions));

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

	/// <summary>
	/// The replacement's positions followed by every OTHER LOD-0 buffer's, decoded from the
	/// template: what the header's bounds have to cover.
	/// </summary>
	private static float[] UnionPositions(byte[] original, MdlInfo info, int bufferIndex, float[] positions)
	{
		var all = new List<float>(positions);
		for (int i = 0; i < info.MeshBuffers.Count; i++)
		{
			if (i == bufferIndex)
			{
				continue;
			}

			MdlMeshBuffer other = info.MeshBuffers[i];
			int stride = StrideFor(other);
			if (other.IsAltPath || other.IsFoliagePath ||
				!Fits(original, other.VertexOffset, (long)other.VertexCount * stride))
			{
				continue;
			}

			for (int v = 0; v < other.VertexCount; v++)
			{
				int p = other.VertexOffset + (v * stride);
				all.Add(MdlReader.HalfToFloat((ushort)((original[p] << 8) | original[p + 1])));
				all.Add(MdlReader.HalfToFloat((ushort)((original[p + 2] << 8) | original[p + 3])));
				all.Add(MdlReader.HalfToFloat((ushort)((original[p + 4] << 8) | original[p + 5])));
			}
		}

		return all.ToArray();
	}

	/// <summary>
	/// Extracts all LOD 0 <see cref="MdlMaterial"/> records with their flat material index and mesh header name.
	/// </summary>
	public static IReadOnlyList<MdlMaterialSlotEntry> ExtractMaterials(byte[] mdlBytes)
	{
		if (mdlBytes is null || mdlBytes.Length < BodyStart)
		{
			return Array.Empty<MdlMaterialSlotEntry>();
		}

		var info = new MdlInfo();
		if (!Engine_MdlEngine.TryParse(mdlBytes, info))
		{
			return Array.Empty<MdlMaterialSlotEntry>();
		}

		var list = new List<MdlMaterialSlotEntry>();
		int flatIdx = 0;
		for (int m = 0; m < info.Meshes.Count; m++)
		{
			MdlMesh mesh = info.Meshes[m];
			for (int j = 0; j < mesh.Materials.Count; j++)
			{
				list.Add(new MdlMaterialSlotEntry
				{
					FlatIndex = flatIdx++,
					MeshIndex = m,
					MeshName = mesh.Name,
					MaterialIndex = j,
					Material = mesh.Materials[j],
				});
			}
		}

		return list;
	}

	/// <summary>
	/// Rewrites texture path strings inside an engine <c>.mdl</c> file's material table across all LODs.
	/// Because the <c>.mdl</c> format is read sequentially without internal byte-offset pointers,
	/// splicing NUL-terminated material strings only requires updating <c>LodSizes[lod]</c> at <c>0x3C + lod * 4</c>.
	/// </summary>
	public static bool TryRewriteMaterialTextures(
		byte[] original,
		IReadOnlyDictionary<int, MdlMaterialTexturePatch> patchesByFlatMaterialIndex,
		out byte[] result,
		out string error)
	{
		result = original;
		error = string.Empty;

		if (original is null || original.Length < BodyStart)
		{
			error = "the model file is shorter than the 104-byte header";
			return false;
		}

		if (patchesByFlatMaterialIndex is null || patchesByFlatMaterialIndex.Count == 0)
		{
			return true;
		}

		var info = new MdlInfo();
		if (!Engine_MdlEngine.TryReadFileHeader(original, info, out long totalBodyBytes))
		{
			error = "the model file header could not be parsed";
			return false;
		}

		if (BodyStart + totalBodyBytes > original.Length)
		{
			error = "the model body is truncated relative to its header LOD sizes";
			return false;
		}

		var r = new MdlReader(original);
		var output = new MemoryStream(original.Length + 256);
		output.Write(original, 0, BodyStart);

		var newLodSizes = new uint[3];
		var lod0ReplacedStrings = new Dictionary<(int Slot, string OldValue), string>();

		int off = BodyStart;
		for (uint lod = 0; lod < info.LodCount; lod++)
		{
			uint oldLodSize = info.LodSizes[lod];
			long secEnd = off + oldLodSize;
			if (secEnd > original.Length)
			{
				error = $"LOD {lod} extends past the end of the file";
				return false;
			}

			if (oldLodSize == 0)
			{
				newLodSizes[lod] = 0;
				continue;
			}

			r.Position = off;
			if (!r.Skip(32)) { error = "truncated LOD prologue"; return false; }
			if (!r.U32(out uint bc) || bc > 100000u) { error = "invalid bone count"; return false; }
			for (uint i = 0; i < bc; i++)
			{
				if (!r.Strz(out _) || !r.U32(out _)) { error = "truncated bone table"; return false; }
			}

			if (!r.U32(out uint btc) || btc > 100000u) { error = "invalid bone transform count"; return false; }
			if (!r.Skip((int)(btc * 44))) { error = "truncated bone transforms"; return false; }
			if (!r.Skip(40)) { error = "truncated LOD counts header"; return false; }

			if (!r.U32(out uint mc) || !r.U32(out uint rigid) || !r.U32(out uint skinned) ||
			    !r.U32(out uint ex52) || !r.U32(out _))
			{
				error = "truncated LOD part counts";
				return false;
			}

			if (mc > 100000u || rigid > 100000u || skinned > 100000u || ex52 != 0)
			{
				error = "unsupported instanced (TypeC) or corrupt material class";
				return false;
			}

			if (!r.U8(out _)) { error = "truncated section flag"; return false; }
			if (!r.U32(out uint tagc) || tagc > 100000u) { error = "invalid tag count"; return false; }
			for (uint t = 0; t < tagc; t++)
			{
				if (!r.Strz(out _) || !r.Skip(1)) { error = "truncated tag entry"; return false; }
			}

			for (int i = 0; i < 5; i++)
			{
				if (!r.U32(out _)) { error = "truncated SH descriptor"; return false; }
			}

			if (!r.U32(out uint fc) || fc > 1000000u || !r.Skip((int)(fc * 4)))
			{
				error = "truncated SH probe data";
				return false;
			}

			if (!r.U32(out uint hrc) || hrc > 1000000u)
			{
				error = "invalid hide region count";
				return false;
			}
			for (uint i = 0; i < hrc; i++)
			{
				if (!r.Strz(out _)) { error = "truncated hide region"; return false; }
			}

			using var lodStream = new MemoryStream((int)oldLodSize + 128);
			int cursor = off;
			int flatMatIdx = 0;

			for (uint m = 0; m < mc; m++)
			{
				if (!r.U32(out uint sel)) { error = "truncated shader selector"; return false; }
				if (sel == 2)
				{
					if (!r.Skip(68)) { error = "truncated shader type 2 header"; return false; }
					continue;
				}

				if (!r.Strz(out _)) { error = "truncated mesh header name"; return false; }
				if (!r.Skip(45)) { error = "truncated material scalar block"; return false; }
				if (!r.U32(out uint matc) || matc > 100000u) { error = "invalid material count"; return false; }

				for (uint j = 0; j < matc; j++)
				{
					patchesByFlatMaterialIndex.TryGetValue(flatMatIdx, out MdlMaterialTexturePatch patch);
					bool hasDirectPatch = patchesByFlatMaterialIndex.ContainsKey(flatMatIdx);

					for (int slot = 0; slot < 5; slot++)
					{
						int strStart = r.Position;
						if (!r.Strz(out string oldVal))
						{
							error = "truncated material texture string";
							return false;
						}
						int strEnd = r.Position;

						string? replacement = hasDirectPatch ? patch.GetSlot(slot) : null;
						if (replacement is null && !string.IsNullOrEmpty(oldVal) &&
						    lod0ReplacedStrings.TryGetValue((slot, oldVal), out string? inheritedReplacement))
						{
							replacement = inheritedReplacement;
						}

						if (replacement is not null)
						{
							if (strStart > cursor)
							{
								lodStream.Write(original, cursor, strStart - cursor);
							}

							byte[] ascii = Encoding.ASCII.GetBytes(replacement);
							lodStream.Write(ascii, 0, ascii.Length);
							lodStream.WriteByte(0);
							cursor = strEnd;

							if (lod == 0 && !string.IsNullOrEmpty(oldVal))
							{
								lod0ReplacedStrings[(slot, oldVal)] = replacement;
							}
						}
					}

					for (int k = 0; k < 4; k++)
					{
						if (!r.Strz(out _)) { error = "truncated shader param string"; return false; }
					}
					if (!r.Skip(8)) { error = "truncated material floats"; return false; }

					flatMatIdx++;
				}
			}

			if (cursor < (int)secEnd)
			{
				lodStream.Write(original, cursor, (int)secEnd - cursor);
			}

			byte[] lodBytes = lodStream.ToArray();
			newLodSizes[lod] = (uint)lodBytes.Length;
			output.Write(lodBytes, 0, lodBytes.Length);
			off = (int)secEnd;
		}

		if (off < original.Length)
		{
			output.Write(original, off, original.Length - off);
		}

		byte[] rewritten = output.ToArray();
		for (int lod = 0; lod < info.LodCount; lod++)
		{
			WriteU32(rewritten, 0x3C + (lod * 4), newLodSizes[lod]);
		}

		var verifyInfo = new MdlInfo();
		if (!Engine_MdlEngine.TryParse(rewritten, verifyInfo))
		{
			error = "rewritten .mdl failed post-write verification parse";
			return false;
		}

		result = rewritten;
		return true;
	}

	/// <summary>
	/// Ensures that the model's mesh 0 states at least <paramref name="targetMaterialCount"/> materials.
	/// If the model currently states fewer materials, additional empty material entries are appended
	/// to mesh 0's material table in LOD 0, collapsing coarser LODs onto LOD 0 and updating the header LOD sizes.
	/// </summary>
	public static bool TryEnsureMaterialSlots(
		byte[] original,
		int targetMaterialCount,
		out byte[] result,
		out string error)
	{
		result = original;
		error = string.Empty;

		if (original is null || original.Length < BodyStart)
		{
			error = "the model file is shorter than the 104-byte header";
			return false;
		}

		if (targetMaterialCount <= 1)
		{
			return true;
		}

		var info = new MdlInfo();
		if (!Engine_MdlEngine.TryReadFileHeader(original, info, out long totalBodyBytes))
		{
			error = "the model file header could not be parsed";
			return false;
		}

		if (BodyStart + totalBodyBytes > original.Length)
		{
			error = "the model body is truncated relative to its header LOD sizes";
			return false;
		}

		var currentMaterials = ExtractMaterials(original);
		if (currentMaterials.Count >= targetMaterialCount)
		{
			return true;
		}

		var r = new MdlReader(original);
		var output = new MemoryStream(original.Length + 256);
		output.Write(original, 0, BodyStart);

		var newLodSizes = new uint[3];
		int off = BodyStart;

		for (uint lod = 0; lod < info.LodCount; lod++)
		{
			uint oldLodSize = info.LodSizes[lod];
			long secEnd = off + oldLodSize;
			if (secEnd > original.Length)
			{
				error = $"LOD {lod} extends past the end of the file";
				return false;
			}

			if (oldLodSize == 0 || lod > 0)
			{
				newLodSizes[lod] = 0;
				off = (int)secEnd;
				continue;
			}

			r.Position = off;
			if (!r.Skip(32)) { error = "truncated LOD prologue"; return false; }
			if (!r.U32(out uint bc) || bc > 100000u) { error = "invalid bone count"; return false; }
			for (uint i = 0; i < bc; i++)
			{
				if (!r.Strz(out _) || !r.U32(out _)) { error = "truncated bone table"; return false; }
			}

			if (!r.U32(out uint btc) || btc > 100000u) { error = "invalid bone transform count"; return false; }
			if (!r.Skip((int)(btc * 44))) { error = "truncated bone transforms"; return false; }
			if (!r.Skip(40)) { error = "truncated LOD counts header"; return false; }

			if (!r.U32(out uint mc) || !r.U32(out uint rigid) || !r.U32(out uint skinned) ||
			    !r.U32(out uint ex52) || !r.U32(out _))
			{
				error = "truncated LOD part counts";
				return false;
			}

			if (mc > 100000u || rigid > 100000u || skinned > 100000u || ex52 != 0)
			{
				error = "unsupported instanced (TypeC) or corrupt material class";
				return false;
			}

			if (!r.U8(out _)) { error = "truncated section flag"; return false; }
			if (!r.U32(out uint tagc) || tagc > 100000u) { error = "invalid tag count"; return false; }
			for (uint t = 0; t < tagc; t++)
			{
				if (!r.Strz(out _) || !r.Skip(1)) { error = "truncated tag entry"; return false; }
			}

			for (int i = 0; i < 5; i++)
			{
				if (!r.U32(out _)) { error = "truncated SH descriptor"; return false; }
			}

			if (!r.U32(out uint fc) || fc > 1000000u || !r.Skip((int)(fc * 4)))
			{
				error = "truncated SH probe data";
				return false;
			}

			if (!r.U32(out uint hrc) || hrc > 1000000u)
			{
				error = "invalid hide region count";
				return false;
			}
			for (uint i = 0; i < hrc; i++)
			{
				if (!r.Strz(out _)) { error = "truncated hide region"; return false; }
			}

			using var lodStream = new MemoryStream((int)oldLodSize + 256);
			int cursor = off;

			for (uint m = 0; m < mc; m++)
			{
				if (!r.U32(out uint sel)) { error = "truncated shader selector"; return false; }
				if (sel == 2)
				{
					if (!r.Skip(68)) { error = "truncated shader type 2 header"; return false; }
					continue;
				}

				if (!r.Strz(out _)) { error = "truncated mesh header name"; return false; }
				if (!r.Skip(45)) { error = "truncated material scalar block"; return false; }

				int matcPos = r.Position;
				if (!r.U32(out uint matc) || matc > 100000u) { error = "invalid material count"; return false; }

				if (m == 0 && matc < targetMaterialCount)
				{
					lodStream.Write(original, cursor, matcPos - cursor);
					byte[] matcBytes = new byte[4];
					WriteU32(matcBytes, 0, (uint)targetMaterialCount);
					lodStream.Write(matcBytes, 0, 4);
					cursor = matcPos + 4;

					for (uint j = 0; j < matc; j++)
					{
						for (int slot = 0; slot < 5; slot++)
						{
							if (!r.Strz(out _)) { error = "truncated material texture string"; return false; }
						}
						for (int k = 0; k < 4; k++)
						{
							if (!r.Strz(out _)) { error = "truncated shader param string"; return false; }
						}
						if (!r.Skip(8)) { error = "truncated material floats"; return false; }
					}

					int existingMatsEnd = r.Position;
					lodStream.Write(original, cursor, existingMatsEnd - cursor);
					cursor = existingMatsEnd;

					for (int j = (int)matc; j < targetMaterialCount; j++)
					{
						for (int s = 0; s < 5; s++) lodStream.WriteByte(0);
						for (int sp = 0; sp < 4; sp++) lodStream.WriteByte(0);
						lodStream.Write(new byte[8], 0, 8);
					}
				}
				else
				{
					for (uint j = 0; j < matc; j++)
					{
						for (int slot = 0; slot < 5; slot++)
						{
							if (!r.Strz(out _)) { error = "truncated material texture string"; return false; }
						}
						for (int k = 0; k < 4; k++)
						{
							if (!r.Strz(out _)) { error = "truncated shader param string"; return false; }
						}
						if (!r.Skip(8)) { error = "truncated material floats"; return false; }
					}
				}
			}

			if (cursor < (int)secEnd)
			{
				lodStream.Write(original, cursor, (int)secEnd - cursor);
			}

			byte[] lodBytes = lodStream.ToArray();
			newLodSizes[lod] = (uint)lodBytes.Length;
			output.Write(lodBytes, 0, lodBytes.Length);
			off = (int)secEnd;
		}

		if (off < original.Length)
		{
			output.Write(original, off, original.Length - off);
		}

		byte[] rewritten = output.ToArray();
		for (int lod = 0; lod < info.LodCount; lod++)
		{
			WriteU32(rewritten, 0x3C + (lod * 4), newLodSizes[lod]);
		}

		var verifyInfo = new MdlInfo();
		if (!Engine_MdlEngine.TryParse(rewritten, verifyInfo))
		{
			error = "expanded material .mdl failed post-write verification parse";
			return false;
		}

		result = rewritten;
		return true;
	}

	/// <summary>A section record: two words, the strip flag, primitive count, start, two vec3.</summary>
	public const int SectionSize = 41;

	/// <summary>Where the geometry payload begins, stated by the header as 16 + headerSize.</summary>
	public const int BodyStart = 104;

	/// <summary>A rigid record's AABB (two vec3) and sphere (vec4) between its counts and its section count.</summary>
	public const int RigidRecordBoundsBytes = 40;

	/// <summary>One extra UV set is a float4 per vertex.</summary>
	public const int SecondStreamSetBytes = 16;

	/// <summary>A section's hide-region word when it belongs to none (`uint.MaxValue`, as the reader reads it).</summary>
	public const uint NoHideRegion = 0xFFFFFFFFu;

	/// <summary>The section flag byte stating a triangle list (non-zero is a strip).</summary>
	public const byte TriangleListSection = 0;

	private static byte[] EncodeVertices(
		float[] positions, float[] uvs, float[] normals, float[] tangents, int vertexCount,
		bool skinned, byte[]? boneIndices, byte[]? boneWeights)
	{
		int stride = skinned ? SkinnedStride : RigidStride;
		int uvAt = skinned ? 20 : 12;
		int tangentAt = skinned ? 24 : 16;
		var vertices = new byte[vertexCount * stride];
		for (int v = 0; v < vertexCount; v++)
		{
			int p = v * stride;
			WriteHalf(vertices, p, positions[v * 3]);
			WriteHalf(vertices, p + 2, positions[(v * 3) + 1]);
			WriteHalf(vertices, p + 4, positions[(v * 3) + 2]);
			WriteHalf(vertices, p + 6, RebuiltClusterOffset);

			WriteU32(vertices, p + 8, normals.Length >= (v + 1) * 3
				? EncodeNormal_11_11_10(normals[v * 3], normals[(v * 3) + 1], normals[(v * 3) + 2])
				: EncodeNormal_11_11_10(0f, 0f, 1f));

			if (skinned)
			{
				for (int k = 0; k < 4; k++)
				{
					vertices[p + 12 + k] = boneIndices![(v * 4) + k];
					vertices[p + 16 + k] = boneWeights![(v * 4) + k];
				}
			}

			WriteHalf(vertices, p + uvAt, uvs.Length >= (v + 1) * 2 ? uvs[v * 2] : 0f);
			WriteHalf(vertices, p + uvAt + 2, uvs.Length >= (v + 1) * 2 ? uvs[(v * 2) + 1] : 0f);

			WriteU32(vertices, p + tangentAt, tangents.Length >= (v + 1) * 4
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
	private static void WriteHeaderBounds(byte[] bytes, float[] positions)
	{
		int vertexCount = positions.Length / 3;
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

	private static void ComputeSectionBounds(
		float[] positions, ushort[] triangleIndices, int startIndex, int triangleCount,
		out float[] min, out float[] max)
	{
		min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
		max = new[] { float.MinValue, float.MinValue, float.MinValue };
		int endIndex = Math.Min(triangleIndices.Length, startIndex + (triangleCount * 3));
		for (int i = startIndex; i < endIndex; i++)
		{
			int v = triangleIndices[i];
			if (v * 3 + 2 < positions.Length)
			{
				for (int k = 0; k < 3; k++)
				{
					float val = positions[v * 3 + k];
					min[k] = MathF.Min(min[k], val);
					max[k] = MathF.Max(max[k], val);
				}
			}
		}
		if (min[0] > max[0])
		{
			min = new[] { -0.1f, -0.1f, -0.1f };
			max = new[] { 0.1f, 0.1f, 0.1f };
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
		return (iw << 30) | (iz << 20) | (iy << 10) | ix;
	}

	private static int Quantise(float value, float scale, int min, int max)
	{
		int quantised = (int)MathF.Round(value * scale);
		return Math.Clamp(quantised, min, max);
	}
}
