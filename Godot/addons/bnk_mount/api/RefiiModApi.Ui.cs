#if TOOLS
using System;
using System.Linq;
using Godot;
using Refii.Mods;
using Refii.Ui;

namespace Refii.EditorTools;

/// <summary>
/// The UI surfaces a mod authors, for the Studio's UI tab and for scripts: the HUD's <c>layout</c>
/// block and <c>text</c> pieces (<c>ui/hud.json</c>, read by <see cref="Engine_HudModLayout"/> and
/// <see cref="Engine_HudModPieces"/>), modal answer records and a mod's own menus with their
/// <c>Opens:</c> / <c>Mod:lua:</c> rows (<c>ui/templates.json</c>, read by
/// <see cref="Engine_UiTemplates"/>, <see cref="Engine_UiModals"/> and <see cref="Engine_UiModMenus"/>),
/// and <c>choice</c> rows on the mod's settings page (<c>settings.ui.json</c>, read by
/// <see cref="ModSettingsUi"/>). Every write goes through the package writer and the schema check
/// the rest of the API uses; each returns an error string, empty when written.
/// </summary>
public partial class RefiiModApi
{
	// ---- one re-mount for many writes ------------------------------------------------------

	private int _changesOpen;
	private bool _changesPending;

	/// <summary>
	/// Holds the re-mount and the Studio's refresh until <see cref="EndChanges"/>: every write
	/// between the two still lands and is still checked, but the mods are re-mounted once — what
	/// an editor saving many entries at once wants. Calls nest.
	/// </summary>
	public void BeginChanges() => _changesOpen++;

	/// <summary>Closes a <see cref="BeginChanges"/>; the outermost re-mounts once if anything was written.</summary>
	public void EndChanges()
	{
		if (_changesOpen == 0 || --_changesOpen != 0 || !_changesPending)
		{
			return;
		}

		_changesPending = false;
		Changed();
	}

	// ---- ui/hud.json: layout ---------------------------------------------------------------

	/// <summary>
	/// Moves one of the game's OWN HUD pieces (an id from <see cref="ListShippedHudPieces"/>): the
	/// entry shape of <c>user://hud/layout.json</c>, so the player's file still wins over it.
	/// </summary>
	public string SetHudLayout(string pieceId, Vector2 anchor, Vector2 offset)
	{
		if (pieceId.Length == 0)
		{
			return "the layout entry needs a piece id";
		}

		Godot.Collections.Dictionary hud = ReadJson(Engine_HudModPieces.ModHudPath);
		Section(hud, Engine_HudModLayout.Section)[pieceId] = new Godot.Collections.Dictionary
		{
			["anchor"] = new Godot.Collections.Array { anchor.X, anchor.Y },
			["offset"] = new Godot.Collections.Array { offset.X, offset.Y },
		};
		return WriteJson(Engine_HudModPieces.ModHudPath, hud);
	}

	/// <summary>Drops the mod's move of <paramref name="pieceId"/>; the piece goes back to the shipped place.</summary>
	public string RemoveHudLayout(string pieceId)
	{
		Godot.Collections.Dictionary hud = ReadJson(Engine_HudModPieces.ModHudPath);
		Section(hud, Engine_HudModLayout.Section).Remove(pieceId);
		return WriteJson(Engine_HudModPieces.ModHudPath, hud);
	}

	/// <summary>The mod's <c>layout</c> block: piece id → <c>{ "anchor": [x, y], "offset": [x, y] }</c>.</summary>
	public Godot.Collections.Dictionary GetHudLayout() =>
		Section(ReadJson(Engine_HudModPieces.ModHudPath), Engine_HudModLayout.Section);

	/// <summary>The active mod's whole <c>ui/hud.json</c> (empty when it ships none).</summary>
	public Godot.Collections.Dictionary GetHud() => ReadJson(Engine_HudModPieces.ModHudPath);

	// ---- ui/hud.json: text pieces ----------------------------------------------------------

	/// <summary>
	/// A <c>text</c> HUD piece, keyed by <c>id</c>: <c>text</c> (a literal, a Babel tag or a value
	/// source — <c>mod:</c>, <c>game:</c>, <c>player:</c>, see <see cref="ListHudValueSources"/>), <c>format</c>, <c>size</c> (point size), <c>colour</c>, <c>align</c>
	/// and the placement every piece takes (<c>anchor</c>, <c>offset</c>, or <c>follow</c>). The
	/// kind is set to <c>text</c>.
	/// </summary>
	public string SetHudText(Godot.Collections.Dictionary piece)
	{
		var record = piece.Duplicate(true);
		record["kind"] = "text";
		return SetHudPiece(record);
	}

	// ---- ui/hud.json: the widgets drawn by their own readers, and value sources ------------

	/// <summary>
	/// The HUD's other widgets, addressable exactly as a cut piece is — <see cref="HideHudPiece"/>,
	/// <see cref="SetHudScale"/>, <see cref="SetHudLayout"/> (moves the widget's home point) and
	/// <see cref="SetHudWidgetCopy"/> — by id → what it draws: the world icons and sprites, the aim
	/// reticule, time control's dial (each with a player-two copy, <c>…2</c>) and the achievement
	/// toast (<see cref="Engine_HudWidgets"/>). The timing meter is the cut piece
	/// <c>group.ExpressionMeter.ExpressionMeter</c>.
	/// </summary>
	public Godot.Collections.Dictionary ListHudWidgets()
	{
		var widgets = new Godot.Collections.Dictionary();
		foreach (Engine_HudWidgets.Widget widget in Engine_HudWidgets.All)
		{
			widgets[widget.Id] = widget.Summary;
		}

		return widgets;
	}

	/// <summary>
	/// A copy of a widget (<paramref name="of"/>, an id from <see cref="ListHudWidgets"/>) drawn
	/// <paramref name="offset"/> design units from it (+Y down): it draws exactly what the original
	/// draws, every frame. Its own <see cref="SetHudScale"/> sizes it.
	/// </summary>
	public string SetHudWidgetCopy(string id, string of, Vector2 offset) =>
		Engine_HudWidgets.Find(of) is null
			? $"'{of}' is not a widget; a copy of a cut piece is SetHudClone"
			: SetHudClone(new Godot.Collections.Dictionary
			{
				["id"] = id,
				["of"] = of,
				["offset"] = new Godot.Collections.Array { offset.X, offset.Y },
			});

	/// <summary>
	/// The value sources a HUD piece (and a copy's <c>values</c>) reads, by prefix → meaning
	/// (<see cref="Engine_UiValueSources"/>): <c>mod:</c>, <c>game:</c>, <c>player:</c>, <c>hero:</c>.
	/// </summary>
	public Godot.Collections.Dictionary ListHudValueSources() => new()
	{
		[Engine_UiValueSources.ModPrefix] = "mod:<mod>.<name> — what a mod's code published (publish, Refii.publish)",
		[Engine_UiValueSources.GamePrefix] = "game:<name> — what the guest published for its own HUD under a hud.bgf binding name: a meter 0..100, or a text field read as text (game:QuestTitle)",
		[Engine_UiValueSources.PlayerPrefix] = "player:<key> — the per-player store (Refii.player_set), read for the piece's player: player two's view and player two's widget read player two's",
		[Engine_UiValueSources.HeroPrefix] = "hero:health / hero:health_max — the local hero's vitals",
	};

	/// <summary>Why <paramref name="source"/> is not a number a HUD piece can read, or "" when it is.</summary>
	public string CheckHudSource(string source) => Engine_UiValueSources.NumberProblem(source) ?? string.Empty;

	/// <summary>Drops the mod's size factor for <paramref name="id"/>; the piece draws at its own size.</summary>
	public string RemoveHudScale(string id)
	{
		Godot.Collections.Dictionary hud = ReadJson(Engine_HudModPieces.ModHudPath);
		Section(hud, "scale").Remove(id);
		return WriteJson(Engine_HudModPieces.ModHudPath, hud);
	}

	// ---- ui/templates.json: modals ---------------------------------------------------------

	/// <summary>
	/// A modal answer record, by key (<c>"13"</c>, or <c>"0@22"</c> for one screen):
	/// <c>{ "kind": "query" | "message", "confirm": code, "cancel": code }</c>. Replaces the
	/// shipped row whole.
	/// </summary>
	public string SetModal(string key, Godot.Collections.Dictionary record)
	{
		if (!Engine_UiModals.TryParseKey(key, out _, out _))
		{
			return $"'{key}' is not a modal key: \"<modal id>\" or \"<modal id>@<screen id>\"";
		}

		Godot.Collections.Dictionary templates = ReadJson(Engine_UiTemplates.ModTemplatesPath);
		Section(templates, Engine_UiModals.Section)[key] = record;
		return WriteJson(Engine_UiTemplates.ModTemplatesPath, templates);
	}

	/// <summary>States that a box is NOT answered by the host (a <c>null</c> record): it keeps the guest's own input.</summary>
	public string ReleaseModal(string key)
	{
		if (!Engine_UiModals.TryParseKey(key, out _, out _))
		{
			return $"'{key}' is not a modal key";
		}

		Godot.Collections.Dictionary templates = ReadJson(Engine_UiTemplates.ModTemplatesPath);
		Section(templates, Engine_UiModals.Section)[key] = default(Variant);
		return WriteJson(Engine_UiTemplates.ModTemplatesPath, templates);
	}

	/// <summary>Drops the mod's statement about a box; the shipped row (or none) stands.</summary>
	public string RemoveModal(string key)
	{
		Godot.Collections.Dictionary templates = ReadJson(Engine_UiTemplates.ModTemplatesPath);
		Section(templates, Engine_UiModals.Section).Remove(key);
		return WriteJson(Engine_UiTemplates.ModTemplatesPath, templates);
	}

	/// <summary>The mod's <c>modals</c> block.</summary>
	public Godot.Collections.Dictionary GetModals() =>
		Section(ReadJson(Engine_UiTemplates.ModTemplatesPath), Engine_UiModals.Section);

	/// <summary>Every modal row as the last run composed it (shipped, mods, the player's), by key.</summary>
	public string[] ListModals() => SnapshotKeys(Engine_UiTemplates.SnapshotPath, Engine_UiModals.Section);

	/// <summary>The shipped modal rows, by key, as <c>{ kind, confirm, cancel }</c> — the starting point to override.</summary>
	public Godot.Collections.Dictionary ShippedModals()
	{
		var rows = new Godot.Collections.Dictionary();
		foreach (Engine_UiModalTemplate modal in Engine_UiModals.Shipped())
		{
			var record = new Godot.Collections.Dictionary
			{
				["kind"] = modal.Dismissal ? "message" : "query",
				["confirm"] = (long)modal.Confirm,
			};
			if (modal.Cancel is { } cancel)
			{
				record["cancel"] = (long)cancel;
			}

			rows[Engine_UiModals.KeyOf(modal)] = record;
		}

		return rows;
	}

	// ---- ui/templates.json: a mod's own menus ----------------------------------------------

	/// <summary>
	/// A whole menu of the mod's own, by name: <c>title</c>, <c>highlight</c>, <c>entries</c> (each
	/// <c>name</c>, <c>tag</c>, <c>command</c>, <c>opens</c>, <c>children</c> …). It opens from a row
	/// whose <c>opens</c> names it or whose command is <see cref="OpensCommand"/>.
	/// </summary>
	public string SetMenu(string name, Godot.Collections.Dictionary record)
	{
		if (name.Length == 0)
		{
			return "the menu needs a name";
		}

		Godot.Collections.Dictionary templates = ReadJson(Engine_UiTemplates.ModTemplatesPath);
		var menu = record.Duplicate(true);
		menu.Remove("patch");
		Section(templates, "menus")[name] = menu;
		return WriteJson(Engine_UiTemplates.ModTemplatesPath, templates);
	}

	/// <summary>Removes a menu (whole or patch) the mod states.</summary>
	public string RemoveMenu(string name)
	{
		Godot.Collections.Dictionary templates = ReadJson(Engine_UiTemplates.ModTemplatesPath);
		Section(templates, "menus").Remove(name);
		return WriteJson(Engine_UiTemplates.ModTemplatesPath, templates);
	}

	/// <summary>
	/// The mod's whole patch of a menu it does not own: <paramref name="record"/>'s <c>entries</c>
	/// replace the mod's patch entries for <paramref name="menu"/> exactly, in order — a name may
	/// appear twice, a <c>remove</c> then its re-insert, which is how a patch moves a row — and its
	/// menu-level fields (highlight, title, source, rowCommand) are stated with them. Unlike
	/// <see cref="PatchMenu"/>, which merges entries by name into what the mod already states.
	/// </summary>
	public string SetMenuPatch(string menu, Godot.Collections.Dictionary record)
	{
		if (menu.Length == 0)
		{
			return "the patch needs a menu name";
		}

		Godot.Collections.Dictionary templates = ReadJson(Engine_UiTemplates.ModTemplatesPath);
		var patch = record.Duplicate(true);
		patch["patch"] = true;
		Section(templates, "menus")[menu] = patch;
		return WriteJson(Engine_UiTemplates.ModTemplatesPath, templates);
	}

	/// <summary>
	/// The mod's PATCH of a screen: only the fields <paramref name="fields"/> states change — a list
	/// replaces the list, <c>fields</c> / <c>switches</c> / <c>boxPaths</c> / <c>menuScriptByMode</c>
	/// merge per key (a null value removes that key). Merged into the patch the mod already states.
	/// </summary>
	public string SetScreenPatch(int screenId, Godot.Collections.Dictionary fields)
	{
		Godot.Collections.Dictionary templates = ReadJson(Engine_UiTemplates.ModTemplatesPath);
		Godot.Collections.Dictionary screens = Section(templates, "screens");
		string key = screenId.ToString(System.Globalization.CultureInfo.InvariantCulture);
		Godot.Collections.Dictionary patch = screens.TryGetValue(key, out Variant standing) &&
			standing.VariantType == Variant.Type.Dictionary && standing.AsGodotDictionary().TryGetValue("patch", out Variant flag) &&
			flag.AsBool()
				? standing.AsGodotDictionary()
				: new Godot.Collections.Dictionary();
		foreach ((Variant name, Variant value) in fields)
		{
			patch[name] = value;
		}

		patch["patch"] = true;
		screens[key] = patch;
		return WriteJson(Engine_UiTemplates.ModTemplatesPath, templates);
	}

	/// <summary>The row command that opens a host screen: <c>Screen:open:&lt;id&gt;</c>.</summary>
	public string OpenScreenCommand(int screenId) => Engine_UiHostScreens.OpenCommand((uint)Math.Max(0, screenId));

	/// <summary>
	/// The row verbs every list runs (<c>Refii.Ui.Engine_UiVerbs</c>): <c>{ usage: { lists, meaning } }</c>,
	/// <c>lists</c> empty for a verb that runs on every list.
	/// </summary>
	public Godot.Collections.Dictionary ListRowVerbs()
	{
		var verbs = new Godot.Collections.Dictionary();
		foreach (Engine_UiVerb verb in Engine_UiVerbs.Game)
		{
			verbs[verb.Usage] = new Godot.Collections.Dictionary
			{
				["lists"] = new Godot.Collections.Array<string>(verb.Lists.Select(Engine_UiCommandSet.ListName)),
				["meaning"] = verb.Description,
			};
		}

		return verbs;
	}

	/// <summary>The row conditions an entry's <c>when</c> can name.</summary>
	public string[] ListRowConditions() => Engine_UiRowConditions.Game.ToArray();

	/// <summary>The page providers a screen record can name; <c>data</c> fills the record's <c>fields</c> and <c>switches</c>.</summary>
	public string[] ListPageProviders() => Engine_UiPageProviders.ShippedNames.ToArray();

	/// <summary>
	/// A row source over published values: <c>values:&lt;binding&gt;=&lt;label&gt;,...</c>, each binding
	/// <c>mod:&lt;key&gt;</c> or <c>player:&lt;key&gt;</c> with an optional <c>|format</c>.
	/// </summary>
	public string ValuesSource(Godot.Collections.Dictionary labelsByBinding) =>
		Engine_UiModRows.ValuesSource + ":" + string.Join(",",
			labelsByBinding.Select(pair => pair.Key.AsString() + "=" + pair.Value.AsString()));

	/// <summary>Why a page or row binding cannot be read, or empty when it can.</summary>
	public string CheckBinding(string binding) => Engine_UiValueSource.Problem(binding) ?? string.Empty;

	/// <summary>Drops the mod's record for a screen; the shipped one (or another mod's) stands.</summary>
	public string RemoveScreen(int screenId)
	{
		Godot.Collections.Dictionary templates = ReadJson(Engine_UiTemplates.ModTemplatesPath);
		Section(templates, "screens").Remove(screenId.ToString(System.Globalization.CultureInfo.InvariantCulture));
		return WriteJson(Engine_UiTemplates.ModTemplatesPath, templates);
	}

	/// <summary>The active mod's whole <c>ui/templates.json</c> (empty when it ships none).</summary>
	public Godot.Collections.Dictionary GetTemplates() => ReadJson(Engine_UiTemplates.ModTemplatesPath);

	/// <summary>The mod's record for <paramref name="name"/> (empty when it states none).</summary>
	public Godot.Collections.Dictionary GetMenu(string name) =>
		Section(ReadJson(Engine_UiTemplates.ModTemplatesPath), "menus").TryGetValue(name, out Variant menu) &&
		menu.VariantType == Variant.Type.Dictionary
			? menu.AsGodotDictionary()
			: new Godot.Collections.Dictionary();

	/// <summary>The menus the mod states, whole or patched.</summary>
	public string[] ListModMenus() =>
		Section(ReadJson(Engine_UiTemplates.ModTemplatesPath), "menus").Keys.Select(k => k.AsString()).ToArray();

	/// <summary>
	/// One row of a menu the mod states, replaced by <c>name</c> or added at the end. In a patch
	/// menu the row composes with other mods' patches, as <see cref="PatchMenu"/> does.
	/// </summary>
	public string SetMenuRow(string menu, Godot.Collections.Dictionary entry)
	{
		string row = entry.TryGetValue("name", out Variant n) ? n.AsString() : string.Empty;
		if (row.Length == 0)
		{
			return "the row needs \"name\"";
		}

		Godot.Collections.Dictionary templates = ReadJson(Engine_UiTemplates.ModTemplatesPath);
		Godot.Collections.Array entries = List(Section(Section(templates, "menus"), menu), "entries");
		int at = -1;
		for (int i = 0; i < entries.Count; i++)
		{
			if (entries[i].VariantType == Variant.Type.Dictionary &&
				entries[i].AsGodotDictionary().TryGetValue("name", out Variant existing) && existing.AsString() == row)
			{
				at = i;
				break;
			}
		}

		if (at >= 0)
		{
			entries[at] = entry;
		}
		else
		{
			entries.Add(entry);
		}

		return WriteJson(Engine_UiTemplates.ModTemplatesPath, templates);
	}

	/// <summary>Removes a row from a menu the mod states.</summary>
	public string RemoveMenuRow(string menu, string row)
	{
		Godot.Collections.Dictionary templates = ReadJson(Engine_UiTemplates.ModTemplatesPath);
		RemoveByKey(List(Section(Section(templates, "menus"), menu), "entries"), "name", row);
		return WriteJson(Engine_UiTemplates.ModTemplatesPath, templates);
	}

	/// <summary>The row command that opens a mod menu: <c>Opens:&lt;menu&gt;</c>.</summary>
	public string OpensCommand(string menu) => Engine_UiModMenuCommand.OpensPrefix + menu;

	/// <summary>
	/// The row command that calls a global Lua function of the mod's: <c>Mod:lua:&lt;function&gt;</c>,
	/// with <c>:&lt;arg&gt;</c> when <paramref name="argument"/> is not empty. The function is called
	/// as <c>function(arg, e)</c>, <c>e</c> the <c>mod_menu_row</c> event's table.
	/// </summary>
	public string LuaCommand(string function, string argument) =>
		Engine_UiModMenuCommand.LuaPrefix + function + (argument.Length == 0 ? string.Empty : ":" + argument);

	/// <summary>
	/// What a row command means: <c>{ "verb": "opens" | "lua" | "", "menu", "function", "argument" }</c>
	/// (argument null when the command states none).
	/// </summary>
	public Godot.Collections.Dictionary ParseMenuCommand(string command)
	{
		Engine_UiModMenuCommand.Parsed parsed = Engine_UiModMenuCommand.Parse(command);
		return new Godot.Collections.Dictionary
		{
			["verb"] = parsed.Verb switch
			{
				Engine_UiModMenuCommand.Verb.Opens => "opens",
				Engine_UiModMenuCommand.Verb.Lua => "lua",
				_ => string.Empty,
			},
			["menu"] = parsed.Menu,
			["function"] = parsed.Function,
			["argument"] = parsed.Argument is null ? new Variant() : Variant.From(parsed.Argument),
		};
	}

	// ---- settings.ui.json: rows, choice rows -----------------------------------------------

	/// <summary>
	/// One settings-page row, keyed by the setting's name without the <c>&lt;mod&gt;.</c> prefix:
	/// <c>label</c>, <c>description</c>, <c>type</c> (slider, toggle or choice), <c>min</c>,
	/// <c>max</c>, <c>step</c>, <c>options</c>.
	/// </summary>
	public string SetSettingRow(string setting, Godot.Collections.Dictionary row)
	{
		if (setting.Length == 0)
		{
			return "the row needs a setting name";
		}

		Godot.Collections.Dictionary page = ReadJson(ModSettingsUi.ModSettingsUiPath);
		page[setting] = row;
		return WriteJson(ModSettingsUi.ModSettingsUiPath, page);
	}

	/// <summary>
	/// A <c>choice</c> row: <paramref name="options"/> is a list of <c>{ "value": number, "label": text }</c>,
	/// stepped through in that order; the setting holds the chosen value.
	/// </summary>
	public string SetSettingChoice(string setting, string label, Godot.Collections.Array options) =>
		SetSettingRow(setting, new Godot.Collections.Dictionary
		{
			["label"] = label,
			["type"] = "choice",
			["options"] = options,
		});

	public string RemoveSettingRow(string setting)
	{
		Godot.Collections.Dictionary page = ReadJson(ModSettingsUi.ModSettingsUiPath);
		page.Remove(setting);
		return WriteJson(ModSettingsUi.ModSettingsUiPath, page);
	}

	/// <summary>The active mod's whole <c>settings.ui.json</c>.</summary>
	public Godot.Collections.Dictionary GetSettingsUi() => ReadJson(ModSettingsUi.ModSettingsUiPath);
}
#endif
