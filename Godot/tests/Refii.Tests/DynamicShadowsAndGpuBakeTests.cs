using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GdUnit4;
using Godot;
using Refii.Entities;
using Refii.Level;
using Refii.Lighting;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

[TestSuite]
public sealed class DynamicShadowsAndGpuBakeTests
{
	private static string GodotRoot([CallerFilePath] string source = "") =>
		Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../.."));

	[TestCase]
	public void GpuComputeShaders_ValidGlsl450Syntax_NoCallSiteParameterQualifiers()
	{
		string[] sources =
		{
			Engine_GpuBvhBaker.ShadowAtlasShaderSource,
			Engine_GpuBvhBaker.GroundAoShaderSource,
			Engine_GpuBvhBaker.GroundAoFilterShaderSource,
		};

		// Matches call-site qualifiers like `foo(a, out b)` while ignoring function signature declarations
		// (`void foo(float a, out float b)` or `bool trace(..., out float hit_dist)`).
		var callSiteOutPattern = new Regex(
			@"(?<!\b(?:void|bool|int|uint|float|vec2|vec3|vec4)\s+)[A-Za-z_][A-Za-z0-9_]*\s*\([^;{}]*\b(?:out|inout)\s+[A-Za-z_][A-Za-z0-9_]*\s*(?:,|\))",
			RegexOptions.CultureInvariant);

		foreach (string glsl in sources)
		{
			AssertThat(glsl.StartsWith("#version 450", StringComparison.Ordinal)).IsTrue();
			AssertThat(glsl.Contains("void main()", StringComparison.Ordinal)).IsTrue();
			AssertThat(callSiteOutPattern.IsMatch(glsl)).IsFalse();

			int braces = 0;
			int parens = 0;
			foreach (char c in glsl)
			{
				if (c == '{') braces++;
				else if (c == '}') braces--;
				else if (c == '(') parens++;
				else if (c == ')') parens--;
				AssertThat(braces).IsGreaterEqual(0);
				AssertThat(parens).IsGreaterEqual(0);
			}

			AssertThat(braces).IsEqual(0);
			AssertThat(parens).IsEqual(0);
		}
	}

	[TestCase]
	public void GpuGroundAo_ComputeTerrainHeightBounds_CropsTightlyWithoutOuterHills()
	{
		var ghf = new GhfHeights
		{
			Width = 9,
			Height = 9,
			Spacing = 10.0f,
			OriginX = 0.0f,
			OriginZ = 0.0f,
			Values = new float[81],
		};

		// Outer border ring has 500m mountains; inner playable region [20..50] x [20..50] spans [12.5m .. 28.0m].
		for (int z = 0; z < 9; z++)
		{
			for (int x = 0; x < 9; x++)
			{
				bool outer = x == 0 || x == 8 || z == 0 || z == 8;
				ghf.Values[z * 9 + x] = outer ? 500.0f : 12.5f + (x - 1) * 1.5f + (z - 1) * 1.0f;
			}
		}

		Engine_GpuBvhBaker.ComputeTerrainHeightBounds(
			in ghf,
			cropOriginX: 20.0f,
			cropOriginZ: 20.0f,
			cropSpanX: 30.0f,
			cropSpanZ: 30.0f,
			out float minH,
			out float maxH);

		AssertThat(minH).IsGreaterEqual(12.5f);
		AssertThat(maxH).IsLess(100.0f);
		AssertThat(maxH).IsGreater(minH);
	}

	[TestCase]
	public void LevelScene_UsesNonBlockingAsyncBake_AndHotBindRespectsActiveScenario()
	{
		string root = GodotRoot();
		string levelScene = File.ReadAllText(Path.Combine(root, "src/Engine/level/scene/Engine_LevelScene.cs"));
		string aoSource = File.ReadAllText(Path.Combine(root, "src/Engine/Lighting/Engine_LevelAmbientOcclusion.cs"));

		// Level load must not block on synchronous EnsureScenarioBuilt(
		AssertThat(levelScene.Contains("Engine_LevelShadowPrebuild.EnsureScenarioBuiltAsync(", StringComparison.Ordinal)).IsTrue();
		AssertThat(levelScene.Contains("Engine_LevelShadowPrebuild.EnsureScenarioBuilt(", StringComparison.Ordinal)).IsFalse();

		// BakeFromBvh must not run the single-threaded GetOrLoadModelAoSource sweep over propBlocks
		int bakeIdx = aoSource.IndexOf("public static StaticAoContainer BakeFromBvh(", StringComparison.Ordinal);
		AssertThat(bakeIdx).IsGreater(0);
		AssertThat(aoSource[bakeIdx..].Contains("GetOrLoadModelAoSource(", StringComparison.Ordinal)).IsFalse();

		var activeLevel = new LevelEntry
		{
			World = "albion",
			Name = $"test_hotbind_{Guid.NewGuid():N}",
			Scenario = "defaultscenario",
		};
		var staleLevel = new LevelEntry
		{
			World = "albion",
			Name = $"test_stale_{Guid.NewGuid():N}",
			Scenario = "defaultscenario",
		};

		try
		{
			bool cacheHit = Engine_LevelAmbientOcclusion.LoadOrClear(activeLevel, "test.gdb");
			AssertThat(cacheHit).IsFalse();

			// Stale bake from an earlier scenario is discarded and does not overwrite Current
			var staleContainer = new StaticAoContainer
			{
				PlacementOffsets = new Dictionary<ulong, int>(),
				PropAoBytes = Array.Empty<byte>(),
				TerrainWidth = 4,
				TerrainHeight = 4,
				TerrainAoBytes = new byte[32],
			};
			Engine_LevelAmbientOcclusion.HotBindIfActive(staleLevel, "test.gdb", staleContainer);
			AssertThat(Engine_LevelAmbientOcclusion.Current is null).IsTrue();
			AssertThat(staleContainer.TerrainAoBytes.Length).IsEqual(0);

			// Matching active scenario bake hot-binds cleanly into Current
			var matchingContainer = new StaticAoContainer
			{
				PlacementOffsets = new Dictionary<ulong, int>(),
				PropAoBytes = Array.Empty<byte>(),
				TerrainWidth = 4,
				TerrainHeight = 4,
				TerrainAoBytes = new byte[32],
			};
			Engine_LevelAmbientOcclusion.HotBindIfActive(activeLevel, "test.gdb", matchingContainer);
			AssertThat(ReferenceEquals(Engine_LevelAmbientOcclusion.Current, matchingContainer)).IsTrue();
		}
		finally
		{
			Engine_LevelAmbientOcclusion.Clear();
		}
	}

	[TestCase]
	public void DynamicCharacterShadows_RendererSceneCullLayerPairing_OutdoorAndIndoorZones()
	{
		bool prevInterior = Engine_LightingFrame.CurrentInterior;
		try
		{
			Engine_LightingFrame.CurrentInterior = false;

			uint baseCharacterBody = 1u | Engine_LevelLights.EntityCasterLayerMask;
			uint outdoorBodyLayers = Engine_LightZones.Zoned(baseCharacterBody, zone: 1u);
			uint indoorBodyLayers = Engine_LightZones.Zoned(baseCharacterBody, zone: 0x123456u);

			uint outdoorProxy = Engine_ShadowProxy.ComputeProxyLayers(outdoorBodyLayers, follows: true);
			uint indoorProxy = Engine_ShadowProxy.ComputeProxyLayers(indoorBodyLayers, follows: true);

			uint outdoorLightCull = Engine_LightZones.OutdoorCullMask;
			uint indoorLightCull = Engine_LightZones.IndoorCullMask;
			const uint unzonedLightCull = uint.MaxValue;

			uint outdoorLightCaster = Engine_LevelLights.CasterMaskOf(
				includeDynamicEntities: true,
				isIndoorLight: false,
				includeStaticWorld: false,
				isOutdoorLight: true);
			uint indoorLightCaster = Engine_LevelLights.CasterMaskOf(
				includeDynamicEntities: true,
				isIndoorLight: true,
				includeStaticWorld: false,
				isOutdoorLight: false);
			uint unzonedLightCaster = Engine_LevelLights.CasterMaskOf(
				includeDynamicEntities: true,
				isIndoorLight: false,
				includeStaticWorld: false,
				isOutdoorLight: false);

			// 1. Outdoor character proxy pairs with outdoor lights (so moving dirties shadow maps!) and casts into them
			AssertThat(outdoorLightCull & outdoorProxy).IsNotEqual(0u);
			AssertThat(outdoorLightCaster & outdoorProxy).IsNotEqual(0u);

			// 2. Outdoor character proxy NEVER pairs with or leaks shadows into indoor lights through walls
			AssertThat(indoorLightCull & outdoorProxy).IsEqual(0u);
			AssertThat(indoorLightCaster & outdoorProxy).IsEqual(0u);

			// 3. Indoor character proxy pairs with indoor lights (so moving inside a tavern dirties shadow maps!) and casts into them
			AssertThat(indoorLightCull & indoorProxy).IsNotEqual(0u);
			AssertThat(indoorLightCaster & indoorProxy).IsNotEqual(0u);

			// 4. Indoor character proxy NEVER pairs with or leaks shadows into outdoor lights through walls
			AssertThat(outdoorLightCull & indoorProxy).IsEqual(0u);
			AssertThat(outdoorLightCaster & indoorProxy).IsEqual(0u);

			// 5. Unzoned (zone 0) lights pair with and cast shadows for both outdoor and indoor character proxies
			AssertThat(unzonedLightCull & outdoorProxy).IsNotEqual(0u);
			AssertThat(unzonedLightCaster & outdoorProxy).IsNotEqual(0u);
			AssertThat(unzonedLightCull & indoorProxy).IsNotEqual(0u);
			AssertThat(unzonedLightCaster & indoorProxy).IsNotEqual(0u);

			// 6. Neither character proxy leaks into planar water reflection layer
			AssertThat(outdoorProxy & Engine_ReflectionLayer.Mask).IsEqual(0u);
			AssertThat(indoorProxy & Engine_ReflectionLayer.Mask).IsEqual(0u);
		}
		finally
		{
			Engine_LightingFrame.CurrentInterior = prevInterior;
		}
	}

	[TestCase]
	public void HiddenClusters_ComposeShadowCollapsedMask_PreservesRiggedClothGarmentsWhileHidingCoveredSkin()
	{
		UInt128 coveredTorso = UInt128.One << 2;
		UInt128 classicDisplaced = UInt128.One << 5;
		UInt128 coatClothSurface = UInt128.One << 9;

		// Rigged character cloth body has CastShadow = Off (clothCastsOwnShadow = false):
		// ShadowProxy MUST keep coatClothSurface (not collapse it) while still collapsing coveredTorso and classicDisplaced.
		UInt128 riggedShadowHidden = Engine_HiddenClusters.ComposeShadowCollapsedMask(
			coveredTorso,
			classicDisplaced,
			coatClothSurface,
			clothCastsOwnShadow: false);
		AssertThat(riggedShadowHidden & coveredTorso).IsNotEqual(UInt128.Zero);
		AssertThat(riggedShadowHidden & classicDisplaced).IsNotEqual(UInt128.Zero);
		AssertThat(riggedShadowHidden & coatClothSurface).IsEqual(UInt128.Zero);

		// Unrigged banner/flag cloth body casts its own shadow (clothCastsOwnShadow = true):
		// ShadowProxy collapses coatClothSurface so it is not double-cast.
		UInt128 unriggedShadowHidden = Engine_HiddenClusters.ComposeShadowCollapsedMask(
			coveredTorso,
			classicDisplaced,
			coatClothSurface,
			clothCastsOwnShadow: true);
		AssertThat(unriggedShadowHidden & coatClothSurface).IsNotEqual(UInt128.Zero);
	}

	[TestCase]
	public void WalkableFloorAo_TempleSubterraneanGhf_EliminatesBleed_Preserves16BitHeight_AndGroundsFirePitFixtures()
	{
		// Simulate Oakfield's Temple of Light:
		// 1. Sloping .ghf dirt passes 15-40 cm UNDERNEATH the architectural stone floor (Y = 20.00 m).
		// 2. A tall distant spire reaches Y = 180.0 m (160 m vertical span to exercise 16-bit height precision).
		// 3. A fire-pit brazier (tagged FlagAoOccluder ONLY, without FlagShadowCaster so it doesn't block its own flame!)
		//    sits at world (-12, 20.00..21.10, 8) on the stone floor.
		// 4. Open stone floor at world (-4, 20.00, 8) has buried .ghf 25 cm underneath it (Y = 19.75 m).
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

		for (int z = 0; z < gridH; z++)
		{
			for (int x = 0; x < gridW; x++)
			{
				// Sloping subterranean .ghf between 19.60 m and 19.88 m right under the Y=20.00 m floor!
				ghf.Values[z * gridW + x] = 19.60f + x * 0.035f;
			}
		}

		var tris = new List<Engine_LevelShadowMaps.BvhTriangle>();

		// Subterranean .ghf terrain triangles (FlagShadowCaster | FlagTerrain | FlagAoOccluder)
		uint terrainFlags = Engine_LevelShadowMaps.BvhTriangle.FlagShadowCaster |
		                    Engine_LevelShadowMaps.BvhTriangle.FlagTerrain |
		                    Engine_LevelShadowMaps.BvhTriangle.FlagAoOccluder;
		tris.Add(new Engine_LevelShadowMaps.BvhTriangle(new Vector3(-16f, 19.75f, 0f), new Vector3(0f, 19.75f, 0f), new Vector3(0f, 19.75f, 16f), terrainFlags));
		tris.Add(new Engine_LevelShadowMaps.BvhTriangle(new Vector3(-16f, 19.75f, 0f), new Vector3(0f, 19.75f, 16f), new Vector3(-16f, 19.75f, 16f), terrainFlags));

		// Temple stone floor slab at Y = 20.00 m (note MirrorX-flipped winding tested too via reversed order!)
		uint archFlags = Engine_LevelShadowMaps.BvhTriangle.DefaultFlags;
		tris.Add(new Engine_LevelShadowMaps.BvhTriangle(new Vector3(-16f, 20.00f, 0f), new Vector3(0f, 20.00f, 16f), new Vector3(0f, 20.00f, 0f), archFlags));
		tris.Add(new Engine_LevelShadowMaps.BvhTriangle(new Vector3(-16f, 20.00f, 0f), new Vector3(-16f, 20.00f, 16f), new Vector3(0f, 20.00f, 16f), archFlags));

		// Distant 180 m spire peak at corner (-15.5, 180, 0.5) so height span covers ~160 m!
		AddBox(tris, new Vector3(-15.9f, 179.0f, 0.1f), new Vector3(-15.5f, 180.0f, 0.5f), archFlags);

		// Fire-pit brazier light-host fixture centered at world (-12, 20.05..21.15, 8) -> localX = 12, localZ = 8 -> texel (12, 8)
		// Tagged FlagAoOccluder ONLY (no FlagShadowCaster)!
		AddBox(
			tris,
			new Vector3(-13.1f, 20.05f, 6.9f),
			new Vector3(-10.9f, 21.20f, 9.1f),
			Engine_LevelShadowMaps.BvhTriangle.FlagAoOccluder);

		var worldBvh = new Engine_LevelShadowMaps.BvhTree(tris);

		// 1. Verify lamp shadow ray originating inside the fire-pit brazier ignores FlagAoOccluder-only triangles!
		bool lampBlockedByOwnBrazier = worldBvh.TraceClosestHit(
			new Vector3(-12.0f, 20.80f, 8.0f),
			Vector3.Right,
			0.05f,
			5.0f,
			out _,
			out _,
			Engine_LevelShadowMaps.BvhTriangle.FlagShadowCaster,
			Engine_LevelShadowMaps.BvhTriangle.FlagShadowCaster);
		AssertThat(lampBlockedByOwnBrazier).IsFalse();

		// 2. Bake Ground AO + Walkable-Floor Height map (17x17 over local [0..16] x [0..16], 1.0 m/texel)
		StaticAoContainer baked = Engine_LevelAmbientOcclusion.BakeFromBvh(
			worldBvh,
			Array.Empty<Engine_LevelProps.StaticShadowPropBlock>(),
			lightHosts: null,
			fixtureModelPaths: null,
			primaryTerrain: ghf,
			read: _ => null);

		AssertThat(baked.TerrainWidth).IsEqual(17);
		AssertThat(baked.TerrainHeight).IsEqual(17);
		AssertThat(baked.TerrainAoBytes.Length).IsEqual(17 * 17 * 4);

		// Open floor tile at local (4, 8) -> world (-4, 20.00, 8), where .ghf is 25 cm below the floor:
		// R channel (buried .ghf) is heavily occluded by the floor underside (< 120),
		// whereas G channel (walkable Temple floor at Y=20.00m) is completely clean (> 245)!
		int openOffset = (8 * baked.TerrainWidth + 4) * 4;
		byte buriedTerrAo = baked.TerrainAoBytes[openOffset];
		byte openFloorAo = baked.TerrainAoBytes[openOffset + 1];
		float decodedOpenFloorY = Engine_LevelAmbientOcclusion.DecodeHeight16(
			baked.TerrainAoBytes[openOffset + 2],
			baked.TerrainAoBytes[openOffset + 3],
			baked.TerrainMinHeight,
			baked.TerrainMaxHeight);

		AssertThat((int)buriedTerrAo).IsLess(120);
		AssertThat((int)openFloorAo).IsGreater(245);
		AssertThat(MathF.Abs(decodedOpenFloorY - 20.00f)).IsLess(0.006f);

		// Floor tile right next to the FlagAoOccluder-only fire-pit brazier at local (10, 8) -> world (-10, 20.00, 8) (0.9m from brazier wall):
		int nearFirePitOffset = (8 * baked.TerrainWidth + 10) * 4;
		byte nearFirePitFloorAo = baked.TerrainAoBytes[nearFirePitOffset + 1];
		float decodedNearFirePitY = Engine_LevelAmbientOcclusion.DecodeHeight16(
			baked.TerrainAoBytes[nearFirePitOffset + 2],
			baked.TerrainAoBytes[nearFirePitOffset + 3],
			baked.TerrainMinHeight,
			baked.TerrainMaxHeight);

		AssertThat((int)nearFirePitFloorAo).IsLess(225);
		AssertThat((int)nearFirePitFloorAo).IsLess((int)openFloorAo - 25);
		AssertThat(MathF.Abs(decodedNearFirePitY - 20.00f)).IsLess(0.006f);
	}

	[TestCase]
	public void WalkableFloorAo_InteriorWithoutGhf_AndFastCreviceSelfAo_OccludeCreasesAndPedestals()
	{
		// 1. Pure interior scenario without a .ghf heightfield (primaryTerrain: null):
		// Floor at Y = 5.0 m spanning world [-6..6] x [-6..6], with a statue pillar centered at (0, 5..8, 0).
		var tris = new List<Engine_LevelShadowMaps.BvhTriangle>();
		uint flags = Engine_LevelShadowMaps.BvhTriangle.DefaultFlags;
		tris.Add(new Engine_LevelShadowMaps.BvhTriangle(new Vector3(-6f, 5f, -6f), new Vector3(6f, 5f, -6f), new Vector3(6f, 5f, 6f), flags));
		tris.Add(new Engine_LevelShadowMaps.BvhTriangle(new Vector3(-6f, 5f, -6f), new Vector3(6f, 5f, 6f), new Vector3(-6f, 5f, 6f), flags));
		AddBox(tris, new Vector3(-0.8f, 5.02f, -0.8f), new Vector3(0.8f, 8.0f, 0.8f), flags);

		var interiorBvh = new Engine_LevelShadowMaps.BvhTree(tris);
		StaticAoContainer interiorAo = Engine_LevelAmbientOcclusion.BakeFromBvh(
			interiorBvh,
			Array.Empty<Engine_LevelProps.StaticShadowPropBlock>(),
			lightHosts: null,
			fixtureModelPaths: null,
			primaryTerrain: null,
			read: _ => null);

		AssertThat(interiorAo.TerrainWidth).IsGreaterEqual(17);
		AssertThat(interiorAo.TerrainAoBytes.Length).IsEqual(interiorAo.TerrainWidth * interiorAo.TerrainHeight * 4);

		// Sample near statue base (-1.0, 5.0, 0.0) vs open floor (-4.5, 5.0, -4.5)
		int SampleWalkAoAt(float worldX, float worldZ)
		{
			float u = Math.Clamp((-worldX - interiorAo.TerrainWorldRect.X) * interiorAo.TerrainWorldRect.Z, 0f, 1f);
			float v = Math.Clamp((worldZ - interiorAo.TerrainWorldRect.Y) * interiorAo.TerrainWorldRect.W, 0f, 1f);
			int tx = (int)MathF.Round(u * (interiorAo.TerrainWidth - 1));
			int ty = (int)MathF.Round(v * (interiorAo.TerrainHeight - 1));
			return interiorAo.TerrainAoBytes[(ty * interiorAo.TerrainWidth + tx) * 4 + 1];
		}

		int pillarBaseAo = SampleWalkAoAt(-1.0f, 0.0f);
		int openChamberAo = SampleWalkAoAt(-4.5f, -4.5f);
		AssertThat(pillarBaseAo).IsLess(220);
		AssertThat(openChamberAo).IsGreater(245);
		AssertThat(pillarBaseAo).IsLess(openChamberAo - 30);

		// 2. Fast O(T) 1-ring crevice AO on a V-groove statue fold vs flat outer flange:
		// Valley vertex (v=1) at (0, 0, 0) with upward normal (0, 1, 0), flanked by steeply rising walls at (-0.25, +0.25, 0) and (+0.25, +0.25, 0),
		// plus flat outer rim vertices (v=4, 5) on horizontal plane Y = 0.25.
		var foldGeo = new MdlGeometry
		{
			Name = "statue_fold",
			Positions = new float[]
			{
				-0.25f, 0.25f, -0.25f, // 0: left ridge front
				 0.00f, 0.00f, -0.25f, // 1: deep crease valley front
				 0.25f, 0.25f, -0.25f, // 2: right ridge front
				 0.00f, 0.00f,  0.25f, // 3: deep crease valley back
				 0.55f, 0.25f, -0.25f, // 4: flat outer terrace front
				 0.55f, 0.25f,  0.25f, // 5: flat outer terrace back
				 0.25f, 0.25f,  0.25f, // 6: right ridge back
			},
			Normals = new float[]
			{
				0f, 1f, 0f,
				0f, 1f, 0f,
				0f, 1f, 0f,
				0f, 1f, 0f,
				0f, 1f, 0f,
				0f, 1f, 0f,
				0f, 1f, 0f,
			},
			Uvs = new float[14],
			Indices = new uint[]
			{
				0, 1, 3,
				1, 2, 6,
				1, 6, 3,
				2, 4, 5,
				2, 5, 6,
			},
			SourceVertices = new int[] { 0, 1, 2, 3, 4, 5, 6 },
		};

		float[] fastCreviceAo = Engine_ModelSelfAo.ComputeFastCreviceAndBaseAo(
			new[] { foldGeo },
			sourceVertexTotal: 7,
			includeGroundPlane: false);

		// Valley crease vertex (1) rises steeply to neighbors (0, 2, 6) -> darkened (< 0.65),
		// while flat outer terrace vertex (4) has coplanar/downward neighbors -> unoccluded (> 0.98)!
		AssertThat(fastCreviceAo[1]).IsLess(0.65f);
		AssertThat(fastCreviceAo[4]).IsGreater(0.98f);
	}

	private static void AddBox(List<Engine_LevelShadowMaps.BvhTriangle> into, Vector3 min, Vector3 max, uint flags)
	{
		Vector3 v000 = new(min.X, min.Y, min.Z);
		Vector3 v100 = new(max.X, min.Y, min.Z);
		Vector3 v010 = new(min.X, max.Y, min.Z);
		Vector3 v110 = new(max.X, max.Y, min.Z);
		Vector3 v001 = new(min.X, min.Y, max.Z);
		Vector3 v101 = new(max.X, min.Y, max.Z);
		Vector3 v011 = new(min.X, max.Y, max.Z);
		Vector3 v111 = new(max.X, max.Y, max.Z);

		void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
		{
			into.Add(new Engine_LevelShadowMaps.BvhTriangle(a, b, c, flags));
			into.Add(new Engine_LevelShadowMaps.BvhTriangle(a, c, d, flags));
		}

		Quad(v000, v100, v110, v010);
		Quad(v101, v001, v011, v111);
		Quad(v001, v000, v010, v011);
		Quad(v100, v101, v111, v110);
		Quad(v010, v110, v111, v011);
		Quad(v001, v101, v100, v000);
	}
}

[TestSuite, RequireGodotRuntime]
public sealed class DynamicShadowsAndGpuBakeGodotRuntimeTests
{
	[TestCase]
	public void GpuComputeShaders_CompileSpirVAndNodeClothMasksWorkInGodotRuntime()
	{
		RenderingDevice? rd = null;
		try
		{
			rd = RenderingServer.CreateLocalRenderingDevice();
			if (rd is not null)
			{
				string[] sources =
				{
					Engine_GpuBvhBaker.ShadowAtlasShaderSource,
					Engine_GpuBvhBaker.GroundAoShaderSource,
					Engine_GpuBvhBaker.GroundAoFilterShaderSource,
				};
				foreach (string glsl in sources)
				{
					RDShaderSpirV spirv = rd.ShaderCompileSpirVFromSource(new RDShaderSource
					{
						SourceCompute = glsl,
						Language = RenderingDevice.ShaderLanguage.Glsl,
					});
					AssertThat(spirv.CompileErrorCompute).IsEqual(string.Empty);
				}
			}
		}
		finally
		{
			rd?.Dispose();
		}

		var body = new MeshInstance3D();
		try
		{
			UInt128 garmentBit = UInt128.One << 4;
			Engine_HiddenClusters.SetClothSurfaces(body, garmentBit, castsShadow: false);
			AssertThat(Engine_HiddenClusters.CollapsedMask(body) & garmentBit).IsNotEqual(UInt128.Zero);
			AssertThat(Engine_HiddenClusters.ShadowCollapsedMask(body) & garmentBit).IsEqual(UInt128.Zero);

			Engine_HiddenClusters.SetClothSurfaces(body, garmentBit, castsShadow: true);
			AssertThat(Engine_HiddenClusters.ShadowCollapsedMask(body) & garmentBit).IsNotEqual(UInt128.Zero);

			Engine_HiddenClusters.SetClothSurfaces(body, UInt128.Zero);
		}
		finally
		{
			body.Free();
		}
	}
}
