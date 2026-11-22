namespace Refii.Tests;

using System;
using System.Collections.Generic;
using GdUnit4;
using Godot;
using Refii.Models;
using static GdUnit4.Assertions;

/// <summary>
/// The fast paths of building a model's mesh against the plain forms they stand in for, and a
/// coarser level over LOD 0's vertices, whose vertices are decoded only when one is appended.
/// </summary>
[TestSuite]
public class MeshBuildTests
{
	/// <summary>The conversion as written out case by case.</summary>
	private static uint ReferenceHalfBits(ushort h)
	{
		uint s = (uint)((h >> 15) & 1);
		uint e = (uint)((h >> 10) & 0x1F);
		uint f = (uint)(h & 0x3FF);
		uint bigE, bigF;
		if (e == 0)
		{
			if (f == 0)
			{
				bigE = 0;
				bigF = 0;
			}
			else
			{
				int t = 0;
				while ((f & 0x400) == 0)
				{
					f <<= 1;
					++t;
				}

				f &= 0x3FF;
				bigE = (uint)(127 - 14 - t);
				bigF = f << 13;
			}
		}
		else if (e == 31)
		{
			bigE = 255;
			bigF = f != 0 ? 0x7FFFFFu : 0u;
		}
		else
		{
			bigE = e + (127 - 15);
			bigF = f << 13;
		}

		return (s << 31) | (bigE << 23) | bigF;
	}

	[TestCase]
	public void EveryHalfConvertsToTheSameBits()
	{
		for (int h = 0; h <= ushort.MaxValue; h++)
		{
			if (BitConverter.SingleToUInt32Bits(MdlReader.HalfToFloat((ushort)h)) != ReferenceHalfBits((ushort)h))
			{
				AssertThat(h).OverrideFailureMessage($"half {h:X4}").IsEqual(-1);
			}
		}
	}

	[TestCase]
	public void SnormTablesAreTheDecodersLanes()
	{
		for (uint i = 0; i < 2048; i++)
		{
			(float x, float y, _) = MdlReader.DecodeNormal_11_11_10(i | (i << 11));
			AssertThat(BitConverter.SingleToUInt32Bits(MdlReader.Snorm11[i])).IsEqual(BitConverter.SingleToUInt32Bits(x));
			AssertThat(BitConverter.SingleToUInt32Bits(MdlReader.Snorm11[i])).IsEqual(BitConverter.SingleToUInt32Bits(y));
		}

		for (uint i = 0; i < 1024; i++)
		{
			(_, _, float nz) = MdlReader.DecodeNormal_11_11_10(i << 22);
			(float tx, float ty, float tz, _) = MdlReader.DecodeTangent_DEC3N(i | (i << 10) | (i << 20));
			uint expected = BitConverter.SingleToUInt32Bits(MdlReader.Snorm10[i]);
			AssertThat(BitConverter.SingleToUInt32Bits(nz)).IsEqual(expected);
			AssertThat(BitConverter.SingleToUInt32Bits(tx)).IsEqual(expected);
			AssertThat(BitConverter.SingleToUInt32Bits(ty)).IsEqual(expected);
			AssertThat(BitConverter.SingleToUInt32Bits(tz)).IsEqual(expected);
		}
	}

	/// <summary>The strip walk as it reads against the section's words.</summary>
	private static List<uint> ReferenceStrip(ushort[] strip)
	{
		var indices = new List<uint>();
		if (strip.Length < 3)
		{
			return indices;
		}

		const ushort restart = 0xFFFF;
		bool wind = false;
		ushort a = strip[0], b = strip[1];
		for (int i = 2; i < strip.Length; ++i)
		{
			ushort c = strip[i];
			if (a == restart || b == restart || c == restart)
			{
				int j = i + 1;
				while (j < strip.Length && strip[j] == restart)
				{
					++j;
				}

				if (j + 1 < strip.Length)
				{
					a = strip[j];
					b = strip[j + 1];
					i = j + 1;
					wind = false;
					continue;
				}

				break;
			}

			if (a != b && b != c && c != a)
			{
				indices.AddRange(wind ? new uint[] { b, a, c } : new uint[] { a, b, c });
			}

			a = b;
			b = c;
			wind = !wind;
		}

		return indices;
	}

	[TestCase]
	public void StripsUnrollAsTheyDid()
	{
		var random = new Random(4177);
		for (int run = 0; run < 2000; run++)
		{
			var strip = new ushort[random.Next(0, 80)];
			for (int i = 0; i < strip.Length; i++)
			{
				int pick = random.Next(100);
				strip[i] = pick < 8 ? (ushort)0xFFFF : (ushort)random.Next(0, 12);
			}

			List<uint> expected = ReferenceStrip(strip);

			var viaList = new List<uint> { 99 };
			MdlReader.BuildTrianglesFromStrip(new List<ushort>(strip), viaList);
			AssertThat(viaList).IsEqual(expected);

			var written = new uint[strip.Length * 3];
			int count = MdlReader.BuildTrianglesFromStrip((ReadOnlySpan<ushort>)strip, written);
			AssertThat(new List<uint>(written.AsSpan(0, count).ToArray())).IsEqual(expected);
		}
	}

	[TestCase]
	public void StringsStopWhereTheyDid()
	{
		byte[] data = { (byte)'a', 0xE9, 0, 0, (byte)'b', (byte)'c', (byte)'d' };
		var reader = new MdlReader(data);

		AssertThat(reader.Strz(out string first)).IsTrue();
		AssertThat(first).IsEqual("aé");
		AssertThat(reader.Position).IsEqual(3);

		AssertThat(reader.Strz(out string empty)).IsTrue();
		AssertThat(empty).IsEqual(string.Empty);
		AssertThat(reader.Position).IsEqual(4);

		// The limit cuts a string short, and leaves the reader there.
		AssertThat(reader.Strz(out string limited, 2)).IsTrue();
		AssertThat(limited).IsEqual("bc");
		AssertThat(reader.Position).IsEqual(6);

		// So does the end of the data.
		AssertThat(reader.Strz(out string last)).IsTrue();
		AssertThat(last).IsEqual("d");
		AssertThat(reader.Position).IsEqual(7);

		AssertThat(reader.Strz(out string none)).IsTrue();
		AssertThat(none).IsEqual(string.Empty);
		AssertThat(reader.Position).IsEqual(7);
	}

	private static void PutU16(byte[] data, int at, int value)
	{
		data[at] = (byte)(value >> 8);
		data[at + 1] = (byte)value;
	}

	/// <summary>
	/// A rigid model of four vertices, two submeshes of one triangle each over a shared buffer,
	/// and a coarser level whose one triangle takes vertices from both.
	/// </summary>
	private static MdlInfo SharedLevelModel(out byte[] data)
	{
		const int Vertices = 4, Stride = 20, Words = 6, LevelWords = 3;
		int faceOffset = Vertices * Stride;
		int levelOffset = faceOffset + (Words * 2);
		data = new byte[levelOffset + (LevelWords * 2)];

		// Every position lane and the UV's u are 1+v, as halves.
		ushort[] one = { 0x3C00, 0x4000, 0x4200, 0x4400 };
		for (int v = 0; v < Vertices; v++)
		{
			int p = v * Stride;
			PutU16(data, p, one[v]);
			PutU16(data, p + 2, one[v]);
			PutU16(data, p + 4, one[v]);
			PutU16(data, p + 12, one[v]);
		}

		int at = faceOffset;
		foreach (int index in new[] { 0, 1, 2, 2, 1, 3 })
		{
			PutU16(data, at, index);
			at += 2;
		}

		foreach (int index in new[] { 0, 1, 3 })
		{
			PutU16(data, at, index);
			at += 2;
		}

		var material = new MdlMaterial { Diffuse = "a.tex" };
		var other = new MdlMaterial { Diffuse = "b.tex" };
		var mesh = new MdlMesh { Name = "m" };
		mesh.Materials.Add(material);
		mesh.Materials.Add(other);

		var lod0 = new MdlMeshBuffer
		{
			VertexCount = Vertices, VertexOffset = 0, FaceCount = Words, FaceOffset = faceOffset, IsEngineRigid = true,
		};
		lod0.SubMeshes.Add(new MdlSubMesh { StartIndex = 0, FaceCount = 1, MaterialIndex = 0, RegionIndex = uint.MaxValue });
		lod0.SubMeshes.Add(new MdlSubMesh { StartIndex = 3, FaceCount = 1, MaterialIndex = 1, RegionIndex = uint.MaxValue });

		var coarse = new MdlMeshBuffer
		{
			VertexCount = Vertices, VertexOffset = 0, FaceCount = LevelWords, FaceOffset = levelOffset,
			IsEngineRigid = true, SharesLod0Vertices = true, SharedVertexBase = 0,
		};
		coarse.SubMeshes.Add(new MdlSubMesh { StartIndex = 0, FaceCount = 1, MaterialIndex = 0, RegionIndex = uint.MaxValue });

		var level = new MdlLod();
		var levelMesh = new MdlMesh { Name = "m" };
		levelMesh.Materials.Add(new MdlMaterial { Diffuse = "a.tex" });
		levelMesh.Materials.Add(new MdlMaterial { Diffuse = "b.tex" });
		level.Meshes.Add(levelMesh);
		level.MeshBuffers.Add(coarse);

		var info = new MdlInfo { LodReference = 1f };
		info.Meshes.Add(mesh);
		info.MeshBuffers.Add(lod0);
		info.Lods.Add(level);
		return info;
	}

	[TestCase]
	public void ALevelOverLod0AppendsOnlyTheVertexItLacks()
	{
		MdlInfo info = SharedLevelModel(out byte[] data);
		AssertThat(Engine_MdlGeometry.TryBuild(data, info, out List<MdlGeometry> surfaces)).IsTrue();
		AssertThat(surfaces.Count).IsEqual(2);
		AssertThat(surfaces[0].VertexCount).IsEqual(3);

		// A level of one triangle is not coarser than a surface of one triangle in Godot's eyes,
		// but Attach only compares keys, which is what is under test here.
		(int attached, int dropped) = Engine_MdlLods.Attach(data, info, surfaces);
		AssertThat(attached).IsEqual(1);
		AssertThat(dropped).IsEqual(0);

		MdlGeometry surface = surfaces[0];
		AssertThat(surface.VertexCount).IsEqual(4);
		AssertThat(surface.Lods.Count).IsEqual(1);
		AssertThat(new List<uint>(surface.Lods[0].Indices)).IsEqual(new List<uint> { 0, 1, 3 });
		AssertThat(new List<int>(surface.SourceVertices)).IsEqual(new List<int> { 0, 1, 2, 3 });

		// The appended vertex is vertex 3 of the buffer, in Y-up: (4, 4, 4) -> (4, 4, -4).
		AssertThat(surface.Positions[9]).IsEqual(4f);
		AssertThat(surface.Positions[10]).IsEqual(4f);
		AssertThat(surface.Positions[11]).IsEqual(-4f);
		AssertThat(surface.Uvs[6]).IsEqual(4f);
		AssertThat(surface.Normals.Length).IsEqual(12);
		AssertThat(surface.Tangents.Length).IsEqual(16);

		// The other surface was not touched.
		AssertThat(surfaces[1].VertexCount).IsEqual(3);
		AssertThat(surfaces[1].Lods.Count).IsEqual(0);
	}

	private static ArrayMesh PartWithTwoSurfaces()
	{
		var part = new ArrayMesh();
		for (int s = 0; s < 2; s++)
		{
			var arrays = new Godot.Collections.Array();
			arrays.Resize((int)Mesh.ArrayType.Max);
			arrays[(int)Mesh.ArrayType.Vertex] = new[] { Vector3.Zero, Vector3.Right, Vector3.Up };
			arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2 };
			Engine_MdlSurfaceArrays.Add(part, Mesh.PrimitiveType.Triangles, arrays, null);
		}

		return part;
	}

	[TestCase]
	[RequireGodotRuntime]
	public void CompositeSurfacesCarryTheirPartsTransfer()
	{
		ArrayMesh part = PartWithTwoSurfaces();
		part.SetMeta(Engine_MdlTransfer.DescriptorMeta, new[] { 4, 3, 1, 1, 1 });
		part.SetMeta(Engine_MdlTransfer.TableMeta, new[] { 0.5f, 0.25f });
		ArrayMesh plain = PartWithTwoSurfaces();

		ArrayMesh? composite = Engine_MdlComposite.Build(
			new[] { "a.mdl", "b.mdl" }, path => path == "a.mdl" ? part : plain, out int resolved, out int missing);

		AssertThat(composite).IsNotNull();
		AssertThat(resolved).IsEqual(2);
		AssertThat(missing).IsEqual(0);
		Godot.Collections.Array descriptors = composite!.GetMeta(Engine_MdlTransfer.SurfaceDescriptorsMeta).AsGodotArray();
		Godot.Collections.Array tables = composite.GetMeta(Engine_MdlTransfer.SurfaceTablesMeta).AsGodotArray();
		AssertThat(descriptors.Count).IsEqual(4);
		AssertThat(tables.Count).IsEqual(4);
		for (int s = 0; s < 4; s++)
		{
			AssertThat(descriptors[s].VariantType).IsEqual(Variant.Type.PackedInt32Array);
			AssertThat(tables[s].VariantType).IsEqual(Variant.Type.PackedFloat32Array);
			AssertThat(descriptors[s].AsInt32Array().Length).IsEqual(s < 2 ? 5 : 0);
			AssertThat(tables[s].AsFloat32Array().Length).IsEqual(s < 2 ? 2 : 0);
		}

		// One part without transfer of its own says nothing of the other's, and none at all sets no metadata.
		ArrayMesh? none = Engine_MdlComposite.Build(new[] { "b.mdl" }, _ => plain, out _, out _);
		AssertThat(none!.HasMeta(Engine_MdlTransfer.SurfaceDescriptorsMeta)).IsFalse();
	}

	/// <summary>
	/// A composite reads a morphed part through its record, so the record must hold the moved
	/// vertices: with the part's own metadata copied over it, every target was listed and nothing
	/// moved (the hero's horns stayed inside the head).
	/// </summary>
	[TestCase]
	[RequireGodotRuntime]
	public void AMorphedPartRecordsItsMovedVertices()
	{
		static ArrayMesh Triangle(Vector3 apex)
		{
			var mesh = new ArrayMesh();
			var arrays = new Godot.Collections.Array();
			arrays.Resize((int)Mesh.ArrayType.Max);
			arrays[(int)Mesh.ArrayType.Vertex] = new[] { Vector3.Zero, Vector3.Right, apex };
			arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2 };
			Engine_MdlSurfaceArrays.Add(mesh, Mesh.PrimitiveType.Triangles, arrays, null);
			return mesh;
		}

		ArrayMesh own = Triangle(Vector3.Up);
		own.SetMeta("mdl_marker", 7);
		ArrayMesh target = Triangle(Vector3.Up * 3f);

		ArrayMesh blended = Refii.Hero.Engine_HeroMorph.Blend(own, new[] { (target, 1f) });

		AssertThat(ReferenceEquals(blended, own)).IsFalse();
		Vector3[] moved = Engine_MdlSurfaceArrays.Of(blended, 0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
		AssertThat(moved[2].Y).IsEqual(3f);
		// The part's own record is untouched, and its other metadata still crosses.
		Vector3[] kept = Engine_MdlSurfaceArrays.Of(own, 0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
		AssertThat(kept[2].Y).IsEqual(1f);
		AssertThat(blended.GetMeta("mdl_marker").AsInt32()).IsEqual(7);
	}

	[TestCase]
	[RequireGodotRuntime]
	public void ALevelTableIsTheOneTheDictionaryWouldGive()
	{
		var built = new Engine_MdlSurfaceArrays.Levels();
		built.Add(2f, new[] { 0, 1, 2 });
		built.Add(1f, new[] { 0, 1, 2, 0, 2, 3 });
		built.Add(2f, new[] { 0, 1, 2, 0, 2, 3, 3, 2, 1 });

		float[] table = built.Packed();
		AssertThat(new List<float>(table)).IsEqual(new List<float> { 1f, 2f, 2f, 3f });
		AssertThat(built.Lods.Count).IsEqual(2);

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = new[] { Vector3.Zero, Vector3.Right, Vector3.Up, Vector3.Back };
		arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 0, 2, 3, 3, 2, 1, 1, 0, 3 };

		var fromTable = new ArrayMesh();
		Engine_MdlSurfaceArrays.Add(fromTable, Mesh.PrimitiveType.Triangles, arrays, built.Lods, table);
		var fromDictionary = new ArrayMesh();
		Engine_MdlSurfaceArrays.Add(fromDictionary, Mesh.PrimitiveType.Triangles, arrays, built.Lods);

		AssertThat(new List<float>(Engine_MdlSurfaceArrays.LevelTable(fromTable, 0)!))
			.IsEqual(new List<float>(Engine_MdlSurfaceArrays.LevelTable(fromDictionary, 0)!));
	}
}
