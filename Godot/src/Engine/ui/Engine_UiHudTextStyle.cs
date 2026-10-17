using Godot;

namespace Refii.Ui;

/// <summary>
/// The HUD TopBox's text metrics, captured from the built HUD so every other in-game box can
/// wear the same look.
/// </summary>
/// <remarks>
/// The shipped game draws its notices through several presentations — `hud.bgf`'s TopBox for
/// quests and offers, `displaybox.bgf` for `DisplayInfoBox`/`DisplayMessageBox`, the counters'
/// CornerBox — each with its own slab art, point size and colours, so "Accept the bard's offer"
/// and "This job is currently not available" sit at the same place on screen in two different
/// styles. The remaster gives them one: the TopBox's title and body sizes and colours, on the
/// rounded panel. Captured rather than authored twice, so the HUD's own `.bgf` stays the single
/// statement of what that look is.
/// </remarks>
public static class Engine_UiHudTextStyle
{
	private static readonly StringName TextColourName = "text_colour";
	private static readonly StringName TextSizeName = "text_size";

	public static bool Captured { get; private set; }

	public static int TitleSize { get; private set; } = 22;

	public static int BodySize { get; private set; } = 18;

	/// <summary>The TopBox title's colour — the HUD's warm yellow when the mesh states none.</summary>
	public static Color TitleColour { get; private set; } = new(1f, 224f / 255f, 128f / 255f);

	public static Color BodyColour { get; private set; } = Colors.White;

	/// <summary>Reads one of the TopBox's live text nodes; the node's name says which role it has.</summary>
	public static void Capture(MeshInstance3D node)
	{
		if (!GodotObject.IsInstanceValid(node) || !node.HasMeta(TextSizeName))
		{
			return;
		}

		string name = node.Name.ToString();
		int size = (int)node.GetMeta(TextSizeName);
		Color? colour = node.HasMeta(TextColourName) ? (Color)node.GetMeta(TextColourName) : null;
		if (name.Contains("Title", System.StringComparison.OrdinalIgnoreCase))
		{
			TitleSize = size;
			if (colour is { } c && c.A > 0.5f)
			{
				TitleColour = c;
			}

			Captured = true;
		}
		else if (name.Contains("Main", System.StringComparison.OrdinalIgnoreCase))
		{
			BodySize = size;
			if (colour is { } c && c.A > 0.5f)
			{
				BodyColour = c;
			}

			Captured = true;
		}
	}
}
