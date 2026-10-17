using System;
using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// The capture's frames as bars, one per frame, coloured against the budgets (8.33 ms for 120 fps,
/// 16.7 ms for 60). Following, it shows the newest; the wheel, a drag or a pan gesture scrolls
/// back through the capture and lets go of the newest; a click or tap opens that frame in the
/// timeline.
/// </summary>
public partial class GameDevTracyFrames : Control
{
	private const float Bar = 5f;
	private const int FontSize = 12;
	private const double Budget120 = 1e9 / 120.0;
	private const double Budget60 = 1e9 / 60.0;

	private long[] _start = Array.Empty<long>();
	private long[] _end = Array.Empty<long>();
	private int _count;
	private ulong _first;
	private ulong _total;
	private ulong _selected = ulong.MaxValue;
	private int _hover = -1;

	/// <summary>Showing the newest frames.</summary>
	public bool Follow { get; set; } = true;

	/// <summary>A frame was picked: its start, end and index.</summary>
	public event Action<long, long, ulong>? FrameSelected;

	/// <summary>The player scrolled away from the newest frames.</summary>
	public event Action? UserMoved;

	public GameDevTracyFrames()
	{
		CustomMinimumSize = new Vector2(0, 96);
		SizeFlagsHorizontal = SizeFlags.ExpandFill;
		MouseFilter = MouseFilterEnum.Stop;
	}

	public ulong Total => _total;

	public void Clear()
	{
		_count = 0;
		_total = 0;
		_first = 0;
		_selected = ulong.MaxValue;
		QueueRedraw();
	}

	/// <summary>The newest frames' span, for a following timeline: the last <paramref name="frames"/>.</summary>
	public (long Start, long End)? Newest(int frames)
	{
		if (_count == 0)
		{
			return null;
		}

		int from = Math.Max(0, _count - frames);
		return (_start[from], _end[_count - 1]);
	}

	public void Reload()
	{
		int visible = Math.Max(1, (int)(Size.X / Bar));
		_total = HostTracy.Frames(0, 0).Total;
		if (Follow)
		{
			_first = _total > (ulong)visible ? _total - (ulong)visible : 0;
		}

		_first = Math.Min(_first, _total > 0 ? _total - 1 : 0);
		(long[] start, long[] end, int count, _) = HostTracy.Frames(_first, visible);
		if (_start.Length < count)
		{
			_start = new long[visible];
			_end = new long[visible];
		}

		Array.Copy(start, _start, count);
		Array.Copy(end, _end, count);
		_count = count;
		QueueRedraw();
	}

	public override void _Draw()
	{
		DrawRect(new Rect2(Vector2.Zero, Size), new Color(0, 0, 0, 0.35f));
		Font font = ThemeDB.FallbackFont;
		if (_count == 0)
		{
			DrawString(font, new Vector2(8, 20), "no frames yet", HorizontalAlignment.Left, -1, FontSize,
				new Color(1, 1, 1, 0.5f));
			return;
		}

		double top = Budget60 * 1.25;
		long sum = 0;
		long worst = 0;
		for (int i = 0; i < _count; ++i)
		{
			long duration = _end[i] - _start[i];
			top = Math.Max(top, duration);
			sum += duration;
			worst = Math.Max(worst, duration);
		}

		float height = Size.Y - 16f;
		float Y(double ns) => Size.Y - (float)(ns / top * height);
		foreach ((double ns, string label) in new[] { (Budget120, "120 fps"), (Budget60, "60 fps") })
		{
			float y = Y(ns);
			DrawLine(new Vector2(0, y), new Vector2(Size.X, y), new Color(1, 1, 1, 0.18f));
			DrawString(font, new Vector2(4, y - 2), label, HorizontalAlignment.Left, -1, 11, new Color(1, 1, 1, 0.4f));
		}

		for (int i = 0; i < _count; ++i)
		{
			long duration = _end[i] - _start[i];
			Color colour = duration <= Budget120 ? new Color(0.35f, 0.8f, 0.45f)
				: duration <= Budget60 ? new Color(0.9f, 0.8f, 0.3f)
				: new Color(0.9f, 0.35f, 0.3f);
			if (_first + (ulong)i == _selected)
			{
				colour = Colors.White;
			}
			else if (i == _hover)
			{
				colour = colour.Lightened(0.4f);
			}

			float x = i * Bar;
			DrawRect(new Rect2(x, Y(duration), Bar - 1f, Size.Y - Y(duration)), colour);
		}

		string text = _hover >= 0 && _hover < _count
			? $"frame {_first + (ulong)_hover}: {GameDevTracyTimeline.FormatNs(_end[_hover] - _start[_hover])}"
			: $"frames {_first}-{_first + (ulong)_count - 1} of {_total}   ·   mean {GameDevTracyTimeline.FormatNs(sum / _count)}   ·   worst {GameDevTracyTimeline.FormatNs(worst)}";
		DrawString(font, new Vector2(Size.X - 8 - font.GetStringSize(text, HorizontalAlignment.Left, -1, FontSize).X, 13),
			text, HorizontalAlignment.Left, -1, FontSize, new Color(1, 1, 1, 0.75f));
	}

	public override void _GuiInput(InputEvent @event)
	{
		switch (@event)
		{
			case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel:
			{
				int visible = Math.Max(1, (int)(Size.X / Bar));
				Scroll(Math.Max(1, visible / 4) * (wheel.ButtonIndex == MouseButton.WheelUp ? -1 : 1));
				AcceptEvent();
				break;
			}

			case InputEventPanGesture pan:
				_dragged += -pan.Delta.X * 8f;
				ScrollByDrag();
				AcceptEvent();
				break;

			case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click:
			{
				int index = (int)(click.Position.X / Bar);
				if (index >= 0 && index < _count)
				{
					_selected = _first + (ulong)index;
					FrameSelected?.Invoke(_start[index], _end[index], _selected);
					QueueRedraw();
				}

				AcceptEvent();
				break;
			}

			case InputEventMouseMotion motion:
				_hover = (int)(motion.Position.X / Bar);
				if ((motion.ButtonMask & MouseButtonMask.Left) != 0 && motion.Relative.X != 0)
				{
					_dragged -= motion.Relative.X;
					ScrollByDrag();
				}

				QueueRedraw();
				break;
		}
	}

	/// <summary>Drag distance not yet worth a whole frame.</summary>
	private float _dragged;

	private void ScrollByDrag()
	{
		int frames = (int)(_dragged / Bar);
		if (frames != 0)
		{
			_dragged -= frames * Bar;
			Scroll(frames);
		}
	}

	private void Scroll(long frames)
	{
		int visible = Math.Max(1, (int)(Size.X / Bar));
		_first = (ulong)Math.Clamp((long)_first + frames, 0, Math.Max(0, (long)_total - visible));
		Follow = false;
		UserMoved?.Invoke();
		Reload();
	}

	public override void _Notification(int what)
	{
		if (what == NotificationMouseExit)
		{
			_hover = -1;
			QueueRedraw();
		}
	}
}
