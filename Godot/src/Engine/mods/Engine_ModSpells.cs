using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Godot;
using Refii.Bnk;
using Refii.Text;

namespace Refii.Mods;

/// <summary>One <c>spells/&lt;id&gt;.json</c> as parsed: a stand-in for one of the eight spell types.</summary>
public sealed record ModSpell(
	string Mod,
	string Id,
	int Type,
	string Name,
	string Description,
	string Icon,
	string Card,
	float Damage,
	string Look,
	float LookEquals,
	float LookSeconds,
	string OnCast,
	string OnHit)
{
	/// <summary>The mod value the look is published under; the recipe's <c>when</c> is <c>mod:</c> + this.</summary>
	public string LookKey => $"{Mod}.spells.{Id}.look";

	/// <summary>The label the generated Lua carries in <c>RefiiModErrors</c> and the mount log.</summary>
	public string Label => $"{Mod}/{ModSpells.Folder}{Id}";
}

/// <summary>
/// The declarative spells kind: <c>spells/&lt;id&gt;.json</c> in a mod package, a custom spell that
/// STANDS IN for one of the eight spell types.
/// </summary>
/// <remarks>
/// <para>WHAT THE BINARY ALLOWS. The spell factory (<c>rex_CSpellRegistry_Create</c>) is a hard
/// switch over the eight types — 0 Lightning, 1 Fireball, 2 Slow Time, 3 Blades, 4 Vortex,
/// 5 Chaos, 6 Force Push, 7 Raise Dead — and the wheel, the abilities book and the hit record all
/// carry the type as that index. A ninth class cannot be data. A custom spell therefore REPLACES
/// one type: it keeps that type's class (its mechanics, its slot, its ability record) and gets its
/// own name, description, icon, damage, look and Lua behaviour.</para>
/// <code>
/// { "replaces": "Chaos",
///   "name": "Frost Nova", "description": "…",
///   "icon": "art/gui/spells/frost.tex.png", "card": "art/gui/spells/frost_card.tex.png",
///   "damage": 1.3,
///   "look": { "base": ["fxspl_chaos_…"], "tint": [0.6, 0.8, 1.4], "seconds": 6 },
///   "on_cast": "Refii.log('nova')", "on_hit": "…" }
/// </code>
/// <para>Each field reaches the game through a mechanism that already exists, never a new one:
/// <c>name</c> and <c>description</c> are Babel rows over the replaced type's tags (the wheel's
/// <c>GUI_SCREEN_ABILITIES_WILL_&lt;X&gt;_NAME_LEVEL_06</c>, the book's per-level
/// <c>_NAME_LEVEL_01..05</c> and <c>_DESC_LEVEL_01..05</c> — <see cref="NameTags"/>,
/// <see cref="DescriptionTags"/>), applied to every language's <c>book.babel</c> in the mount;
/// <c>icon</c> and <c>card</c> are file overlays at the shipped texture paths
/// (<see cref="WheelIconPath"/>, <see cref="CardPath"/>), converted from PNG like a
/// <c>.tex.png</c>; <c>look</c> is an <see cref="Refii.Fx.Engine_FxRecipes"/> recipe body whose
/// <c>when</c>/<c>equals</c> the composer fills in; <c>damage</c>, <c>on_cast</c> and
/// <c>on_hit</c> become one Lua source per spell (<see cref="GenerateLua"/>) appended to
/// <see cref="ModLua.ExtraInitSources"/>, so they run in the game VM on the mod event bus.</para>
/// <para>Two mods replacing the same type: the later one in load order wins and the mount log
/// says so.</para>
/// </remarks>
public static class ModSpells
{
	public const string Folder = "spells/";

	/// <summary>The eight types, by the index the binary uses everywhere.</summary>
	public static readonly string[] TypeNames =
	{
		"Lightning", "Fireball", "SlowTime", "Blades", "Vortex", "Chaos", "ForcePush", "RaiseDead",
	};

	/// <summary>
	/// Per type: the Babel tag stem (<c>GUI_SCREEN_ABILITIES_WILL_&lt;stem&gt;_…</c>), the wheel
	/// icon stem under <c>art/gui/wheelicons/abilities/</c> and the tarot card stem under
	/// <c>art/gui/abilitytarot/</c>. The tag stems and card stems are the level records' own
	/// <c>NameTextTag</c>/<c>DescriptionTextTag</c>/<c>ImageFilePathAndName</c> values in
	/// <c>globals.gdb</c> (the Ability&lt;Name&gt;Base chains); the wheel stems are the literal
	/// table the binary builds the wheel from (<see cref="Refii.Ui.Engine_UiScreenConstants.Wills"/>).
	/// </summary>
	private static readonly (string Tag, string Wheel, string Card)[] Shipped =
	{
		("LIGHTNING", "will_shock", "shock_spell"),
		("FIREBALL", "will_blast", "blast_spell"),
		("SLOWTIME", "icon_will_time_control", "time_spell"),
		("SWORDS", "will_sword_of_damocles", "swords_spell"),
		("VORTEX", "will_vortex", "vortex_spell"),
		("CHAOS", "will_chaos", "chaos_spell"),
		("FORCE_PUSH", "will_force_push", "forcepush"),
		("DEAD_RISING", "will_dead_rising", "dead_rising"),
	};

	/// <summary>Other spellings a modder may reasonably write for a type.</summary>
	private static readonly Dictionary<string, int> Aliases = new(StringComparer.OrdinalIgnoreCase)
	{
		["swords"] = 3,
		["sword"] = 3,
		["timecontrol"] = 2,
		["time"] = 2,
		["deadrising"] = 7,
		["shock"] = 0,
		["blast"] = 1,
	};

	private const int MaxLevel = 5;

	private const float DefaultLookSeconds = 8f;

	/// <summary>A type name (any case, spaces and underscores ignored) or an index 0..7.</summary>
	public static bool TryResolveType(string text, out int type)
	{
		type = -1;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		string trimmed = text.Trim();
		if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
		{
			if (index < 0 || index >= TypeNames.Length)
			{
				return false;
			}

			type = index;
			return true;
		}

		string folded = trimmed.Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
		for (int i = 0; i < TypeNames.Length; i++)
		{
			if (string.Equals(TypeNames[i], folded, StringComparison.OrdinalIgnoreCase))
			{
				type = i;
				return true;
			}
		}

		if (Aliases.TryGetValue(folded, out int alias))
		{
			type = alias;
			return true;
		}

		return false;
	}

	/// <summary>
	/// The tags a type's name is read from: the wheel's <c>_NAME_LEVEL_06</c> (also the book's
	/// name when maxed — the guest overwrites the last character) and the book's <c>_01..05</c>.
	/// </summary>
	public static IReadOnlyList<string> NameTags(int type)
	{
		var tags = new List<string>(MaxLevel + 1);
		for (int level = 1; level <= MaxLevel + 1; level++)
		{
			tags.Add($"GUI_SCREEN_ABILITIES_WILL_{Shipped[type].Tag}_NAME_LEVEL_{level:00}");
		}

		return tags;
	}

	/// <summary>
	/// The book's per-level description tags. <c>globals.gdb</c> also carries
	/// <c>GUI_SCREEN_ABILITIES_WILL_CHAOSDESC_LEVEL_01</c> (no underscore); it is covered too in
	/// case a Chaos level record names it.
	/// </summary>
	public static IReadOnlyList<string> DescriptionTags(int type)
	{
		var tags = new List<string>(MaxLevel + 1);
		for (int level = 1; level <= MaxLevel; level++)
		{
			tags.Add($"GUI_SCREEN_ABILITIES_WILL_{Shipped[type].Tag}_DESC_LEVEL_{level:00}");
		}

		if (type == 5)
		{
			tags.Add("GUI_SCREEN_ABILITIES_WILL_CHAOSDESC_LEVEL_01");
		}

		return tags;
	}

	/// <summary>The wheel icon's shipped path, as the guest builds it from its literal prefix and stem.</summary>
	public static string WheelIconPath(int type) =>
		$"art/gui/wheelicons/abilities/{Shipped[type].Wheel}.tex";

	/// <summary>The abilities book's tarot still: the level record's <c>ImageFilePathAndName</c> + <c>.tex</c>.</summary>
	public static string CardPath(int type) => $"art/gui/abilitytarot/{Shipped[type].Card}.tex";

	/// <summary>Reads one <c>spells/&lt;id&gt;.json</c>; a bad type or a non-object is refused with the reason.</summary>
	public static bool TryParse(string mod, string id, string json, out ModSpell? spell, out string error)
	{
		spell = null;
		error = string.Empty;
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(json, new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip,
			});
		}
		catch (JsonException exception)
		{
			error = $"not JSON: {exception.Message}";
			return false;
		}

		using (document)
		{
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				error = "the file is not a JSON object";
				return false;
			}

			if (!root.TryGetProperty("replaces", out JsonElement replaces))
			{
				error = "\"replaces\" is required: one of " + string.Join(", ", TypeNames) + " or 0..7";
				return false;
			}

			string replacesText = replaces.ValueKind == JsonValueKind.Number
				? replaces.GetRawText()
				: replaces.ValueKind == JsonValueKind.String ? replaces.GetString() ?? string.Empty : replaces.GetRawText();
			if (!TryResolveType(replacesText, out int type))
			{
				error = $"\"replaces\": '{replacesText}' is not a spell type; the binary has exactly " +
					string.Join(", ", TypeNames) + " (0..7)";
				return false;
			}

			float damage = 1f;
			if (root.TryGetProperty("damage", out JsonElement damageElement))
			{
				if (damageElement.ValueKind != JsonValueKind.Number || !damageElement.TryGetSingle(out damage) || damage < 0f)
				{
					error = "\"damage\" must be a non-negative number (a multiplier on every hit of the type)";
					return false;
				}
			}

			string look = string.Empty;
			float lookEquals = 1f;
			float lookSeconds = DefaultLookSeconds;
			if (root.TryGetProperty("look", out JsonElement lookElement))
			{
				if (lookElement.ValueKind != JsonValueKind.Object)
				{
					error = "\"look\" must be an object: one fx recipe body";
					return false;
				}

				look = lookElement.GetRawText();
				if (lookElement.TryGetProperty("equals", out JsonElement equals) && equals.ValueKind == JsonValueKind.Number)
				{
					lookEquals = equals.GetSingle();
				}

				if (lookElement.TryGetProperty("seconds", out JsonElement seconds) && seconds.ValueKind == JsonValueKind.Number)
				{
					lookSeconds = Math.Max(0.1f, seconds.GetSingle());
				}
			}

			spell = new ModSpell(
				mod,
				id,
				type,
				Text(root, "name"),
				Text(root, "description"),
				Text(root, "icon"),
				Text(root, "card"),
				damage,
				look,
				lookEquals,
				lookSeconds,
				Text(root, "on_cast"),
				Text(root, "on_hit"));
			return true;
		}
	}

	private static string Text(JsonElement root, string key) =>
		root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString() ?? string.Empty
			: string.Empty;

	/// <summary>
	/// The spell's Lua, one chunk for the init composition. Every handler is filtered on the
	/// type: <c>spell_launch</c> remembers the type each (caster, slot) launched — the
	/// <c>spell_launched</c> event states the slot but not the type — and <c>spell_launched</c>
	/// consumes it, publishes the look and runs <c>on_cast</c>; <c>hit</c> scales <c>damage</c>;
	/// <c>hit_landed</c> runs <c>on_hit</c>; <c>tick</c> clears the look after its seconds. The
	/// modder's bodies run inside <c>pcall</c>, their error labelled in <c>RefiiModErrors</c>.
	/// <c>Refii.publish</c> is called only when the preamble defines it.
	/// </summary>
	public static string GenerateLua(ModSpell spell)
	{
		var text = new StringBuilder();
		string label = LuaString(spell.Label);
		bool look = spell.Look.Length != 0;
		text.Append("-- ").Append(spell.Label).Append(": a stand-in for spell type ")
			.Append(spell.Type).Append(" (").Append(TypeNames[spell.Type]).Append(")\n");
		text.Append("local SPELL_TYPE = ").Append(spell.Type).Append('\n');
		text.Append("local DAMAGE = ").Append(spell.Damage.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
		text.Append("local LOOK_KEY = ").Append(look ? LuaString(spell.LookKey) : "nil").Append('\n');
		text.Append("local LOOK_VALUE = ").Append(spell.LookEquals.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
		text.Append("local LOOK_SECONDS = ").Append(spell.LookSeconds.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
		text.Append("local clock, look_until = 0, nil\n");
		text.Append("local function fail(where, err)\n");
		text.Append("\tRefiiModErrors = (RefiiModErrors or '') .. ").Append(label)
			.Append(" .. ' ' .. where .. ': ' .. tostring(err) .. '\\n'\n");
		text.Append("end\n");
		text.Append("local function on_cast(e)\n").Append(spell.OnCast).Append("\nend\n");
		text.Append("local function on_hit(e)\n").Append(spell.OnHit).Append("\nend\n");
		text.Append("Refii._spells = Refii._spells or { pending = {} }\n");
		text.Append("local pending = Refii._spells.pending\n");
		text.Append("local function slot_key(e) return tostring(e.caster) .. ':' .. tostring(e.slot) end\n");
		text.Append(
			"Refii.on('spell_launch', function(e)\n" +
			"\tpending[slot_key(e)] = e.type\n" +
			"end)\n");
		text.Append(
			"Refii.on('spell_launched', function(e)\n" +
			"\tlocal key = slot_key(e)\n" +
			"\tif pending[key] ~= SPELL_TYPE then return end\n" +
			"\tpending[key] = nil\n" +
			"\te.type = SPELL_TYPE\n" +
			"\tif LOOK_KEY then\n" +
			"\t\tlook_until = clock + LOOK_SECONDS\n" +
			"\t\tif Refii.publish then Refii.publish(LOOK_KEY, LOOK_VALUE) end\n" +
			"\tend\n" +
			"\tlocal ok, err = pcall(on_cast, e)\n" +
			"\tif not ok then fail('on_cast', err) end\n" +
			"end)\n");
		if (spell.Damage != 1f)
		{
			text.Append(
				"Refii.on('hit', function(e)\n" +
				"\tif e.spell_type == SPELL_TYPE and type(e.damage) == 'number' then e.damage = e.damage * DAMAGE end\n" +
				"end)\n");
		}

		if (spell.OnHit.Trim().Length != 0)
		{
			text.Append(
				"Refii.on('hit_landed', function(e)\n" +
				"\tif e.spell_type ~= SPELL_TYPE then return end\n" +
				"\tlocal ok, err = pcall(on_hit, e)\n" +
				"\tif not ok then fail('on_hit', err) end\n" +
				"end)\n");
		}

		if (look)
		{
			text.Append(
				"Refii.on('tick', function(e)\n" +
				"\tclock = clock + (e.seconds or 0)\n" +
				"\tif look_until and clock >= look_until then\n" +
				"\t\tlook_until = nil\n" +
				"\t\tif Refii.publish then Refii.publish(LOOK_KEY, 0) end\n" +
				"\tend\n" +
				"end)\n");
		}

		return text.ToString();
	}

	private static string LuaString(string text) =>
		"\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

	/// <summary>
	/// The recipe JSON <see cref="Refii.Fx.Engine_FxRecipes.Parse"/> reads for the spell's look:
	/// the body with <c>id</c>, <c>when</c> and <c>equals</c> filled in.
	/// </summary>
	public static string LookRecipesJson(ModSpell spell)
	{
		using JsonDocument body = JsonDocument.Parse(spell.Look);
		var buffer = new MemoryStream();
		using (var writer = new Utf8JsonWriter(buffer))
		{
			writer.WriteStartObject();
			writer.WritePropertyName("recipes");
			writer.WriteStartArray();
			writer.WriteStartObject();
			writer.WriteString("id", $"{spell.Mod}.spells.{spell.Id}");
			writer.WritePropertyName("when");
			writer.WriteStartArray();
			writer.WriteStringValue("mod:" + spell.LookKey);
			writer.WriteEndArray();
			writer.WriteNumber("equals", spell.LookEquals);
			foreach (JsonProperty property in body.RootElement.EnumerateObject())
			{
				if (property.Name is "id" or "when" or "equals" or "seconds")
				{
					continue;
				}

				property.WriteTo(writer);
			}

			writer.WriteEndObject();
			writer.WriteEndArray();
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(buffer.ToArray());
	}

	/// <summary>The labels this composer put into <see cref="ModLua.ExtraInitSources"/> last time.</summary>
	private static readonly HashSet<string> _labels = new(StringComparer.Ordinal);

	private const string RecipeSource = "spells";

	/// <summary>
	/// Reads every enabled mod's <c>spells/*.json</c> (mount order), resolves who stands in for
	/// each type, and installs each winner: Lua into the init composition, Babel rows over the
	/// type's tags, the icon and card at their shipped paths, the look as a recipe. Returns one
	/// line per decision for the mount log; refusals go to the warning channel.
	/// </summary>
	public static List<string> Apply(BnkFileSystem mounted, IReadOnlyList<ModOverlay> overlays)
	{
		ModLua.ExtraInitSources.RemoveAll(entry => _labels.Contains(entry.Label));
		_labels.Clear();

		var report = new List<string>();
		var byType = new Dictionary<int, ModSpell>();
		var sources = new Dictionary<ModSpell, Func<string, byte[]?>>();
		foreach (ModOverlay overlay in overlays)
		{
			foreach ((string id, string json, Func<string, byte[]?> read) in ReadSpells(overlay))
			{
				if (!TryParse(overlay.Name, id, json, out ModSpell? spell, out string error) || spell is null)
				{
					GD.PushWarning($"[mods] spell {overlay.Name}/{Folder}{id}.json refused: {error}");
					continue;
				}

				if (byType.TryGetValue(spell.Type, out ModSpell? earlier))
				{
					GD.PushWarning($"[mods] spell {spell.Label} replaces {TypeNames[spell.Type]} over {earlier.Label}; " +
						"the later mod in the load order wins");
				}

				byType[spell.Type] = spell;
				sources[spell] = read;
			}
		}

		var rows = new List<BabelOverlayRow>();
		var recipes = new List<Refii.Fx.Engine_FxRecipes.Recipe>();
		foreach (int type in SortedKeys(byType))
		{
			ModSpell spell = byType[type];
			Func<string, byte[]?> read = sources[spell];
			var parts = new List<string> { "lua" };

			ModLua.ExtraInitSources.Add((spell.Label, GenerateLua(spell)));
			_labels.Add(spell.Label);

			if (spell.Name.Length != 0)
			{
				foreach (string tag in NameTags(type))
				{
					rows.Add(new BabelOverlayRow { Key = tag, Value = spell.Name, Mod = spell.Mod });
				}

				parts.Add("name");
			}

			if (spell.Description.Length != 0)
			{
				foreach (string tag in DescriptionTags(type))
				{
					rows.Add(new BabelOverlayRow { Key = tag, Value = spell.Description, Mod = spell.Mod });
				}

				parts.Add("description");
			}

			if (spell.Icon.Length != 0 && Overlay(mounted, spell, spell.Icon, WheelIconPath(type), read))
			{
				parts.Add("icon");
			}

			if (spell.Card.Length != 0 && Overlay(mounted, spell, spell.Card, CardPath(type), read))
			{
				parts.Add("card");
			}

			if (spell.Look.Length != 0)
			{
				List<Refii.Fx.Engine_FxRecipes.Recipe> parsed = Refii.Fx.Engine_FxRecipes.Parse(spell.Mod, LookRecipesJson(spell));
				if (parsed.Count != 0)
				{
					recipes.AddRange(parsed);
					parts.Add("look");
				}
				else
				{
					GD.PushWarning($"[mods] spell {spell.Label}: the look is not a recipe the FX recipes read; left out");
				}
			}

			if (spell.Damage != 1f)
			{
				parts.Add($"damage x{spell.Damage.ToString("0.##", CultureInfo.InvariantCulture)}");
			}

			report.Add($"{spell.Label} stands in for {TypeNames[type]} ({type}): {string.Join(", ", parts)}");
		}

		if (rows.Count != 0)
		{
			ApplyRows(mounted, rows, report);
		}

		Refii.Fx.Engine_FxRecipes.Contribute(RecipeSource, recipes);
		return report;
	}

	private static IEnumerable<int> SortedKeys(Dictionary<int, ModSpell> byType)
	{
		var keys = new List<int>(byType.Keys);
		keys.Sort();
		return keys;
	}

	/// <summary>
	/// The rows over every language's <c>book.babel</c> the mount serves — the file the row
	/// pass already composed, so a mod's own <c>.babel.csv</c> rows and these stack the same way
	/// (later wins).
	/// </summary>
	private static void ApplyRows(BnkFileSystem mounted, List<BabelOverlayRow> rows, List<string> report)
	{
		int tables = 0;
		foreach (string language in new List<string>(mounted.EnumerateDirectories("language")))
		{
			string table = $"language/{language}/text/book.babel";
			if (!mounted.TryGetFile(table, out BnkFile file))
			{
				continue;
			}

			byte[] original;
			try
			{
				original = file.ReadAllBytes();
			}
			catch (Exception exception)
			{
				GD.PushWarning($"[mods] spell rows: '{table}' could not be read: {exception.Message}");
				continue;
			}

			BabelOverlayResult result = BabelOverlay.Apply(original, rows);
			foreach (string error in result.Errors)
			{
				GD.PushWarning($"[mods] spell rows: {table}: {error}");
			}

			if (result.Applied == 0)
			{
				continue;
			}

			byte[] bytes = result.Bytes;
			mounted.AddVirtualFile(table, $"<spells:{table}>", bytes.LongLength, () => bytes, rows[^1].Mod);
			tables++;
		}

		if (tables != 0)
		{
			Refii.Gdb.Engine_BnkTextBankLoader.Invalidate();
			report.Add($"{rows.Count} text row(s) over {tables} language table(s)");
		}
	}

	/// <summary>
	/// One image from the package at the replaced type's shipped texture path: a <c>.png</c> is
	/// converted as a <c>.tex.png</c> overlay is, anything else is served as the bytes it is.
	/// </summary>
	private static bool Overlay(
		BnkFileSystem mounted, ModSpell spell, string source, string target, Func<string, byte[]?> read)
	{
		byte[]? bytes = read(source);
		if (bytes is null)
		{
			GD.PushWarning($"[mods] spell {spell.Label}: '{source}' is not in the package");
			return false;
		}

		if (source.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
		{
			var conversion = new ImageConversion { Target = target, Mod = spell.Mod };
			if (!ModImageOverlays.TryConvert(bytes, out byte[] tex, conversion))
			{
				GD.PushWarning($"[mods] {ModImageOverlays.Describe(conversion)}");
				return false;
			}

			bytes = tex;
		}

		byte[] served = bytes;
		mounted.AddVirtualFile(target, $"<spell:{source}>", served.LongLength, () => served, spell.Mod);
		return true;
	}

	/// <summary>
	/// Every <c>spells/*.json</c> in one mod, with a reader for the mod's other files (the icon,
	/// the card) by package path: a packed <c>.f2mod</c> or a loose asset tree, like the image kind.
	/// </summary>
	private static List<(string Id, string Json, Func<string, byte[]?> Read)> ReadSpells(ModOverlay overlay)
	{
		var found = new List<(string Id, string Json, Func<string, byte[]?> Read)>();
		string sourcePath = !string.IsNullOrEmpty(overlay.PackagePath) ? overlay.PackagePath : overlay.AssetRoot;
		if (File.Exists(sourcePath) && sourcePath.EndsWith(".f2mod", StringComparison.OrdinalIgnoreCase))
		{
			if (!F2ModPackage.TryOpen(sourcePath, out F2ModPackage? package, out string openError) || package is null)
			{
				GD.PushWarning($"[mods] spell: {overlay.Name}: {openError}");
				return found;
			}

			using (package)
			{
				foreach (F2ModEntry entry in package.Entries)
				{
					if (!IsSpellFile(Relative(entry.Path), out string id))
					{
						continue;
					}

					string json;
					try
					{
						json = Encoding.UTF8.GetString(package.ReadEntryBytes(entry));
					}
					catch (Exception exception)
					{
						GD.PushWarning($"[mods] spell: {overlay.Name}/{entry.Path}: {exception.Message}");
						continue;
					}

					found.Add((id, json, path => ReadPackaged(sourcePath, path)));
				}
			}

			return found;
		}

		if (!Directory.Exists(overlay.AssetRoot))
		{
			return found;
		}

		foreach (string file in ModManifest.EnumerateAssets(overlay.AssetRoot))
		{
			string relative;
			string json;
			try
			{
				relative = Path.GetRelativePath(overlay.AssetRoot, file);
				if (!IsSpellFile(Relative(relative), out string id))
				{
					continue;
				}

				json = File.ReadAllText(file);
				string root = overlay.AssetRoot;
				found.Add((id, json, path => ReadLoose(root, path)));
			}
			catch (Exception exception)
			{
				GD.PushWarning($"[mods] spell: {overlay.Name}/{file}: {exception.Message}");
			}
		}

		return found;
	}

	private static string Relative(string path)
	{
		string relative = path.Replace('\\', '/').TrimStart('/');
		return relative.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ? relative["files/".Length..] : relative;
	}

	private static bool IsSpellFile(string relative, out string id)
	{
		id = string.Empty;
		string normalized = ModLoadOrder.Normalize(relative);
		if (!normalized.StartsWith(Folder, StringComparison.Ordinal) ||
			!normalized.EndsWith(".json", StringComparison.Ordinal) ||
			normalized.IndexOf('/', Folder.Length) >= 0)
		{
			return false;
		}

		id = normalized[Folder.Length..^".json".Length];
		return id.Length != 0;
	}

	private static byte[]? ReadPackaged(string packagePath, string path)
	{
		if (!F2ModPackage.TryOpen(packagePath, out F2ModPackage? package, out _) || package is null)
		{
			return null;
		}

		using (package)
		{
			return package.TryGetEntry(path, out F2ModEntry? entry) && entry is not null
				? package.ReadEntryBytes(entry)
				: null;
		}
	}

	private static byte[]? ReadLoose(string root, string path)
	{
		string full = Path.Combine(root, path.Replace('\\', '/').TrimStart('/'));
		return File.Exists(full) ? File.ReadAllBytes(full) : null;
	}
}
