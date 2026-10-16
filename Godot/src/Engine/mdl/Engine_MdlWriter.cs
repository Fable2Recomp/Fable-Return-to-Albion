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
		=> TryReplaceMesh(
			original, info, bufferIndex,
			positions, uvs, normals, tangents, triangleIndices,
			sections: null, out result, out error);

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

		int sectionCount = sections is not null && sections.Count > 0 ? sections.Count : 1;
		int oldSectionsBytes = buffer.SubMeshes.Count * SectionSize;
		int newSectionsBytes = sectionCount * SectionSize;

		var built = new System.IO.MemoryStream(original.Length + vertices.Length + indices.Length + (newSectionsBytes - oldSectionsBytes) + 256);
		built.Write(original, 0, sectionsAt);
		built.Write(new byte[newSectionsBytes]);
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
		WriteU32(bytes, sectionCountAt, (uint)sectionCount);

		if (sections is not null && sections.Count > 0)
		{
			for (int s = 0; s < sections.Count; s++)
			{
				MdlSectionSpec sec = sections[s];
				int section = sectionsAt + (s * SectionSize);
				WriteU32(bytes, section, 0xFFFFFFFFu);
				WriteU32(bytes, section + 4, (uint)sec.MaterialIndex);
				bytes[section + 8] = 0;
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
			WriteU32(bytes, section, 0xFFFFFFFFu);
			WriteU32(bytes, section + 4, 0);
			bytes[section + 8] = 0;
			WriteU32(bytes, section + 9, (uint)(triangleIndices.Length / 3));
			WriteU32(bytes, section + 13, 0);

			WriteBounds(bytes, positions, vertexCount, section + 17, section + 29);
		}

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
		return (iw << 30) | (iz << 20) | (iy << 10) | ix;
	}

	private static int Quantise(float value, float scale, int min, int max)
	{
		int quantised = (int)MathF.Round(value * scale);
		return Math.Clamp(quantised, min, max);
	}
}
