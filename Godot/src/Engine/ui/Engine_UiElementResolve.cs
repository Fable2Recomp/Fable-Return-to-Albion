using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// The element pipeline, once: where an element sits, whether it draws, and how opaque it is,
/// resolved for a whole document in one pass. Every consumer (scene builder, owned book, HUD
/// layout and view, world-icon driver) goes through this rather than re-deriving a term.
///
/// The terms are: position accumulates `parent + parentScale * child` (scale composes); opacity
/// accumulates down the tree; visibility is `globalactive` AND the live slide AND the parent
/// chain, since a book turns its pages by slide with the elements still active.
///
/// The algorithm and the state are the guest's; the canvas is ours, so nothing here assumes a
/// fixed 960x720.
/// </summary>
public sealed class Engine_UiElementResolve
{
	private readonly Engine_UiScreen _screen;

	public Engine_UiElementResolve(Engine_UiScreen screen)
	{
		_screen = screen;
		int count = screen.Nodes.Length;
		Drawn = new bool[count];
		Opacity = new float[count];
		Own = new float[count];
		Stated = new bool[count];
		Active = new bool[count];
		Canvas = new Vector2[count];
		_slid = new bool[count];
		_slidOpacity = new float[count];
		SlidPosition = new Vector3[count];
		SlidScale = new Vector3[count];

		for (int i = 0; i < count; ++i)
		{
			Drawn[i] = true;
			Opacity[i] = 1f;
			Own[i] = 1f;
			Active[i] = true;
			_slid[i] = true;

			UiNode node = screen.Nodes[i];
			_slidOpacity[i] = node.Opacity;
			SlidPosition[i] = new Vector3(node.PositionX, node.PositionY, node.PositionZ);
			SlidScale[i] = new Vector3(node.ScaleX, node.ScaleY, node.ScaleZ);
		}

		ResolveCanvas();
	}

	/// <summary>Whether each element draws: its own gate, its slide, and its parents'.</summary>
	public bool[] Drawn { get; }

	/// <summary>Each element's accumulated opacity, 0..1.</summary>
	public float[] Opacity { get; }

	/// <summary>
	/// Each element's own opacity term, 0..1 — before its parents are multiplied in. Kept so the
	/// first ancestor that zeroed can be found; the accumulated number alone cannot say.
	/// </summary>
	public float[] Own { get; }

	/// <summary>
	/// Whether each element's own term came from the guest's stated channel this pass rather than
	/// from the authored or slid value — indistinguishable once multiplied.
	/// </summary>
	public bool[] Stated { get; }

	/// <summary>
	/// The guest's OWN gate term per element — its `globalactive`, before the slide and the
	/// parent chain are folded in.
	/// </summary>
	public bool[] Active { get; }

	/// <summary>Whether the live SLIDE leaves each element on.</summary>
	public bool[] Slid => _slid;

	/// <summary>
	/// Why an element is not drawn — the first element at or above it that is off, and which term
	/// did it: `globalactive` (the guest says it is not up) or its slide (the book is on another
	/// page). Empty when the element draws.
	/// </summary>
	public string WhyDark(int ordinal)
	{
		UiNode[] nodes = _screen.Nodes;
		if (ordinal < 0 || ordinal >= nodes.Length || Drawn[ordinal])
		{
			return string.Empty;
		}

		for (UiNode? at = nodes[ordinal]; at is not null; at = at.Parent)
		{
			int up = at.Ordinal;
			if (up < 0 || up >= Drawn.Length || (Active[up] && _slid[up]))
			{
				continue;
			}

			return $"'{at.Name}'#{up} ({(Active[up] ? "its SLIDE" : "globalactive")})";
		}

		return "no element at fault (a parent chain we did not resolve)";
		}

	/// <summary>Each element's authored place on the canvas, scale composed.</summary>
	public Vector2[] Canvas { get; }

	private readonly bool[] _slid;
	private readonly float[] _slidOpacity;
	private int[] _slidesShown = System.Array.Empty<int>();

	/// <summary>When each owner's stated slide last changed — the order the fold applies them in.</summary>
	private int[] _slideStamp = System.Array.Empty<int>();

	private int _slideStampCounter;

	/// <summary>
	/// Each element's position and scale after the live slide's own attribute writes. A consumer
	/// driving cloned art must base on these, not the authored values — the clone came from a
	/// tree parsed under a different slide.
	/// </summary>
	public Vector3[] SlidPosition { get; }

	public Vector3[] SlidScale { get; }

	/// <summary>Bumped whenever the slide pass ran, so a consumer can rewrite only on a change.</summary>
	public int SlideRevision { get; private set; }

	/// <summary>
	/// An element's authored place on the canvas, without needing an instance. A child's position
	/// is stated in its parent's space, so accumulate `parent + parentScale * child`, never a
	/// plain sum.
	/// </summary>
	public static Vector2 CanvasPosition(UiNode node)
	{
		var at = new Vector2(node.PositionX, node.PositionY);
		for (UiNode? up = node.Parent; up is not null; up = up.Parent)
		{
			at = new Vector2(
				up.PositionX + (up.ScaleX * at.X),
				up.PositionY + (up.ScaleY * at.Y));
		}

		return at;
	}

	private void ResolveCanvas()
	{
		UiNode[] nodes = _screen.Nodes;
		for (int i = 0; i < nodes.Length; ++i)
		{
			Canvas[i] = CanvasPosition(nodes[i]);
		}
	}

	/// <summary>
	/// Applies one sample of the guest's live state. Every argument is optional: an unsampled
	/// channel leaves that term at its authored value.
	/// </summary>
	/// <param name="slides">The live slide per element — how a book turns its pages.</param>
	/// <param name="active">The `globalactive` draw bit per element.</param>
	/// <param name="opacity">The STATED opacity per element, 0..100; accumulated here.</param>
	public void Apply(
		IReadOnlyList<int>? slides,
		IReadOnlyList<bool>? active,
		IReadOnlyList<float?>? opacity,
		IReadOnlyList<bool>? switchedOn = null)
	{
		UiNode[] nodes = _screen.Nodes;

		// The slide pass, only when the selection changed — it walks every slide of every
		// element. The fold below is cheap and runs every time.
		if (slides is not null && SlidesChanged(slides))
		{
			BgfSlideState state = _screen.Document.ComputeSlideState((owner, owned) =>
			{
				if (owner < 0 || owner >= slides.Count || slides[owner] <= 0)
				{
					return null;
				}

				// Slide 0 is the master, which ComputeSlideState already folds in as the
				// baseline; the guest's numbering counts the rest from one.
				int wanted = slides[owner];
				int seen = 0;
				foreach (BgfSlide candidate in owned)
				{
					if (!candidate.IsMaster && ++seen == wanted)
					{
						return candidate;
					}
				}

				return null;
			}, owner => owner >= 0 && owner < _slideStamp.Length ? _slideStamp[owner] : 0);

			for (int i = 0; i < nodes.Length; ++i)
			{
				_slid[i] = i >= state.Active.Length || state.Active[i];

				// What the slide MOVED, not just what it hid: `ExpandableMenu` is authored off
				// canvas at x −800 and every root slide slides it in.
				UiNode node = nodes[i];
				SlidPosition[i] = new Vector3(
					_screen.AttributeUnder(state, i, "position.x", node.PositionX),
					_screen.AttributeUnder(state, i, "position.y", node.PositionY),
					_screen.AttributeUnder(state, i, "position.z", node.PositionZ));
				SlidScale[i] = new Vector3(
					_screen.AttributeUnder(state, i, "scale.x", node.ScaleX),
					_screen.AttributeUnder(state, i, "scale.y", node.ScaleY),
					_screen.AttributeUnder(state, i, "scale.z", node.ScaleZ));
				_slidOpacity[i] = _screen.AttributeUnder(state, i, "opacity", node.Opacity);
			}

			++SlideRevision;
		}

		// One forward pass for both folds: document order is parents-first, so a parent's answer
		// is ready before its children need it.
		for (int i = 0; i < nodes.Length; ++i)
		{
			UiNode node = nodes[i];
			bool parentDraws = node.Parent is not { } up || Drawn[up.Ordinal];
			float parentOpacity = node.Parent is { } above ? Opacity[above.Ordinal] : 1f;

			bool own = active is null || i >= active.Count || active[i];
			Active[i] = own;

			// ONE BIT IN THE ENGINE. `goToSlide` writes an entry's active INTO the element's
			// active bit, and a later `setActive` overwrites it — the logbook's fold slide
			// names every subject page inactive and the guest then lit the chosen one. A host
			// switch ON is that later write, so it is not gated by the slide's entry.
			bool forced = switchedOn is not null && i < switchedOn.Count && switchedOn[i];
			Drawn[i] = own && (_slid[i] || forced) && parentDraws;

			bool fromGuest = opacity is not null && i < opacity.Count && opacity[i] is not null;
			float stated = fromGuest
				? Mathf.Clamp(opacity![i]!.Value, 0f, 100f)
				: _slidOpacity[i];

			Stated[i] = fromGuest;
			Own[i] = stated / 100f;
			Opacity[i] = parentOpacity * Own[i];
		}
	}

	private bool SlidesChanged(IReadOnlyList<int> slides)
	{
		if (_slidesShown.Length != slides.Count)
		{
			_slidesShown = new int[slides.Count];
			_slideStamp = new int[slides.Count];
			for (int i = 0; i < slides.Count; ++i)
			{
				_slidesShown[i] = slides[i];
			}

			return true;
		}

		bool changed = false;
		for (int i = 0; i < slides.Count; ++i)
		{
			if (_slidesShown[i] != slides[i])
			{
				_slidesShown[i] = slides[i];
				// A change within one sample is ordered by ordinal — the engine's own call
				// order is unknowable there and parents-first is the only stable choice.
				_slideStamp[i] = ++_slideStampCounter;
				changed = true;
			}
		}

		return changed;
	}
}
