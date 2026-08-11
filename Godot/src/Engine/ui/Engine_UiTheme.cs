using System.Collections.Generic;
using Godot;

using Refii.Text;

namespace Refii.Ui;

/// <summary>
/// The shipped game's look, in a form a screen we AUTHORED can draw with.
///
/// The front end is Godot's now. That is easy while every screen has a <c>.bgf</c> behind it
/// — <see cref="Engine_UiPresentationHost"/> stands one up and the picture is the original's
/// by construction. It stops being easy the moment we want a screen the original never had:
/// a mod manager, a graphics page, a multiplayer lobby. Those have no presentation to stand
/// up, and the two ways out of that are both bad on their own. Author art ourselves and the
/// new screen looks like a different game bolted onto this one. Author nothing and the
/// feature does not exist.
///
/// So this is the third way: a screen is composed from Godot nodes, and every visual it uses
/// is SOURCED — the fonts the game ships, the strings from the game's own book, and art
/// cloned out of a real presentation by name. A custom screen is then new LAYOUT over old
/// PIXELS, which is the only combination that reads as the same game.
///
/// NOTHING HERE IS EXTRACTED. The art catalogue is built by walking a presentation that is
/// already standing up in the scene tree and duplicating its nodes, so the "conversion" is a
/// Godot node duplicate in memory and no derived asset is ever written. That is not incidental
/// tidiness — converted Fable 2 data must not reach disk.
///
/// WHAT IT DELIBERATELY IS NOT: a widget toolkit. There is no button class, no layout
/// container, no focus manager. Those are decisions a screen should make for itself, and the
/// one screen that has them — <see cref="Engine_FrontEndMenuView"/> — takes every number from
/// shipped Lua rather than from a house style. This holds the RESOURCES; the screen holds the
/// design.
/// </summary>
public sealed class Engine_UiTheme
{
	/// <summary>The bitmap fonts. Without them <see cref="Label"/> produces nothing.</summary>
	public Engine_UiFontLibrary? Fonts { get; init; }

	/// <summary>The localised string database a text tag resolves through.</summary>
	public Engine_BabelDatabase? Strings { get; init; }

	/// <summary>
	/// The canvas a sourced screen was authored on, and therefore the space a custom screen
	/// should place itself in.
	///
	/// Read off the presentation in <see cref="Catalogue"/> rather than assumed. It is 1280x720
	/// on the front-end screens, but the value belongs to the file — a screen that hardcodes it
	/// is a screen that mis-places itself the first time it is pointed at a presentation
	/// authored differently.
	/// </summary>
	public Vector2 Canvas { get; private set; } = new(1280f, 720f);

	/// <summary>The presentation the art was catalogued from, for diagnostics. Empty until then.</summary>
	public string ArtSource { get; private set; } = string.Empty;

	private readonly Dictionary<string, List<Node3D>> _art = new();

	/// <summary>How many distinct element names the catalogue holds.</summary>
	public int ArtCount => _art.Count;

	/// <summary>Every catalogued name, for a tool that wants to list what is available.</summary>
	public IReadOnlyCollection<string> ArtNames => _art.Keys;

	/// <summary>
	/// Walks a built presentation and records every named element as clonable art.
	///
	/// NAMES REPEAT. The title screen has 77 elements sharing 47 names, so a name is not a
	/// key — each one keeps the full list in the tree's own depth-first order, and
	/// <see cref="Art"/> takes an index. Collapsing them to the first would silently hand back
	/// the wrong tab the moment a presentation reuses a name, and that failure looks like a
	/// layout bug rather than a lookup one.
	///
	/// Elements the scene builder SYNTHESISED a name for are skipped: those names are
	/// <c>Type_Ordinal</c>, they are not authored, and they would change under any edit to the
	/// parser's traversal. Only a name the artist typed is a stable handle.
	/// </summary>
	public void Catalogue(Node presentation, string sourceName = "")
	{
		_art.Clear();
		ArtSource = sourceName;

		if (presentation.HasMeta("canvas_width") && presentation.HasMeta("canvas_height"))
		{
			Canvas = new Vector2(
				(float)presentation.GetMeta("canvas_width"),
				(float)presentation.GetMeta("canvas_height"));
		}

		Walk(presentation);
	}

	/// <summary>
	/// Forgets the catalogue, for when the presentation it was taken from is being freed.
	///
	/// Every entry is a node inside that tree, so leaving them behind hands the next caller a
	/// dangling reference. Clones already taken out are unaffected — that is what they are for.
	/// </summary>
	public void Clear()
	{
		_art.Clear();
		ArtSource = string.Empty;
	}

	private void Walk(Node node)
	{
		if (node is Node3D element && !element.HasMeta("name_synthesised"))
		{
			string name = element.Name.ToString();
			if (name.Length > 0)
			{
				if (!_art.TryGetValue(name, out List<Node3D>? matches))
				{
					matches = new List<Node3D>();
					_art[name] = matches;
				}

				matches.Add(element);
			}
		}

		foreach (Node child in node.GetChildren())
		{
			Walk(child);
		}
	}

	/// <summary>
	/// How many elements in the catalogue carry a given name — 0 when there are none.
	///
	/// Worth asking before <see cref="Art"/> on anything a screen depends on: a count of 0 is
	/// "this presentation does not carry that art" and a count above 1 is "you are about to
	/// pick one of several", and those want different handling.
	/// </summary>
	public int ArtVariants(string name) =>
		_art.TryGetValue(name, out List<Node3D>? matches) ? matches.Count : 0;

	/// <summary>
	/// A fresh CLONE of a catalogued element, ready to parent into a custom screen.
	///
	/// A clone rather than the node itself, because the catalogued node is still live inside
	/// the presentation it came from — reparenting it would take the art out of the screen it
	/// belongs to, which is a bug that only shows up when both screens are on at once.
	///
	/// The clone's transform is ZEROED. The original sits wherever the artist placed it in the
	/// presentation, and a custom screen wants the art and its own placement, not the art and
	/// somebody else's. Callers that want the authored position can read it back off the node
	/// <see cref="Art"/> was cloned from.
	///
	/// Returns null when the name is not catalogued, which is not a failure — it means this
	/// presentation is not the one carrying that piece — so a screen can degrade to text
	/// rather than invent a substitute.
	/// </summary>
	public Node3D? Art(string name, int variant = 0)
	{
		if (!_art.TryGetValue(name, out List<Node3D>? matches) ||
			variant < 0 || variant >= matches.Count)
		{
			return null;
		}

		var clone = (Node3D)matches[variant].Duplicate();
		clone.Position = Vector3.Zero;
		clone.Rotation = Vector3.Zero;
		clone.Scale = Vector3.One;
		clone.Visible = true;
		return clone;
	}

	/// <summary>The element a clone would be taken from, for its authored transform.</summary>
	public Node3D? ArtSourceNode(string name, int variant = 0) =>
		_art.TryGetValue(name, out List<Node3D>? matches) && variant >= 0 && variant < matches.Count
			? matches[variant]
			: null;

	/// <summary>
	/// A tag to its display string, through the game's own book.
	///
	/// An unresolved tag is returned VERBATIM rather than blanked. That is what the engine does
	/// — <see cref="Engine_UiScreen"/> falls back the same way — and it makes a missing string
	/// announce itself on screen instead of leaving a hole that reads as a layout fault.
	/// </summary>
	public string Resolve(string tag) =>
		Strings is not null && Strings.TryGetString(tag, out string value) ? value : tag;

	/// <summary>
	/// Builds a text mesh in the game's own fonts, as a node ready to parent.
	///
	/// <paramref name="colour"/> carries the alpha, so a caller can fade a label without
	/// touching the material afterwards. The atlas is chosen by POINT SIZE through
	/// <see cref="Engine_UiFontLibrary.For"/> — the game ships three buckets and picks between
	/// them the same way, so asking for an arbitrary size gets the nearest shipped face scaled,
	/// not a resampled one.
	///
	/// Returns a MeshInstance3D with no mesh when there is no font library or the string is
	/// empty, rather than null: a caller laying out rows wants a node to position either way,
	/// and an empty one draws nothing.
	/// </summary>
	public MeshInstance3D Label(
		string display, int pointSize, Color colour,
		int horizontalAlign = 0, int verticalAlign = 1,
		float boxWidth = 0f, float boxHeight = 0f, int wrapMode = 0)
	{
		var node = new MeshInstance3D { Name = "Label" };
		(ArrayMesh? mesh, Texture2D? atlas) = Glyphs(
			display, pointSize, colour, horizontalAlign, verticalAlign,
			boxWidth, boxHeight, wrapMode);
		if (mesh is null)
		{
			return node;
		}

		node.Mesh = mesh;
		node.MaterialOverride = Engine_UiSceneBuilder.TextMaterial(colour, atlas);
		return node;
	}

	/// <summary>
	/// The glyph mesh and its atlas, without a node wrapped round them.
	///
	/// <see cref="Label"/> is the convenient form and this is the one a LIVE screen wants: a
	/// view that keeps persistent nodes — because that is where an animation has to hang — can
	/// retext a row in place instead of discarding and rebuilding the node it is tweening.
	///
	/// Returns (null, null) for an empty string or a missing font library, which the caller
	/// should treat as "this row draws nothing" rather than as an error: a screen without the
	/// game's fonts still has a correct layout, and reporting the count of empty labels is how
	/// "no fonts" is told apart from "no text".
	/// </summary>
	public (ArrayMesh? Mesh, Texture2D? Atlas) Glyphs(
		string display, int pointSize, Color colour,
		int horizontalAlign = 0, int verticalAlign = 1,
		float boxWidth = 0f, float boxHeight = 0f, int wrapMode = 0)
	{
		if (display.Length == 0 || Fonts?.For(pointSize) is not { } selection)
		{
			return (null, null);
		}

		(UiFont font, float scale) = selection;

		var text = new UiText
		{
			Display = display,
			Size = pointSize,
			HorzAlign = horizontalAlign,
			VertAlign = verticalAlign,
			WrapMode = wrapMode,
			BoxWidth = boxWidth,
			BoxHeight = boxHeight,
			R = colour.R,
			G = colour.G,
			B = colour.B,
		};

		ArrayMesh? mesh = Engine_UiTextMesh.Build(text, font.Font, scale, out _);
		return mesh is null ? (null, null) : (mesh, font.Atlas);
	}

	/// <summary>
	/// Retints an existing label without rebuilding its glyphs.
	///
	/// The one operation a live screen does constantly — a row changing slot changes its
	/// opacity every frame it scrolls — and rebuilding the mesh for it would be the only place
	/// a menu could cost anything. Silently does nothing if the node was never given a
	/// material, which is the state <see cref="Label"/> leaves an empty string in.
	/// </summary>
	public static void Retint(MeshInstance3D label, Color colour)
	{
		if (label.MaterialOverride is StandardMaterial3D material)
		{
			material.AlbedoColor = colour;
		}
	}

	/// <summary>Depth-first search by node name. Godot's own find is by path or by pattern.</summary>
	public static Node? FindByName(Node root, string name)
	{
		if (root.Name == name)
		{
			return root;
		}

		foreach (Node child in root.GetChildren())
		{
			if (FindByName(child, name) is { } hit)
			{
				return hit;
			}
		}

		return null;
	}

	/// <summary>A one-line description of what was sourced, for a tool's log.</summary>
	public string Describe() =>
		$"[ui-theme] art={ArtCount} names from '{(ArtSource.Length == 0 ? "nothing" : ArtSource)}'  " +
		$"canvas={Canvas.X:F0}x{Canvas.Y:F0}  " +
		$"fonts={(Fonts is null || Fonts.IsEmpty ? "MISSING" : Fonts.Directory)}  " +
		$"strings={(Strings is null ? "MISSING" : "loaded")}";
}
