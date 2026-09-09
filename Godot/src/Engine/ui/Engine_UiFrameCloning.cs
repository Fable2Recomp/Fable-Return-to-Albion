using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// The frame border cloning step — `frame/frame.lua`, `frame/framecloning.lua` and
/// `theme/themeframes.lua`: the theme names the slice art, the cloning copies it beside a frame,
/// the update decides when.
///
/// The nine border slices a frame drives are not in the .bgf; they are cloned at runtime from the
/// theme's `Style1Frame`/`Style2Frame` art, which is what
/// <see cref="Engine_UiFrame.State.HaveClonedBorderElements"/> gates pass 7 on. Style is chosen
/// by `BorderSize` alone (`FrameUsesBlocks` is `return 4 == BorderSize`, ported as
/// <see cref="Engine_UiFrame.State.UsesBlocks"/>): style 1 is beam-based, nine slices with one
/// stretched beam per edge; style 2 is block-based, four corners and a Middle plus a fixed pool
/// of whole 32-unit tiles — <see cref="Engine_UiFrame.MaxHorizontalBlocks"/> upper and lower,
/// <see cref="Engine_UiFrame.MaxVerticalBlocks"/> left and right, 99 clones per frame, never
/// resized (pass 7 fades the unused tiles to zero opacity).
///
/// Cloning runs from `FrameUpdate` while `FramesUntilStopUpdating > 0`, gated on
/// `BorderSize ~= 0` (`frame.dis main.0 pc9..11`); pass 7 itself bails on `BorderSize == 0`.
///
/// Shipped quirks, reproduced: cloning happens once per frame ever, since
/// `HaveClonedBorderElements` is set true before the style branch; no theme means no cloning and
/// no flag, so such a frame retries every tick; the block pool counts are the layout maxima
/// (`framestateandconstants.dis main.0 pc17..38`, a `ceil` of the axis in blocks less four, 26
/// and 21); and theme slices are looked up inside the style frame's subtree, since both styles
/// name their corners and Middle identically.
///
/// `FrameArrangeItems`' eight passes are not here: the arithmetic is
/// <see cref="Engine_UiFrame"/>, the application <see cref="Engine_UiFrameDriver"/>.
/// <see cref="Update"/> returning true means "arrange now"; the caller runs the passes and feeds
/// the <see cref="Engine_UiFrame.BorderLayout"/> to <see cref="BorderClones.Apply"/>.
/// </summary>
public sealed class Engine_UiFrameCloning
{
	/// <summary>Style 1's nine slices — corners, beams, Middle — by the slot pass 7 drives.</summary>
	private readonly Dictionary<Engine_UiFrame.Slot, Node3D> _style1 = new();

	/// <summary>Style 2's five stretched cells — corners and Middle. Its edges are the blocks.</summary>
	private readonly Dictionary<Engine_UiFrame.Slot, Node3D> _style2 = new();

	private Node3D? _style2UpperBlock;
	private Node3D? _style2LowerBlock;
	private Node3D? _style2LeftBlock;
	private Node3D? _style2RightBlock;

	/// <summary>
	/// Each frame's clones, keyed by the frame element so they survive the arrange that made
	/// them; `HaveClonedBorderElements` lives on the frame's persistent State
	/// (`frame.dis main.0 pc12..14`).
	/// </summary>
	private readonly Dictionary<UiNode, BorderClones> _cloned = new();

	/// <summary>
	/// The frames this view has taken over. The shipped state is per component instance and dies
	/// with the presentation; this one is keyed to a host-cached document, so on first sight of a
	/// frame the countdown and clone flag are seeded fresh, as `FrameInitialise` does
	/// (`framestateandconstants.dis main.0 pc48..49`).
	/// </summary>
	private readonly HashSet<UiNode> _adopted = new();

	/// <summary>Whether the theme carried a `Style1Frame` with at least one named slice.</summary>
	public bool HasStyle1 => _style1.Count > 0;

	/// <summary>Whether the theme carried a `Style2Frame` likewise.</summary>
	public bool HasStyle2 =>
		_style2.Count > 0 || _style2UpperBlock is not null || _style2LowerBlock is not null ||
		_style2LeftBlock is not null || _style2RightBlock is not null;

	/// <summary>How many of the eighteen theme sources resolved, for the caller's report.</summary>
	public int SourceCount { get; }

	private static readonly (Engine_UiFrame.Slot Slot, string Name)[] Style1Names =
	{
		(Engine_UiFrame.Slot.UpperLeftCorner, "UpperLeftCorner"),
		(Engine_UiFrame.Slot.UpperBeam, "UpperBeam"),
		(Engine_UiFrame.Slot.UpperRightCorner, "UpperRightCorner"),
		(Engine_UiFrame.Slot.LeftBeam, "LeftBeam"),
		(Engine_UiFrame.Slot.Middle, "Middle"),
		(Engine_UiFrame.Slot.RightBeam, "RightBeam"),
		(Engine_UiFrame.Slot.LowerLeftCorner, "LowerLeftCorner"),
		(Engine_UiFrame.Slot.LowerBeam, "LowerBeam"),
		(Engine_UiFrame.Slot.LowerRightCorner, "LowerRightCorner"),
	};

	private static readonly (Engine_UiFrame.Slot Slot, string Name)[] Style2CellNames =
	{
		(Engine_UiFrame.Slot.UpperLeftCorner, "UpperLeftCorner"),
		(Engine_UiFrame.Slot.UpperRightCorner, "UpperRightCorner"),
		(Engine_UiFrame.Slot.LowerLeftCorner, "LowerLeftCorner"),
		(Engine_UiFrame.Slot.LowerRightCorner, "LowerRightCorner"),
		(Engine_UiFrame.Slot.Middle, "Middle"),
	};

	/// <summary>
	/// The theme half — `themeframes.lua`'s top-level chunk, which fetches `Style1Frame` and
	/// `Style2Frame` off the master slide and records the named slice under each. (`Icon` is
	/// fetched there but read by no function in these scripts.)
	///
	/// Missing art degrades, never throws; a theme carrying only one style serves that one. A
	/// slice absent inside a present style frame is tolerated here, where the original would
	/// error on `clone(nil)`.
	/// </summary>
	public Engine_UiFrameCloning(Engine_UiTheme theme)
	{
		int found = 0;

		if (theme.ArtSourceNode("Style1Frame") is { } style1)
		{
			foreach ((Engine_UiFrame.Slot slot, string name) in Style1Names)
			{
				if (Engine_UiTheme.FindByName(style1, name) is Node3D source)
				{
					_style1[slot] = source;
					++found;
				}
			}
		}

		if (theme.ArtSourceNode("Style2Frame") is { } style2)
		{
			foreach ((Engine_UiFrame.Slot slot, string name) in Style2CellNames)
			{
				if (Engine_UiTheme.FindByName(style2, name) is Node3D source)
				{
					_style2[slot] = source;
					++found;
				}
			}

			_style2UpperBlock = Engine_UiTheme.FindByName(style2, "UpperBlock") as Node3D;
			_style2LowerBlock = Engine_UiTheme.FindByName(style2, "LowerBlock") as Node3D;
			_style2LeftBlock = Engine_UiTheme.FindByName(style2, "LeftBlock") as Node3D;
			_style2RightBlock = Engine_UiTheme.FindByName(style2, "RightBlock") as Node3D;
			found += (_style2UpperBlock is null ? 0 : 1) + (_style2LowerBlock is null ? 0 : 1) +
				(_style2LeftBlock is null ? 0 : 1) + (_style2RightBlock is null ? 0 : 1);
		}

		SourceCount = found;
	}

	/// <summary>
	/// `FrameUpdate` — the per-tick gate. Returns whether the caller should arrange now
	/// (`FrameArrangeItems`: the eight <see cref="Engine_UiFrame"/> passes then
	/// <see cref="BorderClones.Apply"/>). The countdown re-lays the frame out for
	/// <see cref="Engine_UiFrame.FramesUntilStopUpdating"/> ticks because contents resolve a tick
	/// or two late; cloning happens on the first of those ticks with a border stated and no
	/// clones yet.
	/// </summary>
	public bool Update(UiNode frame, Engine_UiFrame.State state, Node3D frameParent)
	{
		if (_adopted.Add(frame))
		{
			state.FramesUntilStopUpdating = Engine_UiFrame.FramesUntilStopUpdating;
			state.HaveClonedBorderElements = false;
		}

		if (state.FramesUntilStopUpdating <= 0)
		{
			return false;
		}

		--state.FramesUntilStopUpdating;

		if (state.BorderSize != 0 && !state.HaveClonedBorderElements)
		{
			CloneBorderElements(frame, state, frameParent);
		}

		return true;
	}

	/// <summary>
	/// `FrameCloneBorderElements` — copies the right slice set out of the theme and parents it
	/// beside the frame. Returns the clones, the held set when the frame already cloned, or
	/// null when the theme has nothing to clone from.
	///
	/// Order is the original's: no-theme returns with the flag untouched, already-cloned returns,
	/// then the flag is set true before the style branch.
	/// </summary>
	public BorderClones? CloneBorderElements(
		UiNode frame, Engine_UiFrame.State state, Node3D frameParent)
	{
		if (!HasStyle1 && !HasStyle2)
		{
			return null;
		}

		if (state.HaveClonedBorderElements)
		{
			return _cloned.TryGetValue(frame, out BorderClones? held) ? held : null;
		}

		state.HaveClonedBorderElements = true;

		var clones = new BorderClones();
		if (state.UsesBlocks)
		{
			CloneBlockBased(clones, frameParent);
		}
		else
		{
			CloneBeamBased(clones, frameParent);
		}

		_cloned[frame] = clones;
		return clones;
	}

	/// <summary>The clones a frame already made, or null before <see cref="Update"/> got there.</summary>
	public BorderClones? ClonesFor(UiNode frame) =>
		_cloned.TryGetValue(frame, out BorderClones? held) ? held : null;

	/// <summary>
	/// Forgets one frame's clones without freeing them — for when the parent subtree they live
	/// in is being freed wholesale, which takes the clones with it.
	/// </summary>
	public void Forget(UiNode frame) => _cloned.Remove(frame);

	/// <summary>
	/// The whole dress pass: re-runs the layout engine over the document and, for every frame
	/// whose parent has a live clone in <paramref name="binding"/>, clones the border set and
	/// places it on the computed layout. Returns how many border pieces were placed.
	///
	/// The layout is the parse-time one: a box whose stated text outgrows its authored text keeps
	/// the authored frame until content placement follows live text.
	/// </summary>
	public int Dress(Engine_UiScreen document, Engine_UiCloneBinding binding)
	{
		int placed = 0;
		Engine_UiFrameDriver.Apply(
			document,
			// Clone first, then place — `frame.dis main.0 pc12..21`; pass 7 bails unless the
			// clone has already set `HaveClonedBorderElements`.
			(frame, state) =>
			{
				if (LiveParent(frame, binding) is { } parent)
				{
					Update(frame, state, parent);
				}
			},
			(frame, _, border) =>
			{
				if (border.Ran && ClonesFor(frame) is { } clones)
				{
					placed += clones.Apply(border);
				}
			});

		return placed;
	}

	/// <summary>The live node standing in for the frame's parent, or null when nothing holds it.</summary>
	private static Node3D? LiveParent(UiNode frame, Engine_UiCloneBinding binding) =>
		frame.Parent is { } owner ? binding.NodeFor(owner.Ordinal) : null;

	/// <summary>
	/// `FrameCloneBlockBasedBorderElements` — style 2: corners, Middle, and the fixed tile
	/// pools, in the original's clone order (cells first, then the horizontal pairs, then the
	/// vertical pairs).
	/// </summary>
	private void CloneBlockBased(BorderClones clones, Node3D frameParent)
	{
		foreach ((Engine_UiFrame.Slot slot, string name) in Style2CellNames)
		{
			if (_style2.TryGetValue(slot, out Node3D? source))
			{
				clones.Slots[slot] = Clone(source, frameParent, name);
			}
		}

		for (int i = 1; i <= Engine_UiFrame.MaxHorizontalBlocks; ++i)
		{
			if (_style2UpperBlock is { } upper)
			{
				clones.UpperBlocks.Add(Clone(upper, frameParent, "UpperBlock" + i));
			}

			if (_style2LowerBlock is { } lower)
			{
				clones.LowerBlocks.Add(Clone(lower, frameParent, "LowerBlock" + i));
			}
		}

		for (int i = 1; i <= Engine_UiFrame.MaxVerticalBlocks; ++i)
		{
			if (_style2LeftBlock is { } left)
			{
				clones.LeftBlocks.Add(Clone(left, frameParent, "LeftBlock" + i));
			}

			if (_style2RightBlock is { } right)
			{
				clones.RightBlocks.Add(Clone(right, frameParent, "RightBlock" + i));
			}
		}
	}

	/// <summary>
	/// `FrameCloneBeamBasedBorderElements` — style 1: all nine slices, no pools, in the
	/// original's clone order (upper row, left beam, Middle, right beam, lower row).
	/// </summary>
	private void CloneBeamBased(BorderClones clones, Node3D frameParent)
	{
		foreach ((Engine_UiFrame.Slot slot, string name) in Style1Names)
		{
			if (_style1.TryGetValue(slot, out Node3D? source))
			{
				clones.Slots[slot] = Clone(source, frameParent, name);
			}
		}
	}

	/// <summary>
	/// One clone: duplicated with its own materials (pass 7 fades blocks independently and
	/// `Duplicate()` shares sub-resources), keeping the authored basis, since a block's size is
	/// never driven and a zeroed basis would draw every 32-unit tile as a 100-unit quad. Position
	/// starts at zero; pass 7 places it on the first arrange. The name is stamped so the driver's
	/// by-name path (`SlotNames`, 1-based `UpperBlock1`…) resolves clones like authored slices.
	/// </summary>
	private static Node3D Clone(Node3D source, Node3D parent, string name)
	{
		var clone = (Node3D)source.Duplicate();
		clone.Name = name;
		clone.Transform = new Transform3D(source.Transform.Basis, Vector3.Zero);
		clone.Visible = true;
		Engine_UiTheme.OwnMaterials(clone);
		parent.AddChild(clone);
		return clone;
	}

	/// <summary>One line for the run — what the theme yielded.</summary>
	public string Describe() =>
		$"[frame-cloning] sources={SourceCount}/18" +
		$" style1={(HasStyle1 ? _style1.Count : 0)}/9" +
		$" style2={(HasStyle2 ? _style2.Count : 0)}+blocks" +
		$" frames={_cloned.Count}";

	/// <summary>
	/// One frame's cloned border, keyed as <see cref="Engine_UiFrame.BorderLayout"/> keys its
	/// output: <see cref="Engine_UiFrame.Slot"/> for the stretched slices, 1-based edge lists for
	/// the tiles.
	/// </summary>
	public sealed class BorderClones
	{
		public readonly Dictionary<Engine_UiFrame.Slot, Node3D> Slots = new();

		/// <summary>Index i holds the tile pass 7 calls block i+1 — `UpperBlock1`… in tree order.</summary>
		public readonly List<Node3D> UpperBlocks = new();

		public readonly List<Node3D> LowerBlocks = new();

		public readonly List<Node3D> LeftBlocks = new();

		public readonly List<Node3D> RightBlocks = new();

		/// <summary>Every node this frame cloned.</summary>
		public int Count => Slots.Count +
			UpperBlocks.Count + LowerBlocks.Count + LeftBlocks.Count + RightBlocks.Count;

		/// <summary>
		/// Drives the clones from one pass-7 layout; returns how many nodes were written. A slot
		/// takes position and scale, a block takes opacity always and position only while visible
		/// (a hidden block is faded, not moved). Scale goes through the basis columns, never
		/// <c>node.Scale</c> — a read-modify-write of that flattens axes on a singular basis — and
		/// a stated position lands as <c>origin = position - basis * pivot</c>.
		///
		/// The pivot is the clone's own: pass 7 writes position.x/y, opacity and scale.x/y only
		/// (`framesizeandpositioning.dis main.6`), so a slice keeps its authored pivot, and the
		/// shipped ones carry a non-zero pivot.y.
		/// </summary>
		public int Apply(Engine_UiFrame.BorderLayout layout)
		{
			if (!layout.Ran)
			{
				return 0;
			}

			int written = 0;

			foreach ((Engine_UiFrame.Slot slot, Engine_UiFrame.SlotPlacement at) in layout.Slots)
			{
				if (Slots.TryGetValue(slot, out Node3D? node))
				{
					Place(node, at.X, at.Y, at.ScaleX, at.ScaleY);
					++written;
				}
			}

			written += ApplyBlocks(UpperBlocks, layout.UpperBlocks);
			written += ApplyBlocks(LowerBlocks, layout.LowerBlocks);
			written += ApplyBlocks(LeftBlocks, layout.LeftBlocks);
			written += ApplyBlocks(RightBlocks, layout.RightBlocks);
			return written;
		}

		private static int ApplyBlocks(List<Node3D> clones,
			IReadOnlyList<Engine_UiFrame.BlockPlacement> blocks)
		{
			int written = 0;
			foreach (Engine_UiFrame.BlockPlacement block in blocks)
			{
				int at = block.Index - 1;
				if (at < 0 || at >= clones.Count)
				{
					continue;
				}

				Node3D node = clones[at];
				Fade(node, block.Opacity);
				if (block.Opacity > 0f)
				{
					Place(node, block.X, block.Y, null, null);
				}

				++written;
			}

			return written;
		}

		/// <summary>
		/// One transform write. Null scales leave the authored basis alone, which keeps a block a
		/// 32-unit tile. A scaled column keeps its direction and takes the new length; a zero
		/// column is rebuilt from the cross product of the other two to stay right-handed. Z is
		/// untouched: the engine states X and Y only.
		/// </summary>
		private static void Place(Node3D node, float x, float y, float? scaleX, float? scaleY)
		{
			Transform3D before = node.Transform;
			Basis basis = before.Basis;

			for (int axis = 0; axis < 2; ++axis)
			{
				if ((axis == 0 ? scaleX : scaleY) is not { } length)
				{
					continue;
				}

				Vector3 column = basis[axis];
				if (column.LengthSquared() <= 0.000001f)
				{
					column = basis[(axis + 1) % 3].Cross(basis[(axis + 2) % 3]);
				}

				basis[axis] = column.LengthSquared() > 0.000001f
					? column.Normalized() * length
					: (axis == 0 ? Vector3.Right : Vector3.Up) * length;
			}

			Vector3 offset = basis * PivotOf(node);
			var origin = new Vector3(x - offset.X, y - offset.Y, before.Origin.Z);
			node.Transform = new Transform3D(basis, origin);
		}

		/// <summary>
		/// The clone's authored pivot — node meta stamped by <see cref="Engine_UiSceneBuilder"/>
		/// and carried across by <c>Duplicate()</c>; zero when absent.
		/// </summary>
		private static Vector3 PivotOf(Node3D node) => Engine_UiSceneBuilder.PivotOf(node);

		/// <summary>
		/// One opacity write, in the engine's 0..100 units, applied down the whole subtree: a
		/// tile authored as a small group fades as one.
		/// </summary>
		private static void Fade(Node node, float opacity)
		{
			if (Engine_UiTheme.SetIconOpacity(node, opacity / 100f))
			{
				return;
			}

			if (node is MeshInstance3D mesh && mesh.MaterialOverride is ShaderMaterial material)
			{
				material.SetShaderParameter("opacity", opacity / 100f);
			}

			foreach (Node child in node.GetChildren())
			{
				Fade(child, opacity);
			}
		}
	}
}
