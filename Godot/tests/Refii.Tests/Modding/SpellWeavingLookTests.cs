namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// SPELL WEAVING'S LOOKS FOLLOW FABLE III'S WOVEN EFFECTS. Each pair's effect in fx/effects/*.json
/// is built as Fable III builds that pair's look (its particle bank, read for structure only), drawn
/// with Fable II's own textures; pinned here through the bank's own model of what each entity does
/// per 30 Hz step (<c>Engine_FxSystem</c>). Pure maths on the JSON; no Godot type.
/// <list type="bullet">
/// <item>twister (Vortex + Fire, Shock, Force Push, Chaos; fxspl_vortex_*_twister) — a narrow column:
/// wind sheets shot up at 3 m/s plus up to 3.5, accelerating at 3 m/s², off an emitter an orbital
/// swings round a 0.3 m circle at 7.33 rad/s, small at birth, a 2 m sheet at 1.2 s, a wide faint one
/// at 2 s; bright bits flung from a disc at its foot; a point light low in it;</item>
/// <item>blade burn (Blades + Fire, Shock, Force Push, Chaos; fxspl_swords_*_blades) — a burn riding
/// the blade: a spline along the sword's blade in the sword's own frame, its emitters chained to it, every sprite a
/// fraction of a metre, no light;</item>
/// <item>fireball (Fireball + Shock, Force Push, Chaos; fxspl_enflame_*) — the shot's own look, every
/// emitter on the shot, a point light at its heart;</item>
/// <item>burst (Shock + Force Push, Shock + Chaos, Force Push + Chaos; fxspl_lightning_push) — one
/// smoke burst blown out over a 2 m disc round the victim each play;</item>
/// <item>Vortex + Blades has no Fable III look of its own (its twister is the vortex's plain dust):
/// FABLE II'S OWN BLADES SWORDS (SpellShotDamoclesSword's model) ORBITING one attractor at the old
/// piece's turn rate and reach (the vertical step frozen by <c>vec1.z</c> 0), each laid along its
/// orbit — tip first along the way it flies, climbing by the piece's tilt, the flat facing out
/// (<c>meshAlign</c> under the emitter's rotation, turned about the vertical with the orbit) — and
/// each carrying the sword's own effects (fxspl_damocles_trail, fxspl_damocles_sword_continue, the
/// bank's own emitters) on an orbital in step with it, under the bank's point light with its flicker
/// walk (p19/p20/p21) set to the piece's flicker.</item>
/// </list>
/// </summary>
[TestSuite]
public class SpellWeavingLookTests
{
	private const double Fps = 30.0;
	private const double Gravity = 9.8;

	/// <summary>Vortex + Blades' old piece and light, as fx/recipes.json stated them (meshes left out: the cutlass).</summary>
	private static readonly Dictionary<string, string> Old = new(StringComparer.Ordinal)
	{
		["spell_weaving_vortex_blades"] = """{"light":{"color":[0.8,0.85,1.0],"energy":3,"range":8,"height":1.5,"flicker":0.1},"pieces":[{"kind":"blades","texture":"fxt_democles_aura","color":[0.75,0.85,1.0],"count":5,"radius":1.2,"height":1.0,"speed":1.0,"tilt":15,"size":1.0,"width":1.0,"intensity":2.0,"period":0.3}]}""",
	};

	[TestCase] public void VortexBlades() => Check("spell_weaving_vortex_blades");
	[TestCase] public void VortexFire() => Twister("spell_weaving_vortex_fire");
	[TestCase] public void VortexShock() => Twister("spell_weaving_vortex_shock");
	[TestCase] public void VortexPush() => Twister("spell_weaving_vortex_push");
	[TestCase] public void VortexChaos() => Twister("spell_weaving_vortex_chaos");
	[TestCase] public void BladesFire() => BladeBurn("spell_weaving_blades_fire");
	[TestCase] public void BladesShock() => BladeBurn("spell_weaving_blades_shock");
	[TestCase] public void BladesChaos() => BladeBurn("spell_weaving_blades_chaos");
	[TestCase] public void FireShock() => Fireball("spell_weaving_fire_shock");
	[TestCase] public void FireChaos() => Fireball("spell_weaving_fire_chaos");
	[TestCase] public void ShockPush() => Burst("spell_weaving_shock_push");
	[TestCase] public void ShockChaos() => Burst("spell_weaving_shock_chaos");
	[TestCase] public void PushChaos() => Burst("spell_weaving_push_chaos");

	/// <summary>Fable III's own sheets the weaves draw with that Fable II has no copy of: never named by the mod.</summary>
	private static readonly string[] FableThreeOnly =
	{
		"flames_02", "fxt_basic_mulche_01", "fxt_circular_edge_smoke", "fxt_displacement_orb_01", "fxt_explosion_fire_05",
		"fxt_fabric_scrap_glowing", "fxt_fire_section_01", "fxt_fire_section_02", "fxt_fire_sharp_01", "fxt_fire_soft_glow",
		"fxt_flowing_smoke", "fxt_flowing_smoke_thick", "fxt_glow_ring", "fxt_lightning_clouds", "fxt_lightning_vertical_blue_01",
		"fxt_lightning_vertical_f3_01", "fxt_shimmer_ring_01", "fxt_smoke_highlight_02_p", "fxt_smoke_pillar_round_additive",
		"fxt_snow_chunks", "fxt_soft_glow_cyan", "fxt_twister_wind_01", "fxt_water_splash_01", "fxt_bird_poo_splat",
		"fxt_smokepuff_cloud_grey", "fxt_electric_spark", "fxt_breadcrumb_sparkle", "fxt_snow_spread_01", "fxt_firefly",
		"fxt_lightning_surround_01",
	};

	[TestCase]
	public void EveryLookDrawsFableTwosOwnTextures()
	{
		if (ModFolder() is not { } folder)
		{
			return;
		}

		foreach (string file in Directory.GetFiles(Path.Combine(folder, "fx", "effects"), "*.json"))
		{
			using JsonDocument effect = JsonDocument.Parse(File.ReadAllText(file));
			foreach (JsonElement system in effect.RootElement.GetProperty("systems").EnumerateArray())
			{
				foreach (JsonElement visual in system.GetProperty("visuals").EnumerateArray())
				{
					if (!visual.TryGetProperty("texture", out JsonElement texture))
					{
						continue;
					}

					string path = texture.GetString()!;
					AssertThat(path.StartsWith("art\\fx\\pfx_textures\\fxt_", StringComparison.Ordinal))
						.OverrideFailureMessage($"{Path.GetFileName(file)}: {path} is no Fable II effect sheet").IsTrue();
					string leaf = Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
					AssertThat(FableThreeOnly.Contains(leaf)).OverrideFailureMessage($"{Path.GetFileName(file)}: {leaf} is Fable III's").IsFalse();
				}
			}
		}
	}

	private static List<List<Link>>? Load(string name)
	{
		if (ModFolder() is not { } folder)
		{
			return null;  // a packaged test run without the source tree
		}

		// The elements are read before the document goes: clone the root.
		JsonElement root = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "fx", "effects", name + ".json"))).RootElement.Clone();
		return Chains(root);
	}

	private static double Type(JsonElement e) => Field(e, "typeId", 0);

	private static double Z(JsonElement e) => Floats(e, "pos", new[] { 0.0, 0, 0 })[2];

	/// <summary>A key's sprite as drawn: its size times its global scale, the larger side.</summary>
	private static double Drawn(JsonElement key)
	{
		double[] gs = Floats(key, "globalScale", new[] { 1.0, 1 });
		return Math.Max(Field(key, "sizeX", 1) * gs[0], Field(key, "sizeY", 1) * gs[1]);
	}

	private static void Twister(string name)
	{
		if (Load(name) is not { } chains)
		{
			return;
		}

		var orbitals = chains.SelectMany(c => c).Select(l => l.Emitter).Where(e => Type(e) == 3).Distinct().ToList();
		AssertThat(orbitals.Count > 0).OverrideFailureMessage($"{name}: the column swings on an orbital").IsTrue();
		JsonElement orbital = orbitals[0];
		Near($"{name} orbital turn (rad/s)", Field(orbital, "angularRotationRate", 0) * Fps, 7.33, 0.01);
		Near($"{name} orbital radius", Param(orbital, "p25"), 0.3);

		// The body: the chain under the orbital whose emitter accelerates up.
		List<Link>? body = chains.FirstOrDefault(c => c.Count == 2 && Type(c[0].Emitter) == 3 && c[1].System is not null &&
			Vec(c[1].Emitter, "vec0", new[] { 0.0, 0, 0 })[2] > 0);
		AssertThat(body is not null).OverrideFailureMessage($"{name}: no wind column under the orbital").IsTrue();
		JsonElement column = body![1].Emitter;
		AssertThat(Flag(column, "attachToRoot")).IsTrue();
		Near($"{name} column speed", Param(column, "speed"), 3);
		Near($"{name} column speed spread", Param(column, "speedSpread"), 3.5);
		double lift = Vec(column, "vec0", new[] { 0.0, 0, 0 })[2];
		Near($"{name} column lift", lift, 3);
		AssertThat(Param(column, "rate") >= 1.3).OverrideFailureMessage($"{name}: a dense column, 40 sheets a second or more").IsTrue();

		JsonElement[] keys = Keys(body[1]);
		AssertThat(keys.Length).IsEqual(3);
		Near($"{name} sheet at birth", Field(keys[0], "sizeX", 1), 0.4);
		Near($"{name} sheet at 1.2 s", Field(keys[1], "sizeX", 1), 2);
		Near($"{name} sheet at 2 s", Field(keys[2], "sizeX", 1), 3);
		AssertThat(Math.Abs(Frame(keys[2]) - 60) <= 1).OverrideFailureMessage($"{name}: a sheet lives 2 s").IsTrue();

		// The slowest sheet's height at the 1.2 s key: the column stands five metres and more.
		double t = Frame(keys[1]) / Fps;
		double height = Z(column) + (Param(column, "speed") * t) + (0.5 * lift * t * t);
		AssertThat(height >= 5).OverrideFailureMessage($"{name}: the column reaches {height:F2} m by its 1.2 s key").IsTrue();
		AssertThat(Field(keys[2], "alpha", 1) < Field(keys[1], "alpha", 1)).OverrideFailureMessage($"{name}: the top fades").IsTrue();

		// The foot: a small disc flinging bright bits.
		JsonElement foot = chains[0][0].Emitter;
		AssertThat(Field(foot, "subType", 0)).IsEqual(2.0);
		AssertThat(Field(foot, "spawnRadius", 0) <= 0.2).OverrideFailureMessage($"{name}: the foot is narrow").IsTrue();
		AssertThat(Z(foot) < 0.5).IsTrue();

		var lights = chains.SelectMany(c => c).Select(l => l.Emitter).Where(e => Type(e) == 7).ToList();
		AssertThat(lights.Count).OverrideFailureMessage($"{name}: one light").IsEqual(1);
		AssertThat(Z(lights[0]) < 1).OverrideFailureMessage($"{name}: the light sits low in the column").IsTrue();
	}

	/// <summary>Where the Blades sword's blade begins (its guard) and ends, along its own +Y (metres).</summary>
	private const double BladeGuard = 0.08;
	private const double BladeTip = 0.89;

	private static void BladeBurn(string name)
	{
		if (Load(name) is not { } chains)
		{
			return;
		}

		var splines = chains.SelectMany(c => c).Select(l => l.Emitter).Where(e => Type(e) == 5).Distinct().ToList();
		AssertThat(splines.Count).OverrideFailureMessage($"{name}: one spline down the blade").IsEqual(1);
		double[][] points = splines[0].GetProperty("splinePoints").EnumerateArray()
			.Select(p => p.EnumerateArray().Select(x => x.GetDouble()).ToArray()).ToArray();
		AssertThat(points.Length).IsEqual(2);
		foreach (double[] point in points)
		{
			// Played on the sword, the look stands in the sword's own frame (Engine_FxPlayer.InEntityFrame;
			// classic: the instance's orientation from the placement, mod_commands.cpp guest_fx), and
			// npc_sword_damocles.mdl's blade runs along its own +Y: the guard at 0.08, the tip at 0.89,
			// its .mdl.gmd Weapon.Weapon.Hold at -0.083 and Weapon.Weapon.Top at 0.847.
			AssertThat(Math.Abs(point[0]) < 1e-6 && Math.Abs(point[2]) < 1e-6).OverrideFailureMessage($"{name}: the spline lies along the blade").IsTrue();
			AssertThat(point[1] >= BladeGuard - 1e-6 && point[1] <= BladeTip + 1e-6).OverrideFailureMessage($"{name}: {point[1]} is off the blade").IsTrue();
		}

		AssertThat(points.Max(p => p[1]) - points.Min(p => p[1]) >= 0.75).OverrideFailureMessage($"{name}: the burn runs the blade's length").IsTrue();
		List<Link> burn = chains.First(c => c.Count == 2 && Type(c[0].Emitter) == 5);
		AssertThat(burn[1].System is not null).IsTrue();
		AssertThat(chains.SelectMany(c => c).Any(l => Type(l.Emitter) == 7)).OverrideFailureMessage($"{name}: a blade casts no light").IsFalse();

		// Compact: every sprite riding the blade is a fraction of a metre (the old look was a 2-3 m bonfire).
		foreach (Link link in chains.Where(c => Type(c[0].Emitter) == 5).Select(c => c[^1]).Where(l => l.System is not null))
		{
			foreach (JsonElement key in Keys(link))
			{
				AssertThat(Drawn(key) <= 1.0 + 1e-6).OverrideFailureMessage($"{name}: a {Drawn(key):F2} m sprite on the blade").IsTrue();
			}
		}
	}

	private static void Fireball(string name)
	{
		if (Load(name) is not { } chains)
		{
			return;
		}

		foreach (JsonElement e in chains.SelectMany(c => c).Select(l => l.Emitter))
		{
			double[] pos = Floats(e, "pos", new[] { 0.0, 0, 0 });
			AssertThat(Math.Sqrt((pos[0] * pos[0]) + (pos[1] * pos[1]) + (pos[2] * pos[2])) <= 0.2)
				.OverrideFailureMessage($"{name}: every emitter rides the shot").IsTrue();
			AssertThat(Field(e, "spawnRadius", 0) <= 0.1 + 1e-6).OverrideFailureMessage($"{name}: born on the ball").IsTrue();
		}

		var lights = chains.SelectMany(c => c).Select(l => l.Emitter).Where(e => Type(e) == 7).ToList();
		AssertThat(lights.Count).OverrideFailureMessage($"{name}: a light at its heart").IsEqual(1);
		AssertThat(chains.Any(c => c.Count == 1 && Flag(c[0].Emitter, "attachToRoot") && c[0].System is not null))
			.OverrideFailureMessage($"{name}: a core travelling with the shot").IsTrue();
	}

	private static void Burst(string name)
	{
		if (Load(name) is not { } chains)
		{
			return;
		}

		JsonElement smoke = chains[0][0].Emitter;
		AssertThat(Field(smoke, "subType", 0)).IsEqual(2.0);
		Near($"{name} disc radius", Field(smoke, "spawnRadius", 0), 2);
		AssertThat(Flag(smoke, "shapeFlagA") && Flag(smoke, "shapeFlagB")).OverrideFailureMessage($"{name}: blown out from the disc's edge").IsTrue();
		AssertThat(Field(smoke, "initialNum", 0)).IsEqual(10.0);
		AssertThat(Param(smoke, "rate")).IsEqual(0.0);
		AssertThat(Flag(smoke, "useLife") && Field(smoke, "lifetimeFrames", 0) == 15).OverrideFailureMessage($"{name}: one burst a play").IsTrue();
		JsonElement[] keys = Keys(chains[0][0]);
		AssertThat(Field(keys[^1], "alpha", 1)).IsEqual(0.0);
	}

	/// <summary>The effect's chains in file order: each chain's entities, parent first, with their systems.</summary>
	private static List<List<Link>> Chains(JsonElement root)
	{
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

		return chains;
	}

	/// <summary>
	/// The Blades swords' partner tints (mod/init.lua kSwordTintDefaults): settings.json states every
	/// lane, and each equals the default the Lua falls back to. Red, blue, violet and a dark steel.
	/// </summary>
	[TestCase]
	public void SwordTintsAreStatedAndAgree()
	{
		string? mod = ModFolder();
		if (mod == null)
		{
			return;
		}

		using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(mod, "settings.json")));
		JsonElement weaving = settings.RootElement.GetProperty("spell_weaving");
		string woven = File.ReadAllText(Path.Combine(mod, "mod", "init.lua"));
		var tints = new Dictionary<string, float[]>(StringComparer.Ordinal);
		foreach (string name in new[] { "fire", "shock", "chaos", "push" })
		{
			System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(woven,
				@"\[k\w+\] = \{ """ + name + @""", ([0-9.]+), ([0-9.]+), ([0-9.]+) \},");
			AssertThat(m.Success).OverrideFailureMessage($"mod/init.lua states no default tint for {name}").IsTrue();
			var lanes = new float[3];
			for (int i = 0; i < 3; i++)
			{
				lanes[i] = float.Parse(m.Groups[i + 1].Value, System.Globalization.CultureInfo.InvariantCulture);
				float stated = weaving.GetProperty($"sword_tint_{name}_{"rgb"[i]}").GetSingle();
				AssertThat(Math.Abs(stated - lanes[i]) < 1e-5f).OverrideFailureMessage($"sword_tint_{name}_{"rgb"[i]}: settings {stated}, lua {lanes[i]}").IsTrue();
				// Under the modern look's cap (31.875 / 4), never negative.
				AssertThat(lanes[i] >= 0f && lanes[i] <= 31.875f / 4f).IsTrue();
			}

			tints[name] = lanes;
		}

		AssertThat(tints["fire"][0] > 4f * tints["fire"][1] && tints["fire"][0] > 4f * tints["fire"][2]).IsTrue();
		AssertThat(tints["shock"][2] > 2f * tints["shock"][0] && tints["shock"][2] > 2f * tints["shock"][1]).IsTrue();
		AssertThat(tints["chaos"][2] > 2f * tints["chaos"][0] && tints["chaos"][0] > tints["chaos"][1]).IsTrue();
		AssertThat(tints["push"].All(l => l < 0.6f)).IsTrue();
	}

	private static string? ModFolder()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 16 && at != null; i++)
		{
			string candidate = Path.Combine(at, "Fable2", "mods-src", "spell_weaving");
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
		List<List<Link>> chains = Chains(effect.RootElement);
		int at = 0;
		foreach (JsonElement piece in old.RootElement.GetProperty("pieces").EnumerateArray())
		{
			string kind = piece.GetProperty("kind").GetString()!;
			string where = $"{name} piece {at} ({kind})";
			switch (kind)
			{
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
			.OverrideFailureMessage($"{what}: {actual:R}, the look says {expected:R}")
			.IsTrue();
	}

	// ---- the vortex's blades -----------------------------------------------------------------

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
