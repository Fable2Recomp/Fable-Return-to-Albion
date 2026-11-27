using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using GdUnit4;
using Godot;
using Refii.Level;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// Pure-managed tests verifying <see cref="Engine_WaterQuality"/> tier policies,
/// <see cref="Engine_WaterMesh"/> shoreline apron + 2D interior quad merging,
/// <see cref="Engine_ThemeTimeline.BlendWaterInto"/> reuse, and <c>water.gdshader</c> DXIL/HDR/Lit invariants.
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
			.IsEqual(new WaterMirrorSpec(true, 272, 2, 800f, 4f));
		AssertThat(Engine_WaterQuality.MirrorFor(WaterQualityTier.High))
			.IsEqual(new WaterMirrorSpec(true, 352, 2, 1200f, 3.5f));
		AssertThat(Engine_WaterQuality.MirrorFor(WaterQualityTier.Ultra))
			.IsEqual(new WaterMirrorSpec(true, 576, 1, float.PositiveInfinity, 2f));

		AssertThat(Engine_WaterQuality.SsrStepsFor(WaterQualityTier.Low, hasPlanarMirror: false)).IsEqual(0);
		AssertThat(Engine_WaterQuality.SsrStepsFor(WaterQualityTier.Low, hasPlanarMirror: true)).IsEqual(0);
		AssertThat(Engine_WaterQuality.SsrStepsFor(WaterQualityTier.Balanced, hasPlanarMirror: false)).IsEqual(12);
		AssertThat(Engine_WaterQuality.SsrStepsFor(WaterQualityTier.Balanced, hasPlanarMirror: true)).IsEqual(12);
		AssertThat(Engine_WaterQuality.SsrStepsFor(WaterQualityTier.High, hasPlanarMirror: false)).IsEqual(18);
		AssertThat(Engine_WaterQuality.SsrStepsFor(WaterQualityTier.High, hasPlanarMirror: true)).IsEqual(18);
		AssertThat(Engine_WaterQuality.SsrStepsFor(WaterQualityTier.Ultra, hasPlanarMirror: false)).IsEqual(24);
		AssertThat(Engine_WaterQuality.SsrStepsFor(WaterQualityTier.Ultra, hasPlanarMirror: true)).IsEqual(24);
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

		// Forward+ transparent shader: zero `discard;` anywhere, lit (not `unshaded`), and `fog_disabled`
		// so localized 3D FogVolume mist boxes never project hard edges onto distant VistaOcean.
		AssertThat(Regex.IsMatch(clean, @"\bdiscard\s*;")).IsFalse();
		AssertThat(Regex.IsMatch(clean, @"\bunshaded\b")).IsFalse();
		AssertThat(Regex.IsMatch(clean, @"\bfog_disabled\b")).IsTrue();

		// Scene-referred HDR compliance, emission headroom boost, uncompensated atmosphere, and quality uniforms.
		AssertThat(clean.Contains("uniform bool is_pbr = false;", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("global uniform float fable_emission_boost", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("fable_atmosphere_terms_uncompensated(", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("uniform int water_quality_tier", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("uniform bool coastal_surf", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("ALBEDO = max(lit_albedo, vec3(0.0));", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("EMISSION = max(lit_emissive, vec3(0.0));", StringComparison.Ordinal)).IsTrue();

		// Zero constant array declarations/indexing (`WAVE_DIR[...]`).
		AssertThat(clean.Contains("WAVE_DIR", StringComparison.Ordinal)).IsFalse();
		AssertThat(clean.Contains("WAVE_LEN", StringComparison.Ordinal)).IsFalse();

		// Extract fragment() and light() bodies; verify derivatives come before any `if (`,
		// zero implicit `texture(` calls exist in fragment(), and zero `return;` exists in light().
		int fragStart = clean.IndexOf("void fragment()", StringComparison.Ordinal);
		int lightStart = clean.IndexOf("void light()", StringComparison.Ordinal);
		AssertThat(fragStart).IsGreater(-1);
		AssertThat(lightStart).IsGreater(fragStart);

		string fragBody = clean[fragStart..lightStart];
		string lightBody = clean[lightStart..];

		int dfdxIdx = fragBody.IndexOf("dFdx(", StringComparison.Ordinal);
		int dfdyIdx = fragBody.IndexOf("dFdy(", StringComparison.Ordinal);
		int worldDxIdx = fragBody.IndexOf("dFdx(v_world.xz)", StringComparison.Ordinal);
		int worldDyIdx = fragBody.IndexOf("dFdy(v_world.xz)", StringComparison.Ordinal);
		int firstIfIdx = fragBody.IndexOf("if (", StringComparison.Ordinal);
		AssertThat(dfdxIdx).IsGreater(-1);
		AssertThat(dfdyIdx).IsGreater(-1);
		AssertThat(worldDxIdx).IsGreater(-1);
		AssertThat(worldDyIdx).IsGreater(-1);
		AssertThat(firstIfIdx).IsGreater(-1);
		AssertThat(dfdxIdx).IsLess(firstIfIdx);
		AssertThat(dfdyIdx).IsLess(firstIfIdx);
		AssertThat(worldDxIdx).IsLess(firstIfIdx);
		AssertThat(worldDyIdx).IsLess(firstIfIdx);

		AssertThat(Regex.IsMatch(fragBody, @"\btexture\s*\(")).IsFalse();
		AssertThat(Regex.IsMatch(lightBody, @"\breturn\b")).IsFalse();

		// Full-frame planar mirror (no planar_edge border box fade), front-to-back SSR crossing,
		// channel-packed mipmapped foam + directional V-wake, and zero Voronoi spiderwebs or open-water swell bands.
		AssertThat(clean.Contains("planar_edge", StringComparison.Ordinal)).IsFalse();
		AssertThat(clean.Contains("prev_in_front", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("foreground_occluder", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("body_edge_sea(", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("uniform sampler2D foam_tex", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("eval_packed_foam(", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("eval_swimmer_vwake(", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("above_water_gate", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("eval_caustic_voronoi(", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("voronoi_edge_2x2", StringComparison.Ordinal)).IsFalse();
		AssertThat(clean.Contains("voronoi_cell_2x2", StringComparison.Ordinal)).IsFalse();
		AssertThat(clean.Contains("eval_voronoi_foam_web", StringComparison.Ordinal)).IsFalse();
		AssertThat(clean.Contains("crest_pinch", StringComparison.Ordinal)).IsFalse();
		AssertThat(clean.Contains("shore_lap", StringComparison.Ordinal)).IsFalse();
		AssertThat(clean.Contains("eval_caustic_analytic", StringComparison.Ordinal)).IsFalse();
		AssertThat(clean.Contains("uv_r", StringComparison.Ordinal)).IsFalse();
		AssertThat(clean.Contains("meniscus", StringComparison.Ordinal)).IsFalse();

		// Celestial billboards & stars sort behind RenderPriority = 0 water and place stars on the +Y upper sky hemisphere.
		string starsPath = Path.Combine(FindGodotRoot(), "src", "Engine", "level", "environment", "celestial", "stars.gdshader");
		string starsClean = StripShaderComments(File.ReadAllText(starsPath));
		AssertThat(starsClean.Contains("vec3(dir_xz.x * inv_len, up_y * inv_len, dir_xz.y * inv_len)", StringComparison.Ordinal)).IsTrue();

		string celestialCsPath = Path.Combine(FindGodotRoot(), "src", "Engine", "level", "environment", "celestial", "Engine_CelestialElements.cs");
		string celestialCs = File.ReadAllText(celestialCsPath);
		AssertThat(celestialCs.Contains("StarsRenderPriority = -21;", StringComparison.Ordinal)).IsTrue();
		AssertThat(celestialCs.Contains("BaseRenderPriority = -20;", StringComparison.Ordinal)).IsTrue();

		// Oblique mirror clipper exists with zero `discard;` and is wired into desktop terrain, models, and clouds.
		string root = FindGodotRoot();
		string clipPath = Path.Combine(root, "src", "Engine", "level", "water", "fable_mirror_clip.gdshaderinc");
		AssertThat(File.Exists(clipPath)).IsTrue();
		string clipClean = StripShaderComments(File.ReadAllText(clipPath));
		AssertThat(Regex.IsMatch(clipClean, @"\bdiscard\s*;")).IsFalse();

		AssertThat(File.ReadAllText(Path.Combine(root, "src", "Engine", "level", "terrain", "terrain_splat_body.gdshaderinc"))
			.Contains("fable_mirror_clip_position(", StringComparison.Ordinal)).IsTrue();
		AssertThat(File.ReadAllText(Path.Combine(root, "src", "Engine", "material", "standard_material_body.gdshaderinc"))
			.Contains("fable_mirror_clip_position(", StringComparison.Ordinal)).IsTrue();
		AssertThat(File.ReadAllText(Path.Combine(root, "src", "Engine", "level", "environment", "clouds.gdshader"))
			.Contains("fable_mirror_clip_position(", StringComparison.Ordinal)).IsTrue();

		// Global sky dome samplers (atmos_inscatter, fable_sky_overlay0) clamp edge texels to prevent Rookridge 2-color repeat wrap.
		string skyDomePath = Path.Combine(root, "src", "Engine", "level", "environment", "sky_dome.gdshaderinc");
		string skyDomeClean = StripShaderComments(File.ReadAllText(skyDomePath));
		AssertThat(skyDomeClean.Contains("const float half_texel = 0.5 / 64.0;", StringComparison.Ordinal)).IsTrue();
		AssertThat(skyDomeClean.Contains("half_texel, 1.0 - half_texel", StringComparison.Ordinal)).IsTrue();
		AssertThat(skyDomeClean.Contains("half_v, 1.0 - half_v", StringComparison.Ordinal)).IsTrue();

		// GPU fast-paths: isotropic trilinear foam_tex, distance-gated horizon_sky, deduplicated depth helper, and Balanced wakes.
		AssertThat(clean.Contains("uniform sampler2D foam_tex : hint_default_black, filter_linear_mipmap, repeat_enable;", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("linear_depth_from_raw(", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("if (has_dome && (dist > 55.0 || ocean_mode))", StringComparison.Ordinal)).IsTrue();

		string waterMatCs = File.ReadAllText(Path.Combine(root, "src", "Engine", "level", "water", "Engine_WaterMaterial.cs"));
		AssertThat(waterMatCs.Contains("tierValue >= (int)WaterQualityTier.Balanced && !GuestMode", StringComparison.Ordinal)).IsTrue();

		// Wader GPU gate, stationary swimmer collar removal, unified single-cone glitter, and Moon phase UV sync invariants.
		AssertThat(clean.Contains("uniform bool w_has_wakes = false;", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("uniform vec4 w_celestial =", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("uniform vec3 w_celestial_dir =", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("pre_horizon_glint_hdr", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("smoothstep(1800.0, 140.0, dist)", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("clamp(ndl, 0.0, 1.0)", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("in_glitter_corridor", StringComparison.Ordinal)).IsFalse();
		AssertThat(clean.Contains("cox_munk_glitter_hdr", StringComparison.Ordinal)).IsFalse();
		AssertThat(clean.Contains("far_var_t", StringComparison.Ordinal)).IsFalse();
		AssertThat(clean.Contains("celestial_mirror_hdr", StringComparison.Ordinal)).IsFalse();
		AssertThat(clean.Contains("inner_aureole", StringComparison.Ordinal)).IsFalse();
		AssertThat(clean.Contains("moon_ripple_gate", StringComparison.Ordinal)).IsFalse();
		AssertThat(clean.Contains("idle_collar", StringComparison.Ordinal)).IsFalse();
		AssertThat(celestialCs.Contains("ModernMoonScale = 0.75f;", StringComparison.Ordinal)).IsTrue();
		AssertThat(celestialCs.Contains("moonNode.SetMeta(MoonPhaseMeta, meshPhaseKey);", StringComparison.Ordinal)).IsTrue();
		AssertThat(celestialCs.Contains(": Math.Clamp(moonPhase, 0, 7);", StringComparison.Ordinal)).IsTrue();

		// Placed-light static shadow atlas reuse, SPECULAR_AMOUNT slot-index isolation,
		// daytime Sun sparkle boost with Moon parity, and sparkle-gated directional ray occlusion.
		AssertThat(clean.Contains("global uniform sampler2D fable_light_table;", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("global uniform sampler2DArray fable_static_shadow_atlas : filter_linear;", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("water_placed_light_attenuation(", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("texelFetch(fable_static_shadow_atlas,", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("texelFetch(fable_light_table,", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("spec_brdf * ndl_spec * SPECULAR_AMOUNT", StringComparison.Ordinal)).IsFalse();
		AssertThat(clean.Contains("spec_brdf * ndl_spec * spec_weight", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("mix(0.88, 0.72, is_moon_l)", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("mix(135.0, 72.0, is_moon_l)", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("float base_peak = is_moon ? 5.2 : 14.5;", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("water_directional_ray_vis(", StringComparison.Ordinal)).IsTrue();
		AssertThat(clean.Contains("if ((spec_brdf * light_atten * dir_vis) > 0.004)", StringComparison.Ordinal)).IsTrue();
	}

	[TestCase]
	public void ObliqueMirrorClip_ClipsUnderwaterAndOrdersAboveWaterMonotonically()
	{
		const float waterHeight = 20.0f;
		const float fov = 60f;
		const float aspect = 16f / 9f;
		var eyeOrigin = new Vector3(0f, 23.5f, 0f);
		var eyeTarget = new Vector3(0f, 18.0f, -25.0f);
		var eye = new Transform3D(Basis.LookingAt((eyeTarget - eyeOrigin).Normalized(), Vector3.Up), eyeOrigin);

		Vector3 mirrOrigin = new(eyeOrigin.X, (2f * waterHeight) - eyeOrigin.Y, eyeOrigin.Z);
		Vector3 fwd = (-eye.Basis.Column2);
		Vector3 up = eye.Basis.Column1;
		fwd.Y = -fwd.Y;
		up.Y = -up.Y;
		var mirrorBasis = Basis.LookingAt(fwd, up);
		Vector3 mFwd = -mirrorBasis.Column2;
		Vector3 mUp = mirrorBasis.Column1;
		Vector3 mRight = mirrorBasis.Column0;

		Engine_WaterQuality.ComputeObliqueMirrorParams(
			eyeOrigin, mFwd, mUp, mRight, waterHeight, fov, aspect, keepWidth: false,
			out float waterClipY, out float alpha);

		AssertThat(waterClipY).IsEqual(waterHeight + 0.08f);
		AssertThat(alpha).IsGreater(0f);

		// Underwater beach/lakebed and shallow coarse-LOD shoal points below waterClipY yield clipZ > clipW (ndcZ > 1.0 -> hardware near clipped).
		float wBeach = 12f;
		AssertThat(Engine_WaterQuality.EvaluateObliqueClipZ(new Vector3(0f, 18.0f, -12f), wBeach, waterClipY, alpha))
			.IsGreater(wBeach);
		AssertThat(Engine_WaterQuality.EvaluateObliqueClipZ(new Vector3(2f, 20.04f, -12f), wBeach, waterClipY, alpha))
			.IsGreater(wBeach);

		// Exact waterline point lands on clipZ == clipW (ndcZ == 1.0).
		AssertThat(Math.Abs(Engine_WaterQuality.EvaluateObliqueClipZ(new Vector3(0f, waterClipY, -12f), wBeach, waterClipY, alpha) - wBeach))
			.IsLess(1e-5f);

		// Above-water vertices inside the mirrored frustum (sky sits along -mUp because Mirror flips up.Y)
		// stay strictly inside [0.05..1.0] Reversed-Z NDC depth and decrease monotonically with distance.
		float halfTan = MathF.Tan(fov * (MathF.PI / 360f));
		Vector3 upperRay = (mFwd - (0.4f * halfTan * mUp)).Normalized();
		float prevNdcZ = float.PositiveInfinity;
		foreach (float t in new[] { 15f, 40f, 250f, 1500f })
		{
			Vector3 world = mirrOrigin + (upperRay * t);
			float clipW = t * mFwd.Dot(upperRay);
			float clipZ = Engine_WaterQuality.EvaluateObliqueClipZ(world, clipW, waterClipY, alpha);
			float ndcZ = clipZ / clipW;
			AssertThat(ndcZ).IsGreaterEqual(0.05f);
			AssertThat(ndcZ).IsLessEqual(1.0f);
			AssertThat(ndcZ).IsLess(prevNdcZ);
			prevNdcZ = ndcZ;
		}
	}

	[TestCase]
	public void MainScreenReprojection_MapsCenterEdgesAndCounterRotatesOnPan()
	{
		const float fov = 60f;
		const float aspect = 16f / 9f;
		Vector3 origin = new(-10f, 23.5f, 15f);
		var eye = new Transform3D(Basis.LookingAt(new Vector3(0f, -0.3f, -1f).Normalized(), Vector3.Up), origin);
		Projection reproj = Engine_WaterQuality.BuildMainScreenReprojection(eye, fov, aspect, keepWidth: false);

		static Vector2 ProjectUv(Projection m, Vector3 world)
		{
			Vector4 q = m * new Vector4(world.X, world.Y, world.Z, 1f);
			return new Vector2(q.X / q.W, q.Y / q.W);
		}

		Vector3 fwd = -eye.Basis.Column2;
		Vector3 right = eye.Basis.Column0;
		Vector3 up = eye.Basis.Column1;
		float tanY = MathF.Tan(fov * (MathF.PI / 360f));
		float tanX = tanY * aspect;

		Vector2 center = ProjectUv(reproj, origin + (fwd * 30f));
		AssertThat(Math.Abs(center.X - 0.5f)).IsLess(1e-5f);
		AssertThat(Math.Abs(center.Y - 0.5f)).IsLess(1e-5f);

		Vector2 bottomRight = ProjectUv(reproj, origin + ((fwd + (0.5f * tanX * right) - (0.5f * tanY * up)) * 30f));
		AssertThat(Math.Abs(bottomRight.X - 0.75f)).IsLess(1e-4f);
		AssertThat(Math.Abs(bottomRight.Y - 0.75f)).IsLess(1e-4f);

		// A target along a right-panned view ray lies on the right half of RenderedEye's frame (u > 0.5).
		var pannedBasis = eye.Basis.Rotated(Vector3.Up, -5f * (MathF.PI / 180f));
		Vector3 pannedCenterTarget = origin + ((-pannedBasis.Column2) * 30f);
		Vector2 pannedInRendered = ProjectUv(reproj, pannedCenterTarget);
		AssertThat(pannedInRendered.X).IsGreater(0.5f);
	}

	[TestCase]
	public void WaterWakeRingBuffer_EmitsDecaysAndWrapsCleanly()
	{
		var ring = new WaterWakeRingBuffer();
		const float waterY = 20.0f;

		// Dry land wader (above waterY + 0.12) emits nothing.
		bool activeDry = ring.Step(0.016f, new[] { new Vector3(10f, waterY + 0.5f, 10f) }, waterY,
			out _, out _, out Vector4 w0, out _);
		AssertThat(activeDry).IsFalse();
		AssertThat(w0).IsEqual(Vector4.Zero);
		AssertThat(ring.CountActiveImpulses()).IsEqual(0);

		// Entering shallow water emits a gentle ripple-only wake (< 0.30 churn threshold) and tracks wader0.
		bool activeWet = ring.Step(0.016f, new[] { new Vector3(10f, waterY - 0.25f, 10f) }, waterY,
			out Projection rA, out _, out w0, out _);
		AssertThat(activeWet).IsTrue();
		AssertThat(w0.W).IsEqual(1f);
		AssertThat(ring.CountActiveImpulses()).IsEqual(1);
		AssertThat(rA.X.X).IsEqual(10f);
		AssertThat(rA.X.W).IsLess(0.30f);

		// Active swimming / walking >=0.20 m emits velocity-gated churn pulses (>= 0.54) across all 8 ring slots.
		Projection moveImp = default;
		for (int i = 1; i <= 10; i++)
		{
			ring.Step(0.1f, new[] { new Vector3(10f + (i * 0.4f), waterY - 0.25f, 10f) }, waterY,
				out moveImp, out _, out _, out _);
		}
		AssertThat(ring.CountActiveImpulses()).IsEqual(8);
		AssertThat(moveImp.Y.W).IsGreaterEqual(0.54f);

		// Treading water in place emits gentle liquid ripples (< 0.30, zero wake churn) after IdleEmitIntervalSeconds (1.10 s).
		var idleRing = new WaterWakeRingBuffer();
		Vector3 treadPos = new(12f, waterY - 0.55f, 14f);
		idleRing.Step(0.016f, new[] { treadPos }, waterY, out _, out _, out _, out _);
		float initialClock = idleRing.Clock;
		idleRing.Step(1.15f, new[] { treadPos }, waterY, out Projection idleImp, out _, out _, out _);
		AssertThat(idleImp.Y.Z).IsGreater(initialClock);
		AssertThat(idleImp.Y.W).IsGreater(0.08f);
		AssertThat(idleImp.Y.W).IsLess(0.30f);

		// Leaving water and letting 4.0 s elapse expires every impulse cleanly back to inactive.
		bool stillActive = ring.Step(4.0f, ReadOnlySpan<Vector3>.Empty, waterY,
			out Projection clearedA, out Projection clearedB, out w0, out Vector4 w1);
		AssertThat(stillActive).IsFalse();
		AssertThat(ring.CountActiveImpulses()).IsEqual(0);
		AssertThat(clearedA).IsEqual(default(Projection));
		AssertThat(clearedB).IsEqual(default(Projection));
		AssertThat(w0).IsEqual(Vector4.Zero);
		AssertThat(w1).IsEqual(Vector4.Zero);
	}

	[TestCase]
	public void OverscanMirrorReprojection_KeepsFullMainScreenInsideSafeMargin()
	{
		const float mainFov = 60f;
		const float aspect = 16f / 9f;
		float mirrorFov = Engine_WaterQuality.ComputeOverscanFov(mainFov, Engine_WaterQuality.MirrorOverscanFactor);
		AssertThat(mirrorFov).IsGreater(60f);
		AssertThat(mirrorFov).IsLess(66f);

		Vector3 origin = new(5f, 36f, -12f);
		var eye = new Transform3D(Basis.LookingAt(new Vector3(0f, -0.25f, -1f).Normalized(), Vector3.Up), origin);
		Projection reproj = Engine_WaterQuality.BuildMainScreenReprojection(eye, mirrorFov, aspect, keepWidth: false);

		static Vector2 ProjectUv(Projection m, Vector3 world)
		{
			Vector4 q = m * new Vector4(world.X, world.Y, world.Z, 1f);
			return new Vector2(q.X / q.W, q.Y / q.W);
		}

		Vector3 fwd = -eye.Basis.Column2;
		Vector3 right = eye.Basis.Column0;
		Vector3 up = eye.Basis.Column1;
		float mainTanY = MathF.Tan(mainFov * (MathF.PI / 360f));
		float mainTanX = mainTanY * aspect;

		Vector2 center = ProjectUv(reproj, origin + (fwd * 40f));
		AssertThat(Math.Abs(center.X - 0.5f)).IsLess(1e-5f);
		AssertThat(Math.Abs(center.Y - 0.5f)).IsLess(1e-5f);

		float[] signs = { -1f, 1f };
		foreach (float sx in signs)
		{
			foreach (float sy in signs)
			{
				Vector3 cornerRay = fwd + (sx * mainTanX * right) + (sy * mainTanY * up);
				Vector2 uv = ProjectUv(reproj, origin + (cornerRay * 40f));
				AssertThat(uv.X).IsGreaterEqual(0.04f);
				AssertThat(uv.X).IsLessEqual(0.96f);
				AssertThat(uv.Y).IsGreaterEqual(0.04f);
				AssertThat(uv.Y).IsLessEqual(0.96f);
			}
		}
	}

	[TestCase]
	public void VistaOceanWaterlinePush_OccludesBelowHorizonAndPreservesAboveHorizon()
	{
		const float oceanY = 33f;
		const float aspect = 16f / 9f;
		float py = 1f / MathF.Tan(60f * (MathF.PI / 360f));
		float px = py / aspect;
		Basis levelBasis = Basis.LookingAt(new Vector3(0f, 0f, -1f), Vector3.Up);
		Vector3 eye = new(0f, 35f, 0f);

		Vector4 inactive = Refii.Lighting.Engine_PostFxParams.BuildWaterPlanePush(false, oceanY, eye, levelBasis, px, py);
		AssertThat(inactive).IsEqual(Vector4.Zero);
		AssertThat(float.IsPositiveInfinity(Refii.Lighting.Engine_PostFxParams.EvaluateWaterPlaneFacing(inactive, new Vector2(0.5f, 0.75f)))).IsTrue();

		Vector4 underwaterEye = Refii.Lighting.Engine_PostFxParams.BuildWaterPlanePush(true, oceanY, new Vector3(0f, 32f, 0f), levelBasis, px, py);
		AssertThat(underwaterEye).IsEqual(Vector4.Zero);

		Vector4 active = Refii.Lighting.Engine_PostFxParams.BuildWaterPlanePush(true, oceanY, eye, levelBasis, px, py);
		AssertThat(active.X).IsGreater(1.9f);

		// Above-horizon sky pixel stays unclipped by ocean plane.
		AssertThat(float.IsPositiveInfinity(Refii.Lighting.Engine_PostFxParams.EvaluateWaterPlaneFacing(active, new Vector2(0.5f, 0.25f)))).IsTrue();

		// Just below horizon hits ocean closer than celestial distances (SunDisc 1900 m / Moon 4000 m),
		// while a close harbor lamp halo at 20 m sits well in front of the ocean intersection.
		float justBelow = Refii.Lighting.Engine_PostFxParams.EvaluateWaterPlaneFacing(active, new Vector2(0.5f, 0.51f));
		AssertThat(justBelow).IsGreater(50f);
		AssertThat(justBelow).IsLess(1900f);

		float waterMid = Refii.Lighting.Engine_PostFxParams.EvaluateWaterPlaneFacing(active, new Vector2(0.5f, 0.55f));
		AssertThat(waterMid).IsGreater(25f);
		AssertThat(waterMid).IsLess(45f);
	}

	[TestCase]
	public void SunClockInterpolator_AdvancesUniformlyWithout15HzSawtooth()
	{
		foreach (float ratePerSec in new[] { 1.0f / 2880.0f, 0.012f })
		{
			var clock = new SunClockInterpolator();
			const float displayDt = 1.0f / 60.0f; // 4 display frames per 15 Hz guest tick
			float raw = 0.25f;
			float prev = clock.Step(raw, frozen: false, displayDt);
			float minDelta = float.PositiveInfinity;
			float maxDelta = 0.0f;

			// Run 12 guest ticks (48 display frames); after warmup, verify smooth monotonic advance with <6% variation.
			for (int tick = 0; tick < 12; tick++)
			{
				raw += ratePerSec * (1.0f / 15.0f);
				for (int sub = 0; sub < 4; sub++)
				{
					float cur = clock.Step(raw, frozen: false, displayDt);
					float delta = cur - prev;
					if (tick >= 5)
					{
						AssertThat(delta).IsGreater(0.0f);
						if (delta < minDelta)
						{
							minDelta = delta;
						}
						if (delta > maxDelta)
						{
							maxDelta = delta;
						}
					}
					prev = cur;
				}
			}

			AssertThat(maxDelta / MathF.Max(minDelta, 1e-9f)).IsLess(1.06f);

			// Freeze holds exact value and >SnapThreshold jumps snap immediately.
			float paused = clock.Step(raw, frozen: true, displayDt);
			AssertThat(clock.Step(raw, frozen: true, displayDt)).IsEqual(paused);
			AssertThat(clock.Step(raw + 0.05f, frozen: false, displayDt)).IsEqual(raw + 0.05f);
		}
	}

	[TestCase]
	public void WaterFoamTexture_IsToroidallySeamlessMultiScaleAndMipComplete()
	{
		const int size = 128;
		int expectedBytes = Engine_WaterFoamTexture.ComputeTotalMipBytes(size);
		AssertThat(expectedBytes).IsEqual(87380);

		byte[] mips = Engine_WaterFoamTexture.BuildMipmappedRgba8(size);
		AssertThat(mips.Length).IsEqual(expectedBytes);

		int pixelCount = size * size;
		var minCh = new int[] { 255, 255, 255, 255 };
		var maxCh = new int[] { 0, 0, 0, 0 };
		long sumA = 0;

		for (int i = 0; i < pixelCount; i++)
		{
			int baseIdx = i * 4;
			for (int c = 0; c < 4; c++)
			{
				int v = mips[baseIdx + c];
				if (v < minCh[c]) minCh[c] = v;
				if (v > maxCh[c]) maxCh[c] = v;
			}
			sumA += mips[baseIdx + 3];
		}

		// Every channel (R froth, G round bubbles, B micro-fizz, A histogram-equalized erosion) spans wide dynamic range.
		for (int c = 0; c < 4; c++)
		{
			AssertThat(minCh[c]).IsLess(20);
			AssertThat(maxCh[c]).IsGreater(200);
		}

		float meanA = (float)sumA / pixelCount;
		AssertThat(meanA).IsGreater(115f);
		AssertThat(meanA).IsLess(140f);

		// Toroidal wrap continuity: average border step across (0 <-> size-1) must match adjacent interior steps without seam jumps.
		double borderDiffSum = 0.0;
		double interiorDiffSum = 0.0;
		for (int i = 0; i < size; i++)
		{
			int left = ((i * size) + 0) * 4;
			int right = ((i * size) + (size - 1)) * 4;
			int midA = ((i * size) + 63) * 4;
			int midB = ((i * size) + 64) * 4;

			int top = ((0 * size) + i) * 4;
			int bottom = (((size - 1) * size) + i) * 4;
			int midY0 = ((63 * size) + i) * 4;
			int midY1 = ((64 * size) + i) * 4;

			for (int c = 0; c < 4; c++)
			{
				borderDiffSum += Math.Abs(mips[left + c] - mips[right + c]) + Math.Abs(mips[top + c] - mips[bottom + c]);
				interiorDiffSum += Math.Abs(mips[midA + c] - mips[midB + c]) + Math.Abs(mips[midY0 + c] - mips[midY1 + c]);
			}
		}

		AssertThat(borderDiffSum / Math.Max(interiorDiffSum, 1.0)).IsLess(1.45);

		// Verify coverage-preserving mip levels retain non-zero bubble peaks down at mip 3 (16x16).
		int mip3Offset = (128 * 128 * 4) + (64 * 64 * 4) + (32 * 32 * 4);
		int maxMip3G = 0;
		int maxMip3B = 0;
		for (int i = 0; i < 16 * 16; i++)
		{
			maxMip3G = Math.Max(maxMip3G, mips[mip3Offset + (i * 4) + 1]);
			maxMip3B = Math.Max(maxMip3B, mips[mip3Offset + (i * 4) + 2]);
		}
		AssertThat(maxMip3G).IsGreater(80);
		AssertThat(maxMip3B).IsGreater(60);
	}

	[TestCase]
	public void WaterWakeRingBuffer_TracksDirectionalVelocityAndDecaysChurnWhenIdle()
	{
		var ring = new WaterWakeRingBuffer();
		const float waterY = 20.0f;
		var wader = new Vector3(5f, waterY - 0.65f, 8f);

		// Initial stationary frame: zero speed and zero churn energy.
		ring.Step(0.016f, new[] { wader }, waterY, out _, out _, out _, out _, out Vector4 vel0, out _);
		AssertThat(vel0.Z).IsEqual(0f);
		AssertThat(vel0.W).IsEqual(0f);

		// Swim steadily along +X at 2.5 m/s for 0.6 s -> heading aligns with +X and churn energy ramps above 0.35.
		for (int f = 0; f < 10; f++)
		{
			wader.X += 0.15f;
			ring.Step(0.06f, new[] { wader }, waterY, out _, out _, out _, out _, out vel0, out _);
		}
		AssertThat(vel0.X).IsGreater(0.95f);
		AssertThat(MathF.Abs(vel0.Y)).IsLess(0.10f);
		AssertThat(vel0.Z).IsGreater(1.8f);
		AssertThat(vel0.W).IsGreater(0.35f);

		// Stop moving and tread water in place for 1.5 s -> churn energy decays below eval_swimmer_vwake's 0.04 gate.
		for (int f = 0; f < 25; f++)
		{
			ring.Step(0.06f, new[] { wader }, waterY, out _, out _, out _, out _, out vel0, out _);
		}
		AssertThat(vel0.Z).IsLess(0.05f);
		AssertThat(vel0.W).IsLess(0.04f);
	}

	[TestCase]
	public void WaterWakeRingBuffer_CoSwimmingHeroAndDogDoNotStealSlotsAndFollowCharacterFacing()
	{
		var ring = new WaterWakeRingBuffer();
		const float waterY = 20.0f;
		var heroPos = new Vector3(10f, waterY - 0.65f, 12f);
		var dogPos = new Vector3(11.05f, waterY - 0.55f, 12.60f); // ~1.21 m away (< 1.8 m match radius)

		// 1. Stationary hero + dog treading water within 1.21 m of each other must NEVER steal slots or hallucinate velocity.
		Vector4 vel0 = Vector4.Zero;
		Vector4 vel1 = Vector4.Zero;
		ReadOnlySpan<WaderSample> idlePair = stackalloc WaderSample[]
		{
			new(100u, heroPos, new Vector2(1f, 0f)),
			new(200u, dogPos, new Vector2(0f, 1f)),
		};
		for (int f = 0; f < 30; f++)
		{
			ring.Step(0.033f, idlePair, waterY, out _, out _, out _, out _, out vel0, out vel1);
		}
		AssertThat(vel0.Z).IsEqual(0f);
		AssertThat(vel0.W).IsEqual(0f);
		AssertThat(vel1.Z).IsEqual(0f);
		AssertThat(vel1.W).IsEqual(0f);

		// 2. Anonymous Vector3 overload (Id == 0) also claims slots per step so close stationary waders stay at zero churn.
		var anonRing = new WaterWakeRingBuffer();
		var anonPair = new[] { heroPos, dogPos };
		for (int f = 0; f < 20; f++)
		{
			anonRing.Step(0.033f, anonPair, waterY, out _, out _, out _, out _, out Vector4 aVel0, out Vector4 aVel1);
			AssertThat(aVel0.W).IsEqual(0f);
			AssertThat(aVel1.W).IsEqual(0f);
		}

		// 3. Hero swims forward along diagonal facing (0.7071, 0.7071) while dog stays stationary nearby:
		//    hero's V-wake tracks drawn character facing cleanly while stationary dog keeps zero churn.
		var diagFwd = new Vector2(0.70710677f, 0.70710677f);
		Span<WaderSample> movingHero = stackalloc WaderSample[2];
		for (int f = 0; f < 14; f++)
		{
			heroPos.X += 0.08f;
			heroPos.Z += 0.04f;
			movingHero[0] = new WaderSample(100u, heroPos, diagFwd);
			movingHero[1] = new WaderSample(200u, dogPos, new Vector2(0f, 1f));
			ring.Step(0.033f, movingHero, waterY, out _, out _, out _, out _, out vel0, out vel1);
		}
		AssertThat(vel0.W).IsGreater(0.35f);
		AssertThat(vel0.X).IsGreater(0.65f);
		AssertThat(vel0.Y).IsGreater(0.50f);
		AssertThat(vel1.W).IsEqual(0f);
	}

	[TestCase]
	public void ComputeWaterCelestial_PublishesPinchedGlitterApexAndPreHorizonRiseRamp()
	{
		AssertThat(Engine_CelestialElements.ModernMoonScale).IsEqual(0.75f);

		var theme = new SkyTheme
		{
			SunDiscSize = 1.2f,
			SunDiscIntensity = 1.0f,
			MoonSize = 1.7f,
			MoonIntensity = 1.4f,
		};

		// 1. Daytime Sun active above horizon -> full strength and tight pinched-cone apex (~0.014).
		var dayFrame = new SkyFrame();
		dayFrame.SkyParams[0] = 1.0f;
		dayFrame.SunDirection[0] = 0.0f;
		dayFrame.SunDirection[1] = 0.45f;
		dayFrame.SunDirection[2] = 0.893f;
		dayFrame.MoonDirection[0] = 0.0f;
		dayFrame.MoonDirection[1] = -0.45f;
		dayFrame.MoonDirection[2] = -0.893f;

		(Vector4 dayParams, Vector3 dayDir) = Engine_SkyEnvironment.ComputeWaterCelestial(
			dayFrame, theme, dynamicSkyEnabled: false, modernLighting: true);
		AssertThat(dayParams.X).IsEqual(1.0f);
		AssertThat(dayParams.Z).IsEqual(0.0f);
		AssertThat(dayParams.Y).IsEqual(0.014f);
		AssertThat(dayParams.W).IsEqual(0.014f);
		AssertThat(dayDir.Y).IsGreater(0.40f);

		// 2. Limb-breach Moonrise: before the visible top edge breaches y = 0 (y = -0.085 < -moonLimbSin ~ -0.0744),
		//    glitter stays strictly 0.0; once the upper limb breaches y = 0 (y = -0.045), it eases smoothly > 0.
		var risingMoonFrame = new SkyFrame();
		risingMoonFrame.SkyParams[0] = 0.0f;
		risingMoonFrame.SunDirection[0] = 0.0f;
		risingMoonFrame.SunDirection[1] = -0.40f;
		risingMoonFrame.SunDirection[2] = -0.9165f;
		risingMoonFrame.MoonDirection[0] = 0.25f;
		risingMoonFrame.MoonDirection[1] = -0.085f;
		risingMoonFrame.MoonDirection[2] = 0.9645f;

		(Vector4 belowLimbParams, _) = Engine_SkyEnvironment.ComputeWaterCelestial(
			risingMoonFrame, theme, dynamicSkyEnabled: false, modernLighting: true);
		AssertThat(belowLimbParams.X).IsEqual(0.0f);

		risingMoonFrame.MoonDirection[1] = -0.045f;
		risingMoonFrame.MoonDirection[2] = 0.967f;
		(Vector4 risingParams, _) = Engine_SkyEnvironment.ComputeWaterCelestial(
			risingMoonFrame, theme, dynamicSkyEnabled: false, modernLighting: true);
		AssertThat(risingParams.X).IsGreater(0.02f);
		AssertThat(risingParams.X).IsLess(0.55f);
		AssertThat(risingParams.Z).IsEqual(1.0f);

		// 3. Nighttime Moon well above horizon -> strong HDR glitter weight (>= 0.55) + tight pinched apex (0.013).
		risingMoonFrame.MoonDirection[1] = 0.35f;
		risingMoonFrame.MoonDirection[2] = 0.903f;
		(Vector4 nightParams, Vector3 nightDir) = Engine_SkyEnvironment.ComputeWaterCelestial(
			risingMoonFrame, theme, dynamicSkyEnabled: false, modernLighting: true);
		AssertThat(nightParams.X).IsGreater(0.85f);
		AssertThat(nightParams.Z).IsEqual(1.0f);
		AssertThat(nightParams.Y).IsEqual(0.013f);
		AssertThat(nightParams.W).IsEqual(0.013f);
		AssertThat(nightDir.Y).IsGreater(0.30f);

		// 4. Deep below horizon -> glitter strength gated strictly to 0.
		risingMoonFrame.MoonDirection[1] = -0.30f;
		(Vector4 darkParams, _) = Engine_SkyEnvironment.ComputeWaterCelestial(
			risingMoonFrame, theme, dynamicSkyEnabled: false, modernLighting: true);
		AssertThat(darkParams.X).IsEqual(0.0f);
	}

	[TestCase]
	public void LunarPhaseParamsAndMoonBloomMask_SuppressCoveredHemisphereAndPreserveFullMoonSymmetry()
	{
		const float moonHalf = 225.0f;
		const float glareHalf = 525.0f;

		// 1. Verify all 8 lunar phase 3D light vectors (xyz = litDir with 12-deg celestial tilt, w = discRadiusInGlare):
		//    Phases 1, 2, 6, 7 are curved crescents (litDir.Z < -0.40, never a flat 0.0 half-cut),
		//    Phases 3, 5 are curved gibbous (litDir.Z > 0.60), Phase 4 is Full Moon (litDir.Z == 1.0), Phase 0 is New Moon (-1.0).
		Vector4 newMoon = Engine_CelestialElements.ComputeLunarPhaseParams(0, moonHalf, glareHalf);
		AssertThat(MathF.Abs(newMoon.X)).IsLess(1e-5f);
		AssertThat(MathF.Abs(newMoon.Y)).IsLess(1e-5f);
		AssertThat(newMoon.Z).IsEqual(-1.0f);
		AssertThat(Engine_CelestialElements.ComputeLunarIllumination(newMoon.Z)).IsEqual(0.0f);
		AssertThat(newMoon.W).IsGreater(0.38f);
		AssertThat(newMoon.W).IsLess(0.41f);

		foreach (int p in new[] { 1, 2 })
		{
			Vector4 waxingCrescent = Engine_CelestialElements.ComputeLunarPhaseParams(p, moonHalf, glareHalf);
			AssertThat(waxingCrescent.X).IsGreater(0.65f);
			AssertThat(waxingCrescent.Y).IsLess(-0.10f);
			AssertThat(waxingCrescent.Z).IsLess(-0.40f);
			AssertThat(Engine_CelestialElements.ComputeLunarIllumination(waxingCrescent.Z)).IsGreater(0.13f);
		}

		Vector4 waxingGibbous = Engine_CelestialElements.ComputeLunarPhaseParams(3, moonHalf, glareHalf);
		AssertThat(waxingGibbous.X).IsGreater(0.75f);
		AssertThat(waxingGibbous.Y).IsLess(-0.12f);
		AssertThat(waxingGibbous.Z).IsGreater(0.60f);

		Vector4 fullMoon = Engine_CelestialElements.ComputeLunarPhaseParams(4, moonHalf, glareHalf);
		AssertThat(MathF.Abs(fullMoon.X)).IsLess(1e-5f);
		AssertThat(MathF.Abs(fullMoon.Y)).IsLess(1e-5f);
		AssertThat(fullMoon.Z).IsEqual(1.0f);
		AssertThat(Engine_CelestialElements.ComputeLunarIllumination(fullMoon.Z)).IsEqual(1.0f);

		Vector4 waningGibbous = Engine_CelestialElements.ComputeLunarPhaseParams(5, moonHalf, glareHalf);
		AssertThat(waningGibbous.X).IsLess(-0.75f);
		AssertThat(waningGibbous.Y).IsLess(-0.12f);
		AssertThat(waningGibbous.Z).IsGreater(0.60f);

		foreach (int p in new[] { 6, 7 })
		{
			Vector4 waningCrescent = Engine_CelestialElements.ComputeLunarPhaseParams(p, moonHalf, glareHalf);
			AssertThat(waningCrescent.X).IsLess(-0.65f);
			AssertThat(waningCrescent.Y).IsLess(-0.10f);
			AssertThat(waningCrescent.Z).IsLess(-0.40f);
			AssertThat(Engine_CelestialElements.ComputeLunarIllumination(waningCrescent.Z)).IsGreater(0.13f);
		}

		// 2. Verify Moon star occluder sentinel when inactive vs cosine angular radius when active.
		Vector4 inactiveOcc = Engine_CelestialElements.ComputeMoonStarOccluder(new Vector3(0f, 0.5f, 0.866f), moonHalf, 4000f, moonActive: false);
		AssertThat(inactiveOcc.W).IsEqual(2.0f);

		Vector4 activeOcc = Engine_CelestialElements.ComputeMoonStarOccluder(new Vector3(0f, 0.6f, 0.8f), moonHalf, 4000f, moonActive: true);
		AssertThat(activeOcc.W).IsGreater(0.998f);
		AssertThat(activeOcc.W).IsLess(1.0f);

		// 3. Verify ApplyLunarPhaseMaskToRgba8 carves curved crescents out of a Full-Moon cell (Column 4)
		//    while stripping outer rim fringe (r > 0.92).
		static byte[] BuildFullMoonCell(int size, byte litLuma)
		{
			var buf = new byte[size * size * 4];
			for (int y = 0; y < size; y++)
			{
				float dy = ((y + 0.5f) / (size * 0.5f)) - 1.0f;
				for (int x = 0; x < size; x++)
				{
					float dx = ((x + 0.5f) / (size * 0.5f)) - 1.0f;
					float r = MathF.Sqrt((dx * dx) + (dy * dy));
					int p = ((y * size) + x) * 4;
					if (r > 0.96f)
					{
						buf[p] = 255; buf[p + 1] = 255; buf[p + 2] = 255; buf[p + 3] = 0;
					}
					else if (r > 0.92f)
					{
						buf[p] = 190; buf[p + 1] = 190; buf[p + 2] = 190; buf[p + 3] = 89;
					}
					else
					{
						buf[p] = litLuma; buf[p + 1] = litLuma; buf[p + 2] = litLuma; buf[p + 3] = 128;
					}
				}
			}
			return buf;
		}

		const int sz = 128;
		int midRow = 64 * sz * 4;

		// Phase 0 (New Moon): entire sprite including rim fringe must be zeroed.
		byte[] phase0 = BuildFullMoonCell(sz, 205);
		Refii.Fx.Engine_PostFxHalos.ApplyLunarPhaseMaskToRgba8(phase0, sz, sz, lunarPhase: 0);
		for (int i = 0; i < phase0.Length; i++)
		{
			AssertThat(phase0[i]).IsEqual((byte)0);
		}

		// Phase 2 (Waxing Crescent): center (x=64, y=64) must be carved out into a concave crescent (alpha == 0),
		// while outer right crescent belly (x=106, y=64) is brightly lit!
		byte[] phase2 = BuildFullMoonCell(sz, 210);
		Refii.Fx.Engine_PostFxHalos.ApplyLunarPhaseMaskToRgba8(phase2, sz, sz, lunarPhase: 2);
		AssertThat(phase2[midRow + (4 * 4) + 3]).IsEqual((byte)0);
		AssertThat(phase2[midRow + (64 * 4) + 3]).IsEqual((byte)0);
		AssertThat(phase2[midRow + (106 * 4) + 0]).IsGreater((byte)160);
		AssertThat(phase2[midRow + (106 * 4) + 3]).IsGreater((byte)120);

		// Phase 3 (Waxing Gibbous): covered far-left side (x=10) is zeroed, center (x=64) and right (x=100) are lit.
		byte[] phase3 = BuildFullMoonCell(sz, 210);
		Refii.Fx.Engine_PostFxHalos.ApplyLunarPhaseMaskToRgba8(phase3, sz, sz, lunarPhase: 3);
		AssertThat(phase3[midRow + (10 * 4) + 3]).IsEqual((byte)0);
		AssertThat(phase3[midRow + (64 * 4) + 3]).IsGreater((byte)120);
		AssertThat(phase3[midRow + (100 * 4) + 3]).IsGreater((byte)120);

		// Phase 4 (Full Moon): outer rim (x=4, x=124) cleared, both left (x=24) and right (x=104) symmetrically lit.
		byte[] phase4 = BuildFullMoonCell(sz, 205);
		Refii.Fx.Engine_PostFxHalos.ApplyLunarPhaseMaskToRgba8(phase4, sz, sz, lunarPhase: 4);
		AssertThat(phase4[midRow + (4 * 4) + 3]).IsEqual((byte)0);
		AssertThat(phase4[midRow + (124 * 4) + 3]).IsEqual((byte)0);
		AssertThat(phase4[midRow + (24 * 4) + 3]).IsEqual(phase4[midRow + (104 * 4) + 3]);
		AssertThat(phase4[midRow + (24 * 4) + 3]).IsGreater((byte)120);
	}

	[TestCase]
	public void CelestialShaders_RespectDxilZeroDiscardPrecomputedDerivativesAndPbrIsolation()
	{
		string? dir = AppContext.BaseDirectory;
		string? celDir = null;
		while (!string.IsNullOrEmpty(dir))
		{
			string candidate = System.IO.Path.Combine(dir, "src", "Engine", "level", "environment", "celestial");
			if (System.IO.Directory.Exists(candidate)) { celDir = candidate; break; }
			string nested = System.IO.Path.Combine(dir, "Godot", "src", "Engine", "level", "environment", "celestial");
			if (System.IO.Directory.Exists(nested)) { celDir = nested; break; }
			dir = System.IO.Path.GetDirectoryName(dir);
		}
		AssertThat(celDir).IsNotNull();

		foreach (string file in new[] { "celestial.gdshader", "celestial_overlay.gdshader", "stars.gdshader" })
		{
			string src = System.IO.File.ReadAllText(System.IO.Path.Combine(celDir!, file));
			AssertThat(System.Text.RegularExpressions.Regex.IsMatch(src, @"\bdiscard\s*;")).IsFalse();
			AssertThat(src.Contains("safe_w", StringComparison.Ordinal)).IsTrue();
		}

		string mixSrc = System.IO.File.ReadAllText(System.IO.Path.Combine(celDir!, "celestial.gdshader"));
		AssertThat(mixSrc.Contains("uniform bool is_pbr = false;", StringComparison.Ordinal)).IsTrue();
		AssertThat(mixSrc.Contains("textureGrad(element_tex, UV, uv_dx, uv_dy)", StringComparison.Ordinal)).IsTrue();
		AssertThat(mixSrc.Contains("inversesqrt(", StringComparison.Ordinal)).IsTrue();

		string ovlSrc = System.IO.File.ReadAllText(System.IO.Path.Combine(celDir!, "celestial_overlay.gdshader"));
		AssertThat(ovlSrc.Contains("uniform bool is_pbr = false;", StringComparison.Ordinal)).IsTrue();
		AssertThat(ovlSrc.Contains("textureGrad(element_tex, UV, uv_dx, uv_dy)", StringComparison.Ordinal)).IsTrue();
		AssertThat(ovlSrc.Contains("moon_arc_lobe(", StringComparison.Ordinal)).IsTrue();
		AssertThat(ovlSrc.Contains("(0.5 - UV.y) * 2.0", StringComparison.Ordinal)).IsTrue();

		string starSrc = System.IO.File.ReadAllText(System.IO.Path.Combine(celDir!, "stars.gdshader"));
		AssertThat(starSrc.Contains("moon_occluder", StringComparison.Ordinal)).IsTrue();
	}
}
