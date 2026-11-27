using System;
using System.Collections.Generic;
using Godot;
using Refii.Models;
using Refii.Textures;

namespace Refii.Fx;

/// <summary>
/// Modern-renderer remaster enhancement for Fable II's Will spell magic circle (<c>FX_DAD44F56</c>).
/// Samples the live landscape heightfield alongside the guest's terrain-conforming 324-point spline
/// loop so all 3 concentric harmonic rings, the outward pulsing wave, and the rising perimeter motes
/// hug hills, slopes, and bridges cleanly without clipping. Smoothly transitions the entire circle,
/// motes, and dynamic ground <see cref="OmniLight3D"/> to the active charge tier's Fable II spell
/// colour while scaling ring radiance and ground illumination with spell charge strength (Levels 1–5).
/// Strictly disabled under Classic mode or when <c>fx_remaster_magic_circle</c> is turned off.
/// </summary>
public static class Engine_RemasterMagicCircle
{
	public const string SettingKey = "fx_remaster_magic_circle";
	public const string EnvironmentVariable = "REFII_FX_REMASTER_MAGIC_CIRCLE";
	public const string RingShaderPath = "res://src/Engine/fx/fx_magic_circle_remaster.gdshader";

	/// <summary>FNV-1 hash of Fable II's persistent Will targeting/charging spline circle (<c>FX_DAD44F56</c>).</summary>
	public const uint MagicCircleEffectHash = 0xDAD44F56u;

	/// <summary>FNV-1 hash of Fable II's orbiting Will circle wave segments (<c>FX_AA2128CA</c>).</summary>
	public const uint MagicCircleWaveEffectHash = 0xAA2128CAu;

	/// <summary>
	/// Authored peak guest intensity (<c>handle + 52</c>) for <c>FX_DAD44F56</c>: live inspection
	/// shows Fable II drives the magic circle at <c>0.050</c> steady-state rather than <c>1.0</c>.
	/// </summary>
	public const float AuthoredMaxIntensity = 0.05f;

	/// <summary>Angular subdivision around the circumference (also the width of <c>perimeter_height_tex</c>).</summary>
	public const int AngularBins = 96;

	/// <summary>Maximum Will charge levels (Fable II spell slots 0..4 = Charge Levels 1..5).</summary>
	public const int MaxChargeTiers = 5;

	/// <summary>3 concentric harmonic rings (outer halo skirt, inner filigree, inner sanctum) + 1 outward pulse wave.</summary>
	public const int RingBands = 4;

	/// <summary>Total vertices emitted per frame across the 4 bands.</summary>
	public const int RingVertexCount = AngularBins * RingBands * 6;

	/// <summary>Ground clearance above the resolved terrain/bridge surface to prevent hill triangle clipping.</summary>
	public const float LiftMeters = 0.095f;

	/// <summary>Delegate for world-space landscape heightfield queries (injectable for headless unit tests).</summary>
	public delegate bool GroundHeightSampler(Vector3 worldPos, out float groundY);

	/// <summary>Calibrated hue-preserving linear HDR palette for one Fable II Will spell.</summary>
	public readonly record struct SpellPalette(Color Core, Color Accent, Color Light, float LuminanceScale = 1.0f);

	private static bool _configured;
	private static bool _enabled = true;

	/// <summary>Terrain-conforming circle profile extracted from the live <c>Type 9</c> spline points.</summary>
	public sealed class CircleProfile
	{
		public Vector3 Center;
		public float CenterY;
		public float Radius;
		public readonly float[] PerimeterY = new float[AngularBins];
	}

	private sealed class CircleRig
	{
		public Node3D Root = null!;
		public MeshInstance3D RingMeshNode = null!;
		public ArrayMesh RingMesh = null!;
		public ShaderMaterial RingMaterial = null!;
		public GpuParticles3D Motes = null!;
		public ShaderMaterial MoteProcessMaterial = null!;
		public ShaderMaterial MoteRenderMaterial = null!;
		public Image PerimeterHeightImage = null!;
		public ImageTexture PerimeterHeightTex = null!;
		public OmniLight3D Light = null!;

		public readonly CircleProfile Profile = new();
		public readonly int[] SlotSpells = { -1, -1, -1, -1, -1 };
		public readonly float[] PerimeterLocalY = new float[AngularBins];
		public readonly Vector3[] Positions = new Vector3[RingVertexCount];
		public readonly Vector2[] Uvs = new Vector2[RingVertexCount];
		public readonly Color[] Colors = new Color[RingVertexCount];
		public Godot.Collections.Array? SurfaceArrays;

		public int ActiveChargeLevel = 1;
		public float SmoothedChargeLevel = 1f;
		public Vector3? HeroPosition;
		public Vector3 SmoothedCenter;
		public float SmoothedRadius;
		public bool HasSmoothedState;
		public float TimeSeconds;
		public float LiveAlpha;
		public Color Tint = Godot.Colors.White;
		public Color SmoothedCore = new(0.14f, 0.56f, 1.00f, 1f);
		public Color SmoothedAccent = new(0.45f, 0.84f, 1.00f, 1f);
		public Color SmoothedLight = new(0.24f, 0.72f, 1.00f, 1f);
		public float SmoothedLuminanceScale = 1f;
		public bool HasSmoothedColor;
		public bool SeenThisWalk;

		// Cached uniform/property values to avoid redundant Godot interop writes.
		public float LastShaderAlpha = -1f;
		public float LastShaderTime = -1f;
		public Vector3 LastCoreRad = new(-1f, -1f, -1f);
		public Vector3 LastAccentRad = new(-1f, -1f, -1f);
		public Vector3 LastMoteRadiance = new(-1f, -1f, -1f);
		public float LastMoteRadius = -1f;
		public float LastMoteHeight = -1f;
		public float LastMoteSpin = -1f;
		public float LastMoteAlpha = -1f;
		public bool LastLightVisible;
		public Vector3 LastLightPos = new(float.NaN, float.NaN, float.NaN);
		public float LastLightRange = -1f;
		public float LastLightEnergy = -1f;
		public Color LastLightColor = new(-1f, -1f, -1f, -1f);
	}

	private static readonly Dictionary<uint, CircleRig> _rigs = new();
	private static readonly List<uint> _deadKeys = new();

	private static Shader? _ringShader;
	private static Shader? _moteProcessShader;
	private static Shader? _moteRenderShader;
	private static QuadMesh? _moteQuad;

	private const string MoteProcessShaderCode = """
		shader_type particles;
		uniform sampler2D perimeter_height_tex : filter_linear, repeat_enable;
		uniform float circle_radius = 1.5;
		uniform float climb_height = 0.9;
		uniform float spin_rate = 1.15;
		uniform float mote_alpha = 1.0;
		uniform float mote_size = 0.10;

		uint pcg(uint v) {
			uint state = v * 747796405u + 2891336453u;
			uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
			return (word >> 22u) ^ word;
		}

		float rnd(uint seed) {
			return float(pcg(seed)) / 4294967295.0;
		}

		void start() {
			uint s = RANDOM_SEED + NUMBER * 7919u;
			CUSTOM = vec4(rnd(s) * 6.2831853, rnd(s + 1u) * 0.25, rnd(s + 2u), rnd(s + 3u));
			TRANSFORM[3].xyz = vec3(0.0, -1000.0, 0.0);
		}

		void process() {
			CUSTOM.y += DELTA / LIFETIME;
			float t = CUSTOM.y;
			if (t >= 1.0) {
				ACTIVE = false;
			} else {
				float r_jitter = 0.92 + 0.16 * CUSTOM.z;
				float rr = circle_radius * r_jitter * mix(1.0, 0.88, t * 0.55);
				float ang = CUSTOM.x + spin_rate * t * LIFETIME * (0.75 + 0.5 * CUSTOM.w);
				float lift = t * climb_height * (0.75 + 0.45 * CUSTOM.w);
				float bob = 0.03 * sin(ang * 3.0 + CUSTOM.z * 6.2831853);
				float u = fract(ang / 6.2831853);
				float ground_y = texture(perimeter_height_tex, vec2(u, 0.5)).r;
				TRANSFORM[3].xyz = vec3(cos(ang) * rr, ground_y + 0.14 + lift + bob, sin(ang) * rr);

				float env = sin(clamp(t, 0.0, 1.0) * 3.14159265);
				float twinkle = 0.72 + 0.28 * sin(t * 18.0 + CUSTOM.x * 5.0);
				COLOR = vec4(1.0, 1.0, 1.0, env * twinkle * mote_alpha);

				float sz = mote_size * (0.65 + 0.65 * CUSTOM.z) * mix(1.0, 0.45, t);
				TRANSFORM[0].xyz = vec3(sz, 0.0, 0.0);
				TRANSFORM[1].xyz = vec3(0.0, sz, 0.0);
				TRANSFORM[2].xyz = vec3(0.0, 0.0, sz);
			}
		}
		""";

	private const string MoteRenderShaderCode = """
		shader_type spatial;
		render_mode blend_add, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

		uniform sampler2D mote_tex : hint_default_white, filter_linear, repeat_disable;
		uniform vec3 mote_radiance = vec3(0.75, 1.85, 3.80);
		uniform bool has_mote_tex = false;
		uniform bool is_pbr = false;

		global uniform float fable_emission_boost = 1.0;

		void vertex() {
			mat4 bb = mat4(
				INV_VIEW_MATRIX[0],
				INV_VIEW_MATRIX[1],
				INV_VIEW_MATRIX[2],
				MODEL_MATRIX[3]);
			MODELVIEW_MATRIX = VIEW_MATRIX * bb * mat4(
				vec4(length(MODEL_MATRIX[0].xyz), 0.0, 0.0, 0.0),
				vec4(0.0, length(MODEL_MATRIX[1].xyz), 0.0, 0.0),
				vec4(0.0, 0.0, length(MODEL_MATRIX[2].xyz), 0.0),
				vec4(0.0, 0.0, 0.0, 1.0));
		}

		void fragment() {
			vec2 uv_dx = dFdx(UV);
			vec2 uv_dy = dFdy(UV);
			vec4 texel = textureGrad(mote_tex, UV, uv_dx, uv_dy);

			vec2 centered = (UV - vec2(0.5)) * 2.0;
			float r2 = dot(centered, centered);
			float radial_star = exp(-r2 * 4.5) + 0.35 * exp(-abs(centered.x) * 9.0) * exp(-abs(centered.y) * 9.0);
			float cov = has_mote_tex
				? max(texel.a, max(texel.r, max(texel.g, texel.b))) * clamp(1.0 - r2, 0.0, 1.0)
				: clamp(radial_star, 0.0, 1.0);

			float a = cov * COLOR.a;
			bool should_skip = (a <= 0.003);

			ALBEDO = vec3(0.0);
			ALPHA = 0.0;

			if (!should_skip) {
				float boost = is_pbr ? 1.0 : max(fable_emission_boost, 1.0);
				vec3 rgb = max(mote_radiance * boost, vec3(0.0));
				ALBEDO = rgb;
				ALPHA = clamp(a, 0.0, 1.0);
			}
		}
		""";

	/// <summary>
	/// Returns true when <paramref name="effectHash"/> is one of Fable II's original ground magic
	/// circle effects (<c>FX_DAD44F56</c> or <c>FX_AA2128CA</c>) that should be hidden while the
	/// Modern remaster circle is active.
	/// </summary>
	public static bool ShouldSuppressOriginalEffect(uint effectHash, bool remasterActive) =>
		remasterActive && (effectHash == MagicCircleEffectHash || effectHash == MagicCircleWaveEffectHash);

	/// <summary>Live convenience overload using <see cref="Active"/>.</summary>
	public static bool ShouldSuppressOriginalEffect(uint effectHash) =>
		ShouldSuppressOriginalEffect(effectHash, Active);

	/// <summary>
	/// Normalises <c>FX_DAD44F56</c>'s authored low guest intensity (<c>0.050</c> max) into a
	/// <c>[0, 1.25]</c> remaster visibility strength so full-charge circles render at full radiance.
	/// </summary>
	public static float NormalizeCircleStrength(float intensity, float lightFade, float statedAlpha)
	{
		if (!float.IsFinite(intensity) || !float.IsFinite(lightFade) || !float.IsFinite(statedAlpha))
		{
			return 0f;
		}

		return Math.Clamp((intensity / AuthoredMaxIntensity) * lightFade * statedAlpha, 0f, 1.25f);
	}

	/// <summary>
	/// Maps a Fable II Will spell index (<c>0..7</c> from <c>Engine_UiScreenConstants.Wills</c>)
	/// to its calibrated linear HDR core/accent/light palette so saturated hues (such as Inferno red
	/// and Raise Dead purple) stay faithful under Godot's HDR tonemapping.
	/// </summary>
	public static SpellPalette ResolveSpellPalette(int spellId) => spellId switch
	{
		// 0: Shock (Lightning) — Electric Blue
		0 => new SpellPalette(
			new Color(0.08f, 0.45f, 1.00f, 1f),
			new Color(0.38f, 0.80f, 1.00f, 1f),
			new Color(0.18f, 0.62f, 1.00f, 1f),
			1.00f),
		// 1: Inferno (Fireball) — Rich Fiery Red (low green keeps HDR tonemapping red, not yellow)
		1 => new SpellPalette(
			new Color(1.00f, 0.045f, 0.01f, 1f),
			new Color(1.00f, 0.18f, 0.03f, 1f),
			new Color(1.00f, 0.16f, 0.06f, 1f),
			1.10f),
		// 2: Time Control (Time Slow) — Pure Luminous White (normalized luminance to avoid harsh bloom)
		2 => new SpellPalette(
			new Color(0.95f, 0.97f, 1.00f, 1f),
			new Color(1.00f, 1.00f, 1.00f, 1f),
			new Color(0.92f, 0.96f, 1.00f, 1f),
			0.78f),
		// 3: Blades (Swords) — Radiant Gold
		3 => new SpellPalette(
			new Color(1.00f, 0.62f, 0.06f, 1f),
			new Color(1.00f, 0.82f, 0.24f, 1f),
			new Color(1.00f, 0.72f, 0.16f, 1f),
			0.95f),
		// 4: Vortex — Vibrant Emerald Green
		4 => new SpellPalette(
			new Color(0.06f, 0.95f, 0.28f, 1f),
			new Color(0.32f, 1.00f, 0.56f, 1f),
			new Color(0.16f, 0.95f, 0.38f, 1f),
			0.95f),
		// 5: Chaos — Arcane Magenta-Pink
		5 => new SpellPalette(
			new Color(0.95f, 0.08f, 0.72f, 1f),
			new Color(1.00f, 0.36f, 0.90f, 1f),
			new Color(0.95f, 0.22f, 0.80f, 1f),
			1.00f),
		// 6: Force Push — Crisp Light Blue
		6 => new SpellPalette(
			new Color(0.30f, 0.78f, 1.00f, 1f),
			new Color(0.65f, 0.94f, 1.00f, 1f),
			new Color(0.42f, 0.84f, 1.00f, 1f),
			0.95f),
		// 7: Raise Dead — Spectral Necrotic Teal / Ghostly Turquoise (distinct from Chaos purple-magenta)
		7 => new SpellPalette(
			new Color(0.02f, 0.88f, 0.72f, 1f),
			new Color(0.28f, 1.00f, 0.88f, 1f),
			new Color(0.08f, 0.92f, 0.78f, 1f),
			0.98f),
		// Default / empty slot — Classic Albion Will Cyan
		_ => new SpellPalette(
			new Color(0.14f, 0.56f, 1.00f, 1f),
			new Color(0.45f, 0.84f, 1.00f, 1f),
			new Color(0.24f, 0.72f, 1.00f, 1f),
			1.00f),
	};

	/// <summary>Convenience accessor returning the linear core RGB for <paramref name="spellId"/>.</summary>
	public static Color ResolveSpellColor(int spellId) => ResolveSpellPalette(spellId).Core;

	/// <summary>
	/// Ring HDR radiance multiplier denoting spell strength across Charge Levels <c>1..5</c>
	/// using a perceptually uniform exponential step (~+0.71 EV / +63% per tier, <c>0.28x</c> at
	/// Level 1 up to <c>2.00x</c> at Level 5).
	/// </summary>
	public static float ResolveChargeRadianceScale(float chargeLevel)
	{
		float t = Math.Clamp((chargeLevel - 1f) / (MaxChargeTiers - 1f), 0f, 1f);
		return 0.28f * MathF.Pow(2.00f / 0.28f, t);
	}

	/// <summary>
	/// Ring and mote opacity scale across Charge Levels <c>1..5</c> (<c>0.58</c> delicate glow at
	/// Level 1 up to <c>1.00</c> full luminous density at Level 5).
	/// </summary>
	public static float ResolveChargeAlphaScale(float chargeLevel)
	{
		float t = Math.Clamp((chargeLevel - 1f) / (MaxChargeTiers - 1f), 0f, 1f);
		return 0.58f + (0.42f * t);
	}

	/// <summary>
	/// Dynamic ground <see cref="OmniLight3D"/> base energy denoting spell strength across Charge
	/// Levels <c>1..5</c> using a perceptually uniform exponential step (~1.91x per tier,
	/// <c>0.45</c> at Level 1 up to <c>6.00</c> at Level 5).
	/// </summary>
	public static float ResolveChargeLightEnergy(float chargeLevel)
	{
		float t = Math.Clamp((chargeLevel - 1f) / (MaxChargeTiers - 1f), 0f, 1f);
		return 0.45f * MathF.Pow(6.00f / 0.45f, t);
	}

	/// <summary>
	/// Resolves the rendered circle radius from a single spell-agnostic host curve: expands
	/// monotonically across Charge Levels <c>1..5</c> (<c>2.2 m → 10.4 m</c>) when centered on the
	/// Hero, and smoothly transitions to a compact aimed reticle curve (<c>1.35 m → 2.50 m</c>)
	/// when aimed away from the Hero (<c>&gt; 1.75 m</c> horizontal offset).
	/// </summary>
	public static float ResolveTargetRadius(
		float smoothedChargeLevel,
		Vector3 circleCenter = default,
		Vector3? heroPosition = null)
	{
		float t = Math.Clamp((smoothedChargeLevel - 1f) / (MaxChargeTiers - 1f), 0f, 1f);
		float surroundRadius = 2.2f + (8.2f * MathF.Pow(t, 1.08f));
		if (heroPosition is not { } hero || !hero.IsFinite() || !circleCenter.IsFinite())
		{
			return surroundRadius;
		}

		float dx = circleCenter.X - hero.X;
		float dz = circleCenter.Z - hero.Z;
		float aimDist = MathF.Sqrt((dx * dx) + (dz * dz));
		if (aimDist <= 1.75f)
		{
			return surroundRadius;
		}

		float aimWeight = Math.Clamp((aimDist - 1.75f) / 1.50f, 0f, 1f);
		aimWeight = aimWeight * aimWeight * (3f - (2f * aimWeight));
		float aimedRadius = 1.35f + (1.15f * t);
		return surroundRadius + ((aimedRadius - surroundRadius) * aimWeight);
	}

	/// <summary>
	/// Resolves the exact ground/floor Y height at world <c>(wx, wz)</c>, delegating to the shared
	/// DRY <see cref="Engine_FxGroundConform.ResolveSurfaceY"/> helper.
	/// </summary>
	public static float ResolveSurfaceY(
		float wx,
		float wz,
		float fallbackY,
		float centerY,
		float centerGroundY,
		bool hasCenterGround,
		Engine_FxGroundConform.GroundHeightSampler? sampleGround) =>
		Engine_FxGroundConform.ResolveSurfaceY(
			wx, wz, fallbackY, centerY, centerGroundY, hasCenterGround, sampleGround);

	/// <summary>
	/// Evaluates whether the remaster magic circle enhancement is enabled for the given config and
	/// renderer state. Defaults to <c>materials_modernized</c> when <c>fx_remaster_magic_circle</c>
	/// is not explicitly set, and is strictly off whenever Classic materials are active.
	/// </summary>
	public static bool ResolveEnabled(
		IReadOnlyDictionary<string, string>? config,
		bool classicMaterialActive,
		string? envOverride = null)
	{
		if (classicMaterialActive)
		{
			return false;
		}

		string forced = envOverride ?? string.Empty;
		if (forced == "1" || forced == "0")
		{
			return forced == "1";
		}

		if (config is null)
		{
			return Engine_TexSettings.Modernized;
		}

		bool modernizedDefault = Refii.Core.Engine_RefiiConfig.Bool(config, Engine_TexSettings.ModernizedKey, true);
		return Refii.Core.Engine_RefiiConfig.Bool(config, SettingKey, modernizedDefault);
	}

	public static void Configure(IReadOnlyDictionary<string, string> config)
	{
		string forced = OS.GetEnvironment(EnvironmentVariable);
		_enabled = ResolveEnabled(config, Engine_ClassicMaterial.Active, forced);
		_configured = true;
		if (!_enabled && _rigs.Count > 0)
		{
			ClearAll();
		}
	}

	public static bool Active => !Engine_ClassicMaterial.Active && (_configured ? _enabled : Engine_TexSettings.Modernized);

	/// <summary>
	/// Pure geometric solver: extracts the live world center, horizontal radius, and 96-bin terrain
	/// height profile around the circumference of a closed <c>Type 9</c> magic-circle spline.
	/// Accepts both small aimed target rings (<c>0.12 m+</c>) and wide Level 5 circles (<c>45 m</c>).
	/// </summary>
	public static bool TryExtractProfile(ReadOnlySpan<Vector3> worldPoints, CircleProfile into)
	{
		int count = worldPoints.Length;
		if (count < 8)
		{
			return false;
		}

		if (worldPoints[0].DistanceSquaredTo(worldPoints[count - 1]) < 1e-3f)
		{
			count--;
		}
		if (count < 8)
		{
			return false;
		}

		double sumX = 0.0, sumY = 0.0, sumZ = 0.0;
		int finiteCount = 0;
		for (int i = 0; i < count; i++)
		{
			Vector3 p = worldPoints[i];
			if (!p.IsFinite())
			{
				continue;
			}
			sumX += p.X;
			sumY += p.Y;
			sumZ += p.Z;
			finiteCount++;
		}

		if (finiteCount < 8)
		{
			return false;
		}

		float invN = (float)(1.0 / finiteCount);
		float cx = (float)(sumX * invN);
		float cy = (float)(sumY * invN);
		float cz = (float)(sumZ * invN);

		Span<float> binSumY = stackalloc float[AngularBins];
		Span<int> binHits = stackalloc int[AngularBins];
		binSumY.Clear();
		binHits.Clear();

		float sumRadius = 0f;
		int radiusHits = 0;
		const float InvTwoPi = AngularBins / MathF.Tau;

		for (int i = 0; i < count; i++)
		{
			Vector3 p = worldPoints[i];
			if (!p.IsFinite())
			{
				continue;
			}
			float dx = p.X - cx;
			float dz = p.Z - cz;
			float r = MathF.Sqrt((dx * dx) + (dz * dz));
			if (r < 0.02f || r > 60f)
			{
				continue;
			}

			sumRadius += r;
			radiusHits++;

			float angle = MathF.Atan2(dz, dx);
			if (angle < 0f) angle += MathF.Tau;
			int bin = (int)(angle * InvTwoPi) % AngularBins;
			if (bin < 0) bin += AngularBins;
			binSumY[bin] += p.Y;
			binHits[bin]++;
		}

		if (radiusHits < 8)
		{
			return false;
		}

		float meanRadius = sumRadius / radiusHits;
		if (!float.IsFinite(meanRadius) || meanRadius < 0.12f || meanRadius > 45.0f)
		{
			return false;
		}

		Span<float> rawY = stackalloc float[AngularBins];
		for (int b = 0; b < AngularBins; b++)
		{
			if (binHits[b] > 0)
			{
				rawY[b] = binSumY[b] / binHits[b];
			}
			else
			{
				float foundY = cy;
				for (int offset = 1; offset < AngularBins / 2; offset++)
				{
					int prev = (b - offset + AngularBins) % AngularBins;
					int next = (b + offset) % AngularBins;
					if (binHits[prev] > 0 && binHits[next] > 0)
					{
						foundY = 0.5f * ((binSumY[prev] / binHits[prev]) + (binSumY[next] / binHits[next]));
						break;
					}
					if (binHits[prev] > 0)
					{
						foundY = binSumY[prev] / binHits[prev];
						break;
					}
					if (binHits[next] > 0)
					{
						foundY = binSumY[next] / binHits[next];
						break;
					}
				}
				rawY[b] = foundY;
			}
		}

		float smoothedCenterY = 0f;
		for (int b = 0; b < AngularBins; b++)
		{
			int prev = (b - 1 + AngularBins) % AngularBins;
			int next = (b + 1) % AngularBins;
			float y = (0.25f * rawY[prev]) + (0.50f * rawY[b]) + (0.25f * rawY[next]);
			into.PerimeterY[b] = y;
			smoothedCenterY += y;
		}

		smoothedCenterY /= AngularBins;
		into.CenterY = smoothedCenterY;
		into.Center = new Vector3(cx, smoothedCenterY, cz);
		into.Radius = meanRadius;
		return true;
	}

	/// <summary>
	/// Pure terrain-conforming vertex generator: builds the 3 concentric harmonic Will rings plus
	/// 1 outward pulsing wave (4 bands total) hugging the landscape heightfield and bridges cleanly,
	/// while filling <paramref name="perimeterLocalY"/> with exact local ground heights for motes.
	/// </summary>
	public static int BuildRingVertices(
		CircleProfile profile,
		Vector3 center,
		float radius,
		float timeSeconds,
		Engine_FxGroundConform.GroundHeightSampler? sampleGround,
		Span<Vector3> positions,
		Span<Vector2> uvs,
		Span<Color> colors,
		Span<float> perimeterLocalY)
	{
		if (positions.Length < RingVertexCount || uvs.Length < RingVertexCount || colors.Length < RingVertexCount)
		{
			return 0;
		}

		Span<float> cosA = stackalloc float[AngularBins];
		Span<float> sinA = stackalloc float[AngularBins];

		float centerGroundY = profile.CenterY;
		bool hasCenterGround = false;
		if (sampleGround is not null &&
			sampleGround(new Vector3(center.X, profile.CenterY, center.Z), out float sampledCenterY) &&
			float.IsFinite(sampledCenterY))
		{
			hasCenterGround = true;
			centerGroundY = sampledCenterY;
		}

		for (int s = 0; s < AngularBins; s++)
		{
			float a = (float)s / AngularBins * MathF.Tau;
			cosA[s] = MathF.Cos(a);
			sinA[s] = MathF.Sin(a);
		}

		// Populate exact local ground heights around the outer perimeter for rising motes.
		if (perimeterLocalY.Length >= AngularBins)
		{
			for (int s = 0; s < AngularBins; s++)
			{
				float wx = center.X + (cosA[s] * radius);
				float wz = center.Z + (sinA[s] * radius);
				float surfaceY = ResolveSurfaceY(
					wx, wz, profile.CenterY, profile.CenterY,
					centerGroundY, hasCenterGround, sampleGround);
				perimeterLocalY[s] = surfaceY - center.Y;
			}
		}

		float pulsePhase = timeSeconds / 1.85f;
		pulsePhase -= MathF.Floor(pulsePhase);
		float pulseFrac = 0.14f + (0.84f * MathF.Sqrt(pulsePhase));
		float pulseEnv = MathF.Sin(pulsePhase * MathF.PI);

		ReadOnlySpan<(float R0, float R1, float UvRepeat, float SpinTurns, Color Attr)> bands = stackalloc (float, float, float, float, Color)[]
		{
			// 0. Outer luminous ground halo skirt & perimeter ring
			(0.82f, 1.12f, 2.0f, 0.08f, new Color(0.35f, 0.0f, 0.72f, 0.56f)),
			// 1. Counter-rotating inner harmonic Will filigree ring
			(0.60f, 0.76f, 3.0f, -0.36f, new Color(0.95f, 0.5f, 1.18f, 0.86f)),
			// 2. Inner Will sanctum sigil ring
			(0.29f, 0.41f, 2.0f, 0.22f, new Color(0.75f, 1.0f, 1.00f, 0.74f)),
			// 3. Expanding harmonic Will pulse wave flowing outward
			(MathF.Max(0.04f, pulseFrac - 0.075f), pulseFrac + 0.075f, 2.0f, -0.14f, new Color(0.55f, 0.25f, 1.10f, 0.65f * pulseEnv)),
		};

		Span<float> localY0 = stackalloc float[AngularBins];
		Span<float> localY1 = stackalloc float[AngularBins];

		int write = 0;
		for (int b = 0; b < bands.Length; b++)
		{
			(float frac0, float frac1, float uvRep, float spinTurns, Color attr) = bands[b];
			float r0 = radius * frac0;
			float r1 = radius * frac1;
			float uSpin = timeSeconds * spinTurns;

			for (int s = 0; s < AngularBins; s++)
			{
				float cs = cosA[s], sn = sinA[s];

				float wx0 = center.X + (cs * r0);
				float wz0 = center.Z + (sn * r0);
				float surfY0 = ResolveSurfaceY(
					wx0, wz0, profile.CenterY, profile.CenterY,
					centerGroundY, hasCenterGround, sampleGround);
				localY0[s] = (surfY0 - center.Y) + LiftMeters;

				float wx1 = center.X + (cs * r1);
				float wz1 = center.Z + (sn * r1);
				float surfY1 = ResolveSurfaceY(
					wx1, wz1, profile.CenterY, profile.CenterY,
					centerGroundY, hasCenterGround, sampleGround);
				localY1[s] = (surfY1 - center.Y) + LiftMeters;
			}

			for (int s = 0; s < AngularBins; s++)
			{
				int sNext = (s + 1) % AngularBins;
				float u0 = ((float)s / AngularBins * uvRep) + uSpin;
				float u1 = ((float)(s + 1) / AngularBins * uvRep) + uSpin;

				float c0 = cosA[s], sn0 = sinA[s];
				float c1 = cosA[sNext], sn1 = sinA[sNext];

				Vector3 p00 = new(c0 * r0, localY0[s], sn0 * r0);
				Vector3 p01 = new(c0 * r1, localY1[s], sn0 * r1);
				Vector3 p10 = new(c1 * r0, localY0[sNext], sn1 * r0);
				Vector3 p11 = new(c1 * r1, localY1[sNext], sn1 * r1);

				positions[write + 0] = p00; uvs[write + 0] = new Vector2(u0, 0f); colors[write + 0] = attr;
				positions[write + 1] = p10; uvs[write + 1] = new Vector2(u1, 0f); colors[write + 1] = attr;
				positions[write + 2] = p11; uvs[write + 2] = new Vector2(u1, 1f); colors[write + 2] = attr;

				positions[write + 3] = p00; uvs[write + 3] = new Vector2(u0, 0f); colors[write + 3] = attr;
				positions[write + 4] = p11; uvs[write + 4] = new Vector2(u1, 1f); colors[write + 4] = attr;
				positions[write + 5] = p01; uvs[write + 5] = new Vector2(u0, 1f); colors[write + 5] = attr;
				write += 6;
			}
		}

		return write;
	}

	/// <summary>Marks the start of a refreshed <c>GameFx</c> walk so absent magic circles are cleaned up.</summary>
	public static void BeginWalk()
	{
		foreach (CircleRig rig in _rigs.Values)
		{
			rig.SeenThisWalk = false;
		}
	}

	/// <summary>
	/// Updates or creates the remaster Will magic circle rig for a live <c>FX_DAD44F56</c> handle
	/// from its stated world-space <c>Type 9</c> spline points, current charge tier (<c>1..5</c>),
	/// and assigned Fable II spell IDs per charge slot (<c>0..7</c>).
	/// </summary>
	public static void Observe(
		Node3D parent,
		uint rootKey,
		uint effectHash,
		ReadOnlySpan<Vector3> worldBeamSpline,
		float intensity,
		float lightFade,
		Color statedColour,
		Color recipeTint,
		int activeChargeLevel = 1,
		ReadOnlySpan<int> slotSpells = default,
		Vector3? heroPosition = null)
	{
		if (effectHash != MagicCircleEffectHash || !Active)
		{
			return;
		}

		if (worldBeamSpline.Length < 8 || !GodotObject.IsInstanceValid(parent))
		{
			return;
		}

		if (!_rigs.TryGetValue(rootKey, out CircleRig? rig) || !GodotObject.IsInstanceValid(rig.Root))
		{
			if (rig is not null)
			{
				DestroyRig(rig);
				_rigs.Remove(rootKey);
			}

			rig = CreateRig(parent, rootKey);
			if (rig is null)
			{
				return;
			}
			_rigs[rootKey] = rig;
		}

		if (!TryExtractProfile(worldBeamSpline, rig.Profile))
		{
			return;
		}

		rig.SeenThisWalk = true;
		rig.HeroPosition = heroPosition;
		rig.LiveAlpha = NormalizeCircleStrength(intensity, lightFade, statedColour.A);
		rig.Tint = new Color(
			MathF.Max(statedColour.R * recipeTint.R, 0f),
			MathF.Max(statedColour.G * recipeTint.G, 0f),
			MathF.Max(statedColour.B * recipeTint.B, 0f),
			1f);

		rig.ActiveChargeLevel = Math.Clamp(activeChargeLevel, 1, MaxChargeTiers);
		for (int i = 0; i < MaxChargeTiers; i++)
		{
			rig.SlotSpells[i] = i < slotSpells.Length ? slotSpells[i] : -1;
		}

		if (!rig.HasSmoothedState || rig.SmoothedCenter.DistanceSquaredTo(rig.Profile.Center) > 16f)
		{
			rig.SmoothedCenter = rig.Profile.Center;
			rig.SmoothedChargeLevel = rig.ActiveChargeLevel;
			rig.SmoothedRadius = ResolveTargetRadius(rig.ActiveChargeLevel, rig.Profile.Center, rig.HeroPosition);
			rig.HasSmoothedState = true;
		}
	}

	/// <summary>Retires any remaster magic circle rigs whose <c>FX_DAD44F56</c> handle was not seen.</summary>
	public static void EndWalk()
	{
		if (_rigs.Count == 0)
		{
			return;
		}

		_deadKeys.Clear();
		foreach ((uint key, CircleRig rig) in _rigs)
		{
			if (!rig.SeenThisWalk || !GodotObject.IsInstanceValid(rig.Root))
			{
				_deadKeys.Add(key);
			}
		}

		for (int i = 0; i < _deadKeys.Count; i++)
		{
			Remove(_deadKeys[i]);
		}
	}

	/// <summary>Advances smooth motion, unified spell-colour transitions, charge-scaled light, and terrain-hugging motes each display frame.</summary>
	public static void Step(double delta)
	{
		if (_rigs.Count == 0)
		{
			return;
		}

		if (!Active)
		{
			ClearAll();
			return;
		}

		float dt = (float)Math.Clamp(delta, 0.0, 0.1);
		float blend = 1f - MathF.Exp(-22f * dt);
		float colorBlend = 1f - MathF.Exp(-8.5f * dt);
		float levelBlend = 1f - MathF.Exp(-6.0f * dt);

		Engine_FxGroundConform.GroundHeightSampler? groundSampler = Engine_FxGroundConform.TryGetLiveGroundSampler();

		foreach (CircleRig rig in _rigs.Values)
		{
			if (!rig.HasSmoothedState || !GodotObject.IsInstanceValid(rig.Root))
			{
				continue;
			}

			rig.TimeSeconds += dt;
			rig.SmoothedCenter = rig.SmoothedCenter.Lerp(rig.Profile.Center, blend);
			rig.SmoothedChargeLevel += (rig.ActiveChargeLevel - rig.SmoothedChargeLevel) * levelBlend;

			int activeSlot = Math.Clamp(rig.ActiveChargeLevel - 1, 0, MaxChargeTiers - 1);
			int activeSpellId = rig.SlotSpells[activeSlot];
			float targetRadius = ResolveTargetRadius(rig.SmoothedChargeLevel, rig.SmoothedCenter, rig.HeroPosition);
			rig.SmoothedRadius += (targetRadius - rig.SmoothedRadius) * blend;

			if (!rig.Root.Position.IsEqualApprox(rig.SmoothedCenter))
			{
				rig.Root.Position = rig.SmoothedCenter;
			}

			float alpha = Math.Clamp(rig.LiveAlpha, 0f, 1.25f);
			bool visible = alpha > 0.004f;
			if (rig.RingMeshNode.Visible != visible)
			{
				rig.RingMeshNode.Visible = visible;
			}
			if (rig.Motes.Emitting != visible)
			{
				rig.Motes.Emitting = visible;
			}
			if (!visible)
			{
				rig.HasSmoothedColor = false;
				if (rig.LastLightVisible)
				{
					Refii.AndroidRuntime.AndroidFxLightLimit.SetShown(rig.Light, false);
					rig.LastLightVisible = false;
				}
				continue;
			}

			BuildRingVertices(
				rig.Profile,
				rig.SmoothedCenter,
				rig.SmoothedRadius,
				rig.TimeSeconds,
				groundSampler,
				rig.Positions,
				rig.Uvs,
				rig.Colors,
				rig.PerimeterLocalY);

			Godot.Collections.Array arrays = rig.SurfaceArrays ??= MakeSurfaceArrays();
			Variant vPos = rig.Positions, vUv = rig.Uvs, vCol = rig.Colors;
			arrays[(int)Mesh.ArrayType.Vertex] = vPos;
			arrays[(int)Mesh.ArrayType.TexUV] = vUv;
			arrays[(int)Mesh.ArrayType.Color] = vCol;
			vPos.Dispose();
			vUv.Dispose();
			vCol.Dispose();
			rig.RingMesh.ClearSurfaces();
			Refii.Core.Engine_ArrayMeshes.AddSurface(rig.RingMesh, Mesh.PrimitiveType.Triangles, arrays);
			if (rig.RingMesh.GetSurfaceCount() > 0)
			{
				rig.RingMesh.SurfaceSetMaterial(0, rig.RingMaterial);
			}

			// Upload exact terrain/floor heights around the perimeter to the mote process shader.
			for (int s = 0; s < AngularBins; s++)
			{
				rig.PerimeterHeightImage.SetPixel(s, 0, new Color(rig.PerimeterLocalY[s], 0f, 0f, 1f));
			}
			rig.PerimeterHeightTex.Update(rig.PerimeterHeightImage);

			// Smoothly transition all rings, motes, and ground light to the current charge tier's spell colour.
			SpellPalette targetPal = ResolveSpellPalette(activeSpellId);
			if (!rig.HasSmoothedColor)
			{
				rig.SmoothedCore = targetPal.Core;
				rig.SmoothedAccent = targetPal.Accent;
				rig.SmoothedLight = targetPal.Light;
				rig.SmoothedLuminanceScale = targetPal.LuminanceScale;
				rig.HasSmoothedColor = true;
			}
			else
			{
				rig.SmoothedCore = rig.SmoothedCore.Lerp(targetPal.Core, colorBlend);
				rig.SmoothedAccent = rig.SmoothedAccent.Lerp(targetPal.Accent, colorBlend);
				rig.SmoothedLight = rig.SmoothedLight.Lerp(targetPal.Light, colorBlend);
				rig.SmoothedLuminanceScale += (targetPal.LuminanceScale - rig.SmoothedLuminanceScale) * colorBlend;
			}

			float chargeRadScale = ResolveChargeRadianceScale(rig.SmoothedChargeLevel) * rig.SmoothedLuminanceScale;
			Vector3 coreRad = new(
				MathF.Max(rig.SmoothedCore.R * 2.85f * chargeRadScale * rig.Tint.R, 0f),
				MathF.Max(rig.SmoothedCore.G * 2.85f * chargeRadScale * rig.Tint.G, 0f),
				MathF.Max(rig.SmoothedCore.B * 2.85f * chargeRadScale * rig.Tint.B, 0f));
			Vector3 accentRad = new(
				MathF.Max(rig.SmoothedAccent.R * 3.85f * chargeRadScale * rig.Tint.R, 0f),
				MathF.Max(rig.SmoothedAccent.G * 3.85f * chargeRadScale * rig.Tint.G, 0f),
				MathF.Max(rig.SmoothedAccent.B * 3.85f * chargeRadScale * rig.Tint.B, 0f));
			Vector3 moteRad = new(
				MathF.Max(rig.SmoothedAccent.R * 3.40f * chargeRadScale * rig.Tint.R, 0f),
				MathF.Max(rig.SmoothedAccent.G * 3.40f * chargeRadScale * rig.Tint.G, 0f),
				MathF.Max(rig.SmoothedAccent.B * 3.40f * chargeRadScale * rig.Tint.B, 0f));

			if (rig.LastCoreRad.DistanceSquaredTo(coreRad) > 1e-4f || rig.LastAccentRad.DistanceSquaredTo(accentRad) > 1e-4f)
			{
				rig.RingMaterial.SetShaderParameter("will_core_radiance", coreRad);
				rig.RingMaterial.SetShaderParameter("will_accent_radiance", accentRad);
				rig.LastCoreRad = coreRad;
				rig.LastAccentRad = accentRad;
			}
			if (rig.LastMoteRadiance.DistanceSquaredTo(moteRad) > 1e-4f)
			{
				rig.MoteRenderMaterial.SetShaderParameter("mote_radiance", moteRad);
				rig.LastMoteRadiance = moteRad;
			}
			float chargeAlpha = alpha * ResolveChargeAlphaScale(rig.SmoothedChargeLevel);
			if (MathF.Abs(rig.LastShaderAlpha - chargeAlpha) > 1e-3f)
			{
				rig.RingMaterial.SetShaderParameter("circle_alpha", chargeAlpha);
				rig.LastShaderAlpha = chargeAlpha;
			}
			if (MathF.Abs(rig.LastShaderTime - rig.TimeSeconds) > 1e-3f)
			{
				rig.RingMaterial.SetShaderParameter("rune_time", rig.TimeSeconds);
				rig.LastShaderTime = rig.TimeSeconds;
			}

			float climbHeight = Math.Clamp(0.60f + (0.14f * rig.SmoothedRadius), 0.70f, 1.75f);
			float spinRate = 1.05f + (0.25f * (rig.SmoothedChargeLevel - 1f));
			if (MathF.Abs(rig.LastMoteRadius - rig.SmoothedRadius) > 1e-2f)
			{
				rig.MoteProcessMaterial.SetShaderParameter("circle_radius", rig.SmoothedRadius);
				rig.MoteProcessMaterial.SetShaderParameter("mote_size", Math.Clamp(0.10f + (0.010f * rig.SmoothedRadius), 0.10f, 0.20f));
				float ext = rig.SmoothedRadius + 4.0f;
				rig.Motes.VisibilityAabb = new Aabb(new Vector3(-ext, -6.0f, -ext), new Vector3(ext * 2f, climbHeight + 12.0f, ext * 2f));
				rig.LastMoteRadius = rig.SmoothedRadius;
			}
			if (MathF.Abs(rig.LastMoteHeight - climbHeight) > 1e-2f)
			{
				rig.MoteProcessMaterial.SetShaderParameter("climb_height", climbHeight);
				rig.LastMoteHeight = climbHeight;
			}
			if (MathF.Abs(rig.LastMoteSpin - spinRate) > 1e-2f)
			{
				rig.MoteProcessMaterial.SetShaderParameter("spin_rate", spinRate);
				rig.LastMoteSpin = spinRate;
			}
			float moteAlpha = Math.Clamp(chargeAlpha, 0f, 1f);
			if (MathF.Abs(rig.LastMoteAlpha - moteAlpha) > 1e-3f)
			{
				rig.MoteProcessMaterial.SetShaderParameter("mote_alpha", moteAlpha);
				rig.LastMoteAlpha = moteAlpha;
			}

			// Dynamic Will ground light: colour follows the active spell, energy/range denote spell strength.
			bool showLight = Refii.Lighting.Engine_LightingFrame.ModernModel;
			if (rig.LastLightVisible != showLight)
			{
				Refii.AndroidRuntime.AndroidFxLightLimit.SetShown(rig.Light, showLight);
				rig.LastLightVisible = showLight;
			}

			if (showLight)
			{
				Vector3 lightLocalPos = new(0f, 0.65f + (0.09f * rig.SmoothedRadius), 0f);
				float omniRange = Math.Clamp(4.0f + (rig.SmoothedRadius * 1.40f) + ((rig.SmoothedChargeLevel - 1f) * 0.85f), 4.5f, 22.0f)
					* Refii.Lighting.Engine_LevelLights.PlacedRange;
				float shimmer = 1.0f + (0.09f * MathF.Sin(rig.TimeSeconds * 4.2f));
				float energy = alpha * ResolveChargeLightEnergy(rig.SmoothedChargeLevel) * shimmer * Refii.Lighting.Engine_LevelLights.PlacedScale;
				Color lightCol = new(
					Math.Clamp(rig.SmoothedLight.R * rig.Tint.R, 0f, 2f),
					Math.Clamp(rig.SmoothedLight.G * rig.Tint.G, 0f, 2f),
					Math.Clamp(rig.SmoothedLight.B * rig.Tint.B, 0f, 2f),
					1f);

				if (!rig.LastLightPos.IsFinite() || rig.LastLightPos.DistanceSquaredTo(lightLocalPos) > 1e-4f)
				{
					rig.Light.Position = lightLocalPos;
					rig.LastLightPos = lightLocalPos;
				}
				if (MathF.Abs(rig.LastLightRange - omniRange) > 2e-2f)
				{
					rig.Light.OmniRange = omniRange;
					rig.LastLightRange = omniRange;
				}
				if (MathF.Abs(rig.LastLightColor.R - lightCol.R) > 1e-3f ||
					MathF.Abs(rig.LastLightColor.G - lightCol.G) > 1e-3f ||
					MathF.Abs(rig.LastLightColor.B - lightCol.B) > 1e-3f)
				{
					Refii.Lighting.Engine_LightColour.Apply(rig.Light, lightCol);
					rig.LastLightColor = lightCol;
				}
				if (MathF.Abs(rig.LastLightEnergy - energy) > 2e-3f)
				{
					Refii.AndroidRuntime.AndroidFxLightLimit.SetEnergy(rig.Light, energy);
					rig.LastLightEnergy = energy;
				}
			}
		}
	}

	/// <summary>Immediately frees any remaster magic circle rig associated with <paramref name="rootKey"/>.</summary>
	public static void Remove(uint rootKey)
	{
		if (_rigs.Remove(rootKey, out CircleRig? rig))
		{
			DestroyRig(rig);
		}
	}

	/// <summary>Frees all active remaster magic circle rigs (on level unload or Classic toggle).</summary>
	public static void ClearAll()
	{
		foreach (CircleRig rig in _rigs.Values)
		{
			DestroyRig(rig);
		}
		_rigs.Clear();
	}

	private static CircleRig? CreateRig(Node3D parent, uint rootKey)
	{
		_ringShader ??= GD.Load<Shader>(RingShaderPath);
		if (_ringShader is null)
		{
			return null;
		}

		_moteProcessShader ??= new Shader { Code = MoteProcessShaderCode };
		_moteRenderShader ??= new Shader { Code = MoteRenderShaderCode };
		_moteQuad ??= new QuadMesh { Size = Vector2.One };

		Texture2D? trailTex = Engine_FxRecipePieces.LoadTexture("fxt_spell_circle_trail");
		Texture2D? twistTex = Engine_FxRecipePieces.LoadTexture("fxt_ringtwist_01") ?? trailTex;
		Texture2D? moteTex = Engine_FxRecipePieces.LoadTexture("fxt_breadcrumb_sparkle")
			?? Engine_FxRecipePieces.LoadTexture("fxt_wisp_sparkle");

		var ringMat = new ShaderMaterial { Shader = _ringShader };
		if (trailTex is not null)
		{
			ringMat.SetShaderParameter("fx_tex", trailTex);
			ringMat.SetShaderParameter("has_fx_tex", true);
		}
		if (twistTex is not null)
		{
			ringMat.SetShaderParameter("fx_filigree_tex", twistTex);
			ringMat.SetShaderParameter("has_filigree_tex", true);
		}
		ringMat.SetShaderParameter("fx_soft_inv", 0.0f);

		Image perimImg = Image.CreateEmpty(AngularBins, 1, false, Image.Format.Rf);
		ImageTexture perimTex = ImageTexture.CreateFromImage(perimImg);

		var moteProc = new ShaderMaterial { Shader = _moteProcessShader };
		moteProc.SetShaderParameter("perimeter_height_tex", perimTex);

		var moteRender = new ShaderMaterial { Shader = _moteRenderShader };
		if (moteTex is not null)
		{
			moteRender.SetShaderParameter("mote_tex", moteTex);
			moteRender.SetShaderParameter("has_mote_tex", true);
		}

		var root = new Node3D { Name = $"RemasterMagicCircle_{rootKey:X8}" };
		var ringMesh = new ArrayMesh();
		var ringNode = new MeshInstance3D
		{
			Name = "WillRings",
			Mesh = ringMesh,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			ExtraCullMargin = 16.0f,
		};

		var motes = new GpuParticles3D
		{
			Name = "WillMotes",
			Amount = 48,
			Lifetime = 1.35,
			LocalCoords = true,
			ProcessMaterial = moteProc,
			DrawPass1 = _moteQuad,
			MaterialOverride = moteRender,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			ExtraCullMargin = 16.0f,
			Emitting = true,
		};

		var light = new OmniLight3D
		{
			Name = "WillGroundLight",
			ShadowEnabled = false,
			OmniAttenuation = 1.25f,
			DistanceFadeEnabled = true,
			DistanceFadeBegin = 60f,
			DistanceFadeLength = 25f,
			Visible = false,
		};
		Refii.AndroidRuntime.AndroidFxLightLimit.Track(light);

		root.AddChild(ringNode);
		root.AddChild(motes);
		root.AddChild(light);
		parent.AddChild(root);

		return new CircleRig
		{
			Root = root,
			RingMeshNode = ringNode,
			RingMesh = ringMesh,
			RingMaterial = ringMat,
			Motes = motes,
			MoteProcessMaterial = moteProc,
			MoteRenderMaterial = moteRender,
			PerimeterHeightImage = perimImg,
			PerimeterHeightTex = perimTex,
			Light = light,
		};
	}

	private static void DestroyRig(CircleRig rig)
	{
		if (GodotObject.IsInstanceValid(rig.Light))
		{
			Refii.AndroidRuntime.AndroidFxLightLimit.SetShown(rig.Light, false);
		}
		if (GodotObject.IsInstanceValid(rig.Root))
		{
			rig.Root.QueueFree();
		}
	}

	private static Godot.Collections.Array MakeSurfaceArrays()
	{
		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		return arrays;
	}
}
