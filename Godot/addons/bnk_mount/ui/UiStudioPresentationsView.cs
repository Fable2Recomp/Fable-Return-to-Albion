#if TOOLS
using System;
using System.IO;
using System.Linq;
using Godot;
using Refii.Ui;

namespace Refii.Assets.UI;

/// <summary>
/// The UI tab's Presentations page: a Gameface <c>.bgf</c> as the mount serves it, its element
/// tree edited — a node added, cloned, renamed or moved (<see cref="Engine_BgfEdit"/>) — and
/// exported into the active mod at the presentation's own bank path, written by
/// <see cref="Engine_BgfWriter"/> and read back by the game's parser before the file is handed
/// over. The mount serves the mod's file in the shipped one's place, so the game's menus, boxes
/// and HUD art built from that presentation take the edit; a NEW presentation becomes one of
/// the host's own (<c>Engine_UiPresentationHost.AddModLayers</c>).
///
/// The sidecars travel with it: the <c>.fac</c> (UVs, textures, text) and <c>.bsg</c> (scene
/// nodes, base transforms) are loaded, edited through the same <see cref="Engine_BgfEdit"/> calls
/// — a clone takes its source's records, a new quad gets a geometry node and a UV record naming
/// its texture — and exported beside the <c>.bgf</c> at their own paths.
///
/// The edit lives in this page until exported; Save UI exports it too.
/// </summary>
[Tool]
public partial class UiStudioPresentationsView : VBoxContainer
{
	/// <summary>Where a presentation no bank ships goes: beside the shipped ones.</summary>
	private const string DefaultFolder = "art/gui/gameface";

	private readonly UiStudioDocs _docs;
	private readonly UiStudioPreview _preview;

	/// <summary>The document changed (an edit, a new presentation).</summary>
	public event Action? Edited;

	private OptionButton _presentations = null!;
	private LineEdit _newName = null!;
	private LineEdit _filter = null!;
	private Tree _tree = null!;
	private Label _detail = null!;
	private LineEdit _name = null!;
	private SpinBox _x = null!, _y = null!;
	private LineEdit _childName = null!, _cloneName = null!;
	private LineEdit _quadName = null!, _quadTexture = null!;
	private SpinBox _quadW = null!, _quadH = null!, _quadU = null!, _quadV = null!, _quadDu = null!, _quadDv = null!;
	private Label _status = null!;
	private bool _active;
	private bool _filling;

	private Engine_BgfDocument? _doc;
	private Engine_FacDocument? _fac;
	private Engine_BsgDocument? _bsg;
	private string _path = string.Empty;
	private string _presentation = string.Empty;
	private int _selected = -1;

	/// <summary>An edit is waiting to be exported.</summary>
	public bool Dirty { get; private set; }

	public UiStudioPresentationsView(UiStudioDocs docs, UiStudioPreview preview)
	{
		_docs = docs;
		_preview = preview;
		_preview.HostLoaded += () =>
		{
			if (_active)
			{
				FillPresentations();
			}
		};
	}

	public override void _Ready()
	{
		AddThemeConstantOverride("separation", 8);
		AddChild(ModStudioUiStyles.Note("A presentation is the .bgf the game's menus, boxes and HUD art are cut from, with its .fac (UVs, " +
			"textures, text) and .bsg (scene nodes) beside it. Edit its tree here — add a node or a textured quad, clone, rename or " +
			"move — and export the three files into the mod at their own paths; the mount serves the mod's files in the shipped ones' " +
			"place. A new presentation is the mod's own. Layout is the game's templates' (ui/hud.json, ui/templates.json): a .bgf is " +
			"the ART and STRINGS a template draws."));

		var pick = new HBoxContainer();
		_presentations = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_presentations.ItemSelected += _ => LoadSelected();
		pick.AddChild(_presentations);
		var reload = new Button { Text = "↺ Reload", TooltipText = "Read the presentation again as the mount serves it; unexported edits are dropped" };
		reload.Pressed += LoadSelected;
		pick.AddChild(reload);
		_newName = new LineEdit { PlaceholderText = "newscreen", CustomMinimumSize = new Vector2(140, 0), TooltipText = "The new presentation's name; its file is art/gui/gameface/<name>.bgf" };
		pick.AddChild(_newName);
		var create = new Button { Text = "+ New presentation" };
		create.Pressed += NewPresentation;
		pick.AddChild(create);
		AddChild(pick);

		_filter = new LineEdit { PlaceholderText = "Filter by name" };
		_filter.TextChanged += _ => FillTree();
		AddChild(_filter);

		_tree = new Tree { Columns = 3, ColumnTitlesVisible = true, HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 240) };
		_tree.SetColumnTitle(0, "Element");
		_tree.SetColumnTitle(1, "Type");
		_tree.SetColumnTitle(2, "Ordinal");
		_tree.SetColumnExpand(1, false);
		_tree.SetColumnExpand(2, false);
		_tree.SetColumnCustomMinimumWidth(1, 90);
		_tree.SetColumnCustomMinimumWidth(2, 70);
		_tree.ItemSelected += OnSelected;
		AddChild(_tree);

		var body = new VBoxContainer();
		_detail = ModStudioUiStyles.Note("Pick an element.");
		body.AddChild(_detail);

		var rename = new HBoxContainer();
		_name = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		rename.AddChild(_name);
		var renameButton = new Button { Text = "Rename" };
		renameButton.Pressed += Rename;
		rename.AddChild(renameButton);
		body.AddChild(ModStudioUiStyles.Field("Name", rename, "The element's name attribute and name hash"));

		var move = new HBoxContainer();
		_x = new SpinBox { MinValue = -10000, MaxValue = 10000, Step = 0.5, SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_y = new SpinBox { MinValue = -10000, MaxValue = 10000, Step = 0.5, SizeFlagsHorizontal = SizeFlags.ExpandFill };
		move.AddChild(_x);
		move.AddChild(_y);
		var moveButton = new Button { Text = "Move" };
		moveButton.Pressed += Move;
		move.AddChild(moveButton);
		body.AddChild(ModStudioUiStyles.Field("Position", move, "position.x and position.y, canvas units, +Y up"));

		var child = new HBoxContainer();
		_childName = new LineEdit { PlaceholderText = "NewNode", SizeFlagsHorizontal = SizeFlags.ExpandFill };
		child.AddChild(_childName);
		var childButton = new Button { Text = "+ Add child" };
		childButton.Pressed += AddChild;
		child.AddChild(childButton);
		body.AddChild(ModStudioUiStyles.Field("Child", child, "An empty active node, the last child of the picked element"));

		var clone = new HBoxContainer();
		_cloneName = new LineEdit { PlaceholderText = "Copy", SizeFlagsHorizontal = SizeFlags.ExpandFill };
		clone.AddChild(_cloneName);
		var cloneButton = new Button { Text = "⧉ Clone" };
		cloneButton.Pressed += Clone;
		clone.AddChild(cloneButton);
		body.AddChild(ModStudioUiStyles.Field("Clone", clone, "The picked element and its subtree, copied as its next sibling"));

		var quad = new HBoxContainer();
		_quadName = new LineEdit { PlaceholderText = "Badge", CustomMinimumSize = new Vector2(90, 0) };
		quad.AddChild(_quadName);
		_quadTexture = new LineEdit { PlaceholderText = "art/gui/mymod/badge.tex  (a mod's badge.tex.png, or a shipped texture's path)", SizeFlagsHorizontal = SizeFlags.ExpandFill };
		quad.AddChild(_quadTexture);
		_quadW = new SpinBox { MinValue = 1, MaxValue = 4096, Value = 100, Step = 1, Prefix = "w", CustomMinimumSize = new Vector2(80, 0) };
		_quadH = new SpinBox { MinValue = 1, MaxValue = 4096, Value = 100, Step = 1, Prefix = "h", CustomMinimumSize = new Vector2(80, 0) };
		quad.AddChild(_quadW);
		quad.AddChild(_quadH);
		var quadButton = new Button { Text = "+ Add quad" };
		quadButton.Pressed += AddQuad;
		quad.AddChild(quadButton);
		body.AddChild(ModStudioUiStyles.Field("Quad", quad, "A textured quad under the picked element: its name, the texture it samples, its size in canvas units"));

		var atlas = new HBoxContainer();
		_quadU = new SpinBox { MinValue = 0, MaxValue = 1, Value = 0, Step = 0.001, Prefix = "u", CustomMinimumSize = new Vector2(90, 0) };
		_quadV = new SpinBox { MinValue = 0, MaxValue = 1, Value = 0, Step = 0.001, Prefix = "v", CustomMinimumSize = new Vector2(90, 0) };
		_quadDu = new SpinBox { MinValue = 0, MaxValue = 1, Value = 1, Step = 0.001, Prefix = "du", CustomMinimumSize = new Vector2(90, 0) };
		_quadDv = new SpinBox { MinValue = 0, MaxValue = 1, Value = 1, Step = 0.001, Prefix = "dv", CustomMinimumSize = new Vector2(90, 0) };
		foreach (SpinBox box in new[] { _quadU, _quadV, _quadDu, _quadDv })
		{
			atlas.AddChild(box);
		}

		body.AddChild(ModStudioUiStyles.Field("Atlas rect", atlas, "The part of the texture the quad samples, authored origin: 0 0 1 1 is the whole texture"));

		var export = new Button { Text = "📦 Export .bgf + sidecars into the mod", TooltipText = "Write the presentation and its .fac/.bsg, read each back with the game's parser, and put them in the mod at their own paths" };
		export.Pressed += () => Export(report: true);
		body.AddChild(export);
		_status = ModStudioUiStyles.Note(string.Empty);
		body.AddChild(_status);
		AddChild(ModStudioUiStyles.Card("✎ Element", new Color(0.70f, 0.90f, 0.75f), body));
	}

	public void Activate()
	{
		_active = true;
		_preview.EnsureHost();
		_preview.SetStatus("A presentation's tree is edited on the left and exported into the mod; the preview shows the game's UI as the mount serves it.");
		FillPresentations();
	}

	public void Deactivate() => _active = false;

	/// <summary>Lists the presentations again; an unexported edit is kept.</summary>
	public void Reload()
	{
		if (IsNodeReady())
		{
			FillPresentations();
		}
	}

	private void FillPresentations()
	{
		if (!_preview.Loaded || _preview.Host is not { } host)
		{
			return;
		}

		_filling = true;
		_presentations.Clear();
		int select = -1;
		foreach (string name in host.ScreenNames)
		{
			_presentations.AddItem(name);
			if (string.Equals(name, _presentation, StringComparison.OrdinalIgnoreCase))
			{
				select = _presentations.ItemCount - 1;
			}
		}

		if (select >= 0)
		{
			_presentations.Selected = select;
		}

		_filling = false;
		if (_doc is null && _presentations.ItemCount != 0)
		{
			_presentations.Selected = Math.Max(0, select);
			LoadSelected();
		}
	}

	private void LoadSelected()
	{
		if (_filling || _preview.Host is not { } host || _presentations.Selected < 0)
		{
			return;
		}

		string name = _presentations.GetItemText(_presentations.Selected);
		byte[]? bytes = host.PresentationBytes(name);
		string? path = host.PresentationPath(name);
		if (bytes is null || path is null)
		{
			SetStatus($"'{name}' is not served by the mount.");
			return;
		}

		try
		{
			_doc = Engine_BgfDocument.Parse(bytes);
			byte[]? fac = host.PresentationBytes(name, ".fac");
			byte[]? bsg = host.PresentationBytes(name, ".bsg");
			_fac = fac is null ? Engine_BgfEdit.NewSidecar() : Engine_FacDocument.Parse(fac);
			_bsg = bsg is null ? null : Engine_BsgDocument.Parse(bsg);
		}
		catch (InvalidDataException e)
		{
			SetStatus($"'{name}' does not parse: {e.Message}");
			return;
		}

		_presentation = name;
		_path = path;
		Dirty = false;
		_selected = -1;
		FillTree();
		SetStatus($"{name}: {_doc.Elements.Length} elements, {_doc.Slides.Length} slides, {_doc.Animations.Length} tracks, " +
			$"{_fac.Uv.Count} quads, {_fac.Text.Count} texts, {(_bsg is null ? "no scene graph" : _bsg.Nodes.Length + " scene nodes")} — {path}");
	}

	private void NewPresentation()
	{
		string name = _newName.Text.Trim();
		if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
		{
			SetStatus("A presentation name is a file name: letters, digits, no path.");
			return;
		}

		if (_preview.Host is { } host && host.PresentationPath(name) is { } taken)
		{
			SetStatus($"'{name}' is already served ({taken}); pick it from the list to edit it.");
			return;
		}

		// The folder the shipped presentations sit in, so the host lists the new one with them.
		string folder = _preview.Host?.ScreenNames.Select(_preview.Host.PresentationPath).FirstOrDefault(p => p is not null) is { } any
			? (Path.GetDirectoryName(any.Replace('\\', '/')) ?? DefaultFolder).Replace('\\', '/')
			: DefaultFolder;
		_doc = Engine_BgfEdit.NewDocument(name, 1280, 720, bigEndian: true);
		_fac = Engine_BgfEdit.NewSidecar();
		_bsg = Engine_BgfEdit.NewSceneGraph(bigEndian: true);
		_presentation = name;
		_path = $"{folder}/{name}.bgf";
		Dirty = true;
		_selected = -1;
		FillTree();
		Edited?.Invoke();
		SetStatus($"New presentation '{name}' — one root component; add nodes, then export to {_path}.");
	}

	private void FillTree()
	{
		_tree.Clear();
		if (_doc is null)
		{
			return;
		}

		string filter = _filter.Text.Trim();
		TreeItem root = _tree.CreateItem();
		var items = new TreeItem?[_doc.Elements.Length];
		for (int i = 0; i < _doc.Elements.Length; i++)
		{
			BgfElement e = _doc.Elements[i];
			string name = _doc.GetName(e);
			bool shown = filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase);
			TreeItem? parent = e.ParentOrdinal >= 0 && e.ParentOrdinal < i ? items[e.ParentOrdinal] : null;
			if (!shown && filter.Length != 0)
			{
				// A filtered-out element keeps its place as its children's parent, under the root.
				items[i] = parent;
				continue;
			}

			TreeItem item = _tree.CreateItem(parent ?? root);
			item.SetText(0, name.Length == 0 ? "(unnamed)" : name);
			item.SetText(1, e.Type.ToString());
			item.SetText(2, i.ToString());
			item.SetMetadata(0, i);
			item.Collapsed = filter.Length == 0 && i != 0 && e.ParentOrdinal != 0;
			if (!e.IsActive)
			{
				item.SetCustomColor(0, new Color(0.6f, 0.6f, 0.6f));
			}

			if (i == _selected)
			{
				item.Select(0);
			}

			items[i] = item;
		}
	}

	private void OnSelected()
	{
		if (_doc is null || _tree.GetSelected() is not { } item)
		{
			return;
		}

		_selected = item.GetMetadata(0).AsInt32();
		BgfElement e = _doc.Elements[_selected];
		_name.Text = _doc.GetName(e);
		(float x, float y) = Engine_BgfEdit.PositionOf(_doc, _selected);
		_x.Value = x;
		_y.Value = y;
		int children = _doc.Elements.Count(c => c.ParentOrdinal == _selected);
		int subtree = Engine_BgfEdit.SubtreeEnd(_doc, _selected) - _selected - 1;
		FacUvRecord? uv = _fac?.Uv.LastOrDefault(r => r.StableId == unchecked((int)e.ObjectId));
		_detail.Text = $"#{_selected} {e.Type}{(e.IsComponent ? " (component)" : string.Empty)}{(e.IsActive ? string.Empty : ", inactive")}, " +
			$"{e.Attributes.Length} attributes, {children} children ({subtree} in the subtree), object id {e.ObjectId}, " +
			$"slides owned {_doc.Slides.Count(s => s.OwnerElement == _selected)}" +
			(uv is null ? ". " : $", quad: texture {uv.TextureHash:X8} rect {uv.U0} {uv.V0} {uv.Du} {uv.Dv}. ") +
			string.Join(" ", e.Attributes.Take(12).Select(a => Engine_BgfAttributeNames.Describe(a.Id) + "=" + Describe(a)));
	}

	private string Describe(BgfAttribute a) => a.Type switch
	{
		BgfValueType.Float => a.AsFloat().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
		BgfValueType.String => "\"" + _doc!.GetString(a.AsStringIndex()) + "\"",
		BgfValueType.Bool => a.AsBool() ? "true" : "false",
		_ => a.AsInt().ToString(),
	};

	private bool Picked(out int ordinal)
	{
		ordinal = _selected;
		if (_doc is null || ordinal < 0 || ordinal >= _doc.Elements.Length)
		{
			SetStatus("Pick an element first.");
			return false;
		}

		return true;
	}

	private void Rename()
	{
		string name = _name.Text.Trim();
		if (!Picked(out int ordinal) || name.Length == 0)
		{
			return;
		}

		Engine_BgfEdit.Rename(_doc!, ordinal, name);
		Touched($"#{ordinal} renamed to '{name}'.");
	}

	private void Move()
	{
		if (!Picked(out int ordinal))
		{
			return;
		}

		Engine_BgfEdit.Move(_doc!, ordinal, (float)_x.Value, (float)_y.Value);
		Touched($"#{ordinal} moved to ({_x.Value}, {_y.Value}).");
	}

	private void AddChild()
	{
		string name = _childName.Text.Trim();
		if (!Picked(out int parent) || name.Length == 0)
		{
			return;
		}

		int added = Engine_BgfEdit.AddChild(_doc!, parent, name, 0f, 0f, _bsg);
		_selected = added;
		Touched($"'{name}' added as #{added} under #{parent}.");
	}

	private void AddQuad()
	{
		string name = _quadName.Text.Trim();
		string texture = _quadTexture.Text.Trim().Replace('\\', '/');
		if (!Picked(out int parent) || name.Length == 0)
		{
			return;
		}

		if (texture.Length == 0 || !texture.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
		{
			SetStatus("A quad samples a .tex: a shipped texture's bank path, or a mod's foo.tex (shipped as foo.tex.png).");
			return;
		}

		int added = Engine_BgfEdit.AddQuad(_doc!, _fac ??= Engine_BgfEdit.NewSidecar(), _bsg, parent, name, 0f, 0f,
			(float)_quadW.Value, (float)_quadH.Value, texture,
			(float)_quadU.Value, (float)_quadV.Value, (float)_quadDu.Value, (float)_quadDv.Value);
		_selected = added;
		Touched($"Quad '{name}' added as #{added} under #{parent}, sampling {texture}.");
	}

	private void Clone()
	{
		string name = _cloneName.Text.Trim();
		if (!Picked(out int source) || name.Length == 0)
		{
			return;
		}

		int copy = Engine_BgfEdit.Clone(_doc!, source, name, _fac, _bsg);
		_selected = copy;
		Touched($"#{source} cloned as '{name}' at #{copy} ({Engine_BgfEdit.SubtreeEnd(_doc!, copy) - copy} elements).");
	}

	private void Touched(string what)
	{
		Dirty = true;
		FillTree();
		OnSelected();
		Edited?.Invoke();
		SetStatus(what + " Export to put it in the mod.");
	}

	/// <summary>
	/// Writes the presentation into the active mod at its path. The writer reads its own bytes
	/// back with the game's parser and refuses a file that does not come back equal; the error,
	/// or empty.
	/// </summary>
	public string Export(bool report)
	{
		if (_doc is null || _path.Length == 0)
		{
			return Status(report, "Nothing to export — load or create a presentation first.");
		}

		if (_docs.Mod.Length == 0)
		{
			return Status(report, "No active mod to export into.");
		}

		if (!Engine_BgfWriter.TryWrite(_doc, out byte[] bytes, out string error))
		{
			return Status(report, "⚠ not exported: " + error);
		}

		byte[]? fac = null;
		if (_fac is not null && !Engine_FacWriter.TryWrite(_fac, out fac, out error))
		{
			return Status(report, "⚠ not exported (.fac): " + error);
		}

		byte[]? bsg = null;
		if (_bsg is not null && !Engine_BsgWriter.TryWrite(_bsg, out bsg, out error))
		{
			return Status(report, "⚠ not exported (.bsg): " + error);
		}

		string written = _docs.Api.WriteFile(_path, bytes);
		if (written.Length == 0 && fac is not null)
		{
			written = _docs.Api.WriteFile(Path.ChangeExtension(_path, ".fac"), fac);
		}

		if (written.Length == 0 && bsg is not null)
		{
			written = _docs.Api.WriteFile(Path.ChangeExtension(_path, ".bsg"), bsg);
		}

		if (written.Length != 0)
		{
			return Status(report, "⚠ " + written);
		}

		Dirty = false;
		Edited?.Invoke();
		Status(report, $"Exported {_presentation} ({bytes.Length:N0} bytes, {_doc.Elements.Length} elements" +
			(fac is null ? string.Empty : $"; .fac {fac.Length:N0} bytes") + (bsg is null ? string.Empty : $"; .bsg {bsg.Length:N0} bytes") +
			$") into '{_docs.Mod}' at {_path}. The mount serves them in the shipped files' place on the next remount.");
		return string.Empty;
	}

	private string Status(bool report, string text)
	{
		if (report)
		{
			SetStatus(text);
		}

		return text;
	}

	private void SetStatus(string text) => _status.Text = text;
}
#endif
