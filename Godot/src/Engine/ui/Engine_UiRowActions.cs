using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Refii.Ui;

/// <summary>
/// ROW ACTIONS ON THE GAME'S SCREEN LISTS — the <c>"rowActions"</c> block of <c>ui/templates.json</c>:
/// per menu template name (the list's population script: <c>ItemsMenu</c>, <c>WeaponsMenu</c>,
/// <c>ExpressionsMenu</c>, <c>AbilitiesMenu</c>, or a mod menu's own name), a list of
/// <c>{ "button", "tag", "command" }</c>. While that list is up and a row is highlighted, the game's
/// UI owner draws the prompt (the button's icon and the tag's text) beside the page buttons, holds the
/// button from the guest, and on the press — or a click on the prompt — raises <c>mod_menu_row</c>
/// with the screen's template name, the row's name, the row's own command and the button, then runs
/// the action's command as a mod menu row's is run (<c>Mod:lua:</c>, or any verb).
/// </summary>
/// <remarks>
/// Composed on the template set like the modals: a later file's list for a menu replaces the earlier
/// one's whole, <c>null</c> or <c>[]</c> removes it. Buttons are the two the screen lists can hold
/// from the guest: <c>X</c> (the equip hold) and <c>Y</c> (the flip hold). Pure: held by tests.
/// </remarks>
public sealed class Engine_UiRowActions
{
	public const string Section = "rowActions";

	/// <summary>The buttons a row action may name, as the file spells them.</summary>
	public static readonly string[] Buttons = { "X", "Y" };

	/// <summary>One action: the button, the prompt's text tag (a Babel tag or literal), the command run, the mod that stated it.</summary>
	public readonly record struct RowAction(string Button, string Tag, string Command, string Mod);

	private readonly Dictionary<string, List<RowAction>> _byMenu = new(StringComparer.Ordinal);

	public int Count => _byMenu.Count;

	/// <summary>Every menu with actions, by name, in name order.</summary>
	public IEnumerable<KeyValuePair<string, IReadOnlyList<RowAction>>> Rows =>
		_byMenu.OrderBy(pair => pair.Key, StringComparer.Ordinal)
			.Select(pair => new KeyValuePair<string, IReadOnlyList<RowAction>>(pair.Key, pair.Value));

	/// <summary>The actions stated for <paramref name="menu"/>, in file order; empty for none.</summary>
	public IReadOnlyList<RowAction> For(string menu) =>
		_byMenu.TryGetValue(menu, out List<RowAction>? actions) ? actions : Array.Empty<RowAction>();

	/// <summary>
	/// Applies one templates file's <c>"rowActions"</c> over the table for <paramref name="owner"/> (the
	/// mod's name; empty for the game's or the player's). <paramref name="noteOwner"/> is told each
	/// menu it states (<c>"rowActions:&lt;menu&gt;"</c>), <paramref name="warn"/> each record it
	/// refuses. Returns how many menus it set or removed; 0 for a file with no block or not JSON.
	/// </summary>
	public int ApplyText(string text, string owner, Action<string> noteOwner, Action<string> warn)
	{
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(text);
		}
		catch (JsonException)
		{
			return 0;
		}

		using (document)
		{
			if (document.RootElement.ValueKind != JsonValueKind.Object ||
				!document.RootElement.TryGetProperty(Section, out JsonElement block))
			{
				return 0;
			}

			if (block.ValueKind != JsonValueKind.Object)
			{
				warn($"\"{Section}\" must be an object of action lists by menu name; ignored");
				return 0;
			}

			int taken = 0;
			foreach (JsonProperty menu in block.EnumerateObject())
			{
				string name = menu.Name.Trim();
				if (name.Length == 0)
				{
					warn($"\"{Section}\": a menu needs a name; ignored");
					continue;
				}

				if (menu.Value.ValueKind == JsonValueKind.Null ||
					(menu.Value.ValueKind == JsonValueKind.Array && menu.Value.GetArrayLength() == 0))
				{
					noteOwner($"{Section}:{name}");
					if (_byMenu.Remove(name))
					{
						++taken;
					}

					continue;
				}

				if (menu.Value.ValueKind != JsonValueKind.Array)
				{
					warn($"\"{Section}\" '{name}' must be a list of {{ \"button\", \"tag\", \"command\" }}; ignored");
					continue;
				}

				var actions = new List<RowAction>();
				int index = 0;
				foreach (JsonElement record in menu.Value.EnumerateArray())
				{
					if (ParseRecord(record, owner, out RowAction action, out string error))
					{
						actions.Add(action);
					}
					else
					{
						warn($"\"{Section}\" '{name}'[{index}]: {error}; ignored");
					}

					++index;
				}

				if (actions.Count == 0)
				{
					continue;
				}

				noteOwner($"{Section}:{name}");
				_byMenu[name] = actions;
				++taken;
			}

			return taken;
		}
	}

	/// <summary>One record: a button of <see cref="Buttons"/>, a tag, a command; false with the reason.</summary>
	public static bool ParseRecord(JsonElement record, string owner, out RowAction action, out string error)
	{
		action = default;
		error = string.Empty;
		if (record.ValueKind != JsonValueKind.Object)
		{
			error = "must be an object { \"button\", \"tag\", \"command\" }";
			return false;
		}

		string button = Text(record, "button").ToUpperInvariant();
		if (!Buttons.Contains(button, StringComparer.Ordinal))
		{
			error = $"\"button\" must be one of {string.Join(", ", Buttons)}";
			return false;
		}

		string command = Text(record, "command");
		if (command.Length == 0)
		{
			error = "\"command\" names nothing";
			return false;
		}

		action = new RowAction(button, Text(record, "tag"), command, owner);
		return true;
	}

	private static string Text(JsonElement record, string key) =>
		record.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? (value.GetString() ?? string.Empty).Trim()
			: string.Empty;

	/// <summary>The table as the snapshot's <c>"rowActions"</c> block.</summary>
	public string ToJson()
	{
		var block = new System.Text.Json.Nodes.JsonObject();
		foreach ((string menu, IReadOnlyList<RowAction> actions) in Rows)
		{
			var list = new System.Text.Json.Nodes.JsonArray();
			foreach (RowAction action in actions)
			{
				list.Add(new System.Text.Json.Nodes.JsonObject
				{
					["button"] = action.Button,
					["tag"] = action.Tag,
					["command"] = action.Command,
				});
			}

			block[menu] = list;
		}

		return block.ToJsonString();
	}
}
