namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// SPELL WEAVING'S LOOKS ARE THE OLD PIECES, RE-STATED IN THE BANK'S OWN TERMS. Each effect in
/// fx/effects/*.json is pinned here against the Godot-drawn piece it replaces — the numbers from
/// the old fx/recipes.json (git 6bfa7b4db, before the looks became bank effects) and the maths of
/// <c>Engine_FxRecipePieces</c> — through the bank's own simulation (<c>Engine_FxSystem</c>): what each
/// entity, timeline and key does per 30 Hz step. Pure maths on the JSON; no Godot type.
/// <list type="bullet">
/// <item>embers — a disc emitter shooting out from its EDGE (<c>shapeFlagA</c>, <c>shapeFlagB</c>) at
/// the cone's own widening speed, lifted at the cone's climb (negative <c>gravityScale</c> against a
/// halving <c>vec1.z</c>, which holds the rise to a steady speed after a frame), for the piece's
/// period, at its count ÷ period, with its colour and size keys — a true cone of radius
/// <c>radius</c>→<c>radiusTop</c> (±20%, the piece's own jitter) over <c>height</c>, every particle
/// turning about the cone's axis at the piece's <c>speed</c> (the emitter's <c>spin</c>);</item>
/// <item>arcs — a bolt sprite of the piece's lightning texture struck at a random point of the band
/// the old arcs crawled (a spline round the cone with <c>splineRandomGenerator</c>), at
/// count × rate × 0.65 strikes a second (35% of the old strikes skipped), each lasting one
/// strike's 1 ÷ rate, as long as the old chord and as bright as the old mean flash;</item>
/// <item>ring — one flat ring per period whose size keys follow the piece's square-root ease and
/// whose colour and alpha fall with 1 − t, as the piece's material did;</item>
/// <item>blades — FABLE II'S OWN BLADES SWORDS (SpellShotDamoclesSword's model) ORBITING one attractor at
/// the piece's turn rate and reach (the vertical step frozen by <c>vec1.z</c> 0), each laid along its
/// orbit — tip first along the way it flies, climbing by the piece's tilt, the flat facing out
/// (<c>meshAlign</c> under the emitter's rotation, turned about the vertical with the orbit) — and
/// each carrying the sword's own effects (fxspl_damocles_trail, fxspl_damocles_sword_continue, the
/// bank's own emitters) on an orbital in step with it;</item>
/// <item>light — the bank's point light with its own flicker walk (p19/p20/p21) set to the
/// piece's flicker: the walk's corner at the old wave's fast 23 rad/s, its spread at the wave's.</item>
/// </list>
/// </summary>
[TestSuite]
public class SpellWeavingLookTests
{
	private const double Fps = 30.0;
	private const double Gravity = 9.8;

	/// <summary>The old recipes' pieces and lights, as fx/recipes.json stated them (meshes left out: the cutlass).</summary>
	private static readonly Dictionary<string, string> Old = new(StringComparer.Ordinal)
	{
		["spell_weaving_vortex_blades"] = """{"light":{"color":[0.8,0.85,1.0],"energy":3,"range":8,"height":1.5,"flicker":0.1},"pieces":[{"kind":"blades","texture":"fxt_democles_aura","color":[0.75,0.85,1.0],"count":5,"radius":1.2,"height":1.0,"speed":1.0,"tilt":15,"size":1.0,"width":1.0,"intensity":2.0,"period":0.3}]}""",
		["spell_weaving_vortex_fire"] = """{"light":{"color":[1.0,0.45,0.12],"energy":8,"range":8,"height":1.5,"flicker":0.3},"pieces":[{"kind":"embers","texture":"fxt_smokepuff_cloud_01","color":[1.0,0.5,0.2,0.5],"colorEnd":[1.0,0.5,0.2,0.0],"height":6.5,"radius":0.3,"radiusTop":0.9,"speed":2.0,"size":1.3,"sizeEnd":2.6,"count":100,"period":2.4,"intensity":1.7},{"kind":"embers","texture":"fxt_smokepuff_long_01","color":[1.0,0.5,0.2,0.45],"colorEnd":[1.0,0.5,0.2,0.0],"height":6.8,"radius":0.25,"radiusTop":0.8,"speed":2.4,"size":1.0,"sizeEnd":2.2,"count":60,"period":2.2,"intensity":1.7},{"kind":"embers","texture":"fxt_sparkle_orange_01","color":[1,1,0.8,1.0],"colorEnd":[1,0.5,0.1,0.0],"height":1.4,"radius":0.2,"radiusTop":0.7,"speed":3.0,"size":0.22,"sizeEnd":0.08,"count":90,"period":1.0,"intensity":2.2},{"kind":"embers","texture":"fxt_flames_double_02","color":[1,0.6,0.3,1.0],"colorEnd":[1,0.4,0.1,0.0],"height":1.3,"radius":1.8,"radiusTop":2.4,"speed":0.6,"size":2.2,"sizeEnd":3.0,"count":12,"period":1.8,"intensity":1.6}]}""",
		["spell_weaving_vortex_shock"] = """{"light":{"color":[0.45,0.65,1.0],"energy":6,"range":8,"height":1.5,"flicker":0.5},"pieces":[{"kind":"embers","texture":"fxt_smokepuff_cloud_01","color":[0.5,0.7,1.0,0.5],"colorEnd":[0.5,0.7,1.0,0.0],"height":6.5,"radius":0.3,"radiusTop":0.9,"speed":2.0,"size":1.3,"sizeEnd":2.6,"count":100,"period":2.4,"intensity":1.7},{"kind":"embers","texture":"fxt_smokepuff_long_01","color":[0.5,0.7,1.0,0.45],"colorEnd":[0.5,0.7,1.0,0.0],"height":6.8,"radius":0.25,"radiusTop":0.8,"speed":2.4,"size":1.0,"sizeEnd":2.2,"count":60,"period":2.2,"intensity":1.7},{"kind":"arcs","texture":"fxt_lightning_vertical_cyan_01","color":[0.8,0.9,1.0],"height":6.2,"radius":0.25,"radiusTop":0.6,"rate":10,"count":4,"width":1.3,"intensity":3.5},{"kind":"embers","texture":"fxt_lightningglow_01","color":[1,1,1,0.9],"colorEnd":[0.5,0.7,1.0,0.0],"height":1.0,"radius":0.1,"radiusTop":0.4,"speed":2.0,"size":1.2,"sizeEnd":0.6,"count":14,"period":0.7,"intensity":2.4},{"kind":"embers","texture":"fxt_sparkle_white_01","color":[1,1,1,1.0],"colorEnd":[0.5,0.7,1.0,0.0],"height":1.6,"radius":0.2,"radiusTop":0.7,"speed":3.0,"size":0.2,"sizeEnd":0.06,"count":70,"period":1.0,"intensity":2.0},{"kind":"ring","texture":"fxt_white_ring_01","color":[0.6,0.6,1.0],"radius":2.6,"period":1.5,"height":0.1,"intensity":1.6}]}""",
		["spell_weaving_vortex_push"] = """{"light":{"color":[0.9,0.9,1.0],"energy":3,"range":8,"height":1.5,"flicker":0.6},"pieces":[{"kind":"embers","texture":"fxt_smokepuff_cloud_01","color":[0.3,0.3,0.33,0.8],"colorEnd":[0.3,0.3,0.33,0.0],"height":6.5,"radius":0.3,"radiusTop":0.9,"speed":2.0,"size":1.3,"sizeEnd":2.6,"count":90,"period":2.4,"intensity":1.0,"blend":"mix"},{"kind":"embers","texture":"fxt_smokepuff_long_01","color":[0.7,0.7,0.75,0.18],"colorEnd":[0.7,0.7,0.75,0.0],"height":6.8,"radius":0.25,"radiusTop":0.8,"speed":2.4,"size":1.0,"sizeEnd":2.2,"count":40,"period":2.2,"intensity":1.0},{"kind":"embers","texture":"fxt_dust_fall_cloud","color":[0.7,0.65,0.6,0.6],"colorEnd":[0.7,0.65,0.6,0.0],"height":1.2,"radius":1.6,"radiusTop":2.4,"speed":0.8,"size":1.8,"sizeEnd":2.6,"count":14,"period":1.6,"intensity":0.9},{"kind":"ring","texture":"fxt_smokepuff_cloud_01","color":[0.6,0.65,0.75],"radius":3.5,"period":0.9,"height":0.15,"intensity":1.4}]}""",
		["spell_weaving_vortex_chaos"] = """{"light":{"color":[0.8,0.25,1.0],"energy":6,"range":8,"height":1.5,"flicker":0.4},"pieces":[{"kind":"embers","texture":"fxt_smokepuff_cloud_01","color":[0.7,0.3,1.0,0.5],"colorEnd":[0.7,0.3,1.0,0.0],"height":6.5,"radius":0.3,"radiusTop":0.9,"speed":2.0,"size":1.3,"sizeEnd":2.6,"count":100,"period":2.4,"intensity":1.7},{"kind":"embers","texture":"fxt_smokepuff_long_01","color":[0.7,0.3,1.0,0.45],"colorEnd":[0.7,0.3,1.0,0.0],"height":6.8,"radius":0.25,"radiusTop":0.8,"speed":2.4,"size":1.0,"sizeEnd":2.2,"count":60,"period":2.2,"intensity":1.7},{"kind":"embers","texture":"fxt_blue_twirl_01","color":[0.9,0.5,1.0,1.0],"colorEnd":[0.3,0.05,0.6,0.0],"height":2.0,"radius":0.2,"radiusTop":0.8,"speed":2.5,"size":0.5,"sizeEnd":0.3,"count":60,"period":1.4,"intensity":1.8},{"kind":"embers","texture":"fxt_silk_streams_01","color":[0.7,0.3,1.0,0.8],"colorEnd":[0.3,0.05,0.6,0.0],"height":1.6,"radius":1.6,"radiusTop":2.4,"speed":0.6,"size":1.8,"sizeEnd":2.6,"count":12,"period":1.8,"intensity":1.4}]}""",
		["spell_weaving_blades_fire"] = """{"light":{"color":[1.0,0.45,0.12],"energy":4.8,"range":5,"height":0.8,"flicker":0.3},"pieces":[{"kind":"embers","texture":"fxt_flames_double_02","color":[1,1,1,1.0],"colorEnd":[1,1,0.9,0.3],"height":1.2,"radius":0.15,"radiusTop":0.4,"speed":4.0,"size":0.7,"sizeEnd":0.6,"count":40,"period":0.6,"intensity":2.0},{"kind":"embers","texture":"fxt_sparkle_white_01","color":[0.0,0.0,1.0,0.4],"colorEnd":[0.0,0.0,1.0,0.0],"height":1.0,"radius":0.15,"radiusTop":0.4,"speed":3.0,"size":0.6,"sizeEnd":0.3,"count":12,"period":0.8,"intensity":1.2},{"kind":"embers","texture":"fxt_smokepuff_long_01","color":[1,0.4,0.4,0.5],"colorEnd":[1,0.4,0.4,0.0],"height":1.2,"radius":0.1,"radiusTop":0.4,"speed":2.0,"size":0.3,"sizeEnd":0.3,"count":10,"period":0.6,"intensity":1.0},{"kind":"embers","texture":"fxt_lightningglow_01","color":[1,0.9,0.6,0.3],"colorEnd":[1,1,1,0.0],"height":0.9,"radius":0.05,"radiusTop":0.2,"speed":1.0,"size":1.0,"sizeEnd":1.0,"count":6,"period":1.0,"intensity":1.4}]}""",
		["spell_weaving_blades_shock"] = """{"light":{"color":[0.45,0.65,1.0],"energy":3.5999999999999996,"range":5,"height":0.8,"flicker":0.5},"pieces":[{"kind":"embers","texture":"fxt_lightningglow_01","color":[0.4,0.6,1,0.5],"colorEnd":[0.4,0.6,1,0.0],"height":1.0,"radius":0.1,"radiusTop":0.4,"speed":2.0,"size":1.0,"sizeEnd":1.0,"count":12,"period":0.6,"intensity":1.8},{"kind":"embers","texture":"fxt_electrical_patch","color":[0.4,0.6,1,1.0],"colorEnd":[0.4,0.6,1,0.0],"height":1.0,"radius":0.1,"radiusTop":0.4,"speed":3.0,"size":0.5,"sizeEnd":0.5,"count":16,"period":0.4,"intensity":2.5},{"kind":"arcs","texture":"fxt_lightning_curl_cyan_01","color":[1,1,1],"height":1.2,"radius":0.2,"radiusTop":0.5,"rate":14,"count":4,"width":1.0,"intensity":3.5}]}""",
		["spell_weaving_blades_push"] = """{"light":{"color":[0.9,0.9,1.0],"energy":1.7999999999999998,"range":5,"height":0.8,"flicker":0.6},"pieces":[{"kind":"embers","texture":"fxt_smokepuff_cloud_01","color":[0.5,0.7,0.9,0.25],"colorEnd":[0.5,0.7,0.9,0.0],"height":1.0,"radius":0.1,"radiusTop":0.5,"speed":2.0,"size":1.0,"sizeEnd":1.0,"count":14,"period":0.5,"intensity":1.0},{"kind":"embers","texture":"fxt_smokepuff_long_01","color":[0.7,0.8,1.0,0.5],"colorEnd":[0.7,0.8,1.0,0.0],"height":1.0,"radius":0.1,"radiusTop":0.4,"speed":2.0,"size":1.0,"sizeEnd":1.0,"count":10,"period":0.5,"intensity":1.2},{"kind":"embers","texture":"fxt_dust_fall_cloud","color":[0.2,0.2,0.1,0.8],"colorEnd":[0.2,0.2,0.1,0.0],"height":0.8,"radius":0.1,"radiusTop":0.5,"speed":1.0,"size":0.6,"sizeEnd":0.8,"count":10,"period":0.3,"intensity":1.0,"blend":"mix"},{"kind":"ring","texture":"fxt_white_ring_01","color":[0.5,0.7,0.9],"radius":1.4,"period":0.6,"height":0.08,"intensity":1.6}]}""",
		["spell_weaving_blades_chaos"] = """{"light":{"color":[0.8,0.25,1.0],"energy":3.5999999999999996,"range":5,"height":0.8,"flicker":0.4},"pieces":[{"kind":"embers","texture":"fxt_blue_twirl_01","color":[0.9,0.5,1.0,1.0],"colorEnd":[0.3,0.05,0.6,0.0],"height":1.1,"radius":0.1,"radiusTop":0.4,"speed":3.0,"size":0.45,"sizeEnd":0.3,"count":24,"period":0.7,"intensity":1.8},{"kind":"embers","texture":"fxt_silk_streams_01","color":[0.7,0.3,1.0,0.8],"colorEnd":[0.3,0.05,0.6,0.0],"height":1.2,"radius":0.1,"radiusTop":0.4,"speed":2.5,"size":0.6,"sizeEnd":0.4,"count":16,"period":0.8,"intensity":1.4}]}""",
		["spell_weaving_fire_shock"] = """{"light":{"color":[0.45,0.65,1.0],"energy":3.5999999999999996,"range":5,"height":0.8,"flicker":0.5},"pieces":[{"kind":"embers","texture":"fxt_lightningglow_01","color":[0.6,0.6,1.0,0.25],"colorEnd":[0.6,0.6,1.0,0.0],"height":0.6,"radius":0.0,"radiusTop":0.5,"speed":0.5,"size":1.25,"sizeEnd":1.4,"count":14,"period":0.6,"intensity":1.8},{"kind":"embers","texture":"fxt_smokepuff_cloud_01","color":[1.0,0.8,0.2,0.6],"colorEnd":[0.5,0.3,1.0,0.0],"height":0.9,"radius":0.1,"radiusTop":0.6,"speed":1.0,"size":0.3,"sizeEnd":1.6,"count":24,"period":0.6,"intensity":1.3},{"kind":"embers","texture":"fxt_fireglow_01","color":[0.6,0.6,1.0,0.8],"colorEnd":[0.5,0.3,1.0,0.0],"height":0.2,"radius":0.0,"radiusTop":0.05,"speed":0.0,"size":1.5,"sizeEnd":0.5,"count":3,"period":0.4,"intensity":1.0},{"kind":"embers","texture":"fxt_electrical_patch","color":[1,1,1,1.0],"colorEnd":[0.6,0.6,1.0,0.0],"height":0.5,"radius":0.0,"radiusTop":0.3,"speed":2.0,"size":1.0,"sizeEnd":1.0,"count":8,"period":0.25,"intensity":2.5},{"kind":"arcs","texture":"fxt_lightning_vertical_cyan_01","color":[0.7,0.9,1.0],"height":2.2,"radius":0.2,"radiusTop":1.6,"rate":9,"count":3,"width":1.8,"intensity":3.5},{"kind":"embers","texture":"fxt_sparkle_white_01","color":[1,1,1,1.0],"colorEnd":[1,1,1,0.0],"height":1.2,"radius":0.1,"radiusTop":0.5,"speed":3.0,"size":0.12,"sizeEnd":0.04,"count":40,"period":1.0,"intensity":2.0}]}""",
		["spell_weaving_fire_push"] = """{"light":{"color":[1.0,0.6,0.3],"energy":3,"range":5,"height":0.5,"flicker":0.2},"pieces":[{"kind":"embers","texture":"fxt_flames_double_01","color":[1,0.9,0.9,1.0],"colorEnd":[1,0.5,0.1,0.0],"height":0.8,"radius":0.1,"radiusTop":0.5,"speed":2.0,"size":0.5,"sizeEnd":0.7,"count":40,"period":0.67,"intensity":2.0},{"kind":"embers","texture":"fxt_explosion_fire_02","color":[1,1,1,1.0],"colorEnd":[1,1,1,0.0],"height":0.2,"radius":0.0,"radiusTop":0.1,"speed":0.0,"size":1.0,"sizeEnd":0.2,"count":3,"period":1.0,"intensity":1.4},{"kind":"embers","texture":"fxt_sparkle_orange_01","color":[1,0.6,0.4,1.0],"colorEnd":[0.2,0.2,0.1,0.0],"height":0.6,"radius":0.1,"radiusTop":0.8,"speed":1.0,"size":0.3,"sizeEnd":0.15,"count":30,"period":0.5,"intensity":1.6},{"kind":"ring","texture":"fxt_smokepuff_cloud_01","color":[1.0,0.6,0.4],"radius":1.3,"period":0.5,"height":0.2,"intensity":1.6}]}""",
		["spell_weaving_fire_chaos"] = """{"light":{"color":[0.8,0.25,1.0],"energy":3.5999999999999996,"range":5,"height":0.8,"flicker":0.4},"pieces":[{"kind":"embers","texture":"fxt_blue_twirl_01","color":[0.9,0.5,1.0,1.0],"colorEnd":[0.3,0.05,0.6,0.0],"height":1.0,"radius":0.1,"radiusTop":0.5,"speed":3.0,"size":0.5,"sizeEnd":0.3,"count":30,"period":0.7,"intensity":1.8},{"kind":"embers","texture":"fxt_silk_streams_01","color":[0.7,0.3,1.0,0.8],"colorEnd":[0.3,0.05,0.6,0.0],"height":1.2,"radius":0.1,"radiusTop":0.6,"speed":2.5,"size":0.7,"sizeEnd":0.5,"count":20,"period":0.9,"intensity":1.4}]}""",
		["spell_weaving_shock_push"] = """{"light":{"color":[0.9,0.9,1.0],"energy":1.7999999999999998,"range":5,"height":0.8,"flicker":0.6},"pieces":[{"kind":"ring","texture":"fxt_smokepuff_cloud_01","color":[0.9,0.8,0.6],"radius":2.2,"period":0.6,"height":0.25,"intensity":1.0,"blend":"mix"},{"kind":"embers","texture":"fxt_smokepuff_long_01","color":[0.9,0.8,0.6,0.4],"colorEnd":[0.9,0.8,0.6,0.0],"height":0.9,"radius":0.4,"radiusTop":2.0,"speed":0.6,"size":1.0,"sizeEnd":1.0,"count":20,"period":0.5,"intensity":1.0,"blend":"mix"},{"kind":"ring","texture":"fxt_white_ring_01","color":[0.4,0.4,0.45],"radius":1.0,"period":0.3,"height":0.1,"intensity":0.8}]}""",
		["spell_weaving_shock_chaos"] = """{"light":{"color":[0.8,0.25,1.0],"energy":3.5999999999999996,"range":5,"height":0.8,"flicker":0.4},"pieces":[{"kind":"embers","texture":"fxt_blue_twirl_01","color":[0.9,0.5,1.0,1.0],"colorEnd":[0.3,0.05,0.6,0.0],"height":1.2,"radius":0.1,"radiusTop":0.5,"speed":3.0,"size":0.5,"sizeEnd":0.3,"count":30,"period":0.7,"intensity":1.8},{"kind":"embers","texture":"fxt_silk_streams_01","color":[0.7,0.3,1.0,0.8],"colorEnd":[0.3,0.05,0.6,0.0],"height":1.4,"radius":0.1,"radiusTop":0.6,"speed":2.5,"size":0.7,"sizeEnd":0.5,"count":20,"period":0.9,"intensity":1.4},{"kind":"arcs","texture":"fxt_lightning_curl_cyan_01","color":[0.8,0.5,1.0],"height":1.4,"radius":0.2,"radiusTop":0.6,"rate":8,"count":3,"width":0.9,"intensity":2.5}]}""",
		["spell_weaving_push_chaos"] = """{"light":{"color":[0.8,0.25,1.0],"energy":3.5999999999999996,"range":5,"height":0.8,"flicker":0.4},"pieces":[{"kind":"ring","texture":"fxt_smokepuff_cloud_01","color":[0.6,0.3,0.9],"radius":2.2,"period":0.6,"height":0.25,"intensity":1.2},{"kind":"embers","texture":"fxt_silk_streams_01","color":[0.7,0.3,1.0,0.8],"colorEnd":[0.3,0.05,0.6,0.0],"height":1.2,"radius":0.4,"radiusTop":1.8,"speed":1.0,"size":0.9,"sizeEnd":1.2,"count":30,"period":0.7,"intensity":1.4},{"kind":"embers","texture":"fxt_blue_twirl_01","color":[0.9,0.5,1.0,1.0],"colorEnd":[0.3,0.05,0.6,0.0],"height":1.0,"radius":0.3,"radiusTop":1.6,"speed":1.5,"size":0.5,"sizeEnd":0.3,"count":24,"period":0.6,"intensity":1.6}]}""",
	};

	[TestCase] public void VortexBlades() => Check("spell_weaving_vortex_blades");
	[TestCase] public void VortexFire() => Check("spell_weaving_vortex_fire");
	[TestCase] public void VortexShock() => Check("spell_weaving_vortex_shock");
	[TestCase] public void VortexPush() => Check("spell_weaving_vortex_push");
	[TestCase] public void VortexChaos() => Check("spell_weaving_vortex_chaos");
	[TestCase] public void BladesFire() => Check("spell_weaving_blades_fire");
	[TestCase] public void BladesShock() => Check("spell_weaving_blades_shock");
	[TestCase] public void BladesPush() => Check("spell_weaving_blades_push");
	[TestCase] public void BladesChaos() => Check("spell_weaving_blades_chaos");
	[TestCase] public void FireShock() => Check("spell_weaving_fire_shock");
	[TestCase] public void FirePush() => Check("spell_weaving_fire_push");
	[TestCase] public void FireChaos() => Check("spell_weaving_fire_chaos");
	[TestCase] public void ShockPush() => Check("spell_weaving_shock_push");
	[TestCase] public void ShockChaos() => Check("spell_weaving_shock_chaos");
	[TestCase] public void PushChaos() => Check("spell_weaving_push_chaos");

	private static string? ModFolder()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 16 && at != null; i++)
		{
			string candidate = Path.Combine(at, "mods-src", "spell_weaving");
			if (Directory.Exists(Path.Combine(candidate, "fx", "effects")))
			{
				return candidate;
			}

			at = Path.GetDirectoryName(at);
		}

		return null;
	}

	private sealed record Link(JsonElement Emitter, JsonElement? System);

	private static void Check(string name)
	{
		if (ModFolder() is not { } folder)
		{
			return;  // a packaged test run without the source tree
		}

		using JsonDocument effect = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "fx", "effects", name + ".json")));
		using JsonDocument old = JsonDocument.Parse(Old[name]);
		JsonElement root = effect.RootElement;
		JsonElement[] emitters = root.GetProperty("emitters").EnumerateArray().ToArray();
		JsonElement[] systems = root.GetProperty("systems").EnumerateArray().ToArray();
		var chains = new List<List<Link>>();
		int vector = -1;
		foreach (JsonElement part in root.GetProperty("parts").EnumerateArray())
		{
			int v = part.GetProperty("vector").GetInt32();
			if (v != vector)
			{
				chains.Add(new List<Link>());
				vector = v;
			}

			JsonElement? system = part.TryGetProperty("system", out JsonElement s) ? systems[s.GetInt32()] : null;
			chains[^1].Add(new Link(emitters[part.GetProperty("emitter").GetInt32()], system));
		}

		int at = 0;
		foreach (JsonElement piece in old.RootElement.GetProperty("pieces").EnumerateArray())
		{
			string kind = piece.GetProperty("kind").GetString()!;
			string where = $"{name} piece {at} ({kind})";
			switch (kind)
			{
				case "embers":
					Embers(where, chains[at++], piece);
					break;
				case "arcs":
					Arcs(where, chains[at++], piece);
					break;
				case "ring":
					Ring(where, chains[at++], piece);
					break;
				case "blades":
					at += Blades(where, chains, at, piece);
					break;
				default:
					throw new InvalidOperationException(kind);
			}
		}

		Light($"{name} light", chains[at++], old.RootElement.GetProperty("light"));
		AssertThat(at).OverrideFailureMessage($"{name}: {chains.Count - at} chains beyond the old pieces").IsEqual(chains.Count);
	}

	// ---- the old piece's numbers, with Engine_FxEffectJson.ReadPiece's defaults ----------------

	private static double Num(JsonElement piece, string key, double fallback) =>
		piece.TryGetProperty(key, out JsonElement v) ? v.GetDouble() : fallback;

	private static double[] Rgba(JsonElement piece, string key, double[] fallback)
	{
		if (!piece.TryGetProperty(key, out JsonElement v))
		{
			return fallback;
		}

		double[] c = v.EnumerateArray().Select(x => x.GetDouble()).ToArray();
		return c.Length == 4 ? c : new[] { c[0], c[1], c[2], 1.0 };
	}

	// ---- the effect's numbers, with the bank's defaults ----------------------------------------

	private static readonly double[] ParamDefaults =
	{
		0.95, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 1, 1, 15, 1, 64, 50, 0.05, 0.05, 0.1, 0.2, 0.1, 0.7853982, 0, 0, 0,
	};

	private static readonly string[] ParamNames =
	{
		"p0", "p1", "rate", "rateSpread", "speed", "speedSpread", "accelDirScale", "gravityScale",
		"globalWindScale", "velX", "velY", "velZ", "wind", "p13", "p14", "lightRange",
		"lightIntensity", "lightCutoff", "lightFadeRamp", "p19", "p20", "p21", "p22", "p23",
		"lightConeAngle", "p25", "attractRadius", "attractStrength",
	};

	private static double Param(JsonElement e, string key)
	{
		JsonElement ps = e.GetProperty("params");
		if (ps.TryGetProperty(key, out JsonElement keys))
		{
			JsonElement[] list = keys.EnumerateArray().ToArray();
			AssertThat(list.Length).OverrideFailureMessage($"{key}: one key, a constant").IsEqual(1);
			return list[0][1].GetDouble();
		}

		return ParamDefaults[Array.IndexOf(ParamNames, key)];
	}

	private static double[] Vec(JsonElement e, string key, double[] fallback) =>
		e.GetProperty("vectors").TryGetProperty(key, out JsonElement keys)
			? keys[0].EnumerateArray().Skip(1).Select(x => x.GetDouble()).ToArray()
			: fallback;

	private static double Field(JsonElement e, string key, double fallback) =>
		e.TryGetProperty(key, out JsonElement v) ? v.GetDouble() : fallback;

	private static bool Flag(JsonElement e, string key) => e.TryGetProperty(key, out JsonElement v) && v.GetBoolean();

	private static double[] Floats(JsonElement e, string key, double[] fallback) =>
		e.TryGetProperty(key, out JsonElement v) ? v.EnumerateArray().Select(x => x.GetDouble()).ToArray() : fallback;

	private static JsonElement[] Keys(Link link) => link.System!.Value.GetProperty("visuals").EnumerateArray().ToArray();

	private static int Frame(JsonElement key) => (int)Field(key, "delayFrames", 0);

	private static void Near(string what, double actual, double expected, double tolerance = 1e-3)
	{
		double allowed = tolerance * Math.Max(1.0, Math.Abs(expected));
		AssertThat(Math.Abs(actual - expected) <= allowed)
			.OverrideFailureMessage($"{what}: {actual:R}, the old piece's maths say {expected:R}")
			.IsTrue();
	}

	private static (int Src, int Dst) Blend(string blend) => blend == "mix" ? (1, 5) : (4, 1);

	// ---- the kinds ------------------------------------------------------------------------------

	private static void Embers(string where, List<Link> chain, JsonElement piece)
	{
		AssertThat(chain.Count).OverrideFailureMessage($"{where}: one emitter").IsEqual(1);
		JsonElement e = chain[0].Emitter;
		double period = Math.Max(0.2, Num(piece, "period", 1));
		double count = Math.Max(8, Num(piece, "count", 64));
		double r0 = Num(piece, "radius", 1), r1 = Num(piece, "radiusTop", r0), height = Num(piece, "height", 2);
		int life = (int)Math.Round(period * Fps);

		// A disc shooting out from its edge: every particle leaves at the cone's base radius,
		// radially, and widens at the cone's rate — the piece's lerp(radius, radiusTop, t) ×
		// (0.8 + 0.4·u), as speed + u·speedSpread over the period.
		AssertThat(Field(e, "typeId", 0)).OverrideFailureMessage($"{where}: a standard emitter").IsEqual(0.0);
		AssertThat(Field(e, "subType", 0)).OverrideFailureMessage($"{where}: a disc").IsEqual(2.0);
		AssertThat(Flag(e, "shapeFlagA") && Flag(e, "shapeFlagB")).OverrideFailureMessage($"{where}: from the edge, outward").IsTrue();
		AssertThat(Flag(e, "useLife")).OverrideFailureMessage($"{where}: emits for as long as it plays").IsFalse();
		Near($"{where} base radius", Field(e, "spawnRadius", 0.1), Math.Max(r0, 0.01));
		double speed = Param(e, "speed"), spread = Param(e, "speedSpread");
		Near($"{where} mean top radius", Math.Max(r0, 0.01) + ((speed + (spread / 2)) * period), r1, 0.02);
		Near($"{where} radius jitter", spread * period, 0.4 * r1);

		// The climb: gravity reversed, the vertical velocity halved each step — steady at
		// a·m/(1−m) per step a frame after the spawn, so the particle rises the height over its life.
		double[] damping = Vec(e, "vec1", new[] { 1.0, 1, 1 });
		Near($"{where} no horizontal damping", damping[0] * damping[1], 1);
		double m = damping[2];
		AssertThat(m > 0 && m < 1).OverrideFailureMessage($"{where}: a damped rise").IsTrue();
		double lift = -Param(e, "gravityScale") * Gravity / (Fps * Fps);
		Near($"{where} climb over the life", lift * m / (1 - m) * life, height);

		// The turn: the piece's angle grew by speed × (0.8 + 0.4·w) rad/s about the cone's axis —
		// the emitter's own Z (no rotation stated), at the mean rate.
		AssertThat(e.TryGetProperty("quat", out _)).OverrideFailureMessage($"{where}: the emitter's Z is the cone's axis").IsFalse();
		Near($"{where} spin", Field(e, "spin", 0), Num(piece, "speed", 1));

		// count ÷ period a second, each living the period: the piece's GpuParticles amount. The bank's
		// rate is particles a FRAME: the guest's spawner adds rate × time scale × the step's frames
		// each update (`sub_82A0B0B8`, `sub_822698F8`), as Engine_FxSystem does.
		Near($"{where} rate", Param(e, "rate") * Fps * period, count);
		JsonElement[] keys = Keys(chain[0]);
		AssertThat(keys.Length).IsEqual(2);
		AssertThat(Frame(keys[1])).OverrideFailureMessage($"{where}: lives the period").IsEqual(life);
		double intensity = Num(piece, "intensity", 1);
		double[] c0 = Rgba(piece, "color", new[] { 1.0, 1, 1, 1 });
		double[] c1 = Rgba(piece, "colorEnd", new[] { c0[0], c0[1], c0[2], 0 });
		double size0 = Num(piece, "size", 0.5);
		double size1 = Num(piece, "sizeEnd", size0 * 0.4);
		(int src, int dst) = Blend(piece.TryGetProperty("blend", out JsonElement b) ? b.GetString()! : "add");
		for (int k = 0; k < 3; k++)
		{
			Near($"{where} colour[{k}]", Floats(keys[0], "color", new[] { 1.0, 1, 1 })[k], c0[k] * intensity);
			Near($"{where} end colour[{k}]", Floats(keys[1], "color", new[] { 1.0, 1, 1 })[k], c1[k]);
		}

		Near($"{where} alpha", Field(keys[0], "alpha", 1), c0[3]);
		Near($"{where} end alpha", Field(keys[1], "alpha", 1), c1[3]);
		Near($"{where} size", Field(keys[0], "sizeX", 1), size0);
		Near($"{where} end size", Field(keys[1], "sizeX", 1), size1);
		AssertThat(((int)Field(keys[0], "srcBlend", -1), (int)Field(keys[0], "dstBlend", -1)))
			.OverrideFailureMessage($"{where}: the piece's blend").IsEqual((src, dst));
	}

	private static void Arcs(string where, List<Link> chain, JsonElement piece)
	{
		AssertThat(chain.Count).OverrideFailureMessage($"{where}: a path and its emitter").IsEqual(2);
		double r0 = Num(piece, "radius", 1), r1 = Num(piece, "radiusTop", r0), height = Num(piece, "height", 2);
		double rate = Num(piece, "rate", 10), count = Num(piece, "count", 64), width = Num(piece, "width", 0.1);

		// The path: a turn and more round the cone, over the band the old arcs crawled (a start in
		// the lower 70%, a reach of 15–35% up), every point ON the cone; a fresh random point each step.
		JsonElement path = chain[0].Emitter;
		AssertThat(Field(path, "typeId", 0)).IsEqual(5.0);
		AssertThat(Flag(path, "splineRandomGenerator")).OverrideFailureMessage($"{where}: struck anywhere along it").IsTrue();
		double[][] points = path.GetProperty("splinePoints").EnumerateArray()
			.Select(p => p.EnumerateArray().Select(x => x.GetDouble()).ToArray()).ToArray();
		Near($"{where} band foot", points[0][2], 0.125 * height);
		Near($"{where} band top", points[^1][2], 0.825 * height);
		double turned = 0;
		for (int i = 0; i < points.Length; i++)
		{
			double t = points[i][2] / height;
			Near($"{where} point {i} on the cone", Math.Sqrt((points[i][0] * points[i][0]) + (points[i][1] * points[i][1])), r0 + ((r1 - r0) * t));
			if (i > 0)
			{
				double a = Math.Atan2(points[i][1], points[i][0]) - Math.Atan2(points[i - 1][1], points[i - 1][0]);
				turned += Math.Abs(Math.IEEERemainder(a, 2 * Math.PI));
			}
		}

		AssertThat(turned >= 2 * Math.PI).OverrideFailureMessage($"{where}: all the way round").IsTrue();

		// The strikes: count arcs re-struck rate times a second with 35% skipped, each standing
		// until the next strike.
		JsonElement e = chain[1].Emitter;
		Near($"{where} strikes a second", Param(e, "rate") * Fps, 0.65 * count * rate);
		JsonElement[] keys = Keys(chain[1]);
		int life = Frame(keys[^1]);
		AssertThat(Math.Abs((life / Fps) - (1 / rate)) <= 0.5 / Fps)
			.OverrideFailureMessage($"{where}: a strike stands {life} frames for 1/{rate} s").IsTrue();

		// The bolt: the old ribbon's width, the old chord's mean length, and the old ribbon's mean
		// light — colour × intensity × E[flash²] (flash uniform in 0.55..1, as colour and as alpha).
		double rMid = r0 + ((r1 - r0) * 0.475);
		double chord = Math.Sqrt(Math.Pow(0.25 * height, 2) + Math.Pow(0.25 * (r1 - r0), 2) + Math.Pow(rMid * Math.PI / 8, 2));
		double flash2 = (1 - Math.Pow(0.55, 3)) / (3 * 0.45);
		double intensity = Num(piece, "intensity", 1);
		double[] colour = Rgba(piece, "color", new[] { 1.0, 1, 1, 1 });
		foreach (JsonElement key in keys)
		{
			Near($"{where} width", Field(key, "sizeX", 1), width);
			Near($"{where} length", Field(key, "sizeY", 1), chord);
			double[] drawn = Floats(key, "colorSq", new[] { 1.0, 1, 1 });
			for (int k = 0; k < 3; k++)
			{
				Near($"{where} light[{k}]", drawn[k] * Field(key, "alpha", 1), colour[k] * intensity * flash2);
			}
		}
	}

	private static void Ring(string where, List<Link> chain, JsonElement piece)
	{
		AssertThat(chain.Count).IsEqual(1);
		JsonElement e = chain[0].Emitter;
		double period = Math.Max(0.2, Num(piece, "period", 1));
		double radius = Num(piece, "radius", 1);
		double small = Math.Max(0.05, radius * 0.1);
		int life = (int)Math.Round(period * Fps);
		Near($"{where} height", Floats(e, "pos", new[] { 0.0, 0, 0 })[2], Num(piece, "height", 2));
		Near($"{where} one a period", Param(e, "rate") * Fps * period, 1);
		Near($"{where} the first at once", Field(e, "initialNum", 0), 1);
		Near($"{where} stands still", Param(e, "speed"), 0);

		// The piece's own curve, key by key: radius lerp(small, radius, √t) and colour and alpha
		// both × (1 − t) — enough keys that the bank's straight spans follow the ease.
		JsonElement[] keys = Keys(chain[0]);
		AssertThat(Frame(keys[^1])).IsEqual(life);
		AssertThat(keys.Length >= Math.Min(5, life + 1)).OverrideFailureMessage($"{where}: {keys.Length} keys for an eased curve").IsTrue();
		double intensity = Num(piece, "intensity", 1);
		double[] colour = Rgba(piece, "color", new[] { 1.0, 1, 1, 1 });
		foreach (JsonElement key in keys)
		{
			double t = Frame(key) / (double)life;
			AssertThat(Flag(key, "hasAlignQuat")).OverrideFailureMessage($"{where}: lies flat").IsTrue();
			Near($"{where} size at {t:F2}", Field(key, "sizeX", 1), 2 * (small + ((radius - small) * Math.Sqrt(t))));
			Near($"{where} alpha at {t:F2}", Field(key, "alpha", 1), 1 - t);
			for (int k = 0; k < 3; k++)
			{
				Near($"{where} colour[{k}] at {t:F2}", Floats(key, "color", new[] { 1.0, 1, 1 })[k], colour[k] * intensity * (1 - t));
			}
		}

		for (double t = 0.05; t < 1; t += 0.05)
		{
			double f = t * life;
			int b = Array.FindIndex(keys, k => Frame(k) >= f);
			int a = Math.Max(0, b - 1);
			double fa = Frame(keys[a]), fb = Frame(keys[b]);
			double w = fb > fa ? (f - fa) / (fb - fa) : 0;
			double drawn = Field(keys[a], "sizeX", 1) + ((Field(keys[b], "sizeX", 1) - Field(keys[a], "sizeX", 1)) * w);
			double eased = 2 * (small + ((radius - small) * Math.Sqrt(t)));
			AssertThat(Math.Abs(drawn - eased) <= 0.08 * 2 * radius)
				.OverrideFailureMessage($"{where}: at {t:F2} the keys draw {drawn:F3}, the ease {eased:F3}").IsTrue();
		}
	}

	private static double[,] Mul(double[,] a, double[,] b)
	{
		var o = new double[3, 3];
		for (int r = 0; r < 3; r++)
		{
			for (int c = 0; c < 3; c++)
			{
				o[r, c] = (a[r, 0] * b[0, c]) + (a[r, 1] * b[1, c]) + (a[r, 2] * b[2, c]);
			}
		}

		return o;
	}

	private static double[,] AxisAngle(double[] axis, double angle)
	{
		double l = Math.Sqrt((axis[0] * axis[0]) + (axis[1] * axis[1]) + (axis[2] * axis[2]));
		double x = axis[0] / l, y = axis[1] / l, z = axis[2] / l, c = Math.Cos(angle), s = Math.Sin(angle), ic = 1 - c;
		return new[,]
		{
			{ c + (x * x * ic), (x * y * ic) - (z * s), (x * z * ic) + (y * s) },
			{ (y * x * ic) + (z * s), c + (y * y * ic), (y * z * ic) - (x * s) },
			{ (z * x * ic) - (y * s), (z * y * ic) + (x * s), c + (z * z * ic) },
		};
	}

	private static double[,] Rotation(double[] q)
	{
		double x = q[0], y = q[1], z = q[2], w = q[3];
		return new[,]
		{
			{ 1 - (2 * ((y * y) + (z * z))), 2 * ((x * y) - (z * w)), 2 * ((x * z) + (y * w)) },
			{ 2 * ((x * y) + (z * w)), 1 - (2 * ((x * x) + (z * z))), 2 * ((y * z) - (x * w)) },
			{ 2 * ((x * z) - (y * w)), 2 * ((y * z) + (x * w)), 1 - (2 * ((x * x) + (y * y))) },
		};
	}

	/// <summary>Fable II's Blades sword, as the cast's SpellShotDamoclesSword draws it (globals.gdb ModelFile).</summary>
	private const string BladesSword = "art\\inventory\\weapons\\dotxsi\\npc_sword_damocles\\npc_sword_damocles.mdl";

	/// <summary>
	/// The sword's own effects, by the ids the shipped bank holds: FX_Damocles_Trail's fxspl_damocles_trail
	/// (the record's FX entity, `sub_825A71E0`) and SwordContinueFX's fxspl_damocles_sword_continue
	/// (`sub_825A8310`), each emitter with its system.
	/// </summary>
	private static readonly (uint Emitter, uint System)[] SwordEffects =
	{
		(0x445DD110u, 0x17E73103u), (0x67B71EC8u, 0x5BDA9451u), (0xFE1598B0u, 0xB9ADDDE7u),
		(0xCCC1AADBu, 0x3854C338u), (0x17A034D9u, 0x9085FBA0u), (0x6C55D39Fu, 0xB1B4A838u),
	};

	private static uint Hex(JsonElement e, string key) =>
		e.TryGetProperty(key, out JsonElement v) ? Convert.ToUInt32(v.GetString()!.Replace("0x", string.Empty, StringComparison.Ordinal), 16) : 0u;

	/// <summary>The pull, then per blade its sword chain and its effects' orbital chain; returns how many chains it took.</summary>
	private static int Blades(string where, List<List<Link>> chains, int at, JsonElement piece)
	{
		int count = Math.Max(1, (int)Num(piece, "count", 64));
		double radius = Num(piece, "radius", 1), height = Num(piece, "height", 2), rise = Num(piece, "period", 1);
		double reachStep = Num(piece, "width", 0.1) * 0.25, turns = Num(piece, "speed", 1);
		double size = Num(piece, "size", 0.5);
		double tilt = Num(piece, "tilt", 20) * Math.PI / 180;
		double theta = 2 * Math.PI * turns / Fps;

		var pulls = new List<(double[] Pos, double Radius, double Strength)>();
		while (chains[at + pulls.Count][0].Emitter is var p && Field(p, "typeId", 0) == 4)
		{
			pulls.Add((Floats(p, "pos", new[] { 0.0, 0, 0 }), Param(p, "attractRadius"), Param(p, "attractStrength")));
		}

		AssertThat(pulls.Count > 0).OverrideFailureMessage($"{where}: the orbit's pull").IsTrue();
		int next = at + pulls.Count;
		for (int j = 0; j < count; j++)
		{
			double reach = radius + (j * reachStep);
			double phase = 2 * Math.PI * j / count;
			string who = $"{where} blade {j}";
			List<Link> chain = chains[next++];
			AssertThat(chain.Count).IsEqual(1);
			JsonElement e = chain[0].Emitter;
			AssertThat(Flag(e, "positionalInteractable")).OverrideFailureMessage($"{who}: pulled").IsTrue();
			AssertThat(Field(e, "maxActive", 0) == 1 && Field(e, "initialNum", 0) == 1 && Param(e, "rate") == 0 && !Flag(e, "useLife"))
				.OverrideFailureMessage($"{who}: one, for as long as it plays — never re-spawned").IsTrue();
			AssertThat(Field(chain[0].System!.Value, "loopCount", 0)).OverrideFailureMessage($"{who}: never dies").IsEqual(-1.0);

			// Fly it as the bank does: spawn velocity along the emitter's +Z, the pulls, the
			// damping, one step a frame.
			double[] pos = Floats(e, "pos", new[] { 0.0, 0, 0 });
			double[,] r = Rotation(Floats(e, "quat", new[] { 0.0, 0, 0, 1 }));
			double speed = Param(e, "speed") / Fps;
			double[] vel = { r[0, 2] * speed, r[1, 2] * speed, r[2, 2] * speed };
			double[] damping = Vec(e, "vec1", new[] { 1.0, 1, 1 });
			Near($"{who} start phase", Math.IEEERemainder(Math.Atan2(pos[1], pos[0]) - phase, 2 * Math.PI), 0, 1e-3);
			double start = pos[2], swept = 0, previous = Math.Atan2(pos[1], pos[0]);
			double nearest = double.MaxValue, farthest = 0;
			const int steps = 300;
			var path = new List<(double[] Pos, double[] Vel)>();
			for (int n = 0; n < steps; n++)
			{
				foreach ((double[] a, double reachOf, double strength) in pulls)
				{
					double dx = pos[0] - a[0], dy = pos[1] - a[1], dz = pos[2] - a[2];
					double d = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
					if (d <= reachOf)
					{
						double k = -strength * (1 - (d / reachOf)) / (Fps * Fps) / d;
						vel[0] += dx * k; vel[1] += dy * k; vel[2] += dz * k;
					}
				}

				for (int c = 0; c < 3; c++)
				{
					vel[c] *= damping[c];
					pos[c] += vel[c];
				}

				path.Add(((double[])pos.Clone(), (double[])vel.Clone()));
				double ring = Math.Sqrt((pos[0] * pos[0]) + (pos[1] * pos[1]));
				nearest = Math.Min(nearest, ring);
				farthest = Math.Max(farthest, ring);
				double angle = Math.Atan2(pos[1], pos[0]);
				swept += Math.IEEERemainder(angle - previous, 2 * Math.PI);
				previous = angle;
			}

			Near($"{who} nearest", nearest, reach, 0.005);
			Near($"{who} farthest", farthest, reach, 0.005);
			AssertThat(Math.Abs(pos[2] - start) < 1e-9).OverrideFailureMessage($"{who}: keeps its height").IsTrue();
			AssertThat(Math.Abs(start - (height + (j * rise))) <= 0.11)
				.OverrideFailureMessage($"{who}: height {start:F3}, the piece's {height + (j * rise):F3}").IsTrue();

			// Godot's RotateY(+turns) is clockwise in the guest's Z-up frame.
			Near($"{who} turns a second", -swept / (2 * Math.PI) / (steps / Fps), turns, 0.005);

			// Fable II's own Blades sword, at the cast's size, laid along its orbit: born under the
			// emitter's rotation × the align, then turned about the vertical (the axis stated in the
			// root's frame) by the orbit's rate a frame — so at every frame its tip (the model's −Y)
			// points along its circle where it is, the way it flies, climbing by the tilt, and the
			// flat of the blade (the model's +Z) faces out from the vortex.
			JsonElement key = Keys(chain[0])[0];
			Near($"{who} scale", Field(key, "trailWidth", 1), size);
			AssertThat(key.GetProperty("meshPath").GetString()).IsEqual(BladesSword);
			AssertThat(Flag(key, "hasOrient") && key.TryGetProperty("orRandAxis", out JsonElement random) && !random.GetBoolean())
				.OverrideFailureMessage($"{who}: turns about a stated axis").IsTrue();
			AssertThat(Flag(key, "orRandAngle")).OverrideFailureMessage($"{who}: born at its stated angle").IsFalse();
			AssertThat(Flag(key, "hasMeshAlign")).OverrideFailureMessage($"{who}: laid along its orbit").IsTrue();
			Near($"{who} turns with its orbit", Field(key, "orSpeed", 0), theta);
			double[] axis = Floats(key, "orAxis", new[] { 1.0, 0, 0 });
			double[,] born = Mul(r, Rotation(Floats(key, "meshAlign", new[] { 0.0, 0, 0, 1 })));
			foreach (int frame in new[] { 1, 8, 30, 95, 299 })
			{
				double[,] drawn = Mul(AxisAngle(axis, Field(key, "orSpeed", 0) * frame), born);
				double[] here = path[frame - 1].Pos;
				double rl = Math.Sqrt((here[0] * here[0]) + (here[1] * here[1]));
				double[] forward = { Math.Cos(tilt) * here[1] / rl, -Math.Cos(tilt) * here[0] / rl, Math.Sin(tilt) };
				double[] outward = { here[0] / rl, here[1] / rl, 0 };
				for (int c = 0; c < 3; c++)
				{
					Near($"{who} tip[{c}] at frame {frame}", -drawn[c, 1], forward[c], 0.03);
					Near($"{who} flat[{c}] at frame {frame}", drawn[c, 2], outward[c], 0.03);
				}
			}

			// The sword's effects ride an orbital in step with it: the same circle about the
			// vertical through the vortex, the same start, the same turn a frame (positions after
			// each step: COrbitalEntityType turns before it places, Engine_FxSystem.UpdateChain).
			List<Link> fx = chains[next++];
			JsonElement orbital = fx[0].Emitter;
			AssertThat(Field(orbital, "typeId", 0)).OverrideFailureMessage($"{who}: an orbital carries the sword's effects").IsEqual(3.0);
			double[] centre = Floats(orbital, "pos", new[] { 0.0, 0, 0 });
			double[,] ro = Rotation(Floats(orbital, "quat", new[] { 0.0, 0, 0, 1 }));
			double orbitRadius = Param(orbital, "p25"), rate = Field(orbital, "angularRotationRate", 0);
			foreach (int frame in new[] { 1, 30, 150, 299 })
			{
				double angle = rate * frame;
				double bx = centre[0] + (ro[0, 1] * orbitRadius), by = centre[1] + (ro[1, 1] * orbitRadius);
				double ox = (Math.Cos(angle) * bx) - (Math.Sin(angle) * by), oy = (Math.Sin(angle) * bx) + (Math.Cos(angle) * by);
				double[] sword = path[frame - 1].Pos;
				double off = Math.Sqrt(Math.Pow(ox - sword[0], 2) + Math.Pow(oy - sword[1], 2) + Math.Pow(centre[2] - sword[2], 2));
				AssertThat(off < 0.03).OverrideFailureMessage($"{who}: the effects are {off:F3} m off the sword at frame {frame}").IsTrue();
			}

			var stated = fx.Skip(1).Select(l => (Hex(l.Emitter, "entityId"), Hex(l.System!.Value, "id"))).ToArray();
			AssertThat(stated.SequenceEqual(SwordEffects)).OverrideFailureMessage($"{who}: the Blades sword's own effects").IsTrue();
		}

		return next - at;
	}

	private static void Light(string where, List<Link> chain, JsonElement light)
	{
		AssertThat(chain.Count).IsEqual(1);
		JsonElement e = chain[0].Emitter;
		AssertThat(Field(e, "typeId", 0)).IsEqual(7.0);
		Near($"{where} height", Floats(e, "pos", new[] { 0.0, 0, 0 })[2], light.GetProperty("height").GetDouble());
		Near($"{where} range", Param(e, "lightRange"), light.GetProperty("range").GetDouble());
		Near($"{where} intensity", Param(e, "lightIntensity"), light.GetProperty("energy").GetDouble() / 3);
		double[] colour = Rgba(light, "color", new[] { 1.0, 1, 1, 1 });
		double[] stated = Vec(e, "lightColour", new[] { 0.0, 0, 0 });
		for (int k = 0; k < 3; k++)
		{
			Near($"{where} colour[{k}]", stated[k], colour[k]);
		}

		// The old wave, 0.6 sin 23t + 0.4 sin 7.3t on the energy; the bank's walk steps
		// (rand − phase) × p21 each frame — a low-pass at 23 rad/s — and lights
		// 1 + phase × p20: p20 scales the walk's spread to the wave's.
		double flicker = Num(light, "flicker", 0);
		double walk = 1 - Math.Exp(-23 / Fps);
		double waveSpread = Math.Sqrt(((0.6 * 0.6) + (0.4 * 0.4)) / 2);
		double walkSpread = Math.Sqrt((walk / 3) / (2 - walk));
		Near($"{where} radius steady", Param(e, "p19"), 0);
		Near($"{where} flicker rate", Param(e, "p21"), flicker > 0 ? walk : 0);
		Near($"{where} flicker depth", Param(e, "p20"), flicker * waveSpread / walkSpread);
	}
}
