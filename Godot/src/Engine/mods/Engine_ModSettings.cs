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

	private static Dictionary<string, float>? _values;

	/// <summary>Values set live this session, pushed over the files' on every push.</summary>
	private static readonly Dictionary<string, float> _overrides = new(StringComparer.Ordinal);

	private static bool _pushed;

	/// <summary>Every setting the enabled mods state, by key.</summary>
	public static IReadOnlyDictionary<string, float> All => _values ??= Load();

	/// <summary>Drops the loaded set; the next push reads the files again (the live overrides stay).</summary>
	public static void Reload()
	{
		_values = null;
		_pushed = false;
	}

	/// <summary>Sets a mod setting in the running game and keeps it over the file's value for the session.</summary>
	public static void Set(string key, float value)
	{
		_overrides[key] = value;
		HostBridge.SetModSetting(key, value);
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
		foreach ((string mod, string text) in ModEntries.ReadEnabledText(ModSettingsPath))
		{
			Variant parsed = Json.ParseString(text);
			if (parsed.VariantType != Variant.Type.Dictionary)
			{
				GD.PushWarning($"[mod-settings] {mod}:{ModSettingsPath} is not a JSON object; ignored");
				continue;
			}

			foreach (KeyValuePair<Variant, Variant> entry in parsed.AsGodotDictionary())
			{
				string key = entry.Key.AsString();
				if (entry.Value.VariantType == Variant.Type.Dictionary)
				{
					foreach (KeyValuePair<Variant, Variant> inner in entry.Value.AsGodotDictionary())
					{
						Take(values, mod, key + "." + inner.Key.AsString(), inner.Value);
					}
				}
				else
				{
					Take(values, mod, key, entry.Value);
				}
			}
		}

		return values;
	}

	private static void Take(Dictionary<string, float> values, string mod, string key, Variant value)
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
				break;
		}
	}
}
