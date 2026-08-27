using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// A Godot-owned info panel over a mirrored screen — an item's name, price and description, and
/// the same shape for every other screen that has one.
///
/// The guest states the finished strings: `sub_829B6B50` builds the item panel's text from the
/// GDB and writes each result onto a real element with `sub_82425F40(element, string)`, so the
/// panel reads live text per ordinal and needs only the guest's dotted element PATHS — never the
/// `InventoryItemComponent` chain re-walked here.
///
/// Owning it buys host-side layout and truncation (<see cref="Engine_UiTextMesh"/> has no
/// truncate mode). The guest's own elements for these fields are hidden by the adopter — see
/// <c>GameUi</c>.
/// </summary>
public sealed partial class Engine_UiScreenPanel : Node3D
{
	/// <summary>
	/// One field of a panel: the guest's dotted element path (the join — never paraphrased), and
	/// how we choose to draw it.
	/// </summary>
	/// <param name="Path">The guest's own dotted element path.</param>
	/// <param name="Multiline">True to lay the string out as wrapped paragraphs.</param>
	/// <param name="Alternate">
	/// True when another entry names the same field under a different spelling (clothing calls
	/// the base-value label `ItemValue2`, items and gift call it `ItemValueName`), so only one
	/// can resolve and absence is not counted in <see cref="PathsUnresolved"/>.
	/// </param>
	public readonly record struct Field(
		string Path, bool Multiline = false, bool Alternate = false);

	/// <summary>The fields this panel draws, in the order the guest's screen states them.</summary>
	public IReadOnlyList<Field> Fields { get; set; } = System.Array.Empty<Field>();

	/// <summary>The shipped look — fonts, strings and the text material.</summary>
	public Engine_UiTheme? Theme { get; set; }

	/// <summary>How many fields carried live text on the last read.</summary>
	public int FieldsDrawn { get; private set; }

	/// <summary>Paths that are not in this screen's document at all — a name that has moved.</summary>
	public int PathsUnresolved { get; private set; }

	/// <summary>
	/// Which paths are missing, and which resolved but carry nothing — named, not counted, so a
	/// wrong field list is identifiable from one run's report.
	/// </summary>
	private readonly List<string> _missing = new();
	private readonly List<string> _empty = new();

	/// <summary>
	/// The paths that resolved on this screen — the ones we have taken over. Ownership is not
	/// "currently has text": an empty field is still ours, or the guest's copy blinks back.
	/// </summary>
	private readonly List<string> _owned = new();

	/// <summary>Every field this panel has taken over, whether or not it is drawing today.</summary>
	public IReadOnlyList<string> Owned => _owned;

	private readonly Dictionary<string, MeshInstance3D> _labels = new();
	private readonly Dictionary<string, string> _shown = new();

	/// <summary>Each field's element, resolved once per document. Null means "not in it".</summary>
	private readonly Dictionary<string, UiNode?> _resolved = new();

	/// <summary>The document <see cref="_resolved"/> was built against.</summary>
	private Engine_UiScreen? _resolvedFor;

	/// <summary>
	/// The built node each field is placed from, cached per path. Re-resolved whenever it goes
	/// invalid: the tree is freed on every guest state change and a stale node throws on access.
	/// </summary>
	private readonly Dictionary<string, Node3D> _placedBy = new();

	/// <summary>
	/// Reads each field's live text off the mirrored screen and draws it. Keyed by PATH, not by
	/// ordinal, so a screen rebuild cannot repoint a field at a different element. Returns true
	/// when anything changed.
	/// </summary>
	/// <param name="built">
	/// The built tree for this screen, used only to place each field. The position must come
	/// from the builder: summing `PositionX/Y` along the parent chain drops scale and pivot, and
	/// an origin is `position - basis*pivot`.
	/// </param>
	/// <param name="active">
	/// The guest's own draw gate per element — the `globalactive` bit. A field on an inactive
	/// element must not be drawn: both book pages exist at all times, one gated on at a time.
	/// </param>
	public bool Read(Engine_UiScreen? screen, IReadOnlyList<string?>? text,
		IReadOnlyList<bool>? active, Node3D? built)
	{
		if (screen is null)
		{
			return false;
		}

		bool changed = false;
		FieldsDrawn = 0;
		PathsUnresolved = 0;
		_missing.Clear();
		_empty.Clear();
		_owned.Clear();

		// Resolved once per document, not per frame: `Resolve` scans the whole element array for
		// the path's first segment, and a swept page can be hundreds of fields over thousands of
		// elements. The cache is dropped whole when the document changes.
		if (!ReferenceEquals(_resolvedFor, screen))
		{
			_resolvedFor = screen;
			_resolved.Clear();
		}

		foreach (Field field in Fields)
		{
			if (!_resolved.TryGetValue(field.Path, out UiNode? found))
			{
				found = Resolve(screen, field.Path);
				_resolved[field.Path] = found;
			}

			if (found is not { } node)
			{
				if (!field.Alternate)
				{
					++PathsUnresolved;
					_missing.Add(Leaf(field.Path));
				}

				continue;
			}

			_owned.Add(field.Path);

			// The page moves (the guest animates page turns), so a field placed once at creation
			// drifts away from its art. Following it costs one cached lookup per field per frame.
			Place(field.Path, built);

			string live = node.Ordinal >= 0 && text is not null && node.Ordinal < text.Count
				? text[node.Ordinal] ?? string.Empty
				: string.Empty;

			// The gate, before anything is drawn. Treated as "nothing to show" rather than as a
			// missing field: the element is real and correctly named, it is just not this page.
			if (node.Ordinal >= 0 && active is not null && node.Ordinal < active.Count &&
				!active[node.Ordinal])
			{
				live = string.Empty;
			}

			// The authored tag is a placeholder, not a value: a field answers with the file's tag
			// until the guest writes one, and drawing it would put "GUI_SCREEN_..." on screen.
			if (node.Text is not null &&
				string.Equals(live, node.Text.Tag, System.StringComparison.Ordinal))
			{
				live = string.Empty;
			}

			if (live.Length > 0)
			{
				++FieldsDrawn;
			}
			else
			{
				// Resolved-but-silent is a different fault from missing: a missing path is a
				// wrong name, an empty one is a right name the guest has not written yet.
				_empty.Add(Leaf(field.Path));
			}

			if (_shown.TryGetValue(field.Path, out string? was) &&
				string.Equals(was, live, System.StringComparison.Ordinal))
			{
				continue;
			}

			_shown[field.Path] = live;
			Draw(field, node, live, built);
			changed = true;
		}

		return changed;
	}

	/// <summary>
	/// Lays one field out at the element's own authored place and style; only the layout is ours.
	/// The position comes from the authored element, never a host-side constant — every screen
	/// parks its furniture where it wants.
	/// </summary>
	private void Draw(Field field, UiNode node, string live, Node3D? built)
	{
		if (!_labels.TryGetValue(field.Path, out MeshInstance3D? label))
		{
			label = new MeshInstance3D { Name = Sanitise(field.Path) };
			AddChild(label);
			_labels[field.Path] = label;

			Place(field.Path, built);
		}

		if (live.Length == 0)
		{
			label.Mesh = null;
			label.MaterialOverride = null;
			return;
		}

		UiText? style = node.Text;
		int size = style?.Size ?? DefaultFontSize;
		var colour = new Color(style?.R ?? 1f, style?.G ?? 1f, style?.B ?? 1f, 1f);

		// Wrap mode deliberately differs from the file: the guest states mode 0 (one line,
		// truncated) for content it knows fits its own box; an owned field may re-flow.
		int wrap = field.Multiline ? 1 : style?.WrapMode ?? 0;

		Theme?.Paint(
			label, live, size, colour,
			horizontalAlign: style?.HorzAlign ?? 0,
			verticalAlign: style?.VertAlign ?? 0,
			boxWidth: style?.BoxWidth ?? 0f,
			boxHeight: style?.BoxHeight ?? 0f,
			wrapMode: wrap);
	}

	/// <summary>
	/// Puts a field where the built art for it currently is. X and Y only — this panel already
	/// carries the screen's draw depth, so taking Z from the built node would apply it twice.
	/// </summary>
	private void Place(string path, Node3D? built)
	{
		if (!_labels.TryGetValue(path, out MeshInstance3D? label))
		{
			return;
		}

		if (_placedBy.TryGetValue(path, out Node3D? placed) &&
			!GodotObject.IsInstanceValid(placed))
		{
			_placedBy.Remove(path);
			placed = null;
		}

		if (placed is null)
		{
			if (built is null || Engine_UiTheme.FindByPath(built, path) is not { } found)
			{
				return;
			}

			placed = found;
			_placedBy[path] = placed;
		}

		Vector3 origin = Engine_UiTheme.CanvasTransform(placed).Origin;
		label.Position = new Vector3(origin.X, origin.Y, 0f);
		_placedAt[path] = new Vector2(origin.X, origin.Y);
	}

	/// <summary>Where each field landed, for the report.</summary>
	private readonly System.Collections.Generic.Dictionary<string, Vector2> _placedAt = new();

	/// <summary>The font size for a field whose element carries no authored text record.</summary>
	private const int DefaultFontSize = 18;

	/// <summary>
	/// Finds an element by the guest's dotted path: the first segment is searched for and the
	/// rest walked as children, as the engine's `getElement(path, from)` does. See
	/// <see cref="Engine_UiTheme.FindByPath"/> for the same rule against the built tree.
	/// </summary>
	public static UiNode? Resolve(Engine_UiScreen screen, string path)
	{
		string[] segments = path.Split('.');
		if (segments.Length == 0)
		{
			return null;
		}

		UiNode? at = null;
		foreach (UiNode node in screen.Nodes)
		{
			if (string.Equals(node.Name, segments[0], System.StringComparison.Ordinal))
			{
				at = node;
				break;
			}
		}

		for (int i = 1; at is not null && i < segments.Length; i++)
		{
			at = Engine_UiStatedPanel.Child(at, segments[i]);
		}

		return at;
	}

	/// <summary>
	/// Every <c>[Text]</c> element under one root path, as fields — the whole subtree owned by
	/// stating the page root only (`logbookscreen.bgf` alone carries 280 text elements).
	/// Multiline comes from each element's own wrap mode, never from a flag set here.
	/// </summary>
	public static List<Field> FieldsUnder(Engine_UiScreen screen, string rootPath)
	{
		var fields = new List<Field>();
		if (Resolve(screen, rootPath) is not { } root)
		{
			return fields;
		}

		Collect(root, rootPath);
		return fields;

		void Collect(UiNode node, string path)
		{
			foreach (UiNode child in node.Children)
			{
				if (child.Name.Length == 0)
				{
					continue;
				}

				string childPath = $"{path}.{child.Name}";
				if (child.Text is { } text)
				{
					fields.Add(new Field(childPath, Multiline: text.WrapMode != 0));
				}

				Collect(child, childPath);
			}
		}
	}

	private static string Sanitise(string path) => path.Replace('.', '_');

	/// <summary>The last path segment — enough to identify a field, short enough to read.</summary>
	private static string Leaf(string path)
	{
		int cut = path.LastIndexOf('.');
		return cut < 0 ? path : path[(cut + 1)..];
	}

	/// <summary>
	/// The first few of a list, with a count of the rest — a swept page is legitimately silent
	/// on hundreds of fields, so the report names a handful and counts the rest.
	/// </summary>
	private static string Some(List<string> names, string separator)
	{
		const int Show = 12;
		return names.Count <= Show
			? string.Join(separator, names)
			: string.Join(separator, names.GetRange(0, Show)) + $" (+{names.Count - Show} more)";
	}

	/// <summary>A one-line description for the build report.</summary>
	public string Describe()
	{
		// The canvas is 1280x720 centred, so anything past ±640 / ±360 is off the page.
		var placed = new System.Collections.Generic.List<string>();
		foreach (Field field in Fields)
		{
			if (_placedAt.TryGetValue(field.Path, out Vector2 at))
			{
				string leaf = field.Path[(field.Path.LastIndexOf('.') + 1)..];
				bool offPage = Mathf.Abs(at.X) > 640f || Mathf.Abs(at.Y) > 360f;
				placed.Add($"{leaf}@({at.X:0},{at.Y:0}){(offPage ? " OFF-PAGE" : string.Empty)}");
			}
		}

		return $"{FieldsDrawn}/{Fields.Count} field(s) carry live text" +
			(_empty.Count == 0 ? string.Empty : $"; silent: {Some(_empty, ", ")}") +
			(PathsUnresolved == 0
				? string.Empty
				: $"; NOT IN THE DOCUMENT: {Some(_missing, ", ")}") +
			(placed.Count == 0 ? string.Empty : $"; at {Some(placed, " ")}");
	}
}
