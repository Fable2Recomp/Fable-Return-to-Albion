#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using Refii.EditorTools;
using Refii.Mods;
using Refii.Ui;

namespace Refii.Assets.UI;

/// <summary>
/// The UI files of the mod the Studio is editing, as read and as edited: <c>ui/hud.json</c>,
/// <c>ui/templates.json</c> (its screens and modals — its menus are the menu editor's, through
/// <see cref="ModMenuTarget"/>), <c>settings.ui.json</c> and <c>settings.json</c>. Every read and
/// every write goes through <see cref="RefiiModApi"/>; a save makes the per-entry calls a script
/// would make, between one <see cref="RefiiModApi.BeginChanges"/> and its end, so the mods re-mount
/// once.
/// </summary>
public sealed class UiStudioDocs
{
	public readonly RefiiModApi Api = new();

	public string Mod { get; private set; } = string.Empty;

	public JsonObject HudLoaded { get; private set; } = new();
	public JsonObject Hud { get; private set; } = new();
	public JsonObject TemplatesLoaded { get; private set; } = new();
	public JsonObject Templates { get; private set; } = new();
	public JsonObject SettingsUiLoaded { get; private set; } = new();
	public JsonObject SettingsUi { get; private set; } = new();
	public string SettingsText { get; private set; } = string.Empty;

	public ModMenuTarget Menus { get; }

	public UiStudioDocs() => Menus = new ModMenuTarget(this);

	/// <summary>Reads every file of the active mod again; unsaved edits are dropped.</summary>
	public void Load()
	{
		Mod = Api.GetActiveMod();
		HudLoaded = Read(Engine_HudModPieces.ModHudPath);
		Hud = (JsonObject)HudLoaded.DeepClone();
		TemplatesLoaded = Read(Engine_UiTemplates.ModTemplatesPath);
		Templates = (JsonObject)TemplatesLoaded.DeepClone();
		SettingsUiLoaded = Read(ModSettingsUi.ModSettingsUiPath);
		SettingsUi = (JsonObject)SettingsUiLoaded.DeepClone();
		SettingsText = Api.ReadText(ModSettings.ModSettingsPath);
		Menus.Compose();
	}

	/// <summary>
	/// Reads <c>ui/templates.json</c> again after the menu editor wrote it, keeping the screens and
	/// modals as edited, and composes the menus with it.
	/// </summary>
	public void ReloadTemplates()
	{
		JsonNode? screens = Templates["screens"]?.DeepClone();
		JsonNode? modals = Templates[Engine_UiModals.Section]?.DeepClone();
		TemplatesLoaded = Read(Engine_UiTemplates.ModTemplatesPath);
		Templates = (JsonObject)TemplatesLoaded.DeepClone();
		Put(Templates, "screens", screens);
		Put(Templates, Engine_UiModals.Section, modals);
		Menus.Compose();
	}

	private static void Put(JsonObject root, string key, JsonNode? value)
	{
		if (value is null)
		{
			root.Remove(key);
		}
		else
		{
			root[key] = value;
		}
	}

	/// <summary>
	/// Whether another mod is active now, or the active mod's UI files on disk differ from what
	/// was last read — a write from outside the tab (a script, another editor, a re-import).
	/// </summary>
	public bool Stale() =>
		!string.Equals(Api.GetActiveMod(), Mod, StringComparison.OrdinalIgnoreCase) ||
		!Engine_UiMenuEdit.Same(Read(Engine_HudModPieces.ModHudPath), HudLoaded) ||
		!Engine_UiMenuEdit.Same(Read(Engine_UiTemplates.ModTemplatesPath), TemplatesLoaded) ||
		!Engine_UiMenuEdit.Same(Read(ModSettingsUi.ModSettingsUiPath), SettingsUiLoaded) ||
		Api.ReadText(ModSettings.ModSettingsPath) != SettingsText;

	private JsonObject Read(string path) => Engine_HudLayoutEdit.ParseObject(Mod.Length == 0 ? null : Api.ReadText(path));

	public static string Text(JsonObject root) => root.ToJsonString(Engine_HudLayoutEdit.Indented) + "\n";

	/// <summary>A JSON object as the Godot dictionary the editor API takes.</summary>
	public static Godot.Collections.Dictionary ToGodot(JsonObject record) =>
		Json.ParseString(record.ToJsonString()) is { VariantType: Variant.Type.Dictionary } parsed
			? parsed.AsGodotDictionary()
			: new Godot.Collections.Dictionary();

	/// <summary>A Godot dictionary (a snapshot record) as JSON.</summary>
	public static JsonObject FromGodot(Godot.Collections.Dictionary record) =>
		Engine_HudLayoutEdit.ParseObject(Json.Stringify(record));

	/// <summary>The enabled mods' files at <paramref name="path"/> with this mod's as <paramref name="text"/> (lowest priority first).</summary>
	public List<(string Mod, string Text)> Files(string path, string? text) =>
		Engine_UiMenuEdit.Substitute(ModEntries.ReadEnabledText(path), Mod, text);

	public bool HudChanged => !Engine_UiMenuEdit.Same(HudLoaded, Hud);

	public bool ScreensChanged => !Engine_UiMenuEdit.Same(TemplatesLoaded["screens"], Templates["screens"]);

	public bool ModalsChanged =>
		!Engine_UiMenuEdit.Same(TemplatesLoaded[Engine_UiModals.Section], Templates[Engine_UiModals.Section]);

	public bool TemplatesChanged => ScreensChanged || ModalsChanged;

	public bool SettingsChanged => !Engine_UiMenuEdit.Same(SettingsUiLoaded, SettingsUi);

	/// <summary>
	/// The mod's <c>ui/templates.json</c> as it would be saved now: the screens and modals as edited
	/// and every menu the menu editor changed (whole for the mod's own, a patch for the rest).
	/// </summary>
	public string TemplatesText(IReadOnlyDictionary<string, JsonObject> changedMenus)
	{
		var root = (JsonObject)Templates.DeepClone();
		if (root["menus"] is not JsonObject menus)
		{
			menus = new JsonObject();
			root["menus"] = menus;
		}

		foreach ((string name, JsonObject record) in changedMenus)
		{
			if (Menus.RecordToWrite(name, record) is { } written)
			{
				menus[name] = written;
			}
			else
			{
				menus.Remove(name);
			}
		}

		return Text(root);
	}

	// ---------------------------------------------------------------------------------------
	// Saving — the per-entry calls
	// ---------------------------------------------------------------------------------------

	/// <summary>Writes the HUD's changes through the editor API; the first error, or empty.</summary>
	public string SaveHud()
	{
		foreach (Engine_HudModEdit.Op op in Engine_HudModEdit.Diff(HudLoaded, Hud))
		{
			string error = op.Kind switch
			{
				Engine_HudModEdit.OpKind.RemoveEntry => Api.RemoveHudEntry(op.Id),
				Engine_HudModEdit.OpKind.SetPiece => Api.SetHudPiece(ToGodot((JsonObject)op.Value!)),
				Engine_HudModEdit.OpKind.SetClone => Api.SetHudClone(ToGodot((JsonObject)op.Value!)),
				Engine_HudModEdit.OpKind.Hide => Api.HideHudPiece(op.Id),
				Engine_HudModEdit.OpKind.Show => Api.ShowHudPiece(op.Id),
				Engine_HudModEdit.OpKind.SetScale => Api.SetHudScale(op.Id, (float)(Engine_UiMenuEdit.NumberOf(op.Value) ?? 1.0)),
				Engine_HudModEdit.OpKind.RemoveScale => Api.RemoveHudScale(op.Id),
				Engine_HudModEdit.OpKind.SetLayout => SetLayout(op.Id, op.Value as JsonObject),
				Engine_HudModEdit.OpKind.RemoveLayout => Api.RemoveHudLayout(op.Id),
				_ => string.Empty,
			};
			if (error.Length != 0)
			{
				return $"{op.Kind} '{op.Id}': {error}";
			}
		}

		HudLoaded = (JsonObject)Hud.DeepClone();
		return string.Empty;
	}

	private string SetLayout(string id, JsonObject? entry)
	{
		if (entry is null || Engine_HudModLayout.ParseEntries(System.Text.Json.JsonDocument.Parse(
				new JsonObject { [id] = entry.DeepClone() }.ToJsonString()).RootElement) is not { } moves ||
			!moves.TryGetValue(id, out Engine_HudModLayout.Move move))
		{
			return "the entry needs \"anchor\": [x, y] and \"offset\": [x, y]";
		}

		return Api.SetHudLayout(id, new Vector2(move.AnchorX, move.AnchorY), new Vector2(move.OffsetX, move.OffsetY));
	}

	/// <summary>Writes the screens' and modals' changes; the first error, or empty.</summary>
	public string SaveTemplates()
	{
		JsonObject screensBefore = TemplatesLoaded["screens"] as JsonObject ?? new JsonObject();
		JsonObject screensAfter = Templates["screens"] as JsonObject ?? new JsonObject();
		foreach ((string key, _) in screensBefore)
		{
			if (!screensAfter.ContainsKey(key) && int.TryParse(key, out int id) && Api.RemoveScreen(id) is { Length: > 0 } error)
			{
				return $"screen {key}: {error}";
			}
		}

		foreach ((string key, JsonNode? record) in screensAfter)
		{
			if (record is JsonObject screen && int.TryParse(key, out int id) &&
				(!screensBefore.TryGetPropertyValue(key, out JsonNode? old) || !Engine_UiMenuEdit.Same(old, screen)) &&
				Api.SetScreen(id, ToGodot(screen)) is { Length: > 0 } error)
			{
				return $"screen {key}: {error}";
			}
		}

		JsonObject modalsBefore = TemplatesLoaded[Engine_UiModals.Section] as JsonObject ?? new JsonObject();
		JsonObject modalsAfter = Templates[Engine_UiModals.Section] as JsonObject ?? new JsonObject();
		foreach ((string key, _) in modalsBefore)
		{
			if (!modalsAfter.ContainsKey(key) && Api.RemoveModal(key) is { Length: > 0 } error)
			{
				return $"modal {key}: {error}";
			}
		}

		foreach ((string key, JsonNode? record) in modalsAfter)
		{
			if (modalsBefore.TryGetPropertyValue(key, out JsonNode? old) && Engine_UiMenuEdit.Same(old, record))
			{
				continue;
			}

			string error = record is JsonObject modal ? Api.SetModal(key, ToGodot(modal)) : Api.ReleaseModal(key);
			if (error.Length != 0)
			{
				return $"modal {key}: {error}";
			}
		}

		TemplatesLoaded = (JsonObject)Templates.DeepClone();
		return string.Empty;
	}

	/// <summary>Writes the settings page's changed rows; the first error, or empty.</summary>
	public string SaveSettings()
	{
		foreach ((string key, _) in SettingsUiLoaded)
		{
			if (!SettingsUi.ContainsKey(key) && Api.RemoveSettingRow(key) is { Length: > 0 } error)
			{
				return $"setting {key}: {error}";
			}
		}

		foreach ((string key, JsonNode? row) in SettingsUi)
		{
			if (row is JsonObject record &&
				(!SettingsUiLoaded.TryGetPropertyValue(key, out JsonNode? old) || !Engine_UiMenuEdit.Same(old, record)) &&
				Api.SetSettingRow(key, ToGodot(record)) is { Length: > 0 } error)
			{
				return $"setting {key}: {error}";
			}
		}

		SettingsUiLoaded = (JsonObject)SettingsUi.DeepClone();
		return string.Empty;
	}
}

/// <summary>
/// A mod's menus for the menu editor: the mod's OWN menus (records it states whole) edited and
/// saved whole with <see cref="RefiiModApi.SetMenu"/>, and every other menu edited as it composes
/// with the mod and saved as the mod's patch (<see cref="Engine_UiMenuEdit.Patch"/> against the menu
/// composed WITHOUT the mod, written with <see cref="RefiiModApi.SetMenuPatch"/>). The compositions
/// are the engine's own (<see cref="Engine_UiTemplates.Compose"/>), with the mod's file as it stands.
/// </summary>
public sealed class ModMenuTarget : IUiMenuTarget
{
	private readonly UiStudioDocs _docs;
	private JsonObject _base = new();
	private JsonObject _current = new();

	/// <summary>The conflicts the last composition with the mod recorded.</summary>
	public IReadOnlyList<Engine_UiTemplates.ModConflict> Conflicts { get; private set; } =
		Array.Empty<Engine_UiTemplates.ModConflict>();

	/// <summary>Every screen record as the mod composes, by id — the screens editor's starting point.</summary>
	public JsonObject Screens => _current["screens"] as JsonObject ?? new JsonObject();

	/// <summary>Every modal row as the mod composes, by key.</summary>
	public JsonObject Modals => _current[Engine_UiModals.Section] as JsonObject ?? new JsonObject();

	public ModMenuTarget(UiStudioDocs docs) => _docs = docs;

	/// <summary>Composes the registry without the mod and with its file as loaded.</summary>
	public void Compose() => Compose(Text(_docs.TemplatesLoaded));

	/// <summary>Composes the registry without the mod and with <paramref name="modText"/> as its file.</summary>
	public void Compose(string modText)
	{
		if (_docs.Mod.Length == 0)
		{
			_base = new JsonObject();
			_current = new JsonObject();
			Conflicts = Array.Empty<Engine_UiTemplates.ModConflict>();
			return;
		}

		_base = UiStudioDocs.FromGodot(Engine_UiTemplates.Compose(_docs.Files(Engine_UiTemplates.ModTemplatesPath, null)).Records);
		(Godot.Collections.Dictionary records, IReadOnlyList<Engine_UiTemplates.ModConflict> conflicts) =
			Engine_UiTemplates.Compose(_docs.Files(Engine_UiTemplates.ModTemplatesPath, modText));
		_current = UiStudioDocs.FromGodot(records);
		Conflicts = conflicts;
	}

	/// <summary>The menu <paramref name="name"/> as it composes with the mod's file as last composed.</summary>
	public JsonObject? Composed(string name) => Menu(_current, name);

	private static string Text(JsonObject root) => root.ToJsonString();

	private static JsonObject? Menu(JsonObject root, string name) =>
		root["menus"] is JsonObject menus && menus[name] is JsonObject record ? record : null;

	private JsonObject? ModRecord(string name) => Menu(_docs.TemplatesLoaded, name);

	/// <summary>A menu the mod states whole (not a patch).</summary>
	public bool IsOwn(string name) => ModRecord(name) is { } record && !Engine_UiMenuEdit.Flag(record, "patch", false);

	public string Name => $"mod '{_docs.Mod}'";

	public IReadOnlyList<string> MenuNames()
	{
		IEnumerable<string> names = _base["menus"] is JsonObject menus ? menus.Select(p => p.Key) : Enumerable.Empty<string>();
		if (_docs.TemplatesLoaded["menus"] is JsonObject own)
		{
			names = names.Concat(own.Select(p => p.Key));
		}

		return names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
	}

	public string Describe(string menu)
	{
		if (IsOwn(menu))
		{
			return Menu(_base, menu) is null
				? "The mod's own menu, saved whole. A row opens it with \"opens\": \"" + menu + "\" or the command Opens:" + menu + "; B returns."
				: "The mod REPLACES this menu whole (its record has no \"patch\"). Saved whole.";
		}

		return ModRecord(menu) is not null
			? "A menu the mod patches. Edits are saved as the mod's patch over the menu as the game and the other enabled mods leave it — only the rows you change."
			: "Not changed by the mod. Edits are saved as a patch: only the rows you change, composing with other mods' patches.";
	}

	public JsonObject? Load(string menu)
	{
		if (IsOwn(menu))
		{
			return (JsonObject)ModRecord(menu)!.DeepClone();
		}

		return (Menu(_current, menu) ?? Menu(_base, menu))?.DeepClone() as JsonObject;
	}

	/// <summary>
	/// What the mod's file states for <paramref name="name"/> after an edit to <paramref name="edited"/>:
	/// the record itself for an own menu, else the patch against the composition without the mod
	/// (null when the edit leaves that menu as it was — the mod then states nothing).
	/// </summary>
	public JsonObject? RecordToWrite(string name, JsonObject edited) =>
		IsOwn(name)
			? edited
			: Engine_UiMenuEdit.Patch(Menu(_base, name) ?? new JsonObject(), edited);

	public string Save(IReadOnlyDictionary<string, JsonObject> changed)
	{
		RefiiModApi api = _docs.Api;
		api.BeginChanges();
		try
		{
			foreach ((string name, JsonObject record) in changed)
			{
				JsonObject? written = RecordToWrite(name, record);
				string error = written is null
					? (ModRecord(name) is null ? string.Empty : api.RemoveMenu(name))
					: IsOwn(name)
						? api.SetMenu(name, UiStudioDocs.ToGodot(written))
						: api.SetMenuPatch(name, UiStudioDocs.ToGodot(written));
				if (error.Length != 0)
				{
					return $"menu '{name}': {error}";
				}
			}
		}
		finally
		{
			api.EndChanges();
		}

		_docs.ReloadTemplates();
		return Verify(changed);
	}

	/// <summary>
	/// Composes the saved file and compares each changed patched menu with what the author left:
	/// a difference means the patch does not reproduce the edit (two rows sharing a name, a parent
	/// another mod removes), reported rather than hidden.
	/// </summary>
	private string Verify(IReadOnlyDictionary<string, JsonObject> changed)
	{
		var differs = changed
			.Where(pair => !IsOwn(pair.Key) && Menu(_current, pair.Key) is { } composed &&
				!Engine_UiMenuEdit.Same(Engine_UiMenuEdit.Entries(composed), Engine_UiMenuEdit.Entries(pair.Value)))
			.Select(pair => pair.Key)
			.ToList();
		return differs.Count == 0
			? string.Empty
			: $"saved, but the game composes {string.Join(", ", differs)} differently from your edit — check for rows sharing a name";
	}

	public bool CanCreate => _docs.Mod.Length != 0;

	public string Create(string menu)
	{
		if (Menu(_base, menu) is not null || ModRecord(menu) is not null)
		{
			return $"'{menu}' already exists; pick it from the list";
		}

		string error = _docs.Api.SetMenu(menu, new Godot.Collections.Dictionary
		{
			["title"] = string.Empty,
			["highlight"] = string.Empty,
			["entries"] = new Godot.Collections.Array(),
		});
		if (error.Length == 0)
		{
			_docs.ReloadTemplates();
		}

		return error;
	}

	public string? DropLabel(string menu) =>
		ModRecord(menu) is null ? null : IsOwn(menu) ? "🗑 Delete menu" : "↺ Drop the mod's patch";

	public string Drop(string menu)
	{
		string error = _docs.Api.RemoveMenu(menu);
		if (error.Length == 0)
		{
			_docs.ReloadTemplates();
		}

		return error;
	}
}
#endif
