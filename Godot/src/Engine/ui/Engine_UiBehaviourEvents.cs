using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// THE PRESENTATION EVENTS A SCREEN'S BEHAVIOURS FIRE, timed by the host's own playback of it.
///
/// A behaviour element names its script in a string attribute (`BehaviorEndLegal.lua`; attribute
/// id 0x5DF916A in every shipped file). The guest runs a script's `onDeactivate` when the
/// element stops being live — the walk `sub_822849A0` clears its 0x1000 bit through
/// `sub_82BB55B0` once it is inactive, outside its `timeoffset`..`endtime` window, or under a
/// parent that went (children deactivate with it). An element that was never live never
/// deactivates. Each shipped event script is one line, `fireEvent("&lt;EVENT&gt;",
/// getElement("parent", self.element))` in `onDeactivate`, so the event is the script's and its
/// target is the behaviour's parent.
///
/// <see cref="Observe"/> takes the book's liveness per element (Engine_UiElementResolve.Drawn —
/// own bit, slide, parent chain and window, the guest's own terms) once per stand and reports
/// every tracked behaviour that went from live to not live.
/// </summary>
public sealed class Engine_UiBehaviourEvents
{
	/// <summary>One tracked behaviour: its element, the event its script fires, and that event's target.</summary>
	public readonly record struct Behaviour(int Ordinal, string Event, int Parent, string ParentName);

	/// <summary>One fired event: the name, the parent element's name ("" for the document root).</summary>
	public readonly record struct Fired(string Event, string ParentName, int Ordinal);

	/// <summary>
	/// The shipped event scripts the host posts for, by file name, lower case
	/// (`art/gui/gameface/*.lua`, each `onDeactivate` firing the one event on its parent).
	/// `behaviorboyselected.lua` / `behaviorgirlselected.lua` are absent: the gender card's choice
	/// states the commit itself (`New Game`). `behaviorloadselected.lua` is absent: no shipped
	/// presentation carries it.
	/// </summary>
	private static readonly Dictionary<string, string> EventByScript = new(System.StringComparer.OrdinalIgnoreCase)
	{
		["behaviorendlegal.lua"] = "END_LEGAL",
		["behaviorendstarted.lua"] = "END_STARTED",
		["behaviorcanpressa.lua"] = "CAN_PRESS_A",
		["behaviordialogactive.lua"] = "DIALOG_ACTIVE",
		["behaviorunfreeze.lua"] = "UNFREEZE",
		["behavioroutrofinished.lua"] = "OUTRO_FINISHED",
		["behaviorboy.lua"] = "BOY_ACTIVE",
		["behaviorgirl.lua"] = "GIRL_ACTIVE",
		["behaviorloadcard.lua"] = "LOAD_CARD_ACTIVE",
		["finishedtransitioningtogame.lua"] = "FINISHED_TRANSITIONING_TO_GAME",
	};

	/// <summary>The event a behaviour script fires, or null for a script the host posts nothing for.</summary>
	public static string? EventOfScript(string script)
	{
		int slash = System.Math.Max(script.LastIndexOf('/'), script.LastIndexOf('\\'));
		string file = slash >= 0 ? script[(slash + 1)..] : script;
		return EventByScript.TryGetValue(file, out string? name) ? name : null;
	}

	private readonly Behaviour[] _behaviours;

	/// <summary>Per tracked behaviour: null until seen, else whether it was live at the last observation.</summary>
	private readonly bool?[] _live;

	public Engine_UiBehaviourEvents(IReadOnlyList<Behaviour> behaviours)
	{
		_behaviours = new Behaviour[behaviours.Count];
		for (int i = 0; i < behaviours.Count; ++i)
		{
			_behaviours[i] = behaviours[i];
		}

		_live = new bool?[_behaviours.Length];
	}

	/// <summary>The tracked behaviours, in document order.</summary>
	public IReadOnlyList<Behaviour> Behaviours => _behaviours;

	/// <summary>
	/// The behaviours of a screen whose script fires an event the host posts, or null when it has
	/// none. A behaviour's script is any string attribute naming a `.lua` file.
	/// </summary>
	public static Engine_UiBehaviourEvents? For(Engine_UiScreen screen)
	{
		var found = new List<Behaviour>();
		Engine_BgfDocument file = screen.Document;
		foreach (UiNode node in screen.Nodes)
		{
			if (node.Type != BgfElementType.Behavior || node.Parent is not { } parent)
			{
				continue;
			}

			foreach (BgfAttribute attribute in node.Element.Attributes)
			{
				if (attribute.Type != BgfValueType.String)
				{
					continue;
				}

				string value = file.GetString(attribute.AsStringIndex());
				if (!value.EndsWith(".lua", System.StringComparison.OrdinalIgnoreCase) ||
					EventOfScript(value) is not { } name)
				{
					continue;
				}

				string parentName = parent.Parent is null ? string.Empty : parent.HasAuthoredName ? parent.Name : string.Empty;
				found.Add(new Behaviour(node.Ordinal, name, parent.Ordinal, parentName));
				break;
			}
		}

		return found.Count == 0 ? null : new Engine_UiBehaviourEvents(found);
	}

	/// <summary>The presentation closed: nothing is live, and nothing that was deactivates.</summary>
	public void Reset() => System.Array.Clear(_live);

	/// <summary>
	/// One stand's liveness per element (<paramref name="live"/>, indexed by ordinal). Returns the
	/// behaviours that went from live to not live since the last call, in document order, or null.
	/// <paramref name="rose"/>, when given, collects the ones that came live.
	/// </summary>
	public List<Fired>? Observe(System.Func<int, bool> live, List<Behaviour>? rose = null)
	{
		List<Fired>? fired = null;
		for (int i = 0; i < _behaviours.Length; ++i)
		{
			Behaviour behaviour = _behaviours[i];
			bool now = live(behaviour.Ordinal);
			if (_live[i] == true && !now)
			{
				(fired ??= new List<Fired>()).Add(new Fired(behaviour.Event, behaviour.ParentName, behaviour.Ordinal));
			}
			else if (now && _live[i] != true)
			{
				rose?.Add(behaviour);
			}

			_live[i] = now;
		}

		return fired;
	}

	/// <summary><see cref="Observe(System.Func{int, bool}, List{Behaviour})"/> over a liveness array.</summary>
	public List<Fired>? Observe(IReadOnlyList<bool> live, List<Behaviour>? rose = null) =>
		Observe(ordinal => ordinal >= 0 && ordinal < live.Count && live[ordinal], rose);
}
