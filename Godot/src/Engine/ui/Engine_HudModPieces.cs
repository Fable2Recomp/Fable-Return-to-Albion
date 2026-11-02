using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Ui;

/// <summary>
/// HUD pieces a MOD defines, as opposed to the pieces cut from <c>hud.bgf</c>: meters, panels and
/// pictures, placed by the same anchor rule (<see cref="Engine_HudLayout.Place"/>) and fed by
/// values the game or a mod's hook states.
/// </summary>
/// <remarks>
/// <para>A mod ships <c>ui/hud.json</c>:</para>
/// <code>
/// { "hide": ["group.PlayerHealth.PlayerHealth"],
///   "pieces": [
///   { "id": "souls.hp", "kind": "meter", "anchor": [0, 0], "offset": [96, 40], "size": [0, 10],
///     "value": "hero:health", "max": "hero:health_max",
///     "widthFrom": "hero:health_max", "widthPerUnit": 0.6, "minWidth": 120, "maxWidth": 560,
///     "fill": "#9e1b1b", "back": "#000000b0", "chip": "#d8c27a",
///     "border": "#8a8478", "borderWidth": 1.5, "corner": 1.5 } ] }
/// </code>
/// <list type="bullet">
/// <item><c>hide</c> — ids of SHIPPED pieces the mod replaces; they are not built.</item>
/// <item><c>kind</c> — <c>meter</c> (back, a trailing chip, fill), <c>panel</c> (back and
/// border only — a frame or an emblem plate), <c>picture</c> (a texture) or <c>text</c> (a
/// line drawn with the HUD's own fonts through the theme's Paint path).</item>
/// <item>A <c>text</c> piece's <c>text</c> is a literal, a Babel tag or <c>mod:&lt;key&gt;</c>
/// drawn through <c>format</c> (<see cref="Engine_HudModText"/>); <c>size</c> is its POINT size
/// (a number, not the [w, h] the other kinds take), <c>colour</c> its HTML colour and
/// <c>align</c> left, centre or right of the piece's point. It hangs from the point by its top
/// edge, placed like every other piece (anchor and offset, or follow).</item>
/// <item><c>value</c> / <c>max</c> — a number or a value source. <c>max</c> may itself be a
/// source.</item>
/// <item><c>widthFrom</c> — a bar whose LENGTH is a value (the pool's maximum), times
/// <c>widthPerUnit</c>, clamped to <c>minWidth..maxWidth</c>, in design units. Otherwise the
/// <c>size</c> width.</item>
/// <item><c>chip</c> — the recently-lost part of the bar, held briefly and then drained.</item>
/// <item><c>follow</c> / <c>followElement</c> — hang beneath a shipped piece's element as it is
/// actually drawn, every frame; <c>offset</c> is then from its bottom-left corner and a width
/// of 0 takes the element's width. The first drawn of a comma list wins. A follower is the
/// followed piece's player's: in a view drawn for player two (their half, or an online joiner's
/// window) one following player two's widget hangs beneath player one's widget keyed to player
/// two, and one following player one's is not drawn. A piece with no <c>follow</c> is the
/// screen's and stays out of player two's half.</item>
/// <item><c>textures</c> + <c>index</c> — a picture that shows ONE of a list, chosen by a value
/// source: the value's integer part is the index; hidden while the source states nothing or an
/// index outside the list (-1 is the usual "none"). Spell Weaving's partner glyph beside the
/// charge meter is <c>{ "kind": "picture", "follow": "group.SpellCharging.SpellCharging",
/// "textures": ["game://art/gui/wheelicons/abilities/will_shock.tex", ...],
/// "index": "mod:spell_weaving.hud_partner" }</c>.</item>
/// <item>A picture's texture is any of <see cref="LoadAnyTexture"/>'s paths: a game texture
/// (<c>art/gui/…/x.tex</c>, with or without <c>game://</c>), an enabled mod's PNG
/// (<c>art/x.png</c>) or a project resource (<c>res://</c>). A picture may state none and be
/// FILLED BY A CALL: <c>hud_icon(p, "&lt;piece id&gt;", texture)</c> on any surface states the
/// texture that player's copy of the piece shows (empty clears; it wins over <c>texture</c> and
/// <c>textures</c>); with nothing stated and no texture of its own the picture is not drawn.</item>
/// <item><c>region</c> — <c>[u, v, w, h]</c>, fractions of the texture (0..1, from its top-left):
/// the picture shows that part of it, a piece of an atlas at any of its resolutions.</item>
/// <item><c>tint</c> — an HTML colour (with alpha) a picture's texture is multiplied by: a
/// darkened glass, a dimmed icon, a gold ring.</item>
/// <item>Pieces draw in file order (composed load order): a later piece over an earlier one.</item>
/// <item><c>each</c> — <c>true</c>: a piece with no <c>follow</c> is EACH PLAYER'S — built in
/// player two's half too (their view, or an online joiner's window), reading player two's values
/// and player two's <c>hud_icon</c> textures, placed in that half by the same anchor rule.</item>
/// <item><c>group</c> — the id of a file-level <c>groups</c> record
/// (<see cref="Engine_HudModGroups"/>): the piece, placed as above, is then scaled about the
/// group's pivot and offset by the group's values, read each frame for the piece's player.</item>
/// <item><c>outline</c> / <c>outlineWidth</c>, <c>shadow</c> / <c>shadowOffset</c> — a text
/// piece's ring and drop shadow: the same glyphs drawn again behind it in that colour, offset by
/// the width all round (8 copies) or by the shadow's <c>[x, y]</c>, in the piece's own units.</item>
/// </list>
/// <para>Value sources (<see cref="Engine_UiValueSources"/>, the one resolver every UI record
/// reads through): a number; <c>mod:&lt;mod&gt;.&lt;name&gt;</c>, what a mod published;
/// <c>game:&lt;name&gt;</c>, what the guest published for its own HUD (a meter 0..100, or a text
/// field for a <c>text</c> piece — <c>game:QuestTitle</c>); <c>player:&lt;key&gt;</c>, the player
/// store read for the PIECE'S player (a piece in player two's view, or following player two's
/// widget, reads player two's); <c>hero:health</c> / <c>hero:health_max</c>, the local hero's. A
/// piece whose value is not stated is not drawn — "not stated" must not render as empty or full.</para>
/// <para>Pieces compose by id across mods in load order; a higher-priority mod's piece replaces
/// a lower one's. Made of Godot primitives, so a mod needs no art of its own. Children of the
/// HUD view, so they show and hide with it.</para>
/// </remarks>
[Tool]
public sealed partial class Engine_HudModPieces : Node3D
{
	/// <summary>Where a mod ships its HUD pieces, inside its package.</summary>
	public const string ModHudPath = "ui/hud.json";

	public enum Kind
	{
		Meter,
		Panel,
		Picture,
		Text,
	}

	public sealed record Piece(
		string Id,
		string Mod,
		Kind Kind,
		Engine_HudLayout.Anchor Anchor,
		Vector2 Offset,
		Vector2 Size,
		string Value,
		string Max,
		Color Fill,
		Color Back,
		string Texture,
		string Follow = "",
		string FollowElement = "",
		string WidthFrom = "",
		float WidthPerUnit = 1f,
		float MinWidth = 0f,
		float MaxWidth = 100000f,
		Color? Chip = null,
		Color? Border = null,
		float BorderWidth = 0f,
		float Corner = -1f,
		List<string>? Textures = null,
		string Index = "",
		string Text = "",
		string Format = "",
		int TextSize = Engine_HudModText.DefaultSize,
		Color? TextColour = null,
		int Align = 0,
		Rect2? Region = null,
		bool Each = false,
		Color? Tint = null,
		string Group = "",
		Color? Outline = null,
		float OutlineWidth = 0f,
		Color? Shadow = null,
		Vector2 ShadowOffset = default);

	/// <summary>
	/// A COPY of a shipped piece — the guest's own widget, built again beside the original, so it
	/// moves, fades and hides exactly as the original does. <see cref="Values"/> feeds the nested
	/// meters it names from mod values instead of the guest's (meter name → <c>source/cap</c>,
	/// drawn as <c>source / cap × 100</c>, the 0..100 a meter takes; <c>"none"</c> holds one still);
	/// every meter it does not name stays the guest's. <see cref="Hue"/> rotates the colour of the named elements' art (red health to blue
	/// or green), and <see cref="IconTextures"/> replaces one element's picture — the first that
	/// loads. <see cref="Bars"/> names the elements a meter FILLS (element → meter name): a bar's
	/// fill is the guest's stated scale on that element, not a playhead, so the copy redraws it at
	/// the meter's own value. A copy is its original's player's: a copy of <c>CoopHealth</c> is
	/// player two's, and in a view drawn for player two it is built from player one's widget
	/// keyed to player two (its meters read under player two's names), so it looks exactly like
	/// the same copy of <c>PlayerHealth</c> does for player one. A copy of player one's widget whose
	/// meters read <c>player:</c> values is each player's: one record draws beside both players'
	/// widgets (<see cref="Engine_HudModTwins"/>). <see cref="Slides"/> (hud.json <c>"slides"</c>:
	/// element name → slide number as the game counts them, 1 past the master) puts an element of
	/// the copy — its root, or one beneath it — on a slide of the copy's choosing: a fader the game
	/// never raises, raised for the copy ALONE (<c>Engine_HudView.ResolvedFor</c>); the original's
	/// element keeps the frame's state. A copy lit this way is how a dark shipped piece (the
	/// GenericMeter, the CrucibleMeter) is used as a mod's own.
	/// </summary>
	public sealed record Clone(
		string Id,
		string Mod,
		string Of,
		Vector2 Offset,
		float Hue,
		List<string> HueElements,
		string IconElement,
		List<string> IconTextures,
		Dictionary<string, string> Values,
		Dictionary<string, string> Bars,
		Dictionary<string, int>? Slides = null);

	/// <summary>What every enabled mod's <c>ui/hud.json</c> states, composed.</summary>
	public sealed record ModLayout(
		List<Piece> Pieces, HashSet<string> Hidden, List<Clone> Clones, Dictionary<string, float> Scales,
		Dictionary<string, Engine_HudModGroups.Group>? Groups = null);

	/// <summary>The drawn node of a shipped HUD piece by id, supplied by the HUD view.</summary>
	public System.Func<string, Node3D?>? FindPiece { get; set; }

	/// <summary>The groups a piece's <c>group</c> names (<see cref="ModLayout.Groups"/>); null for none.</summary>
	public IReadOnlyDictionary<string, Engine_HudModGroups.Group>? Groups { get; set; }

	/// <summary>Each group's values this frame, by group and player: read once per frame, not per member.</summary>
	private readonly Dictionary<(string Group, int Player), Engine_HudModGroups.Transform> _groupsNow = new();

	/// <summary>A stand-in value for every piece, for an editor preview with no game running.</summary>
	public float? PreviewValue
	{
		get => _previewValue;
		set
		{
			_previewValue = value;
			_contexts[0] = _contexts[1] = null;
		}
	}

	private float? _previewValue;

	/// <summary>
	/// A stand-in for one value source by its whole text (<c>"mod:souls.stamina"</c>,
	/// <c>"player:souls.stamina"</c>, <c>"game:PlayerHealth"</c>), for an editor preview; a source it
	/// answers null for falls to <see cref="PreviewValue"/> (a <c>game:</c> one to the view's own
	/// stand-in meters).
	/// </summary>
	public System.Func<string, float?>? PreviewSource
	{
		get => _previewSource;
		set
		{
			_previewSource = value;
			_contexts[0] = _contexts[1] = null;
		}
	}

	private System.Func<string, float?>? _previewSource;

	/// <summary>The owning view's value readers (<see cref="Engine_HudView.Sources"/>); null reads mod values and the hero only.</summary>
	public Engine_UiValueSources.Context? Sources
	{
		get => _sources;
		set
		{
			_sources = value;
			_contexts[0] = _contexts[1] = null;
		}
	}

	private Engine_UiValueSources.Context? _sources;

	/// <summary>The owning view is drawn for player two: every piece in it reads player two's values.</summary>
	public bool ForPlayerTwo
	{
		get => _forPlayerTwo;
		set
		{
			_forPlayerTwo = value;
			_contexts[0] = _contexts[1] = null;
		}
	}

	private bool _forPlayerTwo;

	/// <summary>Per player, the readers with the preview's stand-ins over them; rebuilt when <see cref="Sources"/> changes.</summary>
	private readonly Engine_UiValueSources.Context?[] _contexts = new Engine_UiValueSources.Context?[2];

	/// <summary>The readers without a view: published mod values, mod settings and the local hero.</summary>
	private static readonly Engine_UiValueSources.Context Standalone = new()
	{
		Mod = key => !Engine.IsEditorHint() && HostBridge.TryGetModValue(key, out float value) ? value : null,
		Setting = key => Refii.Mods.ModSettings.TryGet(key, out float value) ? value : null,
		Hero = key =>
		{
			if (Engine.IsEditorHint())
			{
				return null;
			}

			HostBridge.HeroVitals vitals = HostBridge.GetHeroVitals();
			return !vitals.Known || vitals.MaxHealth <= 0f ? null : key == "health" ? vitals.Health : vitals.MaxHealth;
		},
	};

	/// <summary>
	/// The readers for one piece: the piece's player is the view's, or player two's when the piece
	/// follows player two's widget in a shared view (<see cref="Engine_UiValueSources.PlayerFor"/>).
	/// </summary>
	private Engine_UiValueSources.Context ContextFor(Piece piece)
	{
		bool followsTwo = piece.Follow.Length != 0 && Engine_HudLayout.OwnerOf(
			new Engine_HudLayout.Piece(piece.Follow.Split(',')[0].Trim(), string.Empty, default, default)) ==
			Engine_UiSeats.Seat.PlayerTwo;
		int player = Engine_UiValueSources.PlayerFor(ForPlayerTwo, followsTwo);
		return _contexts[player - 1] ??= (Sources ?? Standalone).ForPlayer(player) with
		{
			StandIn = PreviewSource is null && PreviewValue is null ? (Sources ?? Standalone).StandIn : PreviewStand,
		};
	}

	/// <summary>The preview's stand-in for a source: the Studio's own, else the one stand-in for every non-game value.</summary>
	private float? PreviewStand(string source)
	{
		if (PreviewSource?.Invoke(source) is { } stated)
		{
			return stated;
		}

		if (PreviewValue is { } preview && !source.StartsWith(Engine_UiValueSources.GamePrefix, System.StringComparison.Ordinal))
		{
			return source.EndsWith("_max", System.StringComparison.Ordinal) ? 100f : preview * 100f;
		}

		return null;
	}

	/// <summary>The shipped look — fonts and the Paint path — that a <c>text</c> piece draws with.</summary>
	public Engine_UiTheme? Theme { get; set; }

	/// <summary>The string book a <c>text</c> piece's tag resolves through.</summary>
	public Refii.Text.Engine_BabelDatabase? StringBook { get; set; }

	/// <summary>A text piece's colour when it states none — the HUD text's own fallback.</summary>
	private static readonly Color DefaultTextColour = new(1f, 224f / 255f, 128f / 255f, 1f);

	/// <summary>The window the anchors resolve against; fed by the owner as for the HUD view.</summary>
	public Vector2 Window { get; set; } = new(1280f, 720f);

	private const float Depth = 0.45f;

	/// <summary>
	/// The sort distance each piece adds over the one before it, so overlapping pieces at the same
	/// depth draw in file order; wider than a meter's own chip and fill steps.
	/// </summary>
	private const float LayerStep = 0.01f;

	/// <summary>How long a lost chunk is held before it drains, and how fast it drains.</summary>
	private const float ChipHoldSeconds = 0.6f;
	private const float ChipDrainPerSecond = 0.6f;

	private sealed class Built
	{
		public required Piece Piece;
		public Engine_UiRoundedPanel? Back;
		public Engine_UiRoundedPanel? Chip;
		public Engine_UiRoundedPanel? Fill;
		public MeshInstance3D? Picture;
		public MeshInstance3D? Label;

		/// <summary>A text piece's outline and shadow copies, children of <see cref="Label"/>, drawn with its glyphs.</summary>
		public MeshInstance3D[]? Rims;
		public string? ShownText;
		/// <summary>The picture's choices, loaded once; a null entry is a path that did not load.</summary>
		public List<Texture2D?>? Textures;
		public int Shown = -2;
		public float ChipFraction = -1f;
		public float ChipHeld;

		/// <summary>The piece's followed element names, split once.</summary>
		public string[]? FollowNames;

		/// <summary>The followed root the nodes below were resolved under.</summary>
		public Node3D? FollowRoot;

		/// <summary>The resolved element node per followed name; null where the root carries none.</summary>
		public Node3D?[]? FollowNodes;

		/// <summary>The picture's own texture (<c>texture</c>), or null when it states none.</summary>
		public Texture2D? Own;

		/// <summary>The <see cref="Engine_UiHudOverrides.Generation"/> and player the stated texture was read under.</summary>
		public int StatedGeneration = -1;
		public int StatedPlayer;
		public Texture2D? Stated;

		/// <summary>The texture on the material now.</summary>
		public Texture2D? OnMaterial;
	}

	private readonly List<Built> _built = new();

	public IReadOnlyList<Piece> Pieces { get; private set; } = new List<Piece>();

	/// <summary>Each built piece's drawn parts, by id — for an editor's hit test in its preview.</summary>
	public IEnumerable<(string Id, Node3D Node)> DrawnNodes()
	{
		foreach (Built built in _built)
		{
			foreach (Node3D? node in new Node3D?[] { built.Back, built.Chip, built.Fill, built.Picture, built.Label })
			{
				if (node is not null && GodotObject.IsInstanceValid(node) && node.IsVisibleInTree())
				{
					yield return (built.Piece.Id, node);
				}
			}
		}
	}

	public void Build(IReadOnlyList<Piece> pieces)
	{
		foreach (Built built in _built)
		{
			built.Back?.QueueFree();
			built.Chip?.QueueFree();
			built.Fill?.QueueFree();
			built.Picture?.QueueFree();
			built.Label?.QueueFree();
		}

		_built.Clear();
		Pieces = pieces;
		foreach (Piece piece in pieces)
		{
			var built = new Built { Piece = piece };
			string name = piece.Id.Replace('.', '_');
			if (piece.Kind == Kind.Text)
			{
				built.Label = new MeshInstance3D
				{
					Name = name,
					CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				};
				AddChild(built.Label);
				built.Rims = Rims(built.Label, piece);
			}
			else if (piece.Kind == Kind.Picture)
			{
				Texture2D? texture = null;
				if (piece.Textures is { Count: > 0 } choices)
				{
					built.Textures = new List<Texture2D?>(choices.Count);
					foreach (string path in choices)
					{
						Texture2D? loaded = LoadTexture(path);
						if (loaded is null)
						{
							GD.PushWarning($"[hud-mods] '{piece.Id}' ({piece.Mod}): no texture at '{path}'");
						}

						built.Textures.Add(loaded);
						texture ??= loaded;
					}
				}
				else if (piece.Texture.Length != 0)
				{
					texture = LoadTexture(piece.Texture);
					built.Own = texture;
					if (texture is null)
					{
						GD.PushWarning($"[hud-mods] '{piece.Id}' ({piece.Mod}): no texture at '{piece.Texture}'");
					}
				}

				var material = new StandardMaterial3D
				{
					AlbedoTexture = texture,
					AlbedoColor = piece.Tint ?? Colors.White,
					ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
					Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				};
				if (piece.Region is { } region)
				{
					// The quad's UV is 0..1 from the top-left: scaled to the region's size and moved to its corner.
					material.Uv1Scale = new Vector3(region.Size.X, region.Size.Y, 1f);
					material.Uv1Offset = new Vector3(region.Position.X, region.Position.Y, 0f);
				}

				built.OnMaterial = texture;
				built.Picture = new MeshInstance3D
				{
					Name = name,
					Mesh = new QuadMesh { Size = piece.Size },
					MaterialOverride = material,
					CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				};
				AddChild(built.Picture);
			}
			else
			{
				built.Back = Panel(name + "_back", piece, piece.Back, piece.Border, piece.BorderWidth);
				if (piece.Kind == Kind.Meter)
				{
					if (piece.Chip is { } chip)
					{
						built.Chip = Panel(name + "_chip", piece, chip, null, 0f);
					}

					built.Fill = Panel(name + "_fill", piece, piece.Fill, null, 0f);
				}
			}

			float layer = _built.Count * LayerStep;
			foreach (GeometryInstance3D? part in new GeometryInstance3D?[] { built.Back, built.Chip, built.Fill, built.Picture, built.Label })
			{
				if (part is not null)
				{
					part.SortingOffset = layer;
				}
			}

			// A text piece's rims sit under its glyphs and over every piece before it.
			foreach (MeshInstance3D rim in built.Rims ?? System.Array.Empty<MeshInstance3D>())
			{
				rim.SortingOffset = layer - LayerStep * 0.5f;
			}

			if (piece.Group.Length != 0 && Groups?.ContainsKey(piece.Group) != true)
			{
				GD.PushWarning($"[hud-mods] '{piece.Id}' ({piece.Mod}): no group '{piece.Group}'; drawn where it is placed");
			}

			_built.Add(built);
		}

		if (_built.Count != 0)
		{
			GD.Print($"[hud-mods] {_built.Count} piece(s) from mods: " +
				string.Join(", ", _built.ConvertAll(b => $"{b.Piece.Id} ({b.Piece.Mod})")));
		}
	}

	/// <summary>The 8 directions an outline's copies stand in, unit length.</summary>
	private static readonly Vector2[] RimDirections =
	{
		new(1f, 0f), new(-1f, 0f), new(0f, 1f), new(0f, -1f),
		new(0.7071f, 0.7071f), new(-0.7071f, 0.7071f), new(0.7071f, -0.7071f), new(-0.7071f, -0.7071f),
	};

	/// <summary>
	/// A text piece's outline copies (8, at <c>outlineWidth</c> all round) then its shadow copy (at
	/// <c>shadowOffset</c>, +Y down), children of the label in its own units, so they scale and hide
	/// with it; null when it states neither. Their glyphs are the label's, set as it is painted.
	/// </summary>
	private static MeshInstance3D[]? Rims(MeshInstance3D label, Piece piece)
	{
		var rims = new List<MeshInstance3D>();
		if (piece.Outline is not null && piece.OutlineWidth > 0f)
		{
			foreach (Vector2 direction in RimDirections)
			{
				rims.Add(Rim(label, direction * piece.OutlineWidth));
			}
		}

		if (piece.Shadow is not null && piece.ShadowOffset != Vector2.Zero)
		{
			rims.Add(Rim(label, piece.ShadowOffset));
		}

		return rims.Count == 0 ? null : rims.ToArray();
	}

	private static MeshInstance3D Rim(MeshInstance3D label, Vector2 offset)
	{
		var rim = new MeshInstance3D
		{
			Name = "Rim",
			Position = new Vector3(offset.X, -offset.Y, -0.0005f),
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		label.AddChild(rim);
		return rim;
	}

	/// <summary>The label's glyphs on its rims, each in its colour: the outline's, then the shadow's.</summary>
	private static void PaintRims(Built built, MeshInstance3D label)
	{
		if (built.Rims is not { } rims)
		{
			return;
		}

		Piece piece = built.Piece;
		int outlines = piece.Outline is not null && piece.OutlineWidth > 0f ? RimDirections.Length : 0;
		Material? material = null;
		for (int i = 0; i < rims.Length; ++i)
		{
			MeshInstance3D rim = rims[i];
			rim.Mesh = label.Mesh;
			if (label.MaterialOverride is not ShaderMaterial glyphs)
			{
				rim.MaterialOverride = null;
				continue;
			}

			// One material per colour: the label's (its atlas and clip) duplicated and recoloured.
			if (i == 0 || i == outlines)
			{
				rim.MaterialOverride = (Material)glyphs.Duplicate();
				Engine_UiSceneBuilder.SetTextColour(rim, i < outlines ? piece.Outline!.Value : piece.Shadow!.Value);
				material = rim.MaterialOverride;
			}
			else
			{
				rim.MaterialOverride = material;
			}
		}
	}

	private Engine_UiRoundedPanel Panel(string name, Piece piece, Color fill, Color? border, float width)
	{
		var panel = new Engine_UiRoundedPanel { Name = name };
		panel.Style(fill, border ?? new Color(0, 0, 0, 0), border is null ? 0f : width);
		panel.Corner(piece.Corner >= 0f
			? piece.Corner
			: Mathf.Min(piece.Size.Y * 0.5f, Engine_UiRoundedPanel.HudCornerRadius));
		AddChild(panel);
		return panel;
	}

	public override void _Process(double delta)
	{
		Vector2 extent = Engine_HudLayout.CanvasExtent(Window);
		float scale = Engine_HudLayout.Scale(extent);
		_groupsNow.Clear();
		foreach (Built built in _built)
		{
			Piece piece = built.Piece;
			Vector2 size = piece.Size;
			if (piece.WidthFrom.Length != 0)
			{
				if (Resolve(piece.WidthFrom, piece) is not { } widthValue)
				{
					Hide(built);
					continue;
				}

				size.X = Mathf.Clamp(widthValue * piece.WidthPerUnit, piece.MinWidth, piece.MaxWidth);
			}

			size *= scale;
			Vector2 corner;
			if (piece.Follow.Length != 0)
			{
				// Beneath the followed element's drawn rect (canvas space, +Y up): its bottom-left
				// corner, then the authored offset (+Y down).
				if (FollowedRect(built) is not { } followed)
				{
					Hide(built);
					continue;
				}

				if (size.X <= 0f)
				{
					size.X = followed.Size.X;
				}

				corner = new Vector2(followed.Position.X + piece.Offset.X * scale,
					followed.Position.Y - piece.Offset.Y * scale);
			}
			else
			{
				// Place() puts the anchor-plus-offset point in canvas space; the piece hangs from it
				// by its top-left corner, the way a layout is authored (+Y down).
				corner = Engine_HudLayout.Place(
					new Engine_HudLayout.Piece(piece.Id, string.Empty, piece.Anchor, piece.Offset), extent);
			}

			// A grouped piece is moved and sized with its group, about the group's pivot.
			float grown = scale;
			if (piece.Group.Length != 0 && Groups is { } groups && groups.TryGetValue(piece.Group, out Engine_HudModGroups.Group? group))
			{
				Engine_UiValueSources.Context readers = ContextFor(piece);
				if (!_groupsNow.TryGetValue((group.Id, readers.Player), out Engine_HudModGroups.Transform moved))
				{
					moved = Engine_HudModGroups.Resolve(group, readers);
					_groupsNow[(group.Id, readers.Player)] = moved;
				}

				corner = Engine_HudModGroups.Apply(corner, group, moved, extent);
				size *= moved.Scale;
				grown = scale * moved.Scale;
			}

			if (built.Label is { } label)
			{
				if (!DrawText(built, label))
				{
					Hide(built);
					continue;
				}

				label.Scale = new Vector3(grown, grown, 1f);
				label.Position = new Vector3(corner.X, corner.Y, Depth);
				label.Visible = true;
				continue;
			}

			Vector2 centre = corner + new Vector2(size.X * 0.5f, -size.Y * 0.5f);

			if (built.Picture is { } picture)
			{
				// A texture a call stated for this piece's player (hud_icon on the piece's id) wins.
				Texture2D? shown = StatedTexture(built, ContextFor(piece).Player);
				if (shown is null && built.Textures is { } choices)
				{
					int index = Resolve(piece.Index, piece) is { } chosen ? (int)Mathf.Floor(chosen) : -1;
					shown = index >= 0 && index < choices.Count ? choices[index] : null;
				}
				else if (shown is null)
				{
					shown = built.Own;
				}

				if (shown is null)
				{
					Hide(built);
					continue;
				}

				if (!ReferenceEquals(built.OnMaterial, shown) && picture.MaterialOverride is StandardMaterial3D material)
				{
					material.AlbedoTexture = shown;
					built.OnMaterial = shown;
				}

				picture.Scale = new Vector3(grown, grown, 1f);
				picture.Position = new Vector3(centre.X, centre.Y, Depth);
				picture.Visible = true;
				continue;
			}

			if (piece.Kind == Kind.Panel)
			{
				Fit(built.Back!, centre, size, Depth);
				continue;
			}

			float? max = Resolve(piece.Max, piece);
			if (Resolve(piece.Value, piece) is not { } current || max is not { } top)
			{
				Hide(built);
				continue;
			}

			float fraction = top > 0f ? Mathf.Clamp(current / top, 0f, 1f) : 0f;
			Fit(built.Back!, centre, size, Depth);
			Bar(built.Fill!, centre, size, fraction, Depth + 0.002f);

			if (built.Chip is { } chipPanel)
			{
				// The lost part: held where it was, then drained down to the fill.
				if (built.ChipFraction < fraction)
				{
					built.ChipFraction = fraction;
					built.ChipHeld = 0f;
				}
				else if (built.ChipFraction > fraction)
				{
					built.ChipHeld += (float)delta;
					if (built.ChipHeld >= ChipHoldSeconds)
					{
						built.ChipFraction = Mathf.Max(fraction,
							built.ChipFraction - ChipDrainPerSecond * (float)delta);
					}
				}

				Bar(chipPanel, centre, size, built.ChipFraction, Depth + 0.001f);
			}
		}
	}

	/// <summary>
	/// Paints a text piece's string when it changed. False while it states nothing — an unpublished
	/// value — or there is no theme to draw with.
	/// </summary>
	private bool DrawText(Built built, MeshInstance3D label)
	{
		Piece piece = built.Piece;
		string? text = Engine_HudModText.Resolve(piece.Text, piece.Format, ContextFor(piece), Tag);
		if (text is null || Theme is null)
		{
			return false;
		}

		if (text != built.ShownText)
		{
			built.ShownText = text;
			Theme.Paint(label, text, piece.TextSize, piece.TextColour ?? DefaultTextColour,
				horizontalAlign: piece.Align, verticalAlign: 0);
			PaintRims(built, label);
		}

		return true;
	}

	private string? Tag(string tag) =>
		StringBook is { } book && book.TryGetString(tag, out string text) && text.Length != 0 ? text : null;

	/// <summary>A left-aligned bar <paramref name="fraction"/> of the way along the back.</summary>
	private static void Bar(Engine_UiRoundedPanel panel, Vector2 centre, Vector2 size, float fraction, float z)
	{
		Vector2 barSize = new(size.X * fraction, size.Y);
		if (barSize.X < 0.5f)
		{
			panel.Visible = false;
			return;
		}

		Fit(panel, new Vector2(centre.X - (size.X - barSize.X) * 0.5f, centre.Y), barSize, z);
	}

	private static void Hide(Built built)
	{
		// Unrolled: this runs per hidden piece per frame, and an array per call is garbage.
		if (built.Back is { } back) back.Visible = false;
		if (built.Chip is { } chip) chip.Visible = false;
		if (built.Fill is { } fill) fill.Visible = false;
		if (built.Picture is { } picture) picture.Visible = false;
		if (built.Label is { } label) label.Visible = false;
	}

	/// <summary>
	/// The followed element's drawn rect in this node's space — Position is its BOTTOM-left
	/// (canvas +Y is up) — or null when the piece is not built or the element is not drawn.
	/// </summary>
	private Rect2? FollowedRect(Built built)
	{
		Piece piece = built.Piece;
		if (FindPiece?.Invoke(piece.Follow) is not { } root || !root.IsVisibleInTree())
		{
			return null;
		}

		// The followed root is a clone the HUD builds once; the element nodes under it are
		// resolved once per root and held, not searched for per frame. A different root (the
		// HUD rebuilt) drops the lot; a held node that was freed or left the root is searched
		// for again; a name the root does not carry is searched for each frame, as before.
		string[] names = built.FollowNames ??= piece.FollowElement.Split(',',
			System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
		if (!ReferenceEquals(built.FollowRoot, root))
		{
			built.FollowRoot = root;
			built.FollowNodes = new Node3D?[names.Length];
		}

		Node3D?[] nodes = built.FollowNodes!;
		for (int i = 0; i < names.Length; ++i)
		{
			Node3D? element = nodes[i];
			if (element is null || !GodotObject.IsInstanceValid(element) || !element.IsInsideTree() ||
				!root.IsAncestorOf(element))
			{
				element = root.FindChild(names[i], recursive: true, owned: false) as Node3D;
				nodes[i] = element;
			}

			if (element is not null && element.IsVisibleInTree() && DrawnRect(element) is { } rect)
			{
				return rect;
			}
		}

		return names.Length == 0 ? DrawnRect(root) : null;
	}

	/// <summary>The union of every drawn mesh under <paramref name="node"/>, in this node's space.</summary>
	private Rect2? DrawnRect(Node node)
	{
		Rect2? bounds = null;
		if (node is MeshInstance3D mesh && mesh.Mesh is not null && mesh.IsVisibleInTree())
		{
			Aabb box = mesh.GlobalTransform * mesh.GetAabb();
			Vector3 a = ToLocal(box.Position);
			Vector3 b = ToLocal(box.End);
			bounds = new Rect2(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y),
				Mathf.Abs(b.X - a.X), Mathf.Abs(b.Y - a.Y));
		}

		// By index: `GetChildren()` is a Godot Array (and a weak handle) per node per call,
		// and this walks every followed subtree every frame — it was the largest single
		// handle source in the HUD.
		for (int i = 0, count = node.GetChildCount(); i < count; ++i)
		{
			if (DrawnRect(node.GetChild(i)) is { } inner)
			{
				bounds = bounds is { } known ? known.Merge(inner) : inner;
			}
		}

		return bounds;
	}

	/// <summary>
	/// <see cref="Engine_UiRoundedPanel.Fit"/> pads the rectangle it is given; this hands it one
	/// shrunk by that padding so the panel lands at exactly <paramref name="size"/>.
	/// </summary>
	private static void Fit(Engine_UiRoundedPanel panel, Vector2 centre, Vector2 size, float z)
	{
		var padding = new Vector2(Engine_UiRoundedPanel.PaddingX, Engine_UiRoundedPanel.PaddingY);
		Vector2 inner = size - padding * 2f;
		panel.Fit(new Rect2(centre - inner * 0.5f, inner), z);
	}

	/// <summary>A number, or the value a source states now for the piece's player; null when the source has none.</summary>
	private float? Resolve(string source, Piece piece) => Engine_UiValueSources.Number(source, ContextFor(piece));

	private static Texture2D? LoadTexture(string path) => LoadAnyTexture(path);

	/// <summary>
	/// The texture a call stated for <paramref name="player"/>'s copy of a picture piece
	/// (<see cref="Engine_UiHudOverrides.IconForPiece"/>), re-read when the overrides change; null
	/// for none, or a path that loads nothing.
	/// </summary>
	private static Texture2D? StatedTexture(Built built, int player)
	{
		int generation = Engine_UiHudOverrides.Generation;
		if (built.StatedGeneration == generation && built.StatedPlayer == player)
		{
			return built.Stated;
		}

		built.StatedGeneration = generation;
		built.StatedPlayer = player;
		built.Stated = Engine.IsEditorHint() || Engine_UiHudOverrides.IconForPiece(player, built.Piece.Id) is not { Length: > 0 } path
			? null
			: LoadAnyTexture(path);
		return built.Stated;
	}

	private static readonly Dictionary<string, Texture2D?> _anyTextures = new(System.StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// A texture by any path a mod names, loaded once per path (a miss remembered too): <c>res://</c>
	/// through Godot's loader; an enabled mod's PNG (<c>art/x.png</c>, through the mount, then the
	/// package itself); otherwise a game texture through the <c>game://</c> loader — the banks with
	/// the mods' overlays in place — with or without the prefix, slashes either way, <c>.tex</c>
	/// added when missing. Null for nothing there.
	/// </summary>
	public static Texture2D? LoadAnyTexture(string path)
	{
		string named = (path ?? string.Empty).Trim();
		if (named.Length == 0)
		{
			return null;
		}

		if (_anyTextures.TryGetValue(named, out Texture2D? known))
		{
			return known;
		}

		Texture2D? texture = null;
		if (named.StartsWith("res://", System.StringComparison.OrdinalIgnoreCase))
		{
			texture = ResourceLoader.Exists(named) ? ResourceLoader.Load<Texture2D>(named) : null;
		}
		else
		{
			string virtualPath = named.Replace('\\', '/');
			if (virtualPath.StartsWith(Refii.Textures.Engine_BnkTextureLoader.PathPrefix, System.StringComparison.OrdinalIgnoreCase))
			{
				virtualPath = virtualPath[Refii.Textures.Engine_BnkTextureLoader.PathPrefix.Length..];
			}

			virtualPath = virtualPath.TrimStart('/');
			if (virtualPath.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase))
			{
				byte[]? bytes = Refii.Textures.Engine_BnkTextureLoader.ReadBytes(virtualPath) ??
					Refii.Mods.ModEntries.ReadEnabledBytes(virtualPath);
				var image = new Image();
				if (bytes is not null && image.LoadPngFromBuffer(bytes) == Error.Ok)
				{
					image.GenerateMipmaps();
					texture = ImageTexture.CreateFromImage(image);
				}
			}
			else
			{
				if (!virtualPath.EndsWith(".tex", System.StringComparison.OrdinalIgnoreCase))
				{
					virtualPath += ".tex";
				}

				Refii.Textures.Engine_BnkTextureLoader.EnsureRegistered();
				string game = Refii.Textures.Engine_BnkTextureLoader.PathPrefix + virtualPath.ToLowerInvariant();
				texture = ResourceLoader.Exists(game) ? ResourceLoader.Load<Texture2D>(game) : null;
			}
		}

		if (texture is null)
		{
			GD.PushWarning($"[hud-mods] no texture at '{named}' (a game texture, an enabled mod's PNG or res://)");
		}

		_anyTextures[named] = texture;
		return texture;
	}

	// -----------------------------------------------------------------------------------
	// Loading — every enabled mod's ui/hud.json, composed by id in load order.
	// -----------------------------------------------------------------------------------

	public static ModLayout LoadFromMods() => LoadFrom(Refii.Mods.ModEntries.ReadEnabledText(ModHudPath));

	/// <summary>
	/// What the given mods' <c>ui/hud.json</c> files state (LOWEST priority first), composed — the
	/// enabled mods' for the game, the mod being edited among them for the Studio's preview.
	/// </summary>
	public static ModLayout LoadFrom(IEnumerable<(string Mod, string Text)> files)
	{
		var byId = new Dictionary<string, Piece>(System.StringComparer.Ordinal);
		var order = new List<string>();
		var hidden = new HashSet<string>(System.StringComparer.Ordinal);
		var clones = new Dictionary<string, Clone>(System.StringComparer.Ordinal);
		var scales = new Dictionary<string, float>(System.StringComparer.Ordinal);
		var groups = new Dictionary<string, Engine_HudModGroups.Group>(System.StringComparer.Ordinal);

		// Lowest priority first, so the highest is applied last and wins a shared id.
		foreach ((string mod, string text) in files)
		{
			ParseClones(text, mod, clones, scales);
			ParseGroups(text, mod, groups);
			foreach (Piece piece in Parse(text, mod, hidden))
			{
				if (byId.TryGetValue(piece.Id, out Piece? previous))
				{
					GD.Print($"[hud-mods] '{piece.Id}': mod '{mod}' replaces mod '{previous.Mod}'");
				}
				else
				{
					order.Add(piece.Id);
				}

				byId[piece.Id] = piece;
			}
		}

		return new ModLayout(Engine_HudModTwins.WithPlayerTwoTwins(order.ConvertAll(id => byId[id])), hidden, new List<Clone>(clones.Values), scales, groups);
	}

	/// <summary>A file's <c>groups</c> (<see cref="Engine_HudModGroups"/>), by id, later mods winning.</summary>
	private static void ParseGroups(string text, string mod, Dictionary<string, Engine_HudModGroups.Group> groups)
	{
		Variant parsed = Json.ParseString(text);
		if (parsed.VariantType != Variant.Type.Dictionary ||
			!parsed.AsGodotDictionary().TryGetValue(Engine_HudModGroups.Section, out Variant section) ||
			section.VariantType != Variant.Type.Dictionary)
		{
			return;
		}

		foreach ((Variant id, Variant value) in section.AsGodotDictionary())
		{
			if (value.VariantType != Variant.Type.Dictionary)
			{
				GD.PushWarning($"[hud-mods] {mod}:{ModHudPath}: group '{id.AsString()}' is not an object; skipped");
				continue;
			}

			var record = value.AsGodotDictionary();
			Vector2 anchor = Pair(record, "anchor", Vector2.Zero);
			string offsetX = string.Empty, offsetY = string.Empty;
			if (record.TryGetValue("offset", out Variant offset) && offset.VariantType == Variant.Type.Array &&
				offset.AsGodotArray() is { Count: 2 } pair)
			{
				offsetX = pair[0].AsString();
				offsetY = pair[1].AsString();
			}

			string name = id.AsString();
			groups[name] = new Engine_HudModGroups.Group(name, mod, new Engine_HudLayout.Anchor(anchor.X, anchor.Y),
				Pair(record, "pivot", Vector2.Zero), Text(record, "scale"), offsetX, offsetY);
		}
	}

	/// <summary>A file's <c>clones</c> and per-piece <c>scale</c>, by id, later mods winning.</summary>
	private static void ParseClones(
		string text, string mod, Dictionary<string, Clone> clones, Dictionary<string, float> scales)
	{
		Variant parsed = Json.ParseString(text);
		if (parsed.VariantType != Variant.Type.Dictionary)
		{
			return;
		}

		var root = parsed.AsGodotDictionary();
		if (root.TryGetValue("scale", out Variant scale) && scale.VariantType == Variant.Type.Dictionary)
		{
			foreach ((Variant id, Variant factor) in scale.AsGodotDictionary())
			{
				scales[id.AsString()] = (float)factor.AsDouble();
			}
		}

		if (!root.TryGetValue("clones", out Variant list) || list.VariantType != Variant.Type.Array)
		{
			return;
		}

		foreach (Variant value in list.AsGodotArray())
		{
			if (value.VariantType != Variant.Type.Dictionary)
			{
				continue;
			}

			var record = value.AsGodotDictionary();
			string id = Text(record, "id");
			string of = Text(record, "of");
			if (id.Length == 0 || of.Length == 0)
			{
				GD.PushWarning($"[hud-mods] {mod}:{ModHudPath}: a clone needs \"id\" and \"of\"; skipped");
				continue;
			}

			var values = new Dictionary<string, string>(System.StringComparer.Ordinal);
			if (record.TryGetValue("values", out Variant map) && map.VariantType == Variant.Type.Dictionary)
			{
				foreach ((Variant meter, Variant source) in map.AsGodotDictionary())
				{
					values[meter.AsString()] = source.AsString();
				}
			}

			var bars = new Dictionary<string, string>(System.StringComparer.Ordinal);
			if (record.TryGetValue("bars", out Variant barMap) && barMap.VariantType == Variant.Type.Dictionary)
			{
				foreach ((Variant element, Variant meter) in barMap.AsGodotDictionary())
				{
					bars[element.AsString()] = meter.AsString();
				}
			}

			string iconElement = string.Empty;
			var iconTextures = new List<string>();
			if (record.TryGetValue("icon", out Variant icon) && icon.VariantType == Variant.Type.Dictionary)
			{
				var iconRecord = icon.AsGodotDictionary();
				iconElement = Text(iconRecord, "element");
				iconTextures = Strings(iconRecord, "textures");
			}

			// "slides": element → slide number (a number, or a numeric string as the Studio's map
			// field writes it); 1 past the master, as the game counts them.
			Dictionary<string, int>? slides = null;
			if (record.TryGetValue("slides", out Variant slideMap) && slideMap.VariantType == Variant.Type.Dictionary)
			{
				foreach ((Variant element, Variant slide) in slideMap.AsGodotDictionary())
				{
					int number = slide.VariantType switch
					{
						Variant.Type.Int or Variant.Type.Float => (int)slide.AsDouble(),
						Variant.Type.String when int.TryParse(slide.AsString().Trim(), System.Globalization.NumberStyles.Integer,
							System.Globalization.CultureInfo.InvariantCulture, out int statedSlide) => statedSlide,
						_ => 0,
					};
					if (number <= 0)
					{
						GD.PushWarning($"[hud-mods] {mod}:{ModHudPath} clone '{id}': slide for '{element.AsString()}' is not a slide number (1 past the master); ignored");
						continue;
					}

					(slides ??= new Dictionary<string, int>(System.StringComparer.Ordinal))[element.AsString()] = number;
				}
			}

			clones[id] = new Clone(id, mod, of, Pair(record, "offset", Vector2.Zero),
				Number(record, "hue", 0f), Strings(record, "hueElements"), iconElement, iconTextures, values, bars, slides);
		}
	}

	private static List<string> Strings(Godot.Collections.Dictionary record, string key)
	{
		var list = new List<string>();
		if (record.TryGetValue(key, out Variant value) && value.VariantType == Variant.Type.Array)
		{
			foreach (Variant item in value.AsGodotArray())
			{
				list.Add(item.AsString());
			}
		}

		return list;
	}

	/// <summary>
	/// A clone's meter value, 0..100, read without a view (mod values and the hero only):
	/// <see cref="Engine_UiValueSources.Ratio"/>. The HUD view reads its copies' through its own
	/// readers, keyed to the copy's player.
	/// </summary>
	public static float? MeterValue(string expression) => Engine_UiValueSources.Ratio(expression, Standalone);

	/// <summary>The pieces one <c>ui/hud.json</c> states, adding its <c>hide</c> ids to <paramref name="hidden"/>.</summary>
	public static List<Piece> Parse(string text, string mod, HashSet<string> hidden)
	{
		var pieces = new List<Piece>();
		Variant parsed = Json.ParseString(text);
		if (parsed.VariantType != Variant.Type.Dictionary)
		{
			GD.PushWarning($"[hud-mods] {mod}:{ModHudPath} is not a JSON object; ignored");
			return pieces;
		}

		var root = parsed.AsGodotDictionary();
		if (root.TryGetValue("hide", out Variant hide) && hide.VariantType == Variant.Type.Array)
		{
			foreach (Variant id in hide.AsGodotArray())
			{
				hidden.Add(id.AsString());
			}
		}

		if (!root.TryGetValue("pieces", out Variant list) || list.VariantType != Variant.Type.Array)
		{
			return pieces;
		}

		foreach (Variant value in list.AsGodotArray())
		{
			if (value.VariantType != Variant.Type.Dictionary)
			{
				continue;
			}

			var record = value.AsGodotDictionary();
			string id = Text(record, "id");
			if (id.Length == 0)
			{
				GD.PushWarning($"[hud-mods] {mod}:{ModHudPath}: a piece has no id; skipped");
				continue;
			}

			Kind kind = Text(record, "kind") switch
			{
				"picture" => Kind.Picture,
				"panel" => Kind.Panel,
				"text" => Kind.Text,
				_ => Kind.Meter,
			};
			Vector2 anchor = Pair(record, "anchor", Vector2.Zero);
			pieces.Add(new Piece(
				id,
				mod,
				kind,
				new Engine_HudLayout.Anchor(anchor.X, anchor.Y),
				Pair(record, "offset", Vector2.Zero),
				kind == Kind.Text ? Vector2.Zero : Pair(record, "size", new Vector2(200f, 10f)),
				Text(record, "value"),
				record.TryGetValue("max", out Variant max) ? max.AsString() : "1",
				ColourOf(record, "fill", new Color(0.25f, 0.69f, 0.29f)) ?? default,
				ColourOf(record, "back", new Color(0f, 0f, 0f, 0.6f)) ?? default,
				Text(record, "texture"),
				Text(record, "follow"),
				Text(record, "followElement"),
				Text(record, "widthFrom"),
				Number(record, "widthPerUnit", 1f),
				Number(record, "minWidth", 0f),
				Number(record, "maxWidth", 100000f),
				ColourOf(record, "chip", null),
				ColourOf(record, "border", null),
				Number(record, "borderWidth", 1f),
				Number(record, "corner", -1f),
				record.ContainsKey("textures") ? Strings(record, "textures") : null,
				Text(record, "index"),
				Text(record, "text"),
				Text(record, "format"),
				kind == Kind.Text
					? PointSize(record)
					: Engine_HudModText.DefaultSize,
				ColourOf(record, "colour", null),
				Engine_HudModText.Align(Text(record, "align")),
				RegionOf(record),
				record.TryGetValue("each", out Variant each) && each.VariantType == Variant.Type.Bool && each.AsBool(),
				kind == Kind.Picture ? ColourOf(record, "tint", null) : null,
				Text(record, "group"),
				kind == Kind.Text ? ColourOf(record, "outline", null) : null,
				Number(record, "outlineWidth", 1.5f),
				kind == Kind.Text ? ColourOf(record, "shadow", null) : null,
				Pair(record, "shadowOffset", new Vector2(1.5f, 1.5f))));
		}

		return pieces;
	}

	/// <summary>A picture's <c>region</c>: <c>[u, v, w, h]</c> fractions of its texture, clamped to it; null for none or a bad one.</summary>
	private static Rect2? RegionOf(Godot.Collections.Dictionary record)
	{
		if (!record.TryGetValue("region", out Variant value) || value.VariantType != Variant.Type.Array)
		{
			return null;
		}

		var array = value.AsGodotArray();
		if (array.Count != 4)
		{
			return null;
		}

		return Region((float)array[0].AsDouble(), (float)array[1].AsDouble(), (float)array[2].AsDouble(), (float)array[3].AsDouble());
	}

	/// <summary>A region of a texture in fractions, clamped inside it; null when it has no area.</summary>
	public static Rect2? Region(float u, float v, float w, float h)
	{
		u = Mathf.Clamp(u, 0f, 1f);
		v = Mathf.Clamp(v, 0f, 1f);
		w = Mathf.Clamp(w, 0f, 1f - u);
		h = Mathf.Clamp(h, 0f, 1f - v);
		return w > 0f && h > 0f ? new Rect2(u, v, w, h) : null;
	}

	/// <summary>A text piece's <c>size</c>: a POINT size when it is a number, else the HUD's own.</summary>
	private static int PointSize(Godot.Collections.Dictionary record) =>
		record.TryGetValue("size", out Variant size) && size.VariantType is Variant.Type.Float or Variant.Type.Int
			? Mathf.Max(1, Mathf.RoundToInt(size.AsDouble()))
			: Engine_HudModText.DefaultSize;

	private static string Text(Godot.Collections.Dictionary record, string key) =>
		record.TryGetValue(key, out Variant value) ? value.AsString() : string.Empty;

	private static float Number(Godot.Collections.Dictionary record, string key, float fallback) =>
		record.TryGetValue(key, out Variant value) ? (float)value.AsDouble() : fallback;

	private static Vector2 Pair(Godot.Collections.Dictionary record, string key, Vector2 fallback)
	{
		if (!record.TryGetValue(key, out Variant value) || value.VariantType != Variant.Type.Array)
		{
			return fallback;
		}

		var array = value.AsGodotArray();
		return array.Count == 2
			? new Vector2((float)array[0].AsDouble(), (float)array[1].AsDouble())
			: fallback;
	}

	private static Color? ColourOf(Godot.Collections.Dictionary record, string key, Color? fallback) =>
		record.TryGetValue(key, out Variant value) && Color.HtmlIsValid(value.AsString())
			? Color.FromHtml(value.AsString())
			: fallback;
}
