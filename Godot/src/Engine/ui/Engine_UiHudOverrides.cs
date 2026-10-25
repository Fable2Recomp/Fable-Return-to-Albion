using System;
using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// A MOD'S HUD OVERRIDES, per player, by element name (<c>hud_icon</c> / <c>hud_slide</c> /
/// <c>hud_hold</c> on every surface: <c>refii_mod_hud_icon</c>, <c>Refii.hud_icon</c>,
/// <c>RefiiHost.hud_icon</c>, and the two beside each): the texture an element shows (a game texture
/// path or an enabled mod's PNG, fitted to the slot), the slide an element sits on (as the game
/// counts them; 0 lets the guest's stand), and the pieces held visible (a piece whose owner chain is
/// kept on its "in" slides while the guest would have hidden it — the D-pad is the case). Player 1
/// or 2 is that player's HUD — the shared HUD for the local player, the second half for local player
/// two — 0 both. The game's UI owner applies them where it folds the guest's HUD state
/// (<c>GameUi</c>, <c>GameUiPlayerTwoHud</c>); everything goes when the mods' Lua reloads or the game
/// goes down.
/// </summary>
/// <remarks>
/// An ELEMENT is named by a slide-toggle binding name (<c>DPadUp</c>, the name the guest's own
/// <c>GUI.ToggleElement</c> states) or by an element path of the HUD document, matched by trailing
/// segments (<c>dpadmenu.group.up.up</c>) — <see cref="MatchesPath"/>. Pure state: held by tests.
/// </remarks>
public static class Engine_UiHudOverrides
{
	/// <summary>The size every HUD icon slot takes; smaller or larger art is fitted into it (<see cref="FitIcon"/>).</summary>
	public const int IconSize = 128;

	private static readonly Dictionary<(int Player, string Element), string> Icons = new();
	private static readonly Dictionary<(int Player, string Element), int> Slides = new();
	private static readonly HashSet<(int Player, string Piece)> Holds = new();

	/// <summary>Bumped on every change, so a HUD feed can re-sample its textures and slides.</summary>
	public static int Generation { get; private set; }

	public static int Count
	{
		get
		{
			lock (Icons)
			{
				return Icons.Count + Slides.Count + Holds.Count;
			}
		}
	}

	/// <summary>
	/// States the texture <paramref name="element"/> shows for <paramref name="player"/> (1, 2 or 0
	/// for both): a game texture path (<c>Art\GUI\WheelIcons\expressions.tex</c>) or an enabled mod's
	/// PNG under <c>art/</c>; empty clears. False for no element name.
	/// </summary>
	public static bool SetIcon(int player, string element, string texture)
	{
		string name = Key(element);
		if (name.Length == 0 || player is < 0 or > 2)
		{
			return false;
		}

		lock (Icons)
		{
			bool changed = false;
			foreach (int p in Players(player))
			{
				string value = (texture ?? string.Empty).Trim();
				if (value.Length == 0)
				{
					changed |= Icons.Remove((p, name));
				}
				else if (!Icons.TryGetValue((p, name), out string? standing) || standing != value)
				{
					Icons[(p, name)] = value;
					changed = true;
				}
			}

			if (changed)
			{
				++Generation;
			}
		}

		return true;
	}

	/// <summary>
	/// States the slide <paramref name="element"/> sits on for <paramref name="player"/>, as the game
	/// counts slides (1 the first); 0 clears and the guest's own stands. False for no element or a
	/// negative slide.
	/// </summary>
	public static bool SetSlide(int player, string element, int slide)
	{
		string name = Key(element);
		if (name.Length == 0 || player is < 0 or > 2 || slide < 0)
		{
			return false;
		}

		lock (Icons)
		{
			bool changed = false;
			foreach (int p in Players(player))
			{
				if (slide == 0)
				{
					changed |= Slides.Remove((p, name));
				}
				else if (!Slides.TryGetValue((p, name), out int standing) || standing != slide)
				{
					Slides[(p, name)] = slide;
					changed = true;
				}
			}

			if (changed)
			{
				++Generation;
			}
		}

		return true;
	}

	/// <summary>Holds the HUD piece <paramref name="piece"/> visible for <paramref name="player"/> (or lets it go). False for no name.</summary>
	public static bool SetHold(int player, string piece, bool on)
	{
		string name = Key(piece);
		if (name.Length == 0 || player is < 0 or > 2)
		{
			return false;
		}

		lock (Icons)
		{
			bool changed = false;
			foreach (int p in Players(player))
			{
				changed |= on ? Holds.Add((p, name)) : Holds.Remove((p, name));
			}

			if (changed)
			{
				++Generation;
			}
		}

		return true;
	}

	/// <summary>The icons stated for <paramref name="player"/>'s HUD: element name → texture.</summary>
	public static List<(string Element, string Texture)> IconsFor(int player)
	{
		var list = new List<(string, string)>();
		lock (Icons)
		{
			foreach (((int p, string element), string texture) in Icons)
			{
				if (p == player)
				{
					list.Add((element, texture));
				}
			}
		}

		return list;
	}

	/// <summary>The slides stated for <paramref name="player"/>'s HUD: element name → slide.</summary>
	public static List<(string Element, int Slide)> SlidesFor(int player)
	{
		var list = new List<(string, int)>();
		lock (Icons)
		{
			foreach (((int p, string element), int slide) in Slides)
			{
				if (p == player)
				{
					list.Add((element, slide));
				}
			}
		}

		return list;
	}

	/// <summary>The pieces held visible for <paramref name="player"/>, sorted.</summary>
	public static List<string> HoldsFor(int player)
	{
		var list = new List<string>();
		lock (Icons)
		{
			foreach ((int p, string piece) in Holds)
			{
				if (p == player)
				{
					list.Add(piece);
				}
			}
		}

		list.Sort(StringComparer.Ordinal);
		return list;
	}

	/// <summary>Whether <paramref name="player"/> has any override standing.</summary>
	public static bool AnyFor(int player)
	{
		lock (Icons)
		{
			foreach ((int p, _) in Icons.Keys) if (p == player) return true;
			foreach ((int p, _) in Slides.Keys) if (p == player) return true;
			foreach ((int p, _) in Holds) if (p == player) return true;
		}

		return false;
	}

	/// <summary>Forgets every override (the mods' Lua reloaded, the game went down).</summary>
	public static void Clear()
	{
		lock (Icons)
		{
			if (Icons.Count + Slides.Count + Holds.Count == 0)
			{
				return;
			}

			Icons.Clear();
			Slides.Clear();
			Holds.Clear();
			++Generation;
		}
	}

	/// <summary>
	/// Whether an element's authored path (<c>a.b.c.d</c>, the named ancestors joined) is the one
	/// <paramref name="key"/> names: equal, or ending in <c>.key</c>, case-insensitive. A key of
	/// several segments that matches nothing is retried without its first segment while two remain,
	/// so a mod naming more of the path than the document carries still lands.
	/// </summary>
	public static bool MatchesPath(string path, string key)
	{
		string want = key.Trim();
		while (want.Length != 0)
		{
			if (string.Equals(path, want, StringComparison.OrdinalIgnoreCase) ||
				path.EndsWith("." + want, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			int dot = want.IndexOf('.');
			want = dot >= 0 && want.IndexOf('.', dot + 1) >= 0 ? want[(dot + 1)..] : string.Empty;
		}

		return false;
	}

	/// <summary>
	/// Where art of <paramref name="width"/> × <paramref name="height"/> sits in a <paramref name="size"/>
	/// square slot: scaled to 84% of the slot on its longer side and centred, as the game's own
	/// slot icons are drawn. Zero width for nothing to fit.
	/// </summary>
	public static (int Width, int Height, int X, int Y) FitIcon(int width, int height, int size = IconSize)
	{
		if (width <= 0 || height <= 0 || size < 2)
		{
			return default;
		}

		double scale = size * 0.84 / Math.Max(width, height);
		int w = Math.Max(1, (int)Math.Round(width * scale));
		int h = Math.Max(1, (int)Math.Round(height * scale));
		return (w, h, (size - w) / 2, (size - h) / 2);
	}

	/// <summary>Whether art is already the slot's size and needs no fitting.</summary>
	public static bool FitsSlot(int width, int height, int size = IconSize) => width == size && height == size;

	private static string Key(string? name) => (name ?? string.Empty).Trim();

	private static IEnumerable<int> Players(int player)
	{
		if (player == 0)
		{
			yield return 1;
			yield return 2;
		}
		else
		{
			yield return player;
		}
	}
}
