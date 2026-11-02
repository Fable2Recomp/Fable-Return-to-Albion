using System;
using Godot;
using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// The pad glyphs drawn rather than shipped: PlayStation's and Switch's vocabularies, and the
/// Xbox's own for a prompt whose game button the player remapped (the shipped art shows the
/// game's button, not the one bound to it). Our own art — shapes and lettering in the game's
/// font on the keycaps' plate — drawn into the same proxies the keycaps use.
/// </summary>
/// <remarks>
/// Positional, as Godot's <c>JoyButton</c> is: <see cref="PadInput.A"/> is the south button, which
/// is Cross on a PlayStation pad and B on a Switch pad. A glyph names the PHYSICAL input the player
/// presses; which one presses a game button is the remap's (<see cref="Engine_PadRemap.Shown"/>).
/// </remarks>
public static partial class Engine_UiKeyGlyphs
{
	private static readonly Color PlateFill = new(0.10f, 0.09f, 0.07f, 0.92f);
	private static readonly Color PlateRim = new(0.85f, 0.80f, 0.68f, 1f);
	private static readonly Color Lettering = new(0.95f, 0.93f, 0.88f, 1f);
	private static readonly Color Lit = new(1f, 224f / 255f, 128f / 255f, 1f);

	private static readonly Color PsCross = new(0.49f, 0.70f, 0.95f, 1f);
	private static readonly Color PsCircle = new(0.95f, 0.42f, 0.42f, 1f);
	private static readonly Color PsSquare = new(0.88f, 0.58f, 0.84f, 1f);
	private static readonly Color PsTriangle = new(0.30f, 0.84f, 0.70f, 1f);

	private static readonly Color XboxA = new(0.45f, 0.80f, 0.30f, 1f);
	private static readonly Color XboxB = new(0.92f, 0.32f, 0.26f, 1f);
	private static readonly Color XboxX = new(0.30f, 0.55f, 0.95f, 1f);
	private static readonly Color XboxY = new(0.98f, 0.80f, 0.22f, 1f);

	/// <summary>A stick prompt's direction, from the shipped art's name (`LeftUp` is up).</summary>
	private enum Direction
	{
		None,
		Up,
		Down,
		Left,
		Right,
	}

	/// <summary>
	/// What one pad glyph shows: a physical input (null for the D-pad as a whole), a stick or
	/// D-pad direction, and whether it is a stick.
	/// </summary>
	private readonly record struct PadGlyph(PadInput? Input, Direction Arrow, bool Stick);

	/// <summary>Draws <paramref name="glyph"/> in <paramref name="style"/> filling one cell.</summary>
	private static void DrawPadGlyph(Canvas canvas, Rect2I cell, PadGlyph glyph, GlyphStyle style, bool ps5)
	{
		canvas.Clear(cell);
		float side = Math.Min(cell.Size.X, cell.Size.Y);
		float cx = cell.Position.X + (cell.Size.X / 2f);
		float cy = cell.Position.Y + (cell.Size.Y / 2f);
		float r = side * 0.44f;

		if (glyph.Stick)
		{
			DrawStick(canvas, cx, cy, r, glyph, style);
			return;
		}

		if (glyph.Input is not { } input || input is PadInput.DpadUp or PadInput.DpadDown or PadInput.DpadLeft or PadInput.DpadRight)
		{
			DrawDpad(canvas, cx, cy, r, glyph.Input switch
			{
				PadInput.DpadUp => Direction.Up,
				PadInput.DpadDown => Direction.Down,
				PadInput.DpadLeft => Direction.Left,
				PadInput.DpadRight => Direction.Right,
				_ => glyph.Arrow,
			});
			return;
		}

		switch (input)
		{
			case PadInput.A or PadInput.B or PadInput.X or PadInput.Y:
				DrawFace(canvas, cx, cy, r, input, style);
				break;
			case PadInput.LB or PadInput.RB or PadInput.LT or PadInput.RT:
			{
				bool trigger = Engine_PadRemap.IsTrigger(input);
				float w = side * 0.86f, h = side * (trigger ? 0.72f : 0.50f);
				var plate = new Rect2(cx - (w / 2f), cy - (h / 2f), w, h);
				canvas.FillRounded(plate, h * (trigger ? 0.42f : 0.5f), PlateFill);
				canvas.StrokeRounded(plate, h * (trigger ? 0.42f : 0.5f), Math.Max(1f, side / 28f), PlateRim);
				DrawText(canvas, plate.Grow(-side * 0.08f), Engine_ControllerFamily.Label(style, input, ps5), Lettering);
				break;
			}
			case PadInput.L3 or PadInput.R3:
				DrawStick(canvas, cx, cy, r, glyph, style);
				break;
			default:
			{
				// Start and Back: a pill, lettered.
				float w = side * 0.90f, h = side * 0.46f;
				var plate = new Rect2(cx - (w / 2f), cy - (h / 2f), w, h);
				canvas.FillRounded(plate, h / 2f, PlateFill);
				canvas.StrokeRounded(plate, h / 2f, Math.Max(1f, side / 28f), PlateRim);
				DrawText(canvas, plate.Grow(-side * 0.07f), Engine_ControllerFamily.Label(style, input, ps5), Lettering);
				break;
			}
		}
	}

	private static void DrawFace(Canvas canvas, float cx, float cy, float r, PadInput input, GlyphStyle style)
	{
		canvas.FillCircle(cx, cy, r, PlateFill);
		float rim = Math.Max(1f, r / 14f);
		switch (style)
		{
			case GlyphStyle.PlayStation:
			{
				Color ink = input switch
				{
					PadInput.A => PsCross,
					PadInput.B => PsCircle,
					PadInput.X => PsSquare,
					_ => PsTriangle,
				};
				canvas.StrokeCircle(cx, cy, r - (rim / 2f), rim, ink with { A = 0.55f });
				float s = r * 0.48f, line = Math.Max(1.5f, r * 0.13f);
				switch (input)
				{
					case PadInput.A:
						canvas.Line(cx - s, cy - s, cx + s, cy + s, line, ink);
						canvas.Line(cx - s, cy + s, cx + s, cy - s, line, ink);
						break;
					case PadInput.B:
						canvas.StrokeCircle(cx, cy, s, line, ink);
						break;
					case PadInput.X:
						canvas.StrokeRounded(new Rect2(cx - s, cy - s, s * 2f, s * 2f), line * 0.3f, line, ink);
						break;
					default:
					{
						float top = cy - (s * 1.05f), bottom = cy + (s * 0.75f), half = s * 1.05f;
						canvas.Line(cx, top, cx + half, bottom, line, ink);
						canvas.Line(cx + half, bottom, cx - half, bottom, line, ink);
						canvas.Line(cx - half, bottom, cx, top, line, ink);
						break;
					}
				}

				break;
			}
			case GlyphStyle.Switch:
				canvas.StrokeCircle(cx, cy, r - (rim / 2f), rim, PlateRim);
				DrawText(canvas, Square(cx, cy, r * 1.2f), Engine_ControllerFamily.Label(style, input, false), Lettering);
				break;
			default:
			{
				Color ink = input switch
				{
					PadInput.A => XboxA,
					PadInput.B => XboxB,
					PadInput.X => XboxX,
					_ => XboxY,
				};
				canvas.StrokeCircle(cx, cy, r - (rim / 2f), rim, ink);
				DrawText(canvas, Square(cx, cy, r * 1.2f), Engine_ControllerFamily.Label(style, input, false), ink);
				break;
			}
		}
	}

	private static void DrawStick(Canvas canvas, float cx, float cy, float r, PadGlyph glyph, GlyphStyle style)
	{
		canvas.FillCircle(cx, cy, r, PlateFill);
		canvas.StrokeCircle(cx, cy, r - Math.Max(0.5f, r / 28f), Math.Max(1f, r / 14f), PlateRim);
		canvas.StrokeCircle(cx, cy, r * 0.62f, Math.Max(1f, r / 20f), PlateRim with { A = 0.5f });
		bool left = glyph.Input is PadInput.L3;
		string label = glyph.Stick
			? (left ? "L" : "R")
			: Engine_ControllerFamily.Label(style, glyph.Input ?? PadInput.L3, false);
		DrawText(canvas, Square(cx, cy, r * 0.95f), label, Lettering);
		if (glyph.Arrow != Direction.None)
		{
			Arrow(canvas, cx, cy, r * 0.82f, r * 0.22f, glyph.Arrow, Lit);
		}
	}

	private static void DrawDpad(Canvas canvas, float cx, float cy, float r, Direction lit)
	{
		float arm = r * 0.62f, len = r * 0.98f;
		var vertical = new Rect2(cx - (arm / 2f), cy - len, arm, len * 2f);
		var horizontal = new Rect2(cx - len, cy - (arm / 2f), len * 2f, arm);
		float stroke = Math.Max(1f, r / 16f);
		canvas.FillRounded(vertical, arm * 0.2f, PlateFill);
		canvas.FillRounded(horizontal, arm * 0.2f, PlateFill);
		canvas.StrokeRounded(vertical, arm * 0.2f, stroke, PlateRim);
		canvas.StrokeRounded(horizontal, arm * 0.2f, stroke, PlateRim);
		canvas.FillRounded(new Rect2(cx - (arm / 2f) + stroke, cy - (arm / 2f) + stroke, arm - (stroke * 2f), arm - (stroke * 2f)), 0f, PlateFill);
		if (lit != Direction.None)
		{
			Arrow(canvas, cx, cy, len * 0.62f, arm * 0.36f, lit, Lit);
		}
	}

	/// <summary>A filled arrowhead <paramref name="reach"/> from the centre, pointing <paramref name="towards"/>.</summary>
	private static void Arrow(Canvas canvas, float cx, float cy, float reach, float size, Direction towards, Color colour)
	{
		(float dx, float dy) = towards switch
		{
			Direction.Up => (0f, -1f),
			Direction.Down => (0f, 1f),
			Direction.Left => (-1f, 0f),
			_ => (1f, 0f),
		};
		float tipX = cx + (dx * reach), tipY = cy + (dy * reach);
		float baseX = tipX - (dx * size * 1.4f), baseY = tipY - (dy * size * 1.4f);
		canvas.FillTriangle(tipX, tipY, baseX + (dy * size), baseY - (dx * size), baseX - (dy * size), baseY + (dx * size), colour);
	}

	private static Rect2 Square(float cx, float cy, float side) => new(cx - (side / 2f), cy - (side / 2f), side, side);

	private static void DrawText(Canvas canvas, Rect2 box, string label, Color colour) =>
		DrawLabel(canvas, new Rect2I((int)box.Position.X, (int)box.Position.Y, (int)box.Size.X, (int)box.Size.Y), label, colour);
}
