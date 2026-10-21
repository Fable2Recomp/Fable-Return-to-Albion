using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Refii.Ui;

/// <summary>
/// One composition of <c>ui/templates.json</c> files: the menus, screens and modals as they stand
/// after each file is applied over the last. The game's own records are the first file
/// (<see cref="Engine_UiGameTemplates"/>), then each enabled mod's in load order, then the player's
/// — every one through <see cref="ApplyText"/>, so the game is composed exactly as a mod is.
/// </summary>
/// <remarks>
/// <para>MENUS replace the whole record, or with <c>"patch": true</c> compose PER ENTRY by
/// <c>name</c>: a patch entry replaces the entry of that name where it stands, or is inserted
/// (<c>"after"</c>/<c>"before"</c> a named sibling, else at the end; <c>"parent"</c> nests it), or
/// with <c>"remove": true</c> deletes it. Siblings sharing a name under different <c>"when"</c>
/// conditions are VARIANTS: a patch entry stating a <c>"when"</c> addresses that variant; one
/// stating none addresses them all — a replacement stands alone in their place, a removal removes
/// every one. Menu-level fields present in a patch replace the menu's own.</para>
/// <para>SCREENS replace the whole record, or with <c>"patch": true</c> merge PER FIELD over the
/// record that stands (<see cref="Engine_UiTemplateJson.ParseScreen"/>).</para>
/// <para>MODALS compose whole records per key (<see cref="Engine_UiModals"/>).</para>
/// <para>Pure .NET: <see cref="Warn"/> and <see cref="Log"/> are the caller's to route.</para>
/// </remarks>
public sealed class Engine_UiTemplateSet
{
	public readonly Dictionary<uint, Engine_UiScreenTemplate> Screens = new();

	public readonly Dictionary<string, Engine_UiMenuTemplate> Menus = new();

	public readonly Engine_UiModals Modals = new();

	/// <summary>Which mod last stated each piece (<c>menu:pause-root/Map</c>, <c>screen:23</c>, <c>screen:23/fields</c>).</summary>
	public readonly Dictionary<string, string> Owners = new(StringComparer.Ordinal);

	public readonly List<Engine_UiTemplates.ModConflict> Conflicts = new();

	/// <summary>Where a file's problems go (a bad record, a patch with nowhere to land).</summary>
	public Action<string>? Warn { get; set; }

	/// <summary>Where a mod replacing another mod's piece is reported.</summary>
	public Action<string>? Log { get; set; }

	/// <summary>
	/// Applies one file over the records that stand. <paramref name="owner"/> is the mod's name,
	/// empty for the game's file and the player's. Returns the pieces it took.
	/// </summary>
	public int ApplyText(string text, string owner, string where)
	{
		if (Engine_UiTemplateJson.ParseObject(text) is not { } root)
		{
			Warn?.Invoke($"'{where}' is not a JSON object; ignored");
			return 0;
		}

		int taken = 0;
		if (root.TryGetPropertyValue("menus", out JsonNode? menusNode) && menusNode is JsonObject menus)
		{
			foreach ((string name, JsonNode? value) in menus)
			{
				if (value is JsonObject record && record.TryGetPropertyValue("patch", out JsonNode? patch) &&
					Engine_UiTemplateJson.Flag(patch))
				{
					taken += PatchMenu(name, record, owner, where);
				}
				else if (Engine_UiTemplateJson.ParseMenu(name, value) is { } menu)
				{
					NoteOwner("menu:" + name, owner);
					Menus[menu.Name] = menu;
					++taken;
				}
			}
		}

		if (root.TryGetPropertyValue("screens", out JsonNode? screensNode) && screensNode is JsonObject screens)
		{
			foreach ((string key, JsonNode? value) in screens)
			{
				if (!uint.TryParse(key, out uint id))
				{
					Warn?.Invoke($"'{where}': screen '{key}' is not a screen id; ignored");
					continue;
				}

				bool patch = value is JsonObject record && record.TryGetPropertyValue("patch", out JsonNode? flag) &&
					Engine_UiTemplateJson.Flag(flag);
				Screens.TryGetValue(id, out Engine_UiScreenTemplate? standing);
				if (Engine_UiTemplateJson.ParseScreen(id, value, patch ? standing : null) is not { } screen)
				{
					continue;
				}

				if (patch && value is JsonObject fields)
				{
					foreach ((string field, _) in fields)
					{
						if (field != "patch")
						{
							NoteOwner($"screen:{key}/{field}", owner);
						}
					}
				}
				else
				{
					NoteOwner("screen:" + key, owner);
				}

				Screens[id] = screen;
				++taken;
			}
		}

		taken += Modals.ApplyText(text, piece => NoteOwner(piece, owner), warning => Warn?.Invoke($"'{where}': {warning}"));
		return taken;
	}

	/// <summary>Records who stated a piece; a second mod stating it is a conflict it won.</summary>
	public void NoteOwner(string piece, string owner)
	{
		if (owner.Length == 0)
		{
			return;
		}

		if (Owners.TryGetValue(piece, out string? previous) &&
			!string.Equals(previous, owner, StringComparison.OrdinalIgnoreCase))
		{
			Conflicts.Add(new Engine_UiTemplates.ModConflict(piece, owner, previous));
			Log?.Invoke($"'{piece}': mod '{owner}' replaces mod '{previous}'");
		}

		Owners[piece] = owner;
	}

	private int PatchMenu(string name, JsonObject record, string owner, string where)
	{
		if (!Menus.TryGetValue(name, out Engine_UiMenuTemplate? menu))
		{
			menu = new Engine_UiMenuTemplate { Name = name };
			Menus[name] = menu;
		}

		Engine_UiTemplateJson.ApplyMenuFields(menu, record);
		if (!record.TryGetPropertyValue("entries", out JsonNode? entriesNode) || entriesNode is not JsonArray entries)
		{
			return 0;
		}

		int taken = 0;
		foreach (JsonNode? value in entries)
		{
			if (value is not JsonObject patch)
			{
				continue;
			}

			string entryName = patch.TryGetPropertyValue("name", out JsonNode? n) ? Engine_UiTemplateJson.Text(n) : string.Empty;
			if (entryName.Length == 0)
			{
				continue;
			}

			string parentName = patch.TryGetPropertyValue("parent", out JsonNode? p) ? Engine_UiTemplateJson.Text(p) : string.Empty;
			List<Engine_UiMenuEntry>? siblings = parentName.Length == 0
				? menu.Entries
				: FindEntry(menu.Entries, parentName)?.Children;
			if (siblings is null)
			{
				Warn?.Invoke($"'{where}': menu '{name}' has no entry '{parentName}' to put '{entryName}' under; ignored");
				continue;
			}

			NoteOwner($"menu:{name}/{entryName}", owner);

			// The variants this patch addresses: the one stating its "when", or every one of the name.
			bool variant = patch.TryGetPropertyValue("when", out JsonNode? w);
			string when = variant ? Engine_UiTemplateJson.Text(w) : string.Empty;
			bool Addressed(Engine_UiMenuEntry e) =>
				e.Name == entryName && (!variant || string.Equals(e.When, when, StringComparison.Ordinal));

			int at = siblings.FindIndex(Addressed);
			if (patch.TryGetPropertyValue("remove", out JsonNode? remove) && Engine_UiTemplateJson.Flag(remove))
			{
				if (at >= 0)
				{
					siblings.RemoveAll(Addressed);
					++taken;
				}

				continue;
			}

			if (Engine_UiTemplateJson.ParseEntry(patch) is not { } parsed)
			{
				continue;
			}

			if (at >= 0)
			{
				siblings[at] = parsed;
				for (int i = siblings.Count - 1; i > at; --i)
				{
					if (Addressed(siblings[i]))
					{
						siblings.RemoveAt(i);
					}
				}
			}
			else
			{
				siblings.Insert(InsertIndex(siblings, patch), parsed);
			}

			++taken;
		}

		return taken;
	}

	private static int InsertIndex(List<Engine_UiMenuEntry> siblings, JsonObject patch)
	{
		if (patch.TryGetPropertyValue("after", out JsonNode? after))
		{
			string name = Engine_UiTemplateJson.Text(after);
			int index = siblings.FindLastIndex(e => e.Name == name);
			if (index >= 0)
			{
				return index + 1;
			}
		}

		if (patch.TryGetPropertyValue("before", out JsonNode? before))
		{
			string name = Engine_UiTemplateJson.Text(before);
			int index = siblings.FindIndex(e => e.Name == name);
			if (index >= 0)
			{
				return index;
			}
		}

		return siblings.Count;
	}

	/// <summary>The first entry of that name at any depth.</summary>
	public static Engine_UiMenuEntry? FindEntry(List<Engine_UiMenuEntry> entries, string name)
	{
		foreach (Engine_UiMenuEntry entry in entries)
		{
			if (entry.Name == name)
			{
				return entry;
			}

			if (FindEntry(entry.Children, name) is { } nested)
			{
				return nested;
			}
		}

		return null;
	}

	/// <summary>
	/// Every record as it stands, in the file's shape (<c>menus</c>, <c>screens</c>, <c>modals</c>).
	/// <paramref name="full"/> writes every screen field — the snapshot; otherwise only what differs
	/// from the reader's default.
	/// </summary>
	public JsonObject Snapshot(bool full = true)
	{
		var menus = new JsonObject();
		foreach ((string name, Engine_UiMenuTemplate menu) in Menus)
		{
			menus[name] = Engine_UiTemplateJson.MenuJson(menu);
		}

		var screens = new JsonObject();
		foreach ((uint id, Engine_UiScreenTemplate screen) in Screens)
		{
			screens[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
				Engine_UiTemplateJson.ScreenJson(screen, full);
		}

		return new JsonObject
		{
			["menus"] = menus,
			["screens"] = screens,
			[Engine_UiModals.Section] = JsonNode.Parse(Modals.ToJson()),
		};
	}

	public string SnapshotText(bool full = true) => Engine_UiTemplateJson.Write(Snapshot(full));
}
