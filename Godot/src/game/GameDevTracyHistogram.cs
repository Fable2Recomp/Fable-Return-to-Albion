using System;
using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// A zone's durations as a histogram (log-spaced bins, shortest to longest instance) with its
/// median and 95th percentile marked. Hover a bar for its range and count.
/// </summary>
public partial class GameDevTracyHistogram : Control
{
	private const int FontSize = 12;

	private HostTracy.FindZoneResult? _result;
	private int _hover = -1;

	public GameDevTracyHistogram()
	{
		CustomMinimumSize = new Vector2(0, 150);
		SizeFlagsHorizontal = SizeFlags.ExpandFill;
		MouseFilter = MouseFilterEnum.Stop;
	}

	public void Show(HostTracy.FindZoneResult? result)
	{
		_result = result;
		QueueRedraw();
	}

	/// <summary>Where a duration falls, 0..1 across the bins.</summary>
	private double Fraction(long ns)
	{
		if (_result is not { } r || r.Max <= r.Min)
		{
			return 0;
		}

		if (r.LogScale && r.Min > 0)
		{
			return (Math.Log(Math.Max(ns, 1)) - Math.Log(r.Min)) / (Math.Log(r.Max) - Math.Log(r.Min));
		}

		return (ns - r.Min) / (double)(r.Max - r.Min);
	}

	/// <summary>The duration at a position, the inverse of <see cref="Fraction"/>.</summary>
	private long At(double t)
	{
		HostTracy.FindZoneResult r = _result!;
		return r.LogScale && r.Min > 0
			? (long)Math.Exp(Math.Log(r.Min) + (t * (Math.Log(r.Max) - Math.Log(r.Min))))
			: r.Min + (long)(t * (r.Max - r.Min));
	}

	public override void _Draw()
	{
		DrawRect(new Rect2(Vector2.Zero, Size), new Color(0, 0, 0, 0.35f));
		Font font = ThemeDB.FallbackFont;
		if (_result is not { Count: > 0 } r)
		{
			DrawString(font, new Vector2(8, 20), "pick a zone above to find all its instances", HorizontalAlignment.Left, -1,
				FontSize, new Color(1, 1, 1, 0.5f));
			return;
		}

		ulong peak = 1;
		foreach (ulong count in r.Histogram)
		{
			peak = Math.Max(peak, count);
		}

		float top = 18f;
		float height = Size.Y - top - 16f;
		float bar = Size.X / r.Histogram.Length;
		for (int i = 0; i < r.Histogram.Length; ++i)
		{
			float h = (float)(r.Histogram[i] / (double)peak * height);
			Color colour = i == _hover ? new Color(0.6f, 0.8f, 1f) : new Color(0.35f, 0.6f, 0.9f);
			DrawRect(new Rect2(i * bar, top + height - h, Mathf.Max(1f, bar - 1f), h), colour);
		}

		foreach ((long ns, string label, Color colour) in new[]
		{
			(r.Median, "median", new Color(0.4f, 0.9f, 0.5f)), (r.P95, "p95", new Color(0.95f, 0.75f, 0.3f)),
		})
		{
			float x = (float)(Fraction(ns) * Size.X);
			DrawLine(new Vector2(x, top), new Vector2(x, top + height), colour, 1.5f);
			DrawString(font, new Vector2(x + 3, top + 12), $"{label} {GameDevTracyTimeline.FormatNs(ns)}",
				HorizontalAlignment.Left, -1, FontSize, colour);
		}

		DrawString(font, new Vector2(4, Size.Y - 3), GameDevTracyTimeline.FormatNs(r.Min), HorizontalAlignment.Left, -1,
			FontSize, new Color(1, 1, 1, 0.6f));
		string max = GameDevTracyTimeline.FormatNs(r.Max);
		DrawString(font, new Vector2(Size.X - 6 - font.GetStringSize(max, HorizontalAlignment.Left, -1, FontSize).X, Size.Y - 3),
			max, HorizontalAlignment.Left, -1, FontSize, new Color(1, 1, 1, 0.6f));

		string heading = _hover >= 0 && _hover < r.Histogram.Length
			? $"{GameDevTracyTimeline.FormatNs(At(_hover / (double)r.Histogram.Length))} – " +
				$"{GameDevTracyTimeline.FormatNs(At((_hover + 1) / (double)r.Histogram.Length))}: {r.Histogram[_hover]:N0} instances"
			: $"{r.Count:N0} instances, {(r.LogScale ? "log" : "linear")} scale";
		DrawString(font, new Vector2(4, 13), heading, HorizontalAlignment.Left, -1, FontSize, new Color(1, 1, 1, 0.8f));
	}

	public override void _GuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseMotion or InputEventMouseButton { Pressed: true } && _result is { } r)
		{
			Vector2 position = @event is InputEventMouse mouse ? mouse.Position : Vector2.Zero;
			_hover = (int)(position.X / Mathf.Max(1f, Size.X) * r.Histogram.Length);
			QueueRedraw();
		}
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
