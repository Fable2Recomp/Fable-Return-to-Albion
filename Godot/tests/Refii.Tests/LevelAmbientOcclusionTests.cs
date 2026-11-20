using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using GdUnit4;
using Godot;
using Refii.Level;
using Refii.Lighting;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

[TestSuite]
public sealed class LevelAmbientOcclusionTests
{
	private static string GodotRoot([CallerFilePath] string source = "") =>
		Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../.."));

	[TestCase]
	public void ModelSelfAo_BvhHemisphere_OccludesConcaveCornerVertices()
	{
		// Construct an L-shaped corner:
		// - Horizontal floor quad in X=[0, 2], Z=[0, 2] at Y=0 with normal +Y
		// - Vertical wall quad at X=0, Y=[0, 2], Z=[0, 2] with normal +X
		// Vertex 0 is at the inner crease (0.1, 0.0, 1.0) with upward normal -> heavily occluded by the X=0 wall.
		// Vertex 1 is at the outer edge (1.9, 0.0, 1.0) with upward normal -> mostly unoccluded.
		var geo = new MdlGeometry
		{
			Name = "corner",
			Positions = new float[]
			{
				// Inner crease vertex (v=0) and outer floor vertex (v=1)
				0.08f, 0.0f, 1.0f,
				1.95f, 0.0f, 1.0f,
				// Vertical occluder wall at X=0 spanning Y=[0..2], Z=[0..2] (v=2..5)
				0.0f, 0.0f, 0.0f,
				0.0f, 2.0f, 0.0f,
				0.0f, 2.0f, 2.0f,
				0.0f, 0.0f, 2.0f,
			},
			Normals = new float[]
			{
				0.0f, 1.0f, 0.0f,
				0.0f, 1.0f, 0.0f,
				1.0f, 0.0f, 0.0f,
				1.0f, 0.0f, 0.0f,
				1.0f, 0.0f, 0.0f,
				1.0f, 0.0f, 0.0f,
			},
			Uvs = new float[12],
			Indices = new uint[]
			{
				2, 3, 4,
				2, 4, 5,
			},
			SourceVertices = new int[] { 0, 1, 2, 3, 4, 5 },
		};

		float[] selfAo = Engine_ModelSelfAo.ComputeBvhSelfAo(
			new[] { geo },
			sourceVertexTotal: 6,
			includeGroundPlane: false,
			maxRadius: 1.5f);

		AssertThat(selfAo.Length).IsEqual(6);
		float innerCreaseAo = selfAo[0];
		float outerEdgeAo = selfAo[1];

		AssertThat(innerCreaseAo).IsGreaterEqual(Engine_ModelSelfAo.MinSelfAo);
		// A single 90-degree wall subtends half the hemisphere; with quadratic distance falloff its AO is ~0.687 vs 1.0 outside.
		AssertThat(innerCreaseAo).IsLess(0.72f);
		AssertThat(outerEdgeAo).IsGreater(0.95f);
		AssertThat(innerCreaseAo).IsLess(outerEdgeAo - 0.25f);
	}

	[TestCase]
	public void ModelSelfAo_BuildColorStream_PacksSelfAoInAlphaWithLodFallback()
	{
		float[] modelSelfAo = { 0.35f, 0.80f, 0.95f };
		var geo = new MdlGeometry
		{
			Name = "lod_test",
			Positions = new float[]
			{
				0.0f, 0.0f, 0.0f,
				1.0f, 2.0f, 3.0f,
				4.0f, 5.0f, 6.0f,
				// v=3 is an unsourced coarse LOD vertex (-1) colocated with v=1 (source vertex 1)
				1.01f, 2.0f, 3.0f,
			},
			Normals = new float[12],
			Uvs = new float[8],
			Indices = new uint[] { 0, 1, 2 },
			SourceVertices = new int[] { 0, 1, 2, -1 },
		};

		Color[] colors = Engine_ModelSelfAo.BuildColorStream(modelSelfAo, geo);
		AssertThat(colors.Length).IsEqual(4);

		// Vertex 0 -> RGB = 1, A = selfAo[0] = 0.35
		AssertThat(MathF.Abs(colors[0].R - 1.0f)).IsLess(1e-5f);
		AssertThat(MathF.Abs(colors[0].G - 1.0f)).IsLess(1e-5f);
		AssertThat(MathF.Abs(colors[0].B - 1.0f)).IsLess(1e-5f);
		AssertThat(MathF.Abs(colors[0].A - 0.35f)).IsLess(1e-5f);

		// Vertex 1 -> A = selfAo[1] = 0.80
		AssertThat(MathF.Abs(colors[1].A - 0.80f)).IsLess(1e-5f);

		// Vertex 3 (unsourced LOD vertex) -> inherits nearest sourced vertex (v=1 -> A = 0.80)
		AssertThat(MathF.Abs(colors[3].A - 0.80f)).IsLess(1e-5f);
	}

	[TestCase]
	public void ModelSelfAo_SerializeAndDeserialize_RoundTrips()
	{
		float[] original = { 0.10f, 0.50f, 0.75f, 1.0f };
		byte[] bytes = Engine_ModelSelfAo.Serialize(original);
		bool ok = Engine_ModelSelfAo.TryDeserialize(bytes, original.Length, out float[] restored);

		AssertThat(ok).IsTrue();
		AssertThat(restored.Length).IsEqual(original.Length);
		for (int i = 0; i < original.Length; i++)
		{
			AssertThat(MathF.Abs(restored[i] - original[i])).IsLess(0.01f);
		}
	}

	[TestCase]
	public void LevelAmbientOcclusion_SerializeAndDeserialize_AndIsFileValid()
	{
		var t1 = new Transform3D(Basis.Identity, new Vector3(10.25f, 2.50f, -4.75f));
		var t2 = new Transform3D(Basis.Identity, new Vector3(-3.00f, 0.00f, 12.10f));
		ulong k1 = Engine_LevelAmbientOcclusion.ComputePlacementAoKey("data/models/prop_a.mdl", in t1);
		ulong k2 = Engine_LevelAmbientOcclusion.ComputePlacementAoKey("data\\models\\PROP_A.MDL", in t1);
		ulong k3 = Engine_LevelAmbientOcclusion.ComputePlacementAoKey("data/models/prop_a.mdl", in t2);

		// Same path (different slash/case) and same transform produce identical key
		AssertThat(k1).IsEqual(k2);
		AssertThat(k1).IsNotEqual(k3);

		var container = new StaticAoContainer
		{
			PlacementOffsets = new Dictionary<ulong, int>
			{
				[k1] = 0,
				[k3] = 4,
			},
			PropAoBytes = new byte[] { 64, 128, 192, 255, 80, 160, 200, 240 },
			TerrainWidth = 4,
			TerrainHeight = 4,
			TerrainWorldRect = new Vector4(-16.0f, -16.0f, 1.0f / 32.0f, 1.0f / 32.0f),
			TerrainMinHeight = -2.0f,
			TerrainMaxHeight = 14.0f,
			TerrainAoBytes = new byte[]
			{
				255, 255, 0, 0,   255, 250, 10, 1,  255, 245, 20, 2,  255, 240, 30, 3,
				255, 235, 40, 4,  100, 95, 50, 5,   110, 105, 60, 6,  255, 230, 70, 7,
				255, 225, 80, 8,  120, 115, 90, 9,  130, 125, 100, 10, 255, 220, 110, 11,
				255, 215, 120, 12, 255, 210, 130, 13, 255, 205, 140, 14, 255, 200, 150, 15,
			},
		};

		byte[] serialized = Engine_LevelAmbientOcclusion.Serialize(container);
		bool ok = Engine_LevelAmbientOcclusion.TryDeserialize(serialized, out StaticAoContainer? restored);
		AssertThat(ok).IsTrue();
		AssertThat(restored is not null).IsTrue();
		AssertThat(restored!.PlacementOffsets.Count).IsEqual(2);
		AssertThat(restored.PlacementOffsets[k1]).IsEqual(0);
		AssertThat(restored.PlacementOffsets[k3]).IsEqual(4);
		AssertThat(restored.PlacementAoFactors.Count).IsEqual(2);
		AssertThat(restored.PlacementAoFactors[k1]).IsGreaterEqual(Engine_LevelAmbientOcclusion.MinBakedAo);
		AssertThat(restored.PlacementAoFactors[k1]).IsLess(restored.PlacementAoFactors[k3]);
		AssertThat(restored.PropAoBytes).IsEqual(container.PropAoBytes);
		AssertThat(restored.TerrainWidth).IsEqual(4);
		AssertThat(restored.TerrainHeight).IsEqual(4);
		AssertThat(MathF.Abs(restored.TerrainMinHeight - (-2.0f))).IsLess(1e-5f);
		AssertThat(MathF.Abs(restored.TerrainMaxHeight - 14.0f)).IsLess(1e-5f);
		AssertThat(restored.TerrainAoBytes).IsEqual(container.TerrainAoBytes);

		string tempFile = Path.Combine(Path.GetTempPath(), $"refii_test_{Guid.NewGuid():N}.ao");
		try
		{
			File.WriteAllBytes(tempFile, serialized);
			AssertThat(Engine_LevelAmbientOcclusion.IsFileValid(tempFile)).IsTrue();

			// Truncate file and verify IsFileValid rejects it
			File.WriteAllBytes(tempFile, serialized.AsSpan(0, 20).ToArray());
			AssertThat(Engine_LevelAmbientOcclusion.IsFileValid(tempFile)).IsFalse();
		}
		finally
		{
			if (File.Exists(tempFile))
			{
				File.Delete(tempFile);
			}
		}
	}

	[TestCase]
	public void LevelAmbientOcclusion_BakeFromBvh_ProducesTerrainContactShadowUnderBox()
	{
		// Flat 9x9 terrain grid from local (0, 0) to (16, 16) (Spacing = 2.0m, Y = 0).
		// Under MirrorX, local X=8, Z=8 maps to world (-8, 0, 8).
		const int gridW = 9;
		const int gridH = 9;
		var ghf = new GhfHeights
		{
			Width = gridW,
			Height = gridH,
			Spacing = 2.0f,
			OriginX = 0.0f,
			OriginZ = 0.0f,
			Values = new float[gridW * gridH],
		};

		// Place an occluding 3m x 2m x 3m box centered at world (-8, 1.0, 8), resting just above Y=0.
		var tris = new List<Engine_LevelShadowMaps.BvhTriangle>();
		Vector3 bMin = new(-9.5f, 0.05f, 6.5f);
		Vector3 bMax = new(-6.5f, 2.5f, 9.5f);
		AddBoxTriangles(tris, bMin, bMax);

		var worldBvh = new Engine_LevelShadowMaps.BvhTree(tris);
		StaticAoContainer baked = Engine_LevelAmbientOcclusion.BakeFromBvh(
			worldBvh,
			Array.Empty<Engine_LevelProps.StaticShadowPropBlock>(),
			lightHosts: null,
			fixtureModelPaths: null,
			primaryTerrain: ghf,
			read: _ => null);

		// Note: box at Y=0.05..2.5 with default flags is treated as a terrain occluder for R channel
		// (and on open terrain around the box, G channel also carries ground contact AO).
		AssertThat(baked.TerrainWidth).IsEqual(17);
		AssertThat(baked.TerrainHeight).IsEqual(17);
		AssertThat(baked.TerrainAoBytes.Length).IsEqual(17 * 17 * Engine_LevelAmbientOcclusion.BytesPerGroundAoTexel);

		// Center texel (8, 8) is directly under the box at world (-8, 0, 8); corner texel (0, 0) is open sky.
		byte centerAo = baked.TerrainAoBytes[(8 * baked.TerrainWidth + 8) * Engine_LevelAmbientOcclusion.BytesPerGroundAoTexel];
		byte cornerAo = baked.TerrainAoBytes[0];

		AssertThat((int)centerAo).IsLess(175);
		AssertThat((int)cornerAo).IsGreater(240);
		AssertThat((int)centerAo).IsLess((int)cornerAo - 70);
	}

	[TestCase]
	public void LevelBakedLighting_ApplyMultiMeshProbes_PacksNormalizedPlacementAoFactorsInAlpha()
	{
		var instances = new[]
		{
			new Transform3D(Basis.Identity, new Vector3(1f, 2f, 3f)),
			new Transform3D(Basis.Identity, new Vector3(4f, 5f, 6f)),
		};
		float[] aoFactors = { 0.45f, 0.85f };
		var buffer = new float[2 * 16];

		bool coloured = Engine_LevelBakedLighting.ApplyMultiMeshProbes(
			buffer,
			stride: 16,
			instances: instances,
			probes: null,
			bakes: null,
			bakeKeys: null,
			authoredColours: null,
			aoFactors: aoFactors);

		// Without probe RGB or authored colours, coloured remains false (preserving BakeModeMeta),
		// while the normalized per-instance AO factors are written into COLOR.a (slot + 15).
		AssertThat(coloured).IsFalse();
		AssertThat(MathF.Abs(buffer[15] - 0.45f)).IsLess(1e-5f);
		AssertThat(MathF.Abs(buffer[16 + 15] - 0.85f)).IsLess(1e-5f);
	}

	[TestCase]
	public void Shaders_AmbientOcclusionPipeline_PreservesSceneReferredLinearSpaceWithoutUpperClamping()
	{
		string root = GodotRoot();
		string lightingInc = File.ReadAllText(Path.Combine(root, "src/Engine/material/fable_lighting.gdshaderinc"));
		string standardBody = File.ReadAllText(Path.Combine(root, "src/Engine/material/standard_material_body.gdshaderinc"));
		string terrainBody = File.ReadAllText(Path.Combine(root, "src/Engine/level/terrain/terrain_splat_body.gdshaderinc"));

		// Verify instance uniform slot 9 pinning and removal of vertex-stage texelFetch(fable_static_prop_ao)
		AssertThat(lightingInc.Contains("instance uniform float instance_baked_ao : instance_index(9) = 1.0;", StringComparison.Ordinal)).IsTrue();
		AssertThat(lightingInc.Contains("fable_static_prop_ao", StringComparison.Ordinal)).IsFalse();
		AssertThat(standardBody.Contains("fable_sample_prop_vertex_ao", StringComparison.Ordinal)).IsFalse();

		// Verify AO helper functions exist and do not clamp AO or emission to 1.0
		int helperStart = lightingInc.IndexOf("vec2 fable_sample_terrain_ao_and_height", StringComparison.Ordinal);
		int helperEnd = lightingInc.IndexOf("/// The main light.", helperStart, StringComparison.Ordinal);
		AssertThat(helperStart).IsGreater(-1);
		AssertThat(helperEnd).IsGreater(helperStart);
		string helperBlock = lightingInc[helperStart..helperEnd];
		AssertThat(helperBlock.Contains("clamp(", StringComparison.Ordinal)).IsFalse();
		AssertThat(helperBlock.Contains("fable_direct_ao", StringComparison.Ordinal)).IsTrue();

		// Verify combined_ao in standard_material_body and terrain_splat_body uses max(..., 0.0) without clamp
		// and modulates direct lighting in light() via fable_direct_ao_for_light(v_combined_ao, LIGHT_IS_DIRECTIONAL).
		// FABLE_UNIFIED is fable_unified in every full build (fable_lighting.gdshaderinc); a phone's lite build may fold it.
		AssertThat(standardBody.Contains("float combined_ao = mix(max(ao, 0.0), fable_apply_ao_strength(max(ao, 0.0) * max(baked_ao, 0.0)), step(0.5, FABLE_UNIFIED));", StringComparison.Ordinal)).IsTrue();
		AssertThat(standardBody.Contains("fable_direct_ao_for_light(v_combined_ao, LIGHT_IS_DIRECTIONAL)", StringComparison.Ordinal)).IsTrue();
		AssertThat(terrainBody.Contains("float combined_ao = mix(max(ao, 0.0), fable_apply_ao_strength(max(ao, 0.0) * max(terrain_contact_ao, 0.0)), step(0.5, FABLE_UNIFIED));", StringComparison.Ordinal)).IsTrue();
		AssertThat(terrainBody.Contains("fable_direct_ao_for_light(v_combined_ao, LIGHT_IS_DIRECTIONAL)", StringComparison.Ordinal)).IsTrue();
		AssertThat(lightingInc.Contains("#define FABLE_UNIFIED fable_unified", StringComparison.Ordinal)).IsTrue();
	}

	[TestCase]
	public void ModelSelfAo_TryGetModelSelfAoFast_DoesNotRaytraceUncachedModelAndFreesContainerBuffersOnBind()
	{
		Engine_ModelSelfAo.ClearMemoryCache();
		var dummyInfo = new MdlInfo();
		bool found = Engine_ModelSelfAo.TryGetModelSelfAoFast(
			Array.Empty<byte>(),
			dummyInfo,
			sourceVertexTotal: 16,
			virtualPath: $"data/models/nonexistent_{Guid.NewGuid():N}.mdl",
			out float[] fastAo);

		AssertThat(found).IsFalse();
		AssertThat(fastAo.Length).IsEqual(0);

		var container = new StaticAoContainer
		{
			PlacementOffsets = new Dictionary<ulong, int> { [1UL] = 0 },
			PropAoBytes = new byte[] { 100, 200, 255 },
			TerrainWidth = 2,
			TerrainHeight = 2,
			TerrainWorldRect = Vector4.Zero,
			TerrainAoBytes = new byte[] { 255, 255, 255, 255 },
		};

		Engine_LevelAmbientOcclusion.UploadAndBind(container);
		AssertThat(container.PropAoBytes.Length).IsEqual(0);
		AssertThat(container.TerrainAoBytes.Length).IsEqual(0);

		Engine_LevelAmbientOcclusion.Clear();
		AssertThat(Engine_LevelAmbientOcclusion.Current is null).IsTrue();
	}

	[TestCase]
	public void HeroTextureMorph_OrmAndStaticShadowShader_AvoidOffThreadGpuReadbackAndStairStepping()
	{
		string root = GodotRoot();
		string heroMorph = File.ReadAllText(Path.Combine(root, "src/Engine/hero/texture/Engine_HeroTextureMorph.cs"));
		string texLib = File.ReadAllText(Path.Combine(root, "src/Engine/tex/Engine_TexLibrary.cs"));
		string lightingInc = File.ReadAllText(Path.Combine(root, "src/Engine/material/fable_lighting.gdshaderinc"));

		// Verify Engine_HeroTextureMorph registers CPU Image buffers in Engine_TexLibrary so BuildOrm uses BuildOrmFromImages without GPU readback
		AssertThat(heroMorph.Contains("Engine_TexLibrary.RegisterCpuImage(", StringComparison.Ordinal)).IsTrue();
		AssertThat(texLib.Contains("BuildOrmFromImages(", StringComparison.Ordinal)).IsTrue();
		AssertThat(heroMorph.Contains("Engine_TexLibrary.BuildOrmFromTextures(", StringComparison.Ordinal)).IsFalse();

		// Verify static shadow atlas lookup uses per-texel texelFetch true bilinear PCF + 3D ray-tangent multi-tap filtering (never raw-depth textureLod interpolation)
		AssertThat(lightingInc.Contains("float fable_sample_static_shadow_tiered(", StringComparison.Ordinal)).IsTrue();
		AssertThat(lightingInc.Contains("float fable_sample_static_shadow_bilinear_pcf(", StringComparison.Ordinal)).IsTrue();
		AssertThat(lightingInc.Contains("texelFetch(fable_static_shadow_atlas,", StringComparison.Ordinal)).IsTrue();
		AssertThat(lightingInc.Contains("textureLod(fable_static_shadow_atlas,", StringComparison.Ordinal)).IsFalse();
	}

	private static void AddBoxTriangles(List<Engine_LevelShadowMaps.BvhTriangle> into, Vector3 min, Vector3 max)
	{
		Vector3 v000 = new(min.X, min.Y, min.Z);
		Vector3 v100 = new(max.X, min.Y, min.Z);
		Vector3 v010 = new(min.X, max.Y, min.Z);
		Vector3 v110 = new(max.X, max.Y, min.Z);
		Vector3 v001 = new(min.X, min.Y, max.Z);
		Vector3 v101 = new(max.X, min.Y, max.Z);
		Vector3 v011 = new(min.X, max.Y, max.Z);
		Vector3 v111 = new(max.X, max.Y, max.Z);

		// Bottom
		into.Add(new Engine_LevelShadowMaps.BvhTriangle(v000, v100, v101));
		into.Add(new Engine_LevelShadowMaps.BvhTriangle(v000, v101, v001));
		// Top
		into.Add(new Engine_LevelShadowMaps.BvhTriangle(v010, v011, v111));
		into.Add(new Engine_LevelShadowMaps.BvhTriangle(v010, v111, v110));
		// Front/Back/Left/Right
		into.Add(new Engine_LevelShadowMaps.BvhTriangle(v000, v010, v110));
		into.Add(new Engine_LevelShadowMaps.BvhTriangle(v000, v110, v100));
		into.Add(new Engine_LevelShadowMaps.BvhTriangle(v001, v101, v111));
		into.Add(new Engine_LevelShadowMaps.BvhTriangle(v001, v111, v011));
		into.Add(new Engine_LevelShadowMaps.BvhTriangle(v000, v001, v011));
		into.Add(new Engine_LevelShadowMaps.BvhTriangle(v000, v011, v010));
		into.Add(new Engine_LevelShadowMaps.BvhTriangle(v100, v110, v111));
		into.Add(new Engine_LevelShadowMaps.BvhTriangle(v100, v111, v101));
	}
}
