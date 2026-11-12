using Godot;

namespace Refii.Ui;

/// <summary>
/// THE HOST BOX'S PAPER ON THE ART — the one driver both papers a host box is drawn on use: in
/// game the query box (<see cref="Engine_UiQueryBox"/>, `querybox`'s `Paper`, for questions and
/// notices alike), on the front end the dialog (<see cref="Engine_FrontEndWarning"/>,
/// `frontendmainmenu`'s `dialog`). Both clone the same `paperbox` nine-slice, so one class sizes
/// it by <see cref="Engine_UiHostBoxLayout"/>: the middle column the file ships switched off (the
/// pieces `messageboxmulti`'s `type4`..`type6` slides switch on) stands up and stretches to the
/// height the wrapped text asks for, the corner columns part around it, the text is top-aligned
/// in the window and clipped to it, the prompts move down with the bottom edge, and text past the
/// window scrolls through <see cref="Engine_UiScrollRegion"/> — the right stick, the d-pad, the
/// wheel, and a cloned `Scrollbar` the pointer drags — as every text page does. Text that fits the
/// authored window keeps the authored placement untouched.
/// </summary>
public sealed class Engine_UiPaperBox
{
	/// <summary>The bar's distance in from the paper's right edge, and the arrows' from the window's ends.</summary>
	private const float BarInset = 22f;

	private const float ArrowInset = 10f;

	private readonly Node3D _view;

	private readonly Node3D _paperbox;

	private readonly Vector3 _paperboxScale;

	/// <summary>Canvas units (view space) per paperbox local unit along x — the paper's height axis.</summary>
	private readonly float _unitsPerLocal;

	/// <summary>The paper as authored: the two corner columns.</summary>
	private readonly float _authoredPaper;

	/// <summary>The paper's centre and horizontal extent in view space.</summary>
	private readonly float _centreY;

	private readonly float _left;

	private readonly float _right;

	/// <summary>The nine-slice's rows: each row's two corner pieces and its middle piece.</summary>
	private readonly (Node3D Left, Node3D? Middle, Node3D Right)[] _rows;

	private readonly Vector3[] _rowRest;

	private readonly Node3D[] _prompts;

	private readonly Vector3[] _promptRest;

	private readonly Engine_UiScrollRegion _scroll = new();

	/// <summary>The paper's top edge to the text's top, and the prompts' band above its bottom
	/// edge — measured off the art on the first layout.</summary>
	private float _topPad = -1f;

	private float _belowText;

	private MeshInstance3D? _text;

	private Vector3 _textRest;

	private float _textBase;

	private float _parentScaleY = 1f;

	private bool _relaid;

	private string _key = string.Empty;

	private Engine_UiHostBoxLayout.Fit _fit;

	private float _windowTop;

	private float _windowBottom;

	private Node3D? _bar;

	private Node3D? _up;

	private Node3D? _down;

	private Node3D? _thumb;

	private bool _drag;

	/// <summary>The theme a `Scrollbar` group is cloned from; null draws no bar — the stick,
	/// d-pad and wheel still scroll.</summary>
	public Engine_UiTheme? BarArt { get; set; }

	/// <summary>Whether the text overflows the window.</summary>
	public bool Scrollable => _scroll.Scrollable;

	/// <summary>The last layout, for the log.</summary>
	public string Report { get; private set; } = string.Empty;

	private Engine_UiPaperBox(Node3D view, Node3D paperbox, (Node3D, Node3D?, Node3D)[] rows, Node3D[] prompts)
	{
		_view = view;
		_paperbox = paperbox;
		_paperboxScale = paperbox.Scale;
		_rows = rows;
		_rowRest = new Vector3[rows.Length * 3];
		for (int i = 0; i < rows.Length; ++i)
		{
			_rowRest[i * 3] = rows[i].Item1.Position;
			_rowRest[(i * 3) + 1] = rows[i].Item2?.Position ?? Vector3.Zero;
			_rowRest[(i * 3) + 2] = rows[i].Item3.Position;
		}

		_prompts = prompts;
		_promptRest = new Vector3[prompts.Length];
		for (int i = 0; i < prompts.Length; ++i)
		{
			_promptRest[i] = prompts[i].Position;
		}

		Transform3D rel = RelativeTo(paperbox);
		_unitsPerLocal = rel.Basis.X.Length();
		_authoredPaper = 2f * Engine_UiHostBoxLayout.CornerLocal * _unitsPerLocal;
		_centreY = (rel * Vector3.Zero).Y;

		// The rows span local y [-63.8, 13] (76.8 across, drawn a quarter-turn: the paper's width).
		Vector3 a = rel * new Vector3(0f, -63.8f, 0f);
		Vector3 b = rel * new Vector3(0f, 13f, 0f);
		_left = Mathf.Min(a.X, b.X);
		_right = Mathf.Max(a.X, b.X);
	}

	/// <summary>
	/// The paper under <paramref name="view"/>: the `paperbox` nine-slice under
	/// <paramref name="paperRoot"/>, and the prompt nodes that ride its bottom edge. Null when the
	/// art is not the shipped nine-slice, and the box keeps its authored size.
	/// </summary>
	public static Engine_UiPaperBox? For(Node3D view, Node3D? paperRoot, params Node3D?[] prompts)
	{
		if (paperRoot is null || Engine_UiTheme.FindByName(paperRoot, "paperbox") is not Node3D paperbox)
		{
			return null;
		}

		var rows = new System.Collections.Generic.List<(Node3D, Node3D?, Node3D)>();
		foreach ((string row, string left, string right) in new[]
			{
				("top", "topleft", "topright"),
				("topmiddle", "left", "right"),
				("bottommiddle", "left", "right"),
				("bottom", "topleft", "topright"),
			})
		{
			if (paperbox.GetNodeOrNull<Node3D>(row) is not { } group ||
				group.GetNodeOrNull<Node3D>(left) is not { } leftPiece ||
				group.GetNodeOrNull<Node3D>(right) is not { } rightPiece)
			{
				return null;
			}

			// A drawn build drops the parked middle column; the paper then stretches whole.
			Node3D? middle = group.GetNodeOrNull<MeshInstance3D>("middle") is { Mesh: not null } piece ? piece : null;
			rows.Add((leftPiece, middle, rightPiece));
		}

		var present = new System.Collections.Generic.List<Node3D>();
		foreach (Node3D? prompt in prompts)
		{
			if (prompt is not null)
			{
				present.Add(prompt);
			}
		}

		return new Engine_UiPaperBox(view, paperbox, rows.ToArray(), present.ToArray());
	}

	/// <summary>
	/// Lays the paper out for the text just meshed on <paramref name="text"/>. Text that fits the
	/// authored window between the paper's top and the prompts stays as authored; longer text
	/// grows the paper, is top-aligned in the window, and scrolls past the window's maximum. A new
	/// <paramref name="key"/> (another body) starts at the top.
	/// </summary>
	public void Layout(MeshInstance3D text, string key)
	{
		if (!ReferenceEquals(_text, text))
		{
			RestoreText();
			_text = text;
			_textRest = text.Position;
		}

		text.Position = _textRest;
		Measure(text);
		if (key != _key)
		{
			_key = key;
			_scroll.Rewind();
		}

		float height = text.Mesh is not null ? text.GetAabb().Size.Y : 0f;
		Transform3D parent = text.GetParent() is Node3D up ? RelativeTo(up) : Transform3D.Identity;
		_parentScaleY = Mathf.Max(0.0001f, parent.Basis.Y.Length());
		float restTop = parent.Origin.Y + ((_textRest.Y + (text.Mesh is not null ? text.GetAabb().End.Y : 0f)) * _parentScaleY);
		float restBottom = restTop - (height * _parentScaleY);
		height *= _parentScaleY;

		var authored = new Engine_UiHostBoxLayout.Fit(
			_authoredPaper, _authoredPaper - _topPad - _belowText, 0f);
		float authoredTop = Engine_UiHostBoxLayout.WindowTop(authored, _centreY, _topPad);
		float authoredBottom = Engine_UiHostBoxLayout.WindowBottom(authored, _centreY, _topPad);
		_relaid = restTop > authoredTop + 0.5f || restBottom < authoredBottom - 0.5f;

		float canvas = Engine_UiPointer.VisibleCanvas is { } visible && Mathf.Abs(visible.Size.Y) > 1f
			? Mathf.Abs(visible.Size.Y)
			: Engine_HudLayout.DesignHeight;
		_fit = _relaid
			? Engine_UiHostBoxLayout.For(height, _authoredPaper,
				Engine_UiHostBoxLayout.MaxPaper(canvas, _centreY), _topPad, _belowText)
			: authored;
		_windowTop = Engine_UiHostBoxLayout.WindowTop(_fit, _centreY, _topPad);
		_windowBottom = Engine_UiHostBoxLayout.WindowBottom(_fit, _centreY, _topPad);

		Resize(_fit.PaperHeight);
		float shift = Engine_UiHostBoxLayout.PromptShift(_fit, _authoredPaper);
		for (int i = 0; i < _prompts.Length; ++i)
		{
			_prompts[i].Position = _promptRest[i] + new Vector3(0f, shift, 0f);
		}

		// Top-aligned in the window: the mesh's top on the window's top.
		_textBase = _relaid
			? ((_windowTop - parent.Origin.Y) / _parentScaleY) - (text.Mesh is not null ? text.GetAabb().End.Y : 0f)
			: _textRest.Y;
		Scroll(0f, 0, 0.0);
		Report = _relaid
			? $"paper {_fit.PaperHeight:0} of max {Engine_UiHostBoxLayout.MaxPaper(canvas, _centreY):0} (text {height:0}, window {_fit.TextWindow:0}, scrolls {_fit.ScrollRange:0})"
			: $"paper authored (text {height:0} fits)";
	}

	/// <summary>
	/// One frame of scrolling: the stick (up positive), else the d-pad held (-1 up, +1 down) at
	/// the stick's rate, over <paramref name="delta"/> seconds; then the text, its clip and the
	/// bar are placed.
	/// </summary>
	public void Scroll(float stick, int direction, double delta)
	{
		float drive = stick != 0f ? stick : -direction;
		float offset = _scroll.Step(drive, delta, _fit.ScrollRange);
		if (_text is not null && GodotObject.IsInstanceValid(_text))
		{
			_text.Position = new Vector3(_textRest.X, _textBase + (offset / _parentScaleY), _textRest.Z);
			Clip(_text);
		}

		PlaceBar();
	}

	/// <summary>
	/// The 0..1 position a window point on the bar stands for, or null off it;
	/// <paramref name="anyHeight"/> clamps a drag that has left the bar instead of declining it.
	/// </summary>
	public float? ScrollBarAt(Vector2 at, bool anyHeight = false)
	{
		if (!Scrollable || _bar is not { Visible: true } || _up is null || _down is null || _thumb is null ||
			!_bar.IsInsideTree())
		{
			return null;
		}

		float top = _up.GlobalPosition.Y;
		float bottom = _down.GlobalPosition.Y;
		if (!anyHeight && (Mathf.Abs(at.X - _thumb.GlobalPosition.X) > 20f || at.Y > top + 10f || at.Y < bottom - 10f))
		{
			return null;
		}

		float length = top - bottom;
		return length <= 0f ? 0f : Mathf.Clamp((top - at.Y) / length, 0f, 1f);
	}

	/// <summary>
	/// A click at <paramref name="at"/> the caller has taken: on the bar it jumps there and starts
	/// a drag, and is true so the caller does not read it as an answer.
	/// </summary>
	public bool Press(Vector2 at)
	{
		if (ScrollBarAt(at) is not { } fraction)
		{
			return false;
		}

		_drag = true;
		_scroll.ScrollTo(fraction);
		return true;
	}

	/// <summary>
	/// The pointer's frame, before <see cref="Scroll"/>: a drag follows the pointer's height while
	/// the button is held; the wheel over the box scrolls it, every notch taken so none steps a
	/// list behind the box.
	/// </summary>
	public void Pointer()
	{
		if (Engine_UiPointer.Canvas is not { } at)
		{
			_drag = false;
			return;
		}

		if (_drag)
		{
			if (Input.IsMouseButtonPressed(MouseButton.Left))
			{
				_scroll.ScrollTo(ScrollBarAt(at, anyHeight: true) ?? 0f);
				return;
			}

			_drag = false;
		}

		if (Scrollable && Engine_UiPointer.WheelSteps != 0 &&
			(ScrollBarAt(at) is not null ||
				(Engine_UiPointer.CanvasBounds(_view) is { } bounds && bounds.HasPoint(at))))
		{
			_scroll.AddWheel(Engine_UiPointer.TakeWheel());
		}
	}

	/// <summary>The paper back at its authored size, the text and prompts where the artist put
	/// them — the box closed.</summary>
	public void Release()
	{
		if (_text is null && _fit.PaperHeight <= 0f)
		{
			// Already at rest: a closed surface releases every frame it stays closed.
			return;
		}

		RestoreText();
		Resize(_authoredPaper);
		for (int i = 0; i < _prompts.Length; ++i)
		{
			_prompts[i].Position = _promptRest[i];
		}

		if (_bar is not null && GodotObject.IsInstanceValid(_bar))
		{
			_bar.Visible = false;
		}

		_scroll.Reset();
		_fit = default;
		_key = string.Empty;
		_drag = false;
	}

	private void RestoreText()
	{
		if (_text is not null && GodotObject.IsInstanceValid(_text))
		{
			_text.Position = _textRest;
			Unclip(_text);
		}

		_text = null;
	}

	/// <summary>
	/// The pads, off the art at rest: the text's authored box top below the paper's top (the
	/// box's top for top-aligned text, its middle plus half its height for centred), and the
	/// highest drawn piece of any prompt above the paper's bottom, plus a gap.
	/// </summary>
	private void Measure(MeshInstance3D text)
	{
		if (_topPad >= 0f)
		{
			return;
		}

		float paperTop = _centreY + (_authoredPaper * 0.5f);
		float paperBottom = _centreY - (_authoredPaper * 0.5f);

		int valign = text.HasMeta("text_valign") ? (int)text.GetMeta("text_valign") : 1;
		Vector2 box = text.HasMeta("text_box") ? (Vector2)text.GetMeta("text_box") : Vector2.Zero;
		float boxTop = valign switch
		{
			0 => 0f,
			2 => box.Y,
			_ => box.Y * 0.5f,
		};
		Transform3D parent = text.GetParent() is Node3D up ? RelativeTo(up) : Transform3D.Identity;
		float textTop = (parent * new Vector3(_textRest.X, _textRest.Y + boxTop, _textRest.Z)).Y;
		_topPad = Mathf.Clamp(paperTop - textTop, 16f, _authoredPaper * 0.5f);

		float promptTop = float.MinValue;
		foreach (Node3D prompt in _prompts)
		{
			Top(prompt);
		}

		void Top(Node node)
		{
			if (node is MeshInstance3D { Mesh: not null } mesh)
			{
				Aabb bounds = RelativeTo(mesh) * mesh.GetAabb();
				promptTop = Mathf.Max(promptTop, bounds.End.Y);
			}

			for (int i = 0, count = node.GetChildCount(); i < count; ++i)
			{
				Top(node.GetChild(i));
			}
		}

		_belowText = promptTop > paperBottom
			? Mathf.Min(promptTop - paperBottom + Engine_UiHostBoxLayout.PromptGap, _authoredPaper * 0.5f)
			: _authoredPaper * 0.25f;
	}

	/// <summary>
	/// The nine-slice at <paramref name="paperHeight"/>: the corner columns part by the middle
	/// column's width and the middle pieces stand up across it, as the message box's taller
	/// slides draw it. The authored height puts every piece back as the file has it.
	/// </summary>
	private void Resize(float paperHeight)
	{
		float middle = Engine_UiHostBoxLayout.MiddleLocal(paperHeight, _unitsPerLocal);
		bool grown = middle > 0.01f;
		bool pieces = true;
		foreach ((Node3D _, Node3D? piece, Node3D _) in _rows)
		{
			pieces &= piece is not null;
		}

		if (!pieces)
		{
			// No middle column to stand up: the whole paper stretches along its height axis.
			float stretch = paperHeight / _authoredPaper;
			Engine_UiSceneBuilder.SetLiveScale(_paperbox,
				new Vector3(_paperboxScale.X * stretch, _paperboxScale.Y, _paperboxScale.Z));
			return;
		}

		float half = middle * 0.5f;
		for (int i = 0; i < _rows.Length; ++i)
		{
			(Node3D left, Node3D? piece, Node3D right) = _rows[i];
			left.Position = _rowRest[i * 3] - new Vector3(half, 0f, 0f);
			right.Position = _rowRest[(i * 3) + 2] + new Vector3(half, 0f, 0f);
			if (piece is null)
			{
				continue;
			}

			piece.Visible = grown;
			if (!grown)
			{
				piece.Position = _rowRest[(i * 3) + 1];
				continue;
			}

			// The middle piece's pivot is on its centre line along x, so its stated x is the
			// column's middle and its x scale the column's width over the 100-unit quad.
			Vector3 scale = piece.Transform.Basis.Scale;
			Engine_UiSceneBuilder.SetLiveScale(
				piece, new Vector3(middle / Engine_UiSceneBuilder.QuadBaseSize, scale.Y, scale.Z));
			piece.Position = new Vector3(_rowRest[(i * 3) + 1].X, piece.Position.Y, piece.Position.Z);
			Engine_UiSceneBuilder.FadeSubtree(piece, 1f);
		}
	}

	/// <summary>
	/// The text window on the text's material while it is relaid. `clip_rect` compares in
	/// laid-out space, so the view-space band goes through the view's layout transform; the seat
	/// rides the material as `clip_seat`, restated because a re-mesh builds a fresh material and
	/// the host stamps the seat only as a node enters the tree.
	/// </summary>
	private void Clip(MeshInstance3D text)
	{
		if (!_relaid)
		{
			Unclip(text);
			return;
		}

		if (!_view.IsInsideTree() || Engine_UiSceneBuilder.EnsureOwnedMaterial(text) is not { } material)
		{
			return;
		}

		Transform3D layout = Engine_UiSeats.LayoutTransform(_view);
		Vector3 a = layout * new Vector3(_left - 1000f, _windowBottom, 0f);
		Vector3 b = layout * new Vector3(_right + 1000f, _windowTop, 0f);
		material.SetShaderParameter(ClipRectName, new Vector4(
			Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y), Mathf.Max(a.X, b.X), Mathf.Max(a.Y, b.Y)));
		material.SetShaderParameter(ClipSeatName, SeatOf(_view));
	}

	private static void Unclip(MeshInstance3D text)
	{
		if (Engine_UiSceneBuilder.EnsureOwnedMaterial(text) is { } material)
		{
			material.SetShaderParameter(ClipRectName, new Vector4(-1e9f, -1e9f, 1e9f, 1e9f));
		}
	}

	/// <summary>The seat of the menu layer above <paramref name="node"/>, as the shaders take it.</summary>
	private static Vector4 SeatOf(Node3D node)
	{
		for (Node? up = node.GetParent(); up is not null; up = up.GetParent())
		{
			if (up is Node3D layer && layer.IsInGroup(Engine_UiSeats.MenuLayerGroup))
			{
				return new Vector4(layer.Scale.X, layer.Scale.Y, layer.Position.X, layer.Position.Y);
			}
		}

		return new Vector4(1f, 1f, 0f, 0f);
	}

	private static readonly StringName ClipRectName = "clip_rect";

	private static readonly StringName ClipSeatName = "clip_seat";

	/// <summary>
	/// The bar beside the window while the text overflows: the cloned `Scrollbar`'s arrows at the
	/// window's ends, dimmed at their limit (slide 1 of 2, `sub_828C3488`, as a fade — the books'
	/// stand-in), the thumb riding the fraction between them.
	/// </summary>
	private void PlaceBar()
	{
		if (!Scrollable)
		{
			if (_bar is not null && GodotObject.IsInstanceValid(_bar))
			{
				_bar.Visible = false;
			}

			return;
		}

		if (_bar is null || !GodotObject.IsInstanceValid(_bar))
		{
			if (BarArt?.Art("Scrollbar") is not { } clone)
			{
				return;
			}

			clone.Name = "PaperScrollbar";
			_view.AddChild(clone);
			_bar = clone;
			_up = Engine_UiTheme.FindByName(clone, "UpArrow") as Node3D;
			_down = Engine_UiTheme.FindByName(clone, "DownArrow") as Node3D;
			_thumb = Engine_UiTheme.FindByName(clone, "Icon") as Node3D;

			// The guest hides a registered bar (opacity ÷ 1024) and its scroll object shows it on
			// overflow; the clone comes back to its authored alpha the same way the books' does.
			Engine_UiSceneBuilder.RestateOpacity(clone, 1f);
			Engine_UiSceneBuilder.FadeSubtree(clone, 1f);
		}

		float z = _text is not null && GodotObject.IsInstanceValid(_text) && _text.GetParent() is Node3D parent
			? (RelativeTo(parent) * _textRest).Z
			: 0f;
		_bar.Position = new Vector3(_right - BarInset, 0f, z);
		_bar.Visible = true;
		if (_up is null || _down is null || _thumb is null)
		{
			return;
		}

		float top = _windowTop - ArrowInset;
		float bottom = _windowBottom + ArrowInset;
		_up.Position = new Vector3(0f, top, _up.Position.Z);
		_down.Position = new Vector3(0f, bottom, _down.Position.Z);
		float fraction = _scroll.Fraction;
		_thumb.Position = new Vector3(0f, Mathf.Lerp(top, bottom, fraction), _thumb.Position.Z);
		Engine_UiSceneBuilder.FadeSubtree(_up, fraction > 0.01f ? 1f : 0.35f);
		Engine_UiSceneBuilder.FadeSubtree(_down, fraction < 0.99f ? 1f : 0.35f);
	}

	/// <summary>A node's transform in the view's space — the canvas the art was authored in —
	/// composed up the parents, so it holds before the view is in the tree.</summary>
	private Transform3D RelativeTo(Node3D node)
	{
		Transform3D transform = Transform3D.Identity;
		for (Node? at = node; at is Node3D spatial && !ReferenceEquals(at, _view); at = at.GetParent())
		{
			transform = spatial.Transform * transform;
		}

		return transform;
	}
}
