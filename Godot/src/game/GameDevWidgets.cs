using System.Collections.Generic;
using Godot;

namespace Refii.Game;

/// <summary>
/// A scrolling line graph of millisecond series — newest on the right — with the frame-budget
/// lines drawn across it. The vertical scale follows the tallest visible sample, never below the
/// highest budget line, so a quiet frame does not magnify noise.
/// </summary>
public partial class GameDevGraph : Control
{
	public sealed class Series
	{
		public required string Name { get; init; }
		public required Color Colour { get; init; }
		public float[] Samples { get; init; } = new float[Capacity];
	}

	public const int Capacity = 360;

	/// <summary>Budget lines: 120 fps (the target at 1440p) and 60 fps.</summary>
	public (float Ms, string Label)[] Budgets { get; set; } =
	{
		(1000f / 120f, "120 fps"), (1000f / 60f, "60 fps"),
	};

	/// <summary>The unit the legend prints after a value.</summary>
	public string Unit { get; set; } = "ms";

	public List<Series> Lines { get; } = new();

	private int _head;
	private int _count;

	/// <summary>Reused between draws — the menu redraws every frame it is open.</summary>
	private Vector2[] _points = System.Array.Empty<Vector2>();

	public GameDevGraph()
	{
		CustomMinimumSize = new Vector2(0, 220);
		SizeFlagsHorizontal = SizeFlags.ExpandFill;
	}

	/// <summary>One sample for every series, in <see cref="Lines"/> order.</summary>
	public void Push(params float[] values)
	{
		for (int i = 0; i < Lines.Count && i < values.Length; ++i)
		{
			Lines[i].Samples[_head] = values[i];
		}

		_head = (_head + 1) % Capacity;
		_count = System.Math.Min(_count + 1, Capacity);
		QueueRedraw();
	}

	private float Sample(Series series, int age) =>
		series.Samples[((_head - 1 - age) % Capacity + Capacity) % Capacity];

	public override void _Draw()
	{
		Vector2 size = Size;
		DrawRect(new Rect2(Vector2.Zero, size), new Color(0, 0, 0, 0.35f));
		if (_count == 0 || size.X < 10f || size.Y < 10f)
		{
			return;
		}

		float top = 0f;
		foreach ((float ms, _) in Budgets)
		{
			top = Mathf.Max(top, ms);
		}

		foreach (Series series in Lines)
		{
			for (int age = 0; age < _count; ++age)
			{
				top = Mathf.Max(top, Sample(series, age));
			}
		}

		top *= 1.1f;
		Font font = ThemeDB.FallbackFont;
		const int fontSize = 12;
		float Y(float value) => size.Y - (Mathf.Clamp(value, 0f, top) / top * size.Y);

		foreach ((float ms, string label) in Budgets)
		{
			float y = Y(ms);
			DrawLine(new Vector2(0, y), new Vector2(size.X, y), new Color(1, 1, 1, 0.18f), 1f);
			DrawString(font, new Vector2(4, y - 3), $"{label} ({ms:0.0} {Unit})",
				HorizontalAlignment.Left, -1, fontSize, new Color(1, 1, 1, 0.45f));
		}

		float step = size.X / (Capacity - 1);
		if (_points.Length != _count)
		{
			_points = new Vector2[_count];
		}

		Vector2[] points = _points;
		foreach (Series series in Lines)
		{
			for (int age = 0; age < _count; ++age)
			{
				points[_count - 1 - age] =
					new Vector2(size.X - (age * step), Y(Sample(series, age)));
			}

			if (_count >= 2)
			{
				DrawPolyline(points, series.Colour, 1.5f, true);
			}
		}

		// Legend: the newest value of each series, top right.
		float x = size.X - 8f;
		for (int i = Lines.Count - 1; i >= 0; --i)
		{
			string text = $"{Lines[i].Name} {Sample(Lines[i], 0):0.0}";
			float width = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X;
			x -= width;
			DrawString(font, new Vector2(x, 14), text, HorizontalAlignment.Left, -1, fontSize,
				Lines[i].Colour);
			x -= 14f;
		}

		DrawString(font, new Vector2(size.X - 60, size.Y - 4), $"max {top / 1.1f:0.0}",
			HorizontalAlignment.Left, -1, fontSize, new Color(1, 1, 1, 0.45f));
	}
}

/// <summary>
/// A sortable table over Godot's <see cref="Tree"/>: click a column title to sort by it, again
/// to reverse. Rows are reused between refreshes, so a table refreshed twice a second does not
/// rebuild its items — and a selection survives a refresh when its key does.
/// </summary>
public partial class GameDevTable : Tree
{
	private readonly List<string[]> _rows = new();
	private readonly List<TreeItem> _items = new();
	private TreeItem? _root;
	private int _sortColumn = -1;
	private bool _descending = true;

	/// <summary>The selected row's first column, or null.</summary>
	public string? SelectedKey { get; private set; }

	/// <summary>Raised when the player picks a row, with its cells.</summary>
	public event System.Action<string[]>? Picked;

	public GameDevTable(string[] titles, int sortColumn = -1)
	{
		Columns = titles.Length;
		ColumnTitlesVisible = true;
		HideRoot = true;
		SelectMode = SelectModeEnum.Row;
		SizeFlagsVertical = SizeFlags.ExpandFill;
		SizeFlagsHorizontal = SizeFlags.ExpandFill;
		for (int i = 0; i < titles.Length; ++i)
		{
			SetColumnTitle(i, titles[i]);
			SetColumnExpand(i, i == 0);
			SetColumnClipContent(i, true);
			if (i > 0)
			{
				SetColumnCustomMinimumWidth(i, 90);
			}
		}

		_sortColumn = sortColumn;
		ColumnTitleClicked += (column, _) =>
		{
			_descending = column != _sortColumn || !_descending;
			_sortColumn = (int)column;
			// Deferred: the tree refuses to create items inside its own mouse event.
			string[][] rows = _rows.ToArray();
			Callable.From(() => Show(rows)).CallDeferred();
		};

		ItemSelected += () =>
		{
			if (GetSelected() is { } item && _items.IndexOf(item) is int index and >= 0 &&
				index < _rows.Count)
			{
				SelectedKey = _rows[index][0];
				Picked?.Invoke(_rows[index]);
			}
		};
	}

	public void SetColumnWidth(int column, int width) =>
		SetColumnCustomMinimumWidth(column, width);

	/// <summary>Shows these rows, sorted by the chosen column (numbers compared as numbers).</summary>
	public void Show(IReadOnlyList<string[]> rows)
	{
		_rows.Clear();
		_rows.AddRange(rows);
		if (_sortColumn >= 0 && _sortColumn < Columns)
		{
			int column = _sortColumn;
			_rows.Sort((a, b) =>
			{
				int order = Compare(a[column], b[column]);
				return _descending ? -order : order;
			});
		}

		_root ??= CreateItem();
		while (_items.Count < _rows.Count)
		{
			_items.Add(CreateItem(_root));
		}

		while (_items.Count > _rows.Count)
		{
			_items[^1].Free();
			_items.RemoveAt(_items.Count - 1);
		}

		for (int r = 0; r < _rows.Count; ++r)
		{
			for (int c = 0; c < Columns && c < _rows[r].Length; ++c)
			{
				if (_items[r].GetText(c) != _rows[r][c])
				{
					_items[r].SetText(c, _rows[r][c]);
				}
			}

			if (SelectedKey is not null && _rows[r][0] == SelectedKey && !_items[r].IsSelected(0))
			{
				_items[r].Select(0);
			}
		}
	}

	private static int Compare(string a, string b)
	{
		bool na = double.TryParse(Trim(a), System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out double x);
		bool nb = double.TryParse(Trim(b), System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out double y);
		return na && nb ? x.CompareTo(y) : string.Compare(a, b, System.StringComparison.OrdinalIgnoreCase);

		static string Trim(string s) => s.TrimEnd('%', ' ', 'M', 'K', 'B', 's');
	}
}
