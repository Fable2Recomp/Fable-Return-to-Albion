namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Fx;
using Refii.Mods;

/// <summary>
/// The guest's side of mod effects and the front end's mod Lua, as far as it is pure logic: the
/// dress table the particle hooks read (Fable2/src/vfs/src/hooks/particle_dress.cpp), the bank reload's
/// trigger, effects carrying pieces through the bank's model and its file form, a recipe's pieces
/// stated as an effect, and the front-end natives agreeing with the payload's table
/// (Fable2/src/vfs/src/hooks/mod_frontend_lua.cpp). No Godot node is constructed.
/// </summary>
[TestSuite]
public class FxGuestDressTests
{
	private static string Msg(string text) => text.Length == 0 ? "(no detail)" : text;

	private static string? RepoRoot()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 16 && at != null; i++)
		{
			if (File.Exists(Path.Combine(at, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")))
			{
				return at;
			}

			at = Path.GetDirectoryName(at);
		}

		return null;
	}

	private static Engine_FxModEffects.Source Effect(string mod, string name, float scale, float[] tint)
	{
		var d = new Engine_FxEffectJson.Definition { Name = name, Scale = scale, Tint = tint };
		d.Emitters.Add(new FxEmitter { TypeId = 0 });
		d.Parts.Add(new FxEffectPart(0, -1, 0, 0));
		return new Engine_FxModEffects.Source(mod, "fx/effects/" + name + ".json", d);
	}

	private static Engine_FxRecipePieces.Piece Piece(string kind) => new(
		kind, "fxt_flames_double_03", new Godot.Color(1f, 0.5f, 0.2f, 1f), new Godot.Color(0.4f, 0f, 0f, 0f),
		3f, 0.3f, 1.4f, 12f, 4f, 0.5f, 0.15f, 96, 1.2f, 0.12f, string.Empty, 20f, 1.3f);

	[TestCase]
	public void TheDressTableHoldsOnlyEffectsWithAScaleOrTint()
	{
		var sources = new List<Engine_FxModEffects.Source>
		{
			Effect("a", "plain_effect", 1f, new[] { 1f, 1f, 1f }),
			Effect("a", "big_green", 2f, new[] { 0.2f, 1f, 0.2f }),
			Effect("b", "tinted_only", 1f, new[] { 1f, 0f, 0f }),
		};
		string text = Engine_FxModEffects.DressText(sources);
		string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		AssertThat(lines.Length).IsEqual(2);

		// The hash is the bank key, "0x%08X"; then scale r g b, as the payload's sscanf reads them.
		var line = new Regex(@"^0x([0-9A-F]{8}) (\S+) (\S+) (\S+) (\S+)$");
		var byHash = new Dictionary<uint, float[]>();
		foreach (string l in lines)
		{
			Match m = line.Match(l);
			AssertThat(m.Success).OverrideFailureMessage(Msg(l)).IsTrue();
			byHash[Convert.ToUInt32(m.Groups[1].Value, 16)] = Enumerable.Range(2, 4)
				.Select(g => float.Parse(m.Groups[g].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
		}

		AssertThat(byHash[Engine_FxEffectJson.HashOf("big_green")]).IsEqual(new[] { 2f, 0.2f, 1f, 0.2f });
		AssertThat(byHash[Engine_FxEffectJson.HashOf("tinted_only")]).IsEqual(new[] { 1f, 1f, 0f, 0f });
		AssertThat(byHash.ContainsKey(Engine_FxEffectJson.HashOf("plain_effect"))).IsFalse();
	}

	[TestCase]
	public void TheLaterSourceOfAnEffectStatesItsDress()
	{
		var sources = new List<Engine_FxModEffects.Source>
		{
			Effect("low", "shared_name", 3f, new[] { 1f, 1f, 1f }),
			Effect("high", "shared_name", 1f, new[] { 1f, 1f, 1f }),
		};

		// The higher-priority mod put it back to identity, which states nothing.
		AssertThat(Engine_FxModEffects.DressText(sources)).IsEqual(string.Empty);
		AssertThat(Engine_FxModEffects.DressText(new List<Engine_FxModEffects.Source>())).IsEqual(string.Empty);
	}

	[TestCase]
	public void SpinAndMeshAlignReachTheGuestByTheIdsItsBankCarries()
	{
		var d = new Engine_FxEffectJson.Definition { Name = "spinner" };
		d.Emitters.Add(new FxEmitter { TypeId = 0, Spin = 2.5f });
		d.Emitters.Add(new FxEmitter { TypeId = 0, EntityId = 0x12345678u });
		d.Emitters.Add(new FxEmitter { TypeId = 4, EntityId = 0x0A0B0C0Du, Spin = 1f });
		var mesh = new FxSystemDef { Id = 0x0BADF00Du, Mode = 4 };
		mesh.Visuals.Add(new FxVisual { HasMesh = true, MeshPath = "x.mdl", HasMeshAlign = true, MeshAlign = new[] { 0f, 0f, 2f, 0f } });
		var sprite = new FxSystemDef();
		sprite.Visuals.Add(new FxVisual { HasTexture = true, Texture = "t.tex", HasMeshAlign = true });
		d.Systems.Add(mesh);
		d.Systems.Add(sprite);
		d.Parts.Add(new FxEffectPart(0, 0, 0, 0));
		d.Parts.Add(new FxEffectPart(1, 1, 0, 1));
		string text = Engine_FxModEffects.DressText(new List<Engine_FxModEffects.Source> { new("a", "fx/effects/spinner.json", d) });

		// An emitter without an id takes the one Apply derives; only emitters spin (an attractor
		// does not); only a system whose first visual is a mesh is aligned, the quaternion unit.
		uint derived = Engine_FxEffectJson.DerivedId("spinner", "emitter", 0);
		AssertThat(text).IsEqual($"spin 0x{derived:X8} 2.5\nalign 0x0BADF00D 0 0 1 0\n");

		// The guest's reader (particle_dress.cpp ReadDressLocked) keys these lines by their word.
		string? root = RepoRoot();
		if (root is not null)
		{
			string reader = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "particle_dress.cpp"));
			AssertThat(reader.Contains("std::strncmp(line, \"spin \", 5)") && reader.Contains("\"%x %f\"")).IsTrue();
			AssertThat(reader.Contains("std::strncmp(line, \"align \", 6)") && reader.Contains("\"%x %f %f %f %f\"")).IsTrue();
		}
	}

	[TestCase]
	public void TheDressKeyIsWhatThePayloadReads()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string dress = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "particle_dress.cpp"));
		AssertThat(dress.Contains($"\"{Engine_FxModEffects.DressKey}\"")).IsTrue();
		string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
		AssertThat(commands.Contains($"op == \"{Engine_FxModEffects.ReloadCommand}\"")).IsTrue();
		AssertThat(commands.Contains($"\"{ModLua.FrontendCommand.Trim()}\"")).IsTrue();
	}

	[TestCase]
	public void TheGuestBanksFilesAreKnownToTheRemount()
	{
		AssertThat(Engine_FxModEffects.IsGuestBankFile("data/art/particles/particle_bank.bnk")).IsTrue();
		AssertThat(Engine_FxModEffects.IsGuestBankFile("Art\\Particles\\PARTICLE_BANK.BNK")).IsTrue();
		AssertThat(Engine_FxModEffects.IsGuestBankFile(Engine_FxModEffects.DressKey)).IsTrue();
		AssertThat(Engine_FxModEffects.IsGuestBankFile("data/art/particles/other.bnk")).IsFalse();
		AssertThat(Engine_FxModEffects.IsGuestBankFile(ModLua.FrontendKey)).IsFalse();
	}

	[TestCase]
	public void PiecesTravelThroughTheEffectFileForm()
	{
		var d = new Engine_FxEffectJson.Definition { Name = "my_mod_vortex" };
		d.Pieces.Add(Piece("embers"));
		d.Pieces.Add(Piece("arcs") with { Blend = "mix", Mesh = "art/x.mdl" });
		string text = Engine_FxEffectJson.Serialize(d);
		var issues = new List<Engine_FxEffectJson.Issue>();
		List<Engine_FxEffectJson.Definition> back = Engine_FxEffectJson.Parse(text, issues);

		// Pieces alone draw something, so there is no "no parts" note.
		AssertThat(issues.Count).OverrideFailureMessage(Msg(string.Join("; ", issues.Select(i => i.Pointer + " " + i.Message)))).IsEqual(0);
		AssertThat(back.Count).IsEqual(1);
		AssertThat(back[0].Pieces.SequenceEqual(d.Pieces)).IsTrue();

		issues.Clear();
		Engine_FxEffectJson.Parse("{ \"name\": \"x\", \"pieces\": [ { \"kind\": \"sparkles\" } ] }", issues);
		AssertThat(issues.Any(i => i.Error && i.Pointer == "pieces[0].kind")).IsTrue();
	}

	[TestCase]
	public void TheOverlayCarriesPiecesOntoTheBanksEffect()
	{
		var d = new Engine_FxEffectJson.Definition { Name = "recipe.pieces" };
		d.Pieces.Add(Piece("ring"));
		var bank = new FxBank();
		Engine_FxModEffects.Apply(bank, new[] { new Engine_FxModEffects.Source("m", "x", d) });
		FxEffect? effect = bank.FindByName("recipe.pieces");
		AssertThat(effect is not null).IsTrue();
		AssertThat(effect!.Parts.Count).IsEqual(0);
		AssertThat(effect.Pieces.Single()).IsEqual(Piece("ring"));

		// ...and an extract of it keeps them.
		AssertThat(Engine_FxEffectJson.Extract(bank, effect).Pieces.Single()).IsEqual(Piece("ring"));
	}

	[TestCase]
	public void ARecipesPiecesAreAnEffectNamedForIt()
	{
		Engine_FxRecipes.Recipe recipe = Engine_FxRecipes.Make(
			"spell_weaving.vortex fire", "spell_weaving", new[] { "mod:x" }, 1f, new[] { "fxspl_vortex_orb" },
			Godot.Colors.White, 1f, 1f, Array.Empty<Engine_FxRecipes.Attachment>(),
			pieces: new[] { Piece("embers"), Piece("blades") });
		AssertThat(Engine_FxRecipes.PiecesEffectName(recipe)).IsEqual("spell_weaving.vortex_fire.pieces");
		Engine_FxEffectJson.Definition d = Engine_FxRecipes.PiecesDefinition(recipe);
		AssertThat(d.Pieces.Select(p => p.Kind).ToArray()).IsEqual(new[] { "embers", "blades" });

		// The definition is a valid effect file of its own.
		var issues = new List<Engine_FxEffectJson.Issue>();
		AssertThat(Engine_FxEffectJson.Parse(Engine_FxEffectJson.Serialize(d), issues).Count).IsEqual(1);
		AssertThat(issues.Count(i => i.Error)).IsEqual(0);
	}

	[TestCase]
	public void TheFrontEndNativesAreThePayloadsTable()
	{
		var mapped = Regex.Matches(ModLua.FrontendNatives, @"= (__refii_\w+)").Select(m => m.Groups[1].Value).ToHashSet();
		// 13 and __refii_text (Refii.text's lookup).
		AssertThat(mapped.Count).IsEqual(14);
		bool compiles = Refii.Lua.Engine_LuaDecompiler.TryCheckSource(ModLua.FrontendNatives, "@frontend", out string error);
		AssertThat(compiles || error.Contains("not available", StringComparison.Ordinal)).OverrideFailureMessage(Msg(error)).IsTrue();
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string cpp = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_frontend_lua.cpp"));
		var registered = Regex.Matches(cpp, "\\{\"(__refii_\\w+)\", &Native").Select(m => m.Groups[1].Value).ToHashSet();
		registered.Remove("__refii_need_init");
		AssertThat(registered.SetEquals(mapped)).OverrideFailureMessage(Msg(string.Join(",", registered) + " vs " + string.Join(",", mapped))).IsTrue();
		AssertThat(cpp.Contains($"\"{ModLua.FrontendKey}\"")).IsTrue();
	}
}
