namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using System.Text.RegularExpressions;
using Godot;
using Refii.Ui;

/// <summary>
/// A MOD'S HUD OVERRIDES ON FABLE 3 (<see cref="Engine_UiHudOverrides"/>: <c>hud_icon</c> / <c>hud_slide</c> /
/// <c>hud_hold</c>), folded into the HUD document after the guest's statements each feed, as Fable 2's
/// GameUi folds them into its HUD. One document carries both players: player one's d-pad is
/// <c>dpad</c>, player two's <c>dpad2</c> (<see cref="Fable3HudRegistry.DpadRoot"/>) (the slot writers pick the root by the d-pad's
/// +0x2D byte: TU3 0x826039C0 / disc 0x825D89C8), so a player's override never reaches the other's twin.
/// The d-pad's toggle names (<c>DPadUp</c>..) name the slot's icon element the game's own writer textures
/// (<c>&lt;root&gt;.dpad.dpadmenu.group.&lt;dir&gt;.&lt;dir&gt;</c>, GuiElement_SetTexture at TU3 0x82603A34 /
/// disc 0x825D8A3C) for an icon, and the toggle element for a slide — whose on/off slides Fable 3 authors
/// identical, so a toggle slide shows nothing. The d-pad stands closed (<c>dpadmenu</c> slide 1) until the
/// game opens it: <c>hud_hold(p, "dpad")</c> keeps it open.
/// </summary>
public sealed partial class Fable3HudDocument
{
	private static readonly Regex OtherThanPlayerTwo = new(@"(^|\.)dpad2(\.|$)", RegexOptions.Compiled);
	// Player one's root at the top of a path (with or without an authored Layer above it); player two's
	// root `dpad2` has a child named `dpad` too, so only the top segment tells them apart.
	private static readonly Regex PlayerOneRoot = new(@"^(Layer\.)?dpad(\.|$)", RegexOptions.Compiled);

	private string[]? _overridePaths;
	private int _overridesFed = -1;
	private readonly HashSet<string> _saidIcons = new(System.StringComparer.Ordinal);
	private readonly Dictionary<string, uint> _overrideArt = new(System.StringComparer.Ordinal);
	private readonly Engine_UiHudHold?[] _holds = new Engine_UiHudHold?[2];
	private readonly string[] _holdKeys = { string.Empty, string.Empty };
	private bool[] _holdActive = System.Array.Empty<bool>();
	private int[] _holdBefore = System.Array.Empty<int>();

	/// <summary>Whether the overrides changed since the last feed (the frame re-feeds then).</summary>
	private bool OverridesChanged => Document == Fable3Elements.HudDocument && _overridesFed != Engine_UiHudOverrides.Generation;

	private static bool Keeps(int player, string path) =>
		player == 2 ? !PlayerOneRoot.IsMatch(path) : !OtherThanPlayerTwo.IsMatch(path);

	private static string? DpadDirection(string element) => element.ToLowerInvariant() switch
	{
		"dpadup" => "up",
		"dpadleft" => "left",
		"dpaddown" => "down",
		"dpadright" => "right",
		_ => null,
	};

	/// <summary>The ordinals an override names for a player: a d-pad toggle's element, else every path it matches.</summary>
	private List<int> OverrideOrdinals(int player, string element, bool icon)
	{
		var found = new List<int>();
		string[] paths = _overridePaths ??= PathsOf(_screen!);
		string? direction = DpadDirection(element);
		// The document's own paths carry the authored Layer above the roots (Layer.dpad.dpad.dpadmenu..).
		string root = "Layer." + Fable3HudRegistry.DpadRoot(player - 1);
		string? exact = direction is null ? null : $"{root}.dpad.dpadmenu.group.{direction}" + (icon ? $".{direction}" : string.Empty);
		for (int i = 0; i < paths.Length; ++i)
		{
			string path = paths[i];
			bool match = exact is not null
				? string.Equals(path, exact, System.StringComparison.OrdinalIgnoreCase)
				: Engine_UiHudOverrides.MatchesPath(path, element) && Keeps(player, path);
			if (match)
			{
				found.Add(i);
			}
		}

		return found;
	}

	/// <summary>A texture an override names (a mod's PNG or a game texture), as a host texture key; 0 when it does not load.</summary>
	private uint OverrideArt(string icon)
	{
		if (!_overrideArt.TryGetValue(icon, out uint key))
		{
			key = Fable3IconTexture(icon) is { } texture ? Engine_UiHostTextures.Register(texture) : 0u;
			if (key == 0u)
			{
				GD.PushWarning($"[fable3-hud] hud_icon: '{icon}' did not load");
			}

			_overrideArt[icon] = key;
		}

		return key;
	}

	/// <summary>
	/// A mod's picture on Fable 3: an enabled mod's PNG, else a game texture through Fable 3's own bank loader
	/// (<see cref="Fable3Art.Load"/>), a marker icon name (<c>quest</c>, <c>gold</c>…) naming this game's art.
	/// </summary>
	internal static Texture2D? Fable3IconTexture(string icon) =>
		Engine_HudModMarkers.LoadIconWith(Engine_HudModMarkers.IconPath(icon), Fable3Art.Load);

	/// <summary>The overrides over this feed's statements, before the playback enters the stated slides.</summary>
	private void ApplyModOverrides()
	{
		if (Document != Fable3Elements.HudDocument)
		{
			return;
		}

		_overridesFed = Engine_UiHudOverrides.Generation;
		if (Engine_UiHudOverrides.Count == 0)
		{
			_holds[0] = _holds[1] = null;
			_holdKeys[0] = _holdKeys[1] = string.Empty;
			return;
		}

		for (int player = 1; player <= 2; ++player)
		{
			foreach ((string element, string texture) in Engine_UiHudOverrides.IconsFor(player))
			{
				uint art = OverrideArt(texture);
				if (art == 0u)
				{
					continue;
				}

				List<int> ordinals = OverrideOrdinals(player, element, icon: true);
				foreach (int ordinal in ordinals)
				{
					_textures[ordinal] = art;
				}

				string said = $"{player}:{element}:{ordinals.Count}";
				if (_saidIcons.Add(said))
				{
					string[] paths = _overridePaths ?? System.Array.Empty<string>();
					GD.Print($"[fable3-hud] hud_icon {player} '{element}' -> {ordinals.Count} element(s)" +
						(ordinals.Count == 0 ? string.Empty : ": " + string.Join(", ", ordinals.ConvertAll(o =>
							$"{paths[o]} (opacity {(_opacity[o]?.ToString() ?? "-")}, slide {_slides[o]})"))));
				}
			}

			foreach ((string element, int slide) in Engine_UiHudOverrides.SlidesFor(player))
			{
				foreach (int ordinal in OverrideOrdinals(player, element, icon: false))
				{
					_slides[ordinal] = slide;
					_slideStamps[ordinal] = 0;
				}
			}

			List<string> pieces = Engine_UiHudOverrides.HoldsFor(player);
			string key = string.Join('\n', pieces);
			if (key != _holdKeys[player - 1])
			{
				_holdKeys[player - 1] = key;
				int p = player;
				_holds[player - 1] = pieces.Count == 0 ? null : new Engine_UiHudHold(_screen!, pieces, path => Keeps(p, path));
			}

			if (_holds[player - 1] is { } hold)
			{
				if (_holdActive.Length != _slides.Length)
				{
					_holdActive = new bool[_slides.Length];
					_holdBefore = new int[_slides.Length];
				}

				System.Array.Copy(_slides, _holdBefore, _slides.Length);
				hold.Apply(_slides, _holdActive, _opacity);
				for (int i = 0; i < _slides.Length; ++i)
				{
					// A held owner keeps its slide: a newer guest stamp must not re-enter the closing one.
					if (_slides[i] != _holdBefore[i])
					{
						_slideStamps[i] = 0;
					}
				}
			}
		}
	}
}
