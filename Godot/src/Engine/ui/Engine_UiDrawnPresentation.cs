using Godot;

namespace Refii.Ui;

/// <summary>
/// One presentation Godot stands for the screen the guest states is up. Every presentation is
/// headless (gui_headless_presentation.cpp: no .bgf scene, no element tree to sample), so the
/// node is an empty stand-in that carries the presentation's DEPTH; its book stands on its file
/// and the guest's statements.
///
/// In the engine rather than beside the game's follower because the owned surfaces are handed
/// one, and a surface reaching into a game-private type cannot be driven from the editor.
/// </summary>
public sealed class Engine_UiDrawnPresentation
{
	public required string Name { get; init; }
	public required Node3D Node { get; init; }

	/// <summary>
	/// The parsed document of the presentation's file, for a per-frame reader that needs the
	/// authored STRUCTURE — names, ordinals, children. The owned in-game list walks it for the
	/// `ExpandableMenu/Buttons` pool.
	/// </summary>
	public Engine_UiScreen? Screen { get; set; }
}
