using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// One Tracy plot over the capture: its values as a line between its own minimum and maximum,
/// with the name, the range and the newest value. Values are formatted by the plot's kind —
/// numbers, memory, percentages or watts.
/// </summary>
public partial class GameDevTracyPlot : Control
{
	private const int FontSize = 12;
	private const int Points = 800;

	private readonly long[] _time = new long[Points];
	private readonly double[] _value = new double[Points];
	private Vector2[] _line = System.Array.Empty<Vector2>();
	private int _count;
	private long _t0;
	private long _t1 = 1;
	private HostTracy.PlotInfo _plot;

	public GameDevTracyPlot()
	{
		CustomMinimumSize = new Vector2(0, 74);
		SizeFlagsHorizontal = SizeFlags.ExpandFill;
	}

	public void Show(HostTracy.PlotInfo plot, long t0, long t1)
	{
		_plot = plot;
		_t0 = t0;
		_t1 = System.Math.Max(t0 + 1, t1);
		_count = HostTracy.PlotData(plot.Index, t0, t1, _time, _value);
		QueueRedraw();
	}

	public override void _Draw()
	{
		DrawRect(new Rect2(Vector2.Zero, Size), new Color(0, 0, 0, 0.3f));
		Font font = ThemeDB.FallbackFont;
		double min = _plot.Min;
		double max = _plot.Max > min ? _plot.Max : min + 1.0;
		float top = 18f;
		float height = Size.Y - top - 4f;
		if (_count > 0)
		{
			if (_line.Length != _count)
			{
				_line = new Vector2[_count];
			}

			for (int i = 0; i < _count; ++i)
			{
				float x = (float)((_time[i] - _t0) / (double)(_t1 - _t0) * Size.X);
				float y = top + height - (float)((_value[i] - min) / (max - min) * height);
				_line[i] = new Vector2(x, y);
			}

			if (_count >= 2)
			{
				DrawPolyline(_line, GameDevTracyTimeline.HashColour(_plot.Name), 1.5f, true);
			}
		}

		string newest = _count > 0 ? Format(_value[_count - 1]) : "—";
		DrawString(font, new Vector2(4, 13), $"{_plot.Name}   {_plot.Count:N0} points   min {Format(_plot.Min)}   max {Format(_plot.Max)}   last {newest}",
			HorizontalAlignment.Left, -1, FontSize, new Color(1, 1, 1, 0.8f));
	}

	private string Format(double value) => _plot.Format switch
	{
		1 => $"{value / 1048576.0:0.0} MB",
		2 => $"{value:0.0} %",
		3 => $"{value:0.00} W",
		_ => System.Math.Abs(value) >= 1e6 || (value != 0 && System.Math.Abs(value) < 1e-3) ? value.ToString("0.###e0") : value.ToString("0.###"),
	};
}
