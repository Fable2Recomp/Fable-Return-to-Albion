#if TOOLS
using Godot;
using Refii.Mods;

namespace Refii.EditorTools;

/// <summary>
/// Input actions — <c>input.json</c>, read by <see cref="ModInput"/>. One entry per action, keyed by
/// the action's unscoped name; the game scopes it <c>&lt;mod&gt;.&lt;action&gt;</c> and raises
/// <c>input_action</c> for each press and release.
/// </summary>
public partial class RefiiModApi
{
	/// <summary>
	/// Declares or replaces an input action. <paramref name="binding"/> is the file's entry:
	/// <c>label</c> (string), <c>keys</c> (Godot key names), <c>buttons</c> (JoyButton names or
	/// A/B/X/Y/LB/RB/L3/R3/LT/RT/Start/Back), <c>axis</c> (a JoyAxis name, optionally signed, or
	/// null). Returns an empty string, or the first name that is not a key, button or axis.
	/// </summary>
	public string SetInputAction(string action, Godot.Collections.Dictionary binding)
	{
		action = action.Trim();
		if (action.Length == 0)
		{
			return "an input action needs a name";
		}

		string error = CheckBinding(binding);
		if (error.Length != 0)
		{
			return error;
		}

		Godot.Collections.Dictionary input = ReadJson(ModInput.ModInputPath);
		input[action] = binding;
		return WriteJson(ModInput.ModInputPath, input);
	}

	/// <summary>Removes an input action from the file; no error when it is not there.</summary>
	public string RemoveInputAction(string action)
	{
		Godot.Collections.Dictionary input = ReadJson(ModInput.ModInputPath);
		input.Remove(action.Trim());
		return WriteJson(ModInput.ModInputPath, input);
	}

	private static string CheckBinding(Godot.Collections.Dictionary binding)
	{
		foreach (string key in Strings(binding, "keys"))
		{
			if (OS.FindKeycodeFromString(key) == Key.None)
			{
				return $"'{key}' is not a Godot key name";
			}
		}

		foreach (string button in Strings(binding, "buttons"))
		{
			if (!ModInput.TryButton(button, out _) && !ModInput.TryTrigger(button, out _))
			{
				return $"'{button}' is not a pad button (JoyButton name or A/B/X/Y/LB/RB/L3/R3/LT/RT/Start/Back)";
			}
		}

		if (binding.TryGetValue("axis", out Variant axis) && axis.VariantType == Variant.Type.String)
		{
			string name = axis.AsString().Trim().TrimStart('+', '-');
			if (name.Length != 0 && (!System.Enum.TryParse(name, true, out JoyAxis parsed) || parsed == JoyAxis.Invalid))
			{
				return $"'{axis.AsString()}' is not a JoyAxis name";
			}
		}

		// The chord: LT / RT / LB / RB, or a key the keyboard holds.
		if (binding.TryGetValue("chord", out Variant chord) && chord.VariantType == Variant.Type.String)
		{
			string name = chord.AsString().Trim();
			if (name.Length != 0 && ModInputClaims.ParseModifier(name) == ModInputClaims.Modifier.Key &&
				OS.FindKeycodeFromString(name) == Key.None)
			{
				return $"'{name}' is neither LT, RT, LB, RB nor a Godot key name (the chord)";
			}
		}

		return string.Empty;
	}

	private static System.Collections.Generic.IEnumerable<string> Strings(Godot.Collections.Dictionary binding, string field)
	{
		if (!binding.TryGetValue(field, out Variant value))
		{
			yield break;
		}

		if (value.VariantType == Variant.Type.String)
		{
			yield return value.AsString();
		}
		else if (value.VariantType == Variant.Type.Array)
		{
			foreach (Variant item in value.AsGodotArray())
			{
				if (item.VariantType == Variant.Type.String)
				{
					yield return item.AsString();
				}
			}
		}
	}
}
#endif
