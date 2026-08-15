using System;
using System.Collections.Generic;
using System.Linq;

using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// Runs <see cref="Engine_UiFrame"/> over a loaded screen, so the frames in it are actually laid
/// out instead of merely being layoutable.
///
/// THIS IS THE SEAM BETWEEN A PORTED SCRIPT AND OUR SCENE. <see cref="Engine_UiFrame"/> is the
/// shipped Lua turned into arithmetic and knows nothing about <see cref="UiNode"/>; this knows
/// about both and nothing about layout. Keeping the split means a wrong answer is attributable:
/// if a panel is the wrong SIZE the passes are wrong, and if it is the right size in the wrong
/// PLACE the application is.
///
/// WHEN IT RUNS: after <see cref="Engine_UiScreen"/> has read the file and before the scene is
/// built, writing straight into the <see cref="UiNode"/> fields the builder already consumes.
/// That is why nothing here touches Godot — laying out into the authored model rather than into
/// the built one means the editor, the viewers and the game all get the same result from the one
/// path, and no second application of the same numbers exists to drift.
///
/// DEEPEST FRAME FIRST. A frame that contains another reads the inner one's size as
/// <c>FrameWidth</c>/<c>FrameHeight</c> — see <see cref="Engine_UiFrame.Item"/> — and the inner
/// one has to have computed it by then. Sorting by depth descending is what makes a nested
/// panel size its parent instead of being sized by it.
/// </summary>
public static class Engine_UiFrameDriver
{
	/// <summary>
	/// The four attributes <c>FrameInitialise</c> fetches. Their PRESENCE is also how a frame is
	/// recognised: the shipped Lua knows which behaviours are frames because the file binds the
	/// frame script to them, and that binding is not something our reader keeps — but only a
	/// frame carries these.
	/// </summary>
	private static readonly uint BorderSizeId = Engine_GameHash.AttributeId("BorderSize");

	private static readonly uint SizeToContentId = Engine_GameHash.AttributeId("SizeToContent");

	/// <summary>The nine border slices, by the names the Lua looks them up under.</summary>
	private static readonly (Engine_UiFrame.Slot Slot, string Name)[] SlotNames =
	{
		(Engine_UiFrame.Slot.UpperLeftCorner, "UpperLeftCorner"),
		(Engine_UiFrame.Slot.UpperRightCorner, "UpperRightCorner"),
		(Engine_UiFrame.Slot.LowerLeftCorner, "LowerLeftCorner"),
		(Engine_UiFrame.Slot.LowerRightCorner, "LowerRightCorner"),
		(Engine_UiFrame.Slot.Middle, "Middle"),
		(Engine_UiFrame.Slot.UpperBeam, "UpperBeam"),
		(Engine_UiFrame.Slot.LowerBeam, "LowerBeam"),
		(Engine_UiFrame.Slot.LeftBeam, "LeftBeam"),
		(Engine_UiFrame.Slot.RightBeam, "RightBeam"),
	};

	/// <summary>What one run did, so a caller can report it rather than guess.</summary>
	public readonly record struct Report(
		int Frames, int Contents, int Slots, int Blocks, int WithoutBorder);

	/// <summary>
	/// Lays out every frame in the screen. Returns what it touched.
	///
	/// Never throws. A screen whose frames cannot be laid out should still put its art up — that
	/// is what it did before this existed, and it is a far better failure than a black menu.
	/// </summary>
	public static Report Apply(Engine_UiScreen screen)
	{
		var frames = screen.Nodes.Where(IsFrame).ToList();
		if (frames.Count == 0)
		{
			return default;
		}

		// Deepest first — see the class remarks.
		frames.Sort((a, b) => Depth(b).CompareTo(Depth(a)));

		var sizes = new Dictionary<UiNode, (float Width, float Height)>();
		var tree = new Tree(sizes);
		var report = new Report(frames.Count, 0, 0, 0, 0);

		foreach (UiNode frame in frames)
		{
			var state = new Engine_UiFrame.State();
			Engine_UiFrame.Initialise(state, tree, frame);

			// THE DIMENSIONS QUAD IS NEVER DRAWN. `FrameInitialise` ends by zeroing its opacity
			// on every path it can take, including the ones that discard the reference.
			if (tree.NamedObject(frame, Engine_UiFrame.DimensionsElement) is UiNode dimensions)
			{
				dimensions.Opacity = 0f;
			}

			// DisplayedContents is Contents after the page has filtered it. We have no page yet,
			// so everything the frame owns is displayed — except elements the slide baseline has
			// switched OFF, because the engine skips an inactive node and its whole subtree and
			// laying one out would reserve space for something that never draws.
			var displayed = state.Contents.Cast<UiNode>().Where(n => n.Visible).ToList();

			var items = displayed.Select(n => Measure(tree, n)).ToList();
			var alignments = displayed
				.Select(n => (int?)tree.Attribute(n, "FrameAlignment"))
				.ToList();

			Engine_UiFrame.CalcLengthAndMaxBreadth(state, items);
			Engine_UiFrame.CalcDimensions(state);
			Engine_UiFrame.ClampSize(state);
			Engine_UiFrame.CalcBorders(state);
			Engine_UiFrame.CalcOuterSize(state);

			// The frame's own size, published for whatever frame contains it. Keyed by the
			// frame's PARENT because that is the element an outer frame sees as its content —
			// the behaviour itself is filtered out of every Contents list.
			if (frame.Parent is { } owner)
			{
				sizes[owner] = (state.Width, state.Height);
			}

			state.HaveClonedBorderElements = FindSlices(frame, out var slices);
			if (!state.HaveClonedBorderElements)
			{
				report = report with { WithoutBorder = report.WithoutBorder + 1 };
			}

			Engine_UiFrame.BorderLayout border =
				Engine_UiFrame.PositionAndScaleBorderElements(state);

			if (border.Ran)
			{
				foreach ((Engine_UiFrame.Slot slot, Engine_UiFrame.SlotPlacement at) in border.Slots)
				{
					if (slices.TryGetValue(slot, out UiNode? node))
					{
						node.PositionX = at.X;
						node.PositionY = at.Y;
						node.ScaleX = at.ScaleX;
						node.ScaleY = at.ScaleY;
						report = report with { Slots = report.Slots + 1 };
					}
				}

				report = report with
				{
					Blocks = report.Blocks +
						ApplyBlocks(frame, "UpperBlock", border.UpperBlocks) +
						ApplyBlocks(frame, "LowerBlock", border.LowerBlocks) +
						ApplyBlocks(frame, "LeftBlock", border.LeftBlocks) +
						ApplyBlocks(frame, "RightBlock", border.RightBlocks),
				};
			}

			IReadOnlyList<Engine_UiFrame.Placement> placed =
				Engine_UiFrame.PlaceContents(state, alignments);

			for (int i = 0; i < placed.Count && i < displayed.Count; ++i)
			{
				displayed[i].PositionX = placed[i].X;
				displayed[i].PositionY = placed[i].Y;
			}

			report = report with { Contents = report.Contents + placed.Count };
		}

		return report;
	}

	/// <summary>
	/// A frame is an element carrying the attributes only a frame carries.
	///
	/// The file binds the frame SCRIPT to a behaviour and our reader does not keep that binding,
	/// so the attributes stand in for it. `BorderSize` and `SizeToContent` are both fetched by
	/// `FrameInitialise` and neither appears on anything else, so either is sufficient; both are
	/// tested because a frame with no border still has to be laid out.
	/// </summary>
	private static bool IsFrame(UiNode node) =>
		node.Element is { } element &&
		(element.TryGetAttribute(BorderSizeId, out _) ||
		 element.TryGetAttribute(SizeToContentId, out _));

	private static int Depth(UiNode node)
	{
		int depth = 0;
		for (UiNode? at = node.Parent; at is not null; at = at.Parent)
		{
			++depth;
		}

		return depth;
	}

	/// <summary>
	/// Measures one item the three ways <c>FrameCalcLengthAndMaxBreadth</c> measures them.
	///
	/// The bounding-box arm is the one that is ours rather than the original's: the engine calls
	/// <c>calculateBoundingBox</c>, which walks the element's real geometry. A TEXT element's box
	/// is authored on it and is used directly; anything else falls back to the quad convention of
	/// <c>scale * 100</c>, which is exact for a quad and an approximation for a group of them.
	/// Stated rather than hidden, because it is the one number in this file that is not the
	/// shipped arithmetic.
	/// </summary>
	private static Engine_UiFrame.Item Measure(Tree tree, UiNode node)
	{
		string kind = tree.TypeOf(node);

		if (kind == Engine_UiFrame.KindModel)
		{
			return new Engine_UiFrame.Item(kind, ScaleX: node.ScaleX, ScaleY: node.ScaleY);
		}

		if (kind == Engine_UiFrame.KindGroup)
		{
			return new Engine_UiFrame.Item(kind,
				FrameWidth: tree.Attribute(node, "FrameWidth"),
				FrameHeight: tree.Attribute(node, "FrameHeight"));
		}

		if (node.Text is { } text && text.BoxWidth > 0f)
		{
			return new Engine_UiFrame.Item(kind,
				BoundsWidth: text.BoxWidth, BoundsHeight: text.BoxHeight);
		}

		return new Engine_UiFrame.Item(kind,
			BoundsWidth: node.ScaleX * Engine_UiFrame.QuadBaseSize,
			BoundsHeight: node.ScaleY * Engine_UiFrame.QuadBaseSize);
	}

	/// <summary>
	/// Finds the nine border slices among the frame's siblings, by name.
	///
	/// Returns whether ALL of them were found, which becomes
	/// <see cref="Engine_UiFrame.State.HaveClonedBorderElements"/> — the original's gate on a
	/// runtime cloning step we do not perform. A partial set is treated as no set on purpose: a
	/// frame with four corners and no beams drawn at its computed size looks like a bug in the
	/// layout rather than a missing step, and the whole point of the flag is to not write
	/// positions onto elements the frame does not own.
	/// </summary>
	private static bool FindSlices(UiNode frame,
		out Dictionary<Engine_UiFrame.Slot, UiNode> slices)
	{
		slices = new Dictionary<Engine_UiFrame.Slot, UiNode>();
		if (frame.Parent is not { } parent)
		{
			return false;
		}

		foreach ((Engine_UiFrame.Slot slot, string name) in SlotNames)
		{
			if (Find(parent, name) is { } node)
			{
				slices[slot] = node;
			}
		}

		// The two styles carry different sets: a block frame has corners and a Middle, a beam
		// frame has those plus four beams. Either complete set counts.
		bool corners =
			slices.ContainsKey(Engine_UiFrame.Slot.UpperLeftCorner) &&
			slices.ContainsKey(Engine_UiFrame.Slot.UpperRightCorner) &&
			slices.ContainsKey(Engine_UiFrame.Slot.LowerLeftCorner) &&
			slices.ContainsKey(Engine_UiFrame.Slot.LowerRightCorner) &&
			slices.ContainsKey(Engine_UiFrame.Slot.Middle);

		return corners;
	}

	/// <summary>
	/// Applies one edge's blocks, which are named with a 1-based suffix. Absent blocks are
	/// skipped rather than reported as an error — the pool is authored per screen and a frame
	/// that never grows past four blocks does not ship thirty.
	/// </summary>
	private static int ApplyBlocks(UiNode frame, string prefix,
		IReadOnlyList<Engine_UiFrame.BlockPlacement> blocks)
	{
		if (frame.Parent is not { } parent)
		{
			return 0;
		}

		int applied = 0;
		foreach (Engine_UiFrame.BlockPlacement block in blocks)
		{
			if (Find(parent, prefix + block.Index) is not { } node)
			{
				continue;
			}

			// A hidden block is faded and NOT moved, which is what the original does — it writes
			// opacity 0 and nothing else on the arm that skips.
			node.Opacity = block.Opacity;
			if (block.Opacity > 0f)
			{
				node.PositionX = block.X;
				node.PositionY = block.Y;
			}

			++applied;
		}

		return applied;
	}

	/// <summary>A direct child by authored name, case-insensitively.</summary>
	private static UiNode? Find(UiNode parent, string name) =>
		parent.Children.FirstOrDefault(c =>
			c.HasAuthoredName && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// <see cref="Engine_UiFrame.IFrameTree"/> over our authored model.
	///
	/// THE TYPE MAPPING IS THE PART THAT MATTERS. The Lua branches on four strings and our
	/// reader has an enum, and the two do not line up one to one: the engine calls a drawable
	/// quad a "model" and a bare container a "group", and both of those are
	/// <see cref="BgfElementType.Node"/> here. Carrying a quad is what separates them, and it is
	/// the right test rather than a convenient one — the "model" arm measures
	/// <c>scale * 100</c>, which IS a quad's size, and the "group" arm asks for a nested frame's
	/// dimensions, which only a container can have.
	/// </summary>
	private sealed class Tree : Engine_UiFrame.IFrameTree
	{
		private readonly Dictionary<UiNode, (float Width, float Height)> _sizes;

		public Tree(Dictionary<UiNode, (float Width, float Height)> sizes) => _sizes = sizes;

		public string TypeOf(object element)
		{
			var node = (UiNode)element;
			return node.Type switch
			{
				BgfElementType.Text => Engine_UiFrame.KindText,
				BgfElementType.Behavior => Engine_UiFrame.KindBehaviour,
				_ => node.Quad is not null
					? Engine_UiFrame.KindModel
					: Engine_UiFrame.KindGroup,
			};
		}

		public float? Attribute(object element, string name)
		{
			var node = (UiNode)element;

			// A nested frame's size is not an authored attribute — it is what that frame worked
			// out a moment ago, published under the name the outer frame reads it by.
			if (_sizes.TryGetValue(node, out (float Width, float Height) size))
			{
				if (name == "FrameWidth")
				{
					return size.Width;
				}

				if (name == "FrameHeight")
				{
					return size.Height;
				}
			}

			if (node.Element is not { } backing ||
				!backing.TryGetAttribute(name, out BgfAttribute attribute))
			{
				return null;
			}

			return attribute.Type switch
			{
				BgfValueType.Float => attribute.AsFloat(),
				BgfValueType.Bool => attribute.AsBool() ? 1f : 0f,
				BgfValueType.Int1 or BgfValueType.Int2 => attribute.AsInt(),
				_ => null,
			};
		}

		public IReadOnlyList<object> Siblings(object element)
		{
			var node = (UiNode)element;
			return node.Parent is { } parent
				? parent.Children.Cast<object>().ToList()
				: Array.Empty<object>();
		}

		public object? NamedObject(object element, string name)
		{
			var node = (UiNode)element;
			return node.Parent is { } parent ? Find(parent, name) : null;
		}
	}
}
