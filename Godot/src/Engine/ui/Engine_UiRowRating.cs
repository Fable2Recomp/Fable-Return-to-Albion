using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// Puts a row's RATING on its cloned art — the star strip and the augment pips.
///
/// The rating is a SLIDE, not a value: `SetAttributesToVisibleIndex` ends in
/// `goToSlide(stars, n)` and `goToSlide(pip, 1|2|3)`, and a slide is a set of per-element
/// overrides, so "three and a half stars" is a different subset of the strip's quads being
/// active. <see cref="Engine_UiMenuFormatter.RatingFor"/> decodes the number into slide indices;
/// this turns the indices into what is drawn.
///
/// Resolved once per slide from the document: `ComputeSlideState` answers which elements are
/// active on slide N, and the answers are cached because they are properties of the FILE.
/// </summary>
public sealed class Engine_UiRowRating
{
	/// <summary>The strip and the pip group, named as the formatter reaches them.</summary>
	public const string StarsPath = "Tab.Stars";

	public const string PipsPathPrefix = "Tab.Pips.Group.pips.pip";

	/// <summary>Active-or-not per descendant of the star strip, indexed by slide then by the
	/// descendant's position in the subtree walk.</summary>
	private readonly Dictionary<int, Dictionary<string, bool>> _stars = new();

	/// <summary>The same, per pip, for the three pip slides.</summary>
	private readonly Dictionary<int, Dictionary<string, bool>>[] _pips =
		new Dictionary<int, Dictionary<string, bool>>[Engine_UiMenuFormatter.PipCount];

	private Engine_UiRowRating()
	{
		for (int i = 0; i < _pips.Length; ++i)
		{
			_pips[i] = new Dictionary<int, Dictionary<string, bool>>();
		}
	}

	/// <summary>
	/// Reads every rating state out of a screen's own document, or null when the screen's button
	/// pool carries no rating art at all — which is most of them.
	/// </summary>
	public static Engine_UiRowRating? Resolve(Engine_UiScreen? screen)
	{
		if (screen?.Document is null ||
			Engine_ExpandableMenuItems.FindGroup(screen) is not { } pool)
		{
			return null;
		}

		// The FIRST authored button is the one the row art is cloned from, so its ordinals are
		// the ones a clone's walk lines up with.
		UiNode? button = null;
		foreach (UiNode child in pool.Children)
		{
			if (child.Type == BgfElementType.Component)
			{
				button = child;
				break;
			}
		}

		if (button is null || Descend(button, StarsPath) is not { } stars)
		{
			return null;
		}

		var rating = new Engine_UiRowRating();
		for (int slide = 1; slide <= Engine_UiMenuConstants.EmptyRatingSlide; ++slide)
		{
			rating._stars[slide] = StateFor(screen, stars, slide);
		}

		for (int i = 0; i < Engine_UiMenuFormatter.PipCount; ++i)
		{
			if (Descend(button, $"{PipsPathPrefix}{i + 1}") is not { } pip)
			{
				continue;
			}

			for (int slide = Engine_UiMenuFormatter.RowRating.PipEmpty;
				slide <= Engine_UiMenuFormatter.RowRating.PipFull; ++slide)
			{
				rating._pips[i][slide] = StateFor(screen, pip, slide);
			}
		}

		return rating;
	}

	/// <summary>Puts one row's rating on its cloned frame.</summary>
	public void Apply(Node3D frame, Engine_UiMenuFormatter.RowRating rating)
	{
		if (Engine_UiTheme.FindByPath(frame, StarsPath.Replace('.', '/')) is { } stars &&
			_stars.TryGetValue(rating.StarSlide, out Dictionary<string, bool>? starState))
		{
			Light(stars, starState);
		}

		for (int i = 0; i < rating.PipSlides.Length && i < _pips.Length; ++i)
		{
			if (Engine_UiTheme.FindByPath(
					frame, $"{PipsPathPrefix}{i + 1}".Replace('.', '/')) is { } pip &&
				_pips[i].TryGetValue(rating.PipSlides[i], out Dictionary<string, bool>? pipState))
			{
				Light(pip, pipState);
			}
		}
	}

	/// <summary>
	/// Which descendants of one element are active when it sits on a given slide.
	///
	/// Slide 0 is the master, which `ComputeSlideState` already folds in as the baseline, so the
	/// guest's numbering counts the rest from one — the same convention
	/// <see cref="Engine_UiElementResolve"/> follows.
	/// </summary>
	private static System.Collections.Generic.Dictionary<string, bool> StateFor(
		Engine_UiScreen screen, UiNode root, int slide)
	{
		BgfSlideState state = screen.Document.ComputeSlideState((owner, owned) =>
		{
			if (owner != root.Ordinal || slide <= 0)
			{
				return null;
			}

			int seen = 0;
			foreach (BgfSlide candidate in owned)
			{
				if (!candidate.IsMaster && ++seen == slide)
				{
					return candidate;
				}
			}

			return null;
		});

		// Keyed by PATH, not by walk position: the document and the scene are not the same shape
		// — the builder drops childless `Material`/`Texture` elements and the pass-throughs above
		// them, drops `Behavior` elements, and the menu view deletes the row's authored `Text`.
		// A path key cannot drift; a node the builder never emitted is never looked up.
		var walk = new System.Collections.Generic.Dictionary<string, bool>(
			System.StringComparer.Ordinal);
		Collect(root, string.Empty);
		return walk;

		void Collect(UiNode node, string prefix)
		{
			foreach (UiNode child in node.Children)
			{
				// The SANITISED name, because that is what the built node is called.
				string path = prefix.Length == 0
					? Engine_UiSceneBuilder.NodeNameFor(child.Name)
					: $"{prefix}/{Engine_UiSceneBuilder.NodeNameFor(child.Name)}";

				// First writer wins: two siblings can share a name in the document, and the
				// scene walk below resolves the same collision the same way.
				if (!walk.ContainsKey(path))
				{
					walk[path] = child.Ordinal >= 0 && child.Ordinal < state.Active.Length &&
						state.Active[child.Ordinal];
				}

				Collect(child, path);
			}
		}
	}

	/// <summary>Writes an active set onto a cloned subtree, joined by path. See
	/// <see cref="StateFor"/> for why it is not joined by walk order.</summary>
	private static void Light(Node3D root, System.Collections.Generic.Dictionary<string, bool> state)
	{
		Walk(root, string.Empty);

		void Walk(Node node, string prefix)
		{
			foreach (Node child in node.GetChildren())
			{
				if (child is not Node3D spatial)
				{
					continue;
				}

				string path = prefix.Length == 0
					? spatial.Name.ToString()
					: $"{prefix}/{spatial.Name}";

				// A node the document does not account for is left alone, not hidden — the
				// clone can carry nodes this slide's state says nothing about.
				if (state.TryGetValue(path, out bool active))
				{
					spatial.Visible = active;
				}

				Walk(spatial, path);
			}
		}
	}

	/// <summary>Follows a dotted authored path down from an element.</summary>
	private static UiNode? Descend(UiNode from, string path)
	{
		UiNode? at = from;
		foreach (string segment in path.Split('.'))
		{
			if (at is null)
			{
				return null;
			}

			UiNode? next = null;
			foreach (UiNode child in at.Children)
			{
				if (string.Equals(child.Name, segment, System.StringComparison.Ordinal))
				{
					next = child;
					break;
				}
			}

			at = next;
		}

		return at;
	}
}
