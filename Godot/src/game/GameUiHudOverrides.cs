using System.Collections.Generic;
using Godot;
using Refii.Host;
using Refii.Ui;

namespace Refii.Game;

/// <summary>
/// THE MODS' HUD OVERRIDES APPLIED (<see cref="Engine_UiHudOverrides"/>): the icons a mod states
/// onto named elements, the slides it puts them on and the pieces it holds visible, folded into
/// player one's HUD feed where the guest's own slides and textures are folded, and into player two's
/// half where their d-pad is driven. An element is a slide-toggle binding name or an element path;
/// an icon is a game texture or an enabled mod's PNG, fitted to the slot like the game's own.
/// </summary>
public partial class GameUi
{
	/// <summary>The override generation the HUD textures were last sampled under.</summary>
	private int _hudOverrideGeneration = -1;

	private readonly Dictionary<string, uint> _hudOverrideTextures = new(System.StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, int[]> _hudOverrideOrdinals = new(System.StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, int[]> _hudOverrideToggles = new(System.StringComparer.OrdinalIgnoreCase);
	private Engine_UiScreen? _hudOverrideScreen;
	private List<(string Path, UiNode Node)>? _hudOverridePaths;

	private Engine_UiHudHold? _hudHold;
	private string _hudHoldKey = string.Empty;
	private Engine_UiHudHold? _hudHoldTwo;
	private string _hudHoldTwoKey = string.Empty;

	/// <summary>Player two's override slides over the shared ones (ordinal → slide), beside their d-pad's.</summary>
	private readonly Dictionary<int, int> _hudTwoOverrideSlides = new();

	private readonly HashSet<int> _hudTwoOverrideTextureOrdinals = new();

	/// <summary>Whose HUD the shared one is: player two's on an online joiner, else player one's.</summary>
	private static int HudLocalPlayer => CoopSession.IsClient ? 2 : 1;

	/// <summary>
	/// The quads an element paints, by its name: the slide-toggle binding of that name (the element it
	/// binds, or the leaf quads beneath it), else the element path matched by its trailing segments.
	/// </summary>
	private int[] HudOverrideOrdinals(Engine_UiScreen screen, Engine_UiBindings? bindings, string element)
	{
		ResetHudOverrideCaches(screen);
		if (_hudOverrideOrdinals.TryGetValue(element, out int[]? known))
		{
			return known;
		}

		var ordinals = new List<int>();
		foreach (int ordinal in HudOverrideToggleOrdinals(screen, bindings, element))
		{
			if (ordinal < 0 || ordinal >= screen.Nodes.Length)
			{
				continue;
			}

			UiNode node = screen.Nodes[ordinal];
			if (node.Quad is not null)
			{
				ordinals.Add(ordinal);
			}
			else
			{
				CollectLeafQuads(node, ordinals);
			}
		}

		GD.Print($"[hud-overrides] element '{element}' → {(ordinals.Count == 0 ? "not found" : string.Join(",", ordinals))}");
		return _hudOverrideOrdinals[element] = ordinals.ToArray();
	}

	/// <summary>The element(s) a name addresses for a slide: the binding's elements, else the path's node.</summary>
	private int[] HudOverrideToggleOrdinals(Engine_UiScreen screen, Engine_UiBindings? bindings, string element)
	{
		ResetHudOverrideCaches(screen);
		if (_hudOverrideToggles.TryGetValue(element, out int[]? known))
		{
			return known;
		}

		var ordinals = new List<int>();
		if (bindings is not null)
		{
			ordinals.AddRange(bindings.Elements(Engine_UiBindings.Kind.SlideToggle, element));
		}

		if (ordinals.Count == 0)
		{
			_hudOverridePaths ??= Engine_UiHudHold.Paths(screen);
			foreach ((string path, UiNode node) in _hudOverridePaths)
			{
				if (Engine_UiHudOverrides.MatchesPath(path, element))
				{
					ordinals.Add(node.Ordinal);
				}
			}
		}

		return _hudOverrideToggles[element] = ordinals.ToArray();
	}

	private void ResetHudOverrideCaches(Engine_UiScreen screen)
	{
		if (ReferenceEquals(_hudOverrideScreen, screen))
		{
			return;
		}

		_hudOverrideScreen = screen;
		_hudOverridePaths = null;
		_hudOverrideOrdinals.Clear();
		_hudOverrideToggles.Clear();
	}

	/// <summary>
	/// A stated icon as a host texture key: the art by what the call named (a game texture path or an
	/// enabled mod's PNG, as markers resolve theirs), fitted into the slot when it is not the slot's
	/// size — 84% on its longer side, centred, transparent around — and registered once.
	/// </summary>
	private uint HudOverrideTexture(string icon)
	{
		if (_hudOverrideTextures.TryGetValue(icon, out uint cached))
		{
			return cached;
		}

		uint hash = 0;
		if (LoadMarkerIcon(icon) is { } texture)
		{
			if (Engine_UiHudOverrides.FitsSlot(texture.GetWidth(), texture.GetHeight()))
			{
				hash = Engine_UiHostTextures.Register(texture);
			}
			else
			{
				using Image source = Refii.Core.Engine_ServerSyncs.Image(texture)!;
				if (source is not null && !source.IsEmpty() && (!source.IsCompressed() || source.Decompress() == Error.Ok))
				{
					source.Convert(Image.Format.Rgba8);
					var fit = Engine_UiHudOverrides.FitIcon(source.GetWidth(), source.GetHeight());
					if (fit.Width != 0)
					{
						source.Resize(fit.Width, fit.Height, Image.Interpolation.Lanczos);
						using Image padded = Image.CreateEmpty(Engine_UiHudOverrides.IconSize, Engine_UiHudOverrides.IconSize, false, Image.Format.Rgba8);
						padded.Fill(Colors.Transparent);
						padded.BlitRect(source, new Rect2I(0, 0, fit.Width, fit.Height), new Vector2I(fit.X, fit.Y));
						padded.GenerateMipmaps();
						hash = Engine_UiHostTextures.Register(ImageTexture.CreateFromImage(padded));
					}
				}
			}
		}

		if (hash == 0)
		{
			GD.PushWarning($"[hud-overrides] icon '{icon}' loads nothing; the element keeps its own art");
		}

		_hudOverrideTextures[icon] = hash;
		return hash;
	}

	/// <summary>Player one's stated icons onto the sampled HUD textures.</summary>
	private void ApplyHudIconOverrides(uint[] textures, int player)
	{
		_hudOverrideGeneration = Engine_UiHudOverrides.Generation;
		if (_hudScreen is not { } screen)
		{
			return;
		}

		foreach ((string element, string icon) in Engine_UiHudOverrides.IconsFor(player))
		{
			uint texture = HudOverrideTexture(icon);
			if (texture == 0)
			{
				continue;
			}

			foreach (int ordinal in HudOverrideOrdinals(screen, _hud?.Bindings, element))
			{
				if (ordinal >= 0 && ordinal < textures.Length)
				{
					textures[ordinal] = texture;
				}
			}
		}
	}

	/// <summary>Player one's stated slides over the folded ones.</summary>
	private void ApplyHudSlideOverrides(int[] slides, int player)
	{
		if (_hudScreen is not { } screen)
		{
			return;
		}

		foreach ((string element, int slide) in Engine_UiHudOverrides.SlidesFor(player))
		{
			foreach (int ordinal in HudOverrideToggleOrdinals(screen, _hud?.Bindings, element))
			{
				if (ordinal >= 0 && ordinal < slides.Length)
				{
					slides[ordinal] = slide;
				}
			}
		}
	}

	/// <summary>
	/// The pieces a mod holds visible for <paramref name="player"/>, folded into one resolve pass; the
	/// hold is rebuilt when the document or the set of pieces changes. Returns the hold standing, or
	/// null for none.
	/// </summary>
	private Engine_UiHudHold? ApplyHudHold(ref Engine_UiHudHold? hold, ref string key, Engine_UiScreen doc,
		int[] slides, bool[] active, float?[] opacity, int player)
	{
		List<string> pieces = Engine_UiHudOverrides.HoldsFor(player);
		if (pieces.Count == 0)
		{
			hold = null;
			key = string.Empty;
			return null;
		}

		string wanted = string.Join('\n', pieces);
		if (hold is null || !ReferenceEquals(hold.Screen, doc) || key != wanted)
		{
			hold = new Engine_UiHudHold(doc, pieces);
			key = wanted;
			GD.Print($"[hud-overrides] player {player} holds {pieces.Count} piece(s): {hold.OwnerCount} slide owner(s)");
		}

		hold.Apply(slides, active, opacity);
		return hold;
	}

	/// <summary>Player two's stated icons and slides, into their half's own tables (beside their d-pad's).</summary>
	private void ApplyHudOverridesTwo(Engine_UiBindings bindings)
	{
		foreach (int ordinal in _hudTwoOverrideTextureOrdinals)
		{
			_hudTwoTextures.Remove(ordinal);
		}

		_hudTwoOverrideTextureOrdinals.Clear();
		_hudTwoOverrideSlides.Clear();
		if (_hudScreen is not { } screen || !Engine_UiHudOverrides.AnyFor(2))
		{
			return;
		}

		foreach ((string element, string icon) in Engine_UiHudOverrides.IconsFor(2))
		{
			uint texture = HudOverrideTexture(icon);
			if (texture == 0)
			{
				continue;
			}

			foreach (int ordinal in HudOverrideOrdinals(screen, bindings, element))
			{
				_hudTwoTextures[ordinal] = texture;
				_hudTwoOverrideTextureOrdinals.Add(ordinal);
			}
		}

		foreach ((string element, int slide) in Engine_UiHudOverrides.SlidesFor(2))
		{
			foreach (int ordinal in HudOverrideToggleOrdinals(screen, bindings, element))
			{
				_hudTwoOverrideSlides[ordinal] = slide;
			}
		}
	}
}
