using Godot;

namespace Refii.Ui;

/// <summary>
/// One presentation Godot is currently mirroring, and the state it was last built from.
///
/// The rebuild test lives in these fields: a mirrored tree is rebuilt when the guest changes
/// something the tree's STRUCTURE depends on. The transform must NOT trigger a rebuild — the
/// guest moves those every frame, so they are written straight onto the built nodes.
///
/// In the engine rather than beside the game's follower because the owned surfaces are handed
/// one, and a surface reaching into a game-private type cannot be driven from the editor.
/// </summary>
public sealed class Engine_UiDrawnPresentation
{
	public required string Name { get; init; }
	public required Node3D Node { get; init; }

	/// <summary>
	/// Whether <see cref="Node"/> is the guest's element tree rebuilt in Godot, or an empty
	/// stand-in that only carries this presentation's DEPTH. False is the goal: a screen Godot
	/// owns is drawn from its own pattern book, and the element SAMPLE that feeds it is a bridge
	/// read, never these nodes. Art catalogue, preview anchor and depth all come from the file
	/// (see <see cref="Engine_UiPresentationDepth"/>).
	/// </summary>
	public bool Mirrored { get; init; } = true;
	public int[]? Slides { get; set; }
	public bool[]? Visible { get; set; }

	/// <summary>
	/// Compared too, and it has to be: the HUD's own hide is an opacity write with no slide
	/// or visibility change anywhere, so a screen that only fades would never be rebuilt.
	/// </summary>
	public float?[]? Opacity { get; set; }

	/// <summary>
	/// The tint WEIGHT per element the screen was last built from. In the rebuild test because a
	/// highlighting wheel item raises its `TintingAmount` and changes nothing else.
	/// </summary>
	public Engine_UiScreen.LiveTint?[]? Tint { get; set; }

	/// <summary>
	/// The live texture hash per element the screen was last built from. In the rebuild test
	/// because a texture swap changes nothing else — equipping an item repaints a dpad slot
	/// without touching its slide.
	/// </summary>
	public uint[]? Textures { get; set; }

	/// <summary>
	/// The builder that produced <see cref="Node"/>, kept for its ordinal map — transforms are
	/// written straight onto the built nodes, see
	/// <see cref="Engine_UiSceneBuilder.ApplyLiveTransform"/>.
	/// </summary>
	public Engine_UiSceneBuilder? Builder { get; set; }

	/// <summary>The last live transform AnimateDrawn read, for owned views that follow
	/// individual stated positions without re-reading the whole attribute table.</summary>
	public float?[]?[]? LastTransform { get; set; }

	/// <summary>How many elements this presentation carries, for the per-frame read.</summary>
	public int ElementCount { get; set; }

	/// <summary>
	/// The live-value bindings this presentation carries, and the view that applies them.
	/// Non-null only for a screen that has bindings — in practice the HUD. Rebuilt with the node
	/// tree, since the view holds nodes belonging to it.
	/// </summary>
	public Engine_UiLiveValues? Values { get; set; }

	/// <summary>
	/// The screen's WIDE layout, applied after the guest's transforms every frame. Per screen
	/// file data, re-read with the tree because it is keyed by element ordinal.
	/// </summary>
	public Engine_UiWidescreen? Widescreen { get; set; }

	/// <summary>
	/// The parsed document this tree was built from, for a per-frame reader that needs the
	/// authored STRUCTURE — names, ordinals, children — rather than the built nodes. The owned
	/// in-game list walks it for the guest's `ExpandableMenu/Buttons` pool every frame, since
	/// these screens repopulate with no event to say so.
	/// </summary>
	public Engine_UiScreen? Screen { get; set; }

	/// <summary>
	/// The guest's live text per element, as last sampled. The owned info panel reads its fields
	/// by NAME out of this — the guest states the finished strings, not the GDB chain.
	/// </summary>
	public string?[]? Texts { get; set; }
}
