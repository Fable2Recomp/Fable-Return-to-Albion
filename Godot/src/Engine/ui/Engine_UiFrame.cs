namespace Refii.Ui;

/// <summary>
/// Fable 2's frame layout engine: the nine-slice, 32-unit-block frame every in-game screen and
/// display box is built on. Port of <c>scripts/gameface/frame/</c>; the eight passes run in the
/// order <c>framesizeandpositioning.lua</c> registers them (CalcLengthAndMaxBreadth,
/// CalcDimensions, ClampSize, CalcBorders, CalcOuterSize, CalcOriginPosition,
/// PositionAndScaleBorderElements, PlaceContents). A frame's authored size is the
/// <c>scale.x</c>/<c>scale.y</c> of its hidden <c>DimensionsImage</c> child (opacity set to 0).
/// </summary>
public static class Engine_UiFrame
{
	/// <summary>Stack down the screen; the default. <c>FRAME_TYPE_VERTICAL = 0</c>.</summary>
	public const int TypeVertical = 0;

	/// <summary>Stack across; swaps length and breadth. <c>FRAME_TYPE_HORIZONTAL = 1</c>.</summary>
	public const int TypeHorizontal = 1;

	public const int AlignmentLeft = 0;
	public const int AlignmentRight = 1;
	public const int AlignmentUpper = 2;
	public const int AlignmentLower = 3;

	/// <summary>The grid every frame is built on; sizes round up to whole blocks. <c>BLOCK_SIZE = 32</c>.</summary>
	public const float BlockSize = 32f;

	/// <summary>Largest inner width, in units. <c>MAX_WIDTH = 960</c> — the 4:3 canvas width.</summary>
	public const float MaxWidth = 960f;

	/// <summary>Largest inner height. <c>MAX_HEIGHT = 800</c>.</summary>
	public const float MaxHeight = 800f;

	/// <summary>A corner slice is two blocks (constants chunk instruction 15: <c>BLOCK_SIZE * 2</c>).</summary>
	public const float CornerSize = BlockSize * 2f;

	/// <summary>How much consecutive lines of text overlap. <c>TEXT_VERTICAL_OVERLAP = 2</c>.</summary>
	public const float TextVerticalOverlap = 2f;

	/// <summary>
	/// <c>MAX_HORIZ_BLOCKS = ceil(MAX_WIDTH / BLOCK_SIZE) - 4</c> = 26 and
	/// <c>MAX_VERT_BLOCKS = ceil(MAX_HEIGHT / BLOCK_SIZE) - 4</c> = 21, derived as the chunk does
	/// (`framestateandconstants.dis main.0 pc17..38`). Block pool per frame:
	/// 4 corners + Middle + 26 + 26 + 21 + 21 = 99 clones.
	/// </summary>
	public static readonly int MaxHorizontalBlocks =
		(int)System.Math.Ceiling(MaxWidth / BlockSize) - 4;

	public static readonly int MaxVerticalBlocks =
		(int)System.Math.Ceiling(MaxHeight / BlockSize) - 4;

	/// <summary>
	/// Border metrics per <c>BorderSize</c>, from <c>FrameCalcBorders</c>' five-way branch. They
	/// overwrite the constants chunk's 24 / 16 / 8 seed every pass (size 2 is 24 / 12 / 12 live);
	/// a size outside 0..4 keeps the seed, see <see cref="CalcBorders"/>. Overlap is half the
	/// block, offset the remainder — a table because the shipped code is a table.
	/// </summary>
	private static readonly (float Block, float Overlap)[] BorderMetrics =
	{
		(0f, 0f),    // 0 — no border
		(18f, 9f),
		(24f, 12f),
		(36f, 18f),
		(64f, 32f),
	};

	/// <summary>Layout passes after the frame looks settled; contents (text, models) can resolve late.</summary>
	public const int FramesUntilStopUpdating = 5;

	/// <summary>The child whose scale carries the authored size, and which is then hidden.</summary>
	public const string DimensionsElement = "DimensionsImage";

	/// <summary>The one `BorderSize` that means the BLOCK family — `FrameUsesBlocks`.</summary>
	public const int BlockBorderSize = 4;

	/// <summary>One frame's working state — the shipped <c>State</c> table; mutable because the passes write it in sequence.</summary>
	public sealed class State
	{
		public int Type = TypeVertical;

		/// <summary>Outer size, after <c>FrameCalcOuterSize</c> adds the border.</summary>
		public float Width;
		public float Height;

		/// <summary>Anchor point 0..8, see <see cref="CalcOriginPosition"/>; an unmatched value behaves as centre.</summary>
		public int Origin = OriginCentre;

		/// <summary>Inner size, before the border — what the contents get.</summary>
		public float InnerWidth;
		public float InnerHeight;

		/// <summary>Accumulated content extent along the stacking axis.</summary>
		public float StackLength;

		/// <summary>Largest content extent across it.</summary>
		public float StackMaxBreadth;

		/// <summary>Per-item extents in display order, filled by <see cref="CalcLengthAndMaxBreadth"/> and walked by index in <see cref="PlaceContents"/>.</summary>
		public readonly System.Collections.Generic.List<float> ItemLength = new();

		public readonly System.Collections.Generic.List<float> ItemBreadth = new();

		/// <summary>Siblings the frame lays out, filled by <see cref="Initialise"/>; the original's <c>DisplayedContents</c> is this after page filtering.</summary>
		public readonly System.Collections.Generic.List<object> Contents = new();

		/// <summary>The authored measuring rectangle, or null when the frame sizes itself; the caller must set its opacity to 0.</summary>
		public object? Dimensions;

		/// <summary>Border block counts per edge, from <c>FrameCalcBorders</c>.</summary>
		public int UpperBlocks;
		public int LowerBlocks;
		public int LeftBlocks;
		public int RightBlocks;

		/// <summary>Which row of <see cref="BorderMetrics"/> this frame uses, 0..4.</summary>
		public int BorderSize;

		/// <summary>
		/// Live border metrics, rewritten by <see cref="CalcBorders"/>. Seeded 24 / 16 / 8
		/// (`framestateandconstants.dis main.0 pc50..52`); the seed is read before that pass runs,
		/// since <c>FrameClampSize</c> precedes <c>FrameCalcBorders</c> (`frame.dis main.1 pc9..14`)
		/// and its non-block arm floors both axes at <c>BorderOverlap * 2</c>
		/// (`framesizeandpositioning.dis main.2 pc70..84`) — first-pass minimum inner size 32.
		/// </summary>
		public float BorderBlockSize = 24f;
		public float BorderOverlap = 16f;
		public float BorderOffset = 8f;

		/// <summary>
		/// Size to contents, or keep the authored size. Absent is false:
		/// `framestateandconstants.dis main.0 pc85..89` tests <c>== true</c>, and nil is not true.
		/// </summary>
		public bool SizeToContent;

		/// <summary>
		/// Whether <c>FrameCalcOuterSize</c> adds <c>BorderOffset</c> to both axes
		/// (`framesizeandpositioning.dis main.4 pc0..2`, reads <c>self.HasBorder</c>). Always false
		/// as shipped: <c>FrameInitialise</c> fetches only Type, SizeToContent, Origin, BorderSize
		/// (`framestateandconstants.dis main.0 pc74..81`), nothing in the corpus assigns it, and its
		/// 27-bit sdbm id 0x0084337A appears in no cached .bgf (BorderSize 0x027D8E93 and
		/// SizeToContent 0x07A85AA3 do, in displaybox.bgf).
		/// </summary>
		public bool HasBorder;

		/// <summary>Whether the frame is built from whole blocks.</summary>
		public bool UsesBlocks = true;

		/// <summary>Set when any measured item is text, which changes the vertical maths.</summary>
		public bool ContainsText;

		/// <summary>False until the nine border slices have been cloned for this frame.</summary>
		public bool HaveClonedBorderElements;

		/// <summary>Counts down from <see cref="FramesUntilStopUpdating"/>.</summary>
		public int FramesUntilStopUpdating = Engine_UiFrame.FramesUntilStopUpdating;
	}

	/// <summary>
	/// Pass 2 — <c>FrameCalcDimensions</c>: the measured stack becomes the inner rectangle. The
	/// axes swap wholesale on a horizontal frame, and a block frame loses one <c>BLOCK_SIZE</c>
	/// off each axis here (the border passes add it back). No-op, not a reset, when the frame
	/// does not size to content.
	/// </summary>
	public static void CalcDimensions(State state)
	{
		if (!state.SizeToContent)
		{
			return;
		}

		if (state.Type == TypeVertical)
		{
			state.InnerWidth = state.StackMaxBreadth;
			state.InnerHeight = state.StackLength;
		}
		else
		{
			state.InnerWidth = state.StackLength;
			state.InnerHeight = state.StackMaxBreadth;
		}

		if (state.UsesBlocks)
		{
			state.InnerWidth -= BlockSize;
			state.InnerHeight -= BlockSize;
		}
	}

	/// <summary>
	/// Pass 3 — <c>FrameClampSize</c>. Block frames: floor <c>BLOCK_SIZE</c>, ceiling
	/// <c>MAX_WIDTH/MAX_HEIGHT - CORNER_SIZE * 2</c> (832 / 672), rounded up to whole blocks.
	/// Non-block frames: floor <c>BorderOverlap * 2</c>, ceiling <c>MAX_WIDTH - BLOCK_SIZE * 2</c>
	/// (896) on BOTH axes — instruction 98 loads constant 6 (<c>MAX_WIDTH</c>) where the block
	/// branch at 32 loads constant 9 (<c>MAX_HEIGHT</c>); a shipped typo, reproduced deliberately.
	/// </summary>
	public static void ClampSize(State state)
	{
		if (state.UsesBlocks)
		{
			state.InnerWidth = System.Math.Max(state.InnerWidth, BlockSize);
			state.InnerHeight = System.Math.Max(state.InnerHeight, BlockSize);

			state.InnerWidth = System.Math.Min(state.InnerWidth, MaxWidth - CornerSize * 2f);
			state.InnerHeight = System.Math.Min(state.InnerHeight, MaxHeight - CornerSize * 2f);

			state.InnerWidth = (float)System.Math.Ceiling(state.InnerWidth / BlockSize) * BlockSize;
			state.InnerHeight = (float)System.Math.Ceiling(state.InnerHeight / BlockSize) * BlockSize;
			return;
		}

		float floor = state.BorderOverlap * 2f;
		state.InnerWidth = System.Math.Max(state.InnerWidth, floor);
		state.InnerHeight = System.Math.Max(state.InnerHeight, floor);

		// MAX_WIDTH on both axes, as shipped — see the summary.
		float ceiling = MaxWidth - BlockSize * 2f;
		state.InnerWidth = System.Math.Min(state.InnerWidth, ceiling);
		state.InnerHeight = System.Math.Min(state.InnerHeight, ceiling);
	}

	/// <summary>
	/// Pass 4 — <c>FrameCalcBorders</c>: five-way branch on <c>BorderSize</c>, then
	/// <c>BorderOffset = BorderBlockSize - BorderOverlap</c>. No else arm
	/// (`framesizeandpositioning.dis main.3 pc32..38` jumps to pc39), so an unmatched size keeps
	/// the current metrics (24 / 16 on the first pass).
	/// </summary>
	public static void CalcBorders(State state)
	{
		if (state.BorderSize >= 0 && state.BorderSize < BorderMetrics.Length)
		{
			(state.BorderBlockSize, state.BorderOverlap) = BorderMetrics[state.BorderSize];
		}

		state.BorderOffset = state.BorderBlockSize - state.BorderOverlap;
	}

	/// <summary>
	/// Pass 5 — <c>FrameCalcOuterSize</c>: adds <c>BorderOffset</c> once per axis (not per edge;
	/// the border overlaps itself, see <see cref="State.BorderOverlap"/>) when <c>HasBorder</c>.
	/// </summary>
	public static void CalcOuterSize(State state)
	{
		state.Width = state.InnerWidth + (state.HasBorder ? state.BorderOffset : 0f);
		state.Height = state.InnerHeight + (state.HasBorder ? state.BorderOffset : 0f);
	}

	/// <summary>The four questions the engine asks of the element tree; the only seam to the caller's element model.</summary>
	public interface IFrameTree
	{
		/// <summary>What <c>getElementType</c> returns: "text", "model", "group", "behaviour".</summary>
		string TypeOf(object element);

		/// <summary>An attribute by authored name, or null when the element has none.</summary>
		float? Attribute(object element, string name);

		/// <summary>The elements that share this one's parent, in authored order.</summary>
		System.Collections.Generic.IReadOnlyList<object> Siblings(object element);

		/// <summary>The element a named object reference points at, or null.</summary>
		object? NamedObject(object element, string name);
	}

	/// <summary>
	/// <c>FrameInitialise</c>: reads <c>Type</c>, <c>SizeToContent</c>, <c>Origin</c> and
	/// <c>BorderSize</c> off the frame element; every other field keeps its default.
	/// <c>DimensionsImage</c>: SizeToContent → nil; no valid reference → nil, Width = Height = 100;
	/// otherwise Width/Height = scale.x/y * 100 — and opacity = 0 in every branch, never drawn.
	/// Contents are the frame's SIBLINGS (<c>FrameGetAllChildren</c> walks
	/// <c>getChildren(getElement("parent", self.element))</c>) minus the frame, the dimensions quad
	/// and any <c>"behaviour"</c>: a frame is a behaviour beside what it lays out, not a container.
	/// </summary>
	public static void Initialise(State state, IFrameTree tree, object element)
	{
		state.Type = (int)(tree.Attribute(element, "Type") ?? TypeVertical);
		// Absent is false (`framestateandconstants.dis main.0 pc85..89` tests `== true`).
		state.SizeToContent = (tree.Attribute(element, "SizeToContent") ?? 0f) != 0f;
		state.Origin = (int)(tree.Attribute(element, "Origin") ?? OriginCentre);
		state.BorderSize = (int)(tree.Attribute(element, "BorderSize") ?? 0f);

		// `FrameUsesBlocks(self)` is `self.BorderSize == 4`; the only thing that picks between the
		// block and beam nine-slice families (`theme/themeframes.lua`: `UpperBlock` vs `UpperBeam`).
		state.UsesBlocks = state.BorderSize == BlockBorderSize;

		object? dimensions = tree.NamedObject(element, DimensionsElement);

		// The original discards the reference for a self-sizing frame.
		if (state.SizeToContent)
		{
			dimensions = null;
		}
		else if (dimensions is null)
		{
			state.Width = QuadBaseSize;
			state.Height = QuadBaseSize;
		}
		else
		{
			state.Width = (tree.Attribute(dimensions, "scale.x") ?? 1f) * QuadBaseSize;
			state.Height = (tree.Attribute(dimensions, "scale.y") ?? 1f) * QuadBaseSize;
		}

		state.Dimensions = dimensions;
		state.Contents.Clear();

		foreach (object sibling in tree.Siblings(element))
		{
			if (!ReferenceEquals(sibling, element) &&
				!ReferenceEquals(sibling, dimensions) &&
				tree.TypeOf(sibling) != KindBehaviour)
			{
				state.Contents.Add(sibling);
			}
		}
	}

	/// <summary>Element kinds <c>getElementType</c> returns, as the shipped strings.</summary>
	public const string KindText = "text";

	public const string KindModel = "model";

	public const string KindGroup = "group";

	/// <summary>
	/// A script behaviour, excluded from a frame's contents (the frame itself is one). The shipped
	/// build compares <c>'behaviour'</c> in <c>FrameGetAllChildren</c>
	/// (`framestateandconstants.dis main.1 pc23`) and <c>'behavior'</c> in
	/// <c>FrameRemoveInappropriateContents</c> (`frame.dis main.2 pc29`); only the British spelling
	/// ever matches, so the filter's arm is dead there and live here.
	/// </summary>
	public const string KindBehaviour = "behaviour";

	/// <summary>One displayed item; the caller fills the fields its <see cref="Kind"/> selects.</summary>
	/// <param name="Kind">What <c>getElementType</c> returned.</param>
	/// <param name="ScaleX">For <see cref="KindModel"/>: the element's <c>scale.x</c>.</param>
	/// <param name="ScaleY">For <see cref="KindModel"/>: the element's <c>scale.y</c>.</param>
	/// <param name="FrameWidth">
	/// For <see cref="KindGroup"/>: the nested frame's own width, or null when it has none.
	/// </param>
	/// <param name="FrameHeight">The same across.</param>
	/// <param name="BoundsWidth">For everything else: the bounding box's x extent.</param>
	/// <param name="BoundsHeight">The bounding box's y extent.</param>
	public readonly record struct Item(
		string Kind,
		float ScaleX = 0f, float ScaleY = 0f,
		float? FrameWidth = null, float? FrameHeight = null,
		float BoundsWidth = 0f, float BoundsHeight = 0f);

	/// <summary>A quad's size is its scale times this: the authored quad is 100 units square.</summary>
	public const float QuadBaseSize = 100f;

	/// <summary>
	/// Pass 1 — <c>FrameCalcLengthAndMaxBreadth</c>. Model = scale * 100; group = FrameWidth/
	/// FrameHeight or 100; else <c>calculateBoundingBox</c> extent. The text test is a separate
	/// <c>if</c> (instruction 26, not an arm of the chain at 30+), so a text item sets
	/// <c>ContainsText</c> and still measures by bounding box. <c>TEXT_VERTICAL_OVERLAP</c> comes
	/// off once for the whole frame after the loop (instructions 151-177), from whichever
	/// accumulator holds the vertical extent.
	/// </summary>
	public static void CalcLengthAndMaxBreadth(
		State state, System.Collections.Generic.IReadOnlyList<Item> contents)
	{
		state.StackLength = 0f;
		state.StackMaxBreadth = 0f;
		state.ItemLength.Clear();
		state.ItemBreadth.Clear();
		state.ContainsText = false;

		foreach (Item item in contents)
		{
			if (item.Kind == KindText)
			{
				state.ContainsText = true;
			}

			float x, y;
			if (item.Kind == KindModel)
			{
				x = item.ScaleX * QuadBaseSize;
				y = item.ScaleY * QuadBaseSize;
			}
			else if (item.Kind == KindGroup)
			{
				// `e.FrameWidth or 100` — an unsized nested frame measures as a bare quad.
				x = item.FrameWidth ?? QuadBaseSize;
				y = item.FrameHeight ?? QuadBaseSize;
			}
			else
			{
				x = item.BoundsWidth;
				y = item.BoundsHeight;
			}

			// Vertical stacks down, so an item's LENGTH is its height; horizontal swaps them.
			float length = state.Type == TypeVertical ? y : x;
			float breadth = state.Type == TypeVertical ? x : y;

			state.ItemLength.Add(length);
			state.ItemBreadth.Add(breadth);

			state.StackLength += length;
			if (state.StackMaxBreadth < breadth)
			{
				state.StackMaxBreadth = breadth;
			}
		}

		if (!state.ContainsText)
		{
			return;
		}

		if (state.Type == TypeVertical)
		{
			state.StackLength -= TextVerticalOverlap;
		}
		else
		{
			state.StackMaxBreadth -= TextVerticalOverlap;
		}
	}

	/// <summary>The nine anchor points, 0..8, left-to-right then top-to-bottom; the original uses bare numbers.</summary>
	public const int OriginUpperLeft = 0;

	public const int OriginUpperCentre = 1;

	public const int OriginUpperRight = 2;

	public const int OriginMiddleLeft = 3;

	public const int OriginCentre = 4;

	public const int OriginMiddleRight = 5;

	public const int OriginLowerLeft = 6;

	public const int OriginLowerCentre = 7;

	public const int OriginLowerRight = 8;

	/// <summary>
	/// Pass 6 — <c>FrameCalcOriginPosition</c>: the frame centre's offset from its anchor, an
	/// if/elseif chain over <c>Origin</c> 0..8 returning ±W/2, ±H/2:
	///
	/// <code>
	///        LEFT        CENTRE      RIGHT
	/// UPPER  (+W/2,-H/2) (0,-H/2)    (-W/2,-H/2)     origins 0 1 2
	/// MIDDLE (+W/2, 0)   (0, 0)      (-W/2, 0)       origins 3 4 5
	/// LOWER  (+W/2,+H/2) (0,+H/2)    (-W/2,+H/2)     origins 6 7 8
	/// </code>
	///
	/// Origin 0 returns (+W/2, -H/2): the anchor is the left edge and, in y-up space, the top.
	/// The 0..8 order is pinned by the constant table (2 is interned as the divisor before the
	/// <c>Origin == 2</c> arm). <c>Origin</c> is read off the frame in the original; it lives
	/// on <see cref="State"/> here.
	/// </summary>
	public static (float X, float Y) CalcOriginPosition(State state)
	{
		float halfWidth = state.Width / 2f;
		float halfHeight = state.Height / 2f;

		float x = state.Origin switch
		{
			OriginUpperLeft or OriginMiddleLeft or OriginLowerLeft => halfWidth,
			OriginUpperRight or OriginMiddleRight or OriginLowerRight => -halfWidth,
			_ => 0f,
		};

		float y = state.Origin switch
		{
			OriginUpperLeft or OriginUpperCentre or OriginUpperRight => -halfHeight,
			OriginLowerLeft or OriginLowerCentre or OriginLowerRight => halfHeight,
			_ => 0f,
		};

		// Unmatched origin: no else arm, both registers keep the 0.0 seeded at instructions 0 and 1.
		return (x, y);
	}

	/// <summary>The nine named border elements a frame drives, by the names the Lua uses.</summary>
	public enum Slot
	{
		UpperLeftCorner,
		UpperRightCorner,
		LowerLeftCorner,
		LowerRightCorner,
		Middle,
		UpperBeam,
		LowerBeam,
		LeftBeam,
		RightBeam,
	}

	/// <summary>Where one border element goes and how far it is stretched.</summary>
	public readonly record struct SlotPlacement(float X, float Y, float ScaleX, float ScaleY);

	/// <summary>One edge block: a whole 32-unit tile, never scaled.</summary>
	public readonly record struct BlockPlacement(int Index, float X, float Y, float Opacity);

	/// <summary>Everything <see cref="PositionAndScaleBorderElements"/> decides.</summary>
	public sealed class BorderLayout
	{
		public readonly System.Collections.Generic.Dictionary<Slot, SlotPlacement> Slots = new();

		public readonly System.Collections.Generic.List<BlockPlacement> UpperBlocks = new();

		public readonly System.Collections.Generic.List<BlockPlacement> LowerBlocks = new();

		public readonly System.Collections.Generic.List<BlockPlacement> LeftBlocks = new();

		public readonly System.Collections.Generic.List<BlockPlacement> RightBlocks = new();

		/// <summary>False when the pass declined to run, and nothing should be written.</summary>
		public bool Ran;
	}

	/// <summary>
	/// Pass 7 — <c>FramePositionAndScaleBorderElements</c>, the nine slices. Bails when
	/// <c>BorderSize == 0</c> or the slices are not cloned yet. Block edges are ±W/2, ±H/2; beam
	/// edges are inset by <c>BorderOverlap</c>. As shipped, the stepped axis of each block loop
	/// omits the origin offset — x = <c>-W/2 + (i-1) * BLOCK_SIZE</c>, y = <c>-H/2 + i * BLOCK_SIZE</c>
	/// (instruction 138 subtracts 1, 233 does not) — invisible on centre-origin frames; reproduced.
	/// Blocks past the size are not moved, only set to opacity 0 (visible ones to 100); the pool is
	/// fixed at <see cref="MaxHorizontalBlocks"/> by <see cref="MaxVerticalBlocks"/>.
	/// </summary>
	public static BorderLayout PositionAndScaleBorderElements(State state)
	{
		var layout = new BorderLayout();
		if (state.BorderSize == 0 || !state.HaveClonedBorderElements)
		{
			return layout;
		}

		layout.Ran = true;
		(float originX, float originY) = CalcOriginPosition(state);

		float left, right, top, bottom;
		if (state.UsesBlocks)
		{
			left = -state.Width / 2f;
			right = state.Width / 2f;
			top = state.Height / 2f;
			bottom = -state.Height / 2f;
		}
		else
		{
			left = -state.Width / 2f + state.BorderOverlap;
			right = state.Width / 2f - state.BorderOverlap;
			top = state.Height / 2f - state.BorderOverlap;
			bottom = -state.Height / 2f + state.BorderOverlap;
		}

		// Beam scales are derived for both families but only applied in the beam arm.
		float cornerScale = state.BorderBlockSize / QuadBaseSize;
		float spanX = (state.Width - state.BorderOverlap * 2f) / QuadBaseSize;
		float spanY = (state.Height - state.BorderOverlap * 2f) / QuadBaseSize;

		layout.Slots[Slot.UpperLeftCorner] =
			new SlotPlacement(left + originX, top + originY, cornerScale, cornerScale);
		layout.Slots[Slot.UpperRightCorner] =
			new SlotPlacement(right + originX, top + originY, cornerScale, cornerScale);
		layout.Slots[Slot.LowerLeftCorner] =
			new SlotPlacement(left + originX, bottom + originY, cornerScale, cornerScale);
		layout.Slots[Slot.LowerRightCorner] =
			new SlotPlacement(right + originX, bottom + originY, cornerScale, cornerScale);

		if (state.UsesBlocks)
		{
			// Middle covers the inner box plus 1.5 blocks per axis (instruction 406: BLOCK_SIZE * 1.5),
			// sliding under the surrounding tiles.
			layout.Slots[Slot.Middle] = new SlotPlacement(originX, originY,
				(state.InnerWidth + BlockSize * 1.5f) / QuadBaseSize,
				(state.InnerHeight + BlockSize * 1.5f) / QuadBaseSize);

			float wanted = state.InnerWidth / BlockSize;
			for (int i = 1; i <= MaxHorizontalBlocks; ++i)
			{
				bool shown = i <= wanted;
				float x = -state.Width / 2f + (i - 1) * BlockSize;

				layout.UpperBlocks.Add(new BlockPlacement(i, x, top + originY, shown ? 100f : 0f));
				layout.LowerBlocks.Add(new BlockPlacement(i, x, bottom + originY, shown ? 100f : 0f));
			}

			float tall = state.InnerHeight / BlockSize;
			for (int i = 1; i <= MaxVerticalBlocks; ++i)
			{
				bool shown = i <= tall;
				float y = -state.Height / 2f + i * BlockSize;

				layout.LeftBlocks.Add(new BlockPlacement(i, left + originX, y, shown ? 100f : 0f));
				layout.RightBlocks.Add(new BlockPlacement(i, right + originX, y, shown ? 100f : 0f));
			}

			return layout;
		}

		layout.Slots[Slot.UpperBeam] =
			new SlotPlacement(originX, top + originY, spanX, cornerScale);
		layout.Slots[Slot.LowerBeam] =
			new SlotPlacement(originX, bottom + originY, spanX, cornerScale);
		layout.Slots[Slot.LeftBeam] =
			new SlotPlacement(left + originX, originY, cornerScale, spanY);
		layout.Slots[Slot.RightBeam] =
			new SlotPlacement(right + originX, originY, cornerScale, spanY);
		layout.Slots[Slot.Middle] =
			new SlotPlacement(originX, originY, spanX, spanY);

		return layout;
	}

	/// <summary>Where one item ends up, relative to the frame's own origin.</summary>
	public readonly record struct Placement(float X, float Y);

	/// <summary>
	/// Pass 8 — <c>FramePlaceContents</c>: distributes the measured items along the inner box.
	/// <paramref name="alignments"/> is each item's <c>FrameAlignment</c>, or null when absent
	/// (<c>hasAttribute</c> before <c>getAttribute</c>). gap = spare length / (#contents + 1),
	/// floored at 0 — a margin at each end, so the pen starts at <c>-StackLength/2 + gap</c>.
	/// Vertical writes <c>y = oy - pen</c> (down in y-up space); horizontal writes <c>x = ox + pen</c>.
	/// A lone item is written (0,0) flat (instruction 56), skipping gap, pen and text adjust.
	/// The vertical-alignment assert is dead: instruction 126 loads constant 1 = <c>0.0</c>, which
	/// is truthy in Lua, so UPPER/LOWER inside a vertical frame keep the 0.0 seeded at instruction
	/// 93 and centre across; reproduced.
	/// </summary>
	public static System.Collections.Generic.IReadOnlyList<Placement> PlaceContents(
		State state, System.Collections.Generic.IReadOnlyList<int?> alignments)
	{
		int count = alignments.Count;
		var placed = new System.Collections.Generic.List<Placement>(count);
		if (count == 0)
		{
			return placed;
		}

		(float originX, float originY) = CalcOriginPosition(state);

		bool vertical = state.Type == TypeVertical;
		float lengthSize = vertical ? state.InnerHeight : state.InnerWidth;
		float breadthSize = vertical ? state.InnerWidth : state.InnerHeight;

		float gap = (lengthSize - state.StackLength) / (count + 1);
		if (gap < 0f)
		{
			gap = 0f;
		}

		// Clamped in place; the placement below reads the list back.
		if (breadthSize < state.StackMaxBreadth)
		{
			for (int i = 0; i < state.ItemBreadth.Count; ++i)
			{
				if (breadthSize < state.ItemBreadth[i])
				{
					state.ItemBreadth[i] = breadthSize;
				}
			}
		}

		if (count == 1)
		{
			placed.Add(new Placement(0f, 0f));
			return placed;
		}

		float pen = -state.StackLength / 2f + gap;
		float textAdjust = state.ContainsText ? TextVerticalOverlap / 1.5f : 0f;

		for (int i = 0; i < count; ++i)
		{
			float half = At(state.ItemLength, i) / 2f;
			pen += half;

			if (vertical)
			{
				// Across the stack: centred unless the item asks for an edge.
				float across = 0f;
				if (alignments[i] is { } alignment)
				{
					float slack = (breadthSize - At(state.ItemBreadth, i)) / 2f;
					across = alignment switch
					{
						AlignmentLeft => -slack,
						AlignmentRight => slack,
						// UPPER and LOWER reach the dead assert and keep the seeded zero.
						_ => 0f,
					};
				}

				placed.Add(new Placement(originX + across, originY - pen + textAdjust));
			}
			else
			{
				// No alignment chain for a horizontal frame: instruction 141 seeds the cross-axis offset to zero.
				placed.Add(new Placement(originX + pen, originY + textAdjust));
			}

			pen += half + gap;
		}

		return placed;
	}

	/// <summary>Reads a per-item extent; a list shorter than the content count yields 0 rather than throwing (the original indexes unguarded).</summary>
	private static float At(System.Collections.Generic.IReadOnlyList<float> values, int index) =>
		index >= 0 && index < values.Count ? values[index] : 0f;
}
