using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// The end credits (screen 29, `GUI.ShowCreditsScreen`). The guest runs the whole roll itself:
/// `sub_826B79D8` loads text group `GUI_CREDITS` and parks 24 recycled Text elements a line
/// apart; its update `sub_826B7D00` moves them up 30 units a second, hands each the next string
/// as it wraps past y 360, and closes the screen when the last string has gone by. Nothing of
/// that is re-derived here — this draws each Text element's live string at its live position.
///
/// `creditsscreen.bgf`'s elements are anonymous (their names are hashes only), which is why no
/// template can name them; the roll needs none, because every Text element in the file is a
/// credits line. Drawn on its own layer ABOVE the video: the script plays `Credits.bik` and
/// opens the screen over it, and the guest drew its UI after the movie frame.
/// </summary>
public sealed partial class Engine_UiCreditsRoll : Control
{
	public const string Presentation = "creditsscreen";

	/// <summary>The game's font library.</summary>
	public Engine_UiFontLibrary? Fonts { get; set; }

	/// <summary>One line: the guest's string at its position on the 1280x720 canvas (origin at
	/// the centre, y up), in the file's authored size and colour.</summary>
	public readonly record struct Line(string Text, Vector2 At, int Size, Color Colour);

	private readonly List<Line> _lines = new();

	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Ignore;
		SetAnchorsPreset(LayoutPreset.FullRect);
	}

	/// <summary>States this frame's lines; an empty set takes the roll down.</summary>
	public void Show(IReadOnlyList<Line> lines)
	{
		_lines.Clear();
		_lines.AddRange(lines);
		Visible = _lines.Count > 0;
		QueueRedraw();
	}

	public override void _Draw()
	{
		if (Fonts is null || Size.Y <= 0f)
		{
			return;
		}

		float scale = Size.Y / 720f;
		Vector2 centre = Size * 0.5f;
		foreach (Line line in _lines)
		{
			if (Fonts.For(line.Size) is not { } picked)
			{
				continue;
			}

			(UiFont font, float fontScale) = picked;
			float textScale = fontScale * scale;
			float width = Engine_UiCanvasText.Measure(font, line.Text) * textScale;
			float height = font.Font.LineHeight * textScale;
			var origin = new Vector2(
				centre.X + (line.At.X * scale) - (width * 0.5f),
				centre.Y - (line.At.Y * scale) - (height * 0.5f));
			Engine_UiCanvasText.DrawLine(this, font, line.Text, origin, textScale, line.Colour);
		}
	}
}
