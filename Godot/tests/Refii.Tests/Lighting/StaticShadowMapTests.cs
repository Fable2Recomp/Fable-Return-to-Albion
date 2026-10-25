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

		var texels = new ushort[64 * 64];
		for (int i = 0; i < texels.Length; i++)
		{
			texels[i] = BitConverter.HalfToUInt16Bits((Half)(i * 0.25f));
		}

		var original = new StaticShadowMapContainer
		{
			AtlasSize = 64,
			LightRects = new Dictionary<ulong, Vector3>
			{
				[keyA] = new Vector3(0.015625f, 0.015625f, 0.46875f),
				[keyB] = new Vector3(0.515625f, 0.015625f, 0.46875f),
			},
			Texels = texels,
		};

		byte[] bytes = Engine_LevelShadowMaps.Serialize(original);
		bool ok = Engine_LevelShadowMaps.TryDeserialize(
			bytes, out StaticShadowMapContainer restored, out string error);

		AssertThat(ok).IsTrue();
		AssertThat(error).IsEmpty();
		AssertThat(restored.AtlasSize).IsEqual(64);
		AssertThat(restored.LightRects.Count).IsEqual(2);
		AssertThat(restored.LightRects[keyA]).IsEqual(original.LightRects[keyA]);
		AssertThat(restored.LightRects[keyB]).IsEqual(original.LightRects[keyB]);
		AssertThat(restored.Texels.Length).IsEqual(texels.Length);
		AssertThat(restored.Texels[123]).IsEqual(texels[123]);
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
		AssertThat(baked.LightRects.TryGetValue(lightKey, out Vector3 rect)).IsTrue();
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
		uint dynamicOnlyMask = Engine_LevelLights.CasterMaskOf(
			includeDynamicEntities: true,
			isIndoorLight: false,
			includeStaticWorld: false);

		uint classicMask = Engine_LevelLights.CasterMaskOf(
			includeDynamicEntities: true,
			isIndoorLight: false,
			includeStaticWorld: true);

		AssertThat(dynamicOnlyMask).IsEqual(Engine_LevelLights.EntityCasterLayerMask);
		AssertThat((classicMask & 1u) != 0u).IsTrue();
		AssertThat((dynamicOnlyMask & 1u) == 0u).IsTrue();
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
	public void TestComputeMaxBakeRangeHonoursPlacedRangeAndUnattachedLights()
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
			EntityName: "TestAttached",
			Unattached: false);

		var unattached = attached with { Unattached = true };

		float attachedMax = Engine_LevelLights.ComputeMaxBakeRange(attached);
		float unattachedMax = Engine_LevelLights.ComputeMaxBakeRange(unattached);

		// Unattached lights only scale energy, never range, and both must include PlacedRange * (1 + FlickerRadiusRange).
		float expectedMin = 8.0f * Engine_LevelLights.PlacedRange * 1.2f;
		AssertThat(MathF.Abs(attachedMax - unattachedMax) < 1e-4f).IsTrue();
		AssertThat(attachedMax >= expectedMin - 1e-3f).IsTrue();
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
	public void TestManifestValidationDetectsChanges()
	{
		string testManifestPath = Engine_LevelShadowPrebuild.ManifestPath;
		string? dir = System.IO.Path.GetDirectoryName(testManifestPath);
		if (!string.IsNullOrEmpty(dir))
		{
			System.IO.Directory.CreateDirectory(dir);
		}

		Engine_LevelShadowPrebuild.WriteManifest(2048, 1.5f, 1.0f);
		AssertThat(Engine_LevelShadowPrebuild.IsManifestValid(2048, 1.5f, 1.0f)).IsTrue();

		// Resolution change -> invalid
		AssertThat(Engine_LevelShadowPrebuild.IsManifestValid(4096, 1.5f, 1.0f)).IsFalse();

		// Reach scale change -> invalid
		AssertThat(Engine_LevelShadowPrebuild.IsManifestValid(2048, 1.8f, 1.0f)).IsFalse();

		// Reach-only validation
		AssertThat(Engine_LevelShadowPrebuild.IsReachScaleValid(1.5f, 1.0f)).IsTrue();
		AssertThat(Engine_LevelShadowPrebuild.IsReachScaleValid(1.8f, 1.0f)).IsFalse();
	}

	[TestCase]
	public void TestTryReadHeaderAndIsFileValid()
	{
		ulong key = Engine_LevelLights.ComposeLightKey(0x11223344u, 0);
		var original = new StaticShadowMapContainer
		{
			AtlasSize = 128,
			LightRects = new Dictionary<ulong, Vector3>
			{
				[key] = new Vector3(0.1f, 0.2f, 0.5f),
			},
			Texels = new ushort[128 * 128],
		};

		byte[] payload = Engine_LevelShadowMaps.Serialize(original);
		string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"refii_test_shadow_{Guid.NewGuid():N}.shadows");
		try
		{
			System.IO.File.WriteAllBytes(tempPath, payload);

			bool headerOk = Engine_LevelShadowMaps.TryReadHeader(
				tempPath, out uint magic, out uint version, out int atlasSize, out int lightCount);

			AssertThat(headerOk).IsTrue();
			AssertThat(magic).IsEqual(Engine_LevelShadowMaps.Magic);
			AssertThat(version).IsEqual(Engine_LevelShadowMaps.Version);
			AssertThat(atlasSize).IsEqual(128);
			AssertThat(lightCount).IsEqual(1);

			// Valid resolution match
			AssertThat(Engine_LevelShadowMaps.IsFileValid(tempPath, 128)).IsTrue();

			// Mismatched resolution -> invalid
			AssertThat(Engine_LevelShadowMaps.IsFileValid(tempPath, 2048)).IsFalse();
			AssertThat(Engine_LevelShadowMaps.IsFileValid(tempPath, 8192)).IsFalse();

			// Mismatched version -> invalid
			AssertThat(Engine_LevelShadowMaps.IsFileValid(tempPath, 128, expectedVersion: 99)).IsFalse();

			// Non-existent path -> false
			AssertThat(Engine_LevelShadowMaps.IsFileValid(tempPath + "_nonexistent", 128)).IsFalse();

			// Truncated file -> false
			string truncPath = tempPath + "_trunc";
			System.IO.File.WriteAllBytes(truncPath, payload.AsSpan(0, 10).ToArray());
			AssertThat(Engine_LevelShadowMaps.IsFileValid(truncPath, 128)).IsFalse();
			try { System.IO.File.Delete(truncPath); } catch { }
		}
		finally
		{
			try { System.IO.File.Delete(tempPath); } catch { }
		}
	}
}
