namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Fx;
using Refii.Mods;

/// <summary>
/// Spell Weaving's looks are mod effects of the bank's own model (fx/effects/*.json), played per
/// cast: each file parses as the effect it is named for, carries no Godot-only pieces, and is what
/// the guest's bank reads back once written into it (so both looks draw the same effect); every
/// recipe binds its tint to one of them through "with". No Godot type is constructed.
/// </summary>
[TestSuite]
public class SpellWeavingFxTests
{
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

	/// <summary>A bank with no records: the header, version 2, and the four empty sections.</summary>
	/// <summary>The shipped records Spell Weaving's blades reference: the Blades sword's effects' emitters and systems.</summary>
	private static readonly HashSet<uint> SwordReferences = new()
	{
		0x445DD110u, 0x67B71EC8u, 0xFE1598B0u, 0xCCC1AADBu, 0x17A034D9u, 0x6C55D39Fu,
		0x17E73103u, 0x5BDA9451u, 0xB9ADDDE7u, 0x3854C338u, 0x9085FBA0u, 0xB1B4A838u,
	};

	private static byte[] EmptyBank()
	{
		var w = new Engine_FxBankWriter.BigEndianWriter(64);
		w.Raw(Encoding.ASCII.GetBytes("ParticleBankFile"));
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

	private static List<(string File, string Text, Engine_FxEffectJson.Definition Effect)> Effects(string folder)
	{
		var effects = new List<(string, string, Engine_FxEffectJson.Definition)>();
		foreach (string file in Directory.GetFiles(Path.Combine(folder, "fx", "effects"), "*.json").OrderBy(f => f, StringComparer.Ordinal))
		{
			string text = File.ReadAllText(file);
			var issues = new List<Engine_FxEffectJson.Issue>();
			List<Engine_FxEffectJson.Definition> parsed = Engine_FxEffectJson.Parse(text, issues);
			AssertThat(issues.Any(i => i.Error))
				.OverrideFailureMessage($"{Path.GetFileName(file)}: {string.Join("; ", issues.Select(i => i.Pointer + " " + i.Message))}")
				.IsFalse();
			AssertThat(parsed.Count).OverrideFailureMessage($"{Path.GetFileName(file)}: one effect per file").IsEqual(1);
			effects.Add((file, text, parsed[0]));
		}

		return effects;
	}

	[TestCase]
	public void EveryLookIsABankEffectNamedForItsFile()
	{
		if (ModFolder() is not { } folder)
		{
			return;  // a packaged test run without the source tree
		}

		var effects = Effects(folder);
		AssertThat(effects.Count).IsEqual(15);
		foreach ((string file, string text, Engine_FxEffectJson.Definition effect) in effects)
		{
			AssertThat(effect.Name).IsEqual(Path.GetFileNameWithoutExtension(file));
			AssertThat(effect.Pieces.Count).OverrideFailureMessage($"{effect.Name}: a piece is Godot's drawing only").IsEqual(0);
			AssertThat(effect.Parts.Count > 0).IsTrue();
			IReadOnlyList<ModIssue> issues = ModValidate.Validate("fx/effects/" + Path.GetFileName(file), text);
			AssertThat(issues.Count)
				.OverrideFailureMessage($"{effect.Name}: {string.Join("; ", issues.Select(i => i.Pointer + " " + i.Message))}").IsEqual(0);
		}
	}

	[TestCase]
	public void TheGuestsBankReadsBackEveryLookAsAuthored()
	{
		if (ModFolder() is not { } folder)
		{
			return;
		}

		var effects = Effects(folder);
		var sources = effects
			.Select(e => new Engine_FxModEffects.Source("spell_weaving", "fx/effects/" + Path.GetFileName(e.File), e.Effect))
			.ToList();
		Engine_FxBankWriter.Composed? composed = Engine_FxBankWriter.Compose(EmptyBank(), sources, out string error);
		AssertThat(composed).OverrideFailureMessage(error.Length == 0 ? "(no detail)" : error).IsNotNull();
		var bank = new FxBank();
		AssertThat(Engine_FxBank.Parse(composed!.Bytes, bank)).OverrideFailureMessage(bank.Error.Length == 0 ? "(no detail)" : bank.Error).IsTrue();

		// What the guest's records have no field for — an emitter's spin, a mesh system's rest
		// orientation — it reads from the dress table beside the bank (particle_dress.cpp).
		var spins = new Dictionary<uint, float>();
		var aligns = new Dictionary<uint, float[]>();
		foreach (string line in Engine_FxModEffects.DressText(sources).Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			string[] words = line.Split(' ');
			float F(int i) => float.Parse(words[i], System.Globalization.CultureInfo.InvariantCulture);
			if (words[0] == "spin")
			{
				spins[Convert.ToUInt32(words[1], 16)] = F(2);
			}
			else if (words[0] == "align")
			{
				aligns[Convert.ToUInt32(words[1], 16)] = new[] { F(2), F(3), F(4), F(5) };
			}
		}

		// The file IS the guest's reading of it: written into the bank and the dress table and read
		// back, each effect serializes to the very text it was read from, so Godot and the guest draw
		// one model.
		foreach ((string file, string text, Engine_FxEffectJson.Definition effect) in effects)
		{
			FxEffect? written = bank.FindByName(effect.Name);
			AssertThat(written).OverrideFailureMessage($"{effect.Name}: not in the written bank").IsNotNull();
			Engine_FxEffectJson.Definition read = Engine_FxEffectJson.Extract(bank, written!, effect.Name);
			foreach (FxEmitter e in read.Emitters)
			{
				e.Spin = spins.TryGetValue(e.EntityId, out float spin) ? spin : 0f;
			}

			// The shipped records a look references (the Blades sword's own effects, stated exactly
			// as the shipped bank holds them) are never written: Engine_FxModEffects.Apply draws the
			// bank's own (FxModEffectsTests). This bank holds no shipped record, so they read back
			// as stated.
			for (int i = 0; i < read.Emitters.Count; i++)
			{
				if (effect.Emitters.FirstOrDefault(e => e.EntityId == read.Emitters[i].EntityId && SwordReferences.Contains(e.EntityId)) is { } stated)
				{
					read.Emitters[i] = stated;
				}
			}

			for (int i = 0; i < read.Systems.Count; i++)
			{
				if (effect.Systems.FirstOrDefault(x => x.Id == read.Systems[i].Id && SwordReferences.Contains(x.Id)) is { } stated)
				{
					read.Systems[i] = stated;
				}
			}

			foreach (FxSystemDef s in read.Systems)
			{
				if (aligns.TryGetValue(s.Id, out float[]? q))
				{
					foreach (FxVisual v in s.Visuals.Where(v => v.HasMesh))
					{
						v.HasMeshAlign = true;
						v.MeshAlign = (float[])q.Clone();
					}
				}
			}

			string back = Engine_FxEffectJson.Serialize(read).Replace("\r\n", "\n");
			string cleanText = text.Replace("\r\n", "\n");
			string[] want = cleanText.Split('\n'), got = back.Split('\n');
			int first = Enumerable.Range(0, Math.Min(want.Length, got.Length)).FirstOrDefault(i => want[i] != got[i], -1);
			string where = first < 0 ? "(lengths differ)" : $"line {first + 1}: '{got[first].Trim()}' for '{want[first].Trim()}'";
			AssertThat(back).OverrideFailureMessage($"{effect.Name}: the guest's bank reads it back differently, {where}").IsEqual(cleanText);
		}
	}

	[TestCase]
	public void EveryRecipeTintsTheBaseOfOneLook()
	{
		if (ModFolder() is not { } folder)
		{
			return;
		}

		var names = Effects(folder).Select(e => e.Effect.Name).ToHashSet(StringComparer.Ordinal);
		string path = Path.Combine(folder, "fx", "recipes.json");
		string text = File.ReadAllText(path);
		AssertThat(ModValidate.Validate("fx/recipes.json", text).Count).IsEqual(0);
		using JsonDocument doc = JsonDocument.Parse(text);
		var bound = new HashSet<string>(StringComparer.Ordinal);
		foreach (JsonElement recipe in doc.RootElement.GetProperty("recipes").EnumerateArray())
		{
			string id = recipe.GetProperty("id").GetString()!;
			string with = recipe.GetProperty("with").GetString()!;
			AssertThat(names.Contains(with)).OverrideFailureMessage($"{id}: with '{with}' is no effect of the mod").IsTrue();
			AssertThat(with).IsEqual(id.Replace('.', '_'));
			foreach (string dropped in new[] { "pieces", "when", "light", "attach", "anchor", "entities" })
			{
				AssertThat(recipe.TryGetProperty(dropped, out _)).OverrideFailureMessage($"{id}: '{dropped}' — the look is the played effect").IsFalse();
			}

			bound.Add(with);
		}

		AssertThat(bound.SetEquals(names)).OverrideFailureMessage("every look has its recipe").IsTrue();
	}

	[TestCase]
	public void TheValidatorTakesWithForWhen()
	{
		IReadOnlyList<ModIssue> ok = ModValidate.Validate("fx/recipes.json",
			"{ \"recipes\": [ { \"id\": \"m.a\", \"with\": \"m_look\", \"withRadius\": 6, \"base\": [\"fxspl_vortex_orb\"] } ] }");
		AssertThat(ok.Count).IsEqual(0);
		IReadOnlyList<ModIssue> neither = ModValidate.Validate("fx/recipes.json",
			"{ \"recipes\": [ { \"id\": \"m.a\", \"base\": [\"fxspl_vortex_orb\"] } ] }");
		AssertThat(neither.Any(i => i.Error && i.Message.Contains("\"with\"", StringComparison.Ordinal))).IsTrue();
		IReadOnlyList<ModIssue> table = ModValidate.Validate("fx/recipes.json",
			"{ \"instances\": \"mod:m.inst_\", \"recipes\": [ { \"id\": \"m.a\", \"when\": \"mod:m.look\", \"base\": [\"x\"] } ] }");
		AssertThat(table.Any(i => i.Pointer == "instances")).OverrideFailureMessage("the instance table is gone").IsTrue();
	}
}
