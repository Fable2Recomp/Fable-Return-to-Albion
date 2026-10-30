namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Bnk;
using Refii.Core;
using Refii.Models;

/// <summary>
/// Fable 3's MeshFile version 47 through the version-36 pipeline: the reader walks each payload
/// to its stated size, repacks the planar vertex blocks into the interleaved layouts the decoders
/// read, and the geometry, LOD and tree-stream readers build from the repacked body. The expected
/// numbers are the reference walker's (mdl47.py) for the same files. Reads the player's Fable 3
/// banks in place and is skipped without them. No Godot type is built.
/// </summary>
[TestSuite]
public class MdlVersion47Tests
{
	private const string Fable3Game =
		"/home/loreaxe/Fable 2/refii-clean/refii/out/package/Fable 2 Return to Albion/assets/Fable3/game";

	private sealed record PartExpect(
		uint VertexCount, uint IndexCount, int Batches, uint Material,
		float[]? PosMin = null, float[]? PosMax = null, float[]? Normal0 = null, float[]? Uv0 = null);

	[TestCase]
	public void Crc32LhMatchesTheGuestsFieldKey()
	{
		AssertThat(Engine_MdlEngine47.Crc32Lh("StandardMeshTextureSet")).IsEqual(0x351B0C30u);
	}

	[TestCase]
	public void ShippedModelsMatchTheReferenceWalker()
	{
		WithMount(fs =>
		{
			MdlInfo hub = Check(fs, "gui_hubroom.mdl", materials: 8, new[]
			{
				new PartExpect(108, 108, 1, 0, new[] { -9.74f, -9.66f, 1.65f }, new[] { 9.74f, 9.66f, 1.73f },
					new[] { 0.984f, -0.173f, 0.000f }, new[] { 0.064f, 0.816f }),
				new PartExpect(1303, 2246, 1, 1, new[] { -9.74f, -9.66f, 1.73f }, new[] { 9.74f, 9.66f, 4.96f }),
				new PartExpect(2122, 4915, 1, 2, new[] { -9.74f, -9.66f, 0.25f }, new[] { 9.74f, 9.66f, 5.8f }),
				new PartExpect(2044, 3291, 1, 3, new[] { -9.3f, -9.22f, 4.7f }, new[] { 9.3f, 9.22f, 5.79f }),
				new PartExpect(156, 301, 1, 4, new[] { -9.16f, -9.09f, 0.25f }, new[] { 9.16f, 9.09f, 0.25f }),
				new PartExpect(613, 1541, 1, 5, new[] { -9.73f, -9.66f, 0.0f }, new[] { 9.73f, 9.66f, 0.5f }),
				new PartExpect(148, 278, 1, 6, new[] { -8.5f, -8.5f, 5.71f }, new[] { 8.5f, 8.5f, 9.67f }),
				new PartExpect(25, 72, 1, 7, new[] { -7.2f, -7.2f, 0.0f }, new[] { 7.2f, 7.2f, 0.0f }),
			});
			AssertThat(hub.MeshBuffers.All(b => b.IsEngineRigid)).IsTrue();
			AssertThat(hub.LightmapUvs.Length).IsEqual(6519 * 2);
			AssertThat(hub.Meshes[1].Materials[0].Diffuse)
				.IsEqual(@"art\environment\shared assets\gui_props\pictures\_sharedtextures\gui_details.tex");
			AssertThat(hub.Meshes[1].Materials[0].Normal)
				.IsEqual(@"art\environment\shared assets\gui_props\pictures\_sharedtextures\gui_details_norm.tex");
			AssertThat(hub.Meshes[1].Materials[0].SpecularPowerMax).IsEqual(20f);

			MdlInfo hero = Check(fs, "ch_heromale_morph_body.mdl", materials: 2, new[]
			{
				new PartExpect(1882, 10182, 13, 0, new[] { -0.21f, -0.11f, 0.0f }, new[] { 0.21f, 0.2f, 1.11f },
					new[] { -0.119f, -0.987f, 0.102f }, new[] { 0.818f, 0.629f }),
				new PartExpect(2667, 15168, 39, 1, new[] { -0.69f, -0.11f, 0.01f }, new[] { 0.69f, 0.16f, 1.62f }),
			});
			AssertThat(hero.MeshBuffers.All(b => b.IsEngineSkinned)).IsTrue();
			AssertThat(hero.Bones.Count).IsEqual(88);
			AssertThat(hero.Bones[0].Name).IsEqual("0x4A91790F");
			AssertThat(hero.Bones[0].ParentId).IsEqual(-1);
			AssertThat(hero.Bones[1].ParentId).IsEqual(0);
			AssertThat(hero.HasBoneTransforms).IsTrue();
			AssertThat(hero.BoneTransforms.Count).IsEqual(88);
			AssertThat(hero.HideRegions[0]).IsEqual("(Default)");
			AssertThat(hero.MeshBuffers[0].SubMeshes[0].RegionIndex).IsEqual(2u);
			AssertThat(hero.MeshBuffers[0].SubMeshes[1].StartIndex).IsEqual(381u);
			AssertThat(hero.Meshes[0].Materials[0].StatedProperties!.ContainsKey("FresnelLightStrength")).IsTrue();
			// Every skin weight quad sums to 255 in the file; the repacked weights at +16 must too.
			byte[] heroBody = hero.Body!;
			foreach (MdlMeshBuffer mb in hero.MeshBuffers)
			{
				for (int v = 0; v < mb.VertexCount; v++)
				{
					int p = mb.VertexOffset + (v * 28);
					int sum = heroBody[p + 16] + heroBody[p + 17] + heroBody[p + 18] + heroBody[p + 19];
					AssertThat(sum).IsEqual(255);
					for (int k = 12; k < 16; k++)
					{
						AssertThat((int)heroBody[p + k]).IsLess(hero.Bones.Count);
					}
				}
			}

			MdlInfo oak = Check(fs, "bw_treelargeoak_6m_v2.mdl", materials: 3, new[]
			{
				new PartExpect(817, 3372, 0, 0, new[] { -3.52f, -3.61f, -1.97f }, new[] { 3.43f, 3.17f, 7.79f },
					new[] { 0.980f, 0.032f, 0.192f }, new[] { 0.354f, 0.251f }),
				new PartExpect(2129, 5490, 0, 1, new[] { -8.83f, -8.62f, 5.59f }, new[] { 8.3f, 9.52f, 20.16f }),
				new PartExpect(12121, 28884, 0, 2, new[] { -11.59f, -11.32f, 5.64f }, new[] { 11.07f, 12.05f, 23.38f }),
			});
			AssertThat(oak.MeshBuffers.All(b => b.IsEngineTree)).IsTrue();
			AssertThat(oak.HasTreeTag).IsTrue();
			AssertThat(Engine_MdlGeometry.TryBuild(ReadModel(fs, "bw_treelargeoak_6m_v2.mdl"), oak, out List<MdlGeometry> oakGeometry)).IsTrue();
			foreach (MdlGeometry geo in oakGeometry)
			{
				AssertThat(Engine_MdlTreeStreams.TryRead(Array.Empty<byte>(), oak, geo.SourceVertices,
					out float[] pivots, out float[] colour1, out _)).IsTrue();
				AssertThat(pivots.Length).IsEqual(geo.VertexCount * 4);
				AssertThat(colour1.All(float.IsFinite)).IsTrue();
				for (int v = 0; v < geo.VertexCount; v++)
				{
					AssertThat(float.IsFinite(pivots[(v * 4) + 1]) && float.IsFinite(pivots[(v * 4) + 2]) &&
						float.IsFinite(pivots[(v * 4) + 3])).IsTrue();
				}
			}

			MdlInfo grass = Check(fs, "glb_longgrass.mdl", materials: 1, new[]
			{
				new PartExpect(16, 20, 1, 0),
			});
			AssertThat(grass.MeshBuffers[0].IsEngineRigid).IsTrue();
			AssertThat(grass.MeshBuffers[0].SubMeshes[0].PrimitiveType).IsEqual((byte)1);
		});
	}

	/// <summary>
	/// Every model in the banks: a body walks to its stated LOD sizes, a header alone is a stub,
	/// nothing fails; the geometry builds with in-range indices; coarser LODs attach; cloth tables
	/// point inside the body.
	/// </summary>
	[TestCase(Timeout = 1_800_000)]
	public void EveryShippedModelParsesAndBuilds()
	{
		WithMount(fs =>
		{
			int models = 0, parsed = 0, stubs = 0, built = 0, noGeometry = 0, withLods = 0, attached = 0, cloth = 0;
			var failures = new List<string>();
			foreach (BnkFile file in fs.EnumerateAllFiles())
			{
				if (!file.VirtualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				models++;
				byte[] bytes = file.ReadAllBytes();
				if (!Engine_MdlData.TryParseInfo(bytes, file.VirtualPath, out MdlInfo info, out string error))
				{
					failures.Add($"{file.VirtualPath}: {error}");
					continue;
				}

				if (info.IsStub)
				{
					stubs++;
					continue;
				}

				parsed++;
				AssertThat(info.Version).IsEqual(47u);
				if (!Engine_MdlGeometry.TryBuild(bytes, info, out List<MdlGeometry> geometry))
				{
					noGeometry++;
					continue;
				}

				built++;
				foreach (MdlGeometry geo in geometry)
				{
					AssertThat(geo.Indices.All(i => i < geo.VertexCount)).IsTrue();
					AssertThat(InsideBounds(geo.Positions, info.FileBoundingBox, 0.5f))
						.OverrideFailureMessage($"{file.VirtualPath}: a position outside the model's bounds").IsTrue();
				}

				if (info.Lods.Count != 0)
				{
					withLods++;
					(int a, _) = Engine_MdlLods.Attach(bytes, info, geometry);
					attached += a;
				}

				foreach (MdlClothBlock block in info.ClothBlocks)
				{
					cloth++;
					AssertThat(block.RestPosOffset + (block.SimVertexCount * 12L) <= info.Body!.Length).IsTrue();
					AssertThat(block.IndexListOffset + (block.IndexCount * 2L) <= info.Body!.Length).IsTrue();
					AssertThat((int)block.RecordIndex).IsLess(info.MeshBuffers.Count);
					for (int v = 0; v < block.SimVertexCount * 3; v++)
					{
						AssertThat(float.IsFinite(BitConverter.Int32BitsToSingle(
							(int)MdlReader.ReadU32BE(info.Body!, block.RestPosOffset + (v * 4))))).IsTrue();
					}

					for (int k = 0; k < block.IndexCount; k++)
					{
						int at = block.IndexListOffset + (k * 2);
						AssertThat((info.Body![at] << 8) | info.Body![at + 1]).IsLess((int)block.SimVertexCount);
					}
				}
			}

			Console.WriteLine(
				$"[mdl47] models {models}, parsed {parsed}, stubs {stubs}, built {built}, no geometry {noGeometry}, " +
				$"with LODs {withLods}, LOD levels attached {attached}, cloth blocks {cloth}, failures {failures.Count}");
			AssertThat(failures.Count).OverrideFailureMessage("failures:\n" + string.Join("\n", failures.Take(20))).IsEqual(0);
			AssertThat(parsed + stubs).IsEqual(models);
			// Every body in globals_models.bnk, at least: the reference walker's 4773.
			AssertThat(parsed).IsGreaterEqual(4773);
			AssertThat(noGeometry).IsEqual(0);
			AssertThat(attached).IsGreater(0);
		});
	}

	private static void WithMount(Action<BnkFileSystem> body)
	{
		if (!Directory.Exists(Path.Combine(Fable3Game, "data")))
		{
			return;
		}

		// The title's loose-data index is Fable 2's pub games, a Godot path: mount as Fable 3.
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

	private static string FindModel(BnkFileSystem fs, string fileName)
	{
		string[] hits = fs.EnumerateAllFiles()
			.Select(f => f.VirtualPath)
			.Where(p => p.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase) ||
				p.EndsWith("\\" + fileName, StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		AssertThat(hits.Length).OverrideFailureMessage($"{fileName}: {hits.Length} matches in the mount").IsEqual(1);
		return hits[0];
	}

	private static byte[] ReadModel(BnkFileSystem fs, string fileName)
	{
		AssertThat(fs.TryGetFile(FindModel(fs, fileName), out BnkFile file)).IsTrue();
		return file.ReadAllBytes();
	}

	private static MdlInfo Check(BnkFileSystem fs, string fileName, int materials, PartExpect[] parts)
	{
		byte[] bytes = ReadModel(fs, fileName);
		AssertThat(Engine_MdlData.TryParseInfo(bytes, out MdlInfo info, out string error))
			.OverrideFailureMessage($"{fileName}: {error}").IsTrue();
		AssertThat(info.IsStub).IsFalse();
		AssertThat(info.Version).IsEqual(47u);
		AssertThat(info.HeaderSize).IsEqual(99u);
		AssertThat(info.IsEngineFormat).IsTrue();
		AssertThat(info.Body).IsNotNull();
		AssertThat(info.Meshes.Count).IsEqual(materials);
		AssertThat(info.Meshes.All(m => m.Materials.Count == 1)).IsTrue();
		AssertThat(info.MeshBuffers.Count).IsEqual(parts.Length);

		byte[] body = info.Body!;
		for (int i = 0; i < parts.Length; i++)
		{
			MdlMeshBuffer mb = info.MeshBuffers[i];
			PartExpect want = parts[i];
			AssertThat(mb.VertexCount).IsEqual(want.VertexCount);
			AssertThat(mb.FaceCount).IsEqual(want.IndexCount);
			AssertThat(mb.SubMeshes.Count).IsEqual(want.Batches);
			AssertThat(mb.MeshIndex).IsEqual(want.Material);

			int stride = mb.IsEngineSkinned ? 28 : mb.IsEngineTree ? 36 : 20;
			int uvAt = mb.IsEngineSkinned ? 20 : 12;
			AssertThat(mb.VertexOffset + ((long)mb.VertexCount * stride) <= body.Length).IsTrue();
			AssertThat(mb.FaceOffset + ((long)mb.FaceCount * 2) <= body.Length).IsTrue();

			for (int k = 0; k < mb.FaceCount; k++)
			{
				int index = (body[mb.FaceOffset + (k * 2)] << 8) | body[mb.FaceOffset + (k * 2) + 1];
				AssertThat(index == 0xFFFF || index < mb.VertexCount).IsTrue();
			}

			if (want.PosMin is not null && want.PosMax is not null)
			{
				var lo = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
				var hi = new[] { float.MinValue, float.MinValue, float.MinValue };
				for (int v = 0; v < mb.VertexCount; v++)
				{
					for (int c = 0; c < 3; c++)
					{
						float x = Half(body, mb.VertexOffset + (v * stride) + (c * 2));
						lo[c] = MathF.Min(lo[c], x);
						hi[c] = MathF.Max(hi[c], x);
					}
				}

				for (int c = 0; c < 3; c++)
				{
					AssertThat(MathF.Abs(lo[c] - want.PosMin[c])).OverrideFailureMessage($"{fileName} part {i} min[{c}] {lo[c]}").IsLessEqual(0.006f);
					AssertThat(MathF.Abs(hi[c] - want.PosMax[c])).OverrideFailureMessage($"{fileName} part {i} max[{c}] {hi[c]}").IsLessEqual(0.006f);
				}
			}

			if (want.Normal0 is not null)
			{
				var (nx, ny, nz) = MdlReader.DecodeNormal_11_11_10(MdlReader.ReadU32BE(body, mb.VertexOffset + 8));
				AssertThat(MathF.Abs(nx - want.Normal0[0])).IsLessEqual(0.0006f);
				AssertThat(MathF.Abs(ny - want.Normal0[1])).IsLessEqual(0.0006f);
				AssertThat(MathF.Abs(nz - want.Normal0[2])).IsLessEqual(0.0006f);
			}

			if (want.Uv0 is not null)
			{
				AssertThat(MathF.Abs(Half(body, mb.VertexOffset + uvAt) - want.Uv0[0])).IsLessEqual(0.0006f);
				AssertThat(MathF.Abs(Half(body, mb.VertexOffset + uvAt + 2) - want.Uv0[1])).IsLessEqual(0.0006f);
			}
		}

		AssertThat(Engine_MdlGeometry.TryBuild(bytes, info, out List<MdlGeometry> geometry))
			.OverrideFailureMessage($"{fileName}: no geometry").IsTrue();
		foreach (MdlGeometry geo in geometry)
		{
			AssertThat(geo.TriangleCount).IsGreater(0);
			AssertThat(geo.Indices.All(i => i < geo.VertexCount)).IsTrue();
			AssertThat(geo.Uvs.All(float.IsFinite)).IsTrue();
			AssertThat(InsideBounds(geo.Positions, info.FileBoundingBox, 0.01f))
				.OverrideFailureMessage($"{fileName}: a position outside the model's bounds").IsTrue();
		}

		return info;
	}

	/// <summary>
	/// Built positions are Y-up (x, z, -y of the file); the file's box is Z-up min xyz, max xyz.
	/// </summary>
	private static bool InsideBounds(float[] positions, float[] box, float tolerance)
	{
		for (int i = 0; i + 2 < positions.Length; i += 3)
		{
			float x = positions[i], y = -positions[i + 2], z = positions[i + 1];
			if (!(x >= box[0] - tolerance && x <= box[3] + tolerance &&
				  y >= box[1] - tolerance && y <= box[4] + tolerance &&
				  z >= box[2] - tolerance && z <= box[5] + tolerance))
			{
				return false;
			}
		}

		return true;
	}

	private static float Half(byte[] data, int at) =>
		MdlReader.HalfToFloat((ushort)((data[at] << 8) | data[at + 1]));
}
