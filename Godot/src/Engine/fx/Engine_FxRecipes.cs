using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Refii.Host;

namespace Refii.Fx;

/// <summary>
/// FX recipes: how a mod changes the LOOK of named effects while a condition holds. The guest
/// still states which effect plays and where; a recipe dresses the node Godot draws for it.
/// </summary>
/// <remarks>
/// <para>A mod ships <c>fx/recipes.json</c>:</para>
/// <code>
/// { "recipes": [
///   { "id": "spell_weaving.vortex_fire",
///     "with": "spell_weaving_vortex_fire", "withRadius": 10,
///     "base": ["fxspl_vortex_orb", "fxspl_vortex_orb_01"],
///     "tint": [1.7, 0.7, 0.25], "intensity": 1.3, "size": 1.0,
///     "attach": [ { "effect": "fxspl_blast_fire_ring", "scale": 0.9, "count": 1 },
///                 { "effect": "fxspl_blast_trail", "count": 3, "radius": 1.2, "height": 0.8,
///                   "turnsPerSecond": 1.0 } ] } ] }
/// </code>
/// <para>A recipe is ACTIVE while any of its <c>when</c> mod values equals <c>equals</c>
/// (<see cref="Begin"/>, once per frame), or — a look bound to a CAST — while the effect it names
/// in <c>with</c> is playing (<c>play_fx_on</c>; see <see cref="WithHash"/>), and then only on the
/// base effects that play binds. While active, every live effect named in <c>base</c>
/// takes the recipe's <c>tint</c> (a multiplier on each particle's colour), <c>intensity</c>
/// and <c>size</c> multipliers, and the <c>attach</c> effects as child nodes: <c>count</c> copies,
/// spread evenly on a ring of <c>radius</c> at <c>height</c>, turning about the base at
/// <c>turnsPerSecond</c>; with no radius they sit on the base's origin. When the recipe stops
/// being active the node is put back as it was and the children are freed. The first active
/// recipe naming an effect wins; mods compose by load order.</para>
/// <para>A recipe may also carry <c>pieces</c>, authored looks the banks never had, drawn with the
/// game's own effect textures and meshes (<see cref="Engine_FxRecipePieces"/>): <c>embers</c>,
/// <c>arcs</c>, <c>ring</c>, <c>blades</c>. ONE FX MODEL: a recipe's pieces are an EFFECT of the
/// bank's model (<see cref="FxEffect.Pieces"/>), named <see cref="PiecesEffectName"/> and stated
/// into Godot's bank (<see cref="Engine_FxModEffects.Contribute"/>), so they hang on the anchor as
/// one more attachment. Every attachment is made by <see cref="Engine_FxPlayer.Attach"/>, the node
/// play_fx makes. A piece and an attachment are Godot's drawing only (the classic look shows
/// neither); a look that must play under both looks is a mod effect (<c>fx/effects/*.json</c>, the
/// bank's own entities) played per cast, with the recipe binding the tint to it through
/// <c>with</c>.</para>
/// <para>Effect names are the <c>.pfx</c> names (FNV-1 lower-cased, as the banks key them) or
/// <c>FX_&lt;hash&gt;</c> tokens.</para>
/// </remarks>
public static class Engine_FxRecipes
{
	public const string RecipesPath = "fx/recipes.json";

	/// <summary><paramref name="Stack"/> copies of the ring, <paramref name="StackStep"/> apart in height, for a look that climbs a column.</summary>
	public sealed record Attachment(
		string Effect, float Scale, int Count, float Radius, float Height, float TurnsPerSecond, float Intensity,
		int Stack = 1, float StackStep = 0.6f, float Stagger = 0f, Color? Tint = null,
		float RadiusStep = 0f, float ScaleStep = 1f, float FadeIn = 0f);

	/// <summary>
	/// A light inside the dressed effect, in the overlay's colour: what makes the base's own
	/// particles and the ground take the overlay's glow. <paramref name="Flicker"/> is the
	/// fraction of energy it wavers by.
	/// </summary>
	public sealed record Light(Color Colour, float Energy, float Range, float Height, float Flicker);

	/// <summary>
	/// <c>stagger</c> seconds between one copy's start and the next, so a stack of one-shot bursts
	/// (a fire ring plays once per clip) keeps some copy bursting at any moment.
	/// </summary>

	public sealed record Recipe(
		string Id,
		string Mod,
		IReadOnlyList<string> When,
		float Match,
		HashSet<uint> Base,
		Color Tint,
		float Intensity,
		float Size,
		IReadOnlyList<Attachment> Attach,
		IReadOnlyList<string> BaseNames,
		HashSet<uint> Anchor,
		IReadOnlyList<string> AnchorNames,
		Light? Glow,
		IReadOnlyList<Engine_FxRecipePieces.Piece>? Pieces = null,
		bool EveryNode = false,
		HashSet<uint>? Entities = null,
		IReadOnlyList<string>? EntityNames = null,
		string With = "",
		float WithRadius = 10f);

	private sealed class Applied
	{
		public Recipe Recipe = null!;
		public readonly List<Node> Children = new();

		/// <summary>Carries the pieces and the light (see Dress).</summary>
		public bool Anchored;

		/// <summary>An effect the recipe names as an anchor — a stand-in otherwise.</summary>
		public bool Named;
	}

	/// <summary>
	/// WITH: A LOOK BOUND TO A CAST BY THE EFFECT THE CAST PLAYS. A mod plays its look per cast with
	/// <c>play_fx_on</c> / <c>play_fx</c> (<see cref="Engine_FxPlayer"/>) on whatever the cast put in the
	/// world — the vortex, the fireball's shot, each sword, each victim. A recipe naming that effect
	/// (<c>"with": "spell_weaving_vortex_fire"</c>) holds while it plays, and dresses a base effect
	/// only where it plays (<see cref="Engine_FxPlayer.Bind"/>): on the entity the guest states the
	/// base node belongs to — the effect feed's owner, what its entity is attached to, or the spell
	/// whose own update places it (the vortex's ground effects, a Blades sword's; the fireball shot's
	/// effect is attached to the shot) — and an effect stated to another cast binds no other play;
	/// only an effect the guest states no cast for binds by standing within <c>withRadius</c> metres
	/// of a play. So two vortices from two slots each take their own partner's tint, and a cast that
	/// plays no look takes none. The node a play binds first carries the recipe's attachments and
	/// light, one per play.
	/// </summary>
	private static uint WithHash(Recipe recipe) => recipe.With.Length == 0 ? 0u : Engine_FxEffectJson.HashOf(recipe.With);

	/// <summary>Where the anchoring node of each (recipe, play) is: the first base node the play bound.</summary>
	private static readonly Dictionary<(string Recipe, uint Play), FableFx> _withAnchors = new();

	/// <summary>
	/// ENTITY ANCHORS. Some of a spell's live things are not effects but ENTITIES the guest
	/// spawns — the Blades' hovering swords, each its own entity with a mesh and no particle
	/// effect of its own while it waits. A recipe names them by definition in <c>entities</c>
	/// (the GDB record's name, or <c>#hash</c>), and every live entity of that definition carries
	/// the recipe's pieces and light on a node that follows the entity's own transform each
	/// frame, from the entity feed (the guest states the entity and where it is; Godot draws the
	/// dressing). Fed by <see cref="ApplyEntities"/> from the entity walk.
	/// </summary>
	private sealed class EntityAnchor
	{
		public Node3D Node = null!;
		public Recipe Recipe = null!;
		public readonly List<Node> Children = new();
		public bool Seen;
	}

	private static readonly Dictionary<uint, EntityAnchor> _entityAnchors = new();

	/// <summary>
	/// Once per frame after the entity walk: dresses every live entity an active recipe names
	/// and drops the dressing of any that is gone. <paramref name="transformOf"/> is the feed's
	/// guest-to-Godot placement; <paramref name="parent"/> holds the anchor nodes.
	/// </summary>
	public static void ApplyEntities(
		IReadOnlyList<HostBridge.GuestEntity> entities, Func<HostBridge.GuestEntity, Transform3D> transformOf, Node3D? parent)
	{
		foreach (EntityAnchor anchor in _entityAnchors.Values)
		{
			anchor.Seen = false;
		}

		if (parent is not null && GodotObject.IsInstanceValid(parent))
		{
			foreach (Recipe recipe in _active)
			{
				if (recipe.Entities is not { Count: > 0 } wanted || recipe.With.Length != 0)
				{
					continue;
				}

				foreach (HostBridge.GuestEntity entity in entities)
				{
					if (!wanted.Contains(entity.RecordHash))
					{
						continue;
					}

					if (!_entityAnchors.TryGetValue(entity.Address, out EntityAnchor? anchor) ||
						!ReferenceEquals(anchor.Recipe, recipe) || !GodotObject.IsInstanceValid(anchor.Node))
					{
						if (anchor is not null)
						{
							Drop(anchor);
							_entityAnchors.Remove(entity.Address);
						}

						anchor = new EntityAnchor
						{
							Node = new Node3D { Name = $"RecipeEntity_{recipe.Id}_{entity.Address:X8}" },
							Recipe = recipe,
						};
						parent.AddChild(anchor.Node);
						DressChildren(anchor.Node, recipe, anchor.Children);
						_entityAnchors[entity.Address] = anchor;
						string key = recipe.Id + "|entity|" + entity.RecordHash.ToString("x8");
						if (_reported.Add(key))
						{
							GD.Print($"[fx-recipes] {recipe.Id}: entity #{entity.RecordHash:x8} live — carries the pieces");
						}
					}

					anchor.Node.GlobalTransform = transformOf(entity);
					anchor.Seen = true;
				}
			}
		}

		List<uint>? gone = null;
		foreach ((uint address, EntityAnchor anchor) in _entityAnchors)
		{
			if (!anchor.Seen)
			{
				Drop(anchor);
				(gone ??= new List<uint>()).Add(address);
			}
		}

		if (gone is not null)
		{
			foreach (uint address in gone)
			{
				_entityAnchors.Remove(address);
			}
		}
	}

	private static void Drop(EntityAnchor anchor)
	{
		foreach (Node child in anchor.Children)
		{
			if (GodotObject.IsInstanceValid(child))
			{
				child.QueueFree();
			}
		}

		anchor.Children.Clear();
		if (GodotObject.IsInstanceValid(anchor.Node))
		{
			anchor.Node.QueueFree();
		}
	}

	/// <summary>
	/// A base effect of an active <c>with</c> recipe that no play of its effect bound, once per
	/// (recipe, effect) every few seconds: where the node stands and who owns it — the line that says
	/// why a cast's effect went undressed.
	/// </summary>
	private static readonly Dictionary<string, ulong> _rejectedAt = new(System.StringComparer.Ordinal);

	private static void ReportRejected(Recipe recipe, uint effectHash, Node3D node, uint owner, uint attached)
	{
		string key = recipe.Id + "|" + effectHash.ToString("x8");
		ulong now = Time.GetTicksMsec();
		if (_rejectedAt.TryGetValue(key, out ulong last) && now - last < 5000)
		{
			return;
		}

		_rejectedAt[key] = now;
		Vector3 at = node.GlobalPosition;
		string name = effectHash.ToString("x8");
		foreach (string candidate in recipe.BaseNames)
		{
			uint hash = Engine_BnkFxLoader.TryParseHashToken(candidate, out uint token) ? token : FxBank.Fnv1Lower(candidate);
			if (hash == effectHash)
			{
				name = candidate;
				break;
			}
		}

		GD.Print($"[fx-recipes] {recipe.Id}: base effect {name} at ({at.X:F1}, {at.Y:F1}, {at.Z:F1}) owner {owner:x8} attached {attached:x8} — " +
			$"no play of '{recipe.With}' rides its entity or stands within {recipe.WithRadius:F0} m");
	}

	/// <summary>Each (recipe, effect) dressing reported once, so a run says what a cast really plays.</summary>
	private static readonly HashSet<string> _reported = new(System.StringComparer.Ordinal);

	private static List<Recipe>? _all;
	private static readonly List<Recipe> _active = new();
	private static readonly Dictionary<FableFx, Applied> _applied = new();
	private static readonly List<FableFx> _gone = new();

	/// <summary>
	/// ONE COLUMN PER RECIPE. A spell plays several effects per cast and names them all in
	/// <c>base</c>; the first live one takes the attachments and the light, the rest take the
	/// tint only. When it dies the next one steps up.
	/// </summary>
	private static readonly Dictionary<Recipe, FableFx> _anchorOf = new();

	public static IReadOnlyList<Recipe> All => _all ??= Load();

	/// <summary>Recipes handed over from code rather than a file, by contributor (see <see cref="Contribute"/>).</summary>
	private static readonly Dictionary<string, List<Recipe>> _contributed = new(StringComparer.Ordinal);

	/// <summary>Guards <see cref="_contributed"/>: a live re-mount contributes from its worker thread.</summary>
	private static readonly object ContributedLock = new();

	/// <summary>
	/// Recipes a composer builds from another kind of mod content — the spells kind's looks
	/// (<see cref="Refii.Mods.ModSpells"/>). <paramref name="source"/>'s previous set is replaced
	/// whole; they load after every mod's <c>fx/recipes.json</c> and a recipe's id wins the same
	/// way. The next frame loads again.
	/// </summary>
	public static void Contribute(string source, IReadOnlyList<Recipe> recipes)
	{
		lock (ContributedLock)
		{
			_contributed[source] = new List<Recipe>(recipes);
		}

		_all = null;
	}

	/// <summary>Drops the loaded set; the next frame loads it again and re-dresses.</summary>
	public static void Reload()
	{
		_all = null;
		foreach ((FableFx node, Applied applied) in _applied)
		{
			if (GodotObject.IsInstanceValid(node))
			{
				Revert(node, applied);
			}
		}

		_applied.Clear();
		foreach (EntityAnchor anchor in _entityAnchors.Values)
		{
			Drop(anchor);
		}

		_entityAnchors.Clear();
		_withAnchors.Clear();
	}

	/// <summary>How many recipes are active this frame, for the FX report.</summary>
	public static int ActiveCount => _active.Count;

	/// <summary>Once per frame, before the live effects are walked: which recipes hold right now.</summary>
	public static void Begin()
	{
		_active.Clear();
		Engine_FxPlayer.BeginLiveEffects();
		foreach (Recipe recipe in All)
		{
			if (recipe.With.Length != 0 && Engine_FxPlayer.IsPlaying(WithHash(recipe)))
			{
				_active.Add(recipe);
				continue;
			}

			foreach (string key in recipe.When)
			{
				string name = key.StartsWith("mod:", StringComparison.Ordinal) ? key[4..] : key;
				if (HostBridge.TryGetModValue(name, out float value) && value == recipe.Match)
				{
					_active.Add(recipe);
					break;
				}
			}
		}

		_gone.Clear();
		foreach ((FableFx node, _) in _applied)
		{
			if (!GodotObject.IsInstanceValid(node) || !node.IsInsideTree())
			{
				_gone.Add(node);
			}
		}

		foreach (FableFx node in _gone)
		{
			_applied.Remove(node);
		}

		foreach ((Recipe recipe, FableFx node) in new List<KeyValuePair<Recipe, FableFx>>(_anchorOf))
		{
			if (!GodotObject.IsInstanceValid(node) || !node.IsInsideTree() || !_applied.ContainsKey(node))
			{
				_anchorOf.Remove(recipe);
			}
		}

		foreach (((string, uint) key, FableFx node) in new List<KeyValuePair<(string, uint), FableFx>>(_withAnchors))
		{
			if (!GodotObject.IsInstanceValid(node) || !node.IsInsideTree() || !_applied.ContainsKey(node))
			{
				_withAnchors.Remove(key);
			}
		}
	}

	/// <summary>
	/// Dresses or undresses one live effect's node for this frame's active recipes.
	/// <paramref name="owner"/> and <paramref name="attached"/> are the entities the feed states
	/// for the effect (the emitter's owner, and what its entity was attached to), 0 when unknown;
	/// <paramref name="handle"/> is the effect instance the feed names it by.
	/// </summary>
	public static void Apply(FableFx node, uint effectHash, uint owner = 0, uint attached = 0, uint handle = 0)
	{
		// What a play riding an entity with no drawn body follows (Engine_FxPlayer).
		Engine_FxPlayer.NoteLiveEffect(node, owner, attached, handle);

		Recipe? want = null;
		uint play = 0;
		foreach (Recipe recipe in _active)
		{
			if (!recipe.Base.Contains(effectHash))
			{
				continue;
			}

			// A look bound to a CAST: the node is dressed where a play of the recipe's effect rides
			// its entity or stands near it.
			if (recipe.With.Length != 0 &&
				!Engine_FxPlayer.Binds(WithHash(recipe), owner, attached, handle, node.GlobalPosition, recipe.WithRadius, out play))
			{
				ReportRejected(recipe, effectHash, node, owner, attached);
				continue;
			}

			want = recipe;
			break;
		}

		_applied.TryGetValue(node, out Applied? have);
		if (ReferenceEquals(have?.Recipe, want))
		{
			return;
		}

		if (have is not null)
		{
			Revert(node, have);
			_applied.Remove(node);
			if (_anchorOf.TryGetValue(have.Recipe, out FableFx? wasAnchor) && ReferenceEquals(wasAnchor, node))
			{
				_anchorOf.Remove(have.Recipe);
			}
		}

		if (want is not null)
		{
			bool named = want.Anchor.Count == 0 || want.Anchor.Contains(effectHash);
			bool free = !_anchorOf.TryGetValue(want, out FableFx? holder) || ReferenceEquals(holder, node) ||
				!GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree();

			// "everyNode": each live ANCHOR effect carries the pieces (each sword's orb, each
			// twister's orb, each fireball) — with no anchor list, each live base effect.
			// Otherwise one column per recipe, on the first live anchor.
			//
			// A STAND-IN WHEN NO ANCHOR IS LIVE. The anchor names are the recipe author's
			// expectation of what a cast plays; a cast that plays none of them must still be
			// dressed, or the look is nothing but a tint — which is how the partner effects went
			// missing once anchors were named. With no named effect live, the first live base
			// effect carries the pieces as the single anchor; the moment a named one appears
			// the stand-in is undressed and the named effects take over.
			// A recipe with ENTITY anchors carries its pieces on the entities; its effects are
			// never stand-ins (the Blades' appear and destroy bursts would otherwise each carry a
			// column for a moment).
			bool anchored;
			if (want.With.Length != 0)
			{
				// One per play: the first base node the play binds carries the light and attachments.
				(string, uint) key = (want.Id, play);
				if (!_withAnchors.TryGetValue(key, out FableFx? first) || !GodotObject.IsInstanceValid(first) ||
					!first.IsInsideTree() || !_applied.ContainsKey(first))
				{
					_withAnchors[key] = node;
					first = node;
				}

				anchored = ReferenceEquals(first, node);
			}
			else if (named)
			{
				anchored = want.EveryNode || free;
			}
			else
			{
				anchored = want.Entities is null && !AnyNamedLive(want) && free;
			}

			if (anchored && (!want.EveryNode || !named) && want.With.Length == 0)
			{
				_anchorOf[want] = node;
			}

			if (anchored && named && want.With.Length == 0)
			{
				foreach ((FableFx other, Applied dressed) in new List<KeyValuePair<FableFx, Applied>>(_applied))
				{
					if (ReferenceEquals(dressed.Recipe, want) && dressed.Anchored && !dressed.Named &&
						!ReferenceEquals(other, node) && GodotObject.IsInstanceValid(other))
					{
						Revert(other, dressed);
						_applied[other] = Dress(other, want, anchored: false);
						if (_anchorOf.TryGetValue(want, out FableFx? standIn) && ReferenceEquals(standIn, other))
						{
							_anchorOf.Remove(want);
						}
					}
				}
			}

			Applied made = Dress(node, want, anchored);
			made.Named = named;
			_applied[node] = made;
			Report(want, effectHash, named, anchored);
		}
	}

	/// <summary>Whether a live effect the recipe names as an anchor is dressed right now.</summary>
	private static bool AnyNamedLive(Recipe recipe)
	{
		foreach ((FableFx other, Applied dressed) in _applied)
		{
			if (ReferenceEquals(dressed.Recipe, recipe) && dressed.Named && dressed.Anchored &&
				GodotObject.IsInstanceValid(other) && other.IsInsideTree())
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// One line per (recipe, effect) the first time it is dressed: the effect's name as the
	/// recipe lists it, whether it is a named anchor or a stand-in, and whether it carries the
	/// pieces — the record of what a cast actually plays, which the anchor lists were written
	/// without.
	/// </summary>
	private static void Report(Recipe recipe, uint effectHash, bool named, bool anchored)
	{
		string effect = effectHash.ToString("X8");
		for (int i = 0; i < recipe.BaseNames.Count; ++i)
		{
			string name = recipe.BaseNames[i];
			uint hash = Engine_BnkFxLoader.TryParseHashToken(name, out uint token) ? token : FxBank.Fnv1Lower(name);
			if (hash == effectHash)
			{
				effect = recipe.BaseNames[i];
				break;
			}
		}

		if (_reported.Add(recipe.Id + "|" + effect))
		{
			GD.Print($"[fx-recipes] {recipe.Id}: base effect {effect} live — " +
				(named ? "a named anchor" : "not an anchor") + (anchored ? ", carries the pieces" : ", tint only"));
		}
	}

	/// <summary>Dresses a node outside the live walk — the FX Lab's preview. Undress with <see cref="Undress"/>.</summary>
	public static void DressPreview(FableFx node, Recipe recipe)
	{
		Undress(node);
		_applied[node] = Dress(node, recipe, true);
	}

	public static void Undress(FableFx node)
	{
		if (_applied.TryGetValue(node, out Applied? applied))
		{
			if (GodotObject.IsInstanceValid(node))
			{
				Revert(node, applied);
			}

			_applied.Remove(node);
		}
	}

	/// <summary><paramref name="anchored"/>: this node carries the attachments and the light; every
	/// dressed node takes the tint. A spell plays several effects per cast, and the column should
	/// rise from one of them.</summary>
	private static Applied Dress(FableFx node, Recipe recipe, bool anchored)
	{
		var applied = new Applied { Recipe = recipe, Anchored = anchored };
		node.Tint = recipe.Tint;
		node.RecipeIntensity = recipe.Intensity;
		node.RecipeSize = recipe.Size;
		if (!anchored)
		{
			return applied;
		}

		DressChildren(node, recipe, applied.Children);
		return applied;
	}

	/// <summary>The light, the authored pieces and the attached effects of <paramref name="recipe"/> under <paramref name="node"/>.</summary>
	private static void DressChildren(Node3D node, Recipe recipe, List<Node> children)
	{
		if (recipe.Glow is { } glow)
		{
			var light = new Engine_FxRecipeLight
			{
				Name = "RecipeLight",
				LightColor = glow.Colour,
				LightEnergy = glow.Energy,
				OmniRange = glow.Range,
				Position = new Vector3(0f, glow.Height, 0f),
				BaseEnergy = glow.Energy,
				Flicker = glow.Flicker,
				ShadowEnabled = false,
			};
			node.AddChild(light);
			children.Add(light);
		}

		if (recipe.Pieces is { Count: > 0 } &&
			Engine_FxPlayer.Attach(node, PiecesEffectName(recipe), Vector3.Zero, nodeName: "RecipePieces", bank: PiecesBank(recipe)) is { } pieces)
		{
			children.Add(pieces);
		}

		foreach (Attachment attachment in recipe.Attach)
		{
			int count = Math.Max(1, attachment.Count);
			Node3D parent = node;
			if (attachment.Radius > 0f || attachment.TurnsPerSecond != 0f)
			{
				var orbit = new Engine_FxOrbit { Name = "Recipe_" + attachment.Effect, TurnsPerSecond = attachment.TurnsPerSecond };
				node.AddChild(orbit);
				children.Add(orbit);
				parent = orbit;
			}

			int stack = Math.Max(1, attachment.Stack);
			for (int level = 0; level < stack; level++)
			{
				for (int i = 0; i < count; i++)
				{
					int ordinal = (level * count) + i;
					float levelScale = attachment.Scale * Mathf.Pow(attachment.ScaleStep, level);
					float levelRadius = attachment.Radius + (attachment.RadiusStep * level);
					// Each level starts a third of a turn on from the last, so a stack spirals.
					float angle = (count > 1 ? Mathf.Tau * i / count : 0f) + (level * Mathf.Tau / 3f);
					var local = new Vector3(
						levelRadius * Mathf.Cos(angle), attachment.Height + (level * attachment.StackStep),
						levelRadius * Mathf.Sin(angle));
					if (Engine_FxPlayer.Attach(
							parent, attachment.Effect, local, levelScale, attachment.Tint ?? Colors.White,
							attachment.FadeIn > 0f ? 0f : attachment.Intensity, true, attachment.Stagger <= 0f || ordinal == 0,
							$"Recipe_{attachment.Effect}_{level}_{i}") is not { } child)
					{
						continue;
					}

					if (attachment.FadeIn > 0f)
					{
						// Brought up over FadeIn seconds: a trail's first frames are a flat flash
						// before it has points to draw between, and this keeps them unseen.
						FableFx rising = child;
						float target = attachment.Intensity;
						var tween = node.CreateTween();
						tween.TweenMethod(Callable.From<float>(v =>
						{
							if (GodotObject.IsInstanceValid(rising))
							{
								rising.Intensity = v;
							}
						}), 0f, target, attachment.FadeIn).SetDelay(attachment.Stagger > 0f ? attachment.Stagger * ordinal : 0f);
					}
					if (attachment.Stagger > 0f && ordinal > 0)
					{
						FableFx late = child;
						node.GetTree()?.CreateTimer(attachment.Stagger * ordinal).Timeout += () =>
						{
							if (GodotObject.IsInstanceValid(late))
							{
								late.Playing = true;
							}
						};
					}
					if (ReferenceEquals(parent, node))
					{
						children.Add(child);
					}
				}
			}
		}

	}

	private static void Revert(FableFx node, Applied applied)
	{
		node.Tint = Colors.White;
		node.RecipeIntensity = 1f;
		node.RecipeSize = 1f;
		foreach (Node child in applied.Children)
		{
			if (GodotObject.IsInstanceValid(child))
			{
				child.QueueFree();
			}
		}

		applied.Children.Clear();
	}

	private static List<Recipe> Load()
	{
		var recipes = new List<Recipe>();
		foreach ((string mod, string text) in Refii.Mods.ModEntries.ReadEnabledText(RecipesPath))
		{
			// THE BUNDLED SOURCE WINS OVER ITS PACKAGE. A mod that ships with the app has its
			// source tree beside the binary (mods-src in a dev tree, bundled-mods exported), and an
			// edit there reaches a running game through `fx-recipes-reload` without a repack —
			// the loop a look is tuned in. Installed-only mods have no source and read as before.
			string source = System.IO.Path.Combine(
				Refii.Core.Engine_AppRoot.BundledModsDirectory, mod, RecipesPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
			string chosen = text;
			if (System.IO.File.Exists(source))
			{
				try
				{
					chosen = System.IO.File.ReadAllText(source);
				}
				catch (System.Exception)
				{
					chosen = text;
				}
			}

			foreach (Recipe recipe in Parse(mod, chosen))
			{
				recipes.RemoveAll(existing => existing.Id == recipe.Id);
				recipes.Add(recipe);
			}
		}

		List<List<Recipe>> contributedSets;
		lock (ContributedLock)
		{
			contributedSets = new List<List<Recipe>>(_contributed.Values);
		}

		foreach (List<Recipe> contributed in contributedSets)
		{
			foreach (Recipe recipe in contributed)
			{
				recipes.RemoveAll(existing => existing.Id == recipe.Id);
				recipes.Add(recipe);
			}
		}

		if (recipes.Count != 0)
		{
			GD.Print($"[fx-recipes] {recipes.Count} from mods");
		}

		ContributePieces(recipes);
		return recipes;
	}

	/// <summary>The effect a recipe's pieces are (<see cref="FxEffect.Pieces"/>): <c>&lt;id&gt;.pieces</c>, in effect-name characters.</summary>
	public static string PiecesEffectName(Recipe recipe)
	{
		var name = new System.Text.StringBuilder(recipe.Id.Length + 7);
		foreach (char c in recipe.Id)
		{
			name.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
		}

		return name.Append(".pieces").ToString();
	}

	/// <summary>A recipe's pieces as the effect definition they are stated as.</summary>
	public static Engine_FxEffectJson.Definition PiecesDefinition(Recipe recipe)
	{
		var d = new Engine_FxEffectJson.Definition { Name = PiecesEffectName(recipe) };
		if (recipe.Pieces is not null)
		{
			d.Pieces.AddRange(recipe.Pieces);
		}

		return d;
	}

	private static string _contributedPieces = string.Empty;

	/// <summary>Every recipe's pieces stated into Godot's bank, when they changed.</summary>
	private static void ContributePieces(IReadOnlyList<Recipe> recipes)
	{
		var sources = new List<Engine_FxModEffects.Source>();
		var signature = new System.Text.StringBuilder();
		foreach (Recipe recipe in recipes)
		{
			if (recipe.Pieces is not { Count: > 0 })
			{
				continue;
			}

			sources.Add(new Engine_FxModEffects.Source(recipe.Mod, RecipesPath, PiecesDefinition(recipe)));
			signature.Append(recipe.Id).Append('|');
			foreach (Engine_FxRecipePieces.Piece piece in recipe.Pieces)
			{
				signature.Append(piece).Append('|');
			}
		}

		string text = signature.ToString();
		if (text == _contributedPieces)
		{
			return;
		}

		_contributedPieces = text;
		Engine_FxModEffects.Contribute(RecipesPath, sources);
	}

	/// <summary>
	/// The bank <paramref name="recipe"/>'s pieces draw from: the shared one when it states them as
	/// this recipe does, else a preview bank with them (a recipe being edited in the FX Lab).
	/// </summary>
	private static FxBank? PiecesBank(Recipe recipe)
	{
		FxBank? shared = FableFx.SharedBank();
		if (shared is null)
		{
			return null;
		}

		FxEffect? stated = shared.FindByName(PiecesEffectName(recipe));
		return stated is not null && recipe.Pieces is not null && stated.Pieces.SequenceEqual(recipe.Pieces)
			? null
			: Engine_FxModEffects.PreviewBank(shared, PiecesDefinition(recipe));
	}

	/// <summary>One mod's <c>fx/recipes.json</c> text as recipes; malformed entries are skipped with a warning.</summary>
	public static List<Recipe> Parse(string mod, string text)
	{
		var recipes = new List<Recipe>();
		Variant parsed = Json.ParseString(text);
		if (parsed.VariantType != Variant.Type.Dictionary ||
			!parsed.AsGodotDictionary().TryGetValue("recipes", out Variant list) ||
			list.VariantType != Variant.Type.Array)
		{
			GD.PushWarning($"[fx-recipes] {mod}:{RecipesPath} has no \"recipes\" array; ignored");
			return recipes;
		}

		foreach (Variant value in list.AsGodotArray())
		{
			if (value.VariantType != Variant.Type.Dictionary)
			{
				continue;
			}

			var record = value.AsGodotDictionary();
			string Text(string key) => record.TryGetValue(key, out Variant v) ? v.AsString() : string.Empty;
			float Number(string key, float fallback) =>
				record.TryGetValue(key, out Variant v) && v.VariantType is Variant.Type.Float or Variant.Type.Int
					? (float)v.AsDouble()
					: fallback;

			var when = new List<string>();
			if (record.TryGetValue("when", out Variant whenValue))
			{
				if (whenValue.VariantType == Variant.Type.Array)
				{
					foreach (Variant item in whenValue.AsGodotArray())
					{
						when.Add(item.AsString());
					}
				}
				else
				{
					when.Add(whenValue.AsString());
				}
			}

			var baseNames = new List<string>();
			if (record.TryGetValue("base", out Variant baseValue) && baseValue.VariantType == Variant.Type.Array)
			{
				foreach (Variant item in baseValue.AsGodotArray())
				{
					string name = item.AsString();
					if (name.Length != 0)
					{
						baseNames.Add(name);
					}
				}
			}

			Color tint = Colors.White;
			if (record.TryGetValue("tint", out Variant tintValue) && tintValue.VariantType == Variant.Type.Array)
			{
				var parts = tintValue.AsGodotArray();
				if (parts.Count >= 3)
				{
					tint = new Color((float)parts[0].AsDouble(), (float)parts[1].AsDouble(), (float)parts[2].AsDouble());
				}
			}

			var attach = new List<Attachment>();
			if (record.TryGetValue("attach", out Variant attachValue) && attachValue.VariantType == Variant.Type.Array)
			{
				foreach (Variant item in attachValue.AsGodotArray())
				{
					if (item.VariantType != Variant.Type.Dictionary)
					{
						continue;
					}

					var a = item.AsGodotDictionary();
					float A(string key, float fallback) =>
						a.TryGetValue(key, out Variant v) && v.VariantType is Variant.Type.Float or Variant.Type.Int
							? (float)v.AsDouble()
							: fallback;
					string effect = a.TryGetValue("effect", out Variant e) ? e.AsString() : string.Empty;
					if (effect.Length == 0)
					{
						continue;
					}

					Color? attachTint = null;
					if (a.TryGetValue("tint", out Variant at) && at.VariantType == Variant.Type.Array && at.AsGodotArray() is { Count: >= 3 } tintParts)
					{
						attachTint = new Color((float)tintParts[0].AsDouble(), (float)tintParts[1].AsDouble(), (float)tintParts[2].AsDouble());
					}

					attach.Add(new Attachment(
						effect, A("scale", 1f), (int)A("count", 1f), A("radius", 0f), A("height", 0f),
						A("turnsPerSecond", 0f), A("intensity", 1f), (int)A("stack", 1f), A("stackStep", 0.6f), A("stagger", 0f), attachTint,
						A("radiusStep", 0f), A("scaleStep", 1f), A("fadeIn", 0f)));
				}
			}

			var anchorNames = new List<string>();
			if (record.TryGetValue("anchor", out Variant anchorValue) && anchorValue.VariantType == Variant.Type.Array)
			{
				foreach (Variant item in anchorValue.AsGodotArray())
				{
					string name = item.AsString();
					if (name.Length != 0)
					{
						anchorNames.Add(name);
					}
				}
			}

			Light? glow = null;
			if (record.TryGetValue("light", out Variant lightValue) && lightValue.VariantType == Variant.Type.Dictionary)
			{
				var l = lightValue.AsGodotDictionary();
				float L(string key, float fallback) =>
					l.TryGetValue(key, out Variant v) && v.VariantType is Variant.Type.Float or Variant.Type.Int ? (float)v.AsDouble() : fallback;
				Color colour = Colors.White;
				if (l.TryGetValue("color", out Variant c) && c.VariantType == Variant.Type.Array && c.AsGodotArray() is { Count: >= 3 } parts)
				{
					colour = new Color((float)parts[0].AsDouble(), (float)parts[1].AsDouble(), (float)parts[2].AsDouble());
				}

				glow = new Light(colour, L("energy", 4f), L("range", 6f), L("height", 1f), L("flicker", 0f));
			}

			var pieces = new List<Engine_FxRecipePieces.Piece>();
			if (record.TryGetValue("pieces", out Variant piecesValue) && piecesValue.VariantType == Variant.Type.Array)
			{
				foreach (Variant item in piecesValue.AsGodotArray())
				{
					if (item.VariantType == Variant.Type.Dictionary && ParsePiece(item.AsGodotDictionary()) is { } piece)
					{
						pieces.Add(piece);
					}
				}
			}

			string with = Text("with");
			float withRadius = Number("withRadius", 10f);

			string id = Text("id");
			if (id.Length == 0 || (when.Count == 0 && with.Length == 0) || baseNames.Count == 0)
			{
				GD.PushWarning($"[fx-recipes] {mod}: a recipe needs id, when (or with) and base; skipped");
				continue;
			}

			bool everyNode = record.TryGetValue("everyNode", out Variant everyValue) && everyValue.AsBool();
			var entityNames = new List<string>();
			if (record.TryGetValue("entities", out Variant entitiesValue) && entitiesValue.VariantType == Variant.Type.Array)
			{
				foreach (Variant item in entitiesValue.AsGodotArray())
				{
					string name = item.AsString();
					if (name.Length != 0)
					{
						entityNames.Add(name);
					}
				}
			}

			recipes.Add(Make(id, mod, when, Number("equals", 1f), baseNames, tint, Number("intensity", 1f), Number("size", 1f), attach, anchorNames, glow, pieces, everyNode, entityNames, with, withRadius));
		}

		return recipes;
	}

	private static Color ColourOf(Godot.Collections.Dictionary d, string key, Color fallback)
	{
		if (d.TryGetValue(key, out Variant v) && v.VariantType == Variant.Type.Array && v.AsGodotArray() is { Count: >= 3 } parts)
		{
			return new Color((float)parts[0].AsDouble(), (float)parts[1].AsDouble(), (float)parts[2].AsDouble(), parts.Count >= 4 ? (float)parts[3].AsDouble() : 1f);
		}

		return fallback;
	}

	private static Engine_FxRecipePieces.Piece? ParsePiece(Godot.Collections.Dictionary d)
	{
		float P(string key, float fallback) =>
			d.TryGetValue(key, out Variant v) && v.VariantType is Variant.Type.Float or Variant.Type.Int ? (float)v.AsDouble() : fallback;
		string S(string key) => d.TryGetValue(key, out Variant v) ? v.AsString() : string.Empty;
		string kind = S("kind");
		if (kind.Length == 0)
		{
			return null;
		}

		Color colour = ColourOf(d, "color", Colors.White);
		return new Engine_FxRecipePieces.Piece(
			kind, S("texture"), colour, ColourOf(d, "colorEnd", colour with { A = 0f }),
			P("height", 2f), P("radius", 1f), P("radiusTop", P("radius", 1f)), P("rate", 10f), P("speed", 1f),
			P("size", 0.5f), P("sizeEnd", P("size", 0.5f) * 0.4f), (int)P("count", 64f), P("period", 1f), P("width", 0.1f),
			S("mesh"), P("tilt", 20f), P("intensity", 1f), S("blend").Length != 0 ? S("blend") : "add");
	}

	private static Godot.Collections.Dictionary SerializePiece(Engine_FxRecipePieces.Piece p)
	{
		static Godot.Collections.Array Rgba(Color c) => new() { c.R, c.G, c.B, c.A };
		var one = new Godot.Collections.Dictionary
		{
			["kind"] = p.Kind, ["color"] = Rgba(p.Colour), ["colorEnd"] = Rgba(p.ColourEnd),
			["height"] = p.Height, ["radius"] = p.Radius, ["radiusTop"] = p.RadiusTop, ["rate"] = p.Rate, ["speed"] = p.Speed,
			["size"] = p.Size, ["sizeEnd"] = p.SizeEnd, ["count"] = p.Count, ["period"] = p.Period, ["width"] = p.Width,
			["tilt"] = p.Tilt, ["intensity"] = p.Intensity,
		};
		if (p.Texture.Length != 0) one["texture"] = p.Texture;
		if (p.Mesh.Length != 0) one["mesh"] = p.Mesh;
		if (p.Blend != "add") one["blend"] = p.Blend;
		return one;
	}

	/// <summary>A recipe from its parts, the base names hashed as the banks key them.</summary>
	public static Recipe Make(
		string id, string mod, IReadOnlyList<string> when, float match, IReadOnlyList<string> baseNames,
		Color tint, float intensity, float size, IReadOnlyList<Attachment> attach,
		IReadOnlyList<string>? anchorNames = null, Light? glow = null, IReadOnlyList<Engine_FxRecipePieces.Piece>? pieces = null,
		bool everyNode = false, IReadOnlyList<string>? entityNames = null, string with = "", float withRadius = 10f)
	{
		static HashSet<uint> Hashes(IReadOnlyList<string> names)
		{
			var hashes = new HashSet<uint>();
			foreach (string name in names)
			{
				hashes.Add(Engine_BnkFxLoader.TryParseHashToken(name, out uint token) ? token : FxBank.Fnv1Lower(name));
			}

			return hashes;
		}

		// An entity definition's hash is FNV1 of its name as written (the entity factory's own
		// hashing, sub_8238B9C8), or a #hash token.
		static HashSet<uint>? EntityHashes(IReadOnlyList<string>? names)
		{
			if (names is not { Count: > 0 })
			{
				return null;
			}

			var hashes = new HashSet<uint>();
			foreach (string name in names)
			{
				hashes.Add(Engine_BnkFxLoader.TryParseHashToken(name, out uint token) ? token : Refii.Core.Engine_GameHash.Fnv1(name));
			}

			return hashes;
		}

		IReadOnlyList<string> anchors = anchorNames ?? System.Array.Empty<string>();
		return new Recipe(id, mod, when, match, Hashes(baseNames), tint, intensity, size, attach, baseNames, Hashes(anchors), anchors, glow, pieces, everyNode,
			EntityHashes(entityNames), entityNames is { Count: > 0 } ? entityNames : null, with, withRadius);
	}

	/// <summary>The recipes as a <c>fx/recipes.json</c> text, the inverse of <see cref="Parse"/>.</summary>
	public static string Serialize(IEnumerable<Recipe> recipes)
	{
		var list = new Godot.Collections.Array();
		foreach (Recipe recipe in recipes)
		{
			var record = new Godot.Collections.Dictionary
			{
				["id"] = recipe.Id,
				["base"] = new Godot.Collections.Array(System.Linq.Enumerable.Select(recipe.BaseNames, b => Variant.CreateFrom(b))),
				["tint"] = new Godot.Collections.Array { recipe.Tint.R, recipe.Tint.G, recipe.Tint.B },
				["intensity"] = recipe.Intensity,
			};
			if (recipe.When.Count != 0)
			{
				record["when"] = new Godot.Collections.Array(System.Linq.Enumerable.Select(recipe.When, w => Variant.CreateFrom(w)));
				record["equals"] = recipe.Match;
			}

			if (recipe.Size != 1f)
			{
				record["size"] = recipe.Size;
			}

			if (recipe.AnchorNames.Count != 0)
			{
				record["anchor"] = new Godot.Collections.Array(System.Linq.Enumerable.Select(recipe.AnchorNames, a => Variant.CreateFrom(a)));
			}

			if (recipe.EveryNode)
			{
				record["everyNode"] = true;
			}

			if (recipe.EntityNames is { Count: > 0 } entityNames)
			{
				record["entities"] = new Godot.Collections.Array(System.Linq.Enumerable.Select(entityNames, e => Variant.CreateFrom(e)));
			}

			if (recipe.With.Length != 0)
			{
				record["with"] = recipe.With;
				if (recipe.WithRadius != 10f)
				{
					record["withRadius"] = recipe.WithRadius;
				}
			}

			if (recipe.Glow is { } glow)
			{
				record["light"] = new Godot.Collections.Dictionary
				{
					["color"] = new Godot.Collections.Array { glow.Colour.R, glow.Colour.G, glow.Colour.B },
					["energy"] = glow.Energy, ["range"] = glow.Range, ["height"] = glow.Height, ["flicker"] = glow.Flicker,
				};
			}

			var attach = new Godot.Collections.Array();
			foreach (Attachment a in recipe.Attach)
			{
				var one = new Godot.Collections.Dictionary { ["effect"] = a.Effect, ["count"] = a.Count };
				if (a.Scale != 1f) one["scale"] = a.Scale;
				if (a.Radius != 0f) one["radius"] = a.Radius;
				if (a.Height != 0f) one["height"] = a.Height;
				if (a.TurnsPerSecond != 0f) one["turnsPerSecond"] = a.TurnsPerSecond;
				if (a.Intensity != 1f) one["intensity"] = a.Intensity;
				if (a.Stack > 1) { one["stack"] = a.Stack; one["stackStep"] = a.StackStep; }
				if (a.Stagger > 0f) one["stagger"] = a.Stagger;
				if (a.Tint is { } tint) one["tint"] = new Godot.Collections.Array { tint.R, tint.G, tint.B };
				if (a.RadiusStep != 0f) one["radiusStep"] = a.RadiusStep;
				if (a.ScaleStep != 1f) one["scaleStep"] = a.ScaleStep;
				if (a.FadeIn > 0f) one["fadeIn"] = a.FadeIn;
				attach.Add(one);
			}

			record["attach"] = attach;
			if (recipe.Pieces is { Count: > 0 })
			{
				var pieces = new Godot.Collections.Array();
				foreach (Engine_FxRecipePieces.Piece p in recipe.Pieces)
				{
					pieces.Add(SerializePiece(p));
				}

				record["pieces"] = pieces;
			}

			list.Add(record);
		}

		var root = new Godot.Collections.Dictionary { ["recipes"] = list };
		string json = Json.Stringify(root, "\t");
		GC.KeepAlive(root);
		return json;
	}
}

/// <summary>A recipe's light: an omni light that wavers by <see cref="Flicker"/> of its energy.</summary>
public partial class Engine_FxRecipeLight : OmniLight3D
{
	public float BaseEnergy { get; set; } = 1f;

	public float Flicker { get; set; }

	private float _phase;

	public override void _Process(double delta)
	{
		if (Flicker <= 0f)
		{
			return;
		}

		_phase += (float)delta;
		float wave = (Mathf.Sin(_phase * 23f) * 0.6f) + (Mathf.Sin(_phase * 7.3f) * 0.4f);
		LightEnergy = BaseEnergy * (1f + (Flicker * wave));
	}
}

/// <summary>A ring of attached effects turning about the base effect's origin.</summary>
public partial class Engine_FxOrbit : Node3D
{
	public float TurnsPerSecond { get; set; }

	public override void _Process(double delta)
	{
		if (TurnsPerSecond != 0f)
		{
			RotateY(TurnsPerSecond * Mathf.Tau * (float)delta);
		}
	}
}
