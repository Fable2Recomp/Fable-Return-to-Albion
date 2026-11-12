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
/// The `.mdl` converter, step by step: the writer is the reader's inverse (step 2), a glTF read by
/// our own parser becomes a model's mesh (3), several primitives and LODs collapse the way the
/// engine already does (4), and a rigged glTF skins to a template's bones by name (5). Every
/// fixture is synthetic and built here; the shipped sweep reads the player's banks in place and
/// is skipped without an install. No Godot type is built.
/// </summary>
[TestSuite]
public class MdlConverterTests
{
	// ------------------------------------------------------------------ fixtures: a synthetic .mdl

	private sealed class Part
	{
		public bool Skinned;

		public float[] Positions = Array.Empty<float>();

		public float[] Uvs = Array.Empty<float>();

		public ushort[] Indices = Array.Empty<ushort>();

		/// <summary>Four per vertex; only for a skinned part.</summary>
		public byte[] BoneIndices = Array.Empty<byte>();

		public byte[] BoneWeights = Array.Empty<byte>();

		public int Sections = 1;

		public int VertexCount => Positions.Length / 3;
	}

	private sealed class Fixture
	{
		public List<Part> Parts = new();

		public string[] Bones = Array.Empty<string>();

		public int[] BoneParents = Array.Empty<int>();

		/// <summary>Extra float4 UV sets per vertex, stated in the LOD header (`ShDescriptor[4]`).</summary>
		public uint SecondStreamSets;

		/// <summary>The section flag: non-zero adds the lightmap channel after the records.</summary>
		public byte SectionFlag;

		/// <summary>LOD sizes: slot 0 is the payload built here; a non-zero later slot gets a copy of it.</summary>
		public int LodCount = 1;

		public bool[] LaterLodsPresent = { true, true };

		public int Materials = 1;
	}

	private static void U32(Stream s, uint v)
	{
		s.WriteByte((byte)(v >> 24));
		s.WriteByte((byte)(v >> 16));
		s.WriteByte((byte)(v >> 8));
		s.WriteByte((byte)v);
	}

	private static void F32(Stream s, float v) => U32(s, unchecked((uint)BitConverter.SingleToInt32Bits(v)));

	private static void Strz(Stream s, string text)
	{
		byte[] ascii = Encoding.ASCII.GetBytes(text);
		s.Write(ascii, 0, ascii.Length);
		s.WriteByte(0);
	}

	private static void Half(Stream s, float v)
	{
		ushort h = Engine_MdlWriter.FloatToHalf(v);
		s.WriteByte((byte)(h >> 8));
		s.WriteByte((byte)h);
	}

	private static byte[] BuildLod(Fixture f)
	{
		using var ms = new MemoryStream();
		ms.Write(new byte[32]);
		U32(ms, (uint)f.Bones.Length);
		for (int i = 0; i < f.Bones.Length; i++)
		{
			Strz(ms, f.Bones[i]);
			U32(ms, f.BoneParents[i] < 0 ? 0xFFFFFFFFu : (uint)f.BoneParents[i]);
		}

		U32(ms, (uint)f.Bones.Length);
		for (int i = 0; i < f.Bones.Length; i++)
		{
			// quaternion xyzw, translation, scale, pad
			F32(ms, 0); F32(ms, 0); F32(ms, 0); F32(ms, 1);
			F32(ms, 0); F32(ms, i); F32(ms, 0);
			F32(ms, 1); F32(ms, 1); F32(ms, 1);
			F32(ms, 0);
		}

		ms.Write(new byte[40]);
		U32(ms, 1u);
		U32(ms, (uint)f.Parts.Count(p => !p.Skinned));
		U32(ms, (uint)f.Parts.Count(p => p.Skinned));
		U32(ms, 0u);
		U32(ms, 0u);
		ms.WriteByte(f.SectionFlag);
		U32(ms, 0u);
		for (int i = 0; i < 4; i++)
		{
			U32(ms, 0u);
		}

		U32(ms, f.SecondStreamSets);
		U32(ms, 0u);
		U32(ms, 0u);

		U32(ms, 0u);
		Strz(ms, "FixtureMesh");
		ms.Write(new byte[45]);
		U32(ms, (uint)f.Materials);
		for (int m = 0; m < f.Materials; m++)
		{
			Strz(ms, $"art\\fixture_{m}_d.tex");
			Strz(ms, $"art\\fixture_{m}_s.tex");
			Strz(ms, $"art\\fixture_{m}_n.tex");
			Strz(ms, string.Empty);
			Strz(ms, string.Empty);
			for (int k = 0; k < 4; k++)
			{
				Strz(ms, string.Empty);
			}

			ms.Write(new byte[8]);
		}

		void Sections(Part p)
		{
			U32(ms, (uint)p.Sections);
			int triangles = p.Indices.Length / 3;
			int per = triangles / p.Sections;
			for (int s = 0; s < p.Sections; s++)
			{
				U32(ms, 0xFFFFFFFFu);
				U32(ms, (uint)Math.Min(s, f.Materials - 1));
				ms.WriteByte(0);
				int count = s == p.Sections - 1 ? triangles - (per * s) : per;
				U32(ms, (uint)count);
				U32(ms, (uint)(per * s * 3));
				for (int k = 0; k < 6; k++)
				{
					F32(ms, 0f);
				}
			}
		}

		void Vertices(Part p)
		{
			for (int v = 0; v < p.VertexCount; v++)
			{
				Half(ms, p.Positions[v * 3]);
				Half(ms, p.Positions[(v * 3) + 1]);
				Half(ms, p.Positions[(v * 3) + 2]);
				ms.WriteByte(0);
				ms.WriteByte(0);
				U32(ms, Engine_MdlWriter.EncodeNormal_11_11_10(0f, 0f, 1f));
				if (p.Skinned)
				{
					for (int k = 0; k < 4; k++)
					{
						ms.WriteByte(p.BoneIndices[(v * 4) + k]);
					}

					for (int k = 0; k < 4; k++)
					{
						ms.WriteByte(p.BoneWeights[(v * 4) + k]);
					}
				}

				Half(ms, p.Uvs.Length > (v * 2) + 1 ? p.Uvs[v * 2] : 0f);
				Half(ms, p.Uvs.Length > (v * 2) + 1 ? p.Uvs[(v * 2) + 1] : 0f);
				U32(ms, Engine_MdlWriter.EncodeTangent_DEC3N(1f, 0f, 0f, 1f));
			}

			foreach (ushort i in p.Indices)
			{
				ms.WriteByte((byte)(i >> 8));
				ms.WriteByte((byte)i);
			}

			ms.Write(new byte[p.VertexCount * 16 * f.SecondStreamSets]);
		}

		foreach (Part p in f.Parts.Where(p => !p.Skinned))
		{
			Strz(ms, "FixtureMesh");
			ms.WriteByte(0);
			U32(ms, 0u);
			U32(ms, 0u);
			U32(ms, 0u);
			U32(ms, (uint)p.Indices.Length);
			U32(ms, (uint)p.VertexCount);
			ms.Write(new byte[40]);
			Sections(p);
			Vertices(p);
			U32(ms, 0u);
		}

		foreach (Part p in f.Parts.Where(p => p.Skinned))
		{
			U32(ms, 0u);
			U32(ms, 0u);
			U32(ms, 0u);
			U32(ms, (uint)p.Indices.Length);
			U32(ms, (uint)p.VertexCount);
			Sections(p);
			Vertices(p);
			U32(ms, 0u);
			U32(ms, 0u);
		}

		if (f.SectionFlag != 0)
		{
			int rigidVertices = f.Parts.Where(p => !p.Skinned).Sum(p => p.VertexCount);
			for (int v = 0; v < rigidVertices; v++)
			{
				// A recognisable lightmap UV per vertex, so a carried slice can be told from a zeroed one.
				Half(ms, 0.25f);
				Half(ms, 0.75f);
			}
		}

		return ms.ToArray();
	}

	private static byte[] BuildMdl(Fixture f)
	{
		byte[] lod = BuildLod(f);
		using var file = new MemoryStream();
		var header = new byte[104];
		Encoding.ASCII.GetBytes("MeshFile").CopyTo(header, 0);
		header[11] = 36;
		header[15] = 88;
		header[0x3B] = (byte)f.LodCount;
		header[0x4C] = (byte)(f.Parts.Any(p => p.Skinned) ? 1 : 0);
		var sizes = new uint[3];
		sizes[0] = (uint)lod.Length;
		for (int i = 1; i < f.LodCount; i++)
		{
			sizes[i] = f.LaterLodsPresent[i - 1] ? (uint)lod.Length : 0u;
		}

		for (int i = 0; i < 3; i++)
		{
			BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x3C + (i * 4), 4), sizes[i]);
		}

		file.Write(header, 0, header.Length);
		for (int i = 0; i < f.LodCount; i++)
		{
			if (sizes[i] != 0)
			{
				file.Write(lod, 0, lod.Length);
			}
		}

		return file.ToArray();
	}

	private static Part Triangle(bool skinned = false) => new()
	{
		Skinned = skinned,
		Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
		Uvs = new float[] { 0, 0, 1, 0, 0, 1 },
		Indices = new ushort[] { 0, 1, 2 },
		BoneIndices = skinned ? new byte[12] : Array.Empty<byte>(),
		BoneWeights = skinned ? new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255 } : Array.Empty<byte>(),
	};

	/// <summary>
	/// A synthetic model for other suites: one triangle part per entry, skinned where true, or a
	/// quad (four vertices, two triangles) where <paramref name="quad"/> names that part.
	/// </summary>
	internal static byte[] SyntheticModel(bool[] skinned, int quad = -1)
	{
		var fixture = new Fixture { Bones = new[] { "Root" }, BoneParents = new[] { -1 } };
		for (int i = 0; i < skinned.Length; i++)
		{
			Part part = Triangle(skinned[i]);
			if (i == quad)
			{
				part.Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 };
				part.Uvs = new float[] { 0, 0, 1, 0, 0, 1, 1, 1 };
				part.Indices = new ushort[] { 0, 1, 2, 2, 1, 3 };
				part.BoneIndices = skinned[i] ? new byte[16] : Array.Empty<byte>();
				part.BoneWeights = skinned[i] ? new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255 } : Array.Empty<byte>();
			}

			fixture.Parts.Add(part);
		}

		return BuildMdl(fixture);
	}

	/// <summary>A unit cube, 8 vertices and 12 triangles, in model space.</summary>
	private static (float[] Positions, float[] Uvs, ushort[] Indices) Cube()
	{
		var positions = new float[24];
		var uvs = new float[16];
		for (int v = 0; v < 8; v++)
		{
			positions[v * 3] = (v & 1) != 0 ? 1f : -1f;
			positions[(v * 3) + 1] = (v & 2) != 0 ? 1f : -1f;
			positions[(v * 3) + 2] = (v & 4) != 0 ? 1f : -1f;
			uvs[v * 2] = (v & 1) != 0 ? 1f : 0f;
			uvs[(v * 2) + 1] = (v & 2) != 0 ? 1f : 0f;
		}

		ushort[] indices =
		{
			0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4,
			2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5,
		};
		return (positions, uvs, indices);
	}

	// ------------------------------------------------------------------ fixtures: a synthetic GLB

	private sealed class GlbPrimitive
	{
		/// <summary>glTF space (Y up).</summary>
		public float[] Positions = Array.Empty<float>();

		public float[]? Normals = null;

		public float[]? Uvs;

		public ushort[]? Indices;

		public int Material = -1;

		public byte[]? Joints;

		public float[]? Weights;
	}

	private sealed class GlbScene
	{
		public List<GlbPrimitive> Primitives = new();

		public string[] MaterialNames = Array.Empty<string>();

		/// <summary>The mesh node's TRS; null for none.</summary>
		public double[]? Translation;

		public double[]? Rotation = null;

		public double[]? Scale;

		/// <summary>Joint names; a skin is written when non-empty, and the mesh node carries it.</summary>
		public string[] JointNames = Array.Empty<string>();

		public bool External;
	}

	private static byte[] BuildGlb(GlbScene scene)
	{
		var bin = new MemoryStream();
		var views = new List<string>();
		var accessors = new List<string>();

		int View(byte[] data)
		{
			while (bin.Length % 4 != 0)
			{
				bin.WriteByte(0);
			}

			views.Add($"{{\"buffer\":0,\"byteOffset\":{bin.Length},\"byteLength\":{data.Length}}}");
			bin.Write(data, 0, data.Length);
			return views.Count - 1;
		}

		int Floats(float[] values, int components, string type)
		{
			var data = new byte[values.Length * 4];
			for (int i = 0; i < values.Length; i++)
			{
				BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4, 4), values[i]);
			}

			int view = View(data);
			string minMax = string.Empty;
			if (type == "VEC3")
			{
				var min = new float[3];
				var max = new float[3];
				for (int c = 0; c < 3; c++)
				{
					min[c] = Enumerable.Range(0, values.Length / 3).Min(v => values[(v * 3) + c]);
					max[c] = Enumerable.Range(0, values.Length / 3).Max(v => values[(v * 3) + c]);
				}

				minMax = $",\"min\":[{F(min[0])},{F(min[1])},{F(min[2])}],\"max\":[{F(max[0])},{F(max[1])},{F(max[2])}]";
			}

			accessors.Add($"{{\"bufferView\":{view},\"componentType\":5126,\"count\":{values.Length / components},\"type\":\"{type}\"{minMax}}}");
			return accessors.Count - 1;
		}

		int Shorts(ushort[] values)
		{
			var data = new byte[values.Length * 2];
			for (int i = 0; i < values.Length; i++)
			{
				BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2, 2), values[i]);
			}

			int view = View(data);
			accessors.Add($"{{\"bufferView\":{view},\"componentType\":5123,\"count\":{values.Length},\"type\":\"SCALAR\"}}");
			return accessors.Count - 1;
		}

		int Bytes4(byte[] values)
		{
			int view = View(values);
			accessors.Add($"{{\"bufferView\":{view},\"componentType\":5121,\"count\":{values.Length / 4},\"type\":\"VEC4\"}}");
			return accessors.Count - 1;
		}

		var prims = new List<string>();
		foreach (GlbPrimitive p in scene.Primitives)
		{
			var attrs = new List<string> { $"\"POSITION\":{Floats(p.Positions, 3, "VEC3")}" };
			if (p.Normals is not null)
			{
				attrs.Add($"\"NORMAL\":{Floats(p.Normals, 3, "VEC3")}");
			}

			if (p.Uvs is not null)
			{
				attrs.Add($"\"TEXCOORD_0\":{Floats(p.Uvs, 2, "VEC2")}");
			}

			if (p.Joints is not null && p.Weights is not null)
			{
				attrs.Add($"\"JOINTS_0\":{Bytes4(p.Joints)}");
				attrs.Add($"\"WEIGHTS_0\":{Floats(p.Weights, 4, "VEC4")}");
			}

			string prim = $"{{\"attributes\":{{{string.Join(",", attrs)}}}";
			if (p.Indices is not null)
			{
				prim += $",\"indices\":{Shorts(p.Indices)}";
			}

			if (p.Material >= 0)
			{
				prim += $",\"material\":{p.Material}";
			}

			prims.Add(prim + "}");
		}

		var nodes = new List<string>();
		string meshNode = "{\"name\":\"Mesh\",\"mesh\":0";
		if (scene.Translation is not null)
		{
			meshNode += $",\"translation\":[{string.Join(",", scene.Translation.Select(D))}]";
		}

		if (scene.Rotation is not null)
		{
			meshNode += $",\"rotation\":[{string.Join(",", scene.Rotation.Select(D))}]";
		}

		if (scene.Scale is not null)
		{
			meshNode += $",\"scale\":[{string.Join(",", scene.Scale.Select(D))}]";
		}

		string skins = string.Empty;
		if (scene.JointNames.Length != 0)
		{
			meshNode += ",\"skin\":0";
			var jointIndices = new List<int>();
			for (int j = 0; j < scene.JointNames.Length; j++)
			{
				jointIndices.Add(1 + j);
			}

			skins = $",\"skins\":[{{\"joints\":[{string.Join(",", jointIndices)}]}}]";
		}

		nodes.Add(meshNode + "}");
		foreach (string joint in scene.JointNames)
		{
			nodes.Add($"{{\"name\":\"{joint}\"}}");
		}

		string materials = scene.MaterialNames.Length == 0
			? string.Empty
			: $",\"materials\":[{string.Join(",", scene.MaterialNames.Select(n => $"{{\"name\":\"{n}\"}}"))}]";

		string buffer = scene.External
			? $"{{\"byteLength\":{bin.Length},\"uri\":\"model.bin\"}}"
			: $"{{\"byteLength\":{bin.Length}}}";

		string json =
			"{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}]," +
			$"\"nodes\":[{string.Join(",", nodes)}]," +
			$"\"meshes\":[{{\"name\":\"FixtureMesh\",\"primitives\":[{string.Join(",", prims)}]}}]" +
			materials + skins +
			$",\"buffers\":[{buffer}]," +
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

	private static string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

	private static string D(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>The cube in glTF space: the reader's rotation of the model-space cube, which is the cube.</summary>
	private static GlbPrimitive CubePrimitive(int material = -1)
	{
		(float[] positions, float[] uvs, ushort[] indices) = Cube();
		return new GlbPrimitive { Positions = positions, Uvs = uvs, Indices = indices, Material = material };
	}

	private static string Msg(string text) => text.Length == 0 ? "(no detail)" : text;

	private static MdlInfo Parse(byte[] mdl)
	{
		var info = new MdlInfo();
		AssertThat(Engine_MdlEngine.TryParse(mdl, info)).OverrideFailureMessage("the model does not parse as an engine model").IsTrue();
		return info;
	}

	private static float HalfAt(byte[] d, int at) => MdlReader.HalfToFloat((ushort)((d[at] << 8) | d[at + 1]));

	private static uint U32At(byte[] d, int at) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at, 4));

	private static float F32At(byte[] d, int at) => BitConverter.Int32BitsToSingle(unchecked((int)U32At(d, at)));

	// ------------------------------------------------------------------ step 2: the writer

	[TestCase]
	public void EveryHalfRoundTripsThroughTheWriter()
	{
		int wrong = 0;
		for (int h = 0; h < 65536; h++)
		{
			float f = MdlReader.HalfToFloat((ushort)h);
			ushort back = Engine_MdlWriter.FloatToHalf(f);
			bool nan = (h & 0x7C00) == 0x7C00 && (h & 0x3FF) != 0;
			if (nan ? (back & 0x7C00) != 0x7C00 || (back & 0x3FF) == 0 : back != h)
			{
				wrong++;
			}
		}

		AssertThat(wrong).OverrideFailureMessage($"{wrong} of 65,536 halves do not come back").IsEqual(0);
	}

	[TestCase]
	public void TheNormalAndTangentEncodersInvertTheDecoders()
	{
		var random = new Random(7);
		for (int i = 0; i < 20000; i++)
		{
			uint packed = (uint)random.Next() ^ ((uint)random.Next() << 16);
			(float x, float y, float z) = MdlReader.DecodeNormal_11_11_10(packed);
			AssertThat(Engine_MdlWriter.EncodeNormal_11_11_10(x, y, z)).IsEqual(packed);

			// The decoder reads handedness bits 0 and 1 as +1 and 2 and 3 as -1; the encoder
			// writes 1 and 3, so only those two values are their own inverse.
			uint tangent = (packed & 0x3FFFFFFFu) | ((packed & 0x40000000u) != 0 ? 0xC0000000u : 0x40000000u);
			(float tx, float ty, float tz, float tw) = MdlReader.DecodeTangent_DEC3N(tangent);
			AssertThat(Engine_MdlWriter.EncodeTangent_DEC3N(tx, ty, tz, tw)).IsEqual(tangent);
		}
	}

	[TestCase]
	public void ASameCountReplacementOfItsOwnGeometryIsByteIdentical()
	{
		byte[] mdl = BuildMdl(new Fixture { Parts = { Triangle(), Triangle(skinned: true) }, Bones = new[] { "Root" }, BoneParents = new[] { -1 } });
		MdlInfo info = Parse(mdl);
		AssertThat(info.MeshBuffers.Count).IsEqual(2);

		for (int b = 0; b < 2; b++)
		{
			MdlMeshBuffer buffer = info.MeshBuffers[b];
			int stride = Engine_MdlWriter.StrideFor(buffer);
			var edit = new MdlGeometryEdit { BufferIndex = b, Positions = new float[buffer.VertexCount * 3], Uvs = new float[buffer.VertexCount * 2], Normals = new float[buffer.VertexCount * 3], Tangents = new float[buffer.VertexCount * 4], Indices = new ushort[buffer.FaceCount] };
			for (int v = 0; v < buffer.VertexCount; v++)
			{
				int p = buffer.VertexOffset + (v * stride);
				for (int c = 0; c < 3; c++)
				{
					edit.Positions[(v * 3) + c] = HalfAt(mdl, p + (c * 2));
				}

				(edit.Normals[v * 3], edit.Normals[(v * 3) + 1], edit.Normals[(v * 3) + 2]) = MdlReader.DecodeNormal_11_11_10(U32At(mdl, p + 8));
				int u = p + Engine_MdlWriter.UvOffsetFor(buffer);
				edit.Uvs[v * 2] = HalfAt(mdl, u);
				edit.Uvs[(v * 2) + 1] = HalfAt(mdl, u + 2);
				(edit.Tangents[v * 4], edit.Tangents[(v * 4) + 1], edit.Tangents[(v * 4) + 2], edit.Tangents[(v * 4) + 3]) = MdlReader.DecodeTangent_DEC3N(U32At(mdl, p + Engine_MdlWriter.TangentOffsetFor(buffer)));
			}

			for (int i = 0; i < buffer.FaceCount; i++)
			{
				edit.Indices[i] = (ushort)((mdl[buffer.FaceOffset + (i * 2)] << 8) | mdl[buffer.FaceOffset + (i * 2) + 1]);
			}

			AssertThat(Engine_MdlWriter.TryReplaceGeometry(mdl, info, edit, out byte[] back, out string error)).OverrideFailureMessage(Msg(error)).IsTrue();
			AssertThat(back).IsEqual(mdl);
		}
	}

	[TestCase]
	public void ResizingARigidBufferRestatesEveryLengthThatFollows()
	{
		// Two rigid parts, an extra UV set per vertex, the lightmap channel, and a second LOD —
		// everything whose length follows from the counts.
		var fixture = new Fixture { Parts = { Triangle(), Triangle() }, SecondStreamSets = 1, SectionFlag = 1, LodCount = 2 };
		byte[] mdl = BuildMdl(fixture);
		MdlInfo before = Parse(mdl);
		AssertThat(before.ShDescriptor[4]).IsEqual(1u);
		AssertThat(before.Lods.Count).IsEqual(1);

		(float[] positions, float[] uvs, ushort[] indices) = Cube();
		var normals = new float[24];
		AssertThat(Engine_MdlWriter.TryReplaceMesh(mdl, before, 0, positions, uvs, normals, Array.Empty<float>(), indices, out byte[] after, out string error))
			.OverrideFailureMessage(Msg(error)).IsTrue();

		MdlInfo info = Parse(after);
		MdlMeshBuffer buffer = info.MeshBuffers[0];
		AssertThat(buffer.VertexCount).IsEqual(8u);
		AssertThat(buffer.FaceCount).IsEqual(36u);
		AssertThat(buffer.VertexOffset + (8 * Engine_MdlWriter.RigidStride)).IsEqual(buffer.FaceOffset);
		AssertThat(buffer.SubMeshes.Count).IsEqual(1);
		AssertThat(buffer.SubMeshes[0].FaceCount).IsEqual(12u);
		AssertThat(buffer.SubMeshes[0].PrimitiveType).IsEqual((byte)0);
		AssertThat(buffer.SubMeshes[0].RegionIndex).IsEqual(uint.MaxValue);

		// The LOD sizes: slot 0 the whole payload, slot 1 zero so it aliases slot 0.
		AssertThat(info.LodSizes[0]).IsEqual((uint)(after.Length - Engine_MdlEngine.BodyStart));
		AssertThat(info.LodSizes[1]).IsEqual(0u);
		AssertThat(info.Lods.Count).IsEqual(0);

		// The other part is untouched: its 3 vertices, and its slice of the lightmap channel.
		MdlMeshBuffer other = info.MeshBuffers[1];
		AssertThat(other.VertexCount).IsEqual(3u);
		AssertThat(HalfAt(after, other.VertexOffset + (1 * 20))).IsEqual(1f);
		int lightmapAt = after.Length - ((8 + 3) * 4);
		for (int v = 0; v < 8; v++)
		{
			AssertThat(U32At(after, lightmapAt + (v * 4))).IsEqual(0u);
		}

		for (int v = 0; v < 3; v++)
		{
			AssertThat(HalfAt(after, lightmapAt + ((8 + v) * 4))).IsEqual(0.25f);
			AssertThat(HalfAt(after, lightmapAt + ((8 + v) * 4) + 2)).IsEqual(0.75f);
		}

		// The rebuilt geometry, and the bounds the guest culls by: header sphere at 0x10,
		// AABB at 0x20, the diameter at 0x48 = max(2r, 0.5) — over BOTH parts.
		for (int v = 0; v < 8; v++)
		{
			for (int c = 0; c < 3; c++)
			{
				AssertThat(HalfAt(after, buffer.VertexOffset + (v * 20) + (c * 2))).IsEqual(positions[(v * 3) + c]);
			}

			// POSITION.w: the PRT cluster offset, cluster 0 for a rebuilt vertex.
			AssertThat(HalfAt(after, buffer.VertexOffset + (v * 20) + 6)).IsEqual(Engine_MdlWriter.RebuiltClusterOffset);
		}

		AssertThat(F32At(after, 0x20)).IsEqual(-1f);
		AssertThat(F32At(after, 0x2C)).IsEqual(1f);
		float radius = F32At(after, 0x1C);
		AssertThat(radius).IsEqualApprox(MathF.Sqrt(3f), 1e-5f);
		AssertThat(F32At(after, 0x48)).IsEqualApprox(2f * radius, 1e-5f);
	}

	[TestCase]
	public void ATreeBufferIsRefusedARezizeWithItsReason()
	{
		// A fixture cannot build a tree part; the refusal is for a rigid buffer told it is skinned
		// without bones, which is the other stated reason.
		byte[] mdl = BuildMdl(new Fixture { Parts = { Triangle(skinned: true) }, Bones = new[] { "Root" }, BoneParents = new[] { -1 } });
		MdlInfo info = Parse(mdl);
		(float[] positions, float[] uvs, ushort[] indices) = Cube();
		AssertThat(Engine_MdlWriter.TryReplaceMesh(mdl, info, 0, positions, uvs, Array.Empty<float>(), Array.Empty<float>(), indices, out _, out string error)).IsFalse();
		AssertThat(error).Contains("SKINNED");
	}

	// ------------------------------------------------------------------ step 3: glTF in

	[TestCase]
	public void TheParserReadsAGlbIntoFlatArrays()
	{
		byte[] glb = BuildGlb(new GlbScene { Primitives = { CubePrimitive(material: 0) }, MaterialNames = new[] { "Paint" } });
		AssertThat(Engine_GltfReader.TryRead(glb, out GltfModel model, out string error)).OverrideFailureMessage(Msg(error)).IsTrue();
		AssertThat(model.Primitives.Count).IsEqual(1);
		GltfPrimitive p = model.Primitives[0];
		AssertThat(p.VertexCount).IsEqual(8);
		AssertThat(p.TriangleCount).IsEqual(12);
		AssertThat(p.MaterialName).IsEqual("Paint");
		AssertThat(p.Uvs.Length).IsEqual(16);
		AssertThat(p.Positions[3]).IsEqual(1f);
	}

	[TestCase]
	public void TheAxesAreTheReadersRunBackwards()
	{
		// The reader maps a model-space (x, y, z) to Godot's (x, z, -y); a glTF vertex at Godot's
		// (1, 3, -2) is therefore the model's (1, 2, 3).
		var prim = new GlbPrimitive { Positions = new float[] { 1, 3, -2, 0, 0, 0, 1, 0, 0 }, Indices = new ushort[] { 0, 1, 2 } };
		byte[] glb = BuildGlb(new GlbScene { Primitives = { prim } });
		AssertThat(Engine_MdlImport.TryFromGltf(glb, out List<MdlImportedSurface> surfaces, out string error)).OverrideFailureMessage(Msg(error)).IsTrue();
		AssertThat(surfaces[0].Positions[0]).IsEqual(1f);
		AssertThat(surfaces[0].Positions[1]).IsEqual(2f);
		AssertThat(surfaces[0].Positions[2]).IsEqual(3f);
	}

	[TestCase]
	public void AGlbBecomesTheModelsMesh()
	{
		byte[] mdl = BuildMdl(new Fixture { Parts = { Triangle() }, SecondStreamSets = 1 });
		byte[] glb = BuildGlb(new GlbScene { Primitives = { CubePrimitive() } });

		AssertThat(ModMeshOverlays.TryConvert(glb, mdl, out byte[] rebuilt, out string error, out int vertices, out int triangles))
			.OverrideFailureMessage(Msg(error)).IsTrue();
		AssertThat(vertices).IsEqual(8);
		AssertThat(triangles).IsEqual(12);

		MdlInfo info = Parse(rebuilt);
		MdlMeshBuffer buffer = info.MeshBuffers[0];
		AssertThat(buffer.VertexCount).IsEqual(8u);
		AssertThat(buffer.FaceCount).IsEqual(36u);

		// Each glTF vertex (x, y, z) lands at model (x, -z, y), and its UV follows it.
		(float[] positions, float[] uvs, ushort[] indices) = Cube();
		for (int v = 0; v < 8; v++)
		{
			int p = buffer.VertexOffset + (v * 20);
			AssertThat(HalfAt(rebuilt, p)).IsEqual(positions[v * 3]);
			AssertThat(HalfAt(rebuilt, p + 2)).IsEqual(-positions[(v * 3) + 2]);
			AssertThat(HalfAt(rebuilt, p + 4)).IsEqual(positions[(v * 3) + 1]);
			AssertThat(HalfAt(rebuilt, p + 12)).IsEqual(uvs[v * 2]);
			AssertThat(HalfAt(rebuilt, p + 14)).IsEqual(uvs[(v * 2) + 1]);
		}

		for (int i = 0; i < 36; i++)
		{
			AssertThat((rebuilt[buffer.FaceOffset + (i * 2)] << 8) | rebuilt[buffer.FaceOffset + (i * 2) + 1]).IsEqual(indices[i]);
		}

		// A glTF stating no normals gets smooth ones from its own triangles, never the default.
		int distinct = Enumerable.Range(0, 8).Select(v => U32At(rebuilt, buffer.VertexOffset + (v * 20) + 8)).Distinct().Count();
		AssertThat(distinct).IsEqual(8);
	}

	[TestCase]
	public void AMeshNodesTransformIsAppliedAndAMirrorReversesTheWinding()
	{
		var prim = new GlbPrimitive { Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Indices = new ushort[] { 0, 1, 2 } };
		byte[] moved = BuildGlb(new GlbScene { Primitives = { prim }, Translation = new double[] { 10, 0, 0 }, Scale = new double[] { 2, 2, 2 } });
		AssertThat(Engine_GltfReader.TryRead(moved, out GltfModel model, out string error)).OverrideFailureMessage(Msg(error)).IsTrue();
		AssertThat(model.Primitives[0].Positions[3]).IsEqual(12f);
		AssertThat(model.Primitives[0].Positions[7]).IsEqual(2f);
		AssertThat(model.Primitives[0].Indices).IsEqual(new uint[] { 0, 1, 2 });

		byte[] mirrored = BuildGlb(new GlbScene { Primitives = { prim }, Scale = new double[] { -1, 1, 1 } });
		AssertThat(Engine_GltfReader.TryRead(mirrored, out model, out error)).OverrideFailureMessage(Msg(error)).IsTrue();
		AssertThat(model.Primitives[0].Positions[3]).IsEqual(-1f);
		AssertThat(model.Primitives[0].Indices).IsEqual(new uint[] { 0, 2, 1 });
	}

	[TestCase]
	public void AnExternalBufferIsRefusedByName()
	{
		byte[] glb = BuildGlb(new GlbScene { Primitives = { CubePrimitive() }, External = true });
		AssertThat(Engine_GltfReader.TryRead(glb, out _, out string error)).IsFalse();
		AssertThat(error).Contains("model.bin");
	}

	[TestCase]
	public void TheMountServesAModsGlbAsTheModel()
	{
		string scratch = Path.Combine(Refii.Tests.TestTemp.Root, "refii-scratch", "mdl", "mount-" + Guid.NewGuid().ToString("N"));
		string mod = Path.Combine(scratch, "mod");
		Directory.CreateDirectory(Path.Combine(mod, "art", "models"));
		try
		{
			byte[] mdl = BuildMdl(new Fixture { Parts = { Triangle() } });
			File.WriteAllBytes(Path.Combine(mod, "art", "models", "crate.mdl.glb"), BuildGlb(new GlbScene { Primitives = { CubePrimitive() } }));
			File.WriteAllBytes(Path.Combine(mod, "art", "models", "missing.mdl.glb"), BuildGlb(new GlbScene { Primitives = { CubePrimitive() } }));

			// Built directly rather than mounted: `BnkFileSystem.Mount` also indexes the pub games,
			// which is a Godot path and no part of what is under test.
			var mounted = new BnkFileSystem();
			mounted.AddVirtualFile("art/models/crate.mdl", "<fixture>", mdl.LongLength, () => mdl);

			List<MeshConversion> conversions = ModMeshOverlays.Apply(mounted, new[] { new ModOverlay { Name = "fixture", AssetRoot = mod } });
			AssertThat(conversions.Count).IsEqual(2);
			MeshConversion crate = conversions.Single(c => c.Target.EndsWith("crate.mdl", StringComparison.Ordinal));
			AssertThat(crate.Ok).OverrideFailureMessage(Msg(crate.Error)).IsTrue();
			AssertThat(crate.VertexCount).IsEqual(8);
			MeshConversion missing = conversions.Single(c => c.Target.EndsWith("missing.mdl", StringComparison.Ordinal));
			AssertThat(missing.Ok).IsFalse();
			AssertThat(missing.Error).Contains("no 'art/models/missing.mdl' to replace");

			AssertThat(mounted.TryGetFile("art/models/crate.mdl", out BnkFile served)).IsTrue();
			AssertThat(served.SourceMod).IsEqual("fixture");
			MdlInfo info = Parse(served.ReadAllBytes());
			AssertThat(info.MeshBuffers[0].VertexCount).IsEqual(8u);
		}
		finally
		{
			Directory.Delete(scratch, recursive: true);
		}
	}

	/// <summary>A new model (models.json) is built on its template from the mount, with the template's socket file beside it.</summary>
	[TestCase]
	public void TheMountBuildsANewModelOnItsTemplate()
	{
		string scratch = Path.Combine(Refii.Tests.TestTemp.Root, "refii-scratch", "mdl", "new-" + Guid.NewGuid().ToString("N"));
		string mod = Path.Combine(scratch, "mod");
		Directory.CreateDirectory(Path.Combine(mod, "art", "mine"));
		try
		{
			byte[] mdl = BuildMdl(new Fixture { Parts = { Triangle() } });
			byte[] sockets = { 0x47, 0x61, 0x6D, 0x65, 0x4D, 0x65, 0x73, 0x68, 0, 1, 2, 3 };
			File.WriteAllBytes(Path.Combine(mod, "art", "mine", "gun.mdl.glb"), BuildGlb(new GlbScene { Primitives = { CubePrimitive() } }));
			File.WriteAllText(Path.Combine(mod, ModMeshOverlays.ModelsPath),
				"[{ \"model\": \"art/mine/gun.mdl\", \"template\": \"art/shipped/rifle.mdl\" }]");

			var mounted = new BnkFileSystem();
			mounted.AddVirtualFile("art/shipped/rifle.mdl", "<fixture>", mdl.LongLength, () => mdl);
			mounted.AddVirtualFile("art/shipped/rifle.mdl.gmd", "<fixture>", sockets.LongLength, () => sockets);

			List<MeshConversion> conversions = ModMeshOverlays.Apply(mounted, new[] { new ModOverlay { Name = "fixture", AssetRoot = mod } });
			MeshConversion gun = conversions.Single(c => c.Target == "art/mine/gun.mdl");
			AssertThat(gun.Ok).OverrideFailureMessage(Msg(gun.Error)).IsTrue();
			AssertThat(mounted.TryGetFile("art/mine/gun.mdl", out BnkFile served)).IsTrue();
			AssertThat(Parse(served.ReadAllBytes()).MeshBuffers[0].VertexCount).IsEqual(8u);
			AssertThat(mounted.TryGetFile("art/mine/gun.mdl.gmd", out BnkFile socketFile)).IsTrue();
			AssertThat(Convert.ToHexString(socketFile.ReadAllBytes())).IsEqual(Convert.ToHexString(sockets));
		}
		finally
		{
			Directory.Delete(scratch, recursive: true);
		}
	}

	// ------------------------------------------------------------------ step 4: sections and LODs

	[TestCase]
	public void TwoPrimitivesBecomeTwoSectionsAndTheMaterialTableGrows()
	{
		byte[] mdl = BuildMdl(new Fixture { Parts = { Triangle() }, Materials = 1 });
		var second = new GlbPrimitive { Positions = new float[] { 5, 5, 5, 6, 5, 5, 5, 6, 5 }, Indices = new ushort[] { 0, 1, 2 }, Material = 2 };
		byte[] glb = BuildGlb(new GlbScene { Primitives = { CubePrimitive(material: 0), second }, MaterialNames = new[] { "A", "B", "C" } });

		AssertThat(ModMeshOverlays.TryConvert(glb, mdl, out byte[] rebuilt, out string error)).OverrideFailureMessage(Msg(error)).IsTrue();
		MdlInfo info = Parse(rebuilt);
		AssertThat(info.Meshes[0].Materials.Count).IsEqual(3);
		MdlMeshBuffer buffer = info.MeshBuffers[0];
		AssertThat(buffer.VertexCount).IsEqual(11u);
		AssertThat(buffer.SubMeshes.Count).IsEqual(2);
		AssertThat(buffer.SubMeshes[0].MaterialIndex).IsEqual(0);
		AssertThat(buffer.SubMeshes[0].FaceCount).IsEqual(12u);
		AssertThat(buffer.SubMeshes[1].MaterialIndex).IsEqual(2);
		AssertThat(buffer.SubMeshes[1].StartIndex).IsEqual(36u);
		AssertThat(buffer.SubMeshes[1].FaceCount).IsEqual(1u);

		// The second section's bounds are its own triangle's, not the whole buffer's.
		AssertThat(buffer.SubMeshes[1].BoundingBox[0]).IsEqual(5f);
		AssertThat(buffer.SubMeshes[1].BoundingBox[3]).IsEqual(6f);
	}

	[TestCase]
	public void ALaterLodCollapsesOntoTheFirst()
	{
		var fixture = new Fixture { Parts = { Triangle() }, LodCount = 3, LaterLodsPresent = new[] { false, true } };
		byte[] mdl = BuildMdl(fixture);
		MdlInfo before = Parse(mdl);
		AssertThat(before.LodSizes[1]).IsEqual(0u);
		AssertThat(before.LodSizes[2]).IsGreater(0u);

		byte[] glb = BuildGlb(new GlbScene { Primitives = { CubePrimitive() } });
		AssertThat(ModMeshOverlays.TryConvert(glb, mdl, out byte[] rebuilt, out string error)).OverrideFailureMessage(Msg(error)).IsTrue();
		MdlInfo info = Parse(rebuilt);
		AssertThat(info.LodCount).IsEqual(3u);
		AssertThat(info.LodSizes[0]).IsEqual((uint)(rebuilt.Length - Engine_MdlEngine.BodyStart));
		AssertThat(info.LodSizes[1]).IsEqual(0u);
		AssertThat(info.LodSizes[2]).IsEqual(0u);
	}

	// ------------------------------------------------------------------ step 5: skinned

	private static Fixture SkinnedFixture() => new()
	{
		Parts = { Triangle(skinned: true) },
		Bones = new[] { "Rig_Asset", "Spine", "Head", "Hand_L" },
		BoneParents = new[] { -1, 0, 1, 1 },
	};

	[TestCase]
	public void ARiggedGlbSkinsToTheTemplatesBonesByName()
	{
		byte[] mdl = BuildMdl(SkinnedFixture());
		var prim = CubePrimitive();
		prim.Joints = new byte[32];
		prim.Weights = new float[32];
		for (int v = 0; v < 8; v++)
		{
			// Joint 0 is "Head", joint 1 is "Spine", joint 2 "Hand_L" — the skin's order, not the model's.
			prim.Joints[v * 4] = 0;
			prim.Joints[(v * 4) + 1] = 1;
			prim.Weights[v * 4] = v < 4 ? 1f : 0.75f;
			prim.Weights[(v * 4) + 1] = v < 4 ? 0f : 0.25f;
		}

		byte[] glb = BuildGlb(new GlbScene { Primitives = { prim }, JointNames = new[] { "Head", "Spine", "Hand_L" } });
		AssertThat(ModMeshOverlays.TryConvert(glb, mdl, out byte[] rebuilt, out string error)).OverrideFailureMessage(Msg(error)).IsTrue();

		MdlInfo info = Parse(rebuilt);
		MdlMeshBuffer buffer = info.MeshBuffers[0];
		AssertThat(buffer.IsEngineSkinned).IsTrue();
		AssertThat(buffer.VertexCount).IsEqual(8u);
		AssertThat(buffer.VertexOffset + (8 * Engine_MdlWriter.SkinnedStride)).IsEqual(buffer.FaceOffset);
		AssertThat(info.Bones.Count).IsEqual(4);
		AssertThat(info.Bones[2].Name).IsEqual("Head");

		for (int v = 0; v < 8; v++)
		{
			int p = buffer.VertexOffset + (v * 28);
			int sum = rebuilt[p + 16] + rebuilt[p + 17] + rebuilt[p + 18] + rebuilt[p + 19];
			AssertThat(sum).IsEqual(255);
			if (v < 4)
			{
				// A sole influence sits in the LAST slot, as every shipped single-influence vertex does.
				AssertThat(rebuilt[p + 15]).IsEqual((byte)2);
				AssertThat(rebuilt[p + 19]).IsEqual((byte)255);
			}
			else
			{
				AssertThat(rebuilt[p + 15]).IsEqual((byte)2);
				AssertThat(rebuilt[p + 19]).IsEqual((byte)191);
				AssertThat(rebuilt[p + 14]).IsEqual((byte)1);
				AssertThat(rebuilt[p + 18]).IsEqual((byte)64);
			}

			AssertThat(HalfAt(rebuilt, p + 20)).IsEqual(prim.Uvs![v * 2]);
		}
	}

	[TestCase]
	public void AJointTheModelHasNoBoneForIsRefusedByName()
	{
		byte[] mdl = BuildMdl(SkinnedFixture());
		var prim = CubePrimitive();
		prim.Joints = new byte[32];
		prim.Weights = new float[32];
		for (int v = 0; v < 8; v++)
		{
			prim.Weights[v * 4] = 1f;
		}

		byte[] glb = BuildGlb(new GlbScene { Primitives = { prim }, JointNames = new[] { "mixamorig:Head" } });
		AssertThat(ModMeshOverlays.TryConvert(glb, mdl, out _, out string error)).IsFalse();
		AssertThat(error).Contains("mixamorig:Head");
		AssertThat(error).Contains("Rig_Asset");
	}

	[TestCase]
	public void AnUnriggedGlbOnASkinnedModelIsRefused()
	{
		byte[] mdl = BuildMdl(SkinnedFixture());
		byte[] glb = BuildGlb(new GlbScene { Primitives = { CubePrimitive() } });
		AssertThat(ModMeshOverlays.TryConvert(glb, mdl, out _, out string error)).IsFalse();
		AssertThat(error).Contains("SKINNED");
	}

	[TestCase]
	public void PackInfluencesKeepsTheHeaviestFourSummingTo255()
	{
		var indices = new byte[4];
		var weights = new byte[4];
		var influences = new List<(int Bone, float Weight)> { (5, 0.1f), (6, 0.4f), (7, 0.3f), (8, 0.15f), (9, 0.05f) };
		ModMeshOverlays.PackInfluences(influences, indices, weights, 0);
		AssertThat(weights.Sum(w => (int)w)).IsEqual(255);
		AssertThat(indices[3]).IsEqual((byte)6);
		AssertThat(indices[2]).IsEqual((byte)7);
		AssertThat(indices[1]).IsEqual((byte)8);
		AssertThat(indices[0]).IsEqual((byte)5);
		AssertThat(weights[3]).IsGreaterEqual(weights[2]);
		AssertThat(weights[2]).IsGreaterEqual(weights[1]);
	}

	// ------------------------------------------------------------------ the shipped sweep

	/// <summary>The packaged install's data directory beside the tree; null without one.</summary>
	private static string? ShippedData()
	{
		string? env = Environment.GetEnvironmentVariable(Engine_GameData.EnvironmentVariable);
		if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "data", "Globals", "globals_model_headers.bnk")))
		{
			return Path.Combine(env, "data");
		}

		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 16 && at != null; i++)
		{
			string direct = Path.Combine(at, "assets", "Fable2", "game", "data");
			if (File.Exists(Path.Combine(direct, "Globals", "globals_model_headers.bnk")))
			{
				return direct;
			}

			string packages = Path.Combine(at, "out", "package");
			if (Directory.Exists(packages))
			{
				foreach (string package in Directory.EnumerateDirectories(packages))
				{
					string candidate = Path.Combine(package, "assets", "Fable2", "game", "data");
					if (File.Exists(Path.Combine(candidate, "Globals", "globals_model_headers.bnk")))
					{
						return candidate;
					}
				}
			}

			at = Path.GetDirectoryName(at);
		}

		return null;
	}

	private static string ModelKey(string relativePath)
	{
		string key = relativePath.Replace('\\', '/').TrimStart('/');
		if (key.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
		{
			key = key[5..];
		}

		return key.ToLowerInvariant();
	}

	private static bool IsHeader(byte[] bytes) =>
		bytes.Length >= 8 && (Encoding.ASCII.GetString(bytes, 0, 8) is "MeshFile" or "DefMeshF");

	/// <summary>
	/// Every shipped model as the mount serves it: the 104-byte header from
	/// `globals_model_headers.bnk` joined to its body from a models bank — the same concatenation
	/// `BnkFile.ReadAllBytes` makes. Banks are opened in place, nested models banks expanded
	/// one at a time, and nothing is written anywhere.
	/// </summary>
	private static IEnumerable<(string Path, byte[] Bytes)> ShippedModels(string data)
	{
		var headers = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		string headerBank = Path.Combine(data, "Globals", "globals_model_headers.bnk");
		if (BnkArchive.TryOpen(headerBank, out BnkArchive? headerArchive, out _) && headerArchive is not null)
		{
			using (headerArchive)
			{
				foreach (BnkEntry entry in headerArchive.Entries)
				{
					if (entry.RelativePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
					{
						headers[ModelKey(entry.RelativePath)] = headerArchive.ReadEntryBytes(entry);
					}
				}
			}
		}

		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (string bank in Directory.EnumerateFiles(data, "*.bnk", SearchOption.AllDirectories).OrderBy(b => b, StringComparer.Ordinal))
		{
			if (bank.EndsWith("globals_model_headers.bnk", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (!BnkArchive.TryOpen(bank, out BnkArchive? archive, out _) || archive is null)
			{
				continue;
			}

			using (archive)
			{
				foreach ((string path, byte[] bytes) in ModelsIn(archive, headers, seen, 0))
				{
					yield return (path, bytes);
				}
			}
		}

		// Headers no body was found for: the mount serves them alone, and so does this.
		foreach ((string key, byte[] header) in headers)
		{
			if (seen.Add(key))
			{
				yield return (key, header);
			}
		}
	}

	private static IEnumerable<(string Path, byte[] Bytes)> ModelsIn(BnkArchive archive, Dictionary<string, byte[]> headers, HashSet<string> seen, int depth)
	{
		foreach (BnkEntry entry in archive.Entries)
		{
			string name = entry.RelativePath;
			if (name.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
			{
				// Only a models bank can hold a body; a nested textures bank is hundreds of megabytes of nothing here.
				if (depth < 3 && Path.GetFileName(name).Contains("model", StringComparison.OrdinalIgnoreCase))
				{
					byte[] nested = archive.ReadEntryBytes(entry);
					if (BnkArchive.TryOpenBytes(name, nested, out BnkArchive? inner, out _) && inner is not null)
					{
						using (inner)
						{
							foreach ((string path, byte[] bytes) in ModelsIn(inner, headers, seen, depth + 1))
							{
								yield return (path, bytes);
							}
						}
					}
				}

				continue;
			}

			if (!name.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			string key = ModelKey(name);
			byte[] body = archive.ReadEntryBytes(entry);
			if (IsHeader(body))
			{
				// A bare header is joined to a body; an entry already carrying its body is whole.
				if (body.Length <= Engine_MdlEngine.BodyStart)
				{
					headers.TryAdd(key, body);
				}
				else if (seen.Add(key))
				{
					yield return (key, body);
				}

				continue;
			}

			if (!headers.TryGetValue(key, out byte[]? header) || !seen.Add(key))
			{
				continue;
			}

			var joined = new byte[header.Length + body.Length];
			header.CopyTo(joined, 0);
			body.CopyTo(joined, header.Length);
			yield return (key, joined);
		}
	}

	/// <summary>Every rigid or skinned buffer of a model, decoded the way the geometry reader decodes it.</summary>
	private static MdlGeometryEdit Decode(byte[] mdl, MdlInfo info, int b)
	{
		MdlMeshBuffer buffer = info.MeshBuffers[b];
		int stride = Engine_MdlWriter.StrideFor(buffer);
		var edit = new MdlGeometryEdit { BufferIndex = b, Positions = new float[buffer.VertexCount * 3], Uvs = new float[buffer.VertexCount * 2], Normals = new float[buffer.VertexCount * 3], Tangents = new float[buffer.VertexCount * 4], Indices = new ushort[buffer.FaceCount] };
		for (int v = 0; v < buffer.VertexCount; v++)
		{
			int p = buffer.VertexOffset + (v * stride);
			for (int c = 0; c < 3; c++)
			{
				edit.Positions[(v * 3) + c] = HalfAt(mdl, p + (c * 2));
			}

			(edit.Normals[v * 3], edit.Normals[(v * 3) + 1], edit.Normals[(v * 3) + 2]) = MdlReader.DecodeNormal_11_11_10(U32At(mdl, p + 8));
			int u = p + Engine_MdlWriter.UvOffsetFor(buffer);
			edit.Uvs[v * 2] = HalfAt(mdl, u);
			edit.Uvs[(v * 2) + 1] = HalfAt(mdl, u + 2);
			(edit.Tangents[v * 4], edit.Tangents[(v * 4) + 1], edit.Tangents[(v * 4) + 2], edit.Tangents[(v * 4) + 3]) = MdlReader.DecodeTangent_DEC3N(U32At(mdl, p + Engine_MdlWriter.TangentOffsetFor(buffer)));
		}

		for (int i = 0; i < buffer.FaceCount; i++)
		{
			edit.Indices[i] = (ushort)((mdl[buffer.FaceOffset + (i * 2)] << 8) | mdl[buffer.FaceOffset + (i * 2) + 1]);
		}

		return edit;
	}

	/// <summary>A buffer's triangles as one list: its list sections verbatim, its strips unrolled.</summary>
	private static ushort[] TriangleList(MdlMeshBuffer buffer, ushort[] indices)
	{
		var list = new List<ushort>();
		foreach (MdlSubMesh section in buffer.SubMeshes)
		{
			int start = (int)section.StartIndex;
			if (section.PrimitiveType == 0)
			{
				int count = (int)section.FaceCount * 3;
				for (int i = start; i < start + count && i < indices.Length; i++)
				{
					list.Add(indices[i]);
				}
			}
			else
			{
				int count = (int)section.FaceCount + 2;
				var strip = new List<ushort>();
				for (int i = start; i < start + count && i < indices.Length; i++)
				{
					strip.Add(indices[i]);
				}

				var unrolled = new List<uint>();
				MdlReader.BuildTrianglesFromStrip(strip, unrolled);
				foreach (uint i in unrolled)
				{
					list.Add((ushort)i);
				}
			}
		}

		return list.ToArray();
	}

	[TestCase(Timeout = 1_800_000)]
	public void EveryShippedModelReEmitsAndResizesThroughTheWriter()
	{
		string? data = ShippedData();
		if (data is null)
		{
			Console.WriteLine("[mdl] no packaged install beside the tree; the shipped sweep is not run here");
			return;
		}

		int names = 0;
		int headersOnly = 0, unparsed = 0, parsed = 0;
		int buffers = 0, sameCountIdentical = 0, sameCountDrift = 0, sameCountFailed = 0;
		int positionsExact = 0, uvsExact = 0, indicesExact = 0;
		int inline = 0, inlineMiss = 0;
		int resized = 0, resizedCorrect = 0, resizedWrong = 0;
		var refused = new SortedDictionary<string, int>(StringComparer.Ordinal);
		var resizeWrong = new List<string>();
		var sameCountWrong = new List<string>();
		var word6Rigid = new SortedDictionary<ushort, long>();
		var word6Skinned = new SortedDictionary<ushort, long>();
		var uvMismatch = new List<string>();
		int uvNaNKept = 0;
		int word6Uniform = 0, word6Mixed = 0;
		// POSITION.w against the model's own PRT descriptor: a cluster offset of
		// cluster x (pca x channels + 4) / 4 vec4s, cluster < clusters; 0 where there is no transfer.
		long word6InCluster = 0, word6Outside = 0, word6NoPrtZero = 0, word6NoPrtNonZero = 0;
		var prtClusterCounts = new SortedDictionary<int, int>();
		var word6Bad = new List<string>();
		long multiInfluence = 0, heaviestLast = 0, heaviestFirst = 0;
		long singleInfluence = 0, singleInSlot3 = 0;
		var markers = new SortedDictionary<uint, int>();

		foreach ((string path, byte[] mdl) in ShippedModels(data))
		{
			names++;
			if (!Engine_MdlData.TryParseInfo(mdl, path, out MdlInfo info, out _))
			{
				unparsed++;
				continue;
			}

			if (info.IsStub)
			{
				headersOnly++;
				continue;
			}

			if (!info.IsEngineFormat)
			{
				unparsed++;
				continue;
			}

			parsed++;

			for (int b = 0; b < info.MeshBuffers.Count; b++)
			{
				MdlMeshBuffer buffer = info.MeshBuffers[b];
				if (buffer.IsEngineTree || buffer.IsAltPath || buffer.IsFoliagePath || buffer.SharesLod0Vertices)
				{
					continue;
				}

				buffers++;
				int stride = Engine_MdlWriter.StrideFor(buffer);
				if (buffer.VertexOffset + ((long)buffer.VertexCount * stride) == buffer.FaceOffset)
				{
					inline++;
				}
				else
				{
					inlineMiss++;
				}

				foreach (MdlSubMesh section in buffer.SubMeshes)
				{
					markers[section.RegionIndex] = markers.GetValueOrDefault(section.RegionIndex) + 1;
				}

				uint[] prt = info.ShDescriptor;
				int prtClusters = prt.Length >= 5 ? (int)prt[2] : 0;
				int prtBlock = prt.Length >= 5 ? (int)((prt[3] * prt[1] + 4) / 4) : 0;
				prtClusterCounts[prtClusters] = prtClusterCounts.GetValueOrDefault(prtClusters) + 1;

				ushort word6First = 0;
				bool word6Same = true;
				for (int v = 0; v < buffer.VertexCount; v++)
				{
					int p = buffer.VertexOffset + (v * stride);
					var word6 = (ushort)((mdl[p + 6] << 8) | mdl[p + 7]);
					SortedDictionary<ushort, long> word6Census = buffer.IsEngineSkinned ? word6Skinned : word6Rigid;
					word6Census[word6] = word6Census.GetValueOrDefault(word6) + 1;

					float w = MdlReader.HalfToFloat(word6);
					if (prtClusters == 0)
					{
						if (w == 0f) word6NoPrtZero++; else word6NoPrtNonZero++;
						if (w != 0f && word6Bad.Count < 6) word6Bad.Add($"{path}[{b}] vertex {v}: w {w} with no transfer");
					}
					else
					{
						int cluster = prtBlock > 0 && w >= 0f ? (int)w / prtBlock : -1;
						bool stated = cluster >= 0 && cluster < prtClusters && (float)(cluster * prtBlock) == w;
						if (stated) word6InCluster++; else word6Outside++;
						if (!stated && word6Bad.Count < 6) word6Bad.Add($"{path}[{b}] vertex {v}: w {w} against {prtClusters} clusters x {prtBlock} vec4s");
					}

					if (v == 0)
					{
						word6First = word6;
						word6Same = true;
					}
					else if (word6 != word6First)
					{
						word6Same = false;
					}

					if (buffer.IsEngineSkinned)
					{
						int nonZero = 0;
						int heaviest = -1;
						for (int k = 0; k < 4; k++)
						{
							if (mdl[p + 16 + k] != 0)
							{
								nonZero++;
								if (heaviest < 0 || mdl[p + 16 + k] > mdl[p + 16 + heaviest])
								{
									heaviest = k;
								}
							}
						}

						if (nonZero == 1)
						{
							singleInfluence++;
							if (heaviest == 3)
							{
								singleInSlot3++;
							}
						}
						else if (nonZero > 1)
						{
							multiInfluence++;
							if (heaviest == 3)
							{
								heaviestLast++;
							}
							else if (heaviest == 0)
							{
								heaviestFirst++;
							}
						}
					}
				}

				if (word6Same) word6Uniform++; else word6Mixed++;

				MdlGeometryEdit edit = Decode(mdl, info, b);
				if (!Engine_MdlWriter.TryReplaceGeometry(mdl, info, edit, out byte[] back, out string error))
				{
					sameCountFailed++;
					if (sameCountWrong.Count < 10)
					{
						sameCountWrong.Add($"{path}[{b}]: {error}");
					}

					continue;
				}

				bool pos = true, uv = true, idx = true, all = back.Length == mdl.Length;
				for (int v = 0; v < buffer.VertexCount && (pos || uv); v++)
				{
					int p = buffer.VertexOffset + (v * stride);
					for (int k = 0; k < 6; k++)
					{
						if (back[p + k] != mdl[p + k])
						{
							pos = false;
						}
					}

					int u = p + Engine_MdlWriter.UvOffsetFor(buffer);
					for (int k = 0; k < 4; k += 2)
					{
						if (back[u + k] != mdl[u + k] || back[u + k + 1] != mdl[u + k + 1])
						{
							// A NaN half comes back a NaN half; its payload is not kept, and that is all.
							bool wasNaN = (mdl[u + k] & 0x7C) == 0x7C && ((mdl[u + k] & 0x03) != 0 || mdl[u + k + 1] != 0);
							bool isNaN = (back[u + k] & 0x7C) == 0x7C && ((back[u + k] & 0x03) != 0 || back[u + k + 1] != 0);
							if (wasNaN && isNaN)
							{
								uvNaNKept++;
								continue;
							}

							if (uv && uvMismatch.Count < 6)
							{
								uvMismatch.Add($"{path}[{b}] vertex {v}: uv bytes {mdl[u]:X2}{mdl[u + 1]:X2} {mdl[u + 2]:X2}{mdl[u + 3]:X2} -> {back[u]:X2}{back[u + 1]:X2} {back[u + 2]:X2}{back[u + 3]:X2}");
							}

							uv = false;
						}
					}
				}

				for (int i = 0; i < buffer.FaceCount * 2 && idx; i++)
				{
					if (back[buffer.FaceOffset + i] != mdl[buffer.FaceOffset + i])
					{
						idx = false;
					}
				}

				if (all)
				{
					for (int i = 0; i < mdl.Length; i++)
					{
						if (back[i] != mdl[i])
						{
							all = false;
							break;
						}
					}
				}

				if (pos) positionsExact++;
				if (uv) uvsExact++;
				if (idx) indicesExact++;
				if (all) sameCountIdentical++; else sameCountDrift++;
				if (!pos || !uv || !idx)
				{
					if (sameCountWrong.Count < 10)
					{
						sameCountWrong.Add($"{path}[{b}]: positions {pos} uvs {uv} indices {idx}");
					}
				}
			}

			// RESIZE: the model's own LOD-0 first buffer as a triangle list, written back through
			// the topology path, then re-read.
			if (info.MeshBuffers.Count == 0)
			{
				continue;
			}

			MdlMeshBuffer first = info.MeshBuffers[0];
			if (first.IsEngineTree || first.IsAltPath || first.IsFoliagePath)
			{
				refused["(tree or legacy first buffer)"] = refused.GetValueOrDefault("(tree or legacy first buffer)") + 1;
				continue;
			}

			MdlGeometryEdit own = Decode(mdl, info, 0);
			ushort[] triangles = TriangleList(first, own.Indices);
			if (triangles.Length == 0)
			{
				refused["(no triangles)"] = refused.GetValueOrDefault("(no triangles)") + 1;
				continue;
			}

			byte[]? boneIndices = null;
			byte[]? boneWeights = null;
			if (first.IsEngineSkinned)
			{
				boneIndices = new byte[first.VertexCount * 4];
				boneWeights = new byte[first.VertexCount * 4];
				for (int v = 0; v < first.VertexCount; v++)
				{
					int p = first.VertexOffset + (v * 28);
					Array.Copy(mdl, p + 12, boneIndices, v * 4, 4);
					Array.Copy(mdl, p + 16, boneWeights, v * 4, 4);
				}
			}

			if (!Engine_MdlWriter.TryReplaceMesh(mdl, info, 0, own.Positions, own.Uvs, own.Normals, own.Tangents, triangles, null, boneIndices, boneWeights, out byte[] resizedBytes, out string refusal))
			{
				string key = refusal.Length > 60 ? refusal[..60] : refusal;
				refused[key] = refused.GetValueOrDefault(key) + 1;
				continue;
			}

			resized++;
			var after = new MdlInfo();
			string wrong = string.Empty;
			if (!Engine_MdlEngine.TryParse(resizedBytes, after))
			{
				wrong = "does not reparse";
			}
			else
			{
				MdlMeshBuffer re = after.MeshBuffers.Count > 0 ? after.MeshBuffers[0] : null!;
				if (re is null) wrong = "no buffer";
				else if (re.VertexCount != first.VertexCount) wrong = $"vertex count {re.VertexCount} != {first.VertexCount}";
				else if (re.FaceCount != (uint)triangles.Length) wrong = $"index count {re.FaceCount} != {triangles.Length}";
				else if (re.SubMeshes.Count != 1 || re.SubMeshes[0].FaceCount != (uint)(triangles.Length / 3)) wrong = "section";
				else if (after.LodSizes[0] != (uint)(resizedBytes.Length - Engine_MdlEngine.BodyStart)) wrong = "lod size";
				else if (after.MeshBuffers.Count != info.MeshBuffers.Count) wrong = $"buffer count {after.MeshBuffers.Count} != {info.MeshBuffers.Count}";
				else if (after.Bones.Count != info.Bones.Count) wrong = "bones";
				else if (after.Meshes.Count != info.Meshes.Count) wrong = "meshes";
				else if (after.IsEngineFormat != true) wrong = "not engine";
				else
				{
					for (int v = 0; v < first.VertexCount && wrong.Length == 0; v++)
					{
						int p = re.VertexOffset + (v * Engine_MdlWriter.StrideFor(re));
						int q = first.VertexOffset + (v * Engine_MdlWriter.StrideFor(first));
						for (int k = 0; k < 6; k++)
						{
							if (resizedBytes[p + k] != mdl[q + k])
							{
								wrong = $"vertex {v} position differs";
								break;
							}
						}

						// A rebuilt vertex states PRT cluster 0 in POSITION.w.
						if (wrong.Length == 0 && (resizedBytes[p + 6] != 0 || resizedBytes[p + 7] != 0))
						{
							wrong = $"vertex {v} states a PRT cluster offset";
						}

						if (first.IsEngineSkinned)
						{
							for (int k = 12; k < 20; k++)
							{
								if (resizedBytes[p + k] != mdl[q + k])
								{
									wrong = $"vertex {v} bones differ";
									break;
								}
							}
						}
					}

					// Every OTHER buffer's bytes are carried: compare its vertex region.
					for (int b = 1; b < info.MeshBuffers.Count && wrong.Length == 0; b++)
					{
						MdlMeshBuffer o = info.MeshBuffers[b];
						MdlMeshBuffer n = after.MeshBuffers[b];
						if (o.VertexCount != n.VertexCount || o.FaceCount != n.FaceCount)
						{
							wrong = $"buffer {b} counts changed";
							break;
						}

						if (o.SharesLod0Vertices || o.IsAltPath || o.IsFoliagePath)
						{
							continue;
						}

						int st = Engine_MdlWriter.StrideFor(o);
						long len = Math.Min((long)o.VertexCount * st, 4096);
						for (int k = 0; k < len; k++)
						{
							if (resizedBytes[n.VertexOffset + k] != mdl[o.VertexOffset + k])
							{
								wrong = $"buffer {b} vertices differ";
								break;
							}
						}
					}
				}
			}

			if (wrong.Length == 0)
			{
				resizedCorrect++;
			}
			else
			{
				resizedWrong++;
				if (resizeWrong.Count < 10)
				{
					resizeWrong.Add($"{path}: {wrong}");
				}
			}
		}

		var report = new StringBuilder();
		report.AppendLine($"[mdl] {names} .mdl names in the banks: {parsed} engine models with a body, {headersOnly} headers only, {unparsed} not parsed");
		report.AppendLine($"[mdl] same-count re-emit over {buffers} rigid+skinned buffers: {sameCountIdentical} whole-file byte-identical, {sameCountDrift} with normal/tangent requantisation drift, {sameCountFailed} refused");
		report.AppendLine($"[mdl]   positions exact {positionsExact}/{buffers}, uvs exact {uvsExact}/{buffers}, indices exact {indicesExact}/{buffers}");
		report.AppendLine($"[mdl]   inline geometry (vertexOffset + count x stride == faceOffset): {inline}/{buffers} ({inlineMiss} not)");
		report.AppendLine($"[mdl]   uv NaN halves kept as NaN (payload not reproduced): {uvNaNKept}");
		report.AppendLine($"[mdl]   vertex word +6 uniform within a buffer: {word6Uniform}, mixed: {word6Mixed}");
		report.AppendLine($"[mdl]   rigid vertex word +6: {string.Join(", ", word6Rigid.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"0x{kv.Key:X4}={kv.Value}"))} ({word6Rigid.Count} distinct)");
		report.AppendLine($"[mdl]   skinned vertex word +6: {string.Join(", ", word6Skinned.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"0x{kv.Key:X4}={kv.Value}"))} ({word6Skinned.Count} distinct)");
		report.AppendLine($"[mdl]   POSITION.w as the PRT cluster offset: {word6InCluster} within the model's descriptor, {word6Outside} outside; no transfer: {word6NoPrtZero} zero, {word6NoPrtNonZero} non-zero; buffers by cluster count: {string.Join(", ", prtClusterCounts.Select(kv => $"{kv.Key}={kv.Value}"))}");
		foreach (string line in word6Bad)
		{
			report.AppendLine($"[mdl]   POSITION.w not a stated cluster: {line}");
		}
		foreach (string line in uvMismatch)
		{
			report.AppendLine($"[mdl]   uv not reproduced: {line}");
		}

		report.AppendLine($"[mdl]   skinned: single-influence {singleInfluence} ({singleInSlot3} in slot 3); multi-influence {multiInfluence} (heaviest in slot 3: {heaviestLast}, in slot 0: {heaviestFirst})");
		report.AppendLine($"[mdl]   section hide-region words: none (0xFFFFFFFF) {markers.GetValueOrDefault(uint.MaxValue)}, a region {markers.Where(kv => kv.Key != uint.MaxValue).Sum(kv => kv.Value)} over {markers.Count - (markers.ContainsKey(uint.MaxValue) ? 1 : 0)} distinct indices");
		report.AppendLine($"[mdl] resize (own LOD-0 first buffer as a list) over {parsed} models: {resized} written, {resizedCorrect} correct, {resizedWrong} wrong");
		foreach ((string reason, int count) in refused)
		{
			report.AppendLine($"[mdl]   refused {count}: {reason}");
		}

		foreach (string line in sameCountWrong)
		{
			report.AppendLine($"[mdl]   same-count wrong: {line}");
		}

		foreach (string line in resizeWrong)
		{
			report.AppendLine($"[mdl]   resize wrong: {line}");
		}

		Console.WriteLine(report.ToString());

		AssertThat(names).OverrideFailureMessage("no .mdl in the banks").IsGreater(0);
		AssertThat(positionsExact).OverrideFailureMessage(report.ToString()).IsEqual(buffers);
		AssertThat(uvsExact).OverrideFailureMessage(report.ToString()).IsEqual(buffers);
		AssertThat(indicesExact).OverrideFailureMessage(report.ToString()).IsEqual(buffers);
		AssertThat(sameCountFailed).OverrideFailureMessage(report.ToString()).IsEqual(0);
		AssertThat(resizedWrong).OverrideFailureMessage(report.ToString()).IsEqual(0);
		AssertThat(word6Outside + word6NoPrtNonZero).OverrideFailureMessage(report.ToString()).IsEqual(0L);
	}
}
