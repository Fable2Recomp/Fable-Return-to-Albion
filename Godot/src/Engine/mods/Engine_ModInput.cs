using System;
using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Mods;

/// <summary>
/// A mod's own input actions: <c>input.json</c> in the package, an object keyed by action name
/// whose values bind keys (Godot key names), pad buttons (<see cref="JoyButton"/> names or the
/// Xbox aliases A/B/X/Y/LB/RB/L3/R3/LT/RT/Start/Back) and at most one pad axis. Actions are
/// scoped <c>&lt;mod&gt;.&lt;action&gt;</c>; each is also a Godot <see cref="InputMap"/> action
/// <c>mod.&lt;mod&gt;.&lt;action&gt;</c> so the Godot side sees it.
/// </summary>
/// <remarks>
/// Polled once per frame from <c>GameInput._Process</c>, after the pads are assigned, with the
/// same focus gate the pads have: an unfocused window reads every device as released. Player 1
/// is the keyboard plus the joypad holding guest pad 0; player 2 is the joypad holding guest
/// pad 1 (<c>GameInput.JoypadForPad</c>) — the pad the local drop-in binds. Only EDGES reach the
/// guest: <see cref="HostBridge.PostModInput"/> is called once when an action goes down and once
/// when it comes up, per player, and the mod event bus raises <c>input_action</c>
/// (<c>name</c>, <c>player</c>, <c>pressed</c>) for each. An action that is still down when its
/// set reloads, or when the devices lose focus, is released exactly once.
/// <para>THE PLAYER'S REBINDS STAND OVER THE FILE'S, where the shipped keys' do: refii.toml, as
/// <c>key_&lt;mod&gt;.&lt;action&gt;</c> (a comma list of Godot key names) and
/// <c>pad_&lt;mod&gt;.&lt;action&gt;</c> (a comma list of pad button names or the Xbox aliases,
/// <c>LT</c> / <c>RT</c> the triggers); <c>none</c> unbinds. A key absent or empty leaves the
/// declaration in force. Read with the shipped keys (<see cref="ReadBindings"/>, from
/// <c>GameInput.ReadControlSettings</c>, so a launcher edit lands within two seconds) and written
/// by the Options › Controls page (<see cref="ModInputUi"/>). An axis is never rebound.</para>
/// </remarks>
public static class ModInput
{
	public const string ModInputPath = "input.json";

	/// <summary>The InputMap prefix every mod action is registered under.</summary>
	public const string ActionPrefix = "mod.";

	/// <summary>A trigger counts as pressed past this much of its travel; a stick axis past this deflection.</summary>
	public const float AxisThreshold = 0.5f;

	/// <summary>
	/// <c>InputEvent.DEVICE_ID_ALL_DEVICES</c>: Godot 4.3+ matches an InputMap event with this
	/// device against every joypad (-1 is the emulation id there, not "all").
	/// </summary>
	private const int AllDevices = -3;

	/// <summary>The refii.toml keys a rebind lives under, before the scoped action name.</summary>
	public const string KeyPrefix = "key_";

	public const string PadPrefix = "pad_";

	/// <summary>The stored value that unbinds a device from an action.</summary>
	public const string Unbound = "none";

	/// <summary>One declared action, as the file states it (the mod's defaults; <see cref="EffectiveKeys"/> is what is read).</summary>
	public sealed record Action(
		string Name,
		string Mod,
		string Label,
		IReadOnlyList<Key> Keys,
		IReadOnlyList<JoyButton> Buttons,
		IReadOnlyList<JoyAxis> Triggers,
		JoyAxis? Axis,
		int AxisSign)
	{
		/// <summary>The Godot InputMap action name.</summary>
		public StringName InputMapAction => ActionPrefix + Name;
	}

	private static List<Action>? _actions;

	/// <summary>The InputMap actions this class registered, erased before the next load.</summary>
	private static readonly List<StringName> _registered = new();

	/// <summary>Down state as last posted, keyed by (action name, player).</summary>
	private static readonly Dictionary<(string, int), bool> _held = new();

	/// <summary>The player's key rebinds by action name (an empty list is "none").</summary>
	private static readonly Dictionary<string, List<Key>> _keyRebinds = new(StringComparer.Ordinal);

	/// <summary>The player's pad rebinds by action name (both empty is "none").</summary>
	private static readonly Dictionary<string, (List<JoyButton> Buttons, List<JoyAxis> Triggers)> _padRebinds =
		new(StringComparer.Ordinal);

	/// <summary>The control settings last read, applied again when the actions reload.</summary>
	private static IReadOnlyDictionary<string, string>? _lastValues;

	/// <summary>Every action the enabled mods declare, in load order.</summary>
	public static IReadOnlyList<Action> All => _actions ??= Load();

	/// <summary>Drops the loaded set; the next pump reads the files and registers the actions again.</summary>
	public static void Reload()
	{
		_actions = null;
	}

	/// <summary>The refii.toml key holding an action's keyboard rebind.</summary>
	public static string KeySettingKey(Action action) => KeyPrefix + action.Name;

	/// <summary>The refii.toml key holding an action's pad rebind.</summary>
	public static string PadSettingKey(Action action) => PadPrefix + action.Name;

	/// <summary>The keys read for an action: the player's rebind when one is stored, else the file's.</summary>
	public static IReadOnlyList<Key> EffectiveKeys(Action action) =>
		_keyRebinds.TryGetValue(action.Name, out List<Key>? keys) ? keys : action.Keys;

	/// <summary>The pad buttons and triggers read for an action: the player's rebind when stored, else the file's.</summary>
	public static (IReadOnlyList<JoyButton> Buttons, IReadOnlyList<JoyAxis> Triggers) EffectivePad(Action action) =>
		_padRebinds.TryGetValue(action.Name, out (List<JoyButton> Buttons, List<JoyAxis> Triggers) pad)
			? (pad.Buttons, pad.Triggers)
			: (action.Buttons, action.Triggers);

	/// <summary>
	/// The player's rebinds from the control settings (refii.toml's values, the dictionary the
	/// shipped keys are read from): every action's <c>key_</c> / <c>pad_</c> entry, and the InputMap
	/// actions registered again with what is now in force. The same instance twice is a no-op.
	/// </summary>
	public static void ReadBindings(IReadOnlyDictionary<string, string> values)
	{
		if (ReferenceEquals(values, _lastValues))
		{
			return;
		}

		_lastValues = values;
		if (_actions is null)
		{
			return;  // applied by Load when the actions are read
		}

		ApplyRebinds(_actions, values);
		foreach (Action action in _actions)
		{
			Register(action);
		}
	}

	private static void ApplyRebinds(IReadOnlyList<Action> actions, IReadOnlyDictionary<string, string>? values)
	{
		_keyRebinds.Clear();
		_padRebinds.Clear();
		if (values is null)
		{
			return;
		}

		foreach (Action action in actions)
		{
			if (values.TryGetValue(KeySettingKey(action), out string? keyText) && Stored(keyText) is { } keys)
			{
				var list = new List<Key>();
				foreach (string name in Names(keys))
				{
					Key key = OS.FindKeycodeFromString(name);
					if (key == Key.None)
					{
						GD.PushWarning($"[mod-input] {KeySettingKey(action)}: unknown key '{name}'; ignored");
						continue;
					}

					list.Add(key);
				}

				_keyRebinds[action.Name] = list;
			}

			if (values.TryGetValue(PadSettingKey(action), out string? padText) && Stored(padText) is { } pad)
			{
				(List<JoyButton> buttons, List<JoyAxis> triggers, List<string> unknown) = ParsePad(pad);
				foreach (string name in unknown)
				{
					GD.PushWarning($"[mod-input] {PadSettingKey(action)}: unknown button '{name}'; ignored");
				}

				_padRebinds[action.Name] = (buttons, triggers);
			}
		}
	}

	/// <summary>A stored rebind's names: null for absent or empty (the file's binding stands), the empty text for <see cref="Unbound"/>.</summary>
	private static string? Stored(string raw)
	{
		string text = raw.Trim().Trim('"').Trim();
		if (text.Length == 0)
		{
			return null;
		}

		return text.Equals(Unbound, StringComparison.OrdinalIgnoreCase) ? string.Empty : text;
	}

	/// <summary>The names of a comma list, trimmed, empties dropped.</summary>
	public static IEnumerable<string> Names(string list)
	{
		foreach (string part in list.Split(','))
		{
			if (part.Trim() is { Length: > 0 } name)
			{
				yield return name;
			}
		}
	}

	/// <summary>A pad rebind's comma list as buttons and triggers, with the names nothing matched.</summary>
	public static (List<JoyButton> Buttons, List<JoyAxis> Triggers, List<string> Unknown) ParsePad(string list)
	{
		var buttons = new List<JoyButton>();
		var triggers = new List<JoyAxis>();
		var unknown = new List<string>();
		foreach (string name in Names(list))
		{
			if (TryButton(name, out JoyButton button))
			{
				buttons.Add(button);
			}
			else if (TryTrigger(name, out JoyAxis trigger))
			{
				triggers.Add(trigger);
			}
			else
			{
				unknown.Add(name);
			}
		}

		return (buttons, triggers, unknown);
	}

	/// <summary>A pad button's name as the file and refii.toml spell it: the Xbox alias where one exists.</summary>
	public static string PadName(JoyButton button) => button switch
	{
		JoyButton.A => "A",
		JoyButton.B => "B",
		JoyButton.X => "X",
		JoyButton.Y => "Y",
		JoyButton.LeftShoulder => "LB",
		JoyButton.RightShoulder => "RB",
		JoyButton.LeftStick => "L3",
		JoyButton.RightStick => "R3",
		JoyButton.Start => "Start",
		JoyButton.Back => "Back",
		_ => button.ToString(),
	};

	/// <summary>A trigger's name: LT or RT.</summary>
	public static string TriggerName(JoyAxis axis) => axis == JoyAxis.TriggerLeft ? "LT" : "RT";

	/// <summary>An action's declared pad binding as the comma list refii.toml would hold.</summary>
	public static string DeclaredPadText(Action action)
	{
		var names = new List<string>();
		foreach (JoyButton button in action.Buttons)
		{
			names.Add(PadName(button));
		}

		foreach (JoyAxis trigger in action.Triggers)
		{
			names.Add(TriggerName(trigger));
		}

		return string.Join(",", names);
	}

	/// <summary>
	/// One frame: reads the devices and posts every edge. <paramref name="devices"/> is whether
	/// the window has focus; false reads every key, button and axis as released so anything
	/// held is released once. Nothing is posted while the guest is down, and the held set
	/// resets so the next guest starts from every action up.
	/// </summary>
	public static void Pump(bool devices)
	{
		if (!HostBridge.IsGuestUp)
		{
			_held.Clear();
			return;
		}

		IReadOnlyList<Action> actions = All;
		if (actions.Count == 0 && _held.Count == 0)
		{
			return;
		}

		// A name that left the set on a reload: release it if it was down.
		if (_held.Count != 0)
		{
			var gone = new List<(string, int)>();
			foreach (((string name, int player), bool down) in _held)
			{
				if (down && !Has(actions, name))
				{
					HostBridge.PostModInput(name, player, false);
				}

				if (!Has(actions, name))
				{
					gone.Add((name, player));
				}
			}

			foreach ((string, int) key in gone)
			{
				_held.Remove(key);
			}
		}

		bool keyboard = devices && !Refii.Game.GameDevMenu.KeyboardCaptured;
		int joypad1 = devices ? Refii.Game.GameInput.JoypadForPad(0) : -1;
		int joypad2 = devices ? Refii.Game.GameInput.JoypadForPad(1) : -1;
		foreach (Action action in actions)
		{
			Post(action, 1, (keyboard && KeysDown(action)) || PadDown(action, joypad1));
			Post(action, 2, PadDown(action, joypad2));
		}
	}

	private static bool Has(IReadOnlyList<Action> actions, string name)
	{
		foreach (Action action in actions)
		{
			if (action.Name == name)
			{
				return true;
			}
		}

		return false;
	}

	private static void Post(Action action, int player, bool down)
	{
		(string, int) key = (action.Name, player);
		bool was = _held.TryGetValue(key, out bool held) && held;
		if (was == down)
		{
			return;
		}

		_held[key] = down;
		HostBridge.PostModInput(action.Name, player, down);
	}

	private static bool KeysDown(Action action)
	{
		foreach (Key key in EffectiveKeys(action))
		{
			if (Input.IsKeyPressed(key) || Input.IsPhysicalKeyPressed(key))
			{
				return true;
			}
		}

		return false;
	}

	private static bool PadDown(Action action, int joypad)
	{
		if (joypad < 0)
		{
			return false;
		}

		(IReadOnlyList<JoyButton> buttons, IReadOnlyList<JoyAxis> triggers) = EffectivePad(action);
		foreach (JoyButton button in buttons)
		{
			if (Input.IsJoyButtonPressed(joypad, button))
			{
				return true;
			}
		}

		foreach (JoyAxis trigger in triggers)
		{
			if (Input.GetJoyAxis(joypad, trigger) > AxisThreshold)
			{
				return true;
			}
		}

		if (action.Axis is { } axis)
		{
			float value = Input.GetJoyAxis(joypad, axis);
			return action.AxisSign switch
			{
				> 0 => value > AxisThreshold,
				< 0 => value < -AxisThreshold,
				_ => Mathf.Abs(value) > AxisThreshold,
			};
		}

		return false;
	}

	// ---------------------------------------------------------------------------------------
	// Loading
	// ---------------------------------------------------------------------------------------

	private static List<Action> Load()
	{
		foreach (StringName registered in _registered)
		{
			if (InputMap.HasAction(registered))
			{
				InputMap.EraseAction(registered);
			}
		}

		_registered.Clear();

		List<Action> actions = ReadDeclared();
		ApplyRebinds(actions, _lastValues);
		foreach (Action action in actions)
		{
			Register(action);
		}

		if (actions.Count != 0)
		{
			GD.Print($"[mod-input] {actions.Count} action(s) declared");
		}

		return actions;
	}

	/// <summary>
	/// Every action the enabled mods declare, in load order, as the files state them — the read
	/// alone, which the launcher's Controls group also takes; nothing is registered.
	/// </summary>
	public static List<Action> ReadDeclared()
	{
		var actions = new List<Action>();
		foreach ((string mod, string text) in ModEntries.ReadEnabledText(ModInputPath))
		{
			Variant parsed = Json.ParseString(text);
			if (parsed.VariantType != Variant.Type.Dictionary)
			{
				GD.PushWarning($"[mod-input] {mod}:{ModInputPath} is not a JSON object; ignored");
				continue;
			}

			foreach (KeyValuePair<Variant, Variant> entry in parsed.AsGodotDictionary())
			{
				string actionName = entry.Key.AsString().Trim();
				if (actionName.Length == 0 || entry.Value.VariantType != Variant.Type.Dictionary)
				{
					GD.PushWarning($"[mod-input] {mod}: '{actionName}' is not a binding object; ignored");
					continue;
				}

				Action? action = Parse(mod, actionName, entry.Value.AsGodotDictionary());
				if (action is null)
				{
					continue;
				}

				// A later mod (higher priority) declaring the same scoped name replaces the earlier one.
				actions.RemoveAll(a => a.Name == action.Name);
				actions.Add(action);
			}
		}

		return actions;
	}

	private static Action? Parse(string mod, string actionName, Godot.Collections.Dictionary binding)
	{
		string name = mod + "." + actionName;
		string label = binding.TryGetValue("label", out Variant labelValue) && labelValue.VariantType == Variant.Type.String
			? labelValue.AsString()
			: actionName;

		var keys = new List<Key>();
		foreach (string keyName in Names(binding, "keys"))
		{
			Key key = OS.FindKeycodeFromString(keyName);
			if (key == Key.None)
			{
				GD.PushWarning($"[mod-input] {name}: unknown key '{keyName}'; ignored");
				continue;
			}

			keys.Add(key);
		}

		var buttons = new List<JoyButton>();
		var triggers = new List<JoyAxis>();
		foreach (string buttonName in Names(binding, "buttons"))
		{
			if (TryButton(buttonName, out JoyButton button))
			{
				buttons.Add(button);
			}
			else if (TryTrigger(buttonName, out JoyAxis trigger))
			{
				triggers.Add(trigger);
			}
			else
			{
				GD.PushWarning($"[mod-input] {name}: unknown button '{buttonName}'; ignored");
			}
		}

		JoyAxis? axis = null;
		int axisSign = 0;
		if (binding.TryGetValue("axis", out Variant axisValue) && axisValue.VariantType == Variant.Type.String)
		{
			string axisName = axisValue.AsString().Trim();
			if (axisName.Length != 0)
			{
				if (axisName[0] == '+' || axisName[0] == '-')
				{
					axisSign = axisName[0] == '+' ? 1 : -1;
					axisName = axisName[1..];
				}

				if (Enum.TryParse(axisName, true, out JoyAxis parsedAxis) && parsedAxis != JoyAxis.Invalid)
				{
					axis = parsedAxis;
				}
				else
				{
					GD.PushWarning($"[mod-input] {name}: unknown axis '{axisValue.AsString()}'; ignored");
					axisSign = 0;
				}
			}
		}

		if (keys.Count == 0 && buttons.Count == 0 && triggers.Count == 0 && axis is null)
		{
			GD.PushWarning($"[mod-input] {name}: no key, button or axis bound; ignored");
			return null;
		}

		return new Action(name, mod, label, keys, buttons, triggers, axis, axisSign);
	}

	/// <summary>A binding list: an array of strings, or one string.</summary>
	private static IEnumerable<string> Names(Godot.Collections.Dictionary binding, string field)
	{
		if (!binding.TryGetValue(field, out Variant value))
		{
			yield break;
		}

		switch (value.VariantType)
		{
			case Variant.Type.String:
				if (value.AsString().Trim() is { Length: > 0 } single)
				{
					yield return single;
				}

				break;
			case Variant.Type.Array:
				foreach (Variant item in value.AsGodotArray())
				{
					if (item.VariantType == Variant.Type.String && item.AsString().Trim() is { Length: > 0 } one)
					{
						yield return one;
					}
				}

				break;
		}
	}

	/// <summary>A <see cref="JoyButton"/> name or an Xbox alias; LT/RT are triggers, not buttons.</summary>
	public static bool TryButton(string name, out JoyButton button)
	{
		switch (name.Trim().ToLowerInvariant())
		{
			case "a": button = JoyButton.A; return true;
			case "b": button = JoyButton.B; return true;
			case "x": button = JoyButton.X; return true;
			case "y": button = JoyButton.Y; return true;
			case "lb": button = JoyButton.LeftShoulder; return true;
			case "rb": button = JoyButton.RightShoulder; return true;
			case "l3": button = JoyButton.LeftStick; return true;
			case "r3": button = JoyButton.RightStick; return true;
			case "start": button = JoyButton.Start; return true;
			case "back":
			case "select": button = JoyButton.Back; return true;
			case "lt":
			case "rt":
				button = JoyButton.Invalid;
				return false;
		}

		return Enum.TryParse(name.Trim(), true, out button) && button != JoyButton.Invalid;
	}

	/// <summary>LT / RT, or a trigger axis by its <see cref="JoyAxis"/> name.</summary>
	public static bool TryTrigger(string name, out JoyAxis axis)
	{
		switch (name.Trim().ToLowerInvariant())
		{
			case "lt":
			case "triggerleft": axis = JoyAxis.TriggerLeft; return true;
			case "rt":
			case "triggerright": axis = JoyAxis.TriggerRight; return true;
		}

		axis = JoyAxis.Invalid;
		return false;
	}

	/// <summary>Registers the action in Godot's InputMap with every binding in force: the player's rebinds over the file's.</summary>
	private static void Register(Action action)
	{
		StringName mapped = action.InputMapAction;
		if (InputMap.HasAction(mapped))
		{
			InputMap.EraseAction(mapped);
		}

		InputMap.AddAction(mapped);
		if (!_registered.Contains(mapped))
		{
			_registered.Add(mapped);
		}

		foreach (Key key in EffectiveKeys(action))
		{
			InputMap.ActionAddEvent(mapped, new InputEventKey { Keycode = key });
		}

		(IReadOnlyList<JoyButton> buttons, IReadOnlyList<JoyAxis> triggers) = EffectivePad(action);
		foreach (JoyButton button in buttons)
		{
			InputMap.ActionAddEvent(mapped, new InputEventJoypadButton { ButtonIndex = button, Device = AllDevices });
		}

		foreach (JoyAxis trigger in triggers)
		{
			InputMap.ActionAddEvent(mapped, new InputEventJoypadMotion { Axis = trigger, AxisValue = 1f, Device = AllDevices });
		}

		if (action.Axis is { } axis)
		{
			if (action.AxisSign >= 0)
			{
				InputMap.ActionAddEvent(mapped, new InputEventJoypadMotion { Axis = axis, AxisValue = 1f, Device = AllDevices });
			}

			if (action.AxisSign <= 0)
			{
				InputMap.ActionAddEvent(mapped, new InputEventJoypadMotion { Axis = axis, AxisValue = -1f, Device = AllDevices });
			}
		}
	}
}
