namespace Refii.Tests.Modding;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Bnk;
using Refii.Core;
using Refii.Models;
using Refii.Mods;

/// <summary>
/// Fable 3's same-count vertex rewrite: the version-47 reader keeps each part's planar block
/// offsets, the writer overwrites positions, normals and tangents there and nothing else, and a
/// glTF over a version-47 template converts through it. The fixtures are synthetic MeshFiles
/// built here; the shipped checks read the player's Fable 3 banks in place and are skipped
/// without them. No Godot type is built.
/// </summary>
[TestSuite]
public class MdlPlanarRewriteTests
{
	private const int RigidVertices = 3;
	private const int SkinnedVertices = 4;

	/// <summary>Where the fixture wrote each part's blocks.</summary>
	private sealed class Layout
	{
		public int RigidS0;
		public int RigidS3;
		public int SkinnedS0;
		public int SkinnedS3;
	}

	// ------------------------------------------------------------------ the synthetic MeshFile 47

	private static void U8(Stream s, byte v) => s.WriteByte(v);

	private static void U32(Stream s, uint v)
	{
		Span<byte> b = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(b, v);
		s.Write(b);
	}

	private static void U16(Stream s, ushort v)
	{
		Span<byte> b = stackalloc byte[2];
		BinaryPrimitives.WriteUInt16BigEndian(b, v);
		s.Write(b);
	}

	private static void F32(Stream s, float v) => U32(s, unchecked((uint)BitConverter.SingleToInt32Bits(v)));

	private static void Half(Stream s, float v) => U16(s, Engine_MdlWriter.FloatToHalf(v));

	private static void Strz(Stream s, string text)
	{
		byte[] b = Encoding.ASCII.GetBytes(text);
		s.Write(b);
		s.WriteByte(0);
	}

	private static void Batch(Stream s, uint prims)
	{
		U32(s, 0xFFFFFFFFu);
		U8(s, 0);
		U32(s, prims);
		U32(s, 0);
		for (int k = 0; k < 6; k++)
		{
			F32(s, k < 3 ? -1f : 1f);
		}
	}

	/// <summary>
	/// One LOD: no bones, one material (class 3), a rigid part of 3 vertices and a skinned part of
	/// 4, no lightmap channel. The PRT w lane holds 4.0 and the tangent handedness bits 3, so a
	/// rewrite that touches either shows.
	/// </summary>
	private static byte[] BuildModel(out Layout layout)
	{
		layout = new Layout();
		var lod = new MemoryStream();
		lod.Write(new byte[12]);
		U8(lod, 0);
		U32(lod, 0);
		U32(lod, 0);
		lod.Write(new byte[40]);
		U32(lod, 1);
		U32(lod, 1);
		U32(lod, 1);
		U32(lod, 0);
		U32(lod, 0);
		U8(lod, 0);
		U32(lod, 0);
		U32(lod, 0);

		U32(lod, 3);
		Strz(lod, "fixture_material");
		U32(lod, 0);
		U32(lod, 0);

		Strz(lod, "rigid");
		U8(lod, 0);
		U32(lod, 0);
		U32(lod, 0);
		U32(lod, 0);
		U32(lod, 3);
		U32(lod, RigidVertices);
		lod.Write(new byte[40]);
		U32(lod, 1);
		Batch(lod, 1);
		layout.RigidS0 = (int)lod.Position;
		for (int v = 0; v < RigidVertices; v++)
		{
			Half(lod, v);
			Half(lod, v + 0.5f);
			Half(lod, -v);
			Half(lod, 4f);
			Half(lod, v * 0.25f);
			Half(lod, 1f - (v * 0.25f));
		}

		layout.RigidS3 = (int)lod.Position;
		for (int v = 0; v < RigidVertices; v++)
		{
			U32(lod, Engine_MdlWriter.EncodeNormal_11_11_10(0f, 0f, 1f));
			U32(lod, Engine_MdlWriter.EncodeTangent_DEC3N(1f, 0f, 0f, -1f));
		}

		U16(lod, 0);
		U16(lod, 1);
		U16(lod, 2);
		U32(lod, 0);

		U8(lod, 0);
		U32(lod, 0);
		U32(lod, 0);
		U32(lod, 0);
		U32(lod, 6);
		U32(lod, SkinnedVertices);
		U32(lod, 1);
		Batch(lod, 2);
		layout.SkinnedS0 = (int)lod.Position;
		for (int v = 0; v < SkinnedVertices; v++)
		{
			Half(lod, v * 2f);
			Half(lod, 1f);
			Half(lod, v * -0.5f);
			Half(lod, 4f);
			lod.Write(new byte[] { 0, 0, 0, 0 });
			lod.Write(new byte[] { 0, 0, 0, 255 });
			Half(lod, 0.5f);
			Half(lod, v * 0.125f);
		}

		layout.SkinnedS3 = (int)lod.Position;
		for (int v = 0; v < SkinnedVertices; v++)
		{
			U32(lod, Engine_MdlWriter.EncodeNormal_11_11_10(0f, 1f, 0f));
			U32(lod, Engine_MdlWriter.EncodeTangent_DEC3N(0f, 0f, 1f, -1f));
		}

		U8(lod, 0);
		foreach (ushort i in new ushort[] { 0, 1, 2, 1, 3, 2 })
		{
			U16(lod, i);
		}

		U32(lod, 0);

		byte[] payload = lod.ToArray();
		var file = new MemoryStream();
		file.Write(Encoding.ASCII.GetBytes("MeshFile"));
		U32(file, 47);
		U32(file, 99);
		file.Write(new byte[12]);
		for (int k = 0; k < 4; k++)
		{
			F32(file, k == 3 ? 10f : 0f);
		}

		for (int k = 0; k < 6; k++)
		{
			F32(file, k < 3 ? -10f : 10f);
		}

		U32(file, 1);
		U32(file, (uint)payload.Length);
		U32(file, 0);
		U32(file, 0);
		F32(file, 1f);
		file.Write(new byte[Engine_MdlEngine47.BodyStart - (int)file.Position]);
		AssertThat((int)file.Position).IsEqual(Engine_MdlEngine47.BodyStart);
		file.Write(payload);

		int at = Engine_MdlEngine47.BodyStart;
		layout.RigidS0 += at;
		layout.RigidS3 += at;
		layout.SkinnedS0 += at;
		layout.SkinnedS3 += at;
		return file.ToArray();
	}

	private static MdlInfo Parse(byte[] bytes)
	{
		var info = new MdlInfo();
		AssertThat(Engine_MdlEngine47.TryParse(bytes, info)).IsTrue();
		return info;
	}

	private static string Msg(string text) => text.Length == 0 ? "(no detail)" : text;

	private static float HalfAt(byte[] d, int at) => MdlReader.HalfToFloat(BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(at, 2)));

	// ------------------------------------------------------------------ the encoders

	[TestCase]
	public void EncodersRoundTripThroughTheReadersDecoders()
	{
		foreach (float x in new[] { 0f, 1f, -1f, 0.5f, -0.333f, 1.25f, 0.0009765625f, -17.5f, 1000.25f })
		{
			float back = MdlReader.HalfToFloat(Engine_MdlWriter.FloatToHalf(x));
			AssertThat(MathF.Abs(back - x)).IsLessEqual(MathF.Max(MathF.Abs(x) / 1024f, 1e-6f));
		}

		var directions = new (float X, float Y, float Z)[]
		{
			(0f, 0f, 1f), (1f, 0f, 0f), (0f, -1f, 0f), (0.577f, -0.577f, 0.577f), (-0.6f, 0.8f, 0f),
		};
		foreach ((float x, float y, float z) in directions)
		{
			(float nx, float ny, float nz) = MdlReader.DecodeNormal_11_11_10(Engine_MdlWriter.EncodeNormal_11_11_10(x, y, z));
			AssertThat(MathF.Abs(nx - x)).IsLessEqual(0.5f / 1023f + 1e-6f);
			AssertThat(MathF.Abs(ny - y)).IsLessEqual(0.5f / 1023f + 1e-6f);
			AssertThat(MathF.Abs(nz - z)).IsLessEqual(0.5f / 511f + 1e-6f);

			foreach (float w in new[] { 1f, -1f })
			{
				(float tx, float ty, float tz, float tw) =
					MdlReader.DecodeTangent_DEC3N(Engine_MdlWriter.EncodeTangent_DEC3N(x, y, z, w));
				AssertThat(MathF.Abs(tx - x)).IsLessEqual(0.5f / 511f + 1e-6f);
				AssertThat(MathF.Abs(ty - y)).IsLessEqual(0.5f / 511f + 1e-6f);
				AssertThat(MathF.Abs(tz - z)).IsLessEqual(0.5f / 511f + 1e-6f);
				AssertThat(tw).IsEqual(w);
			}
		}
	}

	// ------------------------------------------------------------------ the reader keeps the offsets

	[TestCase]
	public void ReaderKeepsEachPartsFileOffsets()
	{
		byte[] bytes = BuildModel(out Layout layout);
		MdlInfo info = Parse(bytes);
		AssertThat(info.MeshBuffers.Count).IsEqual(2);

		AssertThat(Engine_MdlEngine47.TryGetPlanarStreams(info.MeshBuffers[0], out Engine_MdlEngine47.PlanarStreams rigid)).IsTrue();
		AssertThat(rigid.Lod).IsEqual(0);
		AssertThat(rigid.SharesLod0Stream).IsFalse();
		AssertThat(rigid.VertexCount).IsEqual(RigidVertices);
		AssertThat(rigid.PositionOffset).IsEqual(layout.RigidS0);
		AssertThat(rigid.PositionStride).IsEqual(12);
		AssertThat(rigid.NormalOffset).IsEqual(layout.RigidS3);

		AssertThat(Engine_MdlEngine47.TryGetPlanarStreams(info.MeshBuffers[1], out Engine_MdlEngine47.PlanarStreams skinned)).IsTrue();
		AssertThat(skinned.VertexCount).IsEqual(SkinnedVertices);
		AssertThat(skinned.PositionOffset).IsEqual(layout.SkinnedS0);
		AssertThat(skinned.PositionStride).IsEqual(20);
		AssertThat(skinned.NormalOffset).IsEqual(layout.SkinnedS3);

		AssertThat(Engine_MdlEngine47.TryGetPlanarStreams(new MdlMeshBuffer(), out _)).IsFalse();
	}

	// ------------------------------------------------------------------ the rewrite

	[TestCase]
	public void RewriteMovesVerticesAndLeavesEveryOtherByte()
	{
		byte[] original = BuildModel(out Layout layout);
		MdlInfo info = Parse(original);

		float[] rigidPositions = { 0.25f, -1.5f, 2f, 3.125f, 0.0625f, -7f, 100.5f, 0f, -0.75f };
		float[] skinnedPositions = { 1f, 2f, 3f, -1f, -2f, -3f, 0.5f, 0.25f, 0.125f, 9f, -9f, 0.875f };
		float[] skinnedNormals = { 1f, 0f, 0f, 0f, 0f, -1f, 0.6f, 0.8f, 0f, 0f, -0.8f, 0.6f };
		float[] skinnedTangents = { 0f, 1f, 0f, 1f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, -1f, -1f, 0f, 0f, 1f };
		var edits = new List<MdlGeometryEdit>
		{
			new() { BufferIndex = 0, Positions = rigidPositions },
			new() { BufferIndex = 1, Positions = skinnedPositions, Normals = skinnedNormals, Tangents = skinnedTangents },
		};

		AssertThat(Engine_MdlWriter.TryRewritePlanarVertices(original, info, edits, out byte[] rewritten, out string error))
			.OverrideFailureMessage(Msg(error)).IsTrue();
		AssertThat(rewritten.Length).IsEqual(original.Length);
		AssertThat(ReferenceEquals(rewritten, original)).IsFalse();

		// Exactly the written lanes may differ: xyz of each position, and the skinned part's
		// normal and tangent words. The w lane, UVs, bone bytes, the rigid normals, indices and
		// every table are the template's.
		var written = new bool[original.Length];
		void Mark(int at, int count)
		{
			for (int k = 0; k < count; k++)
			{
				written[at + k] = true;
			}
		}

		for (int v = 0; v < RigidVertices; v++)
		{
			Mark(layout.RigidS0 + (v * 12), 6);
		}

		for (int v = 0; v < SkinnedVertices; v++)
		{
			Mark(layout.SkinnedS0 + (v * 20), 6);
			Mark(layout.SkinnedS3 + (v * 8), 8);
		}

		var changed = new List<int>();
		for (int i = 0; i < original.Length; i++)
		{
			if (!written[i] && rewritten[i] != original[i])
			{
				changed.Add(i);
			}
		}

		AssertThat(changed.Count).OverrideFailureMessage($"bytes changed outside the written lanes: {string.Join(", ", changed.Take(16))}").IsEqual(0);

		MdlInfo again = Parse(rewritten);
		AssertThat(again.MeshBuffers.Count).IsEqual(2);
		byte[] body = again.Body!;

		MdlMeshBuffer rigid = again.MeshBuffers[0];
		for (int v = 0; v < RigidVertices; v++)
		{
			int p = rigid.VertexOffset + (v * 20);
			for (int c = 0; c < 3; c++)
			{
				AssertThat(HalfAt(body, p + (c * 2))).IsEqual(rigidPositions[(v * 3) + c]);
			}

			AssertThat(HalfAt(body, p + 6)).IsEqual(4f);
			(float nx, float ny, float nz) = MdlReader.DecodeNormal_11_11_10(MdlReader.ReadU32BE(body, p + 8));
			AssertThat(nx).IsEqual(0f);
			AssertThat(ny).IsEqual(0f);
			AssertThat(nz).IsEqual(1f);
			AssertThat(HalfAt(body, p + 12)).IsEqual(v * 0.25f);
		}

		MdlMeshBuffer skinned = again.MeshBuffers[1];
		for (int v = 0; v < SkinnedVertices; v++)
		{
			int p = skinned.VertexOffset + (v * 28);
			for (int c = 0; c < 3; c++)
			{
				AssertThat(HalfAt(body, p + (c * 2))).IsEqual(skinnedPositions[(v * 3) + c]);
			}

			AssertThat(HalfAt(body, p + 6)).IsEqual(4f);
			(float nx, float ny, float nz) = MdlReader.DecodeNormal_11_11_10(MdlReader.ReadU32BE(body, p + 8));
			AssertThat(MathF.Abs(nx - skinnedNormals[v * 3])).IsLessEqual(1f / 1023f);
			AssertThat(MathF.Abs(ny - skinnedNormals[(v * 3) + 1])).IsLessEqual(1f / 1023f);
			AssertThat(MathF.Abs(nz - skinnedNormals[(v * 3) + 2])).IsLessEqual(1f / 511f);
			AssertThat((int)body[p + 19]).IsEqual(255);
			AssertThat(HalfAt(body, p + 22)).IsEqual(v * 0.125f);
			(float tx, float ty, float tz, float tw) = MdlReader.DecodeTangent_DEC3N(MdlReader.ReadU32BE(body, p + 24));
			AssertThat(MathF.Abs(tx - skinnedTangents[v * 4])).IsLessEqual(1f / 511f);
			AssertThat(MathF.Abs(ty - skinnedTangents[(v * 4) + 1])).IsLessEqual(1f / 511f);
			AssertThat(MathF.Abs(tz - skinnedTangents[(v * 4) + 2])).IsLessEqual(1f / 511f);
			AssertThat(tw).IsEqual(skinnedTangents[(v * 4) + 3]);
		}

		AssertThat(ModMeshOverlays.SameTopology(rewritten, original, out string topologyError))
			.OverrideFailureMessage(Msg(topologyError)).IsTrue();
	}

	[TestCase]
	public void RewriteRefusesWhatItCannotDoInPlace()
	{
		byte[] original = BuildModel(out _);
		MdlInfo info = Parse(original);

		AssertThat(Engine_MdlWriter.TryRewritePlanarVertices(original, info,
			new[] { new MdlGeometryEdit { BufferIndex = 0, Positions = new float[3 * (RigidVertices + 1)] } },
			out _, out string countError)).IsFalse();
		AssertThat(countError).Contains("holds 3 vertices");

		AssertThat(Engine_MdlWriter.TryRewritePlanarVertices(original, info,
			new[] { new MdlGeometryEdit { BufferIndex = 0, Uvs = new float[2 * RigidVertices] } },
			out _, out string uvError)).IsFalse();
		AssertThat(uvError).Contains("UVs and indices");

		AssertThat(Engine_MdlWriter.TryRewritePlanarVertices(original, info,
			new[] { new MdlGeometryEdit { BufferIndex = 2, Positions = new float[3] } },
			out _, out string indexError)).IsFalse();
		AssertThat(indexError).Contains("no part 2");

		AssertThat(Engine_MdlWriter.TryRewritePlanarVertices(original, info,
			new[] { new MdlGeometryEdit { BufferIndex = 0, Positions = Enumerable.Repeat(70000f, 3 * RigidVertices).ToArray() } },
			out _, out string rangeError)).IsFalse();
		AssertThat(rangeError).Contains("FLOAT16");

		// A buffer this reader did not produce has no offsets to write at.
		var foreign = new MdlInfo { Body = Array.Empty<byte>() };
		foreign.MeshBuffers.Add(new MdlMeshBuffer { VertexCount = RigidVertices });
		AssertThat(Engine_MdlWriter.TryRewritePlanarVertices(original, foreign,
			new[] { new MdlGeometryEdit { BufferIndex = 0, Positions = new float[3 * RigidVertices] } },
			out _, out string offsetError)).IsFalse();
		AssertThat(offsetError).Contains("no vertex stream");

		// A version-36 parse (no Body) is not this writer's.
		AssertThat(Engine_MdlWriter.TryRewritePlanarVertices(original, new MdlInfo(),
			Array.Empty<MdlGeometryEdit>(), out _, out string versionError)).IsFalse();
		AssertThat(versionError).Contains("Fable 3");
	}

	// ------------------------------------------------------------------ a glTF over a version-47 template

	/// <summary>A GLB of one mesh: per primitive, positions (glTF Y-up), normals and a triangle list.</summary>
	private static byte[] BuildGlb(IReadOnlyList<(float[] Positions, float[] Normals, ushort[] Indices)> primitives)
	{
		var bin = new MemoryStream();
		var views = new List<string>();
		var accessors = new List<string>();

		int Accessor(byte[] data, int count, int componentType, string type)
		{
			while (bin.Length % 4 != 0)
			{
				bin.WriteByte(0);
			}

			views.Add($"{{\"buffer\":0,\"byteOffset\":{bin.Length},\"byteLength\":{data.Length}}}");
			bin.Write(data);
			accessors.Add($"{{\"bufferView\":{views.Count - 1},\"componentType\":{componentType},\"count\":{count},\"type\":\"{type}\"}}");
			return accessors.Count - 1;
		}

		int Vec3(float[] values)
		{
			var data = new byte[values.Length * 4];
			for (int i = 0; i < values.Length; i++)
			{
				BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4, 4), values[i]);
			}

			return Accessor(data, values.Length / 3, 5126, "VEC3");
		}

		var prims = new List<string>();
		foreach ((float[] positions, float[] normals, ushort[] indices) in primitives)
		{
			int pos = Vec3(positions);
			int nrm = Vec3(normals);
			var data = new byte[indices.Length * 2];
			for (int i = 0; i < indices.Length; i++)
			{
				BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2, 2), indices[i]);
			}

			int idx = Accessor(data, indices.Length, 5123, "SCALAR");
			prims.Add($"{{\"attributes\":{{\"POSITION\":{pos},\"NORMAL\":{nrm}}},\"indices\":{idx}}}");
		}

		string json =
			"{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}]," +
			"\"nodes\":[{\"name\":\"Mesh\",\"mesh\":0}]," +
			$"\"meshes\":[{{\"primitives\":[{string.Join(",", prims)}]}}]," +
			$"\"buffers\":[{{\"byteLength\":{bin.Length}}}]," +
			$"\"bufferViews\":[{string.Join(",", views)}]," +
			$"\"accessors\":[{string.Join(",", accessors)}]}}";

		byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
		int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
		byte[] binBytes = bin.ToArray();
		int binPad = (4 - (binBytes.Length % 4)) % 4;
		int total = 12 + 8 + jsonBytes.Length + jsonPad + 8 + binBytes.Length + binPad;
		var glb = new byte[total];
		BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(0, 4), 0x46546C67u);
		BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4, 4), 2u);
		BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8, 4), (uint)total);
		BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12, 4), (uint)(jsonBytes.Length + jsonPad));
		BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16, 4), 0x4E4F534Au);
		jsonBytes.CopyTo(glb, 20);
		for (int i = 0; i < jsonPad; i++)
		{
			glb[20 + jsonBytes.Length + i] = (byte)' ';
		}

		int binAt = 20 + jsonBytes.Length + jsonPad;
		BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binAt, 4), (uint)(binBytes.Length + binPad));
		BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binAt + 4, 4), 0x004E4942u);
		binBytes.CopyTo(glb, binAt + 8);
		return glb;
	}

	/// <summary>Model space (Z up) to glTF (Y up): the reader's (x, y, z) -> (x, z, -y).</summary>
	private static float[] ToGltf(float[] model)
	{
		var gltf = new float[model.Length];
		for (int i = 0; i + 2 < model.Length; i += 3)
		{
			gltf[i] = model[i];
			gltf[i + 1] = model[i + 2];
			gltf[i + 2] = -model[i + 1];
		}

		return gltf;
	}

	[TestCase]
	public void GltfOverAVersion47TemplateMovesItsVertices()
	{
		byte[] original = BuildModel(out _);
		float[] rigid = { 0.5f, 0.25f, 1f, -0.5f, 0.75f, 2f, 1.5f, -1f, 0f };
		float[] skinned = { 2f, 1f, 0f, 2.5f, 1f, -0.5f, 4f, 1.5f, -1f, 6f, 1f, -1.5f };
		float[] rigidNormals = { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f };
		float[] skinnedNormals = { 1f, 0f, 0f, 1f, 0f, 0f, 0f, -1f, 0f, 0f, -1f, 0f };
		byte[] glb = BuildGlb(new[]
		{
			(ToGltf(rigid), ToGltf(rigidNormals), new ushort[] { 0, 1, 2 }),
			(ToGltf(skinned), ToGltf(skinnedNormals), new ushort[] { 0, 1, 2, 1, 3, 2 }),
		});

		AssertThat(ModMeshOverlays.TryConvert(glb, original, out byte[] rebuilt, out string error, out int vertices, out _))
			.OverrideFailureMessage(Msg(error)).IsTrue();
		AssertThat(vertices).IsEqual(RigidVertices + SkinnedVertices);
		AssertThat(rebuilt.Length).IsEqual(original.Length);

		MdlInfo info = Parse(rebuilt);
		byte[] body = info.Body!;
		for (int v = 0; v < RigidVertices; v++)
		{
			for (int c = 0; c < 3; c++)
			{
				AssertThat(HalfAt(body, info.MeshBuffers[0].VertexOffset + (v * 20) + (c * 2))).IsEqual(rigid[(v * 3) + c]);
			}
		}

		for (int v = 0; v < SkinnedVertices; v++)
		{
			int p = info.MeshBuffers[1].VertexOffset + (v * 28);
			for (int c = 0; c < 3; c++)
			{
				AssertThat(HalfAt(body, p + (c * 2))).IsEqual(skinned[(v * 3) + c]);
			}

			(float nx, float ny, float nz) = MdlReader.DecodeNormal_11_11_10(MdlReader.ReadU32BE(body, p + 8));
			AssertThat(MathF.Abs(nx - skinnedNormals[v * 3])).IsLessEqual(1f / 1023f);
			AssertThat(MathF.Abs(ny - skinnedNormals[(v * 3) + 1])).IsLessEqual(1f / 1023f);
			AssertThat(MathF.Abs(nz - skinnedNormals[(v * 3) + 2])).IsLessEqual(1f / 511f);
		}

		AssertThat(ModMeshOverlays.SameTopology(rebuilt, original, out string topologyError))
			.OverrideFailureMessage(Msg(topologyError)).IsTrue();

		// One primitive over both parts, at the parts' total, splits in part order.
		byte[] joined = BuildGlb(new[]
		{
			(ToGltf(rigid.Concat(skinned).ToArray()), ToGltf(rigidNormals.Concat(skinnedNormals).ToArray()),
				new ushort[] { 0, 1, 2, 3, 4, 5 }),
		});
		AssertThat(ModMeshOverlays.TryConvert(joined, original, out byte[] fromJoined, out string joinedError))
			.OverrideFailureMessage(Msg(joinedError)).IsTrue();
		AssertThat(fromJoined.SequenceEqual(rebuilt)).IsTrue();

		// A primitive per part whose count is not its part's is refused by name.
		byte[] wrong = BuildGlb(new[]
		{
			(ToGltf(rigid), ToGltf(rigidNormals), new ushort[] { 0, 1, 2 }),
			(ToGltf(skinned[..9]), ToGltf(skinnedNormals[..9]), new ushort[] { 0, 1, 2 }),
		});
		AssertThat(ModMeshOverlays.TryConvert(wrong, original, out _, out string wrongError)).IsFalse();
		AssertThat(wrongError).Contains("glTF primitive 1 has 3 vertices");
	}

	// ------------------------------------------------------------------ the shipped models

	private const string Fable3Game =
		"/home/loreaxe/Fable 2/refii-clean/refii/out/package/Fable Return to Albion/assets/Fable3/game";

	private static void WithMount(Action<BnkFileSystem> body)
	{
		if (!Directory.Exists(Path.Combine(Fable3Game, "data")))
		{
			return;
		}

		Engine_Title previous = Engine_Title.Active;
		Engine_Title.Active = Engine_Title.Fable3;
		try
		{
			using BnkFileSystem fs = BnkFileSystem.Mount(Fable3Game);
			body(fs);
		}
		finally
		{
			Engine_Title.Active = previous;
		}
	}

	/// <summary>
	/// Every shipped part with its own stream: the file bytes at its recorded offsets are the
	/// bytes the repack put in the body — position at +0, normal at +8, tangent at the layout's
	/// tangent slot.
	/// </summary>
	[TestCase(Timeout = 1_800_000)]
	public void ShippedOffsetsAddressTheRepackedBytes()
	{
		WithMount(fs =>
		{
			int parts = 0, shared = 0;
			var failures = new List<string>();
			foreach (BnkFile file in fs.EnumerateAllFiles())
			{
				if (!file.VirtualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				byte[] bytes = file.ReadAllBytes();
				if (!Engine_MdlData.TryParseInfo(bytes, file.VirtualPath, out MdlInfo info, out _) || info.IsStub)
				{
					continue;
				}

				IEnumerable<MdlMeshBuffer> buffers = info.MeshBuffers.Concat(info.Lods.SelectMany(l => l.MeshBuffers));
				foreach (MdlMeshBuffer mb in buffers)
				{
					if (!Engine_MdlEngine47.TryGetPlanarStreams(mb, out Engine_MdlEngine47.PlanarStreams planar))
					{
						failures.Add($"{file.VirtualPath}: a buffer without planar streams");
						continue;
					}

					if (planar.SharesLod0Stream)
					{
						shared++;
						continue;
					}

					parts++;
					int stride = Engine_MdlWriter.StrideFor(mb);
					int tangentAt = Engine_MdlWriter.TangentOffsetFor(mb);
					for (int v = 0; v < mb.VertexCount; v++)
					{
						int p = planar.PositionOffset + (v * planar.PositionStride);
						int n = planar.NormalOffset + (v * Engine_MdlEngine47.PlanarStreams.NormalStride);
						int b = mb.VertexOffset + (v * stride);
						if (!bytes.AsSpan(p, 8).SequenceEqual(info.Body.AsSpan(b, 8)) ||
							!bytes.AsSpan(n, 4).SequenceEqual(info.Body.AsSpan(b + 8, 4)) ||
							!bytes.AsSpan(n + 4, 4).SequenceEqual(info.Body.AsSpan(b + tangentAt, 4)))
						{
							failures.Add($"{file.VirtualPath}: vertex {v} differs from the repack");
							break;
						}
					}
				}
			}

			Console.WriteLine($"[mdl47-planar] parts with a stream {parts}, sharing LOD 0's {shared}, failures {failures.Count}");
			AssertThat(failures.Count).OverrideFailureMessage(string.Join("\n", failures.Take(20)) + " ").IsEqual(0);
			AssertThat(parts).IsGreater(0);
		});
	}

	/// <summary>
	/// The hero's morph body moved by a fixed offset: it re-parses, keeps its topology, and every
	/// position reads back moved by that offset within FLOAT16 precision.
	/// </summary>
	[TestCase]
	public void ShippedHeroBodyRewritesInPlace()
	{
		WithMount(fs =>
		{
			string path = fs.EnumerateAllFiles()
				.Select(f => f.VirtualPath)
				.First(p => p.EndsWith("ch_heromale_morph_body.mdl", StringComparison.OrdinalIgnoreCase));
			AssertThat(fs.TryGetFile(path, out BnkFile file)).IsTrue();
			byte[] original = file.ReadAllBytes();
			AssertThat(Engine_MdlData.TryParseInfo(original, out MdlInfo info, out string parseError))
				.OverrideFailureMessage(Msg(parseError)).IsTrue();

			var edits = new List<MdlGeometryEdit>();
			for (int i = 0; i < info.MeshBuffers.Count; i++)
			{
				MdlMeshBuffer mb = info.MeshBuffers[i];
				var positions = new float[mb.VertexCount * 3];
				for (int v = 0; v < mb.VertexCount; v++)
				{
					for (int c = 0; c < 3; c++)
					{
						positions[(v * 3) + c] = HalfAt(info.Body!, mb.VertexOffset + (v * 28) + (c * 2)) + 0.03125f;
					}
				}

				edits.Add(new MdlGeometryEdit { BufferIndex = i, Positions = positions });
			}

			AssertThat(Engine_MdlWriter.TryRewritePlanarVertices(original, info, edits, out byte[] rewritten, out string error))
				.OverrideFailureMessage(Msg(error)).IsTrue();
			AssertThat(ModMeshOverlays.SameTopology(rewritten, original, out string topologyError))
				.OverrideFailureMessage(Msg(topologyError)).IsTrue();

			AssertThat(Engine_MdlData.TryParseInfo(rewritten, out MdlInfo again, out _)).IsTrue();
			for (int i = 0; i < again.MeshBuffers.Count; i++)
			{
				MdlMeshBuffer mb = again.MeshBuffers[i];
				MdlMeshBuffer was = info.MeshBuffers[i];
				for (int v = 0; v < mb.VertexCount; v++)
				{
					for (int c = 0; c < 3; c++)
					{
						float before = HalfAt(info.Body!, was.VertexOffset + (v * 28) + (c * 2));
						float after = HalfAt(again.Body!, mb.VertexOffset + (v * 28) + (c * 2));
						AssertThat(MathF.Abs(after - before - 0.03125f)).IsLessEqual(MathF.Max(MathF.Abs(after), 1f) / 1024f);
					}

					AssertThat(again.Body.AsSpan(mb.VertexOffset + (v * 28) + 6, 22)
						.SequenceEqual(info.Body.AsSpan(was.VertexOffset + (v * 28) + 6, 22))).IsTrue();
				}
			}
		});
	}
}
