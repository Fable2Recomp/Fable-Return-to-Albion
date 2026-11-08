namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// Fable 3's boxes, drawn from the model the `GUI.*` natives fill (<see cref="Fable3GuiNatives"/>):
/// the front message or question box, the intermission card and the menu box. The counters and
/// timers are hud.bgf's `CornerBox`, on the shared HUD view (<see cref="Fable3Hud"/>).
/// </summary>
/// <remarks>
/// GODOT TAKES THE PAD; THE GUEST'S OWN ANSWER RUNS. For the boxes Godot owns (questions, the
/// numeric adjuster, messages, quest boxes, notices, the pause screen's message box) and the menu
/// box, the guest's pad handlers do nothing (display_box_hooks.cpp); a press here becomes the
/// guest tick action <see cref="Fable3BoxAnswers"/> names, which the guest's thread hands to the
/// box's own answer function — so the reply still reaches the scripts through the guest's message
/// bus, and the box still closes on the guest's own lifetime (@Box.Close). What it draws is the
/// box's own text; the labels are the box's stated overrides or the guest's defaults (GUI_YES /
/// GUI_NO, GUI_DIALOG_ACCEPT), each behind the shared keycap for its button
/// (<see cref="Fable3Prompts"/>).
///
/// The place and look are ours, laid out in a 720-high reference and scaled with the window so
/// the text stays legible at any size.
/// </remarks>
public partial class Fable3Boxes : Control
{
	private const float ReferenceHeight = 720f;
	private const float BoxWidth = 620f;
	private const float Padding = 16f;
	private const float ButtonGap = 24f;
	private const float BoxBottom = 150f;

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
		if (_culture != Engine_GameCulture.Current)
		{
			_culture = Engine_GameCulture.Current;
			Engine_UiFontLibrary fonts = Fable3Fonts.For(_culture);
			Fable3Prompts.UseFonts(fonts);
			_font = fonts.Get(UiFontBucket.Normal);
			QueueRedraw();
		}

		Engine_UiGuiBoxes boxes = _natives.Boxes;
		Answer(boxes, delta);
		if (boxes.Generation != _drawn)
		{
			_drawn = boxes.Generation;
			QueueRedraw();
		}
	}

	/// <summary>The box last answered and how long ago: one answer per box until the guest drops it.</summary>
	private uint _answered;
	private double _answeredAge;
	private uint _wasDown;

	/// <summary>The answer is the guest's to act on; a second press inside this is the same press.</summary>
	private const double AnswerLatch = 0.5;

	/// <summary>Reads the pad for the menu box or the front owned box and posts the answer.</summary>
	private void Answer(Engine_UiGuiBoxes boxes, double delta)
	{
		uint down = Refii.Game.GameInput.Pad0.Buttons;
		uint pressedBits = down & ~_wasDown;
		_wasDown = down;
		_answeredAge += delta;
		if (pressedBits == 0u || !Refii.Host.HostBridge.IsGuestUp || Fable3HostOptions.IsOpen)
		{
			return;
		}

		var press = new Fable3BoxAnswers.Press(
			(pressedBits & Refii.Game.GameInput.ButtonA) != 0, (pressedBits & Refii.Game.GameInput.ButtonB) != 0,
			(pressedBits & Refii.Game.GameInput.ButtonX) != 0, (pressedBits & Refii.Game.GameInput.ButtonY) != 0,
			(pressedBits & Refii.Game.GameInput.DpadUp) != 0, (pressedBits & Refii.Game.GameInput.DpadDown) != 0,
			(pressedBits & Refii.Game.GameInput.DpadLeft) != 0, (pressedBits & Refii.Game.GameInput.DpadRight) != 0);

		if (boxes.Menu is { } menu)
		{
			if (press.Up || press.Down)
			{
				menu.Highlight = Fable3BoxAnswers.MenuStep(menu.Highlight, menu.Options.Count, press.Down);
				QueueRedraw();
			}
			else if (press.A && menu.Options.Count > 0)
			{
				Post(Fable3BoxAnswers.MenuPick(menu.Highlight), 0u);
			}
			else if (press.B && menu.Cancelable)
			{
				Post(Fable3BoxAnswers.MenuCancel, 0u);
			}

			return;
		}

		if (_natives.FrontOwned is not { } front)
		{
			return;
		}

		if (front.Kind == "adjuster" && boxes.Adjusting is not null && (press.Left || press.Right || press.Up || press.Down))
		{
			// The value is Godot's until the answer: it is stored where the guest's answer reads it.
			if (boxes.StepAdjuster(press.Right || press.Up))
			{
				QueueRedraw();
			}

			return;
		}

		if (front.Address == _answered && _answeredAge < AnswerLatch)
		{
			return;
		}

		string? action = Fable3BoxAnswers.ForBox(front.Kind, front.Address, press, boxes.Adjusting?.Value ?? 0, front.Download);
		if (action is not null)
		{
			Post(action, front.Address);
		}
	}

	private void Post(string action, uint address)
	{
		Refii.Host.HostBridge.PostFrontEndAction(action);
		_answered = address;
		_answeredAge = 0.0;
		// The press that answered is not a press on whatever the guest shows next.
		Refii.Core.Engine_TitleGame.Active.Input.HoldUntilReleased();
		GD.Print($"[fable3-boxes] answered: {action}");
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

		var buttons = new List<(string Button, string Label)>();
		if (box.Kind == Engine_UiGuiBoxes.BoxKind.Question)
		{
			buttons.Add(("A", Label(box.Buttons?.AcceptText, "GUI_YES", strings)));
			buttons.Add(("B", Label(box.Buttons?.HelpText, "GUI_NO", strings)));
		}
		else
		{
			// A box stated without its A (TU3's non-interactive message box) draws no prompt row.
			if (box.Buttons is not { ShowA: false })
			{
				buttons.Add(("A", Label(null, "GUI_DIALOG_ACCEPT", strings)));
			}

			if (box.Kind == Engine_UiGuiBoxes.BoxKind.Adjuster)
			{
				// sub_8281C638(box, 0): B finishes it without the value (event 70).
				buttons.Add(("B", Label(null, "GUI_DIALOG_BACK", strings)));
			}

			// The download form: sub_8281AFA8 shows `paper.MessageBox.Action` and names its X
			// button by the tag at box+36 (0x8281B048 / 0x8281B07C); X (76) is taken only then
			// (sub_8281B390 at 0x8281B410) and opens the marketplace itself.
			if (box.AcceptCaption.Length > 0)
			{
				buttons.Add(("X", Label(box.AcceptCaption, "GUI_DIALOG_ACCEPT", strings)));
			}
		}

		// The adjuster's value is Godot's until it answers: shown under the question.
		string body = box.Kind == Engine_UiGuiBoxes.BoxKind.Adjuster && _natives.Boxes.Adjusting is { } adjusting
			? box.Text + "\n" + adjusting.Display
			: box.Text;
		int titleLines = box.Title.Length == 0 ? 0 : Engine_UiCanvasText.MeasureWrappedLines(_font, box.Title, inner, scale);
		int bodyLines = body.Length == 0 ? 0 : Engine_UiCanvasText.MeasureWrappedLines(_font, body, inner, scale);
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
			Engine_UiCanvasText.DrawWrapped(this, _font, body, pen, inner, scale, Colors.White);
		}

		// The prompts, right-aligned on the last line: A rightmost, as the guest lays them.
		float x = rect.End.X - (Padding * scale);
		float y = rect.End.Y - (Padding * scale) - lineHeight;
		foreach ((string button, string label) in buttons)
		{
			x -= Fable3Prompts.Measure(_font, label, scale);
			Fable3Prompts.Draw(this, _font, button, label, new Vector2(x, y), scale, Colors.White);
			x -= ButtonGap * scale;
		}
	}

	/// <summary>
	/// The intermission line as intermissionbox.bgf lays it out (canvas units about the screen
	/// centre, y up): `Layer.Group` at y −19 holding `TargetBox`, a 512×32 strip at 50% opacity
	/// at y −289, and `Text` (fertigo 16, white, box 0.9 of the width) at y −302 — a single line
	/// in a strip near the bottom centre, not a title card. It fades with the model's close fade.
	/// </summary>
	private void DrawIntermission(Engine_UiGuiBoxes.Box card, Vector2 size, float scale)
	{
		float alpha = card.Persistent ? 1f : Mathf.Clamp(card.Remaining / Engine_UiGuiBoxes.CloseFadeSeconds, 0f, 1f);
		if (card.Text.Length == 0)
		{
			return;
		}

		float inner = Mathf.Min(IntermissionTextWidth * scale, size.X - (2f * Padding * scale));
		int lines = Engine_UiCanvasText.MeasureWrappedLines(_font!, card.Text, inner, scale);
		float lineHeight = _font!.Font.LineHeight * scale;
		float textWidth = lines > 1 ? inner : Engine_UiCanvasText.Measure(_font, card.Text) * scale;
		float stripWidth = Mathf.Max(IntermissionStripWidth * scale, textWidth + (2f * Padding * scale));
		float stripHeight = Mathf.Max(IntermissionStripHeight * scale, (lines * lineHeight) + (Padding * scale));
		float centreY = (size.Y * 0.5f) - (IntermissionStripY * scale);
		var strip = new Rect2(new Vector2((size.X - stripWidth) * 0.5f, centreY - (stripHeight * 0.5f)), new Vector2(stripWidth, stripHeight));
		DrawRect(strip, new Color(0f, 0f, 0f, 0.5f * alpha));
		var pen = new Vector2((size.X - (lines > 1 ? inner : textWidth)) * 0.5f, centreY - (lines * lineHeight * 0.5f));
		Engine_UiCanvasText.DrawWrapped(this, _font, card.Text, pen, inner, scale, new Color(1f, 1f, 1f, alpha));
	}

	/// <summary>intermissionbox.bgf `Layer.Group` (−19) + `Group.TargetBox` (−289): the strip's centre.</summary>
	private const float IntermissionStripY = -308f;
	private const float IntermissionStripWidth = 512f;
	private const float IntermissionStripHeight = 32f;
	private const float IntermissionTextWidth = 0.9f * 1280f;

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
			string back = Label(null, "GUI_DIALOG_BACK", strings);
			float backWidth = Fable3Prompts.Measure(_font, back, scale);
			Fable3Prompts.Draw(this, _font, "B", back, new Vector2(rect.End.X - (Padding * scale) - backWidth, rect.End.Y - (Padding * scale) - lineHeight), scale, Colors.White);
		}
	}

	/// <summary>A stated label tag, else the guest's default tag, resolved.</summary>
	private static string Label(string? stated, string fallback, Engine_BabelDatabase? strings) =>
		Engine_UiGuiText.Resolve(string.IsNullOrEmpty(stated) ? fallback : stated, strings);
}
