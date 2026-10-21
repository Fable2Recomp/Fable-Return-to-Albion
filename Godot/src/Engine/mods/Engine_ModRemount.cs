using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Refii.Assets;
using Refii.Host;
using Refii.Textures;

namespace Refii.Mods;

/// <summary>
/// The mods folded in again while a session runs, so a change made in the Mod Studio reaches the
/// running game without a relaunch: the mount's passes rebuild the tree on the mount's worker
/// (<see cref="Engine_GameAssets.Remount"/>), then everything those passes feed is reset on the
/// main thread, and the composed init chunk can be run in the game VM again.
/// </summary>
/// <remarks>
/// <para>LIVE: textures (refreshed in place in every material and menu holding them), babel text
/// Godot draws, Godot's database views (item, weapon, clothing and entity indexes), HUD pieces,
/// menu and screen templates, FX recipes, the spell and ability pages, settings (pushed again, the
/// player's choices and live overrides kept), input bindings, Godot scripts (restarted), and with
/// <c>lua</c> the mods' Lua.</para>
/// <para>RELAUNCH: models (a mesh already placed in the world or on a character is not rebuilt),
/// the guest's databases (it reads <c>.gdb</c> and <c>.save</c> once, at boot — Godot's views of
/// them refresh, gameplay does not), native code (compiled into the game module at launch), and
/// any other file the guest has already loaded.</para>
/// </remarks>
public static class ModRemount
{
	/// <summary>
	/// What the live Lua run starts with: the handlers the previous init registered are dropped, so
	/// running the init chunk again does not subscribe every handler twice.
	/// </summary>
	public const string LuaResetPrefix =
		"if Refii then Refii._handlers = {} Refii._masks = {} Refii._dirty = true end\n";

	/// <summary>What the live Lua run ends with: the event masks are read again even when no handler re-registered.</summary>
	public const string LuaResetSuffix = "\nif Refii then Refii._dirty = true end\n";

	/// <summary>One remount's account, for the debug channel.</summary>
	public sealed class Report
	{
		public bool Ok;

		public string Error = string.Empty;

		public string Warning = string.Empty;

		public string ModsRoot = string.Empty;

		public List<string> Mods = new();

		public int Files;

		public List<string> Changed = new();

		public List<string> Refreshed = new();

		/// <summary>What changed but only reaches the game on a relaunch, with why and which files.</summary>
		public List<(string Kind, string Reason, List<string> Paths)> Relaunch = new();

		public bool LuaRequested;

		public bool LuaQueued;

		public ulong LuaSeqBefore;

		public int LuaBytes;

		public string LuaNote = string.Empty;

		public Dictionary<string, object?> ToJson() => new()
		{
			["ok"] = Ok,
			["error"] = Error,
			["warning"] = Warning,
			["modsRoot"] = ModsRoot,
			["mods"] = Mods,
			["files"] = Files,
			["changed"] = Changed,
			["refreshed"] = Refreshed,
			["relaunch"] = Relaunch.Select(r => new Dictionary<string, object?>
			{
				["kind"] = r.Kind,
				["reason"] = r.Reason,
				["paths"] = r.Paths,
			}).ToList(),
			["lua"] = new Dictionary<string, object?>
			{
				["requested"] = LuaRequested,
				["queued"] = LuaQueued,
				["seqBefore"] = LuaSeqBefore,
				["bytes"] = LuaBytes,
				["note"] = LuaNote,
			},
		};
	}

	/// <summary>
	/// Starts a remount; <paramref name="done"/> runs on the main thread when it has finished.
	/// Call on the main thread. False with the reason when nothing is mounted or a mount is running.
	/// </summary>
	public static bool Start(bool lua, Action<Report> done, out string error)
	{
		error = string.Empty;
		if (Engine_GameAssets.Instance is not { } assets || !assets.IsMounted)
		{
			error = "the game's assets are not mounted";
			return false;
		}

		if (assets.IsMounting)
		{
			error = "a mount is already running";
			return false;
		}

		// THE SETTINGS FIRST, on this thread: the worker's Lua pass composes RefiiSettings from
		// them, and the reload marks them to be pushed again.
		ModSettings.Reload();
		_ = ModSettings.All;

		if (!assets.Remount(outcome =>
			{
				Report report;
				try
				{
					report = Finish(outcome, lua);
				}
				catch (Exception exception)
				{
					GD.PushError("[mods] remount: " + exception);
					report = new Report
					{
						Error = "the new mount is served, but refreshing what it feeds failed: " + exception.Message,
						ModsRoot = Engine_GameAssets.ModsRoot,
						Mods = outcome.Mods.ToList(),
						Changed = outcome.Changed.ToList(),
					};
				}

				done(report);
			}))
		{
			error = "a mount is already running";
			return false;
		}

		return true;
	}

	private static Report Finish(Engine_GameAssets.RemountOutcome outcome, bool lua)
	{
		var report = new Report
		{
			Ok = outcome.Ok,
			ModsRoot = Engine_GameAssets.ModsRoot,
			Mods = outcome.Mods.ToList(),
			Files = outcome.FileCount,
			Changed = outcome.Changed.ToList(),
			LuaRequested = lua,
		};

		if (!outcome.Ok)
		{
			report.Error = outcome.Failure.Length != 0
				? outcome.Failure + " — the previous mount is still served"
				: "the mount failed — the previous mount is still served";
			return report;
		}

		report.Warning = outcome.Failure;

		// The readers the main thread walks every frame, reset on it.
		ModInput.Reload();
		Refii.Fx.Engine_FxRecipes.Reload();
		Refii.Ui.Engine_UiSpellAssign.ReloadSecondary();
		Refii.Ui.Engine_UiModAbilities.Reload();
		Refii.Ui.Engine_UiTemplates.Reload();
		report.Refreshed.Add("settings (settings.json), pushed to the game again; the player's choices and live overrides stand");
		report.Refreshed.Add("input bindings (input.json)");
		report.Refreshed.Add("FX recipes (fx/recipes.json), re-dressed on the next frame");
		report.Refreshed.Add("spell and ability pages");
		report.Refreshed.Add("menu and screen templates (ui/templates.json); a screen already open takes them when it next opens");

		// Godot's views of the databases; the text bank was dropped when the loaders were rebound,
		// and the menus' string book reloads on the mount's Mounted signal.
		Refii.Gdb.Engine_GameGdb.Invalidate();
		Refii.Equipment.Engine_ItemIndex.Invalidate();
		Refii.Equipment.Engine_WeaponIndex.Invalidate();
		Refii.Equipment.Engine_ClothingIndex.Invalidate();
		Refii.Entities.Engine_EntityIndex.Invalidate();
		report.Refreshed.Add("text Godot draws (babel rows: menus, HUD, item names)");
		report.Refreshed.Add("Godot's database views (item, weapon, clothing and entity indexes)");

		if (Refii.Ui.Engine_HudView.Current is { } hud && GodotObject.IsInstanceValid(hud))
		{
			hud.Build();
			report.Refreshed.Add("HUD pieces (ui/hud.json), rebuilt");
		}

		if (Refii.Game.GameSession.Current is { } session && GodotObject.IsInstanceValid(session) &&
			HostBridge.IsGuestUp)
		{
			ModGodot.Start(session);
			report.Refreshed.Add("Godot scripts (godot/*.gd), restarted");
		}

		// The presentations: the game's UI host compared each pattern book it holds with the new
		// mount's files when the mount was emitted (Engine_UiPresentationHost.RefreshPatternBooks)
		// and rebuilt the changed ones, and the views built from them.
		var presentations = report.Changed.Where(IsPresentationFile).ToList();
		if (presentations.Count != 0)
		{
			report.Refreshed.Add($"{presentations.Count} UI presentation file(s) (.bgf/.fac/.bsg): their pattern books " +
				"and the menus, boxes, books and HUD art built from them, rebuilt");
		}

		if (Refii.Fx.Engine_FxModEffects.ReloadGuestBank(report.Changed))
		{
			report.Refreshed.Add("the guest's particle bank (fx/effects), read again by the guest's own loader on its next tick: " +
				"every spawn after it takes the new effects, live ones finish with the old");
		}

		if (ModLua.RefreshFrontend(report.Changed))
		{
			report.Refreshed.Add("the mods' front-end Lua (mod/frontend.lua), run again in the front end's presentation before its next call");
		}

		var textures = report.Changed.Where(p => p.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)).ToList();
		if (textures.Count != 0)
		{
			report.Refreshed.Add($"{RefreshTextures(textures)} texture(s) refreshed in place " +
				$"({textures.Count} changed path(s))");
		}

		Classify(report);

		if (lua)
		{
			RunLua(report);
		}
		else if (report.Changed.Any(IsModLuaKey))
		{
			report.Relaunch.Add(("mod Lua",
				"init.lua or camera.lua changed; \"Reload mods + Lua\" runs it now, otherwise it runs at the next session",
				report.Changed.Where(IsModLuaKey).ToList()));
		}

		return report;
	}

	/// <summary>The changed paths that only reach the game on a relaunch, by kind.</summary>
	private static void Classify(Report report)
	{
		var models = new List<string>();
		var databases = new List<string>();
		var other = new List<string>();
		foreach (string path in report.Changed)
		{
			string lower = path.ToLowerInvariant();
			if (lower.EndsWith(".tex") || lower.EndsWith(".babel") || IsModLuaKey(path) || IsPresentationFile(path) ||
				Refii.Fx.Engine_FxModEffects.IsGuestBankFile(path) || ModLua.IsFrontendKey(path))
			{
				continue;
			}

			if (lower.EndsWith(".mdl"))
			{
				models.Add(path);
			}
			else if (lower.EndsWith(".gdb") || lower.EndsWith(".gdb.tu") ||
				lower.EndsWith(".save") || lower.EndsWith(".save.tu"))
			{
				databases.Add(path);
			}
			else
			{
				other.Add(path);
			}
		}

		if (models.Count != 0)
		{
			report.Relaunch.Add(("models",
				"a mesh already placed in the world or on a character is not rebuilt; relaunch to see it", models));
		}

		if (databases.Count != 0)
		{
			report.Relaunch.Add(("guest databases",
				"the guest reads its databases once, at boot: Godot's views of them (names, inventory) refresh now, " +
				"gameplay values need a relaunch", databases));
		}

		if (other.Count != 0)
		{
			report.Relaunch.Add(("other files",
				"served from now on; a file the guest has already loaded stays loaded until a relaunch", other));
		}
	}

	/// <summary>A Gameface presentation's file: the UI host rebuilds what it built from one.</summary>
	private static bool IsPresentationFile(string path) =>
		path.EndsWith(".bgf", StringComparison.OrdinalIgnoreCase) ||
		path.EndsWith(".fac", StringComparison.OrdinalIgnoreCase) ||
		path.EndsWith(".bsg", StringComparison.OrdinalIgnoreCase);

	private static bool IsModLuaKey(string path) =>
		path.Equals(ModLua.InitKey, StringComparison.OrdinalIgnoreCase) ||
		path.Equals(ModLua.CameraKey, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Every live texture decoded from <paramref name="paths"/>, given the mount's bytes in place:
	/// the library's own entries, then whatever Godot's resource cache holds for the
	/// <c>game://</c> spelling. Returns how many textures took new bytes.
	/// </summary>
	private static int RefreshTextures(IReadOnlyList<string> paths)
	{
		var done = new HashSet<ImageTexture>(Engine_TexLibrary.Refresh(paths));
		foreach (string path in paths)
		{
			foreach (string spelling in new[] { path, path.ToLowerInvariant() }.Distinct())
			{
				string key = Engine_GameAssets.PathPrefix + spelling;
				if (!ResourceLoader.HasCached(key) ||
					ResourceLoader.GetCachedRef(key) is not ImageTexture texture ||
					done.Contains(texture))
				{
					continue;
				}

				Image? image = Engine_TexLibrary.Decode(
					spelling, TexSemantic.Ui, Engine_BnkTextureLoader.ReadBytes, out _);
				if (image is null || image.IsEmpty())
				{
					continue;
				}

				texture.SetImage(image);
				done.Add(texture);
				Engine_TexLibrary.TrackUi(spelling, texture, Engine_BnkTextureLoader.ReadBytes);
			}
		}

		return done.Count;
	}

	/// <summary>
	/// The composed init chunk, as the new mount serves it, run in the game VM behind
	/// <see cref="LuaResetPrefix"/>; the camera chunk joins it once a world is up, since the camera
	/// scripts it patches exist only then. Queued on the guest's tick; the outcome is the first
	/// debug-call result numbered after <see cref="Report.LuaSeqBefore"/>.
	/// </summary>
	private static void RunLua(Report report)
	{
		if (!HostBridge.IsGuestUp)
		{
			report.LuaNote = "the guest is not up; the mods' Lua runs when the next session starts";
			return;
		}

		Engine_GameAssets? assets = Engine_GameAssets.Instance;
		string init = Read(assets, ModLua.InitKey);
		bool world = Refii.Ui.Engine_UiSource.Current.GetInGameScreens() is not null;
		string camera = world ? Read(assets, ModLua.CameraKey) : string.Empty;

		var chunk = new StringBuilder(LuaResetPrefix);
		chunk.Append(init).Append('\n').Append(camera).Append(LuaResetSuffix);
		string text = chunk.ToString();

		report.LuaBytes = Encoding.UTF8.GetByteCount(text);
		report.LuaSeqBefore = HostBridge.DebugCallResult()?.Seq ?? 0;
		report.LuaQueued = HostBridge.PostFrontEndAction("debug-gamelua:" + text) != 0;
		report.LuaNote =
			(init.Length == 0 ? "no mod Lua is composed; the handlers were cleared" : "init chunk") +
			(camera.Length != 0 ? " and camera chunk" : world ? string.Empty : " (camera chunk skipped: no world is up)") +
			" queued on the guest tick; init.lua runs again over the live state, so a mod that wraps a " +
			"shipped function should guard the wrap";
		if (!report.LuaQueued)
		{
			report.LuaNote = "the guest tick pump refused the chunk";
		}
	}

	private static string Read(Engine_GameAssets? assets, string key) =>
		assets?.ReadAllBytes(key) is { Length: > 0 } bytes ? Encoding.UTF8.GetString(bytes) : string.Empty;
}
