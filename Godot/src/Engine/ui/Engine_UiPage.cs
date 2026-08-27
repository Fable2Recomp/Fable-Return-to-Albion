using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// The scrolling page widget, `scripts/gameface/page.lua` ported: measures each text item
/// (`calculateBoundingBox` ≙ `MeshInstance3D.GetAabb()`, local box, scale 1 assumed), stacks
/// them, and slides `Contents` under the window stated by `DimensionsImage` (scale.y * 100,
/// never drawn). Position is a 0..1 fraction, 1 = top; an empty page is 100 tall.
/// Not ported, dead as shipped: the `BoxWidthOverride` pass (main.0 pc89..103, never assigned)
/// and the horizontal/2D page types (`Type` is only ever `PAGE_TYPE_VERTICAL`).
/// </summary>
public sealed class Engine_UiPage
{
	/// <summary>`SCROLL_SPEED`: fractions of the page per second at full stick.</summary>
	public const float ScrollSpeed = Engine_UiBoxConstants.PageScrollSpeed;

	/// <summary>`ICON_SIZE`: the scroll thumb's height, subtracted from its travel.</summary>
	public const float IconSize = Engine_UiBoxConstants.PageIconSize;

	/// <summary>The base quad size a scale is stated against (see <see cref="Engine_UiFrame"/>).</summary>
	public const float QuadBaseSize = 100f;

	/// <summary>The five objects `AK.fetchObjects` pulls off a page component.</summary>
	private Node3D? _contents;
	private Node3D? _dimensions;
	private Node3D? _upArrow;
	private Node3D? _downArrow;
	private Node3D? _thumb;

	private readonly List<(MeshInstance3D Node, float Height)> _items = new();

	/// <summary>`VerticalVisualRange`: the visible window height.</summary>
	public float VisualRange { get; private set; } = QuadBaseSize;

	/// <summary>`TotalHeight`: the measured height of every item stacked.</summary>
	public float TotalHeight { get; private set; } = QuadBaseSize;

	/// <summary>`VerticalPosition`, 0..1; 1 is the top.</summary>
	public float Position { get; private set; } = 1f;

	/// <summary>
	/// Pivot of a node this page places, from the owner's live binding. The script writes the
	/// `position.y` attribute (main.4 pc6..17); origin = stated − basis * pivot.
	/// </summary>
	public System.Func<Node3D, Vector3>? PivotOf { get; set; }

	/// <summary>True once <see cref="Bind"/> found a `Contents` and a `DimensionsImage`.</summary>
	public bool IsBound => _contents is not null && _dimensions is not null;

	/// <summary>How many text items the last arrange measured.</summary>
	public int ItemCount => _items.Count;

	/// <summary>`Page.Initialize`: finds the five objects and takes the window off the dimensions quad; false when not a page.</summary>
	public bool Bind(Node3D page)
	{
		_contents = Engine_UiTheme.FindByName(page, "Contents") as Node3D;
		_dimensions = Engine_UiTheme.FindByName(page, "DimensionsImage") as Node3D;
		_upArrow = Engine_UiTheme.FindByName(page, "UpArrow") as Node3D;
		_downArrow = Engine_UiTheme.FindByName(page, "DownArrow") as Node3D;
		_thumb = Engine_UiTheme.FindByName(page, "VerticalScrollingIcon") as Node3D;

		if (!IsBound)
		{
			return false;
		}

		// The window is the dimensions quad's scale; the quad never draws.
		VisualRange = _dimensions!.Scale.Y * QuadBaseSize;
		_dimensions.Visible = false;
		return true;
	}

	/// <summary>
	/// `UpdateItemList` + `CalculateItemSizes` + `ArrangeItemsVertically`. Items are the
	/// elements carrying `textstring`; an empty line still takes its height.
	/// </summary>
	public void Arrange()
	{
		if (!IsBound)
		{
			return;
		}

		_items.Clear();
		Collect(_contents!);

		// Each item's stated `position.y` is the dimensions quad's stated y less the stack so far.
		float top = StatedY(_dimensions!);
		float total = 0f;
		foreach ((MeshInstance3D node, float height) in _items)
		{
			SetStatedY(node, top - total);
			total += height;
		}

		// `if 0 == TotalHeight then TotalHeight = 100`.
		TotalHeight = total == 0f ? QuadBaseSize : total;

		UpdateScrollThumb();
		UpdatePagePosition();
	}

	/// <summary>`Page.onUpdate`'s scroll half: right stick, fractions of the page per second. True when the position moved.</summary>
	public bool Scroll(float stickY, double delta)
	{
		if (!IsBound)
		{
			return false;
		}

		// `VisualHeight / TotalHeight` (main.4 pc29..32); `VisualHeight` is the constant 100
		// (main.0 pc21), not `VerticalVisualRange`.
		float ratio = QuadBaseSize / TotalHeight;
		float moved = Position + (stickY * (float)delta * ScrollSpeed * ratio);
		moved = Mathf.Clamp(moved, 0f, 1f);
		if (Mathf.IsEqualApprox(moved, Position))
		{
			return false;
		}

		Position = moved;
		UpdateScrollThumb();
		UpdatePagePosition();
		return true;
	}

	/// <summary>`UpdateScrollingIcons`: the thumb travels the window less its own height, centred. The script never writes the arrows.</summary>
	private void UpdateScrollThumb()
	{
		if (_thumb is null || !GodotObject.IsInstanceValid(_thumb))
		{
			return;
		}

		float travel = VisualRange - (IconSize / 2f);
		SetStatedY(_thumb, (-(travel / 2f)) + (travel * Position));
	}

	/// <summary>`UpdatePagePosition`: slides `Contents` under the window; the overhang is not clamped, as shipped.</summary>
	private void UpdatePagePosition()
	{
		if (_contents is null || !GodotObject.IsInstanceValid(_contents))
		{
			return;
		}

		SetStatedY(_contents, (TotalHeight - VisualRange) * (1f - Position));
	}

	/// <summary>Every text mesh under `Contents`, with its rendered height.</summary>
	private void Collect(Node node)
	{
		// `GetTableOfElementsWithAttribute(Contents, 'textstring')` (main.2 pc9..13): text
		// elements only; the builder stamps `text` on exactly those. Quads share the 100-unit base mesh.
		if (node is MeshInstance3D mesh && mesh.Mesh is not null && mesh.HasMeta("text"))
		{
			Aabb box = mesh.GetAabb();
			_items.Add((mesh, box.Size.Y));
		}

		foreach (Node child in node.GetChildren())
		{
			Collect(child);
		}
	}

	/// <summary>A node's stated `position.y`, recovered from its origin.</summary>
	private float StatedY(Node3D node) =>
		node.Position.Y + (node.Transform.Basis * Pivot(node)).Y;

	/// <summary>Writes a stated `position.y`, carrying the pivot term.</summary>
	private void SetStatedY(Node3D node, float y)
	{
		Vector3 pivot = Pivot(node);
		Basis basis = node.Transform.Basis;
		Vector3 stated = node.Position + (basis * pivot);
		stated.Y = y;
		node.Position = stated - (basis * pivot);
	}

	private Vector3 Pivot(Node3D node) => PivotOf is null ? Vector3.Zero : PivotOf(node);

	/// <summary>One log line: what the page measured and where it sits.</summary>
	public string Describe() =>
		$"{_items.Count} item(s), {TotalHeight:0} tall in a {VisualRange:0} window, " +
		$"at {Position:0.00}" +
		(_thumb is null ? ", no thumb" : string.Empty) +
		(_upArrow is null && _downArrow is null ? ", no arrows" : string.Empty);
}
