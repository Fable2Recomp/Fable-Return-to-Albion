using System;
using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// The Tracy capture's timeline: a row per game thread with its zones nested by depth and, under
/// them, a bar wherever context switches say the thread was running; then the GPU contexts; then
/// a row per core showing what it ran — a game thread by name, "other" for every other process.
/// </summary>
/// <remarks>
/// Wheel or pinch zooms around the cursor, drag or a pan gesture pans, double-click a zone to fill
/// the view with it, hover — or tap, on a touchscreen — for its details. Only what is visible is asked for: zones narrower than a pixel are left out
/// natively with everything under them, so a whole capture zoomed out costs no more than a frame.
/// </remarks>
public partial class GameDevTracyTimeline : Control
{
	private const float LabelWidth = 170f;
	private const float RulerHeight = 22f;
	private const float ZoneHeight = 17f;
	private const float RunningHeight = 4f;
	private const float RowGap = 8f;
	private const int FontSize = 12;
	private const int Capacity = 20000;
	private const uint MaxDepth = 32;

	private enum Kind
	{
		Thread,
		Gpu,
		Cpu,
	}

	private sealed class Row
	{
		public required Kind Kind;
		public required string Label;
		public ulong Thread;
		public int Index;
		public bool HasZones;
		public bool HasRunning;
		public readonly HostTracy.Spans Spans = new();
		public readonly HostTracy.Intervals Intervals = new();
		public int Depth;
		public float Top;
		public float Height;
	}

	private readonly List<Row> _rows = new();
	private readonly Dictionary<ulong, string> _threadNames = new();
	private bool _dirty = true;
	private Row? _hoverRow;
	private int _hoverIndex = -1;
	private Vector2 _mouse;

	/// <summary>The ruler counts from here: the capture's first frame.</summary>
	public long Origin { get; set; }

	public long ViewStart { get; private set; }

	public long ViewEnd { get; private set; } = 1;

	/// <summary>Raised when the player zooms or pans, so a following view can let go.</summary>
	public event Action? UserMoved;

	public GameDevTracyTimeline()
	{
		SizeFlagsHorizontal = SizeFlags.ExpandFill;
		ClipContents = true;
		MouseFilter = MouseFilterEnum.Stop;
		Resized += () => _dirty = true;
	}

	public int RowCount => _rows.Count;

	public void SetView(long start, long end)
	{
		ViewStart = start;
		ViewEnd = Math.Max(start + 1, end);
		_dirty = true;
	}

	/// <summary>Asks again for the same view: a live capture's newest data.</summary>
	public void Invalidate() => _dirty = true;

	/// <summary>Which threads, GPU contexts and cores the capture has.</summary>
	public void ReloadRows()
	{
		_rows.Clear();
		_threadNames.Clear();
		foreach (HostTracy.ThreadInfo thread in HostTracy.Threads())
		{
			_threadNames[thread.Id] = thread.Name;
			if (thread.HasZones || thread.HasContextSwitches)
			{
				_rows.Add(new Row
				{
					Kind = Kind.Thread, Label = thread.Name, Thread = thread.Id,
					HasZones = thread.HasZones, HasRunning = thread.HasContextSwitches,
				});
			}
		}

		List<(string Name, ulong Zones)> gpus = HostTracy.GpuContexts();
		for (int i = 0; i < gpus.Count; ++i)
		{
			_rows.Add(new Row { Kind = Kind.Gpu, Label = "GPU: " + gpus[i].Name, Index = i, HasZones = true });
		}

		int cores = HostTracy.CpuCount();
		for (int core = 0; core < cores; ++core)
		{
			_rows.Add(new Row { Kind = Kind.Cpu, Label = $"core {core}", Index = core });
		}

		_hoverRow = null;
		_dirty = true;
	}

	public string ThreadName(ulong id) =>
		id == HostTracy.OtherProcessThread ? "other processes"
		: id == 0 ? "idle"
		: _threadNames.TryGetValue(id, out string? name) ? name : $"thread {id}";

	public override void _Process(double delta)
	{
		if (_dirty && IsVisibleInTree())
		{
			Requery();
		}
	}

	private void Requery()
	{
		_dirty = false;
		float width = Mathf.Max(1f, Size.X - LabelWidth);
		long minNs = (long)((ViewEnd - ViewStart) / width);
		float y = RulerHeight;
		foreach (Row row in _rows)
		{
			switch (row.Kind)
			{
				case Kind.Thread:
					if (row.HasZones)
					{
						HostTracy.Zones(row.Thread, ViewStart, ViewEnd, minNs, MaxDepth, Capacity, row.Spans);
					}

					if (row.HasRunning)
					{
						HostTracy.Running(row.Thread, ViewStart, ViewEnd, Capacity, row.Intervals);
					}

					break;
				case Kind.Gpu:
					HostTracy.GpuZones(row.Index, ViewStart, ViewEnd, minNs, MaxDepth, Capacity, row.Spans);
					break;
				case Kind.Cpu:
					HostTracy.CpuSlices(row.Index, ViewStart, ViewEnd, Capacity, row.Intervals);
					break;
			}

			row.Depth = 0;
			for (int i = 0; i < row.Spans.Count; ++i)
			{
				row.Depth = Math.Max(row.Depth, row.Spans.Depth[i] + 1);
			}

			row.Top = y;
			row.Height = row.Kind == Kind.Cpu
				? ZoneHeight
				: (Math.Max(1, row.Depth) * ZoneHeight) + (row.HasRunning ? RunningHeight + 2f : 0f);
			y += row.Height + RowGap;
		}

		CustomMinimumSize = new Vector2(0, y);
		UpdateHover();
		QueueRedraw();
	}

	private float X(long time) =>
		LabelWidth + (float)((time - ViewStart) / (double)(ViewEnd - ViewStart) * (Size.X - LabelWidth));

	private long TimeAt(float x) =>
		ViewStart + (long)((x - LabelWidth) / Mathf.Max(1f, Size.X - LabelWidth) * (ViewEnd - ViewStart));

	public override void _Draw()
	{
		DrawRect(new Rect2(Vector2.Zero, Size), new Color(0, 0, 0, 0.3f));
		Font font = ThemeDB.FallbackFont;
		DrawRuler(font);

		foreach (Row row in _rows)
		{
			DrawRect(new Rect2(0, row.Top, Size.X, row.Height), new Color(1, 1, 1, 0.025f));
			DrawString(font, new Vector2(4, row.Top + 13), Fit(row.Label, LabelWidth - 8),
				HorizontalAlignment.Left, -1, FontSize, new Color(0.8f, 0.88f, 1f));

			if (row.Kind == Kind.Cpu)
			{
				DrawSlices(font, row);
				continue;
			}

			for (int i = 0; i < row.Spans.Count; ++i)
			{
				DrawZone(font, row, i);
			}

			if (row.HasRunning)
			{
				float y = row.Top + (Math.Max(1, row.Depth) * ZoneHeight) + 1f;
				for (int i = 0; i < row.Intervals.Count; ++i)
				{
					(float x0, float x1) = Clip(row.Intervals.Start[i], row.Intervals.End[i]);
					DrawRect(new Rect2(x0, y, x1 - x0, RunningHeight), new Color(0.35f, 0.8f, 0.45f, 0.85f));
				}
			}
		}

		DrawTooltip(font);
	}

	private (float X0, float X1) Clip(long start, long end)
	{
		float x0 = Mathf.Max(LabelWidth, X(start));
		float x1 = Mathf.Min(Size.X, X(end));
		return (x0, Mathf.Max(x0 + 1f, x1));
	}

	private void DrawZone(Font font, Row row, int i)
	{
		(float x0, float x1) = Clip(row.Spans.Start[i], row.Spans.End[i]);
		float y = row.Top + (row.Spans.Depth[i] * ZoneHeight);
		HostTracy.SourceLocation location = HostTracy.Location(row.Spans.SrcLoc[i]);
		Color colour = ZoneColour(location);
		if (row == _hoverRow && i == _hoverIndex)
		{
			colour = colour.Lightened(0.35f);
		}

		DrawRect(new Rect2(x0, y, x1 - x0, ZoneHeight - 1f), colour);
		if (x1 - x0 > 30f)
		{
			string text = Fit($"{location.Name} {FormatNs(row.Spans.End[i] - row.Spans.Start[i])}", x1 - x0 - 6f);
			DrawString(font, new Vector2(x0 + 3f, y + 12f), text, HorizontalAlignment.Left, -1, FontSize,
				new Color(0.05f, 0.05f, 0.05f));
		}
	}

	private void DrawSlices(Font font, Row row)
	{
		for (int i = 0; i < row.Intervals.Count; ++i)
		{
			(float x0, float x1) = Clip(row.Intervals.Start[i], row.Intervals.End[i]);
			ulong thread = row.Intervals.Value[i];
			Color colour = thread == HostTracy.OtherProcessThread
				? new Color(0.4f, 0.4f, 0.4f, 0.7f)
				: HashColour(ThreadName(thread));
			if (row == _hoverRow && i == _hoverIndex)
			{
				colour = colour.Lightened(0.35f);
			}

			DrawRect(new Rect2(x0, row.Top, x1 - x0, ZoneHeight - 1f), colour);
			if (x1 - x0 > 40f)
			{
				DrawString(font, new Vector2(x0 + 3f, row.Top + 12f), Fit(ThreadName(thread), x1 - x0 - 6f),
					HorizontalAlignment.Left, -1, FontSize, new Color(0.05f, 0.05f, 0.05f));
			}
		}
	}

	private void DrawRuler(Font font)
	{
		long span = ViewEnd - ViewStart;
		float width = Mathf.Max(1f, Size.X - LabelWidth);
		double raw = span / width * 100.0;
		double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1.0, raw))));
		double step = raw <= magnitude ? magnitude : raw <= 2 * magnitude ? 2 * magnitude : raw <= 5 * magnitude ? 5 * magnitude : 10 * magnitude;
		long tick = (long)step;
		long first = Origin + ((ViewStart - Origin) / tick * tick);
		for (long t = first; t <= ViewEnd; t += tick)
		{
			float x = X(t);
			if (x < LabelWidth)
			{
				continue;
			}

			DrawLine(new Vector2(x, 0), new Vector2(x, Size.Y), new Color(1, 1, 1, 0.06f));
			DrawString(font, new Vector2(x + 3, 14), FormatNs(t - Origin), HorizontalAlignment.Left, -1, FontSize,
				new Color(1, 1, 1, 0.55f));
		}

		DrawString(font, new Vector2(4, 14), $"view {FormatNs(span)}", HorizontalAlignment.Left, -1, FontSize,
			new Color(1, 1, 1, 0.55f));
	}

	private void DrawTooltip(Font font)
	{
		if (_hoverRow is not { } row || _hoverIndex < 0)
		{
			return;
		}

		var lines = new List<string>();
		if (row.Kind == Kind.Cpu)
		{
			lines.Add(ThreadName(row.Intervals.Value[_hoverIndex]));
			lines.Add($"{FormatNs(row.Intervals.End[_hoverIndex] - row.Intervals.Start[_hoverIndex])} on {row.Label}");
		}
		else
		{
			HostTracy.SourceLocation location = HostTracy.Location(row.Spans.SrcLoc[_hoverIndex]);
			lines.Add(location.Name);
			lines.Add($"{FormatNs(row.Spans.End[_hoverIndex] - row.Spans.Start[_hoverIndex])}   at {FormatNs(row.Spans.Start[_hoverIndex] - Origin)}");
			if (location.Function.Length != 0 && location.Function != location.Name)
			{
				lines.Add(location.Function);
			}

			if (location.File.Length != 0)
			{
				lines.Add($"{location.File}:{location.Line}");
			}
		}

		float width = 0f;
		foreach (string line in lines)
		{
			width = Mathf.Max(width, font.GetStringSize(line, HorizontalAlignment.Left, -1, FontSize).X);
		}

		var box = new Rect2(_mouse + new Vector2(14, 14), new Vector2(width + 12, (lines.Count * 15) + 8));
		if (box.End.X > Size.X)
		{
			box.Position = new Vector2(Mathf.Max(0, Size.X - box.Size.X), box.Position.Y);
		}

		DrawRect(box, new Color(0.08f, 0.09f, 0.11f, 0.95f));
		DrawRect(box, new Color(1, 1, 1, 0.3f), false, 1f);
		for (int i = 0; i < lines.Count; ++i)
		{
			DrawString(font, box.Position + new Vector2(6, 15 + (i * 15)), lines[i], HorizontalAlignment.Left, -1,
				FontSize, i == 0 ? Colors.White : new Color(0.8f, 0.85f, 0.9f));
		}
	}

	public override void _GuiInput(InputEvent @event)
	{
		switch (@event)
		{
			case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel:
				Zoom(wheel.Position.X, wheel.ButtonIndex == MouseButton.WheelUp ? 0.8 : 1.25);
				AcceptEvent();
				break;

			case InputEventMagnifyGesture pinch when pinch.Factor > 0f:
				Zoom(pinch.Position.X, 1.0 / pinch.Factor);
				AcceptEvent();
				break;

			case InputEventPanGesture pan:
				Pan(pan.Delta.X * 8f);
				AcceptEvent();
				break;

			case InputEventMouseButton { Pressed: true, DoubleClick: true, ButtonIndex: MouseButton.Left }:
				if (_hoverRow is { Kind: not Kind.Cpu } row && _hoverIndex >= 0)
				{
					long start = row.Spans.Start[_hoverIndex];
					long end = row.Spans.End[_hoverIndex];
					long margin = Math.Max(1, (end - start) / 20);
					SetView(start - margin, end + margin);
					UserMoved?.Invoke();
				}

				AcceptEvent();
				break;

			// A touchscreen has no hover: a tap shows the details a pointer would.
			case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } press:
				_mouse = press.Position;
				UpdateHover();
				QueueRedraw();
				break;

			case InputEventMouseMotion motion:
				_mouse = motion.Position;
				if ((motion.ButtonMask & MouseButtonMask.Left) != 0 && motion.Relative.X != 0)
				{
					Pan(motion.Relative.X);
				}

				UpdateHover();
				QueueRedraw();
				break;
		}
	}

	/// <summary>Scales the view around the time under <paramref name="x"/>.</summary>
	private void Zoom(float x, double scale)
	{
		long anchor = TimeAt(x);
		SetView(anchor - (long)((anchor - ViewStart) * scale), anchor + (long)((ViewEnd - anchor) * scale));
		UserMoved?.Invoke();
	}

	/// <summary>Moves the view by <paramref name="pixels"/>, as if dragged.</summary>
	private void Pan(float pixels)
	{
		long shift = (long)(-pixels / Mathf.Max(1f, Size.X - LabelWidth) * (ViewEnd - ViewStart));
		SetView(ViewStart + shift, ViewEnd + shift);
		UserMoved?.Invoke();
	}

	public override void _Notification(int what)
	{
		if (what == NotificationMouseExit)
		{
			_hoverRow = null;
			QueueRedraw();
		}
	}

	private void UpdateHover()
	{
		_hoverRow = null;
		_hoverIndex = -1;
		if (_mouse.X < LabelWidth)
		{
			return;
		}

		foreach (Row row in _rows)
		{
			if (_mouse.Y < row.Top || _mouse.Y >= row.Top + row.Height)
			{
				continue;
			}

			if (row.Kind == Kind.Cpu)
			{
				for (int i = 0; i < row.Intervals.Count; ++i)
				{
					(float x0, float x1) = Clip(row.Intervals.Start[i], row.Intervals.End[i]);
					if (_mouse.X >= x0 && _mouse.X < x1)
					{
						_hoverRow = row;
						_hoverIndex = i;
						return;
					}
				}

				return;
			}

			int depth = (int)((_mouse.Y - row.Top) / ZoneHeight);
			for (int i = 0; i < row.Spans.Count; ++i)
			{
				if (row.Spans.Depth[i] != depth)
				{
					continue;
				}

				(float x0, float x1) = Clip(row.Spans.Start[i], row.Spans.End[i]);
				if (_mouse.X >= x0 && _mouse.X < x1)
				{
					_hoverRow = row;
					_hoverIndex = i;
					return;
				}
			}

			return;
		}
	}

	// ---- shared by the Tracy tabs ----------------------------------------------------------

	public static string FormatNs(long ns)
	{
		double abs = Math.Abs((double)ns);
		return abs < 1e3 ? $"{ns} ns"
			: abs < 1e6 ? $"{ns / 1e3:0.##} µs"
			: abs < 1e9 ? $"{ns / 1e6:0.##} ms"
			: $"{ns / 1e9:0.###} s";
	}

	public static Color ZoneColour(HostTracy.SourceLocation location) =>
		location.Color != 0
			? new Color(((location.Color >> 16) & 0xFF) / 255f, ((location.Color >> 8) & 0xFF) / 255f, (location.Color & 0xFF) / 255f)
			: HashColour(location.Name);

	public static Color HashColour(string text)
	{
		uint hash = 2166136261;
		foreach (char c in text)
		{
			hash = (hash ^ c) * 16777619;
		}

		return Color.FromHsv((hash % 360) / 360f, 0.45f, 0.82f);
	}

	private static string Fit(string text, float width)
	{
		int chars = (int)(width / (FontSize * 0.58f));
		return chars <= 1 ? string.Empty : text.Length <= chars ? text : text[..(chars - 1)] + "…";
	}
}
