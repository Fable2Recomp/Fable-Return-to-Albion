using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Refii.Host;

namespace Refii.Mods;

/// <summary>
/// A mod's tunables: <c>settings.json</c> in the package, a flat object of number values keyed
/// <c>&lt;mod&gt;.&lt;name&gt;</c> (one level of nesting is folded with a dot). Pushed to the game as
/// mod settings once the guest is up, and again after the mods are re-mounted, so a mod's
/// native code reads <c>refii::mods::Setting(key, fallback)</c> and sees the file's value. A
/// value set LIVE through <see cref="Set"/> (a partner pick, the abilities page's purchase
/// counter, the debug channel's <c>mod-setting</c> action) is an override: it is pushed over the
/// file's value on every push, so a re-mount or a reload never puts the file's default back over
/// the player's choice — a mod that treats a change of the setting as the choice (Spell
/// Weaving's partners) would otherwise see every partner cleared. Later mods in the load order
/// override earlier ones' keys.
/// </summary>
public static class ModSettings
{
	public const string ModSettingsPath = "settings.json";

	/// <summary>
	/// THE PLAYER'S CHOICES, from the options screen's Mods pages: a flat object of the keys
	/// they changed. Applied over the files' values whenever they are loaded, so every reader of
	/// <see cref="All"/> — the push, the Lua table — sees the player's value; written on every
	/// change. A key a mod no longer states is kept, so a mod disabled and enabled again keeps
	/// its tuning.
	/// </summary>
	public const string PlayerSettingsPath = "user://mod_settings.json";

	private static Dictionary<string, float>? _values;

	private static Dictionary<string, float>? _defaults;

	private static Dictionary<string, float>? _player;

	/// <summary>Values set live this session, pushed over the files' on every push.</summary>
	private static readonly Dictionary<string, float> _overrides = new(StringComparer.Ordinal);

	private static bool _pushed;

	/// <summary>Every setting the enabled mods state, by key, with the player's choices over the files'.</summary>
	public static IReadOnlyDictionary<string, float> All => _values ??= Load();

	/// <summary>The files' values alone — a mod's defaults, for a page to size a range around.</summary>
	public static IReadOnlyDictionary<string, float> Defaults
	{
		get
		{
			_ = All;
			return _defaults!;
		}
	}

	/// <summary>The player's saved choices, by key.</summary>
	public static IReadOnlyDictionary<string, float> Player => _player ??= LoadPlayer();

	/// <summary>Which mod stated each key — the last in the load order to, when several do.</summary>
	public static IReadOnlyDictionary<string, string> Owners
	{
		get
		{
			_ = All;
			return _owners;
		}
	}

	private static readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);

	/// <summary>Drops the loaded set; the next push reads the files again (the live overrides stay).</summary>
	public static void Reload()
	{
		_values = null;
		_defaults = null;
		_pushed = false;
	}

	/// <summary>
	/// The player's choice: in force now like <see cref="Set"/>, and saved to
	/// <see cref="PlayerSettingsPath"/> so it stands over the file's value in every later session.
	/// </summary>
	public static void Persist(string key, float value)
	{
		_ = Player;
		_player![key] = value;
		if (_values is not null)
		{
			_values[key] = value;
		}

		Set(key, value);
		SavePlayer();
	}

	/// <summary>The value in force for a key: the live override first, then the files'.</summary>
	public static bool TryGet(string key, out float value) =>
		_overrides.TryGetValue(key, out value) || All.TryGetValue(key, out value);

	/// <summary>
	/// Every key in force with its value — what the push writes: the files' set with the live
	/// overrides over it, then the overrides no file states. The Lua preamble composes its
	/// <c>RefiiSettings</c> from this, so a chunk run after a live change starts from that change.
	/// </summary>
	public static IEnumerable<(string Key, float Value)> InForce
	{
		get
		{
			foreach ((string key, float value) in All)
			{
				yield return (key, _overrides.TryGetValue(key, out float live) ? live : value);
			}

			foreach ((string key, float value) in _overrides)
			{
				if (!All.ContainsKey(key))
				{
					yield return (key, value);
				}
			}
		}
	}

	/// <summary>
	/// Sets a mod setting in the running game and keeps it over the file's value for the session.
	/// THE ONE SEAM A LIVE CHANGE CROSSES — the Options › Mods page (<see cref="Persist"/>), a Godot
	/// script's <c>set_setting</c>, the debug channel's <c>mod-setting</c>: the native table takes
	/// it at once, and the running Lua's <c>RefiiSettings</c> takes it at the next guest tick
	/// (<see cref="LuaAssignment"/>, a <c>run_lua</c> command; on the title the front end's state
	/// instead), so <c>Refii.setting</c> answers the new value without a reload.
	/// </summary>
	public static void Set(string key, float value)
	{
		_overrides[key] = value;
		HostBridge.SetModSetting(key, value);
		string assignment = LuaAssignment(key, value);
		if (assignment.Length != 0)
		{
			HostBridge.PostModCommand("run_lua " + assignment);
			if (!HostBridge.IsInGame)
			{
				HostBridge.PostModCommand(ModLua.FrontendCommand + assignment);
			}
		}
	}

	/// <summary>
	/// The Lua statement that puts a setting into a running VM's <c>RefiiSettings</c>, one line (the
	/// command line holds it), or empty for a key no Lua string can carry.
	/// </summary>
	public static string LuaAssignment(string key, float value)
	{
		if (string.IsNullOrEmpty(key) || key.IndexOfAny(new[] { '\n', '\r', '\0' }) >= 0)
		{
			return string.Empty;
		}

		string escaped = key.Replace("\\", "\\\\").Replace("\"", "\\\"");
		string number = float.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : "0";
		return "if RefiiSettings then RefiiSettings[\"" + escaped + "\"] = " + number + " end";
	}

	/// <summary>Pushes the settings once per guest; call every frame with whether the guest is up.</summary>
	public static void Pump(bool guestUp)
	{
		if (!guestUp)
		{
			_pushed = false;
			return;
		}

		if (_pushed)
		{
			return;
		}

		foreach ((string key, float value) in All)
		{
			HostBridge.SetModSetting(key, _overrides.TryGetValue(key, out float live) ? live : value);
		}

		foreach ((string key, float value) in _overrides)
		{
			if (!All.ContainsKey(key))
			{
				HostBridge.SetModSetting(key, value);
			}
		}

		_pushed = true;
		if (All.Count != 0)
		{
			GD.Print($"[mod-settings] {All.Count} setting(s) pushed");
		}
	}

	private static Dictionary<string, float> Load()
	{
		var values = new Dictionary<string, float>(StringComparer.Ordinal);
		_owners.Clear();
		foreach ((string mod, string text) in ModEntries.ReadEnabledText(ModSettingsPath))
		{
			Fold(values, mod, text, owned: true);
		}

		// The files' values are the defaults; the player's choices stand over them.
		_defaults = new Dictionary<string, float>(values, StringComparer.Ordinal);
		foreach ((string key, float value) in Player)
		{
			values[key] = value;
		}

		return values;
	}

	private static Dictionary<string, float> LoadPlayer()
	{
		var values = new Dictionary<string, float>(StringComparer.Ordinal);
		if (!FileAccess.FileExists(PlayerSettingsPath))
		{
			return values;
		}

		using FileAccess? file = FileAccess.Open(PlayerSettingsPath, FileAccess.ModeFlags.Read);
		if (file is null)
		{
			return values;
		}

		Variant parsed = Json.ParseString(file.GetAsText());
		if (parsed.VariantType != Variant.Type.Dictionary)
		{
			GD.PushWarning($"[mod-settings] {PlayerSettingsPath} is not a JSON object; ignored");
			return values;
		}

		foreach (KeyValuePair<Variant, Variant> entry in parsed.AsGodotDictionary())
		{
			Take(values, "player", entry.Key.AsString(), entry.Value, owned: false);
		}

		return values;
	}

	private static void SavePlayer()
	{
		var root = new Godot.Collections.Dictionary();
		foreach ((string key, float value) in Player)
		{
			root[key] = value;
		}

		using FileAccess? file = FileAccess.Open(PlayerSettingsPath, FileAccess.ModeFlags.Write);
		if (file is null)
		{
			GD.PushWarning($"[mod-settings] cannot write '{PlayerSettingsPath}': {FileAccess.GetOpenError()}");
			return;
		}

		file.StoreString(Json.Stringify(root, "\t"));
	}

	/// <summary>
	/// One mod's <c>settings.json</c> as numbers by key, one nesting level folded into dotted keys,
	/// as the game reads it — for an editor's preview of a file it is editing. Nothing is recorded
	/// as owned.
	/// </summary>
	public static Dictionary<string, float> Parse(string mod, string text)
	{
		var values = new Dictionary<string, float>(StringComparer.Ordinal);
		Fold(values, mod, text, owned: false);
		return values;
	}

	private static void Fold(Dictionary<string, float> values, string mod, string text, bool owned)
	{
		Variant parsed = Json.ParseString(text);
		if (parsed.VariantType != Variant.Type.Dictionary)
		{
			GD.PushWarning($"[mod-settings] {mod}:{ModSettingsPath} is not a JSON object; ignored");
			return;
		}

		foreach (KeyValuePair<Variant, Variant> entry in parsed.AsGodotDictionary())
		{
			string key = entry.Key.AsString();
			if (entry.Value.VariantType == Variant.Type.Dictionary)
			{
				foreach (KeyValuePair<Variant, Variant> inner in entry.Value.AsGodotDictionary())
				{
					Take(values, mod, key + "." + inner.Key.AsString(), inner.Value, owned);
				}
			}
			else
			{
				Take(values, mod, key, entry.Value, owned);
			}
		}
	}

	private static void Take(Dictionary<string, float> values, string mod, string key, Variant value, bool owned = true)
	{
		switch (value.VariantType)
		{
			case Variant.Type.Float:
			case Variant.Type.Int:
				values[key] = (float)value.AsDouble();
				break;
			case Variant.Type.Bool:
				values[key] = value.AsBool() ? 1f : 0f;
				break;
			case Variant.Type.String when float.TryParse(value.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed):
				values[key] = parsed;
				break;
			default:
				GD.PushWarning($"[mod-settings] {mod}: '{key}' is not a number; ignored");
				return;
		}

		// The player's file states no owner; a mod's does.
		if (owned)
		{
			_owners[key] = mod;
		}
	}
}
