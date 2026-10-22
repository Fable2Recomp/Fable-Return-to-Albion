using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// THE HUD'S OTHER WIDGETS AS PIECES. The world icons, the world sprites, the aim reticule, time
/// control's dial and the achievement toast are drawn by their own readers, not cut from
/// <c>hud.bgf</c> — but a mod addresses them exactly as it addresses a cut piece, in the same
/// <c>ui/hud.json</c> statements: <c>hide</c>, <c>scale</c>, <c>layout</c> (move) and
/// <c>clones</c> (a copy). The timing meter is a cut piece already
/// (<c>group.ExpressionMeter.ExpressionMeter</c>).
/// </summary>
/// <remarks>
/// <para>Each widget is a layout piece (<see cref="Engine_HudLayout.AddWidgets"/>): it is in the
/// snapshot, the player's layout tool and the Studio like every other piece. Its HOME is the point
/// it hangs from — the screen's centre for the layers drawn over the world, the bottom centre for
/// the toast — and a layout entry moves that point: the widget is drawn shifted by where the entry
/// puts it less where its home is. A widget's content follows the world or the guest, so a move
/// SHIFTS it and a scale sizes each mark about its own point (a petal round its aim, an icon over
/// its thing), never the layer about the screen.</para>
/// <para>PLAYER TWO'S COPY is the same widget with a <c>2</c>: <c>widget.Reticule2</c> is player
/// two's reticule — their half on split screen, and the whole window of an online joiner, whose
/// own reticule is player two's. A statement about one player's widget never reaches the other's.</para>
/// <para>A <c>clones</c> entry of a widget builds a COPY that draws exactly what the original draws
/// this frame, at the copy's own offset and scale (<see cref="Copy"/>); a copy's <c>hue</c>,
/// <c>values</c>, <c>bars</c> and <c>icon</c> are a cut piece's and are not read here.</para>
/// </remarks>
public static partial class Engine_HudWidgets
{
	/// <summary>Every widget id starts with this.</summary>
	public const string Prefix = "widget.";

	/// <summary>One addressable widget.</summary>
	/// <param name="Id">What a statement names it by.</param>
	/// <param name="Player">0 the screen's, 1 player one's, 2 player two's.</param>
	/// <param name="Home">The screen point it hangs from.</param>
	/// <param name="Size">Its nominal size in design units, for an editor's stand-in.</param>
	public sealed record Widget(string Id, string Name, int Player, Engine_HudLayout.Anchor Home, Vector2 Size, string Summary);

	public const string WorldIcons = "WorldIcons";
	public const string WorldSprites = "WorldSprites";
	public const string Reticule = "Reticule";
	public const string SlowTimeDial = "SlowTimeDial";
	public const string AchievementToast = "AchievementToast";

	/// <summary>Every widget, player one's before player two's.</summary>
	public static IReadOnlyList<Widget> All => Table;

	// An array walked by index: the interface enumerator is an allocation per lookup, and
	// `Find` runs per widget per frame from the HUD's mount pass.
	private static readonly Widget[] Table =
	{
		new Widget(Prefix + WorldIcons, WorldIcons, 1, Engine_HudLayout.Anchor.Centre, new Vector2(96f, 48f),
			"the pooled world prompts (talk, open, pick up) and the hold meter, over their things"),
		new Widget(Prefix + WorldIcons + "2", WorldIcons, 2, Engine_HudLayout.Anchor.Centre, new Vector2(96f, 48f),
			"player two's world prompts"),
		new Widget(Prefix + WorldSprites, WorldSprites, 1, Engine_HudLayout.Anchor.Centre, new Vector2(96f, 48f),
			"the world-space icons: property signs, income, opinions, quest markers"),
		new Widget(Prefix + WorldSprites + "2", WorldSprites, 2, Engine_HudLayout.Anchor.Centre, new Vector2(96f, 48f),
			"player two's world-space icons"),
		new Widget(Prefix + Reticule, Reticule, 1, Engine_HudLayout.Anchor.Centre, new Vector2(64f, 64f),
			"the aim reticule's petals"),
		new Widget(Prefix + Reticule + "2", Reticule, 2, Engine_HudLayout.Anchor.Centre, new Vector2(64f, 64f),
			"player two's aim reticule"),
		new Widget(Prefix + SlowTimeDial, SlowTimeDial, 1, Engine_HudLayout.Anchor.Centre, new Vector2(160f, 160f),
			"time control's dial"),
		new Widget(Prefix + SlowTimeDial + "2", SlowTimeDial, 2, Engine_HudLayout.Anchor.Centre, new Vector2(160f, 160f),
			"player two's time control dial"),
		new Widget(Prefix + AchievementToast, AchievementToast, 0, Engine_HudLayout.Anchor.BottomCentre, new Vector2(420f, 72f),
			"the achievement toast"),
	};

	/// <summary>The widget an id names, or null.</summary>
	public static Widget? Find(string id)
	{
		for (int i = 0; i < Table.Length; ++i)
		{
			if (Table[i].Id == id)
			{
				return Table[i];
			}
		}

		return null;
	}

	/// <summary>The widget of a reader by name for one player (0 the screen's, 1, 2), or null.</summary>
	private static Widget? FindByPlayer(string name, int player)
	{
		for (int i = 0; i < Table.Length; ++i)
		{
			if (Table[i].Player == player && Table[i].Name == name)
			{
				return Table[i];
			}
		}

		return null;
	}

	/// <summary>Player one's (or the screen's) widget of a reader by its name, or null.</summary>
	public static Widget? FindByName(string name) => Find(Prefix + name);

	/// <summary>Whether <paramref name="id"/> names a widget (not a cut piece).</summary>
	public static bool IsWidget(string id) => id.StartsWith(Prefix, StringComparison.Ordinal);

	/// <summary>
	/// The id statements reach one drawn widget by: <paramref name="name"/>'s player-two id for the
	/// copy seated in player two's half, and for the window's own widget on an online joiner, whose
	/// local player is player two; player one's (or the screen's) otherwise.
	/// </summary>
	public static string DeclaredId(string name, bool seatTwo, bool localPlayerTwo)
	{
		// The ids are the table's own strings — per frame per widget, a concatenation here
		// was most of the HUD mount pass's garbage.
		if ((seatTwo || localPlayerTwo) && FindByPlayer(name, 2) is { } two)
		{
			return two.Id;
		}

		return (FindByPlayer(name, 1) ?? FindByPlayer(name, 0))?.Id ?? Prefix + name;
	}

	/// <summary>What the mods state about one widget, composed.</summary>
	/// <param name="Shift">Where its home moves to, in design units (+Y DOWN), at the given extent.</param>
	public readonly record struct State(bool Hidden, float Scale, Vector2 Shift);

	/// <summary>
	/// One widget's state: hidden by a mod, its mod scale (1 when none), and the shift its layout
	/// piece's place makes from its home at <paramref name="extent"/> (the seat's visible canvas).
	/// The place is the layout's — shipped (home) &lt; mods &lt; the player's file, composed there.
	/// </summary>
	public static State StateOf(
		string id, ISet<string> hidden, IReadOnlyDictionary<string, float> scales,
		Engine_HudLayout.Anchor anchor, Vector2 offset, Vector2 extent)
	{
		Widget? widget = Find(id);
		Vector2 home = widget?.Home is { } h ? new Vector2(h.X, h.Y) : new Vector2(0.5f, 0.5f);
		float scale = Engine_HudLayout.Scale(extent);
		float layoutScale = scale <= 0f ? 1f : scale;

		// Both points through the one placement rule; their difference in DESIGN units, +Y down.
		Vector2 at = Engine_HudLayout.Place(new Engine_HudLayout.Piece(id, string.Empty, anchor, offset), extent);
		Vector2 from = Engine_HudLayout.Place(
			new Engine_HudLayout.Piece(id, string.Empty, new Engine_HudLayout.Anchor(home.X, home.Y), Vector2.Zero), extent);
		Vector2 shift = new((at.X - from.X) / layoutScale, -(at.Y - from.Y) / layoutScale);
		float factor = scales.TryGetValue(id, out float stated) && stated > 0f ? stated : 1f;
		return new State(hidden.Contains(id), factor, shift);
	}

	/// <summary>
	/// A widget copy's place: its original's shift plus the clone's own offset (design units, +Y
	/// down), and its own scale where the mods give one, else the original's.
	/// </summary>
	public static State CopyState(State original, Vector2 cloneOffset, IReadOnlyDictionary<string, float> scales, string copyId) =>
		new(false, scales.TryGetValue(copyId, out float own) && own > 0f ? own : original.Scale, original.Shift + cloneOffset);

	/// <summary>
	/// A COPY OF A WIDGET: draws, every frame, exactly what the original draws — its meshes, their
	/// materials, their place within it — so it moves, fades and hides as the original does, and
	/// needs no feed of its own. Shares the original's mesh and material resources; owns only nodes.
	/// </summary>
	/// <remarks>
	/// Added after the original in the tree, so it is processed after the original has laid itself
	/// out this frame. Its own transform is the original's with its <see cref="Shift"/> (canvas
	/// units, +Y up) and a <see cref="Scale"/> about <see cref="Pivot"/>, the widget's home.
	/// </remarks>
	[Tool]
	public sealed partial class Copy : Node3D
	{
		/// <summary>The widget copied.</summary>
		public Node3D? Source { get; set; }

		/// <summary>The copy's displacement from the original, canvas units, +Y up.</summary>
		public Vector2 Shift { get; set; }

		/// <summary>The copy's size against the original.</summary>
		public float ScaleFactor { get; set; } = 1f;

		/// <summary>The point the copy scales about, in the original's space.</summary>
		public Vector2 Pivot { get; set; }

		private readonly List<MeshInstance3D> _mirrors = new();

		public override void _Process(double delta)
		{
			if (Source is not { } source || !GodotObject.IsInstanceValid(source))
			{
				Visible = false;
				return;
			}

			Visible = source.IsVisibleInTree();
			if (!Visible)
			{
				return;
			}

			var pivot = new Vector3(Pivot.X, Pivot.Y, 0f);
			Transform3D scaled = new Transform3D(Basis.Identity, pivot) *
				new Transform3D(Basis.Identity.Scaled(new Vector3(ScaleFactor, ScaleFactor, 1f)), Vector3.Zero) *
				new Transform3D(Basis.Identity, -pivot);
			Transform = new Transform3D(Basis.Identity, new Vector3(Shift.X, Shift.Y, 0f)) * source.Transform * scaled;

			int used = 0;
			Mirror(source, Transform3D.Identity, ref used);
			for (int i = used; i < _mirrors.Count; ++i)
			{
				_mirrors[i].Visible = false;
			}
		}

		/// <summary>Every visible mesh under <paramref name="node"/>, placed relative to the original.</summary>
		private void Mirror(Node node, Transform3D parent, ref int used)
		{
			int count = node.GetChildCount();
			for (int i = 0; i < count; ++i)
			{
				if (node.GetChild(i) is not Node3D child || !child.Visible)
				{
					continue;
				}

				Transform3D at = parent * child.Transform;
				if (child is MeshInstance3D mesh && mesh.Mesh is not null)
				{
					if (used == _mirrors.Count)
					{
						var made = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
						AddChild(made);
						_mirrors.Add(made);
					}

					MeshInstance3D mirror = _mirrors[used++];
					mirror.Mesh = mesh.Mesh;
					mirror.MaterialOverride = mesh.MaterialOverride;
					mirror.Transform = at;
					mirror.Visible = true;
				}

				Mirror(child, at, ref used);
			}
		}
	}
}
