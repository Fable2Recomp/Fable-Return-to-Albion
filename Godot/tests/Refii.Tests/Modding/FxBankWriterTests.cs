namespace Refii.Tests.Modding;

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Fx;
using Refii.Fx.Xex;

/// <summary>
/// The guest's particle bank with the mods written in (Engine_FxBankWriter): the loader's layout
/// reproduced record for record against the shipped bank when the tree has it, and a mod's effect
/// read back by the bank's own parser as the model the overlay gave Godot. No Godot type is built.
/// </summary>
[TestSuite]
public class FxBankWriterTests
{
	private static string? ShippedPath()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 16 && at != null; i++)
		{
			string candidate = Path.Combine(at, "assets", "game", "data", "art", "particles", "particle_bank.bnk");
			if (File.Exists(candidate))
			{
				return candidate;
			}

			at = Path.GetDirectoryName(at);
		}

		return null;
	}

	private static string Msg(string text) => text.Length == 0 ? "(no detail)" : text;

	private static readonly int[] Frames = Enumerable.Range(-3, 400).ToArray();

	/// <summary>Behavioural equality of the model: timelines by what they evaluate, floats by bits, the rest by fields.</summary>
	private static string? Differ(object? a, object? b, string at, ISet<string> skip)
	{
		switch (a, b)
		{
			case (null, null):
				return null;
			case (FxTimeline x, FxTimeline y):
				foreach (int f in Frames)
				{
					float vx = x.EvalFrame(f), vy = y.EvalFrame(f);
					if (BitConverter.SingleToInt32Bits(vx) != BitConverter.SingleToInt32Bits(vy) && !(float.IsNaN(vx) && float.IsNaN(vy)))
					{
						return $"{at}: frame {f} {vx} != {vy}";
					}
				}

				return null;
			case (FxVectorTimeline x, FxVectorTimeline y):
				foreach (int f in Frames)
				{
					if (x.EvalFrame(f) != y.EvalFrame(f))
					{
						return $"{at}: vector frame {f}";
					}
				}

				return null;
			case (float x, float y):
				return BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y) || (float.IsNaN(x) && float.IsNaN(y)) ? null : $"{at}: {x} != {y}";
			case (string x, string y):
				return x.Replace('/', '\\') == y.Replace('/', '\\') ? null : $"{at}: '{x}' != '{y}'";
			case (IList x, IList y):
				if (x.Count != y.Count)
				{
					return $"{at}: {x.Count} != {y.Count} items";
				}

				for (int i = 0; i < x.Count; i++)
				{
					if (Differ(x[i], y[i], $"{at}[{i}]", skip) is { } d)
					{
						return d;
					}
				}

				return null;
			case (FxEmitter or FxSystemDef or FxVisual, _):
				foreach (FieldInfo field in a.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
				{
					if (!skip.Contains(field.Name) && Differ(field.GetValue(a), field.GetValue(b), $"{at}.{field.Name}", skip) is { } d)
					{
						return d;
					}
				}

				foreach (PropertyInfo property in a.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
				{
					if (!skip.Contains(property.Name) && property.GetIndexParameters().Length == 0 &&
						Differ(property.GetValue(a), property.GetValue(b), $"{at}.{property.Name}", skip) is { } d)
					{
						return d;
					}
				}

				return null;
			default:
				return Equals(a, b) ? null : $"{at}: {a} != {b}";
		}
	}

	/// <summary>
	/// What a written record does not keep, none of it drawn. A visual's <c>Delay</c> is the seconds
	/// the loader turns back into the same <c>DelayFrames</c>; <c>HasColor</c>, <c>HasBlend</c> and
	/// <c>HasBlendOp</c> say which tags the shipped child STATED (the writer states every tag with its
	/// resolved value); a system's <c>ChildCount</c> counts timed materials with no texture or mesh,
	/// which the model drops; the loop bounds are frames carried through seconds (30·(f/30)).
	/// </summary>
	private static readonly HashSet<string> SystemSkip = new(StringComparer.Ordinal)
	{
		"Delay", "HasColor", "HasBlend", "HasBlendOp", "ChildCount", "LoopStartFrames", "LoopEndFrames",
	};

	/// <summary>
	/// <c>EmitOnSurface</c> and <c>UseShapeSpawnDir</c> are the bank parser's names for the shape
	/// flags; the simulation reads <c>ShapeFlagA</c>/<c>ShapeFlagB</c>, which are what is written.
	/// </summary>
	private static readonly HashSet<string> EmitterSkip = new(StringComparer.Ordinal) { "EmitOnSurface", "UseShapeSpawnDir" };

	/// <summary>A bank with no records: the header, version 2, and the four empty sections.</summary>
	private static byte[] EmptyBank()
	{
		var w = new Engine_FxBankWriter.BigEndianWriter(64);
		w.Raw(System.Text.Encoding.ASCII.GetBytes("ParticleBankFile"));
		w.U32(2);
		w.U32(1);
		w.I32(0);
		w.U32(1);
		w.I32(0);
		w.U32(1);
		w.I32(0);
		w.U32(3);
		w.I32(0);
		return w.ToArray();
	}

	private static FxEmitter Standard()
	{
		var e = new FxEmitter { TypeId = 0, SubType = 1, MaxActive = 40, InitialNum = 3, SpawnRadius = 0.25f, ShapeFlagA = true, AttachToRoot = true, LifetimeFrames = 90, UseLife = true };
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

		e.Vec0 = Engine_FxBank.MakeVecTimeline(0, Array.Empty<(float, float, float, float)>());
		e.Vec1 = Engine_FxBank.MakeVecTimeline(1, new[] { (0f, 0.2f, 1f, 0.3f), (1f, 0f, 0.5f, 0f) });
		e.Vec2 = Engine_FxBank.MakeVecTimeline(2, Array.Empty<(float, float, float, float)>());
		e.LightColour = Engine_FxBank.MakeVecTimeline(3, Array.Empty<(float, float, float, float)>());
		Engine_FxBank.LinkParameters(e);
		return e;
	}

	private static Engine_FxEffectJson.Definition ModEffect(string name)
	{
		var d = new Engine_FxEffectJson.Definition { Name = name, UpdateFrequency = 1 };
		var light = new FxEmitter { TypeId = 7, HaloTextureHash = 0xDEADBEEFu };
		for (int i = 0; i < 28; i++)
		{
			light.Generic[i] = Engine_FxBank.MakeTimeline(i, i == 15 ? new[] { (0f, 6f) } : Array.Empty<(float, float)>());
		}

		light.Vec0 = Engine_FxBank.MakeVecTimeline(0, Array.Empty<(float, float, float, float)>());
		light.Vec1 = Engine_FxBank.MakeVecTimeline(1, Array.Empty<(float, float, float, float)>());
		light.Vec2 = Engine_FxBank.MakeVecTimeline(2, Array.Empty<(float, float, float, float)>());
		light.LightColour = Engine_FxBank.MakeVecTimeline(3, new[] { (0f, 0.2f, 1f, 0.1f) });
		Engine_FxBank.LinkParameters(light);

		// A consistent model: the last visual starts at frame 15, which is what the loader derives.
		var system = new FxSystemDef { Mode = 1, LoopCount = 0, LastStartFrames = 15, ChildCount = 2 };
		// Complete visuals, as the bank's own resolve them: every value a mode-1 segment carries.
		static float[] Sq(float[] c) => c.Select(x => x * x).ToArray();
		float[] first = { 0.5f, 1f, 0.5f }, second = { 0.1f, 0.2f, 0.3f };
		system.Visuals.Add(new FxVisual
		{
			HasTexture = true, Texture = "art\\fx\\pfx_textures\\fxt_glow_red_01.tex", TexCols = 4, TexRows = 2, FrameSpeed = 15f,
			HasColor = true, Color = first, ColorSq = Sq(first), HasAlpha = true, Alpha = 1f, HasSize = true, SizeX = 0.4f, SizeY = 0.6f,
			SrcBlend = 2, DstBlend = 2, SoftDepthContrast = 0.25f, HasSelfIllum = true, HasGlobalScale = true, HasSpritePoints = true, AlignQuat = new[] { 0f, 0f, 1f, 0f },
		});
		system.Visuals.Add(new FxVisual
		{
			HasTexture = true, Texture = "art\\fx\\pfx_textures\\fxt_glow_red_01.tex", TexCols = 4, TexRows = 2, FrameSpeed = 15f,
			DelayFrames = 15, InvSpan = 1f / 14f, HasColor = true, Color = second, ColorSq = Sq(second), HasAlpha = true, Alpha = 0.5f,
			HasSize = true, SizeX = 1.5f, SizeY = 1.5f, SrcBlend = 2, DstBlend = 2, HasFlicker = true, FlickerFrames = 7, FlickerPeriod = 7f / 30f,
			FlickerBaseX = 0.8f, FlickerBaseY = 0.9f, FlickerSpreadX = 0.2f, FlickerSpreadY = 0.1f, SoftDepthContrast = 0.25f,
			HasSelfIllum = true, HasGlobalScale = true, HasSpritePoints = true, AlignQuat = new[] { 0f, 0f, 1f, 0f },
		});

		d.Emitters.Add(Standard());
		d.Emitters.Add(light);
		d.Systems.Add(system);
		d.Parts.Add(new FxEffectPart(0, 0, 1, 0));
		d.Parts.Add(new FxEffectPart(1, -1, 2, 1));
		return Engine_FxEffectJson.Copy(d);
	}

	private static FxBank Reparse(byte[] bytes)
	{
		var bank = new FxBank();
		bool ok = Engine_FxBank.Parse(bytes, bank);
		AssertThat(ok).OverrideFailureMessage(Msg(bank.Error)).IsTrue();
		return bank;
	}

	// ---- always ---------------------------------------------------------------------------------

	[TestCase]
	public void AModEffectReadsBackAsTheModelGodotDraws()
	{
		byte[] empty = EmptyBank();
		var source = new Engine_FxModEffects.Source("mod_one", "fx/effects/glow.json", ModEffect("my_mod_glow"));
		Engine_FxBankWriter.Composed? composed = Engine_FxBankWriter.Compose(empty, new[] { source }, out string error);
		AssertThat(composed).OverrideFailureMessage(Msg(error)).IsNotNull();

		// The overlay Godot draws from, built the same way.
		var expected = new FxBank { Ok = true };
		Engine_FxModEffects.Apply(expected, new[] { source });

		FxBank guest = Reparse(composed!.Bytes);
		uint hash = FxBank.Fnv1Lower("my_mod_glow");
		FxEffect? written = guest.FindByHash(hash);
		AssertThat(written).OverrideFailureMessage("the effect is not in the written bank").IsNotNull();
		FxEffect want = expected.FindByHash(hash)!;
		AssertThat(written!.Parts.Count).OverrideFailureMessage($"{written.Parts.Count} parts, want {want.Parts.Count}").IsEqual(want.Parts.Count);
		AssertThat(written.UpdateFrequency).OverrideFailureMessage("update frequency").IsEqual(1u);
		for (int i = 0; i < want.Parts.Count; i++)
		{
			FxEffectPart a = want.Parts[i], b = written.Parts[i];
			AssertThat(b.Group).OverrideFailureMessage($"part{i} group {b.Group} != {a.Group}").IsEqual(a.Group);
			AssertThat(b.Vector).OverrideFailureMessage($"part{i} vector {b.Vector} != {a.Vector}").IsEqual(a.Vector);
			AssertThat(guest.Emitters[b.Emitter].EntityId).OverrideFailureMessage($"part{i} entity").IsEqual(expected.Emitters[a.Emitter].EntityId);
			string? d = Differ(expected.Emitters[a.Emitter], guest.Emitters[b.Emitter], $"part{i}.emitter", EmitterSkip);
			AssertThat(d).OverrideFailureMessage(Msg(d ?? string.Empty)).IsNull();
			AssertThat(b.System >= 0).OverrideFailureMessage($"part{i} system {b.System} / {a.System}").IsEqual(a.System >= 0);
			if (a.System >= 0)
			{
				AssertThat(guest.Systems[b.System].Id).OverrideFailureMessage($"part{i} system id").IsEqual(expected.Systems[a.System].Id);
				d = Differ(expected.Systems[a.System], guest.Systems[b.System], $"part{i}.system", SystemSkip);
				AssertThat(d).OverrideFailureMessage(Msg(d ?? string.Empty)).IsNull();
			}
		}

		// The live feed's join finds the effect from the guest's own type ids.
		AssertThat(guest.EffectForType(guest.Emitters[written.Parts[0].Emitter].EntityId)).OverrideFailureMessage("type join").IsEqual(hash);
	}

	[TestCase]
	public void WrittenMaterialIdsAreFreshAndDistinct()
	{
		var x = new XexBank();
		AssertThat(XexBankParser.ParseBank(EmptyBank(), x)).IsTrue();
		var t = new Engine_FxBankWriter.Templates(x);
		var ids = new HashSet<uint>();
		for (int i = 0; i < 64; i++)
		{
			AssertThat(ids.Add(t.FreshMaterialId(0x1234u, i % 4))).IsTrue();
		}
	}

	[TestCase]
	public void SecondsForRoundTripsThroughTheLoadersFrameCount()
	{
		for (int frames = 0; frames < 2000; frames++)
		{
			float s = Engine_FxBankWriter.SecondsFor(frames);
			AssertThat(XexConst.Fctiwz(XexConst.Fps * s)).IsEqual(frames);
		}
	}

	[TestCase]
	public void TheClassicPlayCommandStatesEveryOptionTheGuestTakes()
	{
		Engine_FxPlayer.Request r = Engine_FxPlayer.ParseRequest(
			"op='play',id=7,name='my_mod_glow',x=1.5,y=-2,z=3.25,entity=12,duration=4,scale=2,r=1,g=0.5,b=1," +
			"intensity=0.75,loop=0,ox=0.1,oy=0,oz=1")!.Value;
		uint hash = Engine_FxEffectJson.HashOf(r.Name);
		string line = Engine_FxPlayer.GuestPlayCommand(r, hash);
		string[] w = line.Split(' ');

		// The grammar mod_commands.cpp reads: fx_play ID HASH X Y Z ENTITY OX OY OZ SCALE INTENSITY LOOP.
		AssertThat(w.Length).IsEqual(13);
		AssertThat(w[0]).IsEqual("fx_play");
		AssertThat(w[1]).IsEqual("7");
		AssertThat(w[2]).IsEqual($"0x{FxBank.Fnv1Lower("my_mod_glow"):X8}");
		AssertThat(float.Parse(w[3], System.Globalization.CultureInfo.InvariantCulture)).IsEqual(1.5f);
		AssertThat(float.Parse(w[5], System.Globalization.CultureInfo.InvariantCulture)).IsEqual(3.25f);
		AssertThat(w[6]).IsEqual("12");
		AssertThat(float.Parse(w[9], System.Globalization.CultureInfo.InvariantCulture)).IsEqual(1f);
		AssertThat(float.Parse(w[10], System.Globalization.CultureInfo.InvariantCulture)).IsEqual(2f);
		AssertThat(float.Parse(w[11], System.Globalization.CultureInfo.InvariantCulture)).IsEqual(0.75f);
		AssertThat(w[12]).IsEqual("0");

		// A hash token names the effect by its key as it stands.
		AssertThat(Engine_FxPlayer.GuestPlayCommand(r with { Name = "FX_0A1B2C3D" }, Engine_FxEffectJson.HashOf("FX_0A1B2C3D")).Split(' ')[2]).IsEqual("0x0A1B2C3D");
		AssertThat(Engine_FxPlayer.GuestStopCommand(7)).IsEqual("fx_stop 7");
	}

	// ---- against the shipped bank, when the tree has the player's data ---------------------------

	[TestCase]
	public void NoModLeavesTheShippedBankByteForByte()
	{
		if (ShippedPath() is not { } path)
		{
			return;
		}

		byte[] shipped = File.ReadAllBytes(path);
		Engine_FxBankWriter.Composed? composed = Engine_FxBankWriter.Compose(shipped, Array.Empty<Engine_FxModEffects.Source>(), out string error);
		AssertThat(composed).OverrideFailureMessage(Msg(error)).IsNotNull();
		AssertThat(composed!.Bytes.SequenceEqual(shipped)).IsTrue();
	}

	[TestCase]
	public void EveryShippedEntityEncodesToItsOwnBytes()
	{
		if (ShippedPath() is not { } path)
		{
			return;
		}

		byte[] shipped = File.ReadAllBytes(path);
		var x = new XexBank();
		AssertThat(XexBankParser.ParseBank(shipped, x)).IsTrue();
		FxBank bank = Reparse(shipped);
		var t = new Engine_FxBankWriter.Templates(x);
		var mismatches = new List<string>();
		for (int i = 0; i < x.EntityOrder.Count; i++)
		{
			XexEntityRef r = x.EntityOrder[i];
			XexEntityType? like = x.Entity(r.EntityId);
			if (like is null || r.Index < 0 || !ReferenceEquals(like, Entity(x, r)))
			{
				continue;
			}

			var w = new Engine_FxBankWriter.BigEndianWriter(1024);
			Engine_FxBankWriter.EncodeEntity(w, bank.Emitters[i], x.BankVersion, x.Fable3Layout, t, like);
			byte[] mine = w.ToArray();
			if (!mine.AsSpan().SequenceEqual(shipped.AsSpan(like.FileBegin, like.FileEnd - like.FileBegin)))
			{
				mismatches.Add($"type {r.TypeId} id 0x{r.EntityId:X8}");
			}
		}

		AssertThat(mismatches.Count).OverrideFailureMessage(Msg($"{mismatches.Count} of {x.EntityOrder.Count}: {string.Join(", ", mismatches.Take(12))}")).IsEqual(0);
	}

	private static XexEntityType? Entity(XexBank x, XexEntityRef r) => r.TypeId switch
	{
		0 => x.StdEmitters[r.Index],
		3 => x.Orbitals[r.Index],
		4 => x.Attractors[r.Index],
		5 => x.SplineEntities[r.Index],
		6 => x.Roots[r.Index],
		7 => x.PointLights[r.Index],
		8 => x.SpotLights[r.Index],
		9 => x.SplineEmitters[r.Index],
		_ => null,
	};

	[TestCase]
	public void EveryShippedEffectEncodesToItsOwnBytes()
	{
		if (ShippedPath() is not { } path)
		{
			return;
		}

		byte[] shipped = File.ReadAllBytes(path);
		var x = new XexBank();
		AssertThat(XexBankParser.ParseBank(shipped, x)).IsTrue();
		FxBank bank = Reparse(shipped);
		var t = new Engine_FxBankWriter.Templates(x);
		int compared = 0;
		var mismatches = new List<string>();
		for (int i = 0; i < x.Effects.Count; i++)
		{
			XexEffectCollection xe = x.Effects[i];
			bool resolved = xe.TypeVectors.All(v => v.Items.Count != 0 && v.Items.All(it =>
				x.EntityById.ContainsKey(it.EntityTypeId) &&
				(it.MaterialTimelineId == 0 || x.SystemById.ContainsKey(it.MaterialTimelineId)) &&
				(it.TrailMaterialTimelineId == 0 || x.SystemById.ContainsKey(it.TrailMaterialTimelineId))));
			if (!resolved || xe.TrailingId != t.EffectTrailing)
			{
				continue;
			}

			var w = new Engine_FxBankWriter.BigEndianWriter(512);
			Engine_FxBankWriter.EncodeEffect(w, bank, bank.Effects[i], x.Section5Version, t);
			compared++;
			if (!w.ToArray().AsSpan().SequenceEqual(shipped.AsSpan(xe.FileBegin, xe.FileEnd - xe.FileBegin)))
			{
				mismatches.Add($"0x{xe.Id:X8}");
			}
		}

		AssertThat(compared > 0).IsTrue();
		AssertThat(mismatches.Count).OverrideFailureMessage(Msg($"{mismatches.Count} of {compared}: {string.Join(", ", mismatches.Take(12))}")).IsEqual(0);
	}

	[TestCase]
	public void EveryShippedSystemRewrittenDrawsTheSameVisuals()
	{
		if (ShippedPath() is not { } path)
		{
			return;
		}

		byte[] shipped = File.ReadAllBytes(path);
		var x = new XexBank();
		AssertThat(XexBankParser.ParseBank(shipped, x)).IsTrue();
		FxBank bank = Reparse(shipped);
		var all = new Engine_FxModEffects.Result();
		foreach (FxSystemDef s in bank.Systems)
		{
			all.StatedSystems.Add(s.Id);
		}

		FxBank guest = Reparse(Engine_FxBankWriter.Write(shipped, x, bank, all));
		AssertThat(guest.Systems.Count).IsEqual(bank.Systems.Count);
		AssertThat(guest.Emitters.Count).IsEqual(bank.Emitters.Count);
		AssertThat(guest.Effects.Count).IsEqual(bank.Effects.Count);
		var mismatches = new List<string>();
		for (int i = 0; i < bank.Systems.Count; i++)
		{
			if (Differ(bank.Systems[i], guest.Systems[i], $"system 0x{bank.Systems[i].Id:X8}", SystemSkip) is { } d)
			{
				mismatches.Add(d);
			}
		}

		AssertThat(mismatches.Count).OverrideFailureMessage(Msg($"{mismatches.Count} of {bank.Systems.Count}: {string.Join(" | ", mismatches.Take(8))}")).IsEqual(0);
	}

	[TestCase]
	public void AnOverrideRewritesOnlyWhatItStates()
	{
		if (ShippedPath() is not { } path)
		{
			return;
		}

		byte[] shipped = File.ReadAllBytes(path);
		FxBank bank = Reparse(shipped);
		FxEffect target = bank.Effects.First(e => e.Parts.Count(p => bank.Emitters[p.Emitter].TypeId == 0) >= 1 && e.Parts.All(p => p.System >= 0 || bank.Emitters[p.Emitter].TypeId != 0));
		Engine_FxEffectJson.Definition d = Engine_FxEffectJson.Extract(bank, target);
		FxEmitter changed = d.Emitters.First(e => e.TypeId == 0);
		changed.MaxActive = 123;
		changed.Generic[2] = Engine_FxBank.MakeTimeline(2, new[] { (0f, 77f) });
		Engine_FxBank.LinkParameters(changed);

		var sources = new[]
		{
			new Engine_FxModEffects.Source("mod_one", "fx/effects/override.json", d),
			new Engine_FxModEffects.Source("mod_one", "fx/effects/glow.json", ModEffect("my_mod_glow")),
		};
		Engine_FxBankWriter.Composed? composed = Engine_FxBankWriter.Compose(shipped, sources, out string error);
		AssertThat(composed).OverrideFailureMessage(Msg(error)).IsNotNull();
		FxBank guest = Reparse(composed!.Bytes);

		FxEmitter? written = guest.EmitterForType(changed.EntityId);
		AssertThat(written).IsNotNull();
		AssertThat(written!.MaxActive).IsEqual(123f);
		AssertThat(written.Rate.Eval(0.5f)).IsEqual(77f);
		AssertThat(guest.FindByName("my_mod_glow")).IsNotNull();
		AssertThat(guest.Effects.Count).IsEqual(bank.Effects.Count + 1);
		AssertThat(guest.Emitters.Count).IsEqual(bank.Emitters.Count + 2);

		// Every entity no mod stated is the shipped one, in its place.
		var stated = new HashSet<uint>(composed.Result.StatedEmitters);
		for (int i = 0; i < bank.Emitters.Count; i++)
		{
			if (stated.Contains(bank.Emitters[i].EntityId))
			{
				continue;
			}

			string? differs = Differ(bank.Emitters[i], guest.Emitters[i], $"emitter 0x{bank.Emitters[i].EntityId:X8}", EmitterSkip);
			AssertThat(differs).OverrideFailureMessage(Msg(differs ?? string.Empty)).IsNull();
		}
	}
}
