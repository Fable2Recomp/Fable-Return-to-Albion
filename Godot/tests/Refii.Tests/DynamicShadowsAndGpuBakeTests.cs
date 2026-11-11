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
