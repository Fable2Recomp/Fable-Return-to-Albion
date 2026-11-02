using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Refii.Mods;

/// <summary>
/// LUA AND NATIVE CODE TALKING TO GODOT ON THE SAME MACHINE: a mod's signals
/// (<c>Refii.signal(name, payload)</c>, <c>refii_mod_signal</c>), raised by RefiiHost as
/// <c>mod_signal(mod, name, payload)</c>, and its published texts (<c>Refii.publish_text</c>,
/// <c>refii_mod_publish_text</c>, <c>RefiiHost.publish_text</c>), read with <c>get_text(key)</c>, a
/// change raising <c>value_changed(key, text)</c>. The same-machine twin of
/// <c>send_to_partner</c>; the payload's records cross on the host-only UI kind with the text
/// base64, so it arrives exactly.
/// </summary>
/// <remarks>
/// A name or key is scoped <c>&lt;mod&gt;.&lt;name&gt;</c> (letters, digits, <c>_ . -</c>, at most
/// <see cref="MaxNameLength"/>); a signal's <c>mod</c> is the part before the first dot. A payload
/// or text is at most <see cref="MaxTextBytes"/> bytes (the payload cuts longer with a mod error).
/// The text store keeps at most <see cref="MaxTexts"/> keys for the process, as the published
/// numbers are kept; a new key past that is refused with a warning. Pure: held by tests.
/// </remarks>
public static class ModSignals
{
	/// <summary>REFII_MOD_SIGNAL_NAME_MAX.</summary>
	public const int MaxNameLength = 96;

	/// <summary>REFII_MOD_SIGNAL_PAYLOAD_MAX.</summary>
	public const int MaxTextBytes = 1024;

	/// <summary>How many published-text keys the store keeps.</summary>
	public const int MaxTexts = 4096;

	public enum Op
	{
		Signal,
		PublishText,
	}

	/// <summary>One record off the bus: a signal (<see cref="Key"/> its scoped name, <see cref="Text"/> the payload) or a published text.</summary>
	public readonly record struct Request(Op Op, string Key, string Text);

	private static readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);

	/// <summary>The request in <paramref name="fields"/>, or null when the record is not a signal or a published text (or is malformed).</summary>
	public static Request? Parse(IEnumerable<KeyValuePair<string, object>> fields)
	{
		string op = string.Empty, name = string.Empty, key = string.Empty, payload = string.Empty, text = string.Empty;
		foreach ((string field, object value) in fields)
		{
			string s = value as string ?? string.Empty;
			switch (field)
			{
				case "op": op = s; break;
				case "name": name = s; break;
				case "key": key = s; break;
				case "payload": payload = s; break;
				case "text": text = s; break;
			}
		}

		switch (op)
		{
			case "signal":
				return ValidName(name) && Unbase(payload) is { } decodedPayload
					? new Request(Op.Signal, name, decodedPayload)
					: null;
			case "publish_text":
				return ValidName(key) && Unbase(text) is { } decodedText
					? new Request(Op.PublishText, key, decodedText)
					: null;
			default:
				return null;
		}
	}

	/// <summary>Whether a record's fields are one of this class's ops (so the UI dispatcher leaves it alone).</summary>
	public static bool IsOurs(IEnumerable<KeyValuePair<string, object>> fields)
	{
		foreach ((string field, object value) in fields)
		{
			if (field == "op")
			{
				return value is "signal" or "publish_text";
			}
		}

		return false;
	}

	/// <summary>A scoped name: <c>&lt;mod&gt;.&lt;name&gt;</c>, a non-empty mod, the API's word characters, at most <see cref="MaxNameLength"/>.</summary>
	public static bool ValidName(string? name)
	{
		if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name[0] == '.' || name.IndexOf('.') < 0)
		{
			return false;
		}

		foreach (char c in name)
		{
			if (!(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '.' or '-'))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>A scoped name split at its first dot: the mod, then the rest.</summary>
	public static (string Mod, string Name) Split(string scoped)
	{
		int dot = scoped.IndexOf('.');
		return dot <= 0 ? (string.Empty, scoped) : (scoped[..dot], scoped[(dot + 1)..]);
	}

	/// <summary>
	/// Stores a published text; <paramref name="changed"/> when it differs from the last one under
	/// the key (a new key counts). False for a bad key, a text over <see cref="MaxTextBytes"/>, or a
	/// new key past <see cref="MaxTexts"/>.
	/// </summary>
	public static bool SetText(string key, string text, out bool changed)
	{
		changed = false;
		if (!ValidName(key) || Encoding.UTF8.GetByteCount(text ?? string.Empty) > MaxTextBytes)
		{
			return false;
		}

		if (_texts.TryGetValue(key, out string? was))
		{
			changed = !string.Equals(was, text, StringComparison.Ordinal);
		}
		else if (_texts.Count >= MaxTexts)
		{
			return false;
		}
		else
		{
			changed = true;
		}

		_texts[key] = text ?? string.Empty;
		return true;
	}

	/// <summary>The last text published under <paramref name="key"/>, or null when none has been.</summary>
	public static string? Text(string key) => _texts.TryGetValue(key ?? string.Empty, out string? text) ? text : null;

	/// <summary>Every key a text is published under, in name order.</summary>
	public static List<string> TextKeys()
	{
		var keys = new List<string>(_texts.Keys);
		keys.Sort(StringComparer.Ordinal);
		return keys;
	}

	/// <summary>Forgets every published text (a test's reset; the game keeps them for the process).</summary>
	public static void ClearTexts() => _texts.Clear();

	/// <summary>
	/// A published-number change record (REFII_MOD_HOST_VALUE_KIND, <c>key='…',value=n</c>) as its key
	/// and value; false when it is not one.
	/// </summary>
	public static bool TryValueChange(IEnumerable<KeyValuePair<string, object>> fields, out string key, out double value)
	{
		key = string.Empty;
		value = double.NaN;
		bool hasKey = false, hasValue = false;
		foreach ((string field, object v) in fields)
		{
			if (field == "key" && v is string k && k.Length != 0)
			{
				key = k;
				hasKey = true;
			}
			else if (field == "value")
			{
				switch (v)
				{
					case long l: value = l; hasValue = true; break;
					case double d: value = d; hasValue = true; break;
					case string s when s.Equals("nan", StringComparison.OrdinalIgnoreCase) || s.Equals("-nan", StringComparison.OrdinalIgnoreCase):
						value = double.NaN;
						hasValue = true;
						break;
					case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed):
						value = parsed;
						hasValue = true;
						break;
				}
			}
		}

		return hasKey && hasValue;
	}

	/// <summary>Standard base64 to its UTF-8 text; empty is empty; null when it is not base64.</summary>
	public static string? Unbase(string text)
	{
		if (text.Length == 0)
		{
			return string.Empty;
		}

		try
		{
			return Encoding.UTF8.GetString(Convert.FromBase64String(text));
		}
		catch (FormatException)
		{
			return null;
		}
	}
}
