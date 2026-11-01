namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// Fable 3's boxes, drawn from the model the `GUI.*` natives fill (<see cref="Fable3GuiNatives"/>):
/// the front message or question box, the intermission card, the counters and timers, and the
/// menu box.
/// </summary>
/// <remarks>
/// THE GUEST ANSWERS ITS OWN BOX. The pad reaches the guest unchanged, its box manager routes the
/// action to the box showing (sub_825C8B68 -> OnAction, 41 yes / 42 no), and the script reads the
/// reply off the message bus — so this draws and never decides. What it draws is the call's own
/// text; the labels are the box's stated overrides or the guest's defaults (GUI_YES / GUI_NO,
/// GUI_DIALOG_ACCEPT).
///
/// The place and look are ours, laid out in a 720-high reference and scaled with the window so
/// the text stays legible at any size, as the TopBox is.
/// </remarks>
public partial class Fable3Boxes : Control
{
	private const float ReferenceHeight = 720f;
	private const float BoxWidth = 620f;
	private const float Padding = 16f;
	private const float ButtonGap = 24f;
	private const float BoxBottom = 150f;
	private const float CounterRight = 40f;
	private const float CounterTop = 110f;
	private const float IntermissionScale = 1.6f;

	private static readonly Color Panel = new(0f, 0f, 0f, 0.7f);
	private static readonly Color TitleColour = new(1f, 0.86f, 0.55f);
	private static readonly Color Disabled = new(0.6f, 0.6f, 0.6f);

	private readonly Fable3GuiNatives _natives;
	private UiFont? _font;
	private string _culture = string.Empty;
	private ulong _drawn = ulong.MaxValue;

	public Fable3Boxes(Fable3GuiNatives natives) => _natives = natives;

	public override void _Ready()
	{
		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;
	}

	public override void _Process(double delta)
	{
		if (_font is null || _culture != Engine_GameCulture.Current)
		{
			_culture = Engine_GameCulture.Current;
			_font = Engine_UiFontLibrary.Load(Refii.Core.Engine_GameData.Resolve(), _culture).Get(UiFontBucket.Normal);
			QueueRedraw();
		}

		// Timers move every frame; a model with none redraws only when it changes.
		Engine_UiGuiBoxes boxes = _natives.Boxes;
		if (boxes.Generation != _drawn || boxes.Counters.Count != 0)
		{
			_drawn = boxes.Generation;
			QueueRedraw();
		}
	}

	public override void _Draw()
	{
		if (_font is null)
		{
			return;
		}

		Engine_UiGuiBoxes boxes = _natives.Boxes;
		if (!boxes.Any)
		{
			return;
		}

		Vector2 size = GetViewportRect().Size;
		float scale = size.Y / ReferenceHeight;
		Engine_BabelDatabase? strings = Fable3Strings.Current;

		DrawCounters(boxes, size, scale);

		Engine_UiGuiBoxes.Box? card = null;
		Engine_UiGuiBoxes.Box? front = null;
		foreach (Engine_UiGuiBoxes.Box box in boxes.Boxes)
		{
			if (box.Name == "IntermissionBox")
			{
				card ??= box;
			}
			else
			{
				front ??= box;
			}
		}

		if (card is not null)
		{
			DrawIntermission(card, size, scale);
		}

		if (boxes.Menu is { } menu)
		{
			DrawMenu(menu, size, scale, strings);
		}
		else if (front is not null)
		{
			DrawFront(front, size, scale, strings);
		}
	}

	private void DrawFront(Engine_UiGuiBoxes.Box box, Vector2 size, float scale, Engine_BabelDatabase? strings)
	{
		float width = Mathf.Min(BoxWidth * scale, size.X - (2f * Padding * scale));
		float inner = width - (2f * Padding * scale);
		float lineHeight = _font!.Font.LineHeight * scale;

		var buttons = new List<string>();
		if (box.Kind == Engine_UiGuiBoxes.BoxKind.Question)
		{
			buttons.Add("A  " + Label(box.Buttons?.AcceptText, "GUI_YES", strings));
			buttons.Add("B  " + Label(box.Buttons?.HelpText, "GUI_NO", strings));
		}
		else
		{
			buttons.Add("A  " + Label(null, "GUI_DIALOG_ACCEPT", strings));
		}

		int titleLines = box.Title.Length == 0 ? 0 : Engine_UiCanvasText.MeasureWrappedLines(_font, box.Title, inner, scale);
		int bodyLines = box.Text.Length == 0 ? 0 : Engine_UiCanvasText.MeasureWrappedLines(_font, box.Text, inner, scale);
		float height = ((titleLines + bodyLines + 1) * lineHeight) + (3f * Padding * scale);
		var rect = new Rect2(new Vector2((size.X - width) * 0.5f, size.Y - (BoxBottom * scale) - height), new Vector2(width, height));
		DrawRect(rect, Panel);

		Vector2 pen = rect.Position + new Vector2(Padding * scale, Padding * scale);
		if (titleLines > 0)
		{
			pen.Y += Engine_UiCanvasText.DrawWrapped(this, _font, box.Title, pen, inner, scale, TitleColour);
		}

		if (bodyLines > 0)
		{
			Engine_UiCanvasText.DrawWrapped(this, _font, box.Text, pen, inner, scale, Colors.White);
		}

		// The prompts, right-aligned on the last line: A rightmost, as the guest lays them.
		float x = rect.End.X - (Padding * scale);
		float y = rect.End.Y - (Padding * scale) - lineHeight;
		foreach (string prompt in buttons)
		{
			float promptWidth = Engine_UiCanvasText.Measure(_font, prompt) * scale;
			x -= promptWidth;
			Engine_UiCanvasText.DrawLine(this, _font, prompt, new Vector2(x, y), scale, Colors.White);
			x -= ButtonGap * scale;
		}
	}

	private void DrawIntermission(Engine_UiGuiBoxes.Box card, Vector2 size, float scale)
	{
		// A title card: the stated text, large and centred, over a dimmed screen. It fades with
		// the model's close fade.
		float alpha = card.Persistent ? 1f : Mathf.Clamp(card.Remaining / Engine_UiGuiBoxes.CloseFadeSeconds, 0f, 1f);
		DrawRect(new Rect2(Vector2.Zero, size), new Color(0f, 0f, 0f, 0.5f * alpha));
		float textScale = scale * IntermissionScale;
		float inner = size.X * 0.8f;
		int lines = Engine_UiCanvasText.MeasureWrappedLines(_font!, card.Text, inner, textScale);
		float lineHeight = _font!.Font.LineHeight * textScale;
		var pen = new Vector2((size.X - inner) * 0.5f, (size.Y - (lines * lineHeight)) * 0.5f);
		Engine_UiCanvasText.DrawWrapped(this, _font, card.Text, pen, inner, textScale, new Color(1f, 1f, 1f, alpha));
	}

	private void DrawCounters(Engine_UiGuiBoxes boxes, Vector2 size, float scale)
	{
		float lineHeight = _font!.Font.LineHeight * scale;
		float y = CounterTop * scale;
		foreach (Engine_UiGuiBoxes.Counter counter in boxes.Counters)
		{
			if (!counter.Active)
			{
				continue;
			}

			string line = counter.Remaining is { } remaining
				? (counter.Text.Length == 0 ? Clock(remaining) : counter.Text + "  " + Clock(remaining))
				: counter.Text;
			if (line.Length == 0)
			{
				continue;
			}

			Color colour = counter.Flashing && (Time.GetTicksMsec() / 250) % 2 == 0 ? TitleColour : Colors.White;
			float width = Engine_UiCanvasText.Measure(_font, line) * scale;
			var pos = new Vector2(size.X - (CounterRight * scale) - width, y);
			DrawRect(new Rect2(pos - new Vector2(8f * scale, 4f * scale), new Vector2(width + (16f * scale), lineHeight + (8f * scale))), Panel);
			Engine_UiCanvasText.DrawLine(this, _font, line, pos, scale, colour);
			y += lineHeight + (12f * scale);
		}
	}

	private void DrawMenu(Engine_UiGuiBoxes.MenuBoxModel menu, Vector2 size, float scale, Engine_BabelDatabase? strings)
	{
		float width = Mathf.Min(BoxWidth * scale, size.X - (2f * Padding * scale));
		float inner = width - (2f * Padding * scale);
		float lineHeight = _font!.Font.LineHeight * scale;
		int titleLines = menu.Text.Length == 0 ? 0 : Engine_UiCanvasText.MeasureWrappedLines(_font, menu.Text, inner, scale);
		float rowHeight = lineHeight + (6f * scale);
		float height = (titleLines * lineHeight) + (menu.Options.Count * rowHeight) + (menu.Cancelable ? lineHeight : 0f) + (3f * Padding * scale);
		var rect = new Rect2(new Vector2((size.X - width) * 0.5f, (size.Y - height) * 0.5f), new Vector2(width, height));
		DrawRect(rect, Panel);

		Vector2 pen = rect.Position + new Vector2(Padding * scale, Padding * scale);
		if (titleLines > 0)
		{
			pen.Y += Engine_UiCanvasText.DrawWrapped(this, _font, menu.Text, pen, inner, scale, TitleColour) + (Padding * 0.5f * scale);
		}

		for (int i = 0; i < menu.Options.Count; ++i)
		{
			Engine_UiGuiBoxes.MenuOption option = menu.Options[i];
			if (i == menu.Highlight)
			{
				DrawRect(new Rect2(pen - new Vector2(6f * scale, 2f * scale), new Vector2(inner + (12f * scale), rowHeight)), new Color(1f, 1f, 1f, 0.15f));
			}

			Engine_UiCanvasText.DrawLine(this, _font, option.Label, pen, scale, option.Enabled ? Colors.White : Disabled);
			pen.Y += rowHeight;
		}

		if (menu.Cancelable)
		{
			string back = "B  " + Label(null, "GUI_DIALOG_BACK", strings);
			float backWidth = Engine_UiCanvasText.Measure(_font, back) * scale;
			Engine_UiCanvasText.DrawLine(this, _font, back, new Vector2(rect.End.X - (Padding * scale) - backWidth, rect.End.Y - (Padding * scale) - lineHeight), scale, Colors.White);
		}
	}

	/// <summary>A stated label tag, else the guest's default tag, resolved.</summary>
	private static string Label(string? stated, string fallback, Engine_BabelDatabase? strings) =>
		Engine_UiGuiText.Resolve(string.IsNullOrEmpty(stated) ? fallback : stated, strings);

	private static string Clock(float seconds)
	{
		int whole = Mathf.Max(0, Mathf.FloorToInt(seconds));
		return $"{whole / 60}:{whole % 60:00}";
	}
}
