using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// Runs <see cref="Engine_UiFrame"/> over a loaded screen. <see cref="Engine_UiFrame"/> is the
/// shipped Lua as arithmetic and knows nothing about <see cref="UiNode"/>; this knows about both
/// and nothing about layout.
///
/// Runs after <see cref="Engine_UiScreen"/> has read the file and before the scene is built,
/// writing into the <see cref="UiNode"/> fields the builder consumes — so nothing here touches
/// Godot and the editor, viewers and game share one path.
///
/// Deepest frame FIRST: a frame containing another reads the inner one's size as
/// <c>FrameWidth</c>/<c>FrameHeight</c> (see <see cref="Engine_UiFrame.Item"/>), so the inner one
/// must have computed it already.
/// </summary>
public static class Engine_UiFrameDriver
{
	/// <summary>
	/// The four attributes <c>FrameInitialise</c> fetches. Their PRESENCE identifies a frame: the
	/// file's frame-script binding is not kept by our reader, and only a frame carries these.
	/// </summary>
	private static readonly uint BorderSizeId = Engine_GameHash.AttributeId("BorderSize");

	/// <summary>
	/// `FrameRemoveInappropriateContents`' verdict on one item — <c>frame.dis main.2 pc12..39</c>.
	/// The nesting is not flat and the order is load-bearing: a <c>text</c> element reaches only
	/// the <c>textstring == ''</c> test (pc16..25), while <c>active == false</c> sits in the arm
	/// for elements that are neither text nor behaviour (pc33..39) — so an inactive but written
	/// label is KEPT.
	///
	/// The behaviour arm (pc26..32) compares against <c>'behavior'</c>, which
	/// <c>getElementType</c> never returns (see <see cref="Engine_UiFrame.KindBehaviour"/>); ours
	/// branches on the element type, so the arm is live here and dead there.
	///
	/// Text is an element that HAS a text record, tested on the finished string
	/// (<c>getAttribute(item, 'textstring')</c>); <c>active</c> is <see cref="UiNode.Visible"/>.
	/// </summary>
	private static bool Displayed(UiNode node)
	{
		if (node.Text is { } text)
		{
			return text.Display.Length != 0;
		}

		if (node.Type == BgfElementType.Behavior)
		{
			return false;
		}

		return node.Visible;
	}

	private static readonly uint SizeToContentId = Engine_GameHash.AttributeId("SizeToContent");

	/// <summary>
	/// Each frame's state, which lives as long as the frame does. `FrameUpdate` reads
	/// <c>self.State.FramesUntilStopUpdating</c> and <c>self.State.HaveClonedBorderElements</c>
	/// off the component instance (`frame.dis main.0 pc0..21`), and `FrameCloneBorderElements`
	/// returns on a second call (`framecloning.dis main.0 pc8..24`) — a frame clones its border
	/// set exactly once. Weakly keyed on the frame element, so it goes with the document.
	/// </summary>
	private static readonly ConditionalWeakTable<UiNode, Engine_UiFrame.State> States = new();

	/// <summary>The nine border slices, by the names the Lua looks them up under.</summary>
	/// <summary>
	/// The nine cell names per style — `theme/themeframes.lua` declares both sets and
	/// `frame/framecloning.lua`'s `FrameCloneBeamBasedBorderElements` /
	/// `FrameCloneBlockBasedBorderElements` are the two branches. Style 1 calls its edges beams,
	/// style 2 blocks; nine cells either way, four corners and a Middle shared.
	/// </summary>
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
	/// Lays out every frame in the screen and returns what it touched. Never throws: a screen
	/// whose frames cannot be laid out still puts its art up.
	/// </summary>
	public static Report Apply(Engine_UiScreen screen) => Apply(screen, null, null);

	/// <summary>
	/// The same drive, with two per-frame taps for a consumer holding live art. The document has
	/// no border slices — the original clones them from the theme at runtime (`framecloning.lua`,
	/// ported in <see cref="Engine_UiFrameCloning"/>):
	///
	/// <list type="bullet">
	/// <item><paramref name="onClone"/> runs BEFORE the arrange, because `FrameUpdate` calls
	/// `FrameCloneBorderElements` before `FrameArrangeItems` (`frame.dis main.0 pc12..21`) and
	/// pass 7 is gated on the flag the clone sets.</item>
	/// <item><paramref name="onPlace"/> runs after, with the computed border — pass 7's output
	/// landing on the nodes the clone produced.</item>
	/// </list>
	/// </summary>
	public static Report Apply(
		Engine_UiScreen screen,
		Action<UiNode, Engine_UiFrame.State>? onClone,
		Action<UiNode, Engine_UiFrame.State, Engine_UiFrame.BorderLayout>? onPlace)
	{
		// Plain loops throughout: this re-runs on every text edge of an owned screen, and the
		// LINQ form was an enumerator, a closure and a list per step of every frame.
		var frames = new List<UiNode>();
		foreach (UiNode node in screen.Nodes)
		{
			if (IsFrame(node))
			{
				frames.Add(node);
			}
		}

		if (frames.Count == 0)
		{
			return default;
		}

		// Deepest first — see the class remarks.
		frames.Sort(static (a, b) => Depth(b).CompareTo(Depth(a)));

		var sizes = new Dictionary<UiNode, (float Width, float Height)>();
		var tree = new Tree(sizes);
		var report = new Report(frames.Count, 0, 0, 0, 0);

		foreach (UiNode frame in frames)
		{
			Engine_UiFrame.State state = States.GetValue(frame, _ => new Engine_UiFrame.State());
			Engine_UiFrame.Initialise(state, tree, frame);

			// The dimensions quad is never drawn: `FrameInitialise` zeroes its opacity on every
			// path, including the ones that discard the reference.
			if (tree.NamedObject(frame, Engine_UiFrame.DimensionsElement) is UiNode dimensions)
			{
				dimensions.Opacity = 0f;
			}

			// The clone step comes first: `FrameUpdate` clones then arranges
			// (`frame.dis main.0 pc12..21`), and pass 7 draws nothing without the clone's flag.
			onClone?.Invoke(frame, state);

			// `FrameRemoveInappropriateContents`: the list is rebuilt from scratch every pass
			// (`frame.dis main.2 pc1..2`, NEWTABLE into `State.DisplayedContents`) and every
			// surviving item appended in order (pc40..47).
			var displayed = new List<UiNode>(state.Contents.Count);
			foreach (object content in state.Contents)
			{
				var node = (UiNode)content;
				if (Displayed(node))
				{
					displayed.Add(node);
				}
			}

			var items = new List<Engine_UiFrame.Item>(displayed.Count);
			var alignments = new List<int?>(displayed.Count);
			foreach (UiNode node in displayed)
			{
				items.Add(Measure(tree, node));
				alignments.Add((int?)tree.Attribute(node, "FrameAlignment"));
			}

			Engine_UiFrame.CalcLengthAndMaxBreadth(state, items);
			Engine_UiFrame.CalcDimensions(state);
			Engine_UiFrame.ClampSize(state);
			Engine_UiFrame.CalcBorders(state);
			Engine_UiFrame.CalcOuterSize(state);

			// The frame's own size, published for whatever frame contains it, keyed by the
			// frame's PARENT — the behaviour itself is filtered out of every Contents list.
			// Only onto a `group` parent: `frame.dis main.1 pc24..40` gates both setAttribute
			// calls on `getElementType(self.parent) == 'group'`, and `Attribute` below consults
			// this map before the authored attributes, so an ungated publish would shadow a
			// parent's own `FrameWidth`.
			if (frame.Parent is { } owner && tree.TypeOf(owner) == Engine_UiFrame.KindGroup)
			{
				sizes[owner] = (state.Width, state.Height);
			}

			// A document that AUTHORED its slices stands in for the clone step; a screen whose
			// tap cloned already carries the flag and needs no search.
			state.HaveClonedBorderElements =
				FindSlices(frame, out var slices) || state.HaveClonedBorderElements;
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

			onPlace?.Invoke(frame, state, border);

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
	/// A frame is an element carrying the attributes only a frame carries. `BorderSize` and
	/// `SizeToContent` are both fetched by `FrameInitialise` and appear on nothing else; both are
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
	/// Measures one item the three ways <c>FrameCalcLengthAndMaxBreadth</c> does. The
	/// bounding-box arm is ours, not the original's <c>calculateBoundingBox</c>: a TEXT element's
	/// authored box is used directly, anything else falls back to <c>scale * 100</c> — exact for
	/// a quad, an approximation for a group.
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
	/// Finds the nine border slices among the frame's siblings, by name. Returns whether ALL were
	/// found, which becomes <see cref="Engine_UiFrame.State.HaveClonedBorderElements"/>. A
	/// partial set counts as no set, so positions are never written onto elements the frame does
	/// not own.
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
	/// Applies one edge's blocks, named with a 1-based suffix. Absent blocks are skipped, not
	/// errors — the pool is authored per screen.
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

			// A hidden block is faded and NOT moved: the original writes opacity 0 and nothing
			// else on the arm that skips.
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
	private static UiNode? Find(UiNode parent, string name)
	{
		foreach (UiNode child in parent.Children)
		{
			if (child.HasAuthoredName && string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
			{
				return child;
			}
		}

		return null;
	}

	/// <summary>
	/// <see cref="Engine_UiFrame.IFrameTree"/> over our authored model. The type mapping is not
	/// one to one: the engine calls a drawable quad a "model" and a bare container a "group", and
	/// both are <see cref="BgfElementType.Node"/> here, separated by whether they carry a quad —
	/// the "model" arm measures <c>scale * 100</c> and the "group" arm asks for a nested frame's
	/// dimensions.
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

			// A nested frame's size is not authored — it is what that frame just computed,
			// published under the name the outer frame reads it by.
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
			if (node.Parent is not { } parent)
			{
				return Array.Empty<object>();
			}

			var siblings = new List<object>(parent.Children.Count);
			foreach (UiNode child in parent.Children)
			{
				siblings.Add(child);
			}

			return siblings;
		}

		public object? NamedObject(object element, string name)
		{
			var node = (UiNode)element;
			return node.Parent is { } parent ? Find(parent, name) : null;
		}
	}
}
