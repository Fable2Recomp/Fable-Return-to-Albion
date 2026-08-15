namespace Refii.Ui;

/// <summary>
/// Fable 2's frame layout engine, in C#.
///
/// THIS IS THE SHARED FOUNDATION OF EVERY IN-GAME SCREEN, which is why it is being ported
/// first. The world map, the logbook, the shop, the quest pages and every display box are all
/// the same widget: a nine-slice frame built out of 32-unit blocks, sized to its contents, with
/// the contents then placed inside it. When the map screen draws its panels overlapping and its
/// labels mirrored, that is not a map-screen fault — it is this engine not running.
///
/// The original is <c>scripts/gameface/frame/</c>, four chunks totalling ~1700 instructions,
/// disassembled with <c>tools/lua51dis.py</c>. The pipeline is stated by
/// <c>framesizeandpositioning.lua</c>'s own top-level chunk, which registers exactly these
/// eight functions in this order:
///
/// <list type="number">
/// <item><c>FrameCalcLengthAndMaxBreadth</c> — measure the contents: per item length and
/// breadth, accumulated into <c>StackLength</c> and <c>StackMaxBreadth</c>, noting whether any
/// item <c>ContainsText</c>.</item>
/// <item><c>FrameCalcDimensions</c> — turn that into <c>InnerWidth</c>/<c>InnerHeight</c>,
/// swapping the two axes on <c>FRAME_TYPE_HORIZONTAL</c>.</item>
/// <item><c>FrameClampSize</c> — round up to whole blocks and clamp to
/// <c>MAX_WIDTH</c>/<c>MAX_HEIGHT</c>.</item>
/// <item><c>FrameCalcBorders</c> — the block counts for each edge.</item>
/// <item><c>FrameCalcOuterSize</c> — add the border to get <c>Width</c>/<c>Height</c>.</item>
/// <item><c>FrameCalcOriginPosition</c> — place the frame by its alignment.</item>
/// <item><c>FramePositionAndScaleBorderElements</c> — the nine slices: corners, edges,
/// middle.</item>
/// <item><c>FramePlaceContents</c> — lay the items out inside.</item>
/// </list>
///
/// A FRAME'S SIZE IS READ OFF A HIDDEN ELEMENT, not from its own transform. Every frame owns a
/// child called <c>DimensionsImage</c> whose <c>scale.x</c>/<c>scale.y</c> carry the authored
/// size — the chunk asserts on it by name, with the shipped message "Frame didn't contain a
/// valid reference to its DimensionsImage" — and then sets that element's <c>opacity</c> to 0
/// so it never draws. A host that ignores it has no size to lay out from.
/// </summary>
public static class Engine_UiFrame
{
	/// <summary>Stack down the screen; the default. <c>FRAME_TYPE_VERTICAL = 0</c>.</summary>
	public const int TypeVertical = 0;

	/// <summary>Stack across. <c>FRAME_TYPE_HORIZONTAL = 1</c>, and it SWAPS length and breadth.</summary>
	public const int TypeHorizontal = 1;

	public const int AlignmentLeft = 0;
	public const int AlignmentRight = 1;
	public const int AlignmentUpper = 2;
	public const int AlignmentLower = 3;

	/// <summary>
	/// The grid every frame is built on. <c>BLOCK_SIZE = 32</c>.
	///
	/// Borders are whole numbers of these, which is why a frame's size is always rounded up:
	/// <c>FrameClampSize</c> takes <c>math.ceil</c> of the content size in blocks.
	/// </summary>
	public const float BlockSize = 32f;

	/// <summary>Largest inner width, in units. <c>MAX_WIDTH = 960</c> — the 4:3 canvas width.</summary>
	public const float MaxWidth = 960f;

	/// <summary>Largest inner height. <c>MAX_HEIGHT = 800</c>.</summary>
	public const float MaxHeight = 800f;

	/// <summary>
	/// A corner slice is TWO blocks, not one. Computed rather than written:
	/// <c>MUL</c> of <c>BLOCK_SIZE</c> by 2.0 at instruction 15 of the constants chunk.
	/// </summary>
	public const float CornerSize = BlockSize * 2f;

	/// <summary>How much consecutive lines of text overlap. <c>TEXT_VERTICAL_OVERLAP = 2</c>.</summary>
	public const float TextVerticalOverlap = 2f;

	/// <summary>
	/// <c>MAX_HORIZ_BLOCKS = ceil(MAX_WIDTH / BLOCK_SIZE)</c> = 30, and
	/// <c>MAX_VERT_BLOCKS = ceil(MAX_HEIGHT / BLOCK_SIZE)</c> = 25. Both are computed in the
	/// chunk (a DIV followed by <c>math.ceil</c>), not authored, so they are derived here the
	/// same way rather than pasted as literals.
	/// </summary>
	public static readonly int MaxHorizontalBlocks =
		(int)System.Math.Ceiling(MaxWidth / BlockSize);

	public static readonly int MaxVerticalBlocks =
		(int)System.Math.Ceiling(MaxHeight / BlockSize);

	/// <summary>
	/// The border metrics for each <c>BorderSize</c>, from <c>FrameCalcBorders</c>.
	///
	/// THESE OVERRIDE THE DEFAULTS IN THE CONSTANTS CHUNK. `framestateandconstants.lua` seeds
	/// the state with 24 / 16 / 8, but `FrameCalcBorders` recomputes all three from the frame's
	/// own <c>BorderSize</c> every pass — so for the common size 2 the live values are
	/// 24 / 12 / 12, not 24 / 16 / 8. Taking the seeded numbers as the answer would put every
	/// border four units out.
	///
	/// The overlap is exactly half the block in every row, and the offset is the remainder —
	/// stated as a table rather than as a formula because the shipped code is a table, and a
	/// formula would be a claim about sizes the game does not use.
	/// </summary>
	private static readonly (float Block, float Overlap)[] BorderMetrics =
	{
		(0f, 0f),    // 0 — no border
		(18f, 9f),
		(24f, 12f),
		(36f, 18f),
		(64f, 32f),
	};

	/// <summary>
	/// Frames the engine keeps re-laying out after it looks settled.
	///
	/// The original does not lay out once and stop — it counts down five frames, because the
	/// contents it measures (text, models) can resolve a frame or two late and change the size.
	/// </summary>
	public const int FramesUntilStopUpdating = 5;

	/// <summary>The child whose scale carries the authored size, and which is then hidden.</summary>
	public const string DimensionsElement = "DimensionsImage";

	/// <summary>
	/// One frame's working state — the fields the shipped <c>State</c> table carries.
	///
	/// Kept as a mutable class rather than a record because the eight passes above each write
	/// part of it in sequence, exactly as the original does.
	/// </summary>
	public sealed class State
	{
		public int Type = TypeVertical;

		/// <summary>Outer size, after <c>FrameCalcOuterSize</c> adds the border.</summary>
		public float Width;
		public float Height;

		/// <summary>
		/// Which of the nine anchor points the frame is pinned by — see
		/// <see cref="CalcOriginPosition"/>. Defaults to the centre, which is also what the
		/// original's chain leaves behind when it matches no arm.
		/// </summary>
		public int Origin = OriginCentre;

		/// <summary>Inner size, before the border — what the contents get.</summary>
		public float InnerWidth;
		public float InnerHeight;

		/// <summary>Accumulated content extent along the stacking axis.</summary>
		public float StackLength;

		/// <summary>Largest content extent across it.</summary>
		public float StackMaxBreadth;

		/// <summary>
		/// Per-item extents, filled by <see cref="CalcLengthAndMaxBreadth"/> and read back by
		/// <c>FramePlaceContents</c> — one entry per displayed item, in display order.
		///
		/// LISTS, NOT SCALARS. The original builds them with <c>State.ItemLength = {}</c> and
		/// <c>table.insert</c>, and the placement pass walks them by index; a single "the item
		/// currently being measured" value cannot be walked later and would place every item at
		/// the last one's size.
		/// </summary>
		public readonly System.Collections.Generic.List<float> ItemLength = new();

		public readonly System.Collections.Generic.List<float> ItemBreadth = new();

		/// <summary>
		/// Everything sharing the frame's parent that the frame is responsible for, filled by
		/// <see cref="Initialise"/>. <c>DisplayedContents</c> in the original is this list after
		/// the page has filtered it; a frame with no page shows all of it.
		/// </summary>
		public readonly System.Collections.Generic.List<object> Contents = new();

		/// <summary>
		/// The authored measuring rectangle, or null when the frame sizes itself. Never drawn —
		/// <see cref="Initialise"/>'s caller must set its opacity to zero.
		/// </summary>
		public object? Dimensions;

		/// <summary>Border block counts per edge, from <c>FrameCalcBorders</c>.</summary>
		public int UpperBlocks;
		public int LowerBlocks;
		public int LeftBlocks;
		public int RightBlocks;

		/// <summary>Which row of <see cref="BorderMetrics"/> this frame uses, 0..4.</summary>
		public int BorderSize;

		/// <summary>Live border metrics, written by <see cref="CalcBorders"/>.</summary>
		public float BorderBlockSize;
		public float BorderOverlap;
		public float BorderOffset;

		/// <summary>Whether the frame sizes itself to its contents, or keeps its authored size.</summary>
		public bool SizeToContent = true;

		/// <summary>Whether the frame draws a border at all — <c>FrameCalcOuterSize</c>'s gate.</summary>
		public bool HasBorder = true;

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
	/// Pass 2 — <c>FrameCalcDimensions</c>. Turns the measured stack into an inner rectangle.
	///
	/// Verbatim from the bytecode:
	///
	/// <code>
	/// if self.SizeToContent then
	///     if self.Const.FRAME_TYPE_VERTICAL == getAttribute(self.element, 'Type') then
	///         State.InnerWidth  = State.StackMaxBreadth
	///         State.InnerHeight = State.StackLength
	///     else
	///         State.InnerWidth  = State.StackLength
	///         State.InnerHeight = State.StackMaxBreadth
	///     end
	///     if FrameUsesBlocks(self) then
	///         State.InnerWidth  = State.InnerWidth  - BLOCK_SIZE
	///         State.InnerHeight = State.InnerHeight - BLOCK_SIZE
	///     end
	/// end
	/// </code>
	///
	/// TWO THINGS THAT LOOK LIKE MISTAKES AND ARE NOT. The axes really do swap wholesale on a
	/// horizontal frame — length becomes width — and a block-built frame really does lose a
	/// whole block off EACH axis here, which the border passes then add back. Dropping either
	/// leaves every block frame one block too big in both directions.
	///
	/// A frame that does not size to its contents keeps whatever it was given, so this is a
	/// no-op for it rather than a reset to zero.
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
	/// Pass 3 — <c>FrameClampSize</c>. Floors, ceilings, and rounds to whole blocks.
	///
	/// Two entirely separate branches, and they do NOT agree on their limits:
	///
	/// <code>
	/// if FrameUsesBlocks(self) then
	///     InnerWidth  = max(InnerWidth,  BLOCK_SIZE)
	///     InnerHeight = max(InnerHeight, BLOCK_SIZE)
	///     InnerWidth  = min(InnerWidth,  MAX_WIDTH  - CORNER_SIZE * 2)   -- 832
	///     InnerHeight = min(InnerHeight, MAX_HEIGHT - CORNER_SIZE * 2)   -- 672
	///     InnerWidth  = ceil(InnerWidth  / BLOCK_SIZE) * BLOCK_SIZE
	///     InnerHeight = ceil(InnerHeight / BLOCK_SIZE) * BLOCK_SIZE
	/// else
	///     InnerWidth  = max(InnerWidth,  BorderOverlap * 2)
	///     InnerHeight = max(InnerHeight, BorderOverlap * 2)
	///     InnerWidth  = min(InnerWidth,  MAX_WIDTH - BLOCK_SIZE * 2)     -- 896
	///     InnerHeight = min(InnerHeight, MAX_WIDTH - BLOCK_SIZE * 2)     -- 896, see below
	/// end
	/// </code>
	///
	/// THE HEIGHT CLAMP IN THE SECOND BRANCH USES <c>MAX_WIDTH</c>, NOT <c>MAX_HEIGHT</c>, and
	/// that is what the shipped bytecode does — instruction 98 loads constant 6, which is
	/// <c>MAX_WIDTH</c>, where the block branch at instruction 32 loads constant 9,
	/// <c>MAX_HEIGHT</c>. It is almost certainly a typo in the original, and it is reproduced
	/// here deliberately: a non-block frame is allowed to be 896 tall rather than 672, and
	/// "correcting" it would move real panels the game positions against that limit. Flagged
	/// rather than silently kept, so the choice is visible if a remastered layout ever wants it.
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

		// MAX_WIDTH on both axes — see the remark above. Not a transcription slip here.
		float ceiling = MaxWidth - BlockSize * 2f;
		state.InnerWidth = System.Math.Min(state.InnerWidth, ceiling);
		state.InnerHeight = System.Math.Min(state.InnerHeight, ceiling);
	}

	/// <summary>
	/// Pass 4 — <c>FrameCalcBorders</c>. Looks the border metrics up by <c>BorderSize</c>.
	///
	/// A five-way branch in the original, one arm per size, ending in the single derived value:
	///
	/// <code>
	/// State.BorderOffset = State.BorderBlockSize - State.BorderOverlap
	/// </code>
	///
	/// An out-of-range size falls to the zero row rather than throwing: the shipped code simply
	/// matches no arm and leaves the pair at whatever it held, and a frame with no border is a
	/// better failure than a crash mid-layout.
	/// </summary>
	public static void CalcBorders(State state)
	{
		(float block, float overlap) = state.BorderSize >= 0 && state.BorderSize < BorderMetrics.Length
			? BorderMetrics[state.BorderSize]
			: BorderMetrics[0];

		state.BorderBlockSize = block;
		state.BorderOverlap = overlap;
		state.BorderOffset = block - overlap;
	}

	/// <summary>
	/// Pass 5 — <c>FrameCalcOuterSize</c>. Adds the border to reach the drawn size.
	///
	/// <code>
	/// if self.HasBorder then
	///     State.Width  = State.InnerWidth  + State.BorderOffset
	///     State.Height = State.InnerHeight + State.BorderOffset
	/// else
	///     State.Width  = State.InnerWidth
	///     State.Height = State.InnerHeight
	/// end
	/// </code>
	///
	/// Note it adds the offset ONCE per axis, not once per edge — the border overlaps itself by
	/// design, which is what <see cref="State.BorderOverlap"/> is for.
	/// </summary>
	public static void CalcOuterSize(State state)
	{
		state.Width = state.InnerWidth + (state.HasBorder ? state.BorderOffset : 0f);
		state.Height = state.InnerHeight + (state.HasBorder ? state.BorderOffset : 0f);
	}

	/// <summary>
	/// What the engine needs to know about one authored element, and nothing more.
	///
	/// The pipeline is otherwise pure arithmetic, and this is deliberately the only seam. A frame
	/// asks four questions of the tree — what type is this, what are its siblings, what does its
	/// <c>scale.x</c> say, is it the dimensions quad — and every one of them is answerable from
	/// whatever element model the caller already has.
	/// </summary>
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
	/// <c>FrameInitialise</c> — seeds a frame's state from what the file authored.
	///
	/// It reads exactly four attributes off the frame's own element:
	/// <c>Type</c>, <c>SizeToContent</c>, <c>Origin</c> and <c>BorderSize</c>. Everything else in
	/// <see cref="State"/> is a constant default, which is why they are initialisers on the
	/// fields rather than assignments here.
	///
	/// THE DIMENSIONS QUAD IS A MEASUREMENT, NOT ART, and it is handled three different ways:
	///
	/// <code>
	/// SizeToContent          -> DimensionsImage = nil        (the contents decide the size)
	/// no valid reference     -> DimensionsImage = nil, Width = Height = 100
	/// otherwise              -> Width  = scale.x * 100
	///                           Height = scale.y * 100
	/// </code>
	///
	/// and then, whichever branch ran, <c>opacity = 0</c>. It is never drawn. A host that renders
	/// it gets a solid rectangle over every panel in the game.
	///
	/// THE CONTENTS ARE THE FRAME'S SIBLINGS, NOT ITS CHILDREN. <c>FrameGetAllChildren</c> takes
	/// <c>getChildren(getElement("parent", self.element))</c> and keeps everything that is not the
	/// frame itself, not the dimensions quad, and not of type <c>"behaviour"</c>. That is the one
	/// piece of this engine that cannot be guessed from the names: a frame is a behaviour attached
	/// alongside the things it lays out, not a container holding them, so walking its children
	/// finds nothing and the frame collapses to an empty box.
	/// </summary>
	public static void Initialise(State state, IFrameTree tree, object element)
	{
		state.Type = (int)(tree.Attribute(element, "Type") ?? TypeVertical);
		state.SizeToContent = (tree.Attribute(element, "SizeToContent") ?? 1f) != 0f;
		state.Origin = (int)(tree.Attribute(element, "Origin") ?? OriginCentre);
		state.BorderSize = (int)(tree.Attribute(element, "BorderSize") ?? 0f);

		object? dimensions = tree.NamedObject(element, DimensionsElement);

		// A frame that sizes itself has no use for the authored rectangle, and the original
		// discards the reference outright rather than leaving it to be read later by mistake.
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

	/// <summary>
	/// The element kinds <c>getElementType</c> returns that this engine branches on. String
	/// comparisons in the original, so they are strings here — the values are the shipped ones,
	/// read out of the chunk's constant table.
	/// </summary>
	public const string KindText = "text";

	public const string KindModel = "model";

	public const string KindGroup = "group";

	/// <summary>
	/// A script behaviour rather than a drawable. Excluded from a frame's contents — the frame
	/// itself is one of these, and so is every other behaviour sharing the parent.
	/// </summary>
	public const string KindBehaviour = "behaviour";

	/// <summary>
	/// One displayed item, measured the three ways <c>FrameCalcLengthAndMaxBreadth</c> measures
	/// them. The caller fills whichever fields its <see cref="Kind"/> selects; the pass picks.
	/// </summary>
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

	/// <summary>
	/// A quad element's size is its scale times this. The engine's authored quad is 100 units
	/// square, which is the same <c>QuadBaseSize</c> the rest of this project's UI already uses.
	/// </summary>
	public const float QuadBaseSize = 100f;

	/// <summary>
	/// Pass 1 — <c>FrameCalcLengthAndMaxBreadth</c>. Measures the contents.
	///
	/// The loop, written out:
	///
	/// <code>
	/// State.StackLength = 0; State.StackMaxBreadth = 0
	/// State.ItemLength = {}; State.ItemBreadth = {}
	/// State.ContainsText = false
	/// for i = 1, #State.DisplayedContents do
	///     local e = State.DisplayedContents[i]
	///     local x, y = 0, 0
	///     if getElementType(e) == "text" then State.ContainsText = true end
	///     if getElementType(e) == "model" then
	///         x = e.scale.x * 100 ; y = e.scale.y * 100
	///     elseif getElementType(e) == "group" then
	///         x = e.FrameWidth or 100 ; y = e.FrameHeight or 100
	///     else
	///         local lo, hi = Vector:new(), Vector:new()
	///         calculateBoundingBox(e, lo, hi)
	///         x = hi.x - lo.x ; y = hi.y - lo.y
	///     end
	///     if Const.FRAME_TYPE_VERTICAL == getAttribute(element, "Type") then
	///         table.insert(State.ItemLength, y) ; table.insert(State.ItemBreadth, x)
	///     else
	///         table.insert(State.ItemLength, x) ; table.insert(State.ItemBreadth, y)
	///     end
	///     State.StackLength = State.StackLength + State.ItemLength[i]
	///     if State.StackMaxBreadth &lt; State.ItemBreadth[i] then
	///         State.StackMaxBreadth = State.ItemBreadth[i]
	///     end
	/// end
	/// if State.ContainsText then
	///     if vertical then State.StackLength     = State.StackLength     - TEXT_VERTICAL_OVERLAP
	///     else             State.StackMaxBreadth = State.StackMaxBreadth - TEXT_VERTICAL_OVERLAP
	///     end
	/// end
	/// </code>
	///
	/// THREE THINGS HERE ARE NOT GUESSABLE AND ALL THREE CHANGE THE LAYOUT.
	///
	/// The text test is SEPARATE from the size chain — it is its own <c>if</c> at instruction
	/// 26, not an arm of the model/group/else chain at 30 onward — so a text element sets the
	/// flag AND then falls through to the bounding-box branch. Folding the two together would
	/// leave every text item measuring zero.
	///
	/// The overlap is subtracted ONCE FOR THE WHOLE FRAME, not once per text item: instructions
	/// 151-177 run after the loop has closed at 150, against the accumulated total.
	///
	/// And it comes off whichever accumulator holds the VERTICAL extent — <c>StackLength</c>
	/// when the frame stacks downward, <c>StackMaxBreadth</c> when it stacks across — which is
	/// why the constant is named for a vertical overlap and why using <c>StackLength</c> in both
	/// cases would shorten a horizontal row that has nothing wrong with its width.
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
				// `e.FrameWidth or 100` — a nested frame that has not been sized yet falls back
				// to the same 100 units a bare quad is, so the outer frame still has a number.
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

	/// <summary>
	/// The nine anchor points a frame can be pinned by, 0..8, reading left-to-right then
	/// top-to-bottom. Named here; the original works in bare numbers.
	/// </summary>
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
	/// Pass 6 — <c>FrameCalcOriginPosition</c>. Where the frame's CENTRE sits relative to the
	/// point it is pinned by.
	///
	/// An if/elseif chain over <c>Origin</c> 0..8, each arm returning a pair built only from
	/// <c>State.Width / 2</c> and <c>State.Height / 2</c> with a sign. Written out, the nine
	/// arms are a three-by-three grid:
	///
	/// <code>
	///        LEFT        CENTRE      RIGHT
	/// UPPER  (+W/2,-H/2) (0,-H/2)    (-W/2,-H/2)     origins 0 1 2
	/// MIDDLE (+W/2, 0)   (0, 0)      (-W/2, 0)       origins 3 4 5
	/// LOWER  (+W/2,+H/2) (0,+H/2)    (-W/2,+H/2)     origins 6 7 8
	/// </code>
	///
	/// THE SIGNS ARE THE PROOF OF WHICH WAY ROUND THE GRID IS, and they are worth reading
	/// rather than assuming. Origin 0 returns +W/2, so the centre is to the RIGHT of the anchor
	/// and the anchor is therefore the LEFT edge; it returns -H/2, so in this scene's y-up space
	/// the centre is BELOW the anchor and the anchor is the TOP. Origin 0 is the upper left.
	/// Reading the grid the other way round — and it is an easy one to read the other way round
	/// — mirrors every frame in the game about both axes.
	///
	/// Derived from the bytecode, not from a name: the constant table interns 0, then "Origin",
	/// "State", "Width", 2, "Height", and then the seven remaining comparison values in source
	/// order. The value 2 is already interned as the divisor by the time the <c>Origin == 2</c>
	/// arm is compiled, which is what pins the chain to ascending order 0,1,2,…,8 rather than
	/// to any other permutation of the same nine numbers.
	///
	/// <c>Origin</c> is read off the FRAME in the original, not off its <c>State</c>; it lives
	/// on <see cref="State"/> here because this class carries one object per frame rather than
	/// the original's two.
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

		// AN UNKNOWN ORIGIN FALLS TO THE CENTRE, which is what the original does by omission:
		// the chain has no else, so every register it would have written keeps the 0.0 both were
		// seeded with at instructions 0 and 1.
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

	/// <summary>
	/// One block along an edge. Blocks carry NO scale — they are whole 32-unit tiles, and the
	/// frame is sized in whole blocks precisely so they never have to be stretched.
	/// </summary>
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
	/// Pass 7 — <c>FramePositionAndScaleBorderElements</c>. The nine slices.
	///
	/// It bails immediately in two cases, and both matter: <c>BorderSize == 0</c> means the frame
	/// has no border to place, and <c>not State.HaveClonedBorderElements</c> means the slices do
	/// not exist yet. Running anyway would write positions onto elements the frame does not own.
	///
	/// The four edge coordinates it works from differ between the two construction styles:
	///
	/// <code>
	/// blocks:  left = -W/2               right = +W/2
	///          top  = +H/2               bottom = -H/2
	/// beams:   left = -W/2 + Overlap     right = +W/2 - Overlap
	///          top  = +H/2 - Overlap     bottom = -H/2 + Overlap
	/// </code>
	///
	/// A BLOCK FRAME'S EDGES ARE NOT INSET BY THE OVERLAP and a beam frame's are. That is the
	/// whole difference between tiles that butt up against each other and beams that overlap
	/// their corners, and swapping it puts a seam or a double-thickness edge on every panel.
	///
	/// THE STEPPED AXIS DROPS THE ORIGIN, and it does so consistently in both block loops. A
	/// horizontal block's x is <c>-W/2 + (i-1) * BLOCK_SIZE</c> with NO <c>+ originX</c>, while
	/// its y is <c>top + originY</c>; a vertical block's y is <c>-H/2 + i * BLOCK_SIZE</c> with no
	/// <c>+ originY</c>, while its x carries <c>+ originX</c>. It reads like an omission and it is
	/// the same omission twice, on whichever axis is being walked. It is invisible on a
	/// centre-origin frame — where both offsets are zero — which is most of them, and that is
	/// presumably why it survived. Reproduced.
	///
	/// AND THE TWO LOOPS DO NOT STEP ALIKE: the horizontal one multiplies by <c>i - 1</c> and the
	/// vertical one by <c>i</c>, so the vertical edge starts one block further along. Instruction
	/// 138 subtracts 1 where instruction 233 does not.
	///
	/// A block past the frame's width or height is not moved at all — it is only faded to zero
	/// opacity, and the visible ones are explicitly faded back to 100. The pool is fixed at
	/// <see cref="MaxHorizontalBlocks"/> by <see cref="MaxVerticalBlocks"/> and never resized,
	/// so opacity is the only thing that makes a frame look the size it is.
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

		// The corner scale is uniform and shared; the beam scales are computed below and unused
		// by a block frame, which is why they are derived either way but only applied in one arm.
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
			// The Middle tile covers the inner box plus one and a HALF blocks on each axis —
			// instruction 406 multiplies BLOCK_SIZE by 1.5, not by 1 or 2. The extra half is what
			// slides under the surrounding tiles so no seam shows through.
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
	/// Pass 8 — <c>FramePlaceContents</c>. Distributes the measured items down or across the
	/// frame's inner box.
	///
	/// <paramref name="alignments"/> carries each item's <c>FrameAlignment</c> custom property,
	/// or null where it has none — the original asks <c>hasAttribute</c> before
	/// <c>getAttribute</c>, and "absent" behaves differently from any present value.
	///
	/// The shape of it:
	///
	/// <code>
	/// local ox, oy = FrameCalcOriginPosition(self)
	/// local lengthSize, breadthSize = (vertical and InnerHeight or InnerWidth),
	///                                 (vertical and InnerWidth  or InnerHeight)
	/// local gap = (lengthSize - StackLength) / (#contents + 1)
	/// if gap &lt; 0 then gap = 0 end
	/// if breadthSize &lt; StackMaxBreadth then
	///     for i do if breadthSize &lt; ItemBreadth[i] then ItemBreadth[i] = breadthSize end end
	/// end
	/// if #contents &gt; 1 then
	///     local pen = -StackLength / 2 + gap
	///     local textAdjust = ContainsText and TEXT_VERTICAL_OVERLAP / 1.5 or 0
	///     for i do
	///         pen = pen + ItemLength[i] / 2
	///         ... place ...
	///         pen = pen + ItemLength[i] / 2 + gap
	///     end
	/// else
	///     contents[1].position = (0, 0)
	/// end
	/// </code>
	///
	/// THE SPARE SPACE IS SHARED BY <c>#contents + 1</c>, NOT <c>#contents - 1</c>. That is the
	/// difference between a stack with a margin at each end and one flush against both edges,
	/// and it is why the pen starts at <c>-StackLength/2 + gap</c> rather than at
	/// <c>-lengthSize/2</c>: the first gap is spent before the first item.
	///
	/// THE PEN RUNS DOWN THE SCREEN BY SUBTRACTING. A vertical frame writes
	/// <c>position.y = oy - pen</c>, so a growing pen moves DOWN in this scene's y-up space,
	/// while a horizontal frame writes <c>position.x = ox + pen</c> and moves right. Mirroring
	/// that sign flips a menu top to bottom while leaving it looking plausible.
	///
	/// A LONE ITEM IS PLACED AT THE ORIGIN AND NOTHING ELSE — the <c>#contents &gt; 1</c> test at
	/// instruction 56 sends a single item to a two-line branch that writes (0,0) flat, skipping
	/// the gap, the pen and the text adjustment entirely. It is not the loop with one iteration.
	///
	/// THE VERTICAL-ALIGNMENT GUARD IS DEAD IN THE SHIPPED BUILD, and the behaviour it was meant
	/// to prevent is what actually happens. Instruction 126 loads constant 1, which is
	/// <c>0.0</c>, so the call is <c>assert(0, "A Vertical frame contains items with specialised
	/// vertical alignment…")</c> — and 0 is TRUTHY in Lua, where only nil and false are not. The
	/// assertion can never fire. An UPPER or LOWER alignment on an item inside a vertical frame
	/// therefore falls through with its offset still at the 0.0 seeded at instruction 93, and
	/// the item is simply centred across. Reproduced, because a frame that quietly centres is
	/// what every screen in the game was authored against.
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

		// Overwide items are cut down to the frame, in place — the placement below reads the
		// clamped value back out of the list, so this is not a local adjustment.
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
				// A horizontal frame has no alignment chain at all — the branch at instruction
				// 141 seeds its cross-axis offset to zero and never writes it again.
				placed.Add(new Placement(originX + pen, originY + textAdjust));
			}

			pen += half + gap;
		}

		return placed;
	}

	/// <summary>
	/// Reads a per-item extent, tolerating a list shorter than the content count.
	///
	/// The original indexes <c>ItemLength[i]</c> unguarded and would error; here a caller that
	/// places before measuring gets zeros instead of an exception, because a frame drawn flat is
	/// diagnosable from a screenshot and a crash mid-layout takes the whole screen with it.
	/// </summary>
	private static float At(System.Collections.Generic.IReadOnlyList<float> values, int index) =>
		index >= 0 && index < values.Count ? values[index] : 0f;
}
