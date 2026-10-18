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
/// <c>mod.&lt;mod&gt;.&lt;action&gt;</c> so the Godot side and a future rebinding UI see it.
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

	/// <summary>One declared action, as the file states it.</summary>
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

	/// <summary>Every action the enabled mods declare, in load order.</summary>
	public static IReadOnlyList<Action> All => _actions ??= Load();

	/// <summary>Drops the loaded set; the next pump reads the files and registers the actions again.</summary>
	public static void Reload()
	{
		_actions = null;
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
		foreach (Key key in action.Keys)
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

		foreach (JoyButton button in action.Buttons)
		{
			if (Input.IsJoyButtonPressed(joypad, button))
			{
				return true;
			}
		}

		foreach (JoyAxis trigger in action.Triggers)
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

	/// <summary>Registers the action in Godot's InputMap with every binding the file states.</summary>
	private static void Register(Action action)
	{
		StringName mapped = action.InputMapAction;
		if (InputMap.HasAction(mapped))
		{
			InputMap.EraseAction(mapped);
		}

		InputMap.AddAction(mapped);
		_registered.Add(mapped);

		foreach (Key key in action.Keys)
		{
			InputMap.ActionAddEvent(mapped, new InputEventKey { Keycode = key });
		}

		foreach (JoyButton button in action.Buttons)
		{
			InputMap.ActionAddEvent(mapped, new InputEventJoypadButton { ButtonIndex = button, Device = AllDevices });
		}

		foreach (JoyAxis trigger in action.Triggers)
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
