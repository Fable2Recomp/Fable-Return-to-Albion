using System;
using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// The sample call tree as a flame graph: outermost callers on top, each function as wide as the
/// samples that passed through it, its callees beneath. Click a function to fill the width with it,
/// right-click (or <see cref="ResetFocus"/>) to step back out; hover or tap for its numbers.
/// </summary>
public partial class GameDevTracyFlame : Control
{
	private const float RowHeight = 18f;
	private const int FontSize = 12;

	private readonly List<HostTracy.TreeNode> _nodes = new();
	private double[] _x = Array.Empty<double>();
	private double[] _w = Array.Empty<double>();
	private ulong _samples;
	private int _focus = -1;
	private int _hover = -1;
	private Vector2 _mouse;

	/// <summary>A function was picked: its address.</summary>
	public event Action<ulong, string>? Picked;

	public GameDevTracyFlame()
	{
		SizeFlagsHorizontal = SizeFlags.ExpandFill;
		SizeFlagsVertical = SizeFlags.ExpandFill;
		MouseFilter = MouseFilterEnum.Stop;
		ClipContents = true;
	}

	/// <summary>Lays the tree out: x and width as fractions of every sample.</summary>
	public void Show(List<HostTracy.TreeNode> nodes, ulong samples)
	{
		_nodes.Clear();
		_nodes.AddRange(nodes);
		_samples = Math.Max(1, samples);
		_x = new double[nodes.Count];
		_w = new double[nodes.Count];
		var next = new double[nodes.Count];
		double rootX = 0;
		int depth = 0;
		for (int i = 0; i < nodes.Count; ++i)
		{
			HostTracy.TreeNode node = nodes[i];
			_w[i] = node.Total / (double)_samples;
			if (node.Parent < 0)
			{
				_x[i] = rootX;
				rootX += _w[i];
			}
			else
			{
				_x[i] = next[node.Parent];
				next[node.Parent] += _w[i];
			}

			next[i] = _x[i];
			depth = Math.Max(depth, node.Depth + 1);
		}

		_focus = -1;
		_hover = -1;
		CustomMinimumSize = new Vector2(0, (depth * RowHeight) + 24);
		QueueRedraw();
	}

	public void ResetFocus()
	{
		_focus = -1;
		QueueRedraw();
	}

	private bool IsAncestorOfFocus(int node)
	{
		for (int at = _focus; at >= 0; at = _nodes[at].Parent)
		{
			if (at == node)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>A node's on-screen span, or null when it is outside the focus.</summary>
	private (float X0, float X1)? Span(int i)
	{
		double x0 = 0;
		double width = 1;
		if (_focus >= 0)
		{
			if (IsAncestorOfFocus(i))
			{
				return (0f, Size.X);
			}

			x0 = _x[_focus];
			width = _w[_focus];
			if (_x[i] < x0 - 1e-12 || _x[i] + _w[i] > x0 + width + 1e-12)
			{
				return null;
			}
		}

		float a = (float)((_x[i] - x0) / width * Size.X);
		float b = (float)((_x[i] + _w[i] - x0) / width * Size.X);
		return b - a < 0.5f ? null : (a, b);
	}

	public override void _Draw()
	{
		DrawRect(new Rect2(Vector2.Zero, Size), new Color(0, 0, 0, 0.3f));
		Font font = ThemeDB.FallbackFont;
		if (_nodes.Count == 0)
		{
			DrawString(font, new Vector2(8, 20), "no samples in this range", HorizontalAlignment.Left, -1, FontSize,
				new Color(1, 1, 1, 0.5f));
			return;
		}

		for (int i = 0; i < _nodes.Count; ++i)
		{
			if (Span(i) is not { } span)
			{
				continue;
			}

			float y = _nodes[i].Depth * RowHeight;
			Color colour = GameDevTracyTimeline.HashColour(_nodes[i].Name);
			if (i == _hover)
			{
				colour = colour.Lightened(0.35f);
			}

			DrawRect(new Rect2(span.X0, y, Mathf.Max(1f, span.X1 - span.X0 - 1f), RowHeight - 1f), colour);
			if (span.X1 - span.X0 > 40f)
			{
				int chars = (int)((span.X1 - span.X0 - 6f) / (FontSize * 0.58f));
				string name = _nodes[i].Name;
				string text = name.Length <= chars ? name : chars > 1 ? name[..(chars - 1)] + "…" : string.Empty;
				DrawString(font, new Vector2(span.X0 + 3f, y + 13f), text, HorizontalAlignment.Left, -1, FontSize,
					new Color(0.05f, 0.05f, 0.05f));
			}
		}

		if (_hover >= 0)
		{
			HostTracy.TreeNode node = _nodes[_hover];
			string[] lines =
			{
				node.Name,
				$"{node.Total:N0} samples ({node.Total * 100.0 / _samples:0.0} %)   self {node.Self:N0} ({node.Self * 100.0 / _samples:0.0} %)",
			};
			float width = 0f;
			foreach (string line in lines)
			{
				width = Mathf.Max(width, font.GetStringSize(line, HorizontalAlignment.Left, -1, FontSize).X);
			}

			var box = new Rect2(_mouse + new Vector2(14, 14), new Vector2(width + 12, 38));
			if (box.End.X > Size.X)
			{
				box.Position = new Vector2(Mathf.Max(0, Size.X - box.Size.X), box.Position.Y);
			}

			DrawRect(box, new Color(0.08f, 0.09f, 0.11f, 0.95f));
			DrawRect(box, new Color(1, 1, 1, 0.3f), false, 1f);
			DrawString(font, box.Position + new Vector2(6, 15), lines[0], HorizontalAlignment.Left, -1, FontSize, Colors.White);
			DrawString(font, box.Position + new Vector2(6, 30), lines[1], HorizontalAlignment.Left, -1, FontSize,
				new Color(0.8f, 0.85f, 0.9f));
		}
	}

	private int HitTest(Vector2 position)
	{
		int depth = (int)(position.Y / RowHeight);
		for (int i = 0; i < _nodes.Count; ++i)
		{
			if (_nodes[i].Depth == depth && Span(i) is { } span && position.X >= span.X0 && position.X < span.X1)
			{
				return i;
			}
		}

		return -1;
	}

	public override void _GuiInput(InputEvent @event)
	{
		switch (@event)
		{
			case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }:
				_focus = _focus >= 0 ? _nodes[_focus].Parent : -1;
				QueueRedraw();
				AcceptEvent();
				break;

			case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } press:
			{
				_mouse = press.Position;
				int hit = HitTest(press.Position);
				_hover = hit;
				if (hit >= 0)
				{
					_focus = hit;
					Picked?.Invoke(_nodes[hit].Address, _nodes[hit].Name);
				}

				QueueRedraw();
				AcceptEvent();
				break;
			}

			case InputEventMouseMotion motion:
				_mouse = motion.Position;
				_hover = HitTest(motion.Position);
				QueueRedraw();
				break;
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
