#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using Refii.Assets;
using Refii.Core;
using Refii.Mods;
using Refii.Text;
using Refii.Ui;

namespace Refii.EditorTools;

/// <summary>
/// THE MODDING API, for scripts in the editor — GDScript and C# alike:
/// <code>
/// var api := RefiiModApi.new()
/// api.CreateMod("my_mod", "My Mod", "Me", "What it does")
/// api.SetModLua("CombatWheel.ActiveAttackerTimeOut = 6")
/// api.SetText("MY_MOD_NAME", "Stamina")
/// api.SetHudClone({"id": "my_mod.mana", "of": "group.PlayerHealth.PlayerHealth", "offset": [0, 34], "hue": 0.62})
/// </code>
/// Every call writes into the ACTIVE mod's <c>.f2mod</c> — the one the Mod Studio is editing;
/// <see cref="SetActiveMod"/> picks it — re-mounts the mods and tells the Studio, through the
/// package writer the Studio's own editors use. Nothing here is a second implementation: each
/// content kind is read at runtime by the engine class its method names, and this only authors
/// the file that class reads.
/// </summary>
/// <remarks>
/// Godot exposes the methods under these names and passes every argument — C# defaults do not
/// reach GDScript, so there are none. Methods that write return an error string; empty means
/// it was written. The runtime contracts the content feeds (mod values, <c>RefiiModSave</c>, the
/// native surface in <c>core/refii_mod.h</c>) are <see cref="DescribeRuntime"/> and the Modding
/// Guide's Script API page.
/// </remarks>
[Tool]
[GlobalClass]
public partial class RefiiModApi : RefCounted
{
	public const string DefaultLanguage = "en-uk";

	// ---------------------------------------------------------------------------------------
	// Mods
	// ---------------------------------------------------------------------------------------

	/// <summary>The mods folder: the app's <c>assets/mods</c>.</summary>
	public string GetModsRoot()
	{
		string root = EditorModValueService.ModsRootOverride ?? Engine_GameAssets.ModsRoot;
		return string.IsNullOrEmpty(root) ? ModWorkspace.GetDefaultModsRoot(Engine_AppRoot.AssetsDirectory) : root;
	}

	/// <summary>The mod every call writes into — the one the Studio is editing.</summary>
	public string GetActiveMod() => EditorModExporter.ActiveModName;

	public void SetActiveMod(string mod) => EditorModExporter.SetActiveModName(mod);

	public string[] ListMods() =>
		ModWorkspace.DiscoverModInstalls(GetModsRoot()).Select(m => m.Name).ToArray();

	/// <summary>Creates a mod package and makes it active.</summary>
	public string CreateMod(string mod, string title, string author, string description)
	{
		if (!ModWorkspace.CreateModPackage(GetModsRoot(), mod, out string package, out string error))
		{
			return error;
		}

		var meta = new ModMetadata
		{
			Title = title.Length == 0 ? mod : title,
			Version = "1.0.0",
			Author = author,
			Description = description,
		};
		F2ModPackage.IncrementalSync(package, Array.Empty<(string, byte[])>(), null, meta);
		SetActiveMod(mod);
		Changed();
		return string.Empty;
	}

	/// <summary>
	/// Packs a mod source folder into the active mod, as the app packs its bundled mods: every
	/// file at its own path, <c>mod.toml</c> as the metadata, the readme left out.
	/// </summary>
	public string ImportFolder(string folder)
	{
		if (!Directory.Exists(folder))
		{
			return $"no folder '{folder}'";
		}

		string package = PackagePath();
		if (package.Length == 0)
		{
			return NoActiveMod;
		}

		string toml = Path.Combine(folder, ModWorkspace.ModMetadataFilename);
		ModMetadata? meta = File.Exists(toml) ? ModWorkspace.ParseModToml(File.ReadAllText(toml)) : null;
		if (!F2ModPackage.IncrementalSync(package, ModBundled.Collect(folder), null, meta))
		{
			return $"could not pack '{folder}' into '{GetActiveMod()}'";
		}

		Changed();
		return string.Empty;
	}

	// ---------------------------------------------------------------------------------------
	// Files
	// ---------------------------------------------------------------------------------------

	/// <summary>Every entry in the active mod, by its virtual path.</summary>
	public string[] ListFiles()
	{
		if (!F2ModPackage.TryOpen(PackagePath(), out F2ModPackage? package, out _) || package is null)
		{
			return Array.Empty<string>();
		}

		using (package)
		{
			return package.Entries.Select(e => e.Path).ToArray();
		}
	}

	/// <summary>Any file at its virtual path — the game sees it there, over the game's own.</summary>
	public string WriteFile(string path, byte[] bytes) => Write(path, bytes);

	public string WriteText(string path, string text) => Write(path, new UTF8Encoding(false).GetBytes(text));

	public byte[] ReadFile(string path) => Read(path) ?? Array.Empty<byte>();

	public string ReadText(string path) => Read(path) is { } bytes ? Encoding.UTF8.GetString(bytes) : string.Empty;

	public string DeleteFile(string path)
	{
		string package = PackagePath();
		if (package.Length == 0)
		{
			return NoActiveMod;
		}

		F2ModPackage.IncrementalSync(package, Array.Empty<(string, byte[])>(), new[] { ModLoadOrder.Normalize(path) });
		Changed();
		return string.Empty;
	}

	// ---------------------------------------------------------------------------------------
	// Lua — composed by Engine_ModLua, run by the payload after the game's own scripts
	// ---------------------------------------------------------------------------------------

	/// <summary>
	/// The mod's Lua, run in the game VM once per session after the shipped scripts load
	/// (<c>mod/init.lua</c>). Patch the shipped tables; never ship an edited copy of a shipped
	/// script. Checked with the game's compiler first.
	/// </summary>
	public string SetModLua(string source) => WriteLua(ModLua.ModInitPath, source);

	/// <summary>Lua run after every load of the camera scripts (<c>mod/camera.lua</c>); must be safe to run again.</summary>
	public string SetCameraLua(string source) => WriteLua(ModLua.ModCameraPath, source);

	/// <summary>The game's Lua compiler's verdict on <paramref name="source"/>; empty when it compiles.</summary>
	public string CheckLua(string source) =>
		Refii.Lua.Engine_LuaDecompiler.TryCheckSource(source, "=check", out string error) ? string.Empty : error;

	// ---------------------------------------------------------------------------------------
	// Native — compiled into the game module by the launcher (Engine_ModNative, GameCodeBuild)
	// ---------------------------------------------------------------------------------------

	/// <summary>
	/// A native source under <c>native/</c>: C++ against <c>core/refii_mod.h</c>, overriding guest
	/// functions by their config names with <c>REX_EXTERN(name) { ... }</c>.
	/// </summary>
	public string SetNativeSource(string fileName, string source)
	{
		string relative = fileName.Replace('\\', '/');
		return ModNative.IsSafeRelativePath(relative)
			? WriteText(ModNative.Folder + relative, source)
			: $"'{fileName}' is not a path inside the mod";
	}

	/// <summary>The guest functions the active mod's native sources override, derived from the sources.</summary>
	public string[] GetNativeOverrides() =>
		Install() is { } install && ModNative.Read(install) is { } sources
			? sources.Overrides.ToArray()
			: Array.Empty<string>();

	/// <summary>
	/// Functions the active mod hooks without ever calling the original while refii or another
	/// enabled mod hooks them beneath it — each one would refuse the game code build. A hook that
	/// calls <c>__imp__name</c> shares its function in a chain and is never listed here. Empty when
	/// there is none.
	/// </summary>
	public string[] CheckNativeConflicts()
	{
		if (Install() is not { } install || ModNative.Read(install) is not { } own)
		{
			return Array.Empty<string>();
		}

		var all = ModNative.ReadEnabled(GetModsRoot()).Where(s => s.ModName != own.ModName).ToList();
		all.Add(own);
		Dictionary<string, string> refii = ModNative.ReadOverridesInDirectory(
			Path.Combine(Engine_AppRoot.BinDirectory, "recompile", "hooks"), ModNative.RefiiOwner);
		return ModNative.FindConflicts(all, refii)
			.Where(c => c.First == own.ModName || c.Second == own.ModName)
			.Select(c => $"{c.Function}: {c.First} replaces it over {c.Second}")
			.ToArray();
	}

	/// <summary>
	/// Functions the active mod hooks that refii or another enabled mod also hooks, each with its
	/// owners outermost first (<c>rex_X: a → b → refii</c>). The hooks run as a chain in load order.
	/// </summary>
	public string[] GetNativeChains()
	{
		if (Install() is not { } install || ModNative.Read(install) is not { } own)
		{
			return Array.Empty<string>();
		}

		var all = ModNative.ReadEnabled(GetModsRoot()).Where(s => s.ModName != own.ModName).ToList();
		all.Add(own);
		Dictionary<string, string> refii = ModNative.ReadOverridesInDirectory(
			Path.Combine(Engine_AppRoot.BinDirectory, "recompile", "hooks"), ModNative.RefiiOwner);
		return ModNative.PlanChains(all, refii)
			.Where(chain => chain.Owners.Contains(own.ModName))
			.Select(chain => $"{chain.Function}: {string.Join(" → ", chain.Owners)}")
			.ToArray();
	}

	/// <summary>The staged <c>refii_mod.h</c> native mods compile against, or empty when not staged.</summary>
	public string GetNativeHeaderPath()
	{
		string include = Path.Combine(Engine_AppRoot.BinDirectory, "recompile", "include");
		return Directory.Exists(include)
			? Directory.EnumerateFiles(include, "refii_mod.h", SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty
			: string.Empty;
	}

	// ---------------------------------------------------------------------------------------
	// The event API — core/refii_mod_events.h, described by Engine_ModApi
	// ---------------------------------------------------------------------------------------

	/// <summary><c>REFII_MOD_API_VERSION</c>: the events and helpers this app's payload offers.</summary>
	public int GetApiVersion() => ModApi.Version;

	/// <summary>Every event's C++ name (<c>REFII_MOD_ON(Name, fn)</c>), in kind order.</summary>
	public string[] ListEvents() => ModApi.Events.Select(e => e.Name).ToArray();

	/// <summary>
	/// One event by its C++ or Lua name: name, lua_name, kind, seam, summary, cancel (empty when
	/// it cannot be cancelled) and fields (name, type, meaning, writable). Empty for an unknown name.
	/// </summary>
	public Godot.Collections.Dictionary DescribeEvent(string name)
	{
		var result = new Godot.Collections.Dictionary();
		if (ModApi.Find(name) is not { } e)
		{
			return result;
		}

		result["name"] = e.Name;
		result["lua_name"] = e.LuaName;
		result["kind"] = e.Kind;
		result["seam"] = e.Seam;
		result["summary"] = e.Summary;
		result["cancel"] = e.Cancel;
		var fields = new Godot.Collections.Array();
		foreach (ModApiField f in e.Fields)
		{
			fields.Add(new Godot.Collections.Dictionary
			{
				["name"] = f.Name,
				["type"] = f.Type,
				["meaning"] = f.Meaning,
				["writable"] = f.Writable,
			});
		}

		result["fields"] = fields;
		return result;
	}

	/// <summary>The native helper calls, each as "signature — meaning".</summary>
	public string[] ListNativeHelpers() => ModApi.Helpers.Select(h => $"{h.Signature} — {h.Meaning}").ToArray();

	/// <summary>The Lua-side calls a mod's init.lua can make, each as "call — meaning".</summary>
	public string[] ListLuaHelpers() => ModApi.LuaHelpers.Select(h => $"{h.Signature} — {h.Meaning}").ToArray();

	/// <summary>
	/// Writes <c>native/&lt;fileName&gt;</c>: a handler for <paramref name="eventName"/> with every
	/// field listed, ready to fill in. Returns an error for an unknown event.
	/// </summary>
	public string NewNativeHandler(string eventName, string fileName)
	{
		if (ModApi.Find(eventName) is not { } e)
		{
			return $"no event '{eventName}' — ListEvents() names them";
		}

		return SetNativeSource(fileName, ModApi.NativeTemplate(e, GetActiveMod()));
	}

	/// <summary>
	/// Appends a <c>Refii.on("&lt;event&gt;", ...)</c> handler to the mod's init.lua, with the
	/// event's fields listed. Returns an error for an unknown event.
	/// </summary>
	public string AddLuaHandler(string eventName)
	{
		if (ModApi.Find(eventName) is not { } e)
		{
			return $"no event '{eventName}' — ListEvents() names them";
		}

		string current = ReadText(ModLua.ModInitPath);
		string source = (current.Length == 0 ? string.Empty : current.TrimEnd() + "\n\n") + ModApi.LuaTemplate(e);
		return WriteLua(ModLua.ModInitPath, source);
	}

	// ---------------------------------------------------------------------------------------
	// The game's own Lua natives — res://modding_tools/game_lua_api.tsv
	// ---------------------------------------------------------------------------------------

	public const string GameLuaApiPath = "res://modding_tools/game_lua_api.tsv";

	/// <summary>The tables the game registers natives under (_G, Player, Dog, GUI, EntityMetatable ...).</summary>
	public string[] ListGameLuaTables() =>
		GameLuaApi().Select(row => row.Table).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToArray();

	/// <summary>The natives of one table, by name; a mod's init.lua can call every one of them.</summary>
	public string[] ListGameLuaNatives(string table) =>
		GameLuaApi().Where(row => string.Equals(row.Table, table, StringComparison.OrdinalIgnoreCase))
			.Select(row => row.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

	/// <summary>Natives whose name contains <paramref name="text"/>, as "Table.Name".</summary>
	public string[] FindGameLuaNatives(string text) =>
		GameLuaApi().Where(row => row.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
			.Select(row => row.Table + "." + row.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

	private static List<(string Table, string Name)>? _gameLuaApi;

	private static List<(string Table, string Name)> GameLuaApi()
	{
		if (_gameLuaApi != null)
		{
			return _gameLuaApi;
		}

		var rows = new List<(string, string)>();
		using Godot.FileAccess? file = Godot.FileAccess.Open(GameLuaApiPath, Godot.FileAccess.ModeFlags.Read);
		if (file != null)
		{
			foreach (string line in file.GetAsText().Split('\n'))
			{
				if (line.Length == 0 || line[0] == '#')
				{
					continue;
				}

				string[] parts = line.Split('\t');
				if (parts.Length >= 2)
				{
					rows.Add((parts[0], parts[1]));
				}
			}
		}

		return _gameLuaApi = rows;
	}

	// ---------------------------------------------------------------------------------------
	// Settings — settings.json, pushed to the game by Engine_ModSettings
	// ---------------------------------------------------------------------------------------

	/// <summary>A tunable the mod's native code reads with <c>refii::mods::Setting(key, default)</c>.</summary>
	public string SetSetting(string key, float value)
	{
		Godot.Collections.Dictionary settings = ReadJson(ModSettings.ModSettingsPath);
		int dot = key.IndexOf('.');
		if (dot > 0 && settings.TryGetValue(key[..dot], out Variant group) && group.VariantType == Variant.Type.Dictionary)
		{
			group.AsGodotDictionary()[key[(dot + 1)..]] = value;
		}
		else
		{
			settings[key] = value;
		}

		return WriteJson(ModSettings.ModSettingsPath, settings);
	}

	// ---------------------------------------------------------------------------------------
	// Text and images
	// ---------------------------------------------------------------------------------------

	/// <summary>One English string (a Babel row): a new tag is added, an existing one replaced.</summary>
	public string SetText(string tag, string value) => SetTextIn(DefaultLanguage, tag, value);

	/// <summary><see cref="SetText"/> for another language folder.</summary>
	public string SetTextIn(string language, string tag, string value)
	{
		string path = $"language/{language}/text/book{BabelOverlay.CsvSuffix}";
		var csv = new StringBuilder("key,value\n");
		foreach (BabelOverlayRow row in BabelOverlay.ParseCsv(ReadText(path), new List<string>()))
		{
			if (row.Key != tag)
			{
				csv.Append(row.Key).Append(',').Append(Quote(row.Value)).Append('\n');
			}
		}

		csv.Append(tag).Append(',').Append(Quote(value)).Append('\n');
		return WriteText(path, csv.ToString());
	}

	/// <summary>
	/// A texture from a Godot image, served to the game as <paramref name="texPath"/>: written as
	/// <c>&lt;texPath&gt;.png</c>, which the mount converts (Engine_ModImageOverlays).
	/// </summary>
	public string SetImage(string texPath, Image image) =>
		texPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
			? WriteFile(texPath + ".png", image.SavePngToBuffer())
			: $"'{texPath}' must be a .tex path";

	// ---------------------------------------------------------------------------------------
	// HUD — ui/hud.json, read by Engine_HudModPieces and Engine_HudView
	// ---------------------------------------------------------------------------------------

	/// <summary>
	/// A HUD piece of the mod's own — meter, panel or picture — keyed by its <c>id</c>. A picture
	/// may carry <c>textures</c> (a list) and <c>index</c> (a value source) to show one of them.
	/// </summary>
	public string SetHudPiece(Godot.Collections.Dictionary piece) =>
		Upsert(Engine_HudModPieces.ModHudPath, "pieces", "id", piece);

	/// <summary>A copy of a shipped HUD piece (<c>of</c>): recoloured, re-iconed, fed by mod values.</summary>
	public string SetHudClone(Godot.Collections.Dictionary clone) =>
		Upsert(Engine_HudModPieces.ModHudPath, "clones", "id", clone);

	/// <summary>Hides a shipped HUD piece the mod replaces.</summary>
	public string HideHudPiece(string id) => SetHidden(id, true);

	/// <summary>Undoes <see cref="HideHudPiece"/>.</summary>
	public string ShowHudPiece(string id) => SetHidden(id, false);

	/// <summary>A size factor for any HUD piece — shipped or a mod's copy.</summary>
	public string SetHudScale(string id, float scale)
	{
		Godot.Collections.Dictionary hud = ReadJson(Engine_HudModPieces.ModHudPath);
		Section(hud, "scale")[id] = scale;
		return WriteJson(Engine_HudModPieces.ModHudPath, hud);
	}

	/// <summary>Removes the mod's piece or copy <paramref name="id"/>.</summary>
	public string RemoveHudEntry(string id) =>
		Remove(Engine_HudModPieces.ModHudPath, new[] { "pieces", "clones" }, "id", id);

	/// <summary>The shipped HUD pieces' ids as the last run built them — what <c>of</c>, <c>follow</c> and hiding name.</summary>
	public string[] ListShippedHudPieces() => SnapshotKeys(Engine_HudLayout.SnapshotPath, "pieces");

	// ---------------------------------------------------------------------------------------
	// Abilities — ui/abilities.json, read by Engine_UiModAbilities
	// ---------------------------------------------------------------------------------------

	/// <summary>
	/// An ability of the mod's own on the abilities book, keyed by <c>name</c> (with category,
	/// nameTag, descriptionTag, maxLevel, the mod-value keys level/cost/stat/buy/refund/refundValue,
	/// and image). The mod's native code does the buying and refunding.
	/// </summary>
	public string SetAbility(Godot.Collections.Dictionary ability) =>
		Upsert(Engine_UiModAbilities.ModAbilitiesPath, "abilities", "name", ability);

	public string RemoveAbility(string name) =>
		Remove(Engine_UiModAbilities.ModAbilitiesPath, new[] { "abilities" }, "name", name);

	// ---------------------------------------------------------------------------------------
	// Menus and screens — ui/templates.json, composed by Engine_UiTemplates
	// ---------------------------------------------------------------------------------------

	/// <summary>
	/// Entries added to, replaced in or removed from <paramref name="menu"/> by name (each may
	/// state <c>after</c>, <c>before</c>, <c>parent</c> or <c>remove</c>), composing with other
	/// mods' patches to the same menu.
	/// </summary>
	public string PatchMenu(string menu, Godot.Collections.Array entries)
	{
		Godot.Collections.Dictionary templates = ReadJson(Engine_UiTemplates.ModTemplatesPath);
		Godot.Collections.Dictionary record = Section(Section(templates, "menus"), menu);
		record["patch"] = true;
		Godot.Collections.Array list = List(record, "entries");
		foreach (Variant entry in entries)
		{
			if (entry.VariantType == Variant.Type.Dictionary)
			{
				RemoveByKey(list, "name", entry.AsGodotDictionary().TryGetValue("name", out Variant n) ? n.AsString() : string.Empty);
				list.Add(entry);
			}
		}

		return WriteJson(Engine_UiTemplates.ModTemplatesPath, templates);
	}

	/// <summary>A whole screen record, by screen id.</summary>
	public string SetScreen(int screenId, Godot.Collections.Dictionary record)
	{
		Godot.Collections.Dictionary templates = ReadJson(Engine_UiTemplates.ModTemplatesPath);
		Section(templates, "screens")[screenId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = record;
		return WriteJson(Engine_UiTemplates.ModTemplatesPath, templates);
	}

	/// <summary>The menus a patch can name, as the last run registered them.</summary>
	public string[] ListMenus() => SnapshotKeys(Engine_UiTemplates.SnapshotPath, "menus");

	// ---------------------------------------------------------------------------------------
	// The runtime side
	// ---------------------------------------------------------------------------------------

	/// <summary>The contracts mod content runs against, by name — the Modding Guide's Script API page.</summary>
	public Godot.Collections.Dictionary DescribeRuntime() => new()
	{
		["mod_values"] = "A native hook publishes a float with refii::mods::Publish(\"<mod>.<name>\", v); " +
			"HUD pieces, copies and abilities read it as \"mod:<mod>.<name>\". The UI hands a hook a " +
			"setting with SetModSetting; the hook reads refii::mods::Setting(key, fallback).",
		["save"] = "Lua: what a mod puts in the table RefiiModSave persists with the save game (numbers, " +
			"strings, tables). Native code reads it back with refii_mod_game_lua_int when " +
			"refii_mod_lua_generation() changes.",
		["events"] = "core/refii_mod_events.h (included by core/refii_mod.h): the game's seams as typed events. " +
			"Native: static void OnHit(refii::mods::HitEvent& e) { ... } REFII_MOD_ON(Hit, OnHit); — e.cancel refuses " +
			"where the event allows it, writable fields (damage, type, level, amount) change what the game gets, " +
			"every handler sees every event in priority then load order. Lua (mod/init.lua): Refii.on(\"hit\", " +
			"function(e) ... return true end) — the same fields as a table, return true to cancel. " +
			"ListEvents(), DescribeEvent(name), NewNativeHandler(event, file), AddLuaHandler(event). Events: " +
			string.Join(", ", ModApi.Events.Select(e => e.Name)) + ".",
		["native"] = "core/refii_mod.h: REX_EXTERN(<config name>) { ... } overrides a guest function, " +
			"REFII_MOD_ORIGINAL(name) declares the game's own body as __imp__name; REFII_MOD_ON_TICK, " +
			"refii_mod_player_hero, refii_mod_local_hero, refii_mod_component, refii_mod_run_game_lua, " +
			"refii::mods::CallGuest/CallVirtual and the Load/Store helpers. Hook functions by their " +
			"config names, never sub_ addresses. Two mods hooking one function run as a chain in load " +
			"order (the higher mod's hook calls the lower's through __imp__name, the lowest calls the " +
			"game); a hook that never calls __imp__name must be the only one on its function.",
		["spellwheel"] = "ui/spellwheel.json: { \"secondary\": { \"setting\": <key>, " +
			"\"settingPlayerTwo\": <key>, \"value\": \"mod:<key>\", \"valuePlayerTwo\": \"mod:<key>\", \"chosen\": \"mod:<key>\", \"scale\": 0.55, \"exclude\": [2, 7], " +
			"\"unlocked\": \"mod:<key>\", \"apply\": <babel tag>, \"remove\": <babel tag> } } " +
			"— on both spell selectors X on the highlighted will writes its type (0 Lightning .. 7 " +
			"Dead Rising) to the setting and Y writes -1; the will the mod states back in " +
			"value is drawn smaller under every assigned slot's icon; exclude lists the will types X ignores; a {slot} in a key is the highlighted slot's number 1..5, for one pick per slot; " +
			"while the unlocked value is not positive the spell page says nothing of the pick; once it is, the abilities blade shows two prompt rows beside the page's own, X with the apply tag's text and Y with the remove tag's.",
		["fx"] = "fx/recipes.json: while a mod value equals a recipe's number, every live effect named in its " +
			"base list is dressed — a tint on each particle, intensity and size multipliers, and attached " +
			"effects (count copies on a ring of radius at height, turning at turnsPerSecond), and authored " +
			"pieces drawn with the game's own effect textures and meshes: embers (a spiral cone of particles), " +
			"arcs (re-struck lightning ribbons), ring (a repeating shockwave), blades (the cutlass mesh orbiting). " +
			"everyNode dresses every live anchor effect; entities (definition names or #hashes) dresses every live " +
			"entity of those definitions on a node that follows it; instances (a mod value prefix) binds each effect " +
			"and entity to the nearest stated instance of its look, so every cast carries its own. " +
			"The guest still states which effect plays and where; Godot changes the look. See Engine_FxRecipes " +
			"and Engine_FxRecipePieces.",
		["dependencies"] = "mod.toml: requires = [\"souls_combat >= 0.1\"] places that mod beneath this one " +
			"(mounted, composed and hooked inside it) and makes its native/include/ visible to this mod's " +
			"native code; after = [\"hd-ui\"] orders without requiring. A mod cannot be enabled while a " +
			"requirement is missing or off.",
		["lua"] = "mod/init.lua runs in the game VM after the shipped scripts, once per session; " +
			"mod/camera.lua after every camera-script load. Each mod runs in its own pcall; errors " +
			"land in the global RefiiModErrors.",
		["game_lua"] = "The game's own 3602 Lua natives in 234 tables (res://modding_tools/game_lua_api.tsv: " +
			"Player, Dog, Creature, GUI, Stats, Combat, Camera, SpellManager, Village, QuestTracker, the entity " +
			"userdata's EntityMetatable, the globals ...) are all callable from mod/init.lua. ListGameLuaTables(), " +
			"ListGameLuaNatives(table), FindGameLuaNatives(text).",
		["probe"] = "The live debug channel's refii_lua runs '@game <chunk>' in the game VM and reports " +
			"whether it ran; assert(<expr>) reads a value out.",
		["settings"] = "settings.json: a flat object of numbers keyed <mod>.<name> (one nesting level is " +
			"folded). Pushed to the game as mod settings when the guest comes up; native code reads " +
			"refii::mods::Setting(key, default). Live: refii_action \"mod-setting <key> <value>\".",
	};

	// ---------------------------------------------------------------------------------------

	private const string NoActiveMod = "no mod is active — create or choose one first";

	private string PackagePath()
	{
		string name = GetActiveMod();
		return name.Length == 0 ? string.Empty : Path.Combine(GetModsRoot(), name + ModWorkspace.PackageExtension);
	}

	private ModInstall? Install()
	{
		string name = GetActiveMod();
		return ModWorkspace.DiscoverModInstalls(GetModsRoot()).FirstOrDefault(m => m.Name == name);
	}

	private byte[]? Read(string path)
	{
		if (!F2ModPackage.TryOpen(PackagePath(), out F2ModPackage? package, out _) || package is null)
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

	private string Write(string path, byte[] bytes)
	{
		string package = PackagePath();
		if (package.Length == 0)
		{
			return NoActiveMod;
		}

		if (!File.Exists(package))
		{
			EditorModExporter.EnsureActiveModWorkspace();
		}

		if (!F2ModPackage.IncrementalSync(package, new[] { (ModLoadOrder.Normalize(path), bytes) }))
		{
			return $"could not write {path} into '{GetActiveMod()}'";
		}

		Changed();
		return string.Empty;
	}

	private string WriteLua(string path, string source)
	{
		string error = CheckLua(source);
		return error.Length != 0 ? error : WriteText(path, source);
	}

	/// <summary>Re-mounts the mods and tells the Studio, as its own editors do after a save.</summary>
	private void Changed()
	{
		BnkMountPlugin.Instance?.ReloadModOverlays();
		EditorModExporter.NotifyModContentChanged(GetActiveMod());
	}

	private Godot.Collections.Dictionary ReadJson(string path)
	{
		string text = ReadText(path);
		Variant parsed = text.Length == 0 ? default : Json.ParseString(text);
		return parsed.VariantType == Variant.Type.Dictionary ? parsed.AsGodotDictionary() : new Godot.Collections.Dictionary();
	}

	private string WriteJson(string path, Godot.Collections.Dictionary root) =>
		WriteText(path, Json.Stringify(root, "\t") + "\n");

	private string SetHidden(string id, bool hide)
	{
		Godot.Collections.Dictionary hud = ReadJson(Engine_HudModPieces.ModHudPath);
		Godot.Collections.Array list = List(hud, "hide");
		list.Remove(id);
		if (hide)
		{
			list.Add(id);
		}

		return WriteJson(Engine_HudModPieces.ModHudPath, hud);
	}

	private string Upsert(string path, string listKey, string idKey, Godot.Collections.Dictionary item)
	{
		string id = item.TryGetValue(idKey, out Variant v) ? v.AsString() : string.Empty;
		if (id.Length == 0)
		{
			return $"the entry needs \"{idKey}\"";
		}

		Godot.Collections.Dictionary root = ReadJson(path);
		Godot.Collections.Array list = List(root, listKey);
		RemoveByKey(list, idKey, id);
		list.Add(item);
		return WriteJson(path, root);
	}

	private string Remove(string path, string[] listKeys, string idKey, string id)
	{
		Godot.Collections.Dictionary root = ReadJson(path);
		foreach (string listKey in listKeys)
		{
			RemoveByKey(List(root, listKey), idKey, id);
		}

		return WriteJson(path, root);
	}

	/// <summary>The object at <paramref name="key"/>, made when missing.</summary>
	private static Godot.Collections.Dictionary Section(Godot.Collections.Dictionary root, string key)
	{
		if (root.TryGetValue(key, out Variant value) && value.VariantType == Variant.Type.Dictionary)
		{
			return value.AsGodotDictionary();
		}

		var section = new Godot.Collections.Dictionary();
		root[key] = section;
		return section;
	}

	/// <summary>The array at <paramref name="key"/>, made when missing.</summary>
	private static Godot.Collections.Array List(Godot.Collections.Dictionary root, string key)
	{
		if (root.TryGetValue(key, out Variant value) && value.VariantType == Variant.Type.Array)
		{
			return value.AsGodotArray();
		}

		var list = new Godot.Collections.Array();
		root[key] = list;
		return list;
	}

	private static void RemoveByKey(Godot.Collections.Array list, string key, string value)
	{
		for (int i = list.Count - 1; i >= 0; i--)
		{
			if (list[i].VariantType == Variant.Type.Dictionary &&
				list[i].AsGodotDictionary().TryGetValue(key, out Variant v) && v.AsString() == value)
			{
				list.RemoveAt(i);
			}
		}
	}

	private static string[] SnapshotKeys(string snapshot, string section)
	{
		using Godot.FileAccess? file = Godot.FileAccess.Open(snapshot, Godot.FileAccess.ModeFlags.Read);
		if (file is null)
		{
			return Array.Empty<string>();
		}

		Variant parsed = Json.ParseString(file.GetAsText());
		return parsed.VariantType == Variant.Type.Dictionary &&
			parsed.AsGodotDictionary().TryGetValue(section, out Variant keys) &&
			keys.VariantType == Variant.Type.Dictionary
				? keys.AsGodotDictionary().Keys.Select(k => k.AsString()).ToArray()
				: Array.Empty<string>();
	}

	private static string Quote(string text) =>
		text.IndexOfAny(new[] { ',', '"', '\n' }) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
}
#endif
