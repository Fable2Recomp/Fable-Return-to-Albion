using System;
using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// What a Tracy tab looks at: the whole capture or the Tracy tab's current timeline view (a frame
/// picked in the frame strip, or wherever the timeline was zoomed), and every thread or one.
/// </summary>
public partial class GameDevTracyScope : HBoxContainer
{
	private readonly OptionButton _range = new();
	private readonly OptionButton _thread = new();
	private readonly List<ulong> _threadIds = new();

	/// <summary>The player changed the range or the thread.</summary>
	public event Action? Changed;

	public GameDevTracyScope()
	{
		AddThemeConstantOverride("separation", 8);
		AddChild(new Label { Text = "range" });
		_range.AddItem("whole capture");
		_range.AddItem("timeline view");
		AddChild(_range);
		AddChild(new Label { Text = "thread" });
		_thread.AddItem("all threads");
		_thread.CustomMinimumSize = new Vector2(220, 0);
		AddChild(_thread);
		_range.ItemSelected += _ => Changed?.Invoke();
		_thread.ItemSelected += _ => Changed?.Invoke();
	}

	/// <summary>Every thread or one: 0 for all.</summary>
	public ulong Thread => _thread.Selected > 0 && _thread.Selected - 1 < _threadIds.Count ? _threadIds[_thread.Selected - 1] : 0;

	/// <summary>The whole capture on every thread: the Worker's own running totals answer it.</summary>
	public bool Whole => _range.Selected == 0 && Thread == 0;

	/// <summary>The range in Tracy's clock; the whole capture when the timeline has no view yet.</summary>
	public (long T0, long T1) Range =>
		_range.Selected == 1 && GameDevTabTracy.TimelineView is { } view ? view : (long.MinValue, long.MaxValue);

	/// <summary>A short description for a summary line.</summary>
	public string Describe() =>
		(_range.Selected == 1 && GameDevTabTracy.TimelineView is { } view
			? $"timeline view ({GameDevTracyTimeline.FormatNs(view.End - view.Start)})"
			: "whole capture") +
		(Thread != 0 ? $", {_thread.GetItemText(_thread.Selected)}" : string.Empty);

	/// <summary>Re-reads the capture's threads, keeping the one picked when it is still there.</summary>
	public void ReloadThreads()
	{
		ulong picked = Thread;
		_threadIds.Clear();
		_thread.Clear();
		_thread.AddItem("all threads");
		foreach (HostTracy.ThreadInfo thread in HostTracy.Threads())
		{
			if (!thread.HasZones && !thread.HasSamples)
			{
				continue;
			}

			_threadIds.Add(thread.Id);
			_thread.AddItem(thread.Name);
			if (thread.Id == picked)
			{
				_thread.Select(_threadIds.Count);
			}
		}
	}
}
