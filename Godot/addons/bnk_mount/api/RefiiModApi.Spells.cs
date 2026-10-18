#if TOOLS
using System;
using System.Linq;
using Godot;
using Refii.Mods;

namespace Refii.EditorTools;

/// <summary>
/// The spells kind on the script API: <c>spells/&lt;id&gt;.json</c>, a custom spell that stands in
/// for one of the eight spell types (<see cref="ModSpells"/>).
/// </summary>
public partial class RefiiModApi
{
	/// <summary>
	/// Writes <c>spells/&lt;id&gt;.json</c> from <paramref name="spell"/>: <c>id</c> names the
	/// file; the rest is the schema (<c>replaces</c> required, then <c>name</c>,
	/// <c>description</c>, <c>icon</c>, <c>card</c>, <c>damage</c>, <c>look</c>, <c>on_cast</c>,
	/// <c>on_hit</c>). Refused with the reason when the file would not be read.
	/// </summary>
	public string SetSpell(Godot.Collections.Dictionary spell)
	{
		string id = spell.TryGetValue("id", out Variant idValue) ? idValue.AsString().Trim() : string.Empty;
		if (id.Length == 0 || id.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-')))
		{
			return "the spell needs \"id\": letters, digits, '_' or '-' (it names spells/<id>.json)";
		}

		var body = new Godot.Collections.Dictionary();
		foreach (System.Collections.Generic.KeyValuePair<Variant, Variant> entry in spell)
		{
			if (entry.Key.AsString() != "id")
			{
				body[entry.Key] = entry.Value;
			}
		}

		string json = Json.Stringify(body, "\t") + "\n";
		if (!ModSpells.TryParse(GetActiveMod(), id, json, out _, out string error))
		{
			return error;
		}

		return WriteText(ModSpells.Folder + id.ToLowerInvariant() + ".json", json);
	}

	/// <summary>Removes <c>spells/&lt;id&gt;.json</c>.</summary>
	public string RemoveSpell(string id) => DeleteFile(ModSpells.Folder + id.Trim().ToLowerInvariant() + ".json");

	/// <summary>The eight spell types a custom spell can stand in for, in the binary's index order 0..7.</summary>
	public string[] ListSpellTypes() => ModSpells.TypeNames.ToArray();
}
#endif
