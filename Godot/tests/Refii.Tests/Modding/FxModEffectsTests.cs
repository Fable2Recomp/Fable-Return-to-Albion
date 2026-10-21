namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Fx;
using Refii.Mods;

/// <summary>
/// Mod effects (fx/effects/*.json): the file form of the bank's own model round-trips — against
/// every effect of the shipped bank when the tree has it, and against constructed models always —
/// the overlay follows its rules, the validator is the reader's verdict, a play_fx request parses,
/// and the API surfaces agree with the catalog. No Godot type is constructed.
/// </summary>
[TestSuite]
public class FxModEffectsTests
{
	private static string? FindUp(params string[] parts)
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 16 && at != null; i++)
		{
			string candidate = Path.Combine(new[] { at }.Concat(parts).ToArray());
			if (File.Exists(candidate))
			{
				return candidate;
			}

			at = Path.GetDirectoryName(at);
		}

		return null;
	}

	/// <summary>A failure message gdUnit accepts (it refuses an empty one).</summary>
	private static string Msg(string text) => text.Length == 0 ? "(no detail)" : text;

	private static string? RepoRoot() =>
		FindUp("src", "vfs", "include", "core", "refii_mod.h") is { } header
			? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(header)!, "..", "..", "..", ".."))
			: null;

	// ---- equality by behaviour --------------------------------------------------------------

	private static readonly int[] Frames = Enumerable.Range(-3, 400).ToArray();

	private static string? Differ(object? a, object? b, string at)
	{
		switch (a, b)
		{
			case (null, null):
				return null;
			case (FxTimeline x, FxTimeline y):
			{
				if (x.Keys.Count != y.Keys.Count || BitConverter.SingleToInt32Bits(x.DefaultValue) != BitConverter.SingleToInt32Bits(y.DefaultValue))
				{
					return $"{at}: keys {x.Keys.Count}/{y.Keys.Count} default {x.DefaultValue}/{y.DefaultValue}";
				}

				for (int i = 0; i < x.Keys.Count; i++)
				{
					if (!x.Keys[i].Equals(y.Keys[i]))
					{
						return $"{at}: key {i}";
					}
				}

				foreach (int f in Frames)
				{
					float vx = x.EvalFrame(f), vy = y.EvalFrame(f);
					if (BitConverter.SingleToInt32Bits(vx) != BitConverter.SingleToInt32Bits(vy) && !(float.IsNaN(vx) && float.IsNaN(vy)))
					{
						return $"{at}: frame {f} {vx} != {vy}";
					}
				}

				return null;
			}

			case (FxVectorTimeline x, FxVectorTimeline y):
			{
				if (x.Keys.Count != y.Keys.Count || !x.DefaultValue.SequenceEqual(y.DefaultValue))
				{
					return $"{at}: vector keys {x.Keys.Count}/{y.Keys.Count}";
				}

				foreach (int f in Frames)
				{
					if (x.EvalFrame(f) != y.EvalFrame(f))
					{
						return $"{at}: vector frame {f}";
					}
				}

				return null;
			}

			case (FxTimeline[] x, FxTimeline[] y):
				return x.Length != y.Length ? $"{at}: {x.Length} parameters" : x.Zip(y).Select((p, i) => Differ(p.First, p.Second, $"{at}[{i}]")).FirstOrDefault(d => d != null);
			case (float[] x, float[] y):
				return x.Length == y.Length && x.Zip(y).All(p => BitConverter.SingleToInt32Bits(p.First) == BitConverter.SingleToInt32Bits(p.Second)) ? null : $"{at}: [{string.Join(",", x)}] != [{string.Join(",", y)}]";
			case (float[][] x, float[][] y):
				return x.Length != y.Length ? $"{at}: {x.Length} rows" : x.Zip(y).Select((p, i) => Differ(p.First, p.Second, $"{at}[{i}]")).FirstOrDefault(d => d != null);
			case (float x, float y):
				return BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y) ? null : $"{at}: {x} != {y}";
			case (FxSystemDef x, FxSystemDef y):
				return Fields(x, y, at) ?? (x.Visuals.Count != y.Visuals.Count
					? $"{at}: {x.Visuals.Count} visuals"
					: x.Visuals.Zip(y.Visuals).Select((p, i) => Fields(p.First, p.Second, $"{at}.visuals[{i}]")).FirstOrDefault(d => d != null));
			case (FxEmitter x, FxEmitter y):
				return Fields(x, y, at);
			default:
				return Equals(a, b) ? null : $"{at}: {a} != {b}";
		}
	}

	/// <summary>Every public field compared: the file form must carry all of them, derived ones included.</summary>
	private static string? Fields(object x, object y, string at)
	{
		foreach (FieldInfo field in x.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
		{
			if (Differ(field.GetValue(x), field.GetValue(y), $"{at}.{field.Name}") is { } d)
			{
				return d;
			}
		}

		return null;
	}

	// ---- the file form ------------------------------------------------------------------------

	[TestCase]
	public void ShippedBankRoundTripsThroughTheFileForm()
	{
		string? path = FindUp("assets", "game", "data", "art", "particles", "particle_bank.bnk");
		if (path is null)
		{
			return;  // a tree without the player's game data
		}

		var bank = new FxBank();
		AssertThat(Engine_FxBank.Parse(File.ReadAllBytes(path), bank)).IsTrue();
		int checkedEffects = 0;
		foreach (FxEffect effect in bank.Effects)
		{
			Engine_FxEffectJson.Definition d = Engine_FxEffectJson.Extract(bank, effect);
			string text = Engine_FxEffectJson.Serialize(d);
			var issues = new List<Engine_FxEffectJson.Issue>();
			List<Engine_FxEffectJson.Definition> back = Engine_FxEffectJson.Parse(text, issues);
			AssertThat(issues.Any(i => i.Error)).OverrideFailureMessage($"{d.Name}: {string.Join("; ", issues.Select(i => i.Pointer + " " + i.Message))}").IsFalse();
			AssertThat(back.Count).IsEqual(1);
			AssertThat(Engine_FxEffectJson.Serialize(back[0])).IsEqual(text);

			// Against the BANK's own objects, part by part: the file form loses nothing the renderer reads.
			int p = 0;
			foreach (FxEffectPart part in effect.Parts.Where(part => part.Emitter >= 0 && part.Emitter < bank.Emitters.Count))
			{
				FxEffectPart local = back[0].Parts[p++];
				AssertThat(local.Group).IsEqual(part.Group);
				AssertThat(local.Vector).IsEqual(part.Vector);
				string? differs = Differ(bank.Emitters[part.Emitter], back[0].Emitters[local.Emitter], $"{d.Name}.emitter");
				if (differs is null && part.System >= 0)
				{
					differs = Differ(bank.Systems[part.System], back[0].Systems[local.System], $"{d.Name}.system");
				}

				if (differs is null && part.TrailSystem >= 0)
				{
					differs = Differ(bank.Systems[part.TrailSystem], back[0].Systems[local.TrailSystem], $"{d.Name}.trail");
				}

				AssertThat(differs).OverrideFailureMessage(Msg(differs ?? string.Empty)).IsNull();
			}

			checkedEffects++;
		}

		AssertThat(checkedEffects).IsEqual(bank.Effects.Count);
	}

	private static Engine_FxEffectJson.Definition Constructed()
	{
		var d = new Engine_FxEffectJson.Definition { Name = "my_mod_green_torch", UpdateFrequency = 1, Scale = 1.5f, Tint = new[] { 0.5f, 1f, 0.25f } };
		var e = new FxEmitter { TypeId = 0, SubType = 1, MaxActive = 40, InitialNum = 3, SpawnRadius = 0.25f, EmitOnSurface = true, AttachToRoot = true };
		e.Pos = new[] { 0f, 0.5f, 1f };
		e.Fades = new[] { 1f, 2f, 30f, 40f };
		for (int i = 0; i < 28; i++)
		{
			e.Generic[i] = Engine_FxBank.MakeTimeline(i, i switch
			{
				2 => new[] { (0f, 12f), (1f, 3f) },
				4 => new[] { (0f, 1.5f) },
				7 => new[] { (0.5f, 1f), (0f, 0.25f), (2f, 0f) },
				_ => Array.Empty<(float, float)>(),
			});
		}

		e.Vec1 = Engine_FxBank.MakeVecTimeline(1, new[] { (0f, 0.2f, 1f, 0.3f), (1f, 0f, 0.5f, 0f) });
		e.Vec0 = Engine_FxBank.MakeVecTimeline(0, Array.Empty<(float, float, float, float)>());
		e.Vec2 = Engine_FxBank.MakeVecTimeline(2, Array.Empty<(float, float, float, float)>());
		e.LightColour = Engine_FxBank.MakeVecTimeline(3, Array.Empty<(float, float, float, float)>());
		Engine_FxBank.LinkParameters(e);

		var light = new FxEmitter { TypeId = 7, EntityId = 0x1234ABCDu, HaloTextureHash = 0xDEADBEEFu };
		for (int i = 0; i < 28; i++)
		{
			light.Generic[i] = Engine_FxBank.MakeTimeline(i, i == 15 ? new[] { (0f, 6f) } : Array.Empty<(float, float)>());
		}

		light.Vec0 = Engine_FxBank.MakeVecTimeline(0, Array.Empty<(float, float, float, float)>());
		light.Vec1 = Engine_FxBank.MakeVecTimeline(1, Array.Empty<(float, float, float, float)>());
		light.Vec2 = Engine_FxBank.MakeVecTimeline(2, Array.Empty<(float, float, float, float)>());
		light.LightColour = Engine_FxBank.MakeVecTimeline(3, new[] { (0f, 0.2f, 1f, 0.1f) });
		Engine_FxBank.LinkParameters(light);

		var spline = new FxEmitter { TypeId = 5, SplinePoints = new[] { new[] { 0f, 0f, 0f }, new[] { 1f, 2f, 3f } }, SplineRate = 0.5f, SplineLooped = true };
		for (int i = 0; i < 28; i++)
		{
			spline.Generic[i] = Engine_FxBank.MakeTimeline(i, Array.Empty<(float, float)>());
		}

		spline.Vec0 = Engine_FxBank.MakeVecTimeline(0, Array.Empty<(float, float, float, float)>());
		spline.Vec1 = Engine_FxBank.MakeVecTimeline(1, Array.Empty<(float, float, float, float)>());
		spline.Vec2 = Engine_FxBank.MakeVecTimeline(2, Array.Empty<(float, float, float, float)>());
		spline.LightColour = Engine_FxBank.MakeVecTimeline(3, Array.Empty<(float, float, float, float)>());
		Engine_FxBank.LinkParameters(spline);

		var system = new FxSystemDef { Id = 0, Mode = 1, LoopCount = -1, LastStartFrames = 30, ChildCount = 1 };
		system.Visuals.Add(new FxVisual
		{
			HasTexture = true, Texture = "art/fx/pfx_textures/flame01.tex", TexCols = 4, TexRows = 4, FrameSpeed = 15f,
			HasColor = true, Color = new[] { 0.5f, 1f, 0.5f }, ColorSq = new[] { 0.25f, 1f, 0.25f }, HasSize = true, SizeX = 0.4f, SizeY = 0.6f,
			Alpha = float.NaN,
		});
		system.Visuals.Add(new FxVisual { HasTexture = true, Texture = "art/fx/pfx_textures/smoke01.tex", Delay = 0.5f, DelayFrames = 15, InvSpan = 1f / 15f });

		d.Emitters.Add(spline);
		d.Emitters.Add(e);
		d.Emitters.Add(light);
		d.Systems.Add(system);
		d.Parts.Add(new FxEffectPart(0, -1, 1, 0));
		d.Parts.Add(new FxEffectPart(1, 0, 1, 0));
		d.Parts.Add(new FxEffectPart(2, -1, 2, 1));
		return d;
	}

	[TestCase]
	public void ConstructedEffectRoundTrips()
	{
		Engine_FxEffectJson.Definition d = Constructed();
		string text = Engine_FxEffectJson.Serialize(d);
		var issues = new List<Engine_FxEffectJson.Issue>();
		List<Engine_FxEffectJson.Definition> back = Engine_FxEffectJson.Parse(text, issues);
		AssertThat(issues.Count).OverrideFailureMessage(Msg(string.Join("; ", issues.Select(i => i.Pointer + " " + i.Message)))).IsEqual(0);
		AssertThat(back.Count).IsEqual(1);
		Engine_FxEffectJson.Definition r = back[0];
		AssertThat(r.Name).IsEqual(d.Name);
		AssertThat(r.Scale).IsEqual(1.5f);
		AssertThat(r.Tint.SequenceEqual(d.Tint)).IsTrue();
		AssertThat(r.UpdateFrequency).IsEqual(1u);
		AssertThat(string.Join(";", r.Parts)).IsEqual(string.Join(";", d.Parts));
		for (int i = 0; i < d.Emitters.Count; i++)
		{
			string? differs = Differ(d.Emitters[i], r.Emitters[i], $"emitter{i}");
			AssertThat(differs).OverrideFailureMessage(Msg(differs ?? string.Empty)).IsNull();
		}

		string? systems = Differ(d.Systems[0], r.Systems[0], "system");
		AssertThat(systems).OverrideFailureMessage(Msg(systems ?? string.Empty)).IsNull();

		// The named views ARE the generic parameters, and the type states the kind.
		AssertThat(ReferenceEquals(r.Emitters[1].Rate, r.Emitters[1].Generic[2])).IsTrue();
		AssertThat(ReferenceEquals(r.Emitters[2].LightRange, r.Emitters[2].Generic[15])).IsTrue();
		AssertThat(r.Emitters[1].HasMotion).IsTrue();
		AssertThat(r.Emitters[2].IsLight).IsTrue();
		AssertThat(r.Emitters[0].IsSpline).IsTrue();
		AssertThat(r.Emitters[1].Rate.Eval(0.5f)).IsEqual(d.Emitters[1].Rate.Eval(0.5f));

		// Hashes are text; defaults are left out.
		AssertThat(text.Contains("\"entityId\": \"0x1234ABCD\"")).IsTrue();
		AssertThat(text.Contains("\"processRadius\"")).IsFalse();
		AssertThat(Engine_FxEffectJson.Serialize(r)).IsEqual(text);
	}

	[TestCase]
	public void AsNewClearsEveryIdAndCopiesDeep()
	{
		Engine_FxEffectJson.Definition d = Constructed();
		Engine_FxEffectJson.Definition fresh = Engine_FxEffectJson.AsNew(d, "my_mod_copy");
		AssertThat(fresh.Name).IsEqual("my_mod_copy");
		AssertThat(fresh.Emitters.All(e => e.EntityId == 0)).IsTrue();
		AssertThat(fresh.Systems.All(s => s.Id == 0)).IsTrue();
		AssertThat(d.Emitters[2].EntityId).IsEqual(0x1234ABCDu);
		fresh.Emitters[1].MaxActive = 999;
		AssertThat(d.Emitters[1].MaxActive).IsEqual(40f);
	}

	[TestCase]
	public void AListFileAndTheParserIssues()
	{
		Engine_FxEffectJson.Definition d = Constructed();
		string many = Engine_FxEffectJson.SerializeMany(new[] { d, Engine_FxEffectJson.AsNew(d, "second") });
		var issues = new List<Engine_FxEffectJson.Issue>();
		AssertThat(Engine_FxEffectJson.Parse(many, issues).Select(x => x.Name).ToArray()).IsEqual(new[] { d.Name, "second" });
		AssertThat(issues.Count).IsEqual(0);

		issues.Clear();
		List<Engine_FxEffectJson.Definition> bad = Engine_FxEffectJson.Parse(
			"{ \"name\": \"x\", \"emitters\": [ { \"typeId\": 2, \"maxActiv\": 3, \"params\": { \"rat\": [[0, 1]] } } ], " +
			"\"parts\": [ { \"emitter\": 4 } ] }", issues);
		AssertThat(bad.Count).IsEqual(0);
		AssertThat(issues.Any(i => i.Error && i.Pointer == "emitters[0].typeId")).IsTrue();
		AssertThat(issues.Any(i => i.Error && i.Pointer == "parts[0].emitter")).IsTrue();
		AssertThat(issues.Any(i => !i.Error && i.Pointer == "emitters[0].maxActiv")).IsTrue();
		AssertThat(issues.Any(i => !i.Error && i.Pointer == "emitters[0].params.rat")).IsTrue();

		issues.Clear();
		Engine_FxEffectJson.Parse("{ \"emitters\": [] }", issues);
		AssertThat(issues.Any(i => i.Error && i.Pointer == "name")).IsTrue();
	}

	// ---- the overlay ----------------------------------------------------------------------------

	/// <summary>A two-effect bank: "torch" (emitters A, B) and "brazier" (emitter A shared).</summary>
	private static FxBank SmallBank()
	{
		var bank = new FxBank { Ok = true };
		Engine_FxEffectJson.Definition c = Constructed();
		FxEmitter a = Engine_FxEffectJson.Copy(c).Emitters[1];
		a.EntityId = 0xA0A0A0A0u;
		FxEmitter b = Engine_FxEffectJson.Copy(c).Emitters[1];
		b.EntityId = 0xB0B0B0B0u;
		bank.Emitters.Add(a);
		bank.Emitters.Add(b);
		FxSystemDef s = Engine_FxEffectJson.Copy(c).Systems[0];
		s.Id = 0x51515151u;
		bank.Systems.Add(s);
		var torch = new FxEffect { NameHash = FxBank.Fnv1Lower("torch"), Name = "torch" };
		torch.Parts.Add(new FxEffectPart(0, 0, 1, 0));
		torch.Parts.Add(new FxEffectPart(1, 0, 1, 0));
		var brazier = new FxEffect { NameHash = FxBank.Fnv1Lower("brazier"), Name = "brazier" };
		brazier.Parts.Add(new FxEffectPart(0, 0, 1, 0));
		bank.Effects.Add(torch);
		bank.Effects.Add(brazier);
		bank.TypeToEffect[a.EntityId] = torch.NameHash;
		bank.TypeToEffect[b.EntityId] = torch.NameHash;
		return bank;
	}

	private static Engine_FxEffectJson.Definition OneEmitter(string name, uint entityId, float maxActive)
	{
		Engine_FxEffectJson.Definition c = Constructed();
		var d = new Engine_FxEffectJson.Definition { Name = name };
		d.Emitters.Add(c.Emitters[1]);
		d.Emitters[0].EntityId = entityId;
		d.Emitters[0].MaxActive = maxActive;
		d.Systems.Add(c.Systems[0]);
		d.Parts.Add(new FxEffectPart(0, 0, 1, 0));
		return Engine_FxEffectJson.Copy(d);
	}

	[TestCase]
	public void OverlayAddsReplacesAndRecordsConflicts()
	{
		FxBank bank = SmallBank();
		FxEmitter shippedA = bank.Emitters[0];
		AssertThat(bank.EmitterForType(0xA0A0A0A0u)).IsEqual(shippedA);

		var sources = new List<Engine_FxModEffects.Source>
		{
			// mod_one: a new effect, and emitter A's parameters replaced.
			new("mod_one", "fx/effects/glow.json", OneEmitter("my_glow", 0, 5)),
			new("mod_one", "fx/effects/a.json", OneEmitter("a_override", 0xA0A0A0A0u, 77)),
			// mod_two: the shipped "torch" replaced whole, and emitter A again (wins over mod_one).
			new("mod_two", "fx/effects/torch.json", OneEmitter("torch", 0, 9)),
			new("mod_two", "fx/effects/a.json", OneEmitter("a_override", 0xA0A0A0A0u, 88)),
		};
		Engine_FxModEffects.Result result = Engine_FxModEffects.Apply(bank, sources);

		// A new name adds.
		AssertThat(bank.FindByName("my_glow")).IsNotNull();
		AssertThat(bank.FindByName("a_override")).IsNotNull();
		AssertThat(result.EffectsAdded).IsEqual(2);

		// A shipped name replaces, in its slot.
		FxEffect torch = bank.FindByName("torch")!;
		AssertThat(bank.Effects.IndexOf(torch)).IsEqual(0);
		AssertThat(torch.Parts.Count).IsEqual(1);
		AssertThat(bank.Emitters[torch.Parts[0].Emitter].MaxActive).IsEqual(9f);
		AssertThat(result.EffectsReplaced).IsEqual(1);

		// A shipped entity id replaces that emitter IN PLACE: every effect using it changes, the
		// live feed's lookup finds the new parameters, and the shipped object is untouched.
		AssertThat(bank.Emitters[0].MaxActive).IsEqual(88f);
		AssertThat(bank.EmitterForType(0xA0A0A0A0u)!.MaxActive).IsEqual(88f);
		FxEffect brazier = bank.FindByName("brazier")!;
		AssertThat(bank.Emitters[brazier.Parts[0].Emitter].MaxActive).IsEqual(88f);
		AssertThat(shippedA.MaxActive).IsEqual(40f);
		AssertThat(result.EmittersReplaced).IsEqual(2);

		// Two mods stating emitter A and "a_override": conflicts, the later mod winning.
		RowConflict emitter = result.Conflicts.Single(c => c.Key == "emitter 0xA0A0A0A0");
		AssertThat(emitter.Winner).IsEqual("mod_two");
		AssertThat(emitter.Losers.ToArray()).IsEqual(new[] { "mod_one" });
		AssertThat(result.Conflicts.Any(c => c.Key == "effect 'a_override'")).IsTrue();
		AssertThat(result.Conflicts.Any(c => c.Key == "effect 'torch'")).IsFalse();

		// The new effects' emitters took derived ids, distinct from each other and from the bank's.
		uint glowId = bank.Emitters[bank.FindByName("my_glow")!.Parts[0].Emitter].EntityId;
		AssertThat(glowId).IsEqual(Engine_FxEffectJson.DerivedId("my_glow", "emitter", 0));
		AssertThat(bank.EffectForType(glowId)).IsEqual(FxBank.Fnv1Lower("my_glow"));
	}

	[TestCase]
	public void PreviewBankLeavesTheSharedOneAlone()
	{
		FxBank shared = SmallBank();
		int emitters = shared.Emitters.Count;
		FxBank preview = Engine_FxModEffects.PreviewBank(shared, OneEmitter("torch", 0xA0A0A0A0u, 123));
		AssertThat(preview.FindByName("torch")).IsNotNull();
		AssertThat(preview.Emitters[0].MaxActive).IsEqual(123f);
		AssertThat(shared.Emitters[0].MaxActive).IsEqual(40f);
		AssertThat(shared.Emitters.Count).IsEqual(emitters);
		AssertThat(shared.FindByName("torch")!.Parts.Count).IsEqual(2);
	}

	[TestCase]
	public void ModsAreReadFromTheirPackages()
	{
		string dir = Path.Combine(Path.GetTempPath(), "refii_fx_effects_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try
		{
			string package = Path.Combine(dir, "fx_mod.f2mod");
			byte[] json = Encoding.UTF8.GetBytes(Engine_FxEffectJson.Serialize(Constructed()));
			byte[] broken = Encoding.UTF8.GetBytes("{ \"name\": \"broken\", \"parts\": [ { \"emitter\": 3 } ] }");
			AssertThat(F2ModPackage.Create(
				package, new ModMetadata { Title = "fx_mod", Version = "1.0.0" },
				new[] { ("fx/effects/my_mod_green_torch.json", json), ("fx/effects/broken.json", broken), ("fx/recipes.json", Encoding.UTF8.GetBytes("{\"recipes\":[]}")) },
				out string error)).OverrideFailureMessage(Msg(error)).IsTrue();

			List<string> lines = Engine_FxModEffects.Load(new[] { new ModOverlay { Name = "fx_mod", PackagePath = package, AssetRoot = package } });
			AssertThat(Engine_FxModEffects.Sources.Count).IsEqual(1);
			AssertThat(Engine_FxModEffects.Sources[0].Effect.Name).IsEqual("my_mod_green_torch");
			AssertThat(lines.Any(l => l.StartsWith("REFUSED fx_mod/fx/effects/broken.json", StringComparison.Ordinal))).IsTrue();
		}
		finally
		{
			Engine_FxModEffects.SetSources(Array.Empty<Engine_FxModEffects.Source>());
			Directory.Delete(dir, true);
		}
	}

	// ---- the validator --------------------------------------------------------------------------

	[TestCase]
	public void ValidatorIsTheReadersVerdict()
	{
		AssertThat(ModValidate.KindOf("files/FX/Effects/My_Fx.json")).IsEqual(ModValidate.FxEffectsKind);
		AssertThat(ModValidate.KindOf("fx/effects/sub/x.json")).IsEqual(string.Empty);
		AssertThat(ModValidate.KindOf(Engine_FxRecipes.RecipesPath)).IsEqual(Engine_FxRecipes.RecipesPath);

		IReadOnlyList<ModIssue> clean = ModValidate.Validate("fx/effects/my_mod_green_torch.json", Engine_FxEffectJson.Serialize(Constructed()));
		AssertThat(clean.Count).OverrideFailureMessage(Msg(string.Join("; ", clean))).IsEqual(0);

		IReadOnlyList<ModIssue> bad = ModValidate.Validate("fx/effects/x.json", "{ \"name\": \"x\", \"scale\": 0, \"tint\": [1, 1] }");
		AssertThat(bad.Any(i => i.Error && i.Pointer == "scale")).IsTrue();
		AssertThat(bad.Any(i => i.Error && i.Pointer == "tint")).IsTrue();
		AssertThat(ModValidate.Validate("fx/effects/x.json", "not json").Single().Error).IsTrue();
	}

	// ---- play_fx --------------------------------------------------------------------------------

	[TestCase]
	public void PlayRequestsParse()
	{
		Engine_FxPlayer.Request? play = Engine_FxPlayer.ParseRequest(
			"op='play',id=5,name='fxspl_blast_fire_ring',x=1.5,y=-2,z=30,entity=0,duration=2.5,scale=1.25,r=1,g=0.5,b=0.25,intensity=0.8,loop=0,ox=0,oy=0,oz=1");
		AssertThat(play).IsNotNull();
		AssertThat(play!.Value.IsPlay).IsTrue();
		AssertThat(play.Value.Id).IsEqual(5u);
		AssertThat(play.Value.Name).IsEqual("fxspl_blast_fire_ring");
		AssertThat(play.Value.Y).IsEqual(-2f);
		AssertThat(play.Value.Duration).IsEqual(2.5f);
		AssertThat(play.Value.Loop).IsFalse();
		AssertThat(play.Value.G).IsEqual(0.5f);
		AssertThat(play.Value.OffsetZ).IsEqual(1f);

		Engine_FxPlayer.Request? defaults = Engine_FxPlayer.ParseRequest("op='play',id=8388608,name='x',entity=12");
		AssertThat(defaults!.Value.Scale).IsEqual(1f);
		AssertThat(defaults.Value.Loop).IsTrue();
		AssertThat(defaults.Value.Entity).IsEqual(12u);
		AssertThat(defaults.Value.Id).IsEqual(Engine_FxPlayer.FirstHostId);

		AssertThat(Engine_FxPlayer.ParseRequest("op='stop',id=9")!.Value.IsPlay).IsFalse();
		AssertThat(Engine_FxPlayer.ParseRequest("op='play',id=9")).IsNull();
		AssertThat(Engine_FxPlayer.ParseRequest("op='spin',id=9,name='x'")).IsNull();
		AssertThat(Engine_FxPlayer.ParseRequest("op='stop',id=0")).IsNull();
	}

	// ---- the surfaces agree ---------------------------------------------------------------------

	[TestCase]
	public void EverySurfaceStatesPlayFx()
	{
		AssertThat(ModApi.Helpers.Any(h => h.Signature.Contains("refii_mod_play_fx"))).IsTrue();
		AssertThat(ModApi.LuaHelpers.Any(h => h.Signature.Contains("Refii.play_fx"))).IsTrue();

		// Every Godot call the catalog names is a method of RefiiHost.
		foreach (ModApiHelper h in ModApi.GodotHelpers)
		{
			foreach (string name in ModApi.GodotMethodNames(h.Signature))
			{
				AssertThat(typeof(RefiiHost).GetMethod(name, BindingFlags.Public | BindingFlags.Instance) is not null)
					.OverrideFailureMessage(Msg($"RefiiHost has no '{name}' ({h.Signature})")).IsTrue();
			}
		}

		string? root = RepoRoot();
		if (root is null)
		{
			return;
		}

		string header = File.ReadAllText(Path.Combine(root, "src", "vfs", "include", "core", "refii_mod.h")) +
			File.ReadAllText(Path.Combine(root, "src", "vfs", "include", "core", "refii_mod_events.h"));
		foreach (string native in new[] { "refii_mod_play_fx", "refii_mod_play_fx_on", "refii_mod_stop_fx" })
		{
			AssertThat(Regex.IsMatch(header, $@"extern ""C"" \w+ {native}\(")).OverrideFailureMessage(Msg($"{native} is not declared")).IsTrue();
		}

		Match kind = Regex.Match(header, @"#define REFII_MOD_HOST_FX_KIND (\d+)");
		AssertThat(kind.Success).IsTrue();
		AssertThat(int.Parse(kind.Groups[1].Value)).IsEqual(Engine_FxPlayer.RequestKind);

		string natives = File.ReadAllText(Path.Combine(root, "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
		AssertThat(natives.Contains("{\"_play_fx\", &NativePlayFx, 0}")).IsTrue();
		AssertThat(natives.Contains("{\"stop_fx\", &NativeStopFx, 0}")).IsTrue();
	}
}
