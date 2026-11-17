using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using GdUnit4;
using Refii.Level;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// Pure-managed tests verifying <see cref="Engine_WaterQuality"/> tier policies,
/// <see cref="Engine_WaterMesh"/> shoreline apron + 2D interior quad merging,
/// <see cref="Engine_ThemeTimeline.BlendWaterInto"/> reuse, and <c>water.gdshader</c> DXIL/HDR invariants.
/// </summary>
[TestSuite]
public sealed class WaterMeshAndQualityTests
{
	private static string FindGodotRoot()
	{
		string? dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			if (Directory.Exists(Path.Combine(dir, "src", "Engine", "level", "water")))
			{
				return dir;
			}

			string candidate = Path.Combine(dir, "Godot");
			if (Directory.Exists(Path.Combine(candidate, "src", "Engine", "level", "water")))
			{
				return candidate;
			}

			dir = Path.GetDirectoryName(dir);
		}

		throw new DirectoryNotFoundException("Could not locate Godot/src/Engine/level/water from test base directory.");
	}

	private static string StripShaderComments(string source)
	{
		string withoutBlock = Regex.Replace(source, @"/\*[\s\S]*?\*/", string.Empty);
		return Regex.Replace(withoutBlock, @"//.*", string.Empty);
	}

	[TestCase]
	public void WaterQuality_ParsesFormatsMirrorSpecsAndSsrSteps()
	{
		AssertThat(Engine_WaterQuality.Parse("low")).IsEqual(WaterQualityTier.Low);
		AssertThat(Engine_WaterQuality.Parse("  \"Balanced\" ")).IsEqual(WaterQualityTier.Balanced);
		AssertThat(Engine_WaterQuality.Parse("medium")).IsEqual(WaterQualityTier.Balanced);
		AssertThat(Engine_WaterQuality.Parse("HIGH")).IsEqual(WaterQualityTier.High);
		AssertThat(Engine_WaterQuality.Parse("3")).IsEqual(WaterQualityTier.Ultra);
		AssertThat(Engine_WaterQuality.Parse("unknown_value", WaterQualityTier.High)).IsEqual(WaterQualityTier.High);
		AssertThat(Engine_WaterQuality.Parse(null, WaterQualityTier.Low)).IsEqual(WaterQualityTier.Low);

		foreach (WaterQualityTier tier in Enum.GetValues<WaterQualityTier>())
		{
			string token = Engine_WaterQuality.ToConfigString(tier);
			AssertThat(Engine_WaterQuality.Parse(token)).IsEqual(tier);
		}

		AssertThat(Engine_WaterQuality.ResolveFromValues(null, isAndroid: false)).IsEqual(WaterQualityTier.Balanced);
		AssertThat(Engine_WaterQuality.ResolveFromValues(null, isAndroid: true)).IsEqual(WaterQualityTier.Low);
		var customValues = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			[Engine_WaterQuality.CVarName] = "ultra",
		};
		AssertThat(Engine_WaterQuality.ResolveFromValues(customValues, isAndroid: true)).IsEqual(WaterQualityTier.Ultra);

		AssertThat(Engine_WaterQuality.MirrorFor(WaterQualityTier.Low))
			.IsEqual(new WaterMirrorSpec(false, 256, 4, 50f, 4f));
		AssertThat(Engine_WaterQuality.MirrorFor(WaterQualityTier.Balanced))
			.IsEqual(new WaterMirrorSpec(true, 256, 4, 200f, 4f));
		AssertThat(Engine_WaterQuality.MirrorFor(WaterQualityTier.High))
			.IsEqual(new WaterMirrorSpec(true, 384, 2, 275f, 3f));
		AssertThat(Engine_WaterQuality.MirrorFor(WaterQualityTier.Ultra))
			.IsEqual(new WaterMirrorSpec(true, 512, 1, 350f, 2f));

		AssertThat(Engine_WaterQuality.SsrStepsFor(WaterQualityTier.Low, hasPlanarMirror: false)).IsEqual(0);
		AssertThat(Engine_WaterQuality.SsrStepsFor(WaterQualityTier.Low, hasPlanarMirror: true)).IsEqual(0);
		AssertThat(Engine_WaterQuality.SsrStepsFor(WaterQualityTier.Balanced, hasPlanarMirror: true)).IsEqual(8);
		AssertThat(Engine_WaterQuality.SsrStepsFor(WaterQualityTier.Balanced, hasPlanarMirror: false)).IsEqual(12);
		AssertThat(Engine_WaterQuality.SsrStepsFor(WaterQualityTier.High, hasPlanarMirror: true)).IsEqual(12);
		AssertThat(Engine_WaterQuality.SsrStepsFor(WaterQualityTier.High, hasPlanarMirror: false)).IsEqual(16);
		AssertThat(Engine_WaterQuality.SsrStepsFor(WaterQualityTier.Ultra, hasPlanarMirror: true)).IsEqual(16);
		AssertThat(Engine_WaterQuality.SsrStepsFor(WaterQualityTier.Ultra, hasPlanarMirror: false)).IsEqual(22);
	}

	[TestCase]
	public void ModernWaterMesh_MergesInteriorRetainsShoreApronAndCullsDryCorners()
	{
		const int cells = 128;
		const float extent = 51.2f; // 0.4 m per cell
		const float cellSize = extent / cells;
		const int waterMin = 24;
		const int waterMax = 103; // [24..103] inclusive active water interior

		var mask = new byte[cells * cells];
		for (int z = waterMin; z <= waterMax; z++)
		{
			for (int x = waterMin; x <= waterMax; x++)
			{
				mask[(z * cells) + x] = 1;
			}
		}

		var body = new WaterBody { BaseHeight = 12.5f };
		body.Tiles.Add(new WaterTile
		{
			CentreX = 0f,
			CentreZ = 0f,
			ExtentX = extent,
			ExtentZ = extent,
			CellsX = cells,
			CellsZ = cells,
			Mask = mask,
		});

		TerrainMesh? modern = Engine_WaterMesh.BuildBody(body, fullPatches: false);
		AssertThat(modern).IsNotNull();
		int vertexCount = modern!.Positions.Length / 3;
		int triCount = modern.Indices.Length / 3;
		AssertThat(modern.Normals.Length).IsEqual(modern.Positions.Length);
		AssertThat(modern.Uvs.Length).IsEqual(vertexCount * 2);
		AssertThat(modern.Indices.Length % 3).IsEqual(0);
		AssertThat(triCount).IsGreater(0);

		// Unmerged 128x128 tile = 32,768 triangles; merged interior + culled dry border must cut >80%.
		const int unmergedTris = cells * cells * 2;
		AssertThat(triCount).IsLess(unmergedTris / 5);

		// Every emitted vertex must be valid and referenced by at least one triangle.
		var referenced = new bool[vertexCount];
		for (int i = 0; i < modern.Indices.Length; i++)
		{
			int idx = modern.Indices[i];
			AssertThat(idx).IsGreaterEqual(0);
			AssertThat(idx).IsLess(vertexCount);
			referenced[idx] = true;
		}

		for (int v = 0; v < vertexCount; v++)
		{
			AssertThat(referenced[v]).IsTrue();
			AssertThat(Math.Abs(modern.Positions[(v * 3) + 1] - 12.5f)).IsLess(1e-5f);
		}

		// Vertices span down to the 3-cell shoreline apron edge (waterMin - ApronCells),
		// while deep dry border cells outside the apron must never appear in emitted geometry.
		float x0 = -extent * 0.5f;
		float expectedApronEdge = x0 + ((waterMin - Engine_WaterMesh.ApronCells) * cellSize);
		float minX = float.MaxValue;
		float maxX = float.MinValue;
		float minZ = float.MaxValue;
		float maxZ = float.MinValue;
		for (int v = 0; v < vertexCount; v++)
		{
			float vx = modern.Positions[v * 3];
			float vz = modern.Positions[(v * 3) + 2];
			if (vx < minX) minX = vx;
			if (vx > maxX) maxX = vx;
			if (vz < minZ) minZ = vz;
			if (vz > maxZ) maxZ = vz;
		}

		AssertThat(Math.Abs(minX - expectedApronEdge)).IsLess(0.01f);
		AssertThat(Math.Abs(minZ - expectedApronEdge)).IsLess(0.01f);
		AssertThat(minX).IsGreater(x0 + (10f * cellSize));
		AssertThat(maxX).IsLess(-x0 - (10f * cellSize));

		// Classic fullPatches mode stays on the coarse 0.2 * extent classic grid.
		TerrainMesh? classic = Engine_WaterMesh.BuildBody(body, fullPatches: true);
		AssertThat(classic).IsNotNull();
		int classicAxis = Math.Max(2, Math.Min(cells, (int)(extent * 0.2f))) - 1;
		AssertThat(classic!.Indices.Length / 3).IsEqual(classicAxis * classicAxis * 2);
		AssertThat(classic.Positions.Length / 3).IsEqual((classicAxis + 1) * (classicAxis + 1));
	}

	[TestCase]
	public void ThemeBlendInto_MatchesAllocatingBlendAndSelectsDominantNormalMap()
	{
		var a = new WaterTheme
		{
			HasAny = true,
			HasNormalMap = true,
			NormalMapHash = 0x11111111u,
		};
		a.Stated[WaterThemeParam.ShallowR] = true;
		a.Values[WaterThemeParam.ShallowR] = 0.1f;
		a.Stated[WaterThemeParam.MaxRefractionDistance] = true;
		a.Values[WaterThemeParam.MaxRefractionDistance] = 4.0f;

		var b = new WaterTheme
		{
			HasAny = true,
			HasNormalMap = true,
			NormalMapHash = 0x22222222u,
		};
		b.Stated[WaterThemeParam.ShallowR] = true;
		b.Values[WaterThemeParam.ShallowR] = 0.5f;
		b.Stated[WaterThemeParam.MaxRefractionDistance] = true;
		b.Values[WaterThemeParam.MaxRefractionDistance] = 12.0f;

		var scratch = new WaterTheme();
		foreach (float weight in new[] { 0.25f, 0.75f })
		{
			WaterTheme expected = Engine_ThemeTimeline.BlendWater(a, b, weight);
			WaterTheme actual = Engine_ThemeTimeline.BlendWaterInto(a, b, weight, scratch);

			AssertThat(object.ReferenceEquals(actual, scratch)).IsTrue();
			AssertThat(actual.HasAny).IsEqual(expected.HasAny);
			AssertThat(actual.HasNormalMap).IsEqual(expected.HasNormalMap);
			AssertThat(actual.NormalMapHash).IsEqual(expected.NormalMapHash);
			AssertThat(actual.NormalMapHash).IsEqual(weight < 0.5f ? 0x11111111u : 0x22222222u);

			for (int i = 0; i < WaterThemeParam.Count; i++)
			{
				AssertThat(actual.Stated[i]).IsEqual(expected.Stated[i]);
				AssertThat(Math.Abs(actual.Values[i] - expected.Values[i])).IsLess(1e-5f);
			}
		}
	}

	[TestCase]
	public void WaterShader_EnforcesDxilAndHdrInvariants()
	{
		string shaderPath = Path.Combine(FindGodotRoot(), "src", "Engine", "level", "water", "water.gdshader");
		string raw = File.ReadAllText(shaderPath);
		string clean = StripShaderComments(raw);

		// Forward+ transparent shader: zero `discard;` anywhere.
		AssertThat(Regex.IsMatch(clean, @"\bdiscard\s*;")).IsFalse();

		// Scene-referred HDR compliance and configurable quality/surf uniforms.
		AssertThat(clean.Contains("uniform bool is_pbr = false;", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("uniform int water_quality_tier", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("uniform bool coastal_surf", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("ALBEDO = max(col, vec3(0.0));", StringComparison.Ordinal)).IsTrue();

		// Zero constant array declarations/indexing (`WAVE_DIR[...]`).
		AssertThat(clean.Contains("WAVE_DIR", StringComparison.Ordinal)).IsFalse();
		AssertThat(clean.Contains("WAVE_LEN", StringComparison.Ordinal)).IsFalse();

		// Extract fragment() body and verify derivatives come before any `if (` and zero implicit `texture(` calls exist.
		int fragStart = clean.IndexOf("void fragment()", StringComparison.Ordinal);
		AssertThat(fragStart).IsGreater(-1);
		string fragBody = clean[fragStart..];

		int dfdxIdx = fragBody.IndexOf("dFdx(", StringComparison.Ordinal);
		int dfdyIdx = fragBody.IndexOf("dFdy(", StringComparison.Ordinal);
		int firstIfIdx = fragBody.IndexOf("if (", StringComparison.Ordinal);
		AssertThat(dfdxIdx).IsGreater(-1);
		AssertThat(dfdyIdx).IsGreater(-1);
		AssertThat(firstIfIdx).IsGreater(-1);
		AssertThat(dfdxIdx).IsLess(firstIfIdx);
		AssertThat(dfdyIdx).IsLess(firstIfIdx);

		AssertThat(Regex.IsMatch(fragBody, @"\btexture\s*\(")).IsFalse();
	}
}
