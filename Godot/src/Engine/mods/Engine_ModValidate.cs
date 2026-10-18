using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Refii.Mods;

/// <summary>
/// One problem in one file of a mod. <see cref="Pointer"/> is the place inside the document —
/// <c>pieces[2].value</c>, <c>souls_combat.swing_cost</c>, <c>requires (line 4)</c> — and empty for
/// the file as a whole. An ERROR is content the loader refuses, drops or reads as something other
/// than what it says; a WARNING is content the loader ignores or corrects on its own.
/// </summary>
public sealed record ModIssue(string Path, string Pointer, bool Error, string Message)
{
	/// <summary><c>path: pointer: message</c>, the pointer left out when the issue is the file's.</summary>
	public override string ToString() =>
		Pointer.Length == 0 ? $"{Path}: {Message}" : $"{Path}: {Pointer}: {Message}";
}

/// <summary>
/// THE SCHEMA CHECK for every content kind a mod ships, run before the game loads it (once per
/// mount) and when the Mod Studio writes it (a write with an error is refused). Each kind is
/// checked against what ITS LOADER reads — the loader is the schema, not a document: required
/// fields, types the loader accepts, ranges, the enum values it knows, and unknown keys (which
/// the loaders ignore silently, so they are warnings).
/// </summary>
/// <remarks>
/// <para>Pure .NET over <see cref="System.Text.Json"/>, so tests run it without a Godot type in the
/// host. The loaders read most kinds with Godot's <c>Json.ParseString</c>, whose numbers are all
/// floats: a whole number written <c>5.0</c> is an integer here too, and a field the loader reads
/// with <c>AsString</c>/<c>AsDouble</c> accepts what those convert, with a warning where the
/// conversion is surprising. A field the loader reads only when its Variant type matches is
/// STRICT: anything else is dropped, which is an error.</para>
/// <para>Not checked here, because only the engine can: key names in <c>input.json</c>
/// (<c>OS.FindKeycodeFromString</c>), menu source names (registered at runtime in
/// <c>Engine_UiMenuSources</c>), whether a texture or effect name exists, and whether a
/// <c>mod:</c> value is ever published.</para>
/// </remarks>
public static partial class ModValidate
{
	/// <summary><c>spells/&lt;id&gt;.json</c>, the one kind matched by folder rather than by path.</summary>
	public const string SpellsKind = "spells/*.json";

	/// <summary>
	/// The kind of a package path, by the path its loader reads (<c>files/</c> stripped, any case);
	/// empty for a path no schema covers.
	/// </summary>
	public static string KindOf(string path)
	{
		string normalized = Normalize(path);
		switch (normalized)
		{
			case ModSettings.ModSettingsPath:
			case ModSettingsUi.ModSettingsUiPath:
			case ModInput.ModInputPath:
			case Refii.Fx.Engine_FxRecipes.RecipesPath:
			case Refii.Ui.Engine_HudModPieces.ModHudPath:
			case Refii.Ui.Engine_UiTemplates.ModTemplatesPath:
			case Refii.Ui.Engine_UiSpellAssign.SecondaryPath:
			case Refii.Ui.Engine_UiModAbilities.ModAbilitiesPath:
			case ModWorkspace.ModMetadataFilename:
				return normalized;
		}

		return normalized.StartsWith(ModSpells.Folder, StringComparison.Ordinal) &&
			normalized.EndsWith(".json", StringComparison.Ordinal) &&
			normalized.Length > ModSpells.Folder.Length + ".json".Length &&
			normalized.IndexOf('/', ModSpells.Folder.Length) < 0
				? SpellsKind
				: string.Empty;
	}

	/// <summary>A package path as the package and the loaders match it: forward slashes, lower case, no <c>files/</c>.</summary>
	public static string Normalize(string path)
	{
		string normalized = path.Replace('\\', '/').Trim('/').ToLowerInvariant();
		return normalized.StartsWith("files/", StringComparison.Ordinal) ? normalized["files/".Length..] : normalized;
	}

	/// <summary>Every issue of one file; empty when the file is clean or its path is no checked kind.</summary>
	public static IReadOnlyList<ModIssue> Validate(string path, string text)
	{
		string kind = KindOf(path);
		if (kind.Length == 0)
		{
			return Array.Empty<ModIssue>();
		}

		var doc = new Doc(Normalize(path));
		bool bom = text.Length != 0 && text[0] == '﻿';
		if (kind == ModWorkspace.ModMetadataFilename)
		{
			if (bom)
			{
				doc.Error(string.Empty, "the file starts with a byte-order mark, which becomes part of the first key; " +
					"save it as UTF-8 without a BOM");
				text = text[1..];
			}

			CheckManifest(doc, text);
			return doc.Issues;
		}

		if (bom)
		{
			doc.Warn(string.Empty, "the file starts with a UTF-8 byte-order mark; save it without one " +
				"(Godot's JSON reader is not guaranteed to skip it)");
			text = text[1..];
		}

		// spells/*.json is read with System.Text.Json allowing comments and trailing commas
		// (ModSpells.TryParse); every other kind with Godot's JSON reader, which allows neither.
		bool lenient = kind == SpellsKind;
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(text, new JsonDocumentOptions
			{
				AllowTrailingCommas = lenient,
				CommentHandling = lenient ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow,
			});
		}
		catch (JsonException exception)
		{
			doc.Error(string.Empty, NotJson(exception, lenient));
			return doc.Issues;
		}

		using (document)
		{
			JsonElement root = document.RootElement;
			switch (kind)
			{
				case ModSettings.ModSettingsPath: CheckSettings(doc, root); break;
				case ModSettingsUi.ModSettingsUiPath: CheckSettingsUi(doc, root); break;
				case ModInput.ModInputPath: CheckInput(doc, root); break;
				case SpellsKind: CheckSpell(doc, root); break;
				case Refii.Fx.Engine_FxRecipes.RecipesPath: CheckRecipes(doc, root); break;
				case Refii.Ui.Engine_HudModPieces.ModHudPath: CheckHud(doc, root); break;
				case Refii.Ui.Engine_UiTemplates.ModTemplatesPath: CheckTemplates(doc, root); break;
				case Refii.Ui.Engine_UiSpellAssign.SecondaryPath: CheckSpellWheel(doc, root); break;
				case Refii.Ui.Engine_UiModAbilities.ModAbilitiesPath: CheckAbilities(doc, root); break;
			}
		}

		return doc.Issues;
	}

	private static string NotJson(JsonException exception, bool lenient)
	{
		string message = exception.Message;
		foreach (string cut in new[] { " Change the reader options.", " Path:", " LineNumber:" })
		{
			int at = message.IndexOf(cut, StringComparison.Ordinal);
			if (at > 0)
			{
				message = message[..at];
			}
		}

		string where = exception.LineNumber is { } line
			? $" at line {line + 1}, column {(exception.BytePositionInLine ?? 0) + 1}"
			: string.Empty;
		return $"not valid JSON{where}: {message.Trim()}" +
			(lenient ? string.Empty : " (Godot's JSON reader takes no comments and no trailing commas)");
	}

	// ---------------------------------------------------------------------------------------
	// settings.json — ModSettings.Load / Take
	// ---------------------------------------------------------------------------------------

	/// <summary>
	/// An object of numbers keyed <c>&lt;mod&gt;.&lt;name&gt;</c>, one level of nesting folded with a
	/// dot. A value is a number, true/false (1/0) or a number in a string; anything else, or a
	/// second level of nesting, is ignored by the loader.
	/// </summary>
	private static void CheckSettings(Doc doc, JsonElement root)
	{
		if (!doc.Object(root, string.Empty,
			"must be a JSON object of settings: { \"<mod>\": { \"<name>\": 1.5 } } or { \"<mod>.<name>\": 1.5 }; the file is ignored"))
		{
			return;
		}

		var folded = new HashSet<string>(StringComparer.Ordinal);
		foreach (JsonProperty entry in root.EnumerateObject())
		{
			string at = At(string.Empty, entry.Name);
			if (entry.Value.ValueKind == JsonValueKind.Object)
			{
				if (!entry.Value.EnumerateObject().Any())
				{
					doc.Warn(at, "states no settings");
				}

				foreach (JsonProperty inner in entry.Value.EnumerateObject())
				{
					SettingValue(doc, At(at, inner.Name), entry.Name + "." + inner.Name, inner.Value, folded);
				}
			}
			else
			{
				SettingValue(doc, at, entry.Name, entry.Value, folded);
			}
		}
	}

	private static void SettingValue(Doc doc, string at, string key, JsonElement value, HashSet<string> folded)
	{
		if (key.Trim().Trim('.').Length == 0)
		{
			doc.Warn(at, "a setting with no name");
		}

		if (!folded.Add(key))
		{
			doc.Warn(at, $"'{key}' is stated twice (a nested and a dotted key fold to the same name); the last one is read");
		}

		switch (value.ValueKind)
		{
			case JsonValueKind.Number:
			case JsonValueKind.True:
			case JsonValueKind.False:
				return;
			case JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _):
				doc.Warn(at, "a number written as text; write it without quotes");
				return;
			case JsonValueKind.Object:
				doc.Error(at, "settings nest one level only (<mod>.<name>); this one is ignored");
				return;
			default:
				doc.Error(at, $"must be a number, not {Describe(value)}; the setting is ignored");
				return;
		}
	}

	// ---------------------------------------------------------------------------------------
	// settings.ui.json — ModSettingsUi.Build / Describe
	// ---------------------------------------------------------------------------------------

	private static readonly string[] SettingsUiRowKeys = { "label", "description", "type", "min", "max", "step" };

	private static readonly string[] SettingsUiTypes = { "slider", "toggle" };

	/// <summary>
	/// An object keyed by setting name (without or with the <c>&lt;mod&gt;.</c> prefix), each an object
	/// of <c>label</c>/<c>description</c> (strings), <c>type</c> (slider or toggle) and
	/// <c>min</c>/<c>max</c>/<c>step</c> (numbers). Every field is read only at its own type.
	/// </summary>
	private static void CheckSettingsUi(Doc doc, JsonElement root)
	{
		if (!doc.Object(root, string.Empty, "must be a JSON object of rows keyed by setting name; the file is ignored"))
		{
			return;
		}

		doc.DuplicateKeys(root, string.Empty);
		foreach (JsonProperty entry in root.EnumerateObject())
		{
			string at = At(string.Empty, entry.Name);
			if (!doc.Object(entry.Value, at,
				"must be an object { \"label\", \"type\", \"min\", \"max\", \"step\", \"description\" }; the row is built from the value alone"))
			{
				continue;
			}

			JsonElement row = entry.Value;
			doc.Keys(row, at, SettingsUiRowKeys);
			doc.Text(row, "label", at, strict: true);
			doc.Text(row, "description", at, strict: true);
			string type = (doc.Text(row, "type", at, strict: true) ?? string.Empty).Trim().ToLowerInvariant();
			if (type.Length != 0 && Array.IndexOf(SettingsUiTypes, type) < 0)
			{
				doc.Error(At(at, "type"), $"'{type}' is not a row type: slider or toggle (it is drawn as a slider)");
			}

			double? min = doc.Number(row, "min", at, strict: true);
			double? max = doc.Number(row, "max", at, strict: true);
			double? step = doc.Number(row, "step", at, strict: true);
			if (type == "toggle" && (Has(row, "min") || Has(row, "max") || Has(row, "step")))
			{
				doc.Warn(at, "a toggle steps 0 and 1; min, max and step are ignored");
			}

			if (min is { } low && max is { } high && high <= low)
			{
				doc.Warn(At(at, "max"), $"must be above min ({Format(low)}); the page widens the range itself");
			}

			if (step is <= 0)
			{
				doc.Warn(At(at, "step"), "must be above 0; the page steps a twentieth of the range instead");
			}
		}
	}

	// ---------------------------------------------------------------------------------------
	// input.json — ModInput.Load / Parse / TryButton / TryTrigger
	// ---------------------------------------------------------------------------------------

	private static readonly string[] InputKeys = { "label", "keys", "buttons", "axis" };

	/// <summary>
	/// ModInput.TryButton's Xbox aliases and TryTrigger's trigger names. Mirrored, not called:
	/// they are a switch over Godot's <c>JoyButton</c>/<c>JoyAxis</c>, and a Godot type cannot be
	/// touched here.
	/// </summary>
	private static readonly string[] PadAliases =
	{
		"a", "b", "x", "y", "lb", "rb", "l3", "r3", "start", "back", "select", "lt", "rt", "triggerleft", "triggerright",
	};

	/// <summary>Godot 4.7's <c>JoyButton</c> names (Invalid excluded), which <c>Enum.TryParse</c> accepts in any case.</summary>
	private static readonly string[] JoyButtonNames =
	{
		"A", "B", "X", "Y", "Back", "Guide", "Start", "LeftStick", "RightStick", "LeftShoulder", "RightShoulder",
		"DpadUp", "DpadDown", "DpadLeft", "DpadRight", "Misc1", "Misc2", "Misc3", "Misc4", "Misc5", "Misc6",
		"Paddle1", "Paddle2", "Paddle3", "Paddle4", "Touchpad", "SdlMax", "Max",
	};

	/// <summary>Godot 4.7's <c>JoyAxis</c> names (Invalid excluded).</summary>
	private static readonly string[] JoyAxisNames =
	{
		"LeftX", "LeftY", "RightX", "RightY", "TriggerLeft", "TriggerRight", "SdlMax", "Max",
	};

	/// <summary>
	/// An object keyed by action name, each a binding object: <c>label</c> (string), <c>keys</c> and
	/// <c>buttons</c> (a string or a list of strings), <c>axis</c> (a string, optionally signed + or -).
	/// An action that binds nothing is ignored. Key names are the engine's to check.
	/// </summary>
	private static void CheckInput(Doc doc, JsonElement root)
	{
		if (!doc.Object(root, string.Empty, "must be a JSON object of actions keyed by name; the file is ignored"))
		{
			return;
		}

		doc.DuplicateKeys(root, string.Empty);
		foreach (JsonProperty entry in root.EnumerateObject())
		{
			string at = At(string.Empty, entry.Name);
			if (entry.Name.Trim().Length == 0)
			{
				doc.Error(at, "an action needs a name; ignored");
				continue;
			}

			if (!doc.Object(entry.Value, at,
				"must be a binding object { \"keys\", \"buttons\", \"axis\", \"label\" }; the action is ignored"))
			{
				continue;
			}

			JsonElement binding = entry.Value;
			doc.Keys(binding, at, InputKeys);
			doc.Text(binding, "label", at, strict: true);
			int bound = 0;
			List<string>? keys = doc.TextList(binding, "keys", at, single: true, strict: true);
			bound += keys?.Count(k => k.Trim().Length != 0) ?? 0;

			List<string>? buttons = doc.TextList(binding, "buttons", at, single: true, strict: true);
			if (buttons is not null)
			{
				bool single = binding.GetProperty("buttons").ValueKind == JsonValueKind.String;
				for (int i = 0; i < buttons.Count; i++)
				{
					string name = buttons[i].Trim();
					if (name.Length == 0)
					{
						continue;
					}

					string where = single ? At(at, "buttons") : At(At(at, "buttons"), i);
					if (IsPadName(name, PadAliases, JoyButtonNames, out bool numeric))
					{
						bound++;
						if (numeric)
						{
							doc.Warn(where, $"a button by number ({name}); name it (A, B, X, Y, LB, RB, LT, RT, Start, Back...)");
						}
					}
					else
					{
						doc.Error(where, $"'{name}' is not a pad button: A, B, X, Y, LB, RB, L3, R3, LT, RT, Start, Back, " +
							"or a Godot JoyButton name (DpadUp, Guide, Paddle1...); ignored");
					}
				}
			}

			string? axis = doc.Text(binding, "axis", at, strict: true);
			if (axis is not null && axis.Trim().Length != 0)
			{
				string name = axis.Trim();
				if (name[0] is '+' or '-')
				{
					name = name[1..];
				}

				if (IsPadName(name, Array.Empty<string>(), JoyAxisNames, out bool numeric))
				{
					bound++;
					if (numeric)
					{
						doc.Warn(At(at, "axis"), $"an axis by number ({name}); name it (LeftX, LeftY, RightX, RightY...)");
					}
				}
				else
				{
					doc.Error(At(at, "axis"), $"'{axis}' is not a pad axis: LeftX, LeftY, RightX, RightY, TriggerLeft or " +
						"TriggerRight, optionally signed (+LeftY, -LeftY); ignored");
				}
			}

			if (bound == 0)
			{
				doc.Error(at, "binds no key, button or axis; the action is ignored");
			}
		}
	}

	/// <summary>An alias, an enum name in any case, or a number <c>Enum.TryParse</c> would take.</summary>
	private static bool IsPadName(string name, string[] aliases, string[] names, out bool numeric)
	{
		numeric = false;
		if (aliases.Contains(name, StringComparer.OrdinalIgnoreCase) || names.Contains(name, StringComparer.OrdinalIgnoreCase))
		{
			return true;
		}

		numeric = int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 0;
		return numeric;
	}

	// ---------------------------------------------------------------------------------------
	// spells/<id>.json — ModSpells.TryParse
	// ---------------------------------------------------------------------------------------

	private static readonly string[] SpellKeys =
	{
		"replaces", "name", "description", "icon", "card", "damage", "look", "on_cast", "on_hit",
	};

	/// <summary>
	/// <c>replaces</c> (required: a type name or 0..7, resolved by ModSpells.TryResolveType),
	/// <c>name</c>/<c>description</c>/<c>icon</c>/<c>card</c>/<c>on_cast</c>/<c>on_hit</c> (strings),
	/// <c>damage</c> (a number at least 0) and <c>look</c> (one fx recipe body, plus <c>seconds</c>).
	/// </summary>
	private static void CheckSpell(Doc doc, JsonElement root)
	{
		if (!doc.Object(root, string.Empty, "the file is not a JSON object; the spell is refused"))
		{
			return;
		}

		doc.Keys(root, string.Empty, SpellKeys);
		string types = string.Join(", ", ModSpells.TypeNames);
		if (!root.TryGetProperty("replaces", out JsonElement replaces))
		{
			doc.Error("replaces", $"\"replaces\" is required: one of {types} or 0..7; the spell is refused");
		}
		else
		{
			string text = replaces.ValueKind == JsonValueKind.String ? replaces.GetString() ?? string.Empty : replaces.GetRawText();
			if (!ModSpells.TryResolveType(text, out _))
			{
				doc.Error("replaces", $"'{text}' is not a spell type; the binary has exactly {types} (0..7); the spell is refused");
			}
		}

		foreach (string key in new[] { "name", "description", "icon", "card", "on_cast", "on_hit" })
		{
			doc.Text(root, key, string.Empty, strict: true);
		}

		if (root.TryGetProperty("damage", out JsonElement damage) &&
			(damage.ValueKind != JsonValueKind.Number || damage.GetDouble() < 0))
		{
			doc.Error("damage", "must be a number at least 0 (a multiplier on every hit of the type); the spell is refused");
		}

		if (root.TryGetProperty("look", out JsonElement look) &&
			doc.Object(look, "look", "must be an object: one fx recipe body; the spell is refused"))
		{
			CheckRecipe(doc, look, "look", fileInstances: false, spellLook: true);
		}
	}

	// ---------------------------------------------------------------------------------------
	// fx/recipes.json — Engine_FxRecipes.Parse / ParsePiece, Engine_FxRecipePieces.Build
	// ---------------------------------------------------------------------------------------

	private static readonly string[] RecipeFileKeys = { "recipes", "instances", "instanceRadius" };

	private static readonly string[] RecipeKeys =
	{
		"id", "when", "equals", "base", "tint", "intensity", "size", "attach", "anchor", "light", "pieces",
		"instances", "instanceRadius", "everyNode", "entities",
	};

	private static readonly string[] SpellLookKeys = RecipeKeys.Append("seconds").ToArray();

	private static readonly string[] AttachKeys =
	{
		"effect", "scale", "count", "radius", "height", "turnsPerSecond", "intensity", "stack", "stackStep", "stagger",
		"tint", "radiusStep", "scaleStep", "fadeIn",
	};

	private static readonly string[] LightKeys = { "color", "energy", "range", "height", "flicker" };

	private static readonly string[] PieceKeys =
	{
		"kind", "texture", "color", "colorEnd", "height", "radius", "radiusTop", "rate", "speed", "size", "sizeEnd",
		"count", "period", "width", "mesh", "tilt", "intensity", "blend",
	};

	/// <summary>Engine_FxRecipePieces.Build's kinds; any other is dropped with a warning at build.</summary>
	private static readonly string[] PieceKinds = { "embers", "arcs", "ring", "blades" };

	/// <summary>Engine_FxRecipePieces.Billboard: "mix" covers, everything else adds.</summary>
	private static readonly string[] Blends = { "add", "mix" };

	/// <summary>
	/// An object with a <c>recipes</c> array (and file-wide <c>instances</c>/<c>instanceRadius</c>).
	/// A recipe needs <c>id</c>, <c>when</c> (or <c>instances</c>) and a non-empty <c>base</c>.
	/// </summary>
	private static void CheckRecipes(Doc doc, JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object ||
			!root.TryGetProperty("recipes", out JsonElement recipes) || recipes.ValueKind != JsonValueKind.Array)
		{
			doc.Error(string.Empty, "must be an object with a \"recipes\" array; the file is ignored");
			return;
		}

		doc.Keys(root, string.Empty, RecipeFileKeys);
		bool fileInstances = (doc.Text(root, "instances", string.Empty) ?? string.Empty).Length != 0;
		doc.Number(root, "instanceRadius", string.Empty, strict: true);
		var ids = new HashSet<string>(StringComparer.Ordinal);
		int i = 0;
		foreach (JsonElement recipe in recipes.EnumerateArray())
		{
			string at = At("recipes", i++);
			if (!doc.Object(recipe, at, "must be an object; skipped"))
			{
				continue;
			}

			string? id = CheckRecipe(doc, recipe, at, fileInstances, spellLook: false);
			if (id is { Length: > 0 } && !ids.Add(id))
			{
				doc.Warn(At(at, "id"), $"a second recipe '{id}' in this file; the later one replaces the earlier");
			}
		}
	}

	/// <summary>One recipe body; returns its id. A spell's look has its id, when and equals filled in by the composer.</summary>
	private static string? CheckRecipe(Doc doc, JsonElement recipe, string at, bool fileInstances, bool spellLook)
	{
		doc.Keys(recipe, at, spellLook ? SpellLookKeys : RecipeKeys);
		string? id = null;
		if (spellLook)
		{
			foreach (string filled in new[] { "id", "when" })
			{
				if (Has(recipe, filled))
				{
					doc.Warn(At(at, filled), "filled in from the spell (its look key); ignored");
				}
			}

			double? seconds = doc.Number(recipe, "seconds", at, strict: true);
			if (seconds is < 0.1)
			{
				doc.Warn(At(at, "seconds"), "below 0.1; the look is held 0.1 s");
			}
		}
		else
		{
			id = doc.Text(recipe, "id", at, required: true);
			List<string> when = doc.TextOrList(recipe, "when", at) ?? new List<string>();
			bool instances = Has(recipe, "instances")
				? (doc.Text(recipe, "instances", at) ?? string.Empty).Length != 0
				: fileInstances;
			if (when.Count == 0 && !instances)
			{
				doc.Error(at, "needs \"when\" (the mod values that switch it on) or \"instances\"; skipped");
			}
		}

		doc.Number(recipe, "equals", at, strict: true);
		doc.Number(recipe, "intensity", at, strict: true);
		doc.Number(recipe, "size", at, strict: true);
		doc.Number(recipe, "instanceRadius", at, strict: true);
		doc.Flag(recipe, "everyNode", at);
		List<string>? baseNames = doc.TextList(recipe, "base", at);
		if (baseNames is null || baseNames.All(name => name.Length == 0))
		{
			doc.Error(At(at, "base"), "\"base\" must name at least one effect (the live effects the recipe dresses); skipped");
		}

		doc.TextList(recipe, "anchor", at);
		doc.TextList(recipe, "entities", at);
		doc.Vector(recipe, "tint", at, 3, 3, extraIgnored: true);

		if (doc.Array(recipe, "attach", at) is { } attach)
		{
			int i = 0;
			foreach (JsonElement item in attach.EnumerateArray())
			{
				string where = At(At(at, "attach"), i++);
				if (!doc.Object(item, where, "must be an object; skipped"))
				{
					continue;
				}

				doc.Keys(item, where, AttachKeys);
				doc.Text(item, "effect", where, required: true);
				foreach (string key in new[] { "scale", "radius", "height", "turnsPerSecond", "intensity", "stackStep", "stagger", "radiusStep", "scaleStep", "fadeIn" })
				{
					doc.Number(item, key, where, strict: true);
				}

				doc.Number(item, "count", where, strict: true, integer: true, min: 0);
				doc.Number(item, "stack", where, strict: true, integer: true, min: 1);
				doc.Vector(item, "tint", where, 3, 3, extraIgnored: true);
			}
		}

		if (Has(recipe, "light"))
		{
			JsonElement light = recipe.GetProperty("light");
			string where = At(at, "light");
			if (doc.Object(light, where, "must be an object { \"color\", \"energy\", \"range\", \"height\", \"flicker\" }; ignored"))
			{
				doc.Keys(light, where, LightKeys);
				doc.Vector(light, "color", where, 3, 3, extraIgnored: true);
				doc.Number(light, "energy", where, strict: true, min: 0);
				doc.Number(light, "range", where, strict: true, min: 0);
				doc.Number(light, "height", where, strict: true);
				doc.Number(light, "flicker", where, strict: true, min: 0);
			}
		}

		if (doc.Array(recipe, "pieces", at) is { } pieces)
		{
			int i = 0;
			foreach (JsonElement piece in pieces.EnumerateArray())
			{
				string where = At(At(at, "pieces"), i++);
				if (!doc.Object(piece, where, "must be an object; skipped"))
				{
					continue;
				}

				doc.Keys(piece, where, PieceKeys);
				string kind = doc.Text(piece, "kind", where, required: true) ?? string.Empty;
				if (kind.Length != 0 && Array.IndexOf(PieceKinds, kind) < 0)
				{
					doc.Error(At(where, "kind"), $"'{kind}' is not a piece kind: {string.Join(", ", PieceKinds)}; the piece is not built");
				}

				doc.Text(piece, "texture", where);
				doc.Text(piece, "mesh", where);
				string blend = doc.Text(piece, "blend", where) ?? string.Empty;
				if (blend.Length != 0 && Array.IndexOf(Blends, blend) < 0)
				{
					doc.Error(At(where, "blend"), $"'{blend}' is not a blend: add (lights the scene up) or mix (covers it); drawn as add");
				}

				doc.Vector(piece, "color", where, 3, 4, extraIgnored: true);
				doc.Vector(piece, "colorEnd", where, 3, 4, extraIgnored: true);
				foreach (string key in new[] { "height", "radius", "radiusTop", "rate", "speed", "size", "sizeEnd", "period", "width", "tilt", "intensity" })
				{
					doc.Number(piece, key, where, strict: true);
				}

				doc.Number(piece, "count", where, strict: true, integer: true, min: 0);
			}
		}

		return id;
	}

	// ---------------------------------------------------------------------------------------
	// ui/hud.json — Engine_HudModPieces.Parse / ParseClones / Source / MeterValue
	// ---------------------------------------------------------------------------------------

	private static readonly string[] HudKeys = { "hide", "pieces", "clones", "scale" };

	private static readonly string[] HudPieceKeys =
	{
		"id", "kind", "anchor", "offset", "size", "value", "max", "fill", "back", "texture", "follow", "followElement",
		"widthFrom", "widthPerUnit", "minWidth", "maxWidth", "chip", "border", "borderWidth", "corner", "textures", "index",
	};

	private static readonly string[] HudCloneKeys = { "id", "of", "offset", "hue", "hueElements", "icon", "values", "bars" };

	private static readonly string[] HudIconKeys = { "element", "textures" };

	private static readonly string[] HudPieceKinds = { "meter", "panel", "picture" };

	/// <summary>
	/// <c>hide</c> (ids), <c>scale</c> (id → factor), <c>clones</c> (copies of a shipped piece: id
	/// and of required) and <c>pieces</c> (meter, panel or picture: id required). Values are a
	/// number or a source the HUD reads: <c>mod:&lt;key&gt;</c>, <c>hero:health</c>,
	/// <c>hero:health_max</c>; colours are HTML hex.
	/// </summary>
	private static void CheckHud(Doc doc, JsonElement root)
	{
		if (!doc.Object(root, string.Empty, "must be a JSON object; the file is ignored"))
		{
			return;
		}

		doc.Keys(root, string.Empty, HudKeys);
		doc.TextList(root, "hide", string.Empty);

		if (Has(root, "scale") && doc.Object(root.GetProperty("scale"), "scale", "must be an object of piece id to factor; ignored"))
		{
			doc.DuplicateKeys(root.GetProperty("scale"), "scale");
			foreach (JsonProperty factor in root.GetProperty("scale").EnumerateObject())
			{
				string at = At("scale", factor.Name);
				if (doc.LooseNumber(factor.Value, at) is <= 0)
				{
					doc.Warn(at, "a factor of 0 or less draws the piece at no size");
				}
			}
		}

		if (doc.Array(root, "clones", string.Empty) is { } clones)
		{
			var ids = new HashSet<string>(StringComparer.Ordinal);
			int i = 0;
			foreach (JsonElement clone in clones.EnumerateArray())
			{
				string at = At("clones", i++);
				if (!doc.Object(clone, at, "must be an object; skipped"))
				{
					continue;
				}

				CheckHudClone(doc, clone, at, ids);
			}
		}

		if (doc.Array(root, "pieces", string.Empty) is { } pieces)
		{
			var ids = new HashSet<string>(StringComparer.Ordinal);
			int i = 0;
			foreach (JsonElement piece in pieces.EnumerateArray())
			{
				string at = At("pieces", i++);
				if (!doc.Object(piece, at, "must be an object; skipped"))
				{
					continue;
				}

				CheckHudPiece(doc, piece, at, ids);
			}
		}
	}

	private static void CheckHudClone(Doc doc, JsonElement clone, string at, HashSet<string> ids)
	{
		doc.Keys(clone, at, HudCloneKeys);
		string? id = doc.Text(clone, "id", at, required: true);
		doc.Text(clone, "of", at, required: true);
		if (id is { Length: > 0 } && !ids.Add(id))
		{
			doc.Warn(At(at, "id"), $"a second clone '{id}' in this file; the later one replaces the earlier");
		}

		doc.Vector(clone, "offset", at, 2, 2, extraIgnored: false);
		double? hue = Has(clone, "hue") ? doc.LooseNumber(clone.GetProperty("hue"), At(at, "hue")) : null;
		List<string>? hueElements = doc.TextList(clone, "hueElements", at);
		if (hue is { } shift && shift != 0 && hueElements is not { Count: > 0 })
		{
			doc.Warn(At(at, "hue"), "a hue with no \"hueElements\" recolours nothing");
		}

		if (Has(clone, "icon"))
		{
			string where = At(at, "icon");
			JsonElement icon = clone.GetProperty("icon");
			if (doc.Object(icon, where, "must be an object { \"element\", \"textures\" }; ignored"))
			{
				doc.Keys(icon, where, HudIconKeys);
				doc.Text(icon, "element", where, required: true);
				doc.TextList(icon, "textures", where, required: true);
			}
		}

		var meters = new HashSet<string>(StringComparer.Ordinal);
		if (Has(clone, "values") &&
			doc.Object(clone.GetProperty("values"), At(at, "values"), "must be an object of meter name to source; ignored"))
		{
			doc.DuplicateKeys(clone.GetProperty("values"), At(at, "values"));
			foreach (JsonProperty meter in clone.GetProperty("values").EnumerateObject())
			{
				string where = At(At(at, "values"), meter.Name);
				meters.Add(meter.Name);
				if (doc.LooseText(meter.Value, where) is { } expression)
				{
					MeterExpression(doc, where, expression);
				}
			}
		}

		if (Has(clone, "bars") &&
			doc.Object(clone.GetProperty("bars"), At(at, "bars"), "must be an object of element name to meter name; ignored"))
		{
			doc.DuplicateKeys(clone.GetProperty("bars"), At(at, "bars"));
			foreach (JsonProperty bar in clone.GetProperty("bars").EnumerateObject())
			{
				string where = At(At(at, "bars"), bar.Name);
				if (doc.LooseText(bar.Value, where) is { } meter && !meters.Contains(meter))
				{
					doc.Error(where, $"names meter '{meter}', which \"values\" does not feed; the bar is not redrawn");
				}
			}
		}
	}

	private static void CheckHudPiece(Doc doc, JsonElement piece, string at, HashSet<string> ids)
	{
		doc.Keys(piece, at, HudPieceKeys);
		string? id = doc.Text(piece, "id", at, required: true);
		if (id is { Length: > 0 } && !ids.Add(id))
		{
			doc.Warn(At(at, "id"), $"a second piece '{id}' in this file; the later one replaces the earlier");
		}

		string kind = doc.Text(piece, "kind", at) ?? string.Empty;
		if (kind.Length != 0 && Array.IndexOf(HudPieceKinds, kind) < 0)
		{
			doc.Error(At(at, "kind"), $"'{kind}' is not a piece kind: meter, panel or picture; it is read as a meter");
			kind = "meter";
		}

		doc.Vector(piece, "anchor", at, 2, 2, extraIgnored: false);
		doc.Vector(piece, "offset", at, 2, 2, extraIgnored: false);
		doc.Vector(piece, "size", at, 2, 2, extraIgnored: false);
		foreach (string colour in new[] { "fill", "back", "chip", "border" })
		{
			doc.Colour(piece, colour, at);
		}

		foreach (string number in new[] { "widthPerUnit", "minWidth", "maxWidth", "borderWidth", "corner" })
		{
			if (Has(piece, number))
			{
				doc.LooseNumber(piece.GetProperty(number), At(at, number));
			}
		}

		doc.Text(piece, "follow", at);
		doc.Text(piece, "followElement", at);
		ValueSource(doc, piece, "widthFrom", at, required: false);
		if (kind is "" or "meter")
		{
			ValueSource(doc, piece, "value", at, required: true, why: "a meter needs \"value\"; without it the piece never draws");
			ValueSource(doc, piece, "max", at, required: false);
		}
		else if (kind == "picture")
		{
			if (Has(piece, "textures"))
			{
				List<string>? textures = doc.TextList(piece, "textures", at);
				for (int i = 0; textures is not null && i < textures.Count; i++)
				{
					TexturePath(doc, At(At(at, "textures"), i), textures[i]);
				}

				ValueSource(doc, piece, "index", at, required: true,
					why: "a picture with \"textures\" needs \"index\", the source that picks one; without it the piece never draws");
			}
			else if (doc.Text(piece, "texture", at) is { Length: > 0 } texture)
			{
				TexturePath(doc, At(at, "texture"), texture);
			}
			else
			{
				doc.Error(at, "a picture needs \"texture\", or \"textures\" with \"index\"; the piece is not built");
			}
		}
	}

	/// <summary>A field holding a number or a value source; <paramref name="why"/> is the message for a missing required one.</summary>
	private static void ValueSource(Doc doc, JsonElement record, string key, string at, bool required, string? why = null)
	{
		string where = At(at, key);
		if (!record.TryGetProperty(key, out JsonElement value))
		{
			if (required)
			{
				doc.Error(where, why ?? $"\"{key}\" is required");
			}

			return;
		}

		if (value.ValueKind == JsonValueKind.Number)
		{
			return;
		}

		if (value.ValueKind != JsonValueKind.String)
		{
			doc.Error(where, $"must be a number or a value source, not {Describe(value)}");
			return;
		}

		string text = value.GetString() ?? string.Empty;
		if (text.Trim().Length == 0)
		{
			if (required)
			{
				doc.Error(where, why ?? $"\"{key}\" is empty");
			}

			return;
		}

		Source(doc, where, text);
	}

	/// <summary>Engine_HudModPieces.Source: a number, <c>mod:&lt;key&gt;</c>, <c>hero:health</c> or <c>hero:health_max</c>.</summary>
	private static void Source(Doc doc, string at, string text)
	{
		string source = text.Trim();
		if (double.TryParse(source, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
			source is "hero:health" or "hero:health_max")
		{
			return;
		}

		if (source.StartsWith("mod:", StringComparison.Ordinal))
		{
			if (source.Length == "mod:".Length)
			{
				doc.Error(at, "'mod:' names no value; write mod:<mod>.<name>");
			}

			return;
		}

		doc.Error(at, $"'{text}' is not a value source: a number, mod:<mod>.<name>, hero:health or hero:health_max; " +
			"it never states a value, so the piece never draws");
	}

	/// <summary>Engine_HudModPieces.MeterValue: <c>source</c>, <c>source/cap</c>, or <c>none</c> to hold the meter still.</summary>
	private static void MeterExpression(Doc doc, string at, string expression)
	{
		if (expression == "none")
		{
			return;
		}

		int slash = expression.LastIndexOf('/');
		if (slash <= 0)
		{
			Source(doc, at, expression);
			return;
		}

		Source(doc, at, expression[..slash]);
		Source(doc, at, expression[(slash + 1)..]);
	}

	/// <summary>A picture piece's texture goes through Godot's ResourceLoader, which resolves a scheme.</summary>
	private static void TexturePath(Doc doc, string at, string path)
	{
		if (path.Length != 0 && !path.Contains("://", StringComparison.Ordinal))
		{
			doc.Warn(at, $"'{path}' is loaded through Godot's resource loader; write game://{path.TrimStart('/')} " +
				"for a game or mod texture");
		}
	}

	// ---------------------------------------------------------------------------------------
	// ui/templates.json — Engine_UiTemplates.ApplyOverrideText / PatchMenu / ParseMenu / ParseEntry / ParseScreen
	// ---------------------------------------------------------------------------------------

	private static readonly string[] TemplatesKeys = { "menus", "screens" };

	private static readonly string[] MenuKeys = { "patch", "highlight", "title", "rowCommand", "source", "entries" };

	private static readonly string[] EntryKeys =
	{
		"name", "tag", "command", "opens", "hostOnly", "drop", "source", "icon", "eventType", "rating", "counted", "children",
	};

	/// <summary>PatchMenu's per-entry placement fields, read only in a <c>"patch": true</c> menu.</summary>
	private static readonly string[] PatchEntryKeys = { "parent", "after", "before", "remove" };

	private static readonly string[] EntryAndPatchKeys = EntryKeys.Concat(PatchEntryKeys).ToArray();

	private static readonly string[] ScreenTextKeys =
	{
		"name", "menuScript", "previewAnchor", "pageElement", "menu", "provider", "revealedAccept", "revealedBack",
		"flipAction", "scrollContent", "scrollMask", "scrollBar", "flowMeasured", "flowBase", "flowTailAnchor",
		"presentation", "pageRootUnit", "backdropUnit", "boxPresentation",
	};

	private static readonly string[] ScreenFlagKeys =
	{
		"ownsList", "ownsRevealedInput", "hasSpellBlade", "fadesTopEnd", "fadesBottomEnd",
	};

	private static readonly string[] ScreenListKeys =
	{
		"chrome", "panelSubtrees", "ownedElsewhere", "liveTexts", "boxUnits", "pages", "guestTextUnits",
		"wideLeft", "wideRight", "wideGround", "wideStretch", "flowMoved", "flowTailMoved",
	};

	private static readonly string[] ScreenKeys = ScreenTextKeys.Concat(ScreenFlagKeys).Concat(ScreenListKeys)
		.Concat(new[] { "pageSlide", "listLift", "boxPaths", "pageStackAt", "statFields", "panelFields", "menuScriptByMode" })
		.ToArray();

	/// <summary>
	/// <c>menus</c> (by name: a whole menu, or with <c>"patch": true</c> per-entry edits by name) and
	/// <c>screens</c> (by numeric screen id, replacing the whole record).
	/// </summary>
	private static void CheckTemplates(Doc doc, JsonElement root)
	{
		if (!doc.Object(root, string.Empty, "must be a JSON object; the file is ignored"))
		{
			return;
		}

		doc.Keys(root, string.Empty, TemplatesKeys);
		if (Has(root, "menus") && doc.Object(root.GetProperty("menus"), "menus", "must be an object of menus by name; ignored"))
		{
			doc.DuplicateKeys(root.GetProperty("menus"), "menus");
			foreach (JsonProperty menu in root.GetProperty("menus").EnumerateObject())
			{
				string at = At("menus", menu.Name);
				if (!doc.Object(menu.Value, at, "must be an object; the menu is ignored"))
				{
					continue;
				}

				doc.Keys(menu.Value, at, MenuKeys);
				bool patch = doc.Flag(menu.Value, "patch", at) ?? false;
				foreach (string key in new[] { "highlight", "title", "rowCommand", "source" })
				{
					doc.Text(menu.Value, key, at);
				}

				if (doc.Array(menu.Value, "entries", at) is { } entries)
				{
					Entries(doc, entries, At(at, "entries"), patch);
				}
			}
		}

		if (Has(root, "screens") && doc.Object(root.GetProperty("screens"), "screens", "must be an object of screens by numeric id; ignored"))
		{
			doc.DuplicateKeys(root.GetProperty("screens"), "screens");
			foreach (JsonProperty screen in root.GetProperty("screens").EnumerateObject())
			{
				string at = At("screens", screen.Name);
				if (!uint.TryParse(screen.Name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
				{
					doc.Error(at, $"'{screen.Name}' is not a screen id; a screen is keyed by its number (\"5\"), so it is ignored");
					continue;
				}

				if (doc.Object(screen.Value, at, "must be an object; the screen is ignored"))
				{
					CheckScreen(doc, screen.Value, at);
				}
			}
		}
	}

	private static void Entries(Doc doc, JsonElement entries, string at, bool patch)
	{
		int i = 0;
		foreach (JsonElement entry in entries.EnumerateArray())
		{
			string where = At(at, i++);
			if (!doc.Object(entry, where, "must be an object; skipped"))
			{
				continue;
			}

			doc.Keys(entry, where, EntryAndPatchKeys);
			if (!patch)
			{
				foreach (string key in PatchEntryKeys)
				{
					if (Has(entry, key))
					{
						doc.Warn(At(where, key), "read only in a \"patch\": true menu; ignored here");
					}
				}
			}

			doc.Text(entry, "name", where, required: true);
			foreach (string key in new[] { "tag", "command", "opens", "source", "icon" })
			{
				doc.Text(entry, key, where);
			}

			foreach (string key in new[] { "hostOnly", "drop", "counted" })
			{
				doc.Flag(entry, key, where);
			}

			foreach (string key in new[] { "eventType", "rating" })
			{
				if (Has(entry, key))
				{
					doc.LooseNumber(entry.GetProperty(key), At(where, key), integer: true);
				}
			}

			if (patch)
			{
				doc.Text(entry, "parent", where);
				doc.Text(entry, "after", where);
				doc.Text(entry, "before", where);
				doc.Flag(entry, "remove", where);
				if (Has(entry, "after") && Has(entry, "before"))
				{
					doc.Warn(where, "both \"after\" and \"before\"; \"after\" is tried first and \"before\" only when that entry is missing");
				}
			}

			// Children are parsed as whole entries, never as patches.
			if (doc.Array(entry, "children", where) is { } children)
			{
				Entries(doc, children, At(where, "children"), patch: false);
			}
		}
	}

	private static void CheckScreen(Doc doc, JsonElement screen, string at)
	{
		doc.Keys(screen, at, ScreenKeys);
		foreach (string key in ScreenTextKeys)
		{
			doc.Text(screen, key, at);
		}

		foreach (string key in ScreenFlagKeys)
		{
			doc.Flag(screen, key, at);
		}

		foreach (string key in ScreenListKeys)
		{
			doc.TextList(screen, key, at);
		}

		if (Has(screen, "pageSlide"))
		{
			doc.LooseNumber(screen.GetProperty("pageSlide"), At(at, "pageSlide"), integer: true);
		}

		if (Has(screen, "listLift"))
		{
			doc.LooseNumber(screen.GetProperty("listLift"), At(at, "listLift"));
		}

		doc.Vector(screen, "pageStackAt", at, 3, 3, extraIgnored: false);
		if (Has(screen, "boxPaths") &&
			doc.Object(screen.GetProperty("boxPaths"), At(at, "boxPaths"), "must be an object of path to path; ignored"))
		{
			foreach (JsonProperty path in screen.GetProperty("boxPaths").EnumerateObject())
			{
				doc.LooseText(path.Value, At(At(at, "boxPaths"), path.Name));
			}
		}

		if (Has(screen, "menuScriptByMode") &&
			doc.Object(screen.GetProperty("menuScriptByMode"), At(at, "menuScriptByMode"), "must be an object of mode number to script; ignored"))
		{
			foreach (JsonProperty mode in screen.GetProperty("menuScriptByMode").EnumerateObject())
			{
				string where = At(At(at, "menuScriptByMode"), mode.Name);
				if (!int.TryParse(mode.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
				{
					doc.Error(where, $"'{mode.Name}' is not a mode number; ignored");
				}
				else if (doc.LooseText(mode.Value, where) is not { Length: > 0 })
				{
					doc.Error(where, "names no script; ignored");
				}
			}
		}

		if (doc.Array(screen, "statFields", at) is { } stats)
		{
			int i = 0;
			foreach (JsonElement stat in stats.EnumerateArray())
			{
				string where = At(At(at, "statFields"), i++);
				if (!doc.Object(stat, where, "must be an object { \"id\", \"path\" }; skipped"))
				{
					continue;
				}

				doc.Keys(stat, where, new[] { "id", "path" });
				if (!Has(stat, "id") || !Has(stat, "path"))
				{
					doc.Error(where, "a stat field needs both \"id\" and \"path\"; skipped");
				}

				if (Has(stat, "id"))
				{
					doc.LooseNumber(stat.GetProperty("id"), At(where, "id"), integer: true);
				}

				doc.Text(stat, "path", where);
			}
		}

		if (doc.Array(screen, "panelFields", at) is { } fields)
		{
			int i = 0;
			foreach (JsonElement field in fields.EnumerateArray())
			{
				string where = At(At(at, "panelFields"), i++);
				if (!doc.Object(field, where, "must be an object { \"path\", \"multiline\", \"alternate\" }; skipped"))
				{
					continue;
				}

				doc.Keys(field, where, new[] { "path", "multiline", "alternate" });
				doc.Text(field, "path", where, required: true);
				doc.Flag(field, "multiline", where);
				doc.Flag(field, "alternate", where);
			}
		}
	}

	// ---------------------------------------------------------------------------------------
	// ui/spellwheel.json — Engine_UiSpellAssign.LoadSecondary
	// ---------------------------------------------------------------------------------------

	private static readonly string[] SecondaryKeys =
	{
		"setting", "settingPlayerTwo", "value", "valuePlayerTwo", "chosen", "chosenPlayerTwo", "empty", "scale",
		"exclude", "unlocked", "apply", "remove",
	};

	/// <summary>
	/// An object with a <c>secondary</c> object: <c>setting</c> and <c>value</c> required, the other
	/// keys strings, <c>scale</c> 0.2..1, <c>exclude</c> a list of will types 0..7.
	/// </summary>
	private static void CheckSpellWheel(Doc doc, JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object ||
			!root.TryGetProperty("secondary", out JsonElement secondary) || secondary.ValueKind != JsonValueKind.Object)
		{
			doc.Error(string.Empty, "must be an object with a \"secondary\" object; the file is ignored");
			return;
		}

		doc.Keys(root, string.Empty, new[] { "secondary" });
		const string at = "secondary";
		doc.Keys(secondary, at, SecondaryKeys);
		string? setting = doc.Text(secondary, "setting", at, required: true);
		doc.Text(secondary, "value", at, required: true);
		string? settingTwo = doc.Text(secondary, "settingPlayerTwo", at);
		string? valueTwo = doc.Text(secondary, "valuePlayerTwo", at);
		doc.Text(secondary, "chosen", at);
		string? chosenTwo = doc.Text(secondary, "chosenPlayerTwo", at);
		foreach (string key in new[] { "empty", "unlocked", "apply", "remove" })
		{
			doc.Text(secondary, key, at);
		}

		foreach ((string key, string? name) in new[] { ("setting", setting), ("settingPlayerTwo", settingTwo) })
		{
			if (name is not null && name.StartsWith("mod:", StringComparison.Ordinal))
			{
				doc.Error(At(at, key), "a setting is named bare (<mod>.<name>); \"mod:\" would be kept as part of its name");
			}
		}

		if ((settingTwo is { Length: > 0 }) != (valueTwo is { Length: > 0 }))
		{
			doc.Warn(at, "player two's pick needs both \"settingPlayerTwo\" and \"valuePlayerTwo\"; with one, player two shares player one's");
		}

		if (chosenTwo is { Length: > 0 } && !Has(secondary, "chosen"))
		{
			doc.Warn(At(at, "chosenPlayerTwo"), "read only beside \"chosen\"; ignored");
		}

		doc.Number(secondary, "scale", at, strict: true, min: 0.2, max: 1, clamps: true);
		if (doc.Array(secondary, "exclude", at) is { } exclude)
		{
			int i = 0;
			foreach (JsonElement item in exclude.EnumerateArray())
			{
				string where = At(At(at, "exclude"), i++);
				if (doc.LooseNumber(item, where, integer: true) is { } type && (type < 0 || type >= ModSpells.TypeNames.Length))
				{
					doc.Warn(where, $"{Format(type)} is no will type; the eight are 0..7 ({string.Join(", ", ModSpells.TypeNames)})");
				}
			}
		}
	}

	// ---------------------------------------------------------------------------------------
	// ui/abilities.json — Engine_UiModAbilities.Load / Pool
	// ---------------------------------------------------------------------------------------

	private static readonly string[] AbilityKeys =
	{
		"name", "category", "nameTag", "descriptionTag", "maxLevel", "level", "cost", "stat", "buy", "refund",
		"refundValue", "image", "disciplines", "totalXpTag",
	};

	/// <summary>Engine_UiModAbilities.Pool's disciplines; any other draws on General.</summary>
	private static readonly string[] Disciplines = { "Strength", "Skill", "Will", "General" };

	/// <summary>The mod value and setting keys an ability names; read bare by HostBridge.TryGetModValue / ModSettings.Set.</summary>
	private static readonly string[] AbilityValueKeys = { "level", "cost", "stat", "buy", "refund", "refundValue" };

	/// <summary>An object with an <c>abilities</c> array; each needs <c>name</c>, <c>category</c> and <c>buy</c>.</summary>
	private static void CheckAbilities(Doc doc, JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object ||
			!root.TryGetProperty("abilities", out JsonElement abilities) || abilities.ValueKind != JsonValueKind.Array)
		{
			doc.Error(string.Empty, "must be an object with an \"abilities\" array; the file is ignored");
			return;
		}

		doc.Keys(root, string.Empty, new[] { "abilities" });
		var names = new HashSet<string>(StringComparer.Ordinal);
		int i = 0;
		foreach (JsonElement ability in abilities.EnumerateArray())
		{
			string at = At("abilities", i++);
			if (!doc.Object(ability, at, "must be an object; skipped"))
			{
				continue;
			}

			doc.Keys(ability, at, AbilityKeys);
			string? name = doc.Text(ability, "name", at, required: true);
			string? category = doc.Text(ability, "category", at, required: true);
			doc.Text(ability, "buy", at, required: true);
			foreach (string key in new[] { "nameTag", "descriptionTag", "level", "cost", "stat", "refund", "refundValue", "image", "totalXpTag" })
			{
				doc.Text(ability, key, at);
			}

			foreach (string key in AbilityValueKeys)
			{
				if (ability.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
					(value.GetString() ?? string.Empty).StartsWith("mod:", StringComparison.Ordinal))
				{
					doc.Error(At(at, key), "names the mod value bare (<mod>.<name>); \"mod:\" would be kept as part of the name");
				}
			}

			if (Has(ability, "maxLevel"))
			{
				doc.LooseNumber(ability.GetProperty("maxLevel"), At(at, "maxLevel"), integer: true, min: 1);
			}

			List<string>? disciplines = doc.TextList(ability, "disciplines", at);
			foreach ((string discipline, int index) in (disciplines ?? new List<string>()).Select((d, n) => (d, n)))
			{
				if (discipline.Length != 0 && Array.IndexOf(Disciplines, discipline) < 0)
				{
					doc.Warn(At(At(at, "disciplines"), index),
						$"'{discipline}' is not a discipline ({string.Join(", ", Disciplines)}); its share is drawn from General");
				}
			}

			if (category == "Hybrid" && disciplines is not { Count: > 0 })
			{
				doc.Warn(At(at, "category"), "a Hybrid ability names its \"disciplines\"; without them it draws on General alone");
			}

			if (name is { Length: > 0 } && !names.Add(name))
			{
				doc.Warn(At(at, "name"), $"a second ability '{name}' in this file; the later one replaces the earlier");
			}
		}
	}

	// ---------------------------------------------------------------------------------------
	// mod.toml — ModWorkspace.ParseModToml / ParseArray, ModRequirement.Parse
	// ---------------------------------------------------------------------------------------

	private static readonly string[] ManifestKeys = { "name", "title", "version", "author", "description", "requires", "after" };

	/// <summary>Comparison operators ModRequirement.Parse does not understand; only <c>&gt;=</c> is.</summary>
	private static readonly char[] VersionOperators = { '<', '>', '=', '!', '^', '~' };

	/// <summary>
	/// One <c>key = value</c> per line, read line by line: <c>#</c> comments and <c>[table]</c> lines are
	/// skipped (their keys still read as top-level), strings quoted, <c>requires</c>/<c>after</c> a list
	/// on ONE line, each requirement <c>name</c> or <c>name &gt;= version</c>.
	/// </summary>
	private static void CheckManifest(Doc doc, string text)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		string[] lines = text.Replace("\r\n", "\n").Split('\n');
		for (int n = 0; n < lines.Length; n++)
		{
			string trimmed = lines[n].Trim();
			string line = $"line {n + 1}";
			if (trimmed.Length == 0 || trimmed[0] == '#')
			{
				continue;
			}

			if (trimmed[0] == '[')
			{
				doc.Warn(line, $"'{trimmed}': tables are not read; the keys after it are read as top-level keys");
				continue;
			}

			int equals = trimmed.IndexOf('=');
			if (equals < 0)
			{
				doc.Error(line, $"'{trimmed}' is not a key = value line; it is ignored (a list must open and close on one line)");
				continue;
			}

			string key = trimmed[..equals].Trim().ToLowerInvariant();
			string raw = trimmed[(equals + 1)..].Trim();
			string at = $"{key} ({line})";
			if (Array.IndexOf(ManifestKeys, key) < 0)
			{
				doc.Warn(at, UnknownKey(key, ManifestKeys));
				continue;
			}

			string canonical = key == "title" ? "name" : key;
			if (!seen.Add(canonical))
			{
				doc.Warn(at, key is "name" or "title"
					? "the title is stated twice (name and title are one field); the last one is read"
					: "stated twice; the last one is read");
			}

			if (key is "requires" or "after")
			{
				ManifestList(doc, at, key, raw);
				continue;
			}

			if (raw.Length != 0 && raw[0] is '"' or '\'' && (raw.Length < 2 || raw[^1] != raw[0]))
			{
				doc.Error(at, "the value does not end at its closing quote; an inline comment or trailing text is read as part of it");
				continue;
			}

			string value = Unquote(raw);
			if (raw.Length != 0 && value.Length == raw.Length)
			{
				doc.Warn(at, $"TOML text is quoted: {key} = \"{raw}\"");
			}

			if (key == "version")
			{
				if (value.Trim().Length == 0)
				{
					doc.Warn(at, "an empty version; a requirement with a version floor never accepts this mod");
				}
				else
				{
					VersionSegments(doc, at, value);
				}
			}
		}
	}

	private static void ManifestList(Doc doc, string at, string key, string raw)
	{
		if (raw.StartsWith('[') && !raw.EndsWith(']'))
		{
			doc.Error(at, "the list must close on the same line: " + (key == "requires"
				? "requires = [\"souls_combat >= 0.1\", \"hd-ui\"]"
				: "after = [\"hd-ui\", \"souls_combat\"]"));
			return;
		}

		string inner = raw.StartsWith('[') ? raw[1..^1] : raw;
		foreach (string item in inner.Split(',').Select(part => Unquote(part.Trim())).Where(part => part.Length != 0))
		{
			if (key == "after")
			{
				if (item.IndexOfAny(VersionOperators) >= 0)
				{
					doc.Warn(at, $"'{item}': \"after\" names mods only, with no version; this reads as a mod named '{item}'");
				}

				continue;
			}

			ModRequirement? requirement = ModRequirement.Parse(item);
			if (requirement is null)
			{
				doc.Error(at, $"'{item}' names no mod; write \"<mod> >= <version>\" or \"<mod>\"");
				continue;
			}

			if (requirement.Name.IndexOfAny(VersionOperators) >= 0 || requirement.MinVersion.IndexOfAny(VersionOperators) >= 0)
			{
				doc.Error(at, $"'{item}': only \"<mod> >= <version>\" is understood; this reads as a mod named " +
					$"'{requirement.Name}'{(requirement.MinVersion.Length == 0 ? string.Empty : $" at least '{requirement.MinVersion}'")}");
				continue;
			}

			if (item.Contains(">=", StringComparison.Ordinal) && requirement.MinVersion.Length == 0)
			{
				doc.Warn(at, $"'{item}': '>=' with no version accepts any version");
			}
			else if (requirement.MinVersion.Length != 0)
			{
				VersionSegments(doc, at, requirement.MinVersion);
			}
		}
	}

	/// <summary>ModRequirement.CompareVersions compares a segment numerically only when both sides are whole numbers.</summary>
	private static void VersionSegments(Doc doc, string at, string version)
	{
		foreach (string segment in version.Trim().Split('.'))
		{
			if (!int.TryParse(segment.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
			{
				doc.Warn(at, $"'{version}': version segments compare as numbers only when each is a whole number; " +
					$"'{segment}' compares as text");
				return;
			}
		}
	}

	/// <summary>ModWorkspace.Unquote: one pair of matching outer quotes removed.</summary>
	private static string Unquote(string value) =>
		value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
			? value[1..^1]
			: value;

	// ---------------------------------------------------------------------------------------
	// The field checks every kind shares
	// ---------------------------------------------------------------------------------------

	/// <summary>A pointer one key deeper: <c>a.b</c>, or <c>a["b.c"]</c> for a key that is not a plain name.</summary>
	private static string At(string at, string key)
	{
		bool plain = key.Length != 0 && key.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');
		if (!plain)
		{
			return $"{at}[\"{key}\"]";
		}

		return at.Length == 0 ? key : at + "." + key;
	}

	private static string At(string at, int index) => $"{at}[{index}]";

	private static bool Has(JsonElement record, string key) =>
		record.ValueKind == JsonValueKind.Object && record.TryGetProperty(key, out _);

	private static string Describe(JsonElement value) => value.ValueKind switch
	{
		JsonValueKind.Object => "an object",
		JsonValueKind.Array => "a list",
		JsonValueKind.String => $"the text '{value.GetString()}'",
		JsonValueKind.Number => $"the number {value.GetRawText()}",
		JsonValueKind.True => "true",
		JsonValueKind.False => "false",
		_ => "null",
	};

	private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

	/// <summary>The message for a key no loader reads, naming the nearest known key when one is close.</summary>
	private static string UnknownKey(string key, IReadOnlyList<string> known)
	{
		string? nearest = null;
		int best = int.MaxValue;
		foreach (string candidate in known)
		{
			int distance = string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)
				? 0
				: Distance(candidate.ToLowerInvariant(), key.ToLowerInvariant());
			if (distance < best)
			{
				best = distance;
				nearest = candidate;
			}
		}

		string message = $"'{key}' is not a field here and is ignored";
		int close = key.Length <= 3 ? 1 : key.Length <= 6 ? 2 : 3;
		if (nearest is not null && best <= close)
		{
			return message + $"; did you mean '{nearest}'?";
		}

		return known.Count <= 12 ? message + $" (the fields are {string.Join(", ", known)})" : message;
	}

	/// <summary>Levenshtein distance, for <see cref="UnknownKey"/>'s suggestion.</summary>
	private static int Distance(string a, string b)
	{
		var row = new int[b.Length + 1];
		for (int j = 0; j <= b.Length; j++)
		{
			row[j] = j;
		}

		for (int i = 1; i <= a.Length; i++)
		{
			int diagonal = row[0];
			row[0] = i;
			for (int j = 1; j <= b.Length; j++)
			{
				int above = row[j];
				row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
				diagonal = above;
			}
		}

		return row[b.Length];
	}

	/// <summary>
	/// One file's issues as they are found. The field readers mirror the two ways the loaders read
	/// a Variant: STRICT (only at the right type, anything else dropped — an error) and LOOSE
	/// (<c>AsString</c>/<c>AsDouble</c>/<c>AsBool</c>, which convert — a warning where the
	/// conversion is unlikely to be what was meant, an error where it yields nothing useful).
	/// </summary>
	private sealed class Doc
	{
		public Doc(string path)
		{
			Path = path;
		}

		public string Path { get; }

		public List<ModIssue> Issues { get; } = new();

		public void Error(string at, string message) => Issues.Add(new ModIssue(Path, at, true, message));

		public void Warn(string at, string message) => Issues.Add(new ModIssue(Path, at, false, message));

		/// <summary>Whether <paramref name="value"/> is an object; <paramref name="otherwise"/> is the error when not.</summary>
		public bool Object(JsonElement value, string at, string otherwise)
		{
			if (value.ValueKind == JsonValueKind.Object)
			{
				return true;
			}

			Error(at, otherwise.StartsWith("must", StringComparison.Ordinal) || otherwise.StartsWith("the", StringComparison.Ordinal)
				? $"{otherwise} (it is {Describe(value)})"
				: otherwise);
			return false;
		}

		/// <summary>Unknown keys (ignored by the loader) and repeated keys (the last is read), as warnings.</summary>
		public void Keys(JsonElement record, string at, IReadOnlyList<string> known)
		{
			DuplicateKeys(record, at);
			foreach (JsonProperty property in record.EnumerateObject())
			{
				if (!known.Contains(property.Name))
				{
					Warn(At(at, property.Name), UnknownKey(property.Name, known));
				}
			}
		}

		public void DuplicateKeys(JsonElement record, string at)
		{
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (JsonProperty property in record.EnumerateObject())
			{
				if (!seen.Add(property.Name))
				{
					Warn(At(at, property.Name), "stated twice; the last one is read");
				}
			}
		}

		/// <summary>A text field; null when absent or unusable.</summary>
		public string? Text(JsonElement record, string key, string at, bool required = false, bool strict = false)
		{
			string where = At(at, key);
			if (!record.TryGetProperty(key, out JsonElement value))
			{
				if (required)
				{
					Error(where, $"\"{key}\" is required");
				}

				return null;
			}

			if (value.ValueKind == JsonValueKind.String)
			{
				string text = value.GetString() ?? string.Empty;
				if (required && text.Trim().Length == 0)
				{
					Error(where, $"\"{key}\" is required and is empty");
				}

				return text;
			}

			if (!strict && value.ValueKind == JsonValueKind.Number)
			{
				Warn(where, $"a number where text is expected; read as '{value.GetRawText()}'");
				return value.GetRawText();
			}

			Error(where, $"must be text, not {Describe(value)}" + (strict ? "; the loader ignores it" : string.Empty));
			return null;
		}

		/// <summary>A loose text value outside a field (a map's value).</summary>
		public string? LooseText(JsonElement value, string at)
		{
			if (value.ValueKind == JsonValueKind.String)
			{
				return value.GetString() ?? string.Empty;
			}

			if (value.ValueKind == JsonValueKind.Number)
			{
				Warn(at, $"a number where text is expected; read as '{value.GetRawText()}'");
				return value.GetRawText();
			}

			Error(at, $"must be text, not {Describe(value)}");
			return null;
		}

		/// <summary>A string, or a list of strings (fx recipes' <c>when</c>, read with AsString per item).</summary>
		public List<string>? TextOrList(JsonElement record, string key, string at) =>
			record.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
				? new List<string> { value.GetString() ?? string.Empty }
				: TextList(record, key, at);

		/// <summary>
		/// A list of text; <paramref name="single"/> also takes one string. Strict lists drop a
		/// non-string item (an error); loose ones read a number as its digits.
		/// </summary>
		public List<string>? TextList(JsonElement record, string key, string at, bool single = false, bool strict = false, bool required = false)
		{
			string where = At(at, key);
			if (!record.TryGetProperty(key, out JsonElement value))
			{
				if (required)
				{
					Error(where, $"\"{key}\" is required");
				}

				return null;
			}

			if (single && value.ValueKind == JsonValueKind.String)
			{
				return new List<string> { value.GetString() ?? string.Empty };
			}

			if (value.ValueKind != JsonValueKind.Array)
			{
				Error(where, (single ? "must be text or a list of text" : "must be a list of text") +
					$", not {Describe(value)}; the loader ignores it");
				return null;
			}

			var items = new List<string>();
			int i = 0;
			foreach (JsonElement item in value.EnumerateArray())
			{
				string itemAt = At(where, i++);
				if (item.ValueKind == JsonValueKind.String)
				{
					string text = item.GetString() ?? string.Empty;
					if (text.Trim().Length == 0)
					{
						Warn(itemAt, "empty; skipped");
					}

					items.Add(text);
				}
				else if (!strict && item.ValueKind == JsonValueKind.Number)
				{
					Warn(itemAt, $"a number where text is expected; read as '{item.GetRawText()}'");
					items.Add(item.GetRawText());
				}
				else
				{
					Error(itemAt, $"must be text, not {Describe(item)}" + (strict ? "; skipped" : string.Empty));
				}
			}

			return items;
		}

		/// <summary>A list field; null when absent or not a list (the latter an error: the loader ignores it).</summary>
		public JsonElement? Array(JsonElement record, string key, string at)
		{
			if (!record.TryGetProperty(key, out JsonElement value))
			{
				return null;
			}

			if (value.ValueKind != JsonValueKind.Array)
			{
				Error(At(at, key), $"must be a list, not {Describe(value)}; the loader ignores it");
				return null;
			}

			return value;
		}

		/// <summary>
		/// A number field. Strict: read only when it is a JSON number. <paramref name="clamps"/>: the
		/// loader clamps an out-of-range value (a warning) rather than misbehaving (an error).
		/// </summary>
		public double? Number(
			JsonElement record, string key, string at, bool strict = false, double? min = null, double? max = null,
			bool integer = false, bool clamps = false)
		{
			if (!record.TryGetProperty(key, out JsonElement value))
			{
				return null;
			}

			string where = At(at, key);
			if (strict && value.ValueKind != JsonValueKind.Number)
			{
				Error(where, $"must be a number, not {Describe(value)}; the loader ignores it and uses its default");
				return null;
			}

			return LooseNumber(value, where, integer, min, max, clamps);
		}

		/// <summary>A value read with AsDouble/AsInt32: text holding a number and true/false convert.</summary>
		public double? LooseNumber(
			JsonElement value, string at, bool integer = false, double? min = null, double? max = null, bool clamps = false)
		{
			double number;
			switch (value.ValueKind)
			{
				case JsonValueKind.Number:
					number = value.GetDouble();
					break;
				case JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number):
					Warn(at, "a number written as text; write it without quotes");
					break;
				case JsonValueKind.True:
				case JsonValueKind.False:
					number = value.ValueKind == JsonValueKind.True ? 1 : 0;
					Warn(at, $"true/false where a number is expected; read as {Format(number)}");
					break;
				default:
					Error(at, $"must be a number, not {Describe(value)}");
					return null;
			}

			if (integer && number != Math.Truncate(number))
			{
				Warn(at, $"must be a whole number; {Format(number)} is read as {Format(Math.Truncate(number))}");
			}

			if (min is { } low && number < low)
			{
				Range(at, $"{Format(number)} is below the least allowed, {Format(low)}", clamps);
			}

			if (max is { } high && number > high)
			{
				Range(at, $"{Format(number)} is above the most allowed, {Format(high)}", clamps);
			}

			return number;
		}

		private void Range(string at, string message, bool clamps)
		{
			if (clamps)
			{
				Warn(at, message + "; it is clamped");
			}
			else
			{
				Error(at, message);
			}
		}

		/// <summary>A true/false field read with AsBool.</summary>
		public bool? Flag(JsonElement record, string key, string at)
		{
			if (!record.TryGetProperty(key, out JsonElement value))
			{
				return null;
			}

			string where = At(at, key);
			switch (value.ValueKind)
			{
				case JsonValueKind.True:
					return true;
				case JsonValueKind.False:
					return false;
				case JsonValueKind.Number:
					Warn(where, $"a number where true or false is expected; read as {(value.GetDouble() != 0 ? "true" : "false")}");
					return value.GetDouble() != 0;
				default:
					Error(where, $"must be true or false, not {Describe(value)}" +
						(value.ValueKind == JsonValueKind.String ? " (text is not read as the word it spells)" : string.Empty));
					return null;
			}
		}

		/// <summary>
		/// A list of <paramref name="least"/>..<paramref name="most"/> numbers. Fewer, or a wrong
		/// count where the loader wants it exact, and the loader uses its default (an error); more
		/// where <paramref name="extraIgnored"/> and the extras are dropped (a warning).
		/// </summary>
		public void Vector(JsonElement record, string key, string at, int least, int most, bool extraIgnored)
		{
			if (!record.TryGetProperty(key, out JsonElement value))
			{
				return;
			}

			string where = At(at, key);
			string shape = least == most ? $"{least} numbers" : $"{least} to {most} numbers";
			if (value.ValueKind != JsonValueKind.Array)
			{
				Error(where, $"must be a list of {shape}, not {Describe(value)}; the loader uses its default");
				return;
			}

			int count = value.GetArrayLength();
			if (count < least || (count > most && !extraIgnored))
			{
				Error(where, $"must hold {shape}, not {count}; the loader uses its default");
				return;
			}

			if (count > most)
			{
				Warn(where, $"holds {count} numbers; only the first {most} are read");
			}

			int i = 0;
			foreach (JsonElement item in value.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Number)
				{
					Error(At(where, i), $"must be a number, not {Describe(item)}");
				}

				i++;
			}
		}

		/// <summary>An HTML hex colour as <c>Color.HtmlIsValid</c> takes it: #rgb, #rgba, #rrggbb or #rrggbbaa, the # optional.</summary>
		public void Colour(JsonElement record, string key, string at)
		{
			if (!record.TryGetProperty(key, out JsonElement value))
			{
				return;
			}

			string text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
			string digits = text.StartsWith('#') ? text[1..] : text;
			if (value.ValueKind != JsonValueKind.String ||
				digits.Length is not (3 or 4 or 6 or 8) || !digits.All(Uri.IsHexDigit))
			{
				Error(At(at, key), $"'{text}' is not a colour: write #rrggbb or #rrggbbaa (hex); the default colour is used");
			}
		}
	}
}
