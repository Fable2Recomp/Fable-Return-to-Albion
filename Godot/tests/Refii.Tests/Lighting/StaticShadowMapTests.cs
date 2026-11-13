namespace Refii.Tests.Lighting;

using System;
using System.Collections.Generic;
using GdUnit4;
using Godot;
using Refii.Level;
using Refii.Lighting;
using static GdUnit4.Assertions;

[TestSuite]
public class StaticShadowMapTests
{
	[TestCase]
	public void TestOctahedralRoundTripAcrossOctants()
	{
		Vector3[] testDirs =
		{
			new Vector3(1f, 0f, 0f),
			new Vector3(-1f, 0f, 0f),
			new Vector3(0f, 1f, 0f),
			new Vector3(0f, -1f, 0f),
			new Vector3(0f, 0f, 1f),
			new Vector3(0f, 0f, -1f),
			new Vector3(1f, 2f, 3f).Normalized(),
			new Vector3(-2f, 1f, -3f).Normalized(),
			new Vector3(3f, -4f, -1f).Normalized(),
			new Vector3(-1f, -1f, 1f).Normalized(),
		};

		foreach (Vector3 dir in testDirs)
		{
			Vector2 uv = Engine_LevelShadowMaps.OctahedralUvFromDirection(dir);
			AssertThat(uv.X >= 0f && uv.X <= 1f).IsTrue();
			AssertThat(uv.Y >= 0f && uv.Y <= 1f).IsTrue();

			Vector3 roundTrip = Engine_LevelShadowMaps.DirectionFromOctahedralUv(uv);
			float dot = dir.Dot(roundTrip);
			AssertThat(dot > 0.999f).IsTrue();
		}
	}

	[TestCase]
	public void TestBinarySerializeDeserializeRoundTrip()
	{
		ulong keyA = Engine_LevelLights.ComposeLightKey(0x12345678u, 0);
		ulong keyB = Engine_LevelLights.ComposeLightKey(0x9ABCDEF0u, 2);

		var texels = new ushort[2 * 64 * 64];
		for (int i = 0; i < texels.Length; i++)
		{
			texels[i] = BitConverter.HalfToUInt16Bits((Half)(i * 0.25f));
		}

		var original = new StaticShadowMapContainer
		{
			AtlasSize = 64,
			SliceCount = 2,
			LightsPerMap = 16,
			PlacedReachScale = 1.25f,
			BuildingInteriorReachScale = 0.75f,
			LightRects = new Dictionary<ulong, Vector4>
			{
				[keyA] = new Vector4(0.015625f, 0.015625f, 0.46875f, 0.0f),
				[keyB] = new Vector4(0.515625f, 0.015625f, 0.46875f, 1.0f),
			},
			Texels = texels,
		};

		byte[] bytes = Engine_LevelShadowMaps.Serialize(original);
		bool ok = Engine_LevelShadowMaps.TryDeserialize(
			bytes, out StaticShadowMapContainer restored, out string error);

		AssertThat(ok).IsTrue();
		AssertThat(error).IsEmpty();
		AssertThat(restored.AtlasSize).IsEqual(64);
		AssertThat(restored.SliceCount).IsEqual(2);
		AssertThat(restored.LightsPerMap).IsEqual(16);
		AssertThat(MathF.Abs(restored.PlacedReachScale - 1.25f) < 1e-4f).IsTrue();
		AssertThat(MathF.Abs(restored.BuildingInteriorReachScale - 0.75f) < 1e-4f).IsTrue();
		AssertThat(restored.LightRects.Count).IsEqual(2);
		AssertThat(restored.LightRects[keyA]).IsEqual(original.LightRects[keyA]);
		AssertThat(restored.LightRects[keyB]).IsEqual(original.LightRects[keyB]);
		AssertThat(restored.Texels.Length).IsEqual(texels.Length);
		AssertThat(restored.Texels[123]).IsEqual(texels[123]);
	}

	[TestCase]
	public void TestBvhFromAnExactArrayMatchesTheListBuildAndKeepsOnlyUsedNodes()
	{
		// A few hundred scattered triangles: the tree a bake builds from its exact-size array must be the
		// one the list constructor builds, node for node, and must not keep the build's spare nodes.
		var list = new List<Engine_LevelShadowMaps.BvhTriangle>();
		var rng = new Random(7);
		for (int i = 0; i < 300; i++)
		{
			var at = new Vector3(rng.Next(-50, 50), rng.Next(0, 10), rng.Next(-50, 50));
			list.Add(new(at, at + new Vector3(1f, 0f, 0f), at + new Vector3(0f, 1f, 0.5f)));
		}

		var fromList = new Engine_LevelShadowMaps.BvhTree(list);
		var fromArray = new Engine_LevelShadowMaps.BvhTree(list.ToArray());

		AssertThat(fromArray.TriangleCount).IsEqual(300);
		AssertThat(fromArray.NodeCount).IsEqual(fromList.NodeCount);
		AssertThat(fromArray.NodeCount < 600).IsTrue();
		fromList.ExportGpuBuffers(out byte[] listNodes, out byte[] listTris);
		fromArray.ExportGpuBuffers(out byte[] arrayNodes, out byte[] arrayTris);
		AssertThat(arrayNodes.Length).IsEqual(fromArray.NodeCount * 32);
		AssertThat(arrayNodes.AsSpan().SequenceEqual(listNodes)).IsTrue();
		AssertThat(arrayTris.AsSpan().SequenceEqual(listTris)).IsTrue();

		// Written in pieces, as the GPU upload does, the buffers are byte for byte the whole export.
		var pieces = new byte[arrayTris.Length];
		fromArray.WriteGpuTriangles(0, pieces.AsSpan(0, 64 * 120));
		fromArray.WriteGpuTriangles(120, pieces.AsSpan(64 * 120));
		AssertThat(pieces.AsSpan().SequenceEqual(arrayTris)).IsTrue();
		var nodePieces = new byte[arrayNodes.Length];
		int half = fromArray.NodeCount / 2;
		fromArray.WriteGpuNodes(0, nodePieces.AsSpan(0, 32 * half));
		fromArray.WriteGpuNodes(half, nodePieces.AsSpan(32 * half));
		AssertThat(nodePieces.AsSpan().SequenceEqual(arrayNodes)).IsTrue();
	}

	[TestCase]
	public void TestBakeFromBvhRecordsWallOcclusionAndLeavesOppositeOpen()
	{
		// Place a vertical wall quad at X = +3.0 spanning Y in [-4, 4] and Z in [-4, 4].
		var tris = new List<Engine_LevelShadowMaps.BvhTriangle>
		{
			new(new Vector3(3f, -4f, -4f), new Vector3(3f, 4f, -4f), new Vector3(3f, 4f, 4f)),
			new(new Vector3(3f, -4f, -4f), new Vector3(3f, 4f, 4f), new Vector3(3f, -4f, 4f)),
		};

		var bvh = new Engine_LevelShadowMaps.BvhTree(tris);
		ulong lightKey = Engine_LevelLights.ComposeLightKey(0xCAFEBABEu, 0);
		var lights = new List<Engine_LevelShadowMaps.BakeLightSpec>
		{
			new(
				Key: lightKey,
				WorldOrigin: Vector3.Zero,
				MaxRange: 10.0f,
				IsSpot: false,
				SpotForward: Vector3.Forward,
				SpotCosLimit: -1.0f),
		};

		StaticShadowMapContainer baked = Engine_LevelShadowMaps.BakeFromBvh(bvh, lights, 128);
		AssertThat(baked.LightRects.TryGetValue(lightKey, out Vector4 rect)).IsTrue();
		AssertThat(rect.Z > 0f).IsTrue();

		// Sample the baked atlas along +X (blocked by wall at 3.0m) and -X (unoccluded).
		Vector2 uvPlusX = Engine_LevelShadowMaps.OctahedralUvFromDirection(new Vector3(1f, 0f, 0f));
		Vector2 atlasUvPlusX = new Vector2(rect.X, rect.Y) + uvPlusX * rect.Z;
		int pxPlus = Math.Clamp((int)(atlasUvPlusX.X * baked.AtlasSize), 0, baked.AtlasSize - 1);
		int pyPlus = Math.Clamp((int)(atlasUvPlusX.Y * baked.AtlasSize), 0, baked.AtlasSize - 1);
		float depthPlusX = (float)BitConverter.UInt16BitsToHalf(baked.Texels[pyPlus * baked.AtlasSize + pxPlus]);

		Vector2 uvMinusX = Engine_LevelShadowMaps.OctahedralUvFromDirection(new Vector3(-1f, 0f, 0f));
		Vector2 atlasUvMinusX = new Vector2(rect.X, rect.Y) + uvMinusX * rect.Z;
		int pxMinus = Math.Clamp((int)(atlasUvMinusX.X * baked.AtlasSize), 0, baked.AtlasSize - 1);
		int pyMinus = Math.Clamp((int)(atlasUvMinusX.Y * baked.AtlasSize), 0, baked.AtlasSize - 1);
		float depthMinusX = (float)BitConverter.UInt16BitsToHalf(baked.Texels[pyMinus * baked.AtlasSize + pxMinus]);

		// +X should record the wall distance (~3.0m + small slope bias), whereas -X should be unoccluded (65504).
		AssertThat(depthPlusX > 2.9f && depthPlusX < 3.5f).IsTrue();
		AssertThat(depthMinusX > 60000.0f).IsTrue();
	}

	[TestCase]
	public void TestModernCasterMaskExcludesStaticWorld()
	{
		uint unzonedDynamicOnlyMask = Engine_LevelLights.CasterMaskOf(
			includeDynamicEntities: true,
			isIndoorLight: false,
			includeStaticWorld: false);
		uint outdoorDynamicOnlyMask = Engine_LevelLights.CasterMaskOf(
			includeDynamicEntities: true,
			isIndoorLight: false,
			includeStaticWorld: false,
			isOutdoorLight: true);

		uint classicMask = Engine_LevelLights.CasterMaskOf(
			includeDynamicEntities: true,
			isIndoorLight: false,
			includeStaticWorld: true);

		AssertThat(outdoorDynamicOnlyMask).IsEqual(Engine_LevelLights.EntityCasterLayerMask);
		AssertThat(unzonedDynamicOnlyMask).IsEqual(
			Engine_LevelLights.EntityCasterLayerMask | Engine_LevelLights.IndoorEntityCasterMask);
		AssertThat((classicMask & 1u) != 0u).IsTrue();
		AssertThat((unzonedDynamicOnlyMask & 1u) == 0u).IsTrue();
		AssertThat((unzonedDynamicOnlyMask & Engine_LightZones.IndoorMask) == 0u).IsTrue();
	}

	[TestCase]
	public void TestShadowMapResourcePathIncludesWorldLevelAndScenario()
	{
		var entry = new LevelEntry
		{
			World = "albion",
			LevelPath = "bowerstonemarket",
			Name = "bowerstonemarket",
			Scenario = "defaultscenario",
		};

		string path = Engine_LevelShadowMaps.GetShadowMapResourcePath(
			entry, "worlds/albion/bowerstonemarket/bowerstonemarket.gdb");
		AssertThat(path).IsEqual("res://assets/ShadowMaps/albion/bowerstonemarket/defaultscenario.shadows");
	}

	[TestCase]
	public void TestOctahedralUvMatchesShaderZPoleConvention()
	{
		// +Z is the upper hemisphere pole -> center (0.5, 0.5)
		Vector2 uvPlusZ = Engine_LevelShadowMaps.OctahedralUvFromDirection(new Vector3(0f, 0f, 1f));
		AssertThat(MathF.Abs(uvPlusZ.X - 0.5f) < 1e-5f).IsTrue();
		AssertThat(MathF.Abs(uvPlusZ.Y - 0.5f) < 1e-5f).IsTrue();

		// +X -> (1.0, 0.5), +Y -> (0.5, 1.0)
		Vector2 uvPlusX = Engine_LevelShadowMaps.OctahedralUvFromDirection(new Vector3(1f, 0f, 0f));
		AssertThat(MathF.Abs(uvPlusX.X - 1.0f) < 1e-5f).IsTrue();
		AssertThat(MathF.Abs(uvPlusX.Y - 0.5f) < 1e-5f).IsTrue();

		Vector2 uvPlusY = Engine_LevelShadowMaps.OctahedralUvFromDirection(new Vector3(0f, 1f, 0f));
		AssertThat(MathF.Abs(uvPlusY.X - 0.5f) < 1e-5f).IsTrue();
		AssertThat(MathF.Abs(uvPlusY.Y - 1.0f) < 1e-5f).IsTrue();
	}

	[TestCase]
	public void TestComputeMaxBakeRangeHonoursPlacedRange()
	{
		// An outdoor level: a level with no sky keeps the stated range (no PlacedRange).
		Engine_LightingFrame.CurrentInterior = false;
		var placement = new Engine_LevelLights.LightPlacement(
			ColourLinear: Vector3.One,
			Intensity: 16.0f,
			Range: 8.0f,
			Type: Engine_LevelLights.LightTypePoint,
			HalfAngleRadians: 0.5f,
			FadeDistance: 40.0f,
			FadeRange: 20.0f,
			CastShadows: false,
			Active: true,
			Dummy: string.Empty,
			FlickerRate: 0.1f,
			FlickerBrightnessRange: 0.1f,
			FlickerRadiusRange: 0.2f);

		var attached = new Engine_LevelLights.ResolvedLightInstance(
			Entity: 0x1000u,
			Index: 0,
			LightKey: Engine_LevelLights.ComposeLightKey(0x1000u, 0),
			Light: placement,
			Transform: Transform3D.Identity,
			EntityName: "TestAttached");

		float maxRange = Engine_LevelLights.ComputeMaxBakeRange(attached);

		// Range must include PlacedRange * (1 + FlickerRadiusRange).
		float expectedMin = 8.0f * Engine_LevelLights.PlacedRange * 1.2f;
		AssertThat(maxRange >= expectedMin - 1e-3f).IsTrue();
	}

	[TestCase]
	public void TestIsLightFixtureModelExcludesLampsButKeepsWorldProps()
	{
		AssertThat(Engine_LevelShadowMaps.IsLightFixtureModel(
			"Art/Environment/Regions/Bowerstone/Props/dotXSI/BS_Lights_Town/BS_Lights_Town.mdl")).IsTrue();
		AssertThat(Engine_LevelShadowMaps.IsLightFixtureModel(
			"Art/Environment/Regions/Bowerstone/Props/dotXSI/BS_Cemetary_OilLamp_Single/BS_Cemetary_OilLamp_Single.mdl")).IsTrue();
		AssertThat(Engine_LevelShadowMaps.IsLightFixtureModel(
			"Art/Environment/Props/Lights/Oil_Lantern_Single.mdl")).IsTrue();
		AssertThat(Engine_LevelShadowMaps.IsLightFixtureModel(
			"Art/Environment/Regions/Bowerstone/Buildings/BS_Tavern/BS_Tavern.mdl")).IsFalse();
		AssertThat(Engine_LevelShadowMaps.IsLightFixtureModel(
			"Art/Environment/Regions/Bowerstone/Props/BS_MarketStall.mdl")).IsFalse();
		AssertThat(Engine_LevelShadowMaps.IsStructuralLightHostModel(
			"Art/Environment/Regions/Bowerstone/Props/BS_MarketStall.mdl")).IsTrue();
		AssertThat(Engine_LevelShadowMaps.IsStructuralLightHostModel(
			"Art/Environment/Regions/Bowerstone/Props/dotXSI/BS_Lights_Town/BS_Lights_Town.mdl")).IsFalse();
	}

	[TestCase]
	public void TestIsCharacterModelExcludesCreaturesAndBirds()
	{
		AssertThat(Engine_LevelProps.IsCharacterModel(
			"Art/Creatures/Ambient/Crow/dotXSI/crow_01/crow_01.mdl")).IsTrue();
		AssertThat(Engine_LevelProps.IsCharacterModel(
			"Art/Creatures/Ambient/Chicken/dotXSI/ChickenBlack/ChickenBlack.mdl")).IsTrue();
		AssertThat(Engine_LevelProps.IsCharacterModel(
			"Art/Characters/Npc/Specific Npc/Shop Owners/Pictures/_SharedTextures/Blacksmith.mdl")).IsTrue();
		AssertThat(Engine_LevelProps.IsCharacterModel(
			"Art/Environment/Regions/Bowerstone/Props/BS_MarketStall.mdl")).IsFalse();
		AssertThat(Engine_LevelProps.IsCharacterModel(
			"Art/Environment/Regions/Bowerstone/Buildings/BS_Tavern/BS_Tavern.mdl")).IsFalse();
	}

	[TestCase]
	public void TestShadowMapDiskAndCachePathResolution()
	{
		var entry = new LevelEntry
		{
			World = "albion",
			LevelPath = "bowerstonemarket",
			Name = "bowerstonemarket",
			Scenario = "defaultscenario",
		};

		string rel = Engine_LevelShadowMaps.GetShadowMapRelativePath(entry);
		AssertThat(rel).IsEqual("albion/bowerstonemarket/defaultscenario.shadows");

		string diskPath = Engine_LevelShadowMaps.GetShadowMapDiskPath(entry);
		AssertThat(diskPath.Contains("cache")).IsTrue();
		AssertThat(diskPath.EndsWith("defaultscenario.shadows")).IsTrue();
	}

	[TestCase]
	public void TestHeaderValidationDetectsChangesAndSkipsRebake()
	{
		ulong key = Engine_LevelLights.ComposeLightKey(0x11223344u, 0);
		var original = new StaticShadowMapContainer
		{
			AtlasSize = 128,
			SliceCount = 1,
			LightsPerMap = 16,
			PlacedReachScale = 1.0f,
			BuildingInteriorReachScale = 1.0f,
			LightRects = new Dictionary<ulong, Vector4>
			{
				[key] = new Vector4(0.1f, 0.2f, 0.5f, 0.0f),
			},
			Texels = new ushort[128 * 128],
		};

		byte[] payload = Engine_LevelShadowMaps.Serialize(original);
		string tempPath = System.IO.Path.Combine(Refii.Tests.TestTemp.Root, $"refii_test_shadow_{Guid.NewGuid():N}.shadows");
		try
		{
			System.IO.File.WriteAllBytes(tempPath, payload);

			// 1. Exact match -> valid (skips rebake)
			AssertThat(Engine_LevelShadowMaps.IsFileValid(tempPath, 128, 16, 1.0f, 1.0f)).IsTrue();

			// 2. Resolution changed -> invalid (triggers rebake)
			AssertThat(Engine_LevelShadowMaps.IsFileValid(tempPath, 256, 16, 1.0f, 1.0f)).IsFalse();

			// 3. LightsPerMap changed -> invalid (triggers rebake)
			AssertThat(Engine_LevelShadowMaps.IsFileValid(tempPath, 128, 25, 1.0f, 1.0f)).IsFalse();

			// 4. Placed reach scale changed -> invalid (triggers rebake)
			AssertThat(Engine_LevelShadowMaps.IsFileValid(tempPath, 128, 16, 1.5f, 1.0f)).IsFalse();

			// 5. Building interior reach scale changed -> invalid (triggers rebake)
			AssertThat(Engine_LevelShadowMaps.IsFileValid(tempPath, 128, 16, 1.0f, 0.8f)).IsFalse();
		}
		finally
		{
			try { System.IO.File.Delete(tempPath); } catch { }
		}
	}

	[TestCase]
	public void TestTryReadHeaderAndIsFileValid()
	{
		ulong key = Engine_LevelLights.ComposeLightKey(0x11223344u, 0);
		var original = new StaticShadowMapContainer
		{
			AtlasSize = 128,
			SliceCount = 2,
			LightsPerMap = 16,
			PlacedReachScale = 1.0f,
			BuildingInteriorReachScale = 1.0f,
			LightRects = new Dictionary<ulong, Vector4>
			{
				[key] = new Vector4(0.1f, 0.2f, 0.5f, 1.0f),
			},
			Texels = new ushort[2 * 128 * 128],
		};

		byte[] payload = Engine_LevelShadowMaps.Serialize(original);
		string tempPath = System.IO.Path.Combine(Refii.Tests.TestTemp.Root, $"refii_test_shadow_{Guid.NewGuid():N}.shadows");
		try
		{
			System.IO.File.WriteAllBytes(tempPath, payload);

			bool headerOk = Engine_LevelShadowMaps.TryReadHeader(
				tempPath,
				out uint magic,
				out uint version,
				out int atlasSize,
				out int sliceCount,
				out int lightCount,
				out int lightsPerMap,
				out float placedReach,
				out float interiorReach);

			AssertThat(headerOk).IsTrue();
			AssertThat(magic).IsEqual(Engine_LevelShadowMaps.Magic);
			AssertThat(version).IsEqual(Engine_LevelShadowMaps.Version);
			AssertThat(atlasSize).IsEqual(128);
			AssertThat(sliceCount).IsEqual(2);
			AssertThat(lightCount).IsEqual(1);
			AssertThat(lightsPerMap).IsEqual(16);
			AssertThat(MathF.Abs(placedReach - 1.0f) < 1e-4f).IsTrue();
			AssertThat(MathF.Abs(interiorReach - 1.0f) < 1e-4f).IsTrue();

			// Valid match
			AssertThat(Engine_LevelShadowMaps.IsFileValid(tempPath, 128, 16, 1.0f, 1.0f)).IsTrue();

			// Mismatched resolution -> invalid
			AssertThat(Engine_LevelShadowMaps.IsFileValid(tempPath, 2048, 16, 1.0f, 1.0f)).IsFalse();

			// Mismatched version -> invalid
			AssertThat(Engine_LevelShadowMaps.IsFileValid(tempPath, 128, 16, 1.0f, 1.0f, expectedVersion: 99)).IsFalse();

			// Non-existent path -> false
			AssertThat(Engine_LevelShadowMaps.IsFileValid(tempPath + "_nonexistent", 128, 16, 1.0f, 1.0f)).IsFalse();

			// Truncated file -> false
			string truncPath = tempPath + "_trunc";
			System.IO.File.WriteAllBytes(truncPath, payload.AsSpan(0, 10).ToArray());
			AssertThat(Engine_LevelShadowMaps.IsFileValid(truncPath, 128, 16, 1.0f, 1.0f)).IsFalse();
			try { System.IO.File.Delete(truncPath); } catch { }
		}
		finally
		{
			try { System.IO.File.Delete(tempPath); } catch { }
		}
	}

	[TestCase]
	public void TestVolumetricLightShaftsTightBoundsAndMediumGate()
	{
		Engine_VolumetricLightShafts.SetMedium(0.0f, Colors.White);
		AssertThat(Engine_VolumetricLightShafts.HasActiveMedium).IsFalse();

		Engine_VolumetricLightShafts.SetMedium(0.012f, new Color(0.7f, 0.8f, 1.0f));
		AssertThat(Engine_VolumetricLightShafts.HasActiveMedium).IsTrue();

		Vector3 eye = new(10f, 5f, -20f);
		Span<Engine_VolumetricLightShafts.ShaftLightInput> lights = stackalloc Engine_VolumetricLightShafts.ShaftLightInput[2];
		lights[0] = new Engine_VolumetricLightShafts.ShaftLightInput(
			Position: new Vector3(14f, 5f, -20f),
			StatedRange: 6.0f,
			NodeRange: 6.0f,
			LinearColor: Vector3.One,
			Energy: 2.0f,
			FogEnergyScale: 4.5f,
			AtlasRect: new Vector4(0.1f, 0.1f, 0.25f, 0.0f),
			IndoorFromOutside: false);
		lights[1] = new Engine_VolumetricLightShafts.ShaftLightInput(
			Position: new Vector3(20f, 7f, -15f),
			StatedRange: 8.0f,
			NodeRange: 8.0f,
			LinearColor: Vector3.One,
			Energy: 2.0f,
			FogEnergyScale: 4.5f,
			AtlasRect: new Vector4(0.4f, 0.1f, 0.25f, 0.0f),
			IndoorFromOutside: true);

		bool ok = Engine_VolumetricLightShafts.ComputeTightLightBounds(
			eye, Engine_VolumetricLightShafts.MaxFogRangeMetres, lights, out Vector3 center, out Vector3 size);
		AssertThat(ok).IsTrue();
		// Min X = min(14-6, 20-8) = 8; Max X = max(14+6, 20+8) = 28 -> size.X = 20, center.X = 18
		AssertThat(Mathf.IsEqualApprox(size.X, 20.0f, 1e-3f)).IsTrue();
		AssertThat(Mathf.IsEqualApprox(center.X, 18.0f, 1e-3f)).IsTrue();

		// Light outside the 64m fog box is clipped out completely
		Span<Engine_VolumetricLightShafts.ShaftLightInput> farLight = stackalloc Engine_VolumetricLightShafts.ShaftLightInput[1];
		farLight[0] = lights[0] with { Position = eye + new Vector3(100f, 0f, 0f), StatedRange = 8.0f };
		AssertThat(Engine_VolumetricLightShafts.ComputeTightLightBounds(
			eye, Engine_VolumetricLightShafts.MaxFogRangeMetres, farLight, out _, out _)).IsFalse();

		// NearEligible culls small lights far behind the camera while keeping lights within 12m or reaching the eye
		AssertThat(Engine_LightShadowBudget.NearEligible(distance: 30.0f, range: 8.0f)).IsFalse();
		AssertThat(Engine_LightShadowBudget.NearEligible(distance: 10.0f, range: 6.0f)).IsTrue();
		AssertThat(Engine_LightShadowBudget.NearEligible(distance: 18.0f, range: 16.0f)).IsTrue();

		Engine_VolumetricLightShafts.SetMedium(0.0f, Colors.Black);
	}

	[TestCase]
	public void TestAutomaticLightsPerMapForResolutionPresets()
	{
		AssertThat(Engine_LevelShadowMaps.GetLightsPerMapForResolution(128)).IsEqual(16);
		AssertThat(Engine_LevelShadowMaps.GetLightsPerMapForResolution(2048)).IsEqual(36);
		AssertThat(Engine_LevelShadowMaps.GetLightsPerMapForResolution(4096)).IsEqual(64);
		AssertThat(Engine_LevelShadowMaps.GetLightsPerMapForResolution(8192)).IsEqual(64);
	}
}
