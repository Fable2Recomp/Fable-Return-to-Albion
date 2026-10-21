#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using Refii.Fx;
using Refii.Mods;

namespace Refii.EditorTools;

/// <summary>
/// Effects on the script API: the recipes (<c>fx/recipes.json</c>, <see cref="Engine_FxRecipes"/>)
/// and the mod's own effects (<c>fx/effects/&lt;name&gt;.json</c>, <see cref="Engine_FxModEffects"/>)
/// — the same files the FX Lab writes, through the same validated package writer.
/// </summary>
public partial class RefiiModApi
{
	// ---- recipes ------------------------------------------------------------------------------

	/// <summary>The recipe ids in the active mod's fx/recipes.json, in file order.</summary>
	public string[] ListRecipes() =>
		Engine_FxRecipes.Parse(GetActiveMod(), ReadText(Engine_FxRecipes.RecipesPath)).Select(r => r.Id).ToArray();

	/// <summary>One recipe record as the file states it, or an empty Dictionary.</summary>
	public Godot.Collections.Dictionary GetRecipe(string id)
	{
		Godot.Collections.Dictionary root = ReadJson(Engine_FxRecipes.RecipesPath);
		foreach (Variant item in List(root, "recipes"))
		{
			if (item.VariantType == Variant.Type.Dictionary &&
				item.AsGodotDictionary().TryGetValue("id", out Variant v) && v.AsString() == id)
			{
				return item.AsGodotDictionary();
			}
		}

		return new Godot.Collections.Dictionary();
	}

	/// <summary>
	/// A whole recipe by its <c>id</c>, added or replacing the one with that id: <c>when</c>,
	/// <c>equals</c>, <c>base</c>, <c>tint</c>, <c>intensity</c>, <c>size</c>, <c>attach</c>, <c>anchor</c>,
	/// <c>light</c>, <c>pieces</c>, <c>everyNode</c>, <c>entities</c>, <c>instances</c>,
	/// <c>instanceRadius</c> — the schema Engine_FxRecipes reads.
	/// </summary>
	public string SetRecipe(Godot.Collections.Dictionary recipe) =>
		Upsert(Engine_FxRecipes.RecipesPath, "recipes", "id", recipe);

	public string RemoveRecipe(string id) =>
		Remove(Engine_FxRecipes.RecipesPath, new[] { "recipes" }, "id", id);

	// ---- the mod's own effects ------------------------------------------------------------------

	/// <summary>Every effect the particle bank draws (shipped and the enabled mods'), by name or FX_ token.</summary>
	public string[] ListBankEffects() =>
		FableFx.SharedBank() is { } bank
			? bank.Effects.Select(e => e.Name.Length != 0 ? e.Name : $"FX_{e.NameHash:X8}").OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray()
			: Array.Empty<string>();

	/// <summary>The effects the active mod ships, by name (one per fx/effects/*.json entry).</summary>
	public string[] ListModEffects() =>
		ListFiles()
			.Where(path => Engine_FxModEffects.IsEffectFile(path, out _))
			.SelectMany(path => Engine_FxEffectJson.Parse(ReadText(path), new List<Engine_FxEffectJson.Issue>()))
			.Select(d => d.Name)
			.ToArray();

	/// <summary>
	/// The file text of effect <paramref name="name"/>: the active mod's own when it ships one, else
	/// the bank's effect extracted (ids kept). Empty when neither holds it.
	/// </summary>
	public string GetEffectJson(string name)
	{
		if (ReadModEffect(name) is { } own)
		{
			return Engine_FxEffectJson.Serialize(own);
		}

		return FableFx.SharedBank() is { } bank && Engine_BnkFxLoader.Resolve(bank, name) is { } effect
			? Engine_FxEffectJson.Serialize(Engine_FxEffectJson.Extract(bank, effect, name))
			: string.Empty;
	}

	/// <summary>
	/// Writes an effect file (one effect object) as <c>fx/effects/&lt;its name&gt;.json</c>, checked by
	/// the reader first. A new name adds an effect; a shipped name, or an emitter carrying a shipped
	/// <c>entityId</c>, replaces it in the bank (Engine_FxModEffects).
	/// </summary>
	public string SetEffectJson(string json)
	{
		var issues = new List<Engine_FxEffectJson.Issue>();
		List<Engine_FxEffectJson.Definition> parsed = Engine_FxEffectJson.Parse(json, issues);
		if (issues.FirstOrDefault(i => i.Error) is { Message: not null } error && issues.Any(i => i.Error))
		{
			return (error.Pointer.Length != 0 ? error.Pointer + ": " : string.Empty) + error.Message;
		}

		if (parsed.Count != 1)
		{
			return "an effect file written here holds one effect object";
		}

		return WriteEffect(parsed[0]);
	}

	/// <summary><see cref="SetEffectJson"/> from a Dictionary of the same shape.</summary>
	public string SetEffect(Godot.Collections.Dictionary effect) => SetEffectJson(Json.Stringify(effect));

	/// <summary>
	/// A NEW effect <paramref name="name"/> in the active mod, copied from <paramref name="source"/>
	/// (the bank's or a mod's): every emitter and system takes a fresh id, so nothing shipped changes.
	/// </summary>
	public string CloneEffect(string source, string name)
	{
		if (SourceEffect(source) is not { } d)
		{
			return $"no effect '{source}' in the bank or the active mod";
		}

		return WriteEffect(Engine_FxEffectJson.AsNew(d, name));
	}

	/// <summary>
	/// The bank's effect <paramref name="name"/> written into the active mod as it stands, ids kept:
	/// an OVERRIDE — edit it and every placement and live spawn of that effect (and of any effect
	/// sharing its emitters) draws the change.
	/// </summary>
	public string OverrideEffect(string name)
	{
		if (FableFx.SharedBank() is not { } bank || Engine_BnkFxLoader.Resolve(bank, name) is not { } effect)
		{
			return $"no effect '{name}' in the bank";
		}

		return WriteEffect(Engine_FxEffectJson.Extract(bank, effect, name));
	}

	public string RemoveEffect(string name) => DeleteFile(Engine_FxModEffects.PathOf(name));

	/// <summary>
	/// One field of emitter <paramref name="emitter"/> of the active mod's effect <paramref name="effect"/>,
	/// by its file key (<c>maxActive</c>, <c>spawnRadius</c>, <c>attachToRoot</c>, <c>pos</c> …).
	/// </summary>
	public string SetEmitterField(string effect, int emitter, string field, Variant value) =>
		EditEmitter(effect, emitter, e => SetField(e, field, value));

	/// <summary>
	/// A generic parameter's timeline (<c>rate</c>, <c>speed</c>, <c>gravityScale</c>, <c>lightRange</c>
	/// … or <c>p&lt;index&gt;</c>) as <c>[[seconds, value], …]</c>; an empty list clears it to its default.
	/// </summary>
	public string SetEmitterKeys(string effect, int emitter, string param, Godot.Collections.Array keys)
	{
		int index = Array.IndexOf(Engine_FxEffectJson.ParamNames, param);
		if (index < 0 && !(param.Length > 1 && param[0] == 'p' && int.TryParse(param.AsSpan(1), out index) && index is >= 0 and < 28))
		{
			return $"'{param}' is no parameter ({string.Join(", ", Engine_FxEffectJson.ParamNames)})";
		}

		var list = new List<(float, float)>();
		foreach (Variant key in keys)
		{
			if (key.VariantType == Variant.Type.Array && key.AsGodotArray() is { Count: >= 2 } pair)
			{
				list.Add(((float)pair[0].AsDouble(), (float)pair[1].AsDouble()));
			}
		}

		return EditEmitter(effect, emitter, e =>
		{
			e.Generic[index] = Engine_FxBank.MakeTimeline(index, list);
			Engine_FxBank.LinkParameters(e);
			return string.Empty;
		});
	}

	/// <summary>
	/// The active mod's effect <paramref name="name"/> saved from a <see cref="FableFx"/> as the
	/// inspector left it: its effect copied as a new one, with the node's <c>EffectScale</c> and
	/// <c>Tint</c> carried as the effect's own authored scale and tint (applied where the node's
	/// are), so the saved effect plays as the node looked.
	/// </summary>
	public string SaveFableFxAsEffect(FableFx node, string name)
	{
		if (SourceEffect(node.EffectName) is not { } d)
		{
			return $"no effect '{node.EffectName}' in the bank";
		}

		Engine_FxEffectJson.Definition saved = Engine_FxEffectJson.AsNew(d, name);
		saved.Scale *= node.EffectScale > 0.0001f ? node.EffectScale : 1f;
		saved.Tint = new[] { saved.Tint[0] * node.Tint.R, saved.Tint[1] * node.Tint.G, saved.Tint[2] * node.Tint.B };
		return WriteEffect(saved);
	}

	/// <summary>An effect as a definition: the active mod's own file first, else the bank's.</summary>
	public Engine_FxEffectJson.Definition? SourceEffect(string name)
	{
		if (ReadModEffect(name) is { } own)
		{
			return own;
		}

		return FableFx.SharedBank() is { } bank && Engine_BnkFxLoader.Resolve(bank, name) is { } effect
			? Engine_FxEffectJson.Extract(bank, effect, name)
			: null;
	}

	/// <summary>Writes a definition as fx/effects/&lt;name&gt;.json.</summary>
	public string WriteEffect(Engine_FxEffectJson.Definition d)
	{
		if (d.Name.Length == 0)
		{
			return "the effect needs a name";
		}

		return WriteText(Engine_FxModEffects.PathOf(d.Name), Engine_FxEffectJson.Serialize(d));
	}

	private Engine_FxEffectJson.Definition? ReadModEffect(string name)
	{
		string text = ReadText(Engine_FxModEffects.PathOf(name));
		if (text.Length == 0)
		{
			return null;
		}

		return Engine_FxEffectJson.Parse(text, new List<Engine_FxEffectJson.Issue>())
			.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
	}

	private string EditEmitter(string effect, int emitter, Func<FxEmitter, string> edit)
	{
		if (ReadModEffect(effect) is not { } d)
		{
			return $"the active mod has no effect '{effect}' (CloneEffect or OverrideEffect first)";
		}

		if (emitter < 0 || emitter >= d.Emitters.Count)
		{
			return $"'{effect}' has emitters 0..{d.Emitters.Count - 1}";
		}

		string error = edit(d.Emitters[emitter]);
		return error.Length != 0 ? error : WriteEffect(d);
	}

	/// <summary>A plain model field by its file key, from a script's Variant.</summary>
	private static string SetField(object target, string key, Variant value)
	{
		FieldInfo? field = Engine_FxEffectJson.PlainFields(target.GetType())
			.FirstOrDefault(f => Engine_FxEffectJson.KeyOf(f.Name) == key);
		if (field is null)
		{
			return $"'{key}' is not a field of {target.GetType().Name}";
		}

		object? converted = field.FieldType switch
		{
			Type t when t == typeof(float) => (float)value.AsDouble(),
			Type t when t == typeof(int) => (int)value.AsInt64(),
			Type t when t == typeof(uint) => value.VariantType == Variant.Type.String
				? Convert.ToUInt32(value.AsString().Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase), 16)
				: (uint)value.AsInt64(),
			Type t when t == typeof(bool) => value.AsBool(),
			Type t when t == typeof(string) => value.AsString(),
			Type t when t == typeof(float[]) => value.AsGodotArray().Select(v => (float)v.AsDouble()).ToArray(),
			_ => null,
		};
		if (converted is null)
		{
			return $"'{key}' is not set this way";
		}

		if (converted is float[] array && field.GetValue(target) is float[] old && array.Length != old.Length)
		{
			return $"'{key}' holds {old.Length} numbers";
		}

		field.SetValue(target, converted);
		return string.Empty;
	}
}
#endif
