using System;
using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Mods;

/// <summary>
/// A mod's actions on a TOUCHSCREEN. A phone has no keyboard, so an action bound to keys alone
/// would never fire there; a mod states how the screen presses it instead, three ways:
/// <list type="bullet">
/// <item><c>input.json</c>'s <c>"touch"</c> on an action: <c>"touch": "Skate"</c> (or <c>true</c>,
/// the action's <c>label</c>) gives it its own PILL among the touch controls, held while a finger
/// is on it; <c>{ "pill": "Blue", "when": "portal_gun.mode" }</c> stands only while that published
/// value is at least 0.5. <c>{ "caption": "Hop", "when": "skate.on" }</c> gives no pill: the
/// on-screen pad button the action is bound to is shown and captioned that while the value holds.</item>
/// <item>A screen area: <see cref="SetArea"/> (<c>RefiiHost.touch_area</c>), a rectangle of the
/// screen a finger presses the action on — the mod's own drawing, a minimap.</item>
/// <item>A HUD piece's <c>"touch"</c> (<c>ui/hud.json</c>): the piece, where it is drawn, is such an
/// area (<see cref="HudArea"/>).</item>
/// </list>
/// AND THE ON-SCREEN PAD IS PLAYER ONE'S PAD: an action with no pill of its own is pressed by the
/// touch controls' buttons it is bound to (<c>"buttons": ["A"]</c>), as a controller's would.
/// </summary>
/// <remarks>
/// Everything here is player one's: the touchscreen is pad 0. The touch controls (Android's) state
/// what is held every frame (<see cref="SetHeld"/>, <see cref="SetPad"/>); <see cref="ModInput.Pump"/> reads it with the
/// other devices, so a screen press reaches <c>input_action</c>, claims and rebinds like any other.
/// A desktop states nothing and reads nothing here.
/// </remarks>
public static class ModInputTouch
{
	/// <summary>The touch controls' move stick: x right and y up, each -1..1; zero with no thumb on it (or no touchscreen).</summary>
	public static Vector2 Stick()
	{
		Refii.Game.GameInput.TouchHold touch = Refii.Game.GameInput.Touch;
		return new Vector2(touch.LeftX / 32767f, touch.LeftY / 32767f).LimitLength(1f);
	}

	/// <summary>An action's <c>"touch"</c>: its pill's text, its pad button's caption, and the published value it stands on.</summary>
	public sealed record Spec(string Pill, string Caption, string When);

	/// <summary>
	/// <c>"touch"</c> on a binding: <c>true</c> (a pill named <paramref name="label"/>), a string (the
	/// pill's text) or <c>{ pill | label, caption, when }</c>. Null when absent, false or empty.
	/// </summary>
	public static Spec? Parse(string name, string label, Godot.Collections.Dictionary binding)
	{
		if (!binding.TryGetValue("touch", out Variant touch))
		{
			return null;
		}

		switch (touch.VariantType)
		{
			case Variant.Type.Bool:
				return touch.AsBool() ? new Spec(label, string.Empty, string.Empty) : null;
			case Variant.Type.String:
				return touch.AsString().Trim() is { Length: > 0 } pill ? new Spec(pill, string.Empty, string.Empty) : null;
			case Variant.Type.Dictionary:
				Godot.Collections.Dictionary record = touch.AsGodotDictionary();
				string Text(string key) =>
					record.TryGetValue(key, out Variant v) && v.VariantType == Variant.Type.String ? v.AsString().Trim() : string.Empty;
				string text = Text("pill").Length != 0 ? Text("pill") : Text("label");
				var spec = new Spec(text, Text("caption"), Text("when"));
				if (spec.Pill.Length == 0 && spec.Caption.Length == 0)
				{
					GD.PushWarning($"[mod-input] {name}: \"touch\" names neither a pill nor a caption; ignored");
					return null;
				}

				return spec;
			default:
				GD.PushWarning($"[mod-input] {name}: \"touch\" is not true, a string or an object; ignored");
				return null;
		}
	}

	/// <summary>Whether an action's touch control stands now: it has one, and its <c>when</c> value (if any) is on.</summary>
	public static bool Shown(ModInput.Action action) =>
		action.Touch is { } touch &&
		(touch.When.Length == 0 || (HostBridge.TryGetModValue(touch.When, out float value) && value >= 0.5f));

	/// <summary>Whether the action has a pill of its own; such an action is not pressed by the on-screen pad.</summary>
	public static bool HasPill(ModInput.Action action) => action.Touch is { Pill.Length: > 0 };

	// ---------------------------------------------------------------------------------------
	// What the screen holds
	// ---------------------------------------------------------------------------------------

	private static readonly HashSet<string> _held = new(StringComparer.Ordinal);

	/// <summary>
	/// The actions a finger holds now — on a pill or an area — stated whole by the touch controls each
	/// frame: what is not named is let go.
	/// </summary>
	public static void SetHeld(IEnumerable<string> actions)
	{
		_held.Clear();
		foreach (string action in actions)
		{
			_held.Add(action);
		}
	}

	/// <summary><see cref="SetHeld(IEnumerable{string})"/> for a list, walked by index: no enumerator is boxed each frame.</summary>
	public static void SetHeld(IReadOnlyList<string> actions)
	{
		_held.Clear();
		for (int i = 0; i < actions.Count; ++i)
		{
			_held.Add(actions[i]);
		}
	}

	/// <summary>The on-screen pad's buttons and triggers a finger presses, as the touch controls state them (<see cref="SetPad"/>).</summary>
	private static uint _padButtons, _padLeft, _padRight;

	/// <summary>
	/// The buttons (XInput bits) and triggers (0..255) fingers press on the on-screen pad now, stated
	/// by the touch controls each frame. Only what a finger presses on a button: a stick pushed past
	/// its ring sprints by holding A for the game, which is no press of A for a mod.
	/// </summary>
	public static void SetPad(uint buttons, uint leftTrigger, uint rightTrigger)
	{
		_padButtons = buttons;
		_padLeft = leftTrigger;
		_padRight = rightTrigger;
	}

	/// <summary>Whether the screen presses <paramref name="action"/> for player one: a finger on its pill or area, or the on-screen pad's button.</summary>
	public static bool Down(ModInput.Action action) => _held.Contains(action.Name) || PadDown(action);

	/// <summary>
	/// Whether the on-screen pad holds a button or trigger the action is bound to, under its chord
	/// (a pad modifier held on the screen too). Never for an action with its own pill.
	/// </summary>
	private static bool PadDown(ModInput.Action action)
	{
		if ((_padButtons == 0u && _padLeft == 0u && _padRight == 0u) || HasPill(action))
		{
			return false;
		}

		ModInputClaims.Modifier modifier = action.ChordModifier;
		if (modifier == ModInputClaims.Modifier.Key ||
			(modifier != ModInputClaims.Modifier.None &&
				!ModInputClaims.ModifierHeld(modifier, _padButtons, _padLeft, _padRight)))
		{
			return false;
		}

		(IReadOnlyList<JoyButton> buttons, IReadOnlyList<JoyAxis> triggers) = ModInput.EffectivePad(action);
		if ((ModInputClaims.PadBits(buttons) & _padButtons) != 0u)
		{
			return true;
		}

		foreach (JoyAxis trigger in triggers)
		{
			if ((trigger == JoyAxis.TriggerLeft ? _padLeft : _padRight) > 30u)
			{
				return true;
			}
		}

		return false;
	}

	// ---------------------------------------------------------------------------------------
	// Areas
	// ---------------------------------------------------------------------------------------

	/// <summary>The areas scripts stated, by action, in the root viewport's coordinates.</summary>
	private static readonly Dictionary<string, Rect2> _areas = new(StringComparer.Ordinal);

	/// <summary>The HUD pieces' areas, by action: the UI camera's canvas rectangle (+Y up) and when it was last drawn (ms).</summary>
	private static readonly Dictionary<string, (Rect2 Canvas, ulong Drawn)> _hudAreas = new(StringComparer.Ordinal);

	/// <summary>How long a HUD piece's area stands after it was last drawn, in milliseconds: past a skipped frame, not past a hidden HUD.</summary>
	private const ulong HudAreaLife = 250;

	/// <summary>
	/// States the screen rectangle (the root viewport's coordinates, where a mod's CanvasLayer draws)
	/// a finger presses <paramref name="action"/> (scoped, <c>&lt;mod&gt;.&lt;action&gt;</c>) on; an
	/// empty rectangle removes it. False for an action no enabled mod declares.
	/// </summary>
	public static bool SetArea(string action, Rect2 rect)
	{
		string name = (action ?? string.Empty).Trim();
		if (!Declared(name))
		{
			return false;
		}

		if (rect.Size.X <= 0f || rect.Size.Y <= 0f)
		{
			_areas.Remove(name);
		}
		else
		{
			_areas[name] = rect;
		}

		return true;
	}

	/// <summary>A HUD piece drawn now with <c>"touch"</c>: its drawn box in the UI camera's canvas (+Y up).</summary>
	public static void HudArea(string action, Rect2 canvas) => _hudAreas[action] = (canvas, Time.GetTicksMsec());

	/// <summary>Whether any area stands (stated, or drawn by the HUD lately).</summary>
	public static bool AnyAreas => _areas.Count != 0 || _hudAreas.Count != 0;

	/// <summary>
	/// Every area standing now into <paramref name="into"/> (cleared first), in the root viewport's
	/// coordinates: the scripts', and the HUD pieces' drawn in the last <see cref="HudAreaLife"/>, through
	/// <paramref name="fromHud"/> (null while the HUD's canvas is not known).
	/// </summary>
	public static void Areas(List<(string Action, Rect2 Rect)> into, Func<Rect2, Rect2?> fromHud)
	{
		into.Clear();
		foreach ((string action, Rect2 rect) in _areas)
		{
			into.Add((action, rect));
		}

		ulong now = Time.GetTicksMsec();
		foreach ((string action, (Rect2 canvas, ulong drawn)) in _hudAreas)
		{
			if (now - drawn <= HudAreaLife && fromHud(canvas) is { } rect)
			{
				into.Add((action, rect));
			}
		}
	}

	/// <summary>Forgets the areas and lets go of everything (the mod set reloads).</summary>
	public static void Clear()
	{
		_areas.Clear();
		_hudAreas.Clear();
		_held.Clear();
		SetPad(0u, 0u, 0u);
	}

	private static bool Declared(string name)
	{
		foreach (ModInput.Action action in ModInput.All)
		{
			if (action.Name == name)
			{
				return true;
			}
		}

		return false;
	}
}
