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

		// Fire-pit brazier light-host fixture centered at world (-12, 20.02..21.80, 8) -> localX = 12, localZ = 8 -> texel (12, 8)
		// Tagged FlagAoOccluder ONLY (no FlagShadowCaster)!
		AddBox(
			tris,
			new Vector3(-13.65f, 20.02f, 6.5f),
			new Vector3(-10.35f, 21.80f, 9.5f),
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

	[TestCase]
	public void DuringLoadBakeArchitecture_NoMainThreadLocalRdSync_HoldsLoadCover_AndPumpsUploadsBeforeCloth()
	{
		string root = GodotRoot();
		string gpuBaker = File.ReadAllText(Path.Combine(root, "src/Engine/Lighting/Engine_GpuBvhBaker.cs"));
		string androidBake = File.ReadAllText(Path.Combine(root, "src/android/AndroidBakedShadows.cs"));
		string prebuild = File.ReadAllText(Path.Combine(root, "src/Engine/Lighting/Engine_LevelShadowPrebuild.cs"));
		string gameWorld = File.ReadAllText(Path.Combine(root, "src/game/GameWorld.cs"));
		string framePhases = File.ReadAllText(Path.Combine(root, "src/Engine/core/Engine_FramePhases.cs"));

		// 1. Worker-owned local RenderingDevice must NEVER marshal Submit/Sync onto the main thread via RunGpuUpload
		AssertThat(gpuBaker.Contains("RunGpuUpload(", StringComparison.Ordinal)).IsFalse();
		AssertThat(androidBake.Contains("RunGpuUpload(", StringComparison.Ordinal)).IsFalse();

		// 2. EnsureScenarioBuiltAsync must start immediately during level-build instead of sleeping until LoadCoverActive drops
		AssertThat(prebuild.Contains("while (Refii.Core.Engine_ServerSyncs.LoadCoverActive", StringComparison.Ordinal)).IsFalse();
		AssertThat(prebuild.Contains("while (Engine_ServerSyncs.LoadCoverActive", StringComparison.Ordinal)).IsFalse();

		// 3. GameWorld holds the loading cover until IsLoadBakePending clears so gameplay never stutters or pops in
		AssertThat(gameWorld.Contains("Engine_LevelShadowPrebuild.IsLoadBakePending", StringComparison.Ordinal)).IsTrue();
		AssertThat(gameWorld.Contains("!lightingBakePending", StringComparison.Ordinal)).IsTrue();

		// 4. Engine_FrameEndNode drains gpu_upload_pump_end BEFORE cloth_land so ThreadPool workers cannot starve cloth
		int endNodeIdx = framePhases.IndexOf("class Engine_FrameEndNode", StringComparison.Ordinal);
		AssertThat(endNodeIdx).IsGreater(0);
		string endNodeBody = framePhases[endNodeIdx..];
		int pumpEndPos = endNodeBody.IndexOf("\"gpu_upload_pump_end\"", StringComparison.Ordinal);
		int clothLandPos = endNodeBody.IndexOf("\"cloth_land\"", StringComparison.Ordinal);
		AssertThat(pumpEndPos).IsGreater(0);
		AssertThat(clothLandPos).IsGreater(pumpEndPos);

		// 5. Compute shaders use 128-byte int stack[32] without float tstack[] (eliminating VRAM scratch register spilling)
		AssertThat(Engine_GpuBvhBaker.ShadowAtlasShaderSource.Contains("int stack[32];", StringComparison.Ordinal)).IsTrue();
		AssertThat(Engine_GpuBvhBaker.GroundAoShaderSource.Contains("int stack[32];", StringComparison.Ordinal)).IsTrue();
		AssertThat(Engine_GpuBvhBaker.ShadowAtlasShaderSource.Contains("tstack[", StringComparison.Ordinal)).IsFalse();
		AssertThat(Engine_GpuBvhBaker.GroundAoShaderSource.Contains("tstack[", StringComparison.Ordinal)).IsFalse();
	}

	[TestCase]
	public void BvhSubtreeMasks_LocalRootNarrowing_AndParallelBuild_MatchFullRootTraversalBitForBit()
	{
		// Build >36,000 triangles across disjoint clusters to exercise Parallel.Invoke in BvhTree.BuildRecursive
		// as well as subtree presence bitmasks (1u = shadow caster, 2u = terrain AO, 4u = prop AO).
		var tris = new List<Engine_LevelShadowMaps.BvhTriangle>(40000);
		uint terrainFlags = Engine_LevelShadowMaps.BvhTriangle.FlagShadowCaster |
		                    Engine_LevelShadowMaps.BvhTriangle.FlagTerrain |
		                    Engine_LevelShadowMaps.BvhTriangle.FlagAoOccluder;
		uint propCasterFlags = Engine_LevelShadowMaps.BvhTriangle.DefaultFlags;
		uint aoOnlyFixtureFlags = Engine_LevelShadowMaps.BvhTriangle.FlagAoOccluder;

		// 1. Terrain grid in [-200..200] x [-200..200] (100 x 100 quads = 20,000 terrain triangles)
		for (int z = 0; z < 100; z++)
		{
			float z0 = -200f + z * 4f;
			float z1 = z0 + 4f;
			for (int x = 0; x < 100; x++)
			{
				float x0 = -200f + x * 4f;
				float x1 = x0 + 4f;
				float y = MathF.Sin(x * 0.1f) * 0.5f;
				tris.Add(new Engine_LevelShadowMaps.BvhTriangle(new Vector3(x0, y, z0), new Vector3(x1, y, z0), new Vector3(x1, y, z1), terrainFlags));
				tris.Add(new Engine_LevelShadowMaps.BvhTriangle(new Vector3(x0, y, z0), new Vector3(x1, y, z1), new Vector3(x0, y, z1), terrainFlags));
			}
		}

		// 2. Town prop cluster around [-35..-5] x [5..35] (1,400 boxes * 12 = 16,800 prop caster triangles)
		for (int i = 0; i < 1400; i++)
		{
			int gx = i % 35;
			int gz = i / 35;
			float bx = -35f + gx * 0.85f;
			float bz = 5f + gz * 0.72f;
			float bh = 1.5f + (i % 5) * 0.6f;
			AddBox(tris, new Vector3(bx, 0.0f, bz), new Vector3(bx + 0.55f, bh, bz + 0.50f), propCasterFlags);
		}

		// 3. Isolated AO-only brazier fixture far away inside cell [152..156] x [152..156] at Y=[10..12] (elevated above Y=0 terrain)
		AddBox(tris, new Vector3(152.4f, 10f, 152.4f), new Vector3(155.6f, 12f, 155.6f), aoOnlyFixtureFlags);

		AssertThat(tris.Count).IsGreater(36000);
		var bvh = new Engine_LevelShadowMaps.BvhTree(tris);

		// Empty region -> -1
		AssertThat(bvh.FindLocalSubtreeRoot(new Vector3(500f, 0f, 500f), new Vector3(510f, 10f, 510f), 1u)).IsEqual(-1);

		// Elevated AO-only fixture box inside [152.4..155.6, 10..12, 152.4..155.6] has NO shadow casters (1u) -> -1, but DOES have prop AO (4u) -> > 0!
		AssertThat(bvh.FindLocalSubtreeRoot(new Vector3(152.2f, 9.5f, 152.2f), new Vector3(155.8f, 12.5f, 155.8f), Engine_LevelShadowMaps.BvhTree.SubtreeHasShadowCaster)).IsEqual(-1);
		AssertThat(bvh.FindLocalSubtreeRoot(new Vector3(152.2f, 9.5f, 152.2f), new Vector3(155.8f, 12.5f, 155.8f), Engine_LevelShadowMaps.BvhTree.SubtreeHasPropAo)).IsGreater(0);

		// Pure terrain region at (100, -2..2, -100) has NO non-terrain props (4u) -> -1!
		AssertThat(bvh.FindLocalSubtreeRoot(new Vector3(95f, -5f, -105f), new Vector3(105f, 10f, -95f), Engine_LevelShadowMaps.BvhTree.SubtreeHasPropAo)).IsEqual(-1);

		// Localized lamp inside town cluster narrows to a deep internal subtree root (> 0)
		var spotSpec = new Engine_LevelShadowMaps.BakeLightSpec(
			1UL,
			new Vector3(-20f, 3.5f, 20f),
			MaxRange: 8.0f,
			IsSpot: true,
			SpotForward: new Vector3(0.2f, -0.95f, 0.24f).Normalized(),
			SpotCosLimit: MathF.Cos(0.55f));
		Engine_LevelShadowMaps.ComputeBakeLightBounds(in spotSpec, out Vector3 spotMin, out Vector3 spotMax);
		float spotVolume = (spotMax.X - spotMin.X) * (spotMax.Y - spotMin.Y) * (spotMax.Z - spotMin.Z);
		float sphereVolume = 16f * 16f * 16f;
		AssertThat(spotVolume).IsLess(sphereVolume * 0.45f);

		int spotRoot = bvh.FindLocalSubtreeRoot(spotMin, spotMax, Engine_LevelShadowMaps.BvhTree.SubtreeHasShadowCaster);
		AssertThat(spotRoot).IsGreater(0);

		// Verify 250 deterministic rays across shadow, AO, and upward floor-scan queries match root-0 traversal bit-for-bit!
		var rng = new Random(133742);
		int hitsVerified = 0;
		for (int r = 0; r < 250; r++)
		{
			Vector3 origin = new(
				-34f + (float)rng.NextDouble() * 28f,
				0.2f + (float)rng.NextDouble() * 3.5f,
				6f + (float)rng.NextDouble() * 28f);
			Vector3 dir = new Vector3(
				(float)rng.NextDouble() * 2f - 1f,
				(float)rng.NextDouble() * 2f - 1f,
				(float)rng.NextDouble() * 2f - 1f).Normalized();
			float maxRange = 6.0f;
			Vector3 rVec = new(maxRange + 0.02f, maxRange + 0.02f, maxRange + 0.02f);

			// Shadow query (flagMask=1u, flagExpected=1u, subtreeMask=1u)
			int sRoot = bvh.FindLocalSubtreeRoot(origin - rVec, origin + rVec, Engine_LevelShadowMaps.BvhTree.SubtreeHasShadowCaster);
			bool refHit = bvh.TraceClosestHit(origin, dir, 0.05f, maxRange, out float refDist, out Vector3 refNorm, 1u, 1u, 0, 0u);
			float optDist = maxRange;
			Vector3 optNorm = Vector3.Up;
			bool optHit = sRoot >= 0 && bvh.TraceClosestHit(origin, dir, 0.05f, maxRange, out optDist, out optNorm, 1u, 1u, sRoot, Engine_LevelShadowMaps.BvhTree.SubtreeHasShadowCaster);

			AssertThat(optHit).IsEqual(refHit);
			if (refHit)
			{
				hitsVerified++;
				AssertThat(MathF.Abs(optDist - refDist)).IsLess(1e-6f);
				AssertThat(optNorm.DistanceTo(refNorm)).IsLess(1e-6f);
			}

			// Prop-only upward scan query (flagMask=6u, flagExpected=4u, subtreeMask=4u)
			int pRoot = bvh.FindLocalSubtreeRoot(
				new Vector3(origin.X - 0.01f, 0.0f, origin.Z - 0.01f),
				new Vector3(origin.X + 0.01f, 10.0f, origin.Z + 0.01f),
				Engine_LevelShadowMaps.BvhTree.SubtreeHasPropAo);
			bool refUp = bvh.TraceClosestHit(new Vector3(origin.X, 0.0f, origin.Z), Vector3.Up, 0.0f, 10.0f, out float refUpDist, out _, 6u, 4u, 0, 0u);
			float optUpDist = 10.0f;
			bool optUp = pRoot >= 0 && bvh.TraceClosestHit(new Vector3(origin.X, 0.0f, origin.Z), Vector3.Up, 0.0f, 10.0f, out optUpDist, out _, 6u, 4u, pRoot, Engine_LevelShadowMaps.BvhTree.SubtreeHasPropAo);

			AssertThat(optUp).IsEqual(refUp);
			if (refUp)
			{
				AssertThat(MathF.Abs(optUpDist - refUpDist)).IsLess(1e-6f);
			}
		}

		AssertThat(hitsVerified).IsGreater(50);
	}

	[TestCase]
	public void PropBoundsCrop_AndVistaExclusion_ConcentrateAoRectOnPlacedProps()
	{
		// 1. Verify vista backdrop heightfields are filtered while main region heightfields are kept
		AssertThat(Engine_LevelShadowMaps.IsVistaHeightfieldPath("data/levels/albion/vistas/vista_bowerlake.ghf")).IsTrue();
		AssertThat(Engine_LevelShadowMaps.IsVistaHeightfieldPath("data\\levels\\albion\\vistas\\vista_sea.ghf")).IsTrue();
		AssertThat(Engine_LevelShadowMaps.IsVistaHeightfieldPath("data/levels/albion/bowerlake/bowerlake.ghf")).IsFalse();

		// 2. Large 65x65 .ghf spanning [0 .. 1280] m (1.28 km outer terrain/sea) with placed props only in local [200..260] x [300..360]
		var ghf = new GhfHeights
		{
			Width = 65,
			Height = 65,
			Spacing = 20.0f,
			OriginX = 0.0f,
			OriginZ = 0.0f,
			Values = new float[65 * 65],
		};

		var tris = new List<Engine_LevelShadowMaps.BvhTriangle>();
		uint terrainFlags = Engine_LevelShadowMaps.BvhTriangle.FlagShadowCaster |
		                    Engine_LevelShadowMaps.BvhTriangle.FlagTerrain |
		                    Engine_LevelShadowMaps.BvhTriangle.FlagAoOccluder;
		tris.Add(new Engine_LevelShadowMaps.BvhTriangle(new Vector3(-1280f, 0f, 0f), new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 1280f), terrainFlags));
		tris.Add(new Engine_LevelShadowMaps.BvhTriangle(new Vector3(-1280f, 0f, 0f), new Vector3(0f, 0f, 1280f), new Vector3(-1280f, 0f, 1280f), terrainFlags));

		// Placed town props span world X in [-260 .. -200] (local X [200 .. 260]) and world Z in [300 .. 360]
		AddBox(tris, new Vector3(-260f, 0f, 300f), new Vector3(-200f, 6f, 360f), Engine_LevelShadowMaps.BvhTriangle.DefaultFlags);

		var worldBvh = new Engine_LevelShadowMaps.BvhTree(tris);
		AssertThat(worldBvh.HasPropBounds).IsTrue();
		AssertThat(MathF.Abs(worldBvh.PropMin.X - (-260f))).IsLess(1e-4f);
		AssertThat(MathF.Abs(worldBvh.PropMax.X - (-200f))).IsLess(1e-4f);
		AssertThat(MathF.Abs(worldBvh.PropMin.Z - 300f)).IsLess(1e-4f);
		AssertThat(MathF.Abs(worldBvh.PropMax.Z - 360f)).IsLess(1e-4f);

		// Pass isCancelled: () => true after rect calculation so we verify the exact cropped TerrainWorldRect in <0.1 ms
		StaticAoContainer cropped = Engine_LevelAmbientOcclusion.BakeFromBvh(
			worldBvh,
			Array.Empty<Engine_LevelProps.StaticShadowPropBlock>(),
			lightHosts: null,
			fixtureModelPaths: null,
			primaryTerrain: ghf,
			read: _ => null,
			isCancelled: () => true);

		// Expected local crop: [200 - 8 .. 260 + 8] = [192 .. 268] (span 76 m) and [300 - 8 .. 360 + 8] = [292 .. 368] (span 76 m),
		// NOT [0 .. 1280] (1280 m)!
		AssertThat(MathF.Abs(cropped.TerrainWorldRect.X - 192.0f)).IsLess(1e-3f);
		AssertThat(MathF.Abs(cropped.TerrainWorldRect.Y - 292.0f)).IsLess(1e-3f);
		float croppedSpanX = 1.0f / cropped.TerrainWorldRect.Z;
		float croppedSpanZ = 1.0f / cropped.TerrainWorldRect.W;
		AssertThat(MathF.Abs(croppedSpanX - 76.0f)).IsLess(1e-2f);
		AssertThat(MathF.Abs(croppedSpanZ - 76.0f)).IsLess(1e-2f);
	}

	[TestCase]
	public void StreamingSerialization_WriteToFileAtomic_MatchesSerializeByteForByte_AndSnapshotPipelineWorks()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"refii_bake_stream_{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		try
		{
			// 1. StaticShadowMapContainer WriteToFileAtomic vs Serialize
			var shadow = new StaticShadowMapContainer
			{
				AtlasSize = 64,
				SliceCount = 2,
				LightsPerMap = 16,
				PlacedReachScale = 1.25f,
				BuildingInteriorReachScale = 0.85f,
				LightRects = new Dictionary<ulong, Vector4>
				{
					[0x123456789ABCDEF0UL] = new(0.125f, 0.25f, 0.20f, 0.0f),
					[0x0FEDCBA987654321UL] = new(0.5f, 0.5f, 0.20f, 1.0f),
				},
				Texels = new ushort[2 * 64 * 64],
			};
			for (int i = 0; i < shadow.Texels.Length; i++)
			{
				shadow.Texels[i] = (ushort)((i * 37) & 0x7BFF);
			}

			string shadowPath = Path.Combine(tempDir, "test_region.shadows");
			byte[] expectedShadow = Engine_LevelShadowMaps.Serialize(shadow);
			Engine_LevelShadowMaps.WriteToFileAtomic(shadowPath, shadow);
			byte[] diskShadow = File.ReadAllBytes(shadowPath);
			AssertThat(diskShadow).IsEqual(expectedShadow);
			AssertThat(Engine_LevelShadowMaps.IsFileValid(shadowPath, 64, 16, 1.25f, 0.85f)).IsTrue();
			AssertThat(Directory.GetFiles(tempDir, "*.tmp").Length).IsEqual(0);

			// 2. StaticAoContainer WriteToFileAtomic vs Serialize
			var ao = new StaticAoContainer
			{
				PlacementOffsets = new Dictionary<ulong, int> { [42UL] = 0 },
				PropAoBytes = new byte[] { 120, 180, 240, 255 },
				TerrainWidth = 8,
				TerrainHeight = 8,
				TerrainWorldRect = new Vector4(-32f, -32f, 1f / 64f, 1f / 64f),
				TerrainMinHeight = -5f,
				TerrainMaxHeight = 45f,
				TerrainAoBytes = new byte[8 * 8 * 4],
			};
			for (int i = 0; i < ao.TerrainAoBytes.Length; i++)
			{
				ao.TerrainAoBytes[i] = (byte)((i * 53) & 0xFF);
			}

			string aoPath = Path.Combine(tempDir, "test_region.ao");
			byte[] expectedAo = Engine_LevelAmbientOcclusion.Serialize(ao);
			Engine_LevelAmbientOcclusion.WriteToFileAtomic(aoPath, ao);
			byte[] diskAo = File.ReadAllBytes(aoPath);
			AssertThat(diskAo).IsEqual(expectedAo);
			AssertThat(Engine_LevelAmbientOcclusion.IsFileValid(aoPath, expectedResolution: 8)).IsTrue();
			AssertThat(Directory.GetFiles(tempDir, "*.tmp").Length).IsEqual(0);

			// 3. End-to-end feature test: TryBuildWorldGeometryFromSnapshot reuses decoded GhfHeights + resolved lights
			var ghf = new GhfHeights
			{
				Width = 5,
				Height = 5,
				Spacing = 4.0f,
				OriginX = 0.0f,
				OriginZ = 0.0f,
				Values = new float[25],
			};
			var lightPlacement = new Engine_LevelLights.LightPlacement
			{
				Active = true,
				Type = Engine_LevelLights.LightTypePoint,
				ColourLinear = new Vector3(1f, 0.8f, 0.6f),
				Intensity = 2.0f,
				Range = 10.0f,
			};
			var snapshot = new Engine_LevelProps.StaticLightingBakeSnapshot
			{
				GdbPath = "levels/albion/test.gdb",
				TerrainHeights = new List<GhfHeights> { ghf },
				ResolvedLights = new List<Engine_LevelLights.ResolvedLightInstance>
				{
					new(0x1111u, 0, Engine_LevelLights.ComposeLightKey(0x1111u, 0), lightPlacement, new Transform3D(Basis.Identity, new Vector3(-8f, 2.5f, 8f)), "lamp_0"),
				},
			};
			var level = new LevelEntry { World = "albion", Name = "test", Scenario = "defaultscenario" };
			bool ok = Engine_LevelShadowMaps.TryBuildWorldGeometryFromSnapshot(
				level,
				snapshot,
				read: _ => null,
				isCancelled: () => false,
				out Engine_LevelShadowMaps.BvhTree? builtBvh,
				out _,
				out _,
				out _,
				out List<Engine_LevelShadowMaps.BakeLightSpec> builtLights,
				out List<GhfHeights> builtTerrains,
				out string outGdb,
				out int triCount,
				out _);

			AssertThat(ok).IsTrue();
			AssertThat(builtBvh is not null).IsTrue();
			AssertThat(triCount).IsEqual(32); // (5-1)*(5-1)*2 terrain triangles
			AssertThat(builtLights.Count).IsEqual(1);
			AssertThat(builtTerrains.Count).IsEqual(1);
			AssertThat(outGdb).IsEqual("levels/albion/test.gdb");
		}
		finally
		{
			if (Directory.Exists(tempDir))
			{
				Directory.Delete(tempDir, recursive: true);
			}
		}
	}

	[TestCase]
	public void AdaptivePacer_WaitForNextDrawnFrame_AndClothSolveGate_WorkStealingAndConcurrency()
	{
		// 1. Adaptive slice sizing & yield frames
		AssertThat(Engine_GpuBvhBaker.ComputeNextSliceUnits(16, elapsedMs: 3.0, targetSliceMs: 6.0, minUnits: 1, maxUnits: 64)).IsEqual(32);
		AssertThat(Engine_GpuBvhBaker.ComputeNextSliceUnits(16, elapsedMs: 24.0, targetSliceMs: 6.0, minUnits: 1, maxUnits: 64)).IsEqual(4);
		AssertThat(Engine_GpuBvhBaker.ComputeYieldFrames(5.0, 6.0)).IsEqual(1);
		AssertThat(Engine_GpuBvhBaker.ComputeYieldFrames(12.0, 6.0)).IsEqual(2);
		AssertThat(Engine_GpuBvhBaker.ComputeYieldFrames(25.0, 6.0)).IsEqual(3);

		// 2. Engine_ServerSyncs.WaitForNextDrawnFrame wakes immediately on MarkDrawEnd()
		using var pacer = new Refii.Core.Engine_ServerSyncs.GpuUploadPacer();
		long beforeFrame = pacer.DrawnFrame;
		var signalTask = System.Threading.Tasks.Task.Run(async () =>
		{
			await System.Threading.Tasks.Task.Delay(15);
			pacer.MarkDrawEnd();
		});
		bool frameObserved = pacer.WaitForNextDrawnFrame(frames: 1, timeoutMs: 500);
		signalTask.GetAwaiter().GetResult();
		AssertThat(frameObserved).IsTrue();
		AssertThat(pacer.DrawnFrame).IsEqual(beforeFrame + 1);

		// 3. ClothSolveGate main-thread work-stealing prevents ThreadPool starvation stalls without double execution
		int solveCount = 0;
		var gate = new Refii.Physics.ClothSolveGate(() => System.Threading.Interlocked.Increment(ref solveCount));

		// Simulate starved ThreadPool: step issued, worker hasn't claimed yet -> main thread steals inline!
		gate.IssueWithoutQueueing();
		AssertThat(gate.HasPendingStep).IsTrue();
		bool stolen = gate.CompleteOrSteal();
		AssertThat(stolen).IsTrue();
		AssertThat(solveCount).IsEqual(1);
		AssertThat(gate.HasPendingStep).IsFalse();

		// Late ThreadPool callback arriving afterwards must be a safe no-op!
		AssertThat(gate.TryExecuteWorkerStep()).IsFalse();
		AssertThat(solveCount).IsEqual(1);

		// Normal ThreadPool execution path
		gate.Schedule();
		gate.CompleteOrSteal();
		AssertThat(solveCount).IsEqual(2);
		AssertThat(gate.HasPendingStep).IsFalse();
	}

	[TestCase]
	public void ExplicitBothSidesCulling_DetectsPosterTwins_LeavesSingleCardsTwoSided_AndRegistersShaderVariants()
	{
		// 1. ESA_Shop_Price_List-style double-sided poster: 2 front triangles + 2 opposite-wound twin back triangles at identical XYZ
		float[] posterPositions =
		{
			-0.6f, 0.0f, 0.01f, // 0: front BL
			 0.6f, 0.0f, 0.01f, // 1: front BR
			 0.6f, 2.0f, 0.01f, // 2: front TR
			-0.6f, 2.0f, 0.01f, // 3: front TL
			-0.6f, 0.0f, 0.01f, // 4: back BL (identical XYZ)
			 0.6f, 0.0f, 0.01f, // 5: back BR
			 0.6f, 2.0f, 0.01f, // 6: back TR
			-0.6f, 2.0f, 0.01f, // 7: back TL
		};
		uint[] posterIndices =
		{
			0, 1, 2,
			0, 2, 3,
			4, 6, 5,
			4, 7, 6,
		};
		AssertThat(Engine_MdlGeometry.DetectExplicitBothSides(posterPositions, posterIndices)).IsTrue();

		// 2. Single-sided foliage card -> false
		uint[] singleCardIndices = { 0, 1, 2, 0, 2, 3 };
		AssertThat(Engine_MdlGeometry.DetectExplicitBothSides(posterPositions.AsSpan(0, 12), singleCardIndices)).IsFalse();

		// 3. Intersecting 90-degree cross-card shrub -> false (cards do not share all 3 vertices)
		float[] crossCardPositions =
		{
			-1f, 0f,  0f,  1f, 0f, 0f,  1f, 2f, 0f, -1f, 2f,  0f,
			 0f, 0f, -1f,  0f, 0f, 1f,  0f, 2f, 1f,  0f, 2f, -1f,
		};
		uint[] crossCardIndices = { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
		AssertThat(Engine_MdlGeometry.DetectExplicitBothSides(crossCardPositions, crossCardIndices)).IsFalse();

		// 4. Closed 12mm wooden frame slab -> false (front & back faces separated in Z)
		float[] slabPositions =
		{
			-0.6f, 0f, 0.000f,  0.6f, 0f, 0.000f,  0.6f, 2f, 0.000f, -0.6f, 2f, 0.000f,
			-0.6f, 0f, 0.012f,  0.6f, 0f, 0.012f,  0.6f, 2f, 0.012f, -0.6f, 2f, 0.012f,
		};
		AssertThat(Engine_MdlGeometry.DetectExplicitBothSides(slabPositions, posterIndices)).IsFalse();

		// 5. Shared material key & shader variant registration
		var matRec = new MdlMaterial { Diffuse = "esa_shop_clutter.tex", Normal = "esa_shop_clutter_norm.tex" };
		var info = new MdlInfo();
		string singleKey = Engine_MdlMaterial.BuildSharedKey(matRec, info, authoredNormals: true, tangentBasis: true, explicitBothSides: false);
		string twinKey = Engine_MdlMaterial.BuildSharedKey(matRec, info, authoredNormals: true, tangentBasis: true, explicitBothSides: true);
		AssertThat(singleKey).IsNotEqual(twinKey);

		string root = GodotRoot();
		AssertThat(File.Exists(Path.Combine(root, "src/Engine/material/standard_material_cutout_culled.gdshader"))).IsTrue();
		AssertThat(File.Exists(Path.Combine(root, "src/Engine/material/standard_material_translucent_culled.gdshader"))).IsTrue();
		AssertThat(Engine_ShadowProxy.CanUseDedicatedCutoutCaster(skinned: true, always: false, Engine_ShaderFamily.StandardCutoutCulledPath)).IsTrue();
	}

	[TestCase]
	public void StaticShadowBilinearPcfShader_UsesPerTexelComparison_RayTangentMultiTap_AndDxilSafeConstructs()
	{
		string root = GodotRoot();
		string lightingInc = File.ReadAllText(Path.Combine(root, "src/Engine/material/fable_lighting.gdshaderinc"));

		int helperStart = lightingInc.IndexOf("float fable_sample_static_shadow_bilinear_pcf(", StringComparison.Ordinal);
		int helperEnd = lightingInc.IndexOf("float fable_attenuation_weighted(", StringComparison.Ordinal);
		AssertThat(helperStart).IsGreater(0);
		AssertThat(helperEnd).IsGreater(helperStart);

		string shadowBlock = lightingInc.Substring(helperStart, helperEnd - helperStart);

		// Must compare each of the 4 integer texel fetches against receiver BEFORE bilinearly mixing visibility
		AssertThat(shadowBlock.Split("texelFetch(fable_static_shadow_atlas,").Length - 1).IsEqual(4);
		AssertThat(shadowBlock.Split("smoothstep(edge0, receiver,").Length - 1).IsEqual(4);
		AssertThat(shadowBlock.Contains("mix(mix(s00, s10, frac_uv.x), mix(s01, s11, frac_uv.x), frac_uv.y)", StringComparison.Ordinal)).IsTrue();

		// Must NOT linearly interpolate raw R16F depths or cut off multi-tap PCF at 7.5m, and must use zero implicit derivatives
		AssertThat(shadowBlock.Contains("textureLod(", StringComparison.Ordinal)).IsFalse();
		AssertThat(shadowBlock.Contains("texture(", StringComparison.Ordinal)).IsFalse();
		AssertThat(shadowBlock.Contains("dFdx(", StringComparison.Ordinal)).IsFalse();
		AssertThat(shadowBlock.Contains("dFdy(", StringComparison.Ordinal)).IsFalse();
		AssertThat(shadowBlock.Contains("d >= 7.5", StringComparison.Ordinal)).IsFalse();
	}

	[TestCase]
	public void MultiScaleNormalCavity_AndSeamWeldedCreviceSelfAo_DarkenWideGroovesAndHardEdgeCorners()
	{
		// 1. Multi-scale ORM normal + height cavity on a 64x64 texture with a flat left half and an 8-texel-wide vertical groove at x=48
		const int w = 64;
		const int h = 64;
		byte[] rgba = new byte[w * h * 4];
		byte[] solvedHeight = new byte[w * h];
		Array.Fill(solvedHeight, (byte)128);

		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				float nx = 0.0f;
				float dx = x - 48.0f;
				if (MathF.Abs(dx) <= 6.0f)
				{
					// Smooth 12-texel-wide concave groove: left slope tilts right (+nx), right slope tilts left (-nx)
					nx = -MathF.Sin((dx / 6.0f) * (MathF.PI * 0.5f)) * 0.75f;
					solvedHeight[(y * w) + x] = (byte)Math.Clamp(128 - (int)MathF.Round((1.0f - (MathF.Abs(dx) / 6.0f)) * 70.0f), 0, 255);
				}

				int o = ((y * w) + x) * 4;
				rgba[o] = (byte)Math.Clamp(MathF.Round((nx * 0.5f + 0.5f) * 255.0f), 0f, 255f);
				rgba[o + 1] = 128;
				rgba[o + 2] = 255;
				rgba[o + 3] = 255;
			}
		}

		byte[] cavityNormalOnly = Refii.Textures.Engine_TexOrm.CavityFromNormalBytes(rgba, w, h, strength: 0.5f, wrap: false);
		byte[] cavityWithHeight = Refii.Textures.Engine_TexOrm.CavityFromNormalBytes(rgba, w, h, strength: 0.5f, wrap: false, solvedHeight);

		byte flatAo = cavityWithHeight[(32 * w) + 12];
		byte grooveNormalAo = cavityNormalOnly[(32 * w) + 48];
		byte grooveHeightAo = cavityWithHeight[(32 * w) + 48];

		AssertThat((int)flatAo).IsEqual(255);
		AssertThat((int)grooveNormalAo).IsLess(175);
		AssertThat((int)grooveHeightAo).IsLess((int)grooveNormalAo);

		// 2. Hard-edge split-seam 90-degree concave corner (distinct SourceVertices at identical 3D corner coordinates!)
		// vs hard-edge 90-degree convex corner
		var splitSeamGeo = new MdlGeometry
		{
			Name = "hard_seam_test",
			Positions = new float[]
			{
				// Concave L-corner at X = 0: horizontal floor [0..0.4] (n = +Y) + vertical wall rising [0..0.4] (n = +X)
				0.0f, 0.0f, 0.0f,   0.4f, 0.0f, 0.0f,   0.4f, 0.0f, 0.4f,   0.0f, 0.0f, 0.4f, // 0..3: floor
				0.0f, 0.0f, 0.0f,   0.0f, 0.4f, 0.0f,   0.0f, 0.4f, 0.4f,   0.0f, 0.0f, 0.4f, // 4..7: rising wall (split seam!)

				// Convex L-corner at X = 5: horizontal top [5..5.4] (n = +Y) + vertical wall dropping [0..-0.4] (n = -X)
				5.0f, 0.0f, 0.0f,   5.4f, 0.0f, 0.0f,   5.4f, 0.0f, 0.4f,   5.0f, 0.0f, 0.4f, // 8..11: top
				5.0f, 0.0f, 0.0f,   5.0f,-0.4f, 0.0f,   5.0f,-0.4f, 0.4f,   5.0f, 0.0f, 0.4f, // 12..15: dropping wall
			},
			Normals = new float[]
			{
				 0f, 1f, 0f,   0f, 1f, 0f,   0f, 1f, 0f,   0f, 1f, 0f,
				 1f, 0f, 0f,   1f, 0f, 0f,   1f, 0f, 0f,   1f, 0f, 0f,
				 0f, 1f, 0f,   0f, 1f, 0f,   0f, 1f, 0f,   0f, 1f, 0f,
				-1f, 0f, 0f,  -1f, 0f, 0f,  -1f, 0f, 0f,  -1f, 0f, 0f,
			},
			Uvs = new float[32],
			Indices = new uint[]
			{
				0, 1, 2,  0, 2, 3,
				4, 5, 6,  4, 6, 7,
				8, 9, 10, 8, 10, 11,
				12, 13, 14, 12, 14, 15,
			},
			SourceVertices = new int[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
		};

		float[] seamAo = Engine_ModelSelfAo.ComputeFastCreviceAndBaseAo(
			new[] { splitSeamGeo },
			sourceVertexTotal: 16,
			includeGroundPlane: false);

		// Split-seam concave corner vertices (0 and 4) detect the perpendicular rising wall (< 0.65),
		// while split-seam convex corner vertices (8 and 12) remain completely unoccluded (>= 0.99)!
		AssertThat(seamAo[0]).IsLess(0.65f);
		AssertThat(seamAo[4]).IsLess(0.65f);
		AssertThat(seamAo[8]).IsGreaterEqual(0.99f);
		AssertThat(seamAo[12]).IsGreaterEqual(0.99f);
	}

	[TestCase]
	public void SahBvh_CroppedAoBvh_XzSubtreeGrid_PhysicalAoDensity_And8kShadowPacking_MatchReferenceAndHitCache()
	{
		// 1. 8K shadow atlas packs 121 (11x11 = 744x744/lamp) lights per slice; load-cover target slice prevents 8-row halo thrashing
		AssertThat(Engine_LevelShadowMaps.GetLightsPerMapForResolution(8192)).IsEqual(121);
		AssertThat(Engine_LevelShadowMaps.GetLightsPerMapForResolution(4096)).IsEqual(64);
		AssertThat(Engine_LevelShadowMaps.GetLightsPerMapForResolution(2048)).IsEqual(36);
		AssertThat(Engine_GpuBvhBaker.LoadCoverTargetSliceMs).IsGreaterEqual(100.0);
		AssertThat(Engine_GpuBvhBaker.GroundAoShaderSource.Contains("HEMISPHERE_KERNEL[12]", StringComparison.Ordinal)).IsTrue();
		AssertThat(Engine_GpuBvhBaker.GroundAoShaderSource.Contains("xz_grid.cells[", StringComparison.Ordinal)).IsTrue();
		AssertThat(Engine_GpuBvhBaker.GroundAoShaderSource.Contains("bool trace_any_hit(", StringComparison.Ordinal)).IsTrue();
		AssertThat(Engine_GpuBvhBaker.GroundAoFilterShaderSource.Contains("for (int dy = -2; dy <= 2; ++dy)", StringComparison.Ordinal)).IsTrue();

		// 2. Physical texel-density cap & aspect-ratio scaling + IsFileValid cache hit without infinite rebake
		Engine_LevelAmbientOcclusion.ComputeTargetAoDimensions(80.0f, 64.0f, presetResolution: 4096, out int cave4kW, out int cave4kH);
		AssertThat(cave4kW).IsEqual(1000);
		AssertThat(cave4kH).IsEqual(800);

		Engine_LevelAmbientOcclusion.ComputeTargetAoDimensions(80.0f, 64.0f, presetResolution: 8192, out int cave8kW, out int cave8kH);
		AssertThat(cave8kW).IsEqual(1600);
		AssertThat(cave8kH).IsEqual(1280);

		string tempAoPath = Path.Combine(Path.GetTempPath(), $"refii_cave_density_{Guid.NewGuid():N}.ao");
		try
		{
			var caveAo = new StaticAoContainer
			{
				PlacementOffsets = new Dictionary<ulong, int>(),
				PropAoBytes = Array.Empty<byte>(),
				TerrainWidth = cave4kW,
				TerrainHeight = cave4kH,
				TerrainWorldRect = new Vector4(-40f, -32f, 1.0f / 80.0f, 1.0f / 64.0f),
				TerrainMinHeight = 0f,
				TerrainMaxHeight = 20f,
				TerrainAoBytes = new byte[cave4kW * cave4kH * 4],
			};
			Engine_LevelAmbientOcclusion.WriteToFileAtomic(tempAoPath, caveAo);

			// Valid at 4096 (1000x800 matches physical cap for 80x64m!), invalid when user upgrades to 8192 (wants 1600x1280)
			AssertThat(Engine_LevelAmbientOcclusion.IsFileValid(tempAoPath, expectedResolution: 4096)).IsTrue();
			AssertThat(Engine_LevelAmbientOcclusion.IsFileValid(tempAoPath, expectedResolution: 8192)).IsFalse();
		}
		finally
		{
			if (File.Exists(tempAoPath))
			{
				File.Delete(tempAoPath);
			}
		}

		// 3. Cropped AO SAH BVH prunes distant non-AO/outer triangles while preserving exact local AO hits & XZ grid roots
		var tris = new List<Engine_LevelShadowMaps.BvhTriangle>(5000);
		// 4,500 distant shadow-only / outer cliff triangles at X = [500..900]
		for (int i = 0; i < 375; i++)
		{
			float x = 500f + (i % 25) * 15f;
			float z = 500f + (i / 25) * 15f;
			AddBox(tris, new Vector3(x, 0f, z), new Vector3(x + 2f, 4f, z + 2f), Engine_LevelShadowMaps.BvhTriangle.FlagShadowCaster);
		}
		// 500 local town AO triangles in world X = [-40..-10] (local X [10..40]), world Z = [10..40]
		for (int i = 0; i < 42; i++)
		{
			float wx = -38f + (i % 7) * 4f;
			float wz = 12f + (i / 7) * 4f;
			AddBox(tris, new Vector3(wx, 0f, wz), new Vector3(wx + 1.5f, 3f, wz + 1.5f), Engine_LevelShadowMaps.BvhTriangle.DefaultFlags);
		}

		var fullBvh = new Engine_LevelShadowMaps.BvhTree(tris);
		Engine_LevelShadowMaps.BvhTree croppedBvh = fullBvh.BuildCroppedAoBvh(
			cropOriginLocalX: 8.0f,
			cropOriginLocalZ: 8.0f,
			cropSpanX: 36.0f,
			cropSpanZ: 36.0f,
			margin: 3.0f);

		AssertThat(ReferenceEquals(croppedBvh, fullBvh)).IsFalse();
		AssertThat(croppedBvh.TriangleCount).IsLess(fullBvh.TriangleCount / 4);

		int[] grid = croppedBvh.BuildXzSubtreeGrid(8.0f, 8.0f, 36.0f, 36.0f, aoRadius: 2.6f, gridRes: 16);
		AssertThat(grid.Length).IsEqual(16 * 16 * 2);
		int nonEmptyCells = 0;
		for (int i = 0; i < grid.Length; i += 2)
		{
			if (grid[i] >= 0)
			{
				nonEmptyCells++;
			}
		}
		AssertThat(nonEmptyCells).IsGreater(0);
	}

	[TestCase]
	public void ClothUploadGate_DistanceScale_SunAndTreeInterpolation_CompositeAo_BrickNeutrality_AndFurShaderInvariants()
	{
		// 1. ClothSolveGate: fast worker finishing BEFORE Land() must still report HasUnuploadedStep = true
		//    and consume upload exactly once via TryConsumeUpload().
		int solves = 0;
		var gate = new Refii.Physics.ClothSolveGate(() => solves++);
		long gen = gate.IssueWithoutQueueing();
		AssertThat(gen).IsEqual(1L);
		AssertThat(gate.HasPendingStep).IsTrue();
		AssertThat(gate.HasUnuploadedStep).IsTrue();
		AssertThat(gate.TryExecuteWorkerStep()).IsTrue();
		AssertThat(solves).IsEqual(1);
		AssertThat(gate.HasPendingStep).IsFalse();
		AssertThat(gate.HasUnuploadedStep).IsTrue();
		AssertThat(gate.TryConsumeUpload()).IsTrue();
		AssertThat(gate.HasUnuploadedStep).IsFalse();
		AssertThat(gate.TryConsumeUpload()).IsFalse();

		// 2. ClothDistanceScale & EffectiveMaxDistance scaling
		try
		{
			AssertThat(Refii.Physics.Engine_ClothBody.EffectiveMaxDistance(20.0f, 0.0f)).IsEqual(0.0f);
			AssertThat(Refii.Physics.Engine_ClothBody.EffectiveMaxDistance(20.0f, 1.25f)).IsEqual(25.0f);
			AssertThat(Refii.Physics.Engine_ClothBody.EffectiveMaxDistance(24.0f, 3.0f)).IsEqual(72.0f);
			Refii.Physics.Engine_ClothBody.DistanceScaleOverride = 2.5f;
			AssertThat(Refii.Physics.Engine_ClothBody.ClothDistanceScale).IsEqual(2.5f);
		}
		finally
		{
			Refii.Physics.Engine_ClothBody.DistanceScaleOverride = null;
		}

		// 3. SunClockInterpolator: monotonic 120 Hz sub-tick smoothing across 15 Hz guest ticks,
		//    midnight wrap (0.9999 -> 0.0001), freeze stability, and >0.004 jump snap.
		var sunClock = new SunClockInterpolator();
		float rawClock = 0.25f;
		const float ratePerSec = 1.0f / 2880.0f; // 48-minute day
		const float guestDt = 1.0f / 15.0f;
		const float displayDt = 1.0f / 120.0f; // 8 display frames per guest tick

		float prev = sunClock.Step(rawClock, frozen: false, displayDt);
		AssertThat(prev).IsEqual(0.25f);

		// Prime 4 guest ticks (32 frames at 120 Hz), then verify all 8 sub-frames of tick 5 advance strictly > 0
		for (int tick = 1; tick <= 5; tick++)
		{
			rawClock += ratePerSec * guestDt;
			for (int sub = 0; sub < 8; sub++)
			{
				float cur = sunClock.Step(rawClock, frozen: false, displayDt);
				if (tick >= 3)
				{
					AssertThat(cur).IsGreater(prev);
				}
				prev = cur;
			}
		}

		// Freeze holds exact value with zero drift
		float frozenVal = sunClock.Step(rawClock, frozen: true, displayDt);
		AssertThat(sunClock.Step(rawClock, frozen: true, displayDt)).IsEqual(frozenVal);

		// Large jump (sleep / fast travel) snaps immediately
		AssertThat(sunClock.Step(0.75f, frozen: false, displayDt)).IsEqual(0.75f);

		// Midnight wrap (0.99995 -> 0.00005) interpolates forward cleanly without rewinding
		var midnightClock = new SunClockInterpolator();
		_ = midnightClock.Step(0.99990f, frozen: false, displayDt);
		float m1 = midnightClock.Step(0.99996f, frozen: false, displayDt);
		float m2 = midnightClock.Step(0.00004f, frozen: false, displayDt);
		AssertThat(m1 >= 0.99990f || m1 < 0.001f).IsTrue();
		AssertThat(m2 >= 0.99990f || m2 <= 0.00005f).IsTrue();

		// Tree sway clock interpolation advances smoothly across sub-tick frames
		double treeSmooth = double.NaN;
		double t0 = Engine_GuestAtmosphere.StepInterpolatedSeconds(ref treeSmooth, 100.0, displayDt);
		AssertThat(t0).IsEqual(100.0);
		double t1 = Engine_GuestAtmosphere.StepInterpolatedSeconds(ref treeSmooth, 100.0 + guestDt, displayDt);
		double t2 = Engine_GuestAtmosphere.StepInterpolatedSeconds(ref treeSmooth, 100.0 + guestDt, displayDt);
		AssertThat(t1).IsGreater(t0);
		AssertThat(t2).IsGreater(t1);

		// 4. Cross-part Composite Self-AO: vertical plaster wall + projecting horizontal timber beam / eave
		var wallSurf = new Engine_ModelSelfAo.CompositeSurfaceInput(
			Vertices: new[]
			{
				new Vector3(-1.0f, 0.0f, 0.0f), // 0: bottom-left
				new Vector3( 1.0f, 0.0f, 0.0f), // 1: bottom-right
				new Vector3( 1.0f, 2.0f, 0.0f), // 2: top-right (right under overhanging eave!)
				new Vector3(-1.0f, 2.0f, 0.0f), // 3: top-left (right under overhanging eave!)
				new Vector3( 0.0f, 1.95f, 0.0f), // 4: T-junction center directly under projecting eave!
			},
			Normals: new[]
			{
				new Vector3(0f, 0f, 1f),
				new Vector3(0f, 0f, 1f),
				new Vector3(0f, 0f, 1f),
				new Vector3(0f, 0f, 1f),
				new Vector3(0f, 0f, 1f),
			},
			Indices: new[] { 0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4 });

		var eaveSurf = new Engine_ModelSelfAo.CompositeSurfaceInput(
			Vertices: new[]
			{
				// Bottom face of eave projecting +Z [0..1.2m] at Y = 2.0m (facing -Y) + open top face at Y = 2.2m (facing +Y)
				new Vector3(-1.0f, 2.0f, 0.0f),
				new Vector3( 1.0f, 2.0f, 0.0f),
				new Vector3( 1.0f, 2.0f, 1.2f),
				new Vector3(-1.0f, 2.0f, 1.2f),

				new Vector3(-1.0f, 2.2f, 0.0f),
				new Vector3( 1.0f, 2.2f, 0.0f),
				new Vector3( 1.0f, 2.2f, 1.2f),
				new Vector3(-1.0f, 2.2f, 1.2f),
			},
			Normals: new[]
			{
				new Vector3(0f, -1f, 0f), new Vector3(0f, -1f, 0f), new Vector3(0f, -1f, 0f), new Vector3(0f, -1f, 0f),
				new Vector3(0f,  1f, 0f), new Vector3(0f,  1f, 0f), new Vector3(0f,  1f, 0f), new Vector3(0f,  1f, 0f),
			},
			Indices: new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7 });

		float[][] compAo = Engine_ModelSelfAo.ComputeCompositeSelfAo(new[] { wallSurf, eaveSurf }, allowSynchronousBvh: true);
		AssertThat(compAo.Length).IsEqual(2);
		// Wall T-junction vertex (4) tucked right under the eave receives strong cross-part occlusion (< 0.72),
		// while exposed roof top vertex (eave 6) stays fully open (>= 0.98)!
		AssertThat(compAo[0][4]).IsLess(0.72f);
		AssertThat(compAo[1][6]).IsGreaterEqual(0.98f);

		// 5. orm-v4 Herringbone Brick Neutrality: raised 12-texel brick plateaus separated by 2-texel mortar joints
		const int bw = 64, bh = 32;
		byte[] brickNormals = new byte[bw * bh * 4];
		for (int y = 0; y < bh; y++)
		{
			for (int x = 0; x < bw; x++)
			{
				int phase = x % 14; // 0: right-tilted groove wall (+nx), 1: left-tilted groove wall (-nx), 2..13: flat brick plateau
				float nx = phase == 0 ? 0.70f : (phase == 1 ? -0.70f : 0.0f);
				int o = ((y * bw) + x) * 4;
				brickNormals[o] = (byte)Math.Clamp(MathF.Round((nx * 0.5f + 0.5f) * 255f), 0f, 255f);
				brickNormals[o + 1] = 128;
				brickNormals[o + 2] = 255;
				brickNormals[o + 3] = 255;
			}
		}
		byte[] brickCavity = Refii.Textures.Engine_TexOrm.CavityFromNormalBytes(brickNormals, bw, bh, strength: 0.5f, wrap: true);
		byte mortarJointAo = brickCavity[(16 * bw) + 1];  // inside mortar joint
		byte brickCenterAo = brickCavity[(16 * bw) + 8];  // middle of flat 12-texel brick face
		AssertThat((int)mortarJointAo).IsLess(190);
		AssertThat((int)brickCenterAo).IsGreaterEqual(250);

		// 6. 4-Split Sun Cascades & Dog Fur / Static Shadow Shader Invariants
		(float s1, float s2, float s3) = Engine_LevelLighting.ComputeCascadeSplits(4, 150.0f);
		AssertThat(MathF.Abs(s1 - (12.0f / 150.0f))).IsLess(1e-4f);
		AssertThat(s2).IsGreater(s1);
		AssertThat(s3).IsGreater(s2);

		string root = GodotRoot();
		string furShader = File.ReadAllText(Path.Combine(root, "src/Engine/material/fur_shell.gdshaderinc"));
		AssertThat(furShader.Contains("uniform sampler2D normal_tex : hint_normal", StringComparison.Ordinal)).IsTrue();
		AssertThat(furShader.Contains("uniform sampler2D orm_tex : hint_default_white", StringComparison.Ordinal)).IsTrue();
		AssertThat(furShader.Contains("NORMAL_MAP = nm;", StringComparison.Ordinal)).IsTrue();
		AssertThat(furShader.Contains("sin_tl = sqrt(", StringComparison.Ordinal)).IsTrue();
		AssertThat(furShader.Contains("fable_light_debug == 11", StringComparison.Ordinal)).IsTrue();

		string lightingShader = File.ReadAllText(Path.Combine(root, "src/Engine/material/fable_lighting.gdshaderinc"));
		AssertThat(lightingShader.Contains("float fable_evaluate_prop_baked_ao_normal(", StringComparison.Ordinal)).IsTrue();
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
