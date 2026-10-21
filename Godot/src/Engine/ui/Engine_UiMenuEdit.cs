using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Refii.Ui;

/// <summary>
/// THE MENU EDIT, shared by every editor of <c>ui/templates.json</c> menus — the Mod Studio's UI tab
/// (a mod's own menus whole, and patches to the menus it does not own) and the player's menu tool
/// (whole records in <c>user://ui/templates.json</c>). The records are held as JSON exactly as read,
/// so every field an editor does not show round-trips; only what an editor changed is written.
/// </summary>
/// <remarks>
/// <para>A menu record is <c>{ highlight, title, source, rowCommand, entries }</c>; an entry is
/// <c>{ name, tag, command, opens, source, icon, eventType, rating, hostOnly, drop, counted,
/// children }</c> (<see cref="Engine_UiTemplates"/>'s reader and its snapshot writer, which leaves
/// a field out at its default — <see cref="SetField"/> follows the same rule).</para>
/// <para><see cref="Patch"/> is the inverse of the engine's patch composition: from the menu a mod
/// patches (composed WITHOUT that mod) and the menu as the author left it, the per-entry records
/// that make the one into the other — replacements by name, inserts placed <c>after</c>/<c>before</c>
/// a sibling under a <c>parent</c>, and <c>remove</c>s. Pure .NET, held by tests.</para>
/// </remarks>
public static class Engine_UiMenuEdit
{
	public const string EntriesKey = "entries";
	public const string ChildrenKey = "children";
	public const string NameKey = "name";

	/// <summary>The menu-level fields a record states beside its entries.</summary>
	public static readonly string[] MenuFields = { "highlight", "title", "source", "rowCommand" };

	/// <summary>The keys a patch entry carries that are about placing it, not about the row.</summary>
	public static readonly string[] PatchKeys = { "patch", "after", "before", "parent", "remove" };

	// ---- equality -------------------------------------------------------------------------

	/// <summary>
	/// Deep equality as the loaders read JSON: object key order is ignored and numbers compare by
	/// value (Godot writes <c>301.0</c> where another writer wrote <c>301</c>).
	/// </summary>
	public static bool Same(JsonNode? a, JsonNode? b)
	{
		if (a is null || b is null)
		{
			return a is null && b is null;
		}

		switch (a)
		{
			case JsonObject left when b is JsonObject right:
				if (left.Count != right.Count)
				{
					return false;
				}

				foreach ((string key, JsonNode? value) in left)
				{
					if (!right.TryGetPropertyValue(key, out JsonNode? other) || !Same(value, other))
					{
						return false;
					}
				}

				return true;

			case JsonArray left when b is JsonArray right:
				if (left.Count != right.Count)
				{
					return false;
				}

				for (int i = 0; i < left.Count; i++)
				{
					if (!Same(left[i], right[i]))
					{
						return false;
					}
				}

				return true;

			case JsonValue left when b is JsonValue right:
				if (Number(left) is { } x && Number(right) is { } y)
				{
					return Math.Abs(x - y) <= 1e-9 * Math.Max(1.0, Math.Abs(x));
				}

				return left.ToJsonString() == right.ToJsonString();
		}

		return false;
	}

	/// <summary>A JSON number's value, whatever it was created from; null for anything else.</summary>
	public static double? NumberOf(JsonNode? node) => node is JsonValue value ? Number(value) : null;

	private static double? Number(JsonValue value) =>
		value.TryGetValue(out double d) ? d :
		value.TryGetValue(out long l) ? l :
		value.TryGetValue(out int i) ? i :
		value.TryGetValue(out float f) ? f :
		null;

	// ---- rows -------------------------------------------------------------------------------

	/// <summary>An entry's <c>name</c>, empty when it has none.</summary>
	public static string Name(JsonObject entry) => Text(entry, NameKey);

	/// <summary>A string field, empty when missing or not a string.</summary>
	public static string Text(JsonObject record, string key) =>
		record[key] is JsonValue value && value.TryGetValue(out string? text) ? text : string.Empty;

	/// <summary>A number field, <paramref name="fallback"/> when missing or not a number.</summary>
	public static double Number(JsonObject record, string key, double fallback) =>
		record[key] is JsonValue value && Number(value) is { } n ? n : fallback;

	/// <summary>A boolean field, <paramref name="fallback"/> when missing or not a boolean.</summary>
	public static bool Flag(JsonObject record, string key, bool fallback) =>
		record[key] is JsonValue value && value.TryGetValue(out bool b) ? b : fallback;

	/// <summary>An entry's children (an empty array when it states none; not attached).</summary>
	public static JsonArray Children(JsonObject entry) =>
		entry[ChildrenKey] as JsonArray ?? new JsonArray();

	/// <summary>A record's entries (an empty array when it states none; not attached).</summary>
	public static JsonArray Entries(JsonObject menu) =>
		menu[EntriesKey] as JsonArray ?? new JsonArray();

	/// <summary>The value each optional row field has when it is not written (the snapshot's rule).</summary>
	private static readonly Dictionary<string, JsonNode?> Defaults = new(StringComparer.Ordinal)
	{
		["command"] = JsonValue.Create(string.Empty),
		["opens"] = JsonValue.Create(string.Empty),
		["source"] = JsonValue.Create(string.Empty),
		["icon"] = JsonValue.Create(string.Empty),
		["eventType"] = JsonValue.Create(0),
		["rating"] = JsonValue.Create(Engine_UiMenuConstants.EmptyRatingSlide),
		["hostOnly"] = JsonValue.Create(false),
		["drop"] = JsonValue.Create(false),
		["counted"] = JsonValue.Create(true),
	};

	/// <summary>
	/// Sets one field of a row as an editor writes it: a value at the field's default is left out,
	/// as the snapshot leaves it out, so an untouched row and a row set back to its default compare
	/// equal. Fields the editor does not set are not touched.
	/// </summary>
	public static void SetField(JsonObject row, string key, JsonNode? value)
	{
		if (value is null || (Defaults.TryGetValue(key, out JsonNode? fallback) && Same(value, fallback)))
		{
			row.Remove(key);
			return;
		}

		row[key] = value;
	}

	/// <summary>The row without its children and without any placing key — what a replacement compares.</summary>
	public static JsonObject OwnFields(JsonObject entry)
	{
		var own = new JsonObject();
		foreach ((string key, JsonNode? value) in entry)
		{
			if (key != ChildrenKey && Array.IndexOf(PatchKeys, key) < 0)
			{
				own[key] = value?.DeepClone();
			}
		}

		return own;
	}

	// ---- the row tree -----------------------------------------------------------------------
	//
	// A row is addressed by its PATH: the index in the entries, then in each level's children.
	// Every operation returns the moved row's new path (null when it cannot apply), so an editor
	// re-renders the record and keeps the row selected. A children array a move empties is dropped,
	// as the snapshot writes none.

	/// <summary>The row at <paramref name="path"/>, or null.</summary>
	public static JsonObject? RowAt(JsonArray entries, IReadOnlyList<int> path) =>
		Locate(entries, path) is { } at ? at.List[at.Index] as JsonObject : null;

	/// <summary>The list holding the row at <paramref name="path"/> and its index there.</summary>
	private static (JsonArray List, int Index)? Locate(JsonArray entries, IReadOnlyList<int> path)
	{
		if (path.Count == 0)
		{
			return null;
		}

		JsonArray list = entries;
		for (int depth = 0; depth < path.Count - 1; depth++)
		{
			if (path[depth] < 0 || path[depth] >= list.Count || list[path[depth]] is not JsonObject row ||
				row[ChildrenKey] is not JsonArray children)
			{
				return null;
			}

			list = children;
		}

		int index = path[^1];
		return index >= 0 && index < list.Count ? (list, index) : null;
	}

	/// <summary>The row's parent row, or null at the top level.</summary>
	private static JsonObject? ParentOf(JsonArray entries, IReadOnlyList<int> path) =>
		path.Count < 2 ? null : RowAt(entries, path.Take(path.Count - 1).ToArray());

	/// <summary>Swaps a row with its neighbour among its siblings (<paramref name="direction"/> −1 up, +1 down).</summary>
	public static int[]? Move(JsonArray entries, IReadOnlyList<int> path, int direction)
	{
		if (Locate(entries, path) is not { } at)
		{
			return null;
		}

		int to = at.Index + Math.Sign(direction);
		if (direction == 0 || to < 0 || to >= at.List.Count)
		{
			return null;
		}

		JsonNode? row = at.List[at.Index];
		at.List.RemoveAt(at.Index);
		at.List.Insert(to, row);
		int[] moved = path.ToArray();
		moved[^1] = to;
		return moved;
	}

	/// <summary>Makes a row the last child of the sibling above it.</summary>
	public static int[]? Nest(JsonArray entries, IReadOnlyList<int> path)
	{
		if (Locate(entries, path) is not { } at || at.Index == 0 || at.List[at.Index - 1] is not JsonObject above)
		{
			return null;
		}

		JsonNode? row = at.List[at.Index];
		at.List.RemoveAt(at.Index);
		if (above[ChildrenKey] is not JsonArray children)
		{
			children = new JsonArray();
			above[ChildrenKey] = children;
		}

		children.Add(row);
		int[] nested = path.ToArray();
		nested[^1] = at.Index - 1;
		return nested.Append(children.Count - 1).ToArray();
	}

	/// <summary>Lifts a row out of its parent to the parent's level, directly after the parent.</summary>
	public static int[]? Promote(JsonArray entries, IReadOnlyList<int> path)
	{
		if (path.Count < 2 || Locate(entries, path) is not { } at ||
			ParentOf(entries, path) is not { } parent ||
			Locate(entries, path.Take(path.Count - 1).ToArray()) is not { } parentAt)
		{
			return null;
		}

		JsonNode? row = at.List[at.Index];
		at.List.RemoveAt(at.Index);
		if (at.List.Count == 0)
		{
			parent.Remove(ChildrenKey);
		}

		parentAt.List.Insert(parentAt.Index + 1, row);
		int[] lifted = path.Take(path.Count - 1).ToArray();
		lifted[^1] = parentAt.Index + 1;
		return lifted;
	}

	/// <summary>
	/// Adds <paramref name="row"/>: as the last child of the row at <paramref name="path"/> when
	/// <paramref name="child"/>, else after it among its siblings; at the end of the entries when
	/// <paramref name="path"/> is null or names no row.
	/// </summary>
	public static int[] Add(JsonArray entries, IReadOnlyList<int>? path, JsonObject row, bool child)
	{
		if (path is null || Locate(entries, path) is not { } at)
		{
			entries.Add(row);
			return new[] { entries.Count - 1 };
		}

		if (child && at.List[at.Index] is JsonObject parent)
		{
			if (parent[ChildrenKey] is not JsonArray children)
			{
				children = new JsonArray();
				parent[ChildrenKey] = children;
			}

			children.Add(row);
			return path.Append(children.Count - 1).ToArray();
		}

		at.List.Insert(at.Index + 1, row);
		int[] added = path.ToArray();
		added[^1] = at.Index + 1;
		return added;
	}

	/// <summary>Removes the row at <paramref name="path"/> and its subtree; false when there is none.</summary>
	public static bool Remove(JsonArray entries, IReadOnlyList<int> path)
	{
		if (Locate(entries, path) is not { } at)
		{
			return false;
		}

		at.List.RemoveAt(at.Index);
		if (at.List.Count == 0 && ParentOf(entries, path) is { } parent)
		{
			parent.Remove(ChildrenKey);
		}

		return true;
	}

	/// <summary>Whether another row anywhere in the record already has <paramref name="name"/>.</summary>
	public static bool NameTaken(JsonArray entries, string name, JsonObject? except = null)
	{
		foreach (JsonNode? node in entries)
		{
			if (node is JsonObject row && !ReferenceEquals(row, except) &&
				(Name(row) == name || NameTaken(Children(row), name, except)))
			{
				return true;
			}
		}

		return false;
	}

	// ---- whole records ---------------------------------------------------------------------

	/// <summary>The names whose record <paramref name="edited"/> states differently from <paramref name="loaded"/> (or new).</summary>
	public static List<string> Changed(
		IReadOnlyDictionary<string, JsonObject> loaded, IReadOnlyDictionary<string, JsonObject> edited)
	{
		var changed = new List<string>();
		foreach ((string name, JsonObject record) in edited)
		{
			if (!loaded.TryGetValue(name, out JsonObject? before) || !Same(before, record))
			{
				changed.Add(name);
			}
		}

		changed.Sort(StringComparer.Ordinal);
		return changed;
	}

	/// <summary>
	/// A templates file with <paramref name="menus"/> written into its <c>menus</c> section, whole,
	/// each replacing the record of its name; every other menu and every other section (screens,
	/// modals, anything else) is kept as it was.
	/// </summary>
	public static string ApplyMenus(string? existing, IReadOnlyDictionary<string, JsonObject> menus)
	{
		JsonObject root = Engine_HudLayoutEdit.ParseObject(existing);
		if (root["menus"] is not JsonObject section)
		{
			section = new JsonObject();
			root["menus"] = section;
		}

		foreach ((string name, JsonObject record) in menus)
		{
			section[name] = record.DeepClone();
		}

		return root.ToJsonString(Engine_HudLayoutEdit.Indented) + "\n";
	}

	// ---- patches ----------------------------------------------------------------------------

	/// <summary>
	/// The patch record that turns <paramref name="baseMenu"/> into <paramref name="edited"/> under
	/// the engine's composition (<c>"patch": true</c>, the menu-level fields that differ, and the
	/// entry records in the order they must apply), or null when the two are the same menu.
	/// </summary>
	public static JsonObject? Patch(JsonObject baseMenu, JsonObject edited)
	{
		var record = new JsonObject { ["patch"] = true };
		foreach (string field in MenuFields)
		{
			if (Text(baseMenu, field) != Text(edited, field))
			{
				record[field] = Text(edited, field);
			}
		}

		JsonArray entries = Diff(Entries(baseMenu), Entries(edited));
		if (entries.Count == 0 && record.Count == 1)
		{
			return null;
		}

		record[EntriesKey] = entries;
		return record;
	}

	private sealed record Placed(string Parent, JsonObject Entry);

	/// <summary>
	/// The entry records that turn <paramref name="baseEntries"/> into <paramref name="edited"/>:
	/// first a <c>remove</c> for every base row that is gone or must move (a row's subtree goes
	/// with it), then, walking the edited tree, a replacement for a row that stays but changed
	/// (its whole subtree, in place) and an insert for a new or moved row (its whole subtree, placed
	/// <c>after</c> its previous sibling or <c>before</c> its next standing one). A row stays when
	/// it keeps its parent and its order among the siblings that also stay (the longest such run).
	/// </summary>
	public static JsonArray Diff(JsonArray baseEntries, JsonArray edited)
	{
		var before = new Dictionary<string, Placed>(StringComparer.Ordinal);
		Index(baseEntries, string.Empty, before);

		var stays = new HashSet<string>(StringComparer.Ordinal);
		Stays(edited, string.Empty, baseEntries, before, stays);

		var ops = new JsonArray();
		Removals(baseEntries, string.Empty, stays, ops);
		Walk(edited, string.Empty, before, stays, ops);
		return ops;
	}

	/// <summary>Every named entry by name, first by depth-first order (the engine's FindEntry).</summary>
	private static void Index(JsonArray list, string parent, Dictionary<string, Placed> into)
	{
		foreach (JsonNode? node in list)
		{
			if (node is JsonObject entry && Name(entry) is { Length: > 0 } name)
			{
				into.TryAdd(name, new Placed(parent, entry));
				Index(Children(entry), name, into);
			}
		}
	}

	private static void Stays(
		JsonArray list, string parent, JsonArray baseList, Dictionary<string, Placed> before, HashSet<string> stays)
	{
		// The base order of the edited rows that kept this parent, in edited order.
		List<string> baseOrder = NamesOf(BaseSiblings(parent, baseList, before));
		var names = new List<string>();
		var positions = new List<int>();
		foreach (JsonNode? node in list)
		{
			if (node is JsonObject entry && Name(entry) is { Length: > 0 } name &&
				before.TryGetValue(name, out Placed? placed) && placed.Parent == parent)
			{
				names.Add(name);
				positions.Add(baseOrder.IndexOf(name));
			}
		}

		foreach (int kept in LongestIncreasing(positions))
		{
			stays.Add(names[kept]);
		}

		foreach (JsonNode? node in list)
		{
			if (node is JsonObject entry && Name(entry) is { Length: > 0 } name)
			{
				Stays(Children(entry), name, baseList, before, stays);
			}
		}
	}

	private static JsonArray BaseSiblings(string parent, JsonArray baseList, Dictionary<string, Placed> before) =>
		parent.Length == 0 ? baseList :
		before.TryGetValue(parent, out Placed? placed) ? Children(placed.Entry) :
		new JsonArray();

	private static List<string> NamesOf(JsonArray list) =>
		list.OfType<JsonObject>().Select(Name).ToList();

	/// <summary>The indices of a longest strictly increasing subsequence of <paramref name="values"/>.</summary>
	private static List<int> LongestIncreasing(List<int> values)
	{
		int n = values.Count;
		var length = new int[n];
		var previous = new int[n];
		int best = -1;
		for (int i = 0; i < n; i++)
		{
			length[i] = 1;
			previous[i] = -1;
			for (int j = 0; j < i; j++)
			{
				if (values[j] < values[i] && length[j] + 1 > length[i])
				{
					length[i] = length[j] + 1;
					previous[i] = j;
				}
			}

			if (best < 0 || length[i] > length[best])
			{
				best = i;
			}
		}

		var kept = new List<int>();
		for (int i = best; i >= 0; i = previous[i])
		{
			kept.Add(i);
		}

		kept.Reverse();
		return kept;
	}

	private static void Removals(JsonArray list, string parent, HashSet<string> stays, JsonArray ops)
	{
		foreach (JsonNode? node in list)
		{
			if (node is not JsonObject entry || Name(entry) is not { Length: > 0 } name)
			{
				continue;
			}

			if (!stays.Contains(name))
			{
				var remove = new JsonObject { [NameKey] = name, ["remove"] = true };
				if (parent.Length != 0)
				{
					remove["parent"] = parent;
				}

				ops.Add(remove);
				continue;
			}

			Removals(Children(entry), name, stays, ops);
		}
	}

	private static void Walk(
		JsonArray list, string parent, Dictionary<string, Placed> before, HashSet<string> stays, JsonArray ops)
	{
		for (int i = 0; i < list.Count; i++)
		{
			if (list[i] is not JsonObject entry || Name(entry) is not { Length: > 0 } name)
			{
				continue;
			}

			if (stays.Contains(name))
			{
				if (!Same(OwnFields(before[name].Entry), OwnFields(entry)))
				{
					ops.Add(Whole(entry, parent));
				}
				else
				{
					Walk(Children(entry), name, before, stays, ops);
				}

				continue;
			}

			JsonObject insert = Whole(entry, parent);
			if (PreviousName(list, i) is { } after)
			{
				insert["after"] = after;
			}
			else if (NextStanding(list, i, stays) is { } next)
			{
				insert["before"] = next;
			}

			ops.Add(insert);
		}
	}

	/// <summary>The row with its whole subtree, stated under <paramref name="parent"/>.</summary>
	private static JsonObject Whole(JsonObject entry, string parent)
	{
		JsonObject whole = StripPlacing(entry);
		if (parent.Length != 0)
		{
			whole["parent"] = parent;
		}

		return whole;
	}

	/// <summary>The row and its subtree with every placing key taken out.</summary>
	private static JsonObject StripPlacing(JsonObject entry)
	{
		JsonObject own = OwnFields(entry);
		if (entry[ChildrenKey] is JsonArray children)
		{
			var copy = new JsonArray();
			foreach (JsonNode? child in children)
			{
				copy.Add(child is JsonObject row ? StripPlacing(row) : child?.DeepClone());
			}

			own[ChildrenKey] = copy;
		}

		return own;
	}

	private static string? PreviousName(JsonArray list, int at)
	{
		for (int i = at - 1; i >= 0; i--)
		{
			if (list[i] is JsonObject entry && Name(entry) is { Length: > 0 } name)
			{
				return name;
			}
		}

		return null;
	}

	private static string? NextStanding(JsonArray list, int at, HashSet<string> stays)
	{
		for (int i = at + 1; i < list.Count; i++)
		{
			if (list[i] is JsonObject entry && Name(entry) is { Length: > 0 } name && stays.Contains(name))
			{
				return name;
			}
		}

		return null;
	}

	// ---- row commands ----------------------------------------------------------------------

	public enum CommandKind
	{
		None,
		Opens,
		Lua,
		Other,
	}

	/// <summary>Which of the mod row commands a command is (<see cref="Engine_UiModMenuCommand"/>), or Other.</summary>
	public static CommandKind KindOf(string? command)
	{
		if (string.IsNullOrEmpty(command))
		{
			return CommandKind.None;
		}

		if (command.StartsWith(Engine_UiModMenuCommand.OpensPrefix, StringComparison.Ordinal))
		{
			return CommandKind.Opens;
		}

		return command.StartsWith(Engine_UiModMenuCommand.LuaPrefix, StringComparison.Ordinal)
			? CommandKind.Lua
			: CommandKind.Other;
	}

	/// <summary>
	/// Why a row command will not run, or empty when it is well formed: an <c>Opens:</c> naming no
	/// menu, or a <c>Mod:lua:</c> whose function is not a Lua name path. Any other command is the
	/// list's own (a host action or a guest row) and is not checked here.
	/// </summary>
	public static string Check(string? command)
	{
		switch (KindOf(command))
		{
			case CommandKind.Opens:
				return Engine_UiModMenuCommand.Parse(command).Verb == Engine_UiModMenuCommand.Verb.Opens
					? string.Empty
					: "Opens: names no menu";

			case CommandKind.Lua:
				if (Engine_UiModMenuCommand.Parse(command).Verb == Engine_UiModMenuCommand.Verb.Lua)
				{
					return string.Empty;
				}

				string body = command![Engine_UiModMenuCommand.LuaPrefix.Length..];
				int colon = body.IndexOf(':');
				string function = (colon < 0 ? body : body[..colon]).Trim();
				return function.Length == 0
					? "Mod:lua: names no function"
					: $"'{function}' is not a Lua function name — identifiers joined by dots (my_mod.on_row)";

			default:
				return string.Empty;
		}
	}

	// ---- composing with an edited file -----------------------------------------------------

	/// <summary>
	/// The enabled mods' files (lowest priority first) with <paramref name="mod"/>'s replaced by
	/// <paramref name="text"/> — appended as the highest when the mod is not among them, and left
	/// out when <paramref name="text"/> is null or empty. What the Studio previews and composes
	/// against: the mod as it is being edited, whether or not it is enabled or saved.
	/// </summary>
	public static List<(string Mod, string Text)> Substitute(
		IEnumerable<(string Mod, string Text)> enabled, string mod, string? text)
	{
		var files = new List<(string Mod, string Text)>();
		bool placed = false;
		foreach ((string name, string body) in enabled)
		{
			if (string.Equals(name, mod, StringComparison.OrdinalIgnoreCase))
			{
				if (!string.IsNullOrEmpty(text))
				{
					files.Add((name, text));
				}

				placed = true;
				continue;
			}

			files.Add((name, body));
		}

		if (!placed && !string.IsNullOrEmpty(text) && mod.Length != 0)
		{
			files.Add((mod, text));
		}

		return files;
	}
}
