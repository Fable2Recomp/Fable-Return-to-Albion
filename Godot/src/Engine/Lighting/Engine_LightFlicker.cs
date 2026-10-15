using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Lighting;

/// <summary>
/// The guest's light flicker — what makes a torch a torch rather than a steady orange lamp.
///
/// A flickering light is authored with three fields on its own component, and the guest steps them
/// in <c>sub_821F0A50</c>, the render light's per-frame update. It is ONE random walk per light
/// driving TWO outputs: the light dims and brightens, and its radius breathes with it.
///
/// ALL FOUR CONSTANTS BELOW WERE READ OUT OF THE IMAGE RATHER THAN TAKEN FROM A SUMMARY, because
/// a flicker that is subtly wrong reads as a flicker rather than as a defect and would never be
/// noticed:
///
///     dbl_83319630 = 40 3E 00 00 00 00 00 00  = 30.0   (the rate normaliser)
///     flt_8209B480 = 40 00 00 00              =  2.0   (the random's upper bound)
///     flt_82099490 = 3F 80 00 00              =  1.0
///     flt_8209D5C8 = BF 80 00 00              = -1.0   (the walk's clamp)
///
/// One updater walks every flickering light in the region rather than each light carrying its own
/// script: the work is a handful of floats and a random per light, and one loop over a list beats
/// several hundred nodes each paying for their own `_Process`.
/// </summary>
public partial class Engine_LightFlicker : Node
{
	public const string NodeName = "LightFlicker";

	/// <summary>
	/// The rate normaliser: the guest's step is scaled by <c>dt * 30</c>.
	/// </summary>
	/// <remarks>
	/// <c>821f0bb8 fmul f10, f13, f0</c> with f0 = <c>dbl_83319630</c>, whose eight bytes are
	/// 40 3E 00 00 00 00 00 00 — 30.0 as a big-endian double. It is the guest's 30 Hz pose clock,
	/// NOT the 15 Hz game tick that drives the light's fade-in and fade-out frame counts. The two
	/// live three words apart in the same data block and are easy to swap.
	/// </remarks>
	public const float RateNormaliser = 30.0f;

	/// <summary>The upper bound of the guest's random draw: <c>flt_8209B480</c> = 2.0.</summary>
	public const float RandomRange = 2.0f;

	/// <summary>
	/// How far the radius walk must move before the write reaches the node. 0 is the guest.
	/// </summary>
	/// <remarks>
	/// `OmniRange` is the one light property that changes the instance's AABB, and every change
	/// re-pairs that light against every geometry instance it touches
	/// (`RendererSceneCull::PairInstances::pair`); `LightEnergy` is cheap. The guest has no BVH
	/// pairing and moves the radius every step (`Flickering_RadiusRange`, component+68), so this
	/// damps a host-only cost.
	///
	/// In units of the authored radius: 0.05 is five per cent of BaseRange. Live-settable through
	/// the debug channel's `render lightFlickerRadius=`.
	/// </remarks>
	public static float RadiusEpsilon;

	private readonly List<Flickering> _lights = new();

	private readonly Random _random = new();

	private sealed class Flickering
	{
		public Light3D Node = null!;

		public float BaseEnergy;

		public float BaseRange;

		public float Rate;

		public float BrightnessRange;

		public float RadiusRange;

		/// <summary>
		/// The walk's state, in [-1, 1]. Starts at ZERO, as the render light's constructor leaves
		/// it (<c>82b1c074 stfs f0, 0x1BC(r31)</c> with f0 = 0.0), so a light opens at exactly its
		/// authored brightness and drifts from there rather than popping on at a random value.
		/// </summary>
		public float Phase;

		/// <summary>The walk's state one step earlier: the light is drawn between the two.</summary>
		public float PreviousPhase;

		public bool FlickersRadius;

		/// <summary>The radius scale last WRITTEN to the node, so <see cref="RadiusEpsilon"/> can
		/// measure against what the renderer actually holds rather than against the walk.</summary>
		public float WrittenRadiusScale;
	}

	/// <summary>
	/// Registers a light, and says whether it flickers at all.
	/// </summary>
	/// <remarks>
	/// A ZERO RATE MEANS NO FLICKER, however large the two ranges are. The step is
	/// <c>(rand - phase - 1) * Rate * dt * 30</c>, so with Rate at zero the walk never leaves its
	/// starting zero and both outputs stay at exactly 1.0 — the guest computes the whole thing and
	/// arrives back where it started. Skipping those lights here is not an optimisation, it is the
	/// same answer for less work.
	/// </remarks>
	public bool Add(Light3D light, in Engine_LevelLights.LightPlacement placement)
	{
		if (placement.FlickerRate <= 0.0f ||
			(placement.FlickerBrightnessRange == 0.0f && placement.FlickerRadiusRange == 0.0f))
		{
			return false;
		}

		_lights.Add(new Flickering
		{
			Node = light,
			// THE BUILT NODE'S VALUES, not the placement's. The node carries the remaster's
			// placed_light_scale and placed_light_range; taking the authored numbers here
			// silently undid both on every flickering fixture — which is every torch in a cave.
			BaseEnergy = light.LightEnergy,
			BaseRange = light is OmniLight3D omni ? omni.OmniRange
				: light is SpotLight3D spot ? spot.SpotRange : placement.Range,
			Rate = placement.FlickerRate,
			BrightnessRange = placement.FlickerBrightnessRange,
			RadiusRange = placement.FlickerRadiusRange,

			// THE RADIUS FLICKER IS POINT-ONLY HERE, and deliberately narrower than the guest.
			// `821f0c44 cmpwi cr6, r10, 1` takes the radius branch outright for a point light;
			// a spot reaches it only past two booleans at renderLight+0x150 and +0x152 whose
			// meaning is NOT ESTABLISHED. Applying it to spots would be guessing at that gate, and
			// spots are 207 of the 6,281 placed lights — so they take the brightness flicker,
			// which is unconditional, and keep a fixed cone.
			FlickersRadius = placement.Type == Engine_LevelLights.LightTypePoint,
		});

		return true;
	}

	public int Count => _lights.Count;

	/// <summary>Light writes made; against the frame count this says what the flicker costs.</summary>
	public static long Updates;

	/// <summary>Radius writes that REACHED the node, against the candidates that were offered.</summary>
	/// <remarks>The ratio to <see cref="RadiusCandidates"/> is what <see cref="RadiusEpsilon"/>
	/// suppresses.</remarks>
	public static long RadiusWrites;

	/// <summary>Lights offered to the radius walk, whether or not the write reached the node.</summary>
	public static long RadiusCandidates;

	private double _carried;

	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("lightflicker");

		// Under classic the placed lights are hidden (Engine_LightShadowBudget) and the title's
		// shaders take the guest's own, already flickered, light set.
		if (Refii.Models.Engine_ClassicMaterial.Active)
		{
			return;
		}

		// THE WALK KEEPS THE GUEST'S STEP; THE DRAW IS GODOT'S. The guest steps this once per its
		// own 30 Hz frame with `dt * 30`, so a step of exactly 1/30 s is one unit. Stepped per
		// display frame instead, a random walk of more, smaller steps averages out and the
		// flicker weakens — a look change. So the walk runs on its own 1/30 s accumulator on
		// Godot's clock (not the guest's frames), and every display frame draws the light
		// between its last two walk states.
		_carried += delta;
		int steps = 0;
		while (_carried >= WalkSeconds && steps < MaxWalkSteps)
		{
			_carried -= WalkSeconds;
			++steps;
			Walk();
		}

		if (steps == MaxWalkSteps)
		{
			_carried = 0.0;
		}

		float between = (float)Math.Clamp(_carried / WalkSeconds, 0.0, 1.0);
		Updates += _lights.Count;
		RadiusCandidates += _lights.Count;
		foreach (Flickering light in _lights)
		{
			if (IsInstanceValid(light.Node))
			{
				Apply(light, light.PreviousPhase + ((light.Phase - light.PreviousPhase) * between));
			}
		}
	}

	/// <summary>One walk step: the guest's frame.</summary>
	private const double WalkSeconds = 1.0 / RateNormaliser;

	/// <summary>A stall walks at most this many steps and drops the rest.</summary>
	private const int MaxWalkSteps = 4;

	/// <summary>One step of every light's walk, `dt * 30` = 1.</summary>
	private void Walk()
	{
		foreach (Flickering light in _lights)
		{
			// The guest's walk, in its own order: draw uniformly from [0, 2), subtract the current
			// phase and one, scale by the rate and the normalised step, and add.
			//
			//     821f0bcc  bl     LHMath_GFFloatRandom     ; f1 = rand in [0, 2)
			//     821f0bd0  fadds  f9, f1, f26              ; f26 = -phase - 1
			//     821f0be8  fmuls  f7, f9, f28              ; * FlickeringRate
			//     821f0bec  fmadds f6, f7, f25, f8          ; * (dt*30), + phase
			//
			// Centred on zero because the draw is centred on one: `rand - phase - 1` has mean
			// `-phase`, so the walk is pulled back toward the authored value and cannot run away.
			light.PreviousPhase = light.Phase;
			float draw = (float)(_random.NextDouble() * RandomRange);
			light.Phase += (draw - light.Phase - 1.0f) * light.Rate;
			light.Phase = Math.Clamp(light.Phase, -1.0f, 1.0f);
		}
	}

	/// <summary>Writes one light's brightness and radius at a walk state.</summary>
	private static void Apply(Flickering light, float phase)
	{
		// `821f0c4c fmadds f11, f13, f27, f0` — phase * BrightnessRange + 1, straight onto the
		// light's colour scale. The floor is ours: the guest stores the product unclamped at
		// this point, and with the shipped ranges (0.05 to 0.23) it cannot go negative anyway,
		// but a modded value past 1.0 would otherwise drive the energy through zero.
		light.Node.LightEnergy =
			light.BaseEnergy * MathF.Max(1.0f + (phase * light.BrightnessRange), 0.0f);

		if (!light.FlickersRadius)
		{
			return;
		}

		// `821f0c84 fmadds f13, f13, f29, f0` — the same phase against the RADIUS range, which
		// is a different number from the brightness one. The two ranges are authored
		// separately (0.21 and 0.23 on the shipped torches) and share only the walk.
		float scale = MathF.Max(1.0f + (phase * light.RadiusRange), 0.01f);
		if (RadiusEpsilon > 0.0f
			&& MathF.Abs(scale - light.WrittenRadiusScale) < RadiusEpsilon)
		{
			return;
		}

		light.WrittenRadiusScale = scale;
		float reach = light.BaseRange * Refii.Lighting.Engine_LevelLights.RangeScale;

		// THE NODE HOLDS THE ENVELOPE, THE TABLE HOLDS THE BREATH. The phase is clamped to
		// [-1, 1], so `1 + RadiusRange` is the largest radius this light can state; the node
		// sits there and is written only when that changes (the live range scale). The radius
		// of THIS step goes to the light table, which is what the shader ramps over.
		var omniNode = (OmniLight3D)light.Node;
		float envelope = reach * (1.0f + MathF.Abs(light.RadiusRange));
		if (MathF.Abs(omniNode.OmniRange - envelope) > 1e-4f)
		{
			omniNode.OmniRange = envelope;
			RadiusWrites++;
		}

		Engine_LightTable.SetStatedRange(omniNode, reach * scale);
	}
}
