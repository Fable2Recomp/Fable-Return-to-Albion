using System;
using Godot;
using Refii.Level;

namespace Refii.Lighting;

public static class Engine_LevelLighting
{
	public const string SunNodeName = "Sun";

	/// <summary>
	/// The sun cascades' reach. An occluder outside every cascade casts nothing, so too short a
	/// reach lets a low sun or moon light terrain straight through the hills beyond it.
	/// </summary>
	/// <remarks>`sun_shadow_distance`: how far the sun's cascades reach by day, in metres.</remarks>
	public static readonly float SunFarDepth = ReadSunFarDepth();

	private static float ReadSunFarDepth()
	{
		try
		{
			return Mathf.Clamp(Refii.Core.Engine_RefiiConfig.Float(
				Refii.Core.Engine_RefiiConfig.Read(out _), "sun_shadow_distance", 200f), 50f, 600f);
		}
		catch (System.Exception)
		{
			return 200f;
		}
	}

	public const float CascadeSplitNear = 35.0f;

	public const float CascadeSplitMid = 110.0f;

	public const float CascadeSplitFar = 240.0f;

	public const float NearSlopeBias = 2.0f;

	/// <summary>`sun_shadow_splits`: cascades by day, 2 or 4.</summary>
	public static readonly int SunSplits = ReadSunSplits();

	private static int ReadSunSplits()
	{
		try
		{
			return Refii.Core.Engine_RefiiConfig.Int(
				Refii.Core.Engine_RefiiConfig.Read(out _), "sun_shadow_splits", 2) >= 4 ? 4 : 2;
		}
		catch (System.Exception)
		{
			return 2;
		}
	}

	/// <summary>`sun_shadow_map`: the directional shadow atlas size; the biases scale with its texel.</summary>
	public static readonly int SunShadowMap = ReadSunShadowMap();

	private static int ReadSunShadowMap()
	{
		try
		{
			return Mathf.Clamp(Refii.Core.Engine_RefiiConfig.Int(
				Refii.Core.Engine_RefiiConfig.Read(out _), "sun_shadow_map", 4096), 512, 16384);
		}
		catch (System.Exception)
		{
			return 4096;
		}
	}

	/// <summary>A live lever: null follows the day set, true/false forces the sun's shadow.</summary>
	public static bool? SunShadowOverride;

	/// <summary>Live lever: 2 or 4 cascades regardless of the sun's energy; null follows it.</summary>
	public static int? SunSplitsOverride;

	/// <summary>Below this energy the light is the moon and gets the night cascades.</summary>
	public const float NightEnergy = 0.35f;

	/// <summary>Shadow depth for the night cascades.</summary>
	public const float NightFarDepth = 150.0f;

	public static DirectionalLight3D BuildSun(LightingFrame frame)
	{
		var light = new DirectionalLight3D
		{
			Name = SunNodeName,
			DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
			DirectionalShadowMaxDistance = SunFarDepth,
			DirectionalShadowSplit1 = CascadeSplitNear / SunFarDepth,
			DirectionalShadowSplit2 = CascadeSplitMid / SunFarDepth,
			DirectionalShadowSplit3 = Mathf.Min(CascadeSplitFar, SunFarDepth * 0.8f) / SunFarDepth,

			// Cascade boundaries otherwise print as a hard band across the landscape where
			// the shadow resolution steps — the "seam that does not match up" tracking the
			// camera's distance rather than any geometry. The blend dithers across it.
			DirectionalShadowBlendSplits = true,

			// NO CASCADES IN A REFLECTION: the reflected bit never selects a caster, so a mirror
			// camera, which sees only that bit, finds no casters (Engine_ReflectionLayer).
			ShadowCasterMask = Refii.Level.Engine_ReflectionLayer.CasterMask(~0u),

			// The normal bias is already in texels (Godot scales it by the shadow texel), so it
			// holds for any map or reach. The constant bias is depth in world units: it only
			// grows, for a map under 4096; scaling it down stripes sunlit ground at a grazing sun.
			ShadowNormalBias = NearSlopeBias,
			ShadowBias = 0.03f * Mathf.Max(1f, 4096f / SunShadowMap),
		};

		UpdateSun(light, frame);
		return light;
	}

	private static float? _sunScale;

	/// <summary>
	/// The remaster's `sun_light_scale`. The shipped balance keeps the direct sun mild against
	/// the bakes — people and ground barely react to it — and the user asked for the opposite.
	/// 1.0 is the shipped energy exactly.
	/// </summary>
	private static float SunScale
	{
		get
		{
			if (_sunScale is null)
			{
				try
				{
					_sunScale = Math.Clamp(
						Refii.Core.Engine_RefiiConfig.Float(
							Refii.Core.Engine_RefiiConfig.Read(out _), "sun_light_scale", 1.35f),
						0.0f, 4.0f);
				}
				catch (Exception)
				{
					_sunScale = 1.35f;
				}
			}

			return _sunScale.Value;
		}
	}

	/// <summary>
	/// Whether Godot's sun casts: the debug override, else the frame's. Never under classic: every
	/// classic material is unshaded, and its sun shadow is the title's own cascades
	/// (Engine_ClassicShadows), so Godot's maps would be drawn and read by nothing.
	/// </summary>
	public static bool SunCasts(bool frameShadow) =>
		SunShadowOverride ?? (frameShadow && !Refii.Models.Engine_ClassicMaterial.Active);

	public static void UpdateSun(DirectionalLight3D light, LightingFrame frame)
	{
		light.LightColor = new Color(frame.SunColour[0], frame.SunColour[1], frame.SunColour[2]);
		light.LightEnergy = frame.SunEnergy * SunScale;
		light.ShadowEnabled = SunCasts(frame.SunShadow);

		// CASCADES BY STRENGTH. The moon at energy 0.2 cast the same four cascades over the
		// whole market as the noon sun: 5,000 draw calls of shadow for a light nobody can see
		// the shadows of. Two splits over a shorter depth at night; the day keeps four.
		// Every cascade redraws every caster in its slice. `sun_shadow_splits` picks two
		// (the near split to 35 m at half the map, the rest to the reach) or four.
		int splits = SunSplitsOverride ?? (frame.SunEnergy < NightEnergy ? 2 : SunSplits);
		light.DirectionalShadowMode = splits <= 2
			? DirectionalLight3D.ShadowMode.Parallel2Splits
			: DirectionalLight3D.ShadowMode.Parallel4Splits;
		light.DirectionalShadowMaxDistance = splits <= 2 ? Mathf.Min(NightFarDepth, SunFarDepth) : SunFarDepth;

		// IN WORLD SPACE, because the node sits under the level root's Scale(-1, 1, 1). A local
		// transform inherits that mirror, and the sun's world aim came out with its X flipped —
		// the agreement diagnostic printed rigDot = -0.888 where -1.0 is correct, travel
		// negating Y and Z but not X — so the light and its shadows arrived from the wrong
		// east/west side of everything the frame stated. Assigning the GLOBAL transform
		// composes the parent chain away, mirror included; before the node enters the tree the
		// local assignment stands in (its parent applies the same mirror to the region, so the
		// first configured frame corrects it).
		var aimed = new Transform3D(SunBasis(frame.LightDirection), Vector3.Zero);
		Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.Light);
		if (light.IsInsideTree())
		{
			light.GlobalTransform = aimed;
		}
		else
		{
			light.Transform = aimed;
		}

		ReportSun(frame);
	}

	private static float _reportedEnergy = float.NaN;
	private static bool _reportedShadow;
	private static float _reportedAmbient = float.NaN;

	/// <summary>
	/// Prints the directional light and the ambient floor whenever either moves materially.
	/// Self-limiting rather than periodic: the interesting question is what the values ARE, and
	/// they only change as the day does. `direct` and `ambient` are the two halves of the guest's
	/// `albedo * (direct + bake)` — if a street reads black, this says which half is missing.
	/// </summary>
	private static void ReportSun(LightingFrame frame)
	{
		float ambient = frame.HasAmbient ? frame.AmbientEnergy : 0f;
		bool moved = float.IsNaN(_reportedEnergy)
			|| MathF.Abs(frame.SunEnergy - _reportedEnergy) > 0.05f
			|| MathF.Abs(ambient - _reportedAmbient) > 0.02f
			|| frame.SunShadow != _reportedShadow;
		if (!moved)
		{
			return;
		}

		_reportedEnergy = frame.SunEnergy;
		_reportedAmbient = ambient;
		_reportedShadow = frame.SunShadow;
		GD.Print(
			$"[lighting] tod={frame.TimeOfDay:0.00}h sun=({frame.SunColour[0]:0.000}, " +
			$"{frame.SunColour[1]:0.000}, {frame.SunColour[2]:0.000}) energy={frame.SunEnergy:0.000} " +
			$"shadow={frame.SunShadow} dir=({frame.LightDirection[0]:0.00}, " +
			$"{frame.LightDirection[1]:0.00}, {frame.LightDirection[2]:0.00})  " +
			$"ambient={(frame.HasAmbient ? $"({frame.AmbientColour[0]:0.000}, {frame.AmbientColour[1]:0.000}, {frame.AmbientColour[2]:0.000}) x{frame.AmbientEnergy:0.000}" : "NONE")}");
	}

	/// <summary>
	/// The luminance that tone mapping maps to white. A STAND-IN — see below.
	///
	/// The guest's curve is a 512-entry 1D LUT that <c>sub_8217E428</c> rebuilds on the CPU
	/// every frame over the input domain [0, 4] — the entries are stored as a ratio against
	/// <c>((i + 0.5) / 512) * 4.0</c> at 0x8217E72C, so the LUT holds a GAIN rather than an
	/// output colour, which is exactly how the tone-map pixel shader uses it:
	///
	///     r0.x   = tex1D(g_ToneMapSampler, luminance * g_RecipMaxLuminance.x).x
	///     colour = saturate(brightness * r0.x * colour)
	///
	/// and <c>g_RecipMaxLuminance</c> is the literal 0.25, so the domain really is [0, 4].
	///
	/// FOUR IS THE DOMAIN BOUND, NOT THE WHITE POINT. The white point is not authored anywhere
	/// — the GDB has no such field — it is derived per frame from the adapted luminance triple:
	///
	///     v3  = *(a1+0x41A0) - *(a1+0x4198)        adaptedMax - adaptedMin
	///     v18 = 1.0 / (v3 * v3)                    1/W^2, at 0x8217E530
	///
	/// Reproducing it therefore needs the triple, which comes from a CONVERTTOLUMINANCE resolve
	/// of the previous frame binned into a CPU histogram (<c>sub_822AA9A0</c>). We do not render
	/// the guest's frame, so there is no triple to read and this stays a constant.
	/// </summary>
	public const float TonemapWhite = 4.0f;

	/// <summary>
	/// A STATIC STAND-IN FOR THE GUEST'S AUTO-EXPOSURE, which Godot 4 cannot reproduce.
	///
	/// The guest's post-process is two stages and the curve is the second one. First
	/// <c>sub_822AA9A0</c> locks the previous frame's luminance surface, bins 1024 histogram
	/// buckets on the CPU and takes percentile thresholds; then exposure walks toward putting
	/// that average on MIDDLE_GRAY before the Reinhard curve sees anything. Shipping the curve
	/// without the exposure is worse than shipping neither — it is designed to receive a signal
	/// that has already been normalised, so everything lands on the toe and the region reads as
	/// dusk at two in the afternoon.
	///
	/// The value comes from two stated defaults rather than from taste. MIDDLE_GRAY is 0.45 and
	/// AVG_LUMINANCE_LOWER_BOUND is 0.15 (both from the theme's default constructor
	/// <c>sub_82A45DD0</c>), and the measured median luminance of this region at midday is
	/// 0.026 — well under that floor, so the guest's own clamp is what would apply.
	///
	/// THE FORM IS NOT QUITE THE GUEST'S, and it is worth being honest about which part is
	/// approximated. The key the curve actually multiplies by is a DIFFERENCE, not a ratio
	/// against the average alone (0x8217E524):
	///
	///     v15 = *(a1+0x2C) / (*(a1+0x419C) - *(a1+0x4198))    middleGray / (avg - min)
	///
	/// so the denominator is the adapted average above the adapted MINIMUM. Both of those are
	/// per-frame histogram outputs, and a static stand-in cannot honour a difference between
	/// two values it does not have — picking floors for them would be inventing the numerator
	/// and denominator both. The ratio below is kept because it produces a working image on the
	/// region it was measured against; it is not the guest's arithmetic.
	///
	/// Also unmodelled: the curve is not pure Reinhard. Each entry is lerped against a contrast
	/// term by <c>*(a1+0x40)</c>, where the contrast is <c>pow(x, *(a1+0x44))</c> with a
	/// squaring fast path when that exponent is 2.0 (0x8217E51C). Do not confuse that squaring
	/// with the colour-texture decode — they are unrelated.
	///
	/// Nothing reads this field. Live exposure is the guest's own loop (`sub_82191C18`), run per
	/// frame against a luminance histogram in `Engine_PostFxEffect` -> `fable_postfx_reduce.glsl`.
	/// Kept only to record the value the Godot Environment would want beside its derivation.
	/// </summary>
	public const float TonemapExposure = 1.0f;

	/// <summary>The middle grey the guest drives the adapted average toward.</summary>
	/// <remarks>
	/// The theme's MIDDLE_GRAY, default 0.45, and the numerator of the curve's key at
	/// 0x8217E524. Stated by the game rather than chosen.
	/// </remarks>
	public const float AutoExposureTargetGrey = 0.45f;

	/// <summary>
	/// How fast exposure follows the scene, as a rate per second.
	/// </summary>
	/// <remarks>
	/// Derived from the guest's own smoother rather than picked. It blends by
	/// <c>1 - (1 - ExposureSpeed)^(dt * 30)</c> with ExposureSpeed 0.01 (0x82276810), so over one
	/// second it closes <c>1 - 0.99^30 = 0.260</c> of the gap. An exponential approach with rate
	/// k closes <c>1 - e^-k</c>, so k = -ln(1 - 0.260) = 0.30.
	/// </remarks>
	public const float AutoExposureSpeed = 0.30f;

	/// <summary>
	/// The guest's auto-exposure, as close as Godot's own control loop gets to it.
	/// </summary>
	/// <remarks>
	/// Godot's own auto-exposure is off deliberately: the guest's loop runs instead, in
	/// `fable_postfx_reduce.glsl`, and two control loops would fight over the same frame.
	/// That loop follows `sub_82191C18`:
	///
	///   - curve key `middleGray / (adaptedAvg - adaptedMin)`, computed as
	///     `knee / max(SmMid - SmBlack, 1e-4)`;
	///   - exposure scalar clamped to [1, 16], twice, in the guest's units;
	///   - all 512 curve entries smoothed, not just the scalar.
	/// </remarks>
	public static CameraAttributesPractical BuildCameraAttributes() => new()
	{
		AutoExposureEnabled = false,
	};

	/// <summary>
	/// The compositor that runs the guest's tone curve, or null where compute is unavailable.
	/// </summary>
	/// <remarks>
	/// One per camera. The effect owns GPU resources keyed to a rendering device, so it is built
	/// here rather than shared as a singleton — a level change disposes the camera and the effect
	/// frees its pipelines with it.
	/// </remarks>
	public static Compositor? BuildCompositor()
	{
		if (RenderingServer.GetRenderingDevice() is null)
		{
			GD.Print("[lighting] no rendering device; tone curve disabled for this renderer");
			return null;
		}

		var effects = new Godot.Collections.Array<CompositorEffect>
		{
			new Refii.Fx.Engine_FxDepthCaptureEffect(),
		};
		if (Refii.Models.Engine_ClassicMaterial.Active)
		{
			// The classic look's screen-space sun shadow mask, between the depth prepass and the
			// opaque colour pass, as the title draws it.
			effects.Add(new Refii.Models.Engine_ClassicScreenShadow());
		}

		if (Engine_PostFxParams.ChainRequested)
		{
			effects.Add(new Engine_PostFxEffect());
		}
		else
		{
			GD.Print($"[lighting] {Engine_PostFxParams.ModeEnvironment}=off; the post-process chain is not attached");
		}

		return new Compositor { CompositorEffects = effects };
	}

	/// <summary>
	/// Godot's dynamic GI, on for the modern model. Static geometry is voxelised as it streams in —
	/// every mesh and MultiMesh at the default static GI mode (renderer_scene_cull.cpp takes any
	/// geometry flagged USES_BAKED_LIGHT) — and guest-driven entities receive it as dynamic objects.
	/// Placed lights enter it at <see cref="Light3D.BakeMode.Static"/>: baked into a cascade when
	/// that cascade refreshes rather than re-injected every update, which is what 124 fixtures in a
	/// town need, and it keeps a torch's flicker out of the bounce.
	/// </summary>
	private static bool? _giHalfResolution;

	private static bool _giHalfApplied;

	private static bool _giHalfEverApplied;

	/// <summary>
	/// `gi_half_resolution`, ON by default: the per-pixel gather of the GI runs at half the frame's
	/// resolution and is upsampled against depth and normals. The cascades, the lights in them and
	/// the bounce are untouched — only how finely the result is resolved on screen. Measured
	/// 2026-09-21 on Bowerstone Market stills: 0.75 % of pixels move perceptibly by day and 0.03 % by
	/// night, all of them on silhouettes; GI as a whole was 2.5 ms of a 9.5 ms GPU frame at 1440p.
	/// Settable live for the comparison.
	/// </summary>
	public static bool GiHalfResolution
	{
		get
		{
			if (_giHalfResolution is null)
			{
				try
				{
					_giHalfResolution = Refii.Core.Engine_RefiiConfig.Bool(
						Refii.Core.Engine_RefiiConfig.Read(out _), "gi_half_resolution", true);
				}
				catch (System.Exception)
				{
					_giHalfResolution = true;
				}
			}

			return _giHalfResolution.Value;
		}
		set => _giHalfResolution = value;
	}

	private static void ConfigureGlobalIllumination(Godot.Environment environment, bool unified)
	{
		// A renderer-wide switch, not the environment's; written only when it changes.
		bool half = GiHalfResolution;
		if (!_giHalfEverApplied || half != _giHalfApplied)
		{
			RenderingServer.GISetUseHalfResolution(half);
			_giHalfApplied = half;
			_giHalfEverApplied = true;
		}

		// A rebuild in progress holds it off for a frame or two; see Engine_GiRefresh.
		environment.SdfgiEnabled = unified && !Engine_GiRefresh.HoldingOff && !Engine_GiRefresh.ForcedOff;
		if (!unified)
		{
			return;
		}

		// Cascade 0 is 0.2 m cells over 25.6 m and each of the six doubles it: a cellar resolves
		// its walls, and the far cascade still spans the 800 m a vista is drawn to.
		environment.SdfgiCascades = 6;
		environment.SdfgiMinCellSize = 0.2f;
		environment.SdfgiYScale = Godot.Environment.SdfgiyScale.Scale75Percent;
		environment.SdfgiUseOcclusion = true;
		environment.SdfgiReadSkyLight = true;
		environment.SdfgiBounceFeedback = 0.5f;
		environment.SdfgiEnergy = 1.0f;
	}

	public static void ConfigureEnvironment(Godot.Environment environment, LightingFrame frame)
	{
		if (environment is null)
		{
			return;
		}

		environment.TonemapMode = Godot.Environment.ToneMapper.Linear;
		Engine_LevelBakedLighting.PublishThemeLighting(frame);

		// ONE FILL FOR EVERY SURFACE, AND GODOT CARRIES IT. The lit shaders no longer switch
		// Godot's ambient off, so exactly one side may supply the fill or it is counted twice:
		//   modern — Godot: this ambient plus the GI below. `fable_unified` = 1 stops every
		//            per-instance guest fill (flat ambient, probe, vertex bake, lightmap) from
		//            emitting, so a character and the wall beside him are lit by the same data
		//            and both take the bounce of the lights that are actually there.
		//   fable  — the guest: the shaders emit the 360's own fills and Godot's ambient has no
		//            energy, which is what `ambient_light_disabled` used to guarantee.
		bool unified = Engine_LightingFrame.ModernModel;
		RenderingServer.GlobalShaderParameterSet("fable_unified", unified ? 1.0f : 0.0f);
		ConfigureGlobalIllumination(environment, unified);

		// Sky reflections are not part of this: the materials carry their own environment map.
		environment.ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled;

		if (unified)
		{
			// GODOT'S AMBIENT IS THE GI'S SKY LIGHT, NOT A FLOOR: inside a cascade the engine replaces
			// it with the trace (`ambient_light = diffuse`), so it lands only where the sky is seen
			// — which is what the guest's BAKE did. It carries the bake's share of the fill,
			// `mean bake * LightmapColour`, in the theme's own colour; the flat floor is the
			// global `fable_ambient_colour` the shader emits. `ambient_light_scale` scales both.
			float[] sky =
			{
				Engine_LightingFrame.MeanAuthoredBake * frame.LightmapColour[0],
				Engine_LightingFrame.MeanAuthoredBake * frame.LightmapColour[1],
				Engine_LightingFrame.MeanAuthoredBake * frame.LightmapColour[2],
			};
			float peak = MathF.Max(sky[0], MathF.Max(sky[1], sky[2]));
			environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
			environment.AmbientLightColor = peak > 0f
				? new Color(sky[0] / peak, sky[1] / peak, sky[2] / peak)
				: Colors.Black;
			// The GI's half of the bake; the other half is the flat floor below.
			float traced = 1f - Engine_LightingFrame.ModernFloorShare;
			environment.AmbientLightEnergy = peak * traced * Engine_LevelBakedLighting.AmbientScale;
			float floor = Engine_LightingFrame.ModernFloorShare * Engine_LevelBakedLighting.AmbientScale;
			RenderingServer.GlobalShaderParameterSet("fable_fill_floor",
				new Vector3(sky[0] * floor, sky[1] * floor, sky[2] * floor));
		}
		else
		{
			// NONE MEANS NONE, not "whatever was here". The Environment outlives the region that
			// last configured it, and a frame with no ambient used to leave the previous
			// region's in place — a cave entered from a sunset kept the sunset (b175).
			environment.AmbientLightSource = Godot.Environment.AmbientSource.Disabled;
			environment.AmbientLightEnergy = 0f;
			RenderingServer.GlobalShaderParameterSet("fable_fill_floor", Vector3.Zero);
		}

		// THE PROBE FIELD'S SKY TETHER. Published in ambient_light_scale space because the
		// probe L0 it mixes with carries the same scale. Every caller that moves the lighting
		// frame comes through here, so the global tracks the day exactly as the theme does.
		// Weight 0 (the fable model, or a theme with no ambient) leaves the probes authored.
		// And it lets go where there is no sky to tether to: a cave whose theme states no main
		// light, or a room painted with its own ambient (frame.SkylightWeight carries both).
		float tether = Engine_LightingFrame.ModernModel && frame.HasAmbient
			? Engine_LightingFrame.ModernProbeSkyBlend * Math.Clamp(frame.SkylightWeight, 0f, 1f)
			: 0f;
		// NO SKY, MODERN MODEL: the bakes are the light. See fable_interior in the shader.
		// A painted day set is not a roof: painted sets author mood over OUTDOOR cells and the
		// data has no interior flag (`Engine_PaintedDaySets`). Gating on SkylightWeight
		// (`hasSky ? 1 - PaintedWeight : 0`) flipped the whole screen when PaintedWeight clamped
		// to exactly 1, and the painted set's ambient is already folded in by its stated weight.
		// The gate is the theme stating no main light; the interior dials are remaster choices.
		bool interior = Engine_LightingFrame.ModernModel && !frame.HasSky;
		Engine_LightingFrame.CurrentInterior = interior;
		Engine_LevelBakedLighting.InteriorNow = interior ? 1.0f : 0.0f;
		Engine_LevelBakedLighting.InteriorBakeNow =
			interior ? Engine_LightingFrame.InteriorBakeScale : 1.0f;
		RenderingServer.GlobalShaderParameterSet("fable_interior", interior ? 1.0f : 0.0f);
		RenderingServer.GlobalShaderParameterSet("fable_interior_fog",
			interior ? Engine_LightingFrame.InteriorFogScale : 1.0f);
		RenderingServer.GlobalShaderParameterSet("fable_interior_bake",
			interior ? Engine_LightingFrame.InteriorBakeScale : 1.0f);
		float ambientScale = Engine_LevelBakedLighting.AmbientScale;
		RenderingServer.GlobalShaderParameterSet("fable_sky_ambient", new Vector4(
			frame.AmbientColour[0] * frame.AmbientEnergy * ambientScale,
			frame.AmbientColour[1] * frame.AmbientEnergy * ambientScale,
			frame.AmbientColour[2] * frame.AmbientEnergy * ambientScale,
			tether));

		// OFF, EXPLICITLY, not merely left alone. Five of the saved level scenes carry
		// `fog_enabled = true` inside their Environment sub-resource from when this function set
		// it, with a fog colour of 5/255 — so simply not touching the property any more would
		// leave those regions running Godot's near-black depth fog UNDERNEATH the atmosphere
		// below, which reads as the world going dark with distance rather than as two fogs.
		environment.FogEnabled = false;

		// NO GODOT FOG, DELIBERATELY. Fable's fog is not a depth ramp and `Environment` cannot be
		// configured into one that resembles it: the transmittance is per channel (Rayleigh is
		// about 3.5x stronger in blue than in red, which is most of the look), the colour it
		// converges on is the sky's in-scatter in the view direction rather than a constant, and
		// the two density values the theme states are control points that solve a power law, not
		// the ends of a lerp — several shipped themes state a near density HIGHER than the far
		// one, which a ramp cannot represent at all.
		//
		// It lives in the surface shaders instead, which is where the guest puts it too. See
		// `fable_atmosphere.gdshaderinc` and `Engine_SkyAtmosphere.SolveFogCurve`.
	}

	/// <summary>
	/// Aims the sun so it agrees with the sun DISC in the sky.
	///
	/// <see cref="Engine_SkyFrame"/> builds SunDirection as (groundX, up, groundZ) — component
	/// [1] is the vertical — so it is already Y-up and needs only the X flip that carries it
	/// into the level root's mirrored space. Engine_CelestialElements does exactly that,
	/// <c>(-d0, d1, d2)</c>, to place the visible disc.
	///
	/// A DirectionalLight3D travels along its own -Z, and light travels FROM the sun, so the
	/// forward vector is the NEGATION of the direction to the disc: <c>(d0, -d1, -d2)</c>.
	///
	/// NOT all three: negating X as well puts the sun on one side of the sky and the light on
	/// the other, so shadows fall the wrong way horizontally — subtle enough to read as "the
	/// lighting needs work" rather than as a sign error, especially under a heavily overcast
	/// sky where the disc is hard to locate.
	/// </summary>
	private static Basis SunBasis(float[] direction)
	{
		// LOCAL, UNDER A MIRRORED ROOT. The light hangs off the region root, whose
		// Scale = (-1, 1, 1) mirrors X again on the way to world space. The world travel we want
		// is -ToGodot(d) = (d0, -d1, -d2); the root maps local (a, b, c) to world (-a, b, c);
		// so the local basis has to be (-d0, -d1, -d2). Taking (d0, -d1, -d2) here — the world
		// answer handed to a mirrored parent — put the light on the wrong side of the sky.
		var forward = -Refii.Level.Engine_SkyFrame.ToRegionLocal(direction);
		if (forward.LengthSquared() < 1e-8f)
		{
			forward = Vector3.Down;
		}

		forward = forward.Normalized();
		Vector3 up = MathF.Abs(forward.Y) > 0.99f ? Vector3.Forward : Vector3.Up;
		return Basis.LookingAt(forward, up);
	}
}
