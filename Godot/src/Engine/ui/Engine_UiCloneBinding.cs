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

		/// <summary><see cref="Node"/> when it is a mesh, the type test made once.</summary>
		public MeshInstance3D? Mesh { get; init; }
		public required int Ordinal { get; init; }

		/// <summary>
		/// The element whose live state the node shows: its own, or the same widget's element as
		/// the guest states it for another player (<see cref="Key"/>); -1 for none.
		/// </summary>
		public int Source { get; set; }

		/// <summary>The node's authored position less its source's, in guest units.</summary>
		public Vector3 Shift { get; set; }

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

		/// <summary>
		/// The local position the node keeps whatever the slide or the guest states, null where
		/// it follows them — see <see cref="HoldPosition"/>.
		/// </summary>
		public Vector2? Held { get; set; }

		/// <summary>What the node shows: the guest's draw gate AND <see cref="Open"/>.</summary>
		public bool Drawn { get; set; } = true;

		/// <summary><see cref="MaterialsCleared"/> as of the last read of the material.</summary>
		public int ClearsSeen { get; set; } = -1;

		/// <summary>The guest's draw gate at the last sample.</summary>
		public bool Stated { get; set; } = true;

		/// <summary>
		/// The element's time window on a scrubbed timeline — see <see cref="Gate"/>. Open
		/// unless a scrub has closed it.
		/// </summary>
		public bool Open { get; set; } = true;

		public float Faded { get; set; } = -1f;
		public uint Pictured { get; set; }

		/// <summary>Last non-zero visual layer mask observed on <see cref="Mesh"/>, restored when alpha rises above zero.</summary>
		public uint ActiveLayers { get; set; } = 1u;

		// A bound node's MATERIAL is deliberately not kept here — see Apply.
	}

	private readonly List<Bound> _bound = new();

	/// <summary>
	/// The FIRST bound node per element ordinal — what <see cref="NodeFor"/> answers. An index
	/// rather than a scan of <see cref="_bound"/>: the mask passes ask it once per bound node,
	/// every text edge and every frame, and on a 524-node book the scan was quadratic.
	/// </summary>
	private readonly Dictionary<int, Node3D> _firstByOrdinal = new();

	/// <summary>
	/// Where <see cref="Apply"/>'s time goes, in Stopwatch ticks, accumulated across every
	/// binding and read back by the `hud` op.
	/// </summary>
	/// <remarks>
	/// Accumulated per section across the whole walk, not lapped per node, so each reads as that
	/// section's share of the HUD's cost until <see cref="ResetProfile"/>.
	/// </remarks>
	public static long TicksTransform;

	public static long TicksDraw;

	public static long TicksMaterial;

	public static long TicksTexture;

	public static long TicksMasks;

	public static long AppliedNodes;

	public static long AppliedCalls;

	/// <summary>
	/// Whether the sections are timed: off until a reader has asked once, since it is a run of
	/// clock reads per node, and thousands a frame are a real share of a phone's main thread. The
	/// counts run regardless.
	/// </summary>
	public static bool Timed { get; private set; }

	/// <summary>The start of a section walk: the clock when <see cref="Timed"/>.</summary>
	internal static long Mark() => Timed ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

	/// <summary>Closes a section onto its counter and starts the next, when <see cref="Timed"/>.</summary>
	internal static void Lap(ref long ticks, ref long mark)
	{
		if (Timed)
		{
			long now = System.Diagnostics.Stopwatch.GetTimestamp();
			ticks += now - mark;
			mark = now;
		}
	}

	/// <summary>
	/// A material was taken off a node with none put in its place, and the next one must be written
	/// whatever the node was last faded to (see <see cref="Apply"/>). Every site that clears a text
	/// node calls this; a bound node reads its material again after one.
	/// </summary>
	public static void NoteMaterialCleared() => ++MaterialsCleared;

	private static int MaterialsCleared;

	/// <summary>Zeroes the section profile; the reader calls this after taking a sample.</summary>
	public static void ResetProfile()
	{
		Timed = true;
		TicksTransform = 0;
		TicksDraw = 0;
		TicksMaterial = 0;
		TicksTexture = 0;
		TicksMasks = 0;
		AppliedNodes = 0;
		AppliedCalls = 0;
	}

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
				for (int c = spatial.GetChildCount() - 1; c >= 0; --c)
				{
					Node child = spatial.GetChild(c);
					spatial.RemoveChild(child);
					child.Free();
				}
				++SuppressedCount;
				return;  // the subtree goes with its root
			}

			MeshInstance3D? meshNode = spatial as MeshInstance3D;
			uint authoredLayers = meshNode is not null && meshNode.Layers != 0u ? meshNode.Layers : 1u;
			float initialFaded = element.Quad is not null && meshNode is not null
				? Engine_UiSceneBuilder.QuadOpacity(meshNode)
				: -1f;
			if (meshNode is not null && initialFaded >= 0f && initialFaded <= 0.001f && meshNode.Layers != 0u)
			{
				meshNode.Layers = 0u;
			}

			_bound.Add(new Bound
			{
				Node = spatial,
				Mesh = meshNode,
				Ordinal = element.Ordinal,
				Source = element.Ordinal,
				IsQuad = element.Quad is not null,
				IsRoot = isRoot,
				// WHAT THE NODE SHOWS NOW, not an assumption. A clone of art the builder parked
				// (`slide_inactive`) arrives invisible; with Drawn defaulting to true, the first
				// sample that stated it drawn found nothing to change and the node stayed hidden
				// for good — the co-op screen's X prompt: active, drawn, opacity 1, HIDDEN.
				Drawn = spatial.Visible,
				Faded = initialFaded,
				ActiveLayers = authoredLayers,
				Pivot = new Vector3(element.PivotX, element.PivotY,
					Engine_UiSceneBuilder.ToGodotZ(element.PivotZ)),
			});
			_firstByOrdinal.TryAdd(element.Ordinal, spatial);

			isRoot = false;
		}

		for (int i = 0, count = node.GetChildCount(); i < count; ++i)
		{
			Bind(node.GetChild(i), screen, suppressed, isRoot);
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

			applied += Paint(bound.Node, Spread(band));
		}

		return applied;
	}

	/// <summary>
	/// How far the owner has moved each half of the book outward (see
	/// <see cref="Engine_UiSceneBuilder.WideShift"/>). Every rect here is measured from the
	/// UNSPREAD layout — the document, or nodes the apply pass has just restated — so the
	/// window follows its page by the same amount.
	/// </summary>
	public float ClipShiftX { get; set; }

	private Rect2 Spread(Rect2 band)
	{
		if (ClipShiftX <= 0f)
		{
			return band;
		}

		float centre = band.Position.X + (band.Size.X * 0.5f);
		return Mathf.Abs(centre) < 50f
			? band.GrowIndividual(ClipShiftX, 0f, ClipShiftX, 0f)
			: new Rect2(band.Position + new Vector2(centre > 0f ? ClipShiftX : -ClipShiftX, 0f), band.Size);
	}

	/// <summary>
	/// A bound mask node's rect in WORLD space — the 100-unit base quad scaled by the node's
	/// world basis, centred on its world origin.
	/// </summary>
	private static Rect2? WorldClip(Node3D? node)
	{
		if (node is null || !GodotObject.IsInstanceValid(node) || !node.IsInsideTree())
		{
			return null;
		}

		Transform3D world = Engine_UiSeats.LayoutTransform(node);
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
		float halfW = 50f * Mathf.Abs(node.ScaleX);
		float halfH = 50f * Mathf.Abs(node.ScaleY);
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
		if (node is MeshInstance3D mesh && mesh.MaterialOverride is ShaderMaterial existing)
		{
			float minX = Mathf.Min(rect.Position.X, rect.End.X);
			float minY = Mathf.Min(rect.Position.Y, rect.End.Y);
			float maxX = Mathf.Max(rect.Position.X, rect.End.X);
			float maxY = Mathf.Max(rect.Position.Y, rect.End.Y);
			var target = new Vector4(minX, minY, maxX, maxY);
			Variant current = existing.GetShaderParameter(ClipRectName);
			if (current.VariantType != Variant.Type.Vector4 || !current.AsVector4().IsEqualApprox(target))
			{
				if (Engine_UiSceneBuilder.EnsureOwnedMaterial(mesh) is { } material)
				{
					material.SetShaderParameter(ClipRectName, target);
				}
			}
			++applied;
		}

		for (int i = 0, count = node.GetChildCount(); i < count; ++i)
		{
			applied += Paint(node.GetChild(i), rect);
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

	/// <summary>The binding slot for an element, for <see cref="Gate"/>; -1 when unbound.</summary>
	public int SlotFor(int ordinal)
	{
		for (int slot = 0; slot < _bound.Count; ++slot)
		{
			if (_bound[slot].Ordinal == ordinal)
			{
				return slot;
			}
		}

		return -1;
	}

	/// <summary>
	/// Opens or closes an element's TIME WINDOW. An element authored with `timeoffset` /
	/// `endtime` exists only while its component's playhead lies inside them — the hold ring
	/// swaps its covers and fills at t = 500 that way — and a scrubbed timeline has no engine
	/// to do that, so the scrub states it here. Combined with the guest's own draw gate: a
	/// closed window hides, an open one shows only what the guest draws.
	/// </summary>
	public void Gate(int slot, bool open)
	{
		if (slot < 0 || slot >= _bound.Count || _bound[slot].Open == open)
		{
			return;
		}

		_bound[slot].Open = open;
		Show(_bound[slot]);
	}

	private static void Show(Bound bound)
	{
		bool draw = bound.Stated && bound.Open;
		if (draw != bound.Drawn)
		{
			bound.Drawn = draw;
			bound.Node.Visible = draw;
		}
	}

	/// <summary>
	/// Keys the bound nodes to other elements' live state: the same widget as the guest states it
	/// for another player. <paramref name="sources"/> maps a bound element to the element whose
	/// draw gate, opacity, picture and stated transform it shows; an element it does not map shows
	/// nothing. A position moves by its source's move from the source's authored position, so the
	/// node keeps its own place in its own art.
	/// </summary>
	public void Key(Engine_UiScreen screen, IReadOnlyDictionary<int, int> sources)
	{
		foreach (Bound bound in _bound)
		{
			if (bound.Ordinal < 0 || bound.Ordinal >= screen.Nodes.Length ||
				!sources.TryGetValue(bound.Ordinal, out int source) ||
				source < 0 || source >= screen.Nodes.Length)
			{
				bound.Source = -1;
				continue;
			}

			UiNode own = screen.Nodes[bound.Ordinal];
			UiNode from = screen.Nodes[source];
			bound.Source = source;
			bound.Shift = new Vector3(
				own.PositionX - from.PositionX, own.PositionY - from.PositionY, own.PositionZ - from.PositionZ);
		}
	}

	public Node3D? NodeFor(int ordinal) =>
		_firstByOrdinal.TryGetValue(ordinal, out Node3D? node) ? node : null;

	/// <summary>
	/// Holds <paramref name="ordinal"/>'s nodes at <paramref name="position"/> (local, guest
	/// units), whatever the slide or the guest states, while scale, rotation and every gate
	/// still follow the guest. For an element that moves only to make room for another widget,
	/// inside a widget the host places itself.
	/// </summary>
	public void HoldPosition(int ordinal, Vector2 position)
	{
		foreach (Bound bound in _bound)
		{
			if (bound.Ordinal == ordinal)
			{
				bound.Held = position;
			}
		}
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
		++AppliedCalls;
		foreach (Bound bound in _bound)
		{
			if (!GodotObject.IsInstanceValid(bound.Node) ||
				bound.Source >= resolve.Drawn.Length)
			{
				continue;
			}

			// Keyed to nothing: the other player's widget has no such element.
			if (bound.Source < 0)
			{
				bound.Stated = false;
				Show(bound);
				continue;
			}

			++AppliedNodes;
			long mark = Mark();
			ApplyTransform(bound, resolve, transform);
			Lap(ref TicksTransform, ref mark);

			bool stated = resolve.Drawn[bound.Source];
			if (stated)
			{
				++DrawnCount;
			}
			else
			{
				_dark.Add(bound.Ordinal);
			}

			bound.Stated = stated;
			Show(bound);

			Lap(ref TicksDraw, ref mark);

			float alpha = resolve.Opacity[bound.Source];

			// Cull zero-opacity MeshInstance3Ds from RendererSceneCull via `Layers = 0u` without
			// touching `Node3D.Visible` (which would hide child subtrees). Prevents hidden UI pages
			// from binding thousands of per-material sampler slots into D3D12's 2,048-sampler heap.
			if (bound.Mesh is { } layerMesh)
			{
				if (layerMesh.Layers != 0u)
				{
					bound.ActiveLayers = layerMesh.Layers;
				}
				uint wantLayers = alpha > 0.001f ? bound.ActiveLayers : 0u;
				if (layerMesh.Layers != wantLayers)
				{
					layerMesh.Layers = wantLayers;
				}
			}

			// A hash of zero is "unstated", not a change: the guest drops a stated hash to zero
			// mid-write, and the file's own texture stands meanwhile.
			uint hash = bound.IsQuad && textures is not null && bound.Source < textures.Count
				? textures[bound.Source]
				: 0;
			bool picturing = hash != 0 && hash != bound.Pictured;

			// Nothing to write, the usual case, so no crossing to fetch the material to write it
			// on. What the read below would still notice is a material gone, which the sites that
			// clear one say (a replaced one carries its opacity across, and is left alone either way).
			if (!picturing && Mathf.IsEqualApprox(alpha, bound.Faded) && bound.ClearsSeen == MaterialsCleared)
			{
				Lap(ref TicksMaterial, ref mark);
				continue;
			}

			// Read each time it is needed, never cached: `Engine_UiTheme.SetMeshText` and
			// `Engine_UiTheme.Paint` replace `MaterialOverride` on every string change (carrying
			// `clip_rect` and `opacity` across), and `Engine_UiSceneBuilder.EnsureOwnedMaterial`
			// replaces a shared pattern-book material lazily on first mutation.
			bound.ClearsSeen = MaterialsCleared;
			if (bound.Mesh is not { } painted ||
				painted.MaterialOverride is not ShaderMaterial)
			{
				// The next material this node is given must be written, whatever it was last
				// faded to — an empty label is left with none at all.
				bound.Faded = -1f;
				Lap(ref TicksMaterial, ref mark);
				continue;
			}

			if (!Mathf.IsEqualApprox(alpha, bound.Faded))
			{
				bound.Faded = alpha;
				// When `alpha <= 0.001f`, `painted.Layers = 0u` above already culls the mesh from
				// rendering; only allocate a private copy-on-write material if visible or already owned.
				if (alpha > 0.001f ||
					(painted.MaterialOverride is ShaderMaterial current && Engine_UiSceneBuilder.IsOwnedMaterial(painted, current)))
				{
					if (Engine_UiSceneBuilder.EnsureOwnedMaterial(painted) is { } fadeMat)
					{
						fadeMat.SetShaderParameter(OpacityName, alpha);
					}
				}
				Engine_UiTheme.FadeInlineIcons(painted, alpha);
			}

			Lap(ref TicksMaterial, ref mark);

			if (picturing && ResolveTexture?.Invoke(hash) is { } picture &&
				Engine_UiSceneBuilder.EnsureOwnedMaterial(painted) is { } picMat)
			{
				bound.Pictured = hash;
				picMat.SetShaderParameter(DiffuseTexName, picture);
			}

			Lap(ref TicksTexture, ref mark);
		}

		long masks = Mark();
		RefreshMasks(resolve, transform);
		Lap(ref TicksMasks, ref masks);
	}

	private Engine_UiScreen? _maskScreen;

	// Written per sample on the frame thread: one StringName each, never a literal (a literal
	// is a fresh StringName per call and a weak handle the next GC frees inside its pause).
	private static readonly StringName ClipRectName = "clip_rect";
	private static readonly StringName OpacityName = "opacity";
	private static readonly StringName DiffuseTexName = "diffuse_tex";

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

				band = Spread(fresh);
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
		static (Vector2 Position, Vector2 Scale) Live(UiNode at, Engine_UiElementResolve resolve,
			IReadOnlyList<float?[]?>? transform)
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
				if (values.Length > 0 && values[0] is { } x && !float.IsNaN(x)) position.X = x;
				if (values.Length > 1 && values[1] is { } y && !float.IsNaN(y)) position.Y = y;
				if (values.Length > 3 && values[3] is { } sx && !float.IsNaN(sx)) scale.X = sx;
				if (values.Length > 4 && values[4] is { } sy && !float.IsNaN(sy)) scale.Y = sy;
			}

			return (position, scale);
		}

		(Vector2 centre, Vector2 own) = Live(node, resolve, transform);
		float halfW = 50f * own.X;
		float halfH = 50f * own.Y;
		for (UiNode? up = node.Parent; up is not null; up = up.Parent)
		{
			(Vector2 position, Vector2 scale) = Live(up, resolve, transform);
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
	/// The origin is `position - basis * pivot` (as <see cref="Engine_UiSceneBuilder.SetLivePosition"/>
	/// builds it), and scale goes through the basis COLUMNS because `Basis.Scale`
	/// reports (0,0,0) on a singular basis and a read-modify-write flattens unmentioned axes.
	/// </summary>
	private static void ApplyTransform(Bound bound, Engine_UiElementResolve resolve,
		IReadOnlyList<float?[]?>? transform)
	{
		int source = bound.Source;
		if (bound.IsRoot || source < 0 || source >= resolve.SlidPosition.Length)
		{
			return;
		}

		Transform3D before = bound.Node.Transform;

		// The base is the SLIDE's, not the cloned node's own transform (which bakes the preview
		// slide). Order is the engine's: element attributes at load, slide on switch, stated
		// runtime write last.
		Vector3 slid = resolve.SlidPosition[source];
		Vector3 scale = resolve.SlidScale[source];
		Vector3 pivot = bound.Pivot;
		Basis? turned = null;

		// The playing slide's tracks, written every frame over the slide's commands.
		if (resolve.Animated is not null)
		{
			slid = Animate(resolve, source, slid, Engine_UiSlidePlayback.Channel.PositionX);
			scale = Animate(resolve, source, scale, Engine_UiSlidePlayback.Channel.ScaleX);
			float px = resolve.AnimatedValue(source, Engine_UiSlidePlayback.Channel.PivotX);
			float py = resolve.AnimatedValue(source, Engine_UiSlidePlayback.Channel.PivotY);
			float pz = resolve.AnimatedValue(source, Engine_UiSlidePlayback.Channel.PivotZ);
			pivot = new Vector3(
				float.IsNaN(px) ? pivot.X : px,
				float.IsNaN(py) ? pivot.Y : py,
				float.IsNaN(pz) ? pivot.Z : Engine_UiSceneBuilder.ToGodotZ(pz));

			float turnX = resolve.AnimatedValue(source, Engine_UiSlidePlayback.Channel.RotationX);
			float turnY = resolve.AnimatedValue(source, Engine_UiSlidePlayback.Channel.RotationY);
			float turnZ = resolve.AnimatedValue(source, Engine_UiSlidePlayback.Channel.RotationZ);
			if (!float.IsNaN(turnX) || !float.IsNaN(turnY) || !float.IsNaN(turnZ))
			{
				UiNode authored = resolve.Node(source);
				turned = Basis.FromEuler(new Vector3(
					-(float.IsNaN(turnX) ? authored.RotationX : turnX),
					-(float.IsNaN(turnY) ? authored.RotationY : turnY),
					float.IsNaN(turnZ) ? authored.RotationZ : turnZ));
			}
		}

		slid += bound.Shift;
		var position = new Vector3(slid.X, slid.Y, Engine_UiSceneBuilder.ToGodotZ(slid.Z));

		float?[]? values = transform is not null && source < transform.Count
			? transform[source]
			: null;

		if (values is not null)
		{
			// A NaN in the stated transform is never a pose — the guest's frozen mirror carries
			// them (the item book's paper backdrop stated `x = NaN` and vanished), and one NaN
			// poisons the basis. Each axis falls back to the slide/authored value instead.
			if (values.Length > 0 && values[0] is { } x && !float.IsNaN(x)) position.X = x + bound.Shift.X;
			if (values.Length > 1 && values[1] is { } y && !float.IsNaN(y)) position.Y = y + bound.Shift.Y;

			if (values.Length > 2 && values[2] is { } z && !float.IsNaN(z))
			{
				position.Z = Engine_UiSceneBuilder.ToGodotZ(z + bound.Shift.Z);
			}

			if (values.Length > 3 && values[3] is { } sx && !float.IsNaN(sx)) scale.X = sx;
			if (values.Length > 4 && values[4] is { } sy && !float.IsNaN(sy)) scale.Y = sy;
			if (values.Length > 5 && values[5] is { } sz && !float.IsNaN(sz)) scale.Z = sz;
		}

		if (bound.Held is { } held)
		{
			position.X = held.X + bound.Shift.X;
			position.Y = held.Y + bound.Shift.Y;
		}

		// Scale through the basis columns, never `node.Scale`: `Basis.Scale` reports (0,0,0) on a
		// singular basis and every `hud.bgf` meter bar is authored with a zero column. Each column
		// keeps its direction (the rotation) and takes the new length; a zero column is rebuilt
		// from the cross product of the other two to stay right-handed.
		Basis basis = turned ?? before.Basis;
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

		// A stated `rotation.z` (slot 6) REPLACES the element's turn: the basis is rebuilt as
		// R(z)·S from the scale alone, the way `Engine_UiSceneBuilder.BuildTransform` composes
		// an authored rotation (`FromEuler(-x, -y, z)`, Z unconjugated), and the pivot term
		// follows it below, so the element turns about its authored pivot.
		if (values is not null && values.Length > 6 && values[6] is { } rz && !float.IsNaN(rz))
		{
			basis = new Basis(Vector3.Back, rz).Scaled(scale);
		}

		var after = new Transform3D(basis, position - (basis * pivot));
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

	/// <summary>Three consecutive animated channels (x, y, z) over a base, axis by axis.</summary>
	private static Vector3 Animate(Engine_UiElementResolve resolve, int source, Vector3 value,
		Engine_UiSlidePlayback.Channel x)
	{
		float ax = resolve.AnimatedValue(source, x);
		float ay = resolve.AnimatedValue(source, x + 1);
		float az = resolve.AnimatedValue(source, x + 2);
		return new Vector3(
			float.IsNaN(ax) ? value.X : ax,
			float.IsNaN(ay) ? value.Y : ay,
			float.IsNaN(az) ? value.Z : az);
	}

	private static Vector3 Axis(int axis) => axis switch
	{
		0 => Vector3.Right,
		1 => Vector3.Up,
		_ => Vector3.Back,
	};
}
