using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// The join between a clone and the elements that drive it, plus the per-frame apply of the
/// three live writes: drawn, opacity, texture.
///
/// The join is the `object_id` metadata the scene builder stamps and `Duplicate()` carries —
/// never a name (names repeat freely) or a tree position (element kinds that make no node
/// differ between trees). Each surface still decides WHERE the clone goes.
/// </summary>
public sealed class Engine_UiCloneBinding
{
	/// <summary>One node inside a clone, and the element whose live state drives it.</summary>
	private sealed class Bound
	{
		public required Node3D Node { get; init; }
		public required int Ordinal { get; init; }
		public required bool IsQuad { get; init; }

		/// <summary>
		/// True for the root of a cloned unit, whose transform is the book's to set: it carries the
		/// accumulated canvas transform of its authored parents, which are not in the clone, so
		/// writing the element's own local position onto it would drop the unit at the origin.
		/// </summary>
		public required bool IsRoot { get; init; }

		/// <summary>
		/// The element's authored pivot — an origin is `position - basis * pivot`. The position is
		/// not kept beside it: the base is the slid position from the resolve.
		/// </summary>
		public Vector3 Pivot { get; init; }

		public bool Drawn { get; set; } = true;
		public float Faded { get; set; } = -1f;
		public uint Pictured { get; set; }
	}

	private readonly List<Bound> _bound = new();

	/// <summary>How many nodes are bound — the count worth reporting after a build.</summary>
	public int Count => _bound.Count;

	/// <summary>How many bound nodes the last <see cref="Apply"/> left drawing.</summary>
	public int DrawnCount { get; private set; }

	/// <summary>
	/// The element ordinals of the bound nodes the last <see cref="Apply"/> left dark — a clone
	/// with everything switched off looks the same as one that never built.
	/// </summary>
	public IReadOnlyList<int> Dark => _dark;

	private readonly List<int> _dark = new();

	/// <summary>Resolves a stated texture hash to a loaded texture. Null leaves pictures alone.</summary>
	public System.Func<uint, Texture2D?>? ResolveTexture { get; set; }

	/// <summary>How many subtrees were dropped because Godot already draws them itself.</summary>
	public int SuppressedCount { get; private set; }

	/// <summary>
	/// Whether an element is one a suppression entry names.
	///
	/// An entry may be a dotted tail — `ExpandableMenu.Buttons` matches an element named
	/// `Buttons` whose ancestors end `…ExpandableMenu` — because a name can have many bearers
	/// (`Buttons` has six in `abilitiesscreen.bgf`). A bare name still matches any bearer.
	/// </summary>
	private static bool Suppresses(IReadOnlySet<string>? suppressed, UiNode element)
	{
		if (suppressed is null || suppressed.Count == 0)
		{
			return false;
		}

		foreach (string entry in suppressed)
		{
			int dot = entry.LastIndexOf('.');
			if (dot < 0)
			{
				if (string.Equals(entry, element.Name, System.StringComparison.Ordinal))
				{
					return true;
				}

				continue;
			}

			if (!string.Equals(entry[(dot + 1)..], element.Name, System.StringComparison.Ordinal))
			{
				continue;
			}

			// Walk the stated path backwards against the ancestors: anchored at the element,
			// open at the top.
			UiNode? at = element.Parent;
			int cut = dot;
			bool matched = true;
			while (cut > 0 && matched)
			{
				int previous = entry.LastIndexOf('.', cut - 1);
				string segment = entry[(previous + 1)..cut];
				matched = at is not null &&
					string.Equals(segment, at.Name, System.StringComparison.Ordinal);
				at = at?.Parent;
				cut = previous;
			}

			if (matched)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Binds a cloned subtree, and everything under it, to <paramref name="screen"/>.
	///
	/// <paramref name="suppressed"/> names elements a Godot-owned view already draws; bound, they
	/// would paint the guest's copy underneath ours. The name tested is the DOCUMENT element's,
	/// not the node's — Godot renames duplicate siblings.
	/// </summary>
	public void Bind(Node node, Engine_UiScreen screen,
		IReadOnlySet<string>? suppressed = null) => Bind(node, screen, suppressed, true);

	private void Bind(Node node, Engine_UiScreen screen,
		IReadOnlySet<string>? suppressed, bool isRoot)
	{
		if (node is Node3D spatial && node.HasMeta("object_id") &&
			screen.NodeForObjectId((uint)node.GetMeta("object_id")) is { } element)
		{
			if (Suppresses(suppressed, element))
			{
				spatial.Visible = false;
				++SuppressedCount;
				return;  // the subtree goes with its root
			}

			_bound.Add(new Bound
			{
				Node = spatial,
				Ordinal = element.Ordinal,
				IsQuad = element.Quad is not null,
				IsRoot = isRoot,
				Pivot = new Vector3(element.PivotX, element.PivotY,
					Engine_UiSceneBuilder.ToGodotZ(element.PivotZ)),
			});

			isRoot = false;
		}

		foreach (Node child in node.GetChildren())
		{
			Bind(child, screen, suppressed, isRoot);
		}
	}

	/// <summary>The cloned node for an element, or null when it is in no bound subtree.</summary>
	/// <summary>
	/// The pivot the binding applies to <paramref name="node"/>, or zero when it binds nothing.
	/// A position is not an origin: re-placing a bound element must go through the same
	/// `origin = position - basis * pivot` (see <see cref="Engine_UiSceneBuilder.SetLivePosition"/>).
	/// </summary>
	/// <summary>
	/// Applies the document's own mask rects to every bound node's material.
	///
	/// A mask is a scroll clip, not the page selector: `sub_828C3250(screen, maskName,
	/// targetPath)` binds each mask to a scrolling list inside one page (`MaskPage2`→
	/// `Page1.Contents.Text` and `SalesPage.Contents.Description`, `MaskFamily`→
	/// `FamilyPage.Contents.Group`, `MaskQuests`→the quests list); `MaskPage3` is referenced
	/// nowhere. Page selection is separate — an `opacity` write, or a per-page slide.
	///
	/// Resolved once here, not per frame. Both UI shaders declare `clip_rect` in the canvas
	/// space this computes.
	/// </summary>
	public int ApplyMasks(Engine_UiScreen screen)
	{
		_maskScreen = screen;
		_maskRects.Clear();
		_lastMaskRects.Clear();
		int applied = 0;
		foreach (Bound bound in _bound)
		{
			if (bound.Ordinal < 0 || bound.Ordinal >= screen.MaskByOrdinal.Length)
			{
				continue;
			}

			int mask = screen.MaskByOrdinal[bound.Ordinal];
			if (mask < 0 || mask >= screen.Nodes.Length)
			{
				continue;
			}

			// Prefer the clone's own mask node: the shader clips in WORLD space
			// (`clip_world = MODEL_MATRIX * VERTEX`), so the document's authored canvas rect
			// only holds while the art sits where the document put it. Document rect is the
			// fallback for a mask the book did not clone.
			Rect2? rect = WorldClip(NodeFor(mask)) ?? Clip(screen.Nodes[mask]);
			if (rect is not { } band)
			{
				continue;
			}

			applied += Paint(bound.Node, band);
		}

		return applied;
	}

	/// <summary>
	/// A bound mask node's rect in WORLD space — the 100-unit base quad scaled by the node's
	/// world basis, centred on its world origin, as `Engine_UiSceneBuilder.RefreshClips` does.
	/// </summary>
	private static Rect2? WorldClip(Node3D? node)
	{
		if (node is null || !GodotObject.IsInstanceValid(node) || !node.IsInsideTree())
		{
			return null;
		}

		Transform3D world = node.GlobalTransform;
		float halfW = 0.5f * Engine_UiSceneBuilder.QuadBaseSize * world.Basis.X.Length();
		float halfH = 0.5f * Engine_UiSceneBuilder.QuadBaseSize * world.Basis.Y.Length();
		if (halfW <= 0f || halfH <= 0f)
		{
			return null;
		}

		return new Rect2(
			world.Origin.X - halfW, world.Origin.Y - halfH, halfW * 2f, halfH * 2f);
	}

	/// <summary>A mask element's canvas rect — the 100-unit base quad through its own chain.</summary>
	private static Rect2? Clip(UiNode node)
	{
		float cx = node.PositionX;
		float cy = node.PositionY;
		float halfW = 50f * node.ScaleX;
		float halfH = 50f * node.ScaleY;
		for (UiNode? up = node.Parent; up is not null; up = up.Parent)
		{
			cx = (cx * up.ScaleX) + up.PositionX;
			cy = (cy * up.ScaleY) + up.PositionY;
			halfW *= Mathf.Abs(up.ScaleX);
			halfH *= Mathf.Abs(up.ScaleY);
		}

		return new Rect2(cx - halfW, cy - halfH, halfW * 2f, halfH * 2f);
	}

	/// <summary>Writes `clip_rect` onto a node and everything under it. Returns how many took it.</summary>
	private static int Paint(Node node, Rect2 rect)
	{
		int applied = 0;
		if (node is MeshInstance3D mesh && mesh.MaterialOverride is ShaderMaterial material)
		{
			material.SetShaderParameter("clip_rect", new Vector4(
				rect.Position.X, rect.Position.Y, rect.End.X, rect.End.Y));
			++applied;
		}

		foreach (Node child in node.GetChildren())
		{
			applied += Paint(child, rect);
		}

		return applied;
	}

	public Vector3 PivotOf(Node3D node)
	{
		foreach (Bound bound in _bound)
		{
			if (ReferenceEquals(bound.Node, node))
			{
				return bound.Pivot;
			}
		}

		return Vector3.Zero;
	}

	/// <summary>The ordinal this binding holds for <paramref name="node"/>, or -1.</summary>
	public int OrdinalOf(Node3D node)
	{
		foreach (Bound bound in _bound)
		{
			if (ReferenceEquals(bound.Node, node))
			{
				return bound.Ordinal;
			}
		}

		return -1;
	}

	public Node3D? NodeFor(int ordinal)
	{
		foreach (Bound bound in _bound)
		{
			if (bound.Ordinal == ordinal)
			{
				return bound.Node;
			}
		}

		return null;
	}

	/// <summary>Every bound node that paints text, with the element it belongs to.</summary>
	public IEnumerable<(MeshInstance3D Node, int Ordinal)> TextNodes(Engine_UiScreen screen)
	{
		foreach (Bound bound in _bound)
		{
			if (bound.Node is MeshInstance3D mesh &&
				bound.Ordinal >= 0 && bound.Ordinal < screen.Nodes.Length &&
				screen.Nodes[bound.Ordinal].Text is not null)
			{
				yield return (mesh, bound.Ordinal);
			}
		}
	}

	/// <summary>
	/// Applies one sample: the draw gate, the accumulated opacity, and the guest's chosen art.
	/// Every write is change-keyed; this runs over a few thousand nodes a frame on the frame thread.
	/// </summary>
	public void Apply(Engine_UiElementResolve resolve, IReadOnlyList<uint>? textures,
		IReadOnlyList<float?[]?>? transform = null)
	{
		DrawnCount = 0;
		_dark.Clear();
		foreach (Bound bound in _bound)
		{
			if (!GodotObject.IsInstanceValid(bound.Node) ||
				bound.Ordinal >= resolve.Drawn.Length)
			{
				continue;
			}

			ApplyTransform(bound, resolve, transform);

			bool draw = resolve.Drawn[bound.Ordinal];
			if (draw)
			{
				++DrawnCount;
			}
			else
			{
				_dark.Add(bound.Ordinal);
			}

			if (draw != bound.Drawn)
			{
				bound.Drawn = draw;
				bound.Node.Visible = draw;
			}

			if (bound.Node is not MeshInstance3D mesh ||
				mesh.MaterialOverride is not ShaderMaterial material)
			{
				continue;
			}

			float alpha = resolve.Opacity[bound.Ordinal];
			if (!Mathf.IsEqualApprox(alpha, bound.Faded))
			{
				bound.Faded = alpha;
				material.SetShaderParameter("opacity", alpha);
			}

			// A hash of zero is "unstated", not a change: the guest drops a stated hash to zero
			// mid-write, and the file's own texture stands meanwhile.
			if (!bound.IsQuad || textures is null || bound.Ordinal >= textures.Count)
			{
				continue;
			}

			uint hash = textures[bound.Ordinal];
			if (hash == 0 || hash == bound.Pictured ||
				ResolveTexture?.Invoke(hash) is not { } picture)
			{
				continue;
			}

			bound.Pictured = hash;
			material.SetShaderParameter("diffuse_tex", picture);
		}

		RefreshMasks(resolve, transform);
	}

	private Engine_UiScreen? _maskScreen;

	/// <summary>This sample's measured band per mask element.</summary>
	private readonly Dictionary<int, Rect2> _maskRects = new();

	/// <summary>
	/// Re-paints the clip band of every mask the book did NOT clone from where the mask element
	/// now sits — the slide's pose with the stated transform on top, composed through its
	/// parents as <see cref="ApplyTransform"/> does. Mask elements travel with their page, so a
	/// band painted once from the file's rest position clips against a spot the page has left.
	/// </summary>
	private void RefreshMasks(Engine_UiElementResolve resolve, IReadOnlyList<float?[]?>? transform)
	{
		if (_maskScreen is not { } screen)
		{
			return;
		}

		foreach (Bound bound in _bound)
		{
			if (bound.Ordinal < 0 || bound.Ordinal >= screen.MaskByOrdinal.Length)
			{
				continue;
			}

			int mask = screen.MaskByOrdinal[bound.Ordinal];
			if (mask < 0 || mask >= screen.Nodes.Length || NodeFor(mask) is not null)
			{
				continue;
			}

			// Measured once per mask per sample; painted only when the band moved.
			if (!_maskRects.TryGetValue(mask, out Rect2 band))
			{
				if (LiveClip(screen.Nodes[mask], resolve, transform) is not { } fresh)
				{
					continue;
				}

				band = fresh;
				_maskRects[mask] = band;
			}

			if (_lastMaskRects.TryGetValue(mask, out Rect2 last) && last == band)
			{
				continue;
			}

			Paint(bound.Node, band);
		}

		foreach ((int mask, Rect2 band) in _maskRects)
		{
			_lastMaskRects[mask] = band;
		}

		_maskRects.Clear();
	}

	/// <summary>The band each mask was last painted with, so a still mask is not repainted.</summary>
	private readonly Dictionary<int, Rect2> _lastMaskRects = new();

	/// <summary>
	/// A mask element's canvas rect from its LIVE pose — the resolve's slid position and
	/// scale, with the guest's stated values on top, composed through the parent chain.
	/// </summary>
	private static Rect2? LiveClip(UiNode node, Engine_UiElementResolve resolve,
		IReadOnlyList<float?[]?>? transform)
	{
		(Vector2 Position, Vector2 Scale) Live(UiNode at)
		{
			int ordinal = at.Ordinal;
			Vector2 position = ordinal >= 0 && ordinal < resolve.SlidPosition.Length
				? new Vector2(resolve.SlidPosition[ordinal].X, resolve.SlidPosition[ordinal].Y)
				: new Vector2(at.PositionX, at.PositionY);
			Vector2 scale = ordinal >= 0 && ordinal < resolve.SlidScale.Length
				? new Vector2(resolve.SlidScale[ordinal].X, resolve.SlidScale[ordinal].Y)
				: new Vector2(at.ScaleX, at.ScaleY);

			float?[]? values = transform is not null && ordinal >= 0 && ordinal < transform.Count
				? transform[ordinal]
				: null;
			if (values is not null)
			{
				if (values.Length > 0 && values[0] is { } x) position.X = x;
				if (values.Length > 1 && values[1] is { } y) position.Y = y;
				if (values.Length > 3 && values[3] is { } sx) scale.X = sx;
				if (values.Length > 4 && values[4] is { } sy) scale.Y = sy;
			}

			return (position, scale);
		}

		(Vector2 centre, Vector2 own) = Live(node);
		float halfW = 50f * own.X;
		float halfH = 50f * own.Y;
		for (UiNode? up = node.Parent; up is not null; up = up.Parent)
		{
			(Vector2 position, Vector2 scale) = Live(up);
			centre = new Vector2((centre.X * scale.X) + position.X, (centre.Y * scale.Y) + position.Y);
			halfW *= Mathf.Abs(scale.X);
			halfH *= Mathf.Abs(scale.Y);
		}

		if (halfW <= 0f || halfH <= 0f)
		{
			return null;
		}

		return new Rect2(centre.X - halfW, centre.Y - halfH, halfW * 2f, halfH * 2f);
	}

	/// <summary>
	/// Writes the guest's stated transform onto one cloned node — half of what a screen does at
	/// runtime is a position or scale attribute write (menus authored at x -800 and tweened in,
	/// meter bars authored `scale.x = 0`), so a clone left at the artist's spot never moves.
	///
	/// Same arithmetic as <see cref="Engine_UiSceneBuilder.ApplyLiveTransform"/>: origin is
	/// `position - basis * pivot`, and scale goes through the basis COLUMNS because `Basis.Scale`
	/// reports (0,0,0) on a singular basis and a read-modify-write flattens unmentioned axes.
	/// </summary>
	private static void ApplyTransform(Bound bound, Engine_UiElementResolve resolve,
		IReadOnlyList<float?[]?>? transform)
	{
		if (bound.IsRoot || bound.Ordinal >= resolve.SlidPosition.Length)
		{
			return;
		}

		Transform3D before = bound.Node.Transform;

		// The base is the SLIDE's, not the cloned node's own transform (which bakes the preview
		// slide). Order is the engine's: element attributes at load, slide on switch, stated
		// runtime write last.
		Vector3 slid = resolve.SlidPosition[bound.Ordinal];
		Vector3 scale = resolve.SlidScale[bound.Ordinal];
		var position = new Vector3(slid.X, slid.Y, Engine_UiSceneBuilder.ToGodotZ(slid.Z));

		float?[]? values = transform is not null && bound.Ordinal < transform.Count
			? transform[bound.Ordinal]
			: null;

		if (values is not null)
		{
			if (values.Length > 0 && values[0] is { } x) position.X = x;
			if (values.Length > 1 && values[1] is { } y) position.Y = y;
			if (values.Length > 2 && values[2] is { } z)
			{
				position.Z = Engine_UiSceneBuilder.ToGodotZ(z);
			}

			if (values.Length > 3 && values[3] is { } sx) scale.X = sx;
			if (values.Length > 4 && values[4] is { } sy) scale.Y = sy;
			if (values.Length > 5 && values[5] is { } sz) scale.Z = sz;
		}

		// Scale through the basis columns, never `node.Scale`: `Basis.Scale` reports (0,0,0) on a
		// singular basis and every `hud.bgf` meter bar is authored with a zero column. Each column
		// keeps its direction (the rotation) and takes the new length; a zero column is rebuilt
		// from the cross product of the other two to stay right-handed.
		Basis basis = before.Basis;
		for (int axis = 0; axis < 3; ++axis)
		{
			float length = axis == 0 ? scale.X : axis == 1 ? scale.Y : scale.Z;
			Vector3 column = basis[axis];
			if (column.LengthSquared() <= 0.000001f)
			{
				column = basis[(axis + 1) % 3].Cross(basis[(axis + 2) % 3]);
			}

			basis[axis] = column.LengthSquared() > 0.000001f
				? column.Normalized() * length
				: Axis(axis) * length;
		}

		var after = new Transform3D(basis, position - (basis * bound.Pivot));
		if (after == before)
		{
			return;
		}

		// Every number is a raw guest word bit-cast out of an attribute slot; an unset or
		// mid-write slot reads as NaN, and one NaN in a UI transform walks the render-list sort
		// off the array. Keeping the previous transform costs one stale frame on one element.
		if (!after.Origin.IsFinite() || !basis.X.IsFinite() ||
			!basis.Y.IsFinite() || !basis.Z.IsFinite())
		{
			return;
		}

		bound.Node.Transform = after;
	}

	private static Vector3 Axis(int axis) => axis switch
	{
		0 => Vector3.Right,
		1 => Vector3.Up,
		_ => Vector3.Back,
	};
}
