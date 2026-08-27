using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// A Godot-owned book screen: art units cloned from the screen's own `.bgf` pattern book, values
/// from the guest's stated text/texture/transform/slide channels. One implementation for every
/// book; the <see cref="Engine_UiScreenTemplate"/> differs. Positions are canvas units.
/// </summary>
public sealed partial class Engine_UiOwnedBook : Node3D
{
	/// <summary>This screen's own pattern book — art, fonts and strings, from its `.bgf`.</summary>
	public Engine_UiTheme? Book { get; set; }

	/// <summary>The parsed document, for resolving each live field's ordinal by path.</summary>
	public Engine_UiScreen? Document { get; set; }

	/// <summary>Resolves a stated texture hash to a loaded texture.</summary>
	public System.Func<uint, Texture2D?>? ResolveTexture { get; set; }

	/// <summary>
	/// Fields the host owns outright, keyed by document path, as final display text (already through
	/// Babel). Detail-page fills are edge-gated on the guest's Lua highlight (abilities compares
	/// `g_Menu:GetHighlightedMenuItemData()` against `screen+624`; `beq` at `0x826c3d24` skips the
	/// fill), so with an owned cursor the guest never re-populates. Anything unnamed stays guest.
	/// </summary>
	public System.Collections.Generic.IReadOnlyDictionary<string, string>? HostValues { get; set; }

	/// <summary>
	/// Quads the host textures outright, keyed by document path, as a resource-name hash
	/// (`Engine_GameHash.ResourcePath` over the lower-cased backslash path, what
	/// `rex_lhResourceName_ComputeHash` computes). Written into the guest's stated-texture array.
	/// </summary>
	public System.Collections.Generic.IReadOnlyDictionary<string, uint>? HostTextures { get; set; }

	/// <summary>
	/// Elements the host places outright, keyed by document path, as a canvas position — e.g. a
	/// slider pip, which `sub_82881E38` moves by writing `position.x = value * 440 - 210` on
	/// `.grp.pip`. Written into the guest's per-ordinal transform array.
	/// </summary>
	public System.Collections.Generic.IReadOnlyDictionary<string, Vector3>? HostPositions { get; set; }

	/// <summary>
	/// Components the host switches, keyed by document path, as a 1-based slide index (1 is the
	/// first slide after the master; `sub_82BB5760` rejects anything below).
	/// </summary>
	public System.Collections.Generic.IReadOnlyDictionary<string, int>? HostSlides { get; set; }

	/// <summary>One live-driven text: where it lives in the doc, and the clone node it paints.</summary>
	private sealed class Field
	{
		public required string Path { get; init; }
		public required int Ordinal { get; init; }
		public required MeshInstance3D Node { get; init; }

		/// <summary>
		/// The authored placeholder tag (`Text`, `000000`, `NO_DATA`, a raw Babel tag); a field still
		/// reading it is unwritten by the guest and must not be drawn.
		/// </summary>
		public required string Authored { get; init; }

		/// <summary>The same placeholder after Babel.</summary>
		public required string AuthoredDisplay { get; init; }

		public string Shown { get; set; } = "unset";
	}

	/// <summary>The shared element pipeline — transform, visibility and opacity in one pass.</summary>
	private Engine_UiElementResolve? _resolve;

	/// <summary>The clone-to-element join and its per-frame apply, shared with the HUD.</summary>
	private readonly Engine_UiCloneBinding _binding = new();

	private readonly List<Field> _fields = new();
	private readonly List<string> _missing = new();

	/// <summary>Template entries dropped because a unit already cloned contains them.</summary>
	private readonly List<string> _nested = new();

	/// <summary>Element ordinal of each cloned unit's root, to the unit's name.</summary>
	private readonly Dictionary<int, string> _cloned = new();

	/// <summary>The document element a pattern-book node came from, or -1.</summary>
	private int Ordinal(Node3D source) =>
		Document is not null && source.HasMeta("object_id") &&
		Document.NodeForObjectId((uint)source.GetMeta("object_id")) is { } element
			? element.Ordinal
			: -1;

	/// <summary>Whether an element sits under a unit already cloned, and which one.</summary>
	private bool Inside(int ordinal, out string owner)
	{
		owner = string.Empty;
		if (Document is null || ordinal < 0 || ordinal >= Document.Nodes.Length)
		{
			return false;
		}

		for (UiNode? up = Document.Nodes[ordinal].Parent; up is not null; up = up.Parent)
		{
			if (_cloned.TryGetValue(up.Ordinal, out string? unit))
			{
				owner = unit;
				return true;
			}
		}

		return false;
	}


	private Node3D? _pages;

	/// <summary>The screen template naming this book's parts (<see cref="Engine_UiTemplates"/>, screen 0 by default).</summary>
	public Engine_UiScreenTemplate? Template { get; set; }

	/// <summary>
	/// Elements a Godot-owned view already draws, which this book must not clone. Registered by
	/// the owning view, same meaning as <see cref="Engine_UiPresentationHost.SuppressedElementNames"/>.
	/// </summary>
	public HashSet<string> Suppressed { get; } = new(System.StringComparer.Ordinal);


	/// <summary>A cloned node by its element name, for a host driver that owns one piece of art outright.</summary>
	public Node3D? Find(string element)
	{
		if (Document is null)
		{
			return null;
		}

		foreach (UiNode node in Document.Walk())
		{
			if (string.Equals(node.Name, element, System.StringComparison.Ordinal) &&
				_binding.NodeFor(node.Ordinal) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	/// <summary>Builds the book from its template. Safe to call once; the units live for the node's life.</summary>
	public void Build()
	{
		Template ??= Engine_UiTemplates.Screen(0);
		if (Book is null || Document is null || Template is null)
		{
			GD.PushWarning("[owned-book] no pattern book, document or template; nothing to build");
			return;
		}

		foreach (string entry in Template.ChromeUnits)
		{
			(string unit, int variant) = SplitVariant(entry);

			// A suppressed unit is not cloned at all; the bind below only suppresses inside a unit.
			if (Suppressed.Contains(unit))
			{
				continue;
			}

			// Zero bearers: the name is not in the pattern book. Too few: the occurrence index moved.
			Node3D? source = Book.ArtSourceNode(unit, variant);
			if (source is null)
			{
				int bearers = Book.ArtVariants(unit);
				_missing.Add(bearers == 0
					? $"{entry} (no bearer in the pattern book)"
					: $"{entry} (only {bearers} bearer(s), variant {variant} asked for)");
				continue;
			}

			// Never clone a unit already inside a cloned one (`Book` contains `Pages` and
			// `RightFrame`); ancestry comes from the document, not the template.
			int ordinal = Ordinal(source);
			if (Inside(ordinal, out string owner))
			{
				_nested.Add($"{entry} (inside {owner})");
				continue;
			}

			Node3D? clone = Book.Art(unit, variant);
			if (clone is null)
			{
				// Catalogued but will not clone: a different fault from absence.
				_missing.Add($"{entry} (catalogued but would not clone)");
				continue;
			}

			// Art() zeroes the transform; the authored one is read off the source chain.
			clone.Transform = Engine_UiTheme.CanvasTransform(source);
			clone.Name = unit;
			AddChild(clone);

			if (ordinal >= 0)
			{
				_cloned[ordinal] = unit;
			}

			// The unit's own root is bound too, so the guest's draw gate reaches it.
			_binding.Bind(clone, Document, Suppressed);

			if (unit == Template.PageRootUnit)
			{
				_pages = clone;
			}
		}

		// Pages parked off-window are clipped by the file's masks, not hidden.
		_masked = Document is null ? 0 : _binding.ApplyMasks(Document);

		// The page stack may have arrived inside another unit; markers hang off it either way.
		_pages ??= Template.PageRootUnit.Length == 0 ? null : Find(Template.PageRootUnit);

		// Bound but not driven: `infowindow.lua`/`iteminfo.lua` still run in the guest
		// (`sub_82426470` is a passthrough) and their `goToSlide` arrives on the slide channel;
		// driving the port too would give the slide two owners.
		_infoWindow.Theme = Book;
		_infoWindow.Bind(this);
		_itemInfo.Theme = Book;
		_itemInfo.Bind(this);

		// One `Page` (`scripts/gameface/page.lua`) per page component, bound by structure (carries
		// `Contents` and `DimensionsImage`, as `AK.fetchObjects` asks), never by name.
		if (_pages is not null)
		{
			foreach (Node child in _pages.GetChildren())
			{
				if (child is not Node3D candidate)
				{
					continue;
				}

				var page = new Engine_UiPage();
				if (page.Bind(candidate))
				{
					_pageWidgets.Add(page);
				}
			}
		}

		// Backdrop: authored 12.8x7.2 over a 100-unit base quad (1280x720); Art() zeroes the clone's
		// transform, so restate it 4x per axis to cover any aspect, behind the book (canvas +Z is
		// toward the camera).
		if (Template.BackdropUnit.Length != 0)
		{
			if (Book.Art(Template.BackdropUnit) is { } tint)
			{
				tint.Name = Template.BackdropUnit;
				tint.Position = new Vector3(0f, 0f, -30f);
				tint.Scale = new Vector3(51.2f, 28.8f, 1f);
				AddChild(tint);
			}
			else
			{
				_missing.Add(Template.BackdropUnit);
			}
		}

		// Every text element inside a cloned unit is a live field, derived from the binding, not
		// listed. Wrap mode, size, alignment and colour are stamped on the node.
		foreach ((MeshInstance3D mesh, int ordinal) in _binding.TextNodes(Document))
		{
			_fields.Add(new Field
			{
				Path = DocumentPath(Document.Nodes[ordinal]),
				Ordinal = ordinal,
				Node = mesh,
				Authored = Document.Nodes[ordinal].Text?.Tag ?? string.Empty,

				// The guest re-states the tag on some elements and the Babel string on others; both
				// forms count as the placeholder.
				AuthoredDisplay = Document.Nodes[ordinal].Text?.Display ?? string.Empty,
			});
		}

		int units = Template.ChromeUnits.Count - CountMissingUnits();

		GD.Print($"[owned-book] '{Template.Name}' built: {units}/{Template.ChromeUnits.Count} " +
			$"art unit(s), {_binding.Count} bound node(s), {_masked} masked, " +
			$"{_fields.Count} live field(s)" +
			(_pageWidgets.Count == 0
				? string.Empty
				: $", {_pageWidgets.Count} page(s) [{string.Join("; ", _pageWidgets.ConvertAll(p => p.Describe()))}]") +
			(_infoWindow.IsBound ? $", {_infoWindow.Describe()}" : string.Empty) +
			(_itemInfo.IsBound ? $", {_itemInfo.Describe()}" : string.Empty) +
			(_binding.SuppressedCount == 0
				? string.Empty
				: $", {_binding.SuppressedCount} subtree(s) left to their owning view") +
			(_nested.Count == 0
				? string.Empty
				: $"; already cloned: {string.Join(", ", _nested)}") +
			(_missing.Count == 0 ? string.Empty : $"; NOT FOUND: {string.Join(", ", _missing)}"));
	}

	/// <summary>
	/// Splits a chrome entry into element name and bearer index: `Name#N`, bare name = first.
	/// Names are not unique; bearers are indexed in document order.
	/// </summary>
	private static (string Name, int Variant) SplitVariant(string entry)
	{
		int hash = entry.LastIndexOf('#');
		if (hash <= 0 || !int.TryParse(entry[(hash + 1)..], out int variant) || variant < 0)
		{
			return (entry, 0);
		}

		return (entry[..hash], variant);
	}

	private int CountMissingUnits()
	{
		int count = 0;
		foreach (string unit in Template?.ChromeUnits ?? new List<string>())
		{
			// Entries carry a cause suffix (`Name (…)`), so match the name, not the whole line.
			foreach (string entry in _missing)
			{
				if (entry == unit || entry.StartsWith(unit + " (", System.StringComparison.Ordinal))
				{
					++count;
					break;
				}
			}
		}

		return count;
	}

	/// <summary>Applies one sample of the guest's statements; every write is change-keyed.</summary>
	public void SetLive(
		string?[]? texts, uint[]? textures, IReadOnlyList<float?[]?>? transform,
		IReadOnlyList<bool>? active = null, IReadOnlyList<float?>? opacity = null,
		IReadOnlyList<int>? slides = null)
	{
		if (Book is null || Document is null)
		{
			return;
		}

		// One resolve for the whole document: position, visibility, opacity.
		_resolve ??= new Engine_UiElementResolve(Document);
		_resolve.Apply(Slid(slides), Activated(active), opacity);

		// Draw gate, accumulated opacity and stated art for every node, in one pass.
		_binding.ResolveTexture = ResolveTexture;
		_binding.Apply(_resolve, Textured(textures), Positioned(transform));

		// The page arranges every frame (`NeedToUpdateState` is set once in `Initialize`, page.dis
		// main.0 pc17, never cleared) and must run after `_binding.Apply`, which rewrites every
		// bound transform from the stated position.
		bool textMoved = ApplyText(texts);
		foreach (Engine_UiPage page in _pageWidgets)
		{
			page.PivotOf ??= _binding.PivotOf;
			page.Arrange();
			page.Scroll(Refii.Game.GameInput.RightStickY, ScrollDelta);
		}

		if (textMoved)
		{
			// Frames re-measure on the text edge (`TilStopUpdating` cadence).
			DriveFrames();
		}

		ReportHostChannels();
		ReportDark();
		ProbeLegend(texts);
		ProbePageStack();
	}

	/// <summary>How much of the book draws and what darkened the rest, by ancestor. Change-keyed.</summary>
	private void ReportDark()
	{
		if (_resolve is null || Document is null)
		{
			return;
		}

		var by = new Dictionary<string, int>(System.StringComparer.Ordinal);
		foreach (int ordinal in _binding.Dark)
		{
			string why = _resolve.WhyDark(ordinal);
			if (why.Length != 0)
			{
				by[why] = by.TryGetValue(why, out int seen) ? seen + 1 : 1;
			}
		}

		var parts = new List<string>();
		foreach ((string why, int count) in by)
		{
			parts.Add($"{why} x{count}");
		}

		parts.Sort();
		string line = $"{_binding.DrawnCount}/{_binding.Count} node(s) drawing" +
			(parts.Count == 0 ? string.Empty : $"; dark at: {string.Join("  ", parts)}");

		if (line == _darkReported)
		{
			return;
		}

		_darkReported = line;
		GD.Print($"[owned-book] '{Template?.Name}' {line}");
	}

	private string _darkReported = string.Empty;

	private string _legendReported = string.Empty;

	/// <summary>
	/// The `clip_rect` of the first clipped mesh under <paramref name="node"/>, as
	/// (minX, minY, maxX, maxY) in world space, read back off the material rather than recomputed.
	/// </summary>
	private static Vector4? FirstClipRect(Node node)
	{
		if (node is MeshInstance3D mesh && mesh.MaterialOverride is ShaderMaterial material &&
			material.GetShaderParameter("clip_rect").VariantType == Variant.Type.Vector4)
		{
			return material.GetShaderParameter("clip_rect").AsVector4();
		}

		foreach (Node child in node.GetChildren())
		{
			if (FirstClipRect(child) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	private void ProbePageStack()
	{
		if (_pages is null || !GodotObject.IsInstanceValid(_pages) || _resolve is null)
		{
			return;
		}

		var line = new System.Text.StringBuilder();
		var key = new System.Text.StringBuilder();
		int up = 0;
		foreach (Node child in _pages.GetChildren())
		{
			if (child is not Node3D page)
			{
				continue;
			}

			int ordinal = _binding.OrdinalOf(page);
			bool drawn = ordinal >= 0 && ordinal < _resolve.Drawn.Length && _resolve.Drawn[ordinal];
			float opacity = ordinal >= 0 && ordinal < _resolve.Opacity.Length
				? _resolve.Opacity[ordinal]
				: -1f;
			bool shows = drawn && opacity > 0.01f;
			if (shows)
			{
				++up;
			}

			// The resolve knows nothing about the shader clip; the page's position does.
			Vector3 at = page.IsInsideTree() ? page.GlobalPosition : page.Position;

			// The page selector writes own opacity 100 for the page in the window and 100/1024 for
			// parked ones; `op` is the accumulated product.
			float own = ordinal >= 0 && ordinal < _resolve.Own.Length
				? _resolve.Own[ordinal]
				: -1f;
			bool stated = ordinal >= 0 && ordinal < _resolve.Stated.Length &&
				_resolve.Stated[ordinal];

			string clip = "clip=none";
			if (FirstClipRect(page) is { } band)
			{
				bool inside = at.X >= band.X && at.X <= band.Z &&
					at.Y >= band.Y && at.Y <= band.W;
				clip = $"clip=({band.X:0},{band.Y:0})..({band.Z:0},{band.W:0}) " +
					(inside ? "CONTAINS it" : "EXCLUDES it");
			}

			key.Append(page.Name).Append(drawn ? '+' : '-')
				.Append(opacity.ToString("0.00")).Append(own.ToString("0.000"))
				.Append(stated ? 'g' : 'f').Append(clip).Append(';');

			line.Append(line.Length == 0 ? string.Empty : "  ")
				.Append(shows ? '*' : ' ')
				.Append(page.Name)
				.Append('#').Append(ordinal)
				.Append(" drawn=").Append(drawn)
				.Append(" op=").Append(opacity.ToString("0.00"))
				.Append(" own=").Append(own.ToString("0.000"))
				.Append(stated ? " (GUEST)" : " (file)")
				.Append(" at=(").Append(at.X.ToString("0")).Append(',')
				.Append(at.Y.ToString("0")).Append(',')
				.Append(at.Z.ToString("0")).Append(") ")
				.Append(clip);
		}

		// The change key excludes the position: the scrolling backdrop moves every frame.
		string report = $"{up} up | {line}";
		if (key.ToString() == _pageStackShown)
		{
			return;
		}

		_pageStackShown = key.ToString();
		GD.Print($"[owned-book] '{Template.Name}' page stack: {report}");
	}

	private int _masked;

	private string _pageStackShown = string.Empty;

	private void ProbeLegend(string?[]? texts)
	{
		if (_resolve is null || Document is null || Template?.ScreenId != 0)
		{
			return;
		}

		var lines = new List<string>();
		foreach (UiNode node in Document.Nodes)
		{
			bool interesting = node.Name.StartsWith("KeyName", System.StringComparison.Ordinal) ||
				node.Name is "Legend" or "Fixed" or "CurrentObjective" or "CurrentQuest";
			if (!interesting || node.Ordinal < 0 || node.Ordinal >= _resolve.Drawn.Length)
			{
				continue;
			}

			int i = node.Ordinal;
			string text = texts is not null && i < texts.Length && texts[i] is { Length: > 0 } t
				? $"'{t}'"
				: "-";
			Vector3? at = _binding.NodeFor(i) is { } live
				? Engine_UiTheme.CanvasTransform(live).Origin
				: null;
			lines.Add($"{node.Name}#{i} drawn={_resolve.Drawn[i]} " +
				$"active={_resolve.Active[i]} slid={_resolve.Slid[i]} " +
				$"op={_resolve.Opacity[i]:0.00} text={text} " +
				(at is { } pos ? $"at=({pos.X:0},{pos.Y:0})" : "at=UNBOUND"));
		}

		string joined = string.Join("  |  ", lines);
		if (joined.Length == 0 || joined == _legendReported)
		{
			return;
		}

		_legendReported = joined;
		GD.Print($"[legend-probe] {joined}");
	}

	/// <summary>
	/// The guest's stated textures with the host's overrides written over them. Copy-on-write: the
	/// array belongs to the caller and is reused across books.
	/// </summary>
	private uint[]? Textured(uint[]? stated)
	{
		if (HostTextures is null || HostTextures.Count == 0 || Document is null)
		{
			return stated;
		}

		uint[]? patched = null;
		foreach (var pair in HostTextures)
		{
			if (pair.Value == 0u || OrdinalForPath(pair.Key) is not { } ordinal)
			{
				continue;
			}

			patched ??= stated is null
				? new uint[Document.Nodes.Length]
				: (uint[])stated.Clone();

			if (ordinal < patched.Length)
			{
				patched[ordinal] = pair.Value;
			}
		}

		return patched ?? stated;
	}

	/// <summary>
	/// The guest's stated transforms with the host's placements written over them. Copy-on-write.
	/// An entry is six slots (x, y, z, three scales); only the position terms are touched.
	/// </summary>
	private IReadOnlyList<float?[]?>? Positioned(IReadOnlyList<float?[]?>? stated)
	{
		if (HostPositions is null || HostPositions.Count == 0 || Document is null)
		{
			return stated;
		}

		List<float?[]?>? patched = null;
		foreach (var pair in HostPositions)
		{
			if (OrdinalForPath(pair.Key) is not { } ordinal)
			{
				continue;
			}

			if (patched is null)
			{
				patched = new List<float?[]?>(Document.Nodes.Length);
				for (int i = 0; i < Document.Nodes.Length; ++i)
				{
					patched.Add(stated is not null && i < stated.Count ? stated[i] : null);
				}
			}

			if (ordinal < 0 || ordinal >= patched.Count)
			{
				continue;
			}

			float?[] slots = patched[ordinal] is { } existing
				? (float?[])existing.Clone()
				: new float?[6];
			if (slots.Length < 6)
			{
				var widened = new float?[6];
				CopyInto(slots, widened);
				slots = widened;
			}

			slots[0] = pair.Value.X;
			slots[1] = pair.Value.Y;
			slots[2] = pair.Value.Z;
			patched[ordinal] = slots;
		}

		return patched ?? stated;

		static void CopyInto(float?[] from, float?[] into)
		{
			for (int i = 0; i < from.Length && i < into.Length; ++i)
			{
				into[i] = from[i];
			}
		}
	}

	/// <summary>The guest's stated slides with the host's switches written over them. Copy-on-write.</summary>
	private IReadOnlyList<int>? Slid(IReadOnlyList<int>? stated)
	{
		if (HostSlides is null || HostSlides.Count == 0 || Document is null)
		{
			return stated;
		}

		List<int>? patched = null;
		foreach (var pair in HostSlides)
		{
			if (pair.Value <= 0 || OrdinalForPath(pair.Key) is not { } ordinal)
			{
				continue;
			}

			if (patched is null)
			{
				patched = new List<int>(Document.Nodes.Length);
				for (int i = 0; i < Document.Nodes.Length; ++i)
				{
					patched.Add(stated is not null && i < stated.Count ? stated[i] : 0);
				}
			}

			if (ordinal >= 0 && ordinal < patched.Count)
			{
				patched[ordinal] = pair.Value;
			}
		}

		return patched ?? stated;
	}

	/// <summary>
	/// The guest's stated draw gates with every host-supplied field switched on. Cannot reveal a
	/// shut page: `Drawn[i]` is `parentDraws &amp;&amp; active[i]`.
	/// </summary>
	private IReadOnlyList<bool>? Activated(IReadOnlyList<bool>? stated)
	{
		if (HostValues is null || HostValues.Count == 0 || Document is null)
		{
			return stated;
		}

		List<bool>? patched = null;
		foreach (var pair in HostValues)
		{
			// An empty value is not a statement; do not light it.
			if (pair.Value.Length == 0 || OrdinalForPath(pair.Key) is not { } ordinal)
			{
				continue;
			}

			if (patched is null)
			{
				patched = new List<bool>(Document.Nodes.Length);
				for (int i = 0; i < Document.Nodes.Length; ++i)
				{
					patched.Add(stated is not null && i < stated.Count && stated[i]);
				}
			}

			if (ordinal >= 0 && ordinal < patched.Count)
			{
				patched[ordinal] = true;
			}
		}

		return patched ?? stated;
	}

	/// <summary>What the host's four channels landed on; a key matching no element is otherwise silent.</summary>
	private void ReportHostChannels()
	{
		int values = Landed(HostValues?.Keys, out int valuesLost);
		int textures = Landed(HostTextures?.Keys, out int texturesLost);
		int positions = Landed(HostPositions?.Keys, out int positionsLost);
		int slides = Landed(HostSlides?.Keys, out int slidesLost);
		if (values + textures + positions + slides +
			valuesLost + texturesLost + positionsLost + slidesLost == 0)
		{
			return;
		}

		string report = $"{values} value(s), {textures} texture(s), {positions} position(s), " +
			$"{slides} slide(s) bound";
		int lost = valuesLost + texturesLost + positionsLost + slidesLost;
		if (lost != 0)
		{
			report += $" — {lost} key(s) MATCHED NO ELEMENT" +
				$" (values {valuesLost}, textures {texturesLost}, " +
				$"positions {positionsLost}, slides {slidesLost})";
		}

		if (report == _channelsShown)
		{
			return;
		}

		_channelsShown = report;
		GD.Print($"[owned-book] '{Template?.Name}' host channels: {report}");
	}

	/// <summary>How many of these paths resolve, and how many name nothing at all.</summary>
	private int Landed(System.Collections.Generic.IEnumerable<string>? paths, out int lost)
	{
		lost = 0;
		if (paths is null)
		{
			return 0;
		}

		int found = 0;
		foreach (string path in paths)
		{
			if (OrdinalForPath(path) is not null)
			{
				++found;
			}
			else
			{
				++lost;
			}
		}

		return found;
	}

	private string _channelsShown = string.Empty;

	/// <summary>Every node's dotted path to its ordinal, built once.</summary>
	private Dictionary<string, int>? _pathOrdinals;

	/// <summary>The ordinal of the node a key names, matched on trailing whole segments.</summary>
	private int? OrdinalForPath(string key)
	{
		if (Document is null)
		{
			return null;
		}

		if (_pathOrdinals is null)
		{
			_pathOrdinals = new Dictionary<string, int>();
			foreach (UiNode node in Document.Nodes)
			{
				string path = DocumentPath(node);
				if (path.Length != 0)
				{
					_pathOrdinals[path] = node.Ordinal;
				}
			}
		}

		foreach (var pair in _pathOrdinals)
		{
			if (PathNames(pair.Key, key))
			{
				return pair.Value;
			}
		}

		return null;
	}

	/// <summary>
	/// The element's dotted path from its authored ancestors (<c>Foreground.Book.Pages.Page1.Contents.AbilityName</c>);
	/// parser-synthesised <c>Type_Ordinal</c> ancestors are skipped, so the path is file-order independent.
	/// </summary>
	private static string DocumentPath(UiNode node)
	{
		var parts = new List<string>();
		for (UiNode? walk = node; walk is not null; walk = walk.Parent)
		{
			if (walk.HasAuthoredName)
			{
				parts.Add(walk.Name);
			}
		}

		parts.Reverse();
		return string.Join('.', parts);
	}

	/// <summary>
	/// Whether <paramref name="key"/> equals <paramref name="path"/> or is a trailing run of whole
	/// segments of it (templates and files disagree about the root name).
	/// </summary>
	private static bool PathNames(string path, string key) =>
		string.Equals(path, key, System.StringComparison.Ordinal) ||
		(path.Length > key.Length &&
		 path[path.Length - key.Length - 1] == '.' &&
		 path.EndsWith(key, System.StringComparison.Ordinal));

	private bool ApplyText(string?[]? texts)
	{
		if (Book is null)
		{
			return false;
		}

		bool changed = false;

		foreach (Field field in _fields)
		{
			if (!GodotObject.IsInstanceValid(field.Node))
			{
				continue;
			}

			string display;

			string? owned = null;
			if (HostValues is not null)
			{
				foreach (var pair in HostValues)
				{
					if (PathNames(field.Path, pair.Key))
					{
						// An empty host value is not a blanking order; the guest's text stands.
						owned = pair.Value.Length == 0 ? null : pair.Value;
						break;
					}
				}
			}

			if (owned is not null)
			{
				// Host-owned text is already through Babel; do not resolve it again.
				display = owned;
			}
			else
			{
				string? stated = texts is not null && field.Ordinal < texts.Length
					? texts[field.Ordinal]
					: null;

				// A placeholder is an authored tag that did not resolve (`NO_DATA`,
				// `DEFAULT_TEXT_ID`, literal 'Text'); a tag Babel resolved is real content.
				bool placeholder =
					string.Equals(stated, field.Authored, System.StringComparison.Ordinal) ||
					(string.Equals(stated, field.AuthoredDisplay, System.StringComparison.Ordinal) &&
					 string.Equals(field.Authored, field.AuthoredDisplay, System.StringComparison.Ordinal));
				display = stated is null || stated.Length == 0 || placeholder
					? string.Empty
					: Book.Resolve(stated);
			}

			if (display == field.Shown)
			{
				continue;
			}

			field.Shown = display;
			Book.SetMeshText(field.Node, display);
			changed = true;
		}

		return changed;
	}

	/// <summary>Seconds since the last sample, for the page scroll (`SCROLL_SPEED` is per second: page.dis main.1 pc15..27).</summary>
	public double ScrollDelta { get; set; } = 1.0 / 30.0;

	private readonly List<Engine_UiPage> _pageWidgets = new();

	/// <summary>The ported `g_InfoWindow`, when this screen's art carries one.</summary>
	private readonly Engine_UiInfoWindow _infoWindow = new();

	/// <summary>The ported `g_ItemInfo`, same condition.</summary>
	private readonly Engine_UiItemInfo _itemInfo = new();

	/// <summary>The runtime border cloner — `framecloning.lua`.</summary>
	private Engine_UiFrameCloning? _frameCloning;

	private int _framesPlaced = -1;

	/// <summary>
	/// Clones and places the nine-slice borders for every frame this book's clones carry — the
	/// slices are not in the file. A frame whose parent has no live clone is left alone.
	/// </summary>
	public void DriveFrames()
	{
		if (Book is null || Document is null)
		{
			return;
		}

		_frameCloning ??= new Engine_UiFrameCloning(Book);
		int placed = _frameCloning.Dress(Document, _binding);

		if (placed != _framesPlaced)
		{
			_framesPlaced = placed;
			GD.Print($"[owned-book] '{Template?.Name}' framed: {placed} border piece(s) placed, " +
				$"{_frameCloning.Describe()}");
		}
	}

	private readonly List<Node3D> _markers = new();
	private string _markersShown = string.Empty;

	/// <summary>
	/// Draws the quest markers: a clone of the matching `ReusableTokens` art at the guest's stated
	/// page-local centre, under the Pages unit.
	/// </summary>
	public void SetMarkers(IReadOnlyList<Engine_UiModel.MapMarker> markers)
	{
		if (_pages is null || Book is null)
		{
			return;
		}

		// Change-keyed: one rebuild per map opening.
		var key = new System.Text.StringBuilder();
		foreach (Engine_UiModel.MapMarker marker in markers)
		{
			key.Append($"{marker.Key:X8}@{marker.X:0},{marker.Y:0};");
		}

		string shown = key.ToString();
		if (shown == _markersShown)
		{
			return;
		}

		_markersShown = shown;
		foreach (Node3D old in _markers)
		{
			if (GodotObject.IsInstanceValid(old))
			{
				old.QueueFree();
			}
		}

		_markers.Clear();

		foreach (Engine_UiModel.MapMarker marker in markers)
		{
			// Matched on the authored UV rect; the marker key is an opaque guest pointer.
			string token = MatchToken(marker);
			if (Book.Art(token) is not { } art)
			{
				continue;
			}

			art.Name = $"Marker_{_markers.Count}";

			// Art() zeroed the transform; the token's authored scale is 0.1.
			art.Position = new Vector3(marker.X, marker.Y, -5f);
			art.Scale = new Vector3(0.1f, 0.1f, 1f);
			_pages.AddChild(art);
			_markers.Add(art);
		}

		GD.Print($"[owned-book] {_markers.Count} quest marker(s) on the minimap" +
			(_markers.Count == 0 ? string.Empty : $": {shown}"));
	}

	/// <summary>
	/// The ReusableTokens entry whose authored UV rect matches a marker's corners; "Quest" is the
	/// default for anything unmatched.
	/// </summary>
	private string MatchToken(Engine_UiModel.MapMarker marker)
	{
		if (Document is null)
		{
			return "Quest";
		}

		string best = "Quest";
		float bestError = float.MaxValue;
		foreach (string token in new[] { "Quest", "QuestGiver", "QuestExit", "LevelExit" })
		{
			UiNode? node = Engine_UiScreenPanel.Resolve(Document, $"ReusableTokens.{token}");
			UiQuad? quad = node?.Quad;
			if (quad is null && node is not null)
			{
				foreach (UiNode child in node.Children)
				{
					if (child.Quad is not null)
					{
						quad = child.Quad;
						break;
					}
				}
			}

			if (quad is null)
			{
				continue;
			}

			float error =
				System.Math.Abs(quad.UMin - marker.UMin) + System.Math.Abs(quad.VMin - marker.VMin) +
				System.Math.Abs(quad.UMax - marker.UMax) + System.Math.Abs(quad.VMax - marker.VMax);
			if (error < bestError)
			{
				bestError = error;
				best = token == "LevelExit" ? "QuestExit" : token;
			}
		}

		return best;
	}

}
