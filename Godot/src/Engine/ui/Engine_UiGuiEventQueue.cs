using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// `guieventqueue.lua`, translated — `GUI.State.EventQueue` and the five calls that work it.
/// The queue is Lua-side only; C++ is pull-only (`sub_826C1810` "GUI:PullEvent()",
/// `sub_82370450` dispatches one per tick).
///
/// Despite the name it is a stack: every push is `table.insert(..., 1, ...)` and `PullEvent`
/// takes `[1]`, so several posts in one tick come back newest-first. The Lua keeps four
/// parallel arrays (`Name`, `Type`, `Param1Low`, `Param1High`); one list of records here.
/// Every push writes `EventQueueSize` from `#Name` before returning; the pull writes it from
/// the depth BEFORE its own pop, so its report counts the event it is handing out.
/// </summary>
public sealed class Engine_UiGuiEventQueue
{
	/// <summary>One queued event — the four parallel arrays, as one record.</summary>
	public readonly record struct Event(string Name, int Type, int Param1Low, int Param1High)
	{
		/// <summary>The two halves recombined, as `sub_826C1810` recombines them:
		/// `(high &lt;&lt; 16) + low`.</summary>
		public uint Param1 => ((uint)Param1High << 16) + (uint)(Param1Low & 0xFFFF);
	}

	/// <summary>`NO_VALID_EVENT` — what `PullEvent` states when the queue is empty.</summary>
	public const string NoValidEvent = "NO_VALID_EVENT";

	private readonly List<Event> _queue = new();

	/// <summary>`#GUI.State.EventQueue.Name` — what `EventQueueSize` is set from.</summary>
	public int Count => _queue.Count;

	/// <summary>The events currently waiting, front first.</summary>
	public IReadOnlyList<Event> Pending => _queue;

	/// <summary>
	/// `GUI:PushButtonEvent(name, type, low, high)` — the one the shipped `SelectMenuItem` calls
	/// with the highlighted row's own identity.
	/// </summary>
	public void PushButtonEvent(string name, int type, int param1Low, int param1High) =>
		_queue.Insert(0, new Event(name, type, param1Low, param1High));

	/// <summary>`GUI:PushNavigationEvent(...)` — `EVENT_TYPE_NAVIGATION`, which is the GUI
	/// layer's 100 and NOT the menu's 201. See <see cref="Engine_UiGuiConstants"/>.</summary>
	public void PushNavigationEvent(string name, int param1Low, int param1High) =>
		PushButtonEvent(name, Engine_UiGuiConstants.EventNavigation, param1Low, param1High);

	/// <summary>`GUI:PushRemoveButtonEvent(...)` — `EVENT_TYPE_BUTTON_REMOVE`, 101.</summary>
	public void PushRemoveButtonEvent(string name, int param1Low, int param1High) =>
		PushButtonEvent(name, Engine_UiGuiConstants.EventButtonRemove, param1Low, param1High);

	/// <summary>`GUI:PushSetButtonEvent(...)` — `EVENT_TYPE_BUTTON_SET`, 102.</summary>
	public void PushSetButtonEvent(string name, int param1Low, int param1High) =>
		PushButtonEvent(name, Engine_UiGuiConstants.EventButtonSet, param1Low, param1High);

	/// <summary>
	/// `GUI:PullEvent()` — takes the front event, or states <see cref="NoValidEvent"/>. The
	/// reported depth is the depth BEFORE the pop, as the script writes it.
	/// </summary>
	public (Event? Event, int SizeBeforePull) PullEvent()
	{
		int size = _queue.Count;
		if (size == 0)
		{
			return (null, 0);
		}

		Event front = _queue[0];
		_queue.RemoveAt(0);
		return (front, size);
	}

	/// <summary>Throws the queue away — a screen change does not carry its events across.</summary>
	public void Clear() => _queue.Clear();

	public string Describe() => _queue.Count == 0
		? "empty"
		: $"{_queue.Count} waiting, front '{_queue[0].Name}' " +
			$"(type {_queue[0].Type}, id 0x{_queue[0].Param1:X8})";
}
