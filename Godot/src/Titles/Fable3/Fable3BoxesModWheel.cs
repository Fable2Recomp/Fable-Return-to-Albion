namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Game;
using Refii.Mods;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// THE CONTROLLER'S MOD WHEEL ON FABLE 3 (<see cref="ModWheel"/>: every input action a mod declares with
/// <c>"wheel"</c>). Fable 3 has no expression wheel for the mods' entries to stand on, so the wheel is
/// drawn here in the game's box look over the world. What it does is the shared wheel's: Back opens and
/// closes it (only while something is on it, so Back stays the game's otherwise), the left stick points,
/// A chooses for the player who chose, B closes, LB and RB turn the page. Each player has their own: pad 0
/// and a couch partner's pad (<see cref="Fable3Input.ModPlayer"/>). The wedges keep the shared numbering —
/// sector i at −45° + i·45° counter-clockwise from the south-east, 0 the first (empty) wedge and 7 the
/// south's Close — so <see cref="ModWheel.EntryAt"/> places each entry as on Fable 2.
/// </summary>
public partial class Fable3Boxes
{
	/// <summary>How far the stick leans before it points at a wedge (of 32767).</summary>
	private const int WheelPointAt = 16384;

	private const float WheelRadius = 190f;
	private const float WheelIcon = 48f;

	/// <summary>Per player (1, 2): the wedge pointed at (-1 none) and last frame's buttons.</summary>
	private static readonly int[] _wheelSector = { -1, -1 };
	private static readonly uint[] _wheelWas = new uint[2];
	private readonly Dictionary<string, Texture2D?> _wheelIcons = new(System.StringComparer.Ordinal);
	private string _wheelDrawn = string.Empty;

	/// <summary>
	/// A pad's frame for the wheel, from <see cref="Fable3Input.PublishPad"/>: follows Back, and drives an
	/// open wheel. Back is taken from the game while the wheel has it; true while the wheel stands, when the
	/// whole pad is the wheel's.
	/// </summary>
	internal static bool FollowModWheel(int player, ref GameInput.PadState guest, bool inWorld)
	{
		if (player is not (1 or 2))
		{
			return false;
		}

		int seat = player - 1;
		bool back = (guest.Buttons & GameInput.Back) != 0;
		bool owned = ModWheel.Follow(player, back, inWorld, out _);
		if (!ModWheel.IsOpen(player))
		{
			_wheelSector[seat] = -1;
			_wheelWas[seat] = guest.Buttons;
			if (owned)
			{
				guest.Buttons &= ~GameInput.Back;
				guest.PhysicalButtons &= ~GameInput.Back;
				guest.OtherButtons &= ~GameInput.Back;
			}

			return false;
		}

		if ((guest.LeftX * (long)guest.LeftX) + (guest.LeftY * (long)guest.LeftY) >= (long)WheelPointAt * WheelPointAt)
		{
			float degrees = Mathf.RadToDeg(Mathf.Atan2(guest.LeftY, guest.LeftX));
			_wheelSector[seat] = ((Mathf.RoundToInt((degrees + 45f) / 45f) % 8) + 8) % 8;
		}

		uint pressed = guest.Buttons & ~_wheelWas[seat];
		_wheelWas[seat] = guest.Buttons;
		if ((pressed & GameInput.ButtonA) != 0 && _wheelSector[seat] >= 0)
		{
			ModWheel.Choose(player, _wheelSector[seat]);
		}
		else if ((pressed & GameInput.ButtonB) != 0)
		{
			ModWheel.Close(player);
		}
		else if ((pressed & GameInput.RightShoulder) != 0)
		{
			ModWheel.StepPage(player, 1);
		}
		else if ((pressed & GameInput.LeftShoulder) != 0)
		{
			ModWheel.StepPage(player, -1);
		}

		if (!ModWheel.IsOpen(player))
		{
			// The press that closed it never reaches the game.
			Refii.Core.Engine_TitleGame.Active.Input.HoldUntilReleased();
		}

		return true;
	}

	/// <summary>Redraws when a wheel opens, closes, pages or points elsewhere.</summary>
	private void WatchModWheel()
	{
		string state = ModWheel.OpenPlayer == 0
			? string.Empty
			: $"{ModWheel.IsOpen(1)}:{ModWheel.PageOf(1)}:{_wheelSector[0]}|{ModWheel.IsOpen(2)}:{ModWheel.PageOf(2)}:{_wheelSector[1]}";
		if (state != _wheelDrawn)
		{
			_wheelDrawn = state;
			QueueRedraw();
		}
	}

	/// <summary>Each open wheel: one in the middle, or the couch pair's side by side.</summary>
	private void DrawModWheels(Vector2 size, float scale, Engine_BabelDatabase? strings)
	{
		bool one = ModWheel.IsOpen(1), two = ModWheel.IsOpen(2);
		if (one)
		{
			DrawModWheel(1, new Vector2(size.X * (two ? 0.3f : 0.5f), size.Y * 0.5f), scale, strings);
		}

		if (two)
		{
			DrawModWheel(2, new Vector2(size.X * (one ? 0.7f : 0.5f), size.Y * 0.5f), scale, strings);
		}
	}

	private void DrawModWheel(int player, Vector2 centre, float scale, Engine_BabelDatabase? strings)
	{
		if (_font is null)
		{
			return;
		}

		List<ModInput.Action> entries = ModWheel.Entries();
		int page = ModWheel.PageOf(player);
		int pages = ModWheel.PageCount(entries.Count, ModWheel.TopTaken);
		int pointed = _wheelSector[player - 1];
		float radius = WheelRadius * scale;
		float lineHeight = _font.Font.LineHeight * scale;
		DrawCircle(centre, radius + (WheelIcon * 0.9f * scale), Panel);
		for (int sector = 0; sector < 8; ++sector)
		{
			float angle = Mathf.DegToRad(-45f + (sector * 45f));
			// Screen Y runs down: the stick's north is up.
			Vector2 at = centre + (new Vector2(Mathf.Cos(angle), -Mathf.Sin(angle)) * radius);
			string label;
			string icon = string.Empty;
			if (sector == ModWheel.CloseSector)
			{
				label = Label(null, "GUI_DIALOG_BACK", strings);
			}
			else if (ModWheel.Top is { } top && sector == ModWheel.TopSector)
			{
				label = ModText.Game(top.Label);
				icon = top.Icon;
			}
			else
			{
				int entry = ModWheel.EntryAt(page, sector, entries.Count, ModWheel.TopTaken);
				if (entry < 0)
				{
					continue;
				}

				label = ModText.Game(entries[entry].Wheel);
				icon = entries[entry].WheelIcon;
			}

			bool lit = sector == pointed;
			if (lit)
			{
				DrawCircle(at, WheelIcon * 0.8f * scale, new Color(1f, 1f, 1f, 0.18f));
			}

			// A picture that does not load leaves the label alone, centred on the wedge.
			Texture2D? texture = icon.Length != 0 ? WheelTexture(icon) : null;
			if (texture is not null)
			{
				float side = WheelIcon * scale;
				DrawTextureRect(texture, new Rect2(at - new Vector2(side * 0.5f, side * 0.75f), new Vector2(side, side)), false);
			}

			float width = Engine_UiCanvasText.Measure(_font, label) * scale;
			Vector2 pen = at + new Vector2(-width * 0.5f, texture is not null ? WheelIcon * 0.3f * scale : -lineHeight * 0.5f);
			Engine_UiCanvasText.DrawLine(this, _font, label, pen, scale, lit ? TitleColour : Colors.White);
		}

		if (pages > 1)
		{
			string pageText = $"{page + 1} / {pages}";
			float width = Engine_UiCanvasText.Measure(_font, pageText) * scale;
			Engine_UiCanvasText.DrawLine(this, _font, pageText, centre - new Vector2(width * 0.5f, lineHeight * 0.5f), scale, TitleColour);
		}
	}

	/// <summary>An entry's picture (a mod's PNG or a game texture), loaded once; null draws the label alone.</summary>
	private Texture2D? WheelTexture(string path)
	{
		if (!_wheelIcons.TryGetValue(path, out Texture2D? texture))
		{
			texture = Fable3HudDocument.Fable3IconTexture(path);
			_wheelIcons[path] = texture;
		}

		return texture;
	}
}
